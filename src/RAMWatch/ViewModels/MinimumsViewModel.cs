using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using RAMWatch.Core.Models;

namespace RAMWatch.ViewModels;

/// <summary>
/// One row in the minimums table.
/// </summary>
public sealed class MinimumRow
{
    public required string TimingName { get; init; }
    public required string GroupName { get; init; }
    public required string CurrentValue { get; init; }
    public required string BestPostedValue { get; init; }
    public required string BestValidatedValue { get; init; }
    public required string Room { get; init; }
    /// <summary>"AtFloor" when Room is 0, "HasRoom" when positive, "NoData" when missing.</summary>
    public required string RoomSeverity { get; init; }
    /// <summary>True when BestPosted is tighter than BestValidated (untested territory).</summary>
    public bool UntestedWarning { get; init; }
}

/// <summary>
/// Backing view model for the Minimums tab.
/// Displays per-frequency tightest timing values.
/// </summary>
public partial class MinimumsViewModel : ObservableObject
{
    public ObservableCollection<MinimumRow> Rows { get; } = [];
    public ObservableCollection<string> AvailableFrequencies { get; } = [];

    [ObservableProperty]
    private string? _selectedFrequency;

    [ObservableProperty]
    private string _bootCountLabel = "";

    [ObservableProperty]
    private bool _hasData;

    private List<FrequencyMinimums>? _minimums;
    private TimingSnapshot? _currentTimings;

    /// <summary>
    /// Timing fields where higher is better.
    /// </summary>
    private static readonly HashSet<string> HigherIsBetter = new(StringComparer.Ordinal) { "REFI" };

    /// <summary>
    /// Ordered list of (group, field) pairs for display.
    /// </summary>
    private static readonly (string Group, string Field)[] DisplayFields =
    [
        ("PRIMARY", "CL"), ("PRIMARY", "RCDRD"), ("PRIMARY", "RCDWR"),
        ("PRIMARY", "RP"), ("PRIMARY", "RAS"), ("PRIMARY", "RC"), ("PRIMARY", "CWL"),
        ("TRFC", "RFC"), ("TRFC", "RFC2"), ("TRFC", "RFC4"),
        ("SECONDARY", "RRDS"), ("SECONDARY", "RRDL"), ("SECONDARY", "FAW"),
        ("SECONDARY", "WTRS"), ("SECONDARY", "WTRL"), ("SECONDARY", "WR"), ("SECONDARY", "RTP"),
        ("SECONDARY", "RDRDSCL"), ("SECONDARY", "WRWRSCL"),
        ("TURN-AROUND", "RDRDSC"), ("TURN-AROUND", "RDRDSD"), ("TURN-AROUND", "RDRDDD"),
        ("TURN-AROUND", "WRWRSC"), ("TURN-AROUND", "WRWRSD"), ("TURN-AROUND", "WRWRDD"),
        ("READ/WRITE", "RDWR"), ("READ/WRITE", "WRRD"),
        ("MISC", "REFI"), ("MISC", "CKE"), ("MISC", "STAG"), ("MISC", "MOD"), ("MISC", "MRD"),
    ];

    partial void OnSelectedFrequencyChanged(string? value) => Rebuild();

    public void LoadFromState(List<FrequencyMinimums>? minimums, TimingSnapshot? currentTimings)
    {
        _minimums = minimums;
        _currentTimings = currentTimings;

        // Rebuild frequency list
        AvailableFrequencies.Clear();
        if (minimums is { Count: > 0 })
        {
            foreach (var m in minimums)
                AvailableFrequencies.Add($"DDR4-{m.MemClockMhz * 2}");

            // Default to current frequency if available
            if (currentTimings is not null && currentTimings.MemClockMhz > 0)
            {
                var currentLabel = $"DDR4-{currentTimings.MemClockMhz * 2}";
                SelectedFrequency = AvailableFrequencies.Contains(currentLabel)
                    ? currentLabel
                    : AvailableFrequencies.FirstOrDefault();
            }
            else
            {
                SelectedFrequency ??= AvailableFrequencies.FirstOrDefault();
            }
        }
        else
        {
            SelectedFrequency = null;
        }

        Rebuild();
    }

    private void Rebuild()
    {
        Rows.Clear();

        if (_minimums is null || SelectedFrequency is null)
        {
            HasData = false;
            BootCountLabel = "";
            return;
        }

        // Parse frequency from label "DDR4-3600" → 1800
        var freqStr = SelectedFrequency.Replace("DDR4-", "");
        if (!int.TryParse(freqStr, out int ddrRate))
        {
            HasData = false;
            return;
        }
        int mclk = ddrRate / 2;

        var freq = _minimums.Find(m => m.MemClockMhz == mclk);
        if (freq is null)
        {
            HasData = false;
            return;
        }

        BootCountLabel = freq.ValidatedBootCount > 0
            ? $"{freq.PostedBootCount} posted, {freq.ValidatedBootCount} validated"
            : $"{freq.PostedBootCount} posted";

        foreach (var (group, field) in DisplayFields)
        {
            int current = _currentTimings is not null && _currentTimings.MemClockMhz == mclk
                ? GetTimingValue(_currentTimings, field)
                : 0;

            freq.BestPosted.TryGetValue(field, out int bestPosted);
            freq.BestValidated.TryGetValue(field, out int bestValidated);

            bool higherBetter = HigherIsBetter.Contains(field);

            string currentStr = current > 0 ? current.ToString() : "--";
            string postedStr = bestPosted > 0 ? bestPosted.ToString() : "--";
            string validatedStr = bestValidated > 0 ? bestValidated.ToString() : "--";

            string room;
            string roomSeverity;
            bool untestedWarning = false;

            if (current == 0 || bestPosted == 0)
            {
                room = "--";
                roomSeverity = "NoData";
            }
            else
            {
                int delta = higherBetter ? bestPosted - current : current - bestPosted;
                room = delta > 0 ? delta.ToString() : "0";
                roomSeverity = delta > 0 ? "HasRoom" : "AtFloor";

                // Untested warning: BestPosted is tighter than BestValidated
                if (bestValidated > 0 && bestPosted > 0)
                {
                    bool postedIsTighter = higherBetter
                        ? bestPosted > bestValidated
                        : bestPosted < bestValidated;
                    untestedWarning = postedIsTighter;
                }
            }

            Rows.Add(new MinimumRow
            {
                TimingName = field,
                GroupName = group,
                CurrentValue = currentStr,
                BestPostedValue = postedStr,
                BestValidatedValue = validatedStr,
                Room = room,
                RoomSeverity = roomSeverity,
                UntestedWarning = untestedWarning
            });
        }

        HasData = Rows.Count > 0;
    }

    private static int GetTimingValue(TimingSnapshot snap, string field) => field switch
    {
        "CL" => snap.CL, "RCDRD" => snap.RCDRD, "RCDWR" => snap.RCDWR,
        "RP" => snap.RP, "RAS" => snap.RAS, "RC" => snap.RC, "CWL" => snap.CWL,
        "RFC" => snap.RFC, "RFC2" => snap.RFC2, "RFC4" => snap.RFC4,
        "RRDS" => snap.RRDS, "RRDL" => snap.RRDL, "FAW" => snap.FAW,
        "WTRS" => snap.WTRS, "WTRL" => snap.WTRL, "WR" => snap.WR, "RTP" => snap.RTP,
        "RDRDSCL" => snap.RDRDSCL, "WRWRSCL" => snap.WRWRSCL,
        "RDRDSC" => snap.RDRDSC, "RDRDSD" => snap.RDRDSD, "RDRDDD" => snap.RDRDDD,
        "WRWRSC" => snap.WRWRSC, "WRWRSD" => snap.WRWRSD, "WRWRDD" => snap.WRWRDD,
        "RDWR" => snap.RDWR, "WRRD" => snap.WRRD,
        "REFI" => snap.REFI, "CKE" => snap.CKE, "STAG" => snap.STAG,
        "MOD" => snap.MOD, "MRD" => snap.MRD,
        _ => 0
    };
}
