using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RAMWatch.Core;
using RAMWatch.Core.Ipc;
using RAMWatch.Core.Models;
using RAMWatch.Service.Hardware;
using RAMWatch.Service.Services;

namespace RAMWatch.Service;

/// <summary>
/// Main service entry point. Manages the pipe server and all monitoring components.
/// Lifecycle: load settings → start pipe → historical scan → mark ready → periodic refresh.
/// </summary>
public sealed class RamWatchService : BackgroundService
{
    private readonly SettingsManager _settings;
    private readonly ILogger<RamWatchService> _logger;
    private PipeServer? _pipeServer;
    private EventLogMonitor? _eventLog;
    private StateAggregator? _aggregator;
    private CsvLogger? _csvLogger;
    private MirrorLogger? _mirrorLogger;
    private IntegrityChecker? _integrity;
    private string _bootId = "";

    // Phase 2 — hardware reads
    private HardwareReader? _hardwareReader;
    private TimingCsvLogger? _timingCsvLogger;
    private TimingSnapshot? _currentTimings;
    // Static system info — read once at startup, stamped onto every snapshot.
    private SystemInfoReader.SystemInfo? _systemInfo;
    private DesignationMap _designations = new();

    // Phase 3 — tuning journal services
    private ConfigChangeDetector? _configChangeDetector;
    private DriftDetector? _driftDetector;
    private ValidationTestLogger? _validationLogger;
    private LkgTracker? _lkgTracker;
    private SnapshotJournal? _snapshotJournal;

    // Tracks whether the first-boot auto-save has fired for this service session.
    // Resets to false on each service start; set to true after the first save.
    private bool _autoSavedThisBoot;

    // Timestamp of service readiness — used to compute uptime in the stop event.
    private DateTime _serviceStartedAt;

    // Shutdown barrier. Set to true at the top of StopAsync before any
    // component is disposed. Callback paths that run on arbitrary threads
    // (EventLogWatcher, PeriodicTimer) check this and bail out rather than
    // racing dispose of the pipe server, CSV loggers, or hardware reader.
    private volatile bool _shuttingDown;

    // Rate limit for RequestTimingRefresh. A misbehaving client looping the
    // message at wire speed would drive per-request hardware reads + state
    // broadcasts, saturating a core and starving the warm/hot loops.
    // Holds DateTime.UtcNow.Ticks of the last accepted refresh.
    private long _lastTimingRefreshTicks;
    private static readonly long MinTimingRefreshIntervalTicks =
        TimeSpan.FromSeconds(1).Ticks;

    // Boot baseline — rolling 50-boot event count history for normal/elevated coloring
    private BootBaselineJournal? _baselineJournal;

    // Eras and boot fails
    private EraJournal? _eraJournal;
    private BootFailJournal? _bootFailJournal;

    // Phase 4 — git-backed history
    private GitCommitter? _gitCommitter;

    public RamWatchService(SettingsManager settings, ILogger<RamWatchService> logger)
    {
        _settings = settings;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var config = _settings.Load();
        _logger.LogInformation("RAMWatch service starting. Data directory: {Path}", DataDirectory.BasePath);

        // Validate paths loaded from settings.json — same check applied to pipe updates.
        // A pre-staged settings.json with a malicious path could direct LocalSystem writes
        // to arbitrary directories. Reset to defaults if invalid.
        if (!AppSettings.IsValidDataPath(config.LogDirectory))
        {
            _logger.LogWarning("LogDirectory from settings.json is invalid, using default");
            config.LogDirectory = "";
        }
        if (!AppSettings.IsValidDataPath(config.MirrorDirectory))
        {
            _logger.LogWarning("MirrorDirectory from settings.json is invalid, using default");
            config.MirrorDirectory = "";
        }

        // Boot ID — sequential counter persisted to disk; never collides.
        _bootId = CsvLogger.GenerateBootId(DataDirectory.BasePath);

        // CSV logger with retention
        _csvLogger = new CsvLogger(
            string.IsNullOrEmpty(config.LogDirectory) ? DataDirectory.LogsPath : config.LogDirectory,
            config.LogRetentionDays,
            config.MaxLogSizeMb);
        _csvLogger.RunRetention();

        // Mirror logger (fire-and-forget copy to Dropbox/OneDrive/etc.)
        _mirrorLogger = new MirrorLogger(config.MirrorDirectory);

        // Pipe server — push full state immediately when a client connects
        _pipeServer = new PipeServer(OnClientMessage, OnClientConnected);
        _pipeServer.Start();
        _logger.LogInformation("Pipe server started on \\\\.\\pipe\\{PipeName}", PipeConstants.PipeName);

        // Integrity checker
        _integrity = new IntegrityChecker();

        // Event log monitor
        _eventLog = new EventLogMonitor();
        _eventLog.EventDetected += OnEventDetected;

        // Phase 3 — tuning journal services.
        // Load persisted state before the first timing snapshot arrives.
        // All four services are safe to construct here even if no hardware data
        // exists yet; they activate when TimingSnapshot data flows (Phase 2).
        _configChangeDetector = new ConfigChangeDetector(DataDirectory.BasePath);
        _configChangeDetector.LoadPrevious();
        _configChangeDetector.LoadChanges();

        _driftDetector = new DriftDetector(DataDirectory.BasePath);
        _driftDetector.LoadWindow();

        _validationLogger = new ValidationTestLogger(DataDirectory.BasePath);
        _validationLogger.Load();

        _lkgTracker = new LkgTracker(DataDirectory.BasePath);
        _lkgTracker.Load();

        _snapshotJournal = new SnapshotJournal(DataDirectory.BasePath);
        _snapshotJournal.Load();

        _baselineJournal = new BootBaselineJournal(DataDirectory.BasePath);
        _baselineJournal.Load();

        _eraJournal = new EraJournal(DataDirectory.BasePath);
        _eraJournal.Load();

        _bootFailJournal = new BootFailJournal(DataDirectory.BasePath);
        _bootFailJournal.Load();

        // Boot-fail auto-detect — scan the event log for crash signals
        // (Kernel-Power 41 / WER 1001 / System 6008) that the next-boot
        // recovery writes after a bugcheck, unexpected shutdown, or boot-
        // time faceplant. Runs once at startup; dedups against existing
        // journal entries so a service restart in the same boot is a no-op.
        // The sinceUtc floor is process-start time: these signals are always
        // written very early in the current boot, so the current-process
        // window is sufficient and excludes older unrelated events.
        try
        {
            // Windows boot time anchor: TickCount64 is milliseconds since
            // system start, so NowUtc - TickCount64 ms = actual boot instant.
            // All signals of interest are written after this; earlier events
            // belong to prior boots we don't care about.
            var windowsBootUtc = DateTime.UtcNow -
                TimeSpan.FromMilliseconds(Environment.TickCount64);

            var detector = BootFailDetector.CreateDefault();
            var entry = detector.DetectPriorCrash(
                sinceUtc:        windowsBootUtc,
                baseSnapshotId:  null,
                activeEraId:     _eraJournal.GetActive()?.EraId,
                existingEntries: _bootFailJournal.GetAll());

            if (entry is not null)
            {
                _bootFailJournal.Save(entry);
                _logger.LogInformation(
                    "Auto-logged prior-boot failure: {Kind} at {Time}. {Notes}",
                    entry.Kind, entry.Timestamp, entry.Notes);
            }
        }
        catch (Exception ex)
        {
            // Auto-detect is best-effort. Never block service start on
            // event log unavailability or a single failed query.
            _logger.LogWarning(ex, "Boot-fail auto-detect failed");
        }

        // Phase 4 — git committer (initialise after LKG so commit context is available)
        _gitCommitter = new GitCommitter(_settings, _logger);
        await _gitCommitter.InitializeAsync(stoppingToken);

        // Hardware reader — detects PawnIO, opens driver, detects CPU
        _hardwareReader = new HardwareReader();
        _logger.LogInformation("Hardware driver: {Name} — {Status}",
            _hardwareReader.DriverName, _hardwareReader.DriverDescription);

        // System info — BIOS version, AGESA, board model (registry, read once)
        _systemInfo = SystemInfoReader.Read();
        _logger.LogInformation("Board: {Vendor} {Model}, BIOS: {Bios}, AGESA: {Agesa}",
            _systemInfo.BoardVendor, _systemInfo.BoardModel,
            _systemInfo.BiosVersion, _systemInfo.AgesaVersion);

        // Load designation map (manual/auto timing labels for drift detection)
        _designations = LoadDesignations();

        // Timing CSV logger
        _timingCsvLogger = new TimingCsvLogger(
            string.IsNullOrEmpty(config.LogDirectory) ? DataDirectory.LogsPath : config.LogDirectory);

        // State aggregator
        _aggregator = new StateAggregator(_eventLog, _settings, _pipeServer);

        // Resolve the board vendor for BIOS timing layout ordering.
        // Settings may override "Auto" with an explicit vendor name.
        var vendorSetting = BiosLayouts.ParseSetting(config.BiosLayout);
        var resolvedVendor = BiosLayouts.Resolve(vendorSetting);
        _aggregator.SetBiosVendor(resolvedVendor == BoardVendor.Default
            ? null
            : resolvedVendor.ToString());
        _logger.LogInformation("BIOS layout vendor: {Vendor}", resolvedVendor);

        // Wire Phase 3 services into the aggregator so state pushes include journal data.
        _aggregator.SetPhase3Services(
            _configChangeDetector,
            _driftDetector,
            _validationLogger,
            _lkgTracker,
            _snapshotJournal,
            _eraJournal,
            _bootFailJournal);

        // Wire boot baseline journal for per-source normal/elevated coloring.
        _aggregator.SetBaselineJournal(_baselineJournal);

        // Set initial driver status (even if no timings yet)
        _aggregator.SetTimings(null, _hardwareReader.DriverStatus);

        // Pass CPU family to event monitor for MCA bank classification
        _eventLog.CpuFamily = _hardwareReader.CpuFamily;

        // Initial timing read — populate before marking ready so the first
        // state push to connecting clients already has timing data.
        // Reordered: perform cold-tier UMC read first before blocking scans.
        await ReadTimingsAsync();

        // Historical scan (blocks briefly, populates error counts from boot)
        _eventLog.Start();
        _integrity.ScanCbsLog();

        _aggregator.ScanLiveKernelReports();
        _aggregator.ReadDimmInfo();
        _aggregator.SetAddressMap(_hardwareReader.ReadAddressMap());
        _aggregator.MarkReady();
        _logger.LogInformation("Monitoring active. Boot ID: {BootId}, Driver: {Driver}",
            _bootId, _hardwareReader.DriverStatus);

        // Emit a startup marker so an empty events CSV is unambiguous —
        // a silent success used to look identical to a crashed service.
        _serviceStartedAt = DateTime.UtcNow;
        EmitLifecycleEvent(
            EventSeverity.Info,
            $"Service ready. Pipe: \\\\.\\pipe\\{PipeConstants.PipeName}. " +
            $"EventLog: up. Hardware: {_hardwareReader.DriverName} ({_hardwareReader.DriverStatus}). " +
            $"Board: {_systemInfo?.BoardVendor} {_systemInfo?.BoardModel}. " +
            $"BIOS: {_systemInfo?.BiosVersion}. AGESA: {_systemInfo?.AgesaVersion}.");

        // Three-tier polling:
        // HOT  (3s): thermal + SVI2 voltages → ThermalUpdateMessage
        // WARM (30-60s): full timing read + state broadcast + CBS scan
        // COLD (boot + trigger): UMC timings, WMI (already done above)
        try
        {
            var hotInterval = TimeSpan.FromSeconds(_settings.Current.HotTierSeconds);
            var hotTimer = new PeriodicTimer(hotInterval);
            var warmInterval = TimeSpan.FromSeconds(_settings.Current.RefreshIntervalSeconds);
            var warmTimer = new PeriodicTimer(warmInterval);

            var hotTask = RunHotLoopAsync(hotTimer, stoppingToken);
            var warmTask = RunWarmLoopAsync(warmTimer, stoppingToken);
            await Task.WhenAll(hotTask, warmTask);
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("RAMWatch service stopping");

        // Record this boot's event counts before disposing the event log.
        if (_baselineJournal is not null && _eventLog is not null)
            _baselineJournal.RecordBoot(_bootId, _eventLog.GetErrorSources());

        // Emit the stop marker BEFORE flipping the shutdown barrier so it
        // flows through the normal event path (CSV + mirror + broadcast).
        var uptime = _serviceStartedAt == default
            ? TimeSpan.Zero
            : DateTime.UtcNow - _serviceStartedAt;
        EmitLifecycleEvent(
            EventSeverity.Info,
            $"Service stopping. Uptime: {(int)uptime.TotalHours:D2}:{uptime.Minutes:D2}:{uptime.Seconds:D2}.");

        // Flip the shutdown barrier. Callback threads entering OnEventDetected
        // after this point early-return before touching any disposed service.
        _shuttingDown = true;

        // Unsubscribe from EventLog callbacks and give any in-flight callbacks
        // a brief window to notice the barrier before we dispose what they use.
        if (_eventLog is not null)
            _eventLog.EventDetected -= OnEventDetected;
        await Task.Delay(50, CancellationToken.None);

        _eventLog?.Dispose();
        _csvLogger?.Dispose();
        _timingCsvLogger?.Dispose();
        _hardwareReader?.Dispose();
        _mirrorLogger = null;
        if (_gitCommitter is not null)
            await _gitCommitter.DisposeAsync();
        if (_pipeServer is not null)
            await _pipeServer.DisposeAsync();
        await base.StopAsync(cancellationToken);
    }

    private async Task OnClientConnected(ConnectedClient client)
    {
        if (_aggregator is null) return;
        var state = _aggregator.BuildState();
        var message = new StateMessage { Type = "state", State = state };
        try
        {
            await client.SendAsync(MessageSerializer.Serialize(message));
        }
        catch
        {
            // Client may have disconnected already
        }
    }

    // ── Three-tier polling loops ───────────────────────────────────

    /// <summary>
    /// HOT tier: thermal telemetry + SVI2 voltages every 3s.
    /// Direct SMN reads (~5μs each) + PM table (~100-200μs) — total &lt;1ms.
    /// Broadcasts a lightweight ThermalUpdateMessage, not a full state push.
    /// </summary>
    private async Task RunHotLoopAsync(PeriodicTimer timer, CancellationToken ct)
    {
        if (_hardwareReader is null || !_hardwareReader.IsAvailable || _aggregator is null)
            return;

        int consecutiveFailures = 0;
        const int maxFailures = 5;

        while (await timer.WaitForNextTickAsync(ct))
        {
            try
            {
                var (thermal, vcore, vsoc) = _hardwareReader.ReadHotTier();
                if (thermal is not null)
                {
                    _aggregator.SetThermalPower(thermal);
                    await _aggregator.BroadcastThermalAsync(thermal, vcore, vsoc);
                    consecutiveFailures = 0;
                }
                else
                {
                    consecutiveFailures++;
                }
            }
            catch
            {
                consecutiveFailures++;
            }

            // After 5 consecutive failures, stop the hot timer and degrade
            // to warm-tier thermal data (updated every 30-60s via full state push).
            if (consecutiveFailures >= maxFailures)
            {
                _logger.LogWarning("Hot tier: {Max} consecutive failures, degrading to warm tier", maxFailures);
                return;
            }
        }
    }

    /// <summary>
    /// WARM tier: full timing read + state broadcast + CBS scan every 30-60s.
    /// This is the original monolithic loop, now separated from thermal polling.
    /// </summary>
    private async Task RunWarmLoopAsync(PeriodicTimer timer, CancellationToken ct)
    {
        while (await timer.WaitForNextTickAsync(ct))
        {
            await ReadTimingsAsync();
            await _aggregator!.BroadcastStateAsync();
            _integrity?.ScanCbsLog();
        }
    }

    /// <summary>
    /// Read hardware timings, update aggregator, log to CSV, and run Phase 3 detection.
    /// Called once on startup (cold tier) and on each warm-tier refresh cycle.
    /// Safe to call when driver is unavailable.
    /// </summary>
    private async Task ReadTimingsAsync()
    {
        if (_hardwareReader is null || !_hardwareReader.IsAvailable || _aggregator is null)
            return;

        TimingSnapshot? snapshot;
        try
        {
            snapshot = _hardwareReader.ReadTimings(_bootId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Hardware timing read failed");
            return;
        }

        if (snapshot is null)
            return;

        // Stamp static system info onto the snapshot
        if (_systemInfo is not null)
        {
            snapshot.CpuCodename = _systemInfo.CpuName;
            snapshot.BiosVersion = _systemInfo.BiosVersion;
            snapshot.AgesaVersion = _systemInfo.AgesaVersion;
        }

        _currentTimings = snapshot;
        _aggregator.SetTimings(snapshot, _hardwareReader.DriverStatus);

        // Thermal telemetry is now handled by the hot tier (3s loop).
        // The warm tier no longer reads thermals — avoids the double PM table
        // read that Legolas flagged in the War Council.

        // Auto-save the first complete timing read of this boot into the snapshot journal.
        // Defer until clocks are populated (FCLK/UCLK > 0) to avoid saving incomplete data.
        // Subsequent reads in the same boot session are skipped — only one auto-save per boot.
        if (!_autoSavedThisBoot && _snapshotJournal is not null
            && snapshot.FclkMhz > 0 && snapshot.UclkMhz > 0)
        {
            _autoSavedThisBoot = true;
            // TimingSnapshot.Label has a public setter; set it before persisting.
            // _currentTimings holds the same reference — keep them in sync.
            snapshot.Label = $"Auto {DateTime.UtcNow.ToLocalTime():yyyy-MM-dd HH:mm}";
            snapshot.EraId = _eraJournal?.GetActive()?.EraId;
            try
            {
                _snapshotJournal.Save(snapshot);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Auto-save snapshot failed");
            }
        }

        // Log to timing CSV
        if (_settings.Current.EnableCsvLogging)
        {
            try
            {
                _timingCsvLogger?.LogSnapshot(snapshot);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Timing CSV write failed");
            }
        }

        // Run Phase 3 change/drift detection
        await OnTimingSnapshotAsync(snapshot, _designations);
    }

    /// <summary>
    /// Runs ConfigChangeDetector and DriftDetector when a new TimingSnapshot arrives,
    /// broadcasts change/drift events, and re-evaluates the LKG snapshot.
    /// </summary>
    private async Task OnTimingSnapshotAsync(TimingSnapshot snapshot, DesignationMap designations)
    {
        if (_configChangeDetector is null || _driftDetector is null ||
            _validationLogger is null || _lkgTracker is null ||
            _aggregator is null || _pipeServer is null)
        {
            return;
        }

        // Detect config changes between this boot and the previous one.
        var change = _configChangeDetector.DetectChanges(snapshot);
        if (change is not null)
        {
            var changeEvt = new MonitoredEvent(
                change.Timestamp,
                "Config Change",
                EventCategory.Application,
                0,
                EventSeverity.Info,
                $"Timing configuration changed: {change.Changes.Count} field(s)");
            var changeMsg = new EventMessage
            {
                Type = "event",
                Event = changeEvt,
                IsCritical = changeEvt.Severity == EventSeverity.Critical
            };
            await _pipeServer.BroadcastAsync(MessageSerializer.Serialize(changeMsg));

            _gitCommitter?.Enqueue(new GitCommitRequest
            {
                Reason          = GitCommitReason.ConfigChange,
                CurrentSnapshot = snapshot,
                Change          = change,
                Designations    = designations,
                RecentValidations = _validationLogger.GetRecentResults(10)
            });
        }

        // Detect drift in auto-trained timings. Gate on cold-boot completion
        // so the startup window (where readers stamp in sequence) isn't
        // misread as drift.
        var driftEvents = _driftDetector.CheckForDrift(
            snapshot, designations, _aggregator.GetColdBootStatus());
        if (driftEvents.Count > 0)
        {
            _aggregator.AddDriftEvents(driftEvents);

            foreach (var drift in driftEvents)
            {
                var driftEvt = new MonitoredEvent(
                    drift.Timestamp,
                    "Drift Detected",
                    EventCategory.Application,
                    0,
                    EventSeverity.Warning,
                    $"{drift.TimingName} drifted: expected {drift.ExpectedValue}, got {drift.ActualValue}");
                var driftMsg = new EventMessage
                {
                    Type = "event",
                    Event = driftEvt,
                    IsCritical = driftEvt.Severity == EventSeverity.Critical
                };
                await _pipeServer.BroadcastAsync(MessageSerializer.Serialize(driftMsg));
            }

            _gitCommitter?.Enqueue(new GitCommitRequest
            {
                Reason          = GitCommitReason.DriftDetected,
                CurrentSnapshot = snapshot,
                DriftEvents     = driftEvents,
                Designations    = designations,
                RecentValidations = _validationLogger.GetRecentResults(10)
            });
        }

        // Re-evaluate LKG against current validation results.
        var allSnapshots = _snapshotJournal?.GetAll() ?? new List<TimingSnapshot> { snapshot };
        _lkgTracker.UpdateLkg(_validationLogger.GetResults(), allSnapshots);
    }

    /// <summary>
    /// Emit a synthetic service-lifecycle event (startup/shutdown) through the
    /// normal event path: daily CSV, mirror copy, and broadcast to clients.
    /// Category is Application so the Hardware-only vitals capture is skipped.
    /// </summary>
    private void EmitLifecycleEvent(EventSeverity severity, string summary)
    {
        var evt = new MonitoredEvent(
            DateTime.UtcNow,
            "Service",
            EventCategory.Application,
            0,
            severity,
            summary);
        OnEventDetected(evt);
    }

    private void OnEventDetected(MonitoredEvent evt)
    {
        // Shutdown barrier — the EventLogWatcher callback runs on an
        // arbitrary thread pool thread. If StopAsync has begun, the
        // hardware reader / CSV loggers / pipe server may be mid-dispose;
        // any attempt to touch them would throw ObjectDisposedException
        // from inside a callback the .NET runtime can't recover from.
        if (_shuttingDown) return;

        try
        {
            // For hardware events (WHEA, bugcheck, etc.), capture a thermal/power
            // snapshot at the moment of the event. Direct SMN reads take ~5μs —
            // fast enough for the EventLogWatcher callback thread.
            if (evt.Category == EventCategory.Hardware && _hardwareReader is { IsAvailable: true })
            {
                try
                {
                    var vitals = _hardwareReader.ReadThermalPower();
                    if (vitals is not null)
                        evt = evt with { Vitals = vitals };
                }
                catch { /* Non-fatal — event continues without vitals */ }
            }

            // Log to CSV
            if (_settings.Current.EnableCsvLogging && _csvLogger is not null)
            {
                _csvLogger.LogEvent(evt, _bootId);

                // Fire-and-forget copy to mirror directory (Dropbox, OneDrive, etc.)
                var currentPath = _csvLogger.CurrentFilePath;
                if (!string.IsNullOrEmpty(currentPath))
                    _mirrorLogger?.EnqueueCopy(currentPath);
            }

            // Broadcast to connected GUI clients. Attach a continuation so a
            // disposed-pipe exception during shutdown is observed and logged
            // rather than escalating to an UnobservedTaskException.
            var broadcast = _aggregator?.BroadcastEventAsync(evt);
            if (broadcast is not null)
            {
                _ = broadcast.ContinueWith(
                    t => _logger.LogDebug(t.Exception, "BroadcastEventAsync failed (likely shutdown)"),
                    TaskContinuationOptions.OnlyOnFaulted);
            }
        }
        catch (ObjectDisposedException)
        {
            // Shutdown race narrowly won by dispose — the _shuttingDown check
            // above closes the window for callbacks arriving after dispose
            // starts, but a callback already in flight when the flag flips
            // can still reach here. Swallow silently; the event is lost but
            // the service doesn't crash.
        }
    }

    private async Task OnClientMessage(string line, ConnectedClient client)
    {
        var message = MessageSerializer.Deserialize(line);
        if (message is null)
            return;

        // Reject mismatched protocol versions before dispatching.
        // The GUI must match the service's compiled-in version (B6).
        if (message.ProtocolVersion != IpcMessage.CurrentProtocolVersion)
        {
            await client.SendAsync(MessageSerializer.Serialize(new ResponseMessage
            {
                Type      = "response",
                RequestId = "",
                Status    = "error",
                Code      = "protocol_mismatch",
                Message   = $"Expected protocol version {IpcMessage.CurrentProtocolVersion}, got {message.ProtocolVersion}"
            }));
            return;
        }

        switch (message)
        {
            case GetStateMessage:
                if (_aggregator is not null)
                {
                    var state = _aggregator.BuildState();
                    var response = new StateMessage { Type = "state", State = state };
                    await client.SendAsync(MessageSerializer.Serialize(response));
                }
                break;

            case UpdateSettingsMessage update:
                if (!AppSettings.IsValidDataPath(update.Settings.LogDirectory) ||
                    !AppSettings.IsValidDataPath(update.Settings.MirrorDirectory))
                {
                    await client.SendAsync(MessageSerializer.Serialize(
                        new ResponseMessage
                        {
                            Type = "response",
                            RequestId = update.RequestId,
                            Status = "error",
                            Code = "invalid_path",
                            Message = "LogDirectory or MirrorDirectory contains a disallowed path"
                        }));
                    break;
                }
                // Re-parse the raw line so we know which fields the client
                // actually sent. System.Text.Json's typed deserialize can't
                // distinguish "field absent" from "field set to default";
                // applying update.Settings wholesale would wipe every field
                // a partial-update client forgot to include.
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    if (doc.RootElement.TryGetProperty("settings", out var settingsPatch))
                    {
                        // ApplyPatch merges by JSON field presence and clamps
                        // every numeric before persisting — no further
                        // clamping needed at the handler.
                        _settings.ApplyPatch(settingsPatch);
                    }
                }
                catch (JsonException)
                {
                    await client.SendAsync(MessageSerializer.Serialize(
                        new ResponseMessage
                        {
                            Type = "response",
                            RequestId = update.RequestId,
                            Status = "error",
                            Code = "invalid_payload",
                            Message = "Settings payload could not be parsed"
                        }));
                    break;
                }
                await client.SendAsync(MessageSerializer.Serialize(
                    new ResponseMessage
                    {
                        Type = "response",
                        RequestId = update.RequestId,
                        Status = "ok"
                    }));
                break;

            case RunIntegrityMessage run:
                // Phase 1: validate the check value, return not-implemented for SFC/DISM
                var allowed = new[] { "sfc", "dism_check", "dism_scan" };
                if (!allowed.Contains(run.Check))
                {
                    await client.SendAsync(MessageSerializer.Serialize(
                        new ResponseMessage
                        {
                            Type = "response",
                            RequestId = run.RequestId,
                            Status = "error",
                            Code = "invalid_check",
                            Message = $"Unknown check type: {run.Check}"
                        }));
                }
                else
                {
                    await client.SendAsync(MessageSerializer.Serialize(
                        new ResponseMessage
                        {
                            Type = "response",
                            RequestId = run.RequestId,
                            Status = "error",
                            Code = "not_implemented",
                            Message = "SFC/DISM execution available in a future release"
                        }));
                }
                break;

            case LogValidationMessage log:
                await HandleLogValidationAsync(log, client);
                break;

            case DeleteValidationMessage del:
                await HandleDeleteValidationAsync(del, client);
                break;

            case DeleteChangeMessage delChg:
                await HandleDeleteChangeAsync(delChg, client);
                break;

            case GetDesignationsMessage getDes:
                await HandleGetDesignationsAsync(getDes, client);
                break;

            case UpdateDesignationsMessage updDes:
                await HandleUpdateDesignationsAsync(updDes, client);
                break;

            case SaveSnapshotMessage save:
                await HandleSaveSnapshotAsync(save, client);
                break;

            case GetSnapshotsMessage getSnaps:
                await client.SendAsync(MessageSerializer.Serialize(
                    new SnapshotsResponseMessage
                    {
                        Type = "snapshotsResponse",
                        RequestId = getSnaps.RequestId,
                        Snapshots = _snapshotJournal?.GetAll() ?? new List<TimingSnapshot>()
                    }));
                break;

            case GetDigestMessage getDigest:
                await HandleGetDigestAsync(getDigest, client);
                break;

            case DeleteSnapshotMessage delSnap:
                await HandleDeleteSnapshotAsync(delSnap, client);
                break;

            case RenameSnapshotMessage renSnap:
                await HandleRenameSnapshotAsync(renSnap, client);
                break;

            // Eras
            case CreateEraMessage createEra:
                await HandleCreateEraAsync(createEra, client);
                break;

            case CloseEraMessage closeEra:
                await HandleCloseEraAsync(closeEra, client);
                break;

            case MoveToEraMessage moveToEra:
                await HandleMoveToEraAsync(moveToEra, client);
                break;

            // Boot fails
            case LogBootFailMessage logFail:
                await HandleLogBootFailAsync(logFail, client);
                break;

            case DeleteBootFailMessage delFail:
                await HandleDeleteBootFailAsync(delFail, client);
                break;

            // Timing refresh — external clients (e.g., RAMBurn) can request
            // an immediate cold-tier re-read after a stress test completes.
            case RequestTimingRefreshMessage refresh:
            {
                long now = DateTime.UtcNow.Ticks;
                long prev = Interlocked.Read(ref _lastTimingRefreshTicks);
                if (now - prev < MinTimingRefreshIntervalTicks)
                {
                    // Too soon since the last refresh — a client loop would
                    // otherwise pin the hardware reader behind its lock.
                    await client.SendAsync(MessageSerializer.Serialize(
                        new ResponseMessage
                        {
                            Type = "response",
                            RequestId = refresh.RequestId,
                            Status = "error",
                            Code = "rate_limited",
                            Message = "Timing refresh requested too soon; minimum interval is 1s"
                        }));
                    break;
                }
                Interlocked.Exchange(ref _lastTimingRefreshTicks, now);
                await ReadTimingsAsync();
                await _aggregator!.BroadcastStateAsync();
                await client.SendAsync(MessageSerializer.Serialize(
                    new ResponseMessage
                    {
                        Type = "response",
                        RequestId = refresh.RequestId,
                        Status = "ok"
                    }));
                break;
            }

            default:
                _logger.LogDebug("Unhandled message type: {Type}", message.Type);
                break;
        }
    }

    private async Task HandleGetDigestAsync(GetDigestMessage msg, ConnectedClient client)
    {
        if (_aggregator is null)
        {
            await client.SendAsync(MessageSerializer.Serialize(
                new DigestResponseMessage
                {
                    Type = "digestResponse",
                    RequestId = msg.RequestId,
                    DigestText = null
                }));
            return;
        }

        var state = _aggregator.BuildState();
        var validations = _validationLogger?.GetRecentResults(10) ?? new List<ValidationResult>();
        var drifts = state.DriftEvents ?? new List<DriftEvent>();
        var lkg = _lkgTracker?.CurrentLkg;

        string digest = DigestBuilder.BuildDigest(
            state,
            _currentTimings,
            lkg,
            validations,
            drifts,
            _designations,
            historyCount: 0, // Snapshot journal count — wire when snapshot persistence lands
            recentChanges: state.RecentChanges);

        await client.SendAsync(MessageSerializer.Serialize(
            new DigestResponseMessage
            {
                Type = "digestResponse",
                RequestId = msg.RequestId,
                DigestText = digest
            }));
    }

    private static readonly string DesignationsPath =
        Path.Combine(DataDirectory.BasePath, "designations.json");

    private static DesignationMap LoadDesignations()
    {
        try
        {
            if (File.Exists(DesignationsPath))
            {
                string json = File.ReadAllText(DesignationsPath);
                return JsonSerializer.Deserialize(json, RamWatchJsonContext.Default.DesignationMap)
                       ?? new DesignationMap();
            }
        }
        catch
        {
            // Corrupt — archive and fall through to defaults so the user's
            // Auto/Manual designations aren't silently lost.
            DataDirectory.ArchiveCorruptFile(DesignationsPath);
        }
        return new DesignationMap();
    }

    private static void SaveDesignations(DesignationMap map)
    {
        map.LastUpdated = DateTime.UtcNow;
        string json = JsonSerializer.Serialize(map, RamWatchJsonContext.Default.DesignationMap);
        string temp = DesignationsPath + ".tmp";
        File.WriteAllText(temp, json);
        File.Move(temp, DesignationsPath, overwrite: true);
    }

    private async Task HandleLogValidationAsync(LogValidationMessage msg, ConnectedClient client)
    {
        if (_validationLogger is null || _lkgTracker is null)
        {
            await client.SendAsync(MessageSerializer.Serialize(
                new ResponseMessage
                {
                    Type = "response",
                    RequestId = msg.RequestId,
                    Status = "error",
                    Code = "not_ready",
                    Message = "Validation logger not initialised"
                }));
            return;
        }

        // Sanitize user-supplied strings at the trust boundary. These flow
        // into the snapshot label, validation result, and ultimately the git
        // commit message + CHANGELOG.md. Without sanitization, a client
        // can inject newlines to fake CHANGELOG sections or grow the file
        // unbounded via a large MetricUnit.
        string safeTestTool   = CommitMessageBuilder.Sanitize(msg.TestTool,   maxLen: 128);
        string safeMetricName = CommitMessageBuilder.Sanitize(msg.MetricName, maxLen: 64);
        string safeMetricUnit = CommitMessageBuilder.Sanitize(msg.MetricUnit, maxLen: 16);

        // Auto-save a snapshot labeled with the test result so it appears
        // in the Snapshots comparison dropdown with a meaningful name.
        string? linkedSnapshotId = msg.ActiveSnapshotId;
        if (_currentTimings is not null && _snapshotJournal is not null)
        {
            string passText = msg.Passed ? "PASS" : "FAIL";
            string metric = msg.MetricValue > 0
                ? $"{msg.MetricValue:G4}{safeMetricUnit}"
                : "";
            string label = $"{safeTestTool} {metric} {passText}".Trim();
            if (label.Length > 256) label = label[..256];
            var snapshot = _currentTimings.WithIdAndLabel(Guid.NewGuid().ToString("N"), label);
            _snapshotJournal.Save(snapshot);
            linkedSnapshotId = snapshot.SnapshotId;
        }

        // Truncate free-text fields to prevent unbounded data from reaching disk.
        var result = new ValidationResult
        {
            Timestamp = DateTime.UtcNow,
            BootId = _bootId,
            TestTool = safeTestTool,
            MetricName = safeMetricName,
            MetricValue = msg.MetricValue,
            MetricUnit = safeMetricUnit,
            Passed = msg.Passed,
            ErrorCount = msg.ErrorCount,
            DurationMinutes = msg.DurationMinutes,
            ActiveSnapshotId = linkedSnapshotId,
            Notes = msg.Notes is { Length: > 2048 } n ? n[..2048] : msg.Notes,
            EraId = _eraJournal?.GetActive()?.EraId
        };

        _validationLogger.LogResult(result);

        // Re-evaluate LKG against all results.
        var journalSnapshots = _snapshotJournal?.GetAll() ?? new List<TimingSnapshot>();
        _lkgTracker.UpdateLkg(_validationLogger.GetResults(), journalSnapshots);

        // Commit the validation result to git history.
        if (_currentTimings is not null)
        {
            _gitCommitter?.Enqueue(new GitCommitRequest
            {
                Reason            = GitCommitReason.ValidationTest,
                CurrentSnapshot   = _currentTimings,
                LkgSnapshot       = _lkgTracker.CurrentLkg,
                Validation        = result,
                Designations      = _designations,
                RecentValidations = _validationLogger.GetRecentResults(10)
            });
        }

        await client.SendAsync(MessageSerializer.Serialize(
            new ResponseMessage
            {
                Type = "response",
                RequestId = msg.RequestId,
                Status = "ok"
            }));

        // Broadcast updated state immediately so Timeline/Snapshots refresh
        if (_aggregator is not null)
            await _aggregator.BroadcastStateAsync();
    }

    private async Task HandleDeleteValidationAsync(DeleteValidationMessage msg, ConnectedClient client)
    {
        if (_validationLogger is null || _lkgTracker is null)
        {
            await client.SendAsync(MessageSerializer.Serialize(
                new ResponseMessage
                {
                    Type      = "response",
                    RequestId = msg.RequestId,
                    Status    = "error",
                    Code      = "not_ready",
                    Message   = "Validation logger not initialised"
                }));
            return;
        }

        bool removed = _validationLogger.DeleteById(msg.ValidationId);
        if (!removed)
        {
            await client.SendAsync(MessageSerializer.Serialize(
                new ResponseMessage
                {
                    Type      = "response",
                    RequestId = msg.RequestId,
                    Status    = "error",
                    Code      = "not_found",
                    Message   = $"No validation result with id {msg.ValidationId}"
                }));
            return;
        }

        // Re-evaluate LKG now that the result set has changed.
        var journalSnapshots = _snapshotJournal?.GetAll() ?? new List<TimingSnapshot>();
        _lkgTracker.UpdateLkg(_validationLogger.GetResults(), journalSnapshots);

        await client.SendAsync(MessageSerializer.Serialize(
            new ResponseMessage
            {
                Type      = "response",
                RequestId = msg.RequestId,
                Status    = "ok"
            }));

        // Broadcast updated state immediately so Timeline tab reflects the removal.
        if (_aggregator is not null)
            await _aggregator.BroadcastStateAsync();
    }

    private async Task HandleDeleteChangeAsync(DeleteChangeMessage msg, ConnectedClient client)
    {
        if (_configChangeDetector is null)
        {
            await client.SendAsync(MessageSerializer.Serialize(
                new ResponseMessage
                {
                    Type      = "response",
                    RequestId = msg.RequestId,
                    Status    = "error",
                    Code      = "not_ready",
                    Message   = "Config change detector not initialised"
                }));
            return;
        }

        bool removed = _configChangeDetector.DeleteById(msg.ChangeId);
        if (!removed)
        {
            await client.SendAsync(MessageSerializer.Serialize(
                new ResponseMessage
                {
                    Type      = "response",
                    RequestId = msg.RequestId,
                    Status    = "error",
                    Code      = "not_found",
                    Message   = $"No config change with id {msg.ChangeId}"
                }));
            return;
        }

        await client.SendAsync(MessageSerializer.Serialize(
            new ResponseMessage
            {
                Type      = "response",
                RequestId = msg.RequestId,
                Status    = "ok"
            }));

        if (_aggregator is not null)
            await _aggregator.BroadcastStateAsync();
    }

    private async Task HandleGetDesignationsAsync(GetDesignationsMessage msg, ConnectedClient client)
    {
        // Convert the internal enum-keyed map to the string-keyed wire format.
        var wireMap = _designations.Designations
            .ToDictionary(
                kvp => kvp.Key,
                kvp => kvp.Value.ToString());

        await client.SendAsync(MessageSerializer.Serialize(
            new DesignationsResponseMessage
            {
                Type         = "designationsResponse",
                RequestId    = msg.RequestId,
                Designations = wireMap
            }));
    }

    /// <summary>Maximum number of designation keys accepted from a single IPC message.</summary>
    private const int MaxDesignationKeys = 500;

    private async Task HandleUpdateDesignationsAsync(UpdateDesignationsMessage msg, ConnectedClient client)
    {
        // Reject oversized payloads — the real app has ~40 timing parameters.
        if (msg.Designations.Count > MaxDesignationKeys)
        {
            await client.SendAsync(MessageSerializer.Serialize(
                new ResponseMessage
                {
                    Type = "response",
                    RequestId = msg.RequestId,
                    Status = "error",
                    Code = "payload_too_large",
                    Message = $"Designations map exceeds {MaxDesignationKeys} entries"
                }));
            return;
        }

        // Parse the string values from the wire format into enums.
        // Unrecognised values are silently treated as Unknown (forward compatibility).
        // Cap individual key length to prevent unbounded string storage.
        var parsed = new Dictionary<string, TimingDesignation>(msg.Designations.Count);
        foreach (var (key, value) in msg.Designations)
        {
            if (key.Length > 64) continue; // Skip absurdly long keys
            parsed[key] = Enum.TryParse<TimingDesignation>(value, ignoreCase: true, out var desig)
                ? desig
                : TimingDesignation.Unknown;
        }

        _designations.Designations = parsed;

        try
        {
            SaveDesignations(_designations);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist designation map");
            await client.SendAsync(MessageSerializer.Serialize(
                new ResponseMessage
                {
                    Type      = "response",
                    RequestId = msg.RequestId,
                    Status    = "error",
                    Code      = "io_error",
                    Message   = "Failed to persist designations"
                }));
            return;
        }

        await client.SendAsync(MessageSerializer.Serialize(
            new ResponseMessage
            {
                Type      = "response",
                RequestId = msg.RequestId,
                Status    = "ok"
            }));

        // Broadcast updated state so the GUI designation display refreshes.
        if (_aggregator is not null)
            await _aggregator.BroadcastStateAsync();
    }

    private async Task HandleSaveSnapshotAsync(SaveSnapshotMessage msg, ConnectedClient client)
    {
        if (_snapshotJournal is null || _currentTimings is null)
        {
            await client.SendAsync(MessageSerializer.Serialize(
                new ResponseMessage
                {
                    Type = "response",
                    RequestId = msg.RequestId,
                    Status = "error",
                    Code = "not_ready",
                    Message = _snapshotJournal is null
                        ? "Snapshot journal not initialised"
                        : "No timing data available yet"
                }));
            return;
        }

        // Apply the user-supplied label, or generate a default from the timestamp.
        // Truncate to 256 chars to prevent unbounded data from reaching disk.
        string label = !string.IsNullOrWhiteSpace(msg.Label)
            ? (msg.Label.Trim() is { Length: > 256 } l ? l[..256] : msg.Label.Trim())
            : $"Manual {DateTime.UtcNow.ToLocalTime():yyyy-MM-dd HH:mm}";

        // Assign a new unique ID so this is always a distinct journal entry,
        // even if the timing values are identical to an existing snapshot.
        var snapshot = _currentTimings.WithIdAndLabel(Guid.NewGuid().ToString("N"), label);

        try
        {
            _snapshotJournal.Save(snapshot);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Manual snapshot save failed");
            await client.SendAsync(MessageSerializer.Serialize(
                new ResponseMessage
                {
                    Type = "response",
                    RequestId = msg.RequestId,
                    Status = "error",
                    Code = "io_error",
                    Message = "Failed to persist snapshot"
                }));
            return;
        }

        // Broadcast updated state so the GUI snapshot dropdown refreshes immediately.
        if (_aggregator is not null)
            await _aggregator.BroadcastStateAsync();

        await client.SendAsync(MessageSerializer.Serialize(
            new ResponseMessage
            {
                Type = "response",
                RequestId = msg.RequestId,
                Status = "ok"
            }));
    }

    private async Task HandleDeleteSnapshotAsync(DeleteSnapshotMessage msg, ConnectedClient client)
    {
        if (_snapshotJournal is null)
        {
            await client.SendAsync(MessageSerializer.Serialize(
                new ResponseMessage
                {
                    Type      = "response",
                    RequestId = msg.RequestId,
                    Status    = "error",
                    Code      = "not_ready",
                    Message   = "Snapshot journal not initialised"
                }));
            return;
        }

        bool removed = _snapshotJournal.DeleteById(msg.SnapshotId);
        if (!removed)
        {
            await client.SendAsync(MessageSerializer.Serialize(
                new ResponseMessage
                {
                    Type      = "response",
                    RequestId = msg.RequestId,
                    Status    = "error",
                    Code      = "not_found",
                    Message   = $"No snapshot with id {msg.SnapshotId}"
                }));
            return;
        }

        // Broadcast updated state so all connected GUIs refresh snapshot lists.
        if (_aggregator is not null)
            await _aggregator.BroadcastStateAsync();

        await client.SendAsync(MessageSerializer.Serialize(
            new ResponseMessage
            {
                Type      = "response",
                RequestId = msg.RequestId,
                Status    = "ok"
            }));
    }

    private async Task HandleRenameSnapshotAsync(RenameSnapshotMessage msg, ConnectedClient client)
    {
        if (_snapshotJournal is null)
        {
            await client.SendAsync(MessageSerializer.Serialize(
                new ResponseMessage
                {
                    Type      = "response",
                    RequestId = msg.RequestId,
                    Status    = "error",
                    Code      = "not_ready",
                    Message   = "Snapshot journal not initialised"
                }));
            return;
        }

        // Reject blank labels — the service never stores a snapshot with no label.
        if (string.IsNullOrWhiteSpace(msg.NewLabel))
        {
            await client.SendAsync(MessageSerializer.Serialize(
                new ResponseMessage
                {
                    Type      = "response",
                    RequestId = msg.RequestId,
                    Status    = "error",
                    Code      = "invalid_label",
                    Message   = "NewLabel must not be empty"
                }));
            return;
        }

        bool updated = _snapshotJournal.RenameById(msg.SnapshotId, msg.NewLabel);
        if (!updated)
        {
            await client.SendAsync(MessageSerializer.Serialize(
                new ResponseMessage
                {
                    Type      = "response",
                    RequestId = msg.RequestId,
                    Status    = "error",
                    Code      = "not_found",
                    Message   = $"No snapshot with id {msg.SnapshotId}"
                }));
            return;
        }

        // Broadcast updated state so all connected GUIs refresh snapshot labels.
        if (_aggregator is not null)
            await _aggregator.BroadcastStateAsync();

        await client.SendAsync(MessageSerializer.Serialize(
            new ResponseMessage
            {
                Type      = "response",
                RequestId = msg.RequestId,
                Status    = "ok"
            }));
    }

    // ── Era handlers ────────────────────────────────────────────

    private async Task HandleCreateEraAsync(CreateEraMessage msg, ConnectedClient client)
    {
        if (_eraJournal is null)
        {
            await SendErrorAsync(client, msg.RequestId, "not_ready", "Era journal not initialised");
            return;
        }

        string name = msg.Name is { Length: > 256 } ? msg.Name[..256] : msg.Name;
        _eraJournal.Create(name);

        await SendOkAsync(client, msg.RequestId);
        if (_aggregator is not null)
            await _aggregator.BroadcastStateAsync();
    }

    private async Task HandleCloseEraAsync(CloseEraMessage msg, ConnectedClient client)
    {
        if (_eraJournal is null || !_eraJournal.Close(msg.EraId))
        {
            await SendErrorAsync(client, msg.RequestId, "not_found", "Era not found or already closed");
            return;
        }

        await SendOkAsync(client, msg.RequestId);
        if (_aggregator is not null)
            await _aggregator.BroadcastStateAsync();
    }

    private async Task HandleMoveToEraAsync(MoveToEraMessage msg, ConnectedClient client)
    {
        if (_snapshotJournal is null)
        {
            await SendErrorAsync(client, msg.RequestId, "not_ready", "Snapshot journal not initialised");
            return;
        }

        if (!_snapshotJournal.SetEraById(msg.SnapshotId, msg.EraId))
        {
            await SendErrorAsync(client, msg.RequestId, "not_found", "Snapshot not found");
            return;
        }

        await SendOkAsync(client, msg.RequestId);
        if (_aggregator is not null)
            await _aggregator.BroadcastStateAsync();
    }

    // ── Boot fail handlers ──────────────────────────────────────

    private async Task HandleLogBootFailAsync(LogBootFailMessage msg, ConnectedClient client)
    {
        if (_bootFailJournal is null)
        {
            await SendErrorAsync(client, msg.RequestId, "not_ready", "Boot fail journal not initialised");
            return;
        }

        // BootFailKind is deserialized as an integer. Enum.IsDefined rejects
        // out-of-range values that a malformed or malicious client could send
        // (e.g. Kind=99), which would otherwise persist a garbage enum that
        // confuses any future GUI branch on the value.
        if (!Enum.IsDefined<BootFailKind>(msg.Kind))
        {
            await SendErrorAsync(client, msg.RequestId, "invalid_kind",
                $"Unknown BootFailKind value: {(int)msg.Kind}");
            return;
        }

        var entry = new BootFailEntry
        {
            BootFailId = Guid.NewGuid().ToString("N"),
            Timestamp = msg.AttemptTimestamp,
            Kind = msg.Kind,
            BaseSnapshotId = msg.BaseSnapshotId,
            AttemptedChanges = msg.AttemptedChanges,
            Notes = msg.Notes is { Length: > 1024 } ? msg.Notes[..1024] : (msg.Notes ?? ""),
            EraId = _eraJournal?.GetActive()?.EraId
        };

        _bootFailJournal.Save(entry);

        await SendOkAsync(client, msg.RequestId);
        if (_aggregator is not null)
            await _aggregator.BroadcastStateAsync();
    }

    private async Task HandleDeleteBootFailAsync(DeleteBootFailMessage msg, ConnectedClient client)
    {
        if (_bootFailJournal is null || !_bootFailJournal.DeleteById(msg.BootFailId))
        {
            await SendErrorAsync(client, msg.RequestId, "not_found", "Boot fail entry not found");
            return;
        }

        await SendOkAsync(client, msg.RequestId);
        if (_aggregator is not null)
            await _aggregator.BroadcastStateAsync();
    }

    // ── Response helpers ────────────────────────────────────────

    private static Task SendOkAsync(ConnectedClient client, string requestId) =>
        client.SendAsync(MessageSerializer.Serialize(
            new ResponseMessage { Type = "response", RequestId = requestId, Status = "ok" }));

    private static Task SendErrorAsync(ConnectedClient client, string requestId, string code, string message) =>
        client.SendAsync(MessageSerializer.Serialize(
            new ResponseMessage { Type = "response", RequestId = requestId, Status = "error", Code = code, Message = message }));
}
