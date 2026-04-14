using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;

namespace RAMWatch;

public partial class App : System.Windows.Application
{
    private Mutex? _instanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        _instanceMutex = new Mutex(true, "RAMWatch_SingleInstance", out bool isNew);
        if (!isNew)
        {
            BringExistingToFront();
            Shutdown();
            return;
        }

        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Release the mutex so a restart can acquire it immediately.
        try { _instanceMutex?.ReleaseMutex(); } catch { }
        _instanceMutex?.Dispose();
        base.OnExit(e);
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
