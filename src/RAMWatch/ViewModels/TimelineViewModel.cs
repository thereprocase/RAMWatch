using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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
/// Delete uses a two-click confirmation with a 3-second timeout.
/// </summary>
public partial class TimelineEntry : ObservableObject
{
    // Stable identifier so the service-side delete IPC can reference this entry.
    // Set to the event's source timestamp at construction time.
    public required Guid EntryId { get; init; }
    public required DateTime Timestamp { get; init; }
    public required TimelineEntryType EntryType { get; init; }
    public required string Summary { get; init; }
    public required string TimestampDisplay { get; init; }
    public required string TypeLabel { get; init; }

    /// <summary>
    /// The service-side identifier for this entry (e.g. ValidationResult.Id).
    /// Null for entry types that have no service-side delete operation (config changes, drift).
    /// When non-null, the confirmed delete calls <see cref="OnConfirmedDeleteAsync"/>.
    /// </summary>
    public string? ServiceId { get; init; }

    /// <summary>
    /// Async delete callback injected by the view model. Receives <see cref="ServiceId"/>.
    /// Called on the second (confirmed) click for entries that have a ServiceId.
    /// The entry removes itself from the collection regardless of callback result.
    /// </summary>
    internal Func<string, Task>? OnConfirmedDeleteAsync { get; set; }

    // ── Delete confirmation state ────────────────────────────

    [ObservableProperty]
    private bool _deleteConfirmPending;

    // The owning collection — injected so the entry can remove itself.
    private ObservableCollection<TimelineEntry>? _owner;

    private System.Threading.Timer? _confirmTimer;

    internal void AttachOwner(ObservableCollection<TimelineEntry> owner)
        => _owner = owner;

    [RelayCommand]
    private void RequestDelete()
    {
        if (!DeleteConfirmPending)
        {
            // First click — enter confirmation state and start the 3-second window.
            DeleteConfirmPending = true;
            _confirmTimer?.Dispose();
            _confirmTimer = new System.Threading.Timer(
                _ => ResetConfirm(),
                null,
                dueTime: TimeSpan.FromSeconds(3),
                period: System.Threading.Timeout.InfiniteTimeSpan);
        }
        else
        {
            // Second click within window — execute the delete.
            _confirmTimer?.Dispose();
            _confirmTimer = null;

            // Remove from the UI collection immediately so the entry disappears.
            _owner?.Remove(this);

            // If this entry maps to a service-side object, fire the IPC delete.
            // Fire-and-forget: if the service is unavailable the UI is still cleaned up.
            if (ServiceId is not null && OnConfirmedDeleteAsync is not null)
                _ = OnConfirmedDeleteAsync(ServiceId);
        }
    }

    private void ResetConfirm()
    {
        _confirmTimer?.Dispose();
        _confirmTimer = null;
        // Property change must happen on the UI thread.
        System.Windows.Application.Current?.Dispatcher.Invoke(
            () => DeleteConfirmPending = false);
    }
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

    // Injected by MainViewModel so each validation entry can send DeleteValidationMessage.
    private Func<string, Task>? _deleteValidationHandler;

    /// <summary>
    /// Injects the async callback used to send DeleteValidationMessage to the service.
    /// Must be set before the first LoadFromState call that includes validation results.
    /// </summary>
    public void SetDeleteValidationHandler(Func<string, Task> handler)
        => _deleteValidationHandler = handler;

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
                    EntryId = Guid.NewGuid(),
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
                    EntryId = Guid.NewGuid(),
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

                var entry = new TimelineEntry
                {
                    EntryId          = Guid.NewGuid(),
                    Timestamp        = result.Timestamp,
                    EntryType        = result.Passed
                        ? TimelineEntryType.ValidationPass
                        : TimelineEntryType.ValidationFail,
                    Summary          = summary,
                    TimestampDisplay = FormatTimestamp(result.Timestamp),
                    TypeLabel        = result.Passed ? "PASS" : "FAIL",
                    // Id on the ValidationResult is the service-side stable identifier.
                    ServiceId        = result.Id
                };
                // Wire the IPC delete callback so confirmed deletes reach the service.
                entry.OnConfirmedDeleteAsync = _deleteValidationHandler;
                entries.Add(entry);
            }
        }

        // Sort newest first
        entries.Sort((a, b) => b.Timestamp.CompareTo(a.Timestamp));

        Entries.Clear();
        foreach (var entry in entries)
        {
            entry.AttachOwner(Entries);
            Entries.Add(entry);
        }

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
