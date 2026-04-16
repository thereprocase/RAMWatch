using System.Text.Json;
using RAMWatch.Core;
using RAMWatch.Core.Models;

namespace RAMWatch.Service.Services;

/// <summary>
/// Detects drift in auto-trained timing values by maintaining a rolling window
/// of the last 20 boot sessions. For each timing marked Auto in the designation
/// map, computes the historical mode (most-common value, ties broken by age —
/// the older first-seen value wins) and emits a DriftEvent when the current
/// boot's value deviates from that mode.
///
/// Drift is checked against the pre-existing window; the current boot's values
/// are added to the window only after the check, so a brand-new drift shows up
/// immediately rather than being absorbed into its own baseline.
/// </summary>
public sealed class DriftDetector : IDisposable
{
    private const int WindowSize = 20;
    private const int MinBootsForDrift = 3;

    private readonly string _windowPath;
    private readonly Lock _lock = new();
    private DriftWindow _window = new();

    public DriftDetector(string dataDirectory)
    {
        _windowPath = Path.Combine(dataDirectory, "drift_window.json");
        Directory.CreateDirectory(dataDirectory);
    }

    /// <summary>
    /// Load the persisted rolling window from disk.
    /// Call once on service startup before the first CheckForDrift call.
    /// </summary>
    public void LoadWindow()
    {
        lock (_lock)
        {
            if (!File.Exists(_windowPath))
            {
                _window = new DriftWindow();
                return;
            }

            try
            {
                string json = File.ReadAllText(_windowPath);
                var loaded = JsonSerializer.Deserialize(json, RamWatchJsonContext.Default.DriftWindow);
                _window = loaded ?? new DriftWindow();
            }
            catch
            {
                // Corrupt file — start fresh. We lose history but gain correctness.
                _window = new DriftWindow();
            }
        }
    }

    /// <summary>
    /// Check the current boot's timing values against the historical window.
    /// Only timings designated Auto participate. Returns one DriftEvent per
    /// drifted timing. After checking, appends the current boot to the window
    /// and persists.
    /// </summary>
    public List<DriftEvent> CheckForDrift(TimingSnapshot current, DesignationMap designations)
    {
        lock (_lock)
        {
            var events = new List<DriftEvent>();

            if (_window.Boots.Count >= MinBootsForDrift)
            {
                foreach (var (name, value) in ExtractAutoTimings(current, designations))
                {
                    var history = CollectHistory(name);
                    if (history.Count == 0) continue;

                    var counts = BuildCounts(history);
                    int mode = ComputeMode(history, counts);
                    int actual = value;

                    if (actual != mode)
                    {
                        // Re-use the counts dictionary built for mode computation
                        // rather than iterating history twice with LINQ.
                        int bootsAtMode   = counts.GetValueOrDefault(mode);
                        int bootsAtActual = counts.GetValueOrDefault(actual);

                        // Stability ratio: fraction of the window that matches the mode.
                        double stabilityRatio = (double)bootsAtMode / _window.Boots.Count;

                        events.Add(new DriftEvent
                        {
                            Timestamp          = current.Timestamp,
                            BootId             = current.BootId,
                            TimingName         = name,
                            ExpectedValue      = mode,
                            ActualValue        = actual,
                            BootsAtExpected    = bootsAtMode,
                            BootsAtActual      = bootsAtActual,
                            WindowBootCount    = _window.Boots.Count,
                            WindowStabilityRatio = stabilityRatio
                        });
                    }
                }
            }

            // Add current boot to window AFTER checking, so its values don't
            // inflate the baseline during the check above.
            AppendToWindow(current);
            SaveWindow();

            return events;
        }
    }

    /// <summary>
    /// Return the current rolling window contents. Primarily for testing.
    /// </summary>
    public IReadOnlyList<BootEntry> GetWindow()
    {
        lock (_lock)
        {
            return _window.Boots.AsReadOnly();
        }
    }

    // -----------------------------------------------------------------------
    // Private helpers
    // -----------------------------------------------------------------------

    /// <summary>
    /// Extract integer timing values for all timings designated Auto.
    /// Returns name/value pairs for every Auto field present in the snapshot.
    /// </summary>
    private static IEnumerable<(string Name, int Value)> ExtractAutoTimings(
        TimingSnapshot snapshot, DesignationMap designations)
    {
        foreach (var (name, desig) in designations.Designations)
        {
            if (desig != TimingDesignation.Auto) continue;

            int? value = GetTimingValue(snapshot, name);
            if (value.HasValue)
                yield return (name, value.Value);
        }
    }

    /// <summary>
    /// Collect the historical values for a named timing across all window entries.
    /// Entries are in insertion order (oldest first).
    /// </summary>
    private List<int> CollectHistory(string timingName)
    {
        var result = new List<int>(_window.Boots.Count);
        foreach (var entry in _window.Boots)
        {
            if (entry.Values.TryGetValue(timingName, out int v))
                result.Add(v);
        }
        return result;
    }

    /// <summary>
    /// Build a value-to-count dictionary from a history list.
    /// Called before ComputeMode so both can share the same dictionary.
    /// </summary>
    private static Dictionary<int, int> BuildCounts(List<int> values)
    {
        var counts = new Dictionary<int, int>(values.Count);
        foreach (int v in values)
            counts[v] = counts.GetValueOrDefault(v) + 1;
        return counts;
    }

    /// <summary>
    /// Compute the mode of a list of values given a pre-built counts dictionary.
    /// Ties are broken by first-seen order: if two values appear equally often,
    /// the one that appeared earliest in the window wins. This means a long-stable
    /// value is not displaced by a newer equally-frequent value.
    /// </summary>
    private static int ComputeMode(List<int> values, Dictionary<int, int> counts)
    {
        // values is ordered oldest-first; track the first index each distinct
        // value appears at so ties resolve to the older value.
        var firstIndex = new Dictionary<int, int>(counts.Count);
        for (int i = 0; i < values.Count; i++)
        {
            int v = values[i];
            if (!firstIndex.ContainsKey(v))
                firstIndex[v] = i;
        }

        // Pick the value with the highest count; break ties by smallest firstIndex.
        int best      = values[0];
        int bestCount = 0;
        int bestFirst = int.MaxValue;

        foreach (var (v, count) in counts)
        {
            if (count > bestCount || (count == bestCount && firstIndex[v] < bestFirst))
            {
                best      = v;
                bestCount = count;
                bestFirst = firstIndex[v];
            }
        }

        return best;
    }

    private void AppendToWindow(TimingSnapshot snapshot)
    {
        var entry = new BootEntry
        {
            BootId    = snapshot.BootId,
            Timestamp = snapshot.Timestamp,
            Values    = ExtractAllIntTimings(snapshot)
        };

        _window.Boots.Add(entry);

        // Trim to window size — drop the oldest entries.
        while (_window.Boots.Count > WindowSize)
            _window.Boots.RemoveAt(0);
    }

    /// <summary>
    /// Extract every integer timing field from a snapshot into a name-value map.
    /// This is what gets stored in the rolling window; storing all fields means we
    /// don't need to re-examine the raw snapshot later when designations change.
    /// Fields are sourced from TimingSnapshotFields so adding a new field to the
    /// helper automatically propagates here without touching this method.
    /// </summary>
    private static Dictionary<string, int> ExtractAllIntTimings(TimingSnapshot s)
    {
        var dict = new Dictionary<string, int>(
            TimingSnapshotFields.Clocks.Length +
            TimingSnapshotFields.Timings.Length +
            TimingSnapshotFields.Phy.Length +
            TimingSnapshotFields.Booleans.Length);

        foreach (var (name, get) in TimingSnapshotFields.Clocks)   dict[name] = get(s);
        foreach (var (name, get) in TimingSnapshotFields.Timings)  dict[name] = get(s);
        foreach (var (name, get) in TimingSnapshotFields.Phy)      dict[name] = get(s);
        foreach (var (name, get) in TimingSnapshotFields.Booleans) dict[name] = get(s) ? 1 : 0;

        return dict;
    }

    /// <summary>
    /// Map a timing name string to the integer value on the snapshot.
    /// Returns null for names that don't correspond to an integer field
    /// (callers should skip nulls). Booleans are projected to 0/1.
    /// Dispatch delegated to TimingSnapshotFields.GetIntField.
    /// </summary>
    private static int? GetTimingValue(TimingSnapshot s, string name)
        => TimingSnapshotFields.GetIntField(s, name);

    private void SaveWindow()
    {
        string dir = Path.GetDirectoryName(_windowPath)!;
        string tempPath = Path.Combine(dir, $"drift_window.{Guid.NewGuid():N}.tmp");

        try
        {
            string json = JsonSerializer.Serialize(_window, RamWatchJsonContext.Default.DriftWindow);
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, _windowPath, overwrite: true);
        }
        catch
        {
            try { File.Delete(tempPath); } catch { }
            // Non-fatal: in-memory window remains valid for this session.
        }
    }

    public void Dispose()
    {
        // Nothing to release — no file handles are held open.
    }
}

// DriftWindow and BootEntry are defined in RAMWatch.Core.Models.TuningJournal
// and registered in RamWatchJsonContext. They live in Core so the GUI can
// read them via IPC without depending on the Service assembly.
