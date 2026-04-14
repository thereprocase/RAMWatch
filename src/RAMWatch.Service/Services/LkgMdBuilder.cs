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
    /// Builds the LKG.md content.
    /// </summary>
    /// <param name="lkgSnapshot">LKG timing snapshot, or null if none established.</param>
    /// <param name="designations">Designation map, or null to treat all timings as manual.</param>
    /// <param name="validationUsed">The validation result that qualified this snapshot as LKG.</param>
    /// <returns>Markdown string, or null if lkgSnapshot is null.</returns>
    public static string? Build(
        TimingSnapshot? lkgSnapshot,
        DesignationMap? designations,
        ValidationResult? validationUsed)
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

        AppendTimingSections(sb, lkgSnapshot, designations);

        if (validationUsed is not null)
        {
            sb.AppendLine();
            AppendValidationUsed(sb, validationUsed);
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
        if (designations is null)
        {
            sb.AppendLine("## BIOS Settings (enter these manually)");
            foreach (var (name, value) in CurrentMdBuilder.AllTimingPairsPublic(snap))
                sb.AppendLine($"{name} = {value}");
            return;
        }

        var manual = new List<(string name, string value)>();
        var auto   = new List<(string name, string value)>();

        foreach (var (name, value) in CurrentMdBuilder.AllTimingPairsPublic(snap))
        {
            if (designations.Designations.TryGetValue(name, out var desig) &&
                desig == TimingDesignation.Auto)
            {
                auto.Add((name, value));
            }
            else
            {
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
