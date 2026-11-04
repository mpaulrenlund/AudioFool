using System.Text.RegularExpressions;
using AudioFool.Core.Scrobbling;

namespace AudioFool.Core.Genius;

/// <summary>
/// Decides which Genius page is a library song. Genius's search is strict
/// about extras ("Paranoid Android (Remastered)" finds nothing), and it ranks
/// its own translation pages next to the song, so:
/// <list type="bullet">
/// <item>the title is searched without extras such as "(Remastered)",
/// "- 2011 Remaster", "(Live)" or "feat. X" (<see cref="CleanTitle"/>);</item>
/// <item>a result counts only when its artist is the song's artist and its
/// title is the song's title, both compared loosely: case, accents,
/// punctuation, "&amp;"/"and" and a leading "The" don't count, and extras are
/// left out on both sides.</item>
/// </list>
/// Nothing else is guessed: with no such result the window offers the nearest
/// ones instead (<see cref="Suggestions"/>).
/// </summary>
public static partial class GeniusMatch
{
    /// <summary>How many near results are offered when nothing matches.</summary>
    public const int SuggestionCount = 5;

    /// <summary>
    /// Words that mark a trailing "(…)", "[…]" or " - …" as an extra rather than
    /// part of the name. A four-digit year counts too. "Part 2" doesn't, so
    /// "Song (Part 1)" and "Song (Part 2)" stay apart.
    /// </summary>
    private static readonly HashSet<string> ExtraWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "remaster", "remastered", "remasters", "remix", "remixed", "mix", "live", "mono", "stereo",
        "version", "edit", "demo", "bonus", "deluxe", "edition", "feat", "ft", "featuring", "with",
        "acoustic", "instrumental", "single", "explicit", "clean", "radio", "anniversary", "take",
        "session", "sessions", "rerecorded", "recorded", "extended", "original", "unplugged",
        "outtake", "alternate", "early", "reissue", "expanded", "bbc", "mtv",
        // "(from Ookiku Furikabutte)", "- From "Frozen"": where a soundtrack song comes from.
        "from",
    };

    [GeneratedRegex(@"\s+(?:feat\.?|ft\.?|featuring)\s+.*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FeaturingTail();

    [GeneratedRegex(@"^(?<name>.+?)\s+[-–—]\s+(?<extra>[^-–—]+)$", RegexOptions.CultureInvariant)]
    private static partial Regex DashTail();

    [GeneratedRegex(@"^(?<name>.+?)\s*[\(\[](?<extra>[^\(\)\[\]]*)[\)\]]$", RegexOptions.CultureInvariant)]
    private static partial Regex BracketTail();

    [GeneratedRegex(@"\s+(?:feat\.?|ft\.?|featuring)\s+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Featuring();

    [GeneratedRegex(@"\s*(?:&|,|;|/|\+|\bx\b|\band\b|\bwith\b|\bvs\.?)\s*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ArtistJoin();

    /// <summary>The title without trailing extras: "Paranoid Android (Remastered)" → "Paranoid Android".</summary>
    public static string CleanTitle(string? title)
    {
        var s = (title ?? "").Trim();
        while (true)
        {
            var before = s;
            s = FeaturingTail().Replace(s, "").Trim();

            if (BracketTail().Match(s) is { Success: true } b && IsExtra(b.Groups["extra"].Value))
                s = b.Groups["name"].Value.Trim();
            else if (DashTail().Match(s) is { Success: true } d && IsExtra(d.Groups["extra"].Value))
                s = d.Groups["name"].Value.Trim();

            if (s == before || s.Length == 0)
                return s.Length == 0 ? before : s;
        }
    }

    /// <summary>The artist to search with: the first one named, before any "feat.".</summary>
    public static string SearchArtist(string? artist)
    {
        var a = (artist ?? "").Trim();
        var feat = Featuring().Match(a);
        return feat.Success ? a[..feat.Index].Trim() : a;
    }

    /// <summary>The first page in <paramref name="hits"/> that is this song, or null.</summary>
    public static GeniusHit? Pick(IEnumerable<GeniusHit> hits, string? artist, string? albumArtist, string? title)
    {
        var ours = ArtistKeys(artist).Concat(ArtistKeys(albumArtist)).ToHashSet(StringComparer.Ordinal);
        var titles = TitleKeys(title).ToHashSet(StringComparer.Ordinal);
        if (ours.Count == 0 || titles.Count == 0)
            return null;

        return hits.FirstOrDefault(h =>
            !IsGeniusOwnPage(h)
            && ArtistKeys(h.Artist).Any(ours.Contains)
            && TitleKeys(h.Title).Any(titles.Contains));
    }

    /// <summary>The nearest results to offer when none is the song: Genius's own translation pages left out, no repeats.</summary>
    public static IReadOnlyList<GeniusHit> Suggestions(IEnumerable<GeniusHit> hits) =>
        [.. hits.Where(h => !IsGeniusOwnPage(h)).DistinctBy(h => h.Id).Take(SuggestionCount)];

    /// <summary>
    /// Pages Genius's own accounts publish, "Genius English Translations",
    /// "Genius Romanizations" and the like: never the song itself.
    /// </summary>
    public static bool IsGeniusOwnPage(GeniusHit hit) =>
        hit.Artist.StartsWith("Genius ", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The keys an artist name answers to: the whole name, and each artist in a
    /// joint credit ("A &amp; B", "A feat. B", "A x B"), each with and without a
    /// leading "The".
    /// </summary>
    public static IEnumerable<string> ArtistKeys(string? artist)
    {
        if (string.IsNullOrWhiteSpace(artist))
            yield break;

        var parts = new List<string> { artist };
        var main = SearchArtist(artist);
        parts.Add(main);
        parts.AddRange(Featuring().Split(artist));
        parts.AddRange(ArtistJoin().Split(main));

        foreach (var part in parts)
        {
            var key = ListeningMatch.Key(part);
            if (key.Length == 0)
                continue;
            yield return key;
            if (key.StartsWith("the", StringComparison.Ordinal) && key.Length > 3
                && part.TrimStart().StartsWith("the ", StringComparison.OrdinalIgnoreCase))
                yield return key[3..];
        }
    }

    private static IEnumerable<string> TitleKeys(string? title)
    {
        var whole = ListeningMatch.Key(title);
        if (whole.Length == 0)
            yield break;
        yield return whole;

        var clean = ListeningMatch.Key(CleanTitle(title));
        if (clean.Length > 0 && clean != whole)
            yield return clean;
    }

    private static bool IsExtra(string text)
    {
        foreach (var word in text.Split([' ', '.', ',', '-', '/', '\'', '’', '"', ':', '!'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (ExtraWords.Contains(word))
                return true;
            if (word.Length == 4 && word.All(char.IsAsciiDigit) && word[0] is '1' or '2')
                return true;
        }
        return false;
    }
}
