using CommunityToolkit.Mvvm.ComponentModel;
using RAMWatch.Core.Models;

namespace RAMWatch.ViewModels;

/// <summary>
/// Backing view model for TimingsTab. Populated from a TimingSnapshot
/// received via the service pipe. When no snapshot is available
/// (driver not loaded), HasTimings is false and the tab shows a
/// "driver not available" message instead of the timing grid.
/// </summary>
public partial class TimingsViewModel : ObservableObject
{
    [ObservableProperty]
    private bool _hasTimings;

    // ── Clocks ───────────────────────────────────────────────

    [ObservableProperty]
    private string _mclkDisplay = "—";

    [ObservableProperty]
    private string _fclkDisplay = "—";

    [ObservableProperty]
    private string _uclkDisplay = "—";

    // ── Primaries ────────────────────────────────────────────

    [ObservableProperty]
    private string _clDisplay = "—";

    [ObservableProperty]
    private string _rcdrdDisplay = "—";

    [ObservableProperty]
    private string _rcdwrDisplay = "—";

    [ObservableProperty]
    private string _rpDisplay = "—";

    [ObservableProperty]
    private string _rasDisplay = "—";

    [ObservableProperty]
    private string _rcDisplay = "—";

    [ObservableProperty]
    private string _cwlDisplay = "—";

    [ObservableProperty]
    private string _gdmDisplay = "—";

    [ObservableProperty]
    private string _commandRateDisplay = "—";

    // ── tRFC ─────────────────────────────────────────────────

    [ObservableProperty]
    private string _rfcDisplay = "—";

    [ObservableProperty]
    private string _rfc2Display = "—";

    [ObservableProperty]
    private string _rfc4Display = "—";

    // ── Secondaries ──────────────────────────────────────────

    [ObservableProperty]
    private string _rrdsDisplay = "—";

    [ObservableProperty]
    private string _rrdlDisplay = "—";

    [ObservableProperty]
    private string _fawDisplay = "—";

    [ObservableProperty]
    private string _wtrsDisplay = "—";

    [ObservableProperty]
    private string _wtrlDisplay = "—";

    [ObservableProperty]
    private string _wrDisplay = "—";

    [ObservableProperty]
    private string _rtpDisplay = "—";

    [ObservableProperty]
    private string _rdrdSclDisplay = "—";

    [ObservableProperty]
    private string _wrwrSclDisplay = "—";

    // ── Voltages ─────────────────────────────────────────────

    [ObservableProperty]
    private string _vsocDisplay = "—";

    [ObservableProperty]
    private string _vdimmDisplay = "—";

    // ── System info ──────────────────────────────────────────

    [ObservableProperty]
    private string _cpuCodename = "";

    // ── Public API ───────────────────────────────────────────

    /// <summary>
    /// Applies a snapshot from the service state. Passing null clears all
    /// displayed values and hides the timing grid.
    /// </summary>
    public void LoadFromSnapshot(TimingSnapshot? snapshot)
    {
        if (snapshot is null)
        {
            HasTimings = false;
            return;
        }

        // Clocks
        MclkDisplay = $"{snapshot.MemClockMhz} MHz";
        FclkDisplay = $"{snapshot.FclkMhz} MHz";
        UclkDisplay = $"{snapshot.UclkMhz} MHz";

        // Primaries
        ClDisplay = snapshot.CL.ToString();
        RcdrdDisplay = snapshot.RCDRD.ToString();
        RcdwrDisplay = snapshot.RCDWR.ToString();
        RpDisplay = snapshot.RP.ToString();
        RasDisplay = snapshot.RAS.ToString();
        RcDisplay = snapshot.RC.ToString();
        CwlDisplay = snapshot.CWL.ToString();
        GdmDisplay = snapshot.GDM ? "On" : "Off";
        CommandRateDisplay = snapshot.Cmd2T ? "2T" : "1T";

        // tRFC — show clocks and nanoseconds when MCLK is known
        RfcDisplay = FormatRfc(snapshot.RFC, snapshot.MemClockMhz);
        Rfc2Display = FormatRfc(snapshot.RFC2, snapshot.MemClockMhz);
        Rfc4Display = FormatRfc(snapshot.RFC4, snapshot.MemClockMhz);

        // Secondaries
        RrdsDisplay = snapshot.RRDS.ToString();
        RrdlDisplay = snapshot.RRDL.ToString();
        FawDisplay = snapshot.FAW.ToString();
        WtrsDisplay = snapshot.WTRS.ToString();
        WtrlDisplay = snapshot.WTRL.ToString();
        WrDisplay = snapshot.WR.ToString();
        RtpDisplay = snapshot.RTP.ToString();
        RdrdSclDisplay = snapshot.RDRDSCL.ToString();
        WrwrSclDisplay = snapshot.WRWRSCL.ToString();

        // Voltages — four decimal places, trailing zeros communicate precision
        VsocDisplay = snapshot.VSoc > 0 ? $"{snapshot.VSoc:F4}" : "—";
        VdimmDisplay = snapshot.VDimm > 0 ? $"{snapshot.VDimm:F4}" : "—";

        // System info
        CpuCodename = snapshot.CpuCodename;

        HasTimings = true;
    }

    // ── Helpers ──────────────────────────────────────────────

    /// <summary>
    /// Formats an RFC clock value as "NNN (NNNns)".
    /// The ns conversion is: clocks / (2 * MCLK_MHz) * 1000.
    /// Returns just the clock count when MCLK is zero (unknown).
    /// </summary>
    private static string FormatRfc(int clocks, int mclkMhz)
    {
        if (clocks == 0) return "—";
        if (mclkMhz <= 0) return clocks.ToString();

        // MCLK is the half-rate. One DDR clock period = 1 / (2 * MCLK) µs.
        // In nanoseconds: clocks * (1000 / (2 * MCLK)).
        double ns = clocks * 1000.0 / (2.0 * mclkMhz);
        return $"{clocks} ({ns:F0}ns)";
    }
}
