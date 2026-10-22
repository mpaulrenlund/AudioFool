using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.Input;
using AudioFool.Core.Library;
using AudioFool.Core.Models;

namespace AudioFool.ViewModels;

/// <summary>
/// Find and Replace (session 63, the user's design): the logo menu's item and
/// Ctrl+H open the window; its saves are made here, so the library, library.json,
/// a scan under way and closing's wait for saves all see them.
/// </summary>
public sealed partial class MainViewModel
{
    [RelayCommand]
    private void ShowFindReplace()
    {
        if (Application.Current.MainWindow is not { } owner)
            return;

        new FindReplaceWindow(NewFindReplace(), owner).ShowDialog();
    }

    /// <summary>
    /// Two or more songs selected in the Songs table limit it to those (the user's
    /// call, session 67), for as long as the window is open; otherwise the library.
    /// </summary>
    private FindReplaceViewModel NewFindReplace()
    {
        var picked = SelectedTracks.Count > 1
            ? SelectedTracks.Select(t => t.FilePath).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : null;
        return new(() => FindReplaceSongs(picked), picked?.Count, [.. MusicFolders], ReplaceInSongsAsync, UndoReplaceAsync);
    }

    /// <summary>
    /// What Find looks through: the ticked folders' songs, as Statistics counts
    /// them, whatever the search box shows; or the picked songs, by path, so a Find
    /// after a replace sees the library's new copies. A folder whose drive isn't
    /// there is left out: its songs are carried over in the library, but can't be
    /// written.
    /// </summary>
    /// <param name="picked">The paths of the songs selected when the window opened; null for the library.</param>
    private IReadOnlyList<Track> FindReplaceSongs(IReadOnlySet<string>? picked)
    {
        var gone = MusicFolders
            .Where(f => UnavailableFolders.Any(u => MusicFolderList.SameFolder(u, f)) || !Directory.Exists(f))
            .Select(f => f.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar)
            .ToList();

        IReadOnlyList<Track> songs = picked is null
            ? _folderFilteredLibrary.AllTracks
            : [.. _library.AllTracks.Where(t => picked.Contains(t.FilePath)).DistinctBy(t => t.FilePath, StringComparer.OrdinalIgnoreCase)];
        return gone.Count == 0
            ? songs
            : [.. songs.Where(t => !gone.Any(p => t.FilePath.StartsWith(p, StringComparison.OrdinalIgnoreCase)))];
    }

    private Task<ReplaceRun> ReplaceInSongsAsync(IReadOnlyList<ReplaceJob> jobs, TextReplace replace, IProgress<int> progress) =>
        RewriteSongsAsync([.. jobs.Select(j => j.Track)], ("saving", "Updated", "since Find"),
            (current, i) => TagWriter.WriteReplacement(current, jobs[i].Tags, replace, _engine), progress);

    private Task<ReplaceRun> UndoReplaceAsync(IReadOnlyList<UndoJob> jobs, IProgress<int> progress) =>
        RewriteSongsAsync([.. jobs.Select(j => j.Track)], ("putting back", "Put back", "since the replace"),
            (current, i) => TagWriter.UndoReplacement(current, jobs[i].Written, _engine), progress);

    /// <summary>
    /// Song by song off the UI thread, as the several-tracks save is: every song
    /// written goes into the library and library.json, the rest are named. A save
    /// that renames the selected artist or album selects wherever its songs went.
    /// </summary>
    /// <param name="words">For the status bar: what it is doing, what it did, and since when a song left alone had changed.</param>
    /// <param name="write">Given the library's own copy of each song, which carries anything changed since Find.</param>
    private async Task<ReplaceRun> RewriteSongsAsync(IReadOnlyList<Track> songs, (string Doing, string Done, string Since) words,
        Func<Track, int, ReplaceWriteResult> write, IProgress<int> progress)
    {
        var doing = words.Doing;
        using var writing = BeginWrite();
        StatusText = $"Find and Replace: {doing} {ReplaceRun.Songs(songs.Count)}...";

        var library = new Dictionary<string, Track>(StringComparer.OrdinalIgnoreCase);
        foreach (var track in _library.AllTracks)
            library.TryAdd(track.FilePath, track);

        var results = await Task.Run(() =>
        {
            var done = new List<ReplaceWriteResult>(songs.Count);
            for (var i = 0; i < songs.Count; i++)
            {
                done.Add(write(library.GetValueOrDefault(songs[i].FilePath) ?? songs[i], i));
                progress.Report(i + 1);
            }

            return done;
        });

        var updated = new Dictionary<string, Track>(StringComparer.OrdinalIgnoreCase);
        var undo = new List<UndoJob>();
        var leftAlone = new List<string>();
        var failed = new List<(string FileName, string Error)>();
        for (var i = 0; i < songs.Count; i++)
        {
            var result = results[i];
            switch (result.Outcome)
            {
                case ReplaceOutcome.Saved:
                    updated[songs[i].FilePath] = result.UpdatedTrack!;
                    undo.Add(new UndoJob(result.UpdatedTrack!, result.Written));
                    break;
                case ReplaceOutcome.Changed:
                    leftAlone.Add(Path.GetFileName(songs[i].FilePath));
                    break;
                default:
                    failed.Add((Path.GetFileName(songs[i].FilePath), result.ErrorMessage ?? "unknown error"));
                    break;
            }
        }

        if (updated.Count > 0)
        {
            var artistBefore = SelectedArtist?.Name;
            var albumBefore = SelectedAlbum?.Album.Title;
            var shown = SelectedAlbum?.Album.Tracks ?? SelectedArtist?.Albums.SelectMany(a => a.Tracks);
            var moved = shown?.FirstOrDefault(t => updated.ContainsKey(t.FilePath)) is { } track ? updated[track.FilePath] : null;

            ReplaceTracksInLibrary(updated);
            if (moved is not null)
                FollowEditedTracks(moved, artistBefore, albumBefore);

            await PersistLibraryAsync();
        }

        var run = new ReplaceRun(updated.Count, leftAlone, failed, undo);
        var said = run.Describe(words.Done, songs.Count, words.Since);
        StatusText = failed.Count == 0 && leftAlone.Count == 0
            ? WithFilterProgress($"Find and Replace: {said}")
            : $"Find and Replace: {said}";
        return run;
    }
}
