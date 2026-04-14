using Xunit;
using RAMWatch.Service.Hardware;
using static RAMWatch.Service.Hardware.CpuDetect;

namespace RAMWatch.Tests;

public class CpuDetectTests
{
    // ── AMD Zen3 ─────────────────────────────────────────────────────────────

    [Fact]
    public void ParseFamilyModel_Zen3_Vermeer()
    {
        // Family 25 = 0x19, Model 33 = 0x21 (Vermeer desktop)
        var family = ParseFamilyModel(
            "AMD64 Family 25 Model 33 Stepping 2",
            "AMD Ryzen 9 5950X 16-Core Processor");

        Assert.Equal(CpuFamily.Zen3, family);
    }

    [Fact]
    public void ParseFamilyModel_Zen3_Cezanne()
    {
        // Family 25 = 0x19, Model 80 = 0x50 (Cezanne APU)
        var family = ParseFamilyModel(
            "AMD64 Family 25 Model 80 Stepping 0",
            "AMD Ryzen 7 5700G with Radeon Graphics");

        Assert.Equal(CpuFamily.Zen3, family);
    }

    // ── AMD Zen2 ─────────────────────────────────────────────────────────────

    [Fact]
    public void ParseFamilyModel_Zen2_Matisse()
    {
        // Family 23 = 0x17, Model 113 = 0x71 (Matisse desktop)
        var family = ParseFamilyModel(
            "AMD64 Family 23 Model 113 Stepping 0",
            "AMD Ryzen 9 3950X 16-Core Processor");

        Assert.Equal(CpuFamily.Zen2, family);
    }

    [Fact]
    public void ParseFamilyModel_Zen2_CastlePeak()
    {
        // Family 23 = 0x17, Model 49 = 0x31 (Castle Peak HEDT)
        var family = ParseFamilyModel(
            "AMD64 Family 23 Model 49 Stepping 0",
            "AMD Ryzen Threadripper 3970X 32-Core Processor");

        Assert.Equal(CpuFamily.Zen2, family);
    }

    // ── AMD Zen5 ─────────────────────────────────────────────────────────────

    [Fact]
    public void ParseFamilyModel_Zen5_GraniteRidge()
    {
        // Family 26 = 0x1A, Model 1 (Granite Ridge desktop)
        var family = ParseFamilyModel(
            "AMD64 Family 26 Model 1 Stepping 0",
            "AMD Ryzen 9 9950X 16-Core Processor");

        Assert.Equal(CpuFamily.Zen5, family);
    }

    // ── Intel → Unknown ──────────────────────────────────────────────────────

    [Fact]
    public void ParseFamilyModel_Intel_ReturnsUnknown()
    {
        // Intel identifier: neither AMD in name nor identifier
        var family = ParseFamilyModel(
            "Intel64 Family 6 Model 183 Stepping 1",
            "Intel(R) Core(TM) i9-13900K");

        Assert.Equal(CpuFamily.Unknown, family);
    }

    [Fact]
    public void ParseFamilyModel_IntelAMD_InProcessorName_ReturnsUnknown()
    {
        // Pathological case: "AMD" appears only in identifier prefix that's actually Intel.
        // ParseFamilyModel checks the name string for "AMD" too —
        // a plain Intel identifier will not contain "AMD".
        var family = ParseFamilyModel(
            "Intel64 Family 6 Model 140 Stepping 1",
            "Intel(R) Core(TM) i7-1165G7");

        Assert.Equal(CpuFamily.Unknown, family);
    }

    // ── Empty / null strings → Unknown ──────────────────────────────────────

    [Fact]
    public void ParseFamilyModel_EmptyBoth_ReturnsUnknown()
    {
        var family = ParseFamilyModel("", "");
        Assert.Equal(CpuFamily.Unknown, family);
    }

    [Fact]
    public void ParseFamilyModel_NullIdentifier_ReturnsUnknown()
    {
        // CpuDetect.ParseFamilyModel takes string parameters (non-nullable in signature),
        // but the callers may produce empty strings. Treat empty as unknown.
        var family = ParseFamilyModel("", "");
        Assert.Equal(CpuFamily.Unknown, family);
    }

    [Fact]
    public void ParseFamilyModel_WhitespaceOnly_ReturnsUnknown()
    {
        var family = ParseFamilyModel("   ", "   ");
        Assert.Equal(CpuFamily.Unknown, family);
    }

    // ── Older Zen generations ────────────────────────────────────────────────

    [Fact]
    public void ParseFamilyModel_Zen_SummitRidge()
    {
        // Family 23 = 0x17, Model 1 (Summit Ridge)
        var family = ParseFamilyModel(
            "AMD64 Family 23 Model 1 Stepping 1",
            "AMD Ryzen 7 1800X Eight-Core Processor");

        Assert.Equal(CpuFamily.Zen, family);
    }

    [Fact]
    public void ParseFamilyModel_ZenPlus_PinnacleRidge()
    {
        // Family 23 = 0x17, Model 8 (Pinnacle Ridge)
        var family = ParseFamilyModel(
            "AMD64 Family 23 Model 8 Stepping 2",
            "AMD Ryzen 7 2700X Eight-Core Processor");

        Assert.Equal(CpuFamily.ZenPlus, family);
    }

    [Fact]
    public void ParseFamilyModel_Zen4_Raphael()
    {
        // Family 25 = 0x19, Model 97 = 0x61 (Raphael desktop)
        var family = ParseFamilyModel(
            "AMD64 Family 25 Model 97 Stepping 2",
            "AMD Ryzen 9 7950X 16-Core Processor");

        Assert.Equal(CpuFamily.Zen4, family);
    }

    // ── Unknown AMD family ───────────────────────────────────────────────────

    [Fact]
    public void ParseFamilyModel_UnrecognizedFamily_ReturnsUnknown()
    {
        // AMD but family/model not in the switch
        var family = ParseFamilyModel(
            "AMD64 Family 20 Model 1 Stepping 0",
            "AMD Engineering Sample");

        Assert.Equal(CpuFamily.Unknown, family);
    }
}
