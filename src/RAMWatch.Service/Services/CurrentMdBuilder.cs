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
    /// Builds the CURRENT.md content using the default (generic) timing layout.
    /// Preserved for callers that do not yet pass a board vendor.
    /// </summary>
    public static string Build(
        TimingSnapshot snapshot,
        DesignationMap? designations,
        ValidationResult? lastValidation)
        => Build(snapshot, designations, lastValidation, BoardVendor.Default);

    /// <summary>
    /// Builds the CURRENT.md content.
    /// Timing fields are ordered and grouped according to the vendor's BIOS OC menu.
    /// </summary>
    /// <param name="snapshot">Current timing snapshot.</param>
    /// <param name="designations">
    /// Designation map used to annotate each group with (manual)/(auto).
    /// If null, all timings are placed under BIOS Settings (conservative).
    /// </param>
    /// <param name="lastValidation">Most recent validation result, or null if none.</param>
    /// <param name="vendor">
    /// Board vendor whose BIOS layout to use. "Auto" is not meaningful here —
    /// resolve it before calling (BoardVendor.Default produces a generic layout).
    /// </param>
    /// <returns>Markdown string.</returns>
    public static string Build(
        TimingSnapshot snapshot,
        DesignationMap? designations,
        ValidationResult? lastValidation,
        BoardVendor vendor)
    {
        var sb = new StringBuilder(1024);
        string date = DateTime.Now.ToString("yyyy-MM-dd");

        sb.AppendLine("# CURRENT — RAMWatch");
        sb.AppendLine($"Updated: {date}");
        sb.AppendLine();

        AppendClockSection(sb, snapshot);
        sb.AppendLine();

        if (designations is null)
        {
            // No designation data — emit grouped layout without manual/auto annotation.
            AppendGroupedTimings(sb, snapshot, BiosLayouts.GetLayout(vendor), annotate: false,
                designations: null);
        }
        else
        {
            AppendGroupedTimings(sb, snapshot, BiosLayouts.GetLayout(vendor), annotate: true,
                designations: designations);
        }

        if (lastValidation is not null)
        {
            sb.AppendLine();
            AppendLastValidation(sb, lastValidation);
        }

        return sb.ToString().TrimEnd();
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private static void AppendClockSection(StringBuilder sb, TimingSnapshot snap)
    {
        int ddr = snap.MemClockMhz * 2;
        sb.AppendLine("## Clock");
        sb.AppendLine($"DDR4-{ddr} | MCLK {snap.MemClockMhz} | FCLK {snap.FclkMhz} | UCLK {snap.UclkMhz}");
    }

    /// <summary>
    /// Emits one markdown section per timing group from the layout.
    /// When annotate is true, appends "(manual)" or "(auto)" per line based on designations.
    /// </summary>
    private static void AppendGroupedTimings(
        StringBuilder sb,
        TimingSnapshot snap,
        IReadOnlyList<TimingGroup> layout,
        bool annotate,
        DesignationMap? designations)
    {
        bool firstGroup = true;
        foreach (var group in layout)
        {
            if (!firstGroup) sb.AppendLine();
            firstGroup = false;

            sb.AppendLine($"## {group.Name}");
            foreach (var field in group.Fields)
            {
                var (_, value) = GetTimingPair(snap, field);

                if (!annotate || designations is null)
                {
                    sb.AppendLine($"{field} = {value}");
                }
                else
                {
                    bool isAuto = designations.Designations.TryGetValue(field, out var desig)
                        && desig == TimingDesignation.Auto;
                    string annotation = isAuto ? " (auto)" : " (manual)";
                    sb.AppendLine($"{field} = {value}{annotation}");
                }
            }
        }
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

    // ── AllTimingPairs — legacy flat ordering (used by LkgMdBuilder and tests) ──

    // Returns all timing fields as (name, value) pairs in the default display order.
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

    // ── GetTimingPair — maps a field name to its snapshot value ───────────────

    /// <summary>
    /// Returns the (name, formatted-value) pair for a named timing field.
    /// Unknown field names return ("Unknown", "?") rather than throwing —
    /// the caller is responsible for only passing valid field names from the layout.
    /// </summary>
    internal static (string name, string value) GetTimingPair(TimingSnapshot snap, string field)
        => field switch
        {
            "CL"       => ("CL",       snap.CL.ToString()),
            "RCDRD"    => ("RCDRD",    snap.RCDRD.ToString()),
            "RCDWR"    => ("RCDWR",    snap.RCDWR.ToString()),
            "RP"       => ("RP",       snap.RP.ToString()),
            "RAS"      => ("RAS",      snap.RAS.ToString()),
            "RC"       => ("RC",       snap.RC.ToString()),
            "CWL"      => ("CWL",      snap.CWL.ToString()),
            "RFC"      => ("RFC",      snap.RFC.ToString()),
            "RFC2"     => ("RFC2",     snap.RFC2.ToString()),
            "RFC4"     => ("RFC4",     snap.RFC4.ToString()),
            "RRDS"     => ("RRDS",     snap.RRDS.ToString()),
            "RRDL"     => ("RRDL",     snap.RRDL.ToString()),
            "FAW"      => ("FAW",      snap.FAW.ToString()),
            "WTRS"     => ("WTRS",     snap.WTRS.ToString()),
            "WTRL"     => ("WTRL",     snap.WTRL.ToString()),
            "WR"       => ("WR",       snap.WR.ToString()),
            "RTP"      => ("RTP",      snap.RTP.ToString()),
            "RDRDSCL"  => ("RDRDSCL",  snap.RDRDSCL.ToString()),
            "WRWRSCL"  => ("WRWRSCL",  snap.WRWRSCL.ToString()),
            "RDRDSC"   => ("RDRDSC",   snap.RDRDSC.ToString()),
            "RDRDSD"   => ("RDRDSD",   snap.RDRDSD.ToString()),
            "RDRDDD"   => ("RDRDDD",   snap.RDRDDD.ToString()),
            "WRWRSC"   => ("WRWRSC",   snap.WRWRSC.ToString()),
            "WRWRSD"   => ("WRWRSD",   snap.WRWRSD.ToString()),
            "WRWRDD"   => ("WRWRDD",   snap.WRWRDD.ToString()),
            "RDWR"     => ("RDWR",     snap.RDWR.ToString()),
            "WRRD"     => ("WRRD",     snap.WRRD.ToString()),
            "REFI"     => ("REFI",     snap.REFI.ToString()),
            "CKE"      => ("CKE",      snap.CKE.ToString()),
            "STAG"     => ("STAG",     snap.STAG.ToString()),
            "MOD"      => ("MOD",      snap.MOD.ToString()),
            "MRD"      => ("MRD",      snap.MRD.ToString()),
            "PHYRDL_A" => ("PHYRDL_A", snap.PHYRDL_A.ToString()),
            "PHYRDL_B" => ("PHYRDL_B", snap.PHYRDL_B.ToString()),
            "GDM"      => ("GDM",      snap.GDM ? "On" : "Off"),
            "Cmd2T"    => ("Cmd2T",    snap.Cmd2T ? "2T" : "1T"),
            _          => (field,      "?"),
        };
}
