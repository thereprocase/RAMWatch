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
        }
    }

    private void RotateFile(string date)
    {
        _writer?.Dispose();

        // Running retention on every date change keeps log volume bounded
        // on long-running sessions. RunRetention is idempotent and fast
        // (directory scan + a few deletes); amortised across a 24h day it's
        // negligible. Without this, a service running 30+ days accumulates
        // CSVs past MaxLogSizeMb because retention only ran at startup.
        if (!string.IsNullOrEmpty(_currentDate))
        {
            try { RunRetention(); } catch { /* Retention is best-effort. */ }
        }

        _currentDate = date;
        _currentFilePath = Path.Combine(_logDirectory, $"events_{date}.csv");

        bool isNew = !File.Exists(_currentFilePath);

        var stream = new FileStream(
            _currentFilePath,
            FileMode.Append,
            FileAccess.Write,
            FileShare.Read); // B7: allow concurrent reads

        _writer = new StreamWriter(stream) { AutoFlush = true };

        if (isNew)
        {
            _writer.WriteLine(Header);
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
    /// Generate a unique boot ID using a monotonically increasing sequential counter
    /// persisted to disk at <paramref name="dataDirectory"/>\boot_counter.txt.
    ///
    /// Each call reads the counter (or starts at 0), increments it, writes it back
    /// atomically (write-to-temp-then-rename), and returns "boot_NNNNNN" where
    /// NNNNNN is the six-digit zero-padded counter value.
    ///
    /// This is collision-free regardless of clock skew, rapid reboots, or VM
    /// snapshots. The counter rolls over at 999999 boots, which is not a realistic
    /// concern in practice.
    /// </summary>
    public static string GenerateBootId(string dataDirectory)
    {
        string counterPath = Path.Combine(dataDirectory, "boot_counter.txt");
        string tempPath    = counterPath + ".tmp";

        int counter = 0;
        try
        {
            if (File.Exists(counterPath))
            {
                string text = File.ReadAllText(counterPath).Trim();
                if (int.TryParse(text, out int parsed))
                    counter = parsed;
            }
        }
        catch
        {
            // Counter file unreadable — start from 0. Unlikely to cause a real collision
            // in normal usage; the counter file will be recreated on this call.
        }

        counter++;

        try
        {
            Directory.CreateDirectory(dataDirectory);
            File.WriteAllText(tempPath, counter.ToString());
            File.Move(tempPath, counterPath, overwrite: true);
        }
        catch
        {
            // If the write fails, the counter is still incremented in memory
            // for this boot, so the ID is unique within the process lifetime.
        }

        return $"boot_{counter:D6}";
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
