using System.IO.Pipes;
using System.Text;

namespace RAMWatch.Core.Ipc;

/// <summary>
/// Named pipe client with automatic reconnection using exponential backoff.
/// Designed for the GUI side — connects to the service pipe, receives
/// state pushes, sends commands.
/// </summary>
public sealed class PipeClient : IAsyncDisposable
{
    private NamedPipeClientStream? _pipe;
    private StreamWriter? _writer;
    private StreamReader? _reader;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public bool IsConnected => _pipe?.IsConnected == true;

    /// <summary>
    /// Attempt to connect to the service pipe. Returns true on success.
    /// </summary>
    public async Task<bool> ConnectAsync(int timeoutMs = 3000, CancellationToken ct = default)
    {
        await DisconnectAsync();

        var pipe = new NamedPipeClientStream(
            ".",
            PipeConstants.PipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);

        try
        {
            await pipe.ConnectAsync(timeoutMs, ct);
            _pipe = pipe;
            _writer = new StreamWriter(pipe, Encoding.UTF8, leaveOpen: true) { AutoFlush = true };
            _reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
            return true;
        }
        catch
        {
            await pipe.DisposeAsync();
            return false;
        }
    }

    /// <summary>
    /// Connect with exponential backoff. Retries until connected or cancelled.
    /// Delays: 500ms, 1s, 2s, 4s, 8s, then caps at 10s.
    /// </summary>
    public async Task ConnectWithRetryAsync(CancellationToken ct = default)
    {
        int delayMs = 500;
        while (!ct.IsCancellationRequested)
        {
            if (await ConnectAsync(3000, ct))
                return;

            try
            {
                await Task.Delay(delayMs, ct);
            }
            catch (OperationCanceledException) { return; }

            delayMs = Math.Min(delayMs * 2, 10_000);
        }
    }

    public async Task SendAsync(string serializedMessage)
    {
        if (_writer is null)
            throw new InvalidOperationException("Not connected");

        await _writeLock.WaitAsync();
        try
        {
            await _writer.WriteAsync(serializedMessage);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async IAsyncEnumerable<string> ReadLinesAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        if (_reader is null)
            yield break;

        while (!ct.IsCancellationRequested)
        {
            string? line;
            try
            {
                line = await _reader.ReadLineAsync(ct);
            }
            catch (OperationCanceledException) { yield break; }
            catch { yield break; }

            if (line is null)
                yield break;

            if (!string.IsNullOrWhiteSpace(line))
                yield return line;
        }
    }

    public async Task DisconnectAsync()
    {
        _writer?.Dispose();
        _reader?.Dispose();
        if (_pipe is not null)
            await _pipe.DisposeAsync();
        _writer = null;
        _reader = null;
        _pipe = null;
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
        _writeLock.Dispose();
    }
}
