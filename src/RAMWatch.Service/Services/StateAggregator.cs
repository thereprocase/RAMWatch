using RAMWatch.Core.Ipc;
using RAMWatch.Core.Models;

namespace RAMWatch.Service.Services;

/// <summary>
/// Combines all monitoring sources into a single ServiceState object.
/// Pushes state over the pipe on connect, periodically, and on events.
/// Manages the ready flag (false during startup scan, true after).
/// </summary>
public sealed class StateAggregator
{
    private readonly EventLogMonitor _eventLog;
    private readonly SettingsManager _settings;
    private readonly PipeServer _pipeServer;
    private readonly Lock _lock = new();
    private bool _ready;
    private DateTime _serviceStartTime;

    // Phase 3 — optional; null until wired in by RamWatchService.
    private ConfigChangeDetector? _configChangeDetector;
    private DriftDetector? _driftDetector;
    private ValidationTestLogger? _validationLogger;
    private LkgTracker? _lkgTracker;
    private SnapshotJournal? _snapshotJournal;

    // Phase 2 — current timing snapshot and driver status, set by RamWatchService
    // after each hardware read cycle.
    private TimingSnapshot? _currentTimings;
    private string _driverStatus = "not_found";

    // Resolved board vendor — set once at service startup, never changes.
    // Stored as string so the Core model doesn't need a reference to the
    // BoardVendor enum in the serialised ServiceState.
    private string? _biosLayoutVendor;

    // Boot baseline — per-source mean counts from past boots
    private BootBaselineJournal? _baselineJournal;

    // Eras and boot fails
    private EraJournal? _eraJournal;
    private BootFailJournal? _bootFailJournal;

    // Phase 3 — current-boot drift events, accumulated here so they survive
    // until the next periodic state push.
    private readonly List<DriftEvent> _currentBootDrift = new();

    public StateAggregator(
        EventLogMonitor eventLog,
        SettingsManager settings,
        PipeServer pipeServer)
    {
        _eventLog = eventLog;
        _settings = settings;
        _pipeServer = pipeServer;
        _serviceStartTime = DateTime.UtcNow;
    }

    /// <summary>
    /// Record the resolved board vendor so it is included in every state push.
    /// Called once at service startup after vendor detection completes.
    /// </summary>
    public void SetBiosVendor(string? vendorName)
    {
        lock (_lock) { _biosLayoutVendor = vendorName; }
    }

    /// <summary>
    /// Update the current timing snapshot and driver status after a hardware read.
    /// Called by RamWatchService on each refresh cycle.
    /// </summary>
    public void SetTimings(TimingSnapshot? timings, string driverStatus)
    {
        lock (_lock)
        {
            _currentTimings = timings;
            _driverStatus = driverStatus;
        }
    }

    /// <summary>
    /// Wire Phase 3 services into the aggregator after they are initialised.
    /// Called once by RamWatchService after startup loading is complete.
    /// </summary>
    public void SetPhase3Services(
        ConfigChangeDetector configChangeDetector,
        DriftDetector driftDetector,
        ValidationTestLogger validationLogger,
        LkgTracker lkgTracker,
        SnapshotJournal snapshotJournal,
        EraJournal? eraJournal = null,
        BootFailJournal? bootFailJournal = null)
    {
        lock (_lock)
        {
            _configChangeDetector = configChangeDetector;
            _driftDetector = driftDetector;
            _validationLogger = validationLogger;
            _lkgTracker = lkgTracker;
            _snapshotJournal = snapshotJournal;
            _eraJournal = eraJournal;
            _bootFailJournal = bootFailJournal;
        }
    }

    /// <summary>
    /// Wire the boot baseline journal so baselines are included in state pushes.
    /// </summary>
    public void SetBaselineJournal(BootBaselineJournal journal)
    {
        lock (_lock) { _baselineJournal = journal; }
    }

    /// <summary>
    /// Record drift events detected during the current boot so they are
    /// included in the next state push.
    /// </summary>
    public void AddDriftEvents(IEnumerable<DriftEvent> events)
    {
        lock (_lock)
        {
            _currentBootDrift.AddRange(events);
        }
    }

    public void MarkReady()
    {
        lock (_lock) { _ready = true; }
    }

    public ServiceState BuildState()
    {
        // Step 1: capture shared state and service references inside the lock.
        // Do NOT call methods on the service objects here — they acquire their
        // own locks and calling them under _lock creates a nested-lock hazard.
        bool ready;
        TimingSnapshot? timings;
        string driverStatus;
        string? biosLayoutVendor;
        List<DriftEvent> driftSnapshot;
        ConfigChangeDetector? changeDetector;
        bool driftDetectorPresent;
        ValidationTestLogger? validationLogger;
        LkgTracker? lkgTracker;
        SnapshotJournal? snapshotJournal;
        BootBaselineJournal? baselineJournal;
        EraJournal? eraJournal;
        BootFailJournal? bootFailJournal;

        lock (_lock)
        {
            ready                = _ready;
            timings              = _currentTimings;
            driverStatus         = _driverStatus;
            biosLayoutVendor     = _biosLayoutVendor;
            driftSnapshot        = new List<DriftEvent>(_currentBootDrift);
            changeDetector       = _configChangeDetector;
            driftDetectorPresent = _driftDetector is not null;
            validationLogger     = _validationLogger;
            lkgTracker           = _lkgTracker;
            snapshotJournal      = _snapshotJournal;
            baselineJournal      = _baselineJournal;
            eraJournal           = _eraJournal;
            bootFailJournal      = _bootFailJournal;
        }

        // Step 2: call methods that acquire their own locks OUTSIDE _lock.
        List<ConfigChange>? recentChanges = null;
        if (changeDetector is not null)
        {
            var recent = changeDetector.GetRecentChanges(5);
            if (recent.Count > 0)
                recentChanges = recent;
        }

        List<DriftEvent>? driftEvents = driftDetectorPresent
            ? driftSnapshot
            : null;

        List<ValidationResult>? recentValidations = validationLogger?.GetResults();

        TimingSnapshot? lkg = lkgTracker?.CurrentLkg;

        List<TimingSnapshot>? snapshots = null;
        if (snapshotJournal is not null)
        {
            var all = snapshotJournal.GetAll();
            // Only include the list when at least one snapshot exists.
            // null is omitted from JSON by WhenWritingNull, keeping wire format lean.
            if (all.Count > 0)
                snapshots = all;
        }

        // Boot baselines — computed once per state push (cheap: just averaging in-memory lists).
        Dictionary<string, BaselineStat>? baselines = null;
        if (baselineJournal is not null)
        {
            var computed = baselineJournal.ComputeBaselines();
            if (computed.Count > 0)
                baselines = computed;
        }

        var bootTime = GetLastBootTime();

        return new ServiceState
        {
            Timestamp = DateTime.UtcNow,
            BootTime = bootTime,
            Ready = ready,
            DriverStatus = driverStatus,
            // ServiceUptime holds system uptime (time since last boot), not service process uptime.
            // System uptime is what users care about for tuning context.
            ServiceUptime = DateTime.UtcNow - bootTime,
            Errors = _eventLog.GetErrorSources(),
            Integrity = new IntegrityState(0, IntegrityCheckStatus.NotRun, IntegrityCheckStatus.NotRun),
            Timings = timings,
            BiosLayoutVendor = biosLayoutVendor,
            // Phase 3 — null when no data yet (omitted from JSON by WhenWritingNull)
            RecentChanges = recentChanges,
            DriftEvents = driftEvents,
            RecentValidations = recentValidations,
            Lkg = lkg,
            Snapshots = snapshots,
            SourceBaselines = baselines,
            CurrentSettings = _settings.Current,
            // Eras and boot fails
            Eras = eraJournal?.GetAll(),
            ActiveEra = eraJournal?.GetActive(),
            BootFails = bootFailJournal?.GetRecent(20),
            // Minimums — computed across all snapshots (era filtering done GUI-side)
            Minimums = ComputeMinimums(snapshots, recentValidations)
        };
    }

    private static List<FrequencyMinimums>? ComputeMinimums(
        List<TimingSnapshot>? snapshots,
        List<ValidationResult>? validations)
    {
        if (snapshots is null or { Count: 0 })
            return null;

        var result = MinimumComputer.Compute(
            snapshots,
            validations ?? [],
            eraId: null); // All eras — GUI can filter client-side

        return result.Count > 0 ? result : null;
    }

    public async Task BroadcastStateAsync()
    {
        var state = BuildState();
        var message = new StateMessage
        {
            Type = "state",
            State = state
        };
        string json = MessageSerializer.Serialize(message);
        await _pipeServer.BroadcastAsync(json);
    }

    public async Task BroadcastEventAsync(MonitoredEvent evt)
    {
        var message = new EventMessage
        {
            Type = "event",
            Event = evt
        };
        string json = MessageSerializer.Serialize(message);
        await _pipeServer.BroadcastAsync(json);
    }

    private static DateTime GetLastBootTime()
    {
        var uptime = TimeSpan.FromMilliseconds(Environment.TickCount64);
        return DateTime.UtcNow - uptime;
    }
}
