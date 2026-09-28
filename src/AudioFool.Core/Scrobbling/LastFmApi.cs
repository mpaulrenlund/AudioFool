using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AudioFool.Core.Scrobbling;

/// <summary>An authorised Last.fm session: the key every write call is signed with.</summary>
public sealed record LastFmSession(string UserName, string Key);

/// <summary>
/// A Last.fm API error. <see cref="Code"/> is Last.fm's own error number, or 0
/// for a network fault or an answer that was not Last.fm's JSON.
/// </summary>
public sealed class LastFmException(int code, string message) : Exception(message)
{
    public int Code { get; } = code;

    /// <summary>
    /// Worth sending again later: the network, a server error, Last.fm saying it
    /// is offline or busy, or rate limiting.
    /// </summary>
    public bool IsTransient => Code is 0 or 11 or 16 or 29;

    /// <summary>
    /// The credentials themselves are wrong - the session was revoked, or the
    /// key or secret is bad - so retrying cannot help until the user reconnects.
    /// </summary>
    public bool IsAuthFailure => Code is 4 or 9 or 10 or 13 or 26;

    /// <summary>auth.getSession before the user has approved the token in the browser.</summary>
    public bool IsTokenNotYetAuthorised => Code == 14;
}

/// <summary>What Last.fm did with one scrobble in a batch.</summary>
public enum ScrobbleOutcome
{
    Accepted,

    /// <summary>Refused for good (a filtered artist, a timestamp too old) - never resend.</summary>
    Ignored,

    /// <summary>The daily scrobble limit: keep it and try again tomorrow.</summary>
    Deferred,
}

/// <summary>
/// The Last.fm 2.0 web API calls a scrobbler needs, signed as the API requires.
/// <para>
/// Authentication is the desktop flow: <see cref="GetTokenAsync"/>, then the user
/// approves the token on last.fm in their own browser (<see cref="AuthorizeUrl"/>),
/// then <see cref="GetSessionAsync"/> trades it for a session key that does not
/// expire. The user's password never passes through the app.
/// </para>
/// </summary>
public sealed class LastFmApi
{
    public const string Endpoint = "https://ws.audioscrobbler.com/2.0/";

    /// <summary>Where to make an API key and secret. Last.fm issues one per application.</summary>
    public const string CreateApiAccountUrl = "https://www.last.fm/api/account/create";

    /// <summary>Where a user can revoke the app's access. The API has no call for it.</summary>
    public const string ApplicationsSettingsUrl = "https://www.last.fm/settings/applications";

    /// <summary>Last.fm takes at most this many scrobbles per request.</summary>
    public const int MaxBatch = 50;

    private const string UserAgent = "AudioFool/1.0 (personal music player)";

    private static readonly HttpClient SharedHttp = CreateClient();

    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly string _secret;

    public LastFmApi(string apiKey, string secret, HttpClient? http = null)
    {
        _apiKey = apiKey.Trim();
        _secret = secret.Trim();
        _http = http ?? SharedHttp;
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        })
        {
            Timeout = TimeSpan.FromSeconds(20),
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        return client;
    }

    public static string AuthorizeUrl(string apiKey, string token) =>
        $"https://www.last.fm/api/auth/?api_key={Uri.EscapeDataString(apiKey.Trim())}&token={Uri.EscapeDataString(token)}";

    /// <summary>
    /// The api_sig: every parameter except <c>format</c> and <c>callback</c>,
    /// sorted by name, each written as name then value with nothing between,
    /// the secret appended, and the UTF-8 bytes MD5-hashed to lowercase hex.
    /// Names are sorted ordinally, so <c>album[0]</c> comes before <c>artist[0]</c>.
    /// </summary>
    public static string Sign(IEnumerable<KeyValuePair<string, string>> parameters, string secret)
    {
        var text = new StringBuilder();
        foreach (var (name, value) in parameters
                     .Where(p => p.Key is not ("format" or "callback"))
                     .OrderBy(p => p.Key, StringComparer.Ordinal))
            text.Append(name).Append(value);
        text.Append(secret);

        return Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    public async Task<string> GetTokenAsync(CancellationToken ct)
    {
        using var doc = await CallAsync("auth.getToken", [], sessionKey: null, ct);
        return doc.RootElement.TryGetProperty("token", out var token) && token.GetString() is { Length: > 0 } t
            ? t
            : throw new LastFmException(0, "Last.fm returned no token.");
    }

    public async Task<LastFmSession> GetSessionAsync(string token, CancellationToken ct)
    {
        using var doc = await CallAsync("auth.getSession", [new("token", token)], sessionKey: null, ct);
        if (doc.RootElement.TryGetProperty("session", out var session)
            && session.TryGetProperty("key", out var key) && key.GetString() is { Length: > 0 } k)
        {
            var name = session.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            return new LastFmSession(name, k);
        }

        throw new LastFmException(0, "Last.fm returned no session.");
    }

    public async Task UpdateNowPlayingAsync(string sessionKey, ScrobbleEntry entry, CancellationToken ct)
    {
        var p = new List<KeyValuePair<string, string>>
        {
            new("artist", entry.Artist),
            new("track", entry.Track),
        };
        AddOptional(p, "album", entry.Album);
        AddOptional(p, "albumArtist", entry.AlbumArtist);
        AddOptional(p, "trackNumber", entry.TrackNumber?.ToString());
        AddOptional(p, "duration", entry.DurationSeconds?.ToString());

        using var _ = await CallAsync("track.updateNowPlaying", p, sessionKey, ct);
    }

    /// <summary>
    /// Sends up to <see cref="MaxBatch"/> scrobbles in one request and returns
    /// what happened to each, in the order given.
    /// </summary>
    public async Task<IReadOnlyList<ScrobbleOutcome>> ScrobbleAsync(
        string sessionKey, IReadOnlyList<ScrobbleEntry> entries, CancellationToken ct)
    {
        if (entries.Count is 0 or > MaxBatch)
            throw new ArgumentOutOfRangeException(nameof(entries), entries.Count, $"Between 1 and {MaxBatch} scrobbles.");

        var p = new List<KeyValuePair<string, string>>();
        for (var i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            p.Add(new($"artist[{i}]", e.Artist));
            p.Add(new($"track[{i}]", e.Track));
            p.Add(new($"timestamp[{i}]", e.Timestamp.ToString()));
            AddOptional(p, $"album[{i}]", e.Album);
            AddOptional(p, $"albumArtist[{i}]", e.AlbumArtist);
            AddOptional(p, $"trackNumber[{i}]", e.TrackNumber?.ToString());
            AddOptional(p, $"duration[{i}]", e.DurationSeconds?.ToString());
        }

        using var doc = await CallAsync("track.scrobble", p, sessionKey, ct);
        return ParseScrobbleOutcomes(doc.RootElement, entries.Count);
    }

    /// <summary>
    /// Reads the per-scrobble <c>ignoredMessage</c> codes. Last.fm answers a
    /// batch of one with an object rather than an array; both are handled. A
    /// response too short to cover every entry counts the rest as accepted -
    /// the request itself succeeded, and resending would duplicate them.
    /// </summary>
    public static IReadOnlyList<ScrobbleOutcome> ParseScrobbleOutcomes(JsonElement root, int count)
    {
        var outcomes = Enumerable.Repeat(ScrobbleOutcome.Accepted, count).ToArray();
        if (!root.TryGetProperty("scrobbles", out var scrobbles)
            || !scrobbles.TryGetProperty("scrobble", out var list))
            return outcomes;

        var items = list.ValueKind == JsonValueKind.Array ? list.EnumerateArray().ToList() : [list];
        for (var i = 0; i < Math.Min(count, items.Count); i++)
        {
            if (!items[i].TryGetProperty("ignoredMessage", out var ignored)
                || !ignored.TryGetProperty("code", out var codeEl))
                continue;

            var code = codeEl.ValueKind == JsonValueKind.Number
                ? codeEl.GetInt32()
                : int.TryParse(codeEl.GetString(), out var c) ? c : 0;

            outcomes[i] = code switch
            {
                0 => ScrobbleOutcome.Accepted,
                5 => ScrobbleOutcome.Deferred,
                _ => ScrobbleOutcome.Ignored,
            };
        }

        return outcomes;
    }

    private static void AddOptional(List<KeyValuePair<string, string>> p, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            p.Add(new(name, value));
    }

    /// <summary>
    /// One signed POST. Every call is signed, including auth.getToken, which
    /// does not need it but accepts it. Last.fm reports errors as JSON with an
    /// <c>error</c> number, usually under a 4xx or 5xx status; anything that is
    /// not JSON at all is treated as a transient fault.
    /// </summary>
    private async Task<JsonDocument> CallAsync(
        string method, List<KeyValuePair<string, string>> parameters, string? sessionKey, CancellationToken ct)
    {
        var p = new List<KeyValuePair<string, string>>(parameters)
        {
            new("method", method),
            new("api_key", _apiKey),
        };
        if (sessionKey is not null)
            p.Add(new("sk", sessionKey));
        p.Add(new("api_sig", Sign(p, _secret)));
        p.Add(new("format", "json"));

        HttpResponseMessage response;
        try
        {
            response = await _http.PostAsync(Endpoint, new FormUrlEncodedContent(p), ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new LastFmException(0, $"Couldn't reach Last.fm: {ex.Message}");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new LastFmException(0, "Last.fm didn't answer in time.");
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(body);
            }
            catch (JsonException)
            {
                throw new LastFmException(0, $"Last.fm answered HTTP {(int)response.StatusCode} without JSON.");
            }

            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.Number)
            {
                var message = doc.RootElement.TryGetProperty("message", out var m) ? m.GetString() : null;
                var code = error.GetInt32();
                doc.Dispose();
                throw new LastFmException(code, message ?? $"Last.fm error {code}.");
            }

            if (!response.IsSuccessStatusCode)
            {
                doc.Dispose();
                throw new LastFmException(0, $"Last.fm answered HTTP {(int)response.StatusCode}.");
            }

            return doc;
        }
    }
}
