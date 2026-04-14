using System.Text;
using RAMWatch.Core.Models;

namespace RAMWatch.Service.Services;

/// <summary>
/// Builds CURRENT.md — a phone-readable BIOS checklist for the active timing configuration.
/// Pure function: no I/O, no side effects.
/// </summary>
public static class CurrentMdBuilder
{
    /// <summary>
    /// Builds the CURRENT.md content.
    /// </summary>
    /// <param name="snapshot">Current timing snapshot.</param>
    /// <param name="designations">
    /// Designation map. If null, all timings are placed under BIOS Settings
    /// (conservative — assume manual).
    /// </param>
    /// <param name="lastValidation">Most recent validation result, or null if none.</param>
    /// <returns>Markdown string.</returns>
    public static string Build(
        TimingSnapshot snapshot,
        DesignationMap? designations,
        ValidationResult? lastValidation)
    {
        var sb = new StringBuilder(1024);
        string date = DateTime.Now.ToString("yyyy-MM-dd");

        sb.AppendLine("# CURRENT — RAMWatch");
        sb.AppendLine($"Updated: {date}");
        sb.AppendLine();

        AppendClockSection(sb, snapshot);
        sb.AppendLine();

        AppendTimingSections(sb, snapshot, designations);

        if (lastValidation is not null)
        {
            sb.AppendLine();
            AppendLastValidation(sb, lastValidation);
        }

        return sb.ToString().TrimEnd();
    }

    private static void AppendClockSection(StringBuilder sb, TimingSnapshot snap)
    {
        int ddr = snap.MemClockMhz * 2;
        sb.AppendLine("## Clock");
        sb.AppendLine($"DDR4-{ddr} | MCLK {snap.MemClockMhz} | FCLK {snap.FclkMhz} | UCLK {snap.UclkMhz}");
    }

    private static void AppendTimingSections(StringBuilder sb, TimingSnapshot snap, DesignationMap? designations)
    {
        // When no designation map, put everything under BIOS Settings (conservative).
        if (designations is null)
        {
            sb.AppendLine("## BIOS Settings (enter these manually)");
            AppendAllTimings(sb, snap);
            return;
        }

        var manual = new List<(string name, string value)>();
        var auto   = new List<(string name, string value)>();

        foreach (var (name, value) in AllTimingPairs(snap))
        {
            if (designations.Designations.TryGetValue(name, out var desig) &&
                desig == TimingDesignation.Auto)
            {
                auto.Add((name, value));
            }
            else
            {
                // Manual or Unknown both go under BIOS Settings.
                manual.Add((name, value));
            }
        }

        if (manual.Count > 0)
        {
            sb.AppendLine("## BIOS Settings (enter these manually)");
            foreach (var (name, value) in manual)
                sb.AppendLine($"{name} = {value}");
        }

        if (auto.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Auto-trained (leave on Auto in BIOS)");
            foreach (var (name, value) in auto)
                sb.AppendLine($"{name} = {value}");
        }
    }

    private static void AppendAllTimings(StringBuilder sb, TimingSnapshot snap)
    {
        foreach (var (name, value) in AllTimingPairsPublic(snap))
            sb.AppendLine($"{name} = {value}");
    }

    private static void AppendLastValidation(StringBuilder sb, ValidationResult v)
    {
        string result = v.Passed ? "PASS" : "FAIL";
        string metric = v.MetricUnit.ToLowerInvariant() switch
        {
            "%" or "percent" or "coverage" => $"{v.MetricValue:0}{v.MetricUnit}",
            "cycles"                        => $"{v.MetricValue:0} cycles",
            _                               => $"{v.MetricValue:0}{v.MetricUnit}"
        };
        string date = v.Timestamp.ToString("yyyy-MM-dd");

        sb.AppendLine("## Last Validation");
        sb.AppendLine($"{v.TestTool} {metric} {result} — {date}");
    }

    // Returns all timing fields as (name, value) pairs in display order.
    // Boolean fields (GDM, Cmd2T) are included as On/Off and 2T/1T respectively.
    // Internal so LkgMdBuilder can share the same ordered list without duplication.
    internal static IEnumerable<(string name, string value)> AllTimingPairsPublic(TimingSnapshot snap)
        => AllTimingPairs(snap);

    private static IEnumerable<(string name, string value)> AllTimingPairs(TimingSnapshot snap)
    {
        yield return ("CL",       snap.CL.ToString());
        yield return ("RCDRD",    snap.RCDRD.ToString());
        yield return ("RCDWR",    snap.RCDWR.ToString());
        yield return ("RP",       snap.RP.ToString());
        yield return ("RAS",      snap.RAS.ToString());
        yield return ("RC",       snap.RC.ToString());
        yield return ("CWL",      snap.CWL.ToString());
        yield return ("RFC",      snap.RFC.ToString());
        yield return ("RFC2",     snap.RFC2.ToString());
        yield return ("RFC4",     snap.RFC4.ToString());
        yield return ("RRDS",     snap.RRDS.ToString());
        yield return ("RRDL",     snap.RRDL.ToString());
        yield return ("FAW",      snap.FAW.ToString());
        yield return ("WTRS",     snap.WTRS.ToString());
        yield return ("WTRL",     snap.WTRL.ToString());
        yield return ("WR",       snap.WR.ToString());
        yield return ("RTP",      snap.RTP.ToString());
        yield return ("RDRDSCL",  snap.RDRDSCL.ToString());
        yield return ("WRWRSCL",  snap.WRWRSCL.ToString());
        yield return ("RDRDSC",   snap.RDRDSC.ToString());
        yield return ("RDRDSD",   snap.RDRDSD.ToString());
        yield return ("RDRDDD",   snap.RDRDDD.ToString());
        yield return ("WRWRSC",   snap.WRWRSC.ToString());
        yield return ("WRWRSD",   snap.WRWRSD.ToString());
        yield return ("WRWRDD",   snap.WRWRDD.ToString());
        yield return ("RDWR",     snap.RDWR.ToString());
        yield return ("WRRD",     snap.WRRD.ToString());
        yield return ("REFI",     snap.REFI.ToString());
        yield return ("CKE",      snap.CKE.ToString());
        yield return ("STAG",     snap.STAG.ToString());
        yield return ("MOD",      snap.MOD.ToString());
        yield return ("MRD",      snap.MRD.ToString());
        yield return ("GDM",      snap.GDM ? "On" : "Off");
        yield return ("Cmd2T",    snap.Cmd2T ? "2T" : "1T");
    }
}
