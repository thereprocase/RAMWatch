using Xunit;
using RAMWatch.Core.Models;
using RAMWatch.Service.Services;

namespace RAMWatch.Tests;

/// <summary>
/// Tests for CurrentMdBuilder and LkgMdBuilder (pure functions — no I/O).
/// </summary>
public class MarkdownBuilderTests
{
    // -----------------------------------------------------------------------
    // Test data
    // -----------------------------------------------------------------------

    private static TimingSnapshot MakeSnapshot(int cl = 16, int rfc = 577) =>
        new TimingSnapshot
        {
            SnapshotId  = "snap-test",
            Timestamp   = new DateTime(2026, 4, 14, 0, 0, 0, DateTimeKind.Local),
            BootId      = "boot-test",
            MemClockMhz = 1800,
            FclkMhz     = 1800,
            UclkMhz     = 1800,
            CL          = cl,
            RCDRD       = 20,
            RCDWR       = 20,
            RP          = 20,
            RAS         = 42,
            RC          = 62,
            CWL         = 16,
            RFC         = rfc,
            RFC2        = 375,
            RFC4        = 260,
            RRDS        = 4,
            RRDL        = 6,
            FAW         = 20,
            WTRS        = 4,
            WTRL        = 12,
            WR          = 24,
            RTP         = 12,
            RDRDSCL     = 4,
            WRWRSCL     = 4,
            RDRDSC      = 1,
            RDRDSD      = 4,
            RDRDDD      = 4,
            WRWRSC      = 1,
            WRWRSD      = 6,
            WRWRDD      = 6,
            RDWR        = 9,
            WRRD        = 2,
            REFI        = 14029,
            CKE         = 5,
            STAG        = 255,
            MOD         = 24,
            MRD         = 8,
            PHYRDL_A    = 28,
            PHYRDL_B    = 26,
            GDM         = false,
            Cmd2T       = false
        };

    private static DesignationMap MakeDesignations() =>
        new DesignationMap
        {
            Designations = new Dictionary<string, TimingDesignation>
            {
                ["CL"]    = TimingDesignation.Manual,
                ["RCDRD"] = TimingDesignation.Manual,
                ["RCDWR"] = TimingDesignation.Manual,
                ["RP"]    = TimingDesignation.Manual,
                ["RAS"]   = TimingDesignation.Manual,
                ["RC"]    = TimingDesignation.Manual,
                ["CWL"]   = TimingDesignation.Manual,
                ["RFC"]   = TimingDesignation.Manual,
                ["RFC2"]  = TimingDesignation.Manual,
                ["RFC4"]  = TimingDesignation.Manual,
                ["RRDS"]  = TimingDesignation.Auto,
                ["RRDL"]  = TimingDesignation.Auto,
                ["FAW"]   = TimingDesignation.Auto,
                ["WTRS"]  = TimingDesignation.Auto,
                ["WTRL"]  = TimingDesignation.Auto,
                ["WR"]    = TimingDesignation.Auto,
            }
        };

    private static ValidationResult MakeValidation(bool passed = true) =>
        new ValidationResult
        {
            Timestamp       = new DateTime(2026, 4, 14, 0, 0, 0, DateTimeKind.Local),
            BootId          = "boot-test",
            TestTool        = "Karhu",
            MetricName      = "coverage",
            MetricValue     = 12400,
            MetricUnit      = "%",
            Passed          = passed
        };

    // -----------------------------------------------------------------------
    // CurrentMdBuilder tests
    // -----------------------------------------------------------------------

    [Fact]
    public void CurrentMd_ContainsHeader()
    {
        string md = CurrentMdBuilder.Build(MakeSnapshot(), null, null);
        Assert.Contains("# CURRENT — RAMWatch", md);
    }

    [Fact]
    public void CurrentMd_ContainsClockSection()
    {
        string md = CurrentMdBuilder.Build(MakeSnapshot(), null, null);
        Assert.Contains("## Clock", md);
        Assert.Contains("DDR4-3600", md);
        Assert.Contains("MCLK 1800", md);
        Assert.Contains("FCLK 1800", md);
        Assert.Contains("UCLK 1800", md);
    }

    [Fact]
    public void CurrentMd_NullDesignations_AllTimingsUnderBiosSettings()
    {
        string md = CurrentMdBuilder.Build(MakeSnapshot(), null, null);
        Assert.Contains("## BIOS Settings (enter these manually)", md);
        // Should NOT contain an Auto-trained section when designations are null
        Assert.DoesNotContain("## Auto-trained", md);
    }

    [Fact]
    public void CurrentMd_WithDesignations_SplitsManualAndAuto()
    {
        string md = CurrentMdBuilder.Build(MakeSnapshot(), MakeDesignations(), null);
        Assert.Contains("## BIOS Settings (enter these manually)", md);
        Assert.Contains("## Auto-trained (leave on Auto in BIOS)", md);
        // Manual timings appear under BIOS Settings
        Assert.Contains("CL = 16", md);
        // Auto timings appear under Auto-trained
        Assert.Contains("RRDS = 4", md);
    }

    [Fact]
    public void CurrentMd_ManualTimings_NotInAutoSection()
    {
        string md = CurrentMdBuilder.Build(MakeSnapshot(), MakeDesignations(), null);
        // CL is Manual — should appear before the Auto-trained section
        int clPos   = md.IndexOf("CL = 16", StringComparison.Ordinal);
        int autoPos = md.IndexOf("## Auto-trained", StringComparison.Ordinal);
        Assert.True(clPos < autoPos, "CL (Manual) should appear before the Auto-trained section");
    }

    [Fact]
    public void CurrentMd_WithLastValidation_ContainsValidationSection()
    {
        string md = CurrentMdBuilder.Build(MakeSnapshot(), null, MakeValidation());
        Assert.Contains("## Last Validation", md);
        Assert.Contains("Karhu", md);
        Assert.Contains("12400%", md);
        Assert.Contains("PASS", md);
    }

    [Fact]
    public void CurrentMd_NoValidation_NoValidationSection()
    {
        string md = CurrentMdBuilder.Build(MakeSnapshot(), null, null);
        Assert.DoesNotContain("## Last Validation", md);
    }

    [Fact]
    public void CurrentMd_FailedValidation_ShowsFail()
    {
        string md = CurrentMdBuilder.Build(MakeSnapshot(), null, MakeValidation(passed: false));
        Assert.Contains("FAIL", md);
    }

    [Fact]
    public void CurrentMd_CyclesMetric_FormattedWithUnit()
    {
        var v = new ValidationResult
        {
            Timestamp   = DateTime.UtcNow,
            BootId      = "b",
            TestTool    = "TM5",
            MetricName  = "cycles",
            MetricValue = 30,
            MetricUnit  = "cycles",
            Passed      = true
        };
        string md = CurrentMdBuilder.Build(MakeSnapshot(), null, v);
        Assert.Contains("30 cycles", md);
    }

    // -----------------------------------------------------------------------
    // LkgMdBuilder tests
    // -----------------------------------------------------------------------

    [Fact]
    public void LkgMd_NullSnapshot_ReturnsNull()
    {
        string? md = LkgMdBuilder.Build(null, null, null);
        Assert.Null(md);
    }

    [Fact]
    public void LkgMd_ContainsLkgHeader()
    {
        string? md = LkgMdBuilder.Build(MakeSnapshot(), null, null);
        Assert.NotNull(md);
        Assert.Contains("# LKG (Last Known Good) — RAMWatch", md);
    }

    [Fact]
    public void LkgMd_ContainsRevertNote()
    {
        string? md = LkgMdBuilder.Build(MakeSnapshot(), null, null);
        Assert.NotNull(md);
        Assert.Contains("## Revert to these settings if unstable", md);
    }

    [Fact]
    public void LkgMd_ContainsClockSection()
    {
        string? md = LkgMdBuilder.Build(MakeSnapshot(), null, null);
        Assert.NotNull(md);
        Assert.Contains("## Clock", md);
        Assert.Contains("DDR4-3600", md);
    }

    [Fact]
    public void LkgMd_WithDesignations_SplitsManualAndAuto()
    {
        string? md = LkgMdBuilder.Build(MakeSnapshot(), MakeDesignations(), null);
        Assert.NotNull(md);
        Assert.Contains("## BIOS Settings (enter these manually)", md);
        Assert.Contains("## Auto-trained (leave on Auto in BIOS)", md);
    }

    [Fact]
    public void LkgMd_WithValidation_ShowsQualifiedBySection()
    {
        string? md = LkgMdBuilder.Build(MakeSnapshot(), null, MakeValidation());
        Assert.NotNull(md);
        Assert.Contains("## Qualified By", md);
        Assert.Contains("Karhu", md);
        Assert.Contains("PASS", md);
    }

    [Fact]
    public void LkgMd_NullDesignations_AllTimingsUnderBiosSettings()
    {
        string? md = LkgMdBuilder.Build(MakeSnapshot(), null, null);
        Assert.NotNull(md);
        Assert.Contains("## BIOS Settings (enter these manually)", md);
        Assert.DoesNotContain("## Auto-trained", md);
    }
}
