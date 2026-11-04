using System.Net;
using System.Text.Json;

namespace AudioFool.Core.Genius;

/// <summary>
/// Reads song pages from genius.com's own web API: the one its pages are
/// built from, at <c>genius.com/api/</c>. It needs no key and, unlike the
/// documented developer API (<c>api.genius.com</c>), it has the comments and
/// the questions and answers. It is undocumented, so a change on Genius's side
/// shows up here as a <see cref="GeniusException"/> and nowhere else.
/// Genius never sends lyrics through either.
/// </summary>
public sealed class GeniusClient
{
    public const string BaseUrl = "https://genius.com/api/";

    /// <summary>Most comments Genius sends in one page.</summary>
    public const int CommentsPerPage = 50;

    private const string UserAgent = "AudioFool/1.0 (personal music player)";

    private static readonly HttpClient SharedHttp = CreateClient();

    private readonly HttpClient _http;

    public GeniusClient(HttpClient? http = null) => _http = http ?? SharedHttp;

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

    /// <summary>Genius's search for songs, best first.</summary>
    public async Task<IReadOnlyList<GeniusHit>> SearchAsync(string query, int count, CancellationToken ct)
    {
        using var doc = await GetAsync($"search/song?per_page={count}&q={Uri.EscapeDataString(query)}", ct).ConfigureAwait(false);
        return GeniusJson.SearchHits(Response(doc));
    }

    public async Task<GeniusSong> GetSongAsync(long id, CancellationToken ct)
    {
        using var doc = await GetAsync($"songs/{id}?text_format=plain", ct).ConfigureAwait(false);
        return GeniusJson.Song(Response(doc));
    }

    public async Task<IReadOnlyList<GeniusAnnotation>> GetAnnotationsAsync(long id, CancellationToken ct)
    {
        using var doc = await GetAsync($"referents?song_id={id}&per_page=50&text_format=plain", ct).ConfigureAwait(false);
        return GeniusJson.Annotations(Response(doc));
    }

    public async Task<IReadOnlyList<GeniusAnswer>> GetAnswersAsync(long id, CancellationToken ct)
    {
        using var doc = await GetAsync($"questions?song_id={id}&per_page=20&text_format=plain", ct).ConfigureAwait(false);
        return GeniusJson.Answers(Response(doc));
    }

    /// <summary>One page of comments, most-voted first; pages count from 1.</summary>
    public async Task<GeniusCommentPage> GetCommentsAsync(long id, int page, CancellationToken ct)
    {
        using var doc = await GetAsync($"songs/{id}/comments?per_page={CommentsPerPage}&page={page}&text_format=plain", ct)
            .ConfigureAwait(false);
        return GeniusJson.Comments(Response(doc));
    }

    private static JsonElement Response(JsonDocument doc) =>
        doc.RootElement.TryGetProperty("response", out var response) && response.ValueKind == JsonValueKind.Object
            ? response
            : throw new GeniusException("Genius answered in a way AudioFool doesn't recognise.");

    /// <summary>
    /// One GET. Genius wraps every answer as <c>{"meta": {"status": ...}, "response": ...}</c>;
    /// anything else (a block page, a timeout, no network) becomes a
    /// <see cref="GeniusException"/> saying which.
    /// </summary>
    private async Task<JsonDocument> GetAsync(string path, CancellationToken ct)
    {
        HttpResponseMessage response;
        try
        {
            response = await _http.GetAsync(BaseUrl + path, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new GeniusException($"Couldn't reach Genius: {ex.Message}");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new GeniusException("Genius didn't answer in time.");
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
                throw new GeniusException($"Genius answered HTTP {(int)response.StatusCode} without a page AudioFool can read.");
            }

            if (!response.IsSuccessStatusCode)
            {
                var message = doc.RootElement.ValueKind == JsonValueKind.Object
                    && doc.RootElement.TryGetProperty("meta", out var meta) && meta.ValueKind == JsonValueKind.Object
                    && meta.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String
                        ? m.GetString()
                        : null;
                doc.Dispose();
                throw new GeniusException(message is { Length: > 0 }
                    ? $"Genius answered HTTP {(int)response.StatusCode}: {message.TrimEnd('.')}"
                    : $"Genius answered HTTP {(int)response.StatusCode}.");
            }

            return doc;
        }
    }
}
