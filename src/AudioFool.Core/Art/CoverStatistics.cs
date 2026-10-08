using AudioFool.Core.Analysis;
using AudioFool.Core.Library;
using AudioFool.Core.Models;

namespace AudioFool.Core.Art;

/// <summary>
/// The cover check's results, counted for the Statistics window: how many tracks
/// have been read, and the one row of tracks carrying more than one picture.
/// </summary>
public sealed class CoverStatistics
{
    public const string Label = "Extra covers";

    public required int Total { get; init; }
    public required int Checked { get; init; }

    /// <summary>Tracks with more than one picture.</summary>
    public required int Tracks { get; init; }

    /// <summary>The albums those tracks are in, grouped as the sidebar groups them.</summary>
    public required int Albums { get; init; }

    /// <summary>What keeping only the best cover in each would free.</summary>
    public required long Freeable { get; init; }

    /// <summary>Narrows the library to the tracks with extra covers; null when there are none.</summary>
    public TrackFilter? Filter { get; init; }

    public int Unchecked => Total - Checked;

    public static CoverStatistics Compute(IReadOnlyList<Track> tracks, CoverCache cache)
    {
        var checkedCount = 0;
        var found = new List<Track>();
        long freeable = 0;
        foreach (var track in tracks)
        {
            if (!cache.TryGet(track, out var finding))
                continue;

            checkedCount++;
            if (finding.HasExtras && CoverCleaner.CanClean(track.FilePath))
            {
                found.Add(track);
                freeable += finding.Freeable;
            }
        }

        // By cache key, not by track: a rebuilt library has new Track objects for
        // the same files, and a cleaned file gets a new key, so it leaves the filter.
        var keys = found.Select(QualityCache.KeyOf).ToHashSet(StringComparer.Ordinal);

        return new CoverStatistics
        {
            Total = tracks.Count,
            Checked = checkedCount,
            Tracks = found.Count,
            Albums = LibraryScanner.Build(found).Artists.Sum(a => a.Albums.Count),
            Freeable = freeable,
            Filter = found.Count == 0
                ? null
                : new TrackFilter(Label, t => keys.Contains(QualityCache.KeyOf(t))) { ShowsExtraCovers = true },
        };
    }
}
