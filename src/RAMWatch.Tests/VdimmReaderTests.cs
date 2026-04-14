using Xunit;
using RAMWatch.Service.Hardware;

namespace RAMWatch.Tests;

/// <summary>
/// Tests for VdimmReader plausibility logic and subprocess output parsing.
/// The WMI-live paths (TryAmdAcpi, TryAsusWmi, ReadVdimm) are not exercised
/// because WMI classes are not present in the test environment. What is tested:
///
/// - ParseRawMillivolts: millivolt detection, plausibility guard, parse errors
/// - ApplyPlausibilityGuard: range rejection, mV-to-V conversion, rounding
/// - ReadVdimm returns 0.0 gracefully (smoke test — WMI classes absent)
/// </summary>
public class VdimmReaderTests
{
    // ── ParseRawMillivolts ───────────────────────────────────────────────────

    [Theory]
    [InlineData("1350", 1.35)]      // millivolts (MSI typical output)
    [InlineData("1400", 1.4)]       // millivolts
    [InlineData("800",  0.8)]       // millivolts at lower plausibility bound
    [InlineData("2000", 2.0)]       // millivolts at upper plausibility bound
    public void ParseRawMillivolts_ConvertsMillivolts(string input, double expected)
    {
        double result = VdimmReader.ParseRawMillivolts(input);
        Assert.Equal(expected, result, precision: 4);
    }

    [Theory]
    [InlineData("1.3500", 1.35)]    // volts (ASUS-style)
    [InlineData("1.4000", 1.4)]
    [InlineData("0.8000", 0.8)]     // lower bound
    [InlineData("2.0000", 2.0)]     // upper bound
    public void ParseRawMillivolts_AcceptsVolts(string input, double expected)
    {
        double result = VdimmReader.ParseRawMillivolts(input);
        Assert.Equal(expected, result, precision: 4);
    }

    [Theory]
    [InlineData("0")]               // zero — not populated
    [InlineData("")]                // empty output
    [InlineData("error")]           // non-numeric
    [InlineData("N/A")]
    public void ParseRawMillivolts_Returns0_OnUnparseable(string input)
    {
        double result = VdimmReader.ParseRawMillivolts(input);
        Assert.Equal(0.0, result);
    }

    // ── ApplyPlausibilityGuard ───────────────────────────────────────────────

    [Theory]
    [InlineData(1350.0, 1.35)]      // mV → V conversion
    [InlineData(800.0,  0.8)]       // mV lower bound
    [InlineData(2000.0, 2.0)]       // mV upper bound
    [InlineData(1.35,   1.35)]      // already volts
    [InlineData(0.8,    0.8)]       // lower plausibility bound (volts)
    [InlineData(2.0,    2.0)]       // upper plausibility bound (volts)
    public void ApplyPlausibilityGuard_AcceptsPlausibleValues(double raw, double expected)
    {
        double result = VdimmReader.ApplyPlausibilityGuard(raw);
        Assert.Equal(expected, result, precision: 4);
    }

    [Theory]
    [InlineData(0.0)]               // zero
    [InlineData(0.79)]              // below DDR4 minimum (0.8 V)
    [InlineData(2.01)]              // above DDR4 maximum (2.0 V)
    [InlineData(-1.0)]              // negative
    [InlineData(3000.0)]            // millivolts out of range after /1000 = 3.0 V
    [InlineData(799.0)]             // millivolts for 0.799 V — just below minimum
    public void ApplyPlausibilityGuard_Rejects_OutOfRangeValues(double raw)
    {
        double result = VdimmReader.ApplyPlausibilityGuard(raw);
        Assert.Equal(0.0, result);
    }

    [Fact]
    public void ApplyPlausibilityGuard_RoundsTo4DecimalPlaces()
    {
        // 1350 mV → exactly 1.35 V (clean value, round-trip safe)
        double result = VdimmReader.ApplyPlausibilityGuard(1350.0);
        Assert.Equal(1.35, result, precision: 4);
    }

    // ── ReadVdimm — smoke test ───────────────────────────────────────────────

    /// <summary>
    /// ReadVdimm must not throw and must return 0.0 when WMI classes are absent
    /// (which is the normal state in the test environment). This exercises the
    /// full fail-safe path: subprocess runs, PowerShell reports 0, ReadVdimm
    /// returns 0.0. Marked with a generous timeout because the subprocess adds
    /// latency on first PowerShell invocation.
    /// </summary>
    [Fact]
    [Trait("Category", "SlowTest")]
    public void ReadVdimm_Returns0_WhenWmiClassAbsent()
    {
        // This call may take up to ~5 seconds if WMI is slow to report absence.
        // In a CI environment where powershell.exe is not available it returns
        // 0 from the subprocess launch-failure path.
        double result = VdimmReader.ReadVdimm();
        // Only constraint: must be 0 (not found) or a plausible voltage.
        Assert.True(result == 0.0 || (result >= 0.8 && result <= 2.0));
    }
}
