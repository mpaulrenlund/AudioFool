using System.Windows;
using System.Windows.Media;
using AudioFool.Theming;
using Wpf.Ui.Appearance;

namespace AudioFool;

/// <summary>
/// Puts the theme in place. There is one theme, applied once at startup, before
/// the main window is built.
/// </summary>
public static class ThemeService
{
    /// <summary>
    /// Merges the theme over WPF-UI's Light base (App.xaml). Call it before
    /// building any window: a DynamicResource for a Style is resolved while the
    /// element initialises, so a dictionary merged afterwards is too late for it.
    /// </summary>
    public static void Apply()
    {
        var app = Application.Current;

        // The central theme from design/theme-tokens.json. Its resource names are
        // the JSON paths (color.panel.bg, albums.rowHeight), which no WPF-UI key
        // shares. First, so {theme:Token} can read it from anything loaded after.
        app.Resources.MergedDictionaries.Add(TokenResources.Build(TokenResources.LoadEmbedded()));

        // The components built from the tokens. Its keys all start with "theme.",
        // apart from the implicit ToolTip, ContextMenu and MenuItem styles.
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/AudioFool;component/Theming/Chrome.xaml"),
        });

        // WPF-UI's menu templates fix their corners; this sets the theme's.
        MenuCorners.Register();

        // WPF-UI's accent: the theme's dark grey, so a stock control that still
        // reads it shows no colour outside the spec's roles. Left alone, WPF-UI
        // would take the Windows accent. Nothing visible reads it today (every
        // button, check box and text box has a template of its own); this is a
        // backstop. Do not reach for ApplySystemAccent: it resolves the theme
        // through ApplicationThemeManager, which this app never drives, so it
        // silently does nothing.
        var accent = (Color)app.Resources["color.control.iconNeutral.value"];
        ApplicationAccentColorManager.Apply(accent, ApplicationTheme.Light);
    }
}
