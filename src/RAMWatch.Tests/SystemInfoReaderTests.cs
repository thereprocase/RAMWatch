using Xunit;
using RAMWatch.Service.Hardware;

namespace RAMWatch.Tests;

public class SystemInfoReaderTests
{
    // ── ExtractAgesaFromString ───────────────────────────────

    [Theory]
    [InlineData("AMD AGESA V2 PI 1.2.0.7", "V2 PI 1.2.0.7")]
    [InlineData("AGESA ComboAM4v2PI 1.2.0.7", "ComboAM4v2PI 1.2.0.7")]
    [InlineData("AGESA CastlePeakPI-SP3r3 1.0.0.6", "CastlePeakPI-SP3r3 1.0.0.6")]
    [InlineData("Some prefix AGESA V2 PI 1.2.0.3c", "V2 PI 1.2.0.3c")]
    public void ExtractAgesaFromString_ParsesVariousFormats(string input, string expected)
    {
        Assert.Equal(expected, SystemInfoReader.ExtractAgesaFromString(input));
    }

    [Fact]
    public void ExtractAgesaFromString_NoAgesa_ReturnsEmpty()
    {
        Assert.Equal("", SystemInfoReader.ExtractAgesaFromString("BIOS version 1.0"));
    }

    [Fact]
    public void ExtractAgesaFromString_EmptyString_ReturnsEmpty()
    {
        Assert.Equal("", SystemInfoReader.ExtractAgesaFromString(""));
    }

    // ── ExtractAgesaVersion (from string array) ─────────────

    [Fact]
    public void ExtractAgesaVersion_FindsAgesaEntry()
    {
        var entries = new[]
        {
            "ALASKA - 1072009",
            "1.G0",
            "AMD AGESA V2 PI 1.2.0.7"
        };

        Assert.Equal("V2 PI 1.2.0.7", SystemInfoReader.ExtractAgesaVersion(entries));
    }

    [Fact]
    public void ExtractAgesaVersion_NoAgesaEntry_ReturnsEmpty()
    {
        var entries = new[] { "ALASKA - 1072009", "1.G0" };
        Assert.Equal("", SystemInfoReader.ExtractAgesaVersion(entries));
    }

    [Fact]
    public void ExtractAgesaVersion_EmptyArray_ReturnsEmpty()
    {
        Assert.Equal("", SystemInfoReader.ExtractAgesaVersion([]));
    }

    // ── ExtractBiosVersion ──────────────────────────────────

    [Fact]
    public void ExtractBiosVersion_SkipsAlaskaAndAgesa_ReturnsVersionEntry()
    {
        var entries = new[]
        {
            "ALASKA - 1072009",
            "American Megatrends - 50014",
            "AMD AGESA V2 PI 1.2.0.7",
            "7C56vAH"
        };

        Assert.Equal("7C56vAH", SystemInfoReader.ExtractBiosVersion(entries));
    }

    [Fact]
    public void ExtractBiosVersion_OnlyAgesaAndAlaska_ReturnsFallback()
    {
        var entries = new[]
        {
            "ALASKA - 1072009",
            "AMD AGESA V2 PI 1.2.0.7"
        };

        // Falls back to first non-empty entry
        Assert.Equal("ALASKA - 1072009", SystemInfoReader.ExtractBiosVersion(entries));
    }

    [Fact]
    public void ExtractBiosVersion_EmptyArray_ReturnsEmpty()
    {
        Assert.Equal("", SystemInfoReader.ExtractBiosVersion([]));
    }

    [Fact]
    public void ExtractBiosVersion_AllEmpty_ReturnsEmpty()
    {
        var entries = new[] { "", "  " };
        Assert.Equal("", SystemInfoReader.ExtractBiosVersion(entries));
    }
}
