using RAMWatch.Core.Models;
using RAMWatch.Service.Hardware.PawnIo;

namespace RAMWatch.Service.Hardware;

/// <summary>
/// Reads SMU-sourced data: SVI2 voltage telemetry, FCLK/UCLK from the
/// SMU power table, and thermal/power telemetry.
///
/// SVI2 voltages: SMN direct reads via IHardwareAccess — no extra module.
/// FCLK/UCLK: SMU power table via RyzenSMU.bin — separate PawnIO handle.
/// Tctl: Direct SMN register 0x59800 — works on all Zen, no PM table needed.
/// Per-CCD temps: SMN registers 0x59954+ (Zen2/3) or 0x59B08+ (Zen4/5).
/// Power/limits: PM table fields (PPT, TDC, EDC, socket/core/SoC power).
///
/// Graceful degradation: if the power table reader fails to initialise,
/// FCLK/UCLK stay at 0. If SVI2 reads return implausible values, VSoc
/// stays at 0. Tctl falls back to SMN direct read if PM table unavailable.
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

    // THM_TCON_CUR_TMP — current Tctl temperature register (all Zen).
    // Read-only. Bits [31:21] = temperature in 0.125°C steps.
    // Source: AMD PPR, confirmed by LibreHardwareMonitor Amd17Cpu.cs.
    private const uint ThmTconCurTmp = 0x00059800;

    // Per-CCD temperature base addresses — generation-dependent.
    // Each CCD's temp is at base + (ccd_index * 4).
    // Zen 2/3: 0x00059954
    // Zen 4/5: 0x00059B08
    private const uint CcdTempBaseZen2 = 0x00059954;
    private const uint CcdTempBaseZen4 = 0x00059B08;

    // Maximum CCDs we will probe. Desktop Zen tops out at 2 CCDs (16 cores).
    // HEDT (Threadripper) can have up to 8 CCDs but we cap at 8 for safety.
    private const int MaxCcds = 8;

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
    /// VDimm and other BIOS config values are populated by HardwareReader
    /// via BiosWmiReader (WMI), not here.
    /// </summary>
    public void PopulateClockVoltage(TimingSnapshot snapshot)
    {
        if (!_hw.IsAvailable) return;

        ReadSvi2Voltages(snapshot);

        // Single power table read for clocks + voltages (avoids double IOCTL)
        _ptReader?.ReadClocksAndVoltages(snapshot);
    }

    /// <summary>
    /// Populate thermal and power telemetry. Uses three independent data paths:
    /// 1. Direct SMN read of Tctl (works on all Zen, even if PM table is unavailable)
    /// 2. Per-CCD temperature registers (Zen 2+)
    /// 3. PM table thermal/power fields (PPT, TDC, EDC, socket power, etc.)
    ///
    /// Each path is independently fault-tolerant. A failure in one does not
    /// prevent the others from contributing data.
    /// </summary>
    public void PopulateThermalPower(ThermalPowerSnapshot tp)
    {
        if (!_hw.IsAvailable) return;

        tp.Timestamp = DateTime.UtcNow;

        // Path 1: Direct SMN Tctl — generation-independent, highest reliability
        ReadTctlDirect(tp);

        // Path 2: Per-CCD temperatures
        ReadCcdTemps(tp);

        // Path 3: PM table thermal/power fields
        _ptReader?.ReadThermalPower(tp);
    }

    public void Dispose()
    {
        _ptReader?.Dispose();
    }

    // ── Direct thermal register reads ────────────────────────────────────

    /// <summary>
    /// Read Tctl/Tdie from the THM_TCON_CUR_TMP SMN register.
    /// This is the same register HWiNFO and LibreHardwareMonitor read.
    /// Works on every Zen generation without version branching.
    ///
    /// Register layout (32-bit, read-only):
    ///   [31:21] — Temperature in 0.125°C steps (11-bit unsigned)
    ///   [20:19] — Range flags (bit 19 = Tctl offset present)
    ///   [18:0]  — Reserved
    ///
    /// Some SKUs (Threadripper, some EPYC) have a +49°C Tctl offset.
    /// We report Tctl as-is — the consumer can subtract the offset if needed.
    /// </summary>
    private void ReadTctlDirect(ThermalPowerSnapshot tp)
    {
        try
        {
            if (!_hw.TryReadSmn(ThmTconCurTmp, out uint raw)) return;

            double tempC = (raw >> 21) * 0.125;

            // Plausibility: -10 to 125°C. The register can read 0 on some
            // APUs or when the sensor is disabled in firmware.
            if (tempC is >= -10 and <= 125)
            {
                tp.CpuTempC = Math.Round(tempC, 1);
                tp.Sources |= ThermalDataSource.SmnTctl;
            }
        }
        catch
        {
            // Non-fatal — CpuTempC stays at 0
        }
    }

    /// <summary>
    /// Read per-CCD die temperatures from SMN registers.
    /// Each CCD has its own thermal sensor at a predictable offset.
    ///
    /// Register layout (32-bit, read-only):
    ///   [11:0] — Temperature raw value
    ///   Formula: (raw * 125 - 305000) / 1000 °C
    ///
    /// A register value of 0 means the CCD is not present or powered down.
    /// We probe up to MaxCcds and stop at the first absent CCD.
    /// </summary>
    private void ReadCcdTemps(ThermalPowerSnapshot tp)
    {
        try
        {
            uint baseAddr = _cpuFamily switch
            {
                CpuDetect.CpuFamily.Zen4 or CpuDetect.CpuFamily.Zen5 => CcdTempBaseZen4,
                CpuDetect.CpuFamily.Zen2 or CpuDetect.CpuFamily.Zen3 => CcdTempBaseZen2,
                _ => 0
            };
            if (baseAddr == 0) return;

            var temps = new List<double>(MaxCcds);
            for (int i = 0; i < MaxCcds; i++)
            {
                uint addr = baseAddr + (uint)(i * 4);
                if (!_hw.TryReadSmn(addr, out uint raw)) break;

                // Raw value of 0 means CCD not present
                uint tempRaw = raw & 0xFFF;
                if (tempRaw == 0) break;

                double tempC = (tempRaw * 125.0 - 305000.0) / 1000.0;

                // Plausibility check
                if (tempC is >= -10 and <= 125)
                    temps.Add(Math.Round(tempC, 1));
                else
                    break; // Implausible = not a real CCD
            }

            if (temps.Count > 0)
            {
                tp.CcdTempsC = temps.ToArray();
                tp.Sources |= ThermalDataSource.SmnCcdTemp;
            }
        }
        catch
        {
            // Non-fatal — CcdTempsC stays null
        }
    }

    // ── SVI2 voltage telemetry ───────────────────────────────────────────

    /// <summary>
    /// Read VSoc and VCore from SVI2 telemetry registers for the active CPU family.
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
        ReadSvi2Plane(GetSocPlaneAddress(_cpuFamily), v => snapshot.VSoc = v);
        ReadSvi2Plane(GetCorePlaneAddress(_cpuFamily), v => snapshot.VCore = v);
    }

    private void ReadSvi2Plane(uint address, Action<double> setter)
    {
        if (address == 0) return;
        if (!_hw.TryReadSmn(address, out uint raw)) return;

        // bits [15:8] are status/lock bits — if non-zero, the SMU is updating
        // the register mid-read. Discard rather than report garbage.
        if ((raw & 0xFF00u) != 0) return;

        uint vid = (raw >> 16) & 0xFF;
        double voltage = VidToVoltage(vid);

        // Plausibility: desktop Zen 2/3 voltages are typically 0.9–1.3 V.
        // Anything outside 0.5–2.0 V is a bad read (or unsupported platform).
        if (voltage is >= 0.5 and <= 2.0)
            setter(Math.Round(voltage, 4));
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

            // Zen2 desktop (Matisse): SOC is on Plane 0 (= F17H_M70H_PLANE1 = 0x5A00C)
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

    /// <summary>
    /// Map CPU family to the SMN address of the Core voltage telemetry plane.
    /// Returns 0 for unsupported families. Core is always the opposite plane from SOC.
    /// </summary>
    internal static uint GetCorePlaneAddress(CpuDetect.CpuFamily family)
    {
        return family switch
        {
            // Zen and Zen+: Core is on Plane 0 (0x5A00C)
            CpuDetect.CpuFamily.Zen or
            CpuDetect.CpuFamily.ZenPlus => Svi2Base + Svi2Plane0Offset,

            // Zen2+: Core is on Plane 1 (0x5A010) — opposite of SOC
            CpuDetect.CpuFamily.Zen2 or
            CpuDetect.CpuFamily.Zen3 or
            CpuDetect.CpuFamily.Zen4 => Svi2Base + Svi2Plane1Offset,

            _ => 0
        };
    }
}
