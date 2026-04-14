namespace RAMWatch.Service.Services;

/// <summary>
/// Async fire-and-forget file copy to a user-configured mirror directory
/// (Dropbox, OneDrive, Synology Drive, etc.). Never blocks the primary
/// write path. Timeout at 5 seconds per copy — sync services can lock
/// files during indexing, and we cannot stall the service waiting.
/// </summary>
public sealed class MirrorLogger
{
    private readonly string _mirrorDirectory;
    private int _consecutiveFailures;

    private const int TimeoutMs = 5000;
    private const int MaxConsecutiveFailures = 10;

    public MirrorLogger(string mirrorDirectory)
    {
        _mirrorDirectory = mirrorDirectory;
    }

    public bool IsEnabled => !string.IsNullOrWhiteSpace(_mirrorDirectory);

    /// <summary>
    /// Copy a file to the mirror directory. Fire-and-forget — failures
    /// are logged locally but never propagated to the caller.
    /// </summary>
    public void EnqueueCopy(string sourceFile)
    {
        if (!IsEnabled) return;
        if (_consecutiveFailures >= MaxConsecutiveFailures) return;

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
    }

    /// <summary>
    /// Reset the failure counter. Call when settings change or on periodic health check.
    /// </summary>
    public void ResetFailures()
    {
        Interlocked.Exchange(ref _consecutiveFailures, 0);
    }
}
