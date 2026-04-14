using Xunit;
using RAMWatch.Core.Models;
using RAMWatch.Service.Services;

namespace RAMWatch.Tests;

public class ConfigChangeDetectorTests : IDisposable
{
    private readonly string _tempDir;

    public ConfigChangeDetectorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"ramwatch-ccd-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static TimingSnapshot MakeSnapshot(
        string bootId = "boot_0414_0900",
        string? snapshotId = null,
        int cl = 18, int rcdrd = 18, int rcdwr = 18,
        int rfc = 312,
        bool gdm = false, bool cmd2t = false, bool powerDown = false) =>
        new TimingSnapshot
        {
            SnapshotId = snapshotId ?? Guid.NewGuid().ToString("N"),
            Timestamp  = DateTime.UtcNow,
            BootId     = bootId,
            CL    = cl,    RCDRD = rcdrd, RCDWR = rcdwr,
            RP    = 18,    RAS   = 36,    RC    = 54,    CWL  = 14,
            RFC   = rfc,   RFC2  = 200,   RFC4  = 100,
            RRDS  = 4,     RRDL  = 6,     FAW   = 16,
            WTRS  = 4,     WTRL  = 12,    WR    = 18,    RTP  = 10,
            RDRDSCL = 2,   WRWRSCL = 2,
            RDRDSC = 2,    RDRDSD = 6,    RDRDDD = 8,
            WRWRSC = 2,    WRWRSD = 6,    WRWRDD = 8,
            RDWR  = 14,    WRRD  = 2,
            REFI  = 65535, CKE   = 6,     STAG  = 2,     MOD  = 6,   MRD = 6,
            PHYRDL_A = 40, PHYRDL_B = 42,
            GDM = gdm, Cmd2T = cmd2t, PowerDown = powerDown,
            VSoc = 1.05, VDimm = 1.35
        };

    // -----------------------------------------------------------------------
    // Tests
    // -----------------------------------------------------------------------

    [Fact]
    public void IdenticalSnapshots_ReturnsNull()
    {
        using var detector = new ConfigChangeDetector(_tempDir);

        // Establish baseline — first call always returns null.
        var result1 = detector.DetectChanges(MakeSnapshot("boot_0414_0900"));
        Assert.Null(result1);

        // Second call with same parameter values.
        var result2 = detector.DetectChanges(MakeSnapshot("boot_0415_0900"));
        Assert.Null(result2);
    }

    [Fact]
    public void SingleFieldChange_CL_ReturnsChangeWithOneEntry()
    {
        using var detector = new ConfigChangeDetector(_tempDir);

        detector.DetectChanges(MakeSnapshot("boot_0414_0900", cl: 18));

        var change = detector.DetectChanges(MakeSnapshot("boot_0415_0900", cl: 16));

        Assert.NotNull(change);
        Assert.Single(change.Changes);
        Assert.True(change.Changes.ContainsKey("CL"));
        Assert.Equal("18", change.Changes["CL"].Before);
        Assert.Equal("16", change.Changes["CL"].After);
    }

    [Fact]
    public void MultipleFieldsChange_AllEntriesPresent()
    {
        using var detector = new ConfigChangeDetector(_tempDir);

        detector.DetectChanges(MakeSnapshot("boot_0414_0900", cl: 18, rcdrd: 18, rfc: 312));

        var change = detector.DetectChanges(MakeSnapshot("boot_0415_0900", cl: 16, rcdrd: 20, rfc: 400));

        Assert.NotNull(change);
        Assert.Equal(3, change.Changes.Count);
        Assert.True(change.Changes.ContainsKey("CL"));
        Assert.True(change.Changes.ContainsKey("RCDRD"));
        Assert.True(change.Changes.ContainsKey("RFC"));

        Assert.Equal("18", change.Changes["CL"].Before);
        Assert.Equal("16", change.Changes["CL"].After);
        Assert.Equal("312", change.Changes["RFC"].Before);
        Assert.Equal("400", change.Changes["RFC"].After);
    }

    [Fact]
    public void FirstBoot_NoPrevious_ReturnsNull()
    {
        // No LoadPrevious call and no prior DetectChanges — true first boot.
        using var detector = new ConfigChangeDetector(_tempDir);

        var result = detector.DetectChanges(MakeSnapshot());

        Assert.Null(result);
    }

    [Fact]
    public void BooleanFieldChange_GDM_Detected()
    {
        using var detector = new ConfigChangeDetector(_tempDir);

        detector.DetectChanges(MakeSnapshot("boot_0414_0900", gdm: false));

        var change = detector.DetectChanges(MakeSnapshot("boot_0415_0900", gdm: true));

        Assert.NotNull(change);
        Assert.True(change.Changes.ContainsKey("GDM"));
        Assert.Equal("False", change.Changes["GDM"].Before);
        Assert.Equal("True",  change.Changes["GDM"].After);
    }

    [Fact]
    public void BooleanFieldChange_Cmd2T_Detected()
    {
        using var detector = new ConfigChangeDetector(_tempDir);

        detector.DetectChanges(MakeSnapshot("boot_0414_0900", cmd2t: false));

        var change = detector.DetectChanges(MakeSnapshot("boot_0415_0900", cmd2t: true));

        Assert.NotNull(change);
        Assert.True(change.Changes.ContainsKey("Cmd2T"));
        Assert.Equal("False", change.Changes["Cmd2T"].Before);
        Assert.Equal("True",  change.Changes["Cmd2T"].After);
    }

    [Fact]
    public void Persistence_RoundTrip_DetectsChangeAfterReload()
    {
        // First instance establishes baseline and persists it.
        using (var detector = new ConfigChangeDetector(_tempDir))
        {
            detector.DetectChanges(MakeSnapshot("boot_0414_0900", cl: 18));
        }

        // Second instance loads the persisted baseline.
        using var detector2 = new ConfigChangeDetector(_tempDir);
        detector2.LoadPrevious();

        var change = detector2.DetectChanges(MakeSnapshot("boot_0415_0900", cl: 16));

        Assert.NotNull(change);
        Assert.True(change.Changes.ContainsKey("CL"));
        Assert.Equal("18", change.Changes["CL"].Before);
        Assert.Equal("16", change.Changes["CL"].After);
    }

    [Fact]
    public void Persistence_NoPreviousFile_TreatedAsFirstBoot()
    {
        // Directory exists but no last_snapshot.json on disk.
        using var detector = new ConfigChangeDetector(_tempDir);
        detector.LoadPrevious();

        var result = detector.DetectChanges(MakeSnapshot());

        Assert.Null(result);
    }

    [Fact]
    public void Persistence_CorruptFile_TreatedAsFirstBoot()
    {
        File.WriteAllText(Path.Combine(_tempDir, "last_snapshot.json"), "{ not valid json !!!");

        using var detector = new ConfigChangeDetector(_tempDir);
        detector.LoadPrevious();

        var result = detector.DetectChanges(MakeSnapshot());

        Assert.Null(result);
    }

    [Fact]
    public void ChangeRecord_HasCorrectBootIdAndSnapshotIds()
    {
        using var detector = new ConfigChangeDetector(_tempDir);

        var before = MakeSnapshot("boot_0414_0900", snapshotId: "snap-before", cl: 18);
        detector.DetectChanges(before);

        var after = MakeSnapshot("boot_0415_0900", snapshotId: "snap-after", cl: 16);
        var change = detector.DetectChanges(after);

        Assert.NotNull(change);
        Assert.Equal("boot_0415_0900", change.BootId);
        Assert.Equal("snap-before",    change.SnapshotBeforeId);
        Assert.Equal("snap-after",     change.SnapshotAfterId);
    }

    [Fact]
    public void AtomicWrite_NoTempFilesAfterSave()
    {
        using var detector = new ConfigChangeDetector(_tempDir);
        detector.DetectChanges(MakeSnapshot());

        var tmpFiles = Directory.GetFiles(_tempDir, "*.tmp");
        Assert.Empty(tmpFiles);
    }

    [Fact]
    public void SnapshotFile_ExistsAfterFirstDetect()
    {
        using var detector = new ConfigChangeDetector(_tempDir);
        detector.DetectChanges(MakeSnapshot());

        Assert.True(File.Exists(Path.Combine(_tempDir, "last_snapshot.json")));
    }

    // -----------------------------------------------------------------------
    // Changes journal
    // -----------------------------------------------------------------------

    [Fact]
    public void ChangesFile_ExistsAfterDetectedChange()
    {
        using var detector = new ConfigChangeDetector(_tempDir);

        detector.DetectChanges(MakeSnapshot("boot_0414_0900", cl: 18));
        detector.DetectChanges(MakeSnapshot("boot_0415_0900", cl: 16));

        Assert.True(File.Exists(Path.Combine(_tempDir, "changes.json")));
    }

    [Fact]
    public void ChangesFile_NotCreatedWhenNoChange()
    {
        using var detector = new ConfigChangeDetector(_tempDir);

        // Identical snapshots produce no change.
        detector.DetectChanges(MakeSnapshot("boot_0414_0900"));
        detector.DetectChanges(MakeSnapshot("boot_0415_0900"));

        Assert.False(File.Exists(Path.Combine(_tempDir, "changes.json")));
    }

    [Fact]
    public void LoadChanges_MissingFile_ReturnsEmpty()
    {
        using var detector = new ConfigChangeDetector(_tempDir);
        detector.LoadChanges();

        Assert.Empty(detector.GetRecentChanges(10));
    }

    [Fact]
    public void LoadChanges_CorruptFile_ReturnsEmpty()
    {
        File.WriteAllText(Path.Combine(_tempDir, "changes.json"), "{ not valid json !!!");

        using var detector = new ConfigChangeDetector(_tempDir);
        detector.LoadChanges();

        Assert.Empty(detector.GetRecentChanges(10));
    }

    [Fact]
    public void ChangesJournal_PersistedAndReloaded()
    {
        // First instance detects the change and persists it.
        using (var detector = new ConfigChangeDetector(_tempDir))
        {
            detector.DetectChanges(MakeSnapshot("boot_0414_0900", cl: 18));
            detector.DetectChanges(MakeSnapshot("boot_0415_0900", cl: 16));
        }

        // Second instance loads the journal from disk.
        using var detector2 = new ConfigChangeDetector(_tempDir);
        detector2.LoadChanges();

        var changes = detector2.GetRecentChanges(10);
        Assert.Single(changes);
        Assert.True(changes[0].Changes.ContainsKey("CL"));
        Assert.Equal("18", changes[0].Changes["CL"].Before);
        Assert.Equal("16", changes[0].Changes["CL"].After);
    }

    [Fact]
    public void GetRecentChanges_ReturnsLastN()
    {
        using var detector = new ConfigChangeDetector(_tempDir);

        // Baseline, then 6 changes (cl alternates 18→16→18→... building a sequence).
        // Each pair of adjacent snapshots differs so each detection produces a change.
        int cl = 18;
        detector.DetectChanges(MakeSnapshot("boot_0", cl: cl));
        for (int i = 1; i <= 6; i++)
        {
            cl = (cl == 18) ? 16 : 18;
            detector.DetectChanges(MakeSnapshot($"boot_{i}", cl: cl));
        }

        var recent = detector.GetRecentChanges(5);

        // Exactly 5 returned even though 6 changes exist.
        Assert.Equal(5, recent.Count);
    }

    [Fact]
    public void GetRecentChanges_WhenFewerThanN_ReturnsAll()
    {
        using var detector = new ConfigChangeDetector(_tempDir);

        detector.DetectChanges(MakeSnapshot("boot_0414_0900", cl: 18));
        detector.DetectChanges(MakeSnapshot("boot_0415_0900", cl: 16));

        var recent = detector.GetRecentChanges(5);

        Assert.Single(recent);
    }

    [Fact]
    public void GetRecentChanges_MultipleChanges_ChronologicalOrder()
    {
        using var detector = new ConfigChangeDetector(_tempDir);

        // Four snapshots produce three changes:
        //   change[0]: CL 18 → 16 (detected at boot_b)
        //   change[1]: CL 16 → 14 (detected at boot_c)
        //   change[2]: CL 14 → 12 (detected at boot_d)
        detector.DetectChanges(MakeSnapshot("boot_a", cl: 18));
        detector.DetectChanges(MakeSnapshot("boot_b", cl: 16));
        detector.DetectChanges(MakeSnapshot("boot_c", cl: 14));
        detector.DetectChanges(MakeSnapshot("boot_d", cl: 12));

        var recent = detector.GetRecentChanges(3);

        // All three returned in chronological (insertion) order.
        Assert.Equal(3, recent.Count);
        Assert.Equal("18", recent[0].Changes["CL"].Before); // change[0]: 18 → 16
        Assert.Equal("16", recent[0].Changes["CL"].After);
        Assert.Equal("16", recent[1].Changes["CL"].Before); // change[1]: 16 → 14
        Assert.Equal("14", recent[2].Changes["CL"].Before); // change[2]: 14 → 12
        Assert.Equal("12", recent[2].Changes["CL"].After);
    }

    [Fact]
    public void ChangesJournal_AtomicWrite_NoTempFiles()
    {
        using var detector = new ConfigChangeDetector(_tempDir);

        detector.DetectChanges(MakeSnapshot("boot_0414_0900", cl: 18));
        detector.DetectChanges(MakeSnapshot("boot_0415_0900", cl: 16));

        var tmpFiles = Directory.GetFiles(_tempDir, "changes.*.tmp");
        Assert.Empty(tmpFiles);
    }
}
