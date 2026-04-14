using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace RAMWatch.Core.Ipc;

/// <summary>
/// Named pipe server with explicit DACL restricting access to SYSTEM
/// and the interactive user SID (B4: no open-to-all-users default).
/// Supports multiple concurrent clients via async accept loop.
/// </summary>
public sealed class PipeServer : IAsyncDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly List<ConnectedClient> _clients = [];
    private readonly Lock _clientsLock = new();
    private readonly Func<string, ConnectedClient, Task>? _onMessage;
    private readonly Func<ConnectedClient, Task>? _onClientConnected;
    private Task? _acceptLoop;

    public PipeServer(
        Func<string, ConnectedClient, Task>? onMessage = null,
        Func<ConnectedClient, Task>? onClientConnected = null)
    {
        _onMessage = onMessage;
        _onClientConnected = onClientConnected;
    }

    public void Start()
    {
        _acceptLoop = AcceptClientsAsync(_cts.Token);
    }

    public async Task BroadcastAsync(string serializedMessage)
    {
        ConnectedClient[] snapshot;
        lock (_clientsLock)
        {
            snapshot = [.. _clients];
        }

        if (snapshot.Length == 0)
            return;

        // Send to all clients in parallel. A slow or dead client cannot block
        // the others — each send has a 5-second timeout.
        var results = await Task.WhenAll(
            snapshot.Select(c => SendWithTimeoutAsync(c, serializedMessage)));

        // Clean up any clients that failed to receive.
        var disconnected = snapshot
            .Zip(results, (client, failed) => (client, failed))
            .Where(p => p.failed)
            .Select(p => p.client)
            .ToList();

        if (disconnected.Count > 0)
        {
            lock (_clientsLock)
            {
                foreach (var c in disconnected)
                    _clients.Remove(c);
            }
            foreach (var c in disconnected)
                await c.DisposeAsync();
        }
    }

    /// <summary>
    /// Send to one client with a 5-second wall-clock timeout.
    /// Returns true if the send failed (client should be removed).
    /// Exceptions are swallowed — a dead client is not a service error.
    /// </summary>
    private static async Task<bool> SendWithTimeoutAsync(ConnectedClient client, string message)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await client.SendAsync(message, cts.Token);
            return false; // success
        }
        catch
        {
            return true; // failed — caller will remove this client
        }
    }

    private async Task AcceptClientsAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var pipe = CreateSecuredPipe();
            try
            {
                await pipe.WaitForConnectionAsync(ct);
                var client = new ConnectedClient(pipe);
                lock (_clientsLock)
                {
                    _clients.Add(client);
                }
                if (_onClientConnected is not null)
                    _ = _onClientConnected(client);
                _ = ReadFromClientAsync(client, ct);
            }
            catch (OperationCanceledException)
            {
                await pipe.DisposeAsync();
                break;
            }
            catch
            {
                await pipe.DisposeAsync();
            }
        }
    }

    private async Task ReadFromClientAsync(ConnectedClient client, CancellationToken ct)
    {
        try
        {
            await foreach (var line in client.ReadLinesAsync(ct))
            {
                if (_onMessage is not null)
                    await _onMessage(line, client);
            }
        }
        catch (OperationCanceledException) { }
        catch { }
        finally
        {
            lock (_clientsLock)
            {
                _clients.Remove(client);
            }
            await client.DisposeAsync();
        }
    }

    private static NamedPipeServerStream CreateSecuredPipe()
    {
        var security = new PipeSecurity();

        // SYSTEM — the service's own identity
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));

        // Interactive users — the logged-in desktop user
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.InteractiveSid, null),
            PipeAccessRights.ReadWrite,
            AccessControlType.Allow));

        var pipe = NamedPipeServerStreamAcl.Create(
            PipeConstants.PipeName,
            PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.WriteThrough,
            PipeConstants.BufferSize,
            PipeConstants.BufferSize,
            security);

        return pipe;
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        if (_acceptLoop is not null)
        {
            try { await _acceptLoop; } catch { }
        }

        ConnectedClient[] snapshot;
        lock (_clientsLock)
        {
            snapshot = [.. _clients];
            _clients.Clear();
        }
        foreach (var c in snapshot)
            await c.DisposeAsync();

        _cts.Dispose();
    }
}

/// <summary>
/// Represents a single connected pipe client.
/// </summary>
public sealed class ConnectedClient : IAsyncDisposable
{
    private readonly NamedPipeServerStream _pipe;
    private readonly StreamWriter _writer;
    private readonly StreamReader _reader;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public ConnectedClient(NamedPipeServerStream pipe)
    {
        _pipe = pipe;
        _writer = new StreamWriter(pipe, Encoding.UTF8, leaveOpen: true) { AutoFlush = true };
        _reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
    }

    public Task SendAsync(string serializedMessage) =>
        SendAsync(serializedMessage, CancellationToken.None);

    public async Task SendAsync(string serializedMessage, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            await _writer.WriteAsync(serializedMessage.AsMemory(), ct);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async IAsyncEnumerable<string> ReadLinesAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
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
                yield break; // Client disconnected

            if (!string.IsNullOrWhiteSpace(line))
                yield return line;
        }
    }

    public ValueTask DisposeAsync()
    {
        _writer.Dispose();
        _reader.Dispose();
        _pipe.Dispose();
        _writeLock.Dispose();
        return ValueTask.CompletedTask;
    }
}
