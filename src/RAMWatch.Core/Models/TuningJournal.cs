namespace RAMWatch.Core.Models;

// ---------------------------------------------------------------------------
// Phase 3 data models — defined now so the on-disk schema is stable before
// Phase 3 implementation begins. Service writes these; GUI reads them via IPC.
// All types are registered in RamWatchJsonContext for source-generated JSON.
// ---------------------------------------------------------------------------

// ---------------------------------------------------------------------------
// Thermal/power telemetry — volatile sensor data from the SMU power table
// and direct SMN register reads. Separate from TimingSnapshot because this
// is real-time telemetry, not a tuning configuration.
// ---------------------------------------------------------------------------

/// <summary>
/// Real-time thermal and power telemetry from the AMD SMU.
/// All fields default to 0, which means "not available" — the service
/// populates only what it can read for the detected CPU generation.
/// Sent alongside TimingSnapshot in ServiceState but not persisted in
/// the snapshot journal (telemetry is ephemeral, timings are permanent).
/// </summary>
public sealed class ThermalPowerSnapshot
{
    public DateTime Timestamp { get; set; }

    // --- CPU temperatures (degrees C) ---
    /// <summary>Tctl/Tdie — primary CPU temp. Read from SMN 0x59800 (all Zen).</summary>
    public double CpuTempC { get; set; }
    /// <summary>Per-CCD temperatures (Zen 2+). Null or empty if unavailable.</summary>
    public double[]? CcdTempsC { get; set; }
    /// <summary>SoC die temperature from the PM table.</summary>
    public double SocTempC { get; set; }
    /// <summary>Peak temperature observed by the SMU since last reset.</summary>
    public double PeakTempC { get; set; }

    // --- Power (watts) ---
    /// <summary>Total socket/package power draw.</summary>
    public double SocketPowerW { get; set; }
    /// <summary>CPU core power (VDDCR_CPU rail).</summary>
    public double CorePowerW { get; set; }
    /// <summary>SoC power (VDDCR_SOC rail).</summary>
    public double SocPowerW { get; set; }

    // --- Current limits (amps) ---
    /// <summary>PPT limit — platform power target.</summary>
    public double PptLimitW { get; set; }
    /// <summary>PPT actual — current platform power consumption.</summary>
    public double PptActualW { get; set; }
    /// <summary>TDC limit — sustained current limit.</summary>
    public double TdcLimitA { get; set; }
    /// <summary>TDC actual — sustained current draw.</summary>
    public double TdcActualA { get; set; }
    /// <summary>EDC limit — peak current limit.</summary>
    public double EdcLimitA { get; set; }
    /// <summary>EDC actual — peak current draw.</summary>
    public double EdcActualA { get; set; }

    // --- Source tracking ---
    /// <summary>
    /// Which data sources contributed to this snapshot.
    /// Helps the consumer know what to trust vs what's absent.
    /// </summary>
    public ThermalDataSource Sources { get; set; }
}

/// <summary>
/// Flags indicating which data sources successfully contributed readings.
/// A consumer can check these before displaying fields that may be zero.
/// </summary>
[Flags]
public enum ThermalDataSource
{
    None = 0,
    /// <summary>Direct SMN register read for Tctl (0x59800). Works on all Zen.</summary>
    SmnTctl = 1,
    /// <summary>Per-CCD temperature registers (0x59954/0x59B08). Zen 2+.</summary>
    SmnCcdTemp = 2,
    /// <summary>SMU PM table fields (power, limits, SoC temp).</summary>
    PmTable = 4,
}

/// <summary>
/// A point-in-time snapshot of the active timing configuration.
/// Field names use the community-standard short names (CL, RCDRD, etc.)
/// that match what users see in BIOS and ZenTimings.
/// </summary>
public sealed class TimingSnapshot
{
    public required string SnapshotId { get; set; }
    public required DateTime Timestamp { get; init; }
    public required string BootId { get; init; }
    public int SchemaVersion { get; init; } = 1;

    // --- Clocks ---
    public int MemClockMhz { get; set; }    // MCLK (half DDR rate)
    public int FclkMhz { get; set; }        // FCLK (infinity fabric)
    public int UclkMhz { get; set; }        // UCLK (memory controller)

    // --- Primaries ---
    public int CL { get; set; }
    public int RCDRD { get; set; }
    public int RCDWR { get; set; }
    public int RP { get; set; }
    public int RAS { get; set; }
    public int RC { get; set; }
    public int CWL { get; set; }

    // --- tRFC group ---
    public int RFC { get; set; }            // tRFC1 in clocks
    public int RFC2 { get; set; }
    public int RFC4 { get; set; }
    /// <summary>
    /// True when the UMC decode fell back to the 0x50264 tRFC register
    /// because 0x50260 returned the ComboAM4v2PI 1.2.0.x magic value
    /// (0x21060138). Surfaces to the UI so users on buggy AGESA know
    /// the displayed tRFC values came from the workaround path.
    /// </summary>
    public bool TrfcReadbackBugDetected { get; set; }

    // --- Secondaries ---
    public int RRDS { get; set; }
    public int RRDL { get; set; }
    public int FAW { get; set; }
    public int WTRS { get; set; }
    public int WTRL { get; set; }
    public int WR { get; set; }
    public int RTP { get; set; }
    public int RDRDSCL { get; set; }
    public int WRWRSCL { get; set; }

    // --- Turn-around ---
    public int RDRDSC { get; set; }
    public int RDRDSD { get; set; }
    public int RDRDDD { get; set; }
    public int WRWRSC { get; set; }
    public int WRWRSD { get; set; }
    public int WRWRDD { get; set; }
    public int RDWR { get; set; }
    public int WRRD { get; set; }

    // --- Misc ---
    public int REFI { get; set; }           // tREFI
    public int CKE { get; set; }
    public int STAG { get; set; }
    public int MOD { get; set; }
    public int MRD { get; set; }

    // --- PHY (per-channel, training artifacts) ---
    public int PHYRDL_A { get; set; }       // channel A
    public int PHYRDL_B { get; set; }       // channel B — mismatch is normal

    // --- Controller config ---
    public bool GDM { get; set; }
    public bool Cmd2T { get; set; }         // true = 2T, false = 1T
    public bool PowerDown { get; set; }

    // --- Voltages ---
    public double VSoc { get; set; }        // SVI2 telemetry (volts)
    public double VCore { get; set; }       // SVI2 telemetry (volts)
    public double VDimm { get; set; }       // BIOS WMI — DRAM voltage (volts)
    public double VDDP { get; set; }        // SMU power table — PLL supply (volts)
    public double VDDG_IOD { get; set; }    // SMU power table — I/O die (volts)
    public double VDDG_CCD { get; set; }    // SMU power table — core complex die (volts)
    public double Vtt { get; set; }         // BIOS WMI — termination rail (volts)
    public double Vpp { get; set; }         // BIOS WMI — pump charge rail (volts)

    // --- Signal integrity (BIOS WMI, APCB buffer) ---
    public double ProcODT { get; set; }              // Ohms (0 = unavailable)
    public string RttNom { get; set; } = "";          // "RZQ/4", "Disabled", etc.
    public string RttWr { get; set; } = "";           // "RZQ/2", "Off", etc.
    public string RttPark { get; set; } = "";         // "RZQ/4", etc.
    public double ClkDrvStren { get; set; }           // Ohms (0 = unavailable)
    public double AddrCmdDrvStren { get; set; }       // Ohms
    public double CsOdtCmdDrvStren { get; set; }     // Ohms
    public double CkeDrvStren { get; set; }           // Ohms
    public string AddrCmdSetup { get; set; } = "";    // "N/M" format
    public string CsOdtSetup { get; set; } = "";
    public string CkeSetup { get; set; } = "";

    // --- System info ---
    public string CpuCodename { get; set; } = "";
    public string AgesaVersion { get; set; } = "";
    public string BiosVersion { get; set; } = "";

    // --- User metadata (Phase 3) ---
    public string Label { get; set; } = "";
    public string Notes { get; set; } = "";

    // --- Era tagging ---
    public string? EraId { get; set; }

    /// <summary>
    /// Returns a shallow copy with the supplied SnapshotId and Label.
    /// MemberwiseClone is correct here because all fields are value types
    /// or immutable strings — no mutable reference types to worry about.
    /// This eliminates the 72-line hand-enumerated copy that silently
    /// dropped any field not explicitly listed (Gandalf's top concern).
    /// </summary>
    public TimingSnapshot WithIdAndLabel(string snapshotId, string label)
    {
        var copy = (TimingSnapshot)MemberwiseClone();
        copy.SnapshotId = snapshotId;
        copy.Label = label;
        return copy;
    }
}

/// <summary>
/// A detected change to the timing configuration between boots.
/// </summary>
public sealed record ConfigChange
{
    public required string ChangeId { get; init; }
    public required DateTime Timestamp { get; init; }
    public required string BootId { get; init; }
    public required Dictionary<string, TimingDelta> Changes { get; init; }
    public string? SnapshotBeforeId { get; init; }
    public string? SnapshotAfterId { get; init; }
    public string? UserNotes { get; init; }
    public string? EraId { get; init; }
}

public sealed record TimingDelta(string Before, string After);

/// <summary>
/// Drift in an auto-trained timing across boots.
/// </summary>
public sealed record DriftEvent
{
    public required DateTime Timestamp { get; init; }
    public required string BootId { get; init; }
    public required string TimingName { get; init; }
    public required int ExpectedValue { get; init; }
    public required int ActualValue { get; init; }
    public required int BootsAtExpected { get; init; }
    public required int BootsAtActual { get; init; }
    public int WindowBootCount { get; init; }
    public double WindowStabilityRatio { get; init; }
}

/// <summary>
/// A stability test result linked to the snapshot that was active.
/// Uses ActiveSnapshotId (foreign key) not an embedded copy —
/// keeps git diffs clean when the snapshot list grows.
/// </summary>
public sealed record ValidationResult
{
    /// <summary>
    /// Stable identifier for this result. Auto-generated on construction.
    /// Used by DeleteValidationMessage to target a specific entry.
    /// </summary>
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public required DateTime Timestamp { get; init; }
    public required string BootId { get; init; }
    public required string TestTool { get; init; }
    public required string MetricName { get; init; }
    public required double MetricValue { get; init; }
    public required string MetricUnit { get; init; }
    public required bool Passed { get; init; }
    public int ErrorCount { get; init; }
    public int DurationMinutes { get; init; }
    public string? ActiveSnapshotId { get; init; }
    public string? Notes { get; init; }
    public string? EraId { get; init; }
}

// ---------------------------------------------------------------------------
// Tuning Eras — named campaigns for organizing tuning sessions
// ---------------------------------------------------------------------------

/// <summary>
/// A named tuning campaign. Active era (EndTimestamp == null) automatically
/// tags all new snapshots, validations, and boot-fail entries with its EraId.
/// Only one era may be active at a time.
/// </summary>
public sealed class TuningEra
{
    public required string EraId { get; init; }
    public required string Name { get; set; }
    public required DateTime StartTimestamp { get; init; }
    public DateTime? EndTimestamp { get; set; }
    public string Notes { get; set; } = "";
}

// ---------------------------------------------------------------------------
// Boot Fail Entries — manually logged failed boot attempts
// ---------------------------------------------------------------------------

/// <summary>
/// A failed boot attempt that RAMWatch could not observe because the service
/// wasn't running. Logged manually by the user after recovering.
/// AttemptedChanges records what the user was trying relative to BaseSnapshotId.
/// </summary>
public sealed class BootFailEntry
{
    public required string BootFailId { get; init; }
    public required DateTime Timestamp { get; init; }
    public required BootFailKind Kind { get; init; }
    public string? BaseSnapshotId { get; init; }
    public Dictionary<string, string>? AttemptedChanges { get; init; }
    public string Notes { get; set; } = "";
    public string? EraId { get; init; }
}

public enum BootFailKind
{
    NoPost,
    BootLoop,
    Unstable,
    Other
}

// ---------------------------------------------------------------------------
// Frequency Minimums — aggregated tightest values per frequency
// ---------------------------------------------------------------------------

/// <summary>
/// Per-frequency minimum observed values for each timing across snapshots.
/// Computed service-side and sent in the state push.
/// </summary>
public sealed class FrequencyMinimums
{
    public required int MemClockMhz { get; init; }
    public required int PostedBootCount { get; init; }
    public required int ValidatedBootCount { get; init; }
    /// <summary>Tightest value seen across all posted snapshots at this frequency.</summary>
    public required Dictionary<string, int> BestPosted { get; init; }
    /// <summary>Tightest value seen across snapshots linked to a passing validation.</summary>
    public required Dictionary<string, int> BestValidated { get; init; }
}

public enum TimingDesignation
{
    Unknown,
    Manual,
    Auto
}

/// <summary>
/// Persisted map of timing name to manual/auto/unknown designation.
/// </summary>
public sealed class DesignationMap
{
    public int SchemaVersion { get; set; } = 1;
    public DateTime LastUpdated { get; set; }
    public Dictionary<string, TimingDesignation> Designations { get; set; } = new();
}

// ---------------------------------------------------------------------------
// DriftDetector persistence types
// ---------------------------------------------------------------------------

/// <summary>
/// Persisted rolling window of timing values across the last N boots.
/// Written by DriftDetector; read back on service startup.
/// </summary>
public sealed class DriftWindow
{
    public int SchemaVersion { get; set; } = 1;
    public List<BootEntry> Boots { get; set; } = new();
}

/// <summary>
/// One entry in the drift window: the timing values observed during a single boot.
/// Boolean fields (GDM, Cmd2T, PowerDown) are stored as 0/1.
/// </summary>
public sealed class BootEntry
{
    public required string BootId { get; init; }
    public required DateTime Timestamp { get; init; }
    public required Dictionary<string, int> Values { get; init; }
}
