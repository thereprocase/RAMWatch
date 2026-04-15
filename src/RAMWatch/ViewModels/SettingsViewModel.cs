using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RAMWatch.Core.Models;

namespace RAMWatch.ViewModels;

/// <summary>
/// A single row in the TIMING DESIGNATIONS editor.
/// Wraps one timing field name and its current designation selection.
/// </summary>
public sealed partial class DesignationItem : ObservableObject
{
    public string TimingName { get; }

    [ObservableProperty]
    private string _designation;

    /// <summary>
    /// Callback invoked when the Designation property changes.
    /// Wired up by SettingsViewModel.LoadDesignations to trigger a save.
    /// </summary>
    internal Action? DesignationChangedCallback;

    public DesignationItem(string timingName, string designation)
    {
        TimingName   = timingName;
        _designation = designation;
    }

    partial void OnDesignationChanged(string value)
    {
        DesignationChangedCallback?.Invoke();
    }
}

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

    // ── BIOS layout ───────────────────────────────────────────

    // Allowed values: "Auto", "MSI", "ASUS", "Gigabyte", "ASRock", "Default"
    [ObservableProperty]
    private string _biosLayout = "Auto";

    // Display-only: "Auto (detected: MSI)" when Auto and a vendor was detected,
    // else just the raw BiosLayout value. Set by MainViewModel after each state push.
    [ObservableProperty]
    private string _biosLayoutDetectedLabel = "";

    // ── Timing Designations ──────────────────────────────────

    // All known timing field names in alphabetical order.
    // These are the fields surfaced in the TIMING DESIGNATIONS section.
    private static readonly string[] AllTimingFields =
    [
        "CKE", "CL", "Cmd2T", "CWL",
        "FAW", "GDM",
        "MOD", "MRD",
        "PHYRDL_A", "PHYRDL_B", "PowerDown",
        "RAS", "RC", "RCDRD", "RCDWR", "RDRDDD", "RDRDSC", "RDRDSCL", "RDRDSD", "RDWR", "REFI",
        "RFC", "RFC2", "RFC4", "RP", "RRDS", "RRDL", "RTP",
        "STAG",
        "WTRL", "WTRS", "WR", "WRRD", "WRWRDD", "WRWRSC", "WRWRSCL", "WRWRSD"
    ];

    /// <summary>Valid designation values shown in each row's ComboBox.</summary>
    public static readonly IReadOnlyList<string> DesignationChoices = ["Manual", "Auto", "Unknown"];

    /// <summary>
    /// Live collection of designation rows. Populated by LoadDesignations
    /// when the service responds to GetDesignationsMessage.
    /// Each item's Designation property drives the row's ComboBox.
    /// </summary>
    public ObservableCollection<DesignationItem> Designations { get; } = [];

    [ObservableProperty]
    private string _designationSaveStatus = "";

    // Guards against re-entrant saves while a save is already in flight.
    private bool _designationSaving;

    /// <summary>
    /// Called by MainViewModel when a DesignationsResponseMessage arrives.
    /// Rebuilds the Designations collection from the wire-format map.
    /// Previously-unknown fields default to "Unknown".
    /// </summary>
    public void LoadDesignations(IReadOnlyDictionary<string, string> map)
    {
        // Suspend change callbacks while rebuilding to avoid a save storm.
        _designationSaving = true;
        try
        {
            Designations.Clear();
            foreach (var field in AllTimingFields)
            {
                string desig = map.TryGetValue(field, out var v)
                    ? NormaliseDesignation(v)
                    : "Unknown";
                var item = new DesignationItem(field, desig);
                item.DesignationChangedCallback = OnAnyDesignationChanged;
                Designations.Add(item);
            }
        }
        finally
        {
            _designationSaving = false;
        }
    }

    /// <summary>
    /// Builds the wire-format designation map from the current collection state.
    /// </summary>
    public Dictionary<string, string> BuildDesignationMap() =>
        Designations.ToDictionary(d => d.TimingName, d => d.Designation);

    /// <summary>
    /// Called whenever any row's Designation changes.
    /// Sends the full map to the service and shows a brief "Saved" label.
    /// </summary>
    private void OnAnyDesignationChanged()
    {
        if (_designationSaving) return;
        _ = SaveDesignationsAsync();
    }

    [RelayCommand]
    private async Task SaveDesignationsAsync()
    {
        DesignationSaveStatus = "Saving...";
        await _main.SendUpdateDesignationsAsync(BuildDesignationMap());
        DesignationSaveStatus = $"Saved {DateTime.Now:HH:mm:ss}";

        // Clear the status after a few seconds so it does not linger.
        await Task.Delay(3000);
        if (DesignationSaveStatus.StartsWith("Saved "))
            DesignationSaveStatus = "";
    }

    private static string NormaliseDesignation(string raw) => raw switch
    {
        "Manual"  => "Manual",
        "Auto"    => "Auto",
        "Unknown" => "Unknown",
        _         => "Unknown"
    };

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
        BiosLayout               = string.IsNullOrWhiteSpace(settings.BiosLayout) ? "Auto" : settings.BiosLayout;
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
        BiosLayout               = BiosLayout,
    };

    // ── Commands ─────────────────────────────────────────────

    [RelayCommand]
    private async Task SaveAsync()
    {
        SaveStatus = "Saving...";
        await _main.SendUpdateSettingsAsync(ToSettings());
        ApplyLaunchAtLogon(LaunchAtLogon);
        SaveStatus = $"Saved {DateTime.Now:HH:mm:ss}";
    }

    /// <summary>
    /// Write or remove the HKCU Run entry that launches RAMWatch at logon.
    /// This is a user-level registry key (no admin required).
    /// </summary>
    private static void ApplyLaunchAtLogon(bool enable)
    {
        const string keyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string valueName = "RAMWatch";

        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(keyPath, writable: true);
            if (key is null) return;

            if (enable)
            {
                // Find the running exe path. Use the installed location if available,
                // fall back to the current process path.
                var exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
                if (!string.IsNullOrEmpty(exePath))
                    key.SetValue(valueName, $"\"{exePath}\" --minimized");
            }
            else
            {
                key.DeleteValue(valueName, throwOnMissingValue: false);
            }
        }
        catch
        {
            // Non-fatal — registry write can fail under unusual security policies.
        }
    }
}
