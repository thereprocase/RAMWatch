using System.Drawing;
using System.Windows;
using H.NotifyIcon;

namespace RAMWatch;

/// <summary>
/// System tray icon with green/red/gray states and context menu.
/// Uses H.NotifyIcon.Wpf for modern WPF tray support.
/// </summary>
public sealed partial class TrayIconManager : IDisposable
{
    private TaskbarIcon? _trayIcon;
    private readonly Window _mainWindow;
    private readonly Action? _onCopyDigest;
    private readonly Action? _onSaveSnapshot;
    private bool _firstMinimize = true;

    // Pre-built icons — created once in Initialize(), swapped in SetState().
    // Avoids per-update GDI handle allocation.
    private Icon? _iconGreen;
    private Icon? _iconRed;
    private Icon? _iconGray;

    // Status line menu item — updated in SetState/UpdateTooltip.
    private System.Windows.Controls.MenuItem? _statusItem;

    public TrayIconManager(Window mainWindow, Action? onCopyDigest = null, Action? onSaveSnapshot = null)
    {
        _mainWindow = mainWindow;
        _onCopyDigest = onCopyDigest;
        _onSaveSnapshot = onSaveSnapshot;
    }

    public void Initialize()
    {
        // Load Lucide circuit-board icons in green/red/gray from embedded resources.
        _iconGreen = LoadResourceIcon("tray-green.ico");
        _iconRed   = LoadResourceIcon("tray-red.ico");
        _iconGray  = LoadResourceIcon("tray-gray.ico");

        _trayIcon = new TaskbarIcon
        {
            ToolTipText = "RAMWatch — Connecting...",
            ContextMenu = BuildContextMenu(),
        };

        // ForceCreate is required when TaskbarIcon is created in code (not XAML).
        // Without it, the icon object exists but never registers with the Windows
        // shell notification area. Pass false to skip Efficiency Mode — we're an
        // active monitor, not a background utility.
        _trayIcon.ForceCreate(false);

        _trayIcon.TrayLeftMouseDown += (_, _) => ShowWindow();

        SetState(TrayState.Gray);
    }

    public void SetState(TrayState state)
    {
        if (_trayIcon is null) return;

        _trayIcon.Icon = state switch
        {
            TrayState.Green => _iconGreen,
            TrayState.Red   => _iconRed,
            _               => _iconGray,
        };

        // Keep status line in sync with the current tray state.
        if (_statusItem is not null)
        {
            _statusItem.Header = state switch
            {
                TrayState.Green => "Status: OK",
                TrayState.Red   => "Status: Errors detected",
                _               => "Status: Not connected",
            };
        }
    }

    public void UpdateTooltip(string text)
    {
        if (_trayIcon is not null)
            _trayIcon.ToolTipText = text;

        // Mirror tooltip text into the status line for quick glance in the menu.
        if (_statusItem is not null)
            _statusItem.Header = text;
    }

    public void MinimizeToTray()
    {
        _mainWindow.Hide();

        if (_firstMinimize)
        {
            _firstMinimize = false;
            // H.NotifyIcon doesn't support simple balloon tips directly.
            // The tooltip text serves as the user's indicator.
            _trayIcon!.ToolTipText = "RAMWatch — still running. Right-click to quit.";
        }
    }

    private void ShowWindow()
    {
        _mainWindow.Show();
        _mainWindow.WindowState = WindowState.Normal;
        _mainWindow.Activate();
    }

    private System.Windows.Controls.ContextMenu BuildContextMenu()
    {
        var menu = new System.Windows.Controls.ContextMenu();

        // Show RAMWatch
        var showItem = new System.Windows.Controls.MenuItem { Header = "Show RAMWatch" };
        showItem.Click += (_, _) => ShowWindow();
        menu.Items.Add(showItem);

        menu.Items.Add(new System.Windows.Controls.Separator());

        // Status line — disabled/gray; updated by SetState and UpdateTooltip.
        _statusItem = new System.Windows.Controls.MenuItem
        {
            Header    = "Status: Not connected",
            IsEnabled = false,
        };
        menu.Items.Add(_statusItem);

        menu.Items.Add(new System.Windows.Controls.Separator());

        // Save Snapshot — fires the snapshot save on MainViewModel.
        var snapshotItem = new System.Windows.Controls.MenuItem { Header = "Save Snapshot" };
        snapshotItem.Click += (_, _) => _onSaveSnapshot?.Invoke();
        menu.Items.Add(snapshotItem);

        // Copy Digest — fires the clipboard export on MainViewModel.
        var copyDigestItem = new System.Windows.Controls.MenuItem { Header = "Copy Digest" };
        copyDigestItem.Click += (_, _) => _onCopyDigest?.Invoke();
        menu.Items.Add(copyDigestItem);

        menu.Items.Add(new System.Windows.Controls.Separator());

        // Quit — routes through QuitApplication() so OnClosing cleanup runs.
        var quitItem = new System.Windows.Controls.MenuItem { Header = "Quit" };
        quitItem.Click += (_, _) => ((MainWindow)_mainWindow).QuitApplication();
        menu.Items.Add(quitItem);

        return menu;
    }

    /// <summary>
    /// Load an icon from a WPF embedded resource (pack:// URI).
    /// Falls back to a simple colored circle if the resource is missing.
    /// </summary>
    private static Icon LoadResourceIcon(string fileName)
    {
        try
        {
            var uri = new Uri($"pack://application:,,,/{fileName}", UriKind.Absolute);
            var stream = System.Windows.Application.GetResourceStream(uri)?.Stream;
            if (stream is not null)
            {
                using (stream)
                    return new Icon(stream);
            }
        }
        catch { }

        // Fallback: simple colored circle.
        // Icon.FromHandle wraps a borrowed HICON — it doesn't destroy it on Dispose.
        // Clone via the Icon(Icon, Size) constructor to get an owned copy, then
        // destroy the original HICON to avoid a GDI handle leak.
        using var bitmap = new Bitmap(16, 16);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);
        using var brush = new SolidBrush(Color.FromArgb(0x61, 0x61, 0x61));
        g.FillEllipse(brush, 1, 1, 14, 14);
        IntPtr hIcon = bitmap.GetHicon();
        try
        {
            using var temp = Icon.FromHandle(hIcon);
            return new Icon(temp, 16, 16); // owned copy
        }
        finally
        {
            DestroyIcon(hIcon);
        }
    }

    [System.Runtime.InteropServices.LibraryImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static partial bool DestroyIcon(IntPtr hIcon);

    public void Dispose()
    {
        _trayIcon?.Dispose();
        _trayIcon = null;

        // Release the three pre-built GDI icon handles.
        _iconGreen?.Dispose();
        _iconRed?.Dispose();
        _iconGray?.Dispose();
        _iconGreen = null;
        _iconRed   = null;
        _iconGray  = null;
    }
}

public enum TrayState
{
    Green,
    Red,
    Gray
}
