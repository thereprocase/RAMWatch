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
    /// On success, also populates FCLK, UCLK, and VSoc from SMU data.
    /// </summary>
    public TimingSnapshot? ReadTimings(string bootId)
    {
        if (_umcDecode is null) return null;

        try
        {
            var snapshot = _umcDecode.ReadTimings(bootId);
            if (snapshot is null) return null;

            // SMU reads are best-effort: FCLK/UCLK/VSoc stay at 0 on failure.
            _smuDecode?.PopulateClockVoltage(snapshot);

            return snapshot;
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
