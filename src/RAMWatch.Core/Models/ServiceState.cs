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
    // Despite the name, this field carries system uptime (time since last OS boot),
    // not service process uptime. Renamed intent is system uptime; name kept for wire
    // compatibility with the GUI client.
    public required TimeSpan ServiceUptime { get; init; }
    public required List<ErrorSource> Errors { get; init; }
    public required IntegrityState Integrity { get; init; }

    // Phase 2 — null when hardware driver is unavailable
    public TimingSnapshot? Timings { get; init; }

    // Resolved board vendor (never "Auto" — service resolves Auto at startup).
    // Null when the registry key is inaccessible (treated as Default by the GUI).
    public string? BiosLayoutVendor { get; init; }

    // Phase 3 — null when no journal data exists yet
    /// <summary>The five most recent config changes across boots.</summary>
    public List<ConfigChange>? RecentChanges { get; init; }
    /// <summary>Drift events detected during the current boot.</summary>
    public List<DriftEvent>? DriftEvents { get; init; }
    /// <summary>The five most recent validation test results.</summary>
    public List<ValidationResult>? RecentValidations { get; init; }
    /// <summary>The last-known-good timing snapshot, or null if none qualifies.</summary>
    public TimingSnapshot? Lkg { get; init; }
    /// <summary>All saved snapshots for the snapshot comparison tab.</summary>
    public List<TimingSnapshot>? Snapshots { get; init; }

    /// <summary>
    /// Current settings so the GUI can populate the Settings tab on connect.
    /// </summary>
    public AppSettings? CurrentSettings { get; init; }

    /// <summary>
    /// Per-source baseline statistics from past boots (IQR-filtered).
    /// Null when fewer than 3 boots of history exist.
    /// Used by the GUI to color-code event counts and show "normal" column.
    /// </summary>
    public Dictionary<string, BaselineStat>? SourceBaselines { get; init; }
}
