using System.Collections;
using System.Globalization;
using System.Windows.Data;
using RAMWatch.ViewModels;

namespace RAMWatch.Converters;

/// <summary>
/// Splits a list of TimingDisplayGroup into two columns using greedy bin-packing.
/// The converter parameter selects which column: "0" for left, "1" for right.
///
/// Height estimate per group: (row count + 1) * 24 + 32 px
///   - 24 px per row
///   - +1 for the header row inside the border
///   - 32 px for the group header TextBlock + margin below the group
/// Groups are assigned to whichever column is currently shorter, left-wins on tie.
/// </summary>
public sealed class GroupColumnConverter : IValueConverter
{
    private const int RowHeight   = 24;
    private const int HeaderPad   = 32;

    private static int EstimateHeight(TimingDisplayGroup group)
        => (group.Rows.Count + 1) * RowHeight + HeaderPad;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not IEnumerable source)
            return Binding.DoNothing;

        int column = parameter?.ToString() == "1" ? 1 : 0;

        var groups = source.Cast<TimingDisplayGroup>().ToList();

        // Greedy bin-packing: assign each group to the shorter column.
        var left  = new List<TimingDisplayGroup>();
        var right = new List<TimingDisplayGroup>();
        int leftH  = 0;
        int rightH = 0;

        foreach (var g in groups)
        {
            int h = EstimateHeight(g);
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

        return column == 0 ? left : right;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
