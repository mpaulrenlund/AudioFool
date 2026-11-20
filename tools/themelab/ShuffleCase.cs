using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Track = AudioFool.Core.Models.Track;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AudioFool;
using AudioFool.Core.Library;
using AudioFool.Core.Models;
using AudioFool.Core.Playback;
using AudioFool.Core.Settings;
using AudioFool.ViewModels;

namespace ThemeLab;

/// <summary>
/// --window shuffle [--theme dark] [--dpi 1.25] (session 80): the Artists header's
/// Shuffle library and the Albums header's Shuffle artist buttons, on the sample
/// library, then through the real view model on a made-up library. Geometry (24 px
/// targets centred in the 28 px headers, 9 px from the strip's right edge (its 14 px
/// padding less panelShuffle.edgeOverhang), the
/// Playlists toggle panelShuffle.gap (0 since 2026-11-17) left of Shuffle library, a panelShuffle.iconSize icon centred, drawn the Playlists icon's size), the icon's
/// colours at rest and hovered (the hover is real: the mouse device's position is
/// set by reflection and Mouse.Synchronize run, as --window eqdouble does; the
/// pointer never moves), the accessible names, playlist mode. The behaviour runs on
/// songs whose files don't exist, so every Play fails at its File.Exists and nothing
/// reaches the sound device; what Play was handed is read from the engine's
/// LastPlayFailure (the start song) over many presses, which must cover the expected
/// queue exactly. Settings save to a scratch file beside --out; the real
/// settings.json is hashed before and after. Renders &lt;out&gt;-rest.png,
/// -hover-library/-artist.png, -playlists.png (and whole-window 3x renders -rest-x3, -hover-*-x3 at 100%).
/// "ALL OK".
/// </summary>
internal static partial class Program
{
    private static int RunShuffle(MainWindow main, MainViewModel vm, AudioEngine engine, string outPath, double w, double h, double scale)
    {
        var stem = System.IO.Path.ChangeExtension(System.IO.Path.GetFullPath(outPath), null);
        var allOk = true;
        void Check(bool ok, string what)
        {
            allOk &= ok;
            Console.WriteLine($"  {(ok ? "OK" : "WRONG")}: {what}");
        }

        var flags = BindingFlags.NonPublic | BindingFlags.Instance;

        // ---------------------------------------------------------- settings to scratch
        var realSettings = AppSettings.SettingsPath;
        string? Hash(string p) => File.Exists(p) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p)))[..16] : null;
        var realBefore = Hash(realSettings);
        var realTimeBefore = File.Exists(realSettings) ? File.GetLastWriteTimeUtc(realSettings) : DateTime.MinValue;
        var settings = (AppSettings)typeof(MainViewModel).GetField("_settings", flags)!.GetValue(vm)!;
        var pathField = typeof(AppSettings).GetField("_path", flags)
            ?? throw new InvalidOperationException("AppSettings._path not found: not running, it could save the real settings");
        var scratchDir = $"{stem}-settings";
        Directory.CreateDirectory(scratchDir);
        var scratch = System.IO.Path.Combine(scratchDir, "settings.json");
        if (File.Exists(scratch))
            File.Delete(scratch);
        pathField.SetValue(settings, scratch);
        Console.WriteLine($"theme {ThemeService.Current}; scale {scale}; settings save to {scratch} (real hash {realBefore ?? "none"}); Shuffle at start {vm.IsShuffle}");

        main.UpdateLayout();
        Pump();

        var lib = (Button)main.FindName("ShuffleLibraryButton");
        var art = (Button)main.FindName("ShuffleArtistButton");
        var toggle = (ToggleButton)main.FindName("PlaylistsToggle");
        var artistsPanel = (HeaderedContentControl)main.FindName("ArtistsPanel");
        var albumsPanel = (HeaderedContentControl)main.FindName("AlbumsPanel");
        var artistList = (ListBox)main.FindName("ArtistList");
        var albumList = (ListBox)main.FindName("AlbumList");
        var grid = (DataGrid)main.FindName("TrackGrid");
        Border Strip(HeaderedContentControl p) => (Border)p.Template.FindName("HeaderStrip", p);
        Rect Box(FrameworkElement e, Visual? to = null) => e.TransformToAncestor(to ?? main).TransformBounds(new Rect(e.RenderSize));
        string R(Rect r) => $"x {r.Left:0.##}-{r.Right:0.##} y {r.Top:0.##}-{r.Bottom:0.##} ({r.Width:0.##}x{r.Height:0.##})";
        Color Token(string key) => ((SolidColorBrush)main.FindResource(key)).Color;
        string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";
        var muted = Token("color.text.muted");
        var primary = Token("color.text.primary");
        Console.WriteLine($"tokens: text.muted {Hex(muted)}, text.primary {Hex(primary)}, panel.bg {Hex(Token("color.panel.bg"))}");

        // ---------------------------------------------------------- 1. geometry
        Console.WriteLine("1. geometry");
        foreach (var (name, button, panel) in new[] { ("Shuffle library", lib, artistsPanel), ("Shuffle artist", art, albumsPanel) })
        {
            var strip = Strip(panel);
            var b = Box(button);
            var s = Box(strip);
            var p = Box(panel);
            var divider = strip.BorderThickness.Bottom;
            var above = b.Top - s.Top;
            var below = s.Bottom - divider - b.Bottom;
            Console.WriteLine($"  {name}: button {R(b)}; header strip {R(s)} (divider {divider}, padding {strip.Padding.Left}/{strip.Padding.Right}); panel {R(p)}");
            Check(Math.Abs(b.Width - 24) < 0.01 && Math.Abs(b.Height - 24) < 0.01, $"{name}: {b.Width:0.##}x{b.Height:0.##}");
            Check(Math.Abs(s.Height - 28) < 0.01, $"{name}: header strip {s.Height:0.##} px tall");
            Check(Math.Abs(above - below) <= 1.01, $"{name}: centred in the header: {above:0.##} above, {below:0.##} below (above the divider)");
            // In the strip's 14 px padding by edgeOverhang (2026-11-17), so 9 px in.
            var inset = strip.Padding.Right - (double)main.FindResource("panelShuffle.edgeOverhang");
            Check(Math.Abs(s.Right - b.Right - inset) <= 0.5, $"{name}: right edge {s.Right - b.Right:0.##} px in from the strip's (inner) right edge; {p.Right - b.Right:0.##} from the panel's outer edge");
            var vb = FindFirst<Viewbox>(button)!;
            var v = Box(vb);
            var iconSize = (double)main.FindResource("panelShuffle.iconSize");
            Check(Math.Abs(v.Width - iconSize) < 0.01 && Math.Abs(v.Height - iconSize) < 0.01
                  // An odd box (13) in an even target (24) can't be centred on whole
                  // pixels: layout puts it half a pixel off, crisp.
                  && Math.Abs(v.Left + v.Width / 2 - (b.Left + b.Width / 2)) <= 0.51 && Math.Abs(v.Top + v.Height / 2 - (b.Top + b.Height / 2)) <= 0.51,
                $"{name}: icon box {R(v)}, centred");
        }

        {
            var t = Box(toggle);
            var l = Box(lib);
            Check(Math.Abs(t.Width - 24) < 0.01 && Math.Abs(t.Height - 24) < 0.01 && Math.Abs(l.Left - t.Right - (double)main.FindResource("panelShuffle.gap")) < 0.01 && Math.Abs(t.Top - l.Top) < 0.01,
                $"PlaylistsToggle {R(t)}: {l.Left - t.Right:0.##} px left of Shuffle library, same top");
            var headerGrid = (FrameworkElement)VisualTreeHelper.GetParent((DependencyObject)VisualTreeHelper.GetParent(lib));
            var hg = Box(headerGrid);
            Console.WriteLine($"  header content grid {R(hg)}: its right edge is where the toggle sat at HEAD (HorizontalAlignment Right in the same grid); Shuffle library's right {l.Right:0.##}");
        }

        // ---------------------------------------------------------- pixels
        (byte[] Px, int Stride, int W, int H) Load(string path)
        {
            using var fs = File.OpenRead(path);
            var frame = BitmapDecoder.Create(fs, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
            var conv = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
            var stride = conv.PixelWidth * 4;
            var buf = new byte[stride * conv.PixelHeight];
            conv.CopyPixels(buf, stride, 0);
            return (buf, stride, conv.PixelWidth, conv.PixelHeight);
        }

        Color At((byte[] Px, int Stride, int W, int H) img, int x, int y)
        {
            var i = y * img.Stride + x * 4;
            return Color.FromRgb(img.Px[i + 2], img.Px[i + 1], img.Px[i]);
        }

        // The icon's ink inside a button's pixel box: bounding box, the colour of its
        // fully covered pixels, and how many pixels are not a blend of the background
        // and the expected colour.
        string Ink((byte[] Px, int Stride, int W, int H) img, Rect dip, double s, Color expect, out Color core, out Rect inkBox)
        {
            int x0 = (int)Math.Round(dip.Left * s), y0 = (int)Math.Round(dip.Top * s);
            int x1 = (int)Math.Round(dip.Right * s), y1 = (int)Math.Round(dip.Bottom * s);
            var bg = At(img, x0 + 1, y0 + 1);
            int l = int.MaxValue, t = int.MaxValue, r = int.MinValue, b = int.MinValue, count = 0, misfit = 0, maxD = 0;
            var pixels = new List<(Color C, int D)>();
            for (var y = y0; y < y1; y++)
                for (var x = x0; x < x1; x++)
                {
                    var c = At(img, x, y);
                    var d = Math.Max(Math.Abs(c.R - bg.R), Math.Max(Math.Abs(c.G - bg.G), Math.Abs(c.B - bg.B)));
                    if (d <= 10)
                        continue;
                    count++;
                    (l, t, r, b) = (Math.Min(l, x), Math.Min(t, y), Math.Max(r, x), Math.Max(b, y));
                    pixels.Add((c, d));
                    maxD = Math.Max(maxD, d);

                    // A blend of bg and the expected colour?
                    double dr = expect.R - bg.R, dg = expect.G - bg.G, db = expect.B - bg.B;
                    var len = dr * dr + dg * dg + db * db;
                    var a = len == 0 ? 0 : Math.Clamp(((c.R - bg.R) * dr + (c.G - bg.G) * dg + (c.B - bg.B) * db) / len, 0, 1);
                    var res = Math.Max(Math.Abs(c.R - (bg.R + a * dr)), Math.Max(Math.Abs(c.G - (bg.G + a * dg)), Math.Abs(c.B - (bg.B + a * db))));
                    if (res > 6)
                        misfit++;
                }

            core = pixels.Where(p => p.D >= maxD - 2).GroupBy(p => p.C).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault();
            if (count == 0)
            {
                inkBox = Rect.Empty;
                return "no ink";
            }

            inkBox = new Rect(l, t, r - l + 1, b - t + 1);
            var cx = (l + r + 1) / 2.0 - (x0 + x1) / 2.0;
            var cy = (t + b + 1) / 2.0 - (y0 + y1) / 2.0;
            return $"ink {r - l + 1}x{b - t + 1} px at {l},{t} (button {x0},{y0}-{x1},{y1}), centre offset {cx:0.#},{cy:0.#}; bg {Hex(bg)}; "
                + $"{count} ink px, core {Hex(core)} ({pixels.Count(p => p.D >= maxD - 2)} px), {misfit} not a blend of bg and {Hex(expect)}";
        }

        Color Fg(Button b) => ((SolidColorBrush)FindFirst<System.Windows.Shapes.Path>(b)!.Stroke).Color;

        // ---------------------------------------------------------- 2. colours at rest
        Console.WriteLine("2. colours");
        var restPng = stem + "-rest.png";
        Save(main, restPng, w, h, scale);
        var rest = Load(restPng);
        var big = _dpi is null;   // 3x colour renders only at 100%
        // The user's call (session 80): drawn the size of the Playlists icon beside it.
        Ink(rest, Box(toggle), scale, muted, out _, out var playlistsInk);
        foreach (var (name, button) in new[] { ("Shuffle library", lib), ("Shuffle artist", art) })
        {
            Check(Fg(button) == muted, $"{name}: stroke at rest {Hex(Fg(button))} (text.muted {Hex(muted)}), IsMouseOver {button.IsMouseOver}");
            var line = Ink(rest, Box(button), scale, muted, out _, out var inkBox);
            Console.WriteLine($"    {name} {scale}x: {line}");
            // Two pixels at 100%, scaled: the glyphs differ in shape (the arrows are
            // square, the list lines wide), so at 125% they part by three.
            var slack = Math.Ceiling(2 * scale);
            Check(Math.Abs(inkBox.Width - playlistsInk.Width) <= slack && Math.Abs(inkBox.Height - playlistsInk.Height) <= slack,
                $"{name}: ink {inkBox.Width}x{inkBox.Height} px, Playlists' {playlistsInk.Width}x{playlistsInk.Height} (within {slack} px each way)");
        }

        void Crops(string suffix, Color expect, string label)
        {
            if (!big)
                return;
            var png = $"{stem}-{suffix}-x3.png";
            Save(main, png, w, h, 3);
            var img = Load(png);
            foreach (var (name, button) in new[] { ("Shuffle library", lib), ("Shuffle artist", art) })
            {
                var line = Ink(img, Box(button), 3, expect, out var core, out _);
                Console.WriteLine($"    {name} 3x: {line}");
                Check(Math.Abs(core.R - expect.R) <= 2 && Math.Abs(core.G - expect.G) <= 2 && Math.Abs(core.B - expect.B) <= 2,
                    $"{name}: {label} ink {Hex(core)} = {Hex(expect)}");
            }
        }

        Crops("rest", muted, "rest");

        // ---------------------------------------------------------- hover, for real
        var mouse = Mouse.PrimaryDevice;
        var srcField = typeof(MouseDevice).GetField("_inputSource", flags)!;
        var posField = typeof(MouseDevice).GetField("_lastPosition", flags)!;
        var hwnd = (HwndSource)PresentationSource.FromVisual(main)!;
        var fakeSource = srcField.FieldType.IsAssignableFrom(typeof(HwndSource))
            ? hwnd
            : Activator.CreateInstance(srcField.FieldType, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance, null, [hwnd], null);

        void Hovered(Point inWindow, Action act)
        {
            var savedSrc = srcField.GetValue(mouse);
            var savedPos = posField.GetValue(mouse);
            try
            {
                srcField.SetValue(mouse, fakeSource);
                posField.SetValue(mouse, hwnd.CompositionTarget.TransformToDevice.Transform(inWindow));
                Mouse.Synchronize();
                Pump();
                act();
            }
            finally
            {
                posField.SetValue(mouse, new Point(-5000, -5000));
                Mouse.Synchronize();
                Pump();
                srcField.SetValue(mouse, savedSrc);
                posField.SetValue(mouse, savedPos);
            }
        }

        foreach (var (name, button) in new[] { ("Shuffle library", lib), ("Shuffle artist", art) })
        {
            var b = Box(button);
            var other = button == lib ? art : lib;
            Hovered(new Point(b.Left + b.Width / 2, b.Top + b.Height / 2), () =>
            {
                Check(button.IsMouseOver && !other.IsMouseOver && !toggle.IsMouseOver,
                    $"{name}: hovered (IsMouseOver {button.IsMouseOver}; the other {other.IsMouseOver}, Playlists {toggle.IsMouseOver})");
                Check(Fg(button) == primary && Fg(other) == muted, $"{name}: stroke hovered {Hex(Fg(button))} (text.primary {Hex(primary)}), the other {Hex(Fg(other))}");
                var png = $"{stem}-hover-{(button == lib ? "library" : "artist")}.png";
                Save(main, png, w, h, scale);
                var img = Load(png);
                Console.WriteLine($"    {name} hovered {scale}x: {Ink(img, b, scale, primary, out _, out _)}");
                if (big)
                {
                    var crop = $"{stem}-hover-{(button == lib ? "library" : "artist")}-x3.png";
                    Save(main, crop, w, h, 3);
                    var line = Ink(Load(crop), b, 3, primary, out var core, out _);
                    Console.WriteLine($"    {name} hovered 3x: {line}");
                    Check(Math.Abs(core.R - primary.R) <= 2 && Math.Abs(core.G - primary.G) <= 2 && Math.Abs(core.B - primary.B) <= 2,
                        $"{name}: hover ink {Hex(core)} = {Hex(primary)}");
                }
            });
            Check(!button.IsMouseOver && Fg(button) == muted, $"{name}: after the pointer leaves, {Hex(Fg(button))}");
        }

        // ---------------------------------------------------------- 6. accessible names
        Console.WriteLine("6. automation");
        foreach (var (want, button) in new[] { ("Shuffle library", (UIElement)lib), ("Shuffle artist", art), ("Playlists", toggle) })
        {
            var peer = UIElementAutomationPeer.CreatePeerForElement(button);
            Console.WriteLine($"    {peer.GetAutomationControlType()} '{peer.GetName()}' id={peer.GetAutomationId()} focusable={peer.IsKeyboardFocusable()} invoke={peer.GetPattern(PatternInterface.Invoke) is not null}");
            Check(peer.GetName() == want, $"name '{peer.GetName()}'");
        }

        // ---------------------------------------------------------- 4. playlist mode
        Console.WriteLine("4. playlist mode");
        var libBefore = Box(lib);
        vm.IsPlaylistMode = true;
        Pump();
        main.UpdateLayout();
        Check(art.Visibility == Visibility.Collapsed && !art.IsVisible, $"Shuffle artist {art.Visibility}");
        Check(lib.IsVisible && Box(lib) == libBefore, $"Shuffle library shown at {R(Box(lib))}");
        Check(toggle.IsChecked == true && toggle.IsVisible, $"Playlists toggle checked {toggle.IsChecked}");
        Settle(1200);
        main.UpdateLayout();
        Save(main, stem + "-playlists.png", w, h, scale);
        vm.IsPlaylistMode = false;
        Pump();
        main.UpdateLayout();
        Check(art.IsVisible, "back from playlists: Shuffle artist shown again");

        // ---------------------------------------------------------- 5. behaviour
        Console.WriteLine("5. behaviour (every Play fails at File.Exists: nothing reaches the device)");
        var ffField = typeof(MainViewModel).GetField("_folderFilteredLibrary", flags)!;
        MusicLibrary Ff() => (MusicLibrary)ffField.GetValue(vm)!;

        void Click(Button b)
        {
            ((IInvokeProvider)new ButtonAutomationPeer(b).GetPattern(PatternInterface.Invoke)).Invoke();
            Pump();
        }

        // Nothing happens: same failure object, same status, same Shuffle, engine idle.
        void Nothing(string what, Button b)
        {
            var failure = engine.LastPlayFailure;
            var status = vm.StatusText;
            var shuffle = vm.IsShuffle;
            var written = File.Exists(scratch) ? File.GetLastWriteTimeUtc(scratch) : DateTime.MinValue;
            Click(b);
            Check(ReferenceEquals(failure, engine.LastPlayFailure) && status == vm.StatusText && shuffle == vm.IsShuffle
                  && engine.State == PlaybackState.Stopped
                  && (File.Exists(scratch) ? File.GetLastWriteTimeUtc(scratch) : DateTime.MinValue) == written,
                $"{what}: nothing happened (Shuffle {vm.IsShuffle}, status '{vm.StatusText}', engine {engine.State})");
        }

        Check(Ff().AllTracks.Count == 0, $"sample lists only: folder-filtered library {Ff().AllTracks.Count} songs");
        Nothing("Shuffle library, empty library", lib);

        // A made-up library: 30 artists in a ticked folder, two albums each (the later
        // one first in the files), two songs each; an unticked folder with an extra
        // album for Artist 07 and an artist of its own. None of the files exist.
        var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "themelab-shuffle-" + Guid.NewGuid().ToString("N")[..8]);
        var ticked = System.IO.Path.Combine(root, "Ticked");
        var unticked = System.IO.Path.Combine(root, "Unticked");
        Track Song(string folder, string artist, string album, int year, int n) => new()
        {
            FilePath = System.IO.Path.Combine(folder, artist, album, $"{n:00} {album} {n}.flac"),
            FileSize = 1000,
            ModifiedUtc = new DateTime(2024, 3, 1, 12, 0, 0, DateTimeKind.Utc),
            Title = $"{album} {n}", Artist = artist, AlbumArtist = artist, Album = album,
            TrackNumber = n, TrackCount = 2, DiscNumber = 1, DiscCount = 1, Year = year, Kind = "FLAC",
            Duration = TimeSpan.FromSeconds(180 + n),
        };
        var songs = new List<Track>();
        for (var i = 1; i <= 30; i++)
            foreach (var (album, year) in new[] { ($"Later {i:00}", 2010), ($"Earlier {i:00}", 2000) })
                for (var n = 2; n >= 1; n--)
                    songs.Add(Song(ticked, $"Artist {i:00}", album, year, n));
        songs.Add(Song(unticked, "Artist 07", "Hidden 07", 2005, 1));
        songs.Add(Song(unticked, "Artist 07", "Hidden 07", 2005, 2));
        songs.Add(Song(unticked, "Only Unticked", "Elsewhere", 2001, 1));
        Check(!Directory.Exists(root), $"no files: {root} does not exist");

        vm.FolderFilters.Add(new FolderFilterItem(ticked, true));
        vm.FolderFilters.Add(new FolderFilterItem(unticked, false));
        typeof(MainViewModel).GetMethod("ApplyLibrary", flags)!.Invoke(vm, [LibraryScanner.Build(songs), false]);
        Pump();
        main.UpdateLayout();
        var tickedSongs = songs.Where(t => t.FilePath.StartsWith(ticked + "\\", StringComparison.Ordinal)).Select(t => t.FilePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Check(Ff().AllTracks.Count == 120 && Ff().AllTracks.All(t => tickedSongs.Contains(t.FilePath)),
            $"library: {songs.Count} songs, folder-filtered {Ff().AllTracks.Count} (the ticked 120); artists listed {vm.Artists.Count}");

        // Somewhere far down, so the lists have scrolled.
        var far = vm.Artists.First(a => a.Name == "Artist 27");
        artistList.SelectedItem = far;
        artistList.ScrollIntoView(far);
        Pump();
        albumList.SelectedIndex = vm.Albums.Count - 1;
        Pump();
        grid.SelectedIndex = 1;
        Pump();
        main.UpdateLayout();

        double Off(DependencyObject d) => FindFirst<ScrollViewer>(d)?.VerticalOffset ?? -1;
        string State() =>
            $"artist '{vm.SelectedArtist?.Name}' [{string.Join(",", artistList.SelectedItems.OfType<ArtistGroup>().Select(a => a.Name))}] scroll {Off(artistList):0.##}; "
            + $"album '{vm.SelectedAlbum?.Album.Title}' [{albumList.SelectedItems.Count}] scroll {Off(albumList):0.##}; "
            + $"song row {grid.SelectedIndex} scroll {Off(grid):0.##}; search '{vm.SearchQuery}'";

        // Presses a button many times; the start songs Play was handed must cover
        // the expected queue, and nothing else.
        void Covers(string what, Button b, IReadOnlyList<Track> expected, int presses)
        {
            var want = expected.Select(t => t.FilePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var firsts = new List<string>();
            for (var i = 0; i < presses; i++)
            {
                if (i < 8)
                    Click(b);                    // through the automation peer, as a click
                else
                    b.Command.Execute(null);      // the same bound command, quicker
                var path = engine.LastPlayFailure?.FilePath ?? "(none)";
                seen[path] = seen.GetValueOrDefault(path) + 1;
                if (i < 8)
                    firsts.Add(System.IO.Path.GetFileNameWithoutExtension(path));
            }

            var outside = seen.Keys.Where(k => !want.Contains(k)).ToList();
            Console.WriteLine($"    {what}: {presses} presses; first starts: {string.Join(" | ", firsts)}");
            Console.WriteLine($"    {what}: {seen.Count} different start songs of {want.Count} expected; per song min {seen.Values.Min()} max {seen.Values.Max()} (expected ~{presses / (double)want.Count:0.#})");
            Check(outside.Count == 0 && seen.Count == want.Count,
                $"{what}: queue = the {want.Count} expected songs{(outside.Count > 0 ? $"; outside it: {string.Join(", ", outside.Take(3))}" : "")}");
            Check(firsts.Distinct().Count() >= (want.Count > 8 ? 4 : 2), $"{what}: start songs vary ({firsts.Distinct().Count()} different in the first 8)");
            Check(vm.IsShuffle && engine.Shuffle && AppSettings.Load(scratch, DateTime.UtcNow).Shuffle,
                $"{what}: Shuffle on (view model {vm.IsShuffle}, engine {engine.Shuffle}, scratch settings file {AppSettings.Load(scratch, DateTime.UtcNow).Shuffle})");
            Check(engine.State == PlaybackState.Stopped && !engine.HasOutput && engine.LastPlayFailure?.Kind is not null,
                $"{what}: engine {engine.State}, device opened {engine.HasOutput}, failure {engine.LastPlayFailure?.Kind}");
        }

        Console.WriteLine("  Shuffle library");
        var before = State();
        Console.WriteLine($"    before: {before}");
        Check(!vm.IsShuffle, $"Shuffle off before ({vm.IsShuffle})");
        Covers("Shuffle library", lib, Ff().AllTracks, 3000);
        Console.WriteLine($"    status line after: '{vm.StatusText}'");
        var after = State();
        Check(after == before, $"selection and scroll unchanged: {after}");

        Console.WriteLine("  Shuffle library with a search");
        vm.SearchQuery = "Earlier 03";
        Settle(600);
        Console.WriteLine($"    search shows {vm.Artists.Count} artist(s); {State()}");
        Covers("Shuffle library, searching", lib, Ff().AllTracks, 3000);
        vm.SearchQuery = "";
        Settle(600);

        Console.WriteLine("  Shuffle artist, one artist");
        var one = vm.Artists.First(a => a.Name == "Artist 12");
        artistList.SelectedItems.Clear();
        artistList.SelectedItem = one;
        Pump();
        var oneExpected = one.Albums.SelectMany(a => a.Tracks).ToList();
        Console.WriteLine($"    selected [{string.Join(",", vm.SelectedArtists.Select(a => a.Name))}]; albums in list order: {string.Join(", ", one.Albums.Select(a => $"{a.Title} ({a.Year})"))}");
        var oneQueue = ShufflePlay.ArtistsQueue(vm.SelectedArtists);
        Console.WriteLine($"    the command's queue (ShufflePlay.ArtistsQueue of SelectedArtists): {string.Join(" | ", oneQueue.Select(t => t.Title))}");
        Check(oneQueue.Select(t => t.FilePath).SequenceEqual(oneExpected.Select(t => t.FilePath))
              && oneQueue.Select(t => t.Year).SequenceEqual(oneQueue.Select(t => t.Year).Order()),
            "album order (oldest first, as the Albums list) and track order");
        var beforeArtist = State();
        Covers("Shuffle artist (one)", art, oneExpected, 300);
        Check(State() == beforeArtist, $"selection and scroll unchanged: {State()}");

        Console.WriteLine("  Shuffle artist, two artists");
        var two = vm.Artists.First(a => a.Name == "Artist 03");
        artistList.SelectedItems.Add(two);
        Pump();
        var twoExpected = one.Albums.SelectMany(a => a.Tracks).Concat(two.Albums.SelectMany(a => a.Tracks)).ToList();
        var twoQueue = ShufflePlay.ArtistsQueue(vm.SelectedArtists);
        Console.WriteLine($"    selected [{string.Join(",", vm.SelectedArtists.Select(a => a.Name))}]; queue {twoQueue.Count} songs, {twoQueue.Distinct().Count()} different");
        Check(twoQueue.Count == 8 && twoQueue.Distinct().Count() == 8, "both artists, each song once");
        Covers("Shuffle artist (two)", art, twoExpected, 600);

        Console.WriteLine("  Shuffle artist, Artist 07 (an album in the unticked folder)");
        var seven = vm.Artists.First(a => a.Name == "Artist 07");
        artistList.SelectedItems.Clear();
        artistList.SelectedItem = seven;
        Pump();
        Covers("Shuffle artist (07)", art, seven.Albums.SelectMany(a => a.Tracks).ToList(), 300);
        Check(seven.Albums.All(a => a.Tracks.All(t => tickedSongs.Contains(t.FilePath))), $"Artist 07's albums: {string.Join(", ", seven.Albums.Select(a => a.Title))} (Hidden 07 left out)");

        Console.WriteLine("  Shuffle artist with a search narrowing the artist");
        vm.SearchQuery = "Earlier 12";
        Settle(600);
        var searched = vm.Artists.FirstOrDefault(a => a.Name == "Artist 12");
        if (searched is not null)
        {
            artistList.SelectedItems.Clear();
            artistList.SelectedItem = searched;
            Pump();
            var q = ShufflePlay.ArtistsQueue(vm.SelectedArtists);
            Console.WriteLine($"    NOTE: with search 'Earlier 12' Artist 12 lists {searched.Albums.Count} album(s); Shuffle artist would queue {q.Count} song(s): {string.Join(" | ", q.Select(t => t.Title))}");
        }
        vm.SearchQuery = "";
        Settle(600);

        Console.WriteLine("  no artist selected");
        vm.IsShuffle = false;
        artistList.SelectedItems.Clear();
        vm.SelectedArtist = null;
        Pump();
        Console.WriteLine($"    selected artists {vm.SelectedArtists.Count}, SelectedArtist {(vm.SelectedArtist?.Name ?? "null")}");
        if (vm.SelectedArtists.Count == 0 && vm.SelectedArtist is null)
            Nothing("Shuffle artist, no artist", art);
        else
            Check(false, "could not clear the artist selection");

        Console.WriteLine("  empty library (both folders unticked)");
        vm.FolderFilters[^2].IsEnabled = false;
        typeof(MainViewModel).GetMethod("ApplyLibrary", flags)!.Invoke(vm, [LibraryScanner.Build(songs), false]);
        Pump();
        Console.WriteLine($"    folder-filtered {Ff().AllTracks.Count}, artists {vm.Artists.Count}, selected {vm.SelectedArtists.Count}/{vm.SelectedArtist?.Name ?? "null"}");
        Nothing("Shuffle library, nothing ticked", lib);
        Nothing("Shuffle artist, nothing ticked", art);

        // ---------------------------------------------------------- the real settings
        var realAfter = Hash(realSettings);
        var realTimeAfter = File.Exists(realSettings) ? File.GetLastWriteTimeUtc(realSettings) : DateTime.MinValue;
        Check(realAfter == realBefore && realTimeAfter == realTimeBefore, $"real settings.json untouched ({realAfter ?? "none"})");
        Check(!Directory.Exists(root), "still no files under the made-up folders");

        Console.WriteLine(allOk ? "ALL OK" : "FAILURES");
        return allOk ? 0 : 1;
    }
}
