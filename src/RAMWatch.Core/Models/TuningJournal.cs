namespace RAMWatch.Core.Models;

// ---------------------------------------------------------------------------
// Phase 3 data models — defined now so the on-disk schema is stable before
// Phase 3 implementation begins. Service writes these; GUI reads them via IPC.
// All types are registered in RamWatchJsonContext for source-generated JSON.
// ---------------------------------------------------------------------------

/// <summary>
/// A point-in-time snapshot of the active timing configuration.
/// Captured before/after a config change and at every LKG promotion.
/// Phase 2 fills hardware fields; Phase 1 may persist a stub with only
/// the boot ID and timestamp.
/// </summary>
public sealed record TimingSnapshot
{
    public required string SnapshotId { get; init; }    // stable across renames
    public required DateTime Timestamp { get; init; }
    public required string BootId { get; init; }
    public int SchemaVersion { get; init; } = 1;

    // --- Clocks (Phase 2) ---
    public int MemClockMhz { get; init; }               // MCLK (half-rate DDR4)
    public int FclkMhz { get; init; }                   // FCLK (infinity fabric)
    public int UclkMhz { get; init; }                   // UCLK (unified memory controller)

    // --- Primary timings (Phase 2) ---
    public int TCL { get; init; }
    public int TRCD { get; init; }
    public int TRP { get; init; }
    public int TRAS { get; init; }
    public int TRC { get; init; }
    public int TRFC1Ns { get; init; }                   // nanoseconds; -1 = tRFC1 readback bug
    public int TRFC2Ns { get; init; }
    public int TREFI { get; init; }
    public int TRRDSc { get; init; }                    // same-bank row-to-row, short
    public int TRRDLc { get; init; }                    // same-bank row-to-row, long
    public int TFAWSc { get; init; }
    public int TCKE { get; init; }
    public int TWTRL { get; init; }
    public int TWTRS { get; init; }
    public int TRDRD { get; init; }
    public int TWRWR { get; init; }

    // --- Secondary timings (Phase 2) ---
    public int TRTP { get; init; }
    public int TWR { get; init; }
    public int TMOD { get; init; }
    public int TMRD { get; init; }
    public int TZQCS { get; init; }
    public int TCWL { get; init; }

    // --- Voltages (Phase 2) ---
    /// <summary>VDIMM in volts. 0 = not read (MSI/WMI path returns static value — label accordingly).</summary>
    public double VDimmV { get; init; }
    /// <summary>SoC voltage from SVI2 registers. 0 = not read.</summary>
    public double VSocV { get; init; }

    // --- Controller config (Phase 2) ---
    public string GearMode { get; init; } = "";         // "gear1" | "gear2" | "gear4"
    public string FgrMode { get; init; } = "";          // "fixed1x" | "fixed2x" | "on_the_fly"
    public string CpuCodename { get; init; } = "";      // e.g. "Vermeer", "Raphael"
    public string AgesaVersion { get; init; } = "";     // e.g. "ComboAM4v2PI 1.2.0.x"
    public string BiosVersion { get; init; } = "";

    // --- User metadata (Phase 3) ---
    public string Label { get; init; } = "";            // user-assigned label, may be empty
    public string Notes { get; init; } = "";
}

/// <summary>
/// A detected or user-noted change to the timing configuration between boots.
/// Stores the before/after delta plus a full after-snapshot so the Timeline
/// tab can reconstruct the state at any point in history.
/// </summary>
public sealed record ConfigChange
{
    public required string ChangeId { get; init; }
    public required DateTime Timestamp { get; init; }
    public required string BootId { get; init; }
    public int SchemaVersion { get; init; } = 1;

    /// <summary>
    /// Per-timing delta. Key = timing name (e.g. "TCL"), value = before/after pair.
    /// Empty dict = user-initiated manual record with no detected hardware delta.
    /// </summary>
    public required Dictionary<string, TimingDelta> Changes { get; init; }

    /// <summary>Full snapshot captured immediately after the change was detected.</summary>
    public required TimingSnapshot SnapshotAfter { get; init; }

    /// <summary>Optional: snapshot from the previous boot for full before/after diff.</summary>
    public TimingSnapshot? SnapshotBefore { get; init; }

    /// <summary>User-supplied notes, editable after the fact via annotateChange IPC message.</summary>
    public string UserNotes { get; init; } = "";

    /// <summary>true = detected automatically by ConfigChangeDetector; false = user-initiated.</summary>
    public bool IsAutoDetected { get; init; }
}

/// <summary>
/// Before/after pair for a single timing field in a ConfigChange record.
/// </summary>
public sealed record TimingDelta(int Before, int After);

/// <summary>
/// A drift event: a timing value shifted outside its expected stable range
/// as measured across the 20-boot rolling window.
/// </summary>
public sealed record DriftEvent
{
    public required string DriftId { get; init; }
    public required DateTime Timestamp { get; init; }
    public required string BootId { get; init; }
    public int SchemaVersion { get; init; } = 1;

    /// <summary>Timing register name as it appears in TimingSnapshot (e.g. "TCL", "TREFI").</summary>
    public required string TimingName { get; init; }

    /// <summary>
    /// Expected stable value — median or mode across the window, depending on
    /// DriftDetector configuration (median is more robust to single outliers).
    /// </summary>
    public required int ExpectedValue { get; init; }

    /// <summary>Observed value in the current boot.</summary>
    public required int ActualValue { get; init; }

    /// <summary>How many of the last N boots were used to compute the baseline.</summary>
    public required int WindowBootCount { get; init; }

    /// <summary>
    /// Fraction of window boots where this timing matched the expected value.
    /// 1.0 = perfectly stable before this event; lower = already noisy baseline.
    /// </summary>
    public required double WindowStabilityRatio { get; init; }
}

/// <summary>
/// Outcome record for a single validation test run (MemTest86, OCCT, TestMem, etc.).
/// Linked to the active TimingSnapshot so history is queryable by config.
/// </summary>
public sealed record ValidationResult
{
    public required string ResultId { get; init; }
    public required DateTime Timestamp { get; init; }
    public required string BootId { get; init; }
    public int SchemaVersion { get; init; } = 1;

    /// <summary>Test tool name. Free-text but expected values: "MemTest86", "OCCT", "TestMem", "Prime95", "custom".</summary>
    public required string Tool { get; init; }

    /// <summary>What was being measured, e.g. "memory_stability", "max_bandwidth", "prime95_blend".</summary>
    public required string Metric { get; init; }

    public required bool Passed { get; init; }

    /// <summary>Total errors reported by the tool. 0 on pass; non-zero on fail.</summary>
    public required int ErrorCount { get; init; }

    /// <summary>Wall-clock test duration. TimeSpan.Zero if not tracked.</summary>
    public required TimeSpan Duration { get; init; }

    /// <summary>The timing config under test. Snapshot ID only — full record stays in snapshots.json.</summary>
    public required string ActiveSnapshotId { get; init; }

    public string Notes { get; init; } = "";
}

/// <summary>
/// How a timing name was designated. Drives the Timeline display and
/// change-detection sensitivity (Manual entries never auto-update).
/// </summary>
public enum TimingDesignation
{
    /// <summary>User explicitly named this timing (locked — auto-detection skips it).</summary>
    Manual,
    /// <summary>Name assigned by ConfigChangeDetector heuristics.</summary>
    Auto,
    /// <summary>No designation yet; shows as "—" in the Timings tab.</summary>
    Unknown
}

/// <summary>
/// Wraps the per-timing designation dictionary with schema versioning.
/// Persisted to %ProgramData%\RAMWatch\designations.json.
/// Service is the sole writer (write-to-temp-then-rename per B7).
/// </summary>
public sealed class DesignationMap
{
    public int SchemaVersion { get; set; } = 1;

    /// <summary>
    /// Key = timing name matching TimingSnapshot property names (case-sensitive).
    /// Value = designation for that timing name.
    /// Missing key is treated as Unknown — never throw on lookup, use TryGetValue.
    /// </summary>
    public Dictionary<string, TimingDesignation> Designations { get; set; } = new();
}
