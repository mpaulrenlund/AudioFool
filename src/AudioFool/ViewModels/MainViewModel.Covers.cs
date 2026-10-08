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
/// cover more than once, and the album menu's "Keep Best Cover", which rewrites
/// them with only the best one (<see cref="CoverCleaner"/>) and writes the
/// folder's cover.jpg. Results are kept in <c>covers.json</c>.
/// </summary>
public sealed partial class MainViewModel
{
    private CoverCache? _coverCache;
    private CancellationTokenSource? _coverCts;
    private Task<CoverScanSummary>? _coverScan;
    private bool _keepingCovers;

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

    /// <summary>While the "Extra covers" row's filter is on: the album menu offers Keep Best Cover.</summary>
    public bool CanKeepBestCover => !IsPlaylistMode && LibraryFilter?.ShowsExtraCovers == true;

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

    /// <summary>
    /// The album menu's "Keep Best Cover": the songs of the album (or of every
    /// selected album) that the "Extra covers" filter shows. Each is rewritten
    /// with only its best cover and checked before it replaces the original;
    /// then each folder gets cover.jpg from the cover kept, unless it has a
    /// bigger one already. The songs are read again, so they leave the filter.
    /// </summary>
    [RelayCommand]
    private async Task KeepBestCoverForAlbumsAsync(AlbumItemViewModel? item)
    {
        if (item is null || !CanKeepBestCover || _keepingCovers)
            return;

        _keepingCovers = true;
        try
        {
            var tracks = AlbumMenuTracks(item).Select(LibraryCopyOf).ToList();
            var albums = (SelectedAlbums.Count > 1 && SelectedAlbums.Contains(item) ? SelectedAlbums : [item])
                .Select(a => a.Album).ToList();
            var dispatcher = Application.Current.Dispatcher;
            var cache = CoverResults;
            StatusText = $"Keeping the best cover in {Songs(tracks.Count)}...";

            var (results, folders, updated) = await Task.Run(() =>
            {
                var done = new List<CoverCleanResult>();
                foreach (var track in tracks)
                {
                    done.Add(CoverCleaner.Clean(track.FilePath, _engine));
                    var count = done.Count;
                    dispatcher.BeginInvoke(() =>
                        StatusText = $"Keeping the best cover... {count} of {tracks.Count} songs");
                }

                var cleaned = done.Where(r => r.Outcome == CoverCleanOutcome.Cleaned).Select(r => r.Path).ToList();
                var covers = cleaned.Count == 0
                    ? []
                    : EmbeddedArtExtractor.ExtractToFolders(cleaned, CoverJpeg.FromPng);

                // Read again: the size, write time and bitrate moved, and a new
                // cover.jpg is now the folder art. Recorded as checked, so they
                // leave the filter without the check having to run again.
                var reread = new Dictionary<string, Track>(StringComparer.OrdinalIgnoreCase);
                foreach (var track in tracks)
                {
                    if (done.First(r => r.Path == track.FilePath).Outcome != CoverCleanOutcome.Cleaned)
                        continue;
                    var fresh = TagReader.Read(track.FilePath);
                    reread[track.FilePath] = fresh;
                    cache.Set(fresh, CoverCleaner.Survey(track.FilePath) ?? new CoverFinding(1, 0));
                }

                cache.Save(CoverCachePath);
                return (done, covers, reread);
            });

            foreach (var album in albums)
                _artService.InvalidateAlbum(album);

            if (updated.Count > 0)
            {
                ReplaceTracksInLibrary(updated);
                await PersistLibraryAsync();
            }

            StatusText = WithFilterProgress(DescribeKeepBestCover(results, folders));
            CoverResultsChanged?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            _keepingCovers = false;
        }
    }

    /// <summary>"Kept the best cover in 14 songs · freed 38.2 MB · saved cover.jpg in 1 folder · 1 skipped (a.mp3: ...)".</summary>
    private static string DescribeKeepBestCover(IReadOnlyList<CoverCleanResult> results, IReadOnlyList<FolderExtract> folders)
    {
        var cleaned = results.Where(r => r.Outcome == CoverCleanOutcome.Cleaned).ToList();
        var failed = results.Where(r => r.Outcome == CoverCleanOutcome.Failed).ToList();

        var parts = new List<string>
        {
            cleaned.Count == 0
                ? "No song needed its covers trimmed"
                : $"Kept the best cover in {Songs(cleaned.Count)} · freed {Display.Size(cleaned.Sum(r => r.BytesFreed))}",
        };

        var notShrunk = cleaned.Count(r => !r.Shrunk);
        if (notShrunk > 0)
            parts.Add($"{Songs(notShrunk)} couldn't be made smaller");

        var saved = folders.Count(f => f.Outcome == ExtractOutcome.Saved);
        if (saved > 0)
            parts.Add($"saved {EmbeddedArtExtractor.FileName} in {(saved == 1 ? "1 folder" : $"{saved} folders")}");

        if (failed.Count > 0)
            parts.Add($"{failed.Count} skipped, left as they were ({Path.GetFileName(failed[0].Path)}: {failed[0].Error})");

        return string.Join(" · ", parts) + ".";
    }
}
