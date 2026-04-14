using System.Text.Json;
using RAMWatch.Core;
using RAMWatch.Core.Models;

namespace RAMWatch.Service.Services;

/// <summary>
/// Tracks the Last Known Good (LKG) timing configuration.
///
/// LKG = the most recent TimingSnapshot whose ActiveSnapshotId appears in a
/// ValidationResult where Passed == true and the metric meets a per-tool
/// threshold. A failing test never qualifies, no matter how high the coverage.
///
/// Default thresholds:
///   Karhu RAM Test  — MetricValue ≥ 1000 (coverage percent)
///   TM5             — MetricValue ≥ 25   (cycles)
///
/// Tools not in the threshold table never qualify. Add entries to
/// <see cref="LkgThresholds"/> to extend.
///
/// Persists the current LKG snapshot to %ProgramData%\RAMWatch\lkg.json
/// using atomic write-to-temp-then-rename (B7).
/// </summary>
public sealed class LkgTracker
{
    private readonly string _path;
    private readonly Lock _lock = new();
    private TimingSnapshot? _currentLkg;

    // Threshold table: TestTool (normalised to lower-case) → minimum MetricValue.
    // Only tools present here can qualify as LKG.
    public Dictionary<string, double> LkgThresholds { get; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["karhu"] = 1000.0,
        ["tm5"]   = 25.0,
    };

    public LkgTracker(string? dataDirectory = null)
    {
        string dir = dataDirectory ?? DataDirectory.BasePath;
        _path = Path.Combine(dir, "lkg.json");
    }

    /// <summary>
    /// The current LKG snapshot, or null if no validated config exists.
    /// </summary>
    public TimingSnapshot? CurrentLkg
    {
        get { lock (_lock) { return _currentLkg; } }
    }

    /// <summary>
    /// Load a previously persisted LKG snapshot from disk.
    /// Missing or corrupt file → null CurrentLkg, no crash.
    /// </summary>
    public void Load()
    {
        lock (_lock)
        {
            if (!File.Exists(_path))
            {
                _currentLkg = null;
                return;
            }

            try
            {
                string json = File.ReadAllText(_path);
                _currentLkg = JsonSerializer.Deserialize(json, RamWatchJsonContext.Default.TimingSnapshot);
            }
            catch (Exception)
            {
                // Corrupt or unreadable — no LKG until a new one is established.
                _currentLkg = null;
            }
        }
    }

    /// <summary>
    /// Scan <paramref name="results"/> for the most recent passing test that
    /// meets the threshold for its tool, look up the corresponding snapshot in
    /// <paramref name="snapshots"/>, and update CurrentLkg.
    ///
    /// "Most recent" is determined by ValidationResult.Timestamp descending.
    /// If no qualifying result exists, CurrentLkg becomes null.
    /// </summary>
    public void UpdateLkg(List<ValidationResult> results, List<TimingSnapshot> snapshots)
    {
        // Build a lookup so we don't scan the snapshot list repeatedly.
        var snapshotById = snapshots
            .Where(s => s.SnapshotId != null)
            .ToDictionary(s => s.SnapshotId, StringComparer.Ordinal);

        // Most recent qualifying result wins.
        TimingSnapshot? candidate = null;

        foreach (var result in results.OrderByDescending(r => r.Timestamp))
        {
            if (!MeetsThreshold(result))
                continue;

            if (result.ActiveSnapshotId == null)
                continue;

            if (!snapshotById.TryGetValue(result.ActiveSnapshotId, out var snapshot))
                continue;

            candidate = snapshot;
            break;
        }

        lock (_lock)
        {
            _currentLkg = candidate;

            if (_currentLkg != null)
                Persist();
            else
                // No qualifying result — remove stale LKG file so a restart
                // doesn't resurrect a now-invalidated snapshot.
                DeletePersistedLkg();
        }
    }

    // Returns true when the result passed and its tool + metric meet the threshold.
    // A result whose TestTool is not in the threshold table never qualifies.
    private bool MeetsThreshold(ValidationResult result)
    {
        if (!result.Passed)
            return false;

        if (!LkgThresholds.TryGetValue(result.TestTool, out double threshold))
            return false;

        return result.MetricValue >= threshold;
    }

    // Write-to-temp-then-rename. Caller must hold _lock.
    private void Persist()
    {
        string json = JsonSerializer.Serialize(_currentLkg, RamWatchJsonContext.Default.TimingSnapshot);

        string dir = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(dir);
        string tempPath = Path.Combine(dir, $"lkg.{Guid.NewGuid():N}.tmp");

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

    private void DeletePersistedLkg()
    {
        try
        {
            if (File.Exists(_path))
                File.Delete(_path);
        }
        catch
        {
            // Non-fatal: stale file left on disk is acceptable.
            // CurrentLkg is authoritative for the running service lifetime.
        }
    }
}
