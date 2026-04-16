using RAMWatch.Core;
using RAMWatch.Core.Models;

namespace RAMWatch.Service.Services;

/// <summary>
/// Computes per-frequency minimum timing values across snapshots.
/// Pure function — no state, no I/O. Called by StateAggregator.
/// </summary>
public static class MinimumComputer
{
    /// <summary>
    /// Timing fields where higher is better (tREFI: longer refresh interval = less overhead).
    /// All other integer timing fields are lower-is-better.
    /// </summary>
    private static readonly HashSet<string> HigherIsBetter = new(StringComparer.Ordinal) { "REFI" };

    /// <summary>
    /// Fields excluded from minimums (voltages, PHY training artifacts, booleans).
    /// </summary>
    private static readonly HashSet<string> ExcludedFields = new(StringComparer.Ordinal)
    {
        "PHYRDL_A", "PHYRDL_B"
    };

    /// <summary>
    /// All integer timing field names tracked for minimums.
    /// </summary>
    private static readonly string[] TimingFields =
    [
        "CL", "RCDRD", "RCDWR", "RP", "RAS", "RC", "CWL",
        "RFC", "RFC2", "RFC4",
        "RRDS", "RRDL", "FAW", "WTRS", "WTRL", "WR", "RTP",
        "RDRDSCL", "WRWRSCL",
        "RDRDSC", "RDRDSD", "RDRDDD", "WRWRSC", "WRWRSD", "WRWRDD",
        "RDWR", "WRRD",
        "REFI", "CKE", "STAG", "MOD", "MRD"
    ];

    /// <summary>
    /// Compute minimums for each frequency bucket.
    /// </summary>
    /// <param name="snapshots">All snapshots (will be filtered by frequency).</param>
    /// <param name="validations">All validation results (to identify validated snapshots).</param>
    /// <param name="eraId">Filter to this era. Null = all eras.</param>
    public static List<FrequencyMinimums> Compute(
        IReadOnlyList<TimingSnapshot> snapshots,
        IReadOnlyList<ValidationResult> validations,
        string? eraId = null)
    {
        // Build lookup: which snapshot IDs have a passing validation?
        var passedIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var v in validations)
        {
            if (v.Passed && !string.IsNullOrEmpty(v.ActiveSnapshotId))
            {
                if (eraId is null || v.EraId == eraId)
                    passedIds.Add(v.ActiveSnapshotId);
            }
        }

        // Filter snapshots by era and plausibility (CL > 0 = real hardware read).
        var filtered = snapshots
            .Where(s => s.CL > 0 && s.MemClockMhz > 0)
            .Where(s => eraId is null || s.EraId == eraId);

        // Group by MemClockMhz.
        var groups = filtered.GroupBy(s => s.MemClockMhz);

        var result = new List<FrequencyMinimums>();

        foreach (var group in groups)
        {
            var bestPosted = new Dictionary<string, int>(StringComparer.Ordinal);
            var bestValidated = new Dictionary<string, int>(StringComparer.Ordinal);
            int postedCount = 0;
            int validatedCount = 0;

            foreach (var snap in group)
            {
                postedCount++;
                bool isValidated = !string.IsNullOrEmpty(snap.SnapshotId) &&
                                   passedIds.Contains(snap.SnapshotId);
                if (isValidated) validatedCount++;

                foreach (string field in TimingFields)
                {
                    if (ExcludedFields.Contains(field)) continue;

                    int value = TimingSnapshotFields.GetIntField(snap, field) ?? 0;
                    if (value == 0) continue; // Skip unset fields

                    bool higherBetter = HigherIsBetter.Contains(field);

                    // Update posted minimum
                    if (!bestPosted.TryGetValue(field, out int currentBest) ||
                        (higherBetter ? value > currentBest : value < currentBest))
                    {
                        bestPosted[field] = value;
                    }

                    // Update validated minimum
                    if (isValidated)
                    {
                        if (!bestValidated.TryGetValue(field, out int currentValBest) ||
                            (higherBetter ? value > currentValBest : value < currentValBest))
                        {
                            bestValidated[field] = value;
                        }
                    }
                }
            }

            result.Add(new FrequencyMinimums
            {
                MemClockMhz = group.Key,
                PostedBootCount = postedCount,
                ValidatedBootCount = validatedCount,
                BestPosted = bestPosted,
                BestValidated = bestValidated
            });
        }

        // Sort by frequency descending (highest first in the dropdown).
        result.Sort((a, b) => b.MemClockMhz.CompareTo(a.MemClockMhz));

        return result;
    }

}
