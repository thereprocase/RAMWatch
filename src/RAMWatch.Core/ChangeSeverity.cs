using RAMWatch.Core.Models;

namespace RAMWatch.Core;

/// <summary>
/// Severity classification for a <see cref="ConfigChange"/>. The timeline
/// treats Major changes as first-class rows and coalesces consecutive
/// Minor changes within the same boot into one synthetic row — most
/// Minor changes are per-boot auto-retraining of secondary timings and
/// they drown the logbook when surfaced individually.
/// </summary>
public enum ChangeSeverity
{
    /// <summary>No fields in the delta are classified as major.</summary>
    Minor,

    /// <summary>At least one primary timing, voltage, clock, or controller
    /// boolean moved. Always surfaced as its own timeline row.</summary>
    Major,
}

/// <summary>
/// Classifies the fields of a <see cref="ConfigChange"/> by severity.
///
/// The major-field set is intentionally conservative: anything a human
/// would have deliberately typed into the BIOS (primary timings, voltages,
/// frequency, controller mode), plus tRFC which drives refresh behaviour
/// hard enough to matter even when the human didn't touch it.
/// </summary>
public static class ChangeSeverityClassifier
{
    /// <summary>
    /// Field names that mark a ConfigChange as Major when present in the
    /// delta. Matches <see cref="TimingSnapshotFields"/> canonical names.
    /// </summary>
    public static readonly HashSet<string> MajorFields = new(StringComparer.Ordinal)
    {
        // Primaries — the CL16-20-20-42 numbers a tuner actually sets.
        "CL", "RCDRD", "RCDWR", "RP", "RAS", "RC", "CWL",
        // tRFC group — drives refresh interval; errors here are instant WHEA.
        "RFC", "RFC2", "RFC4",
        // Clocks — DDR speed, infinity fabric.
        "MemClockMhz", "FclkMhz", "UclkMhz",
        // Voltages — every rail is major. Auto-retraining never touches these.
        "VSoc", "VCore", "VDimm", "VDDP", "VDDG_IOD", "VDDG_CCD", "Vtt", "Vpp",
        // Controller booleans — geardown, 1T/2T, power-down mode.
        "GDM", "Cmd2T", "PowerDown",
    };

    /// <summary>
    /// Classify a single change by the set of field names in its delta.
    /// </summary>
    public static ChangeSeverity Classify(IEnumerable<string> deltaKeys)
    {
        foreach (var k in deltaKeys)
        {
            if (MajorFields.Contains(k))
                return ChangeSeverity.Major;
        }
        return ChangeSeverity.Minor;
    }

    /// <summary>
    /// Classify a <see cref="ConfigChange"/> by inspecting its delta keys.
    /// </summary>
    public static ChangeSeverity Classify(ConfigChange change)
        => Classify(change.Changes.Keys);
}
