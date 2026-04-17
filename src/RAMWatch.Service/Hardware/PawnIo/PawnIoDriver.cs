using System.Runtime.InteropServices;
using System.Text;

namespace RAMWatch.Service.Hardware.PawnIo;

/// <summary>
/// Low-level wrapper around PawnIOLib.dll — the official PawnIO userspace library.
/// Provides pawnio_open/load/execute/close via P/Invoke.
///
/// PawnIOLib.dll lives at C:\Program Files\PawnIO\PawnIOLib.dll (admin-owned path).
/// The library handles all kernel communication (device handle, IOCTLs).
/// We never touch the kernel device directly.
///
/// Thread safety: NOT thread-safe. Callers must serialize access.
/// </summary>
public sealed class PawnIoDriver : IDisposable
{
    private IntPtr _handle;
    private bool _disposed;

    private const string PawnIoLibPath = @"C:\Program Files\PawnIO\PawnIOLib.dll";

    /// <summary>
    /// Check if PawnIOLib.dll exists at the known install path.
    /// </summary>
    public static bool IsInstalled => File.Exists(PawnIoLibPath);

    /// <summary>
    /// Open a PawnIO executor handle. Must be called before Load/Execute.
    /// Requires admin/SYSTEM — the PawnIO device restricts access.
    /// </summary>
    public bool Open()
    {
        if (_handle != IntPtr.Zero) return true;

        int hr = NativeMethods.pawnio_open(out _handle);
        return hr >= 0 && _handle != IntPtr.Zero;
    }

    /// <summary>
    /// Load a compiled Pawn module (.bin) into the PawnIO kernel driver.
    /// Must be called after Open() and before Execute().
    /// </summary>
    public bool LoadModule(byte[] moduleBytes)
    {
        if (_handle == IntPtr.Zero)
            throw new InvalidOperationException("PawnIO handle not open");

        int hr = NativeMethods.pawnio_load(_handle, moduleBytes, (nuint)moduleBytes.Length);
        return hr >= 0;
    }

    /// <summary>
    /// Execute a named function in the loaded module.
    /// </summary>
    /// <param name="functionName">ASCII function name (e.g., "ioctl_read_smn")</param>
    /// <param name="input">Input parameters as 64-bit values</param>
    /// <param name="outputCount">Expected number of output values</param>
    /// <returns>Output values, or null on failure</returns>
    public ulong[]? Execute(string functionName, ulong[] input, int outputCount)
    {
        if (_handle == IntPtr.Zero)
            throw new InvalidOperationException("PawnIO handle not open");

        var output = new ulong[outputCount];
        nuint returnSize = 0;

        int hr = NativeMethods.pawnio_execute(
            _handle,
            functionName,
            input,
            (nuint)input.Length,
            output,
            (nuint)outputCount,
            ref returnSize);

        if (hr < 0) return null;
        return output;
    }

    /// <summary>
    /// Execute a PawnIO function into a caller-supplied output buffer. Zero
    /// allocations per call, for hot paths (PM table read at 3s cadence).
    /// Returns true on success; on failure, the output buffer contents are
    /// undefined and the caller should discard the read.
    /// </summary>
    public bool ExecuteInto(string functionName, ulong[] input, int inputCount, ulong[] output, int outputCount)
    {
        if (_handle == IntPtr.Zero)
            throw new InvalidOperationException("PawnIO handle not open");
        if (output.Length < outputCount)
            throw new ArgumentException("output buffer is smaller than outputCount", nameof(output));
        if (input.Length < inputCount)
            throw new ArgumentException("input buffer is smaller than inputCount", nameof(input));

        nuint returnSize = 0;
        int hr = NativeMethods.pawnio_execute(
            _handle,
            functionName,
            input,
            (nuint)inputCount,
            output,
            (nuint)outputCount,
            ref returnSize);

        return hr >= 0;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_handle != IntPtr.Zero)
        {
            NativeMethods.pawnio_close(_handle);
            _handle = IntPtr.Zero;
        }
    }

    /// <summary>
    /// P/Invoke declarations for PawnIOLib.dll.
    /// Using [DllImport] with explicit path — not [LibraryImport] because
    /// the DLL is at a fixed known path, not on the default search path.
    /// </summary>
    private static class NativeMethods
    {
        // pawnio_open_win32 uses BOOL return + GetLastError convention,
        // but pawnio_open returns HRESULT which is more useful.
        [DllImport(PawnIoLibPath, CallingConvention = CallingConvention.StdCall, EntryPoint = "pawnio_open")]
        public static extern int pawnio_open(out IntPtr handle);

        [DllImport(PawnIoLibPath, CallingConvention = CallingConvention.StdCall, EntryPoint = "pawnio_load")]
        public static extern int pawnio_load(
            IntPtr handle,
            [MarshalAs(UnmanagedType.LPArray)] byte[] blob,
            nuint size);

        [DllImport(PawnIoLibPath, CallingConvention = CallingConvention.StdCall, EntryPoint = "pawnio_execute")]
        public static extern int pawnio_execute(
            IntPtr handle,
            [MarshalAs(UnmanagedType.LPStr)] string name,
            [MarshalAs(UnmanagedType.LPArray)] ulong[] input,
            nuint inputSize,
            [MarshalAs(UnmanagedType.LPArray)] ulong[] output,
            nuint outputSize,
            ref nuint returnSize);

        [DllImport(PawnIoLibPath, CallingConvention = CallingConvention.StdCall, EntryPoint = "pawnio_close")]
        public static extern int pawnio_close(IntPtr handle);
    }
}
