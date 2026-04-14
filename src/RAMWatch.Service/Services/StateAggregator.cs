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
        SnapshotJournal snapshotJournal)
    {
        lock (_lock)
        {
            _configChangeDetector = configChangeDetector;
            _driftDetector = driftDetector;
            _validationLogger = validationLogger;
            _lkgTracker = lkgTracker;
            _snapshotJournal = snapshotJournal;
        }
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
        bool ready;
        TimingSnapshot? timings = null;
        string driverStatus;
        List<ConfigChange>? recentChanges = null;
        List<DriftEvent>? driftEvents = null;
        List<ValidationResult>? recentValidations = null;
        TimingSnapshot? lkg = null;
        List<TimingSnapshot>? snapshots = null;

        lock (_lock)
        {
            ready = _ready;
            timings = _currentTimings;
            driverStatus = _driverStatus;

            if (_configChangeDetector is not null)
            {
                // ConfigChangeDetector does not hold a list in memory — changes.json
                // is not loaded on startup (it is append-only). The recent-changes
                // field is populated in Phase 3 when a full changes journal is added.
                // For now, leave null so the field is omitted from the push.
            }

            if (_driftDetector is not null)
                driftEvents = new List<DriftEvent>(_currentBootDrift);

            if (_validationLogger is not null)
                recentValidations = _validationLogger.GetRecentResults(5);

            if (_lkgTracker is not null)
                lkg = _lkgTracker.CurrentLkg;

            if (_snapshotJournal is not null)
            {
                var all = _snapshotJournal.GetAll();
                // Only include the list when at least one snapshot exists.
                // null is omitted from JSON by WhenWritingNull, keeping wire format lean.
                if (all.Count > 0)
                    snapshots = all;
            }
        }

        var bootTime = GetLastBootTime();

        return new ServiceState
        {
            Timestamp = DateTime.UtcNow,
            BootTime = bootTime,
            Ready = ready,
            DriverStatus = driverStatus,
            ServiceUptime = DateTime.UtcNow - _serviceStartTime,
            Errors = _eventLog.GetErrorSources(),
            Integrity = new IntegrityState(0, IntegrityCheckStatus.NotRun, IntegrityCheckStatus.NotRun),
            Timings = timings,
            // Phase 3 — null when no data yet (omitted from JSON by WhenWritingNull)
            RecentChanges = recentChanges,
            DriftEvents = driftEvents,
            RecentValidations = recentValidations,
            Lkg = lkg,
            Snapshots = snapshots
        };
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
