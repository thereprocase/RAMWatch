using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;

namespace RAMWatch;

public partial class App : System.Windows.Application
{
    private const string MutexName = "RAMWatch_SingleInstance";
    private const string ShowEventName = "RAMWatch_ShowWindow";

    private Mutex? _instanceMutex;
    private EventWaitHandle? _showWindowEvent;
    private RegisteredWaitHandle? _showWindowRegistration;

    /// <summary>
    /// True when launched with --minimized (e.g. from autostart registry entry).
    /// MainWindow reads this to start hidden in the system tray.
    /// </summary>
    public bool StartMinimized { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        _instanceMutex = new Mutex(true, MutexName, out bool isNew);
        if (!isNew)
        {
            SignalExistingToShow();
            Shutdown();
            return;
        }

        // Parse CLI args — Install-RAMWatch.ps1 registers autostart with --minimized.
        StartMinimized = e.Args.Contains("--minimized", StringComparer.OrdinalIgnoreCase);

        // Cross-process show signal. Second-instance launches open this named event
        // and Set() it; the wait registration below marshals the signal back onto
        // the UI thread and raises the main window. This works regardless of
        // whether the window is visible, minimized, or Hide()-to-tray — the older
        // BringExistingToFront path relied on Process.MainWindowHandle, which
        // returns IntPtr.Zero for hidden windows, so autostart-minimized instances
        // were unreachable from a subsequent manual launch.
        _showWindowEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        _showWindowRegistration = ThreadPool.RegisterWaitForSingleObject(
            _showWindowEvent,
            (_, timedOut) =>
            {
                if (timedOut) return;
                Dispatcher.BeginInvoke(() =>
                {
                    if (MainWindow is MainWindow mw) mw.ShowAndActivate();
                });
            },
            state: null,
            timeout: Timeout.InfiniteTimeSpan,
            executeOnlyOnce: false);

        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Unregister the wait before disposing the event — otherwise the thread
        // pool could fire one last callback on a dangling handle.
        _showWindowRegistration?.Unregister(null);
        _showWindowRegistration = null;

        _showWindowEvent?.Dispose();
        _showWindowEvent = null;

        // Release the mutex so a restart can acquire it immediately.
        try { _instanceMutex?.ReleaseMutex(); } catch { }
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }

    /// <summary>
    /// Signal a running RAMWatch instance to show its main window. Primary
    /// path opens the named event; the process-enumeration fallback handles
    /// the unlikely case where the first instance hasn't finished creating
    /// the event yet (window between mutex acquisition and event
    /// construction in OnStartup) or where the handle name is unavailable.
    /// </summary>
    private static void SignalExistingToShow()
    {
        try
        {
            using var evt = EventWaitHandle.OpenExisting(ShowEventName);
            evt.Set();
            return;
        }
        catch
        {
            // Fall through to process-enumeration fallback.
        }

        BringExistingToFront();
    }

    /// <summary>
    /// Finds an existing RAMWatch process and brings its main window to the foreground.
    /// Uses Process enumeration to locate the window handle, then Win32 to restore/raise.
    /// </summary>
    private static void BringExistingToFront()
    {
        try
        {
            var currentId   = Environment.ProcessId;
            var currentName = Process.GetCurrentProcess().ProcessName;

            foreach (var proc in Process.GetProcessesByName(currentName))
            {
                if (proc.Id == currentId) continue;

                var hwnd = proc.MainWindowHandle;
                if (hwnd == IntPtr.Zero) continue;

                // SW_RESTORE (9): unminimize if minimized, otherwise show.
                ShowWindow(hwnd, 9);
                SetForegroundWindow(hwnd);
                break;
            }
        }
        catch { }
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(IntPtr hWnd);
}
