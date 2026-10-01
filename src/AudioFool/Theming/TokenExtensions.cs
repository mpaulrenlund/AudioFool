using System.Globalization;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;

namespace AudioFool.Theming;

/// <summary>
/// <c>{theme:Token albums.rowHeight}</c>: a theme token, converted to whatever the
/// property it is set on needs.
/// <para>
/// The tokens are plain numbers, and a <c>DynamicResource</c> hands its value over
/// unconverted, so a number cannot go straight into a Margin, a CornerRadius or a
/// grid column's width. This looks the property's type up and converts: a number
/// becomes a uniform Thickness or CornerRadius, or a pixel GridLength or
/// DataGridLength, and <c>"right"</c> becomes the matching alignment. Anything else -
/// brushes, styles, effects - passes through as it is.
/// </para>
/// <para>
/// One limit: in a Style, WPF resolves a Setter's Property only after its Value, so
/// there is nothing to convert to and the token arrives as it is. That is why radii
/// are stored as CornerRadius already; for a margin or padding in a setter, use
/// <c>{theme:Thickness}</c>, which always makes a Thickness.
/// </para>
/// <para>
/// It reads the value once, when the XAML loads, from the application's resources.
/// That is safe because <see cref="ThemeService.Apply"/> loads the tokens before any
/// window is built, and the theme never changes while the app runs.
/// </para>
/// </summary>
[MarkupExtensionReturnType(typeof(object))]
public sealed class TokenExtension : MarkupExtension
{
    public TokenExtension() { }

    public TokenExtension(string key) => Key = key;

    [ConstructorArgument("key")]
    public string Key { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider) =>
        Tokens.Convert(Tokens.Find(Key), Tokens.TargetType(serviceProvider), Key);
}

/// <summary>
/// <c>{theme:Thickness X=songTable.listPaddingX, Y=songTable.listPaddingY}</c>: a
/// Thickness put together from number tokens, for the paddings and margins the
/// token file gives per axis or per side. <c>All</c>, then <c>X</c> / <c>Y</c>,
/// then the four sides, each overriding the one before. A side can also be a
/// literal number, for the zeros.
/// </summary>
[MarkupExtensionReturnType(typeof(Thickness))]
public sealed class ThicknessExtension : MarkupExtension
{
    public string? All { get; set; }
    public string? X { get; set; }
    public string? Y { get; set; }
    public string? Left { get; set; }
    public string? Top { get; set; }
    public string? Right { get; set; }
    public string? Bottom { get; set; }

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        double left = 0, top = 0, right = 0, bottom = 0;
        if (All is not null) left = top = right = bottom = Tokens.Number(All);
        if (X is not null) left = right = Tokens.Number(X);
        if (Y is not null) top = bottom = Tokens.Number(Y);
        if (Left is not null) left = Tokens.Number(Left);
        if (Top is not null) top = Tokens.Number(Top);
        if (Right is not null) right = Tokens.Number(Right);
        if (Bottom is not null) bottom = Tokens.Number(Bottom);
        return new Thickness(left, top, right, bottom);
    }
}

internal static class Tokens
{
    public static object Find(string key) =>
        Application.Current?.TryFindResource(key)
        ?? throw new InvalidOperationException(
            $"Theme token \"{key}\" was not found. Check the name against design/theme-tokens.json, "
            + "and that ThemeService.Apply() ran before this XAML was loaded.");

    public static double Number(string keyOrNumber)
    {
        if (double.TryParse(keyOrNumber, NumberStyles.Float, CultureInfo.InvariantCulture, out var literal))
            return literal;
        return Find(keyOrNumber) is double d
            ? d
            : throw new InvalidOperationException($"Theme token \"{keyOrNumber}\" is not a number.");
    }

    /// <summary>
    /// The type of the property being set. In a style the target is the Setter, whose
    /// own property says what the value is for; in a template it is the dependency
    /// property itself.
    /// </summary>
    public static Type? TargetType(IServiceProvider sp)
    {
        if (sp.GetService(typeof(IProvideValueTarget)) is not IProvideValueTarget target)
            return null;
        if (target.TargetObject is Setter { Property: { } setterProperty })
            return setterProperty.PropertyType;
        return target.TargetProperty switch
        {
            DependencyProperty dp => dp.PropertyType,
            PropertyInfo pi => pi.PropertyType,
            _ => null,
        };
    }

    public static object Convert(object value, Type? target, string key)
    {
        if (target is null || target == typeof(object) || target.IsInstanceOfType(value))
            return value;

        if (value is double n)
        {
            if (target == typeof(Thickness)) return new Thickness(n);
            if (target == typeof(CornerRadius)) return new CornerRadius(n);
            if (target == typeof(GridLength)) return new GridLength(n);
            if (target == typeof(DataGridLength)) return new DataGridLength(n);
            if (target == typeof(int)) return (int)Math.Round(n);
        }

        if (value is string s && target.IsEnum && Enum.TryParse(target, s, ignoreCase: true, out var e))
            return e!;

        throw new InvalidOperationException($"Theme token \"{key}\" ({value.GetType().Name}) cannot be used as a {target.Name}.");
    }
}
