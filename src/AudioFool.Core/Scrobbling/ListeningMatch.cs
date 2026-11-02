using System.Globalization;
using System.Text;
using AudioFool.Core.Models;

namespace AudioFool.Core.Scrobbling;

/// <summary>
/// Finds the library's copy of an artist, album or song Last.fm names, so a
/// Listening History row can go to it. Last.fm's names came from tags (this
/// app's and other players'), sometimes corrected by Last.fm, so they are
/// compared loosely: case, accents, punctuation and "&amp;"/"and" don't count.
/// A trailing "(Remastered)" or "[Deluxe Edition]" on an album or song title is
/// tried without, too. Nothing is a guess beyond that: no match, no row to click.
/// </summary>
public sealed class ListeningMatch
{
    private readonly Dictionary<string, Track> _artists = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Track> _albums = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Track> _songs = new(StringComparer.Ordinal);

    public static ListeningMatch Empty { get; } = new([]);

    public ListeningMatch(IEnumerable<Track> tracks)
    {
        var all = tracks.ToList();

        // Every artist the list groups on before any track artist, so an artist
        // row goes to their own entry rather than a compilation they appear on.
        foreach (var t in all)
            Index(_artists, Key(t.GroupingArtist), t);
        foreach (var t in all)
            Index(_artists, Key(t.Artist), t);

        foreach (var t in all)
        {
            foreach (var artist in new[] { t.GroupingArtist, t.Artist })
            {
                var a = Key(artist);
                if (a.Length == 0)
                    continue;

                foreach (var album in Variants(t.Album))
                    Index(_albums, $"{a}\n{album}", t);
                foreach (var title in Variants(t.Title))
                    Index(_songs, $"{a}\n{title}", t);
            }
        }
    }

    /// <summary>A track by the artist, preferring one they are the album artist of.</summary>
    public Track? Artist(string name) => _artists.GetValueOrDefault(Key(name));

    public Track? Album(string artist, string album) => Find(_albums, artist, album);

    public Track? Song(string artist, string title) => Find(_songs, artist, title);

    private static Track? Find(Dictionary<string, Track> index, string artist, string name)
    {
        var a = Key(artist);
        if (a.Length == 0)
            return null;

        foreach (var n in Variants(name))
            if (index.TryGetValue($"{a}\n{n}", out var track))
                return track;

        return null;
    }

    private static void Index(Dictionary<string, Track> index, string key, Track track)
    {
        if (key.Length > 0)
            index.TryAdd(key, track);
    }

    /// <summary>The title as it is, then without a trailing bracketed part.</summary>
    private static IEnumerable<string> Variants(string? title)
    {
        var whole = Key(title);
        if (whole.Length == 0)
            yield break;

        yield return whole;

        var bare = Key(StripTrailingBrackets(title!));
        if (bare.Length > 0 && bare != whole)
            yield return bare;
    }

    private static string StripTrailingBrackets(string title)
    {
        var s = title.TrimEnd();
        while (s.Length > 0 && s[^1] is ')' or ']')
        {
            var open = s.LastIndexOf(s[^1] == ')' ? '(' : '[');
            if (open <= 0)
                break;
            s = s[..open].TrimEnd();
        }
        return s;
    }

    /// <summary>
    /// Lower case, accents dropped, "&amp;" read as "and", and only letters and
    /// digits kept: "Beyoncé &amp; Jay-Z" and "beyonce and jay z" are one key.
    /// </summary>
    public static string Key(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "";

        var text = name.Replace("&", " and ").Normalize(NormalizationForm.FormD);
        var key = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
                continue;
            if (char.IsLetterOrDigit(c))
                key.Append(char.ToLowerInvariant(c));
        }
        return key.ToString();
    }
}
