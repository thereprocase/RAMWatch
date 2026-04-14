using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using RAMWatch.Core.Models;

namespace RAMWatch.ViewModels;

/// <summary>
/// Entry type for timeline interleaving. Each maps to a distinct
/// border color and label in the TimelineTab.
/// </summary>
public enum TimelineEntryType
{
    ConfigChange,
    Drift,
    ValidationPass,
    ValidationFail,
    Info
}

/// <summary>
/// A single row in the timeline logbook. Wraps config changes, drift events,
/// and validation results into a unified chronological list.
/// </summary>
public sealed class TimelineEntry
{
    public required DateTime Timestamp { get; init; }
    public required TimelineEntryType EntryType { get; init; }
    public required string Summary { get; init; }
    public required string TimestampDisplay { get; init; }
    public required string TypeLabel { get; init; }
}

/// <summary>
/// Backing view model for TimelineTab. Merges config changes, drift events,
/// and validation results into a single chronological list sorted newest-first.
/// </summary>
public partial class TimelineViewModel : ObservableObject
{
    public ObservableCollection<TimelineEntry> Entries { get; } = [];

    [ObservableProperty]
    private bool _hasEntries;

    /// <summary>
    /// Rebuilds the timeline from the latest service state push.
    /// Interleaves all three event types and sorts by timestamp descending.
    /// </summary>
    public void LoadFromState(ServiceState state)
    {
        var entries = new List<TimelineEntry>();

        // Config changes
        if (state.RecentChanges is { Count: > 0 })
        {
            foreach (var change in state.RecentChanges)
            {
                var deltas = string.Join(", ", change.Changes.Select(
                    c => $"{c.Key}: {c.Value.Before} -> {c.Value.After}"));
                var summary = string.IsNullOrEmpty(change.UserNotes)
                    ? $"Config changed: {deltas}"
                    : $"{change.UserNotes} ({deltas})";

                entries.Add(new TimelineEntry
                {
                    Timestamp = change.Timestamp,
                    EntryType = TimelineEntryType.ConfigChange,
                    Summary = summary,
                    TimestampDisplay = FormatTimestamp(change.Timestamp),
                    TypeLabel = "CHANGE"
                });
            }
        }

        // Drift events
        if (state.DriftEvents is { Count: > 0 })
        {
            foreach (var drift in state.DriftEvents)
            {
                var summary = $"{drift.TimingName} trained to {drift.ActualValue} " +
                              $"(expected {drift.ExpectedValue}, " +
                              $"stable {drift.BootsAtExpected}/{drift.WindowBootCount} boots)";

                entries.Add(new TimelineEntry
                {
                    Timestamp = drift.Timestamp,
                    EntryType = TimelineEntryType.Drift,
                    Summary = summary,
                    TimestampDisplay = FormatTimestamp(drift.Timestamp),
                    TypeLabel = "DRIFT"
                });
            }
        }

        // Validation results
        if (state.RecentValidations is { Count: > 0 })
        {
            foreach (var result in state.RecentValidations)
            {
                var passText = result.Passed ? "PASSED" : "FAILED";
                var summary = $"{result.TestTool} {passText}: " +
                              $"{result.MetricName} = {result.MetricValue:G6} {result.MetricUnit}";
                if (result.ErrorCount > 0)
                    summary += $" ({result.ErrorCount} errors)";
                if (result.DurationMinutes > 0)
                    summary += $" [{result.DurationMinutes}m]";

                entries.Add(new TimelineEntry
                {
                    Timestamp = result.Timestamp,
                    EntryType = result.Passed
                        ? TimelineEntryType.ValidationPass
                        : TimelineEntryType.ValidationFail,
                    Summary = summary,
                    TimestampDisplay = FormatTimestamp(result.Timestamp),
                    TypeLabel = result.Passed ? "PASS" : "FAIL"
                });
            }
        }

        // Sort newest first
        entries.Sort((a, b) => b.Timestamp.CompareTo(a.Timestamp));

        Entries.Clear();
        foreach (var entry in entries)
            Entries.Add(entry);

        HasEntries = Entries.Count > 0;
    }

    private static string FormatTimestamp(DateTime dt)
    {
        var local = dt.Kind == DateTimeKind.Utc ? dt.ToLocalTime() : dt;
        var today = DateTime.Today;

        if (local.Date == today)
            return local.ToString("HH:mm:ss");
        if (local.Date == today.AddDays(-1))
            return "Yesterday " + local.ToString("HH:mm");

        return local.ToString("MM/dd HH:mm");
    }
}
