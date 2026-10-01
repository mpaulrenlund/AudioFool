using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Effects;
using AudioFool.Core.Theme;

namespace AudioFool.Theming;

/// <summary>
/// The central theme: <c>design/theme-tokens.json</c>, embedded in the exe and
/// turned into one <see cref="ResourceDictionary"/> at startup. The JSON is the only
/// copy of every value, so the file and the app cannot drift; change a value there
/// and rebuild.
/// <para>
/// Every resource is named by its JSON path. What each kind of token becomes:
/// </para>
/// <list type="table">
/// <item><term><c>color.*</c></term><description>a frozen <see cref="SolidColorBrush"/>, plus the raw
/// <see cref="Color"/> under <c>&lt;path&gt;.value</c>.</description></item>
/// <item><term><c>radius.*</c></term><description>a <see cref="CornerRadius"/>, plus the number under
/// <c>&lt;path&gt;.value</c>.</description></item>
/// <item><term>other numbers</term><description>a <see cref="double"/>. In XAML, <c>{theme:Token}</c> turns one into
/// a Thickness, CornerRadius, GridLength or DataGridLength to fit the property - except in a
/// Style setter, where WPF does not yet know the property; use <c>{theme:Thickness}</c> there
/// for margins and paddings.</description></item>
/// <item><term><c>type.*</c></term><description>a <see cref="TextBlock"/> style under <c>type.&lt;name&gt;</c>:
/// font, size, weight, colour, line height, tabular figures, and a line clamp for <c>maxLines</c>.
/// Letter spacing and uppercase are left to the component; they stay available as
/// <c>type.&lt;name&gt;.letterSpacingEm</c> / <c>.uppercase</c>.</description></item>
/// <item><term><c>font.family</c></term><description>a <see cref="FontFamily"/> with the fallbacks in order.</description></item>
/// <item><term><c>shadow.*</c></term><description>the outer layer as a frozen <see cref="DropShadowEffect"/>
/// under the path. WPF has no inset shadow, so each inset layer is published as
/// <c>&lt;path&gt;.inset1</c> (a brush) with <c>.inset1.x</c>, <c>.y</c> and <c>.blur</c>, for a
/// template to draw as an overlay; <c>.inset1.fade</c> is the same colour fading to clear top to
/// bottom, to draw <c>.inset1.depth</c> (offset plus blur) pixels tall along a top edge.</description></item>
/// <item><term>other strings, flags</term><description>a <see cref="string"/> or <see cref="bool"/>.</description></item>
/// <item><term><c>focus.ring.*</c></term><description>built from <c>focus.*</c> for the keyboard focus rings:
/// <c>focus.ring.outset</c> (a negative <see cref="Thickness"/>, the gap plus the line),
/// <c>focus.ring.radius.&lt;name&gt;</c> (each radius grown by the same amount), and
/// <c>focus.sliderRing.outset</c> / <c>.radius</c> for the sliders' wider ring.</description></item>
/// </list>
/// </summary>
public static class TokenResources
{
    private const string EmbeddedName = "AudioFool.theme-tokens.json";

    /// <summary>The tokens the app is running with, once <see cref="ThemeService.Apply"/> has loaded them.</summary>
    public static ThemeTokens? Current { get; private set; }

    public static ThemeTokens LoadEmbedded()
    {
        using var stream = typeof(TokenResources).Assembly.GetManifestResourceStream(EmbeddedName)
            ?? throw new InvalidOperationException($"{EmbeddedName} is not embedded in the assembly.");
        using var reader = new StreamReader(stream);
        return Current = ThemeTokens.Parse(reader.ReadToEnd());
    }

    public static ResourceDictionary Build(ThemeTokens tokens)
    {
        var d = new ResourceDictionary();

        void Add(string key, object value)
        {
            if (d.Contains(key))
                throw new ThemeTokenException(key, "two tokens map to the same resource name");
            d.Add(key, value);
        }

        foreach (var (key, c) in tokens.Colors)
        {
            var color = Color.FromArgb(c.A, c.R, c.G, c.B);
            Add(key, Frozen(new SolidColorBrush(color)));
            Add(key + ".value", color);
        }

        foreach (var (key, n) in tokens.Numbers)
        {
            // Radii are published ready-made. Inside a Style, a Setter's Property
            // is only resolved after its Value, so {theme:Token} cannot see that a
            // setter wants a CornerRadius and would hand over a bare number.
            if (key.StartsWith("radius.", StringComparison.Ordinal))
            {
                Add(key, new CornerRadius(n));
                Add(key + ".value", n);
            }
            else
            {
                Add(key, n);
            }

            // A weight is also published as a FontWeight, for the same reason:
            // a FontWeight setter in a style cannot be handed a number.
            if (key.EndsWith("weight", StringComparison.OrdinalIgnoreCase))
                Add(key + ".fontWeight", FontWeight.FromOpenTypeWeight((int)n));
        }
        // Keyboard focus rings (spec 8), drawn by the focus visual styles in
        // Chrome.xaml as an outline pulled out past the control's edge. The
        // outline's outer corner is the control's radius plus the gap plus the
        // line, as a CSS outline-offset would draw it.
        double Focus(string name) => tokens.Numbers.TryGetValue($"focus.{name}", out var v)
            ? v : throw new ThemeTokenException($"focus.{name}", "missing; the focus rings are built from it");
        var ringWidth = Focus("ringWidth");
        var ringOutset = Focus("ringOffset") + ringWidth;
        var sliderOutset = Focus("sliderRingOffset") + ringWidth;
        Add("focus.ring.outset", new Thickness(-ringOutset));
        foreach (var (key, n) in tokens.Numbers.Where(kv => kv.Key.StartsWith("radius.", StringComparison.Ordinal)))
            Add($"focus.ring.{key}", new CornerRadius(n + ringOutset));
        Add("focus.sliderRing.outset", new Thickness(-sliderOutset));
        Add("focus.sliderRing.radius", new CornerRadius(Focus("sliderStripRadius") + sliderOutset));

        foreach (var (key, f) in tokens.Flags)
            Add(key, f);
        foreach (var (key, s) in tokens.Texts)
            Add(key, s);

        var family = new FontFamily(string.Join(", ", tokens.FontFamily));
        Add("font.family", family);

        foreach (var style in tokens.TextStyles)
            Add(style.Key, TextStyle(style, family, (Brush)d[style.ColorKey]));

        foreach (var (key, layers) in tokens.Shadows)
        {
            var outer = layers.Where(l => !l.Inset).ToList();
            if (outer.Count > 1)
                throw new ThemeTokenException(key, "WPF draws one drop shadow per element; give this token at most one outer layer");
            if (layers.Any(l => l.Spread != 0))
                throw new ThemeTokenException(key, "WPF shadows have no spread");
            if (outer.Count == 1)
                Add(key, Shadow(outer[0]));

            var n = 0;
            foreach (var inset in layers.Where(l => l.Inset))
            {
                var name = $"{key}.inset{++n}";
                var color = Color.FromArgb(inset.Color.A, inset.Color.R, inset.Color.G, inset.Color.B);
                Add(name, Frozen(new SolidColorBrush(color)));
                Add(name + ".x", inset.OffsetX);
                Add(name + ".y", inset.OffsetY);
                Add(name + ".blur", inset.Blur);

                // The usual stand-in for a blurred inset along the top edge: the
                // shadow colour fading to clear, top to bottom, over .depth pixels
                // (the offset plus the blur, where the CSS shadow ends).
                Add(name + ".fade", Frozen(new LinearGradientBrush(color, Color.FromArgb(0, color.R, color.G, color.B), 90)));
                Add(name + ".depth", inset.OffsetY + inset.Blur);
            }
        }

        return d;
    }

    private static Style TextStyle(TextStyleToken t, FontFamily family, Brush foreground)
    {
        var style = new Style(typeof(TextBlock));
        style.Setters.Add(new Setter(TextBlock.FontFamilyProperty, family));
        style.Setters.Add(new Setter(TextBlock.FontSizeProperty, t.Size));
        style.Setters.Add(new Setter(TextBlock.FontWeightProperty, FontWeight.FromOpenTypeWeight(t.Weight)));
        style.Setters.Add(new Setter(TextBlock.ForegroundProperty, foreground));

        if (t.LineHeightPixels is { } lineHeight)
        {
            // BlockLineHeight makes every line exactly this tall, as CSS line-height
            // does, which is also what lets the clamp below be an exact height.
            style.Setters.Add(new Setter(TextBlock.LineHeightProperty, lineHeight));
            style.Setters.Add(new Setter(TextBlock.LineStackingStrategyProperty, LineStackingStrategy.BlockLineHeight));
        }

        if (t.MaxLines is { } lines)
        {
            // WPF has no line clamp. Wrapping inside a height of N lines, with
            // trimming on, ends the last visible line with "…".
            //
            // Layout rounding is off on the block. With it on, as it is in the
            // windows, WPF's trimming went wrong whenever the line height rounds
            // up: a 17.55 px album title clamped to two lines showed one line and
            // "…" over an empty second line, at any clamp from 2 to 3 lines tall
            // (measured in the Albums list; ThemeLab --clampprobe). The 34.5 px
            // header title, which rounds down, was unaffected.
            var lineHeightForClamp = t.LineHeightPixels ?? t.Size * family.LineSpacing;
            style.Setters.Add(new Setter(TextBlock.TextWrappingProperty, TextWrapping.Wrap));
            style.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
            style.Setters.Add(new Setter(FrameworkElement.MaxHeightProperty, lines * lineHeightForClamp));
            style.Setters.Add(new Setter(FrameworkElement.UseLayoutRoundingProperty, false));
        }

        if (t.TabularFigures)
            style.Setters.Add(new Setter(Typography.NumeralAlignmentProperty, FontNumeralAlignment.Tabular));

        return style;
    }

    /// <summary>
    /// CSS offsets are x right and y down; WPF's Direction is degrees anticlockwise
    /// from the right, so straight down is 270. CSS blur and WPF BlurRadius are both
    /// in pixels but not the same curve - compare against the screenshots by eye.
    /// </summary>
    private static DropShadowEffect Shadow(ShadowLayer s)
    {
        var effect = new DropShadowEffect
        {
            Color = Color.FromRgb(s.Color.R, s.Color.G, s.Color.B),
            Opacity = s.Color.Alpha,
            BlurRadius = s.Blur,
            ShadowDepth = Math.Sqrt(s.OffsetX * s.OffsetX + s.OffsetY * s.OffsetY),
            Direction = (Math.Atan2(-s.OffsetY, s.OffsetX) * 180 / Math.PI + 360) % 360,
        };
        effect.Freeze();
        return effect;
    }

    private static T Frozen<T>(T f) where T : Freezable
    {
        f.Freeze();
        return f;
    }
}
