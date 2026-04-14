using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using RAMWatch.Core.Models;

namespace RAMWatch.ViewModels;

/// <summary>
/// A single row in the dynamic timing display — one field with its current value.
/// </summary>
public sealed class TimingDisplayRow
{
    public string Name { get; }
    public string Value { get; }

    public TimingDisplayRow(string name, string value)
    {
        Name  = name;
        Value = value;
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
    private string _vdimmDisplay = "—";

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

    // ── Public API ───────────────────────────────────────────

    /// <summary>
    /// Applies a snapshot from the service state using the Default timing layout.
    /// Passing null clears all displayed values and hides the timing grid.
    /// </summary>
    public void LoadFromSnapshot(TimingSnapshot? snapshot)
        => LoadFromSnapshot(snapshot, BoardVendor.Default);

    /// <summary>
    /// Applies a snapshot from the service state.
    /// The vendor parameter controls which BIOS layout ordering is used.
    /// Passing null snapshot clears all displayed values and hides the timing grid.
    /// </summary>
    public void LoadFromSnapshot(TimingSnapshot? snapshot, BoardVendor vendor)
    {
        if (snapshot is null)
        {
            HasTimings = false;
            TimingDisplayGroups.Clear();
            return;
        }

        // Clocks
        MclkDisplay = $"{snapshot.MemClockMhz} MHz";
        FclkDisplay = $"{snapshot.FclkMhz} MHz";
        UclkDisplay = $"{snapshot.UclkMhz} MHz";

        // Voltages — four decimal places, trailing zeros communicate precision
        VsocDisplay  = snapshot.VSoc  > 0 ? $"{snapshot.VSoc:F4}"  : "—";
        VdimmDisplay = snapshot.VDimm > 0 ? $"{snapshot.VDimm:F4}" : "—";

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
                .Select(field => new TimingDisplayRow(field, GetFieldValue(snapshot, field)))
                .ToList();
            TimingDisplayGroups.Add(new TimingDisplayGroup(group.Name, rows));
        }

        HasTimings = true;
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
        "GDM"      => snap.GDM ? "On" : "Off",
        "Cmd2T"    => snap.Cmd2T ? "2T" : "1T",
        _          => "?",
    };

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
