using System.Globalization;
using System.Windows.Data;

namespace TradeFix.Master.Converters;

/// <summary>Two-way: a 0-1 fraction (as stored on Transform2D.Crop) shown/edited as a 0-100 whole
/// percentage in the UI.</summary>
public sealed class FractionToPercentConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is double fraction ? Math.Round(fraction * 100) : 0.0;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string text && double.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out var percent))
        {
            return Math.Clamp(percent / 100.0, 0.0, 0.95);
        }

        if (value is double d)
        {
            return Math.Clamp(d / 100.0, 0.0, 0.95);
        }

        return 0.0;
    }
}
