using RAMWatch.Core.Models;
using RAMWatch.Service.Services;
using Xunit;

namespace RAMWatch.Tests;

public class MinimumComputerTests
{
    private static TimingSnapshot MakeSnapshot(
        int memClock = 1800, int cl = 16, int rfc = 610, int refi = 65535,
        string? snapshotId = null, string? eraId = null) =>
        new()
        {
            SnapshotId = snapshotId ?? Guid.NewGuid().ToString("N"),
            Timestamp = DateTime.UtcNow,
            BootId = "test",
            MemClockMhz = memClock,
            FclkMhz = 1900, UclkMhz = 1900,
            CL = cl, RCDRD = 20, RCDWR = 20, RP = 20, RAS = 38, RC = 58, CWL = 16,
            RFC = rfc, RFC2 = 396, RFC4 = 274,
            RRDS = 4, RRDL = 8, FAW = 24, WTRS = 4, WTRL = 8, WR = 12, RTP = 12,
            RDRDSCL = 4, WRWRSCL = 4,
            RDRDSC = 1, RDRDSD = 5, RDRDDD = 4,
            WRWRSC = 1, WRWRSD = 7, WRWRDD = 6,
            RDWR = 9, WRRD = 2,
            REFI = refi, CKE = 1, STAG = 255, MOD = 29, MRD = 8,
            EraId = eraId
        };

    private static ValidationResult MakeValidation(string snapshotId, bool passed, string? eraId = null) =>
        new()
        {
            Timestamp = DateTime.UtcNow,
            BootId = "test",
            TestTool = "Karhu",
            MetricName = "coverage",
            MetricValue = 1000,
            MetricUnit = "%",
            Passed = passed,
            ActiveSnapshotId = snapshotId,
            EraId = eraId
        };

    [Fact]
    public void Compute_SingleSnapshot_ReturnsSameValues()
    {
        var snap = MakeSnapshot(cl: 16, rfc: 610);
        var result = MinimumComputer.Compute([snap], []);

        Assert.Single(result);
        Assert.Equal(1800, result[0].MemClockMhz);
        Assert.Equal(1, result[0].PostedBootCount);
        Assert.Equal(0, result[0].ValidatedBootCount);
        Assert.Equal(16, result[0].BestPosted["CL"]);
        Assert.Equal(610, result[0].BestPosted["RFC"]);
        Assert.Empty(result[0].BestValidated);
    }

    [Fact]
    public void Compute_TighterValueWins()
    {
        var snaps = new[]
        {
            MakeSnapshot(cl: 16, rfc: 610),
            MakeSnapshot(cl: 14, rfc: 577),
            MakeSnapshot(cl: 18, rfc: 700)
        };
        var result = MinimumComputer.Compute(snaps, []);

        Assert.Equal(14, result[0].BestPosted["CL"]);
        Assert.Equal(577, result[0].BestPosted["RFC"]);
    }

    [Fact]
    public void Compute_RefiHigherIsBetter()
    {
        var snaps = new[]
        {
            MakeSnapshot(refi: 60000),
            MakeSnapshot(refi: 65535),
            MakeSnapshot(refi: 50000)
        };
        var result = MinimumComputer.Compute(snaps, []);

        // REFI: higher is better
        Assert.Equal(65535, result[0].BestPosted["REFI"]);
    }

    [Fact]
    public void Compute_ValidatedOnly_FromPassingTests()
    {
        var snapA = MakeSnapshot(cl: 14, snapshotId: "a");
        var snapB = MakeSnapshot(cl: 16, snapshotId: "b");

        var validations = new[]
        {
            MakeValidation("b", passed: true), // CL 16 is validated
            MakeValidation("a", passed: false)  // CL 14 failed test
        };

        var result = MinimumComputer.Compute([snapA, snapB], validations);

        Assert.Equal(14, result[0].BestPosted["CL"]); // tightest posted
        Assert.Equal(16, result[0].BestValidated["CL"]); // tightest validated (only b passed)
        Assert.Equal(1, result[0].ValidatedBootCount);
    }

    [Fact]
    public void Compute_GroupsByFrequency()
    {
        var snaps = new[]
        {
            MakeSnapshot(memClock: 1800, cl: 16),
            MakeSnapshot(memClock: 1900, cl: 18),
            MakeSnapshot(memClock: 1800, cl: 14)
        };
        var result = MinimumComputer.Compute(snaps, []);

        Assert.Equal(2, result.Count);
        // Sorted descending by frequency
        Assert.Equal(1900, result[0].MemClockMhz);
        Assert.Equal(1800, result[1].MemClockMhz);
        Assert.Equal(18, result[0].BestPosted["CL"]);
        Assert.Equal(14, result[1].BestPosted["CL"]);
    }

    [Fact]
    public void Compute_FiltersByEra()
    {
        var snaps = new[]
        {
            MakeSnapshot(cl: 14, eraId: "era1"),
            MakeSnapshot(cl: 18, eraId: "era2"),
            MakeSnapshot(cl: 16, eraId: "era1")
        };
        var result = MinimumComputer.Compute(snaps, [], eraId: "era1");

        Assert.Single(result);
        Assert.Equal(14, result[0].BestPosted["CL"]);
        Assert.Equal(2, result[0].PostedBootCount);
    }

    [Fact]
    public void Compute_SkipsZeroCl()
    {
        var snaps = new[]
        {
            MakeSnapshot(cl: 0), // implausible — failed read
            MakeSnapshot(cl: 16)
        };
        var result = MinimumComputer.Compute(snaps, []);

        Assert.Single(result);
        Assert.Equal(16, result[0].BestPosted["CL"]);
        Assert.Equal(1, result[0].PostedBootCount);
    }

    [Fact]
    public void Compute_EmptyInput_ReturnsEmpty()
    {
        var result = MinimumComputer.Compute([], []);
        Assert.Empty(result);
    }
}
