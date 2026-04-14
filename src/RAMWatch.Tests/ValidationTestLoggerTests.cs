using Xunit;
using RAMWatch.Core.Models;
using RAMWatch.Service.Services;

namespace RAMWatch.Tests;

public class ValidationTestLoggerTests : IDisposable
{
    private readonly string _tempDir;

    public ValidationTestLoggerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"ramwatch-vtl-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    [Fact]
    public void LogResult_ThenGetResults_RoundTrips()
    {
        var logger = new ValidationTestLogger(_tempDir);
        logger.Load();

        var result = MakeResult("Karhu", passed: true, metricValue: 1500);
        logger.LogResult(result);

        var all = logger.GetResults();

        Assert.Single(all);
        Assert.Equal("Karhu", all[0].TestTool);
        Assert.Equal(1500, all[0].MetricValue);
        Assert.True(all[0].Passed);
    }

    [Fact]
    public void LogResult_MultipleResults_AllPersistedInOrder()
    {
        var logger = new ValidationTestLogger(_tempDir);
        logger.Load();

        var t0 = DateTime.UtcNow;
        logger.LogResult(MakeResult("Karhu", passed: true,  metricValue: 800,  timestamp: t0));
        logger.LogResult(MakeResult("TM5",   passed: false, metricValue: 10,   timestamp: t0.AddMinutes(1)));
        logger.LogResult(MakeResult("Karhu", passed: true,  metricValue: 2000, timestamp: t0.AddMinutes(2)));

        var all = logger.GetResults();

        Assert.Equal(3, all.Count);
        Assert.Equal("Karhu", all[0].TestTool);
        Assert.Equal(800,     all[0].MetricValue);
        Assert.Equal("TM5",   all[1].TestTool);
        Assert.Equal("Karhu", all[2].TestTool);
        Assert.Equal(2000,    all[2].MetricValue);
    }

    [Fact]
    public void Load_MissingFile_ReturnsEmptyList()
    {
        var logger = new ValidationTestLogger(_tempDir);
        logger.Load();

        Assert.Empty(logger.GetResults());
    }

    [Fact]
    public void Load_CorruptFile_ReturnsEmptyList_DoesNotThrow()
    {
        File.WriteAllText(Path.Combine(_tempDir, "tests.json"), "{ not valid json !!!");

        var logger = new ValidationTestLogger(_tempDir);
        logger.Load();

        Assert.Empty(logger.GetResults());
    }

    [Fact]
    public void Results_SurviveServiceRestart()
    {
        // First instance: log two results and let it go out of scope.
        var logger1 = new ValidationTestLogger(_tempDir);
        logger1.Load();
        logger1.LogResult(MakeResult("Karhu", passed: true, metricValue: 1200));
        logger1.LogResult(MakeResult("TM5",   passed: true, metricValue: 30));

        // Second instance simulates a service restart.
        var logger2 = new ValidationTestLogger(_tempDir);
        logger2.Load();
        var restored = logger2.GetResults();

        Assert.Equal(2, restored.Count);
        Assert.Equal("Karhu", restored[0].TestTool);
        Assert.Equal("TM5",   restored[1].TestTool);
    }

    [Fact]
    public void GetRecentResults_ReturnsLastN()
    {
        var logger = new ValidationTestLogger(_tempDir);
        logger.Load();

        for (int i = 0; i < 5; i++)
            logger.LogResult(MakeResult("Karhu", passed: true, metricValue: 100 * (i + 1)));

        var recent = logger.GetRecentResults(3);

        Assert.Equal(3, recent.Count);
        // Last 3 logged: metricValues 300, 400, 500.
        Assert.Equal(300, recent[0].MetricValue);
        Assert.Equal(400, recent[1].MetricValue);
        Assert.Equal(500, recent[2].MetricValue);
    }

    [Fact]
    public void GetRecentResults_CountExceedsList_ReturnsAll()
    {
        var logger = new ValidationTestLogger(_tempDir);
        logger.Load();

        logger.LogResult(MakeResult("Karhu", passed: true, metricValue: 1000));
        logger.LogResult(MakeResult("Karhu", passed: true, metricValue: 2000));

        var recent = logger.GetRecentResults(10);

        Assert.Equal(2, recent.Count);
    }

    [Fact]
    public void Save_IsAtomic_NoTempFilesRemain()
    {
        var logger = new ValidationTestLogger(_tempDir);
        logger.Load();
        logger.LogResult(MakeResult("Karhu", passed: true, metricValue: 1000));

        var tmpFiles = Directory.GetFiles(_tempDir, "*.tmp");
        Assert.Empty(tmpFiles);
    }

    // ── DeleteById ────────────────────────────────────────────────────────────

    [Fact]
    public void DeleteById_ExistingId_RemovesAndReturnsTrueAndPersists()
    {
        var logger = new ValidationTestLogger(_tempDir);
        logger.Load();

        var result = MakeResult("Karhu", passed: true, metricValue: 1000);
        logger.LogResult(result);

        bool removed = logger.DeleteById(result.Id);

        Assert.True(removed);
        Assert.Empty(logger.GetResults());

        // Verify persistence: a new instance sees the deletion.
        var logger2 = new ValidationTestLogger(_tempDir);
        logger2.Load();
        Assert.Empty(logger2.GetResults());
    }

    [Fact]
    public void DeleteById_UnknownId_ReturnsFalse_LeavesListUnchanged()
    {
        var logger = new ValidationTestLogger(_tempDir);
        logger.Load();

        logger.LogResult(MakeResult("Karhu", passed: true, metricValue: 1000));

        bool removed = logger.DeleteById("does-not-exist");

        Assert.False(removed);
        Assert.Single(logger.GetResults());
    }

    [Fact]
    public void DeleteById_CorrectEntryRemoved_OthersPreserved()
    {
        var logger = new ValidationTestLogger(_tempDir);
        logger.Load();

        var r1 = MakeResult("Karhu", passed: true,  metricValue: 100);
        var r2 = MakeResult("TM5",   passed: false, metricValue: 10);
        var r3 = MakeResult("Karhu", passed: true,  metricValue: 200);
        logger.LogResult(r1);
        logger.LogResult(r2);
        logger.LogResult(r3);

        bool removed = logger.DeleteById(r2.Id);

        Assert.True(removed);
        var remaining = logger.GetResults();
        Assert.Equal(2, remaining.Count);
        Assert.Equal(r1.Id, remaining[0].Id);
        Assert.Equal(r3.Id, remaining[1].Id);
    }

    [Fact]
    public void ValidationResult_AutoGeneratesUniqueIds()
    {
        // Each new result gets a distinct Id even when all fields are identical.
        var r1 = MakeResult("Karhu", passed: true, metricValue: 1000);
        var r2 = MakeResult("Karhu", passed: true, metricValue: 1000);

        Assert.NotEqual(r1.Id, r2.Id);
    }

    // ── DeleteById ────────────────────────────────────────────────────────────

    // ── end ──────────────────────────────────────────────────────────────────

    private static ValidationResult MakeResult(
        string testTool,
        bool passed,
        double metricValue,
        DateTime? timestamp = null,
        string snapshotId = "snap-001")
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
