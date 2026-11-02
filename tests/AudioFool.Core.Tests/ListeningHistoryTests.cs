using System.Net;
using System.Text.Json;
using AudioFool.Core.Models;
using AudioFool.Core.Scrobbling;

namespace AudioFool.Core.Tests;

public class ListeningHistoryTests
{
    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement;

    // ------------------------------------------------------------ parsing

    [Fact]
    public void Profile_reads_numbers_sent_as_strings_and_the_join_date()
    {
        var p = ListeningJson.Profile(Json("""
            {"user":{"name":"marcus","playcount":"48210","artist_count":"1734","album_count":"4120",
                     "track_count":"21007","registered":{"unixtime":"1394668800","#text":1394668800}}}
            """));

        Assert.Equal("marcus", p.UserName);
        Assert.Equal(48210, p.Scrobbles);
        Assert.Equal(1734, p.Artists);
        Assert.Equal(4120, p.Albums);
        Assert.Equal(21007, p.Tracks);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1394668800), p.Registered);
    }

    [Fact]
    public void Profile_without_the_newer_counts_leaves_them_null()
    {
        var p = ListeningJson.Profile(Json("""{"user":{"name":"rj","playcount":12,"registered":{"#text":1037793040}}}"""));

        Assert.Equal(12, p.Scrobbles);
        Assert.Null(p.Artists);
        Assert.Null(p.Albums);
        Assert.Null(p.Tracks);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1037793040), p.Registered);
    }

    [Fact]
    public void Profile_missing_is_a_last_fm_error()
    {
        Assert.Throws<LastFmException>(() => ListeningJson.Profile(Json("{}")));
    }

    [Fact]
    public void Top_albums_read_name_artist_and_plays()
    {
        var top = ListeningJson.Top(Json("""
            {"topalbums":{"album":[
                {"name":"Moving Pictures","playcount":"312","artist":{"name":"Rush","mbid":""},"@attr":{"rank":"1"}},
                {"name":"Lateralus","playcount":"98","artist":{"name":"Tool"},"@attr":{"rank":"2"}}],
              "@attr":{"user":"marcus","total":"2"}}}
            """), "topalbums", "album", 10);

        Assert.Equal([new TopEntry("Moving Pictures", "Rush", 312), new TopEntry("Lateralus", "Tool", 98)], top);
    }

    [Fact]
    public void Top_artists_have_no_artist_and_a_list_of_one_is_an_object()
    {
        var top = ListeningJson.Top(Json("""{"topartists":{"artist":{"name":"Rush","playcount":"7"}}}"""),
            "topartists", "artist", 10);

        Assert.Equal([new TopEntry("Rush", null, 7)], top);
    }

    [Fact]
    public void Top_is_cut_to_the_limit_and_an_empty_list_is_empty()
    {
        var many = string.Join(",", Enumerable.Range(1, 12).Select(i =>
            "{\"name\":\"Song " + i + "\",\"playcount\":\"" + (20 - i) + "\",\"artist\":{\"name\":\"A\"}}"));
        Assert.Equal(10, ListeningJson.Top(Json("{\"toptracks\":{\"track\":[" + many + "]}}"), "toptracks", "track", 10).Count);

        Assert.Empty(ListeningJson.Top(Json("""{"toptracks":{"track":[],"@attr":{"total":"0"}}}"""), "toptracks", "track", 10));
        Assert.Empty(ListeningJson.Top(Json("""{"toptracks":""}"""), "toptracks", "track", 10));
    }

    [Fact]
    public void Recent_puts_the_playing_song_first_without_a_time_and_keeps_to_the_limit()
    {
        var recent = ListeningJson.Recent(Json("""
            {"recenttracks":{"track":[
                {"artist":{"#text":"Rush"},"name":"Limelight","album":{"#text":"Moving Pictures"},"@attr":{"nowplaying":"true"}},
                {"artist":{"#text":"Rush"},"name":"Red Barchetta","album":{"#text":"Moving Pictures"},"date":{"uts":"1793000000","#text":"x"}},
                {"artist":{"#text":"Tool"},"name":"Schism","album":{"#text":""},"date":{"uts":"1792990000"}}]}}
            """), 2);

        Assert.Equal(2, recent.Count);
        Assert.Equal(new RecentPlay("Limelight", "Rush", "Moving Pictures", null, true), recent[0]);
        Assert.Equal(new RecentPlay("Red Barchetta", "Rush", "Moving Pictures", DateTimeOffset.FromUnixTimeSeconds(1793000000), false), recent[1]);
    }

    [Fact]
    public void Recent_reads_the_extended_artist_shape_too()
    {
        var recent = ListeningJson.Recent(Json("""
            {"recenttracks":{"track":{"artist":{"name":"Rush","url":"x"},"name":"YYZ","album":{"#text":"Moving Pictures"},"date":{"uts":"1"}}}}
            """), 20);

        Assert.Equal("Rush", Assert.Single(recent).Artist);
    }

    // ------------------------------------------------------------- times

    private static readonly TimeZoneInfo Zone = TimeZoneInfo.CreateCustomTimeZone("Test", TimeSpan.FromHours(-5), "Test", "Test");
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 15, 0, 0, TimeSpan.FromHours(-5));

    private static string Ago(TimeSpan before) =>
        ListeningTime.Ago(new RecentPlay("t", "a", "b", Now - before, false), Now, Zone);

    [Fact]
    public void Times_read_as_minutes_hours_yesterday_then_a_date()
    {
        Assert.Equal("Now playing", ListeningTime.Ago(new RecentPlay("t", "a", "b", null, true), Now, Zone));
        Assert.Equal("Just now", Ago(TimeSpan.FromSeconds(40)));
        Assert.Equal("12 min ago", Ago(TimeSpan.FromMinutes(12)));
        Assert.Equal("1 hour ago", Ago(TimeSpan.FromMinutes(75)));
        Assert.Equal("14 hours ago", Ago(TimeSpan.FromHours(14.5)));
        Assert.Equal("Yesterday", Ago(TimeSpan.FromHours(16)));
        Assert.Equal("Oct 30", Ago(TimeSpan.FromDays(3)));
        Assert.Equal("Dec 30, 2025", Ago(TimeSpan.FromDays(307)));
    }

    [Fact]
    public void The_join_month_is_short_month_and_year()
    {
        Assert.Equal("Mar 2014", ListeningTime.Month(new DateTimeOffset(2014, 3, 13, 12, 0, 0, TimeSpan.Zero), Zone));
    }

    [Fact]
    public void Periods_have_last_fm_names_and_labels()
    {
        Assert.Equal(["7day", "1month", "3month", "6month", "12month", "overall"],
            ListeningPeriods.All.Select(ListeningPeriods.ApiName));
        Assert.Equal(["7 Days", "1 Month", "3 Months", "6 Months", "12 Months", "All Time"],
            ListeningPeriods.All.Select(ListeningPeriods.Label));
    }

    // ----------------------------------------------------------- matching

    private static Track T(string title, string artist, string album, string albumArtist = "", string? path = null) => new()
    {
        FilePath = path ?? $@"D:\Music\{artist}\{album}\{title}.flac",
        Title = title,
        Artist = artist,
        AlbumArtist = albumArtist,
        Album = album,
    };

    [Fact]
    public void Names_match_without_case_accents_punctuation_or_ampersands()
    {
        var song = T("Déjà Vu", "Crosby, Stills, Nash & Young", "Déjà Vu");
        var match = new ListeningMatch([song]);

        Assert.Same(song, match.Artist("crosby stills nash and young"));
        Assert.Same(song, match.Album("Crosby, Stills, Nash and Young", "Deja vu"));
        Assert.Same(song, match.Song("CROSBY STILLS NASH & YOUNG", "deja-vu"));
    }

    [Fact]
    public void A_trailing_bracketed_edition_is_tried_without()
    {
        var song = T("Tom Sawyer (2011 Remaster)", "Rush", "Moving Pictures [Deluxe Edition]");
        var match = new ListeningMatch([song]);

        Assert.Same(song, match.Album("Rush", "Moving Pictures"));
        Assert.Same(song, match.Song("Rush", "Tom Sawyer"));
        Assert.Same(song, match.Album("Rush", "Moving Pictures (Remastered)"));
    }

    [Fact]
    public void An_artist_goes_to_their_own_album_before_a_compilation_they_are_on()
    {
        var guest = T("Closer to the Heart", "Rush", "Now That's What I Call Prog", "Various Artists", @"D:\Music\VA\01.flac");
        var own = T("Limelight", "Rush", "Moving Pictures", "Rush", @"D:\Music\Rush\02.flac");
        var match = new ListeningMatch([guest, own]);

        Assert.Same(own, match.Artist("Rush"));
        Assert.Same(guest, match.Album("Rush", "Now That's What I Call Prog"));
        Assert.Same(guest, match.Album("Various Artists", "Now That's What I Call Prog"));
    }

    [Fact]
    public void Nothing_is_matched_on_a_different_artist_or_an_empty_name()
    {
        var match = new ListeningMatch([T("Schism", "Tool", "Lateralus")]);

        Assert.Null(match.Song("Rush", "Schism"));
        Assert.Null(match.Album("Tool", "Aenima"));
        Assert.Null(match.Artist(""));
        Assert.Null(match.Song("", "Schism"));
        Assert.Null(ListeningMatch.Empty.Artist("Tool"));
    }

    // ---------------------------------------------------------------- API

    private sealed class RecordingHandler(string json) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
        }
    }

    [Fact]
    public async Task Reads_are_unsigned_gets_with_the_user_and_period()
    {
        var handler = new RecordingHandler("""{"topartists":{"artist":[{"name":"Rush","playcount":"3"}]}}""");
        var api = new LastFmApi("key1", "", new HttpClient(handler));

        var top = await api.GetTopArtistsAsync("marc us", ListeningPeriod.ThreeMonths, 10, CancellationToken.None);

        Assert.Equal("Rush", Assert.Single(top).Name);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        var query = request.RequestUri!.Query.TrimStart('?').Split('&').Select(p => p.Split('=', 2))
            .ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1]));
        Assert.Equal("user.getTopArtists", query["method"]);
        Assert.Equal("marc us", query["user"]);
        Assert.Equal("3month", query["period"]);
        Assert.Equal("10", query["limit"]);
        Assert.Equal("key1", query["api_key"]);
        Assert.Equal("json", query["format"]);
        Assert.False(query.ContainsKey("api_sig"));
        Assert.False(query.ContainsKey("sk"));
    }

    [Fact]
    public async Task A_read_error_from_last_fm_is_its_own_code()
    {
        var api = new LastFmApi("key1", "", new HttpClient(new RecordingHandler("""{"error":6,"message":"User not found"}""")));

        var ex = await Assert.ThrowsAsync<LastFmException>(() => api.GetProfileAsync("nobody", CancellationToken.None));
        Assert.Equal(6, ex.Code);
        Assert.Equal("User not found", ex.Message);
    }
}
