using System.Text.Json;
using RAMWatch.Core;
using RAMWatch.Core.Models;

namespace RAMWatch.Service.Services;

/// <summary>
/// Persists timing snapshots to %ProgramData%\RAMWatch\snapshots.json.
/// Service is the sole writer. Atomic write-to-temp-then-rename (B7).
/// Missing or corrupt file on startup → empty list, no crash.
/// </summary>
public sealed class SnapshotJournal
{
    // Hard cap prevents unbounded disk growth if the service runs for years.
    // Oldest entries are evicted first when the cap is reached.
    private const int MaxSnapshots = 1000;

    private readonly string _path;
    private readonly Lock _lock = new();
    private List<TimingSnapshot> _snapshots;

    public SnapshotJournal(string? dataDirectory = null)
    {
        string dir = dataDirectory ?? DataDirectory.BasePath;
        _path = Path.Combine(dir, "snapshots.json");
        _snapshots = new List<TimingSnapshot>();
    }

    /// <summary>
    /// Load persisted snapshots from disk. Called once on service startup.
    /// Missing or corrupt file produces an empty list — never throws.
    /// </summary>
    public void Load()
    {
        lock (_lock)
        {
            if (!File.Exists(_path))
            {
                _snapshots = new List<TimingSnapshot>();
                return;
            }

            try
            {
                string json = File.ReadAllText(_path);
                var loaded = JsonSerializer.Deserialize(json, RamWatchJsonContext.Default.ListTimingSnapshot);
                _snapshots = loaded ?? new List<TimingSnapshot>();
            }
            catch (Exception)
            {
                // Corrupt or unreadable — empty list, service keeps running.
                _snapshots = new List<TimingSnapshot>();
            }
        }
    }

    /// <summary>
    /// Add a snapshot and persist the full list atomically.
    /// Overwrites any existing snapshot with the same SnapshotId.
    /// Evicts oldest entries when the list exceeds MaxSnapshots.
    /// </summary>
    public void Save(TimingSnapshot snapshot)
    {
        lock (_lock)
        {
            // Replace if same ID already present (e.g. label update), otherwise append.
            int idx = _snapshots.FindIndex(s => s.SnapshotId == snapshot.SnapshotId);
            if (idx >= 0)
                _snapshots[idx] = snapshot;
            else
                _snapshots.Add(snapshot);

            // Evict oldest entries to stay within cap.
            while (_snapshots.Count > MaxSnapshots)
                _snapshots.RemoveAt(0);

            Persist();
        }
    }

    /// <summary>
    /// All saved snapshots in insertion order.
    /// </summary>
    public List<TimingSnapshot> GetAll()
    {
        lock (_lock)
        {
            return new List<TimingSnapshot>(_snapshots);
        }
    }

    /// <summary>
    /// Look up a snapshot by its SnapshotId. Returns null if not found.
    /// </summary>
    public TimingSnapshot? GetById(string snapshotId)
    {
        lock (_lock)
        {
            return _snapshots.Find(s => s.SnapshotId == snapshotId);
        }
    }

    /// <summary>
    /// Remove the snapshot with the given SnapshotId and persist.
    /// Returns true when an entry was found and removed; false when not found.
    /// </summary>
    public bool DeleteById(string snapshotId)
    {
        lock (_lock)
        {
            int idx = _snapshots.FindIndex(s => s.SnapshotId == snapshotId);
            if (idx < 0)
                return false;

            _snapshots.RemoveAt(idx);
            Persist();
            return true;
        }
    }

    /// <summary>
    /// Update the label of the snapshot with the given SnapshotId and persist.
    /// Returns true when an entry was found and updated; false when not found.
    /// NewLabel is truncated to 256 characters.
    /// </summary>
    public bool RenameById(string snapshotId, string newLabel)
    {
        lock (_lock)
        {
            int idx = _snapshots.FindIndex(s => s.SnapshotId == snapshotId);
            if (idx < 0)
                return false;

            string label = newLabel is { Length: > 256 } ? newLabel[..256] : newLabel;
            _snapshots[idx] = _snapshots[idx].WithIdAndLabel(_snapshots[idx].SnapshotId, label);
            Persist();
            return true;
        }
    }

    // Write-to-temp-then-rename. Caller must hold _lock.
    private void Persist()
    {
        string json = JsonSerializer.Serialize(_snapshots, RamWatchJsonContext.Default.ListTimingSnapshot);

        string dir = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(dir);
        string tempPath = Path.Combine(dir, $"snapshots.{Guid.NewGuid():N}.tmp");

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
