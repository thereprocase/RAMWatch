namespace RAMWatch.Core.Models;

public enum EventSeverity
{
    Info,
    Notice,
    Warning,
    Error,
    Critical
}

public enum EventCategory
{
    Hardware,
    Filesystem,
    Integrity,
    Application
}

public sealed record MonitoredEvent(
    DateTime Timestamp,
    string Source,
    EventCategory Category,
    int EventId,
    EventSeverity Severity,
    string Summary,
    string? RawXml = null
);

public sealed record ErrorSource(
    string Name,
    EventCategory Category,
    int Count,
    DateTime? LastSeen
);

/// <summary>
/// Per-source event counts for a single boot. Stored in boot_baselines.json.
/// </summary>
public sealed class BootCountEntry
{
    public required string BootId { get; init; }
    public required DateTime Timestamp { get; init; }
    public required Dictionary<string, int> Counts { get; init; }
}
