using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace AudioFool.Formatting;

/// <summary>
/// Converters for the track grid. Each column binds to the raw property so the
/// grid still sorts numerically when a header is clicked, while displaying the
/// formatted text.
/// </summary>
public abstract class ReadOnlyConverter : IValueConverter
{
    public abstract object Convert(object? value, Type targetType, object? parameter, CultureInfo culture);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException($"{GetType().Name} is display-only.");
}

public sealed class DurationConverter : ReadOnlyConverter
{
    public override object Convert(object? value, Type t, object? p, CultureInfo c) =>
        value is TimeSpan span ? Display.Time(span) : "";
}

public sealed class BitrateConverter : ReadOnlyConverter
{
    public override object Convert(object? value, Type t, object? p, CultureInfo c) =>
        Display.Bitrate(value as int?);
}

public sealed class BitDepthConverter : ReadOnlyConverter
{
    public override object Convert(object? value, Type t, object? p, CultureInfo c) =>
        Display.BitDepth(value as int?);
}

public sealed class SampleRateConverter : ReadOnlyConverter
{
    public override object Convert(object? value, Type t, object? p, CultureInfo c) =>
        Display.SampleRate(value as int?);
}

public sealed class NumberConverter : ReadOnlyConverter
{
    public override object Convert(object? value, Type t, object? p, CultureInfo c) =>
        Display.Number(value as int?);
}

/// <summary>Collapses an element when its bound value is null.</summary>
public sealed class NullToCollapsedConverter : ReadOnlyConverter
{
    public override object Convert(object? value, Type t, object? p, CultureInfo c) =>
        value is null ? Visibility.Collapsed : Visibility.Visible;
}

/// <summary>Shows an element only when its bound value is null - for art placeholders.</summary>
public sealed class NullToVisibleConverter : ReadOnlyConverter
{
    public override object Convert(object? value, Type t, object? p, CultureInfo c) =>
        value is null ? Visibility.Visible : Visibility.Collapsed;
}

public sealed class BoolToVisibilityConverter : ReadOnlyConverter
{
    /// <summary>
    /// ConverterParameter="Inverse" shows the element when the value is false.
    /// "InverseHidden" does too, but hides it rather than collapsing it, so it
    /// keeps its place in the layout.
    /// </summary>
    public override object Convert(object? value, Type t, object? p, CultureInfo c) =>
        (value is true) != (p as string is "Inverse" or "InverseHidden")
            ? Visibility.Visible
            : p as string == "InverseHidden" ? Visibility.Hidden : Visibility.Collapsed;
}

/// <summary>For disabling a control while the opposite condition holds.</summary>
public sealed class InverseBoolConverter : ReadOnlyConverter
{
    public override object Convert(object? value, Type t, object? p, CultureInfo c) =>
        value is not true;
}
