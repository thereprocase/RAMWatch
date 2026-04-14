using Xunit;
using RAMWatch.Core;
using RAMWatch.Core.Models;

namespace RAMWatch.Tests;

public class SnapshotDisplayNameTests
{
    // ── BuildLookup ───────────────────────────────────────────────────────────

    [Fact]
    public void BuildLookup_Null_ReturnsEmpty()
    {
        var result = SnapshotDisplayName.BuildLookup(null);
        Assert.Empty(result);
    }

    [Fact]
    public void BuildLookup_ValidationWithoutSnapshotId_IsIgnored()
    {
        var validations = new List<ValidationResult>
        {
            MakeValidation(null, passed: true, timestamp: DateTime.UtcNow)
        };

        var lookup = SnapshotDisplayName.BuildLookup(validations);

        Assert.Empty(lookup);
    }

    [Fact]
    public void BuildLookup_SingleValidation_KeyedBySnapshotId()
    {
        var validations = new List<ValidationResult>
        {
            MakeValidation("snap-001", passed: true, timestamp: DateTime.UtcNow)
        };

        var lookup = SnapshotDisplayName.BuildLookup(validations);

        Assert.True(lookup.ContainsKey("snap-001"));
    }

    [Fact]
    public void BuildLookup_MultipleValidationsSameSnapshot_KeepsMostRecent()
    {
        var t0 = new DateTime(2026, 4, 14, 10, 0, 0, DateTimeKind.Utc);
        var validations = new List<ValidationResult>
        {
            MakeValidation("snap-001", passed: false, timestamp: t0,              metricValue: 100),
            MakeValidation("snap-001", passed: true,  timestamp: t0.AddHours(1),  metricValue: 8000),
            MakeValidation("snap-001", passed: true,  timestamp: t0.AddMinutes(30), metricValue: 5000),
        };

        var lookup = SnapshotDisplayName.BuildLookup(validations);

        Assert.Single(lookup);
        // t0.AddHours(1) is the newest — metricValue 8000.
        Assert.Equal(8000, lookup["snap-001"].MetricValue);
    }

    [Fact]
    public void BuildLookup_DifferentSnapshots_EachGetOwnEntry()
    {
        var t0 = DateTime.UtcNow;
        var validations = new List<ValidationResult>
        {
            MakeValidation("snap-001", passed: true,  timestamp: t0),
            MakeValidation("snap-002", passed: false, timestamp: t0),
        };

        var lookup = SnapshotDisplayName.BuildLookup(validations);

        Assert.Equal(2, lookup.Count);
        Assert.True(lookup["snap-001"].Passed);
        Assert.False(lookup["snap-002"].Passed);
    }

    // ── Build — custom label ──────────────────────────────────────────────────

    [Fact]
    public void Build_CustomLabel_ReturnedAsIs()
    {
        var snap = MakeSnapshot("snap-001", label: "8000c36 my stable config");
        var name = SnapshotDisplayName.Build(snap, lookup: null);
        Assert.Equal("8000c36 my stable config", name);
    }

    [Fact]
    public void Build_CustomLabel_TakesPriorityOverMatchingValidation()
    {
        var snap = MakeSnapshot("snap-001", label: "daily driver");
        var lookup = SingleLookup("snap-001", passed: true, tool: "Karhu", metricValue: 8000, unit: "%");

        var name = SnapshotDisplayName.Build(snap, lookup);

        Assert.Equal("daily driver", name);
    }

    // ── Build — validation match (pass) ──────────────────────────────────────

    [Fact]
    public void Build_PassingValidation_ShowsToolMetricPass()
    {
        var snap = MakeSnapshot("snap-001", label: "Auto 2026-04-14 13:39");
        var lookup = SingleLookup("snap-001", passed: true, tool: "Karhu", metricValue: 8000,
            unit: "%", timestamp: new DateTime(2026, 4, 14, 10, 0, 0, DateTimeKind.Utc));

        var name = SnapshotDisplayName.Build(snap, lookup);

        Assert.Equal("4/14 Karhu 8000% PASS", name);
    }

    [Fact]
    public void Build_PassingValidation_DecimalMetricFormattedCompactly()
    {
        var snap = MakeSnapshot("snap-001", label: "Auto 2026-04-14 13:39");
        var lookup = SingleLookup("snap-001", passed: true, tool: "TM5", metricValue: 1.5,
            unit: "x", timestamp: new DateTime(2026, 4, 14, 10, 0, 0, DateTimeKind.Utc));

        var name = SnapshotDisplayName.Build(snap, lookup);

        Assert.Equal("4/14 TM5 1.5x PASS", name);
    }

    // ── Build — validation match (fail) ──────────────────────────────────────

    [Fact]
    public void Build_FailingValidation_ShowsToolFailErrors()
    {
        var snap = MakeSnapshot("snap-001", label: "Auto 2026-04-14 13:39");
        var lookup = SingleLookup("snap-001", passed: false, tool: "TM5", metricValue: 0,
            unit: "", timestamp: new DateTime(2026, 4, 14, 10, 0, 0, DateTimeKind.Utc),
            errorCount: 3);

        var name = SnapshotDisplayName.Build(snap, lookup);

        Assert.Equal("4/14 TM5 FAIL (3 errors)", name);
    }

    [Fact]
    public void Build_FailingValidation_NoErrors_SuffixOmitted()
    {
        var snap = MakeSnapshot("snap-001", label: "Auto 2026-04-14 13:39");
        var lookup = SingleLookup("snap-001", passed: false, tool: "Karhu", metricValue: 0,
            unit: "", timestamp: new DateTime(2026, 4, 14, 10, 0, 0, DateTimeKind.Utc),
            errorCount: 0);

        var name = SnapshotDisplayName.Build(snap, lookup);

        Assert.Equal("4/14 Karhu FAIL", name);
    }

    // ── Build — no validation, fallback label ─────────────────────────────────

    [Fact]
    public void Build_NoValidation_AutoLabel_KeepsOriginalLabel()
    {
        var snap = MakeSnapshot("snap-001", label: "Auto 2026-04-14 13:39");
        var name = SnapshotDisplayName.Build(snap, lookup: null);
        Assert.Equal("Auto 2026-04-14 13:39", name);
    }

    [Fact]
    public void Build_NoValidation_ManualLabel_KeepsOriginalLabel()
    {
        var snap = MakeSnapshot("snap-001", label: "Manual save");
        var name = SnapshotDisplayName.Build(snap, lookup: null);
        Assert.Equal("Manual save", name);
    }

    [Fact]
    public void Build_NoValidation_EmptyLabel_UsesTimestampPlaceholder()
    {
        var ts = new DateTime(2026, 4, 14, 13, 39, 0, DateTimeKind.Local);
        var snap = MakeSnapshot("snap-001", label: "", timestamp: ts.ToUniversalTime());
        var name = SnapshotDisplayName.Build(snap, lookup: null);
        Assert.Equal("Snapshot 04/14 13:39", name);
    }

    [Fact]
    public void Build_NoMatchingValidation_FallsBackToLabel()
    {
        var snap = MakeSnapshot("snap-001", label: "Auto 2026-04-14 13:39");
        var lookup = SingleLookup("snap-999", passed: true, tool: "Karhu", metricValue: 8000, unit: "%");

        var name = SnapshotDisplayName.Build(snap, lookup);

        Assert.Equal("Auto 2026-04-14 13:39", name);
    }

    // ── Build — multiple validations, most recent wins ────────────────────────

    [Fact]
    public void Build_MultipleValidations_MostRecentWins()
    {
        var snap = MakeSnapshot("snap-001", label: "Auto 2026-04-14 13:39");
        var t0 = new DateTime(2026, 4, 14, 10, 0, 0, DateTimeKind.Utc);

        var lookup = SnapshotDisplayName.BuildLookup(new List<ValidationResult>
        {
            MakeValidation("snap-001", passed: false, timestamp: t0,             metricValue: 0, tool: "TM5",   unit: ""),
            MakeValidation("snap-001", passed: true,  timestamp: t0.AddHours(2), metricValue: 8000, tool: "Karhu", unit: "%"),
        });

        var name = SnapshotDisplayName.Build(snap, lookup);

        // Newer (pass) result wins.
        Assert.Equal("4/14 Karhu 8000% PASS", name);
    }

    // ── BuildLkg ──────────────────────────────────────────────────────────────

    [Fact]
    public void BuildLkg_NoValidation_ReturnsPlainLkg()
    {
        var lkg = MakeSnapshot("snap-lkg", label: "");
        var name = SnapshotDisplayName.BuildLkg(lkg, lookup: null);
        Assert.Equal("LKG", name);
    }

    [Fact]
    public void BuildLkg_PassingValidation_IncludesQualifyingResult()
    {
        var lkg = MakeSnapshot("snap-lkg", label: "Auto 2026-04-14 00:00");
        var lookup = SingleLookup("snap-lkg", passed: true, tool: "Karhu", metricValue: 12400, unit: "%");

        var name = SnapshotDisplayName.BuildLkg(lkg, lookup);

        Assert.Equal("LKG (Karhu 12400% PASS)", name);
    }

    [Fact]
    public void BuildLkg_FailingValidation_ReturnsPlainLkg()
    {
        // A failing validation should not be shown in the LKG label — LKG is
        // by definition a passing configuration, but the most-recent validation
        // might be a retest that failed. Only pass results annotate the LKG label.
        var lkg = MakeSnapshot("snap-lkg", label: "Auto 2026-04-14 00:00");
        var lookup = SingleLookup("snap-lkg", passed: false, tool: "TM5", metricValue: 0, unit: "");

        var name = SnapshotDisplayName.BuildLkg(lkg, lookup);

        Assert.Equal("LKG", name);
    }

    [Fact]
    public void BuildLkg_NoMatchingValidation_ReturnsPlainLkg()
    {
        var lkg = MakeSnapshot("snap-lkg", label: "Auto 2026-04-14 00:00");
        var lookup = SingleLookup("snap-other", passed: true, tool: "Karhu", metricValue: 8000, unit: "%");

        var name = SnapshotDisplayName.BuildLkg(lkg, lookup);

        Assert.Equal("LKG", name);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static TimingSnapshot MakeSnapshot(
        string id,
        string label,
        DateTime? timestamp = null)
    {
        return new TimingSnapshot
        {
            SnapshotId  = id,
            Timestamp   = timestamp ?? DateTime.UtcNow,
            BootId      = "boot_0414",
            Label       = label,
            MemClockMhz = 4000,
            CL          = 36,
        };
    }

    private static ValidationResult MakeValidation(
        string? snapshotId,
        bool passed,
        DateTime? timestamp = null,
        double metricValue = 8000,
        string tool = "Karhu",
        string unit = "%",
        int errorCount = 0)
    {
        return new ValidationResult
        {
            Timestamp        = timestamp ?? DateTime.UtcNow,
            BootId           = "boot_0414",
            TestTool         = tool,
            MetricName       = "coverage",
            MetricValue      = metricValue,
            MetricUnit       = unit,
            Passed           = passed,
            ErrorCount       = errorCount,
            ActiveSnapshotId = snapshotId,
        };
    }

    /// <summary>
    /// Convenience: creates a lookup with a single entry.
    /// </summary>
    private static IReadOnlyDictionary<string, ValidationResult> SingleLookup(
        string snapshotId,
        bool passed,
        string tool = "Karhu",
        double metricValue = 8000,
        string unit = "%",
        DateTime? timestamp = null,
        int errorCount = 0)
    {
        return SnapshotDisplayName.BuildLookup(new List<ValidationResult>
        {
            MakeValidation(snapshotId, passed, timestamp, metricValue, tool, unit, errorCount)
        });
    }
}
