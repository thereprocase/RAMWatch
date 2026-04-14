using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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

    // ── Connection state ─────────────────────────────────────

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
    private string _bootTimeText = "Boot: --";

    [ObservableProperty]
    private string _uptimeText = "Up: --";

    [ObservableProperty]
    private string _lastUpdateText = "Updated: --";

    [ObservableProperty]
    private string _driverStatus = "unknown";

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

    // ── Timeline + Snapshots (Phase 3) ──────────────────────

    public TimelineViewModel Timeline { get; } = new();
    public SnapshotsViewModel Snapshots { get; } = new();

    public MainViewModel()
    {
        // Wire the IPC delete callbacks into the Timeline so confirmed deletes reach
        // the service rather than only removing the row from the local collection.
        Timeline.SetDeleteValidationHandler(SendDeleteValidationAsync);
        Timeline.SetDeleteChangeHandler(SendDeleteChangeAsync);

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

        _pendingDigestRequestId = requestId;
        await _pipe.SendAsync(MessageSerializer.Serialize(msg));

        // Wait up to 5s for the response. If it doesn't arrive, fall back to
        // the local export so the tray action never silently does nothing.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
            if (_lastDigestText is not null)
            {
                Clipboard.SetText(_lastDigestText);
                _lastDigestText = null;
                _pendingDigestRequestId = null;
                return;
            }
        }

        // Timeout — fall back to local export.
        _pendingDigestRequestId = null;
        Clipboard.SetText(BuildClipboardExport());
    }

    // ── Digest state — set by ProcessMessage when a DigestResponseMessage arrives.
    private string? _pendingDigestRequestId;
    private volatile string? _lastDigestText;

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
            Application.Current?.Dispatcher.Invoke(() => ValidationConfirmation = "");
        });
    }

    [ObservableProperty]
    private string _validationConfirmation = "";

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

    private async Task ConnectAndListenAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            ConnectionStatus = "Connecting to service...";
            IsConnected = false;

            await _pipe.ConnectWithRetryAsync(ct);
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
            case DigestResponseMessage digest:
                // CopyDigestAsync polls _lastDigestText; set it here on the I/O thread.
                if (_pendingDigestRequestId is not null &&
                    digest.RequestId == _pendingDigestRequestId &&
                    !string.IsNullOrWhiteSpace(digest.DigestText))
                {
                    _lastDigestText = digest.DigestText;
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

    private void ApplyState(ServiceState state)
    {
        IsReady = state.Ready;

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

        BootTimeText = $"Boot: {state.BootTime.ToLocalTime():MM/dd HH:mm}";
        // System uptime from BootTime — the service uptime field tracks how long
        // the service process has been running, which is not what users care about here.
        var systemUptime = DateTime.UtcNow - state.BootTime;
        UptimeText = FormatUptime(systemUptime);
        LastUpdateText = $"Updated: {state.Timestamp.ToLocalTime():HH:mm:ss}";
        DriverStatus = state.DriverStatus;

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
        var vendor = BiosLayouts.ParseSetting(state.BiosLayoutVendor);
        var resolvedVendor = vendor == BoardVendor.Auto ? BoardVendor.Default : vendor;
        // Capture the designation map for use inside the Dispatcher lambda.
        var designationsSnapshot = _currentDesignations;
        Application.Current?.Dispatcher.Invoke(() =>
            Timings.LoadFromSnapshot(state.Timings, resolvedVendor, designationsSnapshot));

        // Timeline — interleave config changes, drift events, validation results
        Application.Current?.Dispatcher.Invoke(() =>
            Timeline.LoadFromState(state));

        // Snapshots — update dropdown options (preserves user's current selection).
        // RecentValidations provides the data needed to label entries with test results.
        Application.Current?.Dispatcher.Invoke(() =>
            Snapshots.LoadSnapshots(state.Snapshots, state.Timings, state.Lkg, state.RecentValidations));
    }

    private void ApplyEvent(MonitoredEvent evt)
    {
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

            LastUpdateText = $"Updated: {DateTime.Now:HH:mm:ss}";
        });

        // Send toast notification if enabled and not rate-limited.
        MaybeSendNotification(evt);
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

        // Primary timings — only included when available (Phase 2+ service).
        var t = _currentTimings;
        if (t is not null && t.MemClockMhz > 0)
        {
            lines.Add("");
            lines.Add("TIMINGS");
            lines.Add($"  DDR{t.MemClockMhz * 2} / FCLK {t.FclkMhz} / UCLK {t.UclkMhz}");
            lines.Add($"  CL-RCDRD-RP-RAS: {t.CL}-{t.RCDRD}-{t.RP}-{t.RAS}");
            lines.Add($"  CWL {t.CWL}  RFC {t.RFC}  REFI {t.REFI}");
            lines.Add($"  GDM {(t.GDM ? "on" : "off")}  {(t.Cmd2T ? "2T" : "1T")}");
            if (t.VDimm > 0) lines.Add($"  VDIMM {t.VDimm:F3}V  VSOC {t.VSoc:F3}V");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string FormatUptime(TimeSpan uptime)
    {
        if (uptime.TotalDays >= 1)
            return $"Up: {(int)uptime.TotalDays}d {uptime.Hours}h {uptime.Minutes}m";
        if (uptime.TotalHours >= 1)
            return $"Up: {(int)uptime.TotalHours}h {uptime.Minutes}m";
        return $"Up: {uptime.Minutes}m {uptime.Seconds}s";
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

    [ObservableProperty]
    private int _count;

    [ObservableProperty]
    private string? _lastSeen;

    public ErrorSourceVm(ErrorSource source, Dictionary<string, double>? baselines = null)
    {
        Name = source.Name;
        Category = source.Category.ToString();
        Count = source.Count;
        LastSeen = source.LastSeen?.ToLocalTime().ToString("HH:mm:ss");
        IsStability = source.Category == EventCategory.Hardware;
        DefaultSeverity = IsStability ? "Critical" : "Warning";
        CountSeverity = ComputeCountSeverity(source, baselines);
    }

    private static string ComputeCountSeverity(ErrorSource source, Dictionary<string, double>? baselines)
    {
        if (source.Count == 0)
            return "None";

        // Hardware errors are always alarming when present.
        if (source.Category == EventCategory.Hardware)
            return "High";

        // No baseline data — fall back to amber for any non-zero system source.
        if (baselines is null || !baselines.TryGetValue(source.Name, out double mean))
            return "Elevated";

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
}
