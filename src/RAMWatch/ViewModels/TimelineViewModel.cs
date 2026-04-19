using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RAMWatch.Core;
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
    Snapshot,
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
    /// Severity for ConfigChange entries — Major changes surface as their
    /// own row, Minor changes coalesce by boot. Ignored for non-ConfigChange
    /// types; they're effectively treated as Major (always visible under
    /// their own filter).
    /// </summary>
    public ChangeSeverity ChangeSeverity { get; init; } = ChangeSeverity.Major;

    /// <summary>
    /// Sensor key the row's ProvenanceGlyph looks up in the registry.
    /// Derived from <see cref="EntryType"/> — drift and config-change
    /// entries are Derived (diamond), validation entries are Measured
    /// from a user log (circle).
    /// </summary>
    public string ProvenanceKey => EntryType switch
    {
        TimelineEntryType.ConfigChange   => "TimelineConfigChange",
        TimelineEntryType.Drift          => "TimelineDrift",
        TimelineEntryType.ValidationPass => "TimelineValidation",
        TimelineEntryType.ValidationFail => "TimelineValidation",
        TimelineEntryType.Snapshot       => "TimelineSnapshot",
        _                                => "TimelineValidation",
    };

    /// <summary>
    /// Optional era name a timeline row was recorded under. Shown under
    /// the summary so a reader can trace "which config was I testing?"
    /// without opening another tab. Empty string when the entry predates
    /// eras or wasn't tagged.
    /// </summary>
    public string EraName { get; init; } = "";

    /// <summary>
    /// Compact timing summary for context, e.g. "DDR4-3600 CL16-20-20-42".
    /// Null when no snapshot data is available for this entry.
    /// </summary>
    public string? TimingSummary { get; init; }

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
    private readonly List<TimelineEntry> _allEntries = [];

    [ObservableProperty]
    private bool _hasEntries;

    // ── Active era banner ───────────────────────────────────
    // The tuning-journal anchor. A user boots into a new BIOS config,
    // hits "Start new config", types a name — that becomes the era.
    // Every snapshot, validation, and boot-fail after it is tagged with
    // the EraId until the user ends it. When no era is active and the
    // service reports a recent ConfigChange, HasUnnamedConfig fires a
    // nudge so deliberate changes don't vanish into routine-noise rows.

    [ObservableProperty]
    private bool _hasActiveEra;

    [ObservableProperty]
    private string _activeEraName = "";

    [ObservableProperty]
    private string _activeEraStartText = "";

    [ObservableProperty]
    private string _activeEraId = "";

    [ObservableProperty]
    private bool _hasUnnamedConfig;

    // Inline-naming banner state. StartNaming flips _isNamingEra to true;
    // the banner swaps its button row for a TextBox + Start/Cancel, and
    // focus jumps to the box. Confirm sends CreateEra; Cancel rewinds.
    [ObservableProperty]
    private bool _isNamingEra;

    [ObservableProperty]
    private string _newEraName = "";

    private Func<string, Task>? _createEraHandler;
    private Func<string, Task>? _closeEraHandler;

    public void SetCreateEraHandler(Func<string, Task> handler) => _createEraHandler = handler;
    public void SetCloseEraHandler(Func<string, Task> handler)  => _closeEraHandler  = handler;

    [RelayCommand]
    private void StartNaming()
    {
        IsNamingEra = true;
        // NewEraName is left untouched so the user can keep a half-typed
        // name if they cancel and reopen. Trimmed on submit.
    }

    [RelayCommand]
    private void CancelNaming()
    {
        IsNamingEra = false;
        NewEraName = "";
    }

    [RelayCommand]
    private async Task ConfirmNewEraAsync()
    {
        var name = (NewEraName ?? "").Trim();
        if (name.Length == 0) return;
        if (_createEraHandler is not null)
            await _createEraHandler(name);
        IsNamingEra = false;
        NewEraName = "";
    }

    [RelayCommand]
    private async Task EndActiveEraAsync()
    {
        if (string.IsNullOrEmpty(ActiveEraId)) return;
        if (_closeEraHandler is not null)
            await _closeEraHandler(ActiveEraId);
    }

    /// <summary>
    /// End the active era and immediately open the inline-naming banner so
    /// the user can type the next campaign's name without a second click.
    /// Matches the common "I'm switching to a new attempt" verb.
    /// </summary>
    [RelayCommand]
    private async Task StartNextEraAsync()
    {
        if (!string.IsNullOrEmpty(ActiveEraId) && _closeEraHandler is not null)
            await _closeEraHandler(ActiveEraId);
        IsNamingEra = true;
    }

    // ── Type filters ────────────────────────────────────────

    [ObservableProperty]
    private bool _showPass = true;

    [ObservableProperty]
    private bool _showFail = true;

    // Major ConfigChange rows — a primary timing, voltage, clock, or
    // controller boolean moved. This is the deliberate tuning signal a
    // user actually wants to see. Default on.
    [ObservableProperty]
    private bool _showMajorChange = true;

    // Minor ConfigChange rows ("Retrain") — secondaries, turn-around,
    // PHY, signal integrity. Auto memory retraining between boots
    // produces one per boot; over weeks it drowns deliberate events.
    // Coalesced by boot into one row each when visible. Default off.
    [ObservableProperty]
    private bool _showChange = false;

    [ObservableProperty]
    private bool _showDrift = true;

    // Snapshots are intentional user markers (Save Snapshot / Ctrl+S) —
    // the one entry type a tuner always wants in their logbook. Default on.
    [ObservableProperty]
    private bool _showSnapshot = true;

    partial void OnShowPassChanged(bool value) => ApplyFilters();
    partial void OnShowFailChanged(bool value) => ApplyFilters();
    partial void OnShowMajorChangeChanged(bool value) => ApplyFilters();
    partial void OnShowChangeChanged(bool value) => ApplyFilters();
    partial void OnShowDriftChanged(bool value) => ApplyFilters();
    partial void OnShowSnapshotChanged(bool value) => ApplyFilters();

    private void ApplyFilters()
    {
        Entries.Clear();
        foreach (var entry in _allEntries)
        {
            if (IsVisible(entry))
            {
                entry.AttachOwner(Entries);
                Entries.Add(entry);
            }
        }
        HasEntries = Entries.Count > 0;
    }

    private bool IsVisible(TimelineEntry entry) => entry.EntryType switch
    {
        TimelineEntryType.ValidationPass => ShowPass,
        TimelineEntryType.ValidationFail => ShowFail,
        TimelineEntryType.ConfigChange   => entry.ChangeSeverity == ChangeSeverity.Major
                                              ? ShowMajorChange
                                              : ShowChange,
        TimelineEntryType.Drift          => ShowDrift,
        TimelineEntryType.Snapshot       => ShowSnapshot,
        _ => true
    };

    // Injected by MainViewModel so each validation entry can send DeleteValidationMessage.
    private Func<string, Task>? _deleteValidationHandler;

    // Injected by MainViewModel so config change entries can send DeleteChangeMessage.
    private Func<string, Task>? _deleteChangeHandler;

    /// <summary>
    /// Injects the async callback used to send DeleteValidationMessage to the service.
    /// Must be set before the first LoadFromState call that includes validation results.
    /// </summary>
    public void SetDeleteValidationHandler(Func<string, Task> handler)
        => _deleteValidationHandler = handler;

    /// <summary>
    /// Injects the async callback used to send DeleteChangeMessage to the service.
    /// </summary>
    public void SetDeleteChangeHandler(Func<string, Task> handler)
        => _deleteChangeHandler = handler;

    /// <summary>
    /// Rebuilds the timeline from the latest service state push.
    /// Interleaves all three event types and sorts by timestamp descending.
    /// </summary>
    public void LoadFromState(ServiceState state)
    {
        // Era banner context — set before the list rebuild so bindings
        // fire once, not twice.
        if (state.ActiveEra is { } era)
        {
            HasActiveEra        = true;
            ActiveEraName       = era.Name;
            ActiveEraId         = era.EraId;
            ActiveEraStartText  = $"started {era.StartTimestamp.ToLocalTime():MMM d, HH:mm}";
            HasUnnamedConfig    = false;
        }
        else
        {
            HasActiveEra        = false;
            ActiveEraName       = "";
            ActiveEraId         = "";
            ActiveEraStartText  = "";

            // "Recent ConfigChange and no active era" → the user likely
            // just booted into a new BIOS config and should name it.
            bool recent =
                state.RecentChanges is { Count: > 0 } &&
                (DateTime.UtcNow - state.RecentChanges[0].Timestamp) < TimeSpan.FromHours(2);
            HasUnnamedConfig = recent;
        }

        var entries = new List<TimelineEntry>();

        // Build snapshot lookup for timing summaries on timeline entries.
        var snapLookup = new Dictionary<string, TimingSnapshot>(StringComparer.Ordinal);
        if (state.Snapshots is not null)
        {
            foreach (var snap in state.Snapshots)
            {
                if (!string.IsNullOrEmpty(snap.SnapshotId))
                    snapLookup[snap.SnapshotId] = snap;
            }
        }

        // Era lookup so each row can carry the name of the era it was
        // tagged under — "testing attempt-7 tight tRAS" under a FAIL row
        // is exactly the trace the user asked for.
        var eraLookup = new Dictionary<string, string>(StringComparer.Ordinal);
        if (state.Eras is not null)
        {
            foreach (var e in state.Eras)
                eraLookup[e.EraId] = e.Name;
        }

        string ResolveEraName(string? eraId)
            => eraId is not null && eraLookup.TryGetValue(eraId, out var n) ? n : "";

        // Snapshots — only user-labeled ones appear as timeline rows. The
        // service also auto-captures "before/after" snapshots around each
        // ConfigChange; those have empty labels and would otherwise pepper
        // the timeline with rows nobody saved by hand. The ConfigChange
        // entries below already cover that event on their own row.
        if (state.Snapshots is { Count: > 0 })
        {
            foreach (var snap in state.Snapshots)
            {
                if (string.IsNullOrWhiteSpace(snap.Label)) continue;

                entries.Add(new TimelineEntry
                {
                    EntryId          = Guid.NewGuid(),
                    Timestamp        = snap.Timestamp,
                    EntryType        = TimelineEntryType.Snapshot,
                    Summary          = string.IsNullOrWhiteSpace(snap.Notes)
                        ? $"Saved snapshot: {snap.Label}"
                        : $"Saved snapshot: {snap.Label} — {snap.Notes}",
                    TimestampDisplay = FormatTimestamp(snap.Timestamp),
                    TypeLabel        = "SNAPSHOT",
                    TimingSummary    = FormatTimingSummary(snap),
                    EraName          = ResolveEraName(snap.EraId),
                });
            }
        }

        // Config changes — classified by severity. Major changes surface
        // as individual rows; Minor changes coalesce by BootId into one
        // synthetic "RETRAIN" row per boot so per-boot auto-retraining
        // noise doesn't drown the deliberate tuning events.
        if (state.RecentChanges is { Count: > 0 })
        {
            var minorByBoot = new Dictionary<string, List<ConfigChange>>(StringComparer.Ordinal);

            foreach (var change in state.RecentChanges)
            {
                var severity = ChangeSeverityClassifier.Classify(change);
                if (severity == ChangeSeverity.Minor)
                {
                    if (!minorByBoot.TryGetValue(change.BootId, out var list))
                    {
                        list = [];
                        minorByBoot[change.BootId] = list;
                    }
                    list.Add(change);
                    continue;
                }

                var deltas = string.Join(", ", change.Changes.Select(
                    c => $"{c.Key}: {c.Value.Before} -> {c.Value.After}"));
                var summary = string.IsNullOrEmpty(change.UserNotes)
                    ? $"Config changed: {deltas}"
                    : $"{change.UserNotes} ({deltas})";

                // Use the "after" snapshot for the timing summary (what we changed TO).
                // Fall back to current timings if the snapshot was pruned or not yet persisted.
                TimingSnapshot? snap = null;
                if (change.SnapshotAfterId is not null)
                    snapLookup.TryGetValue(change.SnapshotAfterId, out snap);
                snap ??= state.Timings;

                var changeEntry = new TimelineEntry
                {
                    EntryId = Guid.NewGuid(),
                    Timestamp = change.Timestamp,
                    EntryType = TimelineEntryType.ConfigChange,
                    ChangeSeverity = ChangeSeverity.Major,
                    Summary = summary,
                    TimestampDisplay = FormatTimestamp(change.Timestamp),
                    TypeLabel = "CHANGE",
                    ServiceId = change.ChangeId,
                    TimingSummary = FormatTimingSummary(snap),
                    EraName = ResolveEraName(change.EraId),
                };
                changeEntry.OnConfirmedDeleteAsync = _deleteChangeHandler;
                entries.Add(changeEntry);
            }

            // One synthetic row per boot for minor retrains. Distinct field
            // names (deduplicated) are listed in the summary — same field
            // retraining three times in one boot is still one field, not three.
            foreach (var (bootId, changes) in minorByBoot)
            {
                var latest      = changes.OrderByDescending(c => c.Timestamp).First();
                var fieldSet    = new HashSet<string>(StringComparer.Ordinal);
                foreach (var c in changes)
                    foreach (var k in c.Changes.Keys)
                        fieldSet.Add(k);
                var fieldList   = string.Join(", ", fieldSet);
                var changeCount = changes.Count;
                var fieldCount  = fieldSet.Count;

                string summary = changeCount == 1
                    ? $"Minor retrain: {fieldList}"
                    : $"{changeCount} minor retrains across {fieldCount} field{(fieldCount == 1 ? "" : "s")}: {fieldList}";

                // No ServiceId — coalesced rows aren't individually
                // deletable. The TimelineEntry delete path no-ops when
                // ServiceId is null, and RequestDelete never surfaces a
                // confirm because the row has no ServiceId-gated button.
                entries.Add(new TimelineEntry
                {
                    EntryId          = Guid.NewGuid(),
                    Timestamp        = latest.Timestamp,
                    EntryType        = TimelineEntryType.ConfigChange,
                    ChangeSeverity   = ChangeSeverity.Minor,
                    Summary          = summary,
                    TimestampDisplay = FormatTimestamp(latest.Timestamp),
                    TypeLabel        = "RETRAIN",
                    TimingSummary    = FormatTimingSummary(state.Timings),
                    EraName          = ResolveEraName(latest.EraId),
                });
            }
        }

        // Drift events — use current timings as context (drift happened this boot).
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
                    TypeLabel = "DRIFT",
                    TimingSummary = FormatTimingSummary(state.Timings)
                });
            }
        }

        // Validation results
        if (state.RecentValidations is { Count: > 0 })
        {
            foreach (var result in state.RecentValidations)
            {
                var passText = result.Passed ? "PASSED" : "FAILED";
                var metricStr = Math.Abs(result.MetricValue % 1) < 0.001
                    ? ((long)result.MetricValue).ToString()
                    : result.MetricValue.ToString("F1");
                var summary = $"{result.TestTool} {passText}: " +
                              $"{result.MetricName} = {metricStr} {result.MetricUnit}";
                if (result.ErrorCount > 0)
                    summary += $" ({result.ErrorCount} errors)";
                if (result.DurationMinutes > 0)
                    summary += $" [{result.DurationMinutes}m]";

                // Look up the linked snapshot for timing context.
                // Fall back to current timings if the snapshot was pruned.
                TimingSnapshot? snap = null;
                if (result.ActiveSnapshotId is not null)
                    snapLookup.TryGetValue(result.ActiveSnapshotId, out snap);
                snap ??= state.Timings;

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
                    ServiceId        = result.Id,
                    TimingSummary    = FormatTimingSummary(snap),
                    EraName          = ResolveEraName(result.EraId),
                };
                // Wire the IPC delete callback so confirmed deletes reach the service.
                entry.OnConfirmedDeleteAsync = _deleteValidationHandler;
                entries.Add(entry);
            }
        }

        // Sort newest first and store the full unfiltered list.
        entries.Sort((a, b) => b.Timestamp.CompareTo(a.Timestamp));
        _allEntries.Clear();
        _allEntries.AddRange(entries);

        ApplyFilters();
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

    private static string? FormatTimingSummary(TimingSnapshot? snap)
    {
        if (snap is null) return null;
        string primaries = $"CL{snap.CL}-{snap.RCDRD}-{snap.RP}-{snap.RAS}";
        if (snap.MemClockMhz > 0)
            return $"{SnapshotDisplayName.DdrLabel(snap.MemClockMhz)} {primaries}";
        return primaries;
    }
}
