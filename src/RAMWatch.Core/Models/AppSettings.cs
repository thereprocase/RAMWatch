namespace RAMWatch.Core.Models;

/// <summary>
/// Phase 1 settings subset. Additional fields added in later phases.
/// </summary>
public sealed class AppSettings
{
    public int SchemaVersion { get; set; } = 1;

    // General
    public bool StartMinimized { get; set; }
    public bool MinimizeToTray { get; set; } = true;
    public bool AlwaysOnTop { get; set; }
    public bool LaunchAtLogon { get; set; }

    // Monitoring
    public int RefreshIntervalSeconds { get; set; } = 60;

    // Logging
    public bool EnableCsvLogging { get; set; } = true;
    public string LogDirectory { get; set; } = "";
    public int LogRetentionDays { get; set; } = 90;
    public int MaxLogSizeMb { get; set; } = 100;
    public string MirrorDirectory { get; set; } = "";

    // Notifications
    public bool EnableToastNotifications { get; set; } = true;
    public bool NotifyOnWhea { get; set; } = true;
    public bool NotifyOnBsod { get; set; } = true;
    public bool NotifyOnDrift { get; set; } = true;
    public bool NotifyOnCodeIntegrity { get; set; }
    public bool NotifyOnAppCrash { get; set; }
    public int NotifyCooldownSeconds { get; set; } = 300;

    // Display
    public string Theme { get; set; } = "dark";

    // Advanced
    public bool DebugLogging { get; set; }
}
