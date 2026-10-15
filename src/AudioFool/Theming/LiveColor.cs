using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using System.Windows.Markup;
using System.Windows.Media;

namespace AudioFool.Theming;

/// <summary>
/// <c>{theme:LiveColor color.panel.bg}</c>: a token colour that follows a theme
/// switch, for the one place <c>DynamicResource</c> can't reach - a brush inside a
/// style's own resources, such as the WPF-UI keys Chrome.xaml restates for menus
/// and title bars. WPF seals a style's resources once the style is used, so the
/// brush can't be replaced at a switch, and a brush there has no element to look
/// a resource up from. So its colour is bound instead, to <see cref="LiveColors"/>,
/// which <see cref="ThemeService.Switch"/> refreshes.
/// <para>
/// Takes the colour token's name, without <c>.value</c>. Everywhere else, use
/// <c>{DynamicResource color.…}</c>.
/// </para>
/// </summary>
[MarkupExtensionReturnType(typeof(object))]
public sealed class LiveColorExtension : MarkupExtension
{
    public LiveColorExtension() { }

    public LiveColorExtension(string key) => Key = key;

    [ConstructorArgument("key")]
    public string Key { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        // Fails here, naming the token, rather than as a blank colour later.
        _ = LiveColors.Instance[Key];

        // A binding (not a value), so the brush holding it can't be frozen when
        // its style is sealed, and stays free to change.
        return new Binding($"[{Key}]") { Source = LiveColors.Instance, Mode = BindingMode.OneWay }
            .ProvideValue(serviceProvider);
    }
}

/// <summary>
/// The colour tokens of the theme on screen, by name, for
/// <see cref="LiveColorExtension"/>'s bindings. A switch raises one change for the
/// indexer, and every bound brush reads its colour again.
/// </summary>
public sealed class LiveColors : INotifyPropertyChanged
{
    public static LiveColors Instance { get; } = new();

    private LiveColors() { }

    public Color this[string key] =>
        Application.Current?.TryFindResource(key + ".value") is Color color
            ? color
            : throw new InvalidOperationException(
                $"\"{key}\" is not a colour token. Check the name against design/theme-tokens.json, "
                + "and that ThemeService.Apply() ran before this XAML was loaded.");

    public event PropertyChangedEventHandler? PropertyChanged;

    internal void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(Binding.IndexerName));
}
