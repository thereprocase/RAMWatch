using Xunit;
using RAMWatch.Core;
using RAMWatch.Core.Models;

namespace RAMWatch.Tests;

public class ChangeSeverityClassifierTests
{
    private static ConfigChange Make(params string[] fields)
    {
        var deltas = new Dictionary<string, TimingDelta>(StringComparer.Ordinal);
        foreach (var f in fields)
            deltas[f] = new TimingDelta("1", "2");

        return new ConfigChange
        {
            ChangeId  = Guid.NewGuid().ToString("N"),
            Timestamp = DateTime.UtcNow,
            BootId    = "boot_000001",
            Changes   = deltas,
        };
    }

    [Theory]
    [InlineData("CL")]
    [InlineData("RCDRD")]
    [InlineData("RCDWR")]
    [InlineData("RP")]
    [InlineData("RAS")]
    [InlineData("RC")]
    [InlineData("CWL")]
    [InlineData("RFC")]
    [InlineData("RFC2")]
    [InlineData("RFC4")]
    [InlineData("MemClockMhz")]
    [InlineData("FclkMhz")]
    [InlineData("UclkMhz")]
    [InlineData("VSoc")]
    [InlineData("VCore")]
    [InlineData("VDimm")]
    [InlineData("VDDP")]
    [InlineData("VDDG_IOD")]
    [InlineData("VDDG_CCD")]
    [InlineData("Vtt")]
    [InlineData("Vpp")]
    [InlineData("GDM")]
    [InlineData("Cmd2T")]
    [InlineData("PowerDown")]
    public void Major_field_alone_classifies_as_Major(string key)
    {
        Assert.Equal(ChangeSeverity.Major, ChangeSeverityClassifier.Classify(Make(key)));
    }

    [Theory]
    [InlineData("PHYRDL_A")]
    [InlineData("PHYRDL_B")]
    [InlineData("RRDS")]
    [InlineData("RRDL")]
    [InlineData("FAW")]
    [InlineData("WTRS")]
    [InlineData("WTRL")]
    [InlineData("WR")]
    [InlineData("RTP")]
    [InlineData("REFI")]
    [InlineData("MRD")]
    [InlineData("ProcODT")]
    [InlineData("ClkDrvStren")]
    [InlineData("AddrCmdDrvStren")]
    [InlineData("CsOdtCmdDrvStren")]
    [InlineData("CkeDrvStren")]
    [InlineData("RttNom")]
    [InlineData("RttWr")]
    [InlineData("RttPark")]
    public void Minor_only_field_classifies_as_Minor(string key)
    {
        Assert.Equal(ChangeSeverity.Minor, ChangeSeverityClassifier.Classify(Make(key)));
    }

    [Fact]
    public void Mixed_delta_with_any_major_field_is_Major()
    {
        var change = Make("PHYRDL_A", "RRDS", "CL", "RTP");
        Assert.Equal(ChangeSeverity.Major, ChangeSeverityClassifier.Classify(change));
    }

    [Fact]
    public void Pure_minor_delta_is_Minor()
    {
        var change = Make("PHYRDL_A", "RRDS", "RTP", "ProcODT");
        Assert.Equal(ChangeSeverity.Minor, ChangeSeverityClassifier.Classify(change));
    }

    [Fact]
    public void Empty_delta_is_Minor()
    {
        var change = Make();
        Assert.Equal(ChangeSeverity.Minor, ChangeSeverityClassifier.Classify(change));
    }

    [Fact]
    public void Classifier_is_case_sensitive()
    {
        // Guards against a rename accidentally lowercasing the registry
        // and silently downgrading every major change to minor.
        var change = Make("cl", "vcore");
        Assert.Equal(ChangeSeverity.Minor, ChangeSeverityClassifier.Classify(change));
    }
}
