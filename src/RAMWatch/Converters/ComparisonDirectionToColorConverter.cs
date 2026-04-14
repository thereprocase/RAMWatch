using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace RAMWatch.Converters;

/// <summary>
/// Converts <see cref="ViewModels.ComparisonDirection"/> to a foreground brush.
/// Green = improved, Red = regressed, White = unchanged or neutral.
/// </summary>
public sealed class ComparisonDirectionToColorConverter : IValueConverter
{
    // Cache brushes to avoid per-row allocations
    private static readonly SolidColorBrush Improved = new(Color.FromRgb(0x00, 0xC8, 0x53));
    private static readonly SolidColorBrush Regressed = new(Color.FromRgb(0xFF, 0x17, 0x44));
    private static readonly SolidColorBrush Unchanged = new(Color.FromRgb(0xE0, 0xE0, 0xE0));

    static ComparisonDirectionToColorConverter()
    {
        Improved.Freeze();
        Regressed.Freeze();
        Unchanged.Freeze();
    }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is ViewModels.ComparisonDirection direction)
        {
            return direction switch
            {
                ViewModels.ComparisonDirection.Improved => Improved,
                ViewModels.ComparisonDirection.Regressed => Regressed,
                _ => Unchanged
            };
        }
        return Unchanged;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
