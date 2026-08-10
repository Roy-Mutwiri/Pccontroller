using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace TradeFix.Master.Converters;

public sealed class ResourceKeyToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string key && Application.Current.TryFindResource(key) is Brush brush)
        {
            return brush;
        }

        return Brushes.Gray;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
