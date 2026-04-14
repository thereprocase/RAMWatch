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

        // Boot ID for CSV grouping
        var bootTime = DateTime.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64);
        _bootId = CsvLogger.GenerateBootId(bootTime);

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

        // Phase 4 — git committer (initialise after LKG so commit context is available)
        _gitCommitter = new GitCommitter(_settings, _logger);
        await _gitCommitter.InitializeAsync(stoppingToken);

        // Hardware reader — detects PawnIO, opens driver, detects CPU
        _hardwareReader = new HardwareReader();
        _logger.LogInformation("Hardware driver: {Name} — {Status}",
            _hardwareReader.DriverName, _hardwareReader.DriverDescription);

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
            _snapshotJournal);

        // Set initial driver status (even if no timings yet)
        _aggregator.SetTimings(null, _hardwareReader.DriverStatus);

        // Historical scan (blocks briefly, populates error counts from boot)
        _eventLog.Start();
        _integrity.ScanCbsLog();

        // Initial timing read — populate before marking ready so the first
        // state push to connecting clients already has timing data.
        await ReadTimingsAsync();

        _aggregator.MarkReady();
        _logger.LogInformation("Monitoring active. Boot ID: {BootId}, Driver: {Driver}",
            _bootId, _hardwareReader.DriverStatus);

        // Periodic refresh loop
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await ReadTimingsAsync();
                await _aggregator.BroadcastStateAsync();
                _integrity.ScanCbsLog();

                await Task.Delay(
                    TimeSpan.FromSeconds(_settings.Current.RefreshIntervalSeconds),
                    stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("RAMWatch service stopping");
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

    /// <summary>
    /// Read hardware timings, update aggregator, log to CSV, and run Phase 3 detection.
    /// Called once on startup and on each refresh cycle. Safe to call when driver is unavailable.
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

        _currentTimings = snapshot;
        _aggregator.SetTimings(snapshot, _hardwareReader.DriverStatus);

        // Auto-save the first timing read of this boot into the snapshot journal.
        // Subsequent reads in the same boot session are skipped — only one auto-save per boot.
        if (!_autoSavedThisBoot && _snapshotJournal is not null)
        {
            _autoSavedThisBoot = true;
            // TimingSnapshot.Label has a public setter; set it before persisting.
            // _currentTimings holds the same reference — keep them in sync.
            snapshot.Label = $"Auto {DateTime.UtcNow.ToLocalTime():yyyy-MM-dd HH:mm}";
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
            var changeMsg = new EventMessage
            {
                Type = "event",
                Event = new MonitoredEvent(
                    change.Timestamp,
                    "Config Change",
                    EventCategory.Application,
                    0,
                    EventSeverity.Info,
                    $"Timing configuration changed: {change.Changes.Count} field(s)")
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

        // Detect drift in auto-trained timings.
        var driftEvents = _driftDetector.CheckForDrift(snapshot, designations);
        if (driftEvents.Count > 0)
        {
            _aggregator.AddDriftEvents(driftEvents);

            foreach (var drift in driftEvents)
            {
                var driftMsg = new EventMessage
                {
                    Type = "event",
                    Event = new MonitoredEvent(
                        drift.Timestamp,
                        "Drift Detected",
                        EventCategory.Application,
                        0,
                        EventSeverity.Warning,
                        $"{drift.TimingName} drifted: expected {drift.ExpectedValue}, got {drift.ActualValue}")
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

    private void OnEventDetected(MonitoredEvent evt)
    {
        // Log to CSV
        if (_settings.Current.EnableCsvLogging && _csvLogger is not null)
        {
            _csvLogger.LogEvent(evt, _bootId);

            // Fire-and-forget copy to mirror directory (Dropbox, OneDrive, etc.)
            var currentPath = _csvLogger.CurrentFilePath;
            if (!string.IsNullOrEmpty(currentPath))
                _mirrorLogger?.EnqueueCopy(currentPath);
        }

        // Broadcast to connected GUI clients
        _ = _aggregator?.BroadcastEventAsync(evt);
    }

    private async Task OnClientMessage(string line, ConnectedClient client)
    {
        var message = MessageSerializer.Deserialize(line);
        if (message is null)
            return;

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
                _settings.Update(update.Settings);
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
            historyCount: 0); // Snapshot journal count — wire when snapshot persistence lands

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
            // Corrupt or missing — use defaults
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

        var result = new ValidationResult
        {
            Timestamp = DateTime.UtcNow,
            BootId = _bootId,
            TestTool = msg.TestTool,
            MetricName = msg.MetricName,
            MetricValue = msg.MetricValue,
            MetricUnit = msg.MetricUnit,
            Passed = msg.Passed,
            ErrorCount = msg.ErrorCount,
            DurationMinutes = msg.DurationMinutes,
            ActiveSnapshotId = msg.ActiveSnapshotId,
            Notes = msg.Notes
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
        string label = !string.IsNullOrWhiteSpace(msg.Label)
            ? msg.Label.Trim()
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
}
