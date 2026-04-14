namespace RAMWatch.Core.Models;

/// <summary>
/// Full state pushed from service to GUI on connect and periodically.
/// Phase 1: events + integrity only. Timing fields added in Phase 2.
/// </summary>
public sealed class ServiceState
{
    public required DateTime Timestamp { get; init; }
    public required DateTime BootTime { get; init; }
    public required bool Ready { get; init; }
    public required string DriverStatus { get; init; }
    public required TimeSpan ServiceUptime { get; init; }
    public required List<ErrorSource> Errors { get; init; }
    public required IntegrityState Integrity { get; init; }

    // Phase 2 — null when hardware driver is unavailable
    public TimingSnapshot? Timings { get; init; }
}
