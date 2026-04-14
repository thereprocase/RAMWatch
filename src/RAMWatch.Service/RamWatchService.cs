using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RAMWatch.Core.Ipc;
using RAMWatch.Core.Models;
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

        // State aggregator
        _aggregator = new StateAggregator(_eventLog, _settings, _pipeServer);

        // Historical scan (blocks briefly, populates error counts from boot)
        _eventLog.Start();
        _integrity.ScanCbsLog();
        _aggregator.MarkReady();
        _logger.LogInformation("Monitoring active. Boot ID: {BootId}", _bootId);

        // Periodic refresh loop
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
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
        _mirrorLogger = null;
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

            default:
                _logger.LogDebug("Unhandled message type: {Type}", message.Type);
                break;
        }
    }
}
