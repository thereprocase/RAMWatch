using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using RAMWatch.Core.Models;
using RAMWatch.Service.Hardware.PawnIo;

namespace RAMWatch.Service.Hardware;

/// <summary>
/// Reads FCLK and UCLK from the AMD SMU (System Management Unit) power table
/// via the RyzenSMU.bin PawnIO module.
///
/// Uses a separate PawnIO handle from the AMDFamily17.bin timing reader.
/// Each pawnio_open call returns an independent handle; the two modules
/// coexist without conflict.
///
/// The SMU power table is a version-stamped array of floats in DRAM.
/// The ioctl_resolve_pm_table call returns:
///   [0] = PM table version (uint32)
///   [1] = DRAM base address (int64)
///
/// ioctl_update_pm_table asks the SMU to refresh the table in DRAM.
/// ioctl_read_pm_table reads N floats starting from the DRAM base.
///
/// FCLK and UCLK offsets are float-indexed (offset / 4 = array index).
/// They differ by PM table version. Known desktop versions are listed in
/// the PmTableOffsets table below.
///
/// Version data sourced from ZenStates-Core PowerTable.cs (GPL-3.0).
/// Register offsets are hardware facts; the decode implementation is original.
/// </summary>
public sealed class SmuPowerTableReader : IDisposable
{
    private PawnIoDriver? _driver;
    private bool _available;
    private uint _pmTableVersion;
    private PmTableLayout _layout;

    // SHA-256 of the bundled RyzenSMU.bin (B8: module integrity)
    private const string ExpectedHash =
        "0CF0FE1296C5C38F4BEE0F96352B35F14D32AB97CB58FD17600646D98507D8AA";

    // PawnIO IOCTL function names exported by RyzenSMU.bin
    private const string IoctlResolvePmTable = "ioctl_resolve_pm_table";
    private const string IoctlUpdatePmTable = "ioctl_update_pm_table";
    private const string IoctlReadPmTable = "ioctl_read_pm_table";

    // Minimum table size to attempt reading (in bytes).
    // Even the smallest known PM table is 0x514 bytes.
    private const uint MinPmTableBytes = 0x514;

    // Maximum PM table size we will allocate for.
    // The largest known table (Storm Peak) is ~0x1E48 bytes.
    private const uint MaxPmTableBytes = 0x2000;

    public bool IsAvailable => _available;

    public void Initialize()
    {
        try
        {
            if (!PawnIoDriver.IsInstalled) return;

            _driver = new PawnIoDriver();
            if (!_driver.Open())
            {
                _driver.Dispose();
                _driver = null;
                return;
            }

            byte[]? module = LoadEmbeddedModule("RyzenSMU.bin");
            if (module is null)
            {
                _driver.Dispose();
                _driver = null;
                return;
            }

            // Verify integrity before loading kernel code
            if (!HashMatches(module, ExpectedHash))
            {
                _driver.Dispose();
                _driver = null;
                return;
            }

            if (!_driver.LoadModule(module))
            {
                _driver.Dispose();
                _driver = null;
                return;
            }

            // Resolve PM table version and DRAM base address
            if (!TryResolvePmTable(out _pmTableVersion, out long dramBase) || dramBase == 0)
            {
                _driver.Dispose();
                _driver = null;
                return;
            }

            // Map version to layout. If version is unknown, we cannot read clocks.
            _layout = GetLayout(_pmTableVersion);
            if (!_layout.IsValid)
            {
                // Unknown PM table version — cannot map FCLK/UCLK offsets.
                // Leave available=false so the caller skips FCLK/UCLK reads.
                _driver.Dispose();
                _driver = null;
                return;
            }

            _available = true;
        }
        catch
        {
            _driver?.Dispose();
            _driver = null;
        }
    }

    /// <summary>
    /// Populate FclkMhz and UclkMhz in the snapshot.
    /// Returns immediately if not available.
    /// </summary>
    public void ReadFclkUclk(TimingSnapshot snapshot)
    {
        // Reads clocks only — kept for backward compatibility with callers that
        // don't need voltages. Prefer ReadClocksAndVoltages for the full read.
        ReadClocksAndVoltages(snapshot);
    }

    /// <summary>
    /// Populate FCLK, UCLK, VDDP, VDDG_IOD, and VDDG_CCD from a single
    /// SMU power table read. Replaces the old separate ReadFclkUclk +
    /// ReadVoltages calls that each triggered an independent IOCTL read.
    /// </summary>
    public void ReadClocksAndVoltages(TimingSnapshot snapshot)
    {
        if (_driver is null || !_available) return;

        try
        {
            float[]? table = ReadPmTable();
            if (table is null) return;

            // Clocks
            int fclkIndex = (int)(_layout.FclkByteOffset / 4);
            int uclkIndex = (int)(_layout.UclkByteOffset / 4);

            if (fclkIndex < table.Length && table[fclkIndex] > 0)
                snapshot.FclkMhz = SnapClockMhz(table[fclkIndex]);

            if (uclkIndex < table.Length && table[uclkIndex] > 0)
                snapshot.UclkMhz = SnapClockMhz(table[uclkIndex]);

            // Voltages
            TryReadVoltage(table, _layout.CldoVddpByteOffset, 0.5, 1.2, v => snapshot.VDDP = v);
            TryReadVoltage(table, _layout.CldoVddgIodByteOffset, 0.7, 1.3, v => snapshot.VDDG_IOD = v);
            TryReadVoltage(table, _layout.CldoVddgCcdByteOffset, 0.7, 1.3, v => snapshot.VDDG_CCD = v);
        }
        catch
        {
            // Non-fatal — snapshot retains 0 for clocks/voltages
        }
    }

    /// <summary>Kept for backward compatibility. Use ReadClocksAndVoltages instead.</summary>
    public void ReadVoltages(TimingSnapshot _) { /* no-op: merged into ReadClocksAndVoltages */ }

    /// <summary>
    /// Read thermal and power telemetry from the SMU power table.
    /// Populates only the fields for which this PM table version has known offsets.
    /// Returns false if the table could not be read at all.
    /// </summary>
    public bool ReadThermalPower(ThermalPowerSnapshot tp)
    {
        if (_driver is null || !_available) return false;

        try
        {
            float[]? table = ReadPmTable();
            if (table is null) return false;

            // Temperature fields — plausibility: -10 to 125 °C
            TryReadThermal(table, _layout.ThmValueByteOffset, v => tp.CpuTempC = v);
            TryReadThermal(table, _layout.SocTempByteOffset, v => tp.SocTempC = v);
            TryReadThermal(table, _layout.PeakTempByteOffset, v => tp.PeakTempC = v);

            // Power fields — plausibility: 0 to 1000 W (covers even HEDT)
            TryReadPower(table, _layout.SocketPowerByteOffset, v => tp.SocketPowerW = v);
            TryReadPower(table, _layout.CorePowerByteOffset, v => tp.CorePowerW = v);
            TryReadPower(table, _layout.SocPowerByteOffset, v => tp.SocPowerW = v);
            TryReadPower(table, _layout.PptLimitByteOffset, v => tp.PptLimitW = v);
            TryReadPower(table, _layout.PptValueByteOffset, v => tp.PptActualW = v);

            // Current fields — plausibility: 0 to 500 A
            TryReadCurrent(table, _layout.TdcLimitByteOffset, v => tp.TdcLimitA = v);
            TryReadCurrent(table, _layout.TdcValueByteOffset, v => tp.TdcActualA = v);
            TryReadCurrent(table, _layout.EdcLimitByteOffset, v => tp.EdcLimitA = v);
            TryReadCurrent(table, _layout.EdcValueByteOffset, v => tp.EdcActualA = v);

            tp.Sources |= ThermalDataSource.PmTable;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void TryReadThermal(float[] table, uint byteOffset, Action<double> setter)
    {
        if (byteOffset == PmTableLayout.NoOffset) return;
        int index = (int)(byteOffset / 4);
        if (index >= table.Length) return;
        double v = table[index];
        if (v is >= -10 and <= 125)
            setter(Math.Round(v, 1));
    }

    private static void TryReadPower(float[] table, uint byteOffset, Action<double> setter)
    {
        if (byteOffset == PmTableLayout.NoOffset) return;
        int index = (int)(byteOffset / 4);
        if (index >= table.Length) return;
        double v = table[index];
        if (v is >= 0 and <= 1000)
            setter(Math.Round(v, 2));
    }

    private static void TryReadCurrent(float[] table, uint byteOffset, Action<double> setter)
    {
        if (byteOffset == PmTableLayout.NoOffset) return;
        int index = (int)(byteOffset / 4);
        if (index >= table.Length) return;
        double v = table[index];
        if (v is >= 0 and <= 500)
            setter(Math.Round(v, 2));
    }

    private static void TryReadVoltage(float[] table, uint byteOffset, double min, double max, Action<double> setter)
    {
        if (byteOffset == PmTableLayout.NoOffset) return;
        int index = (int)(byteOffset / 4);
        if (index >= table.Length) return;
        double v = table[index];
        if (v >= min && v <= max)
            setter(Math.Round(v, 4));
    }

    public void Dispose()
    {
        _driver?.Dispose();
        _driver = null;
        _available = false;
    }

    /// <summary>
    /// Snap a raw SMU clock reading to the nearest logical increment.
    /// FCLK/UCLK/MCLK are multiples of BCLK/3 (≈33.33 MHz with BCLK=100).
    /// The SMU power table reports them as floats with ±2-3 MHz jitter.
    /// If the raw value is within 3 MHz of a clean multiple, snap to it.
    /// </summary>
    internal static int SnapClockMhz(float raw)
    {
        const double step = 100.0 / 3.0; // ~33.333 MHz
        double nearest = Math.Round(raw / step) * step;
        int snapped = (int)Math.Round(nearest);
        return Math.Abs(raw - snapped) <= 3 ? snapped : (int)Math.Round(raw);
    }

    // ── Internal helpers ─────────────────────────────────────────────────

    private bool TryResolvePmTable(out uint version, out long dramBase)
    {
        version = 0;
        dramBase = 0;

        try
        {
            // ioctl_resolve_pm_table returns [version, dramBaseAddress] as two uint64 slots.
            // dramBase is a physical address — treat as signed long per ZenStates-Core convention.
            var result = _driver!.Execute(IoctlResolvePmTable, [0UL, 0UL], 2);
            if (result is null || result.Length < 2) return false;

            version = unchecked((uint)(result[0] & 0xFFFFFFFF));
            dramBase = unchecked((long)result[1]);
            return dramBase != 0;
        }
        catch
        {
            return false;
        }
    }

    private float[]? ReadPmTable()
    {
        if (_driver is null) return null;

        // Ask the SMU to refresh the DRAM-mapped table
        try
        {
            _driver.Execute(IoctlUpdatePmTable, [], 0);
        }
        catch
        {
            // If update fails, try reading the stale table anyway
        }

        // Table size is in bytes; ioctl_read_pm_table takes a count of uint64 slots
        // and returns that many uint64 values packed with the float data.
        uint tableBytes = _layout.TableSizeBytes;
        if (tableBytes == 0 || tableBytes > MaxPmTableBytes) return null;

        uint qwordCount = (tableBytes + 7) / 8;
        try
        {
            var raw = _driver.Execute(IoctlReadPmTable, new ulong[qwordCount], (int)qwordCount);
            if (raw is null || raw.Length == 0) return null;

            // Reinterpret the uint64 array as a float array.
            // raw is ulong[] — each element holds 8 bytes, so the byte capacity is raw.Length * 8.
            int floatCount = (int)(tableBytes / 4);
            var floats = new float[floatCount];
            int bytesToCopy = Math.Min(floatCount * 4, raw.Length * 8);
            Buffer.BlockCopy(raw, 0, floats, 0, bytesToCopy);
            return floats;
        }
        catch
        {
            return null;
        }
    }

    private static byte[]? LoadEmbeddedModule(string fileName)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(fileName, StringComparison.OrdinalIgnoreCase));

        if (resourceName is null) return null;

        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream is null) return null;

        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    private static bool HashMatches(byte[] data, string expectedHex)
    {
        byte[] hash = SHA256.HashData(data);
        string actual = Convert.ToHexString(hash);
        return string.Equals(actual, expectedHex, StringComparison.OrdinalIgnoreCase);
    }

    // ── PM table layout table ────────────────────────────────────────────
    //
    // Byte offsets and table sizes are hardware facts from AMD SMU firmware.
    // Sourced from ZenStates-Core PowerTable.cs (GPL-3.0).
    // The struct below is an original abstraction.
    //
    // Layout: { tableVersion, tableSizeBytes, fclkByteOffset, uclkByteOffset }
    //
    // Comment format: "version — codename"
    // Versions whose offsets match the generic entry for their Zen generation
    // share one entry; the generic entry is keyed to 0 (see GetLayout below).
    //
    // Zen2 CPU (desktop Matisse + Castle Peak Threadripper):
    //   offsetFclk 0xB0 / offsetUclk 0xB8  — generic v1 (0x000200)
    //   offsetFclk 0xBC / offsetUclk 0xC4  — later revisions (0x000202/0x000203)
    //
    // Zen3 CPU (desktop Vermeer + Chagall Threadripper):
    //   offsetFclk 0xC0 / offsetUclk 0xC8  — (0x000300)

    // Internal so the test project can call GetLayout directly.
    // The test project is listed in InternalsVisibleTo in RAMWatch.Service.csproj.
    internal readonly struct PmTableLayout()
    {
        public uint TableSizeBytes { get; init; }
        public uint FclkByteOffset { get; init; }
        public uint UclkByteOffset { get; init; }
        // Voltage byte offsets — 0 means not available for this PM table version.
        // Each points to a float in the PM table array.
        public uint VddcrSocByteOffset { get; init; }
        public uint CldoVddpByteOffset { get; init; }
        public uint CldoVddgIodByteOffset { get; init; }
        public uint CldoVddgCcdByteOffset { get; init; }

        // Thermal/power byte offsets — NoOffset means not available.
        // PPT limit genuinely lives at byte offset 0x000, so we can't use 0 as sentinel.
        // All point to floats in the PM table array.
        public const uint NoOffset = uint.MaxValue;
        public uint ThmValueByteOffset { get; init; }                       // Tctl/Tdie (°C)
        public uint SocketPowerByteOffset { get; init; }                   // Total package power (W)
        public uint CorePowerByteOffset { get; init; }                     // VDDCR_CPU power (W)
        public uint SocPowerByteOffset { get; init; }                      // VDDCR_SOC power (W)
        public uint SocTempByteOffset { get; init; } = NoOffset;           // SoC die temp (°C)
        public uint PeakTempByteOffset { get; init; } = NoOffset;          // Peak temp since reset (°C)
        public uint PptLimitByteOffset { get; init; }                      // PPT limit (W) — lives at 0x000
        public uint PptValueByteOffset { get; init; }                      // PPT actual (W)
        public uint TdcLimitByteOffset { get; init; }                      // TDC limit (A)
        public uint TdcValueByteOffset { get; init; } = NoOffset;          // TDC actual (A)
        public uint EdcLimitByteOffset { get; init; }                      // EDC limit (A)
        public uint EdcValueByteOffset { get; init; } = NoOffset;          // EDC actual (A)

        public bool IsValid => TableSizeBytes > 0 && FclkByteOffset > 0 && UclkByteOffset > 0;
    }

    /// <summary>
    /// Look up the PM table layout for a given version.
    /// Returns an invalid layout if the version is unknown.
    /// </summary>
    internal static PmTableLayout GetLayout(uint version)
    {
        // Exact version matches first, then family-generic fallbacks.
        // Voltage offset key:
        //   VddcrSoc  = VDDCR_SOC (cross-check with SVI2, not written to snapshot)
        //   CldoVddp  = CLDO_VDDP (PLL supply)
        //   CldoVddgIod = CLDO_VDDG_IOD (I/O die)
        //   CldoVddgCcd = CLDO_VDDG_CCD (core complex die, Zen3 0x38* only)
        //   0 = not available for this version
        // Thermal/power offset key (Zen 2/3 share the first 12 elements):
        //   Index 0  (0x000) = PPT_LIMIT    Index 1  (0x004) = PPT_VALUE
        //   Index 2  (0x008) = TDC_LIMIT    Index 3  (0x00C) = TDC_VALUE
        //   Index 4  (0x010) = THM_LIMIT    Index 5  (0x014) = THM_VALUE (Tctl °C)
        //   Index 8  (0x020) = EDC_LIMIT    Index 9  (0x024) = EDC_VALUE
        //   Index 29 (0x074) = SOCKET_POWER
        //   Index 42 (0x0A8) = CPU_TELEMETRY_POWER
        //   Index 47 (0x0BC) = SOC_TELEMETRY_POWER
        //   SoC temp, peak temp vary by table version/size.
        //
        // Zen4 restructured the table — PPT/TDC/EDC at different offsets.
        // Offsets sourced from ryzen_monitor pm_tables.c and LibreHardwareMonitor RyzenSMU.cs.

        return version switch
        {
            // ── Zen2 CPU ────────────────────────────────────────────────
            // Generic v1 — SOC 0xA4, VDDP 0x1E4, VDDG_IOD 0x1E8
            0x000200 => new PmTableLayout { TableSizeBytes = 0x7E4, FclkByteOffset = 0xB0, UclkByteOffset = 0xB8,
                VddcrSocByteOffset = 0xA4, CldoVddpByteOffset = 0x1E4, CldoVddgIodByteOffset = 0x1E8,
                PptLimitByteOffset = 0x000, PptValueByteOffset = 0x004, TdcLimitByteOffset = 0x008, TdcValueByteOffset = 0x00C,
                ThmValueByteOffset = 0x014, EdcLimitByteOffset = 0x020, EdcValueByteOffset = 0x024,
                SocketPowerByteOffset = 0x074, CorePowerByteOffset = 0x0A8, SocPowerByteOffset = 0x0BC,
                SocTempByteOffset = 0x1CC, PeakTempByteOffset = 0x1FC },
            0x240003 => new PmTableLayout { TableSizeBytes = 0x18AC, FclkByteOffset = 0xB0, UclkByteOffset = 0xB8,
                VddcrSocByteOffset = 0xA4, CldoVddpByteOffset = 0x1E4, CldoVddgIodByteOffset = 0x1E8,
                PptLimitByteOffset = 0x000, PptValueByteOffset = 0x004, TdcLimitByteOffset = 0x008, TdcValueByteOffset = 0x00C,
                ThmValueByteOffset = 0x014, EdcLimitByteOffset = 0x020, EdcValueByteOffset = 0x024,
                SocketPowerByteOffset = 0x074, CorePowerByteOffset = 0x0A8, SocPowerByteOffset = 0x0BC,
                SocTempByteOffset = 0x1CC, PeakTempByteOffset = 0x1FC },

            // v2 revisions — SOC 0xB0, VDDP 0x1F0, VDDG_IOD 0x1F4
            0x240802 => new PmTableLayout { TableSizeBytes = 0x7E0, FclkByteOffset = 0xBC, UclkByteOffset = 0xC4,
                VddcrSocByteOffset = 0xB0, CldoVddpByteOffset = 0x1F0, CldoVddgIodByteOffset = 0x1F4,
                PptLimitByteOffset = 0x000, PptValueByteOffset = 0x004, TdcLimitByteOffset = 0x008, TdcValueByteOffset = 0x00C,
                ThmValueByteOffset = 0x014, EdcLimitByteOffset = 0x020, EdcValueByteOffset = 0x024,
                SocketPowerByteOffset = 0x074, CorePowerByteOffset = 0x0A8, SocPowerByteOffset = 0x0BC },
            0x240902 => new PmTableLayout { TableSizeBytes = 0x514, FclkByteOffset = 0xBC, UclkByteOffset = 0xC4,
                VddcrSocByteOffset = 0xB0, CldoVddpByteOffset = 0x1F0, CldoVddgIodByteOffset = 0x1F4,
                PptLimitByteOffset = 0x000, PptValueByteOffset = 0x004, TdcLimitByteOffset = 0x008, TdcValueByteOffset = 0x00C,
                ThmValueByteOffset = 0x014, EdcLimitByteOffset = 0x020, EdcValueByteOffset = 0x024,
                SocketPowerByteOffset = 0x074, CorePowerByteOffset = 0x0A8, SocPowerByteOffset = 0x0BC },
            0x000202 => new PmTableLayout { TableSizeBytes = 0x7E4, FclkByteOffset = 0xBC, UclkByteOffset = 0xC4,
                VddcrSocByteOffset = 0xB0, CldoVddpByteOffset = 0x1F0, CldoVddgIodByteOffset = 0x1F4,
                PptLimitByteOffset = 0x000, PptValueByteOffset = 0x004, TdcLimitByteOffset = 0x008, TdcValueByteOffset = 0x00C,
                ThmValueByteOffset = 0x014, EdcLimitByteOffset = 0x020, EdcValueByteOffset = 0x024,
                SocketPowerByteOffset = 0x074, CorePowerByteOffset = 0x0A8, SocPowerByteOffset = 0x0BC },

            // v3 revisions — SOC 0xB4, VDDP 0x1F4, VDDG_IOD 0x1F8
            0x240503 => new PmTableLayout { TableSizeBytes = 0xD7C, FclkByteOffset = 0xC0, UclkByteOffset = 0xC8,
                VddcrSocByteOffset = 0xB4, CldoVddpByteOffset = 0x1F4, CldoVddgIodByteOffset = 0x1F8,
                PptLimitByteOffset = 0x000, PptValueByteOffset = 0x004, TdcLimitByteOffset = 0x008, TdcValueByteOffset = 0x00C,
                ThmValueByteOffset = 0x014, EdcLimitByteOffset = 0x020, EdcValueByteOffset = 0x024,
                SocketPowerByteOffset = 0x074, CorePowerByteOffset = 0x0A8, SocPowerByteOffset = 0x0BC,
                SocTempByteOffset = 0x1CC, PeakTempByteOffset = 0x1FC },
            0x240603 => new PmTableLayout { TableSizeBytes = 0xAB0, FclkByteOffset = 0xC0, UclkByteOffset = 0xC8,
                VddcrSocByteOffset = 0xB4, CldoVddpByteOffset = 0x1F4, CldoVddgIodByteOffset = 0x1F8,
                PptLimitByteOffset = 0x000, PptValueByteOffset = 0x004, TdcLimitByteOffset = 0x008, TdcValueByteOffset = 0x00C,
                ThmValueByteOffset = 0x014, EdcLimitByteOffset = 0x020, EdcValueByteOffset = 0x024,
                SocketPowerByteOffset = 0x074, CorePowerByteOffset = 0x0A8, SocPowerByteOffset = 0x0BC,
                SocTempByteOffset = 0x1CC, PeakTempByteOffset = 0x1FC },
            0x240703 => new PmTableLayout { TableSizeBytes = 0x7E4, FclkByteOffset = 0xC0, UclkByteOffset = 0xC8,
                VddcrSocByteOffset = 0xB4, CldoVddpByteOffset = 0x1F4, CldoVddgIodByteOffset = 0x1F8,
                PptLimitByteOffset = 0x000, PptValueByteOffset = 0x004, TdcLimitByteOffset = 0x008, TdcValueByteOffset = 0x00C,
                ThmValueByteOffset = 0x014, EdcLimitByteOffset = 0x020, EdcValueByteOffset = 0x024,
                SocketPowerByteOffset = 0x074, CorePowerByteOffset = 0x0A8, SocPowerByteOffset = 0x0BC,
                SocTempByteOffset = 0x1CC, PeakTempByteOffset = 0x1FC },
            0x240803 => new PmTableLayout { TableSizeBytes = 0x7E4, FclkByteOffset = 0xC0, UclkByteOffset = 0xC8,
                VddcrSocByteOffset = 0xB4, CldoVddpByteOffset = 0x1F4, CldoVddgIodByteOffset = 0x1F8,
                PptLimitByteOffset = 0x000, PptValueByteOffset = 0x004, TdcLimitByteOffset = 0x008, TdcValueByteOffset = 0x00C,
                ThmValueByteOffset = 0x014, EdcLimitByteOffset = 0x020, EdcValueByteOffset = 0x024,
                SocketPowerByteOffset = 0x074, CorePowerByteOffset = 0x0A8, SocPowerByteOffset = 0x0BC,
                SocTempByteOffset = 0x1CC, PeakTempByteOffset = 0x1FC },
            0x240903 => new PmTableLayout { TableSizeBytes = 0x518, FclkByteOffset = 0xC0, UclkByteOffset = 0xC8,
                VddcrSocByteOffset = 0xB4, CldoVddpByteOffset = 0x1F4, CldoVddgIodByteOffset = 0x1F8,
                PptLimitByteOffset = 0x000, PptValueByteOffset = 0x004, TdcLimitByteOffset = 0x008, TdcValueByteOffset = 0x00C,
                ThmValueByteOffset = 0x014, EdcLimitByteOffset = 0x020, EdcValueByteOffset = 0x024,
                SocketPowerByteOffset = 0x074, CorePowerByteOffset = 0x0A8, SocPowerByteOffset = 0x0BC },
            0x000203 => new PmTableLayout { TableSizeBytes = 0x7E4, FclkByteOffset = 0xC0, UclkByteOffset = 0xC8,
                VddcrSocByteOffset = 0xB4, CldoVddpByteOffset = 0x1F4, CldoVddgIodByteOffset = 0x1F8,
                PptLimitByteOffset = 0x000, PptValueByteOffset = 0x004, TdcLimitByteOffset = 0x008, TdcValueByteOffset = 0x00C,
                ThmValueByteOffset = 0x014, EdcLimitByteOffset = 0x020, EdcValueByteOffset = 0x024,
                SocketPowerByteOffset = 0x074, CorePowerByteOffset = 0x0A8, SocPowerByteOffset = 0x0BC,
                SocTempByteOffset = 0x1CC, PeakTempByteOffset = 0x1FC },

            // ── Zen3 CPU ────────────────────────────────────────────────
            // 0x2D* family — same first-12 element layout as Zen2
            0x2D0008 => new PmTableLayout { TableSizeBytes = 0x1AB0, FclkByteOffset = 0xBC, UclkByteOffset = 0xC4,
                VddcrSocByteOffset = 0xB0, CldoVddpByteOffset = 0x220, CldoVddgIodByteOffset = 0x224,
                PptLimitByteOffset = 0x000, PptValueByteOffset = 0x004, TdcLimitByteOffset = 0x008, TdcValueByteOffset = 0x00C,
                ThmValueByteOffset = 0x014, EdcLimitByteOffset = 0x020, EdcValueByteOffset = 0x024,
                SocketPowerByteOffset = 0x074, CorePowerByteOffset = 0x0A8, SocPowerByteOffset = 0x0BC },
            0x2D0803 => new PmTableLayout { TableSizeBytes = 0x894, FclkByteOffset = 0xBC, UclkByteOffset = 0xC4,
                VddcrSocByteOffset = 0xB0, CldoVddpByteOffset = 0x220, CldoVddgIodByteOffset = 0x224,
                PptLimitByteOffset = 0x000, PptValueByteOffset = 0x004, TdcLimitByteOffset = 0x008, TdcValueByteOffset = 0x00C,
                ThmValueByteOffset = 0x014, EdcLimitByteOffset = 0x020, EdcValueByteOffset = 0x024,
                SocketPowerByteOffset = 0x074, CorePowerByteOffset = 0x0A8, SocPowerByteOffset = 0x0BC },
            0x2D0903 => new PmTableLayout { TableSizeBytes = 0x7E4, FclkByteOffset = 0xBC, UclkByteOffset = 0xC4,
                VddcrSocByteOffset = 0xB0, CldoVddpByteOffset = 0x220, CldoVddgIodByteOffset = 0x224,
                PptLimitByteOffset = 0x000, PptValueByteOffset = 0x004, TdcLimitByteOffset = 0x008, TdcValueByteOffset = 0x00C,
                ThmValueByteOffset = 0x014, EdcLimitByteOffset = 0x020, EdcValueByteOffset = 0x024,
                SocketPowerByteOffset = 0x074, CorePowerByteOffset = 0x0A8, SocPowerByteOffset = 0x0BC },

            // 0x38* family — SOC 0xB4, VDDP 0x224, VDDG_IOD 0x228, VDDG_CCD 0x22C
            // SocTemp at 0x1FC (index 127), same first-12 layout
            0x380005 => new PmTableLayout { TableSizeBytes = 0x1BB0, FclkByteOffset = 0xC0, UclkByteOffset = 0xC8,
                VddcrSocByteOffset = 0xB4, CldoVddpByteOffset = 0x224, CldoVddgIodByteOffset = 0x228, CldoVddgCcdByteOffset = 0x22C,
                PptLimitByteOffset = 0x000, PptValueByteOffset = 0x004, TdcLimitByteOffset = 0x008, TdcValueByteOffset = 0x00C,
                ThmValueByteOffset = 0x014, EdcLimitByteOffset = 0x020, EdcValueByteOffset = 0x024,
                SocketPowerByteOffset = 0x074, CorePowerByteOffset = 0x0A8, SocPowerByteOffset = 0x0BC,
                SocTempByteOffset = 0x1FC },
            0x380505 => new PmTableLayout { TableSizeBytes = 0xF30, FclkByteOffset = 0xC0, UclkByteOffset = 0xC8,
                VddcrSocByteOffset = 0xB4, CldoVddpByteOffset = 0x224, CldoVddgIodByteOffset = 0x228, CldoVddgCcdByteOffset = 0x22C,
                PptLimitByteOffset = 0x000, PptValueByteOffset = 0x004, TdcLimitByteOffset = 0x008, TdcValueByteOffset = 0x00C,
                ThmValueByteOffset = 0x014, EdcLimitByteOffset = 0x020, EdcValueByteOffset = 0x024,
                SocketPowerByteOffset = 0x074, CorePowerByteOffset = 0x0A8, SocPowerByteOffset = 0x0BC,
                SocTempByteOffset = 0x1FC },
            0x380605 => new PmTableLayout { TableSizeBytes = 0xC10, FclkByteOffset = 0xC0, UclkByteOffset = 0xC8,
                VddcrSocByteOffset = 0xB4, CldoVddpByteOffset = 0x224, CldoVddgIodByteOffset = 0x228, CldoVddgCcdByteOffset = 0x22C,
                PptLimitByteOffset = 0x000, PptValueByteOffset = 0x004, TdcLimitByteOffset = 0x008, TdcValueByteOffset = 0x00C,
                ThmValueByteOffset = 0x014, EdcLimitByteOffset = 0x020, EdcValueByteOffset = 0x024,
                SocketPowerByteOffset = 0x074, CorePowerByteOffset = 0x0A8, SocPowerByteOffset = 0x0BC,
                SocTempByteOffset = 0x1FC },
            0x380705 => new PmTableLayout { TableSizeBytes = 0x8F0, FclkByteOffset = 0xC0, UclkByteOffset = 0xC8,
                VddcrSocByteOffset = 0xB4, CldoVddpByteOffset = 0x224, CldoVddgIodByteOffset = 0x228, CldoVddgCcdByteOffset = 0x22C,
                PptLimitByteOffset = 0x000, PptValueByteOffset = 0x004, TdcLimitByteOffset = 0x008, TdcValueByteOffset = 0x00C,
                ThmValueByteOffset = 0x014, EdcLimitByteOffset = 0x020, EdcValueByteOffset = 0x024,
                SocketPowerByteOffset = 0x074, CorePowerByteOffset = 0x0A8, SocPowerByteOffset = 0x0BC,
                SocTempByteOffset = 0x1FC },
            0x380804 => new PmTableLayout { TableSizeBytes = 0x8A4, FclkByteOffset = 0xC0, UclkByteOffset = 0xC8,
                VddcrSocByteOffset = 0xB4, CldoVddpByteOffset = 0x224, CldoVddgIodByteOffset = 0x228, CldoVddgCcdByteOffset = 0x22C,
                PptLimitByteOffset = 0x000, PptValueByteOffset = 0x004, TdcLimitByteOffset = 0x008, TdcValueByteOffset = 0x00C,
                ThmValueByteOffset = 0x014, EdcLimitByteOffset = 0x020, EdcValueByteOffset = 0x024,
                SocketPowerByteOffset = 0x074, CorePowerByteOffset = 0x0A8, SocPowerByteOffset = 0x0BC,
                SocTempByteOffset = 0x1FC },
            0x380805 => new PmTableLayout { TableSizeBytes = 0x8F0, FclkByteOffset = 0xC0, UclkByteOffset = 0xC8,
                VddcrSocByteOffset = 0xB4, CldoVddpByteOffset = 0x224, CldoVddgIodByteOffset = 0x228, CldoVddgCcdByteOffset = 0x22C,
                PptLimitByteOffset = 0x000, PptValueByteOffset = 0x004, TdcLimitByteOffset = 0x008, TdcValueByteOffset = 0x00C,
                ThmValueByteOffset = 0x014, EdcLimitByteOffset = 0x020, EdcValueByteOffset = 0x024,
                SocketPowerByteOffset = 0x074, CorePowerByteOffset = 0x0A8, SocPowerByteOffset = 0x0BC,
                SocTempByteOffset = 0x1FC },
            0x380904 => new PmTableLayout { TableSizeBytes = 0x5A4, FclkByteOffset = 0xC0, UclkByteOffset = 0xC8,
                VddcrSocByteOffset = 0xB4, CldoVddpByteOffset = 0x224, CldoVddgIodByteOffset = 0x228, CldoVddgCcdByteOffset = 0x22C,
                PptLimitByteOffset = 0x000, PptValueByteOffset = 0x004, TdcLimitByteOffset = 0x008, TdcValueByteOffset = 0x00C,
                ThmValueByteOffset = 0x014, EdcLimitByteOffset = 0x020, EdcValueByteOffset = 0x024,
                SocketPowerByteOffset = 0x074, CorePowerByteOffset = 0x0A8, SocPowerByteOffset = 0x0BC },
            0x380905 => new PmTableLayout { TableSizeBytes = 0x5D0, FclkByteOffset = 0xC0, UclkByteOffset = 0xC8,
                VddcrSocByteOffset = 0xB4, CldoVddpByteOffset = 0x224, CldoVddgIodByteOffset = 0x228, CldoVddgCcdByteOffset = 0x22C,
                PptLimitByteOffset = 0x000, PptValueByteOffset = 0x004, TdcLimitByteOffset = 0x008, TdcValueByteOffset = 0x00C,
                ThmValueByteOffset = 0x014, EdcLimitByteOffset = 0x020, EdcValueByteOffset = 0x024,
                SocketPowerByteOffset = 0x074, CorePowerByteOffset = 0x0A8, SocPowerByteOffset = 0x0BC },
            // Generic Zen3 CPU
            0x000300 => new PmTableLayout { TableSizeBytes = 0x948, FclkByteOffset = 0xC0, UclkByteOffset = 0xC8,
                VddcrSocByteOffset = 0xB4, CldoVddpByteOffset = 0x224, CldoVddgIodByteOffset = 0x228, CldoVddgCcdByteOffset = 0x22C,
                PptLimitByteOffset = 0x000, PptValueByteOffset = 0x004, TdcLimitByteOffset = 0x008, TdcValueByteOffset = 0x00C,
                ThmValueByteOffset = 0x014, EdcLimitByteOffset = 0x020, EdcValueByteOffset = 0x024,
                SocketPowerByteOffset = 0x074, CorePowerByteOffset = 0x0A8, SocPowerByteOffset = 0x0BC,
                SocTempByteOffset = 0x1FC },

            // ── Zen4 CPU (Raphael) ─────────────────────────────────────
            // Restructured table: PPT at 0x00C, temp at 0x02C, socket power at 0x068
            // Offsets from LibreHardwareMonitor RyzenSMU.cs (v0x00540004 confirmed)
            0x540100 => new PmTableLayout { TableSizeBytes = 0x618, FclkByteOffset = 0x118, UclkByteOffset = 0x128,
                VddcrSocByteOffset = 0xD0, CldoVddpByteOffset = 0x430,
                ThmValueByteOffset = 0x02C, SocketPowerByteOffset = 0x068,
                CorePowerByteOffset = 0x050, SocPowerByteOffset = 0x054,
                TdcValueByteOffset = 0x0C0, EdcValueByteOffset = 0x0C4 },
            0x540101 => new PmTableLayout { TableSizeBytes = 0x61C, FclkByteOffset = 0x118, UclkByteOffset = 0x128,
                VddcrSocByteOffset = 0xD0, CldoVddpByteOffset = 0x430,
                ThmValueByteOffset = 0x02C, SocketPowerByteOffset = 0x068,
                CorePowerByteOffset = 0x050, SocPowerByteOffset = 0x054,
                TdcValueByteOffset = 0x0C0, EdcValueByteOffset = 0x0C4 },
            0x540102 => new PmTableLayout { TableSizeBytes = 0x66C, FclkByteOffset = 0x118, UclkByteOffset = 0x128,
                VddcrSocByteOffset = 0xD0, CldoVddpByteOffset = 0x430,
                ThmValueByteOffset = 0x02C, SocketPowerByteOffset = 0x068,
                CorePowerByteOffset = 0x050, SocPowerByteOffset = 0x054,
                TdcValueByteOffset = 0x0C0, EdcValueByteOffset = 0x0C4 },
            0x540103 => new PmTableLayout { TableSizeBytes = 0x68C, FclkByteOffset = 0x118, UclkByteOffset = 0x128,
                VddcrSocByteOffset = 0xD0, CldoVddpByteOffset = 0x430,
                ThmValueByteOffset = 0x02C, SocketPowerByteOffset = 0x068,
                CorePowerByteOffset = 0x050, SocPowerByteOffset = 0x054,
                TdcValueByteOffset = 0x0C0, EdcValueByteOffset = 0x0C4 },
            0x540104 => new PmTableLayout { TableSizeBytes = 0x6A8, FclkByteOffset = 0x118, UclkByteOffset = 0x128,
                VddcrSocByteOffset = 0xD0, CldoVddpByteOffset = 0x430,
                ThmValueByteOffset = 0x02C, SocketPowerByteOffset = 0x068,
                CorePowerByteOffset = 0x050, SocPowerByteOffset = 0x054,
                TdcValueByteOffset = 0x0C0, EdcValueByteOffset = 0x0C4 },
            0x540105 => new PmTableLayout { TableSizeBytes = 0x6B4, FclkByteOffset = 0x118, UclkByteOffset = 0x128,
                VddcrSocByteOffset = 0xD0, CldoVddpByteOffset = 0x430,
                ThmValueByteOffset = 0x02C, SocketPowerByteOffset = 0x068,
                CorePowerByteOffset = 0x050, SocPowerByteOffset = 0x054,
                TdcValueByteOffset = 0x0C0, EdcValueByteOffset = 0x0C4 },
            0x540108 => new PmTableLayout { TableSizeBytes = 0x6BC, FclkByteOffset = 0x118, UclkByteOffset = 0x128,
                VddcrSocByteOffset = 0xD0, CldoVddpByteOffset = 0x430,
                ThmValueByteOffset = 0x02C, SocketPowerByteOffset = 0x068,
                CorePowerByteOffset = 0x050, SocPowerByteOffset = 0x054,
                TdcValueByteOffset = 0x0C0, EdcValueByteOffset = 0x0C4 },
            0x540000 => new PmTableLayout { TableSizeBytes = 0x828, FclkByteOffset = 0x118, UclkByteOffset = 0x128,
                VddcrSocByteOffset = 0xD0, CldoVddpByteOffset = 0x430,
                ThmValueByteOffset = 0x02C, SocketPowerByteOffset = 0x068,
                CorePowerByteOffset = 0x050, SocPowerByteOffset = 0x054,
                TdcValueByteOffset = 0x0C0, EdcValueByteOffset = 0x0C4 },
            0x540001 => new PmTableLayout { TableSizeBytes = 0x82C, FclkByteOffset = 0x118, UclkByteOffset = 0x128,
                VddcrSocByteOffset = 0xD0, CldoVddpByteOffset = 0x430,
                ThmValueByteOffset = 0x02C, SocketPowerByteOffset = 0x068,
                CorePowerByteOffset = 0x050, SocPowerByteOffset = 0x054,
                TdcValueByteOffset = 0x0C0, EdcValueByteOffset = 0x0C4 },
            0x540002 => new PmTableLayout { TableSizeBytes = 0x87C, FclkByteOffset = 0x118, UclkByteOffset = 0x128,
                VddcrSocByteOffset = 0xD0, CldoVddpByteOffset = 0x430,
                ThmValueByteOffset = 0x02C, SocketPowerByteOffset = 0x068,
                CorePowerByteOffset = 0x050, SocPowerByteOffset = 0x054,
                TdcValueByteOffset = 0x0C0, EdcValueByteOffset = 0x0C4 },
            0x540003 => new PmTableLayout { TableSizeBytes = 0x89C, FclkByteOffset = 0x118, UclkByteOffset = 0x128,
                VddcrSocByteOffset = 0xD0, CldoVddpByteOffset = 0x430,
                ThmValueByteOffset = 0x02C, SocketPowerByteOffset = 0x068,
                CorePowerByteOffset = 0x050, SocPowerByteOffset = 0x054,
                TdcValueByteOffset = 0x0C0, EdcValueByteOffset = 0x0C4 },
            0x540004 => new PmTableLayout { TableSizeBytes = 0x8BC, FclkByteOffset = 0x118, UclkByteOffset = 0x128,
                VddcrSocByteOffset = 0xD0, CldoVddpByteOffset = 0x430,
                ThmValueByteOffset = 0x02C, SocketPowerByteOffset = 0x068,
                CorePowerByteOffset = 0x050, SocPowerByteOffset = 0x054,
                TdcValueByteOffset = 0x0C0, EdcValueByteOffset = 0x0C4 },
            0x540005 => new PmTableLayout { TableSizeBytes = 0x8C8, FclkByteOffset = 0x118, UclkByteOffset = 0x128,
                VddcrSocByteOffset = 0xD0, CldoVddpByteOffset = 0x430,
                ThmValueByteOffset = 0x02C, SocketPowerByteOffset = 0x068,
                CorePowerByteOffset = 0x050, SocPowerByteOffset = 0x054,
                TdcValueByteOffset = 0x0C0, EdcValueByteOffset = 0x0C4 },
            0x540208 => new PmTableLayout { TableSizeBytes = 0x8D0, FclkByteOffset = 0x11C, UclkByteOffset = 0x12C,
                VddcrSocByteOffset = 0xD4, CldoVddpByteOffset = 0x434,
                ThmValueByteOffset = 0x02C, SocketPowerByteOffset = 0x068,
                CorePowerByteOffset = 0x050, SocPowerByteOffset = 0x054,
                TdcValueByteOffset = 0x0C0, EdcValueByteOffset = 0x0C4 },

            _ => default  // IsValid == false
        };
    }
}
