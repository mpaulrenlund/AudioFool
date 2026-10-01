using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace AudioFool.Theming;

/// <summary>
/// Lets a slider's handle centre on the ends of its track, as the mockup's do
/// (spec 6.7), rather than stopping a half-handle short as WPF's Track does.
/// Given the handle's size: "track" widens the Track by half a handle on each
/// side; "fill" shifts the played portion so it starts at the visible track's
/// left end and ends under the handle's centre.
/// </summary>
public sealed class ThumbOverhangConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var half = System.Convert.ToDouble(value, CultureInfo.InvariantCulture) / 2;
        return (parameter as string) == "fill"
            ? new Thickness(half, 0, -half, 0)
            : new Thickness(-half, 0, -half, 0);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
