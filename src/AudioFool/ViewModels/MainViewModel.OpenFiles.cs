using System.IO;
using AudioFool.Core.Library;
using AudioFool.Core.Models;
using AudioFool.Core.Settings;

namespace AudioFool.ViewModels;

/// <summary>
/// Songs opened from Explorer (a double-click, Open with), handed over by
/// <see cref="Services.SingleInstance"/>. They play at once, in place of what was
/// playing. One song from the library plays as a double-click on its row would:
/// its artist and album shown, the rest of the album after it. Anything else (a
/// song from outside the library, or several songs) plays on its own, as a queue
/// of just those, and the library view stays put; nothing is added to the
/// library and nothing outside it is remembered as last played.
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>Songs opened while the app was starting, opened once it is ready for them.</summary>
    private IReadOnlyList<string>? _pendingOpen;

    /// <summary>Something was opened, so last time's song isn't shown over it.</summary>
    private bool _openedFiles;

    /// <summary>The audio engine has been set up (<see cref="InitialiseAsync"/>).</summary>
    private bool _engineReady;

    public void OpenFiles(IReadOnlyList<string> args)
    {
        var paths = OpenedFiles.InPlayOrder(OpenedFiles.Playable(args));
        if (paths.Count == 0)
        {
            if (args.Count > 0)
                StatusText = args.Count == 1
                    ? $"Can't play {Path.GetFileName(args[0])}: not a music file AudioFool knows, or it's gone."
                    : "None of those are music files AudioFool knows.";
            return;
        }

        _openedFiles = true;
        _pendingOpen = paths;
        OpenPendingWhenReady();
    }

    /// <summary>
    /// Opens the songs waiting in <see cref="_pendingOpen"/> once the engine is set
    /// up and, for a song in a music folder, once the library is shown: it may be
    /// in it, and is then shown in its album. Called again at both points.
    /// </summary>
    private void OpenPendingWhenReady()
    {
        if (_pendingOpen is not { } paths || !_engineReady)
            return;

        if (_restoreLastPlayed && MusicFolders.Count > 0 && paths.Any(p => OpenedFiles.InMusicFolders(p, MusicFolders)))
            return;

        _pendingOpen = null;
        _ = OpenFilesNowAsync(paths);
    }

    private async Task OpenFilesNowAsync(IReadOnlyList<string> paths)
    {
        _restoredQueue = null;

        var tracks = new List<Track>();
        foreach (var path in paths)
        {
            if (InLibrary(path) is { } known)
            {
                tracks.Add(known);
                continue;
            }

            try
            {
                tracks.Add(await Task.Run(() => TagReader.Read(path)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Gone or locked since it was opened: play the rest.
            }
        }

        if (tracks.Count == 0)
        {
            StatusText = $"Couldn't open {Path.GetFileName(paths[0])}.";
            return;
        }

        if (tracks.Count == 1 && InLibrary(tracks[0].FilePath) is not null && ShowInLibrary(tracks[0]) is { } shown)
        {
            PlayTrack(shown);
            return;
        }

        if (!_engine.Play(tracks, 0))
            StatusText = DescribePlayFailure(tracks[0]);
    }

    private Track? InLibrary(string path) =>
        _library.AllTracks.FirstOrDefault(t => string.Equals(t.FilePath, path, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Shows a library song in its artist and album, back in the library view with
    /// no search or Statistics filter, so it can be shown wherever it is.
    /// </summary>
    private Track? ShowInLibrary(Track track)
    {
        if (_library.Locate(new LastPlayedTrack(track.FilePath, "", "")) is not { } found)
            return null;

        IsPlaylistMode = false;
        if (LibraryFilter is not null)
            LibraryFilter = null;

        if (IsSearching)
        {
            SearchQuery = "";
            _searchDebounce.Stop();
            ApplyToView(keepSelection: true);
            StatusText = DescribeStatus(default);
        }

        return SelectInView(found);
    }
}
