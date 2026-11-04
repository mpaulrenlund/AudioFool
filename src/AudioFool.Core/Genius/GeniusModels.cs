namespace AudioFool.Core.Genius;

/// <summary>Genius couldn't be reached, or answered with something other than a page.</summary>
public sealed class GeniusException(string message) : Exception(message);

/// <summary>One song as a search or a connection lists it: enough to show it and to load it.</summary>
public sealed record GeniusHit(long Id, string Title, string Artist, string Url);

/// <summary>A credit line: "Produced by" and who.</summary>
public sealed record GeniusCredit(string Label, IReadOnlyList<string> Names);

/// <summary>One kind of connection ("Samples", "Covered by") and the songs Genius lists for it.</summary>
public sealed record GeniusConnection(string Label, IReadOnlyList<GeniusHit> Songs);

/// <summary>
/// A song's Genius page, less the lyrics (which Genius never hands out). The
/// write-up is null when the page has none; Genius sends "?" for that.
/// </summary>
public sealed record GeniusSong(
    long Id,
    string Title,
    string Artist,
    string Url,
    string? About,
    string? Album,
    string? ReleaseDate,
    IReadOnlyList<GeniusCredit> Credits,
    IReadOnlyList<GeniusConnection> Connections);

/// <summary>An explanation of a lyric line. Verified when the artist wrote it.</summary>
public sealed record GeniusAnnotation(string Fragment, string Body, bool Verified);

/// <summary>A question on the page and its answer. Unanswered ones aren't kept.</summary>
public sealed record GeniusAnswer(string Question, string Answer);

public sealed record GeniusComment(string Author, DateTimeOffset? Posted, int Votes, string Body);

/// <summary>One page of comments, most-voted first; <see cref="NextPage"/> is null after the last.</summary>
public sealed record GeniusCommentPage(IReadOnlyList<GeniusComment> Comments, int Total, int? NextPage)
{
    public static GeniusCommentPage Empty { get; } = new([], 0, null);
}

/// <summary>Everything the Genius window shows for one song, fetched together.</summary>
public sealed record GeniusPage(
    GeniusSong Song,
    IReadOnlyList<GeniusAnswer> Answers,
    IReadOnlyList<GeniusAnnotation> Annotations,
    GeniusCommentPage Comments);

/// <summary>
/// What a search for a library song found: the page that is that song, or
/// none and up to five near results to choose from instead.
/// </summary>
public sealed record GeniusFind(GeniusHit? Match, IReadOnlyList<GeniusHit> Suggestions);
