using System.Collections.Concurrent;
using AudioFool.Core.Models;

namespace AudioFool.Core.Library;

/// <summary>
/// Walks folders, reads tags in parallel, and assembles the Artist -> Album -> Song
/// tree with <see cref="SortRules"/> already applied at every level.
/// </summary>
public static class LibraryScanner
{
    public const string UnknownArtist = "Unknown Artist";
    public const string UnknownAlbum = "Unknown Album";


    /// <summary>
    /// Scans the given folders. When <paramref name="known"/> is supplied, any file
    /// whose size and write time still match its cached entry keeps the tags it
    /// already had instead of being re-read - the difference between an 18-second
    /// startup and a one-second one.
    /// <para>
    /// Folders that aren't reachable - an unplugged portable drive, most often - are
    /// reported rather than reconciled. Their tracks are carried over untouched, so
    /// a disconnected drive can never be mistaken for a deleted library.
    /// </para>
    /// </summary>
    public static async Task<ScanResult> ScanAsync(
        IReadOnlyList<string> rootFolders,
        IReadOnlyDictionary<string, Track>? known = null,
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var reachable = new List<string>();
        var unavailable = new List<string>();

        foreach (var folder in rootFolders)
        {
            if (string.IsNullOrWhiteSpace(folder))
                continue;

            if (DirectoryReachable(folder))
                reachable.Add(folder);
            else
                unavailable.Add(folder);
        }

        var files = await Task.Run(() => EnumerateAudioFiles(reachable), cancellationToken)
            .ConfigureAwait(false);

        return await Task.Run(
            () => ScanFiles(files, unavailable, known, progress, cancellationToken),
            cancellationToken).ConfigureAwait(false);
    }

    private static ScanResult ScanFiles(
        List<string> files,
        List<string> unavailableFolders,
        IReadOnlyDictionary<string, Track>? known,
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        // Tracks on an unreachable drive are kept exactly as they were. Without
        // this, unplugging a drive would report the whole library as removed and
        // the cache would be overwritten with nothing.
        var carriedOver = new List<Track>();
        if (known is not null && unavailableFolders.Count > 0)
        {
            foreach (var track in known.Values)
            {
                if (unavailableFolders.Any(folder => LibraryRelocator.IsUnder(track.FilePath, folder)))
                    carriedOver.Add(track);
            }
        }

        // Decide reuse-or-read for every file we can actually see. Statting 26,000
        // files costs about a second against ~18 seconds to read their tags, so this
        // pass pays for itself even when nothing turns out to be reusable.
        var reusable = new List<Track>();
        var toRead = new List<(string Path, FileStamp Stamp)>();

        // Counted separately from "reusable": a file whose tags need re-reading has
        // still been *seen*, and must not also be counted as removed.
        var seenBefore = 0;

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var stamp = FileStamp.For(file);
            var cached = default(Track);
            var wasKnown = known is not null && known.TryGetValue(file, out cached);

            if (wasKnown)
                seenBefore++;

            if (wasKnown && cached!.MatchesFile(stamp.Length, stamp.ModifiedUtc))
                reusable.Add(cached);
            else
                toRead.Add((file, stamp));
        }

        // Removed means a cached entry we looked for and could not find - not one
        // that merely changed. Carried-over tracks live on folders we could not
        // reach, so they were never looked for either.
        var removed = known is null
            ? 0
            : Math.Max(0, known.Count - seenBefore - carriedOver.Count);


        // Progress counts only the files actually being read, so the bar reflects
        // the work left rather than sitting near 100% through a cached start.
        progress?.Report(new ScanProgress(toRead.Count, 0));

        var freshlyRead = toRead.Count == 0
            ? []
            : ReadTags(toRead, progress, cancellationToken);

        var all = new List<Track>(reusable.Count + freshlyRead.Count + carriedOver.Count);
        all.AddRange(reusable);
        all.AddRange(freshlyRead);
        all.AddRange(carriedOver);

        return new ScanResult
        {
            Library = Build(all),
            Summary = new ScanSummary(reusable.Count + carriedOver.Count, freshlyRead.Count, removed),
            UnavailableFolders = unavailableFolders,
        };
    }

    /// <summary>
    /// Whether a folder can be read right now. A removable drive that has been
    /// pulled reports as simply absent, which is what we need to distinguish.
    /// </summary>
    private static bool DirectoryReachable(string folder)
    {
        try
        {
            return Directory.Exists(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }


    /// <summary>Recursive file walk, skipping folders we aren't allowed to read.</summary>
    public static List<string> EnumerateAudioFiles(IReadOnlyList<string> rootFolders)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
        };

        var results = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in rootFolders)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
                continue;

            try
            {
                foreach (var file in Directory.EnumerateFiles(root, "*", options))
                {
                    if (AudioFormats.IsSupported(file) && seen.Add(file))
                        results.Add(file);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A root that vanished or locked mid-scan shouldn't kill the whole scan.
            }
        }

        return results;
    }

    private static List<Track> ReadTags(
        List<(string Path, FileStamp Stamp)> files,
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        var bag = new ConcurrentBag<Track>();
        var read = 0;

        var parallelOptions = new ParallelOptions
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = Environment.ProcessorCount,
        };

        Parallel.ForEach(files, parallelOptions, file =>
        {
            bag.Add(TagReader.Read(file.Path, file.Stamp));

            // Report roughly every 1%, so a 50k-file scan doesn't drown the UI thread.
            var done = Interlocked.Increment(ref read);
            var step = Math.Max(1, files.Count / 100);
            if (done % step == 0 || done == files.Count)
                progress?.Report(new ScanProgress(files.Count, done));
        });

        return [.. bag];
    }

    /// <summary>Groups flat tracks into the sorted artist/album tree.</summary>
    public static MusicLibrary Build(IEnumerable<Track> tracks)
    {
        var allTracks = tracks.ToList();

        var artists = allTracks
            .GroupBy(t => DisplayName(t.GroupingArtist, UnknownArtist), SortRules.NameComparer)
            .Select(BuildArtist);

        return new MusicLibrary
        {
            Artists = [.. SortRules.SortArtists(artists)],
            AllTracks = allTracks,
        };
    }

    private static ArtistGroup BuildArtist(IGrouping<string, Track> group)
    {
        // Use the most common spelling as the display name; tags are inconsistent
        // about casing and the sidebar shouldn't flip-flop between scans.
        var name = group
            .Select(t => DisplayName(t.GroupingArtist, UnknownArtist))
            .GroupBy(n => n, StringComparer.Ordinal)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .First().Key;

        var albums = group
            .GroupBy(t => DisplayName(t.Album, UnknownAlbum), SortRules.NameComparer)
            .Select(albumGroup => BuildAlbum(name, albumGroup));

        return new ArtistGroup
        {
            Name = name,
            SortKey = SortRules.ArtistSortKey(name),
            Albums = [.. SortRules.SortAlbums(albums)],
        };
    }

    private static Album BuildAlbum(string artistName, IGrouping<string, Track> group)
    {
        var tracks = SortRules.SortTracks(group).ToList();

        return new Album
        {
            Title = group.Key,
            ArtistName = artistName,
            // Earliest tagged year wins: reissue tags on a few tracks shouldn't
            // drag a 1969 album down to the bottom of the list.
            Year = tracks.Select(t => t.Year).Where(y => y.HasValue).Min(),
            Tracks = tracks,
            FolderArtPath = tracks.Select(t => t.FolderArtPath).FirstOrDefault(p => p is not null),
        };
    }

    private static string DisplayName(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
}
