using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AudioFool.Core.Art;

/// <summary>
/// One cover found online. <see cref="Width"/> and <see cref="Height"/> are read
/// from the image's own header by <see cref="OnlineArtSearch"/>, not taken from
/// what the source claims.
/// </summary>
public sealed record ArtCandidate(string Source, string Artist, string Album, string FullUrl, string PreviewUrl)
{
    public int Width { get; init; }
    public int Height { get; init; }

    /// <summary>How well the source's names match the album searched for; see <see cref="OnlineArtSearch.Relevance"/>.</summary>
    public int Relevance { get; init; }

    public string Title =>
        string.IsNullOrWhiteSpace(Artist) ? Album
        : string.IsNullOrWhiteSpace(Album) ? Artist
        : $"{Artist} – {Album}";
}

/// <summary>How a search went, for the status line under the results.</summary>
public sealed record ArtSearchSummary(
    int Shown, int TooSmall, int NotJpeg, int Unrelated, int Unreachable, IReadOnlyList<string> Notes);

/// <summary>
/// Looks for album covers on the iTunes Store, the Cover Art Archive and - when
/// the user has put an API key in settings - fanart.tv, and keeps only JPEGs at
/// least <see cref="MinimumSize"/> pixels on both sides.
/// <para>
/// Every candidate is measured before it is shown: the first 64 KB are fetched
/// with a range request and the frame header read with <see cref="JpegSize"/>.
/// That is what makes the size and format rules hold for sources that report
/// neither, and a candidate is never downloaded in full until it is chosen.
/// </para>
/// <para>
/// Bandcamp is deliberately absent: it has no public API, and its search page
/// answers anything that is not a real browser with a bot challenge.
/// </para>
/// </summary>
public sealed partial class OnlineArtSearch
{
    public const int MinimumSize = 600;

    /// <summary>MusicBrainz asks every client to identify itself.</summary>
    private const string UserAgent = "AudioFool/1.0 (personal music player)";

    private const int ProbeBytes = 64 * 1024;
    private const int ProbeBytesRetry = 512 * 1024;
    private const int MaxParallelProbes = 6;

    private static readonly HttpClient Http = CreateClient();

    private readonly string? _fanartApiKey;

    public OnlineArtSearch(string? fanartApiKey) =>
        _fanartApiKey = string.IsNullOrWhiteSpace(fanartApiKey) ? null : fanartApiKey.Trim();

    public bool HasFanartKey => _fanartApiKey is not null;

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

    /// <summary>
    /// Searches every source and reports each candidate that passes the size and
    /// format rules as soon as it has been measured, so results appear while the
    /// slower sources are still answering.
    /// </summary>
    public async Task<ArtSearchSummary> SearchAsync(
        string artist, string album, Action<ArtCandidate> onFound, CancellationToken ct)
    {
        var notes = new List<string>();
        if (!HasFanartKey)
            notes.Add("fanart.tv skipped: no API key in settings.json");

        var sourceTasks = new List<Task<IReadOnlyList<ArtCandidate>>>
        {
            Guard("iTunes", () => SearchItunesAsync(artist, album, ct), notes),
            Guard("MusicBrainz", () => SearchMusicBrainzAsync(artist, album, notes, ct), notes),
        };

        var all = (await Task.WhenAll(sourceTasks)).SelectMany(r => r)
            .DistinctBy(c => c.FullUrl, StringComparer.OrdinalIgnoreCase)
            .Select(c => c with { Relevance = Relevance(c, artist, album) })
            .ToList();

        // Best matches are measured first, so the likeliest covers appear first.
        var raw = all.Where(c => c.Relevance >= MinimumRelevance(artist))
            .OrderByDescending(c => c.Relevance)
            .ToList();
        var unrelated = all.Count - raw.Count;

        int shown = 0, tooSmall = 0, notJpeg = 0, unreachable = 0;
        using var gate = new SemaphoreSlim(MaxParallelProbes);

        await Task.WhenAll(raw.Select(async candidate =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var (probe, width, height) = await ProbeAsync(candidate.FullUrl, ct).ConfigureAwait(false);
                switch (Classify(probe, width, height))
                {
                    case Verdict.Accept:
                        Interlocked.Increment(ref shown);
                        onFound(candidate with { Width = width, Height = height });
                        break;
                    case Verdict.TooSmall:
                        Interlocked.Increment(ref tooSmall);
                        break;
                    case Verdict.NotJpeg:
                        Interlocked.Increment(ref notJpeg);
                        break;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                Interlocked.Increment(ref unreachable);
            }
            finally
            {
                gate.Release();
            }
        }));

        return new ArtSearchSummary(shown, tooSmall, notJpeg, unrelated, unreachable, notes);
    }

    /// <summary>
    /// 3 when both the artist and the album match, 2 for the artist alone (the
    /// same album under a different title, or another of theirs), 1 for the
    /// album alone, 0 for neither. Names are compared letters and digits only,
    /// ignoring case, accents and a leading "The", and either may contain the
    /// other - "Moving Pictures (Remastered)" matches "Moving Pictures".
    /// </summary>
    public static int Relevance(ArtCandidate candidate, string artist, string album) =>
        (NamesMatch(candidate.Artist, artist) ? 2 : 0) + (NamesMatch(candidate.Album, album) ? 1 : 0);

    /// <summary>
    /// With an artist to go on, a cover must at least be by that artist: an
    /// album-title match by someone else is a karaoke version or a different
    /// record that happens to share a name.
    /// </summary>
    public static int MinimumRelevance(string artist) => string.IsNullOrWhiteSpace(artist) ? 1 : 2;

    private static bool NamesMatch(string? found, string? wanted)
    {
        var a = NormalizeName(found);
        var b = NormalizeName(wanted);
        return a.Length > 0 && b.Length > 0 && (a.Contains(b, StringComparison.Ordinal) || b.Contains(a, StringComparison.Ordinal));
    }

    public static string NormalizeName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";

        var text = value.Trim();
        if (text.StartsWith("The ", StringComparison.OrdinalIgnoreCase))
            text = text[4..];

        var builder = new System.Text.StringBuilder(text.Length);
        foreach (var ch in text.Normalize(System.Text.NormalizationForm.FormD))
        {
            if (char.IsLetterOrDigit(ch))
                builder.Append(char.ToLowerInvariant(ch));
        }

        return builder.ToString();
    }

    public enum Verdict { Accept, TooSmall, NotJpeg }

    /// <summary>The size and format rules. An unmeasurable file counts as not a JPEG.</summary>
    public static Verdict Classify(JpegProbe probe, int width, int height) =>
        probe != JpegProbe.Found ? Verdict.NotJpeg
        : width >= MinimumSize && height >= MinimumSize ? Verdict.Accept
        : Verdict.TooSmall;

    /// <summary>
    /// Downloads a chosen cover in full and checks it again - the bytes that get
    /// written into the tags must pass the same rules as the preview did.
    /// </summary>
    public async Task<byte[]> DownloadAsync(ArtCandidate candidate, CancellationToken ct)
    {
        var bytes = await Http.GetByteArrayAsync(candidate.FullUrl, ct).ConfigureAwait(false);
        var probe = JpegSize.TryRead(bytes, out var width, out var height);
        return Classify(probe, width, height) switch
        {
            Verdict.Accept => bytes,
            Verdict.TooSmall => throw new InvalidDataException($"The image is only {width} × {height}."),
            _ => throw new InvalidDataException("The download is not a JPEG."),
        };
    }

    /// <summary>A small image for the results grid.</summary>
    public async Task<byte[]> DownloadPreviewAsync(ArtCandidate candidate, CancellationToken ct) =>
        await Http.GetByteArrayAsync(candidate.PreviewUrl, ct).ConfigureAwait(false);

    private static async Task<IReadOnlyList<ArtCandidate>> Guard(
        string source, Func<Task<IReadOnlyList<ArtCandidate>>> search, List<string> notes)
    {
        try
        {
            return await search().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            var note = ex is HttpRequestException { StatusCode: { } code }
                ? $"{source} did not answer ({(int)code})"
                : $"{source} did not answer";
            lock (notes)
                notes.Add(note);
            return [];
        }
    }

    private static async Task<(JpegProbe, int, int)> ProbeAsync(string url, CancellationToken ct)
    {
        var probe = await ProbeRangeAsync(url, ProbeBytes, ct).ConfigureAwait(false);
        if (probe.Item1 == JpegProbe.NeedMore)
            probe = await ProbeRangeAsync(url, ProbeBytesRetry, ct).ConfigureAwait(false);
        return probe;
    }

    /// <summary>
    /// Reads at most <paramref name="count"/> bytes. A server that ignores the
    /// range sends the whole file; only the start of it is read before the
    /// response is dropped.
    /// </summary>
    private static async Task<(JpegProbe, int, int)> ProbeRangeAsync(string url, int count, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Range = new RangeHeaderValue(0, count - 1);

        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var buffer = new byte[count];
        var read = 0;
        while (read < count)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read, count - read), ct).ConfigureAwait(false);
            if (n == 0)
                break;
            read += n;
        }

        var result = JpegSize.TryRead(buffer.AsSpan(0, read), out var width, out var height);

        // The whole file arrived and still no frame header: it is not going to appear.
        if (result == JpegProbe.NeedMore && read < count)
            result = JpegProbe.NotJpeg;

        return (result, width, height);
    }

    // ---- iTunes Store ----------------------------------------------------------------

    /// <summary>
    /// Artist and album first. The store matches every word, so a title it spells
    /// differently finds nothing; then the artist's albums are searched instead,
    /// and <see cref="Relevance"/> puts any match with the title first.
    /// </summary>
    private static async Task<IReadOnlyList<ArtCandidate>> SearchItunesAsync(string artist, string album, CancellationToken ct)
    {
        var found = await ItunesQueryAsync($"{artist} {album}", ct).ConfigureAwait(false);
        if (found.Count == 0 && !string.IsNullOrWhiteSpace(artist))
            found = await ItunesQueryAsync(artist, ct).ConfigureAwait(false);
        return found;
    }

    private static async Task<IReadOnlyList<ArtCandidate>> ItunesQueryAsync(string term, CancellationToken ct)
    {
        var json = await Http.GetStringAsync(
            $"https://itunes.apple.com/search?term={Uri.EscapeDataString(term.Trim())}&media=music&entity=album&limit=50", ct)
            .ConfigureAwait(false);
        return ParseItunes(json);
    }

    public static IReadOnlyList<ArtCandidate> ParseItunes(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
            return [];

        var found = new List<ArtCandidate>();
        foreach (var item in results.EnumerateArray())
        {
            var art = Text(item, "artworkUrl100");
            var full = ItunesArtworkUrl(art, 10000);
            var preview = ItunesArtworkUrl(art, 600);
            if (full is null || preview is null)
                continue;

            found.Add(new ArtCandidate("iTunes", Text(item, "artistName") ?? "", Text(item, "collectionName") ?? "", full, preview));
        }

        return found;
    }

    /// <summary>
    /// Rewrites an iTunes thumbnail URL ("…/100x100bb.jpg") to ask for another
    /// size. The store never upscales, so asking for 10000 returns the original
    /// size. The ".jpg" suffix makes it serve a JPEG rendition even when the
    /// label uploaded a PNG.
    /// </summary>
    public static string? ItunesArtworkUrl(string? thumbnailUrl, int size)
    {
        if (string.IsNullOrEmpty(thumbnailUrl))
            return null;

        var match = ItunesSizeSuffix().Match(thumbnailUrl);
        return match.Success
            ? thumbnailUrl[..match.Index] + $"/{size}x{size}bb.jpg"
            : null;
    }

    [GeneratedRegex(@"/\d+x\d+[a-z]*\.(jpg|jpeg|png|webp)$", RegexOptions.IgnoreCase)]
    private static partial Regex ItunesSizeSuffix();

    // ---- MusicBrainz: Cover Art Archive and fanart.tv ------------------------------------

    /// <summary>
    /// One MusicBrainz search finds the release groups; both the Cover Art Archive
    /// and fanart.tv are keyed by release-group ID.
    /// </summary>
    private async Task<IReadOnlyList<ArtCandidate>> SearchMusicBrainzAsync(
        string artist, string album, List<string> notes, CancellationToken ct)
    {
        var query = $"releasegroup:\"{LuceneEscape(album)}\"";
        if (!string.IsNullOrWhiteSpace(artist))
            query += $" AND artist:\"{LuceneEscape(artist)}\"";

        var url = $"https://musicbrainz.org/ws/2/release-group/?query={Uri.EscapeDataString(query)}&fmt=json&limit=5";
        using var response = await GetWithOneRetryAsync(url, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var groups = ParseMusicBrainz(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        var lookups = new List<Task<IReadOnlyList<ArtCandidate>>>();
        foreach (var group in groups)
        {
            lookups.Add(Guard("Cover Art Archive", () => CoverArtArchiveAsync(group, ct), notes));
            if (_fanartApiKey is { } key)
                lookups.Add(Guard("fanart.tv", () => FanartAsync(group, key, ct), notes));
        }

        return (await Task.WhenAll(lookups).ConfigureAwait(false)).SelectMany(r => r).ToList();
    }

    /// <summary>
    /// MusicBrainz answers 503 when it is rate-limiting, which it does to anonymous
    /// clients at busy times; one retry a second later usually gets through.
    /// </summary>
    private static async Task<HttpResponseMessage> GetWithOneRetryAsync(string url, CancellationToken ct)
    {
        var response = await Http.GetAsync(url, ct).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.ServiceUnavailable)
            return response;

        response.Dispose();
        await Task.Delay(TimeSpan.FromSeconds(1.2), ct).ConfigureAwait(false);
        return await Http.GetAsync(url, ct).ConfigureAwait(false);
    }

    public sealed record ReleaseGroup(string Id, string Artist, string Title);

    /// <summary>Close matches only, best first, and at most three of them.</summary>
    public static IReadOnlyList<ReleaseGroup> ParseMusicBrainz(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("release-groups", out var groups) || groups.ValueKind != JsonValueKind.Array)
            return [];

        var found = new List<ReleaseGroup>();
        foreach (var group in groups.EnumerateArray())
        {
            var score = group.TryGetProperty("score", out var s) && s.TryGetInt32(out var n) ? n : 0;
            var id = Text(group, "id");
            if (score < 90 || id is null)
                continue;

            var credit = group.TryGetProperty("artist-credit", out var ac) && ac.ValueKind == JsonValueKind.Array
                ? string.Concat(ac.EnumerateArray().Select(c => Text(c, "name") + Text(c, "joinphrase")))
                : null;
            found.Add(new ReleaseGroup(id, credit ?? "", Text(group, "title") ?? ""));
        }

        return found.Take(3).ToList();
    }

    private static async Task<IReadOnlyList<ArtCandidate>> CoverArtArchiveAsync(ReleaseGroup group, CancellationToken ct)
    {
        using var response = await Http.GetAsync($"https://coverartarchive.org/release-group/{group.Id}", ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return [];
        response.EnsureSuccessStatusCode();
        return ParseCoverArtArchive(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false), group);
    }

    /// <summary>Front covers only - the archive also holds backs, booklets and discs.</summary>
    public static IReadOnlyList<ArtCandidate> ParseCoverArtArchive(string json, ReleaseGroup group)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("images", out var images) || images.ValueKind != JsonValueKind.Array)
            return [];

        var found = new List<ArtCandidate>();
        foreach (var image in images.EnumerateArray())
        {
            if (!(image.TryGetProperty("front", out var front) && front.ValueKind == JsonValueKind.True))
                continue;

            var full = Https(Text(image, "image"));
            if (full is null)
                continue;

            string? preview = null;
            if (image.TryGetProperty("thumbnails", out var thumbs))
                preview = Https(Text(thumbs, "500") ?? Text(thumbs, "large") ?? Text(thumbs, "250"));

            found.Add(new ArtCandidate("Cover Art Archive", group.Artist, group.Title, full, preview ?? full));
        }

        return found;
    }

    private static async Task<IReadOnlyList<ArtCandidate>> FanartAsync(ReleaseGroup group, string key, CancellationToken ct)
    {
        using var response = await Http.GetAsync(
            $"https://webservice.fanart.tv/v3/music/albums/{group.Id}?api_key={Uri.EscapeDataString(key)}", ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return [];
        response.EnsureSuccessStatusCode();
        return ParseFanart(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false), group);
    }

    /// <summary>
    /// fanart.tv nests covers under the release-group ID:
    /// <c>albums.{id}.albumcover[].url</c>. Its preview images live at the same
    /// path under "/preview/" instead of "/fanart/".
    /// </summary>
    public static IReadOnlyList<ArtCandidate> ParseFanart(string json, ReleaseGroup group)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("albums", out var albums) || albums.ValueKind != JsonValueKind.Object)
            return [];

        var found = new List<ArtCandidate>();
        foreach (var entry in albums.EnumerateObject())
        {
            if (!entry.Value.TryGetProperty("albumcover", out var covers) || covers.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var cover in covers.EnumerateArray())
            {
                var full = Https(Text(cover, "url"));
                if (full is null)
                    continue;

                var preview = full.Replace("/fanart/", "/preview/", StringComparison.Ordinal);
                found.Add(new ArtCandidate("fanart.tv", group.Artist, group.Title, full, preview));
            }
        }

        return found;
    }

    // ---- helpers ---------------------------------------------------------------------

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? Https(string? url) =>
        url is null ? null
        : url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ? "https://" + url[7..]
        : url;

    /// <summary>Escapes a phrase for a quoted Lucene term in a MusicBrainz query.</summary>
    public static string LuceneEscape(string value) =>
        LuceneSpecial().Replace(value.Trim(), @"\$0");

    [GeneratedRegex(@"[+\-&|!(){}\[\]^""~*?:\\/]")]
    private static partial Regex LuceneSpecial();
}
