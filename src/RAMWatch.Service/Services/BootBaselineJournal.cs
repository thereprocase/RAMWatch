using System.Text.Json;
using RAMWatch.Core;
using RAMWatch.Core.Models;

namespace RAMWatch.Service.Services;

/// <summary>
/// Tracks per-source event counts across boots. Maintains a rolling journal
/// of the last 50 boots and computes baseline statistics (mean excluding
/// IQR outliers) so the GUI can color-code counts relative to normal.
/// </summary>
public sealed class BootBaselineJournal
{
    private readonly string _journalPath;
    private readonly Lock _lock = new();
    private List<BootCountEntry> _entries = [];
    private const int MaxBoots = 50;

    public BootBaselineJournal(string dataDirectory)
    {
        _journalPath = Path.Combine(dataDirectory, "boot_baselines.json");
    }

    /// <summary>
    /// Load the journal from disk. Call once on service startup.
    /// </summary>
    public void Load()
    {
        lock (_lock)
        {
            if (!File.Exists(_journalPath))
            {
                _entries = [];
                return;
            }

            try
            {
                string json = File.ReadAllText(_journalPath);
                var loaded = JsonSerializer.Deserialize(json, RamWatchJsonContext.Default.ListBootCountEntry);
                _entries = loaded ?? [];
            }
            catch
            {
                _entries = [];
            }
        }
    }

    /// <summary>
    /// Record the final event counts for the current boot. Called on service shutdown.
    /// Trims the journal to the most recent 50 boots.
    /// </summary>
    public void RecordBoot(string bootId, List<ErrorSource> errorSources)
    {
        lock (_lock)
        {
            // Don't record duplicate boot IDs (e.g., service restart within same boot).
            if (_entries.Any(e => e.BootId == bootId))
                return;

            var counts = new Dictionary<string, int>();
            foreach (var src in errorSources)
                counts[src.Name] = src.Count;

            _entries.Add(new BootCountEntry
            {
                BootId = bootId,
                Timestamp = DateTime.UtcNow,
                Counts = counts
            });

            // Trim to rolling window.
            if (_entries.Count > MaxBoots)
                _entries.RemoveRange(0, _entries.Count - MaxBoots);

            Save();
        }
    }

    /// <summary>
    /// Compute baseline means per source, excluding IQR outliers.
    /// Returns a dictionary of source name → baseline mean.
    /// Only includes sources that appear in at least 3 boots.
    /// </summary>
    public Dictionary<string, double> ComputeBaselines()
    {
        lock (_lock)
        {
            if (_entries.Count < 3)
                return new Dictionary<string, double>();

            // Collect all source names across all boots.
            var allSources = new HashSet<string>();
            foreach (var entry in _entries)
                foreach (var key in entry.Counts.Keys)
                    allSources.Add(key);

            var baselines = new Dictionary<string, double>();

            foreach (var source in allSources)
            {
                var values = new List<double>();
                foreach (var entry in _entries)
                {
                    if (entry.Counts.TryGetValue(source, out int count))
                        values.Add(count);
                    else
                        values.Add(0);
                }

                if (values.Count < 3)
                    continue;

                baselines[source] = MeanExcludingOutliers(values);
            }

            return baselines;
        }
    }

    /// <summary>
    /// Compute mean of values after excluding IQR outliers.
    /// Values below Q1 - 1.5*IQR or above Q3 + 1.5*IQR are excluded.
    /// </summary>
    internal static double MeanExcludingOutliers(List<double> values)
    {
        if (values.Count == 0) return 0;
        if (values.Count < 4) return values.Average();

        var sorted = values.OrderBy(v => v).ToList();
        int n = sorted.Count;

        double q1 = Percentile(sorted, 0.25);
        double q3 = Percentile(sorted, 0.75);
        double iqr = q3 - q1;

        double lower = q1 - 1.5 * iqr;
        double upper = q3 + 1.5 * iqr;

        var filtered = sorted.Where(v => v >= lower && v <= upper).ToList();
        return filtered.Count > 0 ? filtered.Average() : sorted.Average();
    }

    private static double Percentile(List<double> sorted, double p)
    {
        double idx = p * (sorted.Count - 1);
        int lower = (int)Math.Floor(idx);
        int upper = (int)Math.Ceiling(idx);
        if (lower == upper) return sorted[lower];
        double frac = idx - lower;
        return sorted[lower] * (1 - frac) + sorted[upper] * frac;
    }

    private void Save()
    {
        string dir = Path.GetDirectoryName(_journalPath)!;
        string tempPath = Path.Combine(dir, $"boot_baselines.{Guid.NewGuid():N}.tmp");

        try
        {
            string json = JsonSerializer.Serialize(_entries, RamWatchJsonContext.Default.ListBootCountEntry);
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, _journalPath, overwrite: true);
        }
        catch
        {
            try { File.Delete(tempPath); } catch { }
        }
    }
}
