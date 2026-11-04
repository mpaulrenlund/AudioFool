using System.Collections.Concurrent;
using AudioFool.Core.Scrobbling;

namespace AudioFool.Core.Genius;

/// <summary>
/// Finds a library song's Genius page and fetches what the Genius window shows,
/// keeping both until AudioFool closes, so opening the same song again asks
/// Genius nothing. Nothing is saved to disk, and a failed request is never
/// kept: the next try asks again.
/// </summary>
public sealed class GeniusLookup(GeniusClient client)
{
    /// <summary>Results asked for per search.</summary>
    public const int SearchCount = 10;

    private readonly ConcurrentDictionary<string, GeniusFind> _finds = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<long, GeniusPage> _pages = new();

    public GeniusClient Client { get; } = client;

    /// <summary>
    /// The page that is this song: searched as "artist title", then by the title
    /// alone (which finds songs the artist search buries). With no page that is
    /// the song, the nearest results of both searches, to choose from.
    /// </summary>
    public async Task<GeniusFind> FindAsync(string? artist, string? albumArtist, string? title, CancellationToken ct)
    {
        var mainArtist = GeniusMatch.SearchArtist(string.IsNullOrWhiteSpace(artist) ? albumArtist : artist);
        var cleanTitle = GeniusMatch.CleanTitle(title);
        if (cleanTitle.Length == 0)
            return new GeniusFind(null, []);

        var key = $"{ListeningMatch.Key(artist)}\n{ListeningMatch.Key(albumArtist)}\n{ListeningMatch.Key(title)}";
        if (_finds.TryGetValue(key, out var known))
            return known;

        List<GeniusHit> seen = [];
        var queries = mainArtist.Length > 0 ? [$"{mainArtist} {cleanTitle}", cleanTitle] : new[] { cleanTitle };
        GeniusHit? match = null;
        foreach (var query in queries)
        {
            var hits = await Client.SearchAsync(query, SearchCount, ct).ConfigureAwait(false);
            seen.AddRange(hits);
            match = GeniusMatch.Pick(hits, artist, albumArtist, title);
            if (match is not null)
                break;
        }

        var find = new GeniusFind(match, match is null ? GeniusMatch.Suggestions(seen) : []);
        _finds[key] = find;
        return find;
    }

    /// <summary>
    /// The song's page, its answered questions, its explanations and the first
    /// page of comments, asked for together. Only the song is required: if one
    /// of the other three fails, the page comes back without it, named in
    /// <see cref="GeniusPageResult.Missing"/>, and isn't kept.
    /// </summary>
    public async Task<GeniusPageResult> LoadAsync(long id, CancellationToken ct)
    {
        if (_pages.TryGetValue(id, out var known))
            return new GeniusPageResult(known, []);

        var song = Client.GetSongAsync(id, ct);
        var answers = Client.GetAnswersAsync(id, ct);
        var annotations = Client.GetAnnotationsAsync(id, ct);
        var comments = Client.GetCommentsAsync(id, 1, ct);

        List<string> missing = [];
        async Task<T> Optional<T>(Task<T> task, string name, T empty)
        {
            try
            {
                return await task.ConfigureAwait(false);
            }
            catch (GeniusException)
            {
                lock (missing)
                    missing.Add(name);
                return empty;
            }
        }

        var a = Optional(answers, "questions and answers", (IReadOnlyList<GeniusAnswer>)[]);
        var n = Optional(annotations, "annotations", (IReadOnlyList<GeniusAnnotation>)[]);
        var c = Optional(comments, "comments", GeniusCommentPage.Empty);
        await Task.WhenAll(a, n, c).ConfigureAwait(false);

        var page = new GeniusPage(await song.ConfigureAwait(false), await a, await n, await c);
        string[] order = ["questions and answers", "annotations", "comments"];
        var missed = order.Where(missing.Contains).ToList();
        if (missed.Count == 0)
            _pages[id] = page;
        return new GeniusPageResult(page, missed);
    }
}

/// <summary>A loaded page, and which of its optional parts couldn't be fetched.</summary>
public sealed record GeniusPageResult(GeniusPage Page, IReadOnlyList<string> Missing);
