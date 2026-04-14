using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RAMWatch.Core.Ipc;
using RAMWatch.Core.Models;

namespace RAMWatch.ViewModels;

/// <summary>
/// Main view model. Connects to the service pipe, receives state pushes,
/// exposes all UI-bound properties. Reconnects automatically on disconnect.
/// </summary>
public partial class MainViewModel : ObservableObject
{
    private readonly PipeClient _pipe = new();
    private CancellationTokenSource? _cts;

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

    [ObservableProperty]
    private string _bootTimeText = "Boot: --";

    [ObservableProperty]
    private string _uptimeText = "Up: --";

    [ObservableProperty]
    private string _lastUpdateText = "Updated: --";

    [ObservableProperty]
    private string _driverStatus = "unknown";

    // ── Error sources ────────────────────────────────────────

    public ObservableCollection<ErrorSourceVm> ErrorSources { get; } = [];

    // ── Timings (Phase 2) ────────────────────────────────────

    public TimingsViewModel Timings { get; } = new();

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
        }
    }

    private void ApplyState(ServiceState state)
    {
        IsReady = state.Ready;

        // Error sources
        Application.Current?.Dispatcher.Invoke(() =>
        {
            ErrorSources.Clear();
            int total = 0;
            foreach (var src in state.Errors)
            {
                ErrorSources.Add(new ErrorSourceVm(src));
                total += src.Count;
            }
            TotalErrorCount = total;
        });

        // Status header
        if (!state.Ready)
        {
            StatusText = "INITIALIZING";
            StatusColor = "Gray";
        }
        else if (TotalErrorCount == 0)
        {
            StatusText = "CLEAN";
            StatusColor = "Green";
        }
        else
        {
            StatusText = $"{TotalErrorCount} ERROR{(TotalErrorCount != 1 ? "S" : "")}";
            StatusColor = "Red";
        }

        BootTimeText = $"Boot: {state.BootTime.ToLocalTime():MM/dd HH:mm}";
        UptimeText = FormatUptime(state.ServiceUptime);
        LastUpdateText = $"Updated: {state.Timestamp.ToLocalTime():HH:mm:ss}";
        DriverStatus = state.DriverStatus;

        // Integrity — human-readable, not raw enum names
        CbsStatus = state.Integrity.CbsCorruptionCount == 0
            ? "Clean" : $"{state.Integrity.CbsCorruptionCount} corruption markers";
        SfcStatus = FormatCheckStatus(state.Integrity.SfcStatus);
        DismStatus = FormatCheckStatus(state.Integrity.DismStatus);

        // Timings — null when driver is unavailable (Phase 1 service will send null)
        Application.Current?.Dispatcher.Invoke(() =>
            Timings.LoadFromSnapshot(state.Timings));
    }

    private void ApplyEvent(MonitoredEvent evt)
    {
        // Update the matching error source count in-place
        Application.Current?.Dispatcher.Invoke(() =>
        {
            var source = ErrorSources.FirstOrDefault(s => s.Name == evt.Source);
            if (source is not null)
            {
                source.Count++;
                source.LastSeen = evt.Timestamp.ToLocalTime().ToString("HH:mm:ss");
            }

            TotalErrorCount = ErrorSources.Sum(s => s.Count);
            StatusText = TotalErrorCount == 0 ? "CLEAN" : $"{TotalErrorCount} ERROR{(TotalErrorCount != 1 ? "S" : "")}";
            StatusColor = TotalErrorCount == 0 ? "Green" : "Red";
            LastUpdateText = $"Updated: {DateTime.Now:HH:mm:ss}";
        });
    }

    private string BuildClipboardExport()
    {
        var lines = new List<string>
        {
            $"RAMWatch — {DateTime.Now:yyyy-MM-dd HH:mm:ss}",
            $"{BootTimeText}  |  {UptimeText}",
            $"Status: {StatusText} — {TotalErrorCount} errors since boot",
            ""
        };

        foreach (var src in ErrorSources)
        {
            lines.Add($"  {src.Name,-30} {src.Count,5}    {src.LastSeen ?? "-"}");
        }

        lines.Add("");
        lines.Add($"CBS: {CbsStatus}  |  SFC: {SfcStatus}  |  DISM: {DismStatus}");
        lines.Add($"Driver: {DriverStatus}");

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
/// </summary>
public partial class ErrorSourceVm : ObservableObject
{
    public string Name { get; }
    public string Category { get; }

    // Severity string for SeverityToColorConverter — derived from source name at
    // construction time. Hardware error sources are always Critical; others Warning.
    public string DefaultSeverity { get; }

    [ObservableProperty]
    private int _count;

    [ObservableProperty]
    private string? _lastSeen;

    public ErrorSourceVm(ErrorSource source)
    {
        Name = source.Name;
        Category = source.Category.ToString();
        Count = source.Count;
        LastSeen = source.LastSeen?.ToLocalTime().ToString("HH:mm:ss");
        DefaultSeverity = MapSeverity(source.Name);
    }

    // Map well-known hardware error source names to severity levels understood
    // by SeverityToColorConverter. Unrecognized names default to Warning so they
    // still get amber rather than invisible gray.
    private static string MapSeverity(string name) => name switch
    {
        "WHEA-Logger" or "MCE" or "Bugcheck" => "Critical",
        "System" or "Application" => "Warning",
        _ => "Warning"
    };
}
