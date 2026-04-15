using System.Text;
using RAMWatch.Core.Models;

namespace RAMWatch.Service.Services;

/// <summary>
/// Builds LKG.md — a phone-readable BIOS checklist for the Last Known Good configuration.
/// Pure function: no I/O, no side effects.
/// Returns null when no LKG snapshot is available.
/// </summary>
public static class LkgMdBuilder
{
    /// <summary>
    /// Builds the LKG.md content using the default (generic) timing layout.
    /// Preserved for callers that do not yet pass a board vendor.
    /// </summary>
    public static string? Build(
        TimingSnapshot? lkgSnapshot,
        DesignationMap? designations,
        ValidationResult? validationUsed)
        => Build(lkgSnapshot, designations, validationUsed, BoardVendor.Default);

    /// <summary>
    /// Builds the LKG.md content.
    /// Timing fields are ordered and grouped according to the vendor's BIOS OC menu.
    /// </summary>
    /// <param name="lkgSnapshot">LKG timing snapshot, or null if none established.</param>
    /// <param name="designations">Designation map, or null to treat all timings as manual.</param>
    /// <param name="validationUsed">The validation result that qualified this snapshot as LKG.</param>
    /// <param name="vendor">
    /// Board vendor whose BIOS layout to use. "Auto" is not meaningful here —
    /// resolve it before calling (BoardVendor.Default produces a generic layout).
    /// </param>
    /// <returns>Markdown string, or null if lkgSnapshot is null.</returns>
    public static string? Build(
        TimingSnapshot? lkgSnapshot,
        DesignationMap? designations,
        ValidationResult? validationUsed,
        BoardVendor vendor)
    {
        if (lkgSnapshot is null)
            return null;

        var sb = new StringBuilder(1024);
        string date = DateTime.Now.ToString("yyyy-MM-dd");

        sb.AppendLine("# LKG (Last Known Good) — RAMWatch");
        sb.AppendLine($"Updated: {date}");
        sb.AppendLine();
        sb.AppendLine("## Revert to these settings if unstable");
        sb.AppendLine();

        AppendClockSection(sb, lkgSnapshot);
        sb.AppendLine();

        AppendGroupedTimings(sb, lkgSnapshot, BiosLayouts.GetLayout(vendor), designations);

        sb.AppendLine();
        AppendVoltageSection(sb, lkgSnapshot);

        if (validationUsed is not null)
        {
            sb.AppendLine();
            AppendValidationUsed(sb, validationUsed);
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

    private static void AppendGroupedTimings(
        StringBuilder sb,
        TimingSnapshot snap,
        IReadOnlyList<TimingGroup> layout,
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
                var (_, value) = CurrentMdBuilder.GetTimingPair(snap, field);

                if (designations is null)
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
    }

    private static void AppendValidationUsed(StringBuilder sb, ValidationResult v)
    {
        string result = v.Passed ? "PASS" : "FAIL";
        string metric = v.MetricUnit.ToLowerInvariant() switch
        {
            "%" or "percent" or "coverage" => $"{v.MetricValue:0}{v.MetricUnit}",
            "cycles"                        => $"{v.MetricValue:0} cycles",
            _                               => $"{v.MetricValue:0}{v.MetricUnit}"
        };
        string date = v.Timestamp.ToString("yyyy-MM-dd");

        sb.AppendLine("## Qualified By");
        sb.AppendLine($"{v.TestTool} {metric} {result} — {date}");
    }
}
