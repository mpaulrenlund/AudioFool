using AudioFool.Core.Analysis;
using AudioFool.Core.Library;
using AudioFool.Core.Models;

namespace AudioFool.Core.Art;

/// <summary>
/// PNG covers, counted for the Statistics window, on the way to replacing them
/// with JPEGs. Two kinds, two rows: pictures embedded in the songs, which the cover
/// check reads (<see cref="CoverFinding.PngBytes"/>), and .png files in the songs'
/// folders, found by looking in those folders.
/// </summary>
public sealed class PngCoverStatistics
{
    public const string EmbeddedLabel = "Embedded PNG covers";
    public const string FilesLabel = "PNG cover files";

    public required int Total { get; init; }

    /// <summary>Tracks the cover check has read.</summary>
    public required int Checked { get; init; }

    public required int EmbeddedTracks { get; init; }
    public required int EmbeddedAlbums { get; init; }
    public required long EmbeddedBytes { get; init; }
    public TrackFilter? EmbeddedFilter { get; init; }

    /// <summary>Null until the folders have been looked in.</summary>
    public PngFileCount? Files { get; init; }

    public int Unchecked => Total - Checked;

    /// <summary>The embedded side only, from the cover check's results; no disk reads.</summary>
    public static PngCoverStatistics Compute(IReadOnlyList<Track> tracks, CoverCache cache, PngFileCount? files = null)
    {
        var checkedCount = 0;
        var found = new List<Track>();
        long bytes = 0;
        foreach (var track in tracks)
        {
            if (!cache.TryGet(track, out var finding))
                continue;

            checkedCount++;
            if (finding.HasPng)
            {
                found.Add(track);
                bytes += finding.PngBytes;
            }
        }

        // By cache key, as the Extra covers row is: a converted file gets a new
        // key, so it leaves the filter.
        var keys = found.Select(QualityCache.KeyOf).ToHashSet(StringComparer.Ordinal);

        return new PngCoverStatistics
        {
            Total = tracks.Count,
            Checked = checkedCount,
            EmbeddedTracks = found.Count,
            EmbeddedAlbums = LibraryScanner.Build(found).Artists.Sum(a => a.Albums.Count),
            EmbeddedBytes = bytes,
            EmbeddedFilter = found.Count == 0
                ? null
                : new TrackFilter(EmbeddedLabel, t => keys.Contains(QualityCache.KeyOf(t))) { PngCovers = PngCoverKind.Embedded },
            Files = files,
        };
    }

    /// <summary>
    /// Looks for .png files in every folder holding one of <paramref name="tracks"/>.
    /// Any name counts, not only cover.png: a back scan is a PNG to convert too.
    /// A folder that can't be read (an unplugged drive) is skipped, not counted.
    /// </summary>
    public static PngFileCount CountFiles(IReadOnlyList<Track> tracks, CancellationToken cancel = default)
    {
        var folders = tracks
            .Select(t => Path.GetDirectoryName(t.FilePath))
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var withPng = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var files = 0;
        long bytes = 0;
        foreach (var folder in folders)
        {
            cancel.ThrowIfCancellationRequested();
            try
            {
                foreach (var file in new DirectoryInfo(folder).EnumerateFiles("*.png"))
                {
                    // EnumerateFiles' pattern also matches ".pngx" style names on
                    // some systems; the extension decides.
                    if (!file.Extension.Equals(".png", StringComparison.OrdinalIgnoreCase))
                        continue;
                    withPng.Add(folder);
                    files++;
                    bytes += file.Length;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
            }
        }

        var inThem = tracks
            .Where(t => Path.GetDirectoryName(t.FilePath) is { } d && withPng.Contains(d))
            .ToList();

        var set = new PngFolderSet(withPng);
        return new PngFileCount(
            Files: files,
            Bytes: bytes,
            Folders: withPng.Count,
            AllFolders: folders.Count,
            Albums: LibraryScanner.Build(inThem).Artists.Sum(a => a.Albums.Count),
            Filter: files == 0
                ? null
                : new TrackFilter(FilesLabel, t => Path.GetDirectoryName(t.FilePath) is { } d && set.Contains(d))
                {
                    PngCovers = PngCoverKind.Files,
                    PngFolders = set,
                });
    }
}

/// <summary>Which PNG COVERS row a filter is.</summary>
public enum PngCoverKind
{
    /// <summary>Songs with a PNG embedded.</summary>
    Embedded,

    /// <summary>Folders holding .png files.</summary>
    Files,
}

/// <summary>The folders the "PNG cover files" row found; thread-safe, since a conversion takes them out as it goes.</summary>
public sealed class PngFolderSet
{
    private readonly HashSet<string> _folders;
    private readonly object _lock = new();

    public PngFolderSet(IEnumerable<string> folders) =>
        _folders = new HashSet<string>(folders, StringComparer.OrdinalIgnoreCase);

    public bool Contains(string folder)
    {
        lock (_lock)
            return _folders.Contains(folder);
    }

    public void Remove(string folder)
    {
        lock (_lock)
            _folders.Remove(folder);
    }
}

/// <summary>The .png files found in the library's folders.</summary>
/// <param name="Folders">Folders holding at least one.</param>
/// <param name="AllFolders">Folders looked in.</param>
/// <param name="Filter">Narrows the library to the songs in those folders; null when there are none.</param>
public sealed record PngFileCount(int Files, long Bytes, int Folders, int AllFolders, int Albums, TrackFilter? Filter);
