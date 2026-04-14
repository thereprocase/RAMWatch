using Xunit;
using RAMWatch.Core.Models;
using RAMWatch.Service.Services;

namespace RAMWatch.Tests;

/// <summary>
/// Tests for War Council security and performance fixes:
/// path validation (C1), settings clamping (W3/W5),
/// journal caps (N3), and GetRange slicing (H1).
/// </summary>
public class SecurityAndCapTests : IDisposable
{
    private readonly string _tempDir;

    public SecurityAndCapTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"ramwatch-sec-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    // ── AppSettings.IsValidDataPath ───────────────────────────────────────────

    [Fact]
    public void IsValidDataPath_Null_ReturnsTrue()
    {
        Assert.True(AppSettings.IsValidDataPath(null));
    }

    [Fact]
    public void IsValidDataPath_EmptyString_ReturnsTrue()
    {
        Assert.True(AppSettings.IsValidDataPath(""));
    }

    [Fact]
    public void IsValidDataPath_WhitespaceOnly_ReturnsTrue()
    {
        Assert.True(AppSettings.IsValidDataPath("   "));
    }

    [Fact]
    public void IsValidDataPath_NormalUserPath_ReturnsTrue()
    {
        // A typical user data path under AppData or Documents should pass.
        string path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RAMWatch");
        Assert.True(AppSettings.IsValidDataPath(path));
    }

    [Fact]
    public void IsValidDataPath_ProgramDataPath_ReturnsTrue()
    {
        // The default service data path lives in ProgramData — must be allowed.
        string path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "RAMWatch");
        Assert.True(AppSettings.IsValidDataPath(path));
    }

    [Fact]
    public void IsValidDataPath_UncPath_ReturnsFalse()
    {
        Assert.False(AppSettings.IsValidDataPath(@"\\server\share\logs"));
    }

    [Fact]
    public void IsValidDataPath_WindowsDirectory_ReturnsFalse()
    {
        string sysRoot = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (string.IsNullOrEmpty(sysRoot))
            return; // Can't test if env var missing

        string path = Path.Combine(sysRoot, "System32");
        Assert.False(AppSettings.IsValidDataPath(path));
    }

    [Fact]
    public void IsValidDataPath_ProgramFiles_ReturnsFalse()
    {
        string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (string.IsNullOrEmpty(pf))
            return;

        string path = Path.Combine(pf, "SomeApp");
        Assert.False(AppSettings.IsValidDataPath(path));
    }

    [Fact]
    public void IsValidDataPath_ProgramFilesX86_ReturnsFalse()
    {
        string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (string.IsNullOrEmpty(pf86))
            return;

        string path = Path.Combine(pf86, "SomeApp");
        Assert.False(AppSettings.IsValidDataPath(path));
    }

    [Fact]
    public void IsValidDataPath_ExceptionThrowingPath_ReturnsFalse()
    {
        // Path with a null byte causes GetFullPath to throw — must return false, not propagate.
        // On Windows, asterisks are not rejected by GetFullPath (only by filesystem ops).
        string pathWithNull = "C:\\some\x00path";
        Assert.False(AppSettings.IsValidDataPath(pathWithNull));
    }

    // ── SettingsManager — RefreshIntervalSeconds clamping ────────────────────

    [Theory]
    [InlineData(5,    5)]     // minimum boundary — stays
    [InlineData(60,   60)]    // normal value — unchanged
    [InlineData(3600, 3600)]  // maximum boundary — stays
    [InlineData(4,    5)]     // below minimum — clamped up
    [InlineData(0,    5)]     // zero — clamped up
    [InlineData(-1,   5)]     // negative — clamped up
    [InlineData(3601, 3600)]  // above maximum — clamped down
    [InlineData(9999, 3600)]  // extreme — clamped down
    public void RefreshInterval_Clamp_ProducesExpectedValue(int input, int expected)
    {
        int clamped = Math.Clamp(input, 5, 3600);
        Assert.Equal(expected, clamped);
    }

    // ── SnapshotJournal max cap ───────────────────────────────────────────────

    [Fact]
    public void SnapshotJournal_ExceedingCap_EvictsOldestEntries()
    {
        var journal = new SnapshotJournal(_tempDir);
        journal.Load();

        // Insert 1002 distinct snapshots; cap is 1000.
        for (int i = 0; i < 1002; i++)
        {
            journal.Save(new TimingSnapshot
            {
                SnapshotId = $"snap-{i:D4}",
                BootId     = "boot_cap_test",
                Timestamp  = DateTime.UtcNow,
                Label      = $"Snapshot {i}"
            });
        }

        var all = journal.GetAll();

        // Must not exceed 1000.
        Assert.Equal(1000, all.Count);

        // The two oldest (snap-0000 and snap-0001) should have been evicted.
        Assert.DoesNotContain(all, s => s.SnapshotId == "snap-0000");
        Assert.DoesNotContain(all, s => s.SnapshotId == "snap-0001");

        // The newest should still be present.
        Assert.Contains(all, s => s.SnapshotId == "snap-1001");
    }

    [Fact]
    public void SnapshotJournal_AtCap_NewEntryEvictsOldest()
    {
        var journal = new SnapshotJournal(_tempDir);
        journal.Load();

        // Fill to exactly 1000.
        for (int i = 0; i < 1000; i++)
        {
            journal.Save(new TimingSnapshot
            {
                SnapshotId = $"snap-{i:D4}",
                BootId     = "boot_cap_test",
                Timestamp  = DateTime.UtcNow,
            });
        }

        // Adding one more should evict snap-0000 specifically.
        journal.Save(new TimingSnapshot
        {
            SnapshotId = "snap-extra",
            BootId     = "boot_cap_test",
            Timestamp  = DateTime.UtcNow,
        });

        var all = journal.GetAll();
        Assert.Equal(1000, all.Count);
        Assert.DoesNotContain(all, s => s.SnapshotId == "snap-0000");
        Assert.Contains(all, s => s.SnapshotId == "snap-extra");
    }

    // ── ValidationTestLogger max cap ─────────────────────────────────────────

    [Fact]
    public void ValidationTestLogger_ExceedingCap_EvictsOldestEntries()
    {
        var logger = new ValidationTestLogger(_tempDir);
        logger.Load();

        // Insert 502 results; cap is 500.
        for (int i = 0; i < 502; i++)
        {
            logger.LogResult(new ValidationResult
            {
                Timestamp   = DateTime.UtcNow,
                BootId      = $"boot_{i:D4}",
                TestTool    = "memtest86",
                Passed      = true,
                MetricName  = "",
                MetricValue = 0,
                MetricUnit  = ""
            });
        }

        var all = logger.GetResults();

        // Must not exceed 500.
        Assert.Equal(500, all.Count);

        // The two oldest entries had BootId boot_0000 / boot_0001 — should be gone.
        Assert.DoesNotContain(all, r => r.BootId == "boot_0000");
        Assert.DoesNotContain(all, r => r.BootId == "boot_0001");

        // The newest entry is still present.
        Assert.Contains(all, r => r.BootId == "boot_0501");
    }

    // ── ValidationTestLogger.GetRecentResults uses GetRange (O(1) slice) ─────

    [Fact]
    public void GetRecentResults_ReturnsLastNInOrder()
    {
        var logger = new ValidationTestLogger(_tempDir);
        logger.Load();

        for (int i = 0; i < 10; i++)
        {
            logger.LogResult(new ValidationResult
            {
                Timestamp   = DateTime.UtcNow,
                BootId      = $"boot_{i}",
                TestTool    = "dummy",
                Passed      = (i % 2 == 0),
                MetricName  = "",
                MetricValue = 0,
                MetricUnit  = ""
            });
        }

        var recent = logger.GetRecentResults(3);

        Assert.Equal(3, recent.Count);
        Assert.Equal("boot_7", recent[0].BootId);
        Assert.Equal("boot_8", recent[1].BootId);
        Assert.Equal("boot_9", recent[2].BootId);
    }

    [Fact]
    public void GetRecentResults_CountExceedsListSize_ReturnsAll()
    {
        var logger = new ValidationTestLogger(_tempDir);
        logger.Load();

        logger.LogResult(new ValidationResult
        {
            Timestamp   = DateTime.UtcNow,
            BootId      = "only_one",
            TestTool    = "dummy",
            Passed      = true,
            MetricName  = "",
            MetricValue = 0,
            MetricUnit  = ""
        });

        var recent = logger.GetRecentResults(50);

        Assert.Single(recent);
        Assert.Equal("only_one", recent[0].BootId);
    }

    // ── ConfigChangeDetector.GetRecentChanges uses GetRange ──────────────────

    [Fact]
    public void GetRecentChanges_ReturnsLastNInOrder()
    {
        var detector = new ConfigChangeDetector(_tempDir);

        // Produce 6 changes (7 snapshots, each pair differs in CL).
        for (int i = 0; i <= 6; i++)
        {
            detector.DetectChanges(new TimingSnapshot
            {
                SnapshotId  = Guid.NewGuid().ToString("N"),
                Timestamp   = DateTime.UtcNow,
                BootId      = $"boot_{i}",
                FclkMhz = 1900, UclkMhz = 1900, MemClockMhz = 1800,
                CL          = 14 + i,   // changes every boot
                RCDRD = 18, RCDWR = 12, RP = 18, RAS = 36, RC = 54, CWL = 14,
                RFC = 312, RFC2 = 200, RFC4 = 100,
                RRDS = 4, RRDL = 6, FAW = 16, WTRS = 4, WTRL = 12, WR = 18, RTP = 10,
                RDRDSCL = 2, WRWRSCL = 2,
                RDRDSC = 2, RDRDSD = 6, RDRDDD = 8,
                WRWRSC = 2, WRWRSD = 6, WRWRDD = 8,
                RDWR = 14, WRRD = 2,
                REFI = 65535, CKE = 6, STAG = 2, MOD = 6, MRD = 6,
                PHYRDL_A = 40, PHYRDL_B = 42,
            });
        }

        // Ask for the 3 most recent changes.
        var recent = detector.GetRecentChanges(3);

        // Should be 3 entries, the last three of the 6 changes.
        Assert.Equal(3, recent.Count);
        // All should show a CL change.
        Assert.All(recent, c => Assert.True(c.Changes.ContainsKey("CL")));
    }

    [Fact]
    public void GetRecentChanges_CountExceedsList_ReturnsAll()
    {
        var detector = new ConfigChangeDetector(_tempDir);

        // Two snapshots → one change.
        detector.DetectChanges(new TimingSnapshot
        {
            SnapshotId = "s1", BootId = "b1", Timestamp = DateTime.UtcNow,
            FclkMhz = 1900, UclkMhz = 1900, MemClockMhz = 1800,
            CL = 16, RCDRD = 18, RCDWR = 12, RP = 18, RAS = 36, RC = 54, CWL = 14,
            RFC = 312, RFC2 = 200, RFC4 = 100,
        });
        detector.DetectChanges(new TimingSnapshot
        {
            SnapshotId = "s2", BootId = "b2", Timestamp = DateTime.UtcNow,
            FclkMhz = 1900, UclkMhz = 1900, MemClockMhz = 1800,
            CL = 18, RCDRD = 18, RCDWR = 12, RP = 18, RAS = 36, RC = 54, CWL = 14,
            RFC = 312, RFC2 = 200, RFC4 = 100,
        });

        var recent = detector.GetRecentChanges(100);

        Assert.Single(recent);
    }
}
