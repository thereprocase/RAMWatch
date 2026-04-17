namespace RAMWatch.Service.Services;

/// <summary>
/// Async fire-and-forget file copy to a user-configured mirror directory
/// (Dropbox, OneDrive, Synology Drive, etc.). Never blocks the primary
/// write path. Timeout at 5 seconds per copy — sync services can lock
/// files during indexing, and we cannot stall the service waiting.
///
/// A SemaphoreSlim caps in-flight copies. Under an event storm plus a
/// slow mirror (Dropbox reindex, OneDrive offline, USB stick), the prior
/// implementation would spawn hundreds of concurrent FileStream-holding
/// tasks before the circuit breaker engaged.
/// </summary>
public sealed class MirrorLogger
{
    private readonly string _mirrorDirectory;
    private int _consecutiveFailures;

    private const int TimeoutMs = 5000;
    private const int MaxConsecutiveFailures = 10;

    // Max concurrent copies. Small by design: mirroring is best-effort and
    // one-in-flight per target is enough for the typical 30-60s event
    // cadence. When the semaphore is full we drop the new copy rather than
    // queue it, so a slow mirror cannot grow a backlog behind the service.
    private const int MaxConcurrentCopies = 4;
    private readonly SemaphoreSlim _slots = new(MaxConcurrentCopies, MaxConcurrentCopies);

    public MirrorLogger(string mirrorDirectory)
    {
        _mirrorDirectory = mirrorDirectory;
    }

    public bool IsEnabled => !string.IsNullOrWhiteSpace(_mirrorDirectory);

    /// <summary>
    /// Copy a file to the mirror directory. Fire-and-forget — failures
    /// are logged locally but never propagated to the caller. Drops the
    /// copy if the in-flight semaphore is full; the CSV is already safe
    /// locally and the mirror will catch up on the next event.
    /// </summary>
    public void EnqueueCopy(string sourceFile)
    {
        if (!IsEnabled) return;
        if (_consecutiveFailures >= MaxConsecutiveFailures) return;

        if (!_slots.Wait(0))
        {
            // All copy slots busy — skip this one rather than pile up. A
            // slow mirror doesn't get to block indefinitely or grow the
            // task list behind the producer.
            return;
        }

        _ = CopyWithTimeoutAsync(sourceFile);
    }

    private async Task CopyWithTimeoutAsync(string sourceFile)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeoutMs);
            var destDir = _mirrorDirectory;
            Directory.CreateDirectory(destDir);

            string fileName = Path.GetFileName(sourceFile);
            string destPath = Path.Combine(destDir, fileName);

            // Read source with sharing (the CSV logger has it open with FileShare.Read)
            using var source = new FileStream(sourceFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var dest = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None);

            await source.CopyToAsync(dest, cts.Token);

            Interlocked.Exchange(ref _consecutiveFailures, 0);
        }
        catch (OperationCanceledException)
        {
            // Timeout — sync service likely has the file locked
            Interlocked.Increment(ref _consecutiveFailures);
        }
        catch
        {
            // Network drive offline, permission denied, etc.
            Interlocked.Increment(ref _consecutiveFailures);
        }
        finally
        {
            _slots.Release();
        }
    }

    /// <summary>
    /// Reset the failure counter. Call when settings change or on periodic health check.
    /// </summary>
    public void ResetFailures()
    {
        Interlocked.Exchange(ref _consecutiveFailures, 0);
    }
}
