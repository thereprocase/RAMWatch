using Xunit;
using RAMWatch.Core.Models;
using RAMWatch.ViewModels;

namespace RAMWatch.Tests;

/// <summary>
/// Covers the display-formatting regression that surfaced during the
/// TimingSnapshotFields refactor: boolean fields were routed through
/// GetIntField (which projects bool→0/1) and rendered as raw "0"/"1"
/// instead of the descriptive "On"/"Off" / "2T"/"1T" strings.
/// </summary>
public class TimingsViewModelTests
{
    [Fact]
    public void GetFieldValue_Gdm_RendersOnOff()
    {
        var snap = MakeSnapshot(gdm: true);
        Assert.Equal("On", TimingsViewModel.GetFieldValue(snap, "GDM"));

        snap = MakeSnapshot(gdm: false);
        Assert.Equal("Off", TimingsViewModel.GetFieldValue(snap, "GDM"));
    }

    [Fact]
    public void GetFieldValue_Cmd2T_Renders2T1T()
    {
        var snap = MakeSnapshot(cmd2T: true);
        Assert.Equal("2T", TimingsViewModel.GetFieldValue(snap, "Cmd2T"));

        snap = MakeSnapshot(cmd2T: false);
        Assert.Equal("1T", TimingsViewModel.GetFieldValue(snap, "Cmd2T"));
    }

    [Fact]
    public void GetFieldValue_PowerDown_RendersOnOff()
    {
        var snap = MakeSnapshot(powerDown: true);
        Assert.Equal("On", TimingsViewModel.GetFieldValue(snap, "PowerDown"));

        snap = MakeSnapshot(powerDown: false);
        Assert.Equal("Off", TimingsViewModel.GetFieldValue(snap, "PowerDown"));
    }

    [Fact]
    public void GetFieldValue_IntegerField_RendersToString()
    {
        var snap = MakeSnapshot();
        snap.CL = 16;
        Assert.Equal("16", TimingsViewModel.GetFieldValue(snap, "CL"));
    }

    [Fact]
    public void GetFieldValue_UnknownField_RendersQuestionMark()
    {
        var snap = MakeSnapshot();
        Assert.Equal("?", TimingsViewModel.GetFieldValue(snap, "NotARealField"));
    }

    private static TimingSnapshot MakeSnapshot(
        bool gdm = false, bool cmd2T = false, bool powerDown = false)
    {
        return new TimingSnapshot
        {
            SnapshotId  = "t",
            Timestamp   = DateTime.UtcNow,
            BootId      = "b",
            MemClockMhz = 1900,
            FclkMhz     = 1900,
            UclkMhz     = 1900,
            GDM         = gdm,
            Cmd2T       = cmd2T,
            PowerDown   = powerDown
        };
    }
}
