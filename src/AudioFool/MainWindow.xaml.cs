using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AudioFool.Core.Library;
using AudioFool.Services;
using AudioFool.ViewModels;
using Wpf.Ui.Controls;

// System.Windows.Controls.Primitives (needed for the slider's drag events) also
// defines a Track, which is the Slider's rail rather than a song.
using AudioFool.Core.Models;
using Track = AudioFool.Core.Models.Track;

namespace AudioFool;

public partial class MainWindow : FluentWindow
{
    private readonly MainViewModel _viewModel;
    private readonly GlobalHotkeys _hotkeys = new();
    private readonly TaskbarControls _taskbarControls;
    private ArtWindow? _artWindow;
    private string _typeAheadBuffer = "";
    private ListBox? _typeAheadTarget;
    private readonly DispatcherTimer _typeAheadTimer;

    public MainWindow(MainViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;

        InitializeComponent();

        ApplySongTableLayout();
        ApplyMinimumWidth();
        FitToWorkArea();

        // The slider marks a click on its track handled, after jumping the value.
        SeekBar.AddHandler(PreviewMouseLeftButtonDownEvent,
            new MouseButtonEventHandler(SeekBar_PreviewMouseLeftButtonDown), handledEventsToo: true);
        // Likewise its arrow, Page and Home/End keys, which are commands.
        SeekBar.AddHandler(KeyDownEvent, new KeyEventHandler(SeekBar_KeyDown), handledEventsToo: true);

        _taskbarControls = new TaskbarControls(this, viewModel);

        Loaded += OnLoaded;
        SourceInitialized += OnSourceInitialized;
        Closing += OnClosing;
        Closed += OnClosed;

        _typeAheadTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
        _typeAheadTimer.Tick += (_, _) =>
        {
            _typeAheadTimer.Stop();
            _typeAheadBuffer = "";
            _typeAheadTarget = null;
        };
        PreviewTextInput += OnTypeAheadInput;
        PreviewKeyDown += OnTypeAheadKeyDown;

        _slowClickTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(GetDoubleClickTime()) };
        _slowClickTimer.Tick += SlowClickTimer_Tick;
        TrackGrid.PreviewMouseWheel += (_, _) => _slowClickTimer.Stop();
        TrackGrid.LostKeyboardFocus += TrackGrid_LostKeyboardFocus;
        HookReorder();
        _viewModel.TracksChanging += OnTracksChanging;
        _viewModel.RevealTrackRequested += OnRevealTrackRequested;

        // A playlist opens in its own order, and an album isn't left sorted as
        // the playlist was.
        _viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.IsPlaylistMode))
                ClearSort();
        };
    }

    /// <summary>
    /// Selects the song the app opened on and scrolls it into view. Deferred until
    /// the grid has bound the album's rows, which happens after the selection that
    /// raised this.
    /// </summary>
    private void OnRevealTrackRequested(object? sender, Track track)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (!TrackGrid.Items.Contains(track))
                return;

            TrackGrid.SelectedItem = track;
            TrackGrid.ScrollIntoView(track);
        }, DispatcherPriority.Background);
    }

    /// <summary>
    /// The ten track columns want a wide window, but the declared size can be
    /// larger than the display - especially over remote desktop, where the
    /// resolution can change while the app is running. Shrink to fit and centre.
    /// </summary>
    private void FitToWorkArea()
    {
        const double margin = 40;
        var work = SystemParameters.WorkArea;

        Width = Math.Min(Width, work.Width - margin);
        Height = Math.Min(Height, work.Height - margin);

        Left = work.Left + ((work.Width - Width) / 2);
        Top = work.Top + ((work.Height - Height) / 2);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;

        ThemeCloseButton();
        FitLogoMenuToSlot();
        ArtistsHeaderLabel.SizeChanged += (_, _) => AlignTitleBar();
        ArtistsPanel.SizeChanged += (_, _) => AlignTitleBar();
        AlbumsPanel.SizeChanged += (_, _) => AlignTitleBar();
        ArtistsPanel.SizeChanged += (_, _) => AlignPlaybackBar();
        AlbumsPanel.SizeChanged += (_, _) => AlignPlaybackBar();
        PlaybackBar.SizeChanged += (_, _) => AlignPlaybackBar();
        UpdateLayout();
        AlignTitleBar();
        AlignPlaybackBar();

        await _viewModel.InitialiseAsync();
    }

    /// <summary>
    /// Claims the global playback keys. Done at SourceInitialized because the window
    /// needs a real HWND before Windows will register a hotkey against it.
    /// </summary>
    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        SourceInitialized -= OnSourceInitialized;

        // Back where it was last closed, before it is first shown. When that is
        // no longer on a screen, it keeps the centred size FitToWorkArea gave it.
        if (_viewModel.SavedWindowBounds is { } bounds)
            WindowPlacement.Apply(this, bounds);

        if (!_viewModel.GlobalHotkeysEnabled)
            return;

        _hotkeys.Pressed += OnHotkeyPressed;
        _hotkeys.Register(this);
        _viewModel.ReportHotkeys(_hotkeys.Unavailable);
    }

    private void OnHotkeyPressed(object? sender, GlobalHotkeys.Action action)
    {
        switch (action)
        {
            case GlobalHotkeys.Action.PlayPause:
                _viewModel.TogglePlayCommand.Execute(null);
                break;

            // Previous already means "restart if past three seconds in, otherwise
            // step back a track" - see AudioEngine.Previous.
            case GlobalHotkeys.Action.Previous:
                _viewModel.PreviousCommand.Execute(null);
                break;

            case GlobalHotkeys.Action.Next:
                _viewModel.NextCommand.Execute(null);
                break;
        }
    }

    /// <summary>Remembers where the window is, while it still has a handle to ask.</summary>
    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (e.Cancel)
            return;

        Closing -= OnClosing;
        if (WindowPlacement.Capture(this) is { } bounds)
            _viewModel.SaveWindowBounds(bounds);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        Closed -= OnClosed;
        _hotkeys.Pressed -= OnHotkeyPressed;
        _hotkeys.Dispose();
        _taskbarControls.Dispose();
        _slowClickTimer.Stop();
        _viewModel.TracksChanging -= OnTracksChanging;
        _viewModel.RevealTrackRequested -= OnRevealTrackRequested;
    }

    // ------------------------------------------------------ layout calculations

    /// <summary>
    /// Nothing scrolls sideways (the user's call, 2026-10-01), so the window may
    /// not get narrower than the panels plus a song table whose columns all fit:
    /// the fixed columns at their spec widths, the two flexible ones at
    /// <see cref="FlexFloor"/>, the gaps between columns, and the table's and
    /// panel's padding and borders. All from the theme tokens, so a column
    /// width changed there moves the minimum with it.
    /// </summary>
    private void ApplyMinimumWidth()
    {
        var n = Theming.TokenResources.Current!.Numbers;

        const string prefix = "songTable.columns.";
        var columnIds = n.Keys
            .Where(k => k.StartsWith(prefix, StringComparison.Ordinal))
            .Select(k => k[prefix.Length..k.LastIndexOf('.')])
            .Distinct()
            .ToList();
        var columns = columnIds.Sum(id => n.TryGetValue($"{prefix}{id}.width", out var width) ? width : FlexFloor);

        // Room for a three-digit track number too (the user's call), so an album
        // of 100+ tracks does not squeeze Song and Album below the floor.
        columns += n["songTable.numberCell.numberWidth3Digits"] - n["songTable.numberCell.numberWidth"];

        var songs = 2 * n["layout.panelBorder"]
            + 2 * n["songTable.listPaddingX"]
            + 2 * n["songTable.rowPaddingX"]
            + columns
            + (columnIds.Count - 1) * n["songTable.columnGap"];

        // The playback bar from Previous rightwards sits under the Songs panel,
        // starting on its left edge (spec 2, rule 3), so the panel must also hold
        // Previous, Play/Pause, Next and the Repeat/Shuffle stack, a seek zone
        // whose track keeps playbackBar.seekMinTrackWidth, the volume zone and
        // the gaps between them (the user's call).
        var transport = 2 * n["playbackBar.buttonSize"] + n["playbackBar.playButtonSize"]
            + n["playbackBar.modeButtonSize"] + 3 * n["playbackBar.buttonGap"];
        var seek = 2 * n["playbackBar.timeLabelWidth"] + 2 * n["playbackBar.seekGap"]
            + n["playbackBar.seekMinTrackWidth"];
        var underSongs = transport
            + n["playbackBar.zoneGap"] + seek + n["playbackBar.zoneGap"] + n["playbackBar.volumeZoneWidth"]
            + n["playbackBar.paddingX"] + n["layout.panelBorder"];
        songs = Math.Max(songs, underSongs);

        SongsColumn.MinWidth = songs;
        MinWidth = 2 * n["layout.windowMarginX"]
            + n["layout.artistsPanelOuterWidth"] + n["layout.panelGap"]
            + n["layout.albumsPanelOuterWidth"] + n["layout.panelGap"]
            + songs;
    }

    /// <summary>
    /// Spec 2, rules 1 and 2: the logo slot is centred over the ARTISTS label,
    /// and the search box starts the token gap after it and ends exactly at the
    /// Albums panel's right outer edge. Measured from the real layout, so it
    /// follows the panels when a splitter moves.
    /// </summary>
    private void AlignTitleBar()
    {
        if (!IsLoaded || ArtistsHeaderLabel.ActualWidth == 0)
            return;

        var n = Theming.TokenResources.Current!.Numbers;

        var labelCentre = ArtistsHeaderLabel.TranslatePoint(new Point(ArtistsHeaderLabel.ActualWidth / 2, 0), this).X;
        var albumsRight = AlbumsPanel.TranslatePoint(new Point(AlbumsPanel.ActualWidth, 0), this).X;
        var headerOrigin = TitleBarHeader.TranslatePoint(new Point(0, 0), this).X - TitleBarHeader.Margin.Left;

        // Whole pixels (spec 8), so the search box's right edge lands exactly on
        // the panel's rather than a rounding step short of it.
        var logoLeft = Math.Round(labelCentre - (LogoSlot.Width / 2));
        albumsRight = Math.Round(albumsRight);
        var searchLeft = logoLeft + LogoSlot.Width + n["titleBar.logoToSearchGap"];

        TitleBarHeader.Margin = new Thickness(Math.Max(0, logoLeft - headerOrigin), 0, 0, 0);
        SearchBox.Width = Math.Max(0, albumsRight - searchLeft);
    }

    /// <summary>
    /// Spec 2, rule 3: the Previous button's left edge lines up with the Songs
    /// panel's outer left edge (the user's call, 2026-10-02; before that Shuffle
    /// lined up with the song list, then with the divider). The Songs panel starts
    /// one panel gap after the Albums panel, so the now-playing zone takes
    /// whatever is left of that point after the zone gap. Measured from the real
    /// layout, so it follows the splitters.
    /// </summary>
    private void AlignPlaybackBar()
    {
        if (!IsLoaded || PlaybackZones.ActualWidth == 0)
            return;

        var n = Theming.TokenResources.Current!.Numbers;

        var albumsRight = AlbumsPanel.TranslatePoint(new Point(AlbumsPanel.ActualWidth, 0), this).X;
        var songsLeft = albumsRight + n["layout.panelGap"];
        var zonesLeft = PlaybackZones.TranslatePoint(new Point(0, 0), this).X;

        // Whole pixels (spec 8).
        var width = Math.Round(songsLeft) - Math.Round(zonesLeft) - n["playbackBar.zoneGap"];
        NowPlayingZone.Width = new GridLength(Math.Max(0, width));
    }

    /// <summary>
    /// Sizes the logo menu's spacer so the whole item - WPF-UI's own padding
    /// either side of it included - is exactly the logo slot's width, and its
    /// hover highlight sits on the slot rather than spilling toward the search box.
    /// </summary>
    private void FitLogoMenuToSlot()
    {
        var chrome = LogoMenuItem.ActualWidth - LogoMenuSpacer.ActualWidth;
        LogoMenuSpacer.Width = Math.Max(0, LogoSlot.Width - chrome);
    }

    /// <summary>
    /// WPF-UI's title-bar template gives the close button a literal white icon on
    /// hover; point it at the token instead.
    /// </summary>
    private void ThemeCloseButton()
    {
        if (AppTitleBar.Template?.FindName("PART_CloseButton", AppTitleBar) is TitleBarButton close)
            close.SetResourceReference(TitleBarButton.MouseOverButtonsForegroundProperty, "color.window.closeHoverIcon");
    }

    /// <summary>
    /// Scrolls a programmatic selection into view.
    /// <para>
    /// Setting <c>SelectedItem</c> highlights a row but does not move the scroll
    /// position, so re-selecting an artist after the search box is cleared would
    /// leave it selected somewhere off-screen among hundreds of others - which looks
    /// exactly like the selection having been lost.
    /// </para>
    /// </summary>
    private void BrowserList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Artists and Albums take several rows (Ctrl/Shift-click) for a tag edit;
        // the view model sees them as DataGrid rows are seen, since SelectedItems
        // cannot be bound.
        if (sender == ArtistList)
            _viewModel.SelectedArtists = ArtistList.SelectedItems.OfType<ArtistGroup>().ToList();
        else if (sender == AlbumList)
            _viewModel.SelectedAlbums = AlbumList.SelectedItems.OfType<AlbumItemViewModel>().ToList();

        if (sender is not ListBox list || list.SelectedItem is null)
            return;

        // A Ctrl- or Shift-click adds rows but leaves SelectedItem (the first) as
        // it was, so there is nothing to bring into view, and scrolling to it
        // would pull the list away from the row just clicked.
        if (!e.AddedItems.Contains(list.SelectedItem))
            return;

        // Deferred: during a rebuild the item's container may not exist yet, and
        // ScrollIntoView silently does nothing without one.
        var target = list.SelectedItem;
        list.Dispatcher.BeginInvoke(
            () =>
            {
                if (!ReferenceEquals(list.SelectedItem, target))
                    return;

                // Scrolling to the end first, then back to the target, lands it near
                // the top of the viewport with the rest of the list below it. Going
                // straight there scrolls the minimum distance instead, leaving the
                // row flush against whichever edge it entered from.
                if (list.Items.Count > 0)
                    list.ScrollIntoView(list.Items[^1]);

                list.ScrollIntoView(target);
            },
            DispatcherPriority.Background);
    }

    private ListBox? GetTypeAheadTarget()
    {
        if (ArtistList.IsMouseOver) return ArtistList;
        if (AlbumList.IsMouseOver) return AlbumList;
        if (PlaylistList.IsMouseOver) return PlaylistList;
        return null;
    }

    private void SelectTypeAheadMatch()
    {
        object? match = null;
        if (_typeAheadTarget == ArtistList)
            match = _viewModel.Artists.FirstOrDefault(a =>
                a.SortKey.StartsWith(_typeAheadBuffer, StringComparison.OrdinalIgnoreCase));
        else if (_typeAheadTarget == AlbumList)
            match = _viewModel.Albums.FirstOrDefault(a =>
                a.Title.StartsWith(_typeAheadBuffer, StringComparison.OrdinalIgnoreCase));
        else if (_typeAheadTarget == PlaylistList)
            match = _viewModel.Playlists.FirstOrDefault(p =>
                p.Name.StartsWith(_typeAheadBuffer, StringComparison.OrdinalIgnoreCase));

        if (_typeAheadTarget is not { } list || match is null)
            return;

        list.SelectedItem = match;
        FocusSelectedRow(list);
    }

    /// <summary>
    /// Gives the list's selected row the keyboard, so the arrow keys carry on
    /// from a type-ahead match: "King" lands on King Gizzard, Down goes on to
    /// Kingdom Hearts. Queued behind the selection's own scroll
    /// (<see cref="BrowserList_SelectionChanged"/>), since a row scrolled out of
    /// a virtualised list has no container to focus until it is in view.
    /// </summary>
    private void FocusSelectedRow(ListBox list)
    {
        var target = list.SelectedItem;
        list.Dispatcher.BeginInvoke(
            () =>
            {
                if (!ReferenceEquals(list.SelectedItem, target))
                    return;

                if (list.ItemContainerGenerator.ContainerFromItem(target) is not ListBoxItem)
                {
                    list.ScrollIntoView(target);
                    list.UpdateLayout();
                }

                (list.ItemContainerGenerator.ContainerFromItem(target) as ListBoxItem)?.Focus();
            },
            DispatcherPriority.ContextIdle);
    }

    private void OnTypeAheadInput(object sender, TextCompositionEventArgs e)
    {
        var target = GetTypeAheadTarget();
        if (target is null || Keyboard.FocusedElement is TextBoxBase)
            return;

        if (_typeAheadTarget != target)
        {
            _typeAheadBuffer = "";
            _typeAheadTarget = target;
        }

        _typeAheadBuffer += e.Text;
        _typeAheadTimer.Stop();
        _typeAheadTimer.Start();
        SelectTypeAheadMatch();
        e.Handled = true;
    }

    private void OnTypeAheadKeyDown(object sender, KeyEventArgs e)
    {
        var target = GetTypeAheadTarget();
        if (target is null || Keyboard.FocusedElement is TextBoxBase)
            return;

        if (e.Key == Key.Back && _typeAheadBuffer.Length > 0 && _typeAheadTarget == target)
        {
            _typeAheadBuffer = _typeAheadBuffer[..^1];
            _typeAheadTimer.Stop();

            if (_typeAheadBuffer.Length > 0)
            {
                _typeAheadTimer.Start();
                SelectTypeAheadMatch();
            }
            else
            {
                _typeAheadTarget = null;
            }

            e.Handled = true;
        }
        else if (e.Key == Key.Escape && _typeAheadBuffer.Length > 0)
        {
            _typeAheadBuffer = "";
            _typeAheadTarget = null;
            _typeAheadTimer.Stop();
            e.Handled = true;
        }
    }

    // Border has no MouseDoubleClick of its own - that's a Control member - so the
    // click count gets checked by hand.

    private void AlbumArt_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        _ = ShowArtAsync(_viewModel.GetSelectedAlbumFullArtAsync(), _viewModel.SelectedAlbumCaption);
    }

    /// <summary>
    /// A right-click on the album header's art opens Edit Album Tags for the album
    /// straight away, with no menu (the user's call). On release, as a context menu
    /// opens, so the new window isn't opened under a press still going to the main
    /// window.
    /// </summary>
    private void AlbumArt_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (sender is UIElement { IsMouseOver: true } && _viewModel.SelectedAlbum is { } album)
            _viewModel.EditAlbumTagsCommand.Execute(album);
    }

    private void PlaylistArt_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        _ = ShowArtAsync(_viewModel.GetSelectedPlaylistFullArtAsync(), _viewModel.SelectedPlaylistCaption);
    }

    private void NowPlayingArt_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        _ = ShowArtAsync(_viewModel.GetNowPlayingFullArtAsync(), _viewModel.NowPlayingCaption);
    }

    private void NowPlayingInfo_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        _viewModel.NavigateToNowPlaying();
    }

    /// <summary>
    /// Opens the enlarged view once the full-size decode finishes. Does nothing when
    /// there's no cover - an empty window is worse than no window.
    /// </summary>
    private async Task ShowArtAsync(Task<BitmapSource?> load, string caption)
    {
        var art = await load;
        if (art is null)
            return;

        // Reuse one viewer rather than stacking windows on repeated double-clicks.
        _artWindow?.Close();

        _artWindow = new ArtWindow(art, caption, this);
        _artWindow.Closed += (s, _) =>
        {
            if (ReferenceEquals(_artWindow, s))
                _artWindow = null;
        };

        _artWindow.Show();
    }

    private void TrackGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // Only a double-click on an actual row should start playback - not one on
        // a column header or the empty space below the rows.
        if (e.OriginalSource is not DependencyObject source)
            return;

        // A double-click on a heart is two likes, not a play.
        if (FindAncestor<LikeToggle>(source) is not null)
            return;

        if (ItemsControl.ContainerFromElement(TrackGrid, source) is DataGridRow { Item: Track track })
            _viewModel.PlayTrackCommand.Execute(track);
    }

    /// <summary>
    /// In a playlist, # is the song's place, which is the playlist's own order:
    /// clicking it puts that order back rather than sorting by track number.
    /// </summary>
    private void TrackGrid_Sorting(object sender, DataGridSortingEventArgs e)
    {
        if (!_viewModel.IsPlaylistMode || e.Column != TrackNumberColumn)
            return;

        e.Handled = true;
        ClearSort();
    }

    private void ClearSort()
    {
        foreach (var column in TrackGrid.Columns)
            column.SortDirection = null;
        TrackGrid.Items.SortDescriptions.Clear();
    }

    private void PlaylistList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source)
            return;

        if (ItemsControl.ContainerFromElement(PlaylistList, source) is ListBoxItem { DataContext: PlaylistItemViewModel playlist })
            _viewModel.PlayPlaylistCommand.Execute(playlist);
    }

    /// <summary>
    /// Selects the row under the cursor before its context menu opens, since a
    /// DataGridRow (unlike a ListBoxItem) doesn't select itself on right-click.
    /// A row already in a multiple selection keeps the selection, so the menu
    /// edits all of it; any other row replaces the selection, as in Explorer.
    /// </summary>
    private void TrackGridRow_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not DataGridRow row || row.IsSelected)
            return;

        TrackGrid.SelectedItems.Clear();
        row.IsSelected = true;
    }

    private void TrackGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _slowClickTimer.Stop();
        _viewModel.SelectedTracks = TrackGrid.SelectedItems.OfType<Track>().ToList();
    }

    private void AlbumList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source)
            return;

        if (ItemsControl.ContainerFromElement(AlbumList, source) is ListBoxItem { DataContext: AlbumItemViewModel album })
            _viewModel.PlayAlbumCommand.Execute(album);
    }

    /// <summary>
    /// Shows an album row's title tooltip only when the title is trimmed. The width
    /// is measured on hover rather than tracked, so resizing the pane needs nothing.
    /// </summary>
    private void AlbumRow_ToolTipOpening(object sender, ToolTipEventArgs e)
    {
        if (sender is FrameworkElement { Tag: System.Windows.Controls.TextBlock title } && !IsTrimmed(title))
            e.Handled = true;
    }

    /// <summary>
    /// Whether a title was cut short. Album titles wrap and are clamped to two
    /// lines (type.albumTitle), so the test is whether the whole title, wrapped
    /// at the block's width, would need more height than the block was given;
    /// an unwrapped title is cut short when it is wider than the block.
    /// </summary>
    internal static bool IsTrimmed(System.Windows.Controls.TextBlock text)
    {
        var formatted = new FormattedText(
            text.Text,
            System.Globalization.CultureInfo.CurrentUICulture,
            text.FlowDirection,
            new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch),
            text.FontSize,
            Brushes.Black,
            VisualTreeHelper.GetDpi(text).PixelsPerDip);

        // Half a pixel of slack for layout rounding.
        if (text.TextWrapping == TextWrapping.NoWrap)
            return formatted.WidthIncludingTrailingWhitespace > text.ActualWidth + 0.5;

        formatted.MaxTextWidth = Math.Max(1, text.ActualWidth);
        if (!double.IsNaN(text.LineHeight))
            formatted.LineHeight = text.LineHeight;
        return formatted.Height > text.ActualHeight + 0.5;
    }

    private void ArtistList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source)
            return;

        if (ItemsControl.ContainerFromElement(ArtistList, source) is ListBoxItem { DataContext: ArtistGroup artist })
            _viewModel.PlayArtistCommand.Execute(artist);
    }

    // ------------------------------------------------------ song table layout

    /// <summary>
    /// A squeezed flexible column keeps at least this much, so it stays a column
    /// rather than disappearing to a sliver.
    /// </summary>
    private const double FlexFloor = 70;

    /// <summary>Whether the # column is currently wide enough for three digits.</summary>
    private bool _wideTrackNumbers;
    private bool _trackNumberCheckQueued;

    private DataGridColumn ColumnFor(string id) => id switch
    {
        "number" => TrackNumberColumn,
        "song" => SongColumn,
        "artist" => ArtistColumn,
        "album" => AlbumColumn,
        "time" => TimeColumn,
        "disc" => DiscColumn,
        "kind" => KindColumn,
        "bitrate" => BitrateColumn,
        "bitDepth" => BitDepthColumn,
        "sampleRate" => SampleRateColumn,
        "like" => LikeColumn,
        _ => throw new InvalidOperationException($"songTable.columns has a column \"{id}\" the track grid does not."),
    };

    /// <summary>
    /// Spec 6.6: each column's width, alignment and text style from
    /// songTable.columns, and the paddings the 12 px gap is split into (see
    /// theme.songTable in Chrome.xaml). These are the starting widths: columns
    /// can still be resized and reordered by dragging, and nothing refits them.
    /// <para>
    /// Every cell and header has half a gap of padding either side, so a fixed
    /// column is its token width plus a gap; Song and Album share what is left
    /// by their flex values. The row's and the header's own paddings each lose
    /// the half gap the outermost cells already supply.
    /// </para>
    /// </summary>
    private void ApplySongTableLayout()
    {
        var tokens = Theming.TokenResources.Current!;
        var n = tokens.Numbers;

        var gap = n["songTable.columnGap"];
        var listX = n["songTable.listPaddingX"];
        var listY = n["songTable.listPaddingY"];
        var rowPadding = n["songTable.rowPaddingX"] - (gap / 2);
        var headerPadding = n["songTable.headerPaddingX"] - (gap / 2);

        TrackGrid.Resources["songTable.derived.cellPadding"] = new Thickness(gap / 2, 0, gap / 2, 0);
        TrackGrid.Resources["songTable.derived.headerPadding"] = new Thickness(headerPadding, 0, headerPadding, 0);

        // DataGrid fits its columns to the viewport less the cells' offset from
        // the grid's left edge, which already includes the left list padding.
        // So the list area keeps only the row padding on the right, and the row's
        // fill stops the rest of the list padding short of the row's right edge.
        TrackGrid.Resources["songTable.derived.listMargin"] = new Thickness(listX, listY, rowPadding, listY);
        TrackGrid.Resources["songTable.derived.rowCellsMargin"] = new Thickness(rowPadding, 0, 0, 0);
        TrackGrid.Resources["songTable.derived.rowFillMargin"] = new Thickness(0, 0, listX - rowPadding, 0);

        const string prefix = "songTable.columns.";
        var ids = n.Keys.Concat(tokens.Texts.Keys)
            .Where(k => k.StartsWith(prefix, StringComparison.Ordinal))
            .Select(k => k[prefix.Length..k.LastIndexOf('.')])
            .Distinct();

        foreach (var id in ids)
        {
            var column = ColumnFor(id);

            if (n.TryGetValue($"{prefix}{id}.width", out var width))
            {
                column.Width = new DataGridLength(width + gap);
            }
            else
            {
                column.Width = new DataGridLength(n[$"{prefix}{id}.flex"], DataGridLengthUnitType.Star);
                column.MinWidth = FlexFloor + gap;
            }

            var alignKey = $"{prefix}{id}.align";
            var text = (TextAlignment)Theming.Tokens.Convert(tokens.Texts[alignKey], typeof(TextAlignment), alignKey);
            var header = (HorizontalAlignment)Theming.Tokens.Convert(tokens.Texts[alignKey], typeof(HorizontalAlignment), alignKey);

            var textStyle = id switch
            {
                "number" => "theme.songNumberText",
                "song" => "theme.songTitleText",
                _ => "theme.songText",
            };

            // On the column rather than the grid: the grid's CellStyle, set by its
            // style, lost to the old theme's implicit DataGridCell style (measured:
            // its 8 px padding, not the half gap). The # column has its own.
            column.CellStyle ??= (Style)FindResource("theme.songCell");

            if (column is DataGridTextColumn textColumn)
            {
                textColumn.ElementStyle = new Style(typeof(System.Windows.Controls.TextBlock), (Style)FindResource(textStyle))
                {
                    Setters = { new Setter(System.Windows.Controls.TextBlock.TextAlignmentProperty, text) },
                };
            }

            column.HeaderStyle = new Style(typeof(DataGridColumnHeader), (Style)FindResource("theme.songHeader"))
            {
                Setters = { new Setter(HorizontalContentAlignmentProperty, header) },
            };
        }

        _wideTrackNumbers = false;
        _viewModel.Tracks.CollectionChanged += (_, _) => QueueTrackNumberCheck();
    }

    /// <summary>
    /// The # column's number space is 16 px, two digits; an album of 100+ tracks
    /// gets numberWidth3Digits instead (spec 6.6). Checked once per batch of
    /// changes, since an album is added to the list a track at a time. Only a
    /// change between two and three digits touches the width, so a column the
    /// user has resized keeps its width from one album to the next.
    /// </summary>
    private void QueueTrackNumberCheck()
    {
        if (_trackNumberCheckQueued)
            return;

        _trackNumberCheckQueued = true;
        Dispatcher.BeginInvoke(() =>
        {
            _trackNumberCheckQueued = false;

            // In a playlist the number is the song's place, which reaches the count.
            var wide = _viewModel.IsPlaylistMode
                ? _viewModel.Tracks.Count >= 100
                : _viewModel.Tracks.Any(t => t.TrackNumber >= 100);
            if (wide == _wideTrackNumbers)
                return;

            _wideTrackNumbers = wide;
            TrackNumberColumn.Width = new DataGridLength(TrackNumberColumnWidth(wide));
        }, DispatcherPriority.Background);
    }

    private static double TrackNumberColumnWidth(bool threeDigits)
    {
        var n = Theming.TokenResources.Current!.Numbers;
        var width = n["songTable.columns.number.width"] + n["songTable.columnGap"];
        return threeDigits
            ? width + n["songTable.numberCell.numberWidth3Digits"] - n["songTable.numberCell.numberWidth"]
            : width;
    }

    /// <summary>
    /// Arms the slow second click that edits a cell in place. Double-clicking a
    /// row plays it (<see cref="TrackGrid_MouseDoubleClick"/>).
    /// </summary>
    private void TrackGrid_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _slowClickTimer.Stop();

        // A heart likes or unlikes its song on the press, and the press goes no
        // further: the cell would select its row on any press inside it, handled
        // or not. Each click of a double-click toggles, like a check box.
        if (e.OriginalSource is DependencyObject pressed
            && FindAncestor<LikeToggle>(pressed) is { DataContext: Track liked })
        {
            _viewModel.ToggleLikeCommand.Execute(liked);
            e.Handled = true;
            return;
        }

        // A press in a playlist may become a drag to reorder (MainWindow.Reorder.cs).
        if (e.ClickCount == 1 && BeginReorderPress(e))
        {
            e.Handled = true;
            return;
        }

        if (e.ClickCount == 1 && e.OriginalSource is DependencyObject source)
            ArmSlowClick(source);
    }

    private static T? FindAncestor<T>(DependencyObject? node) where T : DependencyObject
    {
        while (node is not null and not T)
            node = VisualTreeHelper.GetParent(node);

        return node as T;
    }

    private static T? FindDescendant<T>(DependencyObject node) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
        {
            var child = VisualTreeHelper.GetChild(node, i);
            if (child is T match)
                return match;

            if (FindDescendant<T>(child) is { } deeper)
                return deeper;
        }

        return null;
    }

    private void TrackGrid_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Tab in an edit walks the column too, as in a spreadsheet: down, or up
        // with Shift. Without this it left the table for the transport buttons.
        if (e.Key == Key.Tab && e.OriginalSource is TextBoxBase)
        {
            SaveAndEditNextRow((Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? -1 : 1);
            e.Handled = true;
            return;
        }

        if (e.Key is not (Key.Enter or Key.Return))
            return;

        if (e.OriginalSource is TextBoxBase)
        {
            SaveAndEditNextRow(1);
            e.Handled = true;
            return;
        }

        if (TrackGrid.SelectedItem is Track track)
        {
            _viewModel.PlayTrackCommand.Execute(track);
            e.Handled = true;
        }
    }

    /// <summary>
    /// Tab into the table lands on the selected song, as Tab into the Artists and
    /// Albums lists lands on their selected row, rather than where WPF last left
    /// focus in the table (the first row, the first time). Not for a click, which
    /// focuses the cell under the pointer while the button is down.
    /// </summary>
    private void TrackGrid_PreviewGotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (Mouse.LeftButton == MouseButtonState.Pressed
            || Mouse.RightButton == MouseButtonState.Pressed
            || e.OldFocus is Visual old && TrackGrid.IsAncestorOf(old)
            || e.NewFocus is not DataGridCell { Column: { } column } cell
            || TrackGrid.SelectedItem is not { } selected
            || cell.DataContext == selected)
            return;

        if (TrackGrid.ItemContainerGenerator.ContainerFromItem(selected) is not DataGridRow)
        {
            TrackGrid.ScrollIntoView(selected);
            TrackGrid.UpdateLayout();
        }

        if (TrackGrid.ItemContainerGenerator.ContainerFromItem(selected) is DataGridRow row
            && column.GetCellContent(row)?.Parent is DataGridCell target)
        {
            e.Handled = true;
            target.Focus();
        }
    }

    // ------------------------------------------------------ in-place tag edits

    /// <summary>
    /// # / Song / Artist / Album can be edited in their cells, as a file is
    /// renamed in Explorer: F2, or a second single click on a row that was
    /// already the one selected. Nothing else opens an edit - not the grid's
    /// own click on a selected cell, which would open one on the first half of
    /// every double-click that plays a track, and not typing, which on a grid
    /// that looks like a list would quietly start rewriting a tag.
    /// </summary>
    private readonly DispatcherTimer _slowClickTimer;
    private DataGridCell? _slowClickCell;
    private bool _openingEdit;

    [DllImport("user32.dll")]
    private static extern uint GetDoubleClickTime();

    // In a playlist # shows the song's place there, not its track number, so it isn't edited.
    private InlineField? FieldFor(DataGridColumn? column) =>
        column == TrackNumberColumn ? (_viewModel.IsPlaylistMode ? null : InlineField.TrackNumber)
        : column == SongColumn ? InlineField.Title
        : column == ArtistColumn ? InlineField.Artist
        : column == AlbumColumn ? InlineField.Album
        : null;

    /// <summary>
    /// A plain click on an editable cell of the one row already selected, with
    /// the grid already focused, opens an edit once the double-click time has
    /// passed without a second click - which would play the track instead.
    /// </summary>
    private void ArmSlowClick(DependencyObject source)
    {
        if (Keyboard.Modifiers != ModifierKeys.None
            || !TrackGrid.IsKeyboardFocusWithin
            || TrackGrid.SelectedItems.Count != 1
            || FindAncestor<DataGridCell>(source) is not { IsEditing: false } cell
            || FieldFor(cell.Column) is null
            || FindAncestor<DataGridRow>(cell) is not { IsSelected: true, Item: Track })
            return;

        _slowClickCell = cell;
        _slowClickTimer.Start();
    }

    private void SlowClickTimer_Tick(object? sender, EventArgs e)
    {
        _slowClickTimer.Stop();

        var cell = _slowClickCell;
        _slowClickCell = null;

        // Still the same row, still the only one selected, and the button up:
        // a held button is the start of a drag, not a click.
        if (cell is null || !cell.IsLoaded || Mouse.LeftButton == MouseButtonState.Pressed
            || TrackGrid.SelectedItems.Count != 1
            || !ReferenceEquals(cell.DataContext, TrackGrid.SelectedItem))
            return;

        OpenEdit(new DataGridCellInfo(cell));
    }

    private void OpenEdit(DataGridCellInfo cell)
    {
        TrackGrid.CurrentCell = cell;
        _openingEdit = true;
        try
        {
            TrackGrid.BeginEdit();
        }
        finally
        {
            _openingEdit = false;
        }
    }

    private void TrackGrid_BeginningEdit(object? sender, DataGridBeginningEditEventArgs e)
    {
        // F2 arrives through DataGrid.BeginEditCommand, with no input event;
        // the grid's own click and typing both carry theirs.
        var allowed = _openingEdit
            || (e.EditingEventArgs is null or KeyEventArgs { Key: Key.F2 } && TrackGrid.SelectedItems.Count <= 1);

        if (!allowed || FieldFor(e.Column) is null)
        {
            // Not stopping the slow click here: the grid's own attempt comes from
            // the very click that armed it, whenever that lands on the focused cell.
            e.Cancel = true;
            return;
        }

        _slowClickTimer.Stop();
    }

    private void TrackGrid_CellEditEnding(object? sender, DataGridCellEditEndingEventArgs e)
    {
        if (e.EditAction != DataGridEditAction.Commit
            || e.Row.Item is not Track track
            || e.EditingElement is not System.Windows.Controls.TextBox box
            || FieldFor(e.Column) is not { } field)
            return;

        // The column is bound one way - Track is immutable - so the typed text
        // goes no further than this box unless the save below replaces the row.
        _lastInlineSave = _viewModel.ApplyInlineEditAsync(track, field, box.Text);
    }

    private Task _lastInlineSave = Task.CompletedTask;

    /// <summary>The cell Enter moved to, opened once the save's rebuild has settled.</summary>
    private PendingEdit? _editNext;

    /// <summary>A cell to open for editing, with the text to put back if a rebuild interrupted it.</summary>
    private sealed record PendingEdit(string Path, DataGridColumn Column, TypedText? Typed = null);

    private sealed record TypedText(string Text, int SelectionStart, int SelectionLength);

    /// <summary>
    /// Enter or Tab saves the cell and opens the same field on the row below
    /// (<paramref name="step"/> 1), Shift+Tab on the row above (-1), so a column
    /// of track numbers can be typed straight down. Esc stops. Past the first or
    /// last row it just saves.
    /// </summary>
    private void SaveAndEditNextRow(int step)
    {
        if (TrackGrid.CurrentCell is not { Item: Track track, Column: { } column })
            return;

        var rows = TrackGrid.Items.OfType<Track>().ToList();
        var index = rows.IndexOf(track);

        _lastInlineSave = Task.CompletedTask;
        TrackGrid.CommitEdit(DataGridEditingUnit.Row, exitEditingMode: true);

        if (index < 0 || index + step < 0 || index + step >= rows.Count)
            return;

        // Taken before the save: a new track number can re-sort the edited row
        // elsewhere, and "next" is the track that was beside it.
        var next = rows[index + step];
        TrackGrid.SelectedItems.Clear();
        TrackGrid.SelectedItem = next;
        TrackGrid.CurrentCell = new DataGridCellInfo(next, column);
        FocusCell(next, column);

        // A save rebuilds the rows, which would cancel an edit opened now, so it
        // opens after the rebuild (RestoreGridPosition). A save that writes
        // nothing, or fails, never rebuilds; its finishing opens it instead.
        // Each Enter has its own target, so a slow earlier save finishing late
        // cannot open a later one before that one's rebuild.
        var pending = new PendingEdit(next.FilePath, column);
        _editNext = pending;
        var save = _lastInlineSave;
        if (save.IsCompleted)
            Dispatcher.BeginInvoke(() => OpenPendingEdit(pending), DispatcherPriority.Loaded);
        else
            save.ContinueWith(_ => Dispatcher.BeginInvoke(() => OpenPendingEdit(pending), DispatcherPriority.Loaded),
                TaskScheduler.Default);
    }

    /// <param name="only">Open only if this is still the pending edit.</param>
    /// <param name="hadFocus">
    /// Whether the grid had the keyboard; a rebuild passes what it saw before
    /// clearing the rows, since clearing them takes the focus away.
    /// </param>
    private void OpenPendingEdit(PendingEdit? only = null, bool? hadFocus = null)
    {
        if (_editNext is not { } target || _gridRestore is not null
            || (only is not null && !ReferenceEquals(only, target)))
            return;

        _editNext = null;

        var item = TrackGrid.Items.OfType<Track>().FirstOrDefault(t =>
            string.Equals(t.FilePath, target.Path, StringComparison.OrdinalIgnoreCase));

        // Gone (another album was chosen meanwhile), or the user has clicked
        // away from the grid.
        if (item is null || !(hadFocus ?? TrackGrid.IsKeyboardFocusWithin))
            return;

        // Selected again rather than trusted: after a commit the grid can hand
        // focus, and with it the selection, back to the row just edited.
        if (TrackGrid.SelectedItems.Count != 1 || !ReferenceEquals(TrackGrid.SelectedItem, item))
        {
            TrackGrid.SelectedItems.Clear();
            TrackGrid.SelectedItem = item;
        }

        TrackGrid.CurrentCell = new DataGridCellInfo(item, target.Column);
        FocusCell(item, target.Column);
        OpenEdit(new DataGridCellInfo(item, target.Column));

        // The grid settles its own focus on the rebuilt rows after this, which
        // leaves the box open but unfocused; hand it the keyboard once that's done.
        // The cell is looked up again then: straight after a rebuild its row may
        // not have been realised yet, which is also why this can take a retry.
        var typedApplied = false;
        void FocusBox(bool retry)
        {
            if (CellFor(item, target.Column) is not { IsEditing: true } cell
                || FindDescendant<System.Windows.Controls.TextBox>(cell) is not { } box)
            {
                if (retry)
                    Dispatcher.BeginInvoke(() => FocusBox(retry: false), DispatcherPriority.ApplicationIdle);
                return;
            }

            if (!box.IsKeyboardFocused)
                box.Focus();

            if (!typedApplied)
            {
                typedApplied = true;
                if (target.Typed is { } typed)
                {
                    box.Text = typed.Text;
                    box.Select(typed.SelectionStart, typed.SelectionLength);
                }
                else
                {
                    box.SelectAll();
                }
            }

            if (retry && !box.IsKeyboardFocused)
                Dispatcher.BeginInvoke(() => FocusBox(retry: false), DispatcherPriority.ApplicationIdle);
        }

        Dispatcher.BeginInvoke(() => FocusBox(retry: true), DispatcherPriority.ContextIdle);
    }

    private DataGridCell? CellFor(Track item, DataGridColumn column) =>
        TrackGrid.ItemContainerGenerator.ContainerFromItem(item) is DataGridRow row
        && column.GetCellContent(row)?.Parent is DataGridCell cell
            ? cell
            : null;

    private DataGridCell? FocusCell(Track item, DataGridColumn column)
    {
        TrackGrid.ScrollIntoView(item, column);
        TrackGrid.UpdateLayout();

        var cell = CellFor(item, column);
        cell?.Focus();
        return cell;
    }

    /// <summary>
    /// Clicking away from the grid saves an open edit, as leaving a rename box
    /// does; the grid itself would leave it open.
    /// </summary>
    private void TrackGrid_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        // No new focus is the window being switched away from; the edit waits.
        if (e.NewFocus is not DependencyObject next || IsInside(next, TrackGrid) || next is ContextMenu || next is System.Windows.Controls.MenuItem)
            return;

        TrackGrid.CommitEdit(DataGridEditingUnit.Row, exitEditingMode: true);
    }

    private static bool IsInside(DependencyObject node, DependencyObject ancestor)
    {
        for (var current = node; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (ReferenceEquals(current, ancestor))
                return true;
        }

        return false;
    }

    /// <summary>
    /// The rows are about to be cleared, which the grid cannot do under an open
    /// edit. So an open edit is cancelled and reopened, half-typed text and all,
    /// once the rows are back: a scan or an earlier save landing mid-edit costs
    /// nothing. Choosing another album saves it first, because the click takes
    /// focus off the grid.
    /// </summary>
    private void OnTracksChanging(object? sender, EventArgs e)
    {
        _slowClickTimer.Stop();

        if (TrackGrid.CurrentCell is { Item: Track editing, Column: { } editColumn }
            && CellFor(editing, editColumn) is { IsEditing: true } editCell
            && FindDescendant<System.Windows.Controls.TextBox>(editCell) is { } editBox)
        {
            _editNext = new PendingEdit(editing.FilePath, editColumn,
                new TypedText(editBox.Text, editBox.SelectionStart, editBox.SelectionLength));
        }

        TrackGrid.CancelEdit(DataGridEditingUnit.Row);

        // A save rebuilds the rows too, which would drop the selection and the
        // current column: Enter's move to the next row, and the row a slow click
        // needs selected. Put them back once the rows are refilled. A rebuild can
        // clear the list twice, so the first snapshot is the one kept.
        if (_gridRestore is not null)
            return;

        _gridRestore = new GridPosition(
            TrackGrid.SelectedItems.OfType<Track>().Select(t => t.FilePath).ToHashSet(StringComparer.OrdinalIgnoreCase),
            (TrackGrid.CurrentCell.Item as Track)?.FilePath,
            TrackGrid.CurrentCell.Column,
            TrackGrid.IsKeyboardFocusWithin);

        Dispatcher.BeginInvoke(RestoreGridPosition, DispatcherPriority.Loaded);
    }

    private sealed record GridPosition(
        HashSet<string> Selected, string? CurrentPath, DataGridColumn? CurrentColumn, bool HadFocus);

    private GridPosition? _gridRestore;

    /// <summary>
    /// Reselects the rows and current cell recorded by <see cref="OnTracksChanging"/>,
    /// matched by file path since a save replaces the Track objects. Another
    /// album's rows match nothing, so choosing one is unaffected.
    /// </summary>
    private void RestoreGridPosition()
    {
        var saved = _gridRestore;
        _gridRestore = null;

        if (saved is null)
            return;

        RestoreSelection(saved);

        // An edit Enter moved to, or one the rebuild interrupted, reopens now.
        OpenPendingEdit(hadFocus: saved.HadFocus);
    }

    private void RestoreSelection(GridPosition saved)
    {
        var rows = TrackGrid.Items.OfType<Track>().ToList();
        var selected = rows.Where(t => saved.Selected.Contains(t.FilePath)).ToList();
        if (selected.Count == 0)
            return;

        TrackGrid.SelectedItems.Clear();
        foreach (var track in selected)
            TrackGrid.SelectedItems.Add(track);

        var current = rows.FirstOrDefault(t =>
                          string.Equals(t.FilePath, saved.CurrentPath, StringComparison.OrdinalIgnoreCase))
                      ?? selected[0];
        var column = saved.CurrentColumn ?? SongColumn;

        TrackGrid.CurrentCell = new DataGridCellInfo(current, column);
        TrackGrid.ScrollIntoView(current, column);

        if (!saved.HadFocus)
            return;

        // Focusing the cell is what lets F2 and the slow click carry on.
        if (FocusCell(current, column) is null)
            TrackGrid.Focus();
    }

    // The seek bar is bound two-way, so the position timer and the user's drag
    // would otherwise fight over it. IsSeeking parks the timer mid-drag, and the
    // seek is only sent to the engine once the user lets go.

    /// <summary>
    /// A click on the track (IsMoveToPointEnabled) has already jumped the value
    /// by the time this runs. SliderDrag normally turns that press into a drag
    /// first, which parks the timer (IsSeeking) until the button comes up, so
    /// this does nothing. If no drag started, the seek goes now: waiting for the
    /// button let a position tick put the old spot back in between, and the
    /// handle went there, back, and there again.
    /// </summary>
    private void SeekBar_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!_viewModel.IsSeeking)
            _viewModel.CommitSeek();
    }

    /// <summary>
    /// The keys move the value (arrows 5 s, Page Up/Down 30 s, Home/End to the
    /// ends) with no button to let go of, so each press seeks straight away.
    /// Otherwise the next position tick put the old spot back.
    /// </summary>
    private void SeekBar_KeyDown(object sender, KeyEventArgs e)
    {
        if (!_viewModel.IsSeeking)
            _viewModel.CommitSeek();
    }

    private void SeekBar_DragStarted(object sender, DragStartedEventArgs e) =>
        _viewModel.IsSeeking = true;

    private void SeekBar_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        _viewModel.IsSeeking = false;
        _viewModel.CommitSeek();
    }

    /// <summary>
    /// A backstop: a click on the track has already seeked on the way down, and a
    /// drag on DragCompleted, so normally nothing is pending here.
    /// </summary>
    private void SeekBar_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _viewModel.IsSeeking = false;
        _viewModel.CommitSeek();
    }
}
