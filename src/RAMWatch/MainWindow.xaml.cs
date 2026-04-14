using System.ComponentModel;
using RAMWatch.ViewModels;

namespace RAMWatch;

public partial class MainWindow : System.Windows.Window
{
    private readonly MainViewModel _viewModel = new();
    private TrayIconManager? _tray;
    private bool _minimizeToTray = true;
    private bool _quitting;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
        Loaded += OnLoaded;
        Closing += OnClosing;

        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    private async void OnLoaded(object sender, System.Windows.RoutedEventArgs e)
    {
        _tray = new TrayIconManager(this);
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
    }

    /// <summary>
    /// Called from tray "Quit" to bypass the minimize-to-tray behavior.
    /// </summary>
    internal void QuitApplication()
    {
        _quitting = true;
        Close();
    }
}
