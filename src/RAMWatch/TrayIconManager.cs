using System.Drawing;
using System.Windows;
using H.NotifyIcon;

namespace RAMWatch;

/// <summary>
/// System tray icon with green/red/gray states and context menu.
/// Uses H.NotifyIcon.Wpf for modern WPF tray support.
/// </summary>
public sealed class TrayIconManager : IDisposable
{
    private TaskbarIcon? _trayIcon;
    private readonly Window _mainWindow;
    private bool _firstMinimize = true;

    public TrayIconManager(Window mainWindow)
    {
        _mainWindow = mainWindow;
    }

    public void Initialize()
    {
        _trayIcon = new TaskbarIcon
        {
            ToolTipText = "RAMWatch — Connecting...",
            ContextMenu = BuildContextMenu(),
        };

        _trayIcon.TrayLeftMouseDown += (_, _) => ShowWindow();

        SetState(TrayState.Gray);
    }

    public void SetState(TrayState state)
    {
        if (_trayIcon is null) return;

        var color = state switch
        {
            TrayState.Green => Color.FromArgb(0x00, 0xC8, 0x53),
            TrayState.Red => Color.FromArgb(0xFF, 0x17, 0x44),
            _ => Color.FromArgb(0x61, 0x61, 0x61)
        };

        _trayIcon.Icon = CreateColorIcon(color);
    }

    public void UpdateTooltip(string text)
    {
        if (_trayIcon is not null)
            _trayIcon.ToolTipText = text;
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

        var showItem = new System.Windows.Controls.MenuItem { Header = "Show RAMWatch" };
        showItem.Click += (_, _) => ShowWindow();
        menu.Items.Add(showItem);

        menu.Items.Add(new System.Windows.Controls.Separator());

        var quitItem = new System.Windows.Controls.MenuItem { Header = "Quit" };
        quitItem.Click += (_, _) =>
        {
            Dispose();
            Application.Current.Shutdown();
        };
        menu.Items.Add(quitItem);

        return menu;
    }

    /// <summary>
    /// Generate a simple colored circle icon programmatically.
    /// 16x16, transparent background, filled circle in the given color.
    /// </summary>
    private static Icon CreateColorIcon(Color color)
    {
        using var bitmap = new Bitmap(16, 16);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);
        using var brush = new SolidBrush(color);
        g.FillEllipse(brush, 1, 1, 14, 14);
        return Icon.FromHandle(bitmap.GetHicon());
    }

    public void Dispose()
    {
        _trayIcon?.Dispose();
        _trayIcon = null;
    }
}

public enum TrayState
{
    Green,
    Red,
    Gray
}
