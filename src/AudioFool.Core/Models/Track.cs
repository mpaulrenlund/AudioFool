using System.Text.Json.Serialization;

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
    public int? Year { get; init; }

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
        Title = Title,
        Artist = Artist,
        AlbumArtist = AlbumArtist,
        Album = Album,
        Duration = Duration,
        DiscNumber = DiscNumber,
        Year = Year,
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

    public override string ToString() => $"{TrackNumber}. {DisplayTitle}";
}
