using Xunit;
using RAMWatch.Service.Hardware;

namespace RAMWatch.Tests;

/// <summary>
/// Tests for BiosWmiReader decode tables, CSV parsing, and plausibility logic.
/// Live WMI paths are not exercised (WMI classes absent in test environment).
/// </summary>
public class BiosWmiReaderTests
{
    // ── Voltage plausibility ────────────────────────────────────────────────

    [Theory]
    [InlineData(1350.0, 1.35)]      // mV → V
    [InlineData(800.0,  0.8)]       // mV lower
    [InlineData(2000.0, 2.0)]       // mV upper
    [InlineData(1.35,   1.35)]      // already volts
    [InlineData(0.3,    0.3)]       // lower bound
    [InlineData(2.5,    2.5)]       // upper bound
    public void ApplyVoltagePlausibility_AcceptsPlausible(double raw, double expected)
    {
        Assert.Equal(expected, BiosWmiReader.ApplyVoltagePlausibility(raw), precision: 4);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(0.29)]
    [InlineData(2.51)]
    [InlineData(3000.0)]    // 3.0V after /1000 — out of range
    public void ApplyVoltagePlausibility_RejectsOutOfRange(double raw)
    {
        Assert.Equal(0.0, BiosWmiReader.ApplyVoltagePlausibility(raw));
    }

    // ── ProcODT decode ──────────────────────────────────────────────────────

    [Theory]
    [InlineData(1,  480.0)]
    [InlineData(2,  240.0)]
    [InlineData(3,  160.0)]
    [InlineData(8,  120.0)]
    [InlineData(9,  96.0)]
    [InlineData(10, 80.0)]
    [InlineData(11, 68.6)]
    [InlineData(24, 60.0)]
    [InlineData(25, 53.3)]
    [InlineData(26, 48.0)]
    [InlineData(27, 43.6)]
    [InlineData(56, 40.0)]
    [InlineData(57, 36.9)]
    [InlineData(58, 34.3)]
    [InlineData(59, 32.0)]
    [InlineData(62, 30.0)]
    [InlineData(63, 28.2)]
    [InlineData(0,  0.0)]   // unknown
    [InlineData(99, 0.0)]   // unknown
    public void DecodeProcODT_ReturnsExpected(int raw, double expected)
    {
        Assert.Equal(expected, BiosWmiReader.DecodeProcODT(raw), precision: 1);
    }

    // ── Drive strength decode ───────────────────────────────────────────────

    [Theory]
    [InlineData(0,  120.0)]
    [InlineData(1,  60.0)]
    [InlineData(3,  40.0)]
    [InlineData(7,  30.0)]
    [InlineData(15, 24.0)]
    [InlineData(31, 20.0)]
    [InlineData(99, 0.0)]   // unknown
    public void DecodeDriveStrength_ReturnsExpected(int raw, double expected)
    {
        Assert.Equal(expected, BiosWmiReader.DecodeDriveStrength(raw), precision: 1);
    }

    // ── RttNom decode ───────────────────────────────────────────────────────

    [Theory]
    [InlineData(0, "Disabled")]
    [InlineData(1, "RZQ/4")]
    [InlineData(2, "RZQ/2")]
    [InlineData(3, "RZQ/6")]
    [InlineData(4, "RZQ/1")]
    [InlineData(5, "RZQ/5")]
    [InlineData(6, "RZQ/3")]
    [InlineData(7, "RZQ/7")]
    [InlineData(99, "")]
    public void DecodeRttNom_ReturnsExpected(int raw, string expected)
    {
        Assert.Equal(expected, BiosWmiReader.DecodeRttNom(raw));
    }

    // ── RttWr decode ────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0, "Off")]
    [InlineData(1, "RZQ/2")]
    [InlineData(2, "RZQ/1")]
    [InlineData(3, "Hi-Z")]
    [InlineData(4, "RZQ/3")]
    [InlineData(99, "")]
    public void DecodeRttWr_ReturnsExpected(int raw, string expected)
    {
        Assert.Equal(expected, BiosWmiReader.DecodeRttWr(raw));
    }

    // ── Setup timing decode ─────────────────────────────────────────────────

    [Theory]
    [InlineData(0,  "")]        // zero → empty
    [InlineData(63, "1/31")]    // 63 / 32 = 1 remainder 31
    [InlineData(32, "1/0")]     // 32 / 32 = 1 remainder 0
    [InlineData(1,  "0/1")]     // 1 / 32 = 0 remainder 1
    public void DecodeSetup_ReturnsExpected(int raw, string expected)
    {
        Assert.Equal(expected, BiosWmiReader.DecodeSetup(raw));
    }

    // ── CSV parsing ─────────────────────────────────────────────────────────

    [Fact]
    public void ParseApcbCsv_FullLine_DecodesAllFields()
    {
        // vddio=1350mV, vtt=675mV, vpp=2500mV, procodt=26(48Ω),
        // rttnom=1(RZQ/4), rttwr=2(RZQ/1), rttpark=3(RZQ/6),
        // acsetup=63, cssetup=32, ckesetup=1,
        // clkdrv=1(60Ω), acdrv=3(40Ω), csdrv=7(30Ω), ckedrv=15(24Ω)
        var result = BiosWmiReader.ParseApcbCsv("1350,675,2500,26,1,2,3,63,32,1,1,3,7,15");

        Assert.Equal(1.35, result.VDimm, precision: 4);
        Assert.Equal(0.675, result.Vtt, precision: 4);
        Assert.Equal(2.5, result.Vpp, precision: 4);
        Assert.Equal(48.0, result.ProcODT, precision: 1);
        Assert.Equal("RZQ/4", result.RttNom);
        Assert.Equal("RZQ/1", result.RttWr);
        Assert.Equal("RZQ/6", result.RttPark);
        Assert.Equal(60.0, result.ClkDrvStren, precision: 1);
        Assert.Equal(40.0, result.AddrCmdDrvStren, precision: 1);
        Assert.Equal(30.0, result.CsOdtCmdDrvStren, precision: 1);
        Assert.Equal(24.0, result.CkeDrvStren, precision: 1);
        Assert.Equal("1/31", result.AddrCmdSetup);
        Assert.Equal("1/0", result.CsOdtSetup);
        Assert.Equal("0/1", result.CkeSetup);
    }

    [Fact]
    public void ParseApcbCsv_AllZeros_ReturnsDefault()
    {
        var result = BiosWmiReader.ParseApcbCsv("0,0,0,0,0,0,0,0,0,0,0,0,0,0");

        Assert.Equal(0.0, result.VDimm);
        Assert.Equal(0.0, result.Vtt);
        Assert.Equal(0.0, result.ProcODT);
        Assert.Equal("Disabled", result.RttNom);
        Assert.Equal("Off", result.RttWr);
    }

    [Fact]
    public void ParseApcbCsv_TooFewFields_ReturnsDefault()
    {
        var result = BiosWmiReader.ParseApcbCsv("1350,675,2500");
        Assert.Equal(0.0, result.VDimm);
    }

    [Fact]
    public void ParseApcbCsv_Garbage_ReturnsDefault()
    {
        var result = BiosWmiReader.ParseApcbCsv("error");
        Assert.Equal(0.0, result.VDimm);
    }

    // ── ReadAll smoke test ──────────────────────────────────────────────────

    [Fact]
    [Trait("Category", "SlowTest")]
    public void ReadAll_Returns_WhenWmiAbsent()
    {
        var result = BiosWmiReader.ReadAll();
        // Must not throw. VDimm is 0 (not found) or a plausible voltage.
        Assert.True(result.VDimm == 0.0 || (result.VDimm >= 0.3 && result.VDimm <= 2.5));
    }

    // ── Timeout helper ──────────────────────────────────────────────────────
    // These exercise the wall-clock timeout path lifted out of RunPowerShellCore.
    // WMI's AMD_ACPI queries have hung indefinitely on real boxes under the
    // HardwareReader driver lock; the in-process helper below is what stops
    // that from taking the service with it. Kept close to the source contract
    // (completes → true + value, doesn't complete → kill + false + "").

    [Fact]
    public void TryReadWithTimeout_TaskCompletesBeforeTimeout_ReturnsResultWithoutKilling()
    {
        var completed = Task.FromResult("1350,675,2500,0,0,0,0,0,0,0,0,0,0,0");
        bool killCalled = false;

        bool ok = BiosWmiReader.TryReadWithTimeout(
            completed,
            killProcess: () => killCalled = true,
            timeout: TimeSpan.FromSeconds(1),
            out string output);

        Assert.True(ok);
        Assert.False(killCalled);
        Assert.Equal("1350,675,2500,0,0,0,0,0,0,0,0,0,0,0", output);
    }

    [Fact]
    public void TryReadWithTimeout_TaskBlocksPastTimeout_InvokesKillAndReturnsFalse()
    {
        // TCS never completes on its own. killProcess simulates production's
        // Kill → stdout-closed → ReadToEnd-returns path by completing the TCS.
        var tcs = new TaskCompletionSource<string>();
        int killCalls = 0;

        bool ok = BiosWmiReader.TryReadWithTimeout(
            tcs.Task,
            killProcess: () =>
            {
                Interlocked.Increment(ref killCalls);
                tcs.TrySetResult("(interrupted)");
            },
            timeout: TimeSpan.FromMilliseconds(150),
            out string output);

        Assert.False(ok);
        Assert.Equal(1, killCalls);
        // Output must be the timeout sentinel ("") even though the TCS
        // eventually yielded a result after kill — the caller needs to see
        // failure, not the post-kill garbage.
        Assert.Equal("", output);
    }

    [Fact]
    public void TryReadWithTimeout_KillThrowing_StillReturnsFalseWithEmptyOutput()
    {
        // Production wraps Kill in try/catch because Kill can race with child
        // exit and throw InvalidOperationException. This check asserts the
        // helper doesn't propagate that exception and still surfaces failure.
        var tcs = new TaskCompletionSource<string>();

        bool ok = BiosWmiReader.TryReadWithTimeout(
            tcs.Task,
            killProcess: () => throw new InvalidOperationException("kill raced exit"),
            timeout: TimeSpan.FromMilliseconds(100),
            out string output);

        Assert.False(ok);
        Assert.Equal("", output);
        // tcs left uncompleted — the helper's post-kill Wait(2s) fires its own
        // timeout and the test returns cleanly. The tcs is garbage-collected
        // when the test method exits; nothing observes it.
    }
}
