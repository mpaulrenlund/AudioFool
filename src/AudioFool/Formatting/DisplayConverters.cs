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
    public override object Convert(object? value, Type t, object? p, CultureInfo c) =>
        value is true ? Visibility.Visible : Visibility.Collapsed;
}

/// <summary>For disabling a control while the opposite condition holds.</summary>
public sealed class InverseBoolConverter : ReadOnlyConverter
{
    public override object Convert(object? value, Type t, object? p, CultureInfo c) =>
        value is not true;
}

/// <summary>Dims a whole control group when it has been disabled.</summary>
public sealed class EnabledOpacityConverter : ReadOnlyConverter
{
    public override object Convert(object? value, Type t, object? p, CultureInfo c) =>
        value is true ? 1.0 : 0.32;
}
