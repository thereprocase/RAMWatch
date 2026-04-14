using RAMWatch.Core.Models;
using RAMWatch.Service.Hardware.PawnIo;

namespace RAMWatch.Service.Hardware;

/// <summary>
/// Reads SMU-sourced data: SVI2 voltage telemetry and FCLK/UCLK from the
/// SMU power table. Two separate concerns, but both belong to the SMU layer
/// rather than the UMC register layer.
///
/// SVI2 voltages: SMN direct reads via IHardwareAccess — no extra module.
/// FCLK/UCLK: SMU power table via RyzenSMU.bin — separate PawnIO handle.
///
/// Graceful degradation: if the power table reader fails to initialise,
/// FCLK/UCLK stay at 0. If SVI2 reads return implausible values, VSoc
/// stays at 0. The rest of the snapshot is still valid.
/// </summary>
public sealed class SmuDecode : IDisposable
{
    private readonly IHardwareAccess _hw;
    private readonly CpuDetect.CpuFamily _cpuFamily;
    private readonly SmuPowerTableReader? _ptReader;

    // SVI2 base address — hardware fact from AMD PPR, same across Zen 2/3/4
    private const uint Svi2Base = 0x0005A000;

    // Telemetry plane offsets within the SVI2 base region.
    // The SOC plane address is CPU-family-dependent.
    private const uint Svi2Plane0Offset = 0x00C;  // 0x5A00C
    private const uint Svi2Plane1Offset = 0x010;  // 0x5A010

    public SmuDecode(IHardwareAccess hw, CpuDetect.CpuFamily cpuFamily)
    {
        _hw = hw;
        _cpuFamily = cpuFamily;

        // RyzenSMU.bin is only loaded when the driver is available.
        // Failure here is non-fatal — FCLK/UCLK will be 0.
        if (hw.IsAvailable)
        {
            try
            {
                _ptReader = new SmuPowerTableReader();
                _ptReader.Initialize();
                if (!_ptReader.IsAvailable)
                {
                    _ptReader.Dispose();
                    _ptReader = null;
                }
            }
            catch
            {
                _ptReader?.Dispose();
                _ptReader = null;
            }
        }
    }

    /// <summary>
    /// Populate FclkMhz, UclkMhz, and VSoc fields in the provided snapshot.
    /// VDimm is populated by HardwareReader via VdimmReader (WMI), not here.
    /// </summary>
    public void PopulateClockVoltage(TimingSnapshot snapshot)
    {
        if (!_hw.IsAvailable) return;

        ReadSvi2Voltages(snapshot);

        if (_ptReader is not null)
            _ptReader.ReadFclkUclk(snapshot);
    }

    public void Dispose()
    {
        _ptReader?.Dispose();
    }

    // ── SVI2 voltage telemetry ───────────────────────────────────────────

    /// <summary>
    /// Read VSoc from the SVI2 telemetry register for the active CPU family.
    ///
    /// Register layout (32-bit):
    ///   [31:24] — unused
    ///   [23:16] — VID byte: voltage = 1.55 - vid * 0.00625
    ///   [15:8]  — status bits; non-zero means the read is mid-transition,
    ///             retry in caller (ZenTimings retries up to 20 times)
    ///   [7:0]   — current in some encodings (not used here)
    ///
    /// A single read is taken here. The retry loop from ZenTimings is omitted
    /// because: (a) the service reads this on a slow poll interval, not in a
    /// tight loop; (b) a single stale read rounds to an implausible value and
    /// the plausibility check below discards it.
    /// </summary>
    private void ReadSvi2Voltages(TimingSnapshot snapshot)
    {
        uint socAddress = GetSocPlaneAddress(_cpuFamily);
        if (socAddress == 0) return;

        if (!_hw.TryReadSmn(socAddress, out uint raw)) return;

        // bits [15:8] are status/lock bits — if non-zero, the SMU is updating
        // the register mid-read. Discard rather than report garbage.
        if ((raw & 0xFF00u) != 0) return;

        uint vid = (raw >> 16) & 0xFF;
        double voltage = VidToVoltage(vid);

        // Plausibility: SoC voltage on desktop Zen 2/3 is typically 0.9–1.3 V.
        // Anything outside 0.5–2.0 V is a bad read (or unsupported platform).
        if (voltage is >= 0.5 and <= 2.0)
            snapshot.VSoc = Math.Round(voltage, 4);
    }

    /// <summary>
    /// Convert a SVI2 VID byte to voltage.
    /// Formula: 1.55 - vid * 0.00625 V
    /// Source: AMD PPR, confirmed by ZenStates-Core Utils.VidToVoltage.
    /// </summary>
    internal static double VidToVoltage(uint vid)
    {
        return 1.55 - vid * 0.00625;
    }

    /// <summary>
    /// Map CPU family to the SMN address of the SoC voltage telemetry plane.
    /// Returns 0 for unsupported families.
    ///
    /// Address derivation (hardware facts from AMD PPR and ZenStates-Core Constants.cs):
    ///   F17H_M01H_SVI = 0x5A000
    ///   Plane 0 offset = 0xC  → 0x5A00C
    ///   Plane 1 offset = 0x10 → 0x5A010
    ///
    ///   Zen (SummitRidge) desktop:  SOC = Plane 1 (0x5A010)
    ///   Zen+ (PinnacleRidge) desktop: SOC = Plane 1 (0x5A010)
    ///   Zen2 (Matisse) desktop:     SOC = Plane 0 (0x5A00C) — planes swapped vs Zen/Zen+
    ///   Zen3 (Vermeer) desktop:     SOC = Plane 0 (0x5A00C)
    ///   Zen4 (Raphael) desktop:     SOC = Plane 0 (0x5A00C)
    /// </summary>
    internal static uint GetSocPlaneAddress(CpuDetect.CpuFamily family)
    {
        return family switch
        {
            // Zen and Zen+: SOC is on Plane 1 (0x5A010)
            CpuDetect.CpuFamily.Zen or
            CpuDetect.CpuFamily.ZenPlus => Svi2Base + Svi2Plane1Offset,

            // Zen2 desktop (Matisse): SOC is on Plane 1 (= F17H_M70H_PLANE1 = 0x5A00C)
            // Note: Zen2 swaps CORE and SOC compared to Zen/Zen+
            CpuDetect.CpuFamily.Zen2 => Svi2Base + Svi2Plane0Offset,

            // Zen3 desktop (Vermeer): SOC is F19H_M21H_PLANE1 = 0x5A00C
            CpuDetect.CpuFamily.Zen3 => Svi2Base + Svi2Plane0Offset,

            // Zen4 desktop (Raphael): same as Zen3
            CpuDetect.CpuFamily.Zen4 => Svi2Base + Svi2Plane0Offset,

            // Zen5: unknown at time of writing — skip
            _ => 0
        };
    }
}
