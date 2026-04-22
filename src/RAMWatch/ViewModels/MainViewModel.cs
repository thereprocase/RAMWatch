using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RAMWatch.Core;
using RAMWatch.Core.Ipc;
using RAMWatch.Core.Models;
using RAMWatch.Services;
using RAMWatch.Views;

namespace RAMWatch.ViewModels;

/// <summary>
/// Main view model. Connects to the service pipe, receives state pushes,
/// exposes all UI-bound properties. Reconnects automatically on disconnect.
/// </summary>
public partial class MainViewModel : ObservableObject
{
    private readonly PipeClient _pipe = new();
    private CancellationTokenSource? _cts;

    // Lazily populated when MainWindow wires this up after construction.
    // Used to read notification toggle settings in ApplyEvent.
    internal SettingsViewModel? Settings;

    // Last-received designation map from the service (string-keyed wire format).
    // Updated on DesignationsResponseMessage; forwarded to Timings on each state push.
    private IReadOnlyDictionary<string, string>? _currentDesignations;

    // Timestamps for per-event-type notification cooldown.
    private readonly Dictionary<string, DateTime> _lastNotified = new();

    // Per-source ring buffer of recent events. Seeded on initial state push from
    // ServiceState.RecentEvents, then appended in ApplyEvent. Each source is
    // capped so a noisy source can't grow without bound.
    //
    // Queue&lt;T&gt; — Enqueue + Dequeue-on-overflow is O(1). The prior List&lt;T&gt;
    // with RemoveRange(0, ...) shifted every element on each append once
    // the cap was reached; under a WHEA storm that was 49 entries shifted
    // per event under _eventsLock on the UI-bound hot path.
    private readonly Dictionary<string, Queue<MonitoredEvent>> _eventsBySource = new();
    private readonly Lock _eventsLock = new();
    private const int EventsPerSourceCap = 50;
    private bool _eventsSeeded;

    // ── Connection state ─────────────────────────────────────

    private bool _settingsLoaded;

    [ObservableProperty]
    private bool _isConnected;

    [ObservableProperty]
    private bool _isReady;

    [ObservableProperty]
    private string _connectionStatus = "Connecting to service...";

    // ── Status header ────────────────────────────────────────

    [ObservableProperty]
    private string _statusText = "CONNECTING";

    [ObservableProperty]
    private string _statusColor = "Gray";

    [ObservableProperty]
    private int _totalErrorCount;

    // Stability errors are from hardware-category sources (WHEA, MCE, Bugcheck, etc.).
    // System errors are everything else. Status header color is driven by stability count only.
    [ObservableProperty]
    private int _stabilityErrorCount;

    [ObservableProperty]
    private int _systemEventCount;

    // Shown next to the status text — separates stability from system counts.
    [ObservableProperty]
    private string _statusDetail = "";

    [ObservableProperty]
    private string _bootTimeText = "Boot:--";

    [ObservableProperty]
    private string _uptimeText = "Up:--";

    [ObservableProperty]
    private string _lastUpdateText = "Updated:--";

    // Live wall-clock, distinct from LastUpdateText so the user can see a
    // ticking "now" next to a fixed "last data push" and tell at a glance
    // whether the service is still feeding us.
    [ObservableProperty]
    private string _clockText = "--:--:--";

    // Raw anchors for the live-tick counters. UptimeText is recomputed from
    // _bootTimeUtc on each TickClock(); ClockText is just DateTime.Now.
    // LastUpdateText is set once per push and left alone between ticks.
    private DateTime _bootTimeUtc;

    // Composed from the service's SystemInfoReader output (CPU codename,
    // BIOS version, AGESA version) once a state push arrives. Shown on the
    // status header above the tab control so users can see the board the
    // service locked onto without leaving the Monitor tab. Blank until the
    // service sends its first state message with a populated TimingSnapshot.
    [ObservableProperty]
    private string _systemInfoText = "";

    [ObservableProperty]
    private string _driverStatus = "unknown";

    // Cold-boot completion flag from the service. True means every cold-tier
    // reader has stamped and the data is stable; false means the service is
    // still within the startup window. Defaults to true so pre-tracking
    // servers (null on the wire) and the pre-connect state don't flash the
    // banner — only an explicit false from the service hides tuning
    // affordances.
    [ObservableProperty]
    private bool _coldBootComplete = true;

    // Resolved board vendor from the service — e.g. "MSI", "ASUS". Empty when not yet received.
    // Used to populate the "detected:" label in Settings and to drive TimingsTab layout.
    [ObservableProperty]
    private string _detectedBiosVendor = "";

    // ── Error sources ────────────────────────────────────────

    public ObservableCollection<ErrorSourceVm> ErrorSources { get; } = [];

    // ── Timings (Phase 2) ────────────────────────────────────

    public TimingsViewModel Timings { get; } = new();

    // Raw snapshot from the most recent state push — used for clipboard export.
    private TimingSnapshot? _currentTimings;
    // Cached DIMM list for clipboard export.
    private List<DimmInfo>? _currentDimms;
    private bool _dimmsLoaded;
    // Cached thermal snapshot for clipboard export.
    private ThermalPowerSnapshot? _currentThermalPower;

    // ── Timeline + Snapshots (Phase 3) ──────────────────────

    public TimelineViewModel Timeline { get; } = new();
    public SnapshotsViewModel Snapshots { get; } = new();
    public MinimumsViewModel Minimums { get; } = new();

    public MainViewModel()
    {
        // Wire the IPC delete callbacks into the Timeline so confirmed deletes reach
        // the service rather than only removing the row from the local collection.
        Timeline.SetDeleteValidationHandler(SendDeleteValidationAsync);
        Timeline.SetDeleteChangeHandler(SendDeleteChangeAsync);
        Timeline.SetCreateEraHandler(SendCreateEraAsync);
        Timeline.SetCloseEraHandler(SendCloseEraAsync);

        // Wire IPC callbacks into the Snapshots view model for delete and rename.
        Snapshots.SetDeleteHandler(SendDeleteSnapshotAsync);
        Snapshots.SetRenameHandler(SendRenameSnapshotAsync);
    }

    // ── Integrity ────────────────────────────────────────────

    [ObservableProperty]
    private string _cbsStatus = "Not scanned";

    [ObservableProperty]
    private string _sfcStatus = "Not run";

    [ObservableProperty]
    private string _dismStatus = "Not run";

    // ── Commands ─────────────────────────────────────────────

    [RelayCommand]
    private void CopyToClipboard()
    {
        var text = BuildClipboardExport();
        Clipboard.SetText(text);
    }

    /// <summary>
    /// Sends GetDigestMessage to the service and puts the returned digest text
    /// on the clipboard. Falls back to BuildClipboardExport() when disconnected
    /// or when the service returns an empty digest (no snapshot history yet).
    /// </summary>
    [RelayCommand]
    private async Task CopyDigestAsync()
    {
        if (!_pipe.IsConnected)
        {
            var fallback = BuildClipboardExport();
            Clipboard.SetText(fallback);
            return;
        }

        var requestId = Guid.NewGuid().ToString("N");
        var msg = new GetDigestMessage
        {
            Type = "getDigest",
            RequestId = requestId,
            HistoryCount = 10
        };

        // Register this request's awaitable BEFORE sending so the response
        // handler always has a target — even if the pipe answers before the
        // SendAsync awaiter resumes. Dictionary-keyed completion sources
        // let two rapid Copy Digest invocations each receive their own
        // response; the prior single-slot _pendingDigestRequestId would
        // let the second overwrite the first, and both awaits would drain
        // from the same volatile field.
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_digestLock)
        {
            _digestWaiters[requestId] = tcs;
        }

        try
        {
            await _pipe.SendAsync(MessageSerializer.Serialize(msg));

            // Wait up to 5s for the response. If it doesn't arrive, fall back
            // to the local export so the tray action never silently does nothing.
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                var digest = await tcs.Task.WaitAsync(cts.Token);
                if (!string.IsNullOrWhiteSpace(digest))
                {
                    Clipboard.SetText(digest);
                    return;
                }
            }
            catch (OperationCanceledException) { /* fall through to local export */ }

            Clipboard.SetText(BuildClipboardExport());
        }
        finally
        {
            lock (_digestLock)
            {
                _digestWaiters.Remove(requestId);
            }
        }
    }

    // ── Digest state — set by ProcessMessage when a DigestResponseMessage arrives.
    // Keyed by RequestId so concurrent Copy Digest invocations don't collide.
    private readonly Dictionary<string, TaskCompletionSource<string>> _digestWaiters = new();
    private readonly Lock _digestLock = new();

    [RelayCommand]
    private async Task SaveSnapshotAsync(string? label = null)
    {
        if (!_pipe.IsConnected) return;
        var msg = new SaveSnapshotMessage
        {
            Type = "saveSnapshot",
            RequestId = Guid.NewGuid().ToString("N"),
            Label = string.IsNullOrWhiteSpace(label) ? null : label.Trim()
        };
        await _pipe.SendAsync(MessageSerializer.Serialize(msg));
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (!_pipe.IsConnected) return;
        var msg = new GetStateMessage
        {
            Type = "getState",
            RequestId = Guid.NewGuid().ToString("N")
        };
        await _pipe.SendAsync(MessageSerializer.Serialize(msg));
    }

    /// <summary>
    /// Opens the LogValidation dialog. If the user submits, sends the message to
    /// the service. Updates the status bar label with a brief confirmation.
    /// No-op when the dialog is cancelled.
    /// </summary>
    [RelayCommand]
    private async Task LogValidationAsync()
    {
        var dialog = new LogValidationDialog
        {
            Owner = Application.Current?.MainWindow
        };

        if (dialog.ShowDialog() != true || dialog.Result is null)
            return;

        if (!_pipe.IsConnected)
        {
            ValidationConfirmation = "Service not connected — result not saved.";
            return;
        }

        await _pipe.SendAsync(MessageSerializer.Serialize(dialog.Result));
        var tool = dialog.Result.TestTool;
        var outcome = dialog.Result.Passed ? "pass" : "fail";
        ValidationConfirmation = $"Logged: {tool} — {outcome}";

        // Clear the confirmation label after a few seconds.
        _ = Task.Delay(5000).ContinueWith(_ =>
        {
            try { Application.Current?.Dispatcher.Invoke(() => ValidationConfirmation = ""); }
            catch { /* app shutdown race: Dispatcher disposed while this timer was pending */ }
        });
    }

    [ObservableProperty]
    private string _validationConfirmation = "";

    /// <summary>
    /// Opens the LogBootFail dialog. If the user submits, sends the message to
    /// the service so the attempt is recorded in boot-fails.json even when
    /// RAMWatch couldn't observe the crash boot. Updates the status label with
    /// a brief confirmation. No-op when cancelled or the pipe is disconnected.
    /// </summary>
    [RelayCommand]
    private async Task LogBootFailAsync()
    {
        var dialog = new LogBootFailDialog
        {
            Owner = Application.Current?.MainWindow,
            // Anchor the attempted-changes delta against the most recent
            // captured snapshot — typically the pre-attempt stable boot.
            // Service persists this verbatim; null is acceptable if unknown.
            BaseSnapshotId = _currentTimings?.SnapshotId
        };

        if (dialog.ShowDialog() != true || dialog.Result is null)
            return;

        if (!_pipe.IsConnected)
        {
            BootFailConfirmation = "Service not connected — attempt not saved.";
            return;
        }

        await _pipe.SendAsync(MessageSerializer.Serialize(dialog.Result));
        BootFailConfirmation = $"Logged: {dialog.Result.Kind} boot-fail attempt";

        _ = Task.Delay(5000).ContinueWith(_ =>
        {
            try { Application.Current?.Dispatcher.Invoke(() => BootFailConfirmation = ""); }
            catch { /* app shutdown race: Dispatcher disposed while this timer was pending */ }
        });
    }

    [ObservableProperty]
    private string _bootFailConfirmation = "";

    /// <summary>
    /// Sends an UpdateSettingsMessage to the service. Called by SettingsViewModel.SaveCommand.
    /// No-op if the pipe is not connected.
    /// </summary>
    public async Task SendUpdateSettingsAsync(AppSettings settings)
    {
        if (!_pipe.IsConnected) return;
        var msg = new UpdateSettingsMessage
        {
            Type = "updateSettings",
            RequestId = Guid.NewGuid().ToString("N"),
            Settings = settings
        };
        await _pipe.SendAsync(MessageSerializer.Serialize(msg));
    }

    /// <summary>
    /// Sends a DeleteValidationMessage to the service for the given validation ID.
    /// Called by Timeline entries on confirmed delete. No-op if not connected.
    /// </summary>
    public async Task SendDeleteValidationAsync(string validationId)
    {
        if (!_pipe.IsConnected) return;
        var msg = new DeleteValidationMessage
        {
            Type         = "deleteValidation",
            RequestId    = Guid.NewGuid().ToString("N"),
            ValidationId = validationId
        };
        await _pipe.SendAsync(MessageSerializer.Serialize(msg));
    }

    /// <summary>
    /// Opens a new tuning era with the given name. The service tags every
    /// subsequent snapshot, validation, and boot-fail with the era ID until
    /// CloseEra is called — this is the anchor for the "new deliberate BIOS
    /// config I'm testing" workflow. No-op if not connected.
    /// </summary>
    public async Task SendCreateEraAsync(string name)
    {
        if (!_pipe.IsConnected) return;
        if (string.IsNullOrWhiteSpace(name)) return;
        var msg = new CreateEraMessage
        {
            Type      = "createEra",
            RequestId = Guid.NewGuid().ToString("N"),
            Name      = name.Trim(),
        };
        await _pipe.SendAsync(MessageSerializer.Serialize(msg));
    }

    /// <summary>
    /// Closes the era with the given ID. The service stops auto-tagging new
    /// entries; existing entries keep their EraId for historical lookup.
    /// </summary>
    public async Task SendCloseEraAsync(string eraId)
    {
        if (!_pipe.IsConnected) return;
        if (string.IsNullOrEmpty(eraId)) return;
        var msg = new CloseEraMessage
        {
            Type      = "closeEra",
            RequestId = Guid.NewGuid().ToString("N"),
            EraId     = eraId,
        };
        await _pipe.SendAsync(MessageSerializer.Serialize(msg));
    }

    /// <summary>
    /// Sends a DeleteChangeMessage to the service for the given change ID.
    /// Called by Timeline entries on confirmed delete. No-op if not connected.
    /// </summary>
    public async Task SendDeleteChangeAsync(string changeId)
    {
        if (!_pipe.IsConnected) return;
        var msg = new DeleteChangeMessage
        {
            Type      = "deleteChange",
            RequestId = Guid.NewGuid().ToString("N"),
            ChangeId  = changeId
        };
        await _pipe.SendAsync(MessageSerializer.Serialize(msg));
    }

    /// <summary>
    /// Sends a DeleteSnapshotMessage to the service for the given snapshot ID.
    /// Called by SnapshotsViewModel on confirmed delete. No-op if not connected.
    /// </summary>
    public async Task SendDeleteSnapshotAsync(string snapshotId)
    {
        if (!_pipe.IsConnected) return;
        var msg = new DeleteSnapshotMessage
        {
            Type       = "deleteSnapshot",
            RequestId  = Guid.NewGuid().ToString("N"),
            SnapshotId = snapshotId
        };
        await _pipe.SendAsync(MessageSerializer.Serialize(msg));
    }

    /// <summary>
    /// Sends a RenameSnapshotMessage to the service.
    /// Called by SnapshotsViewModel on rename confirm. No-op if not connected.
    /// </summary>
    public async Task SendRenameSnapshotAsync(string snapshotId, string newLabel)
    {
        if (!_pipe.IsConnected) return;
        var msg = new RenameSnapshotMessage
        {
            Type       = "renameSnapshot",
            RequestId  = Guid.NewGuid().ToString("N"),
            SnapshotId = snapshotId,
            NewLabel   = newLabel
        };
        await _pipe.SendAsync(MessageSerializer.Serialize(msg));
    }

    /// <summary>
    /// Sends a GetDesignationsMessage to the service. The response arrives
    /// asynchronously as a DesignationsResponseMessage in ProcessMessage,
    /// which then calls SettingsViewModel.LoadDesignations.
    /// No-op if the pipe is not connected.
    /// </summary>
    public async Task SendGetDesignationsAsync()
    {
        if (!_pipe.IsConnected) return;
        var msg = new GetDesignationsMessage
        {
            Type = "getDesignations",
            RequestId = Guid.NewGuid().ToString("N")
        };
        await _pipe.SendAsync(MessageSerializer.Serialize(msg));
    }

    /// <summary>
    /// Sends an UpdateDesignationsMessage to the service. Called by SettingsViewModel
    /// whenever the user changes a designation dropdown.
    /// No-op if the pipe is not connected.
    /// </summary>
    public async Task SendUpdateDesignationsAsync(Dictionary<string, string> designations)
    {
        if (!_pipe.IsConnected) return;
        var msg = new UpdateDesignationsMessage
        {
            Type = "updateDesignations",
            RequestId = Guid.NewGuid().ToString("N"),
            Designations = designations
        };
        await _pipe.SendAsync(MessageSerializer.Serialize(msg));
    }

    // ── Lifecycle ────────────────────────────────────────────

    public async Task StartAsync()
    {
        _cts = new CancellationTokenSource();
        await ConnectAndListenAsync(_cts.Token);
    }

    public async Task StopAsync()
    {
        _cts?.Cancel();
        await _pipe.DisposeAsync();
    }

    /// <summary>
    /// How long the GUI waits on a stuck connect before changing the
    /// status text to something more actionable. The PipeClient retries
    /// with exponential backoff forever; without this watchdog the
    /// status would read "Connecting to service..." indefinitely when
    /// the service is just not running, giving the user nothing to act on.
    /// </summary>
    internal static readonly TimeSpan ConnectStuckThreshold = TimeSpan.FromSeconds(12);

    private async Task ConnectAndListenAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            ConnectionStatus = "Connecting to service...";
            IsConnected = false;

            var connectTask = _pipe.ConnectWithRetryAsync(ct);

            // If the first ~12s of retries don't find the service, flip
            // the status to a user-actionable message. Retries continue
            // in the background; a later successful connect still lands
            // cleanly because `IsConnected = true` / "Connected" run after
            // the await below.
            var timeoutTask = Task.Delay(ConnectStuckThreshold, ct);
            if (await Task.WhenAny(connectTask, timeoutTask) == timeoutTask &&
                !ct.IsCancellationRequested && !connectTask.IsCompleted)
            {
                ConnectionStatus = "Service not responding — is RAMWatch.Service running?";
            }

            await connectTask;
            if (ct.IsCancellationRequested) break;

            IsConnected = true;
            ConnectionStatus = "Connected";

            // Fetch the current designation map so the Settings tab populates
            // without requiring the user to open it first.
            await SendGetDesignationsAsync();

            try
            {
                await foreach (var line in _pipe.ReadLinesAsync(ct))
                {
                    ProcessMessage(line);
                }
            }
            catch (OperationCanceledException) { break; }
            catch { }

            // Disconnected — loop will reconnect
            IsConnected = false;
            ConnectionStatus = "Service disconnected. Reconnecting...";
        }
    }

    private void ProcessMessage(string line)
    {
        var message = MessageSerializer.Deserialize(line);
        if (message is null) return;

        switch (message)
        {
            case StateMessage state:
                ApplyState(state.State);
                break;
            case EventMessage evt:
                ApplyEvent(evt.Event);
                break;
            case ThermalUpdateMessage thermal:
                ApplyThermalUpdate(thermal);
                break;
            case DigestResponseMessage digest:
                // Route the response to the specific CopyDigestAsync call that
                // requested it. Multiple in-flight requests each have their own
                // TaskCompletionSource keyed by RequestId.
                {
                    TaskCompletionSource<string>? waiter = null;
                    lock (_digestLock)
                    {
                        if (digest.RequestId is not null)
                            _digestWaiters.TryGetValue(digest.RequestId, out waiter);
                    }
                    waiter?.TrySetResult(digest.DigestText ?? "");
                }
                break;
            case DesignationsResponseMessage desig:
                // Cache the map for use in subsequent state pushes to TimingsViewModel.
                _currentDesignations = desig.Designations;
                // Forward to the Settings tab so the dropdowns reflect current state.
                Application.Current?.Dispatcher.Invoke(() =>
                    Settings?.LoadDesignations(desig.Designations));
                break;
        }
    }

    /// <summary>
    /// Returns a snapshot of recent events for the given source name, newest-first.
    /// Empty list when no events have been recorded for that source.
    /// Thread-safe: returns a copy so the dialog can iterate without locking.
    /// </summary>
    public IReadOnlyList<MonitoredEvent> GetEventsForSource(string sourceName)
    {
        lock (_eventsLock)
        {
            if (_eventsBySource.TryGetValue(sourceName, out var queue))
                return queue.ToList();
        }
        return Array.Empty<MonitoredEvent>();
    }

    private void StoreEvent(MonitoredEvent evt)
    {
        lock (_eventsLock)
        {
            if (!_eventsBySource.TryGetValue(evt.Source, out var queue))
            {
                queue = new Queue<MonitoredEvent>(EventsPerSourceCap);
                _eventsBySource[evt.Source] = queue;
            }
            queue.Enqueue(evt);
            while (queue.Count > EventsPerSourceCap)
                queue.Dequeue();
        }
    }

    private void SeedEvents(IReadOnlyList<MonitoredEvent> events)
    {
        lock (_eventsLock)
        {
            // Replace any prior buffer — the service-side list is authoritative on
            // initial connect. After this seed, ApplyEvent appends ongoing events.
            _eventsBySource.Clear();
            foreach (var evt in events)
            {
                if (!_eventsBySource.TryGetValue(evt.Source, out var queue))
                {
                    queue = new Queue<MonitoredEvent>(EventsPerSourceCap);
                    _eventsBySource[evt.Source] = queue;
                }
                queue.Enqueue(evt);
                while (queue.Count > EventsPerSourceCap)
                    queue.Dequeue();
            }
        }
    }

    private void ApplyState(ServiceState state)
    {
        IsReady = state.Ready;

        // Seed the per-source event buffer once on initial connect so the detail
        // view has history for events that fired before the GUI started. Later
        // state pushes are ignored to preserve in-memory ordering of newer events
        // already received via EventMessage.
        if (!_eventsSeeded && state.RecentEvents is { Count: > 0 })
        {
            _eventsSeeded = true;
            SeedEvents(state.RecentEvents);
        }

        // Error sources — split into stability (Hardware category) and system (everything else).
        Application.Current?.Dispatcher.Invoke(() =>
        {
            ErrorSources.Clear();
            int total = 0;
            int stability = 0;
            int system = 0;
            var baselines = state.SourceBaselines;
            // Stability sources first so they appear at the top of the table.
            foreach (var src in state.Errors.Where(s => s.Category == EventCategory.Hardware).OrderByDescending(s => s.Count))
            {
                ErrorSources.Add(new ErrorSourceVm(src, baselines));
                total += src.Count;
                stability += src.Count;
            }
            foreach (var src in state.Errors.Where(s => s.Category != EventCategory.Hardware).OrderByDescending(s => s.Count))
            {
                ErrorSources.Add(new ErrorSourceVm(src, baselines));
                total += src.Count;
                system += src.Count;
            }
            TotalErrorCount = total;
            StabilityErrorCount = stability;
            SystemEventCount = system;
        });

        // Status header — color and text driven by stability count only.
        // StatusDetail shows the secondary count info next to the main status.
        if (!state.Ready)
        {
            StatusText = "INITIALIZING";
            StatusDetail = "";
            StatusColor = "Gray";
        }
        else if (StabilityErrorCount == 0 && SystemEventCount == 0)
        {
            StatusText = "CLEAN";
            StatusDetail = "";
            StatusColor = "Green";
        }
        else if (StabilityErrorCount == 0)
        {
            StatusText = "CLEAN";
            StatusDetail = $" — {SystemEventCount} system event{(SystemEventCount != 1 ? "s" : "")} since boot";
            StatusColor = "Green";
        }
        else
        {
            StatusText = $"{StabilityErrorCount} STABILITY ERROR{(StabilityErrorCount != 1 ? "S" : "")}";
            StatusDetail = SystemEventCount > 0
                ? $" + {SystemEventCount} system event{(SystemEventCount != 1 ? "s" : "")}"
                : "";
            StatusColor = "Red";
        }

        BootTimeText = $"Boot:{state.BootTime.ToLocalTime():MM/dd HH:mm}";
        // System uptime from BootTime — the service uptime field tracks how long
        // the service process has been running, which is not what users care about here.
        _bootTimeUtc = state.BootTime;
        var stamp = state.Timestamp == default ? DateTime.UtcNow : state.Timestamp;
        LastUpdateText = $"Updated:{stamp.ToLocalTime():HH:mm:ss}";
        TickClock();
        DriverStatus = state.DriverStatus;

        // Null wire value means an older service that doesn't track cold-boot;
        // treat those as ungated so the banner stays hidden for legacy
        // deployments.
        ColdBootComplete = state.ColdBootComplete ?? true;

        // Board/CPU/BIOS line for the status header. Empty strings are
        // skipped so the "  |  " separators don't leave dangling bars.
        if (state.Timings is { } st)
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(st.CpuCodename))   parts.Add(st.CpuCodename);
            if (!string.IsNullOrWhiteSpace(st.BiosVersion))   parts.Add($"BIOS {st.BiosVersion}");
            if (!string.IsNullOrWhiteSpace(st.AgesaVersion))  parts.Add($"AGESA {st.AgesaVersion}");
            SystemInfoText = string.Join("  |  ", parts);
        }

        // Resolved board vendor — set before the Timings call below so the
        // Settings tab can also display it.
        DetectedBiosVendor = state.BiosLayoutVendor ?? "";

        // Integrity — human-readable, not raw enum names
        CbsStatus = state.Integrity.CbsCorruptionCount == 0
            ? "Clean" : $"{state.Integrity.CbsCorruptionCount} corruption markers";
        SfcStatus = FormatCheckStatus(state.Integrity.SfcStatus);
        DismStatus = FormatCheckStatus(state.Integrity.DismStatus);

        // Timings — null when driver is unavailable (Phase 1 service will send null).
        // BiosLayoutVendor is the resolved vendor string from the service ("MSI", "ASUS", etc.).
        // Parse it back to the enum; fall back to Default when absent or unrecognised.
        _currentTimings = state.Timings;
        _currentDimms = state.Dimms;
        var vendor = BiosLayouts.ParseSetting(state.BiosLayoutVendor);
        var resolvedVendor = vendor == BoardVendor.Auto ? BoardVendor.Default : vendor;
        // Capture the designation map for use inside the Dispatcher lambda.
        var designationsSnapshot = _currentDesignations;
        Application.Current?.Dispatcher.Invoke(() =>
            Timings.LoadFromSnapshot(state.Timings, resolvedVendor, designationsSnapshot));

        // Thermal/power telemetry — update display properties on each push.
        _currentThermalPower = state.ThermalPower;
        Application.Current?.Dispatcher.Invoke(() =>
            Timings.LoadThermalPower(state.ThermalPower));

        // DIMMs — read once at service startup, never changes at runtime.
        if (!_dimmsLoaded && state.Dimms is { Count: > 0 })
        {
            _dimmsLoaded = true;
            Application.Current?.Dispatcher.Invoke(() =>
                Timings.LoadDimms(state.Dimms));
        }

        // Timeline — interleave config changes, drift events, validation results.
        // The era banner on the Timeline tab reads its state from inside
        // LoadFromState (state.ActiveEra + whether there's a recent unnamed
        // config change).
        Application.Current?.Dispatcher.Invoke(() =>
            Timeline.LoadFromState(state));

        // Snapshots — update dropdown options (preserves user's current selection).
        // RecentValidations provides the data needed to label entries with test results.
        Application.Current?.Dispatcher.Invoke(() =>
            Snapshots.LoadSnapshots(state.Snapshots, state.Timings, state.Lkg, state.RecentValidations));

        // Minimums — per-frequency tightest values for the Minimums tab.
        Application.Current?.Dispatcher.Invoke(() =>
            Minimums.LoadFromState(state.Minimums, state.Timings));

        // Settings — populate the Settings tab from the service's current config.
        // Only on the first state push (initial connect) to avoid overwriting
        // in-progress user edits during periodic refreshes.
        if (state.CurrentSettings is not null && !_settingsLoaded)
        {
            _settingsLoaded = true;
            Application.Current?.Dispatcher.Invoke(() =>
                Settings?.LoadFromSettings(state.CurrentSettings));
        }
    }

    private void ApplyEvent(MonitoredEvent evt)
    {
        // Capture for the per-source detail dialog. Stored before the dispatcher
        // hop so the data is ready even if the UI thread is busy.
        StoreEvent(evt);

        // Update the matching error source count in-place, then recompute severity-tiered status.
        Application.Current?.Dispatcher.Invoke(() =>
        {
            var source = ErrorSources.FirstOrDefault(s => s.Name == evt.Source);
            if (source is not null)
            {
                source.Count++;
                source.LastSeen = evt.Timestamp.ToLocalTime().ToString("HH:mm:ss");
            }

            TotalErrorCount = ErrorSources.Sum(s => s.Count);
            StabilityErrorCount = ErrorSources.Where(s => s.IsStability).Sum(s => s.Count);
            SystemEventCount = ErrorSources.Where(s => !s.IsStability).Sum(s => s.Count);

            if (StabilityErrorCount == 0 && SystemEventCount == 0)
            {
                StatusText = "CLEAN";
                StatusDetail = "";
                StatusColor = "Green";
            }
            else if (StabilityErrorCount == 0)
            {
                StatusText = "CLEAN";
                StatusDetail = $" — {SystemEventCount} system event{(SystemEventCount != 1 ? "s" : "")} since boot";
                StatusColor = "Green";
            }
            else
            {
                StatusText = $"{StabilityErrorCount} STABILITY ERROR{(StabilityErrorCount != 1 ? "S" : "")}";
                StatusDetail = SystemEventCount > 0
                    ? $" + {SystemEventCount} system event{(SystemEventCount != 1 ? "s" : "")}"
                    : "";
                StatusColor = "Red";
            }

            var stamp = evt.Timestamp == default ? DateTime.UtcNow : evt.Timestamp;
            LastUpdateText = $"Updated:{stamp.ToLocalTime():HH:mm:ss}";
            TickClock();
        });

        // Send toast notification if enabled and not rate-limited.
        MaybeSendNotification(evt);
    }

    /// <summary>
    /// Apply a hot-tier thermal update — patches the thermal display without waiting
    /// for a full state push. Fires every 3s when the hot loop is running.
    /// </summary>
    private void ApplyThermalUpdate(ThermalUpdateMessage msg)
    {
        _currentThermalPower = msg.ThermalPower;
        Application.Current?.Dispatcher.Invoke(() =>
        {
            Timings.LoadThermalPower(msg.ThermalPower);
            LastUpdateText = $"Updated:{DateTime.Now:HH:mm:ss}";
            TickClock();
        });
    }

    /// <summary>
    /// Advances the live-tick strings: uptime (from boot anchor) and the
    /// wall-clock. LastUpdateText is NOT recomputed here — it reflects the
    /// time of the last real data push and stays put until the next one.
    /// Called from the MainWindow's DispatcherTimer while visible, and from
    /// each incoming message so the header is accurate the instant a push
    /// lands. UI thread only.
    /// </summary>
    public void TickClock()
    {
        if (_bootTimeUtc != default)
            UptimeText = FormatUptime(DateTime.UtcNow - _bootTimeUtc);

        ClockText = DateTime.Now.ToString("HH:mm:ss");
    }

    /// <summary>
    /// Checks notification settings and the per-source cooldown before sending a toast.
    /// Called from ApplyEvent on the I/O thread — NotificationHelper is thread-safe.
    /// </summary>
    private void MaybeSendNotification(MonitoredEvent evt)
    {
        if (Settings is null) return;
        if (!Settings.EnableToastNotifications) return;

        bool shouldNotify = evt.Source switch
        {
            var s when s.Contains("WHEA", StringComparison.OrdinalIgnoreCase)
                || s.Contains("MCE",  StringComparison.OrdinalIgnoreCase)   => Settings.NotifyOnWhea,
            var s when s.Contains("Bugcheck", StringComparison.OrdinalIgnoreCase)
                || s.Contains("BSOD", StringComparison.OrdinalIgnoreCase)   => Settings.NotifyOnBsod,
            var s when s.Contains("Drift", StringComparison.OrdinalIgnoreCase) => Settings.NotifyOnDrift,
            var s when s.Contains("Code Integrity", StringComparison.OrdinalIgnoreCase)
                || s.Contains("Kernel", StringComparison.OrdinalIgnoreCase) => Settings.NotifyOnCodeIntegrity,
            var s when s.Contains("Crash", StringComparison.OrdinalIgnoreCase)
                || s.Contains("Application Error", StringComparison.OrdinalIgnoreCase) => Settings.NotifyOnAppCrash,
            _ => false
        };

        if (!shouldNotify) return;

        // Per-source cooldown: suppress if another notification for the same source
        // was sent within NotifyCooldownSeconds.
        var cooldown = TimeSpan.FromSeconds(Math.Max(0, Settings.NotifyCooldownSeconds));
        var key = evt.Source;
        var now = DateTime.UtcNow;
        lock (_lastNotified)
        {
            if (_lastNotified.TryGetValue(key, out var last) && (now - last) < cooldown)
                return;
            _lastNotified[key] = now;
        }

        NotificationHelper.SendToast("RAMWatch", $"{evt.Source}: {evt.Summary}");
    }

    private string BuildClipboardExport()
    {
        var lines = new List<string>
        {
            $"RAMWatch — {DateTime.Now:yyyy-MM-dd HH:mm:ss}",
            $"{BootTimeText}  |  {UptimeText}",
            $"Status: {StatusText}",
            ""
        };

        // Stability errors first, then system events — mirrors the table grouping.
        var stabilityRows = ErrorSources.Where(s => s.IsStability && s.Count > 0).ToList();
        var systemRows = ErrorSources.Where(s => !s.IsStability && s.Count > 0).ToList();

        if (stabilityRows.Count > 0)
        {
            lines.Add("  STABILITY");
            foreach (var src in stabilityRows)
                lines.Add($"    {src.Name,-28} {src.Count,5}    {src.LastSeen ?? "-"}");
        }

        if (systemRows.Count > 0)
        {
            lines.Add("  SYSTEM");
            foreach (var src in systemRows)
                lines.Add($"    {src.Name,-28} {src.Count,5}    {src.LastSeen ?? "-"}");
        }

        if (stabilityRows.Count == 0 && systemRows.Count == 0)
            lines.Add("  (no events since boot)");

        lines.Add("");
        lines.Add($"CBS: {CbsStatus}  |  SFC: {SfcStatus}  |  DISM: {DismStatus}");
        lines.Add($"Driver: {DriverStatus}");

        var t = _currentTimings;
        if (t is not null && t.MemClockMhz > 0)
        {
            // System info (populated by SystemInfoReader when available)
            var sysInfo = new List<string>();
            if (!string.IsNullOrWhiteSpace(t.CpuCodename)) sysInfo.Add(t.CpuCodename);
            if (!string.IsNullOrWhiteSpace(t.BiosVersion)) sysInfo.Add(t.BiosVersion);
            if (!string.IsNullOrWhiteSpace(t.AgesaVersion)) sysInfo.Add($"AGESA {t.AgesaVersion}");
            if (sysInfo.Count > 0)
            {
                lines.Add("");
                lines.Add($"SYSTEM: {string.Join(" | ", sysInfo)}");
            }

            // DIMMs
            var dimms = _currentDimms;
            if (dimms is { Count: > 0 })
            {
                var dimmParts = dimms.Select(d =>
                {
                    long gb = d.CapacityBytes / (1024 * 1024 * 1024);
                    string cap = gb > 0 ? $"{gb}GB" : "";
                    string spd = d.SpeedMTs > 0 ? SnapshotDisplayName.DdrLabel((d.SpeedMTs + 1) / 2) : "";
                    return string.Join(" ", new[] { d.Slot, cap, spd, d.Manufacturer.Trim(), d.PartNumber.Trim() }
                        .Where(s => s.Length > 0));
                });
                lines.Add($"DIMMs: {string.Join(", ", dimmParts)}");
            }

            // Clocks
            lines.Add("");
            lines.Add("TIMINGS");
            string syncNote = (t.FclkMhz > 0 && t.UclkMhz > 0 && t.FclkMhz != t.UclkMhz)
                ? " ** ASYNC **" : "";
            double clNs = t.CL * 1000.0 / t.MemClockMhz;
            lines.Add($"  {SnapshotDisplayName.DdrLabel(t.MemClockMhz)} / FCLK {t.FclkMhz} / UCLK {t.UclkMhz}{syncNote}  (CL ~{clNs:F1}ns)");

            // Primary
            lines.Add($"  CL {t.CL}  RCDRD {t.RCDRD}  RCDWR {t.RCDWR}  RP {t.RP}  RAS {t.RAS}  RC {t.RC}  CWL {t.CWL}");

            // tRFC with ns
            static string fmtRfc(int clk, int mclk) =>
                clk > 0 && mclk > 0 ? $"{clk}({clk * 1000.0 / mclk:F0}ns)" : clk.ToString();
            lines.Add($"  RFC {fmtRfc(t.RFC, t.MemClockMhz)}  RFC2 {fmtRfc(t.RFC2, t.MemClockMhz)}  RFC4 {fmtRfc(t.RFC4, t.MemClockMhz)}");

            // Secondary
            lines.Add($"  RRDS {t.RRDS}  RRDL {t.RRDL}  FAW {t.FAW}  WTRS {t.WTRS}  WTRL {t.WTRL}  WR {t.WR}  RTP {t.RTP}");

            // Turn-around
            lines.Add($"  RDRDSCL {t.RDRDSCL}  WRWRSCL {t.WRWRSCL}  RDWR {t.RDWR}  WRRD {t.WRRD}");
            lines.Add($"  RDRDSC {t.RDRDSC}  RDRDSD {t.RDRDSD}  RDRDDD {t.RDRDDD}  WRWRSC {t.WRWRSC}  WRWRSD {t.WRWRSD}  WRWRDD {t.WRWRDD}");

            // Misc + Controller
            lines.Add($"  REFI {t.REFI}  CKE {t.CKE}  STAG {t.STAG}  MOD {t.MOD}  MRD {t.MRD}");
            lines.Add($"  GDM {(t.GDM ? "on" : "off")}  {(t.Cmd2T ? "2T" : "1T")}  PowerDown {(t.PowerDown ? "on" : "off")}  PHYRDL {t.PHYRDL_A}/{t.PHYRDL_B}");

            // Voltages
            var volts = new List<string>();
            if (t.VCore > 0) volts.Add($"VCore {t.VCore:F3}V");
            if (t.VSoc > 0) volts.Add($"VSoC {t.VSoc:F3}V");
            if (t.VDDP > 0) volts.Add($"VDDP {t.VDDP:F3}V");
            if (t.VDimm > 0) volts.Add($"VDIMM {t.VDimm:F3}V");
            if (t.VDDG_IOD > 0) volts.Add($"VDDG_IOD {t.VDDG_IOD:F3}V");
            if (t.VDDG_CCD > 0) volts.Add($"VDDG_CCD {t.VDDG_CCD:F3}V");
            if (t.Vtt > 0) volts.Add($"Vtt {t.Vtt:F3}V");
            if (t.Vpp > 0) volts.Add($"Vpp {t.Vpp:F3}V");
            if (volts.Count > 0) lines.Add($"  {string.Join("  ", volts)}");

            // Signal integrity
            var si = new List<string>();
            if (t.ProcODT > 0) si.Add($"ProcODT {t.ProcODT:F1}Ω");
            if (t.RttNom.Length > 0) si.Add($"RttNom {t.RttNom}");
            if (t.RttWr.Length > 0) si.Add($"RttWr {t.RttWr}");
            if (t.RttPark.Length > 0) si.Add($"RttPark {t.RttPark}");
            if (si.Count > 0) lines.Add($"  {string.Join("  ", si)}");

            var drv = new List<string>();
            if (t.ClkDrvStren > 0) drv.Add($"ClkDrv {t.ClkDrvStren:F1}Ω");
            if (t.AddrCmdDrvStren > 0) drv.Add($"AddrCmd {t.AddrCmdDrvStren:F1}Ω");
            if (t.CsOdtCmdDrvStren > 0) drv.Add($"CsOdt {t.CsOdtCmdDrvStren:F1}Ω");
            if (t.CkeDrvStren > 0) drv.Add($"CkeDrv {t.CkeDrvStren:F1}Ω");
            if (drv.Count > 0) lines.Add($"  {string.Join("  ", drv)}");
        }

        // Thermal/power telemetry
        var tp = _currentThermalPower;
        if (tp is not null && tp.Sources != ThermalDataSource.None)
        {
            var thermal = new List<string>();
            if (tp.CpuTempC > 0) thermal.Add($"Tctl {tp.CpuTempC:F1}°C");
            if (tp.CcdTempsC is { Length: > 0 })
            {
                for (int i = 0; i < tp.CcdTempsC.Length; i++)
                    thermal.Add($"CCD{i} {tp.CcdTempsC[i]:F1}°C");
            }
            if (tp.SocTempC > 0) thermal.Add($"SoC {tp.SocTempC:F1}°C");
            if (thermal.Count > 0)
            {
                lines.Add("");
                lines.Add($"THERMAL  {string.Join("  ", thermal)}");
            }

            var power = new List<string>();
            if (tp.SocketPowerW > 0) power.Add($"Socket {tp.SocketPowerW:F1}W");
            if (tp.CorePowerW > 0) power.Add($"Core {tp.CorePowerW:F1}W");
            if (tp.SocPowerW > 0) power.Add($"SoC {tp.SocPowerW:F1}W");
            if (tp.PptLimitW > 0) power.Add($"PPT {tp.PptActualW:F0}/{tp.PptLimitW:F0}W");
            if (tp.TdcLimitA > 0) power.Add($"TDC {tp.TdcActualA:F0}/{tp.TdcLimitA:F0}A");
            if (tp.EdcLimitA > 0) power.Add($"EDC {tp.EdcActualA:F0}/{tp.EdcLimitA:F0}A");
            if (power.Count > 0) lines.Add($"  {string.Join("  ", power)}");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string FormatUptime(TimeSpan uptime)
    {
        if (uptime.TotalDays >= 1)
            return $"Up:{(int)uptime.TotalDays}d{uptime.Hours}h{uptime.Minutes}m";
        if (uptime.TotalHours >= 1)
            return $"Up:{(int)uptime.TotalHours}h{uptime.Minutes}m";
        return $"Up:{uptime.Minutes}m{uptime.Seconds}s";
    }

    private static string FormatCheckStatus(IntegrityCheckStatus status)
    {
        return status switch
        {
            IntegrityCheckStatus.NotRun => "Not run",
            IntegrityCheckStatus.Running => "Running...",
            IntegrityCheckStatus.Clean => "Clean",
            IntegrityCheckStatus.CorruptionFound => "Corruption found",
            IntegrityCheckStatus.CorruptionRepaired => "Repaired",
            IntegrityCheckStatus.Failed => "Failed",
            IntegrityCheckStatus.Unknown => "Unknown",
            _ => status.ToString()
        };
    }
}

/// <summary>
/// Observable wrapper for ErrorSource — supports in-place count updates
/// from live events without rebuilding the entire collection.
/// Computes CountSeverity from boot baseline when available.
/// </summary>
public partial class ErrorSourceVm : ObservableObject
{
    public string Name { get; }
    public string Category { get; }

    // True when this source is in the Hardware event category (WHEA, MCE, Bugcheck,
    // Unexpected Shutdown, Memory Diagnostics). Used to drive the status header
    // color and the visual group separator in the Monitor tab.
    public bool IsStability { get; }

    // Severity string for SeverityToColorConverter — Hardware sources are Critical
    // (red), all others are Warning (amber).
    public string DefaultSeverity { get; }

    /// <summary>
    /// Baseline-aware severity for the Count column color.
    /// "None" = zero count (dim grey), "Normal" = within baseline (grey/bold),
    /// "Elevated" = above baseline (amber), "High" = well above baseline (red).
    /// Hardware sources with count > 0 are always "High".
    /// </summary>
    public string CountSeverity { get; }

    /// <summary>
    /// Statistical shorthand for the baseline column.
    /// Examples: "~21 ±4", "rare", "always 0", "—" (no data).
    /// </summary>
    public string BaselineText { get; }

    [ObservableProperty]
    private int _count;

    [ObservableProperty]
    private string? _lastSeen;

    public ErrorSourceVm(ErrorSource source, Dictionary<string, BaselineStat>? baselines = null)
    {
        Name = source.Name;
        Category = source.Category.ToString();
        Count = source.Count;
        LastSeen = source.LastSeen?.ToLocalTime().ToString("HH:mm:ss");
        IsStability = source.Category == EventCategory.Hardware;
        DefaultSeverity = IsStability ? "Critical" : "Warning";
        CountSeverity = ComputeCountSeverity(source, baselines);
        BaselineText = FormatBaseline(source, baselines);
    }

    private static string ComputeCountSeverity(ErrorSource source, Dictionary<string, BaselineStat>? baselines)
    {
        if (source.Count == 0)
            return "None";

        // Hardware errors are always alarming when present.
        if (source.Category == EventCategory.Hardware)
            return "High";

        // No baseline data — fall back to amber for any non-zero system source.
        if (baselines is null || !baselines.TryGetValue(source.Name, out var stat))
            return "Elevated";

        double mean = stat.Mean;

        // Baseline thresholds:
        // <= mean * 1.5 → Normal (within 50% of average)
        // <= mean * 2.5 → Elevated (sketchy)
        // > mean * 2.5  → High (very sketchy)
        // Special case: if mean is 0, any count is unusual.
        if (mean < 0.5)
        {
            // Source is normally zero — any count is at least elevated.
            return source.Count <= 2 ? "Elevated" : "High";
        }

        double ratio = source.Count / mean;
        if (ratio <= 1.5)
            return "Normal";
        if (ratio <= 2.5)
            return "Elevated";
        return "High";
    }

    private static string FormatBaseline(ErrorSource source, Dictionary<string, BaselineStat>? baselines)
    {
        if (baselines is null || !baselines.TryGetValue(source.Name, out var stat))
            return "\u2014"; // em dash — no data yet

        // Hardware sources: if always zero, that's the expected state.
        if (source.Category == EventCategory.Hardware)
        {
            return stat.NonZeroBoots == 0 ? "always 0" : $"seen {stat.NonZeroBoots}/{stat.BootCount}";
        }

        // System sources: show statistical shorthand.
        if (stat.Mean < 0.5)
        {
            // Normally zero.
            if (stat.NonZeroBoots == 0)
                return "always 0";
            // Occasionally appears.
            return $"rare ({stat.NonZeroBoots}/{stat.BootCount})";
        }

        // Has meaningful counts — show mean ± stddev.
        int meanRound = (int)Math.Round(stat.Mean);
        int sdRound = Math.Max(1, (int)Math.Round(stat.StdDev));

        if (stat.StdDev < 0.5)
        {
            // Very stable — just show the number.
            return $"~{meanRound}";
        }

        return $"~{meanRound} \u00b1{sdRound}";
    }
}
