using System.Text.Json.Serialization;
using AudioFool.Core.Library;

namespace AudioFool.Core.Models;

/// <summary>
/// One audio file and everything we know about it. Immutable once scanned.
/// <para>
/// Serialised straight into the library cache, so the shape of this type is a file
/// format. Adding a property is safe; renaming or removing one needs
/// <see cref="Library.LibraryCache.CurrentVersion"/> bumped.
/// </para>
/// </summary>
public sealed class Track
{
    public required string FilePath { get; init; }

    /// <summary>
    /// File size and last-write time, used to decide whether the cached tags are
    /// still good. Re-reading tags costs about 0.7 ms per file; comparing these
    /// two numbers costs nothing, which is the whole basis of the cache.
    /// </summary>
    public long FileSize { get; init; }

    public DateTime ModifiedUtc { get; init; }

    public int? TrackNumber { get; init; }

    /// <summary>
    /// Total tracks on this disc - the "12" in "3/12". Null for the roughly one
    /// file in five that doesn't carry it; the grid then shows the bare number.
    /// </summary>
    public int? TrackCount { get; init; }

    public string Title { get; init; } = "";
    public string Artist { get; init; } = "";

    /// <summary>
    /// The album's credited artist. Distinct from <see cref="Artist"/> so that
    /// compilations and "feat." guests don't fragment the artist list.
    /// </summary>
    public string AlbumArtist { get; init; } = "";

    public string Album { get; init; } = "";
    public TimeSpan Duration { get; init; }
    public int? DiscNumber { get; init; }

    /// <summary>Total discs in the set. Only about one album in eight has more than one.</summary>
    public int? DiscCount { get; init; }

    public int? Year { get; init; }

    /// <summary>
    /// The full release date, when the file names more than the year
    /// ("2026-10-02" or "2026-10"); null otherwise, which is most MP3s and about
    /// two FLACs in three. Only the album sort uses it - see <see cref="SortDate"/>.
    /// </summary>
    public string? ReleaseDate { get; init; }

    /// <summary>
    /// What albums sort on: the release date, or the year alone. ISO dates sort
    /// correctly as plain text, and a bare "2026" sorts before "2026-10-02" - an
    /// album known only by its year files at the start of that year.
    /// </summary>
    [JsonIgnore]
    public string? SortDate => ReleaseDate ?? Year?.ToString("0000", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Container/codec shown in the Kind column, e.g. "FLAC", "MP3", "DSD".</summary>
    public string Kind { get; init; } = "";

    public int? Bitrate { get; init; }

    /// <summary>
    /// Source bit depth. Null for lossy codecs (MP3, AAC, Vorbis, Opus, WMA),
    /// which decode to float and have no meaningful source depth.
    /// </summary>
    public int? BitDepth { get; init; }

    public int? SampleRate { get; init; }

    /// <summary>Path to a cover image sitting next to the file, if one was found.</summary>
    public string? FolderArtPath { get; init; }

    /// <summary>
    /// The name the artist list groups and sorts on: album artist when tagged,
    /// otherwise the track artist.
    /// </summary>
    [JsonIgnore]
    public string GroupingArtist =>
        string.IsNullOrWhiteSpace(AlbumArtist) ? Artist : AlbumArtist;

    /// <summary>Display title, falling back to the file name for untagged files.</summary>
    [JsonIgnore]
    public string DisplayTitle =>
        string.IsNullOrWhiteSpace(Title) ? Path.GetFileNameWithoutExtension(FilePath) : Title;

    /// <summary>
    /// A copy pointing at a new location, with everything else carried over.
    /// Used when a music folder changes drive letter, so the cached tags survive
    /// instead of the whole library needing a rescan.
    /// </summary>
    public Track Relocated(string filePath, string? folderArtPath) => new()
    {
        FilePath = filePath,
        FolderArtPath = folderArtPath,

        FileSize = FileSize,
        ModifiedUtc = ModifiedUtc,
        TrackNumber = TrackNumber,
        TrackCount = TrackCount,
        Title = Title,
        Artist = Artist,
        AlbumArtist = AlbumArtist,
        Album = Album,
        Duration = Duration,
        DiscNumber = DiscNumber,
        DiscCount = DiscCount,
        Year = Year,
        ReleaseDate = ReleaseDate,
        Kind = Kind,
        Bitrate = Bitrate,
        BitDepth = BitDepth,
        SampleRate = SampleRate,
    };

    /// <summary>
    /// True when the file on disk still matches what was cached. Size and write
    /// time together are enough: a tag edit rewrites the file and moves both.
    /// </summary>
    public bool MatchesFile(long length, DateTime modifiedUtc) =>
        FileSize == length && ModifiedUtc == modifiedUtc;

    /// <summary>
    /// A copy with a single-track tag edit applied, re-stamped to the file's new
    /// size/write time so <see cref="MatchesFile"/> reflects what was just written
    /// instead of flagging the file as changed on the next scan.
    /// </summary>
    public Track WithTags(TrackTagEdit edit, FileStamp stamp, string? folderArtPath) => new()
    {
        FilePath = FilePath,
        FolderArtPath = folderArtPath,

        FileSize = stamp.Length,
        ModifiedUtc = stamp.ModifiedUtc,
        TrackNumber = edit.TrackNumber,
        TrackCount = edit.TrackCount,
        Title = edit.Title,
        Artist = edit.Artist,
        AlbumArtist = edit.AlbumArtist,
        Album = edit.Album,
        Duration = Duration,
        DiscNumber = edit.DiscNumber,
        DiscCount = edit.DiscCount,
        Year = edit.Year,
        ReleaseDate = edit.Date,
        Kind = Kind,
        Bitrate = Bitrate,
        BitDepth = BitDepth,
        SampleRate = SampleRate,
    };

    /// <summary>
    /// A copy with a whole-album batch edit applied. Title and track number carry
    /// over unchanged - they are not part of an album-level edit - and so do the
    /// counts and disc number unless the edit sets them.
    /// </summary>
    public Track WithAlbumTags(AlbumTagEdit edit, FileStamp stamp, string? folderArtPath) => new()
    {
        FilePath = FilePath,
        FolderArtPath = folderArtPath,

        FileSize = stamp.Length,
        ModifiedUtc = stamp.ModifiedUtc,
        TrackNumber = TrackNumber,
        TrackCount = edit.TrackCount is { } trackCount ? trackCount.Value : TrackCount,
        Title = Title,
        Artist = edit.Artist,
        AlbumArtist = edit.AlbumArtist,
        Album = edit.Album,
        Duration = Duration,
        DiscNumber = edit.DiscNumber is { } discNumber ? discNumber.Value : DiscNumber,
        DiscCount = edit.DiscCount is { } discCount ? discCount.Value : DiscCount,
        Year = edit.Year,
        ReleaseDate = edit.Date,
        Kind = Kind,
        Bitrate = Bitrate,
        BitDepth = BitDepth,
        SampleRate = SampleRate,
    };

    public override string ToString() => $"{TrackNumber}. {DisplayTitle}";
}
