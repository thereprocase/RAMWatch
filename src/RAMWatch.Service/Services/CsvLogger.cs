using RAMWatch.Core.Models;

namespace RAMWatch.Service.Services;

/// <summary>
/// Append-only daily CSV event logger. One file per day: events_YYYY-MM-DD.csv.
/// Uses FileShare.Read so the mirror logger (and external tools) can read concurrently.
/// Handles midnight rotation and log retention on startup.
/// </summary>
public sealed class CsvLogger : IDisposable
{
    private readonly string _logDirectory;
    private readonly int _retentionDays;
    private readonly int _maxSizeMb;
    private readonly Lock _lock = new();
    private StreamWriter? _writer;
    private string _currentDate = "";
    private string _currentFilePath = "";

    private const string Header = "timestamp,boot_id,source,category,severity,event_id,summary";

    /// <summary>
    /// The absolute path of the currently open CSV file, or empty string if no
    /// file has been opened yet (i.e. no events have been logged this session).
    /// Used by MirrorLogger to know which file to copy after each write.
    /// </summary>
    public string CurrentFilePath
    {
        get { lock (_lock) { return _currentFilePath; } }
    }

    public CsvLogger(string logDirectory, int retentionDays = 90, int maxSizeMb = 100)
    {
        _logDirectory = logDirectory;
        _retentionDays = retentionDays;
        _maxSizeMb = maxSizeMb;
        Directory.CreateDirectory(_logDirectory);
    }

    /// <summary>
    /// Run retention cleanup on startup: delete files older than retention period
    /// and enforce total size cap.
    /// </summary>
    public void RunRetention()
    {
        try
        {
            var cutoff = DateTime.UtcNow.AddDays(-_retentionDays);
            var files = Directory.GetFiles(_logDirectory, "events_*.csv")
                .Select(f => new FileInfo(f))
                .OrderBy(f => f.Name)
                .ToList();

            // Delete old files
            foreach (var file in files.Where(f => f.CreationTimeUtc < cutoff))
            {
                try { file.Delete(); } catch { }
            }

            // Enforce size cap — delete oldest until under limit
            files = Directory.GetFiles(_logDirectory, "events_*.csv")
                .Select(f => new FileInfo(f))
                .OrderBy(f => f.Name)
                .ToList();

            long totalBytes = files.Sum(f => f.Length);
            long maxBytes = (long)_maxSizeMb * 1024 * 1024;

            foreach (var file in files)
            {
                if (totalBytes <= maxBytes) break;
                long size = file.Length;
                try
                {
                    file.Delete();
                    totalBytes -= size;
                }
                catch { }
            }
        }
        catch
        {
            // Retention cleanup is best-effort
        }
    }

    /// <summary>
    /// Append an event to the daily CSV. Handles midnight rotation automatically.
    /// </summary>
    public void LogEvent(MonitoredEvent evt, string bootId)
    {
        lock (_lock)
        {
            string date = evt.Timestamp.ToString("yyyy-MM-dd");

            if (date != _currentDate)
            {
                RotateFile(date);
            }

            _writer?.WriteLine(FormatRow(evt, bootId));
            _writer?.Flush();
        }
    }

    private void RotateFile(string date)
    {
        _writer?.Dispose();
        _currentDate = date;
        _currentFilePath = Path.Combine(_logDirectory, $"events_{date}.csv");

        bool isNew = !File.Exists(_currentFilePath);

        var stream = new FileStream(
            _currentFilePath,
            FileMode.Append,
            FileAccess.Write,
            FileShare.Read); // B7: allow concurrent reads

        _writer = new StreamWriter(stream) { AutoFlush = false };

        if (isNew)
        {
            _writer.WriteLine(Header);
            _writer.Flush();
        }
    }

    /// <summary>
    /// Format a single CSV row. Quotes the summary field to handle commas.
    /// </summary>
    internal static string FormatRow(MonitoredEvent evt, string bootId)
    {
        var summary = CsvEscape(evt.Summary);
        return $"{evt.Timestamp:o},{bootId},{CsvEscape(evt.Source)},{evt.Category},{evt.Severity},{evt.EventId},{summary}";
    }

    /// <summary>
    /// CSV escape: wrap in quotes if the value contains comma, quote, or newline.
    /// Double any embedded quotes per RFC 4180.
    /// </summary>
    internal static string CsvEscape(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r'))
        {
            return $"\"{value.Replace("\"", "\"\"")}\"";
        }
        return value;
    }

    /// <summary>
    /// Generate a boot ID from the last boot time.
    /// Format: boot_MMDD_HHMM (minute granularity).
    ///
    /// WARNING: minute granularity means two boots in the same minute produce the same ID.
    /// Phase 3 drift detection uses boot IDs to index the 20-boot rolling window; a collision
    /// causes two distinct boot records to merge, silently corrupting drift calculations.
    /// Fix: either append seconds (boot_MMDD_HHMMss) or use a monotonic sequential counter
    /// persisted to disk (e.g. %ProgramData%\RAMWatch\boot_counter.txt, incremented atomically
    /// via write-temp-rename on each service start). The sequential counter survives clock skew
    /// and is trivially unique; seconds only reduce the collision window, not eliminate it.
    /// </summary>
    public static string GenerateBootId(DateTime bootTime)
    {
        return $"boot_{bootTime:MMdd_HHmm}";
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }
}
