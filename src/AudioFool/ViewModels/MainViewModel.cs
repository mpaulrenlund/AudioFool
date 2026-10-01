using System.Collections.ObjectModel;
using System.IO;
using System.Runtime;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AudioFool.Core.Art;
using AudioFool.Core.Library;
using AudioFool.Core.Models;
using AudioFool.Core.Playback;
using AudioFool.Core.Scrobbling;
using AudioFool.Core.Settings;
using AudioFool.Formatting;
using AudioFool.Services;

namespace AudioFool.ViewModels;

public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private const int NowPlayingArtWidth = 128;
    private const int AlbumHeaderArtWidth = 320;

    private readonly AudioEngine _engine;
    private readonly BassRuntime _runtime;
    private readonly AlbumArtService _artService;
    private readonly AppSettings _settings;
    private readonly DispatcherTimer _positionTimer;
    private readonly DispatcherTimer _searchDebounce;
    private readonly LastFmScrobbler _scrobbler;

    private MusicLibrary _library = MusicLibrary.Empty;
    private MusicLibrary _folderFilteredLibrary = MusicLibrary.Empty;
    private LibraryCache? _cache;
    private CancellationTokenSource? _scanCts;
    private double? _pendingSeek;
    private bool _updatingPositionFromTimer;

    public MainViewModel(AudioEngine engine, BassRuntime runtime, AlbumArtService artService, AppSettings settings)
    {
        _engine = engine;
        _runtime = runtime;
        _artService = artService;
        _settings = settings;

        _engine.Volume = _volumeState.Effective;

        // Bit-perfect starts off every launch, like the volume above, rather than
        // being restored. Exclusive mode silences every other application on the
        // machine, so it is something to opt into for a listening session - not a
        // state to be surprised by because it was still on days ago.
        settings.OutputMode = OutputMode.Shared;
        _engine.OutputMode = OutputMode.Shared;

        _engine.DsdMode = settings.DsdMode;
        _engine.Shuffle = settings.Shuffle;
        _engine.Repeat = settings.Repeat;

        _engine.TrackChanged += OnEngineTrackChanged;
        _engine.StateChanged += OnEngineStateChanged;
        _engine.PlaybackFinished += OnEnginePlaybackFinished;

        _scrobbler = new LastFmScrobbler(ScrobbleQueue.Load(ScrobbleQueue.DefaultPath, DateTimeOffset.UtcNow))
        {
            Enabled = settings.LastFmScrobbling,
        };
        _scrobbler.AuthFailed += OnScrobblerAuthFailed;
        _scrobbler.StatusChanged += OnScrobblerStatusChanged;
        if (settings is { LastFmApiKey: { Length: > 0 } key, LastFmApiSecret: { Length: > 0 } secret,
                          LastFmSessionKey: { Length: > 0 } session })
            _scrobbler.Connect(key, secret, new LastFmSession(settings.LastFmUserName ?? "", session));
        RefreshLastFmIndicator();
        RefreshEmptyState();

        _positionTimer =new DispatcherTimer(DispatcherPriority.Normal)
        {
            Interval = TimeSpan.FromMilliseconds(250),
        };
        _positionTimer.Tick += OnPositionTick;

        _searchDebounce = new DispatcherTimer(DispatcherPriority.Input)
        {
            Interval = TimeSpan.FromMilliseconds(180),
        };
        _searchDebounce.Tick += OnSearchDebounceTick;

        // Two entries for one folder each get a tick, and both must be ticked for
        // its tracks to show. Merge them; the folder stays ticked if any copy was.
        var folders = MusicFolderList.Distinct(settings.MusicFolders);
        MusicFolders = new ObservableCollection<string>(folders);

        foreach (var folder in folders)
        {
            var enabled = settings.MusicFolders.Any(f => MusicFolderList.SameFolder(f, folder)
                && !settings.DisabledFolders.Contains(f, StringComparer.OrdinalIgnoreCase));
            var item = new FolderFilterItem(folder, enabled);
            item.PropertyChanged += OnFolderFilterItemChanged;
            FolderFilters.Add(item);
        }

        if (!folders.SequenceEqual(settings.MusicFolders, StringComparer.Ordinal))
            SaveFolderSettings();
    }

    private void SaveFolderSettings()
    {
        _settings.MusicFolders = [.. MusicFolders];
        _settings.DisabledFolders = [.. FolderFilters
            .Where(f => !f.IsEnabled)
            .Select(f => f.FolderPath)];
        _settings.Save();
    }

    private void OnFolderFilterItemChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(FolderFilterItem.IsEnabled))
            return;

        SaveFolderSettings();

        ApplyToView(keepSelection: true);
        StatusText = DescribeStatus(default);
    }

    public ObservableCollection<ArtistGroup> Artists { get; } = [];
    public ObservableCollection<AlbumItemViewModel> Albums { get; } = [];
    public ObservableCollection<Track> Tracks { get; } = [];
    public ObservableCollection<string> MusicFolders { get; }
    public ObservableCollection<FolderFilterItem> FolderFilters { get; } = [];

    [ObservableProperty]
    private ArtistGroup? _selectedArtist;

    [ObservableProperty]
    private AlbumItemViewModel? _selectedAlbum;

    [ObservableProperty]
    private BitmapSource? _selectedAlbumArt;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WindowTitle))]
    [NotifyPropertyChangedFor(nameof(NowPlayingFormat))]
    private Track? _nowPlaying;

    /// <summary>
    /// The now-playing format line (spec 6.7): "MP3 · 320 kbps · 44.1 kHz",
    /// leaving out whatever the file doesn't report.
    /// </summary>
    public string NowPlayingFormat => NowPlaying is { } track
        ? string.Join(" · ", new[] { track.Kind, Display.Bitrate(track.Bitrate), Display.SampleRate(track.SampleRate) }
            .Where(part => part.Length > 0))
        : "";

    /// <summary>
    /// The window's own title, which the in-app title bar does not show. It is
    /// what the taskbar thumbnail, its tooltip and Alt-Tab read.
    /// </summary>
    public string WindowTitle => NowPlaying is { } track
        ? string.IsNullOrWhiteSpace(track.Artist)
            ? track.DisplayTitle
            : $"{track.Artist} – {track.DisplayTitle}"
        : "AudioFool";

    [ObservableProperty]
    private BitmapSource? _nowPlayingArt;

    [ObservableProperty]
    private bool _isPlaying;

    [ObservableProperty]
    private bool _isScanning;

    [ObservableProperty]
    private double _scanFraction;

    [ObservableProperty]
    private string _statusText = "Ready";

    [ObservableProperty]
    private double _durationSeconds = 1.0;

    [ObservableProperty]
    private string _positionDisplay = "0:00";

    [ObservableProperty]
    private string _durationDisplay = "0:00";

    /// <summary>True while the user has hold of the seek bar, so the timer stops fighting them.</summary>
    public bool IsSeeking { get; set; }

    private double _positionSeconds;
    public double PositionSeconds
    {
        get => _positionSeconds;
        set
        {
            if (!SetProperty(ref _positionSeconds, value))
                return;

            // A change the timer didn't make is the user moving the seek bar.
            if (!_updatingPositionFromTimer)
                _pendingSeek = value;

            PositionDisplay = Display.Time(TimeSpan.FromSeconds(value));
        }
    }

    private readonly VolumeState _volumeState = new(1.0);

    /// <summary>
    /// What the slider shows: zero while muted. Moving it unmutes at the new
    /// level (spec 6.7).
    /// </summary>
    public double Volume
    {
        get => _volumeState.Effective;
        set
        {
            var wasMuted = _volumeState.IsMuted;
            _volumeState.SetLevel(value);
            ApplyVolume(wasMuted);
        }
    }

    /// <summary>Muted from the speaker button. Not saved: every launch starts unmuted.</summary>
    public bool IsMuted => _volumeState.IsMuted;

    public string MuteLabel => IsMuted ? "Unmute" : "Mute";

    [RelayCommand]
    private void ToggleMute()
    {
        _volumeState.ToggleMute();
        ApplyVolume(!_volumeState.IsMuted);
    }

    private void ApplyVolume(bool wasMuted)
    {
        _engine.Volume = _volumeState.Effective;
        _settings.Volume = _volumeState.Level;
        OnPropertyChanged(nameof(Volume));

        if (wasMuted != _volumeState.IsMuted)
        {
            OnPropertyChanged(nameof(IsMuted));
            OnPropertyChanged(nameof(MuteLabel));
        }
    }

    // ---------------------------------------------------------------- search

    private string _searchQuery = "";

    /// <summary>
    /// Bound to the search box. Applying the filter is deferred by a moment so a
    /// burst of keystrokes rebuilds the tree once rather than once per letter.
    /// </summary>
    public string SearchQuery
    {
        get => _searchQuery;
        set
        {
            if (!SetProperty(ref _searchQuery, value ?? ""))
                return;

            OnPropertyChanged(nameof(IsSearching));

            _searchDebounce.Stop();
            _searchDebounce.Start();
        }
    }

    public bool IsSearching => LibrarySearch.Terms(SearchQuery).Length > 0;

    /// <summary>
    /// Configured folders that were unreachable at the last scan - an unplugged
    /// drive, usually. Their tracks stay listed but cannot be played.
    /// </summary>
    [ObservableProperty]
    private IReadOnlyList<string> _unavailableFolders = [];


    /// <summary>How many tracks the current search matched, for the summary line.</summary>
    [ObservableProperty]
    private int _matchedTrackCount;

    /// <summary>Distinguishes "no results" from "nothing scanned yet".</summary>
    public bool HasNoSearchResults => IsSearching && Artists.Count == 0;

    [RelayCommand]
    private void ClearSearch() => SearchQuery = "";

    // --------------------------------------------------------- library filter

    /// <summary>
    /// A subset chosen from the Statistics window - "Missing Year", "FLAC" - that
    /// the browser is narrowed to. Search runs inside it rather than replacing it,
    /// so "Missing Year" plus a search for an artist finds that artist's untagged
    /// tracks. Session-only: never saved, so the app cannot open mysteriously
    /// showing a fraction of the library.
    /// <para>
    /// Re-applied on every rebuild, including the one after a tag edit, so a track
    /// fixed through the filtered view drops out of it and the count goes down.
    /// </para>
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFiltered))]
    private TrackFilter? _libraryFilter;

    public bool IsFiltered => LibraryFilter is not null;

    /// <summary>Tracks passing the library filter, before any search narrows them further.</summary>
    private int _filteredTrackCount;

    partial void OnLibraryFilterChanged(TrackFilter? value)
    {
        ApplyToView(keepSelection: true);
        StatusText = DescribeStatus(default);
    }

    [RelayCommand]
    private void ClearLibraryFilter() => LibraryFilter = null;

    /// <summary>
    /// Selects an artist in the sidebar, for a top-artist row in Statistics. Any
    /// filter or search hiding them is cleared first - the request was to see
    /// this artist, so a view that could not show them would be the wrong answer.
    /// </summary>
    public void ShowArtist(string name)
    {
        LibraryFilter = null;

        if (!Artists.Any(a => SortRules.NameComparer.Equals(a.Name, name)) && IsSearching)
        {
            // Applied now rather than after the debounce, so the artist is there to select.
            _searchQuery = "";
            OnPropertyChanged(nameof(SearchQuery));
            OnPropertyChanged(nameof(IsSearching));
            ApplyToView(keepSelection: true);
            StatusText = DescribeStatus(default);
        }

        if (Artists.FirstOrDefault(a => SortRules.NameComparer.Equals(a.Name, name)) is { } artist)
            SelectedArtist = artist;
    }

    private void OnSearchDebounceTick(object? sender, EventArgs e)
    {
        _searchDebounce.Stop();

        // Keep whatever is selected if it survives the new filter, and only fall
        // back to the first row when it doesn't. That covers both directions:
        // narrowing past the current artist moves you to a match, while clearing the
        // box leaves you exactly where you had navigated to.
        ApplyToView(keepSelection: true);
        StatusText = DescribeStatus(default);
    }

    // ------------------------------------------------------------- hotkeys

    /// <summary>
    /// Whether to claim the system-wide F9/F10/F11 keys. Read from settings only,
    /// with no UI: taking keys away from every other app is a decision worth making
    /// deliberately by editing settings.json rather than by a stray click.
    /// </summary>
    public bool GlobalHotkeysEnabled => _settings.GlobalHotkeys;

    /// <summary>
    /// Called once registration has been attempted. Keys another application already
    /// owns cannot be claimed, and saying so beats leaving the user pressing a key
    /// that silently does nothing.
    /// </summary>
    public void ReportHotkeys(IReadOnlyList<string> unavailable)
    {
        if (unavailable.Count == 0)
            return;

        StatusText = unavailable.Count == 1
            ? $"{unavailable[0]} is already in use by another app, so that shortcut won't work."
            : $"{string.Join(", ", unavailable)} are already in use by other apps, so those shortcuts won't work.";
    }

    // ---------------------------------------------------------------- output

    /// <summary>Bound to the toolbar toggle. Off = shared, on = exclusive.</summary>
    public bool IsExclusiveOutput
    {
        get => _settings.OutputMode == OutputMode.Exclusive;
        set
        {
            var mode = value ? OutputMode.Exclusive : OutputMode.Shared;
            if (_settings.OutputMode == mode)
                return;

            _settings.OutputMode = mode;
            _settings.Save();
            _engine.OutputMode = mode;

            OnPropertyChanged();

            // DoP is only meaningful over an exclusive connection.
            OnPropertyChanged(nameof(CanUseDsdPassthrough));
            RefreshOutputState();
        }
    }

    /// <summary>Greyed out when the device can't do exclusive mode at all.</summary>
    public bool CanUseExclusiveOutput => _runtime.SupportsExclusive;

    /// <summary>DSD-over-PCM passthrough, for a DAC that can unwrap it.</summary>
    public bool IsDsdPassthrough
    {
        get => _settings.DsdMode == DsdMode.DsdOverPcm;
        set
        {
            var mode = value ? DsdMode.DsdOverPcm : DsdMode.ConvertToPcm;
            if (_settings.DsdMode == mode)
                return;

            _settings.DsdMode = mode;
            _settings.Save();
            _engine.DsdMode = mode;

            OnPropertyChanged();
            RefreshOutputState();
        }
    }

    /// <summary>
    /// DoP needs both an exclusive connection and a device that can clock the DoP
    /// rate - one sixteenth of the DSD rate, so 176.4 kHz for DSD64 upwards.
    /// </summary>
    public bool CanUseDsdPassthrough => _runtime.SupportsDop && IsExclusiveOutput;

    public string OutputDeviceName => _runtime.OutputDeviceName;

    [ObservableProperty]
    private string _outputDescription = "";

    /// <summary>
    /// Whether the status bar shows the output readout. See <see cref="ShouldShowOutput"/>.
    /// </summary>
    [ObservableProperty]
    private bool _isOutputActive;

    /// <summary>
    /// Exclusive mode pins the mixer to unity gain, because attenuating in software
    /// would stop the output being bit-perfect. The slider goes dead rather than
    /// quietly lying about it.
    /// </summary>
    [ObservableProperty]
    private bool _isVolumeEnabled = true;

    public string VolumeTooltip => IsVolumeEnabled
        ? "Volume"
        : "Fixed at 100% in exclusive mode - software attenuation would break bit-perfect output. Use your DAC or Windows volume.";

    /// <summary>
    /// The readout describes a live device connection, so it is only meaningful
    /// while something is loaded. Paused counts: the device is still open and the
    /// description still true, and hiding it on every pause would just flicker.
    /// </summary>
    private bool ShouldShowOutput() =>
        _engine.HasOutput && _engine.State != PlaybackState.Stopped;

    private void RefreshOutputState()
    {
        OutputDescription = _engine.OutputDescription;
        IsOutputActive = ShouldShowOutput();
        IsVolumeEnabled = _engine.SupportsVolume;
        OnPropertyChanged(nameof(VolumeTooltip));

        if (_engine.OutputWarning is { } warning)
            StatusText = warning;
    }

    /// <summary>Header text over the track list.</summary>
    public string AlbumHeaderTitle => SelectedAlbum?.Album.Title ?? "";

    /// <summary>
    /// The four stacked lines under the header title (spec 6.5): artist, year,
    /// "N tracks" and the duration. No disc count (the user's call).
    /// </summary>
    public string AlbumHeaderArtist => SelectedAlbum?.Album.ArtistName ?? "";

    public string AlbumHeaderYear => SelectedAlbum?.Album.YearDisplay ?? "";

    public string AlbumHeaderTrackCount => SelectedAlbum?.Album.TrackCountDisplay ?? "";

    /// <summary>
    /// "1:06:27", or "41:40" under an hour: the Time column's clock format, not
    /// the spec's "1 hr 6 min" (the user's call).
    /// </summary>
    public string AlbumHeaderDuration =>
        SelectedAlbum is { } album ? Display.Time(album.Album.TotalDuration) : "";

    // ---------------------------------------------------------------- startup

    public async Task InitialiseAsync()
    {
        if (!_runtime.Initialise())
        {
            StatusText = _runtime.InitError ?? "Audio engine unavailable.";
        }
        else if (_runtime.MissingFormats.Count > 0)
        {
            StatusText = $"Audio ready. {_runtime.MissingFormats.Count} optional add-on(s) not found - " +
                         "some formats won't play. See Help for the list.";
        }

        // The device is only interrogated inside Initialise, which runs after the
        // window has already bound. Without these the bit-perfect toggle stays
        // disabled for the whole session even on a device that supports it.
        OnPropertyChanged(nameof(CanUseExclusiveOutput));
        OnPropertyChanged(nameof(CanUseDsdPassthrough));
        OnPropertyChanged(nameof(OutputDeviceName));
        OnPropertyChanged(nameof(IsExclusiveOutput));

        // A saved preference for exclusive output is meaningless if this machine's
        // device can't do it; fall back rather than silently failing on first play.
        if (_settings.OutputMode == OutputMode.Exclusive && !_runtime.SupportsExclusive)
        {
            _settings.OutputMode = OutputMode.Shared;
            _engine.OutputMode = OutputMode.Shared;
            OnPropertyChanged(nameof(IsExclusiveOutput));
            StatusText = $"{_runtime.OutputDeviceName} does not support exclusive mode; using shared output.";
        }

        await LoadLibraryAsync();
    }

    /// <summary>
    /// Shows the cached library immediately, then checks the filesystem for changes
    /// in the background. Reading tags is the only slow part of a scan, so a start
    /// that reuses them is effectively instant; the change check that follows costs
    /// about a second whatever the library size.
    /// </summary>
    private async Task LoadLibraryAsync()
    {
        if (MusicFolders.Count == 0)
        {
            StatusText = "No music folders yet. Use \"Add folder\" to point at your music.";
            return;
        }

        _cache = await Task.Run(LibraryCache.Load);

        // A portable drive can come back under a different letter. Re-point the
        // folder and every cached path before scanning, so a letter change costs
        // nothing instead of a full re-read of every tag.
        await RelocateMovedFoldersAsync();

        // Only display the cache directly when it was built from the folders being
        // watched now - otherwise it could describe folders that have since been
        // removed. Its tags stay useful to the scan below either way.
        if (_cache is not null && _cache.CoversSameFolders(MusicFolders))
        {
            ApplyLibrary(await Task.Run(() => LibraryScanner.Build(_cache.Tracks)), keepSelection: false);
            StatusText = $"{DescribeLibrary()} · checking for changes...";
        }

        await ScanAsync();
    }

    /// <summary>
    /// Finds music folders that have changed drive letter and re-points them, along
    /// with the cached tags. A candidate drive is only accepted once files the cache
    /// knows about are confirmed present there, so this can't latch onto an
    /// unrelated drive that happens to have a folder of the same name.
    /// </summary>
    private async Task RelocateMovedFoldersAsync()
    {
        if (_cache is null || MusicFolders.Count == 0)
            return;

        var moves = await Task.Run(() =>
        {
            var found = new List<LibraryRelocator.Relocation>();
            foreach (var folder in MusicFolders)
            {
                if (LibraryRelocator.FindRelocation(folder, _cache.Tracks) is { } move)
                    found.Add(move);
            }

            return found;
        });

        if (moves.Count == 0)
            return;

        var tracks = _cache.Tracks;
        foreach (var move in moves)
        {
            var filterItem = FolderFilters.FirstOrDefault(f => MusicFolderList.SameFolder(f.FolderPath, move.OldFolder));

            // The new place may be listed already - added by hand while the drive
            // had its old letter. Re-pointing the old entry would list it twice, so
            // the old entry goes, and the listed one keeps its own tick.
            if (MusicFolders.Any(f => MusicFolderList.SameFolder(f, move.NewFolder)))
            {
                var index = MusicFolders.IndexOf(move.OldFolder);
                if (index >= 0)
                    MusicFolders.RemoveAt(index);

                if (filterItem is not null)
                {
                    filterItem.PropertyChanged -= OnFolderFilterItemChanged;
                    FolderFilters.Remove(filterItem);
                }
            }
            else
            {
                var index = MusicFolders.IndexOf(move.OldFolder);
                if (index >= 0)
                    MusicFolders[index] = move.NewFolder;

                if (filterItem is not null)
                    filterItem.FolderPath = move.NewFolder;
            }

            tracks = LibraryRelocator.Rebase(tracks, move.OldFolder, move.NewFolder);
        }

        // Two cached copies of a track can now share a path; keep one.
        tracks = [.. tracks.DistinctBy(t => t.FilePath, StringComparer.OrdinalIgnoreCase)];

        SaveFolderSettings();

        // Keeps the loaded version: relocating is not a re-read, and stamping an
        // older cache current here would stop the scan that follows from filling it.
        _cache = LibraryCache.From(MusicFolders, tracks, _cache.Version);
        await Task.Run(_cache.Save, CancellationToken.None);

        var first = moves[0];
        StatusText = moves.Count == 1
            ? $"Music folder moved to {first.NewFolder} - re-pointed {tracks.Count:N0} tracks."
            : $"{moves.Count} music folders moved to new drive letters - re-pointed {tracks.Count:N0} tracks.";
    }


    // ------------------------------------------------------------- selection

    partial void OnSelectedArtistChanged(ArtistGroup? value)
    {
        Albums.Clear();

        if (value is not null)
        {
            foreach (var album in value.Albums)
                Albums.Add(new AlbumItemViewModel(album, _artService));
        }

        // The spec is artist -> albums -> songs, but landing on an empty track
        // pane feels broken, so open the oldest album straight away.
        SelectedAlbum = Albums.FirstOrDefault();
    }

    /// <summary>
    /// Raised just before the track list is replaced - another album, a scan, a
    /// save - so the window can end an in-place edit: the grid cannot keep one
    /// open while its rows are cleared.
    /// </summary>
    public event EventHandler? TracksChanging;

    partial void OnSelectedAlbumChanged(AlbumItemViewModel? value)
    {
        TracksChanging?.Invoke(this, EventArgs.Empty);
        Tracks.Clear();

        if (value is not null)
        {
            foreach (var track in value.Album.Tracks)
                Tracks.Add(track);
        }

        OnPropertyChanged(nameof(AlbumHeaderTitle));
        OnPropertyChanged(nameof(AlbumHeaderArtist));
        OnPropertyChanged(nameof(AlbumHeaderYear));
        OnPropertyChanged(nameof(AlbumHeaderTrackCount));
        OnPropertyChanged(nameof(AlbumHeaderDuration));

        _ = LoadAlbumHeaderArtAsync(value);
    }

    // ------------------------------------------------------------- art viewer

    /// <summary>
    /// Full-resolution art for the selected album, for the enlarged view. Null when
    /// the album has no cover, in which case there's nothing to open.
    /// </summary>
    public Task<BitmapSource?> GetSelectedAlbumFullArtAsync() =>
        SelectedAlbum is { } item
            ? _artService.GetFullAlbumArtAsync(item.Album)
            : Task.FromResult<BitmapSource?>(null);

    public string SelectedAlbumCaption =>
        SelectedAlbum is { } item
            ? $"{item.Album.ArtistName} - {item.Album.Title}"
            : "Album art";

    /// <summary>Full-resolution art for whatever is playing.</summary>
    public Task<BitmapSource?> GetNowPlayingFullArtAsync() =>
        NowPlaying is { } track
            ? _artService.GetFullTrackArtAsync(track)
            : Task.FromResult<BitmapSource?>(null);

    public string NowPlayingCaption =>
        NowPlaying is { } track
            ? $"{(string.IsNullOrWhiteSpace(track.AlbumArtist) ? track.Artist : track.AlbumArtist)} - {track.Album}"
            : "Album art";

    private async Task LoadAlbumHeaderArtAsync(AlbumItemViewModel? item)
    {
        if (item is null)
        {
            SelectedAlbumArt = null;
            return;
        }

        var art = await _artService.GetAlbumArtAsync(item.Album, AlbumHeaderArtWidth);

        // The user may have clicked elsewhere while this was decoding.
        if (ReferenceEquals(SelectedAlbum, item))
            SelectedAlbumArt = art;
    }

    // -------------------------------------------------------------- scanning

    [RelayCommand]
    private async Task ScanAsync()
    {
        if (IsScanning)
            return;

        _scanCts?.Cancel();
        _scanCts = new CancellationTokenSource();
        var token = _scanCts.Token;

        IsScanning = true;
        ScanFraction = 0;

        // A library already on screen means this is a background refresh, and the
        // status shouldn't shout "Scanning..." over a perfectly usable view.
        var refreshing = Artists.Count > 0;
        if (!refreshing)
            StatusText = "Scanning...";

        // A cache from an older version is on screen but missing a field; every
        // tag is read again, once, behind the usable view.
        var reread = _cache?.NeedsReread == true;

        try
        {
            var progress = new Progress<ScanProgress>(p =>
            {
                ScanFraction = p.Fraction;
                StatusText = reread && refreshing
                    ? $"Updating the library for this version... {p.FilesRead:N0} of {p.FilesFound:N0} files"
                    : refreshing
                    ? $"Updating... {p.FilesRead:N0} of {p.FilesFound:N0} changed files"
                    : $"Scanning... {p.FilesRead:N0} of {p.FilesFound:N0} files";
            });

            var result = await LibraryScanner.ScanAsync(
                [.. MusicFolders], _cache?.ByPath(), progress, token, rereadTags: reread);

            UnavailableFolders = result.UnavailableFolders;

            // Nothing changed on disk, so leave the view - and whatever the user has
            // selected in it - completely alone.
            if (refreshing && !result.Summary.AnyChanges)
            {
                _library = result.Library;
                StatusText = DescribeStatus(result.Summary);
                return;
            }

            ApplyLibrary(result.Library, keepSelection: refreshing);
            StatusText = DescribeStatus(result.Summary);

            // Safe to persist even with a drive missing: the scanner carries those
            // tracks over rather than reporting them gone, so the cache keeps them.
            // A re-read that could not reach a drive carried that drive's tracks
            // over without the new field, so the cache keeps its old version and
            // the next start reads again.
            var version = reread && result.UnavailableFolders.Count > 0 ? _cache!.Version : LibraryCache.CurrentVersion;
            _cache = LibraryCache.From(MusicFolders, _library.AllTracks, version);
            await Task.Run(_cache.Save, CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            // A cancelled refresh is routine - the user changed folders or quit.
        }
        catch (Exception ex)
        {
            StatusText = $"Scan failed: {ex.Message}";
        }
        finally
        {
            IsScanning = false;
            ScanFraction = 0;
            ReclaimScanMemory();
        }
    }

    /// <summary>
    /// Swaps in a new library. <paramref name="keepSelection"/> re-picks the same
    /// artist and album by name afterwards: a background refresh can land while the
    /// user is browsing, and yanking them back to the first artist would be worse
    /// than the stale row they were looking at.
    /// </summary>
    private void ApplyLibrary(MusicLibrary library, bool keepSelection)
    {
        _library = library;
        ApplyToView(keepSelection);
    }

    /// <summary>
    /// Rebuilds the visible tree from the full library, narrowed by the current
    /// search. Scanning and searching both come through here, so there's a single
    /// definition of what ends up on screen.
    /// </summary>
    private void ApplyToView(bool keepSelection)
    {
        var artistName = keepSelection ? SelectedArtist?.Name : null;
        var albumTitle = keepSelection ? SelectedAlbum?.Album.Title : null;

        // Build a list of path prefixes for disabled folders so we can exclude their tracks.
        var disabledPrefixes = FolderFilters
            .Where(f => !f.IsEnabled)
            .Select(f => f.FolderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                         + Path.DirectorySeparatorChar)
            .ToList();

        IReadOnlyList<Track> visible = disabledPrefixes.Count == 0
            ? _library.AllTracks
            : _library.AllTracks
                .Where(t => !disabledPrefixes.Any(p =>
                    t.FilePath.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
                .ToList();

        // Keep a folder-filtered view for status bar counts, independent of any
        // active search query so the counts always reflect enabled libraries.
        _folderFilteredLibrary = ReferenceEquals(visible, _library.AllTracks)
            ? _library
            : LibraryScanner.Build(visible);

        // The statistics filter narrows what search sees; the status-bar library
        // above stays unfiltered, so its counts still describe the whole library.
        IReadOnlyList<Track> filtered = LibraryFilter is { } filter
            ? visible.Where(filter.Matches).ToList()
            : visible;
        _filteredTrackCount = filtered.Count;

        var matched = LibrarySearch.Filter(filtered, SearchQuery);

        // Rebuilding from the filtered tracks means the grouping and the three sort
        // rules apply to search results exactly as they do to the whole library.
        var view = ReferenceEquals(matched, visible)
            ? _folderFilteredLibrary
            : LibraryScanner.Build(matched);

        Artists.Clear();
        foreach (var artist in ArtistsByRecent ? SortRules.SortArtistsByRecent(view.Artists) : view.Artists)
            Artists.Add(artist);

        SelectedArtist = artistName is null
            ? Artists.FirstOrDefault()
            : Artists.FirstOrDefault(a => SortRules.NameComparer.Equals(a.Name, artistName))
              ?? Artists.FirstOrDefault();

        // Selecting the artist repopulates Albums, so the album match happens after.
        if (albumTitle is not null)
        {
            var album = Albums.FirstOrDefault(a => SortRules.NameComparer.Equals(a.Album.Title, albumTitle));
            if (album is not null)
                SelectedAlbum = album;
        }

        MatchedTrackCount = matched.Count;
        OnPropertyChanged(nameof(HasNoSearchResults));
        RefreshEmptyState();
    }

    /// <summary>The Songs panel's text while no album is selected.</summary>
    [ObservableProperty]
    private string _emptyStateTitle = "";

    [ObservableProperty]
    private string _emptyStateDetail = "";

    partial void OnIsScanningChanged(bool value) => RefreshEmptyState();

    private void RefreshEmptyState() =>
        (EmptyStateTitle, EmptyStateDetail) = EmptyStateText.Describe(
            hasAnyTracks: _library.AllTracks.Count > 0,
            hasTickedTracks: _folderFilteredLibrary.AllTracks.Count > 0,
            isNarrowed: IsSearching || LibraryFilter is not null,
            hasArtists: Artists.Count > 0,
            isScanning: IsScanning);


    public void NavigateToNowPlaying()
    {
        if (NowPlaying is not { } track)
            return;

        var artist = Artists.FirstOrDefault(a => SortRules.NameComparer.Equals(a.Name, track.GroupingArtist));
        if (artist is null)
            return;

        SelectedArtist = artist;

        var album = Albums.FirstOrDefault(a => SortRules.NameComparer.Equals(a.Album.Title, track.Album));
        if (album is not null)
            SelectedAlbum = album;
    }

    private string DescribeLibrary()
    {
        // Found but hidden is not the same as not found: say which, or the user goes
        // looking for a problem with their files rather than a tick in the menu.
        if (_folderFilteredLibrary.AllTracks.Count == 0 && _library.AllTracks.Count > 0)
            return $"All {_library.AllTracks.Count:N0} tracks are in unticked folders. " +
                   "Tick a folder under Libraries in the logo menu to show it.";

        if (_folderFilteredLibrary.AllTracks.Count == 0)
            return "No audio files found. Use Add folder to point at your music.";

        // Spec 6.8: the size is of the same tracks the counts describe, so it is
        // rebuilt with them, after every scan included.
        return LibrarySummary.Totals(
            _folderFilteredLibrary.Artists.Count,
            _folderFilteredLibrary.AlbumCount,
            _folderFilteredLibrary.AllTracks);
    }

    private string DescribeStatus(ScanSummary summary)
    {
        // A missing drive is the headline: the tracks are still listed but none of
        // them will play, and that needs saying before any counts.
        if (UnavailableFolders.Count > 0)
        {
            var where = UnavailableFolders.Count == 1
                ? UnavailableFolders[0]
                : $"{UnavailableFolders.Count} folders";

            return $"⚠ {where} is not available - reconnect the drive to play. " +
                   $"Showing {_folderFilteredLibrary.AllTracks.Count:N0} tracks from the last scan.";
        }

        if (_folderFilteredLibrary.AllTracks.Count == 0)
            return DescribeLibrary();

        if (LibraryFilter is { } filter)
        {
            if (IsSearching)
            {
                return $"{MatchedTrackCount:N0} of {_filteredTrackCount:N0} tracks match \"{SearchQuery}\"" +
                       $" · {filter.Description}";
            }

            // Reaching zero is the point of a "Missing ..." filter, so say so plainly.
            return _filteredTrackCount == 0
                ? $"{filter.Description}: no tracks left"
                : $"{_filteredTrackCount:N0} of {_folderFilteredLibrary.AllTracks.Count:N0} tracks · {filter.Description}";
        }

        // While searching, the counts that matter are the matches, not the library.
        if (IsSearching)
        {
            return MatchedTrackCount == 0
                ? $"No matches for \"{SearchQuery}\""
                : $"{MatchedTrackCount:N0} of {_folderFilteredLibrary.AllTracks.Count:N0} tracks match \"{SearchQuery}\"";
        }

        var changes = new List<string>();
        if (summary.Read > 0)
            changes.Add($"{summary.Read:N0} new or changed");
        if (summary.Removed > 0)
            changes.Add($"{summary.Removed:N0} removed");

        return changes.Count == 0
            ? DescribeLibrary()
            : $"{DescribeLibrary()} · {string.Join(", ", changes)}";
    }

    /// <summary>
    /// Reading tags from tens of thousands of files churns through a lot of
    /// short-lived buffers, and the app goes idle immediately afterwards. Handing
    /// that memory back stops a large library from parking on a half-gigabyte
    /// working set for the rest of the session.
    /// </summary>
    private static void ReclaimScanMemory() => _ = Task.Run(() =>
    {
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
    });

    [RelayCommand]
    private async Task AddFolderAsync()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Add a music folder",
            Multiselect = true,
        };

        if (dialog.ShowDialog() != true)
            return;

        var added = false;
        foreach (var picked in dialog.FolderNames)
        {
            var folder = MusicFolderList.Normalize(picked);
            if (MusicFolders.Any(f => MusicFolderList.SameFolder(f, folder)))
                continue;

            MusicFolders.Add(folder);

            var filterItem = new FolderFilterItem(folder, enabled: true);
            filterItem.PropertyChanged += OnFolderFilterItemChanged;
            FolderFilters.Add(filterItem);

            added = true;
        }

        if (!added)
            return;

        SaveFolderSettings();
        await ScanAsync();
    }

    /// <summary>
    /// Stops watching a folder and takes its tracks out of the library. Nothing on
    /// disk is touched; Add folder brings it back, at the cost of reading its tags
    /// again.
    /// </summary>
    [RelayCommand]
    private async Task RemoveFolderAsync(FolderFilterItem? item)
    {
        if (item is null)
            return;

        // A scan in flight would put the folder's tracks straight back.
        if (IsScanning)
        {
            StatusText = $"Wait for the scan to finish before removing {item.FolderPath}.";
            return;
        }

        item.PropertyChanged -= OnFolderFilterItemChanged;
        FolderFilters.Remove(item);

        var index = MusicFolders.ToList().FindIndex(f => MusicFolderList.SameFolder(f, item.FolderPath));
        if (index >= 0)
            MusicFolders.RemoveAt(index);

        SaveFolderSettings();

        var remaining = MusicFolderList.WithoutFolder(_library.AllTracks, item.FolderPath, MusicFolders);
        var removedCount = _library.AllTracks.Count - remaining.Count;
        UnavailableFolders = [.. UnavailableFolders.Where(f => !MusicFolderList.SameFolder(f, item.FolderPath))];

        ApplyLibrary(await Task.Run(() => LibraryScanner.Build(remaining)), keepSelection: true);
        await PersistLibraryAsync();

        StatusText = $"Removed {item.FolderPath} and its {removedCount:N0} tracks from the library. "
                   + "Nothing on disk was touched.";
    }

    // --------------------------------------------------------------- last.fm

    [RelayCommand]
    private void ShowLastFm()
    {
        if (Application.Current.MainWindow is not { } owner)
            return;

        new LastFmWindow(new LastFmViewModel(_scrobbler, _settings), owner).ShowDialog();
    }

    private void OnScrobblerAuthFailed(object? sender, string message) => StatusText = message;

    /// <summary>The status-bar indicator's state.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLastFmShown))]
    private ScrobblerState _lastFmState;

    /// <summary>
    /// Only when there is no working session (the user's call): not connected yet,
    /// or Last.fm rejected the session. Hidden while connected, including when
    /// scrobbling is off or a send failed, which sort themselves out or were chosen.
    /// </summary>
    public bool IsLastFmShown => LastFmState is ScrobblerState.Disconnected or ScrobblerState.NeedsReconnect;

    [ObservableProperty]
    private string _lastFmLabel = "Last.fm";

    [ObservableProperty]
    private string _lastFmToolTip = "";

    private void OnScrobblerStatusChanged(object? sender, EventArgs e) => RefreshLastFmIndicator();

    private void RefreshLastFmIndicator()
    {
        LastFmState = _scrobbler.State;
        (LastFmLabel, LastFmToolTip) = LastFmState switch
        {
            ScrobblerState.NeedsReconnect =>
                ("Last.fm: reconnect", "Last.fm stopped accepting scrobbles. Click to reconnect."),
            ScrobblerState.Off =>
                ("Last.fm: off", "Scrobbling is switched off. Click to turn it back on."),
            ScrobblerState.Failing =>
                ($"Last.fm: {_scrobbler.Pending:N0} waiting",
                 $"The last send to Last.fm failed: {_scrobbler.LastError.TrimEnd('.')}. "
                 + "The plays are kept and will be sent once it answers."),
            ScrobblerState.Disconnected =>
                ("Last.fm: not connected", "Not scrobbling. Click to connect to Last.fm."),
            _ => ("Last.fm", $"Scrobbling to Last.fm as {_scrobbler.UserName}."),
        };
    }

    // ------------------------------------------------------------ statistics

    /// <summary>
    /// Counts what the status bar counts: enabled folders only, and the whole of
    /// them regardless of any search in progress.
    /// </summary>
    [RelayCommand]
    private void ShowStatistics()
    {
        if (Application.Current.MainWindow is not { } owner)
            return;

        var stats = LibraryStatistics.Compute(_folderFilteredLibrary);
        var someHidden = FolderFilters.Any(f => !f.IsEnabled);

        var statsVm = new StatisticsViewModel(stats, someHidden);
        if (new StatisticsWindow(statsVm, owner).ShowDialog() == true && statsVm.Chosen is { } row)
            ApplyStatisticsChoice(row);
    }

    /// <summary>What clicking a Statistics row does: go to an artist, or filter to a subset.</summary>
    public void ApplyStatisticsChoice(BarRow row)
    {
        if (row.ArtistName is { } artist)
            ShowArtist(artist);
        else if (row.Filter is { } filter)
            LibraryFilter = filter;
    }

    /// <summary>
    /// "Saved tags for 12 track(s). · 58 left: Missing Year" - while a filter is
    /// on, a save reports how much of it remains, since fixing it is usually why
    /// the filter is on.
    /// </summary>
    private string WithFilterProgress(string message) =>
        LibraryFilter is { } filter
            ? $"{message} · {_filteredTrackCount:N0} left: {filter.Description}"
            : message;

    // ----------------------------------------------------------- tag editing

    /// <summary>
    /// The rows selected in the track grid, kept in step by the window - a
    /// DataGrid's SelectedItems cannot be bound. "Edit Tags..." edits all of them
    /// when the row it was opened on is one of several selected.
    /// </summary>
    public IReadOnlyList<Track> SelectedTracks { get; set; } = [];

    [RelayCommand]
    private void EditTrackTags(Track? track)
    {
        if (track is null || Application.Current.MainWindow is not { } owner)
            return;

        if (SelectedTracks.Count > 1 && SelectedTracks.Contains(track))
        {
            EditSelectedTracksTags(SelectedTracks.ToList(), owner);
            return;
        }

        var editVm = new TagEditViewModel(track);
        var window = new TagEditWindow(editVm, owner);

        if (window.ShowDialog() != true)
            return;

        _ = ApplyTrackEditAsync(track, editVm.BuildTrackEdit());
    }

    [RelayCommand]
    private void EditAlbumTags(AlbumItemViewModel? item)
    {
        if (item is null || Application.Current.MainWindow is not { } owner)
            return;

        var editVm = new TagEditViewModel(item.Album, _artService, new OnlineArtSearch(_settings.FanartTvApiKey));
        var window = new TagEditWindow(editVm, owner);

        if (window.ShowDialog() != true)
            return;

        _ = ApplyAlbumEditAsync(item.Album, editVm.BuildAlbumEdit(), editVm.PickedArtPayload());
    }

    private void EditSelectedTracksTags(IReadOnlyList<Track> tracks, Window owner)
    {
        var editVm = new TagEditViewModel(tracks);
        var window = new TagEditWindow(editVm, owner);

        if (window.ShowDialog() != true)
            return;

        _ = ApplySelectedTracksEditAsync(tracks, editVm.BuildTracksEdit());
    }

    /// <summary>
    /// As <see cref="ApplyAlbumEditAsync"/>, less the art: every successful write
    /// is kept, and the failures are named.
    /// </summary>
    private async Task ApplySelectedTracksEditAsync(IReadOnlyList<Track> tracks, TracksTagEdit edit)
    {
        StatusText = $"Saving tags for {tracks.Count} track(s)...";

        var (updated, failed) = await Task.Run(() =>
        {
            var okTracks = new List<Track>();
            var badTracks = new List<(string FileName, string Error)>();

            foreach (var track in tracks)
            {
                var writeResult = TagWriter.WriteSelectedTrackTags(track, edit, _engine.HoldsFile);
                if (writeResult.Success)
                    okTracks.Add(writeResult.UpdatedTrack!);
                else
                    badTracks.Add((Path.GetFileName(track.FilePath), writeResult.ErrorMessage ?? "unknown error"));
            }

            return (okTracks, badTracks);
        });

        if (updated.Count > 0)
        {
            ReplaceTracksInLibrary(updated.ToDictionary(t => t.FilePath, StringComparer.OrdinalIgnoreCase));
            await PersistLibraryAsync();
        }

        StatusText = failed.Count == 0
            ? WithFilterProgress($"Saved tags for {updated.Count} track(s).")
            : $"Saved tags for {updated.Count} of {tracks.Count} track(s) - " +
              $"{failed.Count} failed ({string.Join(", ", failed.Select(f => f.FileName))}: {failed[0].Error}).";
    }

    private async Task ApplyTrackEditAsync(Track track, TrackTagEdit edit)
    {
        StatusText = "Saving tags...";

        var result = await Task.Run(() =>
            TagWriter.WriteTrackTags(track, edit, art: null, folderArtPath: track.FolderArtPath, _engine.HoldsFile));

        if (!result.Success)
        {
            StatusText = $"Couldn't save tags for {Path.GetFileName(track.FilePath)}: {result.ErrorMessage}";
            return;
        }

        ReplaceTracksInLibrary(new Dictionary<string, Track>(StringComparer.OrdinalIgnoreCase)
        {
            [track.FilePath] = result.UpdatedTrack!,
        });

        StatusText = WithFilterProgress($"Saved tags for {result.UpdatedTrack!.DisplayTitle}.");
        await PersistLibraryAsync();
    }

    /// <summary>
    /// 9 of 11 tracks succeeding is not a failure - every successful write is
    /// folded into the library and persisted regardless of how many others failed,
    /// and the failures are named individually rather than reported as one opaque
    /// "batch failed" message.
    /// </summary>
    private async Task ApplyAlbumEditAsync(Album album, AlbumTagEdit edit, ArtPayload? art)
    {
        StatusText = $"Saving tags for {album.Tracks.Count} track(s)...";

        var folderArtByDirectory = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var folderArtFailed = false;

        if (art is not null)
        {
            foreach (var directory in AlbumDirectories(album))
            {
                var existing = ExistingCoverIn(album, directory);
                var folderResult = TagWriter.WriteFolderArt(directory, art, existing);
                folderArtByDirectory[directory] = folderResult.Success ? folderResult.FolderArtPath : existing;
                folderArtFailed |= !folderResult.Success;
            }
        }

        var (updated, failed) = await Task.Run(() =>
        {
            var okTracks = new List<Track>();
            var badTracks = new List<(string FileName, string Error)>();

            foreach (var track in album.Tracks)
            {
                var directory = Path.GetDirectoryName(track.FilePath) ?? "";
                var folderArtPath = art is not null && folderArtByDirectory.TryGetValue(directory, out var mapped)
                    ? mapped
                    : track.FolderArtPath;

                var writeResult = TagWriter.WriteAlbumTrackTags(track, edit, art, folderArtPath, _engine.HoldsFile);
                if (writeResult.Success)
                    okTracks.Add(writeResult.UpdatedTrack!);
                else
                    badTracks.Add((Path.GetFileName(track.FilePath), writeResult.ErrorMessage ?? "unknown error"));
            }

            return (okTracks, badTracks);
        });

        // Bust the art cache even on partial failure - the tracks that DID write
        // still need their stale thumbnail/header/Now-Playing entries dropped.
        if (art is not null)
            _artService.InvalidateAlbum(album);

        if (updated.Count > 0)
        {
            ReplaceTracksInLibrary(updated.ToDictionary(t => t.FilePath, StringComparer.OrdinalIgnoreCase));
            await PersistLibraryAsync();
        }

        StatusText = failed.Count == 0
            ? WithFilterProgress($"Saved tags for {updated.Count} track(s).")
            : $"Saved tags for {updated.Count} of {album.Tracks.Count} track(s) - " +
              $"{failed.Count} failed ({string.Join(", ", failed.Select(f => f.FileName))}: {failed[0].Error}).";

        if (folderArtFailed)
            StatusText += "  Folder cover file couldn't be updated.";
    }


    private void ReplaceTracksInLibrary(IReadOnlyDictionary<string, Track> updatedByPath)
    {
        var newAll = _library.AllTracks
            .Select(t => updatedByPath.TryGetValue(t.FilePath, out var updated) ? updated : t)
            .ToList();

        ApplyLibrary(LibraryScanner.Build(newAll), keepSelection: true);

        // The now-playing note and the window title compare by reference, so
        // they would lose the playing track once its old copy left the library.
        // The scrobbler holds its own copy, which would scrobble the old tags.
        if (NowPlaying is { } playing && updatedByPath.TryGetValue(playing.FilePath, out var renamed))
        {
            NowPlaying = renamed;
            _scrobbler.TrackRetagged(renamed);
        }
    }

    /// <summary>
    /// The library's current copy of a track, matched by path, or the track itself
    /// when the library no longer holds it (a rescan dropped it).
    /// </summary>
    private Track LibraryCopyOf(Track track) =>
        _library.AllTracks.FirstOrDefault(t =>
            string.Equals(t.FilePath, track.FilePath, StringComparison.OrdinalIgnoreCase)) ?? track;

    /// <summary>
    /// Saves one cell edited in place in the track grid. Unchanged text writes
    /// nothing; text the field cannot take is reported and not saved.
    /// </summary>
    public async Task ApplyInlineEditAsync(Track track, InlineField field, string text)
    {
        var (edit, error) = InlineTagEdit.Build(track, field, text);
        if (error is not null)
        {
            StatusText = $"Not saved: {error}";
            return;
        }

        if (edit is null)
            return;

        // One at a time: two quick edits to the same row would otherwise both
        // start from the copy the grid held, and the second would put the
        // first's field back in memory (the file itself would be right).
        await _inlineSave.WaitAsync();
        try
        {
            var current = LibraryCopyOf(track);

            StatusText = "Saving tags...";

            var result = await Task.Run(() => TagWriter.WriteSelectedTrackTags(current, edit, _engine.HoldsFile));

            if (!result.Success)
            {
                StatusText = $"Couldn't save tags for {Path.GetFileName(track.FilePath)}: {result.ErrorMessage}";
                return;
            }

            ReplaceTracksInLibrary(new Dictionary<string, Track>(StringComparer.OrdinalIgnoreCase)
            {
                [track.FilePath] = result.UpdatedTrack!,
            });

            StatusText = WithFilterProgress($"Saved tags for {result.UpdatedTrack!.DisplayTitle}.");
            await PersistLibraryAsync();
        }
        finally
        {
            _inlineSave.Release();
        }
    }

    private readonly SemaphoreSlim _inlineSave = new(1, 1);

    private async Task PersistLibraryAsync()
    {
        // A tag edit made while an older cache is still being re-read must not
        // mark it current; the re-read saves the new version when it finishes.
        _cache = LibraryCache.From(MusicFolders, _library.AllTracks, _cache?.Version ?? LibraryCache.CurrentVersion);
        await Task.Run(_cache.Save, CancellationToken.None);
    }

    private static IEnumerable<string> AlbumDirectories(Album album) =>
        album.Tracks
            .Select(t => Path.GetDirectoryName(t.FilePath))
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The cover file already sitting in one of an album's directories - not just
    /// <see cref="Album.FolderArtPath"/>, which is only the first one found across
    /// the whole album and may belong to a different directory than this one for a
    /// multi-folder album (a multi-disc set kept as "Disc 1/", "Disc 2/" and the like).
    /// </summary>
    private static string? ExistingCoverIn(Album album, string directory)
    {
        if (album.FolderArtPath is not null &&
            string.Equals(Path.GetDirectoryName(album.FolderArtPath), directory, StringComparison.OrdinalIgnoreCase))
            return album.FolderArtPath;

        var trackInDirectory = album.Tracks.FirstOrDefault(t =>
            string.Equals(Path.GetDirectoryName(t.FilePath), directory, StringComparison.OrdinalIgnoreCase));

        return trackInDirectory is null ? null : TagReader.FindFolderArt(trackInDirectory.FilePath);
    }

    // -------------------------------------------------------------- playback

    /// <summary>
    /// Plays a track with the rest of its album queued behind it - which is what
    /// makes gapless matter, since album transitions are where the gaps show.
    /// </summary>
    [RelayCommand]
    private void PlayTrack(Track? track)
    {
        if (track is null)
            return;

        var queue = Tracks.ToList();
        var index = queue.IndexOf(track);
        if (index < 0)
        {
            queue = [track];
            index = 0;
        }

        if (!_engine.Play(queue, index))
            StatusText = $"Couldn't play {Path.GetFileName(track.FilePath)}. The format may need an add-on that isn't installed.";
    }

    [RelayCommand]
    private void PlayAlbum(AlbumItemViewModel? album)
    {
        if (album is null)
            return;

        SelectedAlbum = album;   // populates Tracks synchronously
        PlayTrack(Tracks.FirstOrDefault());
    }

    [RelayCommand]
    private void PlayArtist(ArtistGroup? artist)
    {
        if (artist is null)
            return;

        SelectedArtist = artist;   // populates Albums, which sets SelectedAlbum to the first
        PlayTrack(Tracks.FirstOrDefault());
    }

    [RelayCommand]
    private void TogglePlay()
    {
        if (_engine.State == PlaybackState.Stopped)
        {
            PlayTrack(Tracks.FirstOrDefault());
            return;
        }

        _engine.TogglePause();
    }

    [RelayCommand]
    private void Next() => _engine.Next();

    [RelayCommand]
    private void Previous() => _engine.Previous();

    /// <summary>
    /// Shuffle plays the queue in a random order. Turning it on leaves the current
    /// track playing and shuffles what follows; turning it off puts the queue back
    /// in album order. The engine owns that logic - this just persists the choice.
    /// </summary>
    public bool IsShuffle
    {
        get => _settings.Shuffle;
        set
        {
            if (_settings.Shuffle == value)
                return;

            _settings.Shuffle = value;
            _settings.Save();
            _engine.Shuffle = value;

            OnPropertyChanged();
            OnPropertyChanged(nameof(ShuffleTooltip));
        }
    }

    public string ShuffleTooltip => IsShuffle ? "Shuffle: on" : "Shuffle: off";

    /// <summary>
    /// Artists by most recently added first instead of A-Z. Clicking the pane header flips
    /// it and starts again at the top of the new order, as startup does. Keeping the
    /// selection would scroll to wherever the old artist landed, which is usually
    /// just the first A-Z artist the app opened on. For this session only: the
    /// app always opens A-Z, at the user's request.
    /// </summary>
    public bool ArtistsByRecent
    {
        get => _artistsByRecent;
        set
        {
            if (_artistsByRecent == value)
                return;

            _artistsByRecent = value;

            OnPropertyChanged();
            OnPropertyChanged(nameof(ArtistSortTooltip));
            ApplyToView(keepSelection: false);
        }
    }

    private bool _artistsByRecent;

    public string ArtistSortTooltip => ArtistsByRecent
        ? "Most recently added first. Click to sort A-Z."
        : "A-Z. Click to show the most recently added first.";

    [RelayCommand]
    private void ToggleArtistSort() => ArtistsByRecent = !ArtistsByRecent;

    /// <summary>Where the window was when it last closed, if it has been closed before.</summary>
    public WindowBounds? SavedWindowBounds => _settings.Window;

    public void SaveWindowBounds(WindowBounds bounds)
    {
        _settings.Window = bounds;
        _settings.Save();
    }

    /// <summary>
    /// Off, All or One. A three-way cycle rather than a checkbox, which is what
    /// the single button in the transport row can express.
    /// </summary>
    public RepeatMode Repeat
    {
        get => _settings.Repeat;
        private set
        {
            if (_settings.Repeat == value)
                return;

            _settings.Repeat = value;
            _settings.Save();
            _engine.Repeat = value;

            OnPropertyChanged();
            OnPropertyChanged(nameof(IsRepeatAll));
            OnPropertyChanged(nameof(IsRepeatOne));
            OnPropertyChanged(nameof(RepeatTooltip));
        }
    }

    /// <summary>
    /// These two drive the glyph swap in the transport button. Off needs no flag
    /// of its own: it is the button's default state.
    /// </summary>
    public bool IsRepeatAll => Repeat == RepeatMode.All;

    public bool IsRepeatOne => Repeat == RepeatMode.One;

    public string RepeatTooltip => Repeat switch
    {
        RepeatMode.All => "Repeat: whole queue",
        RepeatMode.One => "Repeat: this track",
        _ => "Repeat: off",
    };

    [RelayCommand]
    private void ToggleShuffle() => IsShuffle = !IsShuffle;

    [RelayCommand]
    private void CycleRepeat() => Repeat = Repeat switch
    {
        RepeatMode.Off => RepeatMode.All,
        RepeatMode.All => RepeatMode.One,
        _ => RepeatMode.Off,
    };

    [RelayCommand]
    private void Stop() => _engine.Stop();

    /// <summary>Called when the user lets go of the seek bar.</summary>
    public void CommitSeek()
    {
        if (_pendingSeek is not { } seconds)
            return;

        _pendingSeek = null;
        _engine.Seek(TimeSpan.FromSeconds(seconds));
    }

    // ---------------------------------------------------------- engine events

    private void OnEngineTrackChanged(object? sender, Track engineTrack)
    {
        // The engine plays the Track objects its queue was built from. A tag save
        // since then replaced them in the library, so the engine's copy of a
        // queued track can carry the old title and artist - and, being a
        // different object, would lose the grid's now-playing note too.
        var track = LibraryCopyOf(engineTrack);

        NowPlaying = track;

        DurationSeconds = track.Duration.TotalSeconds > 0
            ? track.Duration.TotalSeconds
            : _engine.Duration.TotalSeconds;
        DurationDisplay = Display.Time(TimeSpan.FromSeconds(DurationSeconds));

        // The output chain is opened lazily on the first Play, and reopened
        // whenever an exclusive-mode track arrives at a new sample rate - so the
        // description is only accurate once a track is actually running.
        RefreshOutputState();

        _scrobbler.TrackStarted(track, TimeSpan.FromSeconds(DurationSeconds));

        _ = LoadNowPlayingArtAsync(track);
    }

    private async Task LoadNowPlayingArtAsync(Track track)
    {
        var art = await _artService.GetTrackArtAsync(track, NowPlayingArtWidth);

        if (ReferenceEquals(NowPlaying, track))
            NowPlayingArt = art;
    }

    private void OnEngineStateChanged(object? sender, PlaybackState state)
    {
        IsPlaying = state == PlaybackState.Playing;
        IsOutputActive = ShouldShowOutput();

        if (state == PlaybackState.Playing)
            _positionTimer.Start();
        else
            _positionTimer.Stop();

        if (state == PlaybackState.Stopped)
        {
            _scrobbler.Stopped();

            _updatingPositionFromTimer = true;
            PositionSeconds = 0;
            _updatingPositionFromTimer = false;
        }
    }

    private void OnEnginePlaybackFinished(object? sender, EventArgs e)
    {
        NowPlaying = null;
        NowPlayingArt = null;
    }

    private void OnPositionTick(object? sender, EventArgs e)
    {
        // Before the seeking check: the tracker wants the position the audio is
        // really at, whatever the slider is doing.
        _scrobbler.Advance(_engine.Position, _engine.CurrentTrack);

        if (IsSeeking)
            return;

        var duration = _engine.Duration.TotalSeconds;
        if (duration > 0 && Math.Abs(duration - DurationSeconds) > 0.5)
        {
            DurationSeconds = duration;
            DurationDisplay = Display.Time(TimeSpan.FromSeconds(duration));
        }

        _updatingPositionFromTimer = true;
        PositionSeconds = _engine.Position.TotalSeconds;
        _updatingPositionFromTimer = false;
    }

    public void Dispose()
    {
        _positionTimer.Stop();
        _positionTimer.Tick -= OnPositionTick;

        _searchDebounce.Stop();
        _searchDebounce.Tick -= OnSearchDebounceTick;

        _engine.TrackChanged -= OnEngineTrackChanged;
        _engine.StateChanged -= OnEngineStateChanged;
        _engine.PlaybackFinished -= OnEnginePlaybackFinished;

        _scrobbler.AuthFailed -= OnScrobblerAuthFailed;
        _scrobbler.StatusChanged -= OnScrobblerStatusChanged;

        _scanCts?.Cancel();
        _scanCts?.Dispose();

        SaveVolumeOnly();
    }

    /// <summary>
    /// Persists just the volume, which is the only setting not written the moment
    /// it changes.
    /// <para>
    /// Deliberately re-reads from disk first and copies one value across, rather
    /// than saving this instance's whole settings object. Writing the whole thing
    /// meant a long-running window would overwrite the music folders with the list
    /// it happened to load at startup - clobbering any change made since, whether
    /// by a second instance or from outside the app.
    /// </para>
    /// </summary>
    private void SaveVolumeOnly()
    {
        var onDisk = AppSettings.Load();
        if (Math.Abs(onDisk.Volume - _settings.Volume) < 0.0001)
            return;

        onDisk.Volume = _settings.Volume;
        onDisk.Save();
    }
}
