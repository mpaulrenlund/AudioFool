using CommunityToolkit.Mvvm.Input;
using AudioFool.Core.Analysis;
using AudioFool.Core.Models;

namespace AudioFool.ViewModels;

/// <summary>
/// Clearing songs from Quality Check rows: the user's word that a file the check
/// flagged is genuine. From the song and album menus while a row's filter is on,
/// and from the Analyze window. Kept in <c>quality-cleared.json</c>
/// (<see cref="QualityClearances"/>); the music files are never touched.
/// </summary>
public sealed partial class MainViewModel
{
    private QualityClearances? _qualityClearances;

    /// <summary>Where clearances are kept; ThemeLab points it at a scratch file.</summary>
    public string QualityClearancesPath { get; set; } = QualityClearances.DefaultPath;

    /// <summary>Read from <c>quality-cleared.json</c> the first time they're needed.</summary>
    public QualityClearances Clearances => _qualityClearances ??= QualityClearances.Load(QualityClearancesPath, Now);

    /// <summary>Raised on the UI thread after songs are cleared or put back, for open Analyze windows.</summary>
    public event EventHandler? QualityClearancesChanged;

    /// <summary>"Not Fake 24-bit" while a Quality Check row's filter is on; null otherwise, which hides the menu item.</summary>
    public string? QualityClearHeader =>
        !IsPlaylistMode && LibraryFilter?.QualityFlag is { } flag ? QualityClearances.ClearLabel(flag) : null;

    public bool CanClearQuality => QualityClearHeader is not null;

    /// <summary>While "Cleared by you" is the filter: the menus offer Put Back.</summary>
    public bool CanPutBackQuality => !IsPlaylistMode && LibraryFilter?.ShowsQualityClearances == true;

    private void RaiseQualityMenuChanged()
    {
        OnPropertyChanged(nameof(QualityClearHeader));
        OnPropertyChanged(nameof(CanClearQuality));
        OnPropertyChanged(nameof(CanPutBackQuality));
        OnPropertyChanged(nameof(CanKeepBestCover));
    }

    /// <summary>The flags the library check saved for this song; empty when it hasn't been checked.</summary>
    public IReadOnlyList<QualityFlag> CheckedQualityFlags(Track track) =>
        QualityResults.TryGet(CurrentCopyOf(track), out var result) ? result.Flags : [];

    /// <summary>
    /// The library's own copy of a song, by path. An Analyze window keeps the
    /// track it opened on, which a retag since has replaced.
    /// </summary>
    private Track CurrentCopyOf(Track track) =>
        _library.AllTracks.FirstOrDefault(t => string.Equals(t.FilePath, track.FilePath, StringComparison.OrdinalIgnoreCase))
        ?? track;

    /// <summary>The song menu's "Not Fake 24-bit": the selected songs, out of the filter's row.</summary>
    [RelayCommand]
    private void ClearQualityForSongs()
    {
        if (LibraryFilter?.QualityFlag is { } flag && CanClearQuality)
            ClearQuality(MenuTracks(), flag);
    }

    /// <summary>The album menu's "Not Fake 24-bit": the songs of the album (or selected albums) the filter shows.</summary>
    [RelayCommand]
    private void ClearQualityForAlbums(AlbumItemViewModel? item)
    {
        if (item is not null && LibraryFilter?.QualityFlag is { } flag && CanClearQuality)
            ClearQuality(AlbumMenuTracks(item), flag);
    }

    [RelayCommand]
    private void PutBackQualityForSongs()
    {
        if (CanPutBackQuality)
            PutBackQuality(MenuTracks());
    }

    [RelayCommand]
    private void PutBackQualityForAlbums(AlbumItemViewModel? item)
    {
        if (item is not null && CanPutBackQuality)
            PutBackQuality(AlbumMenuTracks(item));
    }

    /// <summary>
    /// The songs an album row's menu acts on: that album, or every selected album
    /// when it is one of several. Only the songs the filter shows, never the
    /// whole album: a song the check didn't flag has nothing to clear.
    /// </summary>
    private List<Track> AlbumMenuTracks(AlbumItemViewModel item)
    {
        IReadOnlyList<AlbumItemViewModel> picked = SelectedAlbums.Count > 1 && SelectedAlbums.Contains(item)
            ? SelectedAlbums
            : [item];
        return [.. picked.SelectMany(a => a.Album.Tracks)];
    }

    /// <summary>Clears these songs from <paramref name="flag"/>'s row (and its "possibly" twin).</summary>
    public void ClearQuality(IReadOnlyList<Track> tracks, QualityFlag flag)
    {
        if (tracks.Count == 0)
            return;

        var added = Clearances.Clear(tracks.Select(CurrentCopyOf), flag, Now);
        var row = QualityClearances.RowName(flag);
        AfterClearancesChanged(added == 0
            ? $"Already cleared from {row}."
            : $"Cleared {Songs(added)} from {row}.");
    }

    /// <summary>Puts these songs back in <paramref name="flag"/>'s row, or in every row they were cleared from.</summary>
    public void PutBackQuality(IReadOnlyList<Track> tracks, QualityFlag? flag = null)
    {
        if (tracks.Count == 0)
            return;

        var restored = Clearances.PutBack(tracks.Select(CurrentCopyOf), flag);
        var where = flag is { } f ? QualityClearances.RowName(f) : "the Quality Check";
        AfterClearancesChanged(restored == 0
            ? "Nothing to put back."
            : $"Put {Songs(restored)} back in {where}.");
    }

    /// <summary>
    /// A cleared song leaves a Quality Check row's filter at once, as a fixed tag
    /// leaves a Missing tags one, and the status bar counts what is left.
    /// </summary>
    private void AfterClearancesChanged(string message)
    {
        if (LibraryFilter is { } filter && (filter.QualityFlag is not null || filter.ShowsQualityClearances))
            ApplyToView(keepSelection: true);

        StatusText = WithFilterProgress(message);
        QualityClearancesChanged?.Invoke(this, EventArgs.Empty);
    }
}
