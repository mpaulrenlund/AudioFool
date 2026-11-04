using System.Net;
using System.Text;
using System.Text.Json;
using AudioFool.Core.Genius;

namespace AudioFool.Core.Tests;

/// <summary>
/// The Genius window's Core: matching a library song to a page, reading
/// genius.com's answers, and the lookup that ties them together. The answers
/// below are made up in Genius's shape; none of Genius's own text is copied.
/// </summary>
public class GeniusTests
{
    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement;

    // --------------------------------------------------------- title cleaning

    [Theory]
    [InlineData("Paranoid Android (Remastered)", "Paranoid Android")]
    [InlineData("Paranoid Android - 2011 Remaster", "Paranoid Android")]
    [InlineData("Paranoid Android - Remastered 2009", "Paranoid Android")]
    [InlineData("Song [Live at Wembley]", "Song")]
    [InlineData("Song (feat. Somebody)", "Song")]
    [InlineData("Song feat. Somebody", "Song")]
    [InlineData("Song ft. Somebody Else", "Song")]
    [InlineData("Song (Radio Edit) [2014 Remaster]", "Song")]
    [InlineData("Song - Live at X - 2001", "Song")]
    [InlineData("Song (Mono Version)", "Song")]
    [InlineData("Song (1997)", "Song")]
    [InlineData("Seishun Line (from Ookiku Furikabutte)", "Seishun Line")]
    [InlineData("Let It Go - From \"Frozen\"", "Let It Go")]
    [InlineData("Song (Part 2)", "Song (Part 2)")]
    [InlineData("(Don't Fear) The Reaper", "(Don't Fear) The Reaper")]
    [InlineData("Track 1 - Intro", "Track 1 - Intro")]
    [InlineData("Re-Arranged", "Re-Arranged")]
    [InlineData("(Live)", "(Live)")]
    [InlineData("  Plain  ", "Plain")]
    public void CleanTitle_drops_trailing_extras_only(string title, string expected) =>
        Assert.Equal(expected, GeniusMatch.CleanTitle(title));

    [Theory]
    [InlineData("Artist feat. Guest", "Artist")]
    [InlineData("Artist ft. Guest", "Artist")]
    [InlineData("Simon & Garfunkel", "Simon & Garfunkel")]
    [InlineData("  Radiohead ", "Radiohead")]
    public void SearchArtist_is_the_first_artist_before_any_featuring(string artist, string expected) =>
        Assert.Equal(expected, GeniusMatch.SearchArtist(artist));

    // ---------------------------------------------------------------- picking

    private static GeniusHit Hit(long id, string artist, string title) => new(id, title, artist, $"https://genius.com/{id}");

    [Fact]
    public void Pick_takes_the_song_and_skips_Genius_translation_pages()
    {
        GeniusHit[] hits =
        [
            Hit(1, "Genius Türkçe Çeviriler", "Radiohead - Paranoid Android (Türkçe Çeviri)"),
            Hit(2, "Radiohead", "Paranoid Android"),
        ];

        Assert.Equal(2, GeniusMatch.Pick(hits, "Radiohead", "Radiohead", "Paranoid Android (Remastered)")?.Id);
    }

    [Fact]
    public void Pick_needs_both_the_artist_and_the_title()
    {
        GeniusHit[] hits = [Hit(1, "Radiohead", "Karma Police"), Hit(2, "Some Cover Band", "Paranoid Android")];

        Assert.Null(GeniusMatch.Pick(hits, "Radiohead", "", "Paranoid Android"));
    }

    [Theory]
    [InlineData("Beatles", "The Beatles")]
    [InlineData("The Beatles", "Beatles")]
    [InlineData("Beyoncé & Jay-Z", "Beyonce and JAY-Z")]
    [InlineData("Artist feat. Guest", "Artist")]
    [InlineData("Artist", "Artist & Partner")]
    [InlineData("Artist x Partner", "Partner")]
    public void Pick_compares_artists_loosely(string ours, string theirs)
    {
        GeniusHit[] hits = [Hit(7, theirs, "Some Song")];

        Assert.Equal(7, GeniusMatch.Pick(hits, ours, "", "Some Song")?.Id);
    }

    [Fact]
    public void Pick_compares_titles_without_extras_on_either_side()
    {
        GeniusHit[] hits = [Hit(3, "Artist", "Some Song (Live)")];

        Assert.Equal(3, GeniusMatch.Pick(hits, "Artist", "", "Some Song - 2011 Remaster")?.Id);
    }

    [Fact]
    public void Pick_keeps_parts_apart()
    {
        GeniusHit[] hits = [Hit(1, "Artist", "Suite (Part 1)"), Hit(2, "Artist", "Suite (Part 2)")];

        Assert.Equal(2, GeniusMatch.Pick(hits, "Artist", "", "Suite (Part 2)")?.Id);
    }

    [Fact]
    public void Pick_falls_back_on_the_album_artist()
    {
        GeniusHit[] hits = [Hit(4, "Band", "Song")];

        Assert.Equal(4, GeniusMatch.Pick(hits, "Band's Singer", "Band", "Song")?.Id);
    }

    [Fact]
    public void Pick_without_a_title_or_artist_finds_nothing()
    {
        GeniusHit[] hits = [Hit(1, "Artist", "Song")];

        Assert.Null(GeniusMatch.Pick(hits, "Artist", "", ""));
        Assert.Null(GeniusMatch.Pick(hits, "", "", "Song"));
    }

    [Fact]
    public void Suggestions_leave_out_Genius_pages_and_repeats_and_stop_at_five()
    {
        GeniusHit[] hits =
        [
            Hit(1, "A", "One"), Hit(2, "Genius English Translations", "x"), Hit(1, "A", "One"),
            Hit(3, "B", "Two"), Hit(4, "C", "Three"), Hit(5, "D", "Four"), Hit(6, "E", "Five"), Hit(7, "F", "Six"),
        ];

        Assert.Equal([1L, 3, 4, 5, 6], GeniusMatch.Suggestions(hits).Select(h => h.Id));
    }

    // ---------------------------------------------------------------- parsing

    [Fact]
    public void SearchHits_reads_songs_with_their_primary_artist()
    {
        var hits = GeniusJson.SearchHits(Json("""
            {"sections":[{"type":"song","hits":[
              {"type":"song","result":{"_type":"song","id":11,"title":"First","artist_names":"Band (Ft. Guest)",
                "primary_artist":{"name":"Band"},"url":"https://genius.com/Band-first-lyrics"}},
              {"type":"song","result":{"_type":"song","id":12,"title":"Second","artist_names":"Solo","url":"u"}},
              {"type":"song","result":{"_type":"song","title":"No id"}}
            ]}]}
            """));

        Assert.Equal(2, hits.Count);
        Assert.Equal(new GeniusHit(11, "First", "Band", "https://genius.com/Band-first-lyrics"), hits[0]);
        Assert.Equal("Solo", hits[1].Artist);
    }

    private const string SongJson = """
        {"song":{"id":42,"title":"Made-Up Song","url":"https://genius.com/made-up",
          "primary_artist":{"name":"Made-Up Band"},
          "description":{"plain":"First paragraph.\n\nSecond paragraph."},
          "album":{"name":"Made-Up Album"},
          "release_date_for_display":"May 1, 2001",
          "recording_location":"A Barn",
          "writer_artists":[{"name":"Writer One"},{"name":"Writer Two"}],
          "producer_artists":[{"name":"Producer"}],
          "custom_performances":[{"label":"Mastered At","artists":[{"name":"Studio"}]},{"label":"Empty","artists":[]}],
          "song_relationships":[
            {"relationship_type":"samples","songs":[{"id":1,"title":"Old Song","primary_artist":{"name":"Old Band"},"url":"o"}]},
            {"relationship_type":"sampled_in","songs":[]},
            {"relationship_type":"covered_by","songs":[{"id":2,"title":"Made-Up Song","primary_artist":{"name":"Cover Band"},"url":"c"}]},
            {"relationship_type":"translations","songs":[{"id":3,"title":"T","primary_artist":{"name":"Genius Translations"},"url":"t"}]},
            {"relationship_type":"brand_new_kind","songs":[{"id":4,"title":"N","primary_artist":{"name":"New"},"url":"n"}]}
          ]}}
        """;

    [Fact]
    public void Song_reads_the_write_up_album_and_credits_in_order()
    {
        var s = GeniusJson.Song(Json(SongJson));

        Assert.Equal(42, s.Id);
        Assert.Equal("Made-Up Song", s.Title);
        Assert.Equal("Made-Up Band", s.Artist);
        Assert.Equal("First paragraph.\n\nSecond paragraph.", s.About);
        Assert.Equal("Made-Up Album", s.Album);
        Assert.Equal("May 1, 2001", s.ReleaseDate);
        Assert.Equal(["Written by", "Produced by", "Released", "Recorded at", "Mastered at"], s.Credits.Select(c => c.Label));
        Assert.Equal(["Writer One", "Writer Two"], s.Credits[0].Names);
    }

    [Theory]
    [InlineData("Mastered At", "Mastered at")]
    [InlineData("Overdubbed At", "Overdubbed at")]
    [InlineData("Video Director", "Video Director")]
    [InlineData("A&R", "A&R")]
    [InlineData("At Desk For Strings", "At Desk for Strings")]
    public void CreditLabel_lowers_only_the_small_words_after_the_first(string label, string expected) =>
        Assert.Equal(expected, GeniusJson.CreditLabel(label));

    [Fact]
    public void Song_connections_leave_out_empty_kinds_and_translations()
    {
        var s = GeniusJson.Song(Json(SongJson));

        Assert.Equal(["Samples", "Covered by", "Brand new kind"], s.Connections.Select(c => c.Label));
        Assert.Equal("Old Band", s.Connections[0].Songs[0].Artist);
    }

    [Theory]
    [InlineData("""{"plain":"?"}""")]
    [InlineData("""{"plain":""}""")]
    [InlineData("null")]
    public void Song_without_a_write_up_has_no_About(string description)
    {
        var s = GeniusJson.Song(Json("""{"song":{"id":1,"title":"T","primary_artist":{"name":"A"},"description":""" + description + "}}"));

        Assert.Null(s.About);
        Assert.Empty(s.Credits);
        Assert.Empty(s.Connections);
    }

    [Fact]
    public void Annotations_skip_the_write_up_and_unreviewed_ones()
    {
        var notes = GeniusJson.Annotations(Json("""
            {"referents":[
              {"fragment":"Made-Up Song","is_description":true,"classification":"accepted","annotations":[{"body":{"plain":"About text"}}]},
              {"fragment":"a first line","classification":"accepted","annotations":[{"body":{"plain":"What it means."},"verified":false}]},
              {"fragment":"a guess","classification":"unreviewed","annotations":[{"body":{"plain":"Maybe."}}]},
              {"fragment":"the artist's line","classification":"verified","annotations":[{"body":{"plain":"I meant this."},"verified":true}]},
              {"fragment":"no body","classification":"accepted","annotations":[{"body":{"plain":""}}]}
            ]}
            """));

        Assert.Equal(2, notes.Count);
        Assert.Equal(new GeniusAnnotation("a first line", "What it means.", false), notes[0]);
        Assert.True(notes[1].Verified);
    }

    [Fact]
    public void Answers_keep_answered_questions_only()
    {
        var answers = GeniusJson.Answers(Json("""
            {"questions":[
              {"state":"pinned","body":{"plain":"Who wrote it?"},"answer":{"body":{"plain":"The band."}}},
              {"state":"unanswered","body":{"plain":"Why?"},"answer":null},
              {"state":"answered","body":{"plain":"When?"},"answer":{"body":{"plain":""}}}
            ]}
            """));

        Assert.Equal([new GeniusAnswer("Who wrote it?", "The band.")], answers);
    }

    [Fact]
    public void Answers_take_a_question_sent_as_a_bare_string()
    {
        // genius.com sends the question's text bare and only the answer wrapped.
        var answers = GeniusJson.Answers(Json("""
            {"questions":[{"state":"pinned","body":"What did the band say about it?","answer":{"body":{"plain":"That it was quick."}}}]}
            """));

        Assert.Equal([new GeniusAnswer("What did the band say about it?", "That it was quick.")], answers);
    }

    [Fact]
    public void Comments_read_author_date_votes_and_the_next_page()
    {
        var page = GeniusJson.Comments(Json("""
            {"comments":[
              {"author":{"login":"listener"},"created_at":1488906011,"votes_total":297,"body":{"plain":"Great song."}},
              {"author":null,"anonymous_author":{"name":"Someone"},"votes_total":-6,"body":{"plain":"Not for me."}},
              {"author":{"login":"blank"},"votes_total":1,"body":{"plain":""}}
            ],"total_count":134,"next_page":2}
            """));

        Assert.Equal(2, page.Comments.Count);
        Assert.Equal(new GeniusComment("listener", DateTimeOffset.FromUnixTimeSeconds(1488906011), 297, "Great song."), page.Comments[0]);
        Assert.Equal(new GeniusComment("Someone", null, -6, "Not for me."), page.Comments[1]);
        Assert.Equal(134, page.Total);
        Assert.Equal(2, page.NextPage);
    }

    [Fact]
    public void Comments_last_page_has_no_next_page()
    {
        var page = GeniusJson.Comments(Json("""{"comments":[],"total_count":0,"next_page":null}"""));

        Assert.Empty(page.Comments);
        Assert.Null(page.NextPage);
    }

    // ----------------------------------------------------------- the client

    /// <summary>Answers each request from a function of its path and query; records what was asked.</summary>
    private sealed class FakeGenius(Func<string, (HttpStatusCode Status, string Body)> answer) : HttpMessageHandler
    {
        public List<string> Asked { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.PathAndQuery;
            lock (Asked)
                Asked.Add(path);
            var (status, body) = answer(path);
            if (status == 0)
                throw new HttpRequestException("No such host is known.");
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    private static string Ok(string response) => $$"""{"meta":{"status":200},"response":{{response}}}""";

    [Fact]
    public async Task Client_turns_a_block_page_into_a_GeniusException()
    {
        var client = new GeniusClient(new HttpClient(new FakeGenius(_ => (HttpStatusCode.Forbidden, "<html>blocked</html>"))));

        var ex = await Assert.ThrowsAsync<GeniusException>(() => client.GetSongAsync(1, CancellationToken.None));
        Assert.Equal("Genius answered HTTP 403 without a page AudioFool can read.", ex.Message);
    }

    [Fact]
    public async Task Client_reports_Genius_own_error_message()
    {
        var client = new GeniusClient(new HttpClient(new FakeGenius(_ =>
            (HttpStatusCode.NotFound, """{"meta":{"status":404,"message":"Not found."}}"""))));

        var ex = await Assert.ThrowsAsync<GeniusException>(() => client.GetSongAsync(1, CancellationToken.None));
        Assert.Equal("Genius answered HTTP 404: Not found", ex.Message);
    }

    [Fact]
    public async Task Client_says_when_Genius_cant_be_reached()
    {
        var client = new GeniusClient(new HttpClient(new FakeGenius(_ => (0, ""))));

        var ex = await Assert.ThrowsAsync<GeniusException>(() => client.SearchAsync("x", 5, CancellationToken.None));
        Assert.StartsWith("Couldn't reach Genius", ex.Message);
    }

    [Fact]
    public async Task Client_asks_the_web_API_in_plain_text()
    {
        var fake = new FakeGenius(_ => (HttpStatusCode.OK, Ok("""{"comments":[],"total_count":0}""")));
        var client = new GeniusClient(new HttpClient(fake));

        await client.GetCommentsAsync(78831, 2, CancellationToken.None);

        Assert.Equal("/api/songs/78831/comments?per_page=50&page=2&text_format=plain", Assert.Single(fake.Asked));
    }

    // ----------------------------------------------------------- the lookup

    private static string SearchAnswer(params (long Id, string Artist, string Title)[] hits) =>
        Ok(JsonSerializer.Serialize(new
        {
            sections = new[]
            {
                new
                {
                    type = "song",
                    hits = hits.Select(h => new
                    {
                        type = "song",
                        result = new { _type = "song", id = h.Id, title = h.Title, primary_artist = new { name = h.Artist }, url = $"u{h.Id}" },
                    }),
                },
            },
        }));

    [Fact]
    public async Task Find_searches_artist_and_clean_title_first_and_keeps_the_answer()
    {
        var fake = new FakeGenius(_ => (HttpStatusCode.OK, SearchAnswer((5, "Genius Farsi Translations", "Radiohead - Paranoid Android"), (6, "Radiohead", "Paranoid Android"))));
        var lookup = new GeniusLookup(new GeniusClient(new HttpClient(fake)));

        var find = await lookup.FindAsync("Radiohead", "Radiohead", "Paranoid Android (Remastered)", CancellationToken.None);
        var again = await lookup.FindAsync("Radiohead", "Radiohead", "Paranoid Android (Remastered)", CancellationToken.None);

        Assert.Equal(6, find.Match?.Id);
        Assert.Same(find, again);
        Assert.Equal("/api/search/song?per_page=10&q=Radiohead%20Paranoid%20Android", Assert.Single(fake.Asked));
    }

    [Fact]
    public async Task Find_tries_the_title_alone_when_the_artist_search_misses()
    {
        var fake = new FakeGenius(path => (HttpStatusCode.OK, path.Contains("q=Band%20")
            ? SearchAnswer((1, "Other", "Elsewhere"))
            : SearchAnswer((2, "Band", "Song"))));
        var lookup = new GeniusLookup(new GeniusClient(new HttpClient(fake)));

        var find = await lookup.FindAsync("Band", "", "Song", CancellationToken.None);

        Assert.Equal(2, find.Match?.Id);
        Assert.Equal(2, fake.Asked.Count);
    }

    [Fact]
    public async Task Find_with_no_match_offers_the_nearest_results()
    {
        var fake = new FakeGenius(path => (HttpStatusCode.OK, path.Contains("q=Band%20")
            ? SearchAnswer((1, "Other", "Song"), (2, "Genius Romanizations", "Song"))
            : SearchAnswer((1, "Other", "Song"), (3, "Third", "Song"))));
        var lookup = new GeniusLookup(new GeniusClient(new HttpClient(fake)));

        var find = await lookup.FindAsync("Band", "", "Song", CancellationToken.None);

        Assert.Null(find.Match);
        Assert.Equal([1L, 3], find.Suggestions.Select(h => h.Id));
    }

    [Fact]
    public async Task Find_without_a_title_asks_nothing()
    {
        var fake = new FakeGenius(_ => (HttpStatusCode.OK, SearchAnswer()));
        var lookup = new GeniusLookup(new GeniusClient(new HttpClient(fake)));

        var find = await lookup.FindAsync("Band", "", " ", CancellationToken.None);

        Assert.Null(find.Match);
        Assert.Empty(find.Suggestions);
        Assert.Empty(fake.Asked);
    }

    [Fact]
    public async Task Find_that_fails_is_not_kept()
    {
        var offline = true;
        var fake = new FakeGenius(_ => offline ? (0, "") : (HttpStatusCode.OK, SearchAnswer((1, "Band", "Song"))));
        var lookup = new GeniusLookup(new GeniusClient(new HttpClient(fake)));

        await Assert.ThrowsAsync<GeniusException>(() => lookup.FindAsync("Band", "", "Song", CancellationToken.None));
        offline = false;
        var find = await lookup.FindAsync("Band", "", "Song", CancellationToken.None);

        Assert.Equal(1, find.Match?.Id);
    }

    private static (HttpStatusCode, string) PageAnswer(string path, bool commentsFail = false)
    {
        if (path.StartsWith("/api/songs/42/comments"))
            return commentsFail ? (HttpStatusCode.InternalServerError, "oops") : (HttpStatusCode.OK, Ok("""{"comments":[{"author":{"login":"a"},"votes_total":3,"body":{"plain":"Nice."}}],"total_count":1}"""));
        if (path.StartsWith("/api/songs/42"))
            return (HttpStatusCode.OK, Ok(SongJson));
        if (path.StartsWith("/api/questions"))
            return (HttpStatusCode.OK, Ok("""{"questions":[{"body":{"plain":"Q?"},"answer":{"body":{"plain":"A."}}}]}"""));
        return (HttpStatusCode.OK, Ok("""{"referents":[{"fragment":"line","annotations":[{"body":{"plain":"Means."}}]}]}"""));
    }

    [Fact]
    public async Task Load_fetches_all_four_and_keeps_a_whole_page()
    {
        var fake = new FakeGenius(p => PageAnswer(p));
        var lookup = new GeniusLookup(new GeniusClient(new HttpClient(fake)));

        var first = await lookup.LoadAsync(42, CancellationToken.None);
        var again = await lookup.LoadAsync(42, CancellationToken.None);

        Assert.Empty(first.Missing);
        Assert.Equal("Made-Up Song", first.Page.Song.Title);
        Assert.Single(first.Page.Answers);
        Assert.Single(first.Page.Annotations);
        Assert.Single(first.Page.Comments.Comments);
        Assert.Same(first.Page, again.Page);
        Assert.Equal(4, fake.Asked.Count);
    }

    [Fact]
    public async Task Load_without_comments_names_them_and_asks_again_next_time()
    {
        var fake = new FakeGenius(p => PageAnswer(p, commentsFail: true));
        var lookup = new GeniusLookup(new GeniusClient(new HttpClient(fake)));

        var first = await lookup.LoadAsync(42, CancellationToken.None);
        await lookup.LoadAsync(42, CancellationToken.None);

        Assert.Equal(["comments"], first.Missing);
        Assert.Empty(first.Page.Comments.Comments);
        Assert.Single(first.Page.Answers);
        Assert.Equal(8, fake.Asked.Count);
    }

    [Fact]
    public async Task Load_fails_when_the_song_itself_fails()
    {
        var fake = new FakeGenius(p => p.StartsWith("/api/songs/42?") ? (HttpStatusCode.NotFound, """{"meta":{"status":404,"message":"Not found"}}""") : PageAnswer(p));
        var lookup = new GeniusLookup(new GeniusClient(new HttpClient(fake)));

        await Assert.ThrowsAsync<GeniusException>(() => lookup.LoadAsync(42, CancellationToken.None));
    }
}
