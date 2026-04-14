using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RAMWatch.Core.Models;

namespace RAMWatch.ViewModels;

/// <summary>
/// Backing view model for SettingsTab. All properties shadow AppSettings fields.
/// Changes are staged here until SaveCommand is invoked; no live-apply.
/// </summary>
public partial class SettingsViewModel : ObservableObject
{
    private readonly MainViewModel _main;

    public SettingsViewModel(MainViewModel main)
    {
        _main = main;
    }

    // ── General ──────────────────────────────────────────────

    [ObservableProperty]
    private bool _startMinimized;

    [ObservableProperty]
    private bool _minimizeToTray = true;

    [ObservableProperty]
    private bool _alwaysOnTop;

    [ObservableProperty]
    private bool _launchAtLogon;

    // ── Monitoring ───────────────────────────────────────────

    [ObservableProperty]
    private int _refreshIntervalSeconds = 60;

    // ── Logging ──────────────────────────────────────────────

    [ObservableProperty]
    private bool _enableCsvLogging = true;

    [ObservableProperty]
    private int _logRetentionDays = 90;

    [ObservableProperty]
    private int _maxLogSizeMb = 100;

    [ObservableProperty]
    private string _mirrorDirectory = "";

    // ── Notifications ────────────────────────────────────────

    [ObservableProperty]
    private bool _enableToastNotifications = true;

    [ObservableProperty]
    private bool _notifyOnWhea = true;

    [ObservableProperty]
    private bool _notifyOnBsod = true;

    [ObservableProperty]
    private bool _notifyOnDrift = true;

    [ObservableProperty]
    private bool _notifyOnCodeIntegrity;

    [ObservableProperty]
    private bool _notifyOnAppCrash;

    [ObservableProperty]
    private int _notifyCooldownSeconds = 300;

    // ── Display ──────────────────────────────────────────────

    // Theme is fixed to "dark" in Phase 1 — surfaced read-only for future phases.
    public string Theme { get; private set; } = "dark";

    // ── Save state ───────────────────────────────────────────

    [ObservableProperty]
    private string _saveStatus = "";

    // ── Public API ───────────────────────────────────────────

    /// <summary>
    /// Populates all properties from a received AppSettings. Does not trigger
    /// a save — the user must click Save to push changes back to the service.
    /// </summary>
    public void LoadFromSettings(AppSettings settings)
    {
        StartMinimized           = settings.StartMinimized;
        MinimizeToTray           = settings.MinimizeToTray;
        AlwaysOnTop              = settings.AlwaysOnTop;
        LaunchAtLogon            = settings.LaunchAtLogon;
        RefreshIntervalSeconds   = settings.RefreshIntervalSeconds;
        EnableCsvLogging         = settings.EnableCsvLogging;
        LogRetentionDays         = settings.LogRetentionDays;
        MaxLogSizeMb             = settings.MaxLogSizeMb;
        MirrorDirectory          = settings.MirrorDirectory;
        EnableToastNotifications = settings.EnableToastNotifications;
        NotifyOnWhea             = settings.NotifyOnWhea;
        NotifyOnBsod             = settings.NotifyOnBsod;
        NotifyOnDrift            = settings.NotifyOnDrift;
        NotifyOnCodeIntegrity    = settings.NotifyOnCodeIntegrity;
        NotifyOnAppCrash         = settings.NotifyOnAppCrash;
        NotifyCooldownSeconds    = settings.NotifyCooldownSeconds;
        Theme                    = settings.Theme;
        SaveStatus               = "";
    }

    /// <summary>
    /// Builds an AppSettings from current ViewModel values. SchemaVersion is
    /// preserved as CurrentProtocolVersion — the service owns the canonical copy.
    /// </summary>
    public AppSettings ToSettings() => new()
    {
        StartMinimized           = StartMinimized,
        MinimizeToTray           = MinimizeToTray,
        AlwaysOnTop              = AlwaysOnTop,
        LaunchAtLogon            = LaunchAtLogon,
        RefreshIntervalSeconds   = RefreshIntervalSeconds,
        EnableCsvLogging         = EnableCsvLogging,
        LogRetentionDays         = LogRetentionDays,
        MaxLogSizeMb             = MaxLogSizeMb,
        MirrorDirectory          = MirrorDirectory,
        EnableToastNotifications = EnableToastNotifications,
        NotifyOnWhea             = NotifyOnWhea,
        NotifyOnBsod             = NotifyOnBsod,
        NotifyOnDrift            = NotifyOnDrift,
        NotifyOnCodeIntegrity    = NotifyOnCodeIntegrity,
        NotifyOnAppCrash         = NotifyOnAppCrash,
        NotifyCooldownSeconds    = NotifyCooldownSeconds,
        Theme                    = Theme,
    };

    // ── Commands ─────────────────────────────────────────────

    [RelayCommand]
    private async Task SaveAsync()
    {
        SaveStatus = "Saving...";
        await _main.SendUpdateSettingsAsync(ToSettings());
        SaveStatus = $"Saved {DateTime.Now:HH:mm:ss}";
    }
}
