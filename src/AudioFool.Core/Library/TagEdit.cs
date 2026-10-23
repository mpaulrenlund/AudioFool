namespace AudioFool.Core.Library;

/// <summary>
/// Every tag field for a single-track edit. The nine positional fields are
/// authoritative - the caller always pre-fills every field with the track's
/// current value, so there is no "null means leave alone" case to model.
/// <see cref="Details"/> is the exception; see <see cref="TagDetailsEdit"/>.
/// Artist and Album Artist hold one value where a file can hold several, so a
/// box that still reads the file's first value leaves them all in place
/// (<see cref="TagWriter.WriteTrackTags"/>).
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
/// Every field is optional: null leaves each track's own value in place. The
/// dialog sets one only when the user changed it, because tracks legitimately
/// disagree on any of them - a compilation has an artist per track, a two-disc
/// set two disc numbers, a reissue a year of its own - and writing the first
/// track's value to all of them would quietly destroy the rest. (Until session
/// 60 Artist, Album Artist, Album and Year were always written, from the first
/// track; a compilation lost its artists, and an untagged album was given the
/// words "Unknown Album".)
/// </para>
/// </summary>
public sealed record AlbumTagEdit
{
    public string? Artist { get; init; }
    public string? AlbumArtist { get; init; }
    public string? Album { get; init; }

    /// <summary>The year, with the full date when one was typed; null keeps each track's.</summary>
    public DateEdit? Date { get; init; }

    public NumberEdit? TrackCount { get; init; }
    public NumberEdit? DiscNumber { get; init; }
    public NumberEdit? DiscCount { get; init; }
    public TagDetailsEdit Details { get; init; } = TagDetailsEdit.None;

    /// <summary>
    /// Rewrites each track's own track and disc numbers and totals without
    /// leading zeros ("01" becomes "1"), keeping every value. A count or disc
    /// edit above still wins for its field.
    /// </summary>
    public bool RemoveLeadingZeros { get; init; }
}

/// <summary>
/// An edit to tracks the user picked by hand - disc 1 of a two-disc set, say.
/// Every field is optional: null leaves each track's own value in place. Unlike an
/// album, a hand-picked set has no reason to agree on its artist or album, so
/// nothing is written that the user did not change.
/// <para>
/// Also the edit behind a single cell edited in the track grid
/// (<see cref="InlineTagEdit"/>), which is the only thing that sets
/// <see cref="Title"/> or <see cref="TrackNumber"/>: the dialog never gives
/// several tracks one title.
/// </para>
/// </summary>
public sealed record TracksTagEdit
{
    public string? Title { get; init; }
    public NumberEdit? TrackNumber { get; init; }
    public string? Artist { get; init; }
    public string? AlbumArtist { get; init; }
    public string? Album { get; init; }
    public DateEdit? Date { get; init; }
    public NumberEdit? TrackCount { get; init; }
    public NumberEdit? DiscNumber { get; init; }
    public NumberEdit? DiscCount { get; init; }
    public TagDetailsEdit Details { get; init; } = TagDetailsEdit.None;
}

/// <summary>
/// A year to write, with the full release date when one was typed (see
/// <see cref="TrackTagEdit.Date"/>). Both null clears the year.
/// </summary>
public readonly record struct DateEdit(int? Year, string? Date);

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

    /// <summary>
    /// The track and disc numbers and totals as the file spells them, so the
    /// dialog shows "01" where TagLib's number would read 1 - a leading zero is
    /// something the user wants to see and fix. "" for a field the file lacks.
    /// </summary>
    public NumberTexts Numbers { get; init; } = NumberTexts.Empty;

    public static string Join(IEnumerable<string>? values) =>
        values is null
            ? ""
            : string.Join(Separator, values.Select(v => v.Trim()).Where(v => v.Length > 0));

    public static string[] Split(string value) =>
        value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

/// <summary>
/// A file's track and disc numbers and totals as text, leading zeros kept.
/// Read by <see cref="TagReader.ReadDetails"/>.
/// </summary>
public sealed record NumberTexts(string TrackNumber, string TrackCount, string DiscNumber, string DiscCount)
{
    public static NumberTexts Empty { get; } = new("", "", "", "");
}

/// <summary>An image to embed in a track's tags and/or write as a folder cover file.</summary>
public sealed record ArtPayload(byte[] Bytes, string MimeType)
{
    /// <summary>
    /// A picked image, its format judged by its bytes (<see cref="Art.ImageInfo"/>),
    /// never its file name: until session 69 a WebP renamed .jpg was embedded,
    /// labelled JPEG, as the only cover. Null for anything that isn't a readable
    /// JPEG or PNG.
    /// </summary>
    public static ArtPayload? FromImage(byte[] bytes) => Art.ImageInfo.Read(bytes)?.Format switch
    {
        Art.ImageFormat.Jpeg => new ArtPayload(bytes, "image/jpeg"),
        Art.ImageFormat.Png => new ArtPayload(bytes, "image/png"),
        _ => null,
    };
}
