using System.Text;
using RAMWatch.Core;
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

        sb.AppendLine();
        AppendVoltageSection(sb, snapshot);

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
        sb.AppendLine("## Clock");
        sb.AppendLine($"{SnapshotDisplayName.DdrLabel(snap.MemClockMhz)} | MCLK {snap.MemClockMhz} | FCLK {snap.FclkMhz} | UCLK {snap.UclkMhz}");
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

    private static void AppendVoltageSection(StringBuilder sb, TimingSnapshot snap)
    {
        sb.AppendLine("## Voltages");
        if (snap.VSoc > 0) sb.AppendLine($"VSOC = {snap.VSoc:F4}V");
        if (snap.VCore > 0) sb.AppendLine($"VCORE = {snap.VCore:F4}V");
        if (snap.VDimm > 0) sb.AppendLine($"VDIMM = {snap.VDimm:F4}V");
        if (snap.VDDP > 0) sb.AppendLine($"VDDP = {snap.VDDP:F4}V");
        if (snap.VDDG_IOD > 0) sb.AppendLine($"VDDG_IOD = {snap.VDDG_IOD:F4}V");
        if (snap.VDDG_CCD > 0) sb.AppendLine($"VDDG_CCD = {snap.VDDG_CCD:F4}V");
        if (snap.Vtt > 0) sb.AppendLine($"VTT = {snap.Vtt:F4}V");
        if (snap.Vpp > 0) sb.AppendLine($"VPP = {snap.Vpp:F4}V");
        if (snap.ProcODT > 0) sb.AppendLine($"ProcODT = {snap.ProcODT:F1}Ω");
        if (snap.RttNom.Length > 0) sb.AppendLine($"RttNom = {snap.RttNom}");
        if (snap.RttWr.Length > 0) sb.AppendLine($"RttWr = {snap.RttWr}");
        if (snap.RttPark.Length > 0) sb.AppendLine($"RttPark = {snap.RttPark}");
        if (snap.ClkDrvStren > 0) sb.AppendLine($"ClkDrvStren = {snap.ClkDrvStren:F1}Ω");
        if (snap.AddrCmdDrvStren > 0) sb.AppendLine($"AddrCmdDrvStren = {snap.AddrCmdDrvStren:F1}Ω");
        if (snap.CsOdtCmdDrvStren > 0) sb.AppendLine($"CsOdtCmdDrvStren = {snap.CsOdtCmdDrvStren:F1}Ω");
        if (snap.CkeDrvStren > 0) sb.AppendLine($"CkeDrvStren = {snap.CkeDrvStren:F1}Ω");
        if (snap.AddrCmdSetup.Length > 0) sb.AppendLine($"AddrCmdSetup = {snap.AddrCmdSetup}");
        if (snap.CsOdtSetup.Length > 0) sb.AppendLine($"CsOdtSetup = {snap.CsOdtSetup}");
        if (snap.CkeSetup.Length > 0) sb.AppendLine($"CkeSetup = {snap.CkeSetup}");
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

    // ── AllTimingPairs — flat ordering (used by LkgMdBuilder and tests) ──────────

    // Returns all timing fields as (name, value) pairs in canonical display order.
    // Integer fields yield raw value strings. Boolean fields (GDM, Cmd2T) yield
    // the display-formatted strings ("On"/"Off", "2T"/"1T") — formatting stays here
    // because these are only ever used for display, never for comparison or export.
    // Internal so LkgMdBuilder can share the same ordered list without duplication.
    internal static IEnumerable<(string name, string value)> AllTimingPairsPublic(TimingSnapshot snap)
        => AllTimingPairs(snap);

    private static IEnumerable<(string name, string value)> AllTimingPairs(TimingSnapshot snap)
    {
        foreach (var (name, get) in TimingSnapshotFields.Timings)
            yield return (name, get(snap).ToString());

        // Boolean display formatting stays here — raw bools are not meaningful
        // in the CURRENT.md / LKG.md checklist context.
        yield return ("GDM",   snap.GDM   ? "On" : "Off");
        yield return ("Cmd2T", snap.Cmd2T ? "2T" : "1T");
    }

    // ── GetTimingPair — maps a field name to its snapshot value ───────────────

    /// <summary>
    /// Returns the (name, formatted-value) pair for a named timing field.
    /// Unknown field names return (field, "?") rather than throwing —
    /// the caller is responsible for only passing valid field names from the layout.
    /// Display formatting (On/Off, 2T/1T) is applied here for boolean fields.
    /// </summary>
    internal static (string name, string value) GetTimingPair(TimingSnapshot snap, string field)
    {
        // Check integer categories in order: Clocks, Timings, Phy.
        foreach (var (name, get) in TimingSnapshotFields.Clocks)
            if (name == field) return (name, get(snap).ToString());
        foreach (var (name, get) in TimingSnapshotFields.Timings)
            if (name == field) return (name, get(snap).ToString());
        foreach (var (name, get) in TimingSnapshotFields.Phy)
            if (name == field) return (name, get(snap).ToString());

        // Boolean display formatting stays at this call site per the memo.
        return field switch
        {
            "GDM"       => ("GDM",       snap.GDM       ? "On" : "Off"),
            "Cmd2T"     => ("Cmd2T",     snap.Cmd2T     ? "2T" : "1T"),
            "PowerDown" => ("PowerDown", snap.PowerDown ? "On" : "Off"),
            _           => (field, "?"),
        };
    }
}
