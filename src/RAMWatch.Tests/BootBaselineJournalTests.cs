using RAMWatch.Core.Models;
using RAMWatch.Service.Services;
using Xunit;

namespace RAMWatch.Tests;

public class BootBaselineJournalTests
{
    private readonly string _tempDir;
    private readonly BootBaselineJournal _journal;

    public BootBaselineJournalTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"ramwatch_baseline_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _journal = new BootBaselineJournal(_tempDir);
    }

    [Fact]
    public void ComputeBaselines_NoData_ReturnsEmpty()
    {
        var baselines = _journal.ComputeBaselines();
        Assert.Empty(baselines);
    }

    [Fact]
    public void ComputeBaselines_FewerThan3Boots_ReturnsEmpty()
    {
        _journal.RecordBoot("boot1", [new ErrorSource("NTFS Error", EventCategory.Filesystem, 20, null)]);
        _journal.RecordBoot("boot2", [new ErrorSource("NTFS Error", EventCategory.Filesystem, 25, null)]);

        var baselines = _journal.ComputeBaselines();
        Assert.Empty(baselines);
    }

    [Fact]
    public void ComputeBaselines_SteadyCounts_ReturnsMean()
    {
        _journal.RecordBoot("b1", [new ErrorSource("NTFS Error", EventCategory.Filesystem, 20, null)]);
        _journal.RecordBoot("b2", [new ErrorSource("NTFS Error", EventCategory.Filesystem, 22, null)]);
        _journal.RecordBoot("b3", [new ErrorSource("NTFS Error", EventCategory.Filesystem, 18, null)]);
        _journal.RecordBoot("b4", [new ErrorSource("NTFS Error", EventCategory.Filesystem, 21, null)]);
        _journal.RecordBoot("b5", [new ErrorSource("NTFS Error", EventCategory.Filesystem, 19, null)]);

        var baselines = _journal.ComputeBaselines();
        Assert.True(baselines.ContainsKey("NTFS Error"));
        Assert.InRange(baselines["NTFS Error"].Mean, 19, 21);
    }

    [Fact]
    public void ComputeBaselines_ExcludesOutliers()
    {
        // 9 normal boots around 20, one extreme outlier at 200.
        for (int i = 0; i < 9; i++)
            _journal.RecordBoot($"b{i}", [new ErrorSource("NTFS Error", EventCategory.Filesystem, 19 + i % 3, null)]);
        _journal.RecordBoot("outlier", [new ErrorSource("NTFS Error", EventCategory.Filesystem, 200, null)]);

        var baselines = _journal.ComputeBaselines();
        // Mean should be close to 20, not dragged up by the 200.
        Assert.InRange(baselines["NTFS Error"].Mean, 18, 22);
    }

    [Fact]
    public void ComputeBaselines_MultipleSources_AllComputed()
    {
        for (int i = 0; i < 5; i++)
        {
            _journal.RecordBoot($"b{i}",
            [
                new ErrorSource("NTFS Error", EventCategory.Filesystem, 20 + i, null),
                new ErrorSource("Filter Manager", EventCategory.Integrity, 15 + i, null),
                new ErrorSource("Disk Error", EventCategory.Filesystem, 2, null)
            ]);
        }

        var baselines = _journal.ComputeBaselines();
        Assert.Equal(3, baselines.Count);
        Assert.True(baselines.ContainsKey("NTFS Error"));
        Assert.True(baselines.ContainsKey("Filter Manager"));
        Assert.True(baselines.ContainsKey("Disk Error"));
    }

    [Fact]
    public void RecordBoot_DuplicateBootId_Ignored()
    {
        _journal.RecordBoot("b1", [new ErrorSource("NTFS Error", EventCategory.Filesystem, 10, null)]);
        _journal.RecordBoot("b1", [new ErrorSource("NTFS Error", EventCategory.Filesystem, 99, null)]);
        _journal.RecordBoot("b2", [new ErrorSource("NTFS Error", EventCategory.Filesystem, 10, null)]);
        _journal.RecordBoot("b3", [new ErrorSource("NTFS Error", EventCategory.Filesystem, 10, null)]);

        var baselines = _journal.ComputeBaselines();
        // Mean of 10, 10, 10 = 10 (not affected by the duplicate 99).
        Assert.Equal(10, baselines["NTFS Error"].Mean);
    }

    [Fact]
    public void Persistence_SurvivesReload()
    {
        for (int i = 0; i < 5; i++)
            _journal.RecordBoot($"b{i}", [new ErrorSource("NTFS Error", EventCategory.Filesystem, 20, null)]);

        // Create a new journal pointing to the same directory.
        var reloaded = new BootBaselineJournal(_tempDir);
        reloaded.Load();

        var baselines = reloaded.ComputeBaselines();
        Assert.Equal(20, baselines["NTFS Error"].Mean);
    }

    [Fact]
    public void MeanExcludingOutliers_AllSameValue_ReturnsThatValue()
    {
        var values = new List<double> { 10, 10, 10, 10, 10 };
        Assert.Equal(10, BootBaselineJournal.MeanExcludingOutliers(values));
    }

    [Fact]
    public void MeanExcludingOutliers_Empty_ReturnsZero()
    {
        Assert.Equal(0, BootBaselineJournal.MeanExcludingOutliers([]));
    }

    [Fact]
    public void MeanExcludingOutliers_SmallList_ReturnsSimpleMean()
    {
        var values = new List<double> { 10, 20, 30 };
        Assert.Equal(20, BootBaselineJournal.MeanExcludingOutliers(values));
    }

    [Fact]
    public void ComputeBaselines_ReturnsStdDev()
    {
        _journal.RecordBoot("b1", [new ErrorSource("X", EventCategory.Filesystem, 10, null)]);
        _journal.RecordBoot("b2", [new ErrorSource("X", EventCategory.Filesystem, 20, null)]);
        _journal.RecordBoot("b3", [new ErrorSource("X", EventCategory.Filesystem, 30, null)]);
        _journal.RecordBoot("b4", [new ErrorSource("X", EventCategory.Filesystem, 20, null)]);

        var stat = _journal.ComputeBaselines()["X"];
        // Population σ of {10, 20, 30, 20} = √50 ≈ 7.07
        Assert.InRange(stat.StdDev, 7.0, 7.2);
        Assert.Equal(4, stat.BootCount);
        Assert.Equal(4, stat.NonZeroBoots);
    }

    [Fact]
    public void ComputeBaselines_TracksNonZeroBoots()
    {
        _journal.RecordBoot("b1", [new ErrorSource("X", EventCategory.Filesystem, 0, null)]);
        _journal.RecordBoot("b2", [new ErrorSource("X", EventCategory.Filesystem, 5, null)]);
        _journal.RecordBoot("b3", [new ErrorSource("X", EventCategory.Filesystem, 0, null)]);
        _journal.RecordBoot("b4", [new ErrorSource("X", EventCategory.Filesystem, 0, null)]);

        var stat = _journal.ComputeBaselines()["X"];
        Assert.Equal(1, stat.NonZeroBoots);
        Assert.Equal(4, stat.BootCount);
    }

    [Fact]
    public void StdDevExcludingOutliers_ConstantValues_ReturnsZero()
    {
        var values = new List<double> { 10, 10, 10, 10, 10 };
        Assert.Equal(0, BootBaselineJournal.StdDevExcludingOutliers(values));
    }

    [Fact]
    public void StdDevExcludingOutliers_SingleValue_ReturnsZero()
    {
        Assert.Equal(0, BootBaselineJournal.StdDevExcludingOutliers([5]));
    }

    [Fact]
    public void RecordBoot_TrimsToMax50()
    {
        for (int i = 0; i < 55; i++)
            _journal.RecordBoot($"b{i}", [new ErrorSource("X", EventCategory.Filesystem, i, null)]);

        // Reload and check — should only have 50 entries.
        var reloaded = new BootBaselineJournal(_tempDir);
        reloaded.Load();
        var baselines = reloaded.ComputeBaselines();
        // The first 5 boots (0-4) should be trimmed. Mean of 5..54 = 29.5
        Assert.True(baselines.ContainsKey("X"));
        Assert.InRange(baselines["X"].Mean, 28, 32);
    }
}
