using RAMWatch.Services;
using Xunit;

namespace RAMWatch.Tests;

/// <summary>
/// Guards the registry contract (right source per sensor tier), the
/// observer demotion heuristic (flat Measured/Reported → Unknown, drifted
/// Static → Unknown), and the ForVoltage sentinel path (Vdimm=0 on
/// boards without AMD_ACPI → Unknown with a board-specific explanation).
/// </summary>
public class SensorProvenanceTests
{
    // ── Registry ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Vsoc",            Provenance.Measured, "SVI2 registers")]
    [InlineData("Vcore",           Provenance.Measured, "SVI2 registers")]
    [InlineData("Vddp",            Provenance.Reported, "SMU PM table")]
    [InlineData("VddgIod",         Provenance.Reported, "SMU PM table")]
    [InlineData("VddgCcd",         Provenance.Reported, "SMU PM table")]
    [InlineData("Fclk",            Provenance.Reported, "SMU PM table")]
    [InlineData("Uclk",            Provenance.Reported, "SMU PM table")]
    [InlineData("Mclk",            Provenance.Reported, "UMC registers")]
    [InlineData("CpuTemp",         Provenance.Measured, "SMU PM table")]
    [InlineData("SocketPower",     Provenance.Measured, "SMU PM table")]
    [InlineData("Ppt",             Provenance.Measured, "SMU PM table")]
    [InlineData("Vdimm",           Provenance.Static,   "BIOS AMD_ACPI WMI")]
    [InlineData("Vtt",             Provenance.Static,   "BIOS AMD_ACPI WMI")]
    [InlineData("Vpp",             Provenance.Static,   "BIOS AMD_ACPI WMI")]
    [InlineData("ProcOdt",         Provenance.Static,   "BIOS AMD_ACPI WMI")]
    [InlineData("RttNom",          Provenance.Static,   "BIOS AMD_ACPI WMI")]
    [InlineData("RttWr",           Provenance.Static,   "BIOS AMD_ACPI WMI")]
    [InlineData("RttPark",         Provenance.Static,   "BIOS AMD_ACPI WMI")]
    [InlineData("ClkDrvStren",     Provenance.Static,   "BIOS AMD_ACPI WMI")]
    [InlineData("AddrCmdDrvStren", Provenance.Static,   "BIOS AMD_ACPI WMI")]
    [InlineData("CsOdtCmdDrvStren",Provenance.Static,   "BIOS AMD_ACPI WMI")]
    [InlineData("CkeDrvStren",     Provenance.Static,   "BIOS AMD_ACPI WMI")]
    [InlineData("EventMonitor",        Provenance.Measured, "Windows Event Log")]
    [InlineData("Integrity",           Provenance.Reported, "CBS.log tail")]
    [InlineData("SystemInfo",          Provenance.Static,   "Windows registry + WMI")]
    [InlineData("TimelineConfigChange",Provenance.Reported, "ConfigChangeDetector")]
    [InlineData("TimelineDrift",       Provenance.Reported, "DriftDetector + 20-boot window")]
    [InlineData("TimelineValidation",  Provenance.Measured, "User test log")]
    public void Registry_For_ReturnsExpectedTier(string key, Provenance expectedTier, string expectedSource)
    {
        var info = SensorProvenanceRegistry.For(key);
        Assert.Equal(expectedTier, info.Provenance);
        Assert.Equal(expectedSource, info.Source);
    }

    [Fact]
    public void Registry_For_UnknownKey_ReturnsUnknownSentinel()
    {
        var info = SensorProvenanceRegistry.For("DefinitelyNotARealSensor");
        Assert.Equal(Provenance.Unknown, info.Provenance);
        Assert.Equal(ProvenanceShape.Square, info.Shape);
    }

    [Theory]
    [InlineData("Vdimm", 0.0)]
    [InlineData("Vtt",   0.0)]
    [InlineData("Vpp",   0.0)]
    public void Registry_ForVoltage_ZeroOnBiosWmiKey_FlipsToUnknown(string key, double value)
    {
        var info = SensorProvenanceRegistry.ForVoltage(key, value);
        Assert.Equal(Provenance.Unknown, info.Provenance);
        Assert.Equal(ProvenanceShape.Square, info.Shape);
        Assert.Contains("AMD_ACPI", info.Source);
    }

    [Fact]
    public void Registry_ForVoltage_NonZeroVdimm_StaysStatic()
    {
        var info = SensorProvenanceRegistry.ForVoltage("Vdimm", 1.35);
        Assert.Equal(Provenance.Static, info.Provenance);
        Assert.Equal(ProvenanceShape.Circle, info.Shape);
    }

    [Fact]
    public void Registry_ForVoltage_ZeroOnMeasuredKey_DoesNotFlipSilently()
    {
        // Vcore is Measured (SVI2); a 0 reading isn't a board-config sentinel
        // — it means the read was rejected (VID=0 guard). We do NOT want the
        // ForVoltage sentinel path to claim it's an ASRock board quirk.
        var info = SensorProvenanceRegistry.ForVoltage("Vcore", 0.0);
        Assert.Equal(Provenance.Measured, info.Provenance);
    }

    // ── Observer demotion heuristic ──────────────────────────────────────

    [Fact]
    public void Observer_BelowMinSamples_DoesNotDemote()
    {
        var obs = new ProvenanceObserver { MinSamples = 10 };
        var declared = SensorProvenanceInfo.Measured("SVI2", "detail");

        for (int i = 0; i < 5; i++) obs.Record("VsocTest", 1.1);

        // Only 5 samples → still Measured.
        Assert.Equal(Provenance.Measured, obs.Adjust("VsocTest", declared).Provenance);
    }

    [Fact]
    public void Observer_FlatMeasured_AtMinSamples_DemotesToUnknown()
    {
        var obs = new ProvenanceObserver { MinSamples = 5 };
        var declared = SensorProvenanceInfo.Measured("SVI2", "detail");

        for (int i = 0; i < 5; i++) obs.Record("FlatMeasured", 1.1);

        var adjusted = obs.Adjust("FlatMeasured", declared);
        Assert.Equal(Provenance.Unknown, adjusted.Provenance);
        Assert.Contains("has not varied", adjusted.Detail);
    }

    [Fact]
    public void Observer_FlatReported_AtMinSamples_DemotesToUnknown()
    {
        var obs = new ProvenanceObserver { MinSamples = 5 };
        var declared = SensorProvenanceInfo.Reported("SMU PM table", "detail");

        for (int i = 0; i < 5; i++) obs.Record("FlatReported", 1.025);

        var adjusted = obs.Adjust("FlatReported", declared);
        Assert.Equal(Provenance.Unknown, adjusted.Provenance);
    }

    [Fact]
    public void Observer_VaryingMeasured_StaysMeasured()
    {
        var obs = new ProvenanceObserver { MinSamples = 5 };
        var declared = SensorProvenanceInfo.Measured("SVI2", "detail");

        // Spread above FlatEpsilon (1e-9) — well above.
        for (int i = 0; i < 10; i++) obs.Record("LiveMeasured", 1.1 + i * 0.001);

        var adjusted = obs.Adjust("LiveMeasured", declared);
        Assert.Equal(Provenance.Measured, adjusted.Provenance);
    }

    [Fact]
    public void Observer_DriftedStatic_DemotesToUnknown()
    {
        var obs = new ProvenanceObserver { MinSamples = 5, DriftEpsilon = 1e-4 };
        var declared = SensorProvenanceInfo.Static("BIOS WMI", "detail");

        // DriftEpsilon is 1e-4; spread must exceed it. 1.35 → 1.3505 = 5e-4 drift.
        for (int i = 0; i < 10; i++) obs.Record("DriftedStatic", 1.35 + i * 5e-5);

        var adjusted = obs.Adjust("DriftedStatic", declared);
        Assert.Equal(Provenance.Unknown, adjusted.Provenance);
        Assert.Contains("drifted", adjusted.Detail);
    }

    [Fact]
    public void Observer_StableStatic_StaysStatic()
    {
        var obs = new ProvenanceObserver { MinSamples = 5 };
        var declared = SensorProvenanceInfo.Static("BIOS WMI", "detail");

        for (int i = 0; i < 10; i++) obs.Record("StableStatic", 1.35);

        // Static + flat is the happy path (BIOS WMI value doesn't move).
        var adjusted = obs.Adjust("StableStatic", declared);
        Assert.Equal(Provenance.Static, adjusted.Provenance);
    }

    [Fact]
    public void Observer_Record_IgnoresNaN()
    {
        var obs = new ProvenanceObserver { MinSamples = 3 };
        var declared = SensorProvenanceInfo.Measured("SVI2", "detail");

        // NaN reads must not accrue toward MinSamples — otherwise a
        // sensor that's always unread looks "flat" to the heuristic.
        for (int i = 0; i < 10; i++) obs.Record("NaNSensor", double.NaN);

        // Still below MinSamples — should not demote.
        Assert.Equal(Provenance.Measured, obs.Adjust("NaNSensor", declared).Provenance);
    }

    // ── Derived-tier glyph shape (diamond) contract ──────────────────────

    [Fact]
    public void Registry_TimelineDerived_EntriesAreDiamondShape()
    {
        // Derived entries must carry the diamond shape so the glyph renders
        // with the DrawGeometry path, not DrawEllipse. The colour tracks the
        // weakest input; drift/config-change wrap Reported UMC register
        // readbacks so they inherit amber.
        var drift = SensorProvenanceRegistry.For("TimelineDrift");
        Assert.Equal(ProvenanceShape.Diamond, drift.Shape);

        var change = SensorProvenanceRegistry.For("TimelineConfigChange");
        Assert.Equal(ProvenanceShape.Diamond, change.Shape);
    }

    [Fact]
    public void Registry_TimelineValidation_IsMeasuredCircle()
    {
        // User-logged stress-test results are primary observations, not
        // computations — they render as circles.
        var validation = SensorProvenanceRegistry.For("TimelineValidation");
        Assert.Equal(ProvenanceShape.Circle, validation.Shape);
        Assert.Equal(Provenance.Measured, validation.Provenance);
    }

    // ── Observer event ──────────────────────────────────────────────────

    [Fact]
    public void Observer_SensorUpdated_FiresOncePerRecord()
    {
        var obs = new ProvenanceObserver();
        int fires = 0;
        obs.SensorUpdated += _ => Interlocked.Increment(ref fires);

        for (int i = 0; i < 7; i++) obs.Record("EventCounter", i);

        Assert.Equal(7, fires);
    }
}
