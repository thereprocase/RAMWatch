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
    string? RawXml = null,
    McaDetails? Mca = null,
    ThermalPowerSnapshot? Vitals = null
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

/// <summary>
/// Baseline statistics for a single error source across historical boots.
/// IQR-filtered mean and standard deviation, plus how many boots contributed.
/// </summary>
public sealed class BaselineStat
{
    public required double Mean { get; init; }
    public required double StdDev { get; init; }
    public required int BootCount { get; init; }
    /// <summary>How many boots had count > 0 for this source.</summary>
    public required int NonZeroBoots { get; init; }
}
