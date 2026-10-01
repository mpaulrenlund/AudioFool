using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace AudioFool.Formatting;

/// <summary>
/// Whether the two bound values are the same object reference (i.e. this row's
/// Track is the one currently playing): a bool for a bool target, otherwise
/// Visible or Collapsed.
/// </summary>
internal sealed class IsCurrentTrackConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        var same = values.Length == 2 && values[0] is not null && ReferenceEquals(values[0], values[1]);

        if (targetType == typeof(bool) || targetType == typeof(object))
            return same;

        return same ? Visibility.Visible : Visibility.Collapsed;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
