using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using AudioFool.Core.Art;
using AudioFool.Core.Theme;

namespace AudioFool.Theming;

/// <summary>
/// The cover's colour in the playback bar (session 77, the user's request: as the
/// phone's Now Playing does; spec 6.7, <i>Cover colour</i>), and on the album
/// header's artist name, from the header's own cover (spec 6.5). On an element,
/// <c>theme:CoverAccent.Color</c> is the cover's main colour (null for a
/// cover with no colour, or none), and <c>theme:CoverAccent.Keys</c> names the
/// theme keys that colour stands in for there, space-separated:
/// <code>
/// &lt;Button theme:CoverAccent.Color="{Binding NowPlayingCoverColor}"
///         theme:CoverAccent.Keys="color.control.playPause color.control.playRing shadow.playGlow" /&gt;
/// </code>
/// Each key then gets the colour, fitted to the theme (<see cref="CoverColor.Accent"/>),
/// in the element's own resources, so every <c>DynamicResource</c> on the element,
/// its style's and its template's, finds it before the theme's. A brush keeps the
/// theme brush's alpha and a glow its own opacity, so what the theme leaves clear
/// (Light's ring and glow) stays clear. With no colour the keys are taken out again
/// and the theme's own show. A theme switch fits the colour again.
/// <para>
/// The colour is fitted to <c>theme:CoverAccent.FitTo</c>, <c>color.accent.playback</c>
/// unless set: on Dark no darker than it and up to
/// <c>playbackBar.coverAccent.lightnessRange</c> lighter, on Light the other way.
/// The artist's name is text and is fitted to <c>color.accent.blue</c>.
/// </para>
/// </summary>
public static class CoverAccent
{
    public static readonly DependencyProperty ColorProperty = DependencyProperty.RegisterAttached(
        "Color", typeof(Color?), typeof(CoverAccent), new PropertyMetadata(null, OnChanged));

    public static readonly DependencyProperty KeysProperty = DependencyProperty.RegisterAttached(
        "Keys", typeof(string), typeof(CoverAccent), new PropertyMetadata("", OnChanged));

    public static readonly DependencyProperty FitToProperty = DependencyProperty.RegisterAttached(
        "FitTo", typeof(string), typeof(CoverAccent), new PropertyMetadata("color.accent.playback", OnChanged));

    public static Color? GetColor(DependencyObject d) => (Color?)d.GetValue(ColorProperty);
    public static void SetColor(DependencyObject d, Color? value) => d.SetValue(ColorProperty, value);
    public static string GetKeys(DependencyObject d) => (string)d.GetValue(KeysProperty);
    public static void SetKeys(DependencyObject d, string value) => d.SetValue(KeysProperty, value);
    public static string GetFitTo(DependencyObject d) => (string)d.GetValue(FitToProperty);
    public static void SetFitTo(DependencyObject d, string value) => d.SetValue(FitToProperty, value);

    // The elements given a colour, to fit again at a theme switch. The main
    // window's few, so a plain list; weak, so a closed window isn't held.
    private static readonly List<WeakReference<FrameworkElement>> Elements = [];

    static CoverAccent() => ThemeService.Switched += (_, _) => FitAll();

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element) return;
        if (!Elements.Any(w => w.TryGetTarget(out var known) && ReferenceEquals(known, element)))
            Elements.Add(new WeakReference<FrameworkElement>(element));
        Fit(element);
    }

    private static void FitAll()
    {
        Elements.RemoveAll(w => !w.TryGetTarget(out _));
        foreach (var weak in Elements)
            if (weak.TryGetTarget(out var element))
                Fit(element);
    }

    private static void Fit(FrameworkElement element)
    {
        var main = GetColor(element);
        var keys = GetKeys(element).Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (main is not { } colour)
        {
            foreach (var key in keys)
                element.Resources.Remove(key);
            return;
        }

        var app = Application.Current;
        var range = app.TryFindResource("playbackBar.coverAccent.lightnessRange") is double r
            ? r
            : throw new InvalidOperationException("playbackBar.coverAccent.lightnessRange is missing from the token set.");
        var fitTo = app.TryFindResource(GetFitTo(element) + ".value") is Color anchor
            ? anchor
            : throw new InvalidOperationException($"\"{GetFitTo(element)}\" is not a colour token.");
        var fitted = CoverColor.Accent(Argb(colour), Argb(fitTo), ThemeService.Current == AppTheme.Dark, range);
        var (red, green, blue) = ((byte)(fitted >> 16), (byte)(fitted >> 8), (byte)fitted);

        foreach (var key in keys)
        {
            // The theme's own, from the application: the element's would be ours.
            element.Resources[key] = app.TryFindResource(key) switch
            {
                SolidColorBrush brush => Frozen(new SolidColorBrush(Color.FromArgb(brush.Color.A, red, green, blue))
                {
                    Opacity = brush.Opacity,
                }),
                DropShadowEffect glow => Glow(glow, red, green, blue),
                var other => throw new InvalidOperationException(
                    $"CoverAccent can stand in for a colour or a glow, and \"{key}\" is {other?.GetType().Name ?? "missing"}."),
            };
        }
    }

    /// <summary>
    /// The theme's glow in the cover's colour, at its own opacity, blur and offset.
    /// The colour goes in as scRGB as <see cref="TokenResources"/> puts a shadow's,
    /// so it draws as the colour itself and not a deeper one.
    /// </summary>
    private static DropShadowEffect Glow(DropShadowEffect theme, byte red, byte green, byte blue)
    {
        var glow = theme.Clone();
        glow.Color = Color.FromScRgb(1, red / 255f, green / 255f, blue / 255f);
        return Frozen(glow);
    }

    /// <summary>
    /// The cover's main colour (<see cref="CoverColor.Main"/>) from the square the
    /// bar, or the album header, shows, scaled to <see cref="CoverColor.SampleSide"/>
    /// a side; null for no cover or one with no colour. Off the UI thread is fine:
    /// the art service's pictures are frozen.
    /// <para>
    /// The square is the cover's top-left one, not its middle (the phone's): the
    /// bar's and the header's <c>Image</c> fill their square (<c>UniformToFill</c>) and WPF keeps the
    /// overflow's top-left, so a wide cover shows its left end (ThemeLab
    /// <c>--window coveraccent</c>, session 77). Square covers, nearly all of them,
    /// are the same either way.
    /// </para>
    /// </summary>
    public static Color? MainColorOf(BitmapSource? cover)
    {
        if (cover is null || cover.PixelWidth == 0 || cover.PixelHeight == 0) return null;

        var side = Math.Min(cover.PixelWidth, cover.PixelHeight);
        BitmapSource square = new CroppedBitmap(cover, new Int32Rect(0, 0, side, side));
        if (side != CoverColor.SampleSide)
        {
            var scale = (double)CoverColor.SampleSide / side;
            square = new TransformedBitmap(square, new ScaleTransform(scale, scale));
        }
        var pixels = new FormatConvertedBitmap(square, PixelFormats.Bgra32, null, 0);

        var buffer = new uint[pixels.PixelWidth * pixels.PixelHeight];
        pixels.CopyPixels(buffer, pixels.PixelWidth * 4, 0);

        return CoverColor.Main(buffer) is { } main
            ? Color.FromRgb((byte)(main >> 16), (byte)(main >> 8), (byte)main)
            : null;
    }

    private static uint Argb(Color c) => (uint)c.A << 24 | (uint)c.R << 16 | (uint)c.G << 8 | c.B;

    private static T Frozen<T>(T f) where T : Freezable
    {
        f.Freeze();
        return f;
    }
}
