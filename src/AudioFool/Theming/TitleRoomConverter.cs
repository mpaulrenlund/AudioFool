using System.Globalization;
using System.Windows.Data;

namespace AudioFool.Theming;

/// <summary>
/// The width a dialog's title may take, given its title bar's width: the bar
/// less the three window buttons and the title's left margin. WPF-UI's
/// TitleBar puts its header in an Auto column, which offers unlimited width,
/// so without this cap a long title ("Edit Album Tags - Andy Timmons Band
/// Plays Sgt. Pepper") never trims and pushes Close past the window's edge.
/// </summary>
public sealed class TitleRoomConverter : IValueConverter
{
    private const int WindowButtons = 3;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var barWidth = System.Convert.ToDouble(value, CultureInfo.InvariantCulture);
        var numbers = TokenResources.Current?.Numbers;
        var buttonWidth = numbers?.GetValueOrDefault("titleBar.windowButton.width") ?? 0;
        var margin = numbers?.GetValueOrDefault("layout.windowMarginX") ?? 0;

        // Zero before the bar has been laid out: no cap yet, rather than none fitting.
        return barWidth <= 0
            ? double.PositiveInfinity
            : Math.Max(0, barWidth - (WindowButtons * buttonWidth) - margin);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
