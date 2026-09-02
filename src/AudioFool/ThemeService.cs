using System.Windows;
using System.Windows.Media;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace AudioFool;

public static class ThemeService
{
    /// <summary>Every theme the app offers, in menu order.</summary>
    public static readonly string[] Names = ["Dark", "Vista", "PS1"];

    /// <summary>
    /// Each theme names its accent explicitly. WPF-UI derives the lighter primary /
    /// secondary / tertiary shades from this single colour for the dark surface.
    /// <para>
    /// Do <em>not</em> reach for <c>ApplicationAccentColorManager.ApplySystemAccent</c>
    /// to put the blue back. It resolves the theme through
    /// <c>ApplicationThemeManager</c>, which this app never drives - the theme comes
    /// from a <c>ThemesDictionary</c> in App.xaml - so it silently does nothing and
    /// leaves whichever accent was applied last in place.
    /// </para>
    /// </summary>
    private static readonly Color DarkAccent = Color.FromRgb(0x14, 0xB8, 0xA6);

    /// <summary>
    /// Windows' own blue. Vista is an Aero Glass look and reads as blue throughout;
    /// teal fights it.
    /// </summary>
    private static readonly Color VistaAccent = Color.FromRgb(0x00, 0x78, 0xD4);

    /// <summary>PlayStation blue, the accent PS1 selects and acts with.</summary>
    private static readonly Color Ps1Accent = Color.FromRgb(0x2D, 0x7D, 0xFF);

    /// <summary>
    /// The accent brushes <see cref="ApplicationAccentColorManager"/> writes.
    /// <para>
    /// It puts them straight into <c>Application.Resources</c>, which outranks
    /// every merged dictionary, so a theme cannot restate the accent just by
    /// redefining these keys in its own file - that write lands on top of it.
    /// PS1 wants a saturated key carrying white text rather than the pastel WinUI
    /// derives for a dark surface, so its values are copied out of the overlay
    /// and pinned at the same precedence afterwards. Clearing them first is what
    /// lets the other themes fall back to the derived shades.
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
    ];

    private static ResourceDictionary? _overlay;
    private static ResourceDictionary? _motion;

    /// <summary>
    /// The backdrop the active theme asks for. Secondary windows read this so a
    /// dialog does not open as Mica glass in front of an opaque theme.
    /// </summary>
    public static WindowBackdropType Backdrop { get; private set; } = WindowBackdropType.Mica;

    public static void Apply(string theme)
    {
        var app = Application.Current;

        foreach (var key in AccentKeys)
            app.Resources.Remove(key);

        if (_motion is not null)
        {
            app.Resources.MergedDictionaries.Remove(_motion);
            _motion = null;
        }

        if (_overlay is not null)
        {
            app.Resources.MergedDictionaries.Remove(_overlay);
            _overlay = null;
        }

        var backdrop = WindowBackdropType.Mica;
        var accent = DarkAccent;

        switch (theme)
        {
            case "Vista":
                _overlay = Load("VistaTheme");
                backdrop = WindowBackdropType.Acrylic;
                accent = VistaAccent;
                break;

            case "PS1":
                _overlay = Load("Ps1Theme");
                accent = Ps1Accent;

                // Opaque on purpose: PS1 is a moulded hardware surface, and Mica
                // would let the desktop show through the chassis.
                backdrop = WindowBackdropType.None;

                // Motion lives in its own dictionary, so honouring Windows'
                // "show animations" setting is a matter of not merging it. The
                // alternative - zeroing the duration tokens at runtime - is not
                // available: a storyboard inside a style cannot read a
                // DynamicResource, because applying a style seals it and freezes
                // the freezables it holds.
                if (SystemParameters.ClientAreaAnimation)
                    _motion = Load("Ps1Motion");

                break;
        }

        if (_overlay is not null)
            app.Resources.MergedDictionaries.Add(_overlay);

        if (_motion is not null)
            app.Resources.MergedDictionaries.Add(_motion);

        ApplicationAccentColorManager.Apply(accent, ApplicationTheme.Dark);

        if (_overlay is not null)
        {
            foreach (var key in AccentKeys)
            {
                if (_overlay.Contains(key))
                    app.Resources[key] = _overlay[key];
            }
        }

        Backdrop = backdrop;

        if (app.MainWindow is FluentWindow window)
            window.WindowBackdropType = backdrop;
    }

    private static ResourceDictionary Load(string name) => new()
    {
        Source = new Uri($"pack://application:,,,/AudioFool;component/Themes/{name}.xaml"),
    };
}
