using System.Globalization;
using System.Text;
using AudioFool.Core.Models;

namespace AudioFool.Core.Library;

/// <summary>The tags Find and Replace looks in: the four text tags the library holds for every song.</summary>
public enum ReplaceField
{
    Title,
    Artist,
    AlbumArtist,
    Album,
}

/// <summary>
/// The text Find and Replace looks for and what goes in its place (session 63, the
/// user's design).
/// <list type="bullet">
/// <item>Case is ignored unless <see cref="MatchCase"/>.</item>
/// <item><see cref="WholeWords"/> keeps "and" out of "Andy" and "Band": a match that
/// starts or ends with a letter or digit must not run on into another one. One that
/// starts or ends with punctuation, "(Live)", is whole at that end.</item>
/// <item>Accents always count, and the comparison goes character by character, so
/// what is typed is what matches. A replace writes, and "Motley Crue" quietly
/// matching "Mötley Crüe" would rewrite the band's name.</item>
/// <item>An empty <see cref="Replace"/> deletes the text. The spaces a change leaves
/// are tidied (the user's call): two spaces meeting where the text was become one,
/// and the ends are trimmed, so deleting "(Live)" from "Song (Live) Mix" gives
/// "Song Mix", not "Song  Mix".</item>
/// </list>
/// </summary>
public sealed record TextReplace(string Find, string Replace, bool MatchCase = false, bool WholeWords = false)
{
    private StringComparison Comparison => MatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    /// <summary>True when <paramref name="value"/> holds the text at least once.</summary>
    public bool Matches(string value) => NextMatch(value, 0) >= 0;

    /// <summary>
    /// <paramref name="value"/> with every match replaced, left to right, and its
    /// spaces tidied. Null when nothing matched, or when the result reads exactly as
    /// the value did (a replacement that only repeats the match).
    /// </summary>
    public string? Apply(string value)
    {
        var at = NextMatch(value, 0);
        if (at < 0)
            return null;

        var result = new StringBuilder(value.Length + Replace.Length);
        var from = 0;
        while (at >= 0)
        {
            AppendTidy(result, value.AsSpan(from, at - from));
            AppendTidy(result, Replace);
            from = at + Find.Length;
            at = NextMatch(value, from);
        }

        AppendTidy(result, value.AsSpan(from));

        var replaced = result.ToString().Trim();
        return string.Equals(replaced, value, StringComparison.Ordinal) ? null : replaced;
    }

    /// <summary>
    /// Appends a piece, dropping its first space when the text so far ends in one:
    /// the only double spaces it removes are the ones a seam makes, so a value's own
    /// stay as they were.
    /// </summary>
    private static void AppendTidy(StringBuilder result, ReadOnlySpan<char> piece)
    {
        if (piece.Length > 0 && piece[0] == ' ' && result.Length > 0 && result[^1] == ' ')
            piece = piece[1..];
        result.Append(piece);
    }

    private int NextMatch(string value, int from)
    {
        if (Find.Length == 0)
            return -1;

        while (from <= value.Length - Find.Length)
        {
            var at = value.IndexOf(Find, from, Comparison);
            if (at < 0)
                return -1;
            if (!WholeWords || IsWhole(value, at))
                return at;
            from = at + 1;
        }

        return -1;
    }

    /// <summary>
    /// Ordinal comparisons match as many characters as they compare, so the match
    /// is as long as <see cref="Find"/>.
    /// </summary>
    private bool IsWhole(string value, int at)
    {
        var end = at + Find.Length;
        var runsOnBefore = IsWordCharacter(Find[0]) && at > 0 && IsWordCharacter(value[at - 1]);
        var runsOnAfter = IsWordCharacter(Find[^1]) && end < value.Length && IsWordCharacter(value[end]);
        return !runsOnBefore && !runsOnAfter;
    }

    /// <summary>A letter, a digit, or an accent written as a mark of its own after its letter.</summary>
    private static bool IsWordCharacter(char c) =>
        char.IsLetterOrDigit(c)
        || CharUnicodeInfo.GetUnicodeCategory(c) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark;
}

/// <summary>One tag of one song that Find found, and what the library has it reading.</summary>
public sealed record ReplaceMatch(Track Track, ReplaceField Field, string Before);

/// <summary>
/// Find and Replace over the library: what Find lists. The file writes are
/// <see cref="TagWriter.WriteReplacement"/> and <see cref="TagWriter.UndoReplacement"/>.
/// </summary>
public static class FindReplace
{
    /// <summary>The four tags, in the order a song's matches are listed.</summary>
    public static IReadOnlyList<ReplaceField> Fields { get; } =
        [ReplaceField.Title, ReplaceField.Artist, ReplaceField.AlbumArtist, ReplaceField.Album];

    /// <summary>
    /// What the library has a tag reading, as the Songs table shows it. Artist is the
    /// album artist for a song with no artist of its own (<see cref="TagReader"/>), so
    /// such a song's album artist is found twice; <see cref="TagWriter.WriteReplacement"/>
    /// writes it once.
    /// </summary>
    public static string ValueOf(Track track, ReplaceField field) => field switch
    {
        ReplaceField.Title => track.Title,
        ReplaceField.Artist => track.Artist,
        ReplaceField.AlbumArtist => track.AlbumArtist,
        _ => track.Album,
    };

    /// <summary>The tag's name as the tag windows label it.</summary>
    public static string Name(ReplaceField field) => field switch
    {
        ReplaceField.Title => "Title",
        ReplaceField.Artist => "Artist",
        ReplaceField.AlbumArtist => "Album Artist",
        _ => "Album",
    };

    /// <summary>
    /// Every tag of <paramref name="tracks"/> holding the text: song by song in the
    /// order given, each song's in <see cref="Fields"/> order. Only the Find text and
    /// the two options decide what matches; the replacement doesn't.
    /// </summary>
    public static IReadOnlyList<ReplaceMatch> Find(IEnumerable<Track> tracks, TextReplace replace)
    {
        var matches = new List<ReplaceMatch>();
        if (replace.Find.Length == 0)
            return matches;

        foreach (var track in tracks)
        {
            foreach (var field in Fields)
            {
                var value = ValueOf(track, field);
                if (value.Length > 0 && replace.Matches(value))
                    matches.Add(new ReplaceMatch(track, field, value));
            }
        }

        return matches;
    }
}
