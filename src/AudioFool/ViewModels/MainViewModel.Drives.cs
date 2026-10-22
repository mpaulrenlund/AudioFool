using System.IO;
using AudioFool.Core.Library;
using CommunityToolkit.Mvvm.Input;

namespace AudioFool.ViewModels;

/// <summary>
/// Drives plugged in and pulled out while AudioFool runs (session 68). F5 and the
/// status bar's Refresh look for a music folder back under another drive letter
/// first, as startup does, then scan; a drive change does the same by itself
/// when one of the music folders has come or gone (<see cref="DriveChange"/>).
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>
    /// The library is being loaded or its folders re-pointed, before the scan
    /// that follows starts. A drive change meanwhile waits, as during a scan.
    /// </summary>
    private bool _loadingLibrary;

    /// <summary>A drive change that came during a scan or a load: whether a drive arrived.</summary>
    private bool? _driveChangeWaiting;

    /// <summary>Said once the scan after a relocation ends, instead of the counts.</summary>
    private string? _relocationNote;

    [RelayCommand]
    private Task RefreshAsync() => RefreshLibraryAsync();

    /// <summary>Re-points music folders that changed drive letter, then scans.</summary>
    private async Task RefreshLibraryAsync()
    {
        if (IsScanning || _loadingLibrary)
            return;

        _loadingLibrary = true;
        try
        {
            await RelocateMovedFoldersAsync();
        }
        finally
        {
            _loadingLibrary = false;
        }

        await ScanAsync();
    }

    /// <summary>
    /// A drive was plugged in (<paramref name="arrived"/>) or pulled out. Called
    /// once the burst of notices Windows sends for one drive has settled.
    /// </summary>
    public void DrivesChanged(bool arrived)
    {
        if (!DriveChange.WorthRefresh([.. MusicFolders], UnavailableFolders, arrived, FolderExists))
            return;

        if (IsScanning || _loadingLibrary)
        {
            _driveChangeWaiting = arrived || _driveChangeWaiting == true;
            return;
        }

        _ = RefreshLibraryAsync();
    }

    /// <summary>
    /// After a scan: a drive change that came during it is looked at again, against
    /// what the scan found. Posted, so the scan has finished before another starts.
    /// </summary>
    private async Task RunWaitingDriveChangeAsync()
    {
        if (_driveChangeWaiting is not { } arrived)
            return;

        _driveChangeWaiting = null;
        await Task.Yield();
        DrivesChanged(arrived);
    }

    private static bool FolderExists(string folder)
    {
        try { return Directory.Exists(folder); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>
    /// The status after a scan that followed a relocation: the move, unless a
    /// warning outranks it.
    /// </summary>
    private string? TakeRelocationNote()
    {
        var note = _relocationNote;
        _relocationNote = null;

        return _settings.IsReadOnly || PlaylistsWarning is not null || UnavailableFolders.Count > 0
            ? null
            : note;
    }
}
