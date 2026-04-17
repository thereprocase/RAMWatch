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
/// Auto-saves to the service on every property change with a 500ms debounce.
/// </summary>
public partial class SettingsViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private bool _suppressAutoSave;
    private CancellationTokenSource? _debounceCts;

    public SettingsViewModel(MainViewModel main)
    {
        _main = main;
    }

    /// <summary>
    /// Auto-save on any property change (debounced 500ms).
    /// Skipped during LoadFromSettings to avoid echo-saving.
    /// </summary>
    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);

        // Skip non-setting properties and internal state.
        if (_suppressAutoSave || e.PropertyName is "SaveStatus" or "BiosLayoutDetectedLabel"
            or "DesignationSaveStatus")
            return;

        ScheduleAutoSave();
    }

    private void ScheduleAutoSave()
    {
        // Cancel the previous debounce; defer its Dispose to the worker's
        // catch block. Disposing here while the captured token is inside
        // Task.Delay raises ObjectDisposedException rather than the
        // OperationCanceledException the worker expects — that ODE would
        // fault the unobserved task and, depending on runtime config,
        // propagate as UnobservedTaskException. Swap the CTS reference
        // atomically and let the worker clean up its own.
        var previous = _debounceCts;
        _debounceCts = new CancellationTokenSource();
        previous?.Cancel();
        var token = _debounceCts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(500, token);
                if (!token.IsCancellationRequested)
                {
                    await System.Windows.Application.Current.Dispatcher.InvokeAsync(
                        async () => await AutoSaveAsync());
                }
            }
            catch (OperationCanceledException) { /* expected on re-trigger */ }
            catch (ObjectDisposedException) { /* previous CTS disposed mid-delay */ }
            finally
            {
                try { previous?.Dispose(); } catch { }
            }
        }, token);
    }

    private async Task AutoSaveAsync()
    {
        await _main.SendUpdateSettingsAsync(ToSettings());
        bool autoStartOk = ApplyLaunchAtLogon(LaunchAtLogon);
        SaveStatus = autoStartOk
            ? $"Saved {DateTime.Now:HH:mm:ss}"
            : $"Saved {DateTime.Now:HH:mm:ss} (autostart: registry write failed)";
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
    private void SetAllManual()
    {
        _designationSaving = true;
        foreach (var item in Designations)
            item.Designation = "Manual";
        _designationSaving = false;
        _ = SaveDesignationsAsync();
    }

    [RelayCommand]
    private void SetAllAuto()
    {
        _designationSaving = true;
        foreach (var item in Designations)
            item.Designation = "Auto";
        _designationSaving = false;
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

    // Preserves the last AppSettings received from the service so fields the
    // GUI doesn't surface (SchemaVersion, LogDirectory, DebugLogging, git
    // integration fields) round-trip unchanged through ToSettings instead
    // of being reset to their type defaults — the service-side ApplyPatch
    // merges by JSON field presence, and a default-valued field is still
    // present in the payload, so those omissions silently wiped settings.
    private AppSettings _lastLoadedSettings = new();

    // ── Public API ───────────────────────────────────────────

    /// <summary>
    /// Populates all properties from a received AppSettings.
    /// Auto-save is suppressed during this call to avoid echo-saving.
    /// </summary>
    public void LoadFromSettings(AppSettings settings)
    {
        _lastLoadedSettings = settings;
        _suppressAutoSave = true;
        try
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
        finally { _suppressAutoSave = false; }
    }

    /// <summary>
    /// Builds an AppSettings from current ViewModel values. Starts from the
    /// last payload received from the service so fields this GUI doesn't
    /// surface (LogDirectory, DebugLogging, git integration fields,
    /// SchemaVersion) survive the round-trip. The service's ApplyPatch
    /// merges by JSON field presence and System.Text.Json emits all
    /// properties including defaults — a `new()` here would have wiped
    /// every unsurfaced field on every auto-save.
    /// </summary>
    public AppSettings ToSettings()
    {
        var src = _lastLoadedSettings;
        return new AppSettings
        {
            // Preserved verbatim from the last service payload.
            SchemaVersion            = src.SchemaVersion,
            LogDirectory             = src.LogDirectory,
            DebugLogging             = src.DebugLogging,
            EnableGitIntegration     = src.EnableGitIntegration,
            EnableGitPush            = src.EnableGitPush,
            GitRemoteRepo            = src.GitRemoteRepo,
            GitUserDisplayName       = src.GitUserDisplayName,

            // GUI-managed fields.
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
    }

    // ── Commands ─────────────────────────────────────────────

    /// <summary>
    /// Write or remove the HKCU Run entry that launches RAMWatch at logon.
    /// This is a user-level registry key (no admin required).
    /// </summary>
    /// <summary>
    /// Returns false if the registry write failed (e.g., group policy blocking HKCU\Run).
    /// </summary>
    private static bool ApplyLaunchAtLogon(bool enable)
    {
        const string keyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string valueName = "RAMWatch";

        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(keyPath, writable: true);
            if (key is null) return false;

            if (enable)
            {
                // Environment.ProcessPath is reliable for single-file deployments.
                // Process.MainModule?.FileName can return null for self-contained apps.
                var exePath = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exePath)) return false;
                key.SetValue(valueName, $"\"{exePath}\" --minimized");
            }
            else
            {
                key.DeleteValue(valueName, throwOnMissingValue: false);
            }
            return true;
        }
        catch
        {
            return false;
        }
    }
}
