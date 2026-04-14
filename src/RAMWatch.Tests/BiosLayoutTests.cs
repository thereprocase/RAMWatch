using Xunit;
using RAMWatch.Core.Models;
using RAMWatch.Service.Services;

namespace RAMWatch.Tests;

/// <summary>
/// Tests for BiosLayouts — vendor detection, layout completeness, and markdown ordering.
/// </summary>
public class BiosLayoutTests
{
    // All timing field names that must appear in every vendor layout.
    // This list matches the fields tracked in TimingSnapshot (excluding clocks and voltages,
    // which are displayed in their own fixed sections).
    private static readonly string[] AllTimingFields =
    [
        "CL", "RCDRD", "RCDWR", "RP", "RAS", "RC", "CWL",
        "RFC", "RFC2", "RFC4",
        "RRDS", "RRDL", "FAW", "WTRS", "WTRL", "WR", "RTP",
        "RDRDSCL", "WRWRSCL",
        "RDRDSC", "RDRDSD", "RDRDDD",
        "WRWRSC", "WRWRSD", "WRWRDD",
        "RDWR", "WRRD",
        "REFI", "CKE", "STAG", "MOD", "MRD",
        "PHYRDL_A", "PHYRDL_B",
        "GDM", "Cmd2T",
    ];

    // -----------------------------------------------------------------------
    // DetectVendor — live registry call
    // -----------------------------------------------------------------------

    [Fact]
    public void DetectVendor_ReturnsConcreteVendor()
    {
        // In a test environment the registry key may be missing or return an
        // unrecognised string. Both are valid — the only constraint is that
        // the result is never Auto (Auto is an input sentinel, not an output).
        var vendor = BiosLayouts.DetectVendor();
        Assert.NotEqual(BoardVendor.Auto, vendor);
    }

    // -----------------------------------------------------------------------
    // ClassifyManufacturer — deterministic string matching
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData("MSI",                               BoardVendor.MSI)]
    [InlineData("Micro-Star International",          BoardVendor.MSI)]
    [InlineData("micro-star",                        BoardVendor.MSI)]
    [InlineData("ASUSTeK COMPUTER INC.",             BoardVendor.ASUS)]
    [InlineData("ASUS",                              BoardVendor.ASUS)]
    [InlineData("Gigabyte Technology Co., Ltd.",     BoardVendor.Gigabyte)]
    [InlineData("gigabyte",                          BoardVendor.Gigabyte)]
    [InlineData("ASRock",                            BoardVendor.ASRock)]
    [InlineData("ASROCK INCORPORATION",              BoardVendor.ASRock)]
    [InlineData("",                                  BoardVendor.Default)]
    [InlineData("Unknown OEM",                       BoardVendor.Default)]
    [InlineData("Biostar",                           BoardVendor.Default)]
    public void ClassifyManufacturer_ReturnsExpectedVendor(string manufacturer, BoardVendor expected)
    {
        var result = BiosLayouts.ClassifyManufacturer(manufacturer);
        Assert.Equal(expected, result);
    }

    // -----------------------------------------------------------------------
    // ParseSetting — string → enum round-trip
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData("Auto",     BoardVendor.Auto)]
    [InlineData("auto",     BoardVendor.Auto)]
    [InlineData("MSI",      BoardVendor.MSI)]
    [InlineData("ASUS",     BoardVendor.ASUS)]
    [InlineData("Gigabyte", BoardVendor.Gigabyte)]
    [InlineData("ASRock",   BoardVendor.ASRock)]
    [InlineData("Default",  BoardVendor.Default)]
    [InlineData("",         BoardVendor.Auto)]
    [InlineData(null,       BoardVendor.Auto)]
    [InlineData("bogus",    BoardVendor.Auto)]
    public void ParseSetting_RoundTrips(string? input, BoardVendor expected)
    {
        var result = BiosLayouts.ParseSetting(input);
        Assert.Equal(expected, result);
    }

    // -----------------------------------------------------------------------
    // Resolve — Auto resolves to a concrete vendor
    // -----------------------------------------------------------------------

    [Fact]
    public void Resolve_Auto_ReturnsConcreteVendor()
    {
        var result = BiosLayouts.Resolve(BoardVendor.Auto);
        Assert.NotEqual(BoardVendor.Auto, result);
    }

    [Theory]
    [InlineData(BoardVendor.MSI)]
    [InlineData(BoardVendor.ASUS)]
    [InlineData(BoardVendor.Gigabyte)]
    [InlineData(BoardVendor.ASRock)]
    [InlineData(BoardVendor.Default)]
    public void Resolve_NonAuto_ReturnsItself(BoardVendor vendor)
    {
        var result = BiosLayouts.Resolve(vendor);
        Assert.Equal(vendor, result);
    }

    // -----------------------------------------------------------------------
    // GetLayout — layout completeness
    // Each vendor layout must contain every timing field exactly once.
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData(BoardVendor.MSI)]
    [InlineData(BoardVendor.ASUS)]
    [InlineData(BoardVendor.Gigabyte)]
    [InlineData(BoardVendor.ASRock)]
    [InlineData(BoardVendor.Default)]
    public void GetLayout_ContainsAllTimingFields(BoardVendor vendor)
    {
        var layout = BiosLayouts.GetLayout(vendor);
        var allFieldsInLayout = layout.SelectMany(g => g.Fields).ToHashSet();

        var missing = AllTimingFields.Where(f => !allFieldsInLayout.Contains(f)).ToList();
        Assert.Empty(missing);
    }

    [Theory]
    [InlineData(BoardVendor.MSI)]
    [InlineData(BoardVendor.ASUS)]
    [InlineData(BoardVendor.Gigabyte)]
    [InlineData(BoardVendor.ASRock)]
    [InlineData(BoardVendor.Default)]
    public void GetLayout_NoFieldAppearsMoreThanOnce(BoardVendor vendor)
    {
        var layout = BiosLayouts.GetLayout(vendor);
        var allFields = layout.SelectMany(g => g.Fields).ToList();
        var duplicates = allFields
            .GroupBy(f => f)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        Assert.Empty(duplicates);
    }

    [Theory]
    [InlineData(BoardVendor.MSI)]
    [InlineData(BoardVendor.ASUS)]
    [InlineData(BoardVendor.Gigabyte)]
    [InlineData(BoardVendor.ASRock)]
    [InlineData(BoardVendor.Default)]
    public void GetLayout_HasAtLeastTwoGroups(BoardVendor vendor)
    {
        var layout = BiosLayouts.GetLayout(vendor);
        Assert.True(layout.Count >= 2, $"{vendor} layout should have at least 2 groups");
    }

    [Fact]
    public void GetLayout_MSI_HasPhyGroup()
    {
        var layout = BiosLayouts.GetLayout(BoardVendor.MSI);
        Assert.Contains(layout, g => g.Name == "PHY");
    }

    [Fact]
    public void GetLayout_ASUS_HasCwlGdmGroup()
    {
        var layout = BiosLayouts.GetLayout(BoardVendor.ASUS);
        Assert.Contains(layout, g => g.Name == "CWL/GDM");
    }

    [Fact]
    public void GetLayout_Gigabyte_HasSclGroup()
    {
        var layout = BiosLayouts.GetLayout(BoardVendor.Gigabyte);
        Assert.Contains(layout, g => g.Name == "SCL");
    }

    [Fact]
    public void GetLayout_ASRock_HasSubTimingsGroup()
    {
        var layout = BiosLayouts.GetLayout(BoardVendor.ASRock);
        Assert.Contains(layout, g => g.Name == "Sub Timings");
    }

    [Fact]
    public void GetLayout_Default_HasPrimariesGroup()
    {
        var layout = BiosLayouts.GetLayout(BoardVendor.Default);
        Assert.Contains(layout, g => g.Name == "Primaries");
    }

    // -----------------------------------------------------------------------
    // Markdown section ordering — CurrentMdBuilder
    // Each vendor layout must produce groups in the correct sequence.
    // -----------------------------------------------------------------------

    private static TimingSnapshot MakeSnapshot() =>
        new TimingSnapshot
        {
            SnapshotId  = "test",
            Timestamp   = new DateTime(2026, 4, 14, 0, 0, 0, DateTimeKind.Local),
            BootId      = "boot-test",
            MemClockMhz = 1800,
            FclkMhz     = 1800,
            UclkMhz     = 1800,
            CL          = 16,
            RCDRD       = 20,
            RCDWR       = 20,
            RP          = 20,
            RAS         = 42,
            RC          = 62,
            CWL         = 16,
            RFC         = 577,
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
            Cmd2T       = false,
        };

    [Theory]
    [InlineData(BoardVendor.MSI,      "## Primary",    "## tRFC")]
    [InlineData(BoardVendor.ASUS,     "## Primary",    "## tRFC")]
    [InlineData(BoardVendor.Gigabyte, "## Primary",    "## tRFC")]
    [InlineData(BoardVendor.ASRock,   "## Primary",    "## tRFC")]
    [InlineData(BoardVendor.Default,  "## Primaries",  "## tRFC")]
    public void CurrentMd_PrimaryGroupAppearsBeforesRfc(
        BoardVendor vendor, string primaryHeading, string rfcHeading)
    {
        string md = CurrentMdBuilder.Build(MakeSnapshot(), null, null, vendor);

        int primaryPos = md.IndexOf(primaryHeading, StringComparison.Ordinal);
        int rfcPos     = md.IndexOf(rfcHeading,     StringComparison.Ordinal);

        Assert.True(primaryPos >= 0, $"Expected '{primaryHeading}' in output");
        Assert.True(rfcPos     >= 0, $"Expected '{rfcHeading}' in output");
        Assert.True(primaryPos < rfcPos, $"Primary group must precede tRFC for {vendor}");
    }

    [Theory]
    [InlineData(BoardVendor.MSI)]
    [InlineData(BoardVendor.ASUS)]
    [InlineData(BoardVendor.Gigabyte)]
    [InlineData(BoardVendor.ASRock)]
    [InlineData(BoardVendor.Default)]
    public void CurrentMd_PhyGroupAppearsLast(BoardVendor vendor)
    {
        string md = CurrentMdBuilder.Build(MakeSnapshot(), null, null, vendor);

        int phyPos  = md.IndexOf("## PHY", StringComparison.Ordinal);
        int rfcPos  = md.IndexOf("## tRFC", StringComparison.Ordinal);

        Assert.True(phyPos >= 0, "Expected '## PHY' in output");
        Assert.True(rfcPos >= 0, "Expected '## tRFC' in output");
        Assert.True(phyPos > rfcPos, $"PHY group must appear after tRFC for {vendor}");
    }

    [Theory]
    [InlineData(BoardVendor.MSI)]
    [InlineData(BoardVendor.ASUS)]
    [InlineData(BoardVendor.Gigabyte)]
    [InlineData(BoardVendor.ASRock)]
    [InlineData(BoardVendor.Default)]
    public void CurrentMd_AllFieldsPresent(BoardVendor vendor)
    {
        string md = CurrentMdBuilder.Build(MakeSnapshot(), null, null, vendor);

        // Every timing field name should appear in the output (as "FieldName = value").
        var missing = AllTimingFields
            .Where(f => !md.Contains($"{f} = ", StringComparison.Ordinal))
            .ToList();

        Assert.Empty(missing);
    }

    [Fact]
    public void CurrentMd_MSI_GdmInGdmCmdGroup()
    {
        // MSI puts GDM in the "GDM/Cmd" group, which appears after Primary.
        string md = CurrentMdBuilder.Build(MakeSnapshot(), null, null, BoardVendor.MSI);

        int primaryPos = md.IndexOf("## Primary", StringComparison.Ordinal);
        int gdmCmdPos  = md.IndexOf("## GDM/Cmd", StringComparison.Ordinal);
        int gdmLinePos = md.IndexOf("GDM = ",     StringComparison.Ordinal);

        Assert.True(gdmCmdPos > primaryPos, "GDM/Cmd group must follow Primary");
        // GDM field must appear after the GDM/Cmd heading
        Assert.True(gdmLinePos > gdmCmdPos, "GDM value must appear inside GDM/Cmd group");
    }

    [Fact]
    public void CurrentMd_ASUS_CwlInCwlGdmGroup()
    {
        // ASUS puts CWL in a separate CWL/GDM group (not in Primary).
        string md = CurrentMdBuilder.Build(MakeSnapshot(), null, null, BoardVendor.ASUS);

        int primaryPos = md.IndexOf("## Primary",  StringComparison.Ordinal);
        int cwlGdmPos  = md.IndexOf("## CWL/GDM",  StringComparison.Ordinal);
        int cwlLinePos = md.IndexOf("CWL = ",       StringComparison.Ordinal);

        Assert.True(cwlGdmPos > primaryPos, "CWL/GDM group must follow Primary");
        Assert.True(cwlLinePos > cwlGdmPos, "CWL value must appear inside CWL/GDM group");
    }

    [Fact]
    public void CurrentMd_ASRock_AllSecondaryCombinedInSubTimings()
    {
        // ASRock flattens everything after tRFC into "Sub Timings".
        string md = CurrentMdBuilder.Build(MakeSnapshot(), null, null, BoardVendor.ASRock);

        int subTimingsPos = md.IndexOf("## Sub Timings", StringComparison.Ordinal);
        int rdrdSclPos    = md.IndexOf("RDRDSCL = ",     StringComparison.Ordinal);
        int stagePos      = md.IndexOf("STAG = ",        StringComparison.Ordinal);

        Assert.True(subTimingsPos >= 0, "Expected '## Sub Timings' in output");
        // Both an SC-latency field and a misc field must appear after Sub Timings heading
        Assert.True(rdrdSclPos > subTimingsPos, "RDRDSCL must appear inside Sub Timings");
        Assert.True(stagePos > subTimingsPos,   "STAG must appear inside Sub Timings");
    }
}
