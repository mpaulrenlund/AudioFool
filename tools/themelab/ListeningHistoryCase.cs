using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using AudioFool;
using AudioFool.Core.Library;
using AudioFool.Core.Scrobbling;
using AudioFool.ViewModels;

namespace ThemeLab;

/// <summary>
/// --window listening [--state loaded|loading|offline|disconnected] (session 74):
/// Listening History over the real library.json (read only), with Last.fm's
/// answers made up from names in it plus a few it doesn't hold, so nothing
/// reaches Last.fm. Checks the tiles, the rows and which can be clicked, a
/// period switch and the way back (no second fetch), a period that fails, and
/// clicks on an artist, an album and a song, applied to the main window.
/// Renders &lt;out&gt;.png, and for loaded also &lt;out&gt;-week.png and &lt;out&gt;-periodfail.png.
/// </summary>
internal static partial class Program
{
    private static int RunListeningHistory(MainWindow main, MainViewModel vm, string outPath, double w, double scale, string state)
    {
        LoadRealLibrary(vm);
        Settle(300);

        var allOk = true;
        void Check(bool ok, string what)
        {
            allOk &= ok;
            Console.WriteLine($"  {(ok ? "OK" : "WRONG")}: {what}");
        }

        var library = LibraryScanner.Build([.. vm.StatisticsTracks]);
        var fake = new FakeListening(library, failPeriods: state == "loaded" ? ["1month"] : []);
        var api = state == "disconnected" ? null : new LastFmApi("0123456789abcdef0123456789abcdef", "", new HttpClient(fake));
        fake.Mode = state switch { "loading" => FakeListening.Never, "offline" => FakeListening.Offline, _ => FakeListening.Answer };

        var clock = new FixedClock(new DateTimeOffset(2026, 11, 2, 15, 0, 0, TimeSpan.Zero));
        using var history = new ListeningHistoryViewModel(api, state == "disconnected" ? null : "marcusrenlund", vm.StatisticsTracks, clock);
        var dialog = new ListeningHistoryWindow(history, main);
        var load = history.LoadAsync();
        for (var i = 0; i < 100 && !load.IsCompleted && state != "loading"; i++)
            Settle(50);

        FrameworkElement Render(string path)
        {
            dialog.ApplyTemplate();
            var root = (FrameworkElement)dialog.Content;
            root.Measure(new Size(w, double.PositiveInfinity));
            var height = Math.Ceiling(root.DesiredSize.Height);
            root.Arrange(new Rect(0, 0, w, height));
            root.UpdateLayout();
            var bar = Descendants<Wpf.Ui.Controls.TitleBar>(root).First();
            RaiseLoaded(bar);
            Settle(400);
            Save(root, path, w, height, scale);
            Console.WriteLine($"  rendered {Path.GetFileName(path)} {w}x{height}");
            return root;
        }

        Console.WriteLine($"listening {state}: title '{history.Title}', message '{history.Message}', loaded {history.IsLoaded}");
        var root = Render(outPath);
        PrintTitleBar(Descendants<Wpf.Ui.Controls.TitleBar>(root).First(), root);

        if (state != "loaded")
        {
            var expect = state switch
            {
                "loading" => history.Message == "Loading from Last.fm…" && !history.IsLoaded,
                "offline" => history.Message?.StartsWith("Couldn't load your listening history. Couldn't reach Last.fm") == true && !history.IsLoaded,
                _ => history.IsNotConnected && history.Message is null && !history.IsLoaded && fake.Calls.Count == 0,
            };
            Check(expect, $"{state}: message '{history.Message}', loaded {history.IsLoaded}, calls {fake.Calls.Count}");
            Console.WriteLine(allOk ? "ALL OK" : "SOMETHING WRONG");
            return allOk ? 0 : 1;
        }

        void PrintRows(string name, IReadOnlyList<ListeningRow> rows)
        {
            Console.WriteLine($"  [{name}] {rows.Count} rows, {rows.Count(r => r.IsClickable)} clickable");
            foreach (var r in rows.Take(4))
                Console.WriteLine($"    {r.Label,-36} {r.Value,-12} {r.Detail,-40} bar={r.HasBar} {r.Fill.Value:0.00} clickable={r.IsClickable}");
        }
        foreach (var t in history.Tiles)
            Console.WriteLine($"  tile {t.Label,-18} {t.Value}");
        PrintRows("top artists", history.TopArtists);
        PrintRows("top albums", history.TopAlbums);
        PrintRows("top tracks", history.TopTracks);
        PrintRows("recent", history.Recent);

        Check(history.IsLoaded && history.Message is null, "loaded, no message");
        Check(history.Tiles.Select(t => t.Label).SequenceEqual(["Scrobbles", "Scrobbling since", "Artists", "Albums", "Tracks"])
              && history.Tiles[0].Value == "48,210" && history.Tiles[1].Value == "Mar 2014", "five tiles, 48,210 scrobbles since Mar 2014");
        Check(history.TopArtists.Count == 10 && history.TopAlbums.Count == 10 && history.TopTracks.Count == 10 && history.Recent.Count == 20,
            "10 / 10 / 10 / 20 rows");
        var owned = fake.Owned;
        Check(history.TopArtists.Count(r => r.IsClickable) == owned && history.TopAlbums.Count(r => r.IsClickable) == owned
              && history.TopTracks.Count(r => r.IsClickable) == owned,
            $"the {owned} the library holds can be clicked in each top list, the rest can't");
        Check(history.TopArtists.All(r => r.HasBar) && history.Recent.All(r => !r.HasBar) && history.TopArtists[0].Fill.Value == 1,
            "bars on the top lists only, the first full");
        Check(history.Recent[0].Value == "Now playing" && history.Recent[1].Value == "12 min ago", "recent: now playing, then 12 min ago");
        Check(history.Periods.Single(p => p.IsSelected).Label == "12 Months", "opens on 12 Months");

        var buttons = Descendants<Button>(root).Where(b => b.DataContext is ListeningRow).ToList();
        Check(buttons.Count(b => b.IsEnabled) == buttons.Count(b => ((ListeningRow)b.DataContext).IsClickable), "row buttons enabled only when clickable");
        var radios = Descendants<RadioButton>(root).ToList();
        Console.WriteLine("  periods: " + string.Join("  ", radios.Select(r =>
            $"{(r.DataContext is PeriodChoice c ? c.Label : "?")} at {r.TranslatePoint(new Point(0, 0), root).X:0} w {r.ActualWidth:0}{(r.IsChecked == true ? " (chosen)" : "")}")));
        var widths = radios.Select(r => r.ActualWidth).ToList();

        // A period switch, by the radio button as a click would, and back.
        var yearCalls = fake.CallsFor("12month");
        radios[0].IsChecked = true;
        for (var i = 0; i < 60 && history.TopEmptyText == "Loading…"; i++)
            Settle(50);
        Settle(200);
        Check(history.TopArtists.FirstOrDefault()?.Value == "1 play" && fake.CallsFor("7day") == 3,
            $"7 Days fetched (3 calls), its own rows: first '{history.TopArtists.FirstOrDefault()?.Label}' {history.TopArtists.FirstOrDefault()?.Value}");
        Check(radios.Select(r => r.ActualWidth).SequenceEqual(widths), "the words keep their widths when the choice moves");
        Render(Path.ChangeExtension(outPath, null) + "-week.png");

        radios[4].IsChecked = true;
        Settle(300);
        Check(fake.CallsFor("12month") == yearCalls && history.TopArtists[0].Value == "412 plays", "back to 12 Months without fetching it again");

        radios[1].IsChecked = true;
        for (var i = 0; i < 60 && history.TopEmptyText == "Loading…"; i++)
            Settle(50);
        Check(history.TopArtists.Count == 0 && history.TopEmptyText.StartsWith("Couldn't load this period. Rate limit exceeded"),
            $"a period that fails: '{history.TopEmptyText}'");
        Render(Path.ChangeExtension(outPath, null) + "-periodfail.png");
        radios[4].IsChecked = true;
        Settle(300);

        // Clicks: the row records the choice (DialogResult then throws, as the
        // window was never shown), and the main window goes to it.
        root = Render(Path.ChangeExtension(outPath, null) + "-again.png");
        var navigate = typeof(MainViewModel).GetMethod("NavigateTo", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        void Click(IReadOnlyList<ListeningRow> rows, string what)
        {
            var row = rows.First(r => r.IsClickable);
            var button = Descendants<Button>(root).First(b => ReferenceEquals(b.DataContext, row));
            history.Chosen = null;
            vm.SelectedArtist = vm.Artists[^1];
            Settle(200);
            try
            {
                button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, button));
            }
            catch (InvalidOperationException)
            {
            }
            if (history.Chosen is not { Target: { } target } chosen)
            {
                Check(false, $"{what}: nothing chosen");
                return;
            }
            navigate.Invoke(vm, [target, chosen.Kind]);
            Settle(300);
            var grid = (DataGrid)main.FindName("TrackGrid");
            var artistOk = vm.SelectedArtist?.Albums.Any(a => a.Tracks.Any(t => t.FilePath == target.FilePath)) == true;
            var albumOk = vm.SelectedAlbum?.Album.Tracks.Any(t => t.FilePath == target.FilePath) == true;
            var songOk = grid.SelectedItem is AudioFool.Core.Models.Track t2 && t2.FilePath == target.FilePath;
            var ok = chosen.Kind switch
            {
                ListeningKind.Artist => artistOk && ListeningMatch.Key(vm.SelectedArtist!.Name) == ListeningMatch.Key(row.Label),
                ListeningKind.Album => artistOk && albumOk,
                _ => artistOk && albumOk && songOk,
            };
            Check(ok, $"{what} '{row.Label}': artist '{vm.SelectedArtist?.Name}', album '{vm.SelectedAlbum?.Album.Title}', song selected {songOk}");
        }
        Click(history.TopArtists, "artist");
        Click(history.TopAlbums, "album");
        Click(history.TopTracks, "song");
        Click(history.Recent, "recent song");

        var unmatched = Descendants<Button>(root).First(b => b.DataContext is ListeningRow { IsClickable: false });
        Check(!unmatched.IsEnabled, $"a row the library doesn't hold ('{((ListeningRow)unmatched.DataContext).Label}') is disabled");

        Console.WriteLine(allOk ? "ALL OK" : "SOMETHING WRONG");
        return allOk ? 0 : 1;
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    /// <summary>
    /// Last.fm's read methods, answered from the library: the eight biggest
    /// artists, an album and a song of each, and two of each it doesn't hold.
    /// 7 Days answers with one play apiece, so its rows are told apart.
    /// </summary>
    private sealed class FakeListening(MusicLibrary library, string[] failPeriods) : HttpMessageHandler
    {
        public const int Answer = 0, Never = 1, Offline = 2;
        public int Mode { get; set; }
        public List<(string Method, string? Period)> Calls { get; } = [];
        public int Owned => Picks.Count;

        private readonly List<(string Artist, string Album, string Song)> Picks = [.. library.Artists
            .Where(a => a.Albums.Count > 0 && a.Albums[0].Tracks.Count > 0 && a.Albums[0].Tracks[0].Title.Length > 0)
            .OrderByDescending(a => a.Albums.Sum(al => al.Tracks.Count))
            .Take(8)
            .Select(a => (a.Name, a.Albums[0].Title, a.Albums[0].Tracks[0].Title))];

        private static readonly (string Artist, string Album, string Song)[] Strangers =
            [("Nobody In The Library", "An Album You Don't Own", "A Song You Don't Own"),
             ("Another Stranger", "Borrowed Record", "Heard Elsewhere")];

        public int CallsFor(string period) => Calls.Count(c => c.Period == period);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var query = request.RequestUri!.Query.TrimStart('?').Split('&').Select(p => p.Split('=', 2))
                .ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1]));
            var method = query["method"];
            var period = query.GetValueOrDefault("period");
            lock (Calls)
                Calls.Add((method, period));

            if (Mode == Never)
                await Task.Delay(Timeout.Infinite, ct);
            if (Mode == Offline)
                throw new HttpRequestException("No such host is known.");
            if (period is not null && failPeriods.Contains(period))
                return Json(HttpStatusCode.TooManyRequests, """{"error":29,"message":"Rate limit exceeded"}""");

            var all = Picks.Concat(Strangers).ToList();
            long Plays(int i) => period == "7day" ? 1 : 412 - i * 37;
            object body = method switch
            {
                "user.getInfo" => new { user = new { name = "marcusrenlund", playcount = "48210", artist_count = "1734", album_count = "4120", track_count = "21007", registered = new { unixtime = "1394668800" } } },
                "user.getTopArtists" => new { topartists = new { artist = all.Select((p, i) => new { name = p.Artist, playcount = Plays(i).ToString() }) } },
                "user.getTopAlbums" => new { topalbums = new { album = all.Select((p, i) => new { name = p.Album, playcount = Plays(i).ToString(), artist = new { name = p.Artist } }) } },
                "user.getTopTracks" => new { toptracks = new { track = all.Select((p, i) => new { name = p.Song, playcount = Plays(i).ToString(), artist = new { name = p.Artist } }) } },
                _ => new { recenttracks = new { track = Enumerable.Range(0, 21).Select(i => (object)(i == 0
                    ? new Dictionary<string, object> { ["name"] = all[0].Song, ["artist"] = new Dictionary<string, string> { ["#text"] = all[0].Artist }, ["album"] = new Dictionary<string, string> { ["#text"] = all[0].Album }, ["@attr"] = new { nowplaying = "true" } }
                    : new Dictionary<string, object> { ["name"] = all[i % all.Count].Song, ["artist"] = new Dictionary<string, string> { ["#text"] = all[i % all.Count].Artist }, ["album"] = new Dictionary<string, string> { ["#text"] = all[i % all.Count].Album }, ["date"] = new { uts = new DateTimeOffset(2026, 11, 2, 15, 0, 0, TimeSpan.Zero).AddMinutes(-12 * i * i).ToUnixTimeSeconds().ToString() } })) } },
            };
            return Json(HttpStatusCode.OK, JsonSerializer.Serialize(body));
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
            new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}
