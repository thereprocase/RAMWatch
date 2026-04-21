using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using RAMWatch.Core.Models;
using RAMWatch.ViewModels;
using RAMWatch.Views;

namespace RAMWatch;

public partial class MainWindow : System.Windows.Window
{
    private readonly MainViewModel _viewModel = new();
    private readonly SettingsViewModel _settingsVm;
    private TrayIconManager? _tray;
    private bool _minimizeToTray = true;
    private bool _quitting;

    // Live-tick clock for the status header. Hooked into
    // CompositionTarget.Rendering (the WPF compositor loop) instead of a
    // DispatcherTimer, because the dispatcher queue is hammered by
    // synchronous Dispatcher.Invoke calls from the pipe reader during
    // every state/event/thermal push. A Background-priority timer gets
    // starved by those bursts and the tick clumps — "one... three...
    // four-five-six-seven... nine". CompositionTarget.Rendering fires per
    // frame on the UI thread and can't be starved the same way. We throttle
    // internally to 1Hz by tracking the last-rendered second, so per-frame
    // cost is a single integer compare plus a branch. Subscription is gated
    // on IsVisibleChanged so a minimised / tray-resident window pays zero.
    private int _lastClockSecond = -1;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;

        // Wire up SettingsViewModel with its own DataContext (Critical fix #1)
        _settingsVm = new SettingsViewModel(_viewModel);
        SettingsTabContent.DataContext = _settingsVm;

        // Give MainViewModel a reference to Settings so it can read notification
        // toggles in ApplyEvent and forward designation responses to the UI.
        _viewModel.Settings = _settingsVm;

        // Apply size and position BEFORE the window is shown.
        // CenterScreen computes position from Width/Height at show time —
        // if these are 0 (no hardcoded XAML values), it positions at 0,0.
        ApplyDefaultSize();
        RestoreWindowPosition();

        Loaded += OnLoaded;
        Closing += OnClosing;

        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        _settingsVm.PropertyChanged += OnSettingsPropertyChanged;

        // Clock tick plumbing. Subscribe/unsubscribe on IsVisibleChanged so
        // a hidden or tray-resident window pays nothing at all.
        IsVisibleChanged += OnIsVisibleChanged;

        // Keyboard shortcuts (Critical fix #2)
        InputBindings.Add(new KeyBinding(_viewModel.CopyToClipboardCommand, Key.C, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(_viewModel.RefreshCommand, Key.F5, ModifierKeys.None));
        InputBindings.Add(new KeyBinding(new RelayCommand(() => MainTabControl.SelectedIndex = 0), Key.D1, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(new RelayCommand(() => MainTabControl.SelectedIndex = 1), Key.D2, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(new RelayCommand(() => MainTabControl.SelectedIndex = 2), Key.D3, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(new RelayCommand(() => MainTabControl.SelectedIndex = 3), Key.D4, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(new RelayCommand(() => MainTabControl.SelectedIndex = 4), Key.D5, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(new RelayCommand(() => MainTabControl.SelectedIndex = 5), Key.D6, ModifierKeys.Control));
        // Ctrl+S — show snapshot naming dialog then save. Guard against
        // the era-naming TextBox: Window-level InputBindings fire even when
        // a TextBox inside the window has focus, so Ctrl+S mid-type on the
        // Timeline banner would pop the snapshot dialog on top of the era
        // naming TextBox. Skip the shortcut while IsNamingEra is true.
        InputBindings.Add(new KeyBinding(new RelayCommand(() =>
        {
            if (_viewModel.Timeline.IsNamingEra) return;
            ShowSnapshotDialogAndSave();
        }), Key.S, ModifierKeys.Control));
    }

    private async void OnLoaded(object sender, System.Windows.RoutedEventArgs e)
    {
        // async void is unavoidable on WPF Loaded; any unhandled exception
        // is routed to the Dispatcher's unhandled-exception path and the
        // tray-resident process dies with no visible window — the user
        // sees the tray icon disappear. Wrap the entire body so a rogue
        // exception surfaces in the log and the reconnect loop inside
        // StartAsync has a chance to recover.
        try
        {
            // Dark title bar on Windows 11 (Frodo warning #9)
            EnableDarkTitleBar();

            _tray = new TrayIconManager(
                this,
                onCopyDigest: () => _viewModel.CopyDigestCommand.Execute(null),
                onSaveSnapshot: () => Dispatcher.Invoke(ShowSnapshotDialogAndSave));
            _tray.Initialize();

            // Start minimized to tray if launched with --minimized (autostart)
            // or if the StartMinimized setting is enabled.
            bool cliMinimized = Application.Current is App app && app.StartMinimized;
            if (cliMinimized || _settingsVm.StartMinimized)
            {
                _tray.MinimizeToTray();
            }

            await _viewModel.StartAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MainWindow.OnLoaded] unhandled: {ex}");
            // Don't rethrow — the reconnect machinery inside StartAsync
            // handles its own loop errors, and any exception that escapes
            // here means the window couldn't even start. The alternative
            // (process termination) is strictly worse for the user.
        }
    }

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_minimizeToTray && !_quitting)
        {
            e.Cancel = true;
            _tray?.MinimizeToTray();
            return;
        }

        // Save window position before closing (Critical fix #3)
        SaveWindowPosition();

        await _viewModel.StopAsync();
        _tray?.Dispose();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_tray is null) return;

        switch (e.PropertyName)
        {
            case nameof(MainViewModel.StatusColor):
                var state = _viewModel.StatusColor switch
                {
                    "Green" => TrayState.Green,
                    "Red" => TrayState.Red,
                    _ => TrayState.Gray
                };
                _tray.SetState(state);
                break;

            case nameof(MainViewModel.StabilityErrorCount):
            case nameof(MainViewModel.SystemEventCount):
            case nameof(MainViewModel.UptimeText):
                var tooltip = _viewModel.StabilityErrorCount == 0
                    ? $"RAMWatch — Clean ({_viewModel.UptimeText})"
                    : $"RAMWatch — {_viewModel.StabilityErrorCount} stability error{(_viewModel.StabilityErrorCount != 1 ? "s" : "")} since boot";
                _tray.UpdateTooltip(tooltip);
                break;

            case nameof(MainViewModel.IsConnected):
                if (!_viewModel.IsConnected)
                {
                    _tray.SetState(TrayState.Gray);
                    _tray.UpdateTooltip($"RAMWatch — {_viewModel.ConnectionStatus}");
                }
                break;

            case nameof(MainViewModel.ConnectionStatus):
                // Mirror the richer ConnectionStatus into the tray tooltip
                // while the pipe is offline. Once connected, the stability
                // handler above overwrites with "Clean (uptime)" and wins.
                if (!_viewModel.IsConnected)
                    _tray.UpdateTooltip($"RAMWatch — {_viewModel.ConnectionStatus}");
                break;
        }

        // When the service sends a detected board vendor, update the Settings tab label
        // so it shows e.g. "Auto (detected: MSI)" next to the layout dropdown.
        if (e.PropertyName == nameof(MainViewModel.DetectedBiosVendor))
        {
            var vendor = _viewModel.DetectedBiosVendor;
            _settingsVm.BiosLayoutDetectedLabel = string.IsNullOrEmpty(vendor)
                ? ""
                : $"(detected: {vendor})";
        }
    }

    private void OnSettingsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SettingsViewModel.AlwaysOnTop))
            Topmost = _settingsVm.AlwaysOnTop;
        if (e.PropertyName == nameof(SettingsViewModel.MinimizeToTray))
            _minimizeToTray = _settingsVm.MinimizeToTray;
        // ClockTickSeconds is a no-op for the Now clock now that it rides the
        // composition loop — the throttle is always a fresh second. Retain
        // the setting for future use (sub-second tick, 5s low-power mode).
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible)
        {
            // Render an up-to-date clock immediately so the user never sees
            // a stale value until the next frame.
            _lastClockSecond = -1;
            _viewModel.TickClock();
            CompositionTarget.Rendering -= OnCompositionRendering;
            CompositionTarget.Rendering += OnCompositionRendering;
        }
        else
        {
            CompositionTarget.Rendering -= OnCompositionRendering;
        }
    }

    private void OnCompositionRendering(object? sender, EventArgs e)
    {
        // Throttle: only touch observable properties when the wall-clock
        // second changes. Per-frame cost when the second is unchanged is
        // one DateTime.Now, one int compare, one branch — cheaper than any
        // timer-based approach would be when it does fire.
        int second = DateTime.Now.Second;
        if (second == _lastClockSecond) return;
        _lastClockSecond = second;
        _viewModel.TickClock();
    }

    /// <summary>
    /// Shows the snapshot naming dialog. If the user confirms, executes SaveSnapshotCommand
    /// with the label they entered. Called from Ctrl+S, the tray icon, and the Timings tab button.
    /// </summary>
    internal void ShowSnapshotDialogAndSave()
    {
        // Build a default label from current timings — falls back to date when no timings.
        // PrimaryTimingsLabel is "CL16-20-20-42" when timings are available, empty otherwise.
        var t = _viewModel.Timings;
        string defaultLabel = string.IsNullOrEmpty(t.PrimaryTimingsLabel)
            ? DateTime.Today.ToString("yyyy-MM-dd")
            : $"{t.PrimaryTimingsLabel} {DateTime.Today:yyyy-MM-dd}";

        var dialog = new SnapshotNameDialog(defaultLabel)
        {
            Owner = this
        };

        if (dialog.ShowDialog() != true)
            return;

        _viewModel.SaveSnapshotCommand.Execute(dialog.Label);
    }

    internal void QuitApplication()
    {
        _quitting = true;
        Close();
    }

    /// <summary>
    /// Raise, un-hide, un-minimize, and focus the main window. Called by
    /// App when a second-instance launch signals us via the cross-process
    /// show event. Preserves the user's AlwaysOnTop choice by restoring
    /// the Topmost value after the force-to-front toggle.
    /// </summary>
    internal void ShowAndActivate()
    {
        Show();
        if (WindowState == System.Windows.WindowState.Minimized)
            WindowState = System.Windows.WindowState.Normal;
        Activate();
        bool wasTopmost = Topmost;
        Topmost = true;
        Topmost = wasTopmost;
        Focus();
    }

    // ── DPI-responsive default size ───────────────────────────

    /// <summary>
    /// Sets the window to a sensible DPI-independent default size.
    /// Uses device-independent pixels (DIPs) so the size is appropriate
    /// at all DPI scales. Only applied before a saved position is restored;
    /// RestoreWindowPosition will overwrite these values when prefs exist.
    /// </summary>
    private void ApplyDefaultSize()
    {
        // Width/Height are set in XAML. Only clamp to MinWidth/MinHeight in case
        // the work area is smaller than the default (e.g. very low-res display).
        var workArea = SystemParameters.WorkArea;
        Width  = Math.Max(MinWidth,  Math.Min(Width,  workArea.Width));
        Height = Math.Max(MinHeight, Math.Min(Height, workArea.Height));
    }

    // ── Window position persistence (Critical fix #3) ────────

    private void SaveWindowPosition()
    {
        try
        {
            var prefs = new WindowPrefs
            {
                Left = Left,
                Top = Top,
                Width = Width,
                Height = Height,
                IsMaximized = WindowState == System.Windows.WindowState.Maximized
            };
            var json = System.Text.Json.JsonSerializer.Serialize(prefs);
            var prefsDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "RAMWatch");
            Directory.CreateDirectory(prefsDir);
            File.WriteAllText(Path.Combine(prefsDir, "window.json"), json);
        }
        catch { }
    }

    private void RestoreWindowPosition()
    {
        try
        {
            var prefsPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "RAMWatch", "window.json");
            if (!File.Exists(prefsPath)) return;

            var json = File.ReadAllText(prefsPath);
            var prefs = System.Text.Json.JsonSerializer.Deserialize<WindowPrefs>(json);
            if (prefs is null) return;

            // Validate the title bar (top 40px) is reachable on an active monitor.
            // Previous check required the entire window to fit, which failed when
            // the saved size was from a different DPI/resolution session.
            double vsLeft = SystemParameters.VirtualScreenLeft;
            double vsTop = SystemParameters.VirtualScreenTop;
            double vsRight = vsLeft + SystemParameters.VirtualScreenWidth;
            double vsBottom = vsTop + SystemParameters.VirtualScreenHeight;

            bool titleBarVisible =
                prefs.Left + 100 < vsRight &&   // at least 100px of title bar visible horizontally
                prefs.Left + prefs.Width > vsLeft + 50 && // not entirely off-screen left
                prefs.Top >= vsTop - 10 &&       // title bar not above all screens
                prefs.Top < vsBottom - 40;       // title bar not below all screens

            if (titleBarVisible)
            {
                Left = prefs.Left;
                Top = prefs.Top;
                Width  = Math.Max(MinWidth,  Math.Min(prefs.Width,  vsRight  - prefs.Left));
                Height = Math.Max(MinHeight, Math.Min(prefs.Height, vsBottom - prefs.Top));
                WindowStartupLocation = System.Windows.WindowStartupLocation.Manual;
                if (prefs.IsMaximized)
                    WindowState = System.Windows.WindowState.Maximized;
            }
        }
        catch { }
    }

    // ── Dark title bar (Frodo warning #9) ────────────────────

    private void EnableDarkTitleBar()
    {
        try
        {
            if (PresentationSource.FromVisual(this) is HwndSource source)
            {
                DwmSetWindowAttribute(source.Handle, 20, [1], sizeof(int));
            }
        }
        catch { }
    }

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(IntPtr hwnd, int attr, int[] attrValue, int attrSize);
}

internal sealed record WindowPrefs
{
    public double Left { get; init; }
    public double Top { get; init; }
    public double Width { get; init; }
    public double Height { get; init; }
    public bool IsMaximized { get; init; }
}

/// <summary>
/// Simple relay command for keyboard bindings.
/// </summary>
internal sealed class RelayCommand(Action execute) : ICommand
{
    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => execute();
}
