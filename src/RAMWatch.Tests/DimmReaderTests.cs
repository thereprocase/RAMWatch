using Xunit;
using RAMWatch.Service.Hardware;

namespace RAMWatch.Tests;

public class DimmReaderTests
{
    [Fact]
    public void ParseDimmOutput_SingleDimm()
    {
        string output = "BANK 0|17179869184|3600|G.Skill|F4-3600C16-16GTZN";
        var result = DimmReader.ParseDimmOutput(output);

        Assert.NotNull(result);
        Assert.Single(result);
        Assert.Equal("BANK 0", result[0].Slot);
        Assert.Equal(17179869184L, result[0].CapacityBytes); // 16 GB
        Assert.Equal(3600, result[0].SpeedMTs);
        Assert.Equal("G.Skill", result[0].Manufacturer);
        Assert.Equal("F4-3600C16-16GTZN", result[0].PartNumber);
    }

    [Fact]
    public void ParseDimmOutput_MultipleDimms()
    {
        string output = "BANK 0|17179869184|3600|G.Skill|F4-3600C16-16GTZN\nBANK 2|17179869184|3600|G.Skill|F4-3600C16-16GTZN";
        var result = DimmReader.ParseDimmOutput(output);

        Assert.NotNull(result);
        Assert.Equal(2, result.Count);
        Assert.Equal("BANK 0", result[0].Slot);
        Assert.Equal("BANK 2", result[1].Slot);
    }

    [Fact]
    public void ParseDimmOutput_EmptyString_ReturnsNull()
    {
        Assert.Null(DimmReader.ParseDimmOutput(""));
        Assert.Null(DimmReader.ParseDimmOutput("  "));
    }

    [Fact]
    public void ParseDimmOutput_Error_ReturnsNull()
    {
        Assert.Null(DimmReader.ParseDimmOutput("ERROR"));
    }

    [Fact]
    public void ParseDimmOutput_MalformedLine_Skipped()
    {
        string output = "bad data\nBANK 0|17179869184|3600|G.Skill|F4-3600C16-16GTZN";
        var result = DimmReader.ParseDimmOutput(output);

        Assert.NotNull(result);
        Assert.Single(result);
        Assert.Equal("BANK 0", result[0].Slot);
    }

    [Fact]
    public void ParseDimmOutput_ZeroCapacity_StillParsed()
    {
        string output = "BANK 0|0|0||";
        var result = DimmReader.ParseDimmOutput(output);

        Assert.NotNull(result);
        Assert.Single(result);
        Assert.Equal(0L, result[0].CapacityBytes);
        Assert.Equal(0, result[0].SpeedMTs);
    }

    [Fact]
    public void ParseDimmOutput_WindowsLineEndings_SlotHasNoCarriageReturn()
    {
        string output = "BANK 0|17179869184|3600|G.Skill|F4-3600C16-16GTZN\r\nBANK 2|17179869184|3600|G.Skill|F4-3600C16-16GTZN\r\n";
        var result = DimmReader.ParseDimmOutput(output);

        Assert.NotNull(result);
        Assert.Equal(2, result.Count);
        Assert.Equal("BANK 0", result[0].Slot);
        Assert.Equal("BANK 2", result[1].Slot);
        Assert.DoesNotContain("\r", result[0].Slot);
        Assert.DoesNotContain("\r", result[1].PartNumber);
    }
}
