using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AudioFool.Core.Art;
using AudioFool.Core.Library;
using AudioFool.Core.Models;
using AudioFool.Formatting;
using AudioFool.Services;

namespace AudioFool.ViewModels;

/// <summary>
/// Convert PNG Covers to JPEG, from the artist and album menus while a PNG COVERS
/// row's filter is on. Under "Embedded PNG covers" each song's cover is re-encoded
/// and swapped in through the checked rewrite (<see cref="PngCoverConverter"/>);
/// under "PNG cover files" each folder's .png files become JPEGs of the same name
/// and the PNGs go to the Recycle Bin. Built as Keep Best Cover is: folder by
/// folder off the UI thread, saved every 30 seconds, stoppable from the same menu.
/// </summary>
public sealed partial class MainViewModel
{
    private CancellationTokenSource? _pngCts;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsProgress), nameof(ProgressFraction), nameof(ConvertPngHeader), nameof(CanConvertPngs), nameof(CanKeepBestCover))]
    private bool _isConvertingPngs;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressFraction))]
    private (int Done, int Total) _pngProgress;

    /// <summary>The menus' item: "Convert PNG Covers to JPEG", or while a run goes, the way to stop it.</summary>
    public string ConvertPngHeader => IsConvertingPngs ? "Stop Converting PNG Covers" : "Convert PNG Covers to JPEG";

    /// <summary>While a PNG COVERS row's filter is on, and no other rewrite is running.</summary>
    public bool CanConvertPngs => IsConvertingPngs
        || (!IsPlaylistMode && !IsKeepingCovers && LibraryFilter?.PngCovers is not null);

    [RelayCommand]
    private void ConvertPngsForAlbums(AlbumItemViewModel? item)
    {
        if (IsConvertingPngs)
            _pngCts?.Cancel();
        else if (item is not null && CanConvertPngs)
            StartConvertingPngs(AlbumMenuTracks(item));
    }

    /// <summary>The songs the filter shows of the artist, or of every selected artist; Ctrl+A does the library.</summary>
    [RelayCommand]
    private void ConvertPngsForArtists(ArtistGroup? artist)
    {
        if (IsConvertingPngs)
            _pngCts?.Cancel();
        else if (artist is not null && CanConvertPngs)
            StartConvertingPngs(ArtistsSelection(artist).Tracks);
    }

    private void StartConvertingPngs(IReadOnlyList<Track> picked)
    {
        if (LibraryFilter is not { PngCovers: { } kind } filter)
            return;

        var tracks = filter.Apply(picked).Select(LibraryCopyOf).ToList();
        if (tracks.Count == 0)
            return;

        _ = kind == PngCoverKind.Embedded
            ? ConvertEmbeddedAsync(tracks)
            : ConvertFilesAsync(tracks, filter.PngFolders);
    }

    private static IEnumerable<IGrouping<string, Track>> ByFolder(IReadOnlyList<Track> tracks) => tracks
        .GroupBy(t => Path.GetDirectoryName(t.FilePath) ?? "", StringComparer.OrdinalIgnoreCase)
        .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase);

    private async Task ConvertEmbeddedAsync(IReadOnlyList<Track> tracks)
    {
        using var writing = BeginWrite();
        var cancel = BeginPngRun(tracks.Count, $"Converting the PNG cover in {Songs(tracks.Count)}…");
        var cache = CoverResults;
        var results = new List<CoverCleanResult>();
        var pending = new List<string>();
        var lastCommit = DateTime.UtcNow;

        try
        {
            foreach (var folder in ByFolder(tracks))
            {
                if (cancel.IsCancellationRequested)
                    break;

                var songs = folder.ToList();
                var done = await Task.Run(() =>
                {
                    var list = new List<CoverCleanResult>();
                    foreach (var song in songs)
                    {
                        if (cancel.IsCancellationRequested)
                            break;
                        list.Add(PngCoverConverter.ConvertEmbedded(song.FilePath, CoverJpeg.ForConversion, _engine));
                    }
                    return list;
                });

                results.AddRange(done);
                pending.AddRange(done.Where(r => r.Outcome == CoverCleanOutcome.Cleaned).Select(r => r.Path));
                PngProgress = (results.Count, tracks.Count);
                StatusText = $"Converting PNG covers... {results.Count:N0} of {tracks.Count:N0} songs · saved {Display.Size(Saved(results))}";

                if (DateTime.UtcNow - lastCommit >= KeepCommitEvery)
                {
                    await CommitKeptCoversAsync(pending, cache);
                    pending.Clear();
                    lastCommit = DateTime.UtcNow;
                }
            }

            await CommitKeptCoversAsync(pending, cache);
            StatusText = WithFilterProgress(DescribeEmbedded(results, tracks.Count));
        }
        catch (Exception ex)
        {
            await CommitKeptCoversAsync(pending, cache);
            StatusText = $"Converting PNG covers stopped: {ex.Message}";
        }
        finally
        {
            EndPngRun();
        }
    }

    private async Task ConvertFilesAsync(IReadOnlyList<Track> tracks, PngFolderSet? folders)
    {
        using var writing = BeginWrite();
        var groups = ByFolder(tracks).ToList();
        var cancel = BeginPngRun(groups.Count, $"Converting the PNG files in {Folders(groups.Count)}…");
        var cache = CoverResults;
        var results = new List<PngFileResult>();
        var pending = new List<string>();
        var foldersDone = 0;
        var lastCommit = DateTime.UtcNow;

        try
        {
            foreach (var group in groups)
            {
                if (cancel.IsCancellationRequested)
                    break;

                var done = await Task.Run(() => PngCoverConverter.ConvertFolder(group.Key, CoverJpeg.ForConversion, RecycleBin.Send));
                results.AddRange(done);
                foldersDone++;

                if (done.Any(r => r.Outcome is not PngFileOutcome.Failed))
                {
                    // Its songs are read again so they point at the new cover file.
                    pending.AddRange(_library.AllTracks
                        .Where(t => string.Equals(Path.GetDirectoryName(t.FilePath), group.Key, StringComparison.OrdinalIgnoreCase))
                        .Select(t => t.FilePath));
                }

                // A folder with no PNG left leaves the filter.
                if (done.All(r => r.Outcome is PngFileOutcome.Converted or PngFileOutcome.Duplicate))
                    folders?.Remove(group.Key);

                PngProgress = (foldersDone, groups.Count);
                StatusText = $"Converting PNG files... {foldersDone:N0} of {groups.Count:N0} folders · saved {Display.Size(results.Sum(r => r.Saved))}";

                if (DateTime.UtcNow - lastCommit >= KeepCommitEvery)
                {
                    await CommitKeptCoversAsync(pending, cache);
                    pending.Clear();
                    lastCommit = DateTime.UtcNow;
                }
            }

            await CommitKeptCoversAsync(pending, cache);
            StatusText = WithFilterProgress(DescribeFiles(results, foldersDone, groups.Count));
        }
        catch (Exception ex)
        {
            await CommitKeptCoversAsync(pending, cache);
            StatusText = $"Converting PNG files stopped: {ex.Message}";
        }
        finally
        {
            EndPngRun();
        }
    }

    private CancellationToken BeginPngRun(int total, string status)
    {
        _pngCts?.Dispose();
        _pngCts = new CancellationTokenSource();
        PngProgress = (0, total);
        IsConvertingPngs = true;
        StatusText = status;
        return _pngCts.Token;
    }

    private void EndPngRun()
    {
        IsConvertingPngs = false;
        CoverResultsChanged?.Invoke(this, EventArgs.Empty);
        ReclaimScanMemory();
    }

    private static long Saved(IEnumerable<CoverCleanResult> results) =>
        results.Where(r => r.Outcome == CoverCleanOutcome.Cleaned).Sum(r => r.BytesFreed);

    private static string Folders(int count) => count == 1 ? "1 folder" : $"{count:N0} folders";

    /// <summary>"Converted the PNG cover in 14 songs to JPEG · saved 38.2 MB · 1 skipped (a.mp3: ...)".</summary>
    private static string DescribeEmbedded(IReadOnlyList<CoverCleanResult> results, int total)
    {
        var converted = results.Count(r => r.Outcome == CoverCleanOutcome.Cleaned);
        var failed = results.Where(r => r.Outcome == CoverCleanOutcome.Failed).ToList();

        var parts = new List<string>
        {
            converted == 0
                ? "No song's cover was converted"
                : $"Converted the PNG cover in {Songs(converted)} to JPEG · saved {Display.Size(Saved(results))}",
        };

        if (results.Count < total)
            parts.Add($"stopped after {results.Count:N0} of {total:N0}; run it again for the rest");

        if (failed.Count > 0)
            parts.Add($"{failed.Count:N0} skipped, left as they were ({Path.GetFileName(failed[0].Path)}: {failed[0].Error})");

        return string.Join(" · ", parts) + ".";
    }

    /// <summary>"Converted 70 PNG files to JPEG · removed 58 duplicates · saved 200 MB · 1 kept (...)".</summary>
    private static string DescribeFiles(IReadOnlyList<PngFileResult> results, int foldersDone, int folders)
    {
        static string Files(int n) => n == 1 ? "1 PNG file" : $"{n:N0} PNG files";

        var converted = results.Count(r => r.Outcome == PngFileOutcome.Converted);
        var duplicates = results.Count(r => r.Outcome == PngFileOutcome.Duplicate);
        var kept = results.Where(r => r.Outcome == PngFileOutcome.Kept).ToList();
        var failed = results.Where(r => r.Outcome == PngFileOutcome.Failed).ToList();

        var parts = new List<string>
        {
            converted == 0 && duplicates == 0 ? "No PNG file was converted" : $"Converted {Files(converted)} to JPEG",
        };
        if (duplicates > 0)
            parts.Add($"removed {Files(duplicates)} already there as JPEG");
        parts.Add($"saved {Display.Size(results.Sum(r => r.Saved))}");
        if (foldersDone < folders)
            parts.Add($"stopped after {foldersDone:N0} of {folders:N0} folders; run it again for the rest");
        if (kept.Count > 0)
            parts.Add($"{kept.Count:N0} PNG kept beside its JPEG ({Path.GetFileName(kept[0].Path)}: {kept[0].Error})");
        if (failed.Count > 0)
            parts.Add($"{failed.Count:N0} skipped, left as they were ({Path.GetFileName(failed[0].Path)}: {failed[0].Error})");

        return string.Join(" · ", parts) + ".";
    }
}
