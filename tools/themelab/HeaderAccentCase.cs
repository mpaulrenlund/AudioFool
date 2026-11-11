using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AudioFool;
using AudioFool.Core.Art;
using AudioFool.Core.Models;
using AudioFool.Core.Theme;
using AudioFool.Services;
using AudioFool.Theming;
using AudioFool.ViewModels;

namespace ThemeLab;

/// <summary>
/// --window headeraccent [--theme dark] (session 77): the album header's artist name in
/// the header's own cover's colour, as the playback bar's artist takes the playing
/// cover's. On the sample library's Second Sight, each made-up cover of
/// <c>--window coveraccent</c> goes in as the header's art would (the picture and its
/// colour together), and the name is read back against the fitted colour; the title,
/// year, track count and duration stay the theme's, and so does the bar. The same
/// cover in the header and the bar gives the same colour, two covers each their own.
/// A switch to the other theme and back fits again. Renders &lt;out&gt;-&lt;cover&gt;.png.
/// <para>
/// --realalbums N: then the real library (read only), N artists spread through it:
/// each one's first album selected as a click would, its header colour from the
/// app's own path, against the colour the bar would take for its first song. Both
/// read the cover at their own size, so this shows whether they agree in practice.
/// --realalbum "Artist/Album": also render that album's header, &lt;out&gt;-real.png.
/// </para>
/// </summary>
internal static partial class Program
{
    private static int RunHeaderAccent(MainWindow main, MainViewModel vm, string outPath, double w, double h, double scale)
    {
        var stem = System.IO.Path.ChangeExtension(outPath, null);
        var allOk = true;

        void Check(bool ok, string what)
        {
            allOk &= ok;
            Console.WriteLine($"  {(ok ? "OK" : "WRONG")}: {what}");
        }

        Pump();
        Console.WriteLine($"header: '{vm.AlbumHeaderTitle}' by '{vm.AlbumHeaderArtist}', shown {vm.ShowsAlbumHeader}");
        Check(vm.ShowsAlbumHeader && vm.AlbumHeaderArtist.Length > 0, "the album header shows, with an artist");

        (string Name, BitmapSource? Cover)[] covers =
        [
            ("red", CoverDeepRed()),
            ("yellow", CoverYellow()),
            ("landscape", CoverLandscape()),
            ("wide", CoverWide()),
            ("tall", CoverTall()),
            ("grey", CoverBlackAndWhite()),
            ("none", null),
        ];

        foreach (var (name, cover) in covers)
        {
            var colour = CoverAccent.MainColorOf(cover);
            Console.WriteLine($"{name}: main colour {Hex(colour)}");
            vm.SelectedAlbumArt = cover;
            vm.AlbumHeaderCoverColor = colour;
            Pump();
            allOk &= CheckHeader(main, colour, null, Check);
            Save(main, $"{stem}-{name}.png", w, h, scale);
        }

        // The same cover in the header and the bar: the same colour on both names.
        var red = CoverAccent.MainColorOf(CoverDeepRed());
        var yellow = CoverAccent.MainColorOf(CoverYellow());
        vm.AlbumHeaderCoverColor = red;
        vm.NowPlayingCoverColor = red;
        Pump();
        Console.WriteLine("the same cover in the header and the bar:");
        allOk &= CheckHeader(main, red, red, Check);
        var headerBrush = (SolidColorBrush)((TextBlock)main.FindName("HeaderArtist")).Foreground;
        var barBrush = (SolidColorBrush)((TextBlock)main.FindName("NowPlayingArtist")).Foreground;
        Check(headerBrush.Color == barBrush.Color, $"header {Hex(headerBrush.Color)}, bar {Hex(barBrush.Color)}: the same");

        // Two covers: each name its own.
        vm.AlbumHeaderCoverColor = yellow;
        Pump();
        Console.WriteLine("yellow in the header, red in the bar:");
        allOk &= CheckHeader(main, yellow, red, Check);

        // A switch fits again, and back.
        var start = ThemeService.Current;
        var other = start == AppTheme.Dark ? AppTheme.Light : AppTheme.Dark;
        ThemeService.Switch(other);
        Pump();
        Console.WriteLine($"switched to {other}:");
        allOk &= CheckHeader(main, yellow, red, Check);
        ThemeService.Switch(start);
        Pump();
        Console.WriteLine($"back to {start}:");
        allOk &= CheckHeader(main, yellow, red, Check);

        // The colours going: the theme's again.
        vm.AlbumHeaderCoverColor = null;
        vm.NowPlayingCoverColor = null;
        Pump();
        Console.WriteLine("colours gone:");
        allOk &= CheckHeader(main, null, null, Check);

        if (Arg(Environment.GetCommandLineArgs(), "--realalbums") is { } realCount)
            allOk &= RealHeaders(main, vm, int.Parse(realCount, System.Globalization.CultureInfo.InvariantCulture), stem, w, h, scale, Check);

        Console.WriteLine(allOk ? "ALL OK" : "SOMETHING WRONG");
        return allOk ? 0 : 1;
    }

    /// <summary>
    /// The header's artist name against <paramref name="header"/>'s fitted colour (the
    /// theme's own brush without one), the header's other lines against the theme's,
    /// and the bar's artist against <paramref name="bar"/>'s.
    /// </summary>
    private static bool CheckHeader(MainWindow main, Color? header, Color? bar, Action<bool, string> check)
    {
        var ok = true;
        void Check(bool good, string what) { ok &= good; check(good, what); }

        var app = Application.Current;
        var dark = ThemeService.Current == AppTheme.Dark;
        var range = (double)app.FindResource("playbackBar.coverAccent.lightnessRange");
        var blue = (SolidColorBrush)app.FindResource("color.accent.blue");

        void Name(string what, TextBlock text, Color? cover)
        {
            if (cover is not { } c)
            {
                Check(ReferenceEquals(text.Foreground, blue), $"{what}: the theme's color.accent.blue");
                return;
            }
            var expected = FromArgb(CoverColor.Accent(Argb(c), Argb((Color)app.FindResource("color.accent.blue.value")), dark, range));
            expected.A = blue.Color.A;
            var got = (text.Foreground as SolidColorBrush)?.Color;
            Check(got == expected, $"{what}: {Hex(got)}, fitted to color.accent.blue {Hex(expected)}");
        }

        var artist = (TextBlock)main.FindName("HeaderArtist");
        Name("header artist", artist, header);
        Name("bar artist", (TextBlock)main.FindName("NowPlayingArtist"), bar);

        // Untouched: the title, and the year, track count and duration under the name.
        var details = (Panel)artist.Parent;
        var title = ((Panel)details.Parent).Children.OfType<TextBlock>().First();
        Check(ReferenceEquals(title.Foreground, app.FindResource("color.text.heading")), "title: the theme's text.heading");
        var others = details.Children.OfType<TextBlock>().Where(t => !ReferenceEquals(t, artist)).ToList();
        Check(others.Count == 3 && others.All(t => ReferenceEquals(t.Foreground, app.FindResource("color.text.secondary"))),
            $"year, count, duration ({others.Count}): the theme's text.secondary");

        if (header is not null)
            Console.WriteLine($"    contrast: header artist {Contrast(((SolidColorBrush)artist.Foreground).Color, (Color)app.FindResource("color.panel.bg.value")):0.0}:1 on panel.bg");
        return ok;
    }

    /// <summary>
    /// --realalbums: real albums, each selected as a click would. The header's colour
    /// comes from the app's own path (LoadAlbumHeaderArtAsync, the header's 320 px
    /// cover); the bar's is worked out as LoadNowPlayingArtAsync does it for the
    /// album's first song (its 128 px cover). Fitted for this theme, they should
    /// mostly be the same; any that aren't are listed.
    /// </summary>
    private static bool RealHeaders(MainWindow main, MainViewModel vm, int count, string stem, double w, double h, double scale, Action<bool, string> check)
    {
        LoadRealLibrary(vm);
        Pump();

        var load = typeof(MainViewModel).GetMethod("LoadAlbumHeaderArtAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var art = (AlbumArtService)typeof(MainViewModel).GetField("_artService", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(vm)!;
        var app = Application.Current;
        var dark = ThemeService.Current == AppTheme.Dark;
        var range = (double)app.FindResource("playbackBar.coverAccent.lightnessRange");
        var anchor = Argb((Color)app.FindResource("color.accent.blue.value"));
        uint? Fit(Color? c) => c is { } v ? CoverColor.Accent(Argb(v), anchor, dark, range) : null;

        T Wait<T>(Task<T> task)
        {
            while (!task.IsCompleted) { Pump(); Thread.Sleep(5); }
            return task.GetAwaiter().GetResult();
        }

        void Select(ArtistGroup artist, AlbumItemViewModel? album)
        {
            vm.SelectedArtist = artist;
            Pump();
            vm.SelectedAlbum = album ?? vm.Albums.FirstOrDefault();
            if (vm.HeaderAlbum is { } shown)
            {
                var task = (Task)load.Invoke(vm, [shown])!;
                while (!task.IsCompleted) { Pump(); Thread.Sleep(5); }
                task.GetAwaiter().GetResult();
            }
            Pump();
        }

        var step = Math.Max(1, vm.Artists.Count / count);
        var (same, coloured, grey, missing) = (0, 0, 0, 0);
        var apart = new List<string>();
        double worst = 0;
        foreach (var artist in vm.Artists.Where((_, i) => i % step == 0).Take(count).ToList())
        {
            Select(artist, null);
            if (vm.HeaderAlbum is not { } shown || shown.Album.Tracks.Count == 0) continue;

            var first = shown.Album.Tracks[0];
            var barArt = Wait(art.GetTrackArtAsync(first, 128));
            var barColour = barArt is null ? null : CoverAccent.MainColorOf(barArt);
            var (headerFit, barFit) = (Fit(vm.AlbumHeaderCoverColor), Fit(barColour));

            if (vm.SelectedAlbumArt is null) missing++;
            else if (vm.AlbumHeaderCoverColor is null) grey++;
            else coloured++;

            // The name on screen is the app's brush: it must be the header colour, fitted.
            var shownBrush = ((SolidColorBrush)((TextBlock)main.FindName("HeaderArtist")).Foreground).Color;
            if (headerFit is { } hf)
                check((Argb(shownBrush) & 0xFFFFFF) == (hf & 0xFFFFFF), $"{artist.Name} / {shown.Album.Title}: shown {Hex(shownBrush)}");

            if (headerFit == barFit)
            {
                same++;
                continue;
            }
            var d = headerFit is { } a && barFit is { } b
                ? Math.Max(Math.Abs((int)(a >> 16 & 0xFF) - (int)(b >> 16 & 0xFF)),
                    Math.Max(Math.Abs((int)(a >> 8 & 0xFF) - (int)(b >> 8 & 0xFF)), Math.Abs((int)(a & 0xFF) - (int)(b & 0xFF))))
                : 255;
            worst = Math.Max(worst, d);
            apart.Add($"{artist.Name} / {shown.Album.Title}: header {(headerFit is { } x ? $"#{x & 0xFFFFFF:X6}" : "theme")}, bar {(barFit is { } y ? $"#{y & 0xFFFFFF:X6}" : "theme")} (up to {d} a channel)");
        }

        Console.WriteLine($"real albums: {coloured} coloured, {grey} without colour, {missing} without a cover; header and bar the same on {same} of {coloured + grey + missing}");
        foreach (var line in apart) Console.WriteLine($"    apart: {line}");
        Console.WriteLine($"    largest difference: {worst} a channel");

        if (Arg(Environment.GetCommandLineArgs(), "--realalbum") is { } wanted && wanted.Split('/', 2) is [var artistName, var albumTitle])
        {
            var group = vm.Artists.FirstOrDefault(a => string.Equals(a.Name, artistName, StringComparison.OrdinalIgnoreCase));
            if (group is null)
            {
                Console.WriteLine($"real album: no artist '{artistName}'");
            }
            else
            {
                vm.SelectedArtist = group;
                Pump();
                Select(group, vm.Albums.FirstOrDefault(a => string.Equals(a.Album.Title, albumTitle, StringComparison.OrdinalIgnoreCase)));
                Console.WriteLine($"real album: '{vm.AlbumHeaderTitle}' by '{vm.AlbumHeaderArtist}', colour {Hex(vm.AlbumHeaderCoverColor)}, "
                    + $"name {Hex(((SolidColorBrush)((TextBlock)main.FindName("HeaderArtist")).Foreground).Color)}");
                Save(main, $"{stem}-real.png", w, h, scale);
            }
        }
        return true;
    }
}
