using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace TradeFix.Master.Converters;

/// <summary>Visible when the bound value is non-null (and, for strings specifically,
/// non-empty). Pass ConverterParameter="Invert" to flip the result.</summary>
public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var isEmpty = value switch
        {
            null => true,
            string s => string.IsNullOrEmpty(s),
            _ => false
        };

        if (string.Equals(parameter as string, "Invert", StringComparison.OrdinalIgnoreCase))
        {
            isEmpty = !isEmpty;
        }

        return isEmpty ? Visibility.Collapsed : Visibility.Visible;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
