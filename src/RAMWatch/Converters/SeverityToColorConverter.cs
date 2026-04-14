using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace RAMWatch.Converters;

public sealed class SeverityToColorConverter : IValueConverter
{
    private static readonly SolidColorBrush Green = new(Color.FromRgb(0x00, 0xC8, 0x53));
    private static readonly SolidColorBrush Red = new(Color.FromRgb(0xFF, 0x17, 0x44));
    private static readonly SolidColorBrush Amber = new(Color.FromRgb(0xFF, 0xB3, 0x00));
    private static readonly SolidColorBrush Gray = new(Color.FromRgb(0x61, 0x61, 0x61));

    static SeverityToColorConverter()
    {
        Green.Freeze();
        Red.Freeze();
        Amber.Freeze();
        Gray.Freeze();
    }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value?.ToString() switch
        {
            "Critical" or "Error" => Red,
            "Warning" => Amber,
            "Notice" => Amber,
            "Info" => Green,
            _ => Gray
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
