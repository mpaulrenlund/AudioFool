using System.IO;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AudioFool.Core.Library;
using AudioFool.Core.Models;
using AudioFool.Services;

namespace AudioFool.ViewModels;

/// <summary>
/// Backs the tag-edit dialog for both a single track and a whole album. One class
/// rather than two: the two modes share every field except which ones are shown
/// (<see cref="IsAlbumMode"/> drives that in the XAML) and how Save builds its edit.
/// <para>
/// Art only appears in album mode - Title/Track #/Disc # are the "per-track"
/// fields and Artist/Album Artist/Album/Year are the "shared album" fields; art is
/// squarely a shared-album concept (it always replaces every track's embedded
/// picture plus the folder cover), so putting a picker on the single-track dialog
/// would silently change the whole album from what looks like a single-row edit.
/// </para>
/// </summary>
public sealed partial class TagEditViewModel : ObservableObject
{
    public bool IsAlbumMode { get; }

    /// <summary>Where "Choose Image..." should open, so it starts at the album's own folder.</summary>
    private readonly string? _artStartDirectory;

    /// <summary>The inverse of <see cref="IsAlbumMode"/>, for the fields only a single-track edit shows.</summary>
    public bool IsTrackMode => !IsAlbumMode;

    [ObservableProperty]
    private string _windowTitle = "";

    [ObservableProperty]
    private string _title = "";

    [ObservableProperty]
    private string _artist = "";

    [ObservableProperty]
    private string _albumArtist = "";

    [ObservableProperty]
    private string _albumTitle = "";

    [ObservableProperty]
    private string _year = "";

    [ObservableProperty]
    private string _trackNumber = "";

    /// <summary>Total tracks on this disc - the "of 12" in "3 of 12".</summary>
    [ObservableProperty]
    private string _trackCount = "";

    [ObservableProperty]
    private string _discNumber = "";

    /// <summary>Total discs in the set.</summary>
    [ObservableProperty]
    private string _discCount = "";

    [ObservableProperty]
    private BitmapSource? _artPreview;

    [ObservableProperty]
    private string? _pickedArtFilePath;

    [ObservableProperty]
    private string _validationError = "";

    [ObservableProperty]
    private bool _canSave = true;

    /// <summary>Single-track edit: every field pre-filled from the track's current tags.</summary>
    public TagEditViewModel(Track track)
    {
        IsAlbumMode = false;
        WindowTitle = "Edit Track Tags";

        Title = track.Title;
        Artist = track.Artist;
        AlbumArtist = track.AlbumArtist;
        AlbumTitle = track.Album;
        Year = track.Year?.ToString() ?? "";
        TrackNumber = track.TrackNumber?.ToString() ?? "";
        TrackCount = track.TrackCount?.ToString() ?? "";
        DiscNumber = track.DiscNumber?.ToString() ?? "";
        DiscCount = track.DiscCount?.ToString() ?? "";
    }

    /// <summary>
    /// Whole-album batch edit. Pre-fills from the first track - if tracks disagree
    /// on Artist/Album Artist/Album/Year today, Save will make them agree.
    /// </summary>
    public TagEditViewModel(Album album, AlbumArtService artService)
    {
        IsAlbumMode = true;
        WindowTitle = $"Edit Album Tags - {album.Title}";

        var first = album.Tracks.FirstOrDefault();
        Artist = first?.Artist ?? "";
        AlbumArtist = first?.AlbumArtist ?? album.ArtistName;
        AlbumTitle = album.Title;
        Year = album.Year?.ToString() ?? "";

        _artStartDirectory = first is null ? null : Path.GetDirectoryName(first.FilePath);

        _ = LoadPreviewAsync(album, artService);
    }

    private async Task LoadPreviewAsync(Album album, AlbumArtService artService) =>
        ArtPreview = await artService.GetAlbumArtAsync(album, decodeWidth: 200);

    [RelayCommand]
    private void ChooseImage()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose cover art",
            Filter = "Image files (*.jpg;*.jpeg;*.png)|*.jpg;*.jpeg;*.png",
            InitialDirectory = _artStartDirectory ?? "",
        };

        if (dialog.ShowDialog() != true)
            return;

        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = 200;
            bitmap.UriSource = new Uri(dialog.FileName);
            bitmap.EndInit();
            bitmap.Freeze();

            PickedArtFilePath = dialog.FileName;
            ArtPreview = bitmap;
        }
        catch (Exception ex) when (ex is NotSupportedException or IOException or UnauthorizedAccessException)
        {
            ValidationError = $"Couldn't open that image: {ex.Message}";
        }
    }

    partial void OnTrackNumberChanged(string value) => Revalidate();
    partial void OnTrackCountChanged(string value) => Revalidate();
    partial void OnDiscNumberChanged(string value) => Revalidate();
    partial void OnDiscCountChanged(string value) => Revalidate();
    partial void OnAlbumTitleChanged(string value) => Revalidate();

    private void Revalidate()
    {
        if (string.IsNullOrWhiteSpace(AlbumTitle))
        {
            ValidationError = "Album can't be empty.";
            CanSave = false;
            return;
        }

        var numeric = new[]
        {
            ("Year", Year),
            ("Track #", TrackNumber),
            ("Total tracks", TrackCount),
            ("Disc #", DiscNumber),
            ("Total discs", DiscCount),
        };

        foreach (var (label, value) in numeric)
        {
            if (value.Length > 0 && !int.TryParse(value, out _))
            {
                ValidationError = $"{label} must be a number.";
                CanSave = false;
                return;
            }
        }

        ValidationError = "";
        CanSave = true;
    }

    private static int? ParseOrNull(string value) =>
        int.TryParse(value, out var n) ? n : null;

    public TrackTagEdit BuildTrackEdit() => new(
        Title.Trim(), Artist.Trim(), AlbumArtist.Trim(), AlbumTitle.Trim(),
        ParseOrNull(Year),
        ParseOrNull(TrackNumber), ParseOrNull(TrackCount),
        ParseOrNull(DiscNumber), ParseOrNull(DiscCount));

    public AlbumTagEdit BuildAlbumEdit() => new(
        Artist.Trim(), AlbumArtist.Trim(), AlbumTitle.Trim(), ParseOrNull(Year));

    public ArtPayload? PickedArtPayload()
    {
        if (PickedArtFilePath is null)
            return null;

        var bytes = File.ReadAllBytes(PickedArtFilePath);
        var mimeType = Path.GetExtension(PickedArtFilePath).Equals(".png", StringComparison.OrdinalIgnoreCase)
            ? "image/png"
            : "image/jpeg";

        return new ArtPayload(bytes, mimeType);
    }
}
