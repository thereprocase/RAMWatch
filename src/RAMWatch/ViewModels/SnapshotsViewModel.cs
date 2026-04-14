using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using RAMWatch.Core.Models;

namespace RAMWatch.ViewModels;

/// <summary>
/// Direction of change for a timing comparison row.
/// "Better" and "Worse" depend on the timing — lower is better for most,
/// but tREFI (REFI) is higher-is-better.
/// </summary>
public enum ComparisonDirection
{
    Unchanged,
    Improved,
    Regressed,
    Neutral // booleans, voltages — no universal "better"
}

/// <summary>
/// One row in the snapshot comparison grid.
/// </summary>
public sealed class ComparisonRow
{
    public required string TimingName { get; init; }
    public required string LeftValue { get; init; }
    public required string RightValue { get; init; }
    public required string Delta { get; init; }
    public required ComparisonDirection Direction { get; init; }
}

/// <summary>
/// Selectable entry in the snapshot dropdown.
/// </summary>
public sealed class SnapshotOption
{
    public required string DisplayName { get; init; }
    public TimingSnapshot? Snapshot { get; init; }

    public override string ToString() => DisplayName;
}

/// <summary>
/// Backing view model for SnapshotsTab. Manages two snapshot slots
/// and produces a comparison grid showing deltas and direction.
/// </summary>
public partial class SnapshotsViewModel : ObservableObject
{
    // Full unfiltered list — kept so we can reapply the filter without
    // re-running the full load when the user toggles ShowAllSnapshots.
    private readonly List<SnapshotOption> _allOptions = [];

    public ObservableCollection<SnapshotOption> AvailableSnapshots { get; } = [];
    public ObservableCollection<ComparisonRow> ComparisonRows { get; } = [];

    [ObservableProperty]
    private SnapshotOption? _leftSelection;

    [ObservableProperty]
    private SnapshotOption? _rightSelection;

    [ObservableProperty]
    private bool _hasSnapshots;

    [ObservableProperty]
    private bool _hasComparison;

    /// <summary>
    /// When false (default), only show snapshots where timings differ from
    /// the previous snapshot, plus any manually-labelled entries.
    /// When true, show every available snapshot.
    /// </summary>
    [ObservableProperty]
    private bool _showAllSnapshots;

    partial void OnLeftSelectionChanged(SnapshotOption? value) => Recompare();
    partial void OnRightSelectionChanged(SnapshotOption? value) => Recompare();
    partial void OnShowAllSnapshotsChanged(bool value) => ApplyFilter();

    /// <summary>
    /// Rebuilds the dropdown options from available snapshots.
    /// Called on each state push from the service.
    /// </summary>
    public void LoadSnapshots(
        List<TimingSnapshot>? available,
        TimingSnapshot? current,
        TimingSnapshot? lkg)
    {
        // Preserve current selections by display name so a state refresh
        // doesn't reset the user's choice.
        var leftName  = LeftSelection?.DisplayName;
        var rightName = RightSelection?.DisplayName;

        _allOptions.Clear();

        if (current is not null)
            _allOptions.Add(new SnapshotOption { DisplayName = "Current", Snapshot = current });

        if (lkg is not null)
            _allOptions.Add(new SnapshotOption { DisplayName = "LKG", Snapshot = lkg });

        if (available is { Count: > 0 })
        {
            foreach (var snap in available)
            {
                var label = !string.IsNullOrEmpty(snap.Label)
                    ? snap.Label
                    : $"Snapshot {snap.Timestamp.ToLocalTime():MM/dd HH:mm}";
                _allOptions.Add(new SnapshotOption { DisplayName = label, Snapshot = snap });
            }
        }

        ApplyFilter();

        // Restore selections or pick sensible defaults
        LeftSelection  = AvailableSnapshots.FirstOrDefault(o => o.DisplayName == leftName)
                         ?? AvailableSnapshots.FirstOrDefault(o => o.DisplayName == "Current");
        RightSelection = AvailableSnapshots.FirstOrDefault(o => o.DisplayName == rightName)
                         ?? AvailableSnapshots.FirstOrDefault(o => o.DisplayName == "LKG");
    }

    /// <summary>
    /// Rebuilds AvailableSnapshots from _allOptions applying the current filter.
    /// Preserves the current left/right selection by display name.
    /// </summary>
    private void ApplyFilter()
    {
        var leftName  = LeftSelection?.DisplayName;
        var rightName = RightSelection?.DisplayName;

        AvailableSnapshots.Clear();

        // Always include the synthetic "Current" and "LKG" entries — they are
        // never auto-boot noise and always useful for comparison.
        foreach (var opt in _allOptions.Where(o => o.Snapshot is null || IsAlwaysVisible(o)))
            AvailableSnapshots.Add(opt);

        if (ShowAllSnapshots)
        {
            // Add all remaining entries in order.
            foreach (var opt in _allOptions.Where(o => !IsAlwaysVisible(o)))
                AvailableSnapshots.Add(opt);
        }
        else
        {
            // Only include snapshots that differ from the previous one in the list,
            // or that carry a manual label (non-auto saves are always included).
            // The comparison is against the preceding entry in the original ordered
            // list (_allOptions), not the filtered list, so "previous" is stable.
            var timed = _allOptions
                .Where(o => !IsAlwaysVisible(o))
                .ToList();

            for (int i = 0; i < timed.Count; i++)
            {
                var opt = timed[i];

                // Manual label: always show.
                if (IsManualLabel(opt))
                {
                    AvailableSnapshots.Add(opt);
                    continue;
                }

                // Auto snapshot: include only if timings differ from the previous entry.
                if (i == 0)
                {
                    // First auto snapshot — always include (no previous to compare).
                    AvailableSnapshots.Add(opt);
                    continue;
                }

                var prev = timed[i - 1];
                if (opt.Snapshot is not null && prev.Snapshot is not null &&
                    !TimingsEqual(opt.Snapshot, prev.Snapshot))
                {
                    AvailableSnapshots.Add(opt);
                }
            }
        }

        HasSnapshots = AvailableSnapshots.Count >= 2;

        // Restore selections after filter rebuild.
        if (leftName is not null)
            LeftSelection  = AvailableSnapshots.FirstOrDefault(o => o.DisplayName == leftName);
        if (rightName is not null)
            RightSelection = AvailableSnapshots.FirstOrDefault(o => o.DisplayName == rightName);
    }

    /// <summary>
    /// Returns true for synthetic entries (Current, LKG) that are always shown
    /// regardless of filter state. These are identified by having a null or
    /// well-known DisplayName rather than a timestamped label.
    /// </summary>
    private static bool IsAlwaysVisible(SnapshotOption opt) =>
        opt.DisplayName is "Current" or "LKG";

    /// <summary>
    /// Returns true when the snapshot carries a user-supplied label rather than
    /// the auto-generated "Snapshot MM/dd HH:mm" format.
    /// The label field on TimingSnapshot is empty for auto-saves.
    /// </summary>
    private static bool IsManualLabel(SnapshotOption opt) =>
        opt.Snapshot is not null && !string.IsNullOrEmpty(opt.Snapshot.Label);

    /// <summary>
    /// Compares the timing fields that a user would care about when scanning
    /// for changes across boots. Voltages and clocks are intentionally excluded
    /// from this equality check — small VDIMM readback noise would produce false
    /// positives. Clock changes are significant but typically captured by a label.
    /// </summary>
    private static bool TimingsEqual(TimingSnapshot a, TimingSnapshot b) =>
        a.CL       == b.CL
     && a.RCDRD    == b.RCDRD
     && a.RCDWR    == b.RCDWR
     && a.RP       == b.RP
     && a.RAS      == b.RAS
     && a.RC       == b.RC
     && a.CWL      == b.CWL
     && a.RFC      == b.RFC
     && a.RFC2     == b.RFC2
     && a.RFC4     == b.RFC4
     && a.RRDS     == b.RRDS
     && a.RRDL     == b.RRDL
     && a.FAW      == b.FAW
     && a.WTRS     == b.WTRS
     && a.WTRL     == b.WTRL
     && a.WR       == b.WR
     && a.RTP      == b.RTP
     && a.RDRDSCL  == b.RDRDSCL
     && a.WRWRSCL  == b.WRWRSCL
     && a.RDRDSC   == b.RDRDSC
     && a.RDRDSD   == b.RDRDSD
     && a.RDRDDD   == b.RDRDDD
     && a.WRWRSC   == b.WRWRSC
     && a.WRWRSD   == b.WRWRSD
     && a.WRWRDD   == b.WRWRDD
     && a.RDWR     == b.RDWR
     && a.WRRD     == b.WRRD
     && a.REFI     == b.REFI
     && a.CKE      == b.CKE
     && a.STAG     == b.STAG
     && a.MOD      == b.MOD
     && a.MRD      == b.MRD
     && a.GDM      == b.GDM
     && a.Cmd2T    == b.Cmd2T
     && a.PowerDown == b.PowerDown
     && a.MemClockMhz == b.MemClockMhz
     && a.FclkMhz  == b.FclkMhz
     && a.UclkMhz  == b.UclkMhz;

    private void Recompare()
    {
        ComparisonRows.Clear();

        var left  = LeftSelection?.Snapshot;
        var right = RightSelection?.Snapshot;

        if (left is null || right is null)
        {
            HasComparison = false;
            return;
        }

        // Build comparison rows for all timing fields
        AddRow("MCLK", left.MemClockMhz, right.MemClockMhz, "MHz", higherIsBetter: true);
        AddRow("FCLK", left.FclkMhz, right.FclkMhz, "MHz", higherIsBetter: true);
        AddRow("UCLK", left.UclkMhz, right.UclkMhz, "MHz", higherIsBetter: true);

        // Primaries — lower is better (tighter)
        AddRow("CL",     left.CL,    right.CL);
        AddRow("tRCDRD", left.RCDRD, right.RCDRD);
        AddRow("tRCDWR", left.RCDWR, right.RCDWR);
        AddRow("tRP",    left.RP,    right.RP);
        AddRow("tRAS",   left.RAS,   right.RAS);
        AddRow("tRC",    left.RC,    right.RC);
        AddRow("CWL",    left.CWL,   right.CWL);

        // tRFC — lower is better
        AddRow("tRFC",  left.RFC,  right.RFC);
        AddRow("tRFC2", left.RFC2, right.RFC2);
        AddRow("tRFC4", left.RFC4, right.RFC4);

        // Secondaries — lower is better
        AddRow("tRRDS",  left.RRDS,    right.RRDS);
        AddRow("tRRDL",  left.RRDL,    right.RRDL);
        AddRow("tFAW",   left.FAW,     right.FAW);
        AddRow("tWTRS",  left.WTRS,    right.WTRS);
        AddRow("tWTRL",  left.WTRL,    right.WTRL);
        AddRow("tWR",    left.WR,      right.WR);
        AddRow("tRTP",   left.RTP,     right.RTP);
        AddRow("RdRdScl", left.RDRDSCL, right.RDRDSCL);
        AddRow("WrWrScl", left.WRWRSCL, right.WRWRSCL);

        // Turn-around — lower is better
        AddRow("RdRdSc", left.RDRDSC, right.RDRDSC);
        AddRow("RdRdSd", left.RDRDSD, right.RDRDSD);
        AddRow("RdRdDd", left.RDRDDD, right.RDRDDD);
        AddRow("WrWrSc", left.WRWRSC, right.WRWRSC);
        AddRow("WrWrSd", left.WRWRSD, right.WRWRSD);
        AddRow("WrWrDd", left.WRWRDD, right.WRWRDD);
        AddRow("tRDWR",  left.RDWR,   right.RDWR);
        AddRow("tWRRD",  left.WRRD,   right.WRRD);

        // Misc
        AddRow("tREFI", left.REFI, right.REFI, higherIsBetter: true);
        AddRow("tCKE",  left.CKE,  right.CKE);
        AddRow("tSTAG", left.STAG, right.STAG);
        AddRow("tMOD",  left.MOD,  right.MOD);
        AddRow("tMRD",  left.MRD,  right.MRD);

        // PHY
        AddRow("PHYRDL A", left.PHYRDL_A, right.PHYRDL_A, neutral: true);
        AddRow("PHYRDL B", left.PHYRDL_B, right.PHYRDL_B, neutral: true);

        // Booleans
        AddBoolRow("GDM",       left.GDM,       right.GDM);
        AddBoolRow("Cmd2T",     left.Cmd2T,     right.Cmd2T);
        AddBoolRow("PowerDown", left.PowerDown, right.PowerDown);

        // Voltages
        AddVoltageRow("VSOC",  left.VSoc,  right.VSoc);
        AddVoltageRow("VDIMM", left.VDimm, right.VDimm);

        HasComparison = ComparisonRows.Count > 0;
    }

    private void AddRow(string name, int leftVal, int rightVal,
        string? suffix = null, bool higherIsBetter = false, bool neutral = false)
    {
        var leftStr  = leftVal  == 0 ? "-" : leftVal.ToString();
        var rightStr = rightVal == 0 ? "-" : rightVal.ToString();

        if (suffix is not null)
        {
            if (leftVal  != 0) leftStr  += $" {suffix}";
            if (rightVal != 0) rightStr += $" {suffix}";
        }

        int diff = rightVal - leftVal;
        string delta;
        ComparisonDirection direction;

        if (leftVal == 0 || rightVal == 0 || diff == 0)
        {
            delta     = "";
            direction = ComparisonDirection.Unchanged;
        }
        else if (neutral)
        {
            delta     = diff > 0 ? $"+{diff}" : diff.ToString();
            direction = ComparisonDirection.Neutral;
        }
        else
        {
            delta = diff > 0 ? $"+{diff}" : diff.ToString();
            // For most timings, lower is better (negative diff = improved).
            // For tREFI/clocks, higher is better (positive diff = improved).
            direction = higherIsBetter
                ? (diff > 0 ? ComparisonDirection.Improved : ComparisonDirection.Regressed)
                : (diff < 0 ? ComparisonDirection.Improved : ComparisonDirection.Regressed);
        }

        ComparisonRows.Add(new ComparisonRow
        {
            TimingName = name,
            LeftValue  = leftStr,
            RightValue = rightStr,
            Delta      = delta,
            Direction  = direction
        });
    }

    private void AddBoolRow(string name, bool leftVal, bool rightVal)
    {
        var leftStr  = name == "Cmd2T" ? (leftVal  ? "2T" : "1T") : (leftVal  ? "On" : "Off");
        var rightStr = name == "Cmd2T" ? (rightVal ? "2T" : "1T") : (rightVal ? "On" : "Off");

        ComparisonRows.Add(new ComparisonRow
        {
            TimingName = name,
            LeftValue  = leftStr,
            RightValue = rightStr,
            Delta      = leftVal == rightVal ? "" : "*",
            Direction  = leftVal == rightVal ? ComparisonDirection.Unchanged : ComparisonDirection.Neutral
        });
    }

    private void AddVoltageRow(string name, double leftVal, double rightVal)
    {
        var leftStr  = leftVal  > 0 ? $"{leftVal:F4}"  : "-";
        var rightStr = rightVal > 0 ? $"{rightVal:F4}" : "-";

        string delta;
        if (leftVal <= 0 || rightVal <= 0 || Math.Abs(leftVal - rightVal) < 0.0001)
        {
            delta = "";
        }
        else
        {
            var diff = rightVal - leftVal;
            delta = diff > 0 ? $"+{diff:F4}" : $"{diff:F4}";
        }

        ComparisonRows.Add(new ComparisonRow
        {
            TimingName = name,
            LeftValue  = leftStr,
            RightValue = rightStr,
            Delta      = delta,
            Direction  = ComparisonDirection.Neutral
        });
    }
}
