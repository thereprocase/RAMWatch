using Xunit;
using RAMWatch.Services;

namespace RAMWatch.Tests;

public class VoltageThresholdsTests
{
    // ── Zero-reading is always None — shared invariant ───────────────

    [Fact]
    public void Zero_maps_to_None_on_every_rail()
    {
        Assert.Equal(StatusLevel.None, VoltageThresholds.Vcore(0));
        Assert.Equal(StatusLevel.None, VoltageThresholds.Vsoc(0));
        Assert.Equal(StatusLevel.None, VoltageThresholds.Vdimm(0));
        Assert.Equal(StatusLevel.None, VoltageThresholds.Vddp(0));
        Assert.Equal(StatusLevel.None, VoltageThresholds.VddgIod(0));
        Assert.Equal(StatusLevel.None, VoltageThresholds.VddgCcd(0));
    }

    [Fact]
    public void Negative_is_treated_as_None_like_zero()
    {
        // The SVI2 path never reports negative; guard against a future
        // signed-int underflow appearing as a spurious Pass.
        Assert.Equal(StatusLevel.None, VoltageThresholds.Vcore(-0.5));
        Assert.Equal(StatusLevel.None, VoltageThresholds.VddgIod(-0.001));
    }

    // ── VCore — Pass ≤ 1.35 V, Warn ≤ 1.40 V, Crit > 1.40 V ──────────

    [Theory]
    [InlineData(1.225, StatusLevel.Pass)]  // user baseline
    [InlineData(1.35,  StatusLevel.Pass)]  // boundary-inclusive
    [InlineData(1.36,  StatusLevel.Warn)]
    [InlineData(1.40,  StatusLevel.Warn)]
    [InlineData(1.41,  StatusLevel.Crit)]
    [InlineData(1.50,  StatusLevel.Crit)]
    public void Vcore_bands(double v, StatusLevel expected)
        => Assert.Equal(expected, VoltageThresholds.Vcore(v));

    // ── VSoC — Pass ≤ 1.15 V, Warn ≤ 1.20 V, Crit > 1.20 V ───────────

    [Theory]
    [InlineData(1.1125, StatusLevel.Pass)] // user baseline
    [InlineData(1.15,   StatusLevel.Pass)]
    [InlineData(1.16,   StatusLevel.Warn)]
    [InlineData(1.20,   StatusLevel.Warn)]
    [InlineData(1.21,   StatusLevel.Crit)]
    public void Vsoc_bands(double v, StatusLevel expected)
        => Assert.Equal(expected, VoltageThresholds.Vsoc(v));

    // ── VDIMM — Pass ≤ 1.45 V, Warn ≤ 1.50 V, Crit > 1.50 V ──────────

    [Theory]
    [InlineData(1.35, StatusLevel.Pass)]
    [InlineData(1.40, StatusLevel.Pass)]   // user baseline
    [InlineData(1.45, StatusLevel.Pass)]
    [InlineData(1.46, StatusLevel.Warn)]
    [InlineData(1.50, StatusLevel.Warn)]
    [InlineData(1.55, StatusLevel.Crit)]
    public void Vdimm_bands(double v, StatusLevel expected)
        => Assert.Equal(expected, VoltageThresholds.Vdimm(v));

    // ── VDDP — Pass ≤ 1.00 V, Warn ≤ 1.05 V, Crit > 1.05 V ───────────

    [Theory]
    [InlineData(0.90,   StatusLevel.Pass)]
    [InlineData(1.00,   StatusLevel.Pass)]
    [InlineData(1.0743, StatusLevel.Crit)] // user's auto-set — hot zone
    [InlineData(1.05,   StatusLevel.Warn)]
    [InlineData(1.10,   StatusLevel.Crit)]
    public void Vddp_bands(double v, StatusLevel expected)
        => Assert.Equal(expected, VoltageThresholds.Vddp(v));

    // ── VDDG IOD — Pass ≤ 1.05, Warn ≤ 1.10, Crit > 1.10 ─────────────

    [Theory]
    [InlineData(0.95,   StatusLevel.Pass)]
    [InlineData(1.0241, StatusLevel.Pass)] // user baseline
    [InlineData(1.05,   StatusLevel.Pass)]
    [InlineData(1.06,   StatusLevel.Warn)]
    [InlineData(1.10,   StatusLevel.Warn)]
    [InlineData(1.11,   StatusLevel.Crit)]
    public void VddgIod_bands(double v, StatusLevel expected)
        => Assert.Equal(expected, VoltageThresholds.VddgIod(v));

    // ── VDDG CCD — same band as IOD ──────────────────────────────────

    [Theory]
    [InlineData(1.0241, StatusLevel.Pass)] // user baseline
    [InlineData(1.08,   StatusLevel.Warn)]
    [InlineData(1.15,   StatusLevel.Crit)]
    public void VddgCcd_bands(double v, StatusLevel expected)
        => Assert.Equal(expected, VoltageThresholds.VddgCcd(v));

    // Sanity: IOD and CCD classifiers behave identically for any reading.
    [Theory]
    [InlineData(0.5)]
    [InlineData(1.00)]
    [InlineData(1.05)]
    [InlineData(1.10)]
    [InlineData(1.15)]
    public void IOD_and_CCD_bands_are_identical(double v)
        => Assert.Equal(VoltageThresholds.VddgIod(v), VoltageThresholds.VddgCcd(v));
}

public class ThermalThresholdsTests
{
    [Fact]
    public void Zero_maps_to_None()
    {
        Assert.Equal(StatusLevel.None, ThermalThresholds.CpuTemp(0));
        Assert.Equal(StatusLevel.None, ThermalThresholds.CcdTemp(0));
    }

    [Theory]
    [InlineData(35,  StatusLevel.Pass)]  // idle
    [InlineData(70,  StatusLevel.Pass)]  // gaming sustained
    [InlineData(75,  StatusLevel.Pass)]  // boundary
    [InlineData(80,  StatusLevel.Warn)]
    [InlineData(85,  StatusLevel.Warn)]
    [InlineData(87,  StatusLevel.Crit)]
    [InlineData(90,  StatusLevel.Crit)]  // Tj_max
    public void CpuTemp_bands(double c, StatusLevel expected)
        => Assert.Equal(expected, ThermalThresholds.CpuTemp(c));

    [Theory]
    [InlineData(60,  StatusLevel.Pass)]
    [InlineData(84,  StatusLevel.Warn)]
    [InlineData(92,  StatusLevel.Crit)]
    public void CcdTemp_bands(double c, StatusLevel expected)
        => Assert.Equal(expected, ThermalThresholds.CcdTemp(c));
}
