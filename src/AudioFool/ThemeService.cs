using System.Windows;
using System.Windows.Media;
using Wpf.Ui.Appearance;

namespace AudioFool;

/// <summary>
/// Puts the PS1 look in place. There is one theme, applied once at startup,
/// before the main window is built.
/// </summary>
public static class ThemeService
{
    /// <summary>
    /// PS1's primary keys are a dark grey, not a colour: its four controller
    /// colours are kept for state, and WPF-UI's accent would otherwise spread one
    /// of them over every Primary button, check box and switch.
    /// <para>
    /// Do <em>not</em> reach for <c>ApplicationAccentColorManager.ApplySystemAccent</c>.
    /// It resolves the theme through <c>ApplicationThemeManager</c>, which this app
    /// never drives - the base comes from a <c>ThemesDictionary</c> in App.xaml - so
    /// it silently does nothing.
    /// </para>
    /// </summary>
    private static readonly Color Accent = Color.FromRgb(0x4A, 0x47, 0x46);

    /// <summary>
    /// The accent brushes <see cref="ApplicationAccentColorManager"/> writes.
    /// <para>
    /// It puts them straight into <c>Application.Resources</c>, which outranks
    /// every merged dictionary, so the theme cannot restate the accent just by
    /// redefining these keys in its own file - that write lands on top of it.
    /// PS1 wants its own values rather than the shades WinUI derives, so they are
    /// copied out of the theme dictionary and pinned at the same precedence
    /// afterwards.
    /// </para>
    /// </summary>
    private static readonly string[] AccentKeys =
    [
        "AccentFillColorDefaultBrush",
        "AccentFillColorSecondaryBrush",
        "AccentFillColorTertiaryBrush",
        "AccentFillColorDisabledBrush",
        "AccentTextFillColorPrimaryBrush",
        "AccentTextFillColorSecondaryBrush",
        "AccentTextFillColorTertiaryBrush",
        "AccentTextFillColorDisabledBrush",
        "AccentControlElevationBorderBrush",
        "AccentFillColorSelectedTextBackgroundBrush",
    ];

    /// <summary>
    /// Merges the theme over the design system. Call it before building any
    /// window: a DynamicResource for a Style is resolved while the element
    /// initialises, so a dictionary merged afterwards is too late for it.
    /// <para>
    /// App.xaml already runs on WPF-UI's Light base, since PS1 is dark ink on
    /// grey plastic: any stock key the theme does not restate falls back to a
    /// light value rather than to white text.
    /// </para>
    /// </summary>
    public static void Apply()
    {
        var app = Application.Current;
        var theme = Load("Ps1Theme");
        app.Resources.MergedDictionaries.Add(theme);

        // Motion lives in its own dictionary, so honouring Windows' "show
        // animations" setting is a matter of not merging it. The alternative -
        // zeroing the duration tokens at runtime - is not available: a storyboard
        // inside a style cannot read a DynamicResource, because applying a style
        // seals it and freezes the freezables it holds.
        if (SystemParameters.ClientAreaAnimation)
            app.Resources.MergedDictionaries.Add(Load("Ps1Motion"));

        ApplicationAccentColorManager.Apply(Accent, ApplicationTheme.Light);

        foreach (var key in AccentKeys)
        {
            if (theme.Contains(key))
                app.Resources[key] = theme[key];
        }
    }

    private static ResourceDictionary Load(string name) => new()
    {
        Source = new Uri($"pack://application:,,,/AudioFool;component/Themes/{name}.xaml"),
    };
}
