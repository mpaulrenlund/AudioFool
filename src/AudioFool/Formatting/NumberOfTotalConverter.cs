using System.Globalization;
using System.Windows.Data;

namespace AudioFool.Formatting;

/// <summary>
/// Renders a number and its total as "3/12" for the # and Disc columns. A
/// MultiBinding rather than a computed property on <c>Track</c>: the model stays
/// free of presentation, and the column keeps binding its <c>SortMemberPath</c>
/// to the raw number so a header click still sorts numerically.
/// </summary>
internal sealed class NumberOfTotalConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) =>
        values.Length == 2
            ? Display.NumberOfTotal(values[0] as int?, values[1] as int?)
            : "";

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
