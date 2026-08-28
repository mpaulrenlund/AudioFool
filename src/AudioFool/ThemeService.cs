using System.Windows;
using System.Windows.Media;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace AudioFool;

public static class ThemeService
{
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

    private static ResourceDictionary? _overlay;

    public static void Apply(string theme)
    {
        var app = Application.Current;

        if (_overlay is not null)
        {
            app.Resources.MergedDictionaries.Remove(_overlay);
            _overlay = null;
        }

        var backdrop = WindowBackdropType.Mica;

        if (theme == "Vista")
        {
            _overlay = new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/Themes/VistaTheme.xaml")
            };
            app.Resources.MergedDictionaries.Add(_overlay);
            backdrop = WindowBackdropType.Acrylic;
        }

        ApplicationAccentColorManager.Apply(
            theme == "Vista" ? VistaAccent : DarkAccent,
            ApplicationTheme.Dark);

        if (app.MainWindow is FluentWindow window)
            window.WindowBackdropType = backdrop;
    }
}
