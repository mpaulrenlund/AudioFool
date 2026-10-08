using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AudioFool.Core.Art;
using AudioFool.Core.Library;
using AudioFool.Core.Models;
using AudioFool.Formatting;
using AudioFool.Services;

namespace AudioFool.ViewModels;

/// <summary>
/// Extra embedded covers: the Statistics check that finds songs carrying the
/// cover more than once, and the artist and album menus' "Keep Best Cover", which rewrites
/// them with only the best one (<see cref="CoverCleaner"/>) and writes the
/// folder's cover.jpg. Results are kept in <c>covers.json</c>.
/// </summary>
public sealed partial class MainViewModel
{
    private CoverCache? _coverCache;
    private CancellationTokenSource? _coverCts;
    private Task<CoverScanSummary>? _coverScan;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsProgress), nameof(ProgressFraction))]
    private bool _isCheckingCovers;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressFraction))]
    private CoverScanProgress _coverProgress;

    /// <summary>Raised on the UI thread whenever new results have been saved, or songs cleaned.</summary>
    public event EventHandler? CoverResultsChanged;

    /// <summary>Where the results are kept; ThemeLab points it at a scratch file.</summary>
    public string CoverCachePath { get; set; } = CoverCache.DefaultPath;

    private CoverCache CoverResults => _coverCache ??= CoverCache.Load(CoverCachePath);

    /// <summary>
    /// While the "Extra covers" row's filter is on, the artist and album menus
    /// offer Keep Best Cover; while a run goes, they offer to stop it, filter or not.
    /// </summary>
    public bool CanKeepBestCover => IsKeepingCovers || (!IsPlaylistMode && LibraryFilter?.ShowsExtraCovers == true);

    /// <summary>Counted over what Statistics counts: the ticked folders, ignoring any search.</summary>
    public CoverStatistics ComputeCoverStatistics() =>
        CoverStatistics.Compute(_folderFilteredLibrary.AllTracks, CoverResults);

    /// <summary>Reads the pictures of every track not read yet, in the background, with progress in the status bar.</summary>
    public void StartCoverCheck()
    {
        if (!IsCheckingCovers)
            _ = RunCoverCheckAsync();
    }

    public void StopCoverCheck() => _coverCts?.Cancel();

    private async Task RunCoverCheckAsync()
    {
        var cache = CoverResults;
        var tracks = _folderFilteredLibrary.AllTracks;
        var dispatcher = Application.Current.Dispatcher;

        _coverCts?.Dispose();
        _coverCts = new CancellationTokenSource();
        CoverProgress = default;
        IsCheckingCovers = true;

        var progress = new Progress<CoverScanProgress>(p =>
        {
            CoverProgress = p;
            StatusText = $"Checking covers... {p.Done:N0} of {p.Total:N0} tracks"
                + (p.Found > 0 ? $" · {p.Found:N0} with extra covers" : "");
        });

        try
        {
            _coverScan = CoverScanner.RunAsync(tracks, cache, progress, _coverCts.Token,
                save: () =>
                {
                    cache.Save(CoverCachePath);
                    dispatcher.BeginInvoke(() => CoverResultsChanged?.Invoke(this, EventArgs.Empty));
                });

            // Kept, so closing the app can wait for the last save.
            var summary = await _coverScan;
            StatusText = DescribeCoverCheck(summary);
        }
        catch (Exception ex)
        {
            StatusText = $"Cover check failed: {ex.Message}";
        }
        finally
        {
            IsCheckingCovers = false;
            CoverResultsChanged?.Invoke(this, EventArgs.Empty);
            // Reading every cover churns through buffers, as a tag scan does.
            ReclaimScanMemory();
        }
    }

    private static string DescribeCoverCheck(CoverScanSummary s)
    {
        var found = s.Found == 1 ? "1 track with extra covers" : $"{s.Found:N0} tracks with extra covers";
        var message = s.Cancelled
            ? $"Cover check stopped after {s.Checked:N0} tracks · {found} · it carries on from there next time"
            : $"Cover check done: {s.Checked:N0} tracks checked · {found} · see Statistics";

        if (s.Missing > 0)
            message += $" · {s.Missing:N0} not found (is the drive connected?)";

        return message;
    }

    // ------------------------------------------------------------ keep best cover

    private CancellationTokenSource? _keepCts;

    /// <summary>How often the songs done so far are put into the library and saved, so a long run keeps its work.</summary>
    private static readonly TimeSpan KeepCommitEvery = TimeSpan.FromSeconds(30);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsProgress), nameof(ProgressFraction), nameof(KeepBestCoverHeader), nameof(CanKeepBestCover))]
    private bool _isKeepingCovers;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressFraction))]
    private (int Done, int Total) _keepProgress;

    /// <summary>The artist and album menus' item: "Keep Best Cover", or while a run goes, the way to stop it.</summary>
    public string KeepBestCoverHeader => IsKeepingCovers ? "Stop Keeping Best Covers" : "Keep Best Cover";

    /// <summary>
    /// The album menu's "Keep Best Cover": the songs the "Extra covers" filter
    /// shows of the album, or of every selected album.
    /// </summary>
    [RelayCommand]
    private void KeepBestCoverForAlbums(AlbumItemViewModel? item)
    {
        if (IsKeepingCovers)
            _keepCts?.Cancel();
        else if (item is not null && CanKeepBestCover)
            StartKeepingBestCovers(AlbumMenuTracks(item));
    }

    /// <summary>
    /// The artist menu's "Keep Best Cover": the songs the "Extra covers" filter
    /// shows of the artist, or of every selected artist, so Ctrl+A in Artists
    /// under the filter does the whole library.
    /// </summary>
    [RelayCommand]
    private void KeepBestCoverForArtists(ArtistGroup? artist)
    {
        if (IsKeepingCovers)
            _keepCts?.Cancel();
        else if (artist is not null && CanKeepBestCover)
            StartKeepingBestCovers(ArtistsSelection(artist).Tracks);
    }

    private void StartKeepingBestCovers(IReadOnlyList<Track> picked)
    {
        // Only what the filter shows: a song without extra covers has nothing to do.
        var tracks = (LibraryFilter is { } filter ? filter.Apply(picked) : picked).Select(LibraryCopyOf).ToList();
        if (tracks.Count == 0)
            return;

        _ = KeepBestCoversAsync(tracks);
    }

    /// <summary>
    /// Folder by folder: each song is rewritten with only its best cover and
    /// checked before it replaces the original (<see cref="CoverCleaner"/>), then
    /// the folder gets cover.jpg from the cover kept, unless it has one at least
    /// as big. Every 30 seconds, and at the end, the songs done are read again
    /// and put into the library, so they leave the filter, and covers.json and
    /// library.json are saved. Stopping finishes the song in hand; what's done
    /// stays done, and a second run picks up the rest.
    /// </summary>
    private async Task KeepBestCoversAsync(IReadOnlyList<Track> tracks)
    {
        _keepCts?.Dispose();
        _keepCts = new CancellationTokenSource();
        var cancel = _keepCts.Token;
        var cache = CoverResults;
        var folders = tracks
            .GroupBy(t => Path.GetDirectoryName(t.FilePath) ?? "", StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var results = new List<CoverCleanResult>();
        var extracts = new List<FolderExtract>();
        var pending = new List<string>();
        long freed = 0;
        var lastCommit = DateTime.UtcNow;

        KeepProgress = (0, tracks.Count);
        IsKeepingCovers = true;
        StatusText = $"Keeping the best cover in {Songs(tracks.Count)}...";

        try
        {
            foreach (var folder in folders)
            {
                if (cancel.IsCancellationRequested)
                    break;

                var (done, extract) = await Task.Run(() => CleanFolder(folder.ToList(), cancel));
                results.AddRange(done);
                if (extract is not null)
                    extracts.Add(extract);

                var cleaned = done.Where(r => r.Outcome == CoverCleanOutcome.Cleaned).ToList();
                pending.AddRange(cleaned.Select(r => r.Path));
                freed += cleaned.Sum(r => r.BytesFreed);
                KeepProgress = (results.Count, tracks.Count);
                StatusText = $"Keeping the best cover... {results.Count:N0} of {tracks.Count:N0} songs · freed {Display.Size(freed)}";

                if (DateTime.UtcNow - lastCommit >= KeepCommitEvery)
                {
                    await CommitKeptCoversAsync(pending, cache);
                    pending.Clear();
                    lastCommit = DateTime.UtcNow;
                }
            }

            await CommitKeptCoversAsync(pending, cache);
            StatusText = WithFilterProgress(DescribeKeepBestCover(results, extracts, tracks.Count));
        }
        catch (Exception ex)
        {
            await CommitKeptCoversAsync(pending, cache);
            StatusText = $"Keep Best Cover stopped: {ex.Message}";
        }
        finally
        {
            IsKeepingCovers = false;
            CoverResultsChanged?.Invoke(this, EventArgs.Empty);
            ReclaimScanMemory();
        }
    }

    /// <summary>One folder's songs, then its cover.jpg from the covers kept. Off the UI thread.</summary>
    private (List<CoverCleanResult> Done, FolderExtract? Extract) CleanFolder(List<Track> songs, CancellationToken cancel)
    {
        var done = new List<CoverCleanResult>();
        foreach (var song in songs)
        {
            if (cancel.IsCancellationRequested)
                break;
            done.Add(CoverCleaner.Clean(song.FilePath, _engine));
        }

        var cleaned = done.Where(r => r.Outcome == CoverCleanOutcome.Cleaned).Select(r => r.Path).ToList();
        var extract = cleaned.Count == 0 ? null : EmbeddedArtExtractor.ExtractToFolders(cleaned, CoverJpeg.FromPng).FirstOrDefault();
        return (done, extract);
    }

    /// <summary>
    /// Reads the cleaned songs again (size, write time, bitrate and folder art
    /// moved) and records their new survey, so they stay checked and leave the
    /// filter; drops their albums' cached pictures; saves covers.json and the library.
    /// </summary>
    private async Task CommitKeptCoversAsync(IReadOnlyList<string> paths, CoverCache cache)
    {
        if (paths.Count == 0)
            return;

        var updated = await Task.Run(() =>
        {
            var reread = new Dictionary<string, Track>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in paths)
            {
                var fresh = TagReader.Read(path);
                reread[path] = fresh;
                cache.Set(fresh, CoverCleaner.Survey(path) ?? new CoverFinding(1, 0));
            }

            cache.Save(CoverCachePath);
            return reread;
        });

        var before = _library.AllTracks.Where(t => updated.ContainsKey(t.FilePath)).ToList();
        foreach (var album in LibraryScanner.Build(before).Artists.SelectMany(a => a.Albums))
            _artService.InvalidateAlbum(album);

        ReplaceTracksInLibrary(updated);
        await PersistLibraryAsync();
        CoverResultsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>"Kept the best cover in 14 songs · freed 38.2 MB · saved cover.jpg in 1 folder · 1 skipped (a.mp3: ...)".</summary>
    private static string DescribeKeepBestCover(IReadOnlyList<CoverCleanResult> results, IReadOnlyList<FolderExtract> folders, int total)
    {
        var cleaned = results.Where(r => r.Outcome == CoverCleanOutcome.Cleaned).ToList();
        var failed = results.Where(r => r.Outcome == CoverCleanOutcome.Failed).ToList();

        var parts = new List<string>
        {
            cleaned.Count == 0
                ? "No song needed its covers trimmed"
                : $"Kept the best cover in {Songs(cleaned.Count)} · freed {Display.Size(cleaned.Sum(r => r.BytesFreed))}",
        };

        if (results.Count < total)
            parts.Add($"stopped after {results.Count:N0} of {total:N0}; run it again for the rest");

        var notShrunk = cleaned.Count(r => !r.Shrunk);
        if (notShrunk > 0)
            parts.Add($"{Songs(notShrunk)} couldn't be made smaller");

        var saved = folders.Count(f => f.Outcome == ExtractOutcome.Saved);
        if (saved > 0)
            parts.Add($"saved {EmbeddedArtExtractor.FileName} in {(saved == 1 ? "1 folder" : $"{saved:N0} folders")}");

        if (failed.Count > 0)
            parts.Add($"{failed.Count:N0} skipped, left as they were ({Path.GetFileName(failed[0].Path)}: {failed[0].Error})");

        return string.Join(" · ", parts) + ".";
    }
}
