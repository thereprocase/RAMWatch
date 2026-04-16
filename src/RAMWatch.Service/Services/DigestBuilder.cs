using System.Text;
using RAMWatch.Core.Models;

namespace RAMWatch.Service.Services;

/// <summary>
/// Builds the AI Helper Digest — a dense, context-window-friendly text export
/// for pasting into a conversation with an AI tuning assistant.
///
/// Pure function: no file I/O, no side effects. All inputs come in via parameters.
/// Target output: under 2000 tokens (~1500 words) for a typical configuration.
/// </summary>
public static class DigestBuilder
{
    /// <summary>
    /// Builds the digest text.
    /// </summary>
    /// <param name="state">Current service state (error counts, driver status).</param>
    /// <param name="current">Current timing snapshot, or null if driver not loaded.</param>
    /// <param name="lkg">Last Known Good snapshot, or null if none validated yet.</param>
    /// <param name="recentValidations">Recent validation results, newest first.</param>
    /// <param name="drifts">Recent drift events detected across boots.</param>
    /// <param name="designations">Manual/auto designation map, or null if unavailable.</param>
    /// <param name="historyCount">Total number of timing snapshots on record.</param>
    /// <returns>Digest text, ready for clipboard.</returns>
    public static string BuildDigest(
        ServiceState state,
        TimingSnapshot? current,
        TimingSnapshot? lkg,
        List<ValidationResult> recentValidations,
        List<DriftEvent> drifts,
        DesignationMap? designations,
        int historyCount)
    {
        var sb = new StringBuilder(2048);
        var now = DateTime.Now;

        // Build a lookup from timing name to the most recent drift event for
        // that timing so we can annotate affected rows inline.
        var latestDrift = BuildDriftLookup(drifts);

        // Header
        sb.AppendLine($"RAMWatch Digest — {now:yyyy-MM-dd}");
        sb.AppendLine();

        // Hardware / RAM section
        AppendHardwareSection(sb, state, current);
        sb.AppendLine();

        // Timing sections
        if (current == null || state.DriverStatus == "not_found")
        {
            sb.AppendLine("Timings: [Not available — driver not loaded]");
        }
        else
        {
            AppendClockLine(sb, current);
            sb.AppendLine();
            AppendTimingSections(sb, current, designations, latestDrift);
        }

        sb.AppendLine();

        // LKG comparison
        AppendLkgSection(sb, current, lkg, recentValidations);
        sb.AppendLine();

        // Validation history
        AppendValidationHistory(sb, recentValidations, current);
        sb.AppendLine();

        // Error summary
        AppendErrorSection(sb, state);

        // Next planned change note — pulled from snapshot label if present
        if (!string.IsNullOrWhiteSpace(current?.Notes))
        {
            sb.AppendLine();
            sb.AppendLine($"Next planned: {current.Notes}");
        }

        return sb.ToString().TrimEnd();
    }

    // -------------------------------------------------------------------------
    // Section builders
    // -------------------------------------------------------------------------

    private static void AppendHardwareSection(StringBuilder sb, ServiceState state, TimingSnapshot? current)
    {
        if (state.DriverStatus == "not_found")
        {
            sb.AppendLine("Hardware: [PawnIO driver required]");
            return;
        }

        // Build hardware line from whatever is populated in the snapshot.
        // Fields not yet available (board, RAM part, die type) are omitted rather
        // than shown as empty — reduces noise for the AI reader.
        var hwParts = new List<string>();
        if (!string.IsNullOrWhiteSpace(current?.CpuCodename))
            hwParts.Add(current.CpuCodename);
        if (!string.IsNullOrWhiteSpace(current?.BiosVersion))
            hwParts.Add(current.BiosVersion);
        if (!string.IsNullOrWhiteSpace(current?.AgesaVersion))
            hwParts.Add($"AGESA {current.AgesaVersion}");

        if (hwParts.Count == 0)
            sb.AppendLine("Hardware: [data not yet populated]");
        else
            sb.AppendLine($"Hardware: {string.Join(" | ", hwParts)}");

        // Thermal/power telemetry — point-in-time at digest generation.
        var tp = state.ThermalPower;
        if (tp is not null && tp.Sources != ThermalDataSource.None)
        {
            var parts = new List<string>();
            if (tp.CpuTempC > 0) parts.Add($"Tctl {tp.CpuTempC:F1}°C");
            if (tp.SocketPowerW > 0) parts.Add($"{tp.SocketPowerW:F0}W");
            if (tp.PptLimitW > 0) parts.Add($"PPT {tp.PptActualW:F0}/{tp.PptLimitW:F0}W");
            if (tp.TdcLimitA > 0) parts.Add($"TDC {tp.TdcActualA:F0}/{tp.TdcLimitA:F0}A");
            if (tp.EdcLimitA > 0) parts.Add($"EDC {tp.EdcActualA:F0}/{tp.EdcLimitA:F0}A");
            if (parts.Count > 0)
                sb.AppendLine($"Thermal: {string.Join(" | ", parts)}");
        }
    }

    private static void AppendClockLine(StringBuilder sb, TimingSnapshot snap)
    {
        int ddr = snap.MemClockMhz * 2;
        string ratio = DeriveRatio(snap);
        string vdimmLabel = snap.VDimm > 0 ? $" | VDIMM {snap.VDimm:F3}V" : "";
        sb.AppendLine($"Current: DDR4-{ddr} | FCLK {snap.FclkMhz}{vdimmLabel} | {ratio}");
    }

    private static string DeriveRatio(TimingSnapshot snap)
    {
        // Derive MCLK:FCLK:UCLK ratio label.
        // 1:1:1 when all three are equal; otherwise show actual values.
        if (snap.MemClockMhz == 0 || snap.FclkMhz == 0 || snap.UclkMhz == 0)
            return "ratio n/a";

        if (snap.MemClockMhz == snap.FclkMhz && snap.FclkMhz == snap.UclkMhz)
            return "1:1:1";

        // Normalise to smallest unit (GCD of the three values).
        int g = Gcd(Gcd(snap.MemClockMhz, snap.FclkMhz), snap.UclkMhz);
        return $"{snap.MemClockMhz / g}:{snap.FclkMhz / g}:{snap.UclkMhz / g}";
    }

    private static void AppendTimingSections(
        StringBuilder sb,
        TimingSnapshot snap,
        DesignationMap? desig,
        Dictionary<string, DriftEvent> latestDrift)
    {
        // Primaries
        {
            string label = GroupLabel("Primaries", ["CL", "RCDRD", "RCDWR", "RP", "RAS", "RC"], desig);
            sb.Append($"{label}: CL {snap.CL} | RCDRD {snap.RCDRD} | RCDWR {snap.RCDWR}");
            sb.Append($" | RP {snap.RP} | RAS {snap.RAS} | RC {snap.RC}");
            sb.AppendLine();
            AppendDriftWarnings(sb, ["CL", "RCDRD", "RCDWR", "RP", "RAS", "RC"], latestDrift);
        }

        // CWL / GDM / Cmd
        {
            string cwlLabel = DesigLabel("CWL", desig);
            string gdmLabel = DesigLabel("GDM", desig);
            string cmdStr = snap.Cmd2T ? "2T" : "1T";
            sb.AppendLine($"CWL ({cwlLabel}): {snap.CWL} | GDM: {(snap.GDM ? "On" : "Off")} | Cmd: {cmdStr}");
            AppendDriftWarnings(sb, ["CWL", "GDM"], latestDrift);
        }

        // tRFC
        {
            string rfcLabel = GroupLabel("tRFC", ["RFC", "RFC2", "RFC4"], desig);
            // Convert clocks to nanoseconds at MCLK rate.
            // tRFC is counted in MCLK cycles. 1 MCLK cycle = 1000/MCLK_MHz ns.
            // (DDR is double data rate but timing registers count MCLK, not DDR clocks.)
            string nsStr = snap.MemClockMhz > 0
                ? $"{(int)Math.Round(snap.RFC * 1000.0 / snap.MemClockMhz)}ns"
                : "?ns";
            sb.AppendLine($"{rfcLabel}: {snap.RFC}/{snap.RFC2}/{snap.RFC4} ({nsStr})");
            AppendDriftWarnings(sb, ["RFC", "RFC2", "RFC4"], latestDrift);
        }

        // Secondaries
        {
            string label = GroupLabel("Secondaries", ["RRDS", "RRDL", "FAW", "WTRS", "WTRL", "WR"], desig);
            sb.AppendLine($"{label}: RRDS {snap.RRDS} | RRDL {snap.RRDL} | FAW {snap.FAW} | WTRS {snap.WTRS} | WTRL {snap.WTRL} | WR {snap.WR}");
            AppendDriftWarnings(sb, ["RRDS", "RRDL", "FAW", "WTRS", "WTRL", "WR"], latestDrift);
        }

        // SCL
        {
            string label = GroupLabel("SCL", ["RDRDSCL", "WRWRSCL"], desig);
            sb.AppendLine($"{label}: RDRD {snap.RDRDSCL} | WRWR {snap.WRWRSCL}");
            AppendDriftWarnings(sb, ["RDRDSCL", "WRWRSCL"], latestDrift);
        }

        // Misc
        {
            string label = GroupLabel("Misc", ["RTP", "RDWR", "WRRD", "CKE", "REFI", "STAG"], desig);
            sb.AppendLine($"{label}: RTP {snap.RTP} | RDWR {snap.RDWR} | WRRD {snap.WRRD} | CKE {snap.CKE} | REFI {snap.REFI} | STAG {snap.STAG}");
            AppendDriftWarnings(sb, ["RTP", "RDWR", "WRRD", "CKE", "REFI", "STAG"], latestDrift);
        }

        // Turn-around
        {
            string label = GroupLabel("Turn-around", ["RDRDSC", "RDRDSD", "RDRDDD", "WRWRSC", "WRWRSD", "WRWRDD"], desig);
            sb.AppendLine($"{label}: RDRDSC {snap.RDRDSC} | RDRDSD {snap.RDRDSD} | RDRDDD {snap.RDRDDD} | WRWRSC {snap.WRWRSC} | WRWRSD {snap.WRWRSD} | WRWRDD {snap.WRWRDD}");
            AppendDriftWarnings(sb, ["RDRDSC", "RDRDSD", "RDRDDD", "WRWRSC", "WRWRSD", "WRWRDD"], latestDrift);
        }

        // MOD group
        {
            string label = GroupLabel("MOD", ["MOD", "MRD"], desig);
            sb.AppendLine($"{label}: MOD {snap.MOD} | MRD {snap.MRD}");
            AppendDriftWarnings(sb, ["MOD", "MRD"], latestDrift);
        }

        // PHY — per-channel values; mismatch between A and B is normal (PHY training artifact)
        {
            string label = GroupLabel("PHY", ["PHYRDL_A", "PHYRDL_B"], desig);
            sb.AppendLine($"{label}: PHYRDL {snap.PHYRDL_A}/{snap.PHYRDL_B}");
            // PHY mismatch is expected; do not emit drift warnings for it here.
        }

        // Voltages — always auto (hardware read-back), label omitted
        var vParts = new List<string>();
        if (snap.VSoc > 0) vParts.Add($"VSOC {snap.VSoc:F3}");
        if (snap.VCore > 0) vParts.Add($"VCore {snap.VCore:F3}");
        if (snap.VDimm > 0) vParts.Add($"VDIMM {snap.VDimm:F3}");
        if (snap.VDDP > 0) vParts.Add($"VDDP {snap.VDDP:F3}");
        if (snap.VDDG_IOD > 0) vParts.Add($"VDDG_IOD {snap.VDDG_IOD:F3}");
        if (snap.VDDG_CCD > 0) vParts.Add($"VDDG_CCD {snap.VDDG_CCD:F3}");
        if (snap.Vtt > 0) vParts.Add($"Vtt {snap.Vtt:F3}");
        if (snap.Vpp > 0) vParts.Add($"Vpp {snap.Vpp:F3}");
        if (vParts.Count > 0) sb.AppendLine($"Voltages: {string.Join(" | ", vParts)}");
        // Resistance/termination
        if (snap.ProcODT > 0 || snap.RttNom.Length > 0)
        {
            var rParts = new List<string>();
            if (snap.ProcODT > 0) rParts.Add($"ProcODT {snap.ProcODT:F1}Ω");
            if (snap.RttNom.Length > 0) rParts.Add($"RttNom {snap.RttNom}");
            if (snap.RttWr.Length > 0) rParts.Add($"RttWr {snap.RttWr}");
            if (snap.RttPark.Length > 0) rParts.Add($"RttPark {snap.RttPark}");
            sb.AppendLine($"Signal: {string.Join(" | ", rParts)}");
        }
    }

    private static void AppendDriftWarnings(
        StringBuilder sb,
        string[] timingNames,
        Dictionary<string, DriftEvent> latestDrift)
    {
        foreach (string name in timingNames)
        {
            if (!latestDrift.TryGetValue(name, out var d))
                continue;

            // Inline warning indented under the row it belongs to.
            sb.AppendLine($"  \u26a0 {name} drifted: {d.ExpectedValue}\u2192{d.ActualValue} on boot {d.Timestamp:MM/dd HH:mm}");
        }
    }

    private static void AppendLkgSection(
        StringBuilder sb,
        TimingSnapshot? current,
        TimingSnapshot? lkg,
        List<ValidationResult> recentValidations)
    {
        if (lkg == null)
        {
            sb.AppendLine("LKG: None — no validated configuration on record");
            return;
        }

        if (current == null)
        {
            sb.AppendLine("LKG: [Timings unavailable — cannot compare]");
            return;
        }

        if (SnapshotsEqual(current, lkg))
        {
            // Find the most recent passing validation timestamp.
            var lastPass = recentValidations
                .Where(v => v.Passed)
                .Select(v => (DateTime?)v.Timestamp)
                .FirstOrDefault();

            string validatedStr = lastPass.HasValue
                ? $" (validated {lastPass.Value:MM/dd HH:mm})"
                : "";

            sb.AppendLine($"LKG: Same as current{validatedStr}");
        }
        else
        {
            sb.AppendLine("LKG diff:");
            AppendSnapshotDiff(sb, current, lkg);
        }
    }

    private static void AppendSnapshotDiff(StringBuilder sb, TimingSnapshot current, TimingSnapshot lkg)
    {
        // Compare every integer timing field and emit lines for changed values.
        var diffs = new List<string>();

        void Check(string name, int cur, int lkgVal)
        {
            if (cur != lkgVal) diffs.Add($"  {name}: {lkgVal}\u2192{cur}");
        }

        void CheckBool(string name, bool cur, bool lkgVal)
        {
            if (cur != lkgVal) diffs.Add($"  {name}: {lkgVal}\u2192{cur}");
        }

        Check("CL",       current.CL,       lkg.CL);
        Check("RCDRD",    current.RCDRD,    lkg.RCDRD);
        Check("RCDWR",    current.RCDWR,    lkg.RCDWR);
        Check("RP",       current.RP,       lkg.RP);
        Check("RAS",      current.RAS,      lkg.RAS);
        Check("RC",       current.RC,       lkg.RC);
        Check("CWL",      current.CWL,      lkg.CWL);
        Check("RFC",      current.RFC,      lkg.RFC);
        Check("RFC2",     current.RFC2,     lkg.RFC2);
        Check("RFC4",     current.RFC4,     lkg.RFC4);
        Check("RRDS",     current.RRDS,     lkg.RRDS);
        Check("RRDL",     current.RRDL,     lkg.RRDL);
        Check("FAW",      current.FAW,      lkg.FAW);
        Check("WTRS",     current.WTRS,     lkg.WTRS);
        Check("WTRL",     current.WTRL,     lkg.WTRL);
        Check("WR",       current.WR,       lkg.WR);
        Check("RTP",      current.RTP,      lkg.RTP);
        Check("RDRDSCL",  current.RDRDSCL,  lkg.RDRDSCL);
        Check("WRWRSCL",  current.WRWRSCL,  lkg.WRWRSCL);
        Check("RDRDSC",   current.RDRDSC,   lkg.RDRDSC);
        Check("RDRDSD",   current.RDRDSD,   lkg.RDRDSD);
        Check("RDRDDD",   current.RDRDDD,   lkg.RDRDDD);
        Check("WRWRSC",   current.WRWRSC,   lkg.WRWRSC);
        Check("WRWRSD",   current.WRWRSD,   lkg.WRWRSD);
        Check("WRWRDD",   current.WRWRDD,   lkg.WRWRDD);
        Check("RDWR",     current.RDWR,     lkg.RDWR);
        Check("WRRD",     current.WRRD,     lkg.WRRD);
        Check("REFI",     current.REFI,     lkg.REFI);
        Check("CKE",      current.CKE,      lkg.CKE);
        Check("STAG",     current.STAG,     lkg.STAG);
        Check("MOD",      current.MOD,      lkg.MOD);
        Check("MRD",      current.MRD,      lkg.MRD);
        CheckBool("GDM",  current.GDM,      lkg.GDM);
        CheckBool("Cmd2T", current.Cmd2T,   lkg.Cmd2T);

        if (diffs.Count == 0)
        {
            // Clocks or voltages differ but no timing field changed.
            sb.AppendLine("  (clocks or voltages differ — timing fields identical)");
        }
        else
        {
            foreach (string line in diffs)
                sb.AppendLine(line);
        }
    }

    private static void AppendValidationHistory(
        StringBuilder sb,
        List<ValidationResult> validations,
        TimingSnapshot? current)
    {
        if (validations.Count == 0)
        {
            sb.AppendLine("Validation History: No validation results");
            return;
        }

        sb.AppendLine($"Validation History (last {validations.Count}):");
        foreach (var v in validations)
        {
            string passStr = v.Passed ? "PASS" : "FAIL";
            string metric  = FormatMetric(v);
            string timing  = FormatTimingSummary(v, current);
            string notes   = string.IsNullOrWhiteSpace(v.Notes) ? "" : $" ({v.Notes})";
            sb.AppendLine($"  {v.Timestamp:MM/dd HH:mm}  {v.TestTool,-6} {metric,-8} {passStr}  {timing}{notes}");
        }
    }

    private static string FormatMetric(ValidationResult v)
    {
        // "12400%" for coverage-based tools, "25 cycles" for TM5, plain value otherwise.
        return v.MetricUnit.ToLowerInvariant() switch
        {
            "%" or "percent" or "coverage" => $"{v.MetricValue:0}{v.MetricUnit}",
            "cycles" => $"{v.MetricValue:0}cyc",
            _ => $"{v.MetricValue:0}{v.MetricUnit}"
        };
    }

    private static string FormatTimingSummary(ValidationResult v, TimingSnapshot? current)
    {
        // Produce a brief "CL-RCDRD-RP-RAS tRFCxxx" string from the snapshot
        // linked to this validation. We only have the current snapshot in scope;
        // for historical results the snapshot link isn't resolved here — we emit
        // the primaries if the validation's snapshot ID matches the current one,
        // otherwise we show just the snapshot ID prefix.
        //
        // Full snapshot resolution requires a snapshot lookup table that belongs
        // in the caller (ValidationTestLogger), not in the pure digest builder.
        // For now: match current snapshot or omit primaries.
        if (current == null || v.ActiveSnapshotId != current.SnapshotId)
            return v.ActiveSnapshotId != null ? $"snap:{v.ActiveSnapshotId[..Math.Min(8, v.ActiveSnapshotId.Length)]}" : "";

        return $"{current.CL}-{current.RCDRD}-{current.RP}-{current.RAS} tRFC{current.RFC}";
    }

    private static void AppendErrorSection(StringBuilder sb, ServiceState state)
    {
        // This-boot error count from ErrorSources aggregate.
        int thisBootTotal = state.Errors.Sum(e => e.Count);

        // All-time breakdown by category name convention: WHEA, MCE, BSOD.
        int whea = state.Errors.Where(e => e.Name.Contains("WHEA", StringComparison.OrdinalIgnoreCase)).Sum(e => e.Count);
        int mce  = state.Errors.Where(e => e.Name.Contains("MCE",  StringComparison.OrdinalIgnoreCase)).Sum(e => e.Count);
        int bsod = state.Errors.Where(e => e.Name.Contains("BSOD", StringComparison.OrdinalIgnoreCase)
                                        || e.Name.Contains("BugCheck", StringComparison.OrdinalIgnoreCase)).Sum(e => e.Count);

        sb.AppendLine($"Errors (this boot): {thisBootTotal}");
        sb.AppendLine($"Errors (all time): {whea} WHEA, {mce} MCE, {bsod} BSOD");
    }

    // -------------------------------------------------------------------------
    // Label helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Returns a group label like "Primaries (manual)", "Primaries (auto)", or
    /// "Primaries" when designations are unavailable or mixed.
    /// </summary>
    private static string GroupLabel(string groupName, string[] timings, DesignationMap? desig)
    {
        if (desig == null || desig.Designations.Count == 0)
            return groupName;

        var known = timings
            .Where(t => desig.Designations.TryGetValue(t, out var d) && d != TimingDesignation.Unknown)
            .Select(t => desig.Designations[t])
            .Distinct()
            .ToList();

        if (known.Count == 1)
            return $"{groupName} ({known[0].ToString().ToLowerInvariant()})";

        return groupName;
    }

    /// <summary>
    /// Returns a single timing's designation label string, or "unknown" if unset.
    /// </summary>
    private static string DesigLabel(string timingName, DesignationMap? desig)
    {
        if (desig == null)
            return "unknown";
        if (desig.Designations.TryGetValue(timingName, out var d) && d != TimingDesignation.Unknown)
            return d.ToString().ToLowerInvariant();
        return "unknown";
    }

    // -------------------------------------------------------------------------
    // Utility
    // -------------------------------------------------------------------------

    private static Dictionary<string, DriftEvent> BuildDriftLookup(List<DriftEvent> drifts)
    {
        // Keep only the most recent drift event per timing name.
        var result = new Dictionary<string, DriftEvent>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in drifts)
        {
            if (!result.ContainsKey(d.TimingName) ||
                d.Timestamp > result[d.TimingName].Timestamp)
            {
                result[d.TimingName] = d;
            }
        }
        return result;
    }

    private static bool SnapshotsEqual(TimingSnapshot a, TimingSnapshot b)
    {
        return a.CL == b.CL
            && a.RCDRD == b.RCDRD
            && a.RCDWR == b.RCDWR
            && a.RP == b.RP
            && a.RAS == b.RAS
            && a.RC == b.RC
            && a.CWL == b.CWL
            && a.RFC == b.RFC
            && a.RFC2 == b.RFC2
            && a.RFC4 == b.RFC4
            && a.RRDS == b.RRDS
            && a.RRDL == b.RRDL
            && a.FAW == b.FAW
            && a.WTRS == b.WTRS
            && a.WTRL == b.WTRL
            && a.WR == b.WR
            && a.RTP == b.RTP
            && a.RDRDSCL == b.RDRDSCL
            && a.WRWRSCL == b.WRWRSCL
            && a.RDRDSC == b.RDRDSC
            && a.RDRDSD == b.RDRDSD
            && a.RDRDDD == b.RDRDDD
            && a.WRWRSC == b.WRWRSC
            && a.WRWRSD == b.WRWRSD
            && a.WRWRDD == b.WRWRDD
            && a.RDWR == b.RDWR
            && a.WRRD == b.WRRD
            && a.REFI == b.REFI
            && a.CKE == b.CKE
            && a.STAG == b.STAG
            && a.MOD == b.MOD
            && a.MRD == b.MRD
            && a.PowerDown == b.PowerDown
            && a.GDM == b.GDM
            && a.Cmd2T == b.Cmd2T
            && a.MemClockMhz == b.MemClockMhz
            && a.FclkMhz == b.FclkMhz
            && a.UclkMhz == b.UclkMhz;
    }

    private static int Gcd(int a, int b) => b == 0 ? a : Gcd(b, a % b);
}
