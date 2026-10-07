using System.IO;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AudioFool.Core.Art;
using AudioFool.Core.Library;
using AudioFool.Core.Models;
using AudioFool.Services;

namespace AudioFool.ViewModels;

/// <summary>
/// Backs the tag-edit dialog for a single track, a whole album, and tracks picked
/// in the grid. One class rather than three: the modes share every field except which ones are shown
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
    /// <summary>
    /// True for any edit of several tracks - a whole album, or tracks picked in the
    /// grid (<see cref="IsSelectionMode"/>). It lays the dialog out without Title
    /// and Track #.
    /// </summary>
    public bool IsAlbumMode { get; }

    /// <summary>
    /// An edit of tracks picked in the grid. Laid out like the album dialog, but
    /// with no art, and every field is kept unless changed - Artist, Album Artist,
    /// Album and Year included, which the album dialog always writes.
    /// </summary>
    public bool IsSelectionMode { get; }

    /// <summary>Art belongs to the whole album, so only the album dialog offers it.</summary>
    public bool ShowsArt => IsAlbumMode && !IsSelectionMode;

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
    public string ArtistPlaceholder { get; private set; } = "";
    public string AlbumArtistPlaceholder { get; private set; } = "";
    public string AlbumTitlePlaceholder { get; private set; } = "";
    public string YearPlaceholder { get; private set; } = "";
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

    public string ClearToolTip =>
        IsSelectionMode ? "Clear this tag on every selected track"
        : IsAlbumMode ? "Clear this tag on every track"
        : "Clear this tag";

    /// <summary>
    /// The text each optional field was pre-filled with. Save writes only the
    /// ones that no longer match - see <see cref="AlbumTagEdit"/> for why.
    /// </summary>
    private readonly Dictionary<string, string> _initial = [];

    [ObservableProperty]
    private BitmapSource? _artPreview;

    [ObservableProperty]
    private string? _pickedArtFilePath;

    /// <summary>A cover chosen from "Search Internet" - always a JPEG, held in memory until Save.</summary>
    private byte[]? _downloadedArt;

    /// <summary>Album mode: the online cover search behind "Search Internet".</summary>
    public OnlineArtSearch ArtSearch { get; } = new(fanartApiKey: null);

    /// <summary>Who to search for: the album artist, since that is who the album files under.</summary>
    public string SearchArtist =>
        (string.IsNullOrWhiteSpace(AlbumArtist) ? Artist : AlbumArtist).Trim();

    [ObservableProperty]
    private string _validationError = "";

    [ObservableProperty]
    private bool _canSave = true;

    /// <summary>Single-track edit: every field pre-filled from the track's current tags.</summary>
    public TagEditViewModel(Track track)
    {
        IsAlbumMode = false;
        WindowTitle = $"Edit Track Tags - {track.DisplayTitle}";

        Title = track.Title;
        Artist = track.Artist;
        AlbumArtist = track.AlbumArtist;
        AlbumTitle = track.Album;
        Year = track.Year?.ToString() ?? "";

        var details = TagReader.ReadDetails(track.FilePath);
        var numbers = details?.Numbers ?? CachedNumbers(track);
        TrackNumber = numbers.TrackNumber;
        TrackCount = numbers.TrackCount;
        DiscNumber = numbers.DiscNumber;
        DiscCount = numbers.DiscCount;

        Publisher = details?.Publisher ?? "";
        Composer = details?.Composer ?? "";
        Conductor = details?.Conductor ?? "";
        Genre = details?.Genre ?? "";
        Comment = details?.Comment ?? "";

        // The cache holds only the year; the file may hold the whole date.
        if (details is { Date.Length: > 0 })
            Year = details.Date;

        RememberInitialDetails();
        // Not for Save, which writes every field: only so HasChanges can tell.
        _initial[nameof(Title)] = Title;
        _initial[nameof(Artist)] = Artist;
        _initial[nameof(AlbumArtist)] = AlbumArtist;
        _initial[nameof(AlbumTitle)] = AlbumTitle;
        _initial[nameof(Year)] = Year;
        _initial[nameof(TrackNumber)] = TrackNumber;
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
    public TagEditViewModel(Album album, AlbumArtService artService, OnlineArtSearch? artSearch = null)
    {
        IsAlbumMode = true;
        ArtSearch = artSearch ?? new OnlineArtSearch(fanartApiKey: null);
        WindowTitle = $"Edit Album Tags - {album.Title}";

        var first = album.Tracks.FirstOrDefault();
        Artist = first?.Artist ?? "";
        AlbumArtist = first?.AlbumArtist ?? album.ArtistName;
        AlbumTitle = album.Title;
        Year = album.Year?.ToString() ?? "";

        var tracks = album.Tracks;
        _albumTrackPaths = tracks.Select(t => t.FilePath).ToList();
        var read = tracks.Select(t => TagReader.ReadDetails(t.FilePath)).ToList();
        ShowNumbers(tracks, read);

        // A file that cannot be opened has no say: its write will fail anyway,
        // and counting it as "empty" would hide a value every other track shares.
        var details = read.OfType<TagDetails>().ToList();
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

        // A full date only when every track has the same one. Year is written
        // album-wide, so otherwise the album's year is what Save would write.
        var dates = details.Select(d => d.Date).Distinct(StringComparer.Ordinal).ToList();
        if (dates is [{ Length: > 0 } date] && details.Count == tracks.Count)
            Year = date;

        RememberInitialDetails();
        // Not for Save, which writes these four album-wide regardless: only so
        // HasChanges can tell whether anything was typed.
        _initial[nameof(Artist)] = Artist;
        _initial[nameof(AlbumArtist)] = AlbumArtist;
        _initial[nameof(AlbumTitle)] = AlbumTitle;
        _initial[nameof(Year)] = Year;

        _artStartDirectory = first is null ? null : Path.GetDirectoryName(first.FilePath);

        _ = LoadPreviewAsync(album, artService);
    }

    /// <summary>
    /// Tracks picked in the grid. Every field pre-fills only when all of them
    /// agree, and Save writes only what was changed, so picking disc 1 of a set
    /// and typing a disc number touches the disc number and nothing else.
    /// Reading the detail fields opens every selected file. <paramref name="scope"/>
    /// names what was picked, for the title ("3 artists, 214 tracks").
    /// </summary>
    public TagEditViewModel(IReadOnlyList<Track> tracks, string? scope = null)
    {
        IsAlbumMode = true;
        IsSelectionMode = true;
        WindowTitle = $"Edit Tags - {scope ?? $"{tracks.Count} tracks"}";

        const string varies = "Varies by track - kept unless changed";
        Artist = Shared(tracks.Select(t => t.Artist), varies, out var artistHint);
        AlbumArtist = Shared(tracks.Select(t => t.AlbumArtist), varies, out var albumArtistHint);
        AlbumTitle = Shared(tracks.Select(t => t.Album), varies, out var albumHint);
        // The cache holds the full date whenever the file names more than a year.
        Year = Shared(tracks.Select(t => t.ReleaseDate ?? t.Year?.ToString() ?? ""), "Varies", out var yearHint);
        ArtistPlaceholder = artistHint;
        AlbumArtistPlaceholder = albumArtistHint;
        AlbumTitlePlaceholder = albumHint;
        YearPlaceholder = yearHint;

        var read = tracks.Select(t => TagReader.ReadDetails(t.FilePath)).ToList();
        ShowNumbers(tracks, read);

        var details = read.OfType<TagDetails>().ToList();
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
        _initial[nameof(Artist)] = Artist;
        _initial[nameof(AlbumArtist)] = AlbumArtist;
        _initial[nameof(AlbumTitle)] = AlbumTitle;
        _initial[nameof(Year)] = Year;
        Revalidate();
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

    /// <summary>
    /// The count and disc boxes for several tracks, spelled as the files spell
    /// them, so "01" shows as "01" and a mix of "01" and "1" reads as varying.
    /// A file that could not be read contributes its cached number instead.
    /// </summary>
    private void ShowNumbers(IReadOnlyList<Track> tracks, IReadOnlyList<TagDetails?> read)
    {
        var numbers = tracks.Zip(read, (t, d) => d?.Numbers ?? CachedNumbers(t)).ToList();
        var padded = numbers.Count(n => IsPadded(n.TrackNumber) || IsPadded(n.TrackCount)
                                        || IsPadded(n.DiscNumber) || IsPadded(n.DiscCount));
        LeadingZerosText = padded == 0 ? "None found" : $"{padded} of {tracks.Count} tracks";
        _hasLeadingZeros = padded > 0;

        TrackCount = Shared(numbers.Select(n => n.TrackCount), "Varies", out var trackCountHint);
        DiscNumber = Shared(numbers.Select(n => n.DiscNumber), "Varies", out var discNumberHint);
        DiscCount = Shared(numbers.Select(n => n.DiscCount), "Varies", out var discCountHint);
        TrackCountPlaceholder = trackCountHint;
        DiscNumberPlaceholder = discNumberHint;
        DiscCountPlaceholder = discCountHint;
    }

    private static bool IsPadded(string number) => number.Length > 1 && number[0] == '0';

    /// <summary>The album dialog's "Remove Leading Zeros" - one button for the whole album.</summary>
    public bool ShowsRemoveLeadingZeros => IsAlbumMode && !IsSelectionMode;

    /// <summary>How many tracks have a padded number, or that Save will remove them.</summary>
    [ObservableProperty]
    private string _leadingZerosText = "";

    private bool _hasLeadingZeros;

    /// <summary>Set by the button: Save rewrites every track's numbers without the zeros.</summary>
    public bool RemovesLeadingZeros { get; private set; }

    private bool CanRemoveLeadingZeros() => _hasLeadingZeros && !RemovesLeadingZeros;

    /// <summary>
    /// Marks every track's track and disc numbers to be rewritten without leading
    /// zeros, and strips them from the boxes too. A box is filled only when every
    /// track agrees, so writing its stripped text to all of them is safe.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRemoveLeadingZeros))]
    private void RemoveLeadingZeros()
    {
        if (!CanRemoveLeadingZeros())
            return;
        RemovesLeadingZeros = true;
        TrackCount = Unpadded(TrackCount);
        DiscNumber = Unpadded(DiscNumber);
        DiscCount = Unpadded(DiscCount);
        LeadingZerosText = "Removed on Save";
        RemoveLeadingZerosCommand.NotifyCanExecuteChanged();

        static string Unpadded(string text) =>
            IsPadded(text) && int.TryParse(text, out var n) && n > 0 ? n.ToString() : text;
    }

    private static NumberTexts CachedNumbers(Track track) => new(
        track.TrackNumber?.ToString() ?? "", track.TrackCount?.ToString() ?? "",
        track.DiscNumber?.ToString() ?? "", track.DiscCount?.ToString() ?? "");

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
        var placeholder =
            IsSelectionMode ? "Will be cleared on every selected track"
            : IsAlbumMode ? "Will be cleared on every track"
            : "Will be cleared";
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

    /// <summary>"1400 × 1400" under the cover: the current one, or the one Save would write.</summary>
    [ObservableProperty]
    private string _artSizeText = "";

    /// <summary>"JPG" or "PNG".</summary>
    [ObservableProperty]
    private string _artFormatText = "";

    /// <summary>Where the shown cover comes from, for the tooltip.</summary>
    [ObservableProperty]
    private string? _artSourceText;

    private async Task LoadPreviewAsync(Album album, AlbumArtService artService)
    {
        var infoTask = Task.Run(() => ReadCurrentArtInfo(album));
        ArtPreview = await artService.GetAlbumArtAsync(album, decodeWidth: 200);

        var (info, source) = await infoTask;

        // A cover picked while this was reading has already replaced both.
        if (PickedArtFilePath is null && _downloadedArt is null)
            ShowArtInfo(info, source);
    }

    /// <summary>
    /// The same cover the preview shows, found the way <see cref="AlbumArtService"/>
    /// finds it: embedded in one of the first five tracks, else the folder file.
    /// </summary>
    private static (ImageInfo? Info, string? Source) ReadCurrentArtInfo(Album album)
    {
        foreach (var track in album.Tracks.Take(5))
        {
            if (TagReader.ReadEmbeddedArt(track.FilePath) is { } embedded)
                return (ImageInfo.Read(embedded), "Embedded in the tracks");
        }

        var folderArt = album.FolderArtPath;
        if (string.IsNullOrEmpty(folderArt))
            return (null, null);

        try
        {
            return (ImageInfo.Read(File.ReadAllBytes(folderArt)), $"Folder file: {Path.GetFileName(folderArt)}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (null, null);
        }
    }

    private void ShowArtInfo(ImageInfo? info, string? source)
    {
        ArtSizeText = info?.SizeText ?? "";
        ArtFormatText = info?.FormatText ?? (source is null ? "" : "Unknown format");
        ArtSourceText = source;
    }

    /// <summary>The album's files, for "Save Embedded Art".</summary>
    private readonly IReadOnlyList<string> _albumTrackPaths = [];

    /// <summary>
    /// The cover.jpg of each folder that has one after "Save Embedded Art" (written,
    /// or already there and no smaller), keyed by folder. The window that opened
    /// this dialog teaches the library about them, even if the dialog is cancelled:
    /// the files were written the moment the button was pressed.
    /// </summary>
    public Dictionary<string, string> SavedCovers { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The running (or last) extraction, so a caller can wait for it after the dialog closes.</summary>
    public Task ArtExtraction { get; private set; } = Task.CompletedTask;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveEmbeddedArtCommand))]
    private bool _isExtractingArt;

    /// <summary>What "Save Embedded Art" did. Shown on the dialog's message line when nothing is wrong.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FooterMessage))]
    private string _artMessage = "";

    /// <summary>The dialog's message line: a validation problem first, else the last art result.</summary>
    public string FooterMessage => ValidationError.Length > 0 ? ValidationError : ArtMessage;

    partial void OnValidationErrorChanged(string value) => OnPropertyChanged(nameof(FooterMessage));

    /// <summary>
    /// Writes the album's embedded art to cover.jpg beside the files, now rather
    /// than on Save. See <see cref="EmbeddedArtExtractor"/> for which picture wins
    /// and why nothing is compressed.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSaveEmbeddedArt))]
    private Task SaveEmbeddedArt()
    {
        IsExtractingArt = true;
        ArtMessage = "Reading the embedded art...";
        ArtExtraction = ExtractAsync();
        return ArtExtraction;

        async Task ExtractAsync()
        {
            try
            {
                var results = await Task.Run(() =>
                    EmbeddedArtExtractor.ExtractToFolders(_albumTrackPaths, CoverJpeg.FromPng));

                foreach (var r in results)
                {
                    if (r.Outcome is ExtractOutcome.Saved or ExtractOutcome.KeptExisting && r.CoverPath is not null)
                        SavedCovers[r.Directory] = r.CoverPath;
                }

                ArtMessage = EmbeddedArtExtractor.Describe(results);
            }
            catch (Exception ex)
            {
                ArtMessage = $"Couldn't save the embedded art: {ex.Message}";
            }
            finally
            {
                IsExtractingArt = false;
            }
        }
    }

    private bool CanSaveEmbeddedArt() => ShowsArt && !IsExtractingArt;

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
            _downloadedArt = null;
            ArtPreview = bitmap;
            ShowArtInfo(ImageInfo.Read(File.ReadAllBytes(dialog.FileName)),
                        $"New cover, from {Path.GetFileName(dialog.FileName)} - written on Save");
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
        // A selection whose tracks disagree on the album starts with an empty box,
        // and empty there means "keep each track's own". Only emptying an album
        // that was there is refused.
        var albumMayBeEmpty = IsSelectionMode && _initial.GetValueOrDefault(nameof(AlbumTitle)) is "";
        if (string.IsNullOrWhiteSpace(AlbumTitle) && !albumMayBeEmpty)
        {
            ValidationError = "Album can't be empty.";
            CanSave = false;
            return;
        }

        if (Year.Trim().Length > 0 && !ReleaseDate.TryParse(Year, out _, out _))
        {
            ValidationError = "Year must be a year (2026) or a date (2026-10-02).";
            CanSave = false;
            return;
        }

        var numeric = new[]
        {
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

    /// <summary>The Year box as a year and, when it names a month or day, the full date.</summary>
    private (int? Year, string? Date) ParsedDate() =>
        ReleaseDate.TryParse(Year, out var date, out var year)
            ? (year, ReleaseDate.HasMonth(date) ? date : null)
            : (null, null);

    public TrackTagEdit BuildTrackEdit()
    {
        var (year, date) = ParsedDate();
        return new(
            Title.Trim(), Artist.Trim(), AlbumArtist.Trim(), AlbumTitle.Trim(),
            year,
            ParseOrNull(TrackNumber), ParseOrNull(TrackCount),
            ParseOrNull(DiscNumber), ParseOrNull(DiscCount))
        {
            Details = BuildDetailsEdit(),
            Date = date,
        };
    }

    /// <summary>
    /// Album and track dialogs: whether anything differs from what the boxes
    /// started with - typed text, a cleared field, Remove Leading Zeros, or a new
    /// cover. "Save Embedded Art" isn't counted: it wrote its files when pressed.
    /// Asked before the window moves on to another album or track.
    /// </summary>
    public bool HasChanges =>
        Typed(nameof(Title), Title) || Typed(nameof(TrackNumber), TrackNumber)
        || Typed(nameof(Artist), Artist) || Typed(nameof(AlbumArtist), AlbumArtist)
        || Typed(nameof(AlbumTitle), AlbumTitle) || Typed(nameof(Year), Year)
        || BuildDetailsEdit() != TagDetailsEdit.None
        || NumberIfChanged(nameof(TrackCount), TrackCount) is not null
        || NumberIfChanged(nameof(DiscNumber), DiscNumber) is not null
        || NumberIfChanged(nameof(DiscCount), DiscCount) is not null
        || RemovesLeadingZeros || PickedArtFilePath is not null || _downloadedArt is not null;

    private bool Typed(string name, string value) =>
        !string.Equals(value.Trim(), _initial.GetValueOrDefault(name, "").Trim(), StringComparison.Ordinal);

    public AlbumTagEdit BuildAlbumEdit()
    {
        var (year, date) = ParsedDate();
        return new(Artist.Trim(), AlbumArtist.Trim(), AlbumTitle.Trim(), year)
        {
            TrackCount = NumberIfChanged(nameof(TrackCount), TrackCount),
            DiscNumber = NumberIfChanged(nameof(DiscNumber), DiscNumber),
            DiscCount = NumberIfChanged(nameof(DiscCount), DiscCount),
            Details = BuildDetailsEdit(),
            Date = date,
            RemoveLeadingZeros = RemovesLeadingZeros,
        };
    }

    public TracksTagEdit BuildTracksEdit()
    {
        var (year, date) = ParsedDate();
        return new()
        {
            Artist = IfChanged(nameof(Artist), Artist),
            AlbumArtist = IfChanged(nameof(AlbumArtist), AlbumArtist),
            Album = IfChanged(nameof(AlbumTitle), AlbumTitle),
            Date = IfChanged(nameof(Year), Year) is null ? null : new DateEdit(year, date),
            TrackCount = NumberIfChanged(nameof(TrackCount), TrackCount),
            DiscNumber = NumberIfChanged(nameof(DiscNumber), DiscNumber),
            DiscCount = NumberIfChanged(nameof(DiscCount), DiscCount),
            Details = BuildDetailsEdit(),
        };
    }

    /// <summary>Takes a cover from "Search Internet" in place of any file picked earlier.</summary>
    public void UseDownloadedArt(byte[] jpeg)
    {
        try
        {
            ArtPreview = ArtSearchViewModel.Decode(jpeg, 200);
            _downloadedArt = jpeg;
            PickedArtFilePath = null;
            ShowArtInfo(ImageInfo.Read(jpeg), "New cover, downloaded - written on Save");
        }
        catch (Exception ex) when (ex is NotSupportedException or IOException or ArgumentException)
        {
            ValidationError = $"Couldn't open that image: {ex.Message}";
        }
    }

    public ArtPayload? PickedArtPayload()
    {
        if (_downloadedArt is { } downloaded)
            return new ArtPayload(downloaded, "image/jpeg");

        if (PickedArtFilePath is null)
            return null;

        var bytes = File.ReadAllBytes(PickedArtFilePath);
        var mimeType = Path.GetExtension(PickedArtFilePath).Equals(".png", StringComparison.OrdinalIgnoreCase)
            ? "image/png"
            : "image/jpeg";

        return new ArtPayload(bytes, mimeType);
    }
}
