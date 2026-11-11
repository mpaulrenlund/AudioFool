using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using AudioFool;
using AudioFool.Core.Art;
using AudioFool.Core.Models;
using AudioFool.Core.Theme;
using AudioFool.Theming;
using AudioFool.ViewModels;

namespace ThemeLab;

/// <summary>
/// --window coveraccent [--theme dark] (session 77): the playback bar in the
/// cover's colour. Made-up covers (the phone's ThemeLab ones: deep red, yellow, a
/// landscape, black and white; plus a wide and a tall one whose shown square
/// alone is green or orange) go into the view model as a played cover would, and each element's
/// colour is read back: the artist's name, Previous, Play/Pause (icon, ring, glow),
/// Next and the seekbar against the fitted colour; Repeat, Shuffle, the title and
/// the volume slider against the theme's own. No colour: everything the theme's.
/// A switch to the other theme and back fits again. Paused and with nothing loaded,
/// Play/Pause has no glow (only while playing; drawn, Dark's arcs around it change
/// and Light's don't), and playing again it's back. Renders &lt;out&gt;-&lt;cover&gt;.png
/// for each, &lt;out&gt;-switched.png and &lt;out&gt;-paused.png.
/// </summary>
internal static partial class Program
{
    private const int CoverPixels = 128;   // MainViewModel.NowPlayingArtWidth

    private static int RunCoverAccent(MainWindow main, MainViewModel vm, string outPath, double w, double h, double scale)
    {
        var stem = System.IO.Path.ChangeExtension(outPath, null);
        var allOk = true;

        void Check(bool ok, string what)
        {
            allOk &= ok;
            Console.WriteLine($"  {(ok ? "OK" : "WRONG")}: {what}");
        }

        vm.NowPlaying = new Track
        {
            FilePath = @"C:\x\a.flac", Title = "One Of These Days", Artist = "Pink Floyd", Album = "Meddle",
            Kind = "FLAC", Bitrate = 1516, BitDepth = 24, SampleRate = 96000,
        };
        vm.DurationSeconds = 300;
        vm.PositionSeconds = 120;
        vm.NowPlayingWaveform = Enumerable.Range(0, 600).Select(i => (float)(0.35 + 0.3 * Math.Sin(i / 9.0) + 0.2 * Math.Sin(i / 2.3))).ToArray();
        vm.IsPlaying = true;   // Dark's glow is lit only while playing; paused is checked at the end
        Pump();

        (string Name, BitmapSource? Cover, uint? Hue)[] covers =
        [
            ("red", CoverDeepRed(), 0x5A0E12),
            ("yellow", CoverYellow(), 0xF2C500),
            ("landscape", CoverLandscape(), 0x4A90D9),
            ("wide", CoverWide(), 0x3FA34D),
            ("tall", CoverTall(), 0xF39C12),
            ("grey", CoverBlackAndWhite(), null),
            ("none", null, null),
        ];

        foreach (var (name, cover, hue) in covers)
        {
            var colour = CoverAccent.MainColorOf(cover);
            Console.WriteLine($"{name}: main colour {(colour is { } m ? $"#{m.R:X2}{m.G:X2}{m.B:X2}" : "none")}");
            if (hue is { } expected)
            {
                var found = colour is { } c ? CoverColor.Hue(Argb(c)) : double.NaN;
                var apart = Math.Abs(found - CoverColor.Hue(0xFF000000 | expected));
                Check(Math.Min(apart, 360 - apart) <= 6, $"hue {found:0.0}, the cover's {CoverColor.Hue(0xFF000000 | expected):0.0}");
            }
            else
            {
                Check(colour is null, "no colour");
            }

            vm.NowPlayingArt = cover;
            vm.NowPlayingCoverColor = colour;
            Pump();
            allOk &= CheckBar(main, colour, Check);

            // The wide and tall covers' colour is the square the bar shows: the middle
            // of the art, as drawn, is that colour (the middle square would be half each).
            if (hue is { } shownHue && name is "wide" or "tall")
            {
                var shown = ArtMiddle(main);
                var apart = Math.Abs(CoverColor.Hue(Argb(shown)) - CoverColor.Hue(0xFF000000 | shownHue));
                Check(Math.Min(apart, 360 - apart) <= 6, $"the art shows {Hex(shown)} in its middle, the colour's hue");
            }
            Save(main, $"{stem}-{name}.png", w, h, scale);
        }

        // A switch while a colour shows fits it to the other theme, and back.
        var red = CoverDeepRed();
        vm.NowPlayingArt = red;
        vm.NowPlayingCoverColor = CoverAccent.MainColorOf(red);
        Pump();
        var start = ThemeService.Current;
        var other = start == AppTheme.Dark ? AppTheme.Light : AppTheme.Dark;
        ThemeService.Switch(other);
        Pump();
        Console.WriteLine($"switched to {other}:");
        allOk &= CheckBar(main, vm.NowPlayingCoverColor, Check);
        Save(main, $"{stem}-switched.png", w, h, scale);
        ThemeService.Switch(start);
        Pump();
        Console.WriteLine($"back to {start}:");
        allOk &= CheckBar(main, vm.NowPlayingCoverColor, Check);

        // The colour going: the theme's own again, after a switch too.
        vm.NowPlayingCoverColor = null;
        Pump();
        Console.WriteLine("colour gone:");
        allOk &= CheckBar(main, null, Check);

        // The glow only while a song plays (the user's call, 2026-11-10): paused,
        // with the cover's colour or the theme's, it goes and the ring stays; playing
        // again, it's back. Drawn, the arcs above and below the button change in Dark
        // and not in Light (its glow is clear).
        var dark = ThemeService.Current == AppTheme.Dark;
        foreach (var withColour in new[] { true, false })
        {
            vm.NowPlayingCoverColor = withColour ? CoverAccent.MainColorOf(red) : null;
            Pump();
            var lit = AroundPlay(main);
            vm.IsPlaying = false;
            Pump();
            Console.WriteLine($"paused, {(withColour ? "the cover's colour" : "the theme's")}:");
            allOk &= CheckBar(main, vm.NowPlayingCoverColor, Check, playing: false);
            var unlit = AroundPlay(main);
            var changed = lit.Zip(unlit).Count(p => p.First != p.Second);
            var (litSum, unlitSum) = (lit.Sum(Brightness), unlit.Sum(Brightness));
            Check(dark ? changed > lit.Length / 2 && litSum > unlitSum : changed == 0,
                $"around the button: {changed} of {lit.Length} pixels changed, brightness {litSum} playing, {unlitSum} paused");
            if (withColour) Save(main, $"{stem}-paused.png", w, h, scale);

            vm.IsPlaying = true;
            Pump();
            Console.WriteLine("playing again:");
            allOk &= CheckBar(main, vm.NowPlayingCoverColor, Check);
            Check(AroundPlay(main).SequenceEqual(lit), "around the button: as before the pause");
        }

        // Nothing loaded: not playing, so no glow.
        vm.NowPlaying = null;
        vm.NowPlayingArt = null;
        vm.IsPlaying = false;
        Pump();
        Console.WriteLine("nothing loaded:");
        allOk &= CheckBar(main, null, Check, playing: false);

        Console.WriteLine(allOk ? "ALL OK" : "SOMETHING WRONG");
        return allOk ? 0 : 1;
    }

    /// <summary>
    /// Every element of the bar against what it should show for <paramref name="cover"/>,
    /// <paramref name="playing"/> or not.
    /// </summary>
    private static bool CheckBar(MainWindow main, Color? cover, Action<bool, string> check, bool playing = true)
    {
        var ok = true;
        void Check(bool good, string what) { ok &= good; check(good, what); }

        var app = Application.Current;
        var dark = ThemeService.Current == AppTheme.Dark;
        var range = (double)app.FindResource("playbackBar.coverAccent.lightnessRange");
        Color Fitted(string fitTo) =>
            FromArgb(CoverColor.Accent(Argb(cover!.Value), Argb((Color)app.FindResource(fitTo + ".value")), dark, range));

        var previous = (Button)main.FindName("PreviousButton");
        var play = (Button)main.FindName("PlayPauseButton");
        var next = (Button)main.FindName("NextButton");
        var artist = (TextBlock)main.FindName("NowPlayingArtist");
        var seek = (Slider)main.FindName("SeekBar");
        var ring = (Ellipse)play.Template.FindName("Ring", play);
        var glow = (Ellipse)play.Template.FindName("Glow", play);

        // A brush against the theme's: the same object without a colour; with one,
        // the fitted colour at the theme brush's alpha.
        void Brush(string what, Brush? actual, string key, string fitTo = "color.accent.playback")
        {
            var theme = (SolidColorBrush)app.FindResource(key);
            if (cover is null)
            {
                Check(ReferenceEquals(actual, theme), $"{what}: the theme's {key}");
                return;
            }
            var expected = Fitted(fitTo);
            expected.A = theme.Color.A;
            var got = (actual as SolidColorBrush)?.Color;
            Check(got == expected, $"{what}: {Hex(got)}, fitted to {fitTo} {Hex(expected)}");
        }

        Brush("Previous", previous.Foreground, "color.control.iconNeutral");
        Brush("Play/Pause", play.Foreground, "color.control.playPause");
        Brush("Next", next.Foreground, "color.control.iconNeutral");
        Brush("artist", artist.Foreground, "color.accent.blue", "color.accent.blue");
        Brush("seekbar", seek.Foreground, "color.slider.seekFill");
        Brush("ring", ring.Stroke, "color.control.playRing");

        Check(ring.Visibility == Visibility.Visible, $"ring {ring.Visibility}, {(playing ? "playing" : "not playing")}");
        Check(PlayGlow.GetLit(play) == playing, $"PlayGlow.Lit {PlayGlow.GetLit(play)}, {(playing ? "playing" : "not playing")}");

        var themeGlow = (DropShadowEffect)app.FindResource("shadow.playGlow");
        if (!playing)
        {
            Check(glow.Effect is null && glow.Visibility == Visibility.Collapsed,
                $"glow: none while not playing ({glow.Effect?.GetType().Name ?? "no effect"}, {glow.Visibility})");
        }
        else if (cover is null)
        {
            Check(ReferenceEquals(glow.Effect, themeGlow), "glow: the theme's shadow.playGlow");
        }
        else
        {
            var fitted = Fitted("color.accent.playback");
            var expected = Color.FromScRgb(1, fitted.R / 255f, fitted.G / 255f, fitted.B / 255f);
            var effect = glow.Effect as DropShadowEffect;
            Check(effect is not null && effect.Color == expected && effect.Opacity == themeGlow.Opacity
                  && effect.BlurRadius == themeGlow.BlurRadius && effect.ShadowDepth == themeGlow.ShadowDepth,
                $"glow: {Hex(effect is null ? null : FromScRgb(effect.Color))} at {effect?.Opacity:0.##}, fitted {Hex(fitted)} at {themeGlow.Opacity:0.##}");
        }
        if (playing)
            Check(glow.Visibility == (dark ? Visibility.Visible : Visibility.Collapsed), $"glow {glow.Visibility} in {ThemeService.Current}");

        // Untouched.
        var title = (TextBlock)main.FindName("NowPlayingTitle");
        var repeat = (Button)main.FindName("RepeatButton");
        var shuffle = (System.Windows.Controls.Primitives.ToggleButton)main.FindName("ShuffleButton");
        var volume = (Slider)main.FindName("VolumeSlider");
        Check(ReferenceEquals(title.Foreground, app.FindResource("color.text.primary")), "title: the theme's text.primary");
        Check(ReferenceEquals(repeat.Foreground, app.FindResource("color.control.iconOff"))
              || ReferenceEquals(repeat.Foreground, app.FindResource("color.control.repeatOn")), "Repeat: the theme's");
        Check(ReferenceEquals(shuffle.Foreground, app.FindResource(shuffle.IsChecked == true ? "color.control.shuffleOn" : "color.control.iconOff")), "Shuffle: the theme's");
        Check(ReferenceEquals(volume.Foreground, app.FindResource("color.slider.volumeFill")), "volume: the theme's slider.volumeFill");

        if (cover is not null)
        {
            // For the record: how well each reads (WCAG contrast).
            Color Bg(string key) => (Color)app.FindResource(key + ".value");
            Console.WriteLine($"    contrast: artist {Contrast(((SolidColorBrush)artist.Foreground).Color, Bg("color.panel.bg")):0.0}:1 on panel.bg, "
                + $"icons {Contrast(((SolidColorBrush)play.Foreground).Color, Bg("color.control.face")):0.0}:1 on control.face, "
                + $"seek fill {Contrast(((SolidColorBrush)seek.Foreground).Color, Bg("color.slider.track")):0.0}:1 against slider.track");
        }
        return ok;
    }

    /// <summary>The pixel in the middle of the now-playing art, as drawn.</summary>
    private static Color ArtMiddle(MainWindow main)
    {
        var text = (FrameworkElement)((FrameworkElement)main.FindName("NowPlayingArtist")).Parent;
        var art = ((Grid)text.Parent).Children.OfType<Border>().First();
        var (width, height) = ((int)art.ActualWidth, (int)art.ActualHeight);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
            dc.DrawRectangle(new VisualBrush(art) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top },
                null, new Rect(0, 0, width, height));
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var pixel = new byte[4];
        bitmap.CopyPixels(new Int32Rect(width / 2, height / 2, 1, 1), pixel, 4, 0);
        return Color.FromRgb(pixel[2], pixel[1], pixel[0]);
    }

    /// <summary>
    /// The window as drawn, at 96 dpi, in the two arcs above and below Play/Pause
    /// from 34 to 44 px out from its middle: past the ring (31), where the glow
    /// shows, and clear of Previous and Next (from 46 to either side). One packed
    /// BGRA pixel each.
    /// </summary>
    private static uint[] AroundPlay(MainWindow main)
    {
        var play = (Button)main.FindName("PlayPauseButton");
        var middle = play.TransformToAncestor(main).Transform(new Point(play.ActualWidth / 2, play.ActualHeight / 2));
        var (width, height) = ((int)main.ActualWidth, (int)main.ActualHeight);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
            dc.DrawRectangle(new VisualBrush(main)
            {
                Viewbox = new Rect(0, 0, width, height), ViewboxUnits = BrushMappingMode.Absolute,
                Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top,
            }, null, new Rect(0, 0, width, height));
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);

        const int Outer = 44, Inner = 34, Side = 20;
        var (left, top) = ((int)Math.Round(middle.X) - Outer, (int)Math.Round(middle.Y) - Outer);
        var square = new uint[(2 * Outer + 1) * (2 * Outer + 1)];
        bitmap.CopyPixels(new Int32Rect(left, top, 2 * Outer + 1, 2 * Outer + 1), square, (2 * Outer + 1) * 4, 0);
        var arcs = new List<uint>();
        for (var y = -Outer; y <= Outer; y++)
            for (var x = -Side; x <= Side; x++)
            {
                var d = Math.Sqrt(x * x + y * y);
                if (d >= Inner && d <= Outer)
                    arcs.Add(square[(y + Outer) * (2 * Outer + 1) + x + Outer]);
            }
        return [.. arcs];
    }

    private static long Brightness(uint bgra) => (bgra & 0xFF) + (bgra >> 8 & 0xFF) + (bgra >> 16 & 0xFF);

    private static string Hex(Color? c) => c is { } v ? $"#{v.A:X2}{v.R:X2}{v.G:X2}{v.B:X2}" : "none";

    private static uint Argb(Color c) => (uint)c.A << 24 | (uint)c.R << 16 | (uint)c.G << 8 | c.B;

    private static Color FromArgb(uint argb) => Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);

    /// <summary>A DropShadowEffect's colour back as the sRGB bytes TokenResources put in as scRGB.</summary>
    private static Color FromScRgb(Color c) =>
        Color.FromRgb((byte)Math.Round(c.ScR * 255), (byte)Math.Round(c.ScG * 255), (byte)Math.Round(c.ScB * 255));

    private static double Contrast(Color a, Color b)
    {
        static double Lum(Color c)
        {
            static double Ch(byte v) { var s = v / 255.0; return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); }
            return 0.2126 * Ch(c.R) + 0.7152 * Ch(c.G) + 0.0722 * Ch(c.B);
        }
        var (la, lb) = (Lum(a), Lum(b));
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    // ---- made-up covers, as the phone's ThemeLab draws them ----

    private static BitmapSource Cover(int width, uint ground, Action<DrawingContext, double> draw, int height = CoverPixels)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Fill(ground), null, new Rect(0, 0, width, height));
            draw(dc, CoverPixels);
        }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private static SolidColorBrush Fill(uint rgb) => new(Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));

    private static void Disc(DrawingContext dc, uint rgb, double x, double y, double r) => dc.DrawEllipse(Fill(rgb), null, new Point(x, y), r, r);

    /// <summary>Deep red, darker than either theme's playback colour: a near-black band and an off-white disc on burgundy.</summary>
    private static BitmapSource CoverDeepRed() => Cover(CoverPixels, 0x5A0E12, (dc, s) =>
    {
        dc.DrawRectangle(Fill(0x1C0506), null, new Rect(0, s * 0.7, s, s * 0.3));
        Disc(dc, 0xF0E0D0, s * 0.5, s * 0.4, s * 0.16);
    });

    /// <summary>Bright yellow, lighter than either theme's playback colour: black bars and a white disc.</summary>
    private static BitmapSource CoverYellow() => Cover(CoverPixels, 0xF2C500, (dc, s) =>
    {
        for (var i = 0; i < 3; i++)
            dc.DrawRectangle(Fill(0x000000), null, new Rect(s * 0.12, s * (0.58 + i * 0.1), s * 0.76, s * 0.04));
        Disc(dc, 0xFFFFFF, s * 0.5, s * 0.3, s * 0.14);
    });

    /// <summary>Several colours: a blue sky over green grass, an orange sun. The sky is most of it.</summary>
    private static BitmapSource CoverLandscape() => Cover(CoverPixels, 0x4A90D9, (dc, s) =>
    {
        dc.DrawRectangle(Fill(0x5B8C3A), null, new Rect(0, s * 0.68, s, s * 0.32));
        Disc(dc, 0xF39C12, s * 0.72, s * 0.26, s * 0.11);
    });

    /// <summary>
    /// Twice as wide as tall: green in the left square, the one the bar shows (its
    /// Image keeps an overflow's top-left), magenta beyond it. The middle square
    /// would be half each.
    /// </summary>
    private static BitmapSource CoverWide() => Cover(CoverPixels * 2, 0xC2187A, (dc, s) =>
        dc.DrawRectangle(Fill(0x3FA34D), null, new Rect(0, 0, s, s)));

    /// <summary>Twice as tall as wide: orange in the top square, the one the bar shows, purple under it.</summary>
    private static BitmapSource CoverTall() => Cover(CoverPixels, 0x6A1B9A, (dc, s) =>
        dc.DrawRectangle(Fill(0xF39C12), null, new Rect(0, 0, s, s)), height: CoverPixels * 2);

    /// <summary>Black and white: no colour to take.</summary>
    private static BitmapSource CoverBlackAndWhite() => Cover(CoverPixels, 0x161616, (dc, s) =>
    {
        Disc(dc, 0x8A8A8A, s * 0.5, s * 0.5, s * 0.34);
        Disc(dc, 0xF2F2F2, s * 0.5, s * 0.5, s * 0.2);
    });
}
