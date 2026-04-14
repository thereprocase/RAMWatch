using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RAMWatch.Core.Ipc;
using RAMWatch.Service.Services;

namespace RAMWatch.Service;

/// <summary>
/// Main service entry point. Manages the pipe server and monitoring lifecycle.
/// Phase 1: pipe server + settings. Event monitors added in later commits.
/// </summary>
public sealed class RamWatchService : BackgroundService
{
    private readonly SettingsManager _settings;
    private readonly ILogger<RamWatchService> _logger;
    private PipeServer? _pipeServer;

    public RamWatchService(SettingsManager settings, ILogger<RamWatchService> logger)
    {
        _settings = settings;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _settings.Load();
        _logger.LogInformation("RAMWatch service starting. Data directory: {Path}", DataDirectory.BasePath);

        _pipeServer = new PipeServer(OnClientMessage);
        _pipeServer.Start();
        _logger.LogInformation("Pipe server started on \\\\.\\pipe\\{PipeName}", PipeConstants.PipeName);

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("RAMWatch service stopping");
        if (_pipeServer is not null)
            await _pipeServer.DisposeAsync();
        await base.StopAsync(cancellationToken);
    }

    private Task OnClientMessage(string line, ConnectedClient client)
    {
        var message = MessageSerializer.Deserialize(line);
        if (message is null)
            return Task.CompletedTask;

        // Phase 1: handle getState and updateSettings.
        // Additional message handlers added in later commits.
        _logger.LogDebug("Received message type: {Type}", message.Type);
        return Task.CompletedTask;
    }
}
