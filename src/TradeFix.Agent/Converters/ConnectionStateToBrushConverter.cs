using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using TradeFix.Shared.Enums;

namespace TradeFix.Agent.Converters;

public sealed class ConnectionStateToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value is NodeConnectionState state
            ? state switch
            {
                NodeConnectionState.Online or NodeConnectionState.Synced => "Brush.Success",
                NodeConnectionState.Connecting or NodeConnectionState.Pairing or NodeConnectionState.Syncing => "Brush.Warning",
                NodeConnectionState.Warning => "Brush.Warning",
                NodeConnectionState.Error or NodeConnectionState.Offline => "Brush.Danger",
                _ => "Brush.TextSecondary"
            }
            : "Brush.TextSecondary";

        return Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
