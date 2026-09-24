namespace AudioFool.Core.Library;

/// <summary>
/// Every tag field for a single-track edit. The nine positional fields are
/// authoritative - the caller always pre-fills every field with the track's
/// current value, so there is no "null means leave alone" case to model.
/// <see cref="Details"/> is the exception; see <see cref="TagDetailsEdit"/>.
/// </summary>
public sealed record TrackTagEdit(
    string Title,
    string Artist,
    string AlbumArtist,
    string Album,
    int? Year,
    int? TrackNumber,
    int? TrackCount,
    int? DiscNumber,
    int? DiscCount)
{
    public TagDetailsEdit Details { get; init; } = TagDetailsEdit.None;

    /// <summary>
    /// The full release date when one was typed ("2026-10-02"), normalised by
    /// <see cref="ReleaseDate"/>; <see cref="Year"/> is its year. Null writes
    /// the year alone.
    /// </summary>
    public string? Date { get; init; }
}

/// <summary>
/// The fields shared by a whole-album batch edit. Title and track number are
/// deliberately absent - they differ per track and are left alone.
/// <para>
/// The four positional fields are written to every track. The init-only ones
/// are optional: null leaves each track's own value in place. The dialog sets
/// one only when the user changed it, because these are the fields tracks
/// legitimately disagree on - a two-disc set has two disc numbers, a classical
/// album a composer per work - and writing the first track's value to all of
/// them would quietly destroy the rest.
/// </para>
/// </summary>
public sealed record AlbumTagEdit(
    string Artist,
    string AlbumArtist,
    string Album,
    int? Year)
{
    public NumberEdit? TrackCount { get; init; }
    public NumberEdit? DiscNumber { get; init; }
    public NumberEdit? DiscCount { get; init; }
    public TagDetailsEdit Details { get; init; } = TagDetailsEdit.None;

    /// <summary>As <see cref="TrackTagEdit.Date"/>: written album-wide with <see cref="Year"/>.</summary>
    public string? Date { get; init; }
}

/// <summary>
/// A number to write, where <see cref="Value"/> null clears the tag. Wrapped so
/// that "clear it" and "leave it alone" (the wrapper itself being null) differ.
/// </summary>
public readonly record struct NumberEdit(int? Value);

/// <summary>
/// Changes to the tags the library cache does not hold - they are shown only
/// in the tag dialog, so they are read from the file when it opens
/// (<see cref="TagReader.ReadDetails"/>) rather than stored for every track.
/// <para>
/// Each field is null to leave the file's value alone, "" to clear it, or the
/// new text. Composer and Genre are multi-valued in most formats; they are
/// edited as one string with values separated by semicolons.
/// </para>
/// </summary>
public sealed record TagDetailsEdit
{
    public static TagDetailsEdit None { get; } = new();

    public string? Publisher { get; init; }
    public string? Composer { get; init; }
    public string? Conductor { get; init; }
    public string? Genre { get; init; }
    public string? Comment { get; init; }
}

/// <summary>
/// What a file currently holds for the <see cref="TagDetailsEdit"/> fields,
/// with multi-valued fields joined by <see cref="TagDetails.Separator"/>.
/// </summary>
public sealed record TagDetails(
    string Publisher,
    string Composer,
    string Conductor,
    string Genre,
    string Comment)
{
    /// <summary>Joins multiple composers or genres for display, and splits them on save.</summary>
    public const string Separator = "; ";

    /// <summary>
    /// The release date as the file stores it, when it names more than the year
    /// ("2014-05-01"); "" otherwise. Not one of the five detail fields - it is
    /// shown in the Year box - but read from the file for the same reason: the
    /// cache holds only the year.
    /// </summary>
    public string Date { get; init; } = "";

    public static string Join(IEnumerable<string>? values) =>
        values is null
            ? ""
            : string.Join(Separator, values.Select(v => v.Trim()).Where(v => v.Length > 0));

    public static string[] Split(string value) =>
        value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

/// <summary>An image to embed in a track's tags and/or write as a folder cover file.</summary>
public sealed record ArtPayload(byte[] Bytes, string MimeType);
