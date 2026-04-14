using RAMWatch.Core.Models;

namespace RAMWatch.Service.Services;

/// <summary>
/// Checks Windows component store integrity. Phase 1: CBS.log parsing only.
/// SFC/DISM execution deferred to Phase 5 (requires careful process management).
/// </summary>
public sealed class IntegrityChecker
{
    private readonly Lock _lock = new();
    private IntegrityState _state = new(0, IntegrityCheckStatus.NotRun, IntegrityCheckStatus.NotRun);

    public IntegrityState State
    {
        get { lock (_lock) { return _state; } }
    }

    /// <summary>
    /// Scan the tail of CBS.log for corruption markers.
    /// Called on each refresh cycle. Pure string processing, sub-millisecond.
    /// </summary>
    public void ScanCbsLog()
    {
        try
        {
            string cbsLogPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                "Logs", "CBS", "CBS.log");

            if (!File.Exists(cbsLogPath))
            {
                lock (_lock) { _state = _state with { CbsCorruptionCount = 0 }; }
                return;
            }

            int count = CountCbsCorruption(cbsLogPath);
            lock (_lock) { _state = _state with { CbsCorruptionCount = count }; }
        }
        catch
        {
            // CBS.log may be locked by Windows during rotation — skip this cycle
        }
    }

    /// <summary>
    /// Count corruption markers in CBS.log. Extracted as a testable pure function.
    /// Reads the last 64KB of the file to avoid scanning the entire log.
    /// </summary>
    internal static int CountCbsCorruption(string cbsLogPath)
    {
        return CountCbsCorruptionInText(ReadTail(cbsLogPath, 65536));
    }

    /// <summary>
    /// Count corruption markers in CBS.log text. Pure function for testing.
    /// </summary>
    internal static int CountCbsCorruptionInText(string text)
    {
        if (string.IsNullOrEmpty(text))
            return 0;

        int count = 0;
        // Known CBS.log corruption markers
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Contains("store corruption", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Contains("component store is repairable", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Contains("Manifest hash mismatch", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Contains("payloads cannot be found", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Contains("component was not remapped", StringComparison.OrdinalIgnoreCase))
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>
    /// Parse SFC output text into a status. Pure function for testing.
    /// </summary>
    internal static IntegrityCheckStatus ParseSfcOutput(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return IntegrityCheckStatus.Unknown;

        if (output.Contains("did not find any integrity violations", StringComparison.OrdinalIgnoreCase))
            return IntegrityCheckStatus.Clean;

        if (output.Contains("found corrupt files and successfully repaired", StringComparison.OrdinalIgnoreCase))
            return IntegrityCheckStatus.CorruptionRepaired;

        if (output.Contains("found corrupt files but was unable to fix", StringComparison.OrdinalIgnoreCase))
            return IntegrityCheckStatus.CorruptionFound;

        if (output.Contains("could not perform the requested operation", StringComparison.OrdinalIgnoreCase))
            return IntegrityCheckStatus.Failed;

        return IntegrityCheckStatus.Unknown;
    }

    /// <summary>
    /// Parse DISM output text into a status. Pure function for testing.
    /// </summary>
    internal static IntegrityCheckStatus ParseDismOutput(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return IntegrityCheckStatus.Unknown;

        if (output.Contains("No component store corruption detected", StringComparison.OrdinalIgnoreCase))
            return IntegrityCheckStatus.Clean;

        if (output.Contains("The component store is repairable", StringComparison.OrdinalIgnoreCase))
            return IntegrityCheckStatus.CorruptionFound;

        if (output.Contains("The restore operation completed successfully", StringComparison.OrdinalIgnoreCase))
            return IntegrityCheckStatus.CorruptionRepaired;

        return IntegrityCheckStatus.Unknown;
    }

    private static string ReadTail(string path, int maxBytes)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (stream.Length > maxBytes)
            stream.Seek(-maxBytes, SeekOrigin.End);

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
