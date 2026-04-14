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
