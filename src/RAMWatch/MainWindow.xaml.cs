using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using RAMWatch.Core.Models;
using RAMWatch.ViewModels;

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

        Loaded += OnLoaded;
        Closing += OnClosing;

        _viewModel.PropertyChanged += OnViewModelPropertyChanged;

        // Keyboard shortcuts (Critical fix #2)
        InputBindings.Add(new KeyBinding(_viewModel.CopyToClipboardCommand, Key.C, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(_viewModel.RefreshCommand, Key.F5, ModifierKeys.None));
        InputBindings.Add(new KeyBinding(new RelayCommand(() => MainTabControl.SelectedIndex = 0), Key.D1, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(new RelayCommand(() => MainTabControl.SelectedIndex = 1), Key.D2, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(new RelayCommand(() => MainTabControl.SelectedIndex = 2), Key.D3, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(new RelayCommand(() => MainTabControl.SelectedIndex = 3), Key.D4, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(new RelayCommand(() => MainTabControl.SelectedIndex = 4), Key.D5, ModifierKeys.Control));
    }

    private async void OnLoaded(object sender, System.Windows.RoutedEventArgs e)
    {
        // Dark title bar on Windows 11 (Frodo warning #9)
        EnableDarkTitleBar();

        // Restore window position (Critical fix #3)
        RestoreWindowPosition();

        _tray = new TrayIconManager(
            this,
            onCopyDigest: () => _viewModel.CopyToClipboardCommand.Execute(null),
            onSaveSnapshot: () => _viewModel.SaveSnapshotCommand.Execute(null));
        _tray.Initialize();

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

            case nameof(MainViewModel.TotalErrorCount):
            case nameof(MainViewModel.UptimeText):
                var tooltip = _viewModel.TotalErrorCount == 0
                    ? $"RAMWatch — Clean, 0 errors ({_viewModel.UptimeText})"
                    : $"RAMWatch — {_viewModel.TotalErrorCount} errors since boot";
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

    internal void QuitApplication()
    {
        _quitting = true;
        Close();
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

            // Validate the position is on an active monitor
            if (prefs.Left >= SystemParameters.VirtualScreenLeft &&
                prefs.Top >= SystemParameters.VirtualScreenTop &&
                prefs.Left + prefs.Width <= SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth &&
                prefs.Top + prefs.Height <= SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight)
            {
                Left = prefs.Left;
                Top = prefs.Top;
                Width = prefs.Width;
                Height = prefs.Height;
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
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => execute();
}
