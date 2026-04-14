using Xunit;
using RAMWatch.Core.Models;
using RAMWatch.Service.Services;

namespace RAMWatch.Tests;

public class SnapshotJournalTests : IDisposable
{
    private readonly string _tempDir;

    public SnapshotJournalTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"ramwatch-sj-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    // ── Save / Load round-trip ────────────────────────────────────────────────

    [Fact]
    public void Save_ThenLoad_RoundTrips()
    {
        var journal = new SnapshotJournal(_tempDir);
        journal.Load();

        var snap = MakeSnapshot("snap-001", "Boot A");
        journal.Save(snap);

        // Fresh instance simulates a service restart.
        var journal2 = new SnapshotJournal(_tempDir);
        journal2.Load();
        var all = journal2.GetAll();

        Assert.Single(all);
        Assert.Equal("snap-001", all[0].SnapshotId);
        Assert.Equal("Boot A", all[0].Label);
        Assert.Equal(3200, all[0].MemClockMhz);
    }

    [Fact]
    public void Save_MultipleSnapshots_OrderPreserved()
    {
        var journal = new SnapshotJournal(_tempDir);
        journal.Load();

        journal.Save(MakeSnapshot("snap-001", "First"));
        journal.Save(MakeSnapshot("snap-002", "Second"));
        journal.Save(MakeSnapshot("snap-003", "Third"));

        var all = journal.GetAll();

        Assert.Equal(3, all.Count);
        Assert.Equal("snap-001", all[0].SnapshotId);
        Assert.Equal("snap-002", all[1].SnapshotId);
        Assert.Equal("snap-003", all[2].SnapshotId);
    }

    // ── GetById ───────────────────────────────────────────────────────────────

    [Fact]
    public void GetById_ExistingId_ReturnsCorrectSnapshot()
    {
        var journal = new SnapshotJournal(_tempDir);
        journal.Load();

        journal.Save(MakeSnapshot("snap-aaa", "Alpha"));
        journal.Save(MakeSnapshot("snap-bbb", "Beta"));

        var result = journal.GetById("snap-bbb");

        Assert.NotNull(result);
        Assert.Equal("Beta", result.Label);
    }

    [Fact]
    public void GetById_UnknownId_ReturnsNull()
    {
        var journal = new SnapshotJournal(_tempDir);
        journal.Load();

        journal.Save(MakeSnapshot("snap-001", "Only one"));

        Assert.Null(journal.GetById("snap-does-not-exist"));
    }

    // ── Missing / corrupt file ────────────────────────────────────────────────

    [Fact]
    public void Load_MissingFile_ReturnsEmptyList()
    {
        var journal = new SnapshotJournal(_tempDir);
        journal.Load();

        Assert.Empty(journal.GetAll());
    }

    [Fact]
    public void Load_CorruptFile_ReturnsEmptyList_DoesNotThrow()
    {
        File.WriteAllText(Path.Combine(_tempDir, "snapshots.json"), "{ not valid json !!!");

        var journal = new SnapshotJournal(_tempDir);
        journal.Load();

        Assert.Empty(journal.GetAll());
    }

    // ── Persistence across restarts ───────────────────────────────────────────

    [Fact]
    public void Snapshots_SurviveServiceRestart()
    {
        var journal1 = new SnapshotJournal(_tempDir);
        journal1.Load();
        journal1.Save(MakeSnapshot("snap-001", "Before restart"));
        journal1.Save(MakeSnapshot("snap-002", "Also before"));

        var journal2 = new SnapshotJournal(_tempDir);
        journal2.Load();
        var restored = journal2.GetAll();

        Assert.Equal(2, restored.Count);
        Assert.Equal("Before restart", restored[0].Label);
        Assert.Equal("Also before", restored[1].Label);
    }

    // ── Deduplication by SnapshotId ───────────────────────────────────────────

    [Fact]
    public void Save_SameId_ReplacesExistingEntry()
    {
        var journal = new SnapshotJournal(_tempDir);
        journal.Load();

        journal.Save(MakeSnapshot("snap-001", "Original label"));

        // Same ID, different label — should update in place, not append.
        var updated = MakeSnapshot("snap-001", "Updated label");
        journal.Save(updated);

        var all = journal.GetAll();

        Assert.Single(all);
        Assert.Equal("Updated label", all[0].Label);
    }

    // ── Auto-save flag logic ──────────────────────────────────────────────────

    [Fact]
    public void AutoSave_FirstBootFlag_SavesOnlyOnce()
    {
        // The flag lives in RamWatchService, not in SnapshotJournal, but
        // the journal behaviour it relies on — Save() adding a new entry and
        // the list count growing — is tested here as its precondition.
        var journal = new SnapshotJournal(_tempDir);
        journal.Load();

        bool autoSavedThisBoot = false;

        void SimulateTimingRead(TimingSnapshot snapshot)
        {
            if (!autoSavedThisBoot)
            {
                autoSavedThisBoot = true;
                var autoSnap = snapshot.WithIdAndLabel(
                    "auto-boot-001",
                    $"Auto {DateTime.UtcNow.ToLocalTime():yyyy-MM-dd HH:mm}");
                journal.Save(autoSnap);
            }
        }

        var snap = MakeSnapshot("snap-live", "live");
        SimulateTimingRead(snap);
        SimulateTimingRead(snap);  // second call — flag already set
        SimulateTimingRead(snap);  // third call — flag already set

        var all = journal.GetAll();

        Assert.Single(all);                          // only one auto-save, not three
        Assert.Equal("auto-boot-001", all[0].SnapshotId);
        Assert.StartsWith("Auto ", all[0].Label);
    }

    // ── Atomic writes ─────────────────────────────────────────────────────────

    [Fact]
    public void Save_IsAtomic_NoTempFilesRemain()
    {
        var journal = new SnapshotJournal(_tempDir);
        journal.Load();

        journal.Save(MakeSnapshot("snap-001", "Clean"));

        var tmpFiles = Directory.GetFiles(_tempDir, "*.tmp");
        Assert.Empty(tmpFiles);
    }

    // ── GetAll returns a copy ─────────────────────────────────────────────────

    [Fact]
    public void GetAll_ReturnsCopy_MutatingResultDoesNotAffectJournal()
    {
        var journal = new SnapshotJournal(_tempDir);
        journal.Load();

        journal.Save(MakeSnapshot("snap-001", "A"));
        journal.Save(MakeSnapshot("snap-002", "B"));

        var copy = journal.GetAll();
        copy.Clear();

        // Journal internal list should still have two entries.
        Assert.Equal(2, journal.GetAll().Count);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static TimingSnapshot MakeSnapshot(string id, string label)
    {
        return new TimingSnapshot
        {
            SnapshotId  = id,
            Timestamp   = DateTime.UtcNow,
            BootId      = "boot_0414_0900",
            Label       = label,
            MemClockMhz = 3200,
            FclkMhz     = 1600,
            CL          = 16,
            RCDRD       = 18,
            RCDWR       = 12,
            RP          = 18,
            RAS         = 36,
        };
    }
}
