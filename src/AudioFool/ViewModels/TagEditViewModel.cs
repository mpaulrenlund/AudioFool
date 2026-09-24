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
/// Art only appears in album mode - Title and Track # are the "per-track" fields
/// and everything else can be set album-wide; art is
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
    private string _publisher = "";

    /// <summary>Several composers are separated by semicolons.</summary>
    [ObservableProperty]
    private string _composer = "";

    [ObservableProperty]
    private string _conductor = "";

    /// <summary>Several genres are separated by semicolons.</summary>
    [ObservableProperty]
    private string _genre = "";

    [ObservableProperty]
    private string _comment = "";

    /// <summary>
    /// Album mode: the placeholder each optional field shows when the album's
    /// tracks disagree on it and the box therefore starts empty. Empty when they
    /// agree, and always empty in track mode.
    /// </summary>
    public string TrackCountPlaceholder { get; private set; } = "";
    public string DiscNumberPlaceholder { get; private set; } = "";
    public string DiscCountPlaceholder { get; private set; } = "";

    // The detail placeholders change when a field is cleared, so they notify.
    [ObservableProperty]
    private string _publisherPlaceholder = "";

    [ObservableProperty]
    private string _composerPlaceholder = "";

    [ObservableProperty]
    private string _conductorPlaceholder = "";

    [ObservableProperty]
    private string _genrePlaceholder = "";

    [ObservableProperty]
    private string _commentPlaceholder = "";

    /// <summary>
    /// Detail fields the clear button was used on. An empty box normally means
    /// "keep" when the tracks disagree, since it started empty; these are the
    /// ones the user explicitly asked to clear on every track.
    /// </summary>
    private readonly HashSet<string> _cleared = [];

    public string ClearToolTip => IsAlbumMode ? "Clear this tag on every track" : "Clear this tag";

    /// <summary>
    /// The text each optional field was pre-filled with. Save writes only the
    /// ones that no longer match - see <see cref="AlbumTagEdit"/> for why.
    /// </summary>
    private readonly Dictionary<string, string> _initial = [];

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

        var details = TagReader.ReadDetails(track.FilePath);
        Publisher = details?.Publisher ?? "";
        Composer = details?.Composer ?? "";
        Conductor = details?.Conductor ?? "";
        Genre = details?.Genre ?? "";
        Comment = details?.Comment ?? "";

        RememberInitialDetails();
    }

    /// <summary>
    /// Whole-album batch edit. Artist/Album Artist/Album/Year pre-fill from the
    /// first track - if tracks disagree on those today, Save will make them agree.
    /// <para>
    /// The optional fields pre-fill only when every track agrees; otherwise the
    /// box starts empty with a "varies" placeholder, and is left alone on Save
    /// unless something is typed into it. Reading them opens every file in the
    /// album, since the cache holds none of the five detail fields.
    /// </para>
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

        var tracks = album.Tracks;
        TrackCount = Shared(tracks.Select(t => t.TrackCount?.ToString() ?? ""), "Varies", out var trackCountHint);
        DiscNumber = Shared(tracks.Select(t => t.DiscNumber?.ToString() ?? ""), "Varies", out var discNumberHint);
        DiscCount = Shared(tracks.Select(t => t.DiscCount?.ToString() ?? ""), "Varies", out var discCountHint);
        TrackCountPlaceholder = trackCountHint;
        DiscNumberPlaceholder = discNumberHint;
        DiscCountPlaceholder = discCountHint;

        // A file that cannot be opened has no say: its write will fail anyway,
        // and counting it as "empty" would hide a value every other track shares.
        var details = tracks.Select(t => TagReader.ReadDetails(t.FilePath)).OfType<TagDetails>().ToList();
        const string varies = "Varies by track - kept unless changed";
        Publisher = Shared(details.Select(d => d.Publisher), varies, out var publisherHint);
        Composer = Shared(details.Select(d => d.Composer), varies, out var composerHint);
        Conductor = Shared(details.Select(d => d.Conductor), varies, out var conductorHint);
        Genre = Shared(details.Select(d => d.Genre), varies, out var genreHint);
        Comment = Shared(details.Select(d => d.Comment), varies, out var commentHint);
        PublisherPlaceholder = publisherHint;
        ComposerPlaceholder = composerHint;
        ConductorPlaceholder = conductorHint;
        GenrePlaceholder = genreHint;
        CommentPlaceholder = commentHint;

        RememberInitialDetails();

        _artStartDirectory = first is null ? null : Path.GetDirectoryName(first.FilePath);

        _ = LoadPreviewAsync(album, artService);
    }

    /// <summary>
    /// The value every track agrees on, or "" with <paramref name="placeholder"/>
    /// set to <paramref name="variesText"/> when they do not.
    /// </summary>
    private static string Shared(IEnumerable<string> values, string variesText, out string placeholder)
    {
        var distinct = values.Distinct(StringComparer.Ordinal).Take(2).ToList();
        placeholder = distinct.Count > 1 ? variesText : "";
        return distinct.Count == 1 ? distinct[0] : "";
    }

    private void RememberInitialDetails()
    {
        _initial[nameof(TrackCount)] = TrackCount;
        _initial[nameof(DiscNumber)] = DiscNumber;
        _initial[nameof(DiscCount)] = DiscCount;
        _initial[nameof(Publisher)] = Publisher;
        _initial[nameof(Composer)] = Composer;
        _initial[nameof(Conductor)] = Conductor;
        _initial[nameof(Genre)] = Genre;
        _initial[nameof(Comment)] = Comment;
    }

    /// <summary>
    /// The field's text when the user changed it or cleared it with the button,
    /// otherwise null for "leave alone".
    /// </summary>
    private string? IfChanged(string name, string value)
    {
        var text = value.Trim();
        if (text.Length == 0 && _cleared.Contains(name))
            return "";
        return string.Equals(text, _initial[name].Trim(), StringComparison.Ordinal) ? null : text;
    }

    /// <summary>
    /// Empties a detail field and marks it to be cleared - the only way to clear
    /// one whose tracks disagree, because its box already starts empty. Typing
    /// into the box afterwards writes what was typed instead.
    /// </summary>
    [RelayCommand]
    private void ClearField(string name)
    {
        _cleared.Add(name);
        var placeholder = IsAlbumMode ? "Will be cleared on every track" : "Will be cleared";
        switch (name)
        {
            case nameof(Publisher): Publisher = ""; PublisherPlaceholder = placeholder; break;
            case nameof(Composer): Composer = ""; ComposerPlaceholder = placeholder; break;
            case nameof(Conductor): Conductor = ""; ConductorPlaceholder = placeholder; break;
            case nameof(Genre): Genre = ""; GenrePlaceholder = placeholder; break;
            case nameof(Comment): Comment = ""; CommentPlaceholder = placeholder; break;
        }
    }

    private NumberEdit? NumberIfChanged(string name, string value) =>
        IfChanged(name, value) is { } changed ? new NumberEdit(ParseOrNull(changed)) : null;

    private TagDetailsEdit BuildDetailsEdit() => new()
    {
        Publisher = IfChanged(nameof(Publisher), Publisher),
        Composer = IfChanged(nameof(Composer), Composer),
        Conductor = IfChanged(nameof(Conductor), Conductor),
        Genre = IfChanged(nameof(Genre), Genre),
        Comment = IfChanged(nameof(Comment), Comment),
    };

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
    partial void OnYearChanged(string value) => Revalidate();

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
        ParseOrNull(DiscNumber), ParseOrNull(DiscCount))
    {
        Details = BuildDetailsEdit(),
    };

    public AlbumTagEdit BuildAlbumEdit() => new(
        Artist.Trim(), AlbumArtist.Trim(), AlbumTitle.Trim(), ParseOrNull(Year))
    {
        TrackCount = NumberIfChanged(nameof(TrackCount), TrackCount),
        DiscNumber = NumberIfChanged(nameof(DiscNumber), DiscNumber),
        DiscCount = NumberIfChanged(nameof(DiscCount), DiscCount),
        Details = BuildDetailsEdit(),
    };

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
