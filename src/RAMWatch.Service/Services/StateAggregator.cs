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

    // Phase 2 — current timing snapshot, thermal telemetry, and driver status,
    // set by RamWatchService after each hardware read cycle.
    private TimingSnapshot? _currentTimings;
    private ThermalPowerSnapshot? _currentThermalPower;
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

    // LiveKernelReports — scanned once at startup, cached.
    private LiveKernelReportSummary? _liveKernelReports;

    // DIMM info — read once at startup, cached.
    private List<DimmInfo>? _dimms;

    // UMC address map — read once at startup, cached.
    private List<AddressMapConfig>? _addressMap;

    // Cold-boot stamps — first-success UTC per cold-tier reader.
    // Stays null until that reader produces a non-null result, after which
    // it never moves. Drift detection and peer clients gate on IsComplete so
    // the startup window isn't misread as drift.
    private DateTime? _timingsStampedUtc;
    private DateTime? _dimmsStampedUtc;
    private DateTime? _addressMapStampedUtc;

    // Phase 3 — current-boot drift events, accumulated here so they survive
    // until the next periodic state push.
    private readonly List<DriftEvent> _currentBootDrift = new();

    // Boot time computed once at construction — TickCount64 is monotonic but
    // DateTime.UtcNow can shift from NTP syncs, so recomputing on every state
    // push would cause the boot timestamp to wobble.
    private readonly DateTime _bootTime;

    public StateAggregator(
        EventLogMonitor eventLog,
        SettingsManager settings,
        PipeServer pipeServer)
    {
        _eventLog = eventLog;
        _settings = settings;
        _pipeServer = pipeServer;
        _serviceStartTime = DateTime.UtcNow;
        _bootTime = GetLastBootTime();
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
            // First non-null timing snapshot marks UMC/SMU/BIOS-WMI cold-tier
            // done. Subsequent calls don't advance the stamp — the data is
            // boot-time; freshness is tracked separately on the GUI side.
            if (timings is not null && _timingsStampedUtc is null)
                _timingsStampedUtc = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Update the current thermal/power telemetry snapshot.
    /// Called by RamWatchService on each refresh cycle after hardware reads.
    /// </summary>
    public void SetThermalPower(ThermalPowerSnapshot? thermalPower)
    {
        lock (_lock)
        {
            _currentThermalPower = thermalPower;
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
            // Cap to prevent unbounded growth during pathological drift storms.
            if (_currentBootDrift.Count > 200)
                _currentBootDrift.RemoveRange(0, _currentBootDrift.Count - 200);
        }
    }

    /// <summary>
    /// Scan LiveKernelReports directory and cache the result.
    /// Called once at service startup (service runs as SYSTEM, has access).
    /// </summary>
    public void ScanLiveKernelReports()
    {
        var summary = LiveKernelReportScanner.Scan();
        lock (_lock) { _liveKernelReports = summary; }
    }

    /// <summary>
    /// Read installed DIMM information via WMI. Called once at service startup.
    /// </summary>
    public void ReadDimmInfo()
    {
        var dimms = Hardware.DimmReader.ReadDimms();
        lock (_lock)
        {
            _dimms = dimms;
            if (dimms is not null && _dimmsStampedUtc is null)
                _dimmsStampedUtc = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Store the UMC address map configuration. Called once at service startup.
    /// </summary>
    public void SetAddressMap(List<AddressMapConfig>? addressMap)
    {
        lock (_lock)
        {
            _addressMap = addressMap;
            if (addressMap is not null && _addressMapStampedUtc is null)
                _addressMapStampedUtc = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Snapshot of which cold-tier readers have produced their first result.
    /// Safe to call from any thread; never blocks on downstream services.
    /// </summary>
    public ColdBootStatus GetColdBootStatus()
    {
        lock (_lock)
        {
            return new ColdBootStatus
            {
                TimingsStampedUtc    = _timingsStampedUtc,
                DimmsStampedUtc      = _dimmsStampedUtc,
                AddressMapStampedUtc = _addressMapStampedUtc,
            };
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
        ThermalPowerSnapshot? thermalPower;
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
        LiveKernelReportSummary? liveKernelReports;
        List<DimmInfo>? dimms;
        List<AddressMapConfig>? addressMap;
        ColdBootStatus coldBootStatus;

        lock (_lock)
        {
            ready                = _ready;
            timings              = _currentTimings;
            thermalPower         = _currentThermalPower;
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
            liveKernelReports    = _liveKernelReports;
            dimms                = _dimms;
            addressMap           = _addressMap;
            coldBootStatus       = new ColdBootStatus
            {
                TimingsStampedUtc    = _timingsStampedUtc,
                DimmsStampedUtc      = _dimmsStampedUtc,
                AddressMapStampedUtc = _addressMapStampedUtc,
            };
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

        return new ServiceState
        {
            Timestamp = DateTime.UtcNow,
            BootTime = _bootTime,
            Ready = ready,
            DriverStatus = driverStatus,
            // ServiceUptime holds system uptime (time since last boot), not service process uptime.
            // System uptime is what users care about for tuning context.
            ServiceUptime = DateTime.UtcNow - _bootTime,
            Errors = _eventLog.GetErrorSources(),
            Integrity = new IntegrityState(0, IntegrityCheckStatus.NotRun, IntegrityCheckStatus.NotRun),
            Timings = timings,
            ThermalPower = thermalPower,
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
            Minimums = ComputeMinimums(snapshots, recentValidations),
            LiveKernelReports = liveKernelReports,
            Dimms = dimms,
            AddressMap = addressMap,
            // Seed the GUI's per-source event buffer on connect. EventLogMonitor
            // owns its own lock; called outside _lock to avoid nesting.
            RecentEvents = GetRecentEventsForState(),
            ColdBootComplete = coldBootStatus.IsComplete
        };
    }

    private List<MonitoredEvent>? GetRecentEventsForState()
    {
        var recent = _eventLog.GetRecentEvents(50);
        return recent.Count > 0 ? recent : null;
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

    /// <summary>
    /// Push a lightweight thermal/power update to all connected clients.
    /// Called by the hot tier (every 2-5s) — much faster than a full state push.
    /// </summary>
    public async Task BroadcastThermalAsync(ThermalPowerSnapshot tp, double vcore, double vsoc)
    {
        var message = new ThermalUpdateMessage
        {
            Type = "thermalUpdate",
            ThermalPower = tp,
            VCore = vcore,
            VSoc = vsoc
        };
        string json = MessageSerializer.Serialize(message);
        await _pipeServer.BroadcastAsync(json);
    }

    public async Task BroadcastEventAsync(MonitoredEvent evt)
    {
        var message = new EventMessage
        {
            Type = "event",
            Event = evt,
            IsCritical = evt.Severity == EventSeverity.Critical
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
