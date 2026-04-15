using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using RAMWatch.Core.Models;

namespace RAMWatch.ViewModels;

/// <summary>
/// A single row in the dynamic timing display — one field with its current value
/// and an optional designation indicator for manually-set timings.
/// </summary>
public sealed class TimingDisplayRow
{
    public string Name { get; }
    public string Value { get; }

    /// <summary>
    /// Designation for this timing: "Manual", "Auto", or "Unknown".
    /// Empty string when no designation data is available.
    /// </summary>
    public string Designation { get; }

    /// <summary>
    /// Unicode bullet shown after the value for manually-designated timings.
    /// Empty string for Auto and Unknown so the column stays narrow by default.
    /// </summary>
    public string DesignationIndicator => Designation == "Manual" ? "●" : "";

    public TimingDisplayRow(string name, string value, string designation = "")
    {
        Name        = name;
        Value       = value;
        Designation = designation;
    }
}

/// <summary>
/// One named group of timing rows, corresponding to one group in the vendor BIOS layout.
/// </summary>
public sealed class TimingDisplayGroup
{
    public string GroupName { get; }
    public IReadOnlyList<TimingDisplayRow> Rows { get; }

    public TimingDisplayGroup(string groupName, IReadOnlyList<TimingDisplayRow> rows)
    {
        GroupName = groupName;
        Rows      = rows;
    }
}

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
    // These stay as flat properties — Clocks are not part of BIOS timing ordering.

    [ObservableProperty]
    private string _mclkDisplay = "—";

    [ObservableProperty]
    private string _fclkDisplay = "—";

    [ObservableProperty]
    private string _uclkDisplay = "—";

    // ── Voltages ─────────────────────────────────────────────
    // Voltages also stay as flat properties.

    [ObservableProperty]
    private string _vsocDisplay = "—";

    [ObservableProperty]
    private string _vcoreDisplay = "—";

    [ObservableProperty]
    private string _vdimmDisplay = "—";

    [ObservableProperty]
    private string _vddpDisplay = "—";

    [ObservableProperty]
    private string _vddgIodDisplay = "—";

    [ObservableProperty]
    private string _vddgCcdDisplay = "—";

    [ObservableProperty]
    private string _vttDisplay = "—";

    [ObservableProperty]
    private string _vppDisplay = "—";

    // ── Signal integrity ─────────────────────────────────────
    [ObservableProperty]
    private string _procOdtDisplay = "—";

    [ObservableProperty]
    private string _rttNomDisplay = "—";

    [ObservableProperty]
    private string _rttWrDisplay = "—";

    [ObservableProperty]
    private string _rttParkDisplay = "—";

    [ObservableProperty]
    private string _clkDrvStrenDisplay = "—";

    [ObservableProperty]
    private string _addrCmdDrvStrenDisplay = "—";

    [ObservableProperty]
    private string _csOdtCmdDrvStrenDisplay = "—";

    [ObservableProperty]
    private string _ckeDrvStrenDisplay = "—";

    // ── DIMM info ─────────────────────────────────────────────
    // Read once from Win32_PhysicalMemory at service startup.
    // Collapsed summary line (e.g., "2x 8GB DDR4-3200 (Micron)")
    // plus detail rows for the expanded view.

    [ObservableProperty]
    private string _dimmSummary = "";

    [ObservableProperty]
    private bool _hasDimms;

    public ObservableCollection<DimmDisplayRow> DimmRows { get; } = [];

    // ── System info ──────────────────────────────────────────

    [ObservableProperty]
    private string _cpuCodename = "";

    // ── Vendor label ─────────────────────────────────────────

    [ObservableProperty]
    private string _biosLayoutLabel = "";

    // ── Dynamic timing groups ────────────────────────────────
    // Built from the vendor BIOS layout — replaces the old per-field properties
    // for the timing section between Clocks and Voltages.

    public ObservableCollection<TimingDisplayGroup> TimingDisplayGroups { get; } = [];

    // Masonry two-column split — computed after TimingDisplayGroups is populated.
    // Greedy bin-packing: assign each group to the shorter column.
    [ObservableProperty]
    private List<TimingDisplayGroup> _leftColumnGroups = [];

    [ObservableProperty]
    private List<TimingDisplayGroup> _rightColumnGroups = [];

    // ── Summary label ────────────────────────────────────────

    // "CL16-20-20-42" — built from primary timings, empty when no timings available.
    // Used as the default snapshot name suggestion in the naming dialog.
    [ObservableProperty]
    private string _primaryTimingsLabel = "";

    // ── Public API ───────────────────────────────────────────

    /// <summary>
    /// Applies a snapshot from the service state using the Default timing layout.
    /// Passing null clears all displayed values and hides the timing grid.
    /// </summary>
    public void LoadFromSnapshot(TimingSnapshot? snapshot)
        => LoadFromSnapshot(snapshot, BoardVendor.Default, null);

    /// <summary>
    /// Applies a snapshot from the service state.
    /// The vendor parameter controls which BIOS layout ordering is used.
    /// Passing null snapshot clears all displayed values and hides the timing grid.
    /// </summary>
    public void LoadFromSnapshot(TimingSnapshot? snapshot, BoardVendor vendor)
        => LoadFromSnapshot(snapshot, vendor, null);

    /// <summary>
    /// Applies a snapshot from the service state.
    /// The vendor parameter controls which BIOS layout ordering is used.
    /// The designations map (field name → "Manual"/"Auto"/"Unknown") drives the
    /// designation indicator (●) shown on each row.
    /// Passing null snapshot clears all displayed values and hides the timing grid.
    /// </summary>
    public void LoadFromSnapshot(
        TimingSnapshot? snapshot,
        BoardVendor vendor,
        IReadOnlyDictionary<string, string>? designations)
    {
        if (snapshot is null)
        {
            HasTimings = false;
            PrimaryTimingsLabel = "";
            TimingDisplayGroups.Clear();
            LeftColumnGroups = [];
            RightColumnGroups = [];
            return;
        }

        // Primary timings summary label — "CL16-20-20-42" format used in snapshot names.
        PrimaryTimingsLabel = $"CL{snapshot.CL}-{snapshot.RCDRD}-{snapshot.RP}-{snapshot.RAS}";

        // Clocks
        MclkDisplay = $"{snapshot.MemClockMhz} MHz";
        FclkDisplay = $"{snapshot.FclkMhz} MHz";
        UclkDisplay = $"{snapshot.UclkMhz} MHz";

        // Voltages — four decimal places, trailing zeros communicate precision.
        // VDIMM on desktop boards is not readable from hardware registers; BIOS WMI
        // on some boards (e.g. MSI) provides it, but most will always return 0.
        // Display "N/A" rather than "—" so users know the field exists but is not available
        // (as opposed to "—" which reads as "no hardware reads at all").
        VsocDisplay     = snapshot.VSoc     > 0 ? $"{snapshot.VSoc:F4}"     : "N/A";
        VcoreDisplay    = snapshot.VCore    > 0 ? $"{snapshot.VCore:F4}"    : "N/A";
        VdimmDisplay    = snapshot.VDimm    > 0 ? $"{snapshot.VDimm:F4}"    : "N/A";
        VddpDisplay     = snapshot.VDDP     > 0 ? $"{snapshot.VDDP:F4}"     : "N/A";
        VddgIodDisplay  = snapshot.VDDG_IOD > 0 ? $"{snapshot.VDDG_IOD:F4}" : "N/A";
        VddgCcdDisplay  = snapshot.VDDG_CCD > 0 ? $"{snapshot.VDDG_CCD:F4}" : "N/A";
        VttDisplay      = snapshot.Vtt      > 0 ? $"{snapshot.Vtt:F4}"      : "N/A";
        VppDisplay      = snapshot.Vpp      > 0 ? $"{snapshot.Vpp:F4}"      : "N/A";

        // Signal integrity
        ProcOdtDisplay        = snapshot.ProcODT > 0 ? $"{snapshot.ProcODT:F1} Ω" : "N/A";
        RttNomDisplay         = snapshot.RttNom.Length > 0 ? snapshot.RttNom : "N/A";
        RttWrDisplay          = snapshot.RttWr.Length > 0 ? snapshot.RttWr : "N/A";
        RttParkDisplay        = snapshot.RttPark.Length > 0 ? snapshot.RttPark : "N/A";
        ClkDrvStrenDisplay    = snapshot.ClkDrvStren > 0 ? $"{snapshot.ClkDrvStren:F1} Ω" : "N/A";
        AddrCmdDrvStrenDisplay = snapshot.AddrCmdDrvStren > 0 ? $"{snapshot.AddrCmdDrvStren:F1} Ω" : "N/A";
        CsOdtCmdDrvStrenDisplay = snapshot.CsOdtCmdDrvStren > 0 ? $"{snapshot.CsOdtCmdDrvStren:F1} Ω" : "N/A";
        CkeDrvStrenDisplay    = snapshot.CkeDrvStren > 0 ? $"{snapshot.CkeDrvStren:F1} Ω" : "N/A";

        // System info
        CpuCodename = snapshot.CpuCodename;

        // BIOS layout label
        BiosLayoutLabel = vendor == BoardVendor.Default
            ? ""
            : $"Layout: {vendor}";

        // Build dynamic groups from the vendor layout
        TimingDisplayGroups.Clear();
        var layout = BiosLayouts.GetLayout(vendor);
        foreach (var group in layout)
        {
            var rows = group.Fields
                .Select(field =>
                {
                    string desig = designations is not null && designations.TryGetValue(field, out var d)
                        ? d
                        : "";
                    return new TimingDisplayRow(field, GetFieldValue(snapshot, field), desig);
                })
                .ToList();
            TimingDisplayGroups.Add(new TimingDisplayGroup(group.Name, rows));
        }

        // Compute masonry column split
        ComputeColumns();

        HasTimings = true;
    }

    private void ComputeColumns()
    {
        var left = new List<TimingDisplayGroup>();
        var right = new List<TimingDisplayGroup>();
        int leftH = 0, rightH = 0;

        foreach (var g in TimingDisplayGroups)
        {
            int h = (g.Rows.Count + 1) * 24 + 32;
            if (leftH <= rightH)
            {
                left.Add(g);
                leftH += h;
            }
            else
            {
                right.Add(g);
                rightH += h;
            }
        }

        LeftColumnGroups = left;
        RightColumnGroups = right;
    }

    // ── Helpers ──────────────────────────────────────────────

    /// <summary>
    /// Returns the formatted display value for a named timing field.
    /// RFC fields include the nanosecond conversion when MCLK is known.
    /// </summary>
    private static string GetFieldValue(TimingSnapshot snap, string field) => field switch
    {
        "CL"       => snap.CL.ToString(),
        "RCDRD"    => snap.RCDRD.ToString(),
        "RCDWR"    => snap.RCDWR.ToString(),
        "RP"       => snap.RP.ToString(),
        "RAS"      => snap.RAS.ToString(),
        "RC"       => snap.RC.ToString(),
        "CWL"      => snap.CWL.ToString(),
        "RFC"      => FormatRfc(snap.RFC, snap.MemClockMhz),
        "RFC2"     => FormatRfc(snap.RFC2, snap.MemClockMhz),
        "RFC4"     => FormatRfc(snap.RFC4, snap.MemClockMhz),
        "RRDS"     => snap.RRDS.ToString(),
        "RRDL"     => snap.RRDL.ToString(),
        "FAW"      => snap.FAW.ToString(),
        "WTRS"     => snap.WTRS.ToString(),
        "WTRL"     => snap.WTRL.ToString(),
        "WR"       => snap.WR.ToString(),
        "RTP"      => snap.RTP.ToString(),
        "RDRDSCL"  => snap.RDRDSCL.ToString(),
        "WRWRSCL"  => snap.WRWRSCL.ToString(),
        "RDRDSC"   => snap.RDRDSC.ToString(),
        "RDRDSD"   => snap.RDRDSD.ToString(),
        "RDRDDD"   => snap.RDRDDD.ToString(),
        "WRWRSC"   => snap.WRWRSC.ToString(),
        "WRWRSD"   => snap.WRWRSD.ToString(),
        "WRWRDD"   => snap.WRWRDD.ToString(),
        "RDWR"     => snap.RDWR.ToString(),
        "WRRD"     => snap.WRRD.ToString(),
        "REFI"     => snap.REFI.ToString(),
        "CKE"      => snap.CKE.ToString(),
        "STAG"     => snap.STAG.ToString(),
        "MOD"      => snap.MOD.ToString(),
        "MRD"      => snap.MRD.ToString(),
        "PHYRDL_A" => snap.PHYRDL_A.ToString(),
        "PHYRDL_B" => snap.PHYRDL_B.ToString(),
        "GDM"       => snap.GDM ? "On" : "Off",
        "Cmd2T"     => snap.Cmd2T ? "2T" : "1T",
        "PowerDown" => snap.PowerDown ? "On" : "Off",
        _           => "?",
    };

    /// <summary>
    /// Updates the DIMM information display. Called once when the first state
    /// message arrives with non-null Dimms. Idempotent — subsequent calls with
    /// the same data are harmless.
    /// </summary>
    public void LoadDimms(List<DimmInfo>? dimms)
    {
        DimmRows.Clear();

        if (dimms is null || dimms.Count == 0)
        {
            HasDimms = false;
            DimmSummary = "";
            return;
        }

        // Build detail rows
        foreach (var d in dimms)
        {
            long gb = d.CapacityBytes / (1024 * 1024 * 1024);
            string capacity = gb > 0 ? $"{gb} GB" : $"{d.CapacityBytes / (1024 * 1024)} MB";
            string speed = d.SpeedMTs > 0 ? $"DDR4-{d.SpeedMTs}" : "";
            string detail = string.Join("  ", new[] { capacity, speed, d.Manufacturer.Trim(), d.PartNumber.Trim() }
                .Where(s => s.Length > 0));

            DimmRows.Add(new DimmDisplayRow(d.Slot, detail));
        }

        // Build summary line: "2x 8GB DDR4-3200 (Micron)" or similar
        var distinctCapacities = dimms
            .Where(d => d.CapacityBytes > 0)
            .Select(d => d.CapacityBytes / (1024 * 1024 * 1024))
            .Distinct()
            .ToList();

        var distinctSpeeds = dimms
            .Where(d => d.SpeedMTs > 0)
            .Select(d => d.SpeedMTs)
            .Distinct()
            .ToList();

        var distinctMfrs = dimms
            .Select(d => d.Manufacturer.Trim())
            .Where(m => m.Length > 0)
            .Distinct()
            .ToList();

        var parts = new List<string>();
        parts.Add($"{dimms.Count}x");
        if (distinctCapacities.Count == 1)
            parts.Add($"{distinctCapacities[0]} GB");
        else if (distinctCapacities.Count > 1)
            parts.Add($"{distinctCapacities.Sum()} GB total");

        if (distinctSpeeds.Count == 1)
            parts.Add($"DDR4-{distinctSpeeds[0]}");

        if (distinctMfrs.Count == 1)
            parts.Add($"({distinctMfrs[0]})");
        else if (distinctMfrs.Count > 1)
            parts.Add($"({string.Join(", ", distinctMfrs)})");

        DimmSummary = string.Join(" ", parts);
        HasDimms = true;
    }

    /// <summary>
    /// Formats an RFC clock value as "NNN (NNNns)".
    /// The ns conversion is: clocks / (2 * MCLK_MHz) * 1000.
    /// Returns just the clock count when MCLK is zero (unknown).
    /// </summary>
    private static string FormatRfc(int clocks, int mclkMhz)
    {
        if (clocks == 0) return "—";
        if (mclkMhz <= 0) return clocks.ToString();

        // tRFC is counted in MCLK cycles. One MCLK cycle = 1000/MCLK_MHz ns.
        // (DDR is double data rate but timing registers count MCLK, not DDR clocks.)
        double ns = clocks * 1000.0 / mclkMhz;
        return $"{clocks} ({ns:F0}ns)";
    }
}

/// <summary>
/// One row in the DIMM detail display — slot name and formatted info.
/// </summary>
public sealed class DimmDisplayRow
{
    public string Slot { get; }
    public string Detail { get; }

    public DimmDisplayRow(string slot, string detail)
    {
        Slot = slot;
        Detail = detail;
    }
}
