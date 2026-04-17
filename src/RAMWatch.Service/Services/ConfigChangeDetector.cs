using System.Text.Json;
using RAMWatch.Core;
using RAMWatch.Core.Models;

namespace RAMWatch.Service.Services;

/// <summary>
/// Detects timing configuration changes between boots by comparing
/// the current TimingSnapshot against the one persisted from the previous boot.
/// Persists the last-seen snapshot to disk using atomic write-temp-rename (B7).
/// Also maintains a journal of all detected changes in changes.json so the
/// StateAggregator can populate RecentChanges for the Timeline tab.
/// </summary>
public sealed class ConfigChangeDetector : IDisposable
{
    private readonly string _snapshotPath;
    private readonly string _changesPath;
    private readonly Lock _lock = new();
    private TimingSnapshot? _previous;
    private List<ConfigChange> _changes = new();

    public ConfigChangeDetector(string dataDirectory)
    {
        _snapshotPath = Path.Combine(dataDirectory, "last_snapshot.json");
        _changesPath  = Path.Combine(dataDirectory, "changes.json");
        Directory.CreateDirectory(dataDirectory);
    }

    /// <summary>
    /// Load the previously persisted snapshot from disk.
    /// Call once on service startup before the first DetectChanges call.
    /// </summary>
    public void LoadPrevious()
    {
        lock (_lock)
        {
            if (!File.Exists(_snapshotPath))
            {
                _previous = null;
                return;
            }

            try
            {
                string json = File.ReadAllText(_snapshotPath);
                _previous = JsonSerializer.Deserialize(json, RamWatchJsonContext.Default.TimingSnapshot);
            }
            catch
            {
                // Corrupt file — archive and treat as first boot; we'll
                // overwrite on next save.
                DataDirectory.ArchiveCorruptFile(_snapshotPath);
                _previous = null;
            }
        }
    }

    /// <summary>
    /// Load the persisted changes journal from disk.
    /// Call once on service startup, alongside LoadPrevious.
    /// Missing or corrupt file produces an empty list — never throws.
    /// </summary>
    public void LoadChanges()
    {
        lock (_lock)
        {
            if (!File.Exists(_changesPath))
            {
                _changes = new List<ConfigChange>();
                return;
            }

            try
            {
                string json = File.ReadAllText(_changesPath);
                var loaded = JsonSerializer.Deserialize(json, RamWatchJsonContext.Default.ListConfigChange);
                _changes = loaded ?? new List<ConfigChange>();
            }
            catch
            {
                // Corrupt file — archive so change history isn't silently
                // lost, then recover to empty list.
                DataDirectory.ArchiveCorruptFile(_changesPath);
                _changes = new List<ConfigChange>();
            }
        }
    }

    /// <summary>
    /// Compare current snapshot against the previous one.
    /// Returns a ConfigChange if any timing values differ, null if nothing changed
    /// or if this is the first boot (no previous snapshot).
    /// Always updates the persisted snapshot to current after comparison.
    /// </summary>
    /// <summary>
    /// Tolerance in MHz for clock fields (FCLK, UCLK, MemClock).
    /// SMU readback jitter of ±2-3 MHz is normal and not a config change.
    /// </summary>
    private const int ClockToleranceMhz = 5;

    public ConfigChange? DetectChanges(TimingSnapshot current)
    {
        lock (_lock)
        {
            // Incomplete hardware read — clocks haven't populated yet.
            // Don't update _previous or save; wait for a complete read so the
            // first real comparison has accurate clock values.
            if (current.FclkMhz == 0 || current.UclkMhz == 0)
                return null;

            TimingSnapshot? before = _previous;
            _previous = current;
            SaveSnapshot(current);

            if (before is null)
            {
                // First boot — establish baseline, no change to report.
                return null;
            }

            var deltas = BuildDeltas(before, current);
            if (deltas.Count == 0)
            {
                return null;
            }

            var change = new ConfigChange
            {
                ChangeId = Guid.NewGuid().ToString("N"),
                Timestamp = current.Timestamp,
                BootId = current.BootId,
                Changes = deltas,
                SnapshotBeforeId = before.SnapshotId,
                SnapshotAfterId = current.SnapshotId
            };

            _changes.Add(change);
            SaveChanges();

            return change;
        }
    }

    /// <summary>
    /// Delete a config change by its ChangeId. Returns true if found and removed.
    /// Persists the updated journal to disk.
    /// </summary>
    public bool DeleteById(string changeId)
    {
        lock (_lock)
        {
            int idx = _changes.FindIndex(c => c.ChangeId == changeId);
            if (idx < 0)
                return false;

            _changes.RemoveAt(idx);
            SaveChanges();
            return true;
        }
    }

    /// <summary>
    /// Returns the last <paramref name="count"/> detected changes in chronological order.
    /// Returns fewer entries when the journal holds less than count.
    /// </summary>
    public List<ConfigChange> GetRecentChanges(int count)
    {
        lock (_lock)
        {
            int take  = Math.Min(count, _changes.Count);
            int start = _changes.Count - take;
            // GetRange is O(count) rather than the O(n) enumeration of Skip+ToList.
            return _changes.GetRange(start, take);
        }
    }

    /// <summary>
    /// Explicit comparison of every named timing field.
    /// Reflection-free: the compiler sees every field name, so renames are caught at build time.
    /// </summary>
    private static Dictionary<string, TimingDelta> BuildDeltas(TimingSnapshot before, TimingSnapshot after)
    {
        var d = new Dictionary<string, TimingDelta>();

        // --- Clocks (with jitter tolerance, skip zero = incomplete read) ---
        CheckClock(d, "MemClockMhz", before.MemClockMhz, after.MemClockMhz);
        CheckClock(d, "FclkMhz",     before.FclkMhz,     after.FclkMhz);
        CheckClock(d, "UclkMhz",     before.UclkMhz,     after.UclkMhz);

        // --- Primaries ---
        Check(d, "CL",    before.CL,    after.CL);
        Check(d, "RCDRD", before.RCDRD, after.RCDRD);
        Check(d, "RCDWR", before.RCDWR, after.RCDWR);
        Check(d, "RP",    before.RP,    after.RP);
        Check(d, "RAS",   before.RAS,   after.RAS);
        Check(d, "RC",    before.RC,    after.RC);
        Check(d, "CWL",   before.CWL,   after.CWL);

        // --- tRFC group ---
        Check(d, "RFC",  before.RFC,  after.RFC);
        Check(d, "RFC2", before.RFC2, after.RFC2);
        Check(d, "RFC4", before.RFC4, after.RFC4);

        // --- Secondaries ---
        Check(d, "RRDS",     before.RRDS,     after.RRDS);
        Check(d, "RRDL",     before.RRDL,     after.RRDL);
        Check(d, "FAW",      before.FAW,      after.FAW);
        Check(d, "WTRS",     before.WTRS,     after.WTRS);
        Check(d, "WTRL",     before.WTRL,     after.WTRL);
        Check(d, "WR",       before.WR,       after.WR);
        Check(d, "RTP",      before.RTP,      after.RTP);
        Check(d, "RDRDSCL",  before.RDRDSCL,  after.RDRDSCL);
        Check(d, "WRWRSCL",  before.WRWRSCL,  after.WRWRSCL);

        // --- Turn-around ---
        Check(d, "RDRDSC", before.RDRDSC, after.RDRDSC);
        Check(d, "RDRDSD", before.RDRDSD, after.RDRDSD);
        Check(d, "RDRDDD", before.RDRDDD, after.RDRDDD);
        Check(d, "WRWRSC", before.WRWRSC, after.WRWRSC);
        Check(d, "WRWRSD", before.WRWRSD, after.WRWRSD);
        Check(d, "WRWRDD", before.WRWRDD, after.WRWRDD);
        Check(d, "RDWR",   before.RDWR,   after.RDWR);
        Check(d, "WRRD",   before.WRRD,   after.WRRD);

        // --- Misc ---
        Check(d, "REFI", before.REFI, after.REFI);
        Check(d, "CKE",  before.CKE,  after.CKE);
        Check(d, "STAG", before.STAG, after.STAG);
        Check(d, "MOD",  before.MOD,  after.MOD);
        Check(d, "MRD",  before.MRD,  after.MRD);

        // --- PHY ---
        Check(d, "PHYRDL_A", before.PHYRDL_A, after.PHYRDL_A);
        Check(d, "PHYRDL_B", before.PHYRDL_B, after.PHYRDL_B);

        // --- Controller config (booleans) ---
        CheckBool(d, "GDM",       before.GDM,       after.GDM);
        CheckBool(d, "Cmd2T",     before.Cmd2T,     after.Cmd2T);
        CheckBool(d, "PowerDown", before.PowerDown, after.PowerDown);

        // Voltages intentionally excluded: they are analog telemetry that varies
        // continuously and is not a "configuration" change.

        return d;
    }

    private static void Check(Dictionary<string, TimingDelta> d, string name, int before, int after)
    {
        if (before != after)
            d[name] = new TimingDelta(before.ToString(), after.ToString());
    }

    /// <summary>
    /// Clock-specific check: ignores zero values (incomplete reads) and
    /// differences within <see cref="ClockToleranceMhz"/> (SMU jitter).
    /// </summary>
    private static void CheckClock(Dictionary<string, TimingDelta> d, string name, int before, int after)
    {
        if (before == 0 || after == 0) return;
        if (Math.Abs(before - after) <= ClockToleranceMhz) return;
        d[name] = new TimingDelta(before.ToString(), after.ToString());
    }

    private static void CheckBool(Dictionary<string, TimingDelta> d, string name, bool before, bool after)
    {
        if (before != after)
            d[name] = new TimingDelta(before.ToString(), after.ToString());
    }

    private void SaveSnapshot(TimingSnapshot snapshot)
    {
        string dir = Path.GetDirectoryName(_snapshotPath)!;
        string tempPath = Path.Combine(dir, $"last_snapshot.{Guid.NewGuid():N}.tmp");

        try
        {
            string json = JsonSerializer.Serialize(snapshot, RamWatchJsonContext.Default.TimingSnapshot);
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, _snapshotPath, overwrite: true);
        }
        catch
        {
            try { File.Delete(tempPath); } catch { }
            // Persistence failure is non-fatal. The in-memory _previous is still valid
            // for the rest of this session; we'll try again on the next DetectChanges call.
        }
    }

    // Persist the full changes journal atomically. Caller must hold _lock.
    private void SaveChanges()
    {
        string dir = Path.GetDirectoryName(_changesPath)!;
        string tempPath = Path.Combine(dir, $"changes.{Guid.NewGuid():N}.tmp");

        try
        {
            string json = JsonSerializer.Serialize(_changes, RamWatchJsonContext.Default.ListConfigChange);
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, _changesPath, overwrite: true);
        }
        catch
        {
            try { File.Delete(tempPath); } catch { }
            // Persistence failure is non-fatal. The in-memory _changes list is still valid.
        }
    }

    public void Dispose()
    {
        // Nothing to release — no file handles are held open.
    }
}
