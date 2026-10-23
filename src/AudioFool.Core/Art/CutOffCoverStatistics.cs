using AudioFool.Core.Analysis;
using AudioFool.Core.Library;
using AudioFool.Core.Models;

namespace AudioFool.Core.Art;

/// <summary>
/// Cut-off covers (<see cref="CutOffImage"/>), counted for the Statistics window
/// (session 70). Two kinds, two rows, as PNG COVERS has: pictures embedded in the
/// songs, which the cover check reads (<see cref="CoverFinding.CutOff"/>), and the
/// folder cover files the songs show (<see cref="Track.FolderArtPath"/>), read when
/// the window opens.
/// </summary>
public sealed class CutOffCoverStatistics
{
    public const string EmbeddedLabel = "Cut off in songs";
    public const string FilesLabel = "Cut-off cover files";

    public required int Total { get; init; }

    /// <summary>Tracks the cover check has read.</summary>
    public required int Checked { get; init; }

    public required int EmbeddedTracks { get; init; }
    public required int EmbeddedAlbums { get; init; }
    public TrackFilter? EmbeddedFilter { get; init; }

    /// <summary>Null until the files have been read.</summary>
    public CutOffFileCount? Files { get; init; }

    public int Unchecked => Total - Checked;

    /// <summary>The embedded side only, from the cover check's results; no disk reads.</summary>
    public static CutOffCoverStatistics Compute(IReadOnlyList<Track> tracks, CoverCache cache, CutOffFileCount? files = null)
    {
        var checkedCount = 0;
        var found = new List<Track>();
        foreach (var track in tracks)
        {
            if (!cache.TryGet(track, out var finding))
                continue;

            checkedCount++;
            if (finding.HasCutOff)
                found.Add(track);
        }

        // By cache key, as the other cover rows are: a fixed file gets a new key, so it leaves the filter.
        var keys = found.Select(QualityCache.KeyOf).ToHashSet(StringComparer.Ordinal);

        return new CutOffCoverStatistics
        {
            Total = tracks.Count,
            Checked = checkedCount,
            EmbeddedTracks = found.Count,
            EmbeddedAlbums = LibraryScanner.Build(found).Artists.Sum(a => a.Albums.Count),
            EmbeddedFilter = found.Count == 0
                ? null
                : new TrackFilter(EmbeddedLabel, t => keys.Contains(QualityCache.KeyOf(t))),
            Files = files,
        };
    }

    /// <summary>
    /// Reads every folder cover file the tracks show, each from its end first
    /// (<see cref="CutOffImage.IsFile"/>), so a whole one costs one small read. A
    /// file that can't be read (an unplugged drive) isn't counted as cut off.
    /// </summary>
    public static CutOffFileCount CountFiles(IReadOnlyList<Track> tracks, CancellationToken cancel = default)
    {
        var files = tracks
            .Select(t => t.FolderArtPath)
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var cutOff = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            cancel.ThrowIfCancellationRequested();
            if (CutOffImage.IsFile(file))
                cutOff.Add(file);
        }

        var showing = tracks.Where(t => t.FolderArtPath is { } f && cutOff.Contains(f)).ToList();
        return new CutOffFileCount(
            Files: cutOff.Count,
            AllFiles: files.Count,
            Albums: LibraryScanner.Build(showing).Artists.Sum(a => a.Albums.Count),
            Filter: cutOff.Count == 0
                ? null
                : new TrackFilter(FilesLabel, t => t.FolderArtPath is { } f && cutOff.Contains(f)));
    }
}

/// <summary>The cut-off folder cover files.</summary>
/// <param name="Files">Cut-off files.</param>
/// <param name="AllFiles">Files read.</param>
/// <param name="Filter">Narrows the library to the songs showing those files; null when there are none.</param>
public sealed record CutOffFileCount(int Files, int AllFiles, int Albums, TrackFilter? Filter);
