using System.Windows;
using System.Windows.Media;
using AudioFool.Core.Theme;
using AudioFool.Theming;
using Wpf.Ui.Appearance;
using Wpf.Ui.Markup;

namespace AudioFool;

/// <summary>
/// Puts a theme in place at startup and switches it while the app runs (PS2 Dark
/// spec 3). A theme is one token set (<see cref="TokenResources"/>). Both sets use
/// the same resource names, so a switch puts the other set where the first one
/// was, and everything that looks a colour up by name - <c>DynamicResource</c>, or
/// <c>{theme:LiveColor}</c> where that can't reach - follows at once.
/// </summary>
public static class ThemeService
{
    /// <summary>The theme on screen.</summary>
    public static AppTheme Current { get; private set; }

    /// <summary>After each switch, once the new theme is in place.</summary>
    public static event EventHandler? Switched;

    // The token set in Application.Resources, to be replaced at a switch.
    private static ResourceDictionary? _tokens;

    // WPF-UI's base as App.xaml merges it: Light.
    private static ApplicationTheme _base = ApplicationTheme.Light;

    /// <summary>
    /// Merges the theme over WPF-UI's base (App.xaml). Call it before building any
    /// window: a DynamicResource for a Style is resolved while the element
    /// initialises, so a dictionary merged afterwards is too late for it.
    /// </summary>
    public static void Apply(AppTheme theme)
    {
        var app = Application.Current;
        Current = theme;
        SetBase(theme);

        // The central theme, from design/theme-tokens.json or its dark twin. Its
        // resource names are the JSON paths (color.panel.bg, albums.rowHeight),
        // which no WPF-UI key shares. First, so {theme:Token} can read it from
        // anything loaded after.
        _tokens = TokenResources.Build(TokenResources.LoadEmbedded(theme));
        app.Resources.MergedDictionaries.Add(_tokens);

        // The components built from the tokens. Its keys all start with "theme.",
        // apart from the implicit ToolTip, ContextMenu and MenuItem styles. The
        // same for both themes: it names its colours, it doesn't hold them.
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/AudioFool;component/Theming/Chrome.xaml"),
        });

        // WPF-UI's menu templates fix their corners; this sets the theme's.
        MenuCorners.Register();

        SetAccent(theme);
        WindowFrames.Follow(theme);
    }

    /// <summary>
    /// Switches every open window to the other theme. Only resources change: no
    /// window, template or list is rebuilt, so playback, the queue, selections,
    /// scroll positions and open dialogs stay as they are (spec 3.2). Windows
    /// opened later are built in the new theme, as at startup.
    /// </summary>
    public static void Switch(AppTheme theme)
    {
        if (_tokens is null)
            throw new InvalidOperationException("ThemeService.Apply runs first, at startup.");
        if (theme == Current)
            return;

        Current = theme;
        SetBase(theme);

        var merged = Application.Current.Resources.MergedDictionaries;
        var tokens = TokenResources.Build(TokenResources.LoadEmbedded(theme));
        merged[merged.IndexOf(_tokens)] = tokens;
        _tokens = tokens;

        // The brushes inside styles' own resources, which can't name a resource.
        LiveColors.Instance.Refresh();

        SetAccent(theme);
        WindowFrames.Follow(theme);
        Switched?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>
    /// WPF-UI's Light or Dark dictionary, for any stock key the tokens don't
    /// restate, so a fallback is a light value in Light and a dark one in Dark. It
    /// takes the place of the one App.xaml merged, so the order stays the same.
    /// The app never drives WPF-UI's ApplicationThemeManager; this is its swap,
    /// done here.
    /// </summary>
    private static void SetBase(AppTheme theme)
    {
        var wanted = theme == AppTheme.Dark ? ApplicationTheme.Dark : ApplicationTheme.Light;
        if (wanted == _base)
            return;

        var merged = Application.Current.Resources.MergedDictionaries;
        var index = merged.IndexOf(merged.OfType<ThemesDictionary>().First());
        merged[index] = new ThemesDictionary { Theme = wanted };
        _base = wanted;
    }

    /// <summary>
    /// WPF-UI's accent: the theme's control grey, so a stock control that still
    /// reads it shows no colour outside the spec's roles. Left alone, WPF-UI would
    /// take the Windows accent. Nothing visible reads it today (every button,
    /// check box and text box has a template of its own); this is a backstop.
    /// Do not reach for ApplySystemAccent: it resolves the theme through
    /// ApplicationThemeManager, which this app never drives, so it silently does
    /// nothing. The values go straight into Application.Resources, so a switch
    /// sets them again.
    /// </summary>
    private static void SetAccent(AppTheme theme)
    {
        var accent = (Color)Application.Current.Resources["color.control.iconNeutral.value"];
        ApplicationAccentColorManager.Apply(accent, theme == AppTheme.Dark ? ApplicationTheme.Dark : ApplicationTheme.Light);
    }
}
