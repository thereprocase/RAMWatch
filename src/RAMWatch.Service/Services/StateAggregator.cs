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

    public void MarkReady()
    {
        lock (_lock) { _ready = true; }
    }

    public ServiceState BuildState()
    {
        bool ready;
        lock (_lock) { ready = _ready; }

        var bootTime = GetLastBootTime();

        return new ServiceState
        {
            Timestamp = DateTime.UtcNow,
            BootTime = bootTime,
            Ready = ready,
            DriverStatus = "not_found", // Phase 1: no hardware reads
            ServiceUptime = DateTime.UtcNow - _serviceStartTime,
            Errors = _eventLog.GetErrorSources(),
            Integrity = new IntegrityState(0, IntegrityCheckStatus.NotRun, IntegrityCheckStatus.NotRun)
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
