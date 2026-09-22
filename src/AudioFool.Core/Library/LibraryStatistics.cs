using AudioFool.Core.Models;

namespace AudioFool.Core.Library;

/// <summary>
/// Aggregate facts about a library, for the Statistics window. Pure arithmetic
/// over tracks the scan already holds: no file is opened, so it costs a few
/// milliseconds over 26,000 tracks and can be recomputed every time the window
/// opens rather than cached and invalidated.
/// </summary>
public sealed class LibraryStatistics
{
    public int TrackCount { get; init; }
    public int AlbumCount { get; init; }
    public int ArtistCount { get; init; }
    public TimeSpan TotalDuration { get; init; }
    public long TotalBytes { get; init; }

    /// <summary>Artists with the most tracks, most first.</summary>
    public IReadOnlyList<ArtistStat> TopArtists { get; init; } = [];

    /// <summary>One row per Kind-column value ("FLAC", "MP3", ...), most tracks first.</summary>
    public IReadOnlyList<Slice> FileTypes { get; init; } = [];

    /// <summary>Tracks by <see cref="QualityTier"/>, in tier order, empty tiers left out.</summary>
    public IReadOnlyList<Slice> Quality { get; init; } = [];

    /// <summary>Tracks by decade of their Year tag, oldest first, untagged last.</summary>
    public IReadOnlyList<Slice> Decades { get; init; } = [];

    /// <summary>Every checked field, the most often missing first.</summary>
    public IReadOnlyList<TagGap> TagGaps { get; init; } = [];

    /// <summary>
    /// Tracks carrying every essential tag: title, artist, album, year and track
    /// number. The disc fields are left out on purpose - a single-disc album
    /// rarely carries them and loses nothing by it - as are the album artist
    /// (the grouping falls back to the track artist) and the cover file.
    /// </summary>
    public int FullyTaggedCount { get; init; }

    public static LibraryStatistics Empty { get; } = new();

    public const string UnknownDecade = "Unknown";

    public static LibraryStatistics Compute(MusicLibrary library, int topArtistCount = 5)
    {
        var tracks = library.AllTracks;
        var total = tracks.Count;
        if (total == 0)
            return Empty;

        return new LibraryStatistics
        {
            TrackCount = total,
            AlbumCount = library.AlbumCount,
            ArtistCount = library.Artists.Count,
            TotalDuration = TimeSpan.FromTicks(tracks.Sum(t => t.Duration.Ticks)),
            TotalBytes = tracks.Sum(t => t.FileSize),

            // Taken from the artist tree rather than regrouped here, so the names
            // and counts are exactly the ones the sidebar shows.
            TopArtists = [.. library.Artists
                .Select(a => new ArtistStat(
                    a.Name,
                    a.TrackCount,
                    a.Albums.Count,
                    TimeSpan.FromTicks(a.Albums.Sum(al => al.TotalDuration.Ticks)),
                    Share(a.TrackCount, total)))
                .OrderByDescending(a => a.TrackCount)
                .ThenBy(a => a.Name, SortRules.NameComparer)
                .Take(topArtistCount)],

            // Every row carries the filter that reproduces it, built from the same
            // key its group was formed on, so clicking a row can never show a
            // different set of tracks from the one it counted.
            FileTypes = [.. tracks
                .GroupBy(KindOf, StringComparer.OrdinalIgnoreCase)
                .Select(g => new Slice(g.Key, g.Count(), Share(g.Count(), total), g.Sum(t => t.FileSize))
                {
                    Filter = new TrackFilter(g.Key,
                        t => string.Equals(KindOf(t), g.Key, StringComparison.OrdinalIgnoreCase)),
                })
                .OrderByDescending(s => s.Count)
                .ThenBy(s => s.Label, StringComparer.OrdinalIgnoreCase)],

            Quality = [.. tracks
                .GroupBy(Classify)
                .OrderBy(g => g.Key)
                .Select(g => new Slice(Describe(g.Key), g.Count(), Share(g.Count(), total), g.Sum(t => t.FileSize))
                {
                    Filter = new TrackFilter(Describe(g.Key), t => Classify(t) == g.Key),
                })],

            Decades = [.. tracks
                .GroupBy(t => DecadeOf(t.Year))
                .OrderBy(g => g.Key ?? int.MaxValue)
                .Select(g => new Slice(
                    g.Key is { } d ? $"{d}s" : UnknownDecade,
                    g.Count(),
                    Share(g.Count(), total),
                    g.Sum(t => t.FileSize))
                {
                    Filter = new TrackFilter(
                        g.Key is { } d2 ? $"From the {d2}s" : "Missing Year",
                        t => DecadeOf(t.Year) == g.Key),
                })],

            TagGaps = [.. TagChecks
                .Select(c =>
                {
                    var missing = tracks.Count(c.IsMissing);
                    return new TagGap(c.Field, missing, Share(missing, total), c.Note)
                    {
                        Filter = new TrackFilter($"Missing {c.Field}", c.IsMissing),
                    };
                })
                .OrderByDescending(g => g.Missing)],

            FullyTaggedCount = tracks.Count(t => TagChecks.Where(c => c.Essential).All(c => !c.IsMissing(t))),
        };
    }

    /// <summary>The File types grouping key: the Kind column, or "Other" when blank.</summary>
    private static string KindOf(Track track) =>
        string.IsNullOrWhiteSpace(track.Kind) ? "Other" : track.Kind;

    // ------------------------------------------------------------ quality

    /// <summary>
    /// Classifies a track the way a listener would sort a collection when
    /// deciding what is worth upgrading. The order here is the display order.
    /// </summary>
    public static QualityTier Classify(Track track)
    {
        if (AudioFormats.IsDsd(track.FilePath))
            return QualityTier.Dsd;

        if (AudioFormats.IsModule(track.FilePath))
            return QualityTier.Module;

        // An .m4a is lossy by extension but may hold ALAC; TagReader has already
        // looked inside and said which, so Kind is the authority for it.
        var lossy = AudioFormats.IsLossy(track.FilePath)
                    && !string.Equals(track.Kind, "ALAC", StringComparison.OrdinalIgnoreCase);

        if (lossy)
            return track.Bitrate is >= HighBitrateKbps ? QualityTier.LossyHigh : QualityTier.LossyLow;

        return track.BitDepth is > 16 || track.SampleRate is > 48_000
            ? QualityTier.HiRes
            : QualityTier.CdQuality;
    }

    /// <summary>
    /// Where "good lossy" starts. 256 kbps is the iTunes Plus / Amazon rate;
    /// below it is where a lossless replacement is most audibly worth having.
    /// </summary>
    public const int HighBitrateKbps = 256;

    public static string Describe(QualityTier tier) => tier switch
    {
        QualityTier.HiRes => "Hi-res lossless",
        QualityTier.CdQuality => "CD-quality lossless",
        QualityTier.Dsd => "DSD",
        QualityTier.LossyHigh => $"Lossy, {HighBitrateKbps} kbps and up",
        QualityTier.LossyLow => $"Lossy, under {HighBitrateKbps} kbps",
        QualityTier.Module => "Tracker module",
        _ => tier.ToString(),
    };

    // -------------------------------------------------------------- decades

    /// <summary>1994 → 1990. Null for untagged years.</summary>
    public static int? DecadeOf(int? year) => year is > 0 ? year.Value / 10 * 10 : null;

    // ----------------------------------------------------------------- tags

    private sealed record TagCheck(string Field, Func<Track, bool> IsMissing, bool Essential = false, string? Note = null);

    private const string DiscNote = "Mostly single-disc albums, where it rarely matters";

    /// <summary>
    /// The fields the tag editor can write, plus the cover file. Each test is
    /// the same emptiness the grid would show as a blank cell.
    /// </summary>
    private static readonly TagCheck[] TagChecks =
    [
        new("Title", t => string.IsNullOrWhiteSpace(t.Title), Essential: true),
        new("Artist", t => string.IsNullOrWhiteSpace(t.Artist), Essential: true),
        new("Album Artist", t => string.IsNullOrWhiteSpace(t.AlbumArtist)),
        new("Album", t => string.IsNullOrWhiteSpace(t.Album), Essential: true),
        new("Year", t => t.Year is not > 0, Essential: true),
        new("Track #", t => t.TrackNumber is not > 0, Essential: true),
        new("Track total", t => t.TrackCount is not > 0),
        new("Disc #", t => t.DiscNumber is not > 0, Note: DiscNote),
        new("Disc total", t => t.DiscCount is not > 0, Note: DiscNote),

        // Embedded pictures are read on demand for display and never cached, so
        // only the folder image is knowable here without opening every file.
        new("Cover image file", t => t.FolderArtPath is null,
            Note: "Folder images only - embedded artwork is not counted"),
    ];

    private static double Share(int count, int total) => total == 0 ? 0 : (double)count / total;
}

/// <summary>Display order matters: the Quality list is sorted by this.</summary>
public enum QualityTier
{
    HiRes,
    CdQuality,
    Dsd,
    LossyHigh,
    LossyLow,
    Module,
}

/// <param name="Share">Fraction of all tracks, 0 to 1.</param>
public sealed record Slice(string Label, int Count, double Share, long Bytes)
{
    /// <summary>Selects exactly the <see cref="Count"/> tracks this row counted.</summary>
    public TrackFilter? Filter { get; init; }
}

public sealed record ArtistStat(string Name, int TrackCount, int AlbumCount, TimeSpan Duration, double Share);

/// <param name="Share">Fraction of all tracks missing this field, 0 to 1.</param>
public sealed record TagGap(string Field, int Missing, double Share, string? Note)
{
    /// <summary>Selects exactly the <see cref="Missing"/> tracks without this field.</summary>
    public TrackFilter? Filter { get; init; }
}

/// <summary>
/// A named subset of the library - "Missing Year", "FLAC", "From the 1990s" -
/// that the browser can be narrowed to. The name is what the filter chip and the
/// status bar show while it is on.
/// </summary>
public sealed class TrackFilter(string description, Func<Track, bool> matches)
{
    public string Description { get; } = description;

    public bool Matches(Track track) => matches(track);

    public override string ToString() => Description;
}
