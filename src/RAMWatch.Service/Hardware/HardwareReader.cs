using RAMWatch.Core.Models;
using RAMWatch.Service.Hardware.PawnIo;

namespace RAMWatch.Service.Hardware;

/// <summary>
/// Orchestrates hardware detection and timing reads.
/// Detection chain: PawnIO → NullHardwareAccess (graceful degradation).
/// </summary>
public sealed class HardwareReader : IDisposable
{
    private readonly IHardwareAccess _driver;
    private readonly UmcDecode? _umcDecode;
    private readonly SmuDecode? _smuDecode;
    private readonly CpuDetect.CpuFamily _cpuFamily;
    // BIOS WMI values are BIOS-set constants — read once, reuse forever.
    // A service restart picks up changes after a BIOS flash.
    private BiosWmiReader.BiosConfig? _cachedBiosConfig;

    public bool IsAvailable => _driver.IsAvailable;
    public string DriverStatus => _driver.IsAvailable ? "loaded" : "not_found";
    public string DriverDescription => _driver.StatusDescription;
    public string DriverName => _driver.DriverName;
    public CpuDetect.CpuFamily CpuFamily => _cpuFamily;

    public HardwareReader()
    {
        _driver = DetectDriver();
        _cpuFamily = CpuDetect.Detect(_driver);

        if (_driver.IsAvailable && _cpuFamily != CpuDetect.CpuFamily.Unknown)
        {
            _umcDecode = new UmcDecode(_driver);
            _smuDecode = new SmuDecode(_driver, _cpuFamily);
        }
    }

    /// <summary>
    /// Read current DRAM timings. Returns null if driver or CPU unsupported.
    /// On success, also populates FCLK, UCLK, VSoc, and VDimm.
    ///
    /// VDimm is read from vendor-specific WMI via a PowerShell subprocess.
    /// The call takes ~200–500 ms and is performed once per ReadTimings call.
    /// The caller (RamWatchService) is responsible for not calling ReadTimings
    /// on a tight loop — the existing 30-second poll interval is appropriate.
    /// </summary>
    public TimingSnapshot? ReadTimings(string bootId)
    {
        if (_umcDecode is null) return null;

        try
        {
            var snapshot = _umcDecode.ReadTimings(bootId);
            if (snapshot is null) return null;

            // SMU reads are best-effort: FCLK/UCLK/VSoc/VCore/VDDP/VDDG stay at 0 on failure.
            _smuDecode?.PopulateClockVoltage(snapshot);

            // BIOS WMI: VDimm, Vtt, Vpp, resistance/impedance parameters.
            // Covers MSI (AMD_ACPI) and ASUS boards. Returns zeroes on ASRock
            // or any board that does not expose the WMI interface.
            // Cached after first read — these are BIOS-set constants, not telemetry.
            _cachedBiosConfig ??= BiosWmiReader.ReadAll();
            var bios = _cachedBiosConfig.Value;
            if (bios.VDimm > 0) snapshot.VDimm = bios.VDimm;
            if (bios.Vtt > 0) snapshot.Vtt = bios.Vtt;
            if (bios.Vpp > 0) snapshot.Vpp = bios.Vpp;
            if (bios.ProcODT > 0) snapshot.ProcODT = bios.ProcODT;
            if (bios.RttNom.Length > 0) snapshot.RttNom = bios.RttNom;
            if (bios.RttWr.Length > 0) snapshot.RttWr = bios.RttWr;
            if (bios.RttPark.Length > 0) snapshot.RttPark = bios.RttPark;
            if (bios.ClkDrvStren > 0) snapshot.ClkDrvStren = bios.ClkDrvStren;
            if (bios.AddrCmdDrvStren > 0) snapshot.AddrCmdDrvStren = bios.AddrCmdDrvStren;
            if (bios.CsOdtCmdDrvStren > 0) snapshot.CsOdtCmdDrvStren = bios.CsOdtCmdDrvStren;
            if (bios.CkeDrvStren > 0) snapshot.CkeDrvStren = bios.CkeDrvStren;
            if (bios.AddrCmdSetup.Length > 0) snapshot.AddrCmdSetup = bios.AddrCmdSetup;
            if (bios.CsOdtSetup.Length > 0) snapshot.CsOdtSetup = bios.CsOdtSetup;
            if (bios.CkeSetup.Length > 0) snapshot.CkeSetup = bios.CkeSetup;

            // Plausibility check: CL and RAS are never zero on real hardware.
            // If either is 0, a register read failed silently and the snapshot
            // contains a mix of real and zero values — discard it entirely.
            if (snapshot.CL == 0 || snapshot.RAS == 0)
                return null;

            return snapshot;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Read real-time thermal and power telemetry. Returns null if driver or CPU unsupported.
    /// Independent from ReadTimings — this can be called on a faster cadence if needed.
    /// </summary>
    public ThermalPowerSnapshot? ReadThermalPower()
    {
        if (_smuDecode is null) return null;

        try
        {
            var tp = new ThermalPowerSnapshot();
            _smuDecode.PopulateThermalPower(tp);

            // If no data source succeeded, return null rather than an empty object.
            if (tp.Sources == ThermalDataSource.None)
                return null;

            return tp;
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        _smuDecode?.Dispose();
        _driver.Dispose();
    }

    private static IHardwareAccess DetectDriver()
    {
        // Try PawnIO first (the only supported driver backend)
        try
        {
            if (PawnIoDriver.IsInstalled)
            {
                var access = new PawnIoAccess();
                access.Initialize();
                if (access.IsAvailable)
                    return access;

                access.Dispose();
            }
        }
        catch
        {
            // PawnIO initialization failed entirely
        }

        return new NullHardwareAccess();
    }
}
