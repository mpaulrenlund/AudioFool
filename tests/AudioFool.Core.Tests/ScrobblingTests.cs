using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AudioFool.Core.Models;
using AudioFool.Core.Scrobbling;

namespace AudioFool.Core.Tests;

public class ScrobblingTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);

    private static Track MakeTrack(string title = "Tom Sawyer", string artist = "Rush", string albumArtist = "Rush",
                                   string path = @"D:\Music\Rush\01.flac") =>
        new()
        {
            FilePath = path,
            Title = title,
            Artist = artist,
            AlbumArtist = albumArtist,
            Album = "Moving Pictures",
            TrackNumber = 1,
            Duration = TimeSpan.FromMinutes(4.5),
        };

    private static string Md5(string text) =>
        Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(text)));

    /// <summary>Plays from <paramref name="from"/> to <paramref name="to"/> in 250 ms ticks.</summary>
    private static List<AdvanceResult> Play(PlayTracker tracker, double from, double to, DateTimeOffset start)
    {
        var results = new List<AdvanceResult>();
        for (var s = from; s <= to + 1e-9; s += 0.25)
            results.Add(tracker.Advance(TimeSpan.FromSeconds(s), start + TimeSpan.FromSeconds(s - from)));
        return results;
    }

    // ------------------------------------------------------------ signing

    [Fact]
    public void Signature_sorts_names_skips_format_and_appends_secret()
    {
        var sig = LastFmApi.Sign(
        [
            new("token", "tok"),
            new("method", "auth.getSession"),
            new("format", "json"),
            new("api_key", "key"),
        ], "secret");

        Assert.Equal(Md5("api_keykeymethodauth.getSessiontokentoksecret"), sig);
    }

    [Fact]
    public void Signature_orders_array_parameters_ordinally_and_hashes_utf8()
    {
        var sig = LastFmApi.Sign(
        [
            new("artist[0]", "Björk"),
            new("albumArtist[0]", "Björk"),
            new("album[0]", "Homogenic"),
        ], "s");

        // 'A' (0x41) sorts before '[' (0x5B), so albumArtist[0] precedes album[0].
        Assert.Equal(Md5("albumArtist[0]Björkalbum[0]Homogenicartist[0]Björks"), sig);
    }

    [Fact]
    public void Authorize_url_carries_key_and_token()
    {
        Assert.Equal("https://www.last.fm/api/auth/?api_key=abc&token=t%2B1",
            LastFmApi.AuthorizeUrl(" abc ", "t+1"));
    }

    // ------------------------------------------------------------ entries

    [Fact]
    public void Entry_uses_track_artist_and_sends_album_artist_separately()
    {
        var e = ScrobbleEntry.From(MakeTrack(artist: "Elton John feat. Kiki Dee", albumArtist: "Elton John"),
            TimeSpan.FromSeconds(270.4), T0)!;

        Assert.Equal("Elton John feat. Kiki Dee", e.Artist);
        Assert.Equal("Elton John", e.AlbumArtist);
        Assert.Equal(270, e.DurationSeconds);
        Assert.Equal(T0.ToUnixTimeSeconds(), e.Timestamp);
    }

    [Fact]
    public void Entry_falls_back_to_album_artist()
    {
        Assert.Equal("Rush", ScrobbleEntry.From(MakeTrack(artist: " "), TimeSpan.FromMinutes(4), T0)!.Artist);
    }

    [Theory]
    [InlineData("", "Rush", "Rush")]
    [InlineData("Tom Sawyer", "", "")]
    public void Entry_needs_a_title_and_an_artist(string title, string artist, string albumArtist)
    {
        Assert.Null(ScrobbleEntry.From(MakeTrack(title, artist, albumArtist), TimeSpan.FromMinutes(4), T0));
    }

    // ------------------------------------------------------------ rules

    [Theory]
    [InlineData(20, null)]
    [InlineData(30, null)]
    [InlineData(31, 15.5)]
    [InlineData(300, 150.0)]
    [InlineData(480, 240.0)]
    [InlineData(1200, 240.0)]
    public void Threshold_is_half_or_four_minutes(double seconds, double? expected)
    {
        Assert.Equal(expected, PlayTracker.Threshold(TimeSpan.FromSeconds(seconds))?.TotalSeconds);
    }

    [Fact]
    public void A_play_scrobbles_once_at_the_threshold_with_its_start_time()
    {
        var tracker = new PlayTracker();
        Assert.True(tracker.Start(MakeTrack(), TimeSpan.FromSeconds(200), T0));

        var results = Play(tracker, 0, 199, T0);
        var due = results.Select((r, i) => (r.Due, i)).Where(x => x.Due is not null).ToList();

        Assert.Single(due);
        Assert.Equal(100 * 4, due[0].i); // tick at 100 s
        Assert.Equal(T0.ToUnixTimeSeconds(), due[0].Due!.Timestamp);
    }

    [Fact]
    public void Seeking_forward_is_not_listening()
    {
        var tracker = new PlayTracker();
        tracker.Start(MakeTrack(), TimeSpan.FromSeconds(200), T0);

        Play(tracker, 0, 10, T0);
        var after = Play(tracker, 180, 199, T0.AddSeconds(11));

        Assert.All(after, r => Assert.Null(r.Due));
        Assert.True(tracker.Played < TimeSpan.FromSeconds(31));
    }

    [Fact]
    public void Pausing_adds_nothing()
    {
        var tracker = new PlayTracker();
        tracker.Start(MakeTrack(), TimeSpan.FromSeconds(200), T0);
        Play(tracker, 0, 50, T0);

        for (var i = 0; i < 1000; i++)
            tracker.Advance(TimeSpan.FromSeconds(50), T0.AddSeconds(60 + i));

        Assert.Equal(TimeSpan.FromSeconds(50), tracker.Played);
    }

    [Fact]
    public void A_jump_back_to_the_start_is_a_new_play_and_scrobbles_again()
    {
        var tracker = new PlayTracker();
        tracker.Start(MakeTrack(), TimeSpan.FromSeconds(100), T0);
        var first = Play(tracker, 0, 99.75, T0);

        // Repeat-One: the position simply wraps round.
        var loopAt = T0.AddSeconds(100);
        var wrap = tracker.Advance(TimeSpan.FromSeconds(0.25), loopAt);
        var second = Play(tracker, 0.5, 99.75, loopAt.AddSeconds(0.25));

        Assert.Single(first, r => r.Due is not null);
        Assert.True(wrap.Restarted);
        var again = Assert.Single(second, r => r.Due is not null).Due!;
        Assert.Equal((loopAt - TimeSpan.FromSeconds(0.25)).ToUnixTimeSeconds(), again.Timestamp);
    }

    [Fact]
    public void Seeking_back_mid_track_is_not_a_restart()
    {
        var tracker = new PlayTracker();
        tracker.Start(MakeTrack(), TimeSpan.FromSeconds(200), T0);
        Play(tracker, 0, 60, T0);

        Assert.False(tracker.Advance(TimeSpan.FromSeconds(30), T0.AddSeconds(61)).Restarted);
        Assert.Equal(TimeSpan.FromSeconds(60), tracker.Played);
    }

    [Fact]
    public void The_same_file_starting_again_before_scrobbling_continues_the_play()
    {
        var tracker = new PlayTracker();
        tracker.Start(MakeTrack(), TimeSpan.FromSeconds(200), T0);
        Play(tracker, 0, 60, T0);

        // Switching output mode reopens the current track from the top.
        Assert.False(tracker.Start(MakeTrack(), TimeSpan.FromSeconds(200), T0.AddSeconds(61)));
        var due = Play(tracker, 0, 40, T0.AddSeconds(61)).Single(r => r.Due is not null).Due!;

        Assert.Equal(T0.ToUnixTimeSeconds(), due.Timestamp);
    }

    [Fact]
    public void Another_track_starts_a_new_play()
    {
        var tracker = new PlayTracker();
        tracker.Start(MakeTrack(), TimeSpan.FromSeconds(200), T0);
        Play(tracker, 0, 60, T0);

        Assert.True(tracker.Start(MakeTrack(path: @"D:\Music\Rush\02.flac"), TimeSpan.FromSeconds(200), T0.AddSeconds(61)));
        Assert.Equal(TimeSpan.Zero, tracker.Played);
    }

    [Fact]
    public void Short_tracks_never_scrobble()
    {
        var tracker = new PlayTracker();
        tracker.Start(MakeTrack(), TimeSpan.FromSeconds(30), T0);

        Assert.All(Play(tracker, 0, 30, T0), r => Assert.Null(r.Due));
    }

    [Fact]
    public void After_stop_nothing_scrobbles()
    {
        var tracker = new PlayTracker();
        tracker.Start(MakeTrack(), TimeSpan.FromSeconds(200), T0);
        Play(tracker, 0, 50, T0);
        tracker.Stop();

        Assert.All(Play(tracker, 50, 150, T0), r => Assert.Null(r.Due));
    }

    // ------------------------------------------------------------ responses

    [Fact]
    public void Batch_outcomes_read_each_ignored_code()
    {
        using var doc = JsonDocument.Parse("""
            {"scrobbles":{"@attr":{"accepted":1,"ignored":2},"scrobble":[
              {"ignoredMessage":{"code":"0","#text":""}},
              {"ignoredMessage":{"code":"3","#text":"Timestamp too old"}},
              {"ignoredMessage":{"code":"5","#text":"Daily limit"}}]}}
            """);

        Assert.Equal([ScrobbleOutcome.Accepted, ScrobbleOutcome.Ignored, ScrobbleOutcome.Deferred],
            LastFmApi.ParseScrobbleOutcomes(doc.RootElement, 3));
    }

    [Fact]
    public void A_batch_of_one_comes_back_as_an_object()
    {
        using var doc = JsonDocument.Parse("""
            {"scrobbles":{"@attr":{"accepted":0,"ignored":1},"scrobble":
              {"ignoredMessage":{"code":"1","#text":"Artist ignored"}}}}
            """);

        Assert.Equal([ScrobbleOutcome.Ignored], LastFmApi.ParseScrobbleOutcomes(doc.RootElement, 1));
    }

    // ------------------------------------------------------------ queue

    private static string TempQueuePath() =>
        Path.Combine(Path.GetTempPath(), "AudioFoolTests", Guid.NewGuid().ToString("N"), "scrobbles.json");

    private static ScrobbleEntry Entry(string track, DateTimeOffset at) =>
        new() { Artist = "Rush", Track = track, Timestamp = at.ToUnixTimeSeconds() };

    [Fact]
    public void Queue_survives_a_reload_and_drops_what_is_too_old()
    {
        var path = TempQueuePath();
        var q = ScrobbleQueue.Load(path, T0);
        q.Add(Entry("old", T0 - TimeSpan.FromDays(15)));
        q.Add(Entry("recent", T0 - TimeSpan.FromDays(2)));

        var reloaded = ScrobbleQueue.Load(path, T0);

        Assert.Equal(["recent"], reloaded.Peek(50).Select(e => e.Track));
        Assert.Equal(1, ScrobbleQueue.Load(path, T0).Count);
    }

    [Fact]
    public void Queue_removes_only_what_was_sent()
    {
        var q = ScrobbleQueue.Load(TempQueuePath(), T0);
        q.Add(Entry("a", T0));
        var batch = q.Peek(50);
        q.Add(Entry("b", T0));

        q.Remove(batch);

        Assert.Equal(["b"], q.Peek(50).Select(e => e.Track));
    }

    [Fact]
    public void An_unreadable_queue_file_is_an_empty_queue()
    {
        var path = TempQueuePath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ not json");

        Assert.Equal(0, ScrobbleQueue.Load(path, T0).Count);
    }

    // ------------------------------------------------------------ scrobbler

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <summary>Answers Last.fm calls by method name and records every form it was sent.</summary>
    private sealed class FakeLastFm : HttpMessageHandler
    {
        public List<Dictionary<string, string>> Calls { get; } = [];
        public Func<Dictionary<string, string>, (HttpStatusCode, string)> Answer { get; set; } =
            _ => (HttpStatusCode.OK, "{}");

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = await request.Content!.ReadAsStringAsync(ct);
            var form = body.Split('&').Select(p => p.Split('=', 2))
                .ToDictionary(p => WebUtility.UrlDecode(p[0]), p => WebUtility.UrlDecode(p[1]));
            lock (Calls)
                Calls.Add(form);

            var (status, json) = Answer(form);
            return new HttpResponseMessage(status) { Content = new StringContent(json) };
        }

        public List<Dictionary<string, string>> Of(string method)
        {
            lock (Calls)
                return Calls.Where(c => c["method"] == method).ToList();
        }
    }

    private static async Task Until(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
            await Task.Delay(10);
        Assert.True(condition());
    }

    private static (LastFmScrobbler, FakeLastFm, ManualClock) Connected()
    {
        var fake = new FakeLastFm();
        var http = new HttpClient(fake);
        var clock = new ManualClock(T0);
        var scrobbler = new LastFmScrobbler(ScrobbleQueue.Load(TempQueuePath(), T0), clock,
            (key, secret) => new LastFmApi(key, secret, http));
        scrobbler.Connect("key", "secret", new LastFmSession("marcus", "sk1"));
        return (scrobbler, fake, clock);
    }

    private static void PlayThrough(LastFmScrobbler scrobbler, ManualClock clock, double seconds, double from = 0.25)
    {
        for (var s = from; s <= seconds; s += 0.25)
        {
            clock.Now = T0.AddSeconds(s);
            scrobbler.Advance(TimeSpan.FromSeconds(s));
        }
    }

    [Fact]
    public async Task A_play_sends_now_playing_then_one_signed_scrobble()
    {
        var (scrobbler, fake, clock) = Connected();

        scrobbler.TrackStarted(MakeTrack(), TimeSpan.FromSeconds(200));
        PlayThrough(scrobbler, clock, 150);

        await Until(() => fake.Of("track.scrobble").Count == 1 && scrobbler.Pending == 0);
        var nowPlaying = Assert.Single(fake.Of("track.updateNowPlaying"));
        Assert.Equal("Tom Sawyer", nowPlaying["track"]);

        var sent = fake.Of("track.scrobble")[0];
        Assert.Equal("Rush", sent["artist[0]"]);
        Assert.Equal("Rush", sent["albumArtist[0]"]);
        Assert.Equal(T0.ToUnixTimeSeconds().ToString(), sent["timestamp[0]"]);
        Assert.Equal("sk1", sent["sk"]);

        var signed = sent.Where(p => p.Key is not ("api_sig" or "format")).ToList();
        Assert.Equal(LastFmApi.Sign(signed, "secret"), sent["api_sig"]);
    }

    [Fact]
    public async Task A_failed_send_keeps_the_scrobble_and_retries_after_the_backoff()
    {
        var (scrobbler, fake, clock) = Connected();
        fake.Answer = f => f["method"] == "track.scrobble"
            ? (HttpStatusCode.ServiceUnavailable, """{"error":16,"message":"Temporarily unavailable"}""")
            : (HttpStatusCode.OK, "{}");

        scrobbler.TrackStarted(MakeTrack(), TimeSpan.FromSeconds(200));
        PlayThrough(scrobbler, clock, 101);
        await Until(() => scrobbler.LastError is not null);

        // Still inside the one-minute backoff: no second attempt.
        PlayThrough(scrobbler, clock, 130, from: 101.25);
        await Task.Delay(50);
        Assert.Single(fake.Of("track.scrobble"));
        Assert.Equal(1, scrobbler.Pending);

        fake.Answer = _ => (HttpStatusCode.OK, "{}");
        clock.Now = T0.AddMinutes(3);
        scrobbler.Advance(TimeSpan.FromSeconds(130.25));

        await Until(() => scrobbler.Pending == 0);
        Assert.Null(scrobbler.LastError);
    }

    [Fact]
    public async Task A_rejected_session_stops_sending_and_keeps_the_queue()
    {
        var (scrobbler, fake, clock) = Connected();
        fake.Answer = f => f["method"] == "track.scrobble"
            ? (HttpStatusCode.Forbidden, """{"error":9,"message":"Invalid session key"}""")
            : (HttpStatusCode.OK, "{}");
        var failures = new List<string>();
        scrobbler.AuthFailed += (_, m) => failures.Add(m);

        scrobbler.TrackStarted(MakeTrack(), TimeSpan.FromSeconds(200));
        PlayThrough(scrobbler, clock, 101);
        await Until(() => scrobbler.NeedsReconnect);

        clock.Now = T0.AddHours(2);
        scrobbler.Advance(TimeSpan.FromSeconds(101.25));
        await Task.Delay(50);

        Assert.Single(fake.Of("track.scrobble"));
        Assert.Single(failures);
        Assert.Equal(1, scrobbler.Pending);
    }

    [Fact]
    public async Task Ticks_from_the_next_track_before_its_change_event_are_skipped()
    {
        var (scrobbler, fake, clock) = Connected();
        var first = MakeTrack();
        var next = MakeTrack(title: "Red Barchetta", path: @"D:\Music\Rush\02.flac");

        scrobbler.TrackStarted(first, TimeSpan.FromSeconds(200));
        PlayThrough(scrobbler, clock, 199.75);
        await Until(() => fake.Of("track.scrobble").Count == 1);

        // Gapless handover: the engine already plays the next track, whose
        // position starts again near zero, but TrackChanged has not arrived.
        scrobbler.Advance(TimeSpan.FromSeconds(0.25), next);
        scrobbler.Advance(TimeSpan.FromSeconds(0.5), next);
        await Task.Delay(50);

        Assert.Single(fake.Of("track.updateNowPlaying"));
    }

    [Fact]
    public async Task State_follows_connection_switch_failures_and_rejection()
    {
        var unconnected = new LastFmScrobbler(ScrobbleQueue.Load(TempQueuePath(), T0));
        Assert.Equal(ScrobblerState.Disconnected, unconnected.State);

        var (scrobbler, fake, clock) = Connected();
        var changes = 0;
        scrobbler.StatusChanged += (_, _) => changes++;
        Assert.Equal(ScrobblerState.Scrobbling, scrobbler.State);

        scrobbler.Enabled = false;
        Assert.Equal(ScrobblerState.Off, scrobbler.State);
        Assert.Equal(1, changes);
        scrobbler.Enabled = true;

        fake.Answer = f => f["method"] == "track.scrobble"
            ? (HttpStatusCode.ServiceUnavailable, """{"error":11,"message":"Service offline"}""")
            : (HttpStatusCode.OK, "{}");
        scrobbler.TrackStarted(MakeTrack(), TimeSpan.FromSeconds(200));
        PlayThrough(scrobbler, clock, 101);
        await Until(() => scrobbler.State == ScrobblerState.Failing);

        // The failed flush may still be unwinding, and a flush in progress makes
        // another a no-op, so keep asking until one runs.
        fake.Answer = _ => (HttpStatusCode.OK, "{}");
        await Until(() =>
        {
            _ = scrobbler.FlushAsync();
            return scrobbler.State == ScrobblerState.Scrobbling;
        });

        fake.Answer = f => f["method"] == "track.scrobble"
            ? (HttpStatusCode.Forbidden, """{"error":9,"message":"Invalid session key"}""")
            : (HttpStatusCode.OK, "{}");
        scrobbler.TrackStarted(MakeTrack(path: @"D:\Music\Rush\02.flac"), TimeSpan.FromSeconds(200));
        PlayThrough(scrobbler, clock, 101);
        await Until(() => scrobbler.State == ScrobblerState.NeedsReconnect);
    }

    [Fact]
    public async Task Switched_off_nothing_is_sent_or_queued()
    {
        var (scrobbler, fake, clock) = Connected();
        scrobbler.Enabled = false;

        scrobbler.TrackStarted(MakeTrack(), TimeSpan.FromSeconds(200));
        PlayThrough(scrobbler, clock, 150);
        await Task.Delay(50);

        Assert.Empty(fake.Calls);
        Assert.Equal(0, scrobbler.Pending);
    }

    [Fact]
    public async Task The_daily_limit_keeps_the_scrobble_queued()
    {
        var (scrobbler, fake, clock) = Connected();
        fake.Answer = f => f["method"] == "track.scrobble"
            ? (HttpStatusCode.OK, """{"scrobbles":{"scrobble":{"ignoredMessage":{"code":"5","#text":"Daily limit"}}}}""")
            : (HttpStatusCode.OK, "{}");

        scrobbler.TrackStarted(MakeTrack(), TimeSpan.FromSeconds(200));
        PlayThrough(scrobbler, clock, 101);
        await Until(() => scrobbler.LastSentAt is not null);

        Assert.Equal(1, scrobbler.Pending);
    }
}
