using System.Text.Json;
using RAMWatch.Core;
using RAMWatch.Core.Models;

namespace RAMWatch.Service.Services;

/// <summary>
/// Persists stability test results to %ProgramData%\RAMWatch\tests.json.
/// Service is the sole writer. Atomic write-to-temp-then-rename (B7).
/// Missing or corrupt file on startup → empty list, no crash.
/// </summary>
public sealed class ValidationTestLogger
{
    // Hard cap prevents unbounded disk growth. Oldest results are evicted first.
    private const int MaxResults = 500;

    private readonly string _path;
    private readonly Lock _lock = new();
    private List<ValidationResult> _results;

    public ValidationTestLogger(string? dataDirectory = null)
    {
        string dir = dataDirectory ?? DataDirectory.BasePath;
        _path = Path.Combine(dir, "tests.json");
        _results = new List<ValidationResult>();
    }

    /// <summary>
    /// Load persisted results from disk. Called once on service startup.
    /// Missing or corrupt file produces an empty list — never throws.
    /// </summary>
    public void Load()
    {
        lock (_lock)
        {
            if (!File.Exists(_path))
            {
                _results = new List<ValidationResult>();
                return;
            }

            try
            {
                string json = File.ReadAllText(_path);
                var loaded = JsonSerializer.Deserialize(json, RamWatchJsonContext.Default.ListValidationResult);
                _results = loaded ?? new List<ValidationResult>();
            }
            catch (Exception)
            {
                // Corrupt or unreadable — empty list, service keeps running (architecture requirement).
                _results = new List<ValidationResult>();
            }
        }
    }

    /// <summary>
    /// Append a result and persist the full list atomically.
    /// Evicts oldest entries when the list exceeds MaxResults.
    /// </summary>
    public void LogResult(ValidationResult result)
    {
        lock (_lock)
        {
            _results.Add(result);

            // Evict oldest entries to stay within cap.
            while (_results.Count > MaxResults)
                _results.RemoveAt(0);

            Persist();
        }
    }

    /// <summary>
    /// All logged results in insertion order.
    /// </summary>
    public List<ValidationResult> GetResults()
    {
        lock (_lock)
        {
            return new List<ValidationResult>(_results);
        }
    }

    /// <summary>
    /// Last <paramref name="count"/> results in insertion order.
    /// Returns fewer if the list is shorter than count.
    /// </summary>
    public List<ValidationResult> GetRecentResults(int count)
    {
        lock (_lock)
        {
            int take  = Math.Min(count, _results.Count);
            int start = _results.Count - take;
            // GetRange is O(count) rather than the O(n) enumeration of Skip+ToList.
            return _results.GetRange(start, take);
        }
    }

    // Write-to-temp-then-rename. Caller must hold _lock.
    private void Persist()
    {
        string json = JsonSerializer.Serialize(_results, RamWatchJsonContext.Default.ListValidationResult);

        string dir = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(dir);
        string tempPath = Path.Combine(dir, $"tests.{Guid.NewGuid():N}.tmp");

        try
        {
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, _path, overwrite: true);
        }
        catch
        {
            try { File.Delete(tempPath); } catch { }
            throw;
        }
    }
}
