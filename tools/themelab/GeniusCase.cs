using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AudioFool;
using AudioFool.Core.Genius;
using AudioFool.Core.Models;
using AudioFool.Formatting;
using AudioFool.ViewModels;

namespace ThemeLab;

/// <summary>
/// --window genius (session 75): the Genius window over a made-up Genius, so
/// nothing reaches genius.com and none of its text is used. Every state, each
/// rendered: a full page (&lt;out&gt;-full.png), "Show N more" comments, the
/// same song again from the lookup's memory, a song with no match and its
/// nearest results (-nomatch), one of them chosen (-chosen), a page with only
/// credits (-sparse), an empty page (-empty), the comments missing (-missing),
/// a song with no title (-notitle), no network (-offline) and an answer that
/// never comes (-loading). Also Ctrl+G and the song menu's item, and the
/// links found in text. "ALL OK".
/// </summary>
internal static partial class Program
{
    private static int RunGenius(MainWindow main, string outPath, double w, double scale)
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));

        var allOk = true;
        void Check(bool ok, string what)
        {
            allOk &= ok;
            Console.WriteLine($"  {(ok ? "OK" : "WRONG")}: {what}");
        }

        var fake = new FakeGenius();
        var lookup = new GeniusLookup(new GeniusClient(new HttpClient(fake)));
        var cover = MakeCover();
        var withArt = true;
        using var model = new GeniusViewModel(lookup, _ => Task.FromResult<BitmapSource?>(withArt ? cover : null));
        var dialog = new GeniusWindow(model, main);
        var basePath = Path.ChangeExtension(outPath, null);

        void Wait(Task task)
        {
            for (var i = 0; i < 200 && !task.IsCompleted; i++)
                Settle(25);
            Settle(150);
        }

        FrameworkElement Render(string name)
        {
            dialog.ApplyTemplate();
            var root = (FrameworkElement)dialog.Content;
            root.Measure(new Size(w, double.PositiveInfinity));
            var height = Math.Ceiling(root.DesiredSize.Height);
            root.Arrange(new Rect(0, 0, w, height));
            root.UpdateLayout();
            RaiseLoaded(Descendants<Wpf.Ui.Controls.TitleBar>(root).First());
            Settle(300);
            var path = $"{basePath}-{name}.png";
            Save(root, path, w, height, scale);
            Console.WriteLine($"  rendered {Path.GetFileName(path)} {w}x{height}");
            return root;
        }

        bool Shown(FrameworkElement root, string name) =>
            root.FindName(name) is UIElement e ? e.Visibility == Visibility.Visible : Descendants<FrameworkElement>(root).First(x => x.Name == name).Visibility == Visibility.Visible;

        string[] sections = ["AboutSection", "AnswersSection", "AnnotationsSection", "CreditsSection", "ConnectionsSection", "CommentsSection"];
        string ShownSections(FrameworkElement root) => string.Join(",", sections.Where(s => Shown(root, s)));

        // ------------------------------------------------ Ctrl+G and the menu
        const System.Reflection.BindingFlags own = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var resolved = typeof(MainWindow).GetMethod("ResolveShortcut", own)!
            .Invoke(main, [System.Windows.Input.Key.G, System.Windows.Input.ModifierKeys.Control, null])!.ToString();
        Check(resolved == "Genius", $"Ctrl+G resolves to {resolved}");
        var grid = (DataGrid)main.FindName("TrackGrid");
        var menu = grid.RowStyle.Setters.OfType<Setter>().Single(s => s.Property == FrameworkElement.ContextMenuProperty).Value as ContextMenu;
        var headers = menu!.Items.OfType<MenuItem>().Select(i => i.Header as string ?? "(bound)").ToList();
        Console.WriteLine("  song menu: " + string.Join(" | ", headers));
        Check(headers.IndexOf("Genius…") == headers.IndexOf("Analyze…") + 1, "Genius… sits just under Analyze…");
        Check(ShortcutGuide.For(false).SelectMany(g => g.Rows).Any(i => i.Keys == "Ctrl+G"), "Ctrl+G is in the shortcut guide");

        // ---------------------------------------------------------- links
        var parts = LinkedText.Split("Watch https://www.youtube.com/watch?v=abc123. Or (https://example.org/x).").ToList();
        Console.WriteLine("  link split: " + string.Join(" | ", parts.Select(p => $"{(p.IsLink ? "LINK " : "")}'{p.Text}'")));
        Check(parts.Count(p => p.IsLink) == 2 && parts[1].Text == "https://www.youtube.com/watch?v=abc123" && parts[2].Text.StartsWith(". Or (")
              && parts[3].Text == "https://example.org/x" && parts[4].Text == ").", "addresses become links, their trailing full stop and bracket stay text");

        // ------------------------------------------------------- full page
        var full = Song("Made-Up Song (2011 Remaster)", "The Made-Up Band", "Made-Up Album", 1997);
        Wait(dialog.ShowSong(full));
        var root = Render("full");
        PrintTitleBar(Descendants<Wpf.Ui.Controls.TitleBar>(root).First(), root);
        Console.WriteLine($"  full: title '{model.Title}', message '{model.Message}', sections {ShownSections(root)}");
        Check(model.Title == "Made-Up Song (2011 Remaster) · Genius", "window title is the song · Genius");
        Check(model.Message is null && model.HasPage && model.PageUrl == "https://genius.com/made-up-song", "page loaded, Open on Genius shown");
        Check(ShownSections(root) == string.Join(",", sections), "all six sections shown");
        Check(model.DetailLine == "Made-Up Album · 1997" && model.Artist == "The Made-Up Band" && model.Art is not null, "header: tags and the library cover");
        Check(model.Answers.Count == 3 && model.Annotations.Count == 3 && model.Credits.Count == 9 && model.Connections.Count == 2,
            $"3 answers, 3 annotations (the write-up's and an unreviewed one left out), 9 credits, 2 connections: {model.Answers.Count}/{model.Annotations.Count}/{model.Credits.Count}/{model.Connections.Count}");
        Check(model.Annotations.Count(a => a.Figure == "Verified") == 1 && model.Annotations.All(a => a.IsQuote), "one Verified, lyric lines as quotes");
        Check(model.Connections[1].Value.EndsWith("\nand 3 more") && model.Connections[1].Value.Split('\n').Length == 6, "eight covers: five named, and 3 more");
        Check(model.Comments.Count == 50 && model.CommentCount == "57 comments" && model.MoreCommentsText == "Show 7 more",
            $"50 of 57 comments, '{model.MoreCommentsText}'");
        Check(model.Comments[0].Figure == "Mar 7, 2017 · 297 votes" && model.Comments[1].Figure!.EndsWith("· 1 vote"), $"comment figures '{model.Comments[0].Figure}', '{model.Comments[1].Figure}'");
        var links = Descendants<TextBlock>(root).SelectMany(t => t.Inlines.OfType<Hyperlink>()).ToList();
        Console.WriteLine($"  hyperlinks: {links.Count}: " + string.Join(" | ", links.Select(l => l.NavigateUri?.AbsoluteUri ?? new TextRange(l.ContentStart, l.ContentEnd).Text)));
        Check(links.Any(l => l.NavigateUri?.Host == "www.youtube.com") && links.Any(l => l.NavigateUri?.Host == "example.org"), "links in the write-up and an answer");
        Check(Descendants<ScrollViewer>(root).All(s => s.ComputedVerticalScrollBarVisibility != Visibility.Visible), "no visible scrollbar");
        Check(Descendants<FrameworkElement>(root).All(e => e.ToolTip is null), "no tooltips");
        var firstCalls = fake.Asked.Count;
        Console.WriteLine($"  asked Genius: {string.Join(" ; ", fake.Asked)}");
        Check(fake.Asked.Count(a => a.StartsWith("/api/search")) == 1, "one search: artist and clean title");

        // "Show 7 more", the way the link's click does.
        Wait(model.ShowMoreCommentsAsync());
        Check(model.Comments.Count == 57 && !model.HasMoreComments, $"Show 7 more: {model.Comments.Count} comments, link gone");

        // The same song again: from memory, nothing asked.
        var asked = fake.Asked.Count;
        Wait(dialog.ShowSong(full));
        Check(fake.Asked.Count == asked && model.Comments.Count == 50 && model.HasMoreComments, "same song again: nothing asked, back to the first 50");

        // ------------------------------------------------ no match, then a choice
        withArt = false;
        Wait(dialog.ShowSong(Song("Nowhere Tune", "Obscure Act", "", null)));
        root = Render("nomatch");
        Console.WriteLine($"  nomatch: message '{model.Message}', suggestions {string.Join(" | ", model.Suggestions.Select(s => $"{s.Label} · {s.Detail}"))}, sections '{ShownSections(root)}'");
        Check(model.Message == GeniusViewModel.NoMatch && model.Suggestions.Count == 2 && !model.HasPage && ShownSections(root) == "",
            "no match: the line and two nearest results, translation page left out, no sections");
        Check(model.DetailLine is null && model.Art is null, "header without album, year or cover: placeholder note");

        var button = Descendants<Button>(root).First(b => b.DataContext is GeniusChoice);
        button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, button));
        for (var i = 0; i < 200 && model.Message is not null; i++)
            Settle(25);
        Settle(150);
        root = Render("chosen");
        Console.WriteLine($"  chosen: message '{model.Message}', sections {ShownSections(root)}, header '{model.SongTitle}'");
        Check(model.HasPage && !model.HasSuggestions && model.SongTitle == "Nowhere Tune" && ShownSections(root) == "AboutSection,CreditsSection",
            "a choice loads its page under the same header");

        // --------------------------------------------------------- other states
        withArt = true;
        Wait(dialog.ShowSong(Song("Sparse Song", "Quiet Artist", "Quiet Album", 2020)));
        root = Render("sparse");
        Check(model.Message is null && ShownSections(root) == "CreditsSection", $"credits only: {ShownSections(root)}");

        Wait(dialog.ShowSong(Song("Empty Song", "Quiet Artist", "Quiet Album", 2020)));
        root = Render("empty");
        Check(model.Message == GeniusViewModel.EmptyPage && model.HasPage && ShownSections(root) == "", $"empty page: '{model.Message}', link {model.HasPage}");

        Wait(dialog.ShowSong(Song("Missing Song", "The Made-Up Band", "Made-Up Album", 1997)));
        root = Render("missing");
        Console.WriteLine($"  missing: '{model.MissingText}'");
        Check(model.MissingText == "Genius didn't send the comments this time. Open the song again to try once more."
              && !model.HasComments && model.HasAbout, "comments missing: said in a line, the rest shown");
        var missingAsked = fake.Asked.Count;
        Wait(dialog.ShowSong(Song("Missing Song", "The Made-Up Band", "Made-Up Album", 1997)));
        Check(fake.Asked.Count == missingAsked + 4, "a page with a part missing isn't kept: asked again (4 requests, the search remembered)");

        asked = fake.Asked.Count;
        Wait(dialog.ShowSong(Song("", "The Made-Up Band", "Made-Up Album", 1997)));
        root = Render("notitle");
        Check(model.Message == GeniusViewModel.NoTitle && fake.Asked.Count == asked && model.SongTitle == "untitled", $"no title: '{model.Message}', header '{model.SongTitle}'");

        fake.Mode = FakeGenius.Offline;
        Wait(dialog.ShowSong(Song("Offline Song", "Someone", "", null)));
        root = Render("offline");
        Check(model.Message == "Couldn't load the Genius page. Couldn't reach Genius: No such host is known.", $"offline: '{model.Message}'");

        fake.Mode = FakeGenius.Never;
        var never = dialog.ShowSong(Song("Waiting Song", "Someone", "", null));
        Settle(300);
        root = Render("loading");
        Check(model.Message == GeniusViewModel.LookingUp && !never.IsCompleted, $"loading: '{model.Message}'");

        // A new song drops the one still waiting.
        fake.Mode = FakeGenius.Answer;
        Wait(dialog.ShowSong(full));
        Wait(never);
        Check(never.IsCompleted && model.SongTitle.StartsWith("Made-Up Song") && model.Message is null && model.HasComments,
            "a new song drops the one still waiting, and the old answer never lands");

        Console.WriteLine(allOk ? "ALL OK" : "SOMETHING WRONG");
        return allOk ? 0 : 1;
    }

    private static Track Song(string title, string artist, string album, int? year) => new()
    {
        FilePath = $@"X:\Scratch\{(title.Length > 0 ? title : "untitled")}.flac",
        Title = title,
        Artist = artist,
        AlbumArtist = artist,
        Album = album,
        Year = year,
    };

    /// <summary>A stand-in cover: two tones and a square, so its border and shadow show.</summary>
    private static BitmapSource MakeCover()
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x5A, 0x6E, 0x7A)), null, new Rect(0, 0, 288, 288));
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0xC9, 0xB2, 0x8A)), null, new Rect(72, 72, 144, 144));
        }
        var bitmap = new RenderTargetBitmap(288, 288, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>
    /// genius.com's web API, made up: answers by path, in Genius's shape, with
    /// text written for this case. Song 100 is the full page; 201 and 202 are
    /// the nearest results for a song with no match; 300 has only credits, 400
    /// nothing, and 500's comments fail.
    /// </summary>
    private sealed class FakeGenius : HttpMessageHandler
    {
        public const int Answer = 0, Never = 1, Offline = 2;
        public int Mode { get; set; }
        public List<string> Asked { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var uri = request.RequestUri!;
            lock (Asked)
                Asked.Add(Uri.UnescapeDataString(uri.PathAndQuery));
            if (Mode == Never)
                await Task.Delay(Timeout.Infinite, ct);
            if (Mode == Offline)
                throw new HttpRequestException("No such host is known.");

            var query = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Split('=', 2))
                .ToDictionary(p => p[0], p => Uri.UnescapeDataString(p.Length > 1 ? p[1] : ""));
            var path = uri.AbsolutePath;

            if (path == "/api/search/song")
                return Ok(Search(query["q"]));
            if (path == "/api/referents")
                return Ok(query["song_id"] == "100" ? Referents() : new { referents = Array.Empty<object>() });
            if (path == "/api/questions")
                return Ok(query["song_id"] == "100" ? Questions() : new { questions = Array.Empty<object>() });
            if (path.EndsWith("/comments"))
            {
                var id = path.Split('/')[3];
                if (id == "500")
                    return new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("<html>oops</html>") };
                return Ok(id == "100" ? Comments(int.Parse(query["page"])) : new { comments = Array.Empty<object>(), total_count = 0, next_page = (int?)null });
            }
            if (path.StartsWith("/api/songs/"))
                return Ok(new { song = SongPage(long.Parse(path.Split('/')[3])) });

            return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("""{"meta":{"status":404,"message":"Not found"}}""") };
        }

        private static HttpResponseMessage Ok(object response) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { meta = new { status = 200 }, response }), Encoding.UTF8, "application/json"),
        };

        private static object Hit(long id, string title, string artist) =>
            new { _type = "song", id, title, primary_artist = new { name = artist }, artist_names = artist, url = $"https://genius.com/song-{id}" };

        private static object Search(string q)
        {
            object[] hits = q switch
            {
                _ when q.Contains("Made-Up Song") => [Hit(901, "The Made-Up Band - Made-Up Song (English Translation)", "Genius English Translations"), Hit(100, "Made-Up Song", "The Made-Up Band")],
                _ when q.Contains("Nowhere Tune") => [Hit(903, "Nowhere Tune (Romanized)", "Genius Romanizations"), Hit(201, "Nowhere Tune", "Someone Else Entirely"), Hit(202, "Nowhere Tunes", "Another Act")],
                _ when q.Contains("Sparse Song") => [Hit(300, "Sparse Song", "Quiet Artist")],
                _ when q.Contains("Empty Song") => [Hit(400, "Empty Song", "Quiet Artist")],
                _ when q.Contains("Missing Song") => [Hit(500, "Missing Song", "The Made-Up Band")],
                _ => [],
            };
            return new { sections = new[] { new { type = "song", hits = hits.Select(h => new { type = "song", result = h }) } } };
        }

        private static object Names(params string[] names) => names.Select(n => new { name = n });

        private static object SongPage(long id) => id switch
        {
            100 or 500 => new
            {
                id,
                title = id == 100 ? "Made-Up Song" : "Missing Song",
                url = id == 100 ? "https://genius.com/made-up-song" : "https://genius.com/missing-song",
                primary_artist = new { name = "The Made-Up Band" },
                description = new { plain = "A song written for this test, in three parts that each change key, about a long drive home after a show that went badly.\n\n"
                    + "The band recorded it live in one take in a converted barn, then added the strings a month later. A short film of the session is at https://www.youtube.com/watch?v=abc123." },
                album = new { name = "Made-Up Album" },
                release_date_for_display = "May 21, 1997",
                recording_location = "A Converted Barn, Somewhere Rural",
                writer_artists = Names("First Writer", "Second Writer", "Third Writer"),
                producer_artists = Names("The Producer"),
                custom_performances = new object[]
                {
                    new { label = "Mastered At", artists = Names("A Mastering Studio") },
                    new { label = "Drums", artists = Names("The Drummer") },
                    new { label = "Bass", artists = Names("The Bassist") },
                    new { label = "Strings", artists = Names("A Small String Section", "Its Arranger") },
                    new { label = "Artwork", artists = Names("A Painter") },
                },
                song_relationships = new object[]
                {
                    new { relationship_type = "samples", songs = new[] { Hit(11, "An Older Record", "A Sixties Group") } },
                    new { relationship_type = "covered_by", songs = Enumerable.Range(1, 8).Select(i => Hit(20 + i, "Made-Up Song", $"Cover Act {i}")) },
                    new { relationship_type = "translations", songs = new[] { Hit(901, "Made-Up Song (English Translation)", "Genius English Translations") } },
                },
            },
            201 => new
            {
                id,
                title = "Nowhere Tune",
                url = "https://genius.com/nowhere-tune",
                primary_artist = new { name = "Someone Else Entirely" },
                description = new { plain = "A different song of the same name, chosen from the nearest results." },
                writer_artists = Names("Someone Else Entirely"),
            },
            300 => new { id, title = "Sparse Song", url = "https://genius.com/sparse-song", primary_artist = new { name = "Quiet Artist" }, description = new { plain = "?" }, producer_artists = Names("A Producer"), release_date_for_display = "2020" },
            _ => new { id, title = "Empty Song", url = "https://genius.com/empty-song", primary_artist = new { name = "Quiet Artist" }, description = new { plain = "?" } },
        };

        private static object Referents() => new
        {
            referents = new object[]
            {
                new { fragment = "Made-Up Song", is_description = true, classification = "accepted", annotations = new[] { new { body = new { plain = "The write-up again." }, verified = false } } },
                new { fragment = "The headlights on the empty road", classification = "accepted", annotations = new[] { new { body = new { plain = "The drive home is the frame for the whole song: each part is another stretch of the same road." }, verified = false } } },
                new { fragment = "Nobody stayed for the encore\nSo we played it to the chairs", classification = "verified", annotations = new[] { new { body = new { plain = "We really did. The hall was empty by then, and it was the best we played all tour." }, verified = true } } },
                new { fragment = "a guess at a line", classification = "unreviewed", annotations = new[] { new { body = new { plain = "Not reviewed yet." }, verified = false } } },
                new { fragment = "[Part III]", classification = "accepted", annotations = new[] { new { body = new { plain = "The last part brings back the first part's tune, slower, as the car pulls in." }, verified = false } } },
            },
        };

        // As the live site sends them (2026-11-04): the question bare, its answer wrapped.
        private static object Questions() => new
        {
            questions = new object[]
            {
                new { body = "What did The Made-Up Band say about \"Made-Up Song\"?", answer = new { body = new { plain = "In an interview the singer said it was written in the van the morning after, \"before we could talk ourselves out of it\"." } } },
                new { body = "Was the song a single?", answer = new { body = new { plain = "Yes, the second single from Made-Up Album, a month before the album." } } },
                new { body = "Is there a live performance of \"Made-Up Song\"?", answer = new { body = new { plain = "https://example.org/x" } } },
                new { body = "Unanswered question?", answer = (object?)null },
            },
        };

        private static object Comments(int page)
        {
            const int total = 57;
            var from = (page - 1) * 50;
            var count = Math.Min(50, total - from);
            var start = new DateTimeOffset(2017, 3, 7, 12, 0, 0, TimeSpan.Zero);
            return new
            {
                comments = Enumerable.Range(from, count).Select(i => new
                {
                    author = new { login = i == 0 ? "first_listener" : $"listener{i}" },
                    created_at = start.AddDays(i * 41).ToUnixTimeSeconds(),
                    votes_total = i == 0 ? 297 : i == 1 ? 1 : 260 - i * 5,
                    body = new { plain = i switch
                    {
                        0 => "One of the best songs ever, in my opinion.",
                        1 => "The three parts shouldn't work together, and somehow they do. The key change into the last part gets me every time, especially live.\nAlso: the strings.",
                        _ => $"Comment number {i + 1}, made up for the test.",
                    } },
                }),
                total_count = total,
                next_page = from + count < total ? page + 1 : (int?)null,
            };
        }
    }
}
