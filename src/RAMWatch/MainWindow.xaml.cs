using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
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

        // Keyboard shortcuts (Critical fix #2)
        InputBindings.Add(new KeyBinding(_viewModel.CopyToClipboardCommand, Key.C, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(_viewModel.RefreshCommand, Key.F5, ModifierKeys.None));
        InputBindings.Add(new KeyBinding(new RelayCommand(() => MainTabControl.SelectedIndex = 0), Key.D1, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(new RelayCommand(() => MainTabControl.SelectedIndex = 1), Key.D2, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(new RelayCommand(() => MainTabControl.SelectedIndex = 2), Key.D3, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(new RelayCommand(() => MainTabControl.SelectedIndex = 3), Key.D4, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(new RelayCommand(() => MainTabControl.SelectedIndex = 4), Key.D5, ModifierKeys.Control));
        // Ctrl+S — show snapshot naming dialog then save
        InputBindings.Add(new KeyBinding(new RelayCommand(ShowSnapshotDialogAndSave), Key.S, ModifierKeys.Control));
    }

    private async void OnLoaded(object sender, System.Windows.RoutedEventArgs e)
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
                    _tray.UpdateTooltip("RAMWatch — Service not connected");
                }
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

    // ── DPI-responsive default size ───────────────────────────

    /// <summary>
    /// Sets the window to a sensible DPI-independent default size.
    /// Uses device-independent pixels (DIPs) so the size is appropriate
    /// at all DPI scales. Only applied before a saved position is restored;
    /// RestoreWindowPosition will overwrite these values when prefs exist.
    /// </summary>
    private void ApplyDefaultSize()
    {
        var workArea = SystemParameters.WorkArea;

        // Target 28% of work-area width, 50% of work-area height.
        // Timings tab drives minimum width (~440px at tightened density).
        // 50% height shows full timings without scrolling on most displays.
        double targetW = workArea.Width  * 0.28;
        double targetH = workArea.Height * 0.50;

        // Clamp to reasonable bounds so the window is never absurdly small or large.
        Width  = Math.Clamp(targetW, 440, 520);
        Height = Math.Clamp(targetH, 500, 720);
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
                Width = Math.Min(prefs.Width, vsRight - prefs.Left);  // don't extend past right edge
                Height = Math.Min(prefs.Height, vsBottom - prefs.Top); // don't extend past bottom
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
