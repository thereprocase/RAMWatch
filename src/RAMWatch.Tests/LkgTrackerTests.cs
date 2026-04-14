using Xunit;
using RAMWatch.Core.Models;
using RAMWatch.Service.Services;

namespace RAMWatch.Tests;

public class LkgTrackerTests : IDisposable
{
    private readonly string _tempDir;

    public LkgTrackerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"ramwatch-lkg-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    [Fact]
    public void NoValidations_LkgIsNull()
    {
        var tracker = new LkgTracker(_tempDir);
        tracker.Load();

        tracker.UpdateLkg(new List<ValidationResult>(), new List<TimingSnapshot>());

        Assert.Null(tracker.CurrentLkg);
    }

    [Fact]
    public void OnePassingKarhu2000pct_SnapshotBecomesLkg()
    {
        var tracker = new LkgTracker(_tempDir);
        tracker.Load();

        var snapshot = MakeSnapshot("snap-001");
        var result   = MakeResult("Karhu", passed: true, metricValue: 2000, snapshotId: "snap-001");

        tracker.UpdateLkg(new List<ValidationResult> { result }, new List<TimingSnapshot> { snapshot });

        Assert.NotNull(tracker.CurrentLkg);
        Assert.Equal("snap-001", tracker.CurrentLkg!.SnapshotId);
    }

    [Fact]
    public void PassingTestBelowThreshold_LkgStaysNull()
    {
        // Karhu threshold is ≥ 1000. 500 < 1000 — should not qualify.
        var tracker  = new LkgTracker(_tempDir);
        tracker.Load();

        var snapshot = MakeSnapshot("snap-001");
        var result   = MakeResult("Karhu", passed: true, metricValue: 500, snapshotId: "snap-001");

        tracker.UpdateLkg(new List<ValidationResult> { result }, new List<TimingSnapshot> { snapshot });

        Assert.Null(tracker.CurrentLkg);
    }

    [Fact]
    public void MultiplePassingTests_MostRecentIsLkg()
    {
        var tracker = new LkgTracker(_tempDir);
        tracker.Load();

        var snap1 = MakeSnapshot("snap-001");
        var snap2 = MakeSnapshot("snap-002");
        var t0    = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc);

        var results = new List<ValidationResult>
        {
            MakeResult("Karhu", passed: true, metricValue: 1200, snapshotId: "snap-001", timestamp: t0),
            MakeResult("Karhu", passed: true, metricValue: 1500, snapshotId: "snap-002", timestamp: t0.AddDays(1)),
        };

        tracker.UpdateLkg(results, new List<TimingSnapshot> { snap1, snap2 });

        Assert.NotNull(tracker.CurrentLkg);
        // snap-002 is more recent, so it wins.
        Assert.Equal("snap-002", tracker.CurrentLkg!.SnapshotId);
    }

    [Fact]
    public void FailingTestWithHighCoverage_NotLkg()
    {
        // Passed == false means it never qualifies, regardless of MetricValue.
        var tracker  = new LkgTracker(_tempDir);
        tracker.Load();

        var snapshot = MakeSnapshot("snap-001");
        var result   = MakeResult("Karhu", passed: false, metricValue: 9999, snapshotId: "snap-001");

        tracker.UpdateLkg(new List<ValidationResult> { result }, new List<TimingSnapshot> { snapshot });

        Assert.Null(tracker.CurrentLkg);
    }

    [Fact]
    public void CustomTestTypeWithNoThreshold_NeverQualifies()
    {
        // "HCI Memtest" is not in the threshold table — should never become LKG.
        var tracker  = new LkgTracker(_tempDir);
        tracker.Load();

        var snapshot = MakeSnapshot("snap-001");
        var result   = MakeResult("HCI Memtest", passed: true, metricValue: 1_000_000, snapshotId: "snap-001");

        tracker.UpdateLkg(new List<ValidationResult> { result }, new List<TimingSnapshot> { snapshot });

        Assert.Null(tracker.CurrentLkg);
    }

    [Fact]
    public void Tm5PassingAboveThreshold_BecomesLkg()
    {
        // Explicit coverage of the TM5 path (threshold = 25 cycles).
        var tracker  = new LkgTracker(_tempDir);
        tracker.Load();

        var snapshot = MakeSnapshot("snap-tm5");
        var result   = MakeResult("TM5", passed: true, metricValue: 30, snapshotId: "snap-tm5");

        tracker.UpdateLkg(new List<ValidationResult> { result }, new List<TimingSnapshot> { snapshot });

        Assert.NotNull(tracker.CurrentLkg);
        Assert.Equal("snap-tm5", tracker.CurrentLkg!.SnapshotId);
    }

    [Fact]
    public void Tm5BelowThreshold_NotLkg()
    {
        // TM5 threshold = 25 cycles. 10 < 25.
        var tracker  = new LkgTracker(_tempDir);
        tracker.Load();

        var snapshot = MakeSnapshot("snap-tm5");
        var result   = MakeResult("TM5", passed: true, metricValue: 10, snapshotId: "snap-tm5");

        tracker.UpdateLkg(new List<ValidationResult> { result }, new List<TimingSnapshot> { snapshot });

        Assert.Null(tracker.CurrentLkg);
    }

    [Fact]
    public void LkgPersistsAcrossRestart()
    {
        // First tracker establishes LKG.
        var tracker1 = new LkgTracker(_tempDir);
        tracker1.Load();

        var snapshot = MakeSnapshot("snap-persist");
        var result   = MakeResult("Karhu", passed: true, metricValue: 1800, snapshotId: "snap-persist");
        tracker1.UpdateLkg(new List<ValidationResult> { result }, new List<TimingSnapshot> { snapshot });

        Assert.NotNull(tracker1.CurrentLkg);

        // Second tracker simulates a service restart — loads from disk.
        var tracker2 = new LkgTracker(_tempDir);
        tracker2.Load();

        Assert.NotNull(tracker2.CurrentLkg);
        Assert.Equal("snap-persist", tracker2.CurrentLkg!.SnapshotId);
    }

    [Fact]
    public void UpdateLkg_WithNoQualifyingResult_ClearsPersistedLkg()
    {
        // Establish an LKG on disk first.
        var tracker1 = new LkgTracker(_tempDir);
        tracker1.Load();
        tracker1.UpdateLkg(
            new List<ValidationResult> { MakeResult("Karhu", passed: true, metricValue: 1200, snapshotId: "snap-001") },
            new List<TimingSnapshot>   { MakeSnapshot("snap-001") });

        Assert.NotNull(tracker1.CurrentLkg);

        // Now update with only failing results — LKG should go null and the file should vanish.
        tracker1.UpdateLkg(
            new List<ValidationResult> { MakeResult("Karhu", passed: false, metricValue: 1200, snapshotId: "snap-001") },
            new List<TimingSnapshot>   { MakeSnapshot("snap-001") });

        Assert.Null(tracker1.CurrentLkg);

        // A fresh instance finds nothing on disk.
        var tracker2 = new LkgTracker(_tempDir);
        tracker2.Load();
        Assert.Null(tracker2.CurrentLkg);
    }

    [Fact]
    public void Load_MissingFile_LkgIsNull()
    {
        var tracker = new LkgTracker(_tempDir);
        tracker.Load();

        Assert.Null(tracker.CurrentLkg);
    }

    [Fact]
    public void Load_CorruptFile_LkgIsNull_DoesNotThrow()
    {
        File.WriteAllText(Path.Combine(_tempDir, "lkg.json"), "{ broken json !!!");

        var tracker = new LkgTracker(_tempDir);
        tracker.Load();

        Assert.Null(tracker.CurrentLkg);
    }

    // -------------------------------------------------------------------------

    private static TimingSnapshot MakeSnapshot(string id, int cl = 36, int memClockMhz = 2000)
    {
        return new TimingSnapshot
        {
            SnapshotId   = id,
            Timestamp    = DateTime.UtcNow,
            BootId       = "boot_0414_0901",
            MemClockMhz  = memClockMhz,
            CL           = cl,
        };
    }

    private static ValidationResult MakeResult(
        string testTool,
        bool passed,
        double metricValue,
        string snapshotId  = "snap-001",
        DateTime? timestamp = null)
    {
        return new ValidationResult
        {
            Timestamp        = timestamp ?? DateTime.UtcNow,
            BootId           = "boot_0414_0901",
            TestTool         = testTool,
            MetricName       = testTool == "TM5" ? "cycles" : "coverage",
            MetricValue      = metricValue,
            MetricUnit       = testTool == "TM5" ? "cycles" : "%",
            Passed           = passed,
            ActiveSnapshotId = snapshotId,
        };
    }
}
