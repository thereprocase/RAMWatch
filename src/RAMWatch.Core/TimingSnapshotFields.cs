using RAMWatch.Core.Models;

namespace RAMWatch.Core;

// ---------------------------------------------------------------------------
// TimingSnapshotFields — single source of truth for enumerating the
// TimingSnapshot record by category.
//
// TimingSnapshot has ~75 fields. Nine call sites used to enumerate subsets by
// hand, drifting in field coverage every time the record grew. This file
// collapses the enumeration into typed, AOT-safe category accessors so adding
// a new field is a one-line edit and the next caller picks it up automatically.
//
// Design constraints:
//   - AOT-safe: no reflection, no expression trees, no Activator.
//   - No boxing on the iteration hot path: each category returns its own
//     value-typed selector tuple. Generic accessors are intentionally absent.
//   - Selectors live in static readonly arrays built once at type init; calling
//     EnumerateXxx() does not allocate.
//   - Field names match the strings already used by drift, minimums, digest,
//     designations, and CSV headers. Renaming a field here is a wire-format
//     change — treat with care.
//
// Categories:
//   - Clocks         : MemClockMhz, FclkMhz, UclkMhz                      (int)
//   - Timings        : 32 integer timing fields (CL through MRD)          (int)
//   - Phy            : PHYRDL_A, PHYRDL_B                                 (int)
//   - Booleans       : GDM, Cmd2T, PowerDown                              (bool)
//   - Voltages       : VSoc, VCore, VDimm, VDDP, VDDG_IOD, VDDG_CCD,
//                      Vtt, Vpp                                           (double)
//   - SignalIntegrity: ProcODT + drive strengths + Rtt strings            (mixed —
//                      see EnumerateSignalIntegrityNumeric / Strings)
//
// What this file does NOT replace:
//   - TimingCsvLogger.FormatRow — structural, frozen header order, keep as-is.
//   - Per-call display formatting (e.g. RFC nanosecond conversion) — that
//     belongs to the view layer; this file returns raw values only.
// ---------------------------------------------------------------------------

/// <summary>
/// Categorised, allocation-free accessors for the integer/double/bool fields
/// of <see cref="TimingSnapshot"/>. See file header for design rationale.
/// </summary>
public static class TimingSnapshotFields
{
    // ── Clocks ────────────────────────────────────────────────────────────

    /// <summary>Frequency-derived integer fields. Excluded from drift "is this a
    /// timing change" checks because clocks change with profile selection, not
    /// with hand-tuning.</summary>
    public static readonly (string Name, Func<TimingSnapshot, int> Get)[] Clocks =
    [
        ("MemClockMhz", static s => s.MemClockMhz),
        ("FclkMhz",     static s => s.FclkMhz),
        ("UclkMhz",     static s => s.UclkMhz),
    ];

    // ── Timings (integer, no PHY, no booleans) ────────────────────────────

    /// <summary>The 32 integer timing fields in canonical display order:
    /// primaries → tRFC group → secondaries → turn-around → misc.
    /// PHY and booleans are separate categories — combine when needed.</summary>
    public static readonly (string Name, Func<TimingSnapshot, int> Get)[] Timings =
    [
        // Primaries
        ("CL",       static s => s.CL),
        ("RCDRD",    static s => s.RCDRD),
        ("RCDWR",    static s => s.RCDWR),
        ("RP",       static s => s.RP),
        ("RAS",      static s => s.RAS),
        ("RC",       static s => s.RC),
        ("CWL",      static s => s.CWL),
        // tRFC group
        ("RFC",      static s => s.RFC),
        ("RFC2",     static s => s.RFC2),
        ("RFC4",     static s => s.RFC4),
        // Secondaries
        ("RRDS",     static s => s.RRDS),
        ("RRDL",     static s => s.RRDL),
        ("FAW",      static s => s.FAW),
        ("WTRS",     static s => s.WTRS),
        ("WTRL",     static s => s.WTRL),
        ("WR",       static s => s.WR),
        ("RTP",      static s => s.RTP),
        ("RDRDSCL",  static s => s.RDRDSCL),
        ("WRWRSCL",  static s => s.WRWRSCL),
        // Turn-around
        ("RDRDSC",   static s => s.RDRDSC),
        ("RDRDSD",   static s => s.RDRDSD),
        ("RDRDDD",   static s => s.RDRDDD),
        ("WRWRSC",   static s => s.WRWRSC),
        ("WRWRSD",   static s => s.WRWRSD),
        ("WRWRDD",   static s => s.WRWRDD),
        ("RDWR",     static s => s.RDWR),
        ("WRRD",     static s => s.WRRD),
        // Misc
        ("REFI",     static s => s.REFI),
        ("CKE",      static s => s.CKE),
        ("STAG",     static s => s.STAG),
        ("MOD",      static s => s.MOD),
        ("MRD",      static s => s.MRD),
    ];

    // ── PHY (training artifacts, per-channel) ─────────────────────────────

    /// <summary>PHY readback levelling values per channel. Mismatch between
    /// channels is a normal training artifact, not an error. Excluded from
    /// the minimums computation but tracked in drift and digest diffs.</summary>
    public static readonly (string Name, Func<TimingSnapshot, int> Get)[] Phy =
    [
        ("PHYRDL_A", static s => s.PHYRDL_A),
        ("PHYRDL_B", static s => s.PHYRDL_B),
    ];

    // ── Boolean controller config ─────────────────────────────────────────

    /// <summary>Boolean controller-configuration fields. Drift stores these
    /// as 0/1 ints; minimums excludes them (no "minimum" of a bool).</summary>
    public static readonly (string Name, Func<TimingSnapshot, bool> Get)[] Booleans =
    [
        ("GDM",       static s => s.GDM),
        ("Cmd2T",     static s => s.Cmd2T),
        ("PowerDown", static s => s.PowerDown),
    ];

    // ── Voltages (SVI2, BIOS WMI, SMU PM table) ───────────────────────────

    /// <summary>All voltage rails in volts. Source-mixed (SVI2 telemetry vs
    /// static BIOS WMI) — consumers requiring source distinction must look
    /// at the per-field comments on TimingSnapshot.</summary>
    public static readonly (string Name, Func<TimingSnapshot, double> Get)[] Voltages =
    [
        ("VSoc",     static s => s.VSoc),
        ("VCore",    static s => s.VCore),
        ("VDimm",    static s => s.VDimm),
        ("VDDP",     static s => s.VDDP),
        ("VDDG_IOD", static s => s.VDDG_IOD),
        ("VDDG_CCD", static s => s.VDDG_CCD),
        ("Vtt",      static s => s.Vtt),
        ("Vpp",      static s => s.Vpp),
    ];

    // ── Signal integrity (numeric — drive strengths in ohms, ProcODT) ─────

    /// <summary>Numeric signal-integrity fields (ohms). Zero means unavailable
    /// — consumers must guard before displaying.</summary>
    public static readonly (string Name, Func<TimingSnapshot, double> Get)[] SignalIntegrityNumeric =
    [
        ("ProcODT",          static s => s.ProcODT),
        ("ClkDrvStren",      static s => s.ClkDrvStren),
        ("AddrCmdDrvStren",  static s => s.AddrCmdDrvStren),
        ("CsOdtCmdDrvStren", static s => s.CsOdtCmdDrvStren),
        ("CkeDrvStren",      static s => s.CkeDrvStren),
    ];

    /// <summary>String-valued signal-integrity fields (Rtt encodings, "N/M"
    /// setup-time strings). Empty string means unavailable.</summary>
    public static readonly (string Name, Func<TimingSnapshot, string> Get)[] SignalIntegrityStrings =
    [
        ("RttNom",       static s => s.RttNom),
        ("RttWr",        static s => s.RttWr),
        ("RttPark",      static s => s.RttPark),
        ("AddrCmdSetup", static s => s.AddrCmdSetup),
        ("CsOdtSetup",   static s => s.CsOdtSetup),
        ("CkeSetup",     static s => s.CkeSetup),
    ];

    // ── Combined name→int dispatch ────────────────────────────────────────

    /// <summary>
    /// Return a named integer field as int. Booleans are projected to 0/1.
    /// Unknown field names return null — callers should skip nulls rather
    /// than throw, since field sets vary by call site.
    ///
    /// Covers Clocks ∪ Timings ∪ Phy ∪ Booleans. Voltages and signal
    /// integrity are intentionally excluded — they aren't integers and
    /// shouldn't be coerced to one. If a caller needs them, use the
    /// category-typed enumerators above.
    /// </summary>
    public static int? GetIntField(TimingSnapshot s, string name) => name switch
    {
        // Clocks
        "MemClockMhz" => s.MemClockMhz,
        "FclkMhz"     => s.FclkMhz,
        "UclkMhz"     => s.UclkMhz,
        // Primaries
        "CL"          => s.CL,
        "RCDRD"       => s.RCDRD,
        "RCDWR"       => s.RCDWR,
        "RP"          => s.RP,
        "RAS"         => s.RAS,
        "RC"          => s.RC,
        "CWL"         => s.CWL,
        // tRFC
        "RFC"         => s.RFC,
        "RFC2"        => s.RFC2,
        "RFC4"        => s.RFC4,
        // Secondaries
        "RRDS"        => s.RRDS,
        "RRDL"        => s.RRDL,
        "FAW"         => s.FAW,
        "WTRS"        => s.WTRS,
        "WTRL"        => s.WTRL,
        "WR"          => s.WR,
        "RTP"         => s.RTP,
        "RDRDSCL"     => s.RDRDSCL,
        "WRWRSCL"     => s.WRWRSCL,
        // Turn-around
        "RDRDSC"      => s.RDRDSC,
        "RDRDSD"      => s.RDRDSD,
        "RDRDDD"      => s.RDRDDD,
        "WRWRSC"      => s.WRWRSC,
        "WRWRSD"      => s.WRWRSD,
        "WRWRDD"      => s.WRWRDD,
        "RDWR"        => s.RDWR,
        "WRRD"        => s.WRRD,
        // Misc
        "REFI"        => s.REFI,
        "CKE"         => s.CKE,
        "STAG"        => s.STAG,
        "MOD"         => s.MOD,
        "MRD"         => s.MRD,
        // PHY
        "PHYRDL_A"    => s.PHYRDL_A,
        "PHYRDL_B"    => s.PHYRDL_B,
        // Booleans projected to 0/1
        "GDM"         => s.GDM       ? 1 : 0,
        "Cmd2T"       => s.Cmd2T     ? 1 : 0,
        "PowerDown"   => s.PowerDown ? 1 : 0,
        _             => null,
    };

    // ── Equality helpers ──────────────────────────────────────────────────

    /// <summary>
    /// True iff every tuning-relevant field is equal between a and b. Tuning
    /// relevance includes clocks, all integer timings, PHY, and booleans.
    /// Voltages and signal integrity are deliberately excluded — voltage
    /// drift on the SVI2 telemetry rails is sub-millivolt-noisy and does
    /// not represent a tuning change. See timing-snapshot-refactor.md.
    /// </summary>
    public static bool TuningEqual(TimingSnapshot a, TimingSnapshot b)
    {
        foreach (var (_, get) in Clocks)
            if (get(a) != get(b)) return false;
        foreach (var (_, get) in Timings)
            if (get(a) != get(b)) return false;
        foreach (var (_, get) in Phy)
            if (get(a) != get(b)) return false;
        foreach (var (_, get) in Booleans)
            if (get(a) != get(b)) return false;
        return true;
    }
}
