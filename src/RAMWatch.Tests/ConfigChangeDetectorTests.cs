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
}
