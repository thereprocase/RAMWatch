using RAMWatch.Core.Models;

namespace RAMWatch.Core;

/// <summary>
/// Builds the human-readable display label shown in the snapshot comparison dropdowns.
///
/// Priority order:
///   1. Custom label (not prefixed "Auto" or "Manual") — shown as-is.
///   2. Matching passing validation — "{M/d} {TestTool} {MetricValue}{MetricUnit} PASS"
///   3. Matching failing validation — "{M/d} {TestTool} FAIL ({n} errors)"
///   4. Fallback to the snapshot's stored label (or a timestamp-based placeholder
///      when the label is empty).
///
/// When multiple validations reference the same snapshot, the most recent one wins.
/// </summary>
public static class SnapshotDisplayName
{
    /// <summary>
    /// Builds a display label for a single snapshot entry.
    /// </summary>
    /// <param name="snapshot">The snapshot to label.</param>
    /// <param name="lookup">
    ///   Pre-built dictionary keyed by ActiveSnapshotId, containing the most
    ///   recent ValidationResult for each snapshot. Pass null or empty when no
    ///   validation data is available.
    /// </param>
    public static string Build(
        TimingSnapshot snapshot,
        IReadOnlyDictionary<string, ValidationResult>? lookup)
    {
        // Rule 1: user-supplied label that is neither an auto-save nor a "Manual" prefix.
        // Auto-saves are labelled "Auto yyyy-MM-dd HH:mm"; manual saves are "Manual ...".
        // Any other non-empty label is a custom designation — show it verbatim.
        if (!string.IsNullOrEmpty(snapshot.Label)
            && !snapshot.Label.StartsWith("Auto ", StringComparison.Ordinal)
            && !snapshot.Label.StartsWith("Manual ", StringComparison.Ordinal))
        {
            return snapshot.Label;
        }

        // Rules 2 & 3: look for a validation result linked to this snapshot.
        if (lookup is not null
            && lookup.TryGetValue(snapshot.SnapshotId, out var validation))
        {
            var date = validation.Timestamp.ToLocalTime().ToString("M/d");

            if (validation.Passed)
            {
                // Format metric value: strip trailing zeros for whole numbers.
                var metricStr = validation.MetricValue % 1 == 0
                    ? ((long)validation.MetricValue).ToString()
                    : validation.MetricValue.ToString("G6");

                return $"{date} {validation.TestTool} {metricStr}{validation.MetricUnit} PASS";
            }
            else
            {
                var errorSuffix = validation.ErrorCount > 0
                    ? $" ({validation.ErrorCount} error{(validation.ErrorCount != 1 ? "s" : "")})"
                    : "";
                return $"{date} {validation.TestTool} FAIL{errorSuffix}";
            }
        }

        // Rule 4: fallback — original label or timestamp placeholder.
        return !string.IsNullOrEmpty(snapshot.Label)
            ? snapshot.Label
            : $"Snapshot {snapshot.Timestamp.ToLocalTime():MM/dd HH:mm}";
    }

    /// <summary>
    /// Builds the display label for the LKG entry.
    ///
    /// If a validation result qualifies the LKG snapshot, the returned name is
    /// "LKG ({TestTool} {MetricValue}{MetricUnit} PASS)" — e.g. "LKG (Karhu 12400% PASS)".
    /// Falls back to plain "LKG" when no validation is linked.
    /// </summary>
    public static string BuildLkg(
        TimingSnapshot lkg,
        IReadOnlyDictionary<string, ValidationResult>? lookup)
    {
        if (lookup is not null
            && lookup.TryGetValue(lkg.SnapshotId, out var validation)
            && validation.Passed)
        {
            var metricStr = validation.MetricValue % 1 == 0
                ? ((long)validation.MetricValue).ToString()
                : validation.MetricValue.ToString("G6");

            return $"LKG ({validation.TestTool} {metricStr}{validation.MetricUnit} PASS)";
        }

        return "LKG";
    }

    /// <summary>
    /// Builds a lookup from ActiveSnapshotId to the most recent ValidationResult
    /// for that snapshot. Snapshots with no associated validation are absent from
    /// the dictionary. Validations without an ActiveSnapshotId are ignored.
    /// </summary>
    public static Dictionary<string, ValidationResult> BuildLookup(
        IEnumerable<ValidationResult>? validations)
    {
        var lookup = new Dictionary<string, ValidationResult>(StringComparer.Ordinal);

        if (validations is null)
            return lookup;

        foreach (var v in validations)
        {
            if (string.IsNullOrEmpty(v.ActiveSnapshotId))
                continue;

            // Keep the most recent result per snapshot.
            if (!lookup.TryGetValue(v.ActiveSnapshotId, out var existing)
                || v.Timestamp > existing.Timestamp)
            {
                lookup[v.ActiveSnapshotId] = v;
            }
        }

        return lookup;
    }
}
