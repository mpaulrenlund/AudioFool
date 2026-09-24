using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AudioFool;
using AudioFool.Core.Models;
using AudioFool.Core.Playback;
using AudioFool.Core.Settings;
using AudioFool.Services;
using AudioFool.ViewModels;

namespace ThemeLab;

/// <summary>
/// Renders the real AudioFool windows off-screen to PNG so a theme can be
/// reviewed without showing a window, stealing focus, or touching the library.
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var theme = Arg(args, "--theme") ?? "Dark";
        var outPath = Arg(args, "--out") ?? "shot.png";
        var w = double.Parse(Arg(args, "--w") ?? "1560", CultureInfo.InvariantCulture);
        var h = double.Parse(Arg(args, "--h") ?? "900", CultureInfo.InvariantCulture);
        var scale = double.Parse(Arg(args, "--scale") ?? "1", CultureInfo.InvariantCulture);
        var dumpPath = Arg(args, "--dump");
        var which = Arg(args, "--window") ?? "main";

        if (Arg(args, "--bg") is { } bg)
        {
            _backdrop = (Color)ColorConverter.ConvertFromString(bg);
        }

        var app = new LabApp();
        app.InitializeComponent();

        if (dumpPath is not null)
        {
            // After Apply, so the dump shows what the theme overlay and
            // ApplicationAccentColorManager actually leave in place.
            ThemeService.Apply(theme);
            Dump(dumpPath);
            return 0;
        }

        // A throwaway settings object: never Load()ed and never Save()d, so the
        // real settings.json is untouched. No music folders means InitialiseAsync
        // short-circuits before any scan.
        var settings = new AppSettings
        {
            Theme = theme,
            ScanOnStartup = false,
            GlobalHotkeys = false,
        };

        var runtime = new BassRuntime();
        var engine = new AudioEngine(runtime);
        var vm = new MainViewModel(engine, runtime, new AlbumArtService(), settings);

        // Same order as App.OnStartup: theme first, then the window, because a
        // DynamicResource Style is resolved as the element initialises. --switch
        // starts under Dark and swaps afterwards instead, which is what choosing
        // a theme from the menu does.
        var switching = Arg(args, "--switch") is not null;
        var startTheme = switching ? "Dark" : theme;
        ThemeService.Apply(startTheme);

        var main = new MainWindow(vm);
        Application.Current.MainWindow = main;
        main.WindowBackdropType = ThemeService.Backdrop;

        // Far off every monitor and shown without activation, so nothing appears
        // on the desktop and nothing takes focus.
        main.Width = w;
        main.Height = h;
        main.WindowStartupLocation = WindowStartupLocation.Manual;
        main.Left = -20000;
        main.Top = -20000;
        main.ShowActivated = false;
        main.ShowInTaskbar = false;
        main.Topmost = false;
        main.Show();

        Pump();
        if (which == "click")
        {
            LoadRealLibrary(vm);
        }
        else
        {
            Populate(vm);
        }

        Settle(300);

        if (switching)
        {
            ThemeService.Apply(theme);
            Settle(300);
        }

        if (main.FindName("TrackGrid") is System.Windows.Controls.DataGrid grid)
        {
            grid.SelectedIndex = int.Parse(Arg(args, "--row") ?? "4", CultureInfo.InvariantCulture);
        }

        if (Arg(args, "--focus") is { } focusName
            && main.FindName(focusName) is System.Windows.IInputElement target)
        {
            System.Windows.Input.Keyboard.Focus(target);
            Console.WriteLine($"focus {focusName}: keyboard={(target as UIElement)?.IsKeyboardFocused}");
        }

        Settle(1200);
        main.UpdateLayout();

        if (Arg(args, "--probe") is not null)
        {
            var pane = FindFirst<System.Windows.Controls.HeaderedContentControl>(main);
            Console.WriteLine($"pane={pane}  styleIsAppAfPane={ReferenceEquals(pane?.Style, Application.Current.TryFindResource("AfPane"))}");
            if (pane is not null)
            {
                var border = FindFirst<System.Windows.Controls.Border>(pane);
                Console.WriteLine($"outer border radius={border?.CornerRadius} bg={Describe(border?.Background)} "
                    + $"stroke={Describe(border?.BorderBrush)}");
                Console.WriteLine($"pane resolves AfRadiusPanel={Describe(pane.TryFindResource("AfRadiusPanel"))} "
                    + $"AfSurfacePanel={Describe(pane.TryFindResource("AfSurfacePanel"))}");
                Console.WriteLine($"window resolves AfRadiusPanel={Describe(main.TryFindResource("AfRadiusPanel"))}");

                for (DependencyObject? node = pane; node is not null; node = LogicalTreeHelper.GetParent(node) ?? VisualTreeHelper.GetParent(node))
                {
                    if (node is FrameworkElement fe && fe.Resources.Count + fe.Resources.MergedDictionaries.Count > 0)
                    {
                        Console.WriteLine($"  scope {fe.GetType().Name}: own={fe.Resources.Count} merged={fe.Resources.MergedDictionaries.Count} "
                            + $"hasToken={fe.Resources.Contains("AfRadiusPanel")}");
                    }
                }

                var appRes = Application.Current.Resources;
                for (var i = 0; i < appRes.MergedDictionaries.Count; i++)
                {
                    var d = appRes.MergedDictionaries[i];
                    Console.WriteLine($"  app[{i}] src={d.Source} count={d.Count} hasToken={d.Contains("AfRadiusPanel")} "
                        + $"value={(d.Contains("AfRadiusPanel") ? Describe(d["AfRadiusPanel"]) : "-")}");
                }
            }
        }

        // --artmenu: does right-clicking the album header art offer Edit Album Tags
        // for the selected album? Opens the menu off-screen and reads its bindings.
        if (Arg(args, "--artmenu") is not null)
        {
            var art = FindAll<System.Windows.Controls.Border>(main)
                .FirstOrDefault(b => b.ContextMenu is not null
                    && ReferenceEquals(b.Style, main.TryFindResource("AfArtFrameLarge")));
            if (art?.ContextMenu is not { } menu)
            {
                Console.WriteLine("artmenu: no context menu on the header art");
            }
            else
            {
                menu.PlacementTarget = art;
                menu.IsOpen = true;
                Settle(300);
                foreach (var entry in menu.Items.OfType<System.Windows.Controls.MenuItem>())
                {
                    var album = entry.CommandParameter as AudioFool.ViewModels.AlbumItemViewModel;
                    Console.WriteLine($"artmenu: '{entry.Header}' command={entry.Command is not null} "
                        + $"canExecute={entry.Command?.CanExecute(entry.CommandParameter)} enabled={entry.IsEnabled} "
                        + $"param='{album?.Album.Title}' selected='{vm.SelectedAlbum?.Album.Title}' "
                        + $"same={ReferenceEquals(album, vm.SelectedAlbum)}");
                }
                menu.IsOpen = false;
            }
        }

        // --menu: does clicking the logo open the File-style menu? Two checks that
        // do not need a mouse. First hit-test the middle of the logo and walk up,
        // which is the path a click takes; then expand through the automation peer,
        // which is what MenuItem's own click handler ends up doing.
        if (Arg(args, "--menu") is not null)
        {
            var item = FindFirst<System.Windows.Controls.MenuItem>(main);
            if (item is null)
            {
                Console.WriteLine("menu: no MenuItem found");
            }
            else
            {
                var header = item.Header as FrameworkElement;
                Console.WriteLine($"menu: header={item.Header?.GetType().Name} role={item.Role} "
                    + $"items={item.Items.Count} headerSize={header?.ActualWidth:0.#}x{header?.ActualHeight:0.#}");

                if (header is not null)
                {
                    var mid = header.TranslatePoint(
                        new Point(header.ActualWidth / 2, header.ActualHeight / 2), main);
                    var hit = main.InputHitTest(mid) as DependencyObject;
                    var reachesItem = false;
                    for (var node = hit; node is not null; node = VisualTreeHelper.GetParent(node))
                    {
                        if (ReferenceEquals(node, item))
                        {
                            reachesItem = true;
                            break;
                        }
                    }

                    Console.WriteLine($"menu: hit-test at logo centre {mid.X:0.#},{mid.Y:0.#} -> "
                        + $"{hit?.GetType().Name ?? "<nothing>"}, inside the MenuItem={reachesItem}");
                }

                // Walk the MenuItem's visuals: something in WPF-UI's template clips
                // the header, so print every element's size, explicit height limits
                // and clip geometry.
                void Walk(DependencyObject node, int depth)
                {
                    var pad = new string(' ', depth * 2);
                    if (node is FrameworkElement fe)
                    {
                        var clip = VisualTreeHelper.GetClip(fe);
                        var extra = "";
                        if (!double.IsNaN(fe.Height)) extra += $" Height={fe.Height}";
                        if (!double.IsPositiveInfinity(fe.MaxHeight)) extra += $" MaxHeight={fe.MaxHeight}";
                        if (fe.MinHeight > 0) extra += $" MinHeight={fe.MinHeight}";
                        if (fe.ClipToBounds) extra += " ClipToBounds";
                        if (clip is not null) extra += $" Clip={clip.Bounds}";
                        if (fe is System.Windows.Controls.Border b2) extra += $" Padding={b2.Padding}";
                        if (fe is System.Windows.Controls.Control cc) extra += $" Padding={cc.Padding}";
                        Console.WriteLine($"  {pad}{fe.GetType().Name} {fe.Name} "
                            + $"{fe.ActualWidth:0.#}x{fe.ActualHeight:0.#} desired={fe.DesiredSize.Width:0.#}x{fe.DesiredSize.Height:0.#}{extra}");
                    }
                    else
                    {
                        Console.WriteLine($"  {pad}{node.GetType().Name}");
                    }

                    for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
                    {
                        Walk(VisualTreeHelper.GetChild(node, i), depth + 1);
                    }
                }

                if (Arg(args, "--menutree") is not null)
                {
                    Console.WriteLine("menu: visual tree under the MenuItem");
                    Walk(item, 0);
                }

                var peer = System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(item);
                var pattern = peer?.GetPattern(System.Windows.Automation.Peers.PatternInterface.ExpandCollapse)
                    as System.Windows.Automation.Provider.IExpandCollapseProvider;
                Console.WriteLine($"menu: ExpandCollapse pattern={(pattern is null ? "<none>" : "yes")}");
                pattern?.Expand();
                Settle(200);
                Console.WriteLine($"menu: IsSubmenuOpen={item.IsSubmenuOpen}");
                foreach (var child in item.Items)
                {
                    if (child is System.Windows.Controls.MenuItem mi)
                    {
                        Console.WriteLine($"  - {mi.Header} (checkable={mi.IsCheckable}, enabled={mi.IsEnabled})");
                    }
                    else
                    {
                        Console.WriteLine($"  - <{child?.GetType().Name}>");
                    }
                }

                pattern?.Collapse();
                Settle(100);
                Console.WriteLine($"menu: closed again IsSubmenuOpen={item.IsSubmenuOpen}");
            }
        }

        if (which == "tags")
        {
            // Never shown: TagEditWindow re-centres itself over its owner on
            // Loaded, which would drag it onto a real monitor. Laying it out by
            // hand keeps it off-screen entirely.
            // --album "<title>" opens the album dialog on a real album from
            // library.json instead, and --track "<title>" the track dialog on a
            // real track. Both read the files' tags (for the detail fields) and
            // never write them.
            TagEditViewModel editVm;
            var albumName = Arg(args, "--album");
            var trackName = Arg(args, "--track");
            if (albumName is not null || trackName is not null)
            {
                var cached = AudioFool.Core.Library.LibraryCache.Load();
                var library = AudioFool.Core.Library.LibraryScanner.Build(cached?.Tracks ?? SampleTracks());
                var clock = System.Diagnostics.Stopwatch.StartNew();
                if (albumName is not null)
                {
                    var album = library.Artists.SelectMany(a => a.Albums)
                        .First(a => string.Equals(a.Title, albumName, StringComparison.OrdinalIgnoreCase));
                    editVm = new TagEditViewModel(album, new AlbumArtService());
                    Console.WriteLine($"album: {album.Title} ({album.Tracks.Count} tracks), opened in {clock.ElapsedMilliseconds} ms");
                }
                else
                {
                    var track = library.AllTracks
                        .First(t => string.Equals(t.Title, trackName, StringComparison.OrdinalIgnoreCase));
                    editVm = new TagEditViewModel(track);
                    Console.WriteLine($"track: {track.FilePath}, opened in {clock.ElapsedMilliseconds} ms");
                }

                Console.WriteLine($"  counts    '{editVm.TrackCount}' [{editVm.TrackCountPlaceholder}]  disc '{editVm.DiscNumber}' [{editVm.DiscNumberPlaceholder}] of '{editVm.DiscCount}' [{editVm.DiscCountPlaceholder}]");
                Console.WriteLine($"  publisher '{editVm.Publisher}' [{editVm.PublisherPlaceholder}]");
                Console.WriteLine($"  composer  '{editVm.Composer}' [{editVm.ComposerPlaceholder}]");
                Console.WriteLine($"  conductor '{editVm.Conductor}' [{editVm.ConductorPlaceholder}]");
                Console.WriteLine($"  genre     '{editVm.Genre}' [{editVm.GenrePlaceholder}]");
                Console.WriteLine($"  comment   '{editVm.Comment}' [{editVm.CommentPlaceholder}]");

                // --set "Genre=Rock;DiscNumber=2" types into the boxes, then prints
                // what Save would write. Nothing is saved: the edit is only built.
                if (Arg(args, "--set") is { } sets)
                {
                    foreach (var pair in sets.Split(';', StringSplitOptions.RemoveEmptyEntries))
                    {
                        // "!Comment" presses that field's clear button instead.
                        if (pair.StartsWith('!'))
                        {
                            editVm.ClearFieldCommand.Execute(pair[1..]);
                            continue;
                        }

                        var (name, value) = (pair[..pair.IndexOf('=')], pair[(pair.IndexOf('=') + 1)..]);
                        typeof(TagEditViewModel).GetProperty(name)!.SetValue(editVm, value);
                    }
                }

                static string Q(string? s) => s is null ? "(keep)" : $"'{s}'";
                static string N(AudioFool.Core.Library.NumberEdit? n) => n is { } e ? $"'{e.Value}'" : "(keep)";
                var details = albumName is not null ? editVm.BuildAlbumEdit().Details : editVm.BuildTrackEdit().Details;
                if (albumName is not null)
                {
                    var albumEdit = editVm.BuildAlbumEdit();
                    Console.WriteLine($"save: tracks {N(albumEdit.TrackCount)} disc {N(albumEdit.DiscNumber)} of {N(albumEdit.DiscCount)}");
                }
                Console.WriteLine($"save: publisher {Q(details.Publisher)} composer {Q(details.Composer)} conductor {Q(details.Conductor)} "
                    + $"genre {Q(details.Genre)} comment {Q(details.Comment)}  valid={editVm.CanSave} {editVm.ValidationError}");
            }
            else
            {
                editVm = new TagEditViewModel(SampleTracks()[2]);
            }

            var dialog = new TagEditWindow(editVm, main);
            dialog.ApplyTemplate();

            // Its content, not the window: an unshown Window measures to nothing,
            // and the chrome here is only a title bar anyway.
            var root = (FrameworkElement)dialog.Content;
            root.Measure(new Size(w, double.PositiveInfinity));
            var height = Math.Ceiling(root.DesiredSize.Height);
            root.Arrange(new Rect(0, 0, w, height));
            root.UpdateLayout();
            Settle(400);
            Save(root, outPath, w, height, scale);
        }
        else if (which == "click")
        {
            // --window click --click "<row label>": clicks a Statistics row the way
            // a user would - a real Click on the row's Button inside a real
            // StatisticsWindow - then hands the choice to the main view model and
            // renders the main window with the result. No mouse, nothing on screen.
            var label = Arg(args, "--click") ?? "Year";

            // --search: start with a query in the box, to check a filter combines
            // with it and that an artist row clears one that would hide the artist.
            if (Arg(args, "--search") is { } query)
            {
                vm.SearchQuery = query;
                Settle(400);
                Console.WriteLine($"search '{query}': artists={vm.Artists.Count:N0} status='{vm.StatusText}'");
            }
            var libraryField = typeof(MainViewModel).GetField("_folderFilteredLibrary",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            var library = (AudioFool.Core.Library.MusicLibrary)libraryField.GetValue(vm)!;
            var statsVm = new StatisticsViewModel(AudioFool.Core.Library.LibraryStatistics.Compute(library), false);

            var dialog = new StatisticsWindow(statsVm, main);
            dialog.ApplyTemplate();
            var root = (FrameworkElement)dialog.Content;
            root.Measure(new Size(900, double.PositiveInfinity));
            root.Arrange(new Rect(root.DesiredSize));
            root.UpdateLayout();
            Pump();

            var buttons = FindAll<System.Windows.Controls.Button>(root)
                .Where(b => b.DataContext is BarRow)
                .ToList();
            Console.WriteLine($"click: {buttons.Count} rows, {buttons.Count(b => b.IsEnabled)} clickable; "
                + $"inert: {string.Join(", ", buttons.Where(b => !b.IsEnabled).Select(b => ((BarRow)b.DataContext).Label))}");

            var rowButton = buttons.FirstOrDefault(b => ((BarRow)b.DataContext).Label.EndsWith(label, StringComparison.Ordinal));
            if (rowButton is null)
            {
                Console.WriteLine($"click: no row labelled '{label}'");
                return 1;
            }

            var chosenRow = (BarRow)rowButton.DataContext;
            Console.WriteLine($"click: '{chosenRow.Label}' enabled={rowButton.IsEnabled} tooltip='{chosenRow.ToolTip}' "
                + $"uia='{System.Windows.Automation.AutomationProperties.GetName(rowButton)}'");

            // Setting DialogResult on a window that was never shown modally throws,
            // after Row_Click has already recorded the choice - which is the part
            // under test. ShowDialog would put a real window on the desktop.
            try
            {
                rowButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, rowButton));
            }
            catch (InvalidOperationException)
            {
            }

            Console.WriteLine($"click: Chosen = {statsVm.Chosen?.Label ?? "<none>"}");
            if (statsVm.Chosen is { } picked)
            {
                vm.ApplyStatisticsChoice(picked);
            }

            Settle(400);
            main.UpdateLayout();

            var shown = vm.Artists.Sum(a => a.TrackCount);
            var chip = (FrameworkElement)main.FindName("LibraryFilterChip");
            Console.WriteLine($"main: filter='{vm.LibraryFilter?.Description}' artists={vm.Artists.Count:N0} tracks={shown:N0} "
                + $"selected='{vm.SelectedArtist?.Name}' / '{vm.SelectedAlbum?.Album.Title}'");
            Console.WriteLine($"main: status='{vm.StatusText}'");
            Console.WriteLine($"main: chip visible={chip.IsVisible} size={chip.ActualWidth:0}x{chip.ActualHeight:0} "
                + $"uia='{System.Windows.Automation.AutomationProperties.GetName(chip)}'");

            Save(main, outPath, w, h, scale);

            // --fix: an album-tag edit on the selected album, fed through the same
            // private ReplaceTracksInLibrary a real save ends with - in memory only,
            // no file is written - to check the fixed tracks leave the filter.
            if (Arg(args, "--fix") is { } year && vm.SelectedAlbum is { } fixAlbum)
            {
                var edit = new AudioFool.Core.Library.AlbumTagEdit(
                    fixAlbum.Album.ArtistName, fixAlbum.Album.ArtistName, fixAlbum.Album.Title,
                    int.Parse(year, CultureInfo.InvariantCulture));
                var fixedTracks = fixAlbum.Album.Tracks.ToDictionary(
                    t => t.FilePath,
                    t => t.WithAlbumTags(edit, new AudioFool.Core.Library.FileStamp(t.FileSize, t.ModifiedUtc), t.FolderArtPath),
                    StringComparer.OrdinalIgnoreCase);
                typeof(MainViewModel)
                    .GetMethod("ReplaceTracksInLibrary", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                    .Invoke(vm, [fixedTracks]);
                var progress = (string)typeof(MainViewModel)
                    .GetMethod("WithFilterProgress", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                    .Invoke(vm, [$"Saved tags for {fixedTracks.Count} track(s)."])!;
                Settle(300);
                Console.WriteLine($"fixed '{fixAlbum.Album.Title}' ({fixedTracks.Count} tracks): "
                    + $"tracks now={vm.Artists.Sum(a => a.TrackCount):N0} artists={vm.Artists.Count} selected='{vm.SelectedArtist?.Name}'");
                Console.WriteLine($"fixed: status would read '{progress}'");
            }

            // Then clear it through the chip's own automation peer, which is the
            // path a click on it takes.
            if (chip.IsVisible)
            {
                var peer = System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(chip);
                (peer?.GetPattern(System.Windows.Automation.Peers.PatternInterface.Invoke)
                    as System.Windows.Automation.Provider.IInvokeProvider)?.Invoke();
                Settle(300);
                Console.WriteLine($"cleared: filter='{vm.LibraryFilter?.Description}' chip visible={chip.IsVisible} "
                    + $"tracks={vm.Artists.Sum(a => a.TrackCount):N0} selected='{vm.SelectedArtist?.Name}'");
                Console.WriteLine($"cleared: status='{vm.StatusText}'");
            }
        }
        else if (which == "stats")
        {
            // The real library, read from the cache and never written back:
            // LibraryCache.Load only opens the file for reading. Falls back to
            // the sample tracks when there is no cache on this machine.
            var cached = AudioFool.Core.Library.LibraryCache.Load();
            var tracks = cached?.Tracks ?? SampleTracks();
            var library = AudioFool.Core.Library.LibraryScanner.Build(tracks);

            var clock = System.Diagnostics.Stopwatch.StartNew();
            var stats = AudioFool.Core.Library.LibraryStatistics.Compute(library);
            Console.WriteLine($"stats: {tracks.Count:N0} tracks from {(cached is null ? "samples" : "library.json")}, "
                + $"computed in {clock.ElapsedMilliseconds} ms");

            var statsVm = new StatisticsViewModel(stats, someFoldersHidden: Arg(args, "--hidden") is not null);
            foreach (var t in statsVm.Tiles) Console.WriteLine($"  tile  {t.Label,-10} {t.Value}");
            void Rows(string name, IEnumerable<BarRow> rows)
            {
                Console.WriteLine($"  [{name}]");
                foreach (var r in rows) Console.WriteLine($"    {r.Label,-32} {r.Value,-30} {r.Detail}");
            }
            Rows("top artists", statsVm.TopArtists);
            Rows("file types", statsVm.FileTypes);
            Rows("quality", statsVm.Quality);
            Rows("missing tags", statsVm.TagGaps);
            Console.WriteLine($"    {statsVm.TagSummary}");
            Rows("decades", statsVm.Decades);

            // Never shown, for the same reason as the tag dialog above.
            var dialog = new StatisticsWindow(statsVm, main);
            dialog.ApplyTemplate();
            var root = (FrameworkElement)dialog.Content;
            root.Measure(new Size(w, double.PositiveInfinity));
            var height = Math.Ceiling(root.DesiredSize.Height);
            root.Arrange(new Rect(0, 0, w, height));
            root.UpdateLayout();
            Settle(400);
            Save(root, outPath, w, height, scale);
        }
        else
        {
            Save(main, outPath, w, h, scale);
        }

        main.Close();
        Console.WriteLine("wrote " + outPath);
        return 0;
    }

    private static string? Arg(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == name)
            {
                return args[i + 1];
            }
        }

        return null;
    }

    /// <summary>
    /// Prints the resolved value of every theme resource key listed, so token
    /// defaults can be baked from what WPF-UI actually supplies.
    /// </summary>
    private static void Dump(string path)
    {
        string[] keys =
        [
            "ApplicationBackgroundBrush",
            "ControlFillColorDefaultBrush", "ControlFillColorSecondaryBrush",
            "ControlFillColorTertiaryBrush", "ControlFillColorDisabledBrush",
            "ControlElevationBorderBrush", "ControlStrokeColorDefaultBrush",
            "ControlStrokeColorSecondaryBrush", "CardBackgroundFillColorDefaultBrush",
            "CardBackgroundFillColorSecondaryBrush", "CardStrokeColorDefaultBrush",
            "SolidBackgroundFillColorBaseBrush", "SolidBackgroundFillColorBaseAltBrush",
            "SolidBackgroundFillColorSecondaryBrush", "SolidBackgroundFillColorTertiaryBrush",
            "SolidBackgroundFillColorQuarternaryBrush",
            "LayerFillColorDefaultBrush", "LayerFillColorAltBrush",
            "SubtleFillColorSecondaryBrush", "SubtleFillColorTertiaryBrush",
            "SubtleFillColorTransparentBrush", "SubtleFillColorDisabledBrush",
            "TextFillColorPrimaryBrush", "TextFillColorSecondaryBrush",
            "TextFillColorTertiaryBrush", "TextFillColorDisabledBrush",
            "TextFillColorInverseBrush", "TextOnAccentFillColorPrimaryBrush",
            "TextOnAccentFillColorSecondaryBrush", "TextOnAccentFillColorDisabledBrush",
            "AccentFillColorDefaultBrush", "AccentFillColorSecondaryBrush",
            "AccentFillColorTertiaryBrush", "AccentFillColorDisabledBrush",
            "AccentTextFillColorPrimaryBrush", "AccentTextFillColorSecondaryBrush",
            "AccentControlElevationBorderBrush", "ControlStrokeColorOnAccentDefaultBrush",
            "DividerStrokeColorDefaultBrush", "SurfaceStrokeColorDefaultBrush",
            "SurfaceStrokeColorFlyoutBrush",
            "FocusStrokeColorOuterBrush", "FocusStrokeColorInnerBrush",
            "KeyboardFocusBorderColorBrush",
            "SystemFillColorSuccessBrush", "SystemFillColorCautionBrush",
            "SystemFillColorCriticalBrush", "SystemFillColorAttentionBrush",
            "SystemFillColorNeutralBrush",
            "ControlCornerRadius", "OverlayCornerRadius",
            "MenuBorderColorDefaultBrush", "ContextMenuBorderBrush", "MenuBarItemBorderBrush",
            "ToolTipBorderBrush", "ProgressBarBorderBrush",
            "TextControlBorderBrush", "TextControlElevationBorderBrush",
            "TextControlFocusedBorderBrush", "TextPlaceholderColorBrush",
            "ToggleButtonBorderBrush", "ButtonBorderBrush",
            "ControlStrongFillColorDefaultBrush", "ControlStrongStrokeColorDefaultBrush",
            "ListBoxItemSelectedBackgroundThemeBrush",
            "HorizontalGridLinesBrush", "VerticalGridLinesBrush",
            "SliderTrackFill", "SliderTrackFillPointerOver",
            "SliderOuterThumbBackground", "SliderThumbBackground",
            "SliderThumbBackgroundPointerOver",
            "ContentControlThemeFontFamily", "ControlContentThemeFontSize",
            "AfSurfacePanel", "AfSurfaceShell", "AfRadiusPanel", "AfFontMono", "AfStrokeWarm",
            "AfSizeBrandMark", "AfSizeTrackRowHeight", "AfStrokeSelectionBar",
        ];

        var sb = new StringBuilder();
        foreach (var key in keys)
        {
            sb.AppendLine(key + " = " + Describe(Application.Current.TryFindResource(key)));
        }

        File.WriteAllText(path, sb.ToString());
        Console.WriteLine(sb.ToString());
    }

    private static string Describe(object? value) => value switch
    {
        null => "<missing>",
        SolidColorBrush s => $"Solid {s.Color} op={s.Opacity:0.###}",
        LinearGradientBrush g =>
            $"LinearGradient {g.StartPoint}->{g.EndPoint} map={g.MappingMode} op={g.Opacity:0.###} ["
            + string.Join(", ", g.GradientStops.Select(x => $"{x.Color}@{x.Offset:0.####}")) + "]",
        CornerRadius c => $"CornerRadius {c}",
        Thickness t => $"Thickness {t}",
        _ => value.GetType().Name + " " + value,
    };

    // ------------------------------------------------------------ sample data

    private static Track Make(
        string title, string artist, string album, int n, int? disc, int year,
        string kind, int? bitrate, int? depth, int? rate, int seconds,
        int? trackCount = 10, int? discCount = 2) => new()
        {
            FilePath = $@"D:\Music\{artist}\{album}\{n:00} {title}.flac",
            FileSize = 40_000_000,
            ModifiedUtc = new DateTime(2024, 3, 1, 12, 0, 0, DateTimeKind.Utc),
            Title = title,
            Artist = artist,
            AlbumArtist = artist,
            Album = album,
            TrackNumber = n,
            TrackCount = trackCount,
            DiscNumber = disc,
            DiscCount = discCount,
            Year = year,
            Kind = kind,
            Bitrate = bitrate,
            BitDepth = depth,
            SampleRate = rate,
            Duration = TimeSpan.FromSeconds(seconds),
        };

    private static List<Track> SampleTracks() =>
    [
        Make("Sunlight Through Static", "Aphelion Drive", "Second Sight", 1, 1, 1997, "FLAC", 1084, 24, 96000, 271),
        Make("Memory Card", "Aphelion Drive", "Second Sight", 2, 1, 1997, "FLAC", 998, 24, 96000, 214),
        Make("Polygon Weather", "Aphelion Drive", "Second Sight", 3, 1, 1997, "FLAC", 1140, 24, 96000, 332),
        Make("Low Poly Sunrise", "Aphelion Drive", "Second Sight", 4, 1, 1997, "DSD", null, null, 2822400, 289),
        Make("Analogue Stick", "Aphelion Drive", "Second Sight", 5, 1, 1997, "FLAC", 1012, 24, 96000, 198),
        Make("Wireframe Hymn", "Aphelion Drive", "Second Sight", 6, 2, 1997, "FLAC", 1067, 24, 96000, 401),
        // One untagged-for-totals row, so the render shows the bare-number fallback.
        Make("Disc Read Error", "Aphelion Drive", "Second Sight", 7, 2, 1997, "MP3", 320, null, 44100, 176, null, null),
        Make("Grey Plastic", "Aphelion Drive", "Second Sight", 8, 2, 1997, "FLAC", 921, 16, 44100, 243),
        Make("Startup Chime", "Aphelion Drive", "Second Sight", 9, 2, 1997, "FLAC", 1188, 24, 192000, 88),
        Make("Second Sight", "Aphelion Drive", "Second Sight", 10, 2, 1997, "FLAC", 1023, 24, 96000, 366),
    ];

    private static ArtistGroup Artist(string name, params Album[] albums) =>
        new() { Name = name, SortKey = name, Albums = albums };

    private static Album Alb(string title, string artist, int year, IReadOnlyList<Track> tracks) =>
        new() { Title = title, ArtistName = artist, Year = year, Tracks = tracks };

    private static void Populate(MainViewModel vm)
    {
        var tracks = SampleTracks();

        var aphelion = Artist(
            "Aphelion Drive",
            Alb("First Light", "Aphelion Drive", 1995, tracks.Take(6).ToList()),
            Alb("Second Sight", "Aphelion Drive", 1997, tracks),
            Alb("Third Person", "Aphelion Drive", 2001, tracks.Take(8).ToList()));

        vm.Artists.Add(Artist("Aeon Static", Alb("Cold Boot", "Aeon Static", 1994, tracks.Take(4).ToList())));
        vm.Artists.Add(aphelion);
        vm.Artists.Add(Artist(
            "Bitcrush Quartet",
            Alb("Quantise", "Bitcrush Quartet", 1998, tracks.Take(7).ToList()),
            Alb("Resample", "Bitcrush Quartet", 2000, tracks.Take(5).ToList())));
        vm.Artists.Add(Artist("Dual Shock Ensemble", Alb("Rumble", "Dual Shock Ensemble", 1999, tracks.Take(9).ToList())));
        vm.Artists.Add(Artist("Emotion Engine", Alb("Vector Unit", "Emotion Engine", 2000, tracks.Take(6).ToList())));
        vm.Artists.Add(Artist("Fifth Generation", Alb("Load Screen", "Fifth Generation", 1996, tracks.Take(3).ToList())));
        vm.Artists.Add(Artist("Grey Chassis", Alb("Moulded", "Grey Chassis", 1997, tracks.Take(5).ToList())));

        vm.SelectedArtist = aphelion;
        vm.SelectedAlbum = vm.Albums.Count > 1 ? vm.Albums[1] : vm.Albums.FirstOrDefault();

        vm.NowPlaying = tracks[2];
        vm.IsPlaying = true;
        vm.DurationSeconds = 332;
        vm.PositionSeconds = 138;
        vm.DurationDisplay = "5:32";
        vm.StatusText = "26,418 tracks in 1,204 albums by 312 artists";
        vm.OutputDescription = "24-bit / 96 kHz - shared";
        vm.IsOutputActive = true;
    }

    // -------------------------------------------------------------- rendering

    /// <summary>
    /// Colour painted behind the window before it is drawn. A Mica or Acrylic
    /// window has a transparent background - the composited backdrop is the
    /// desktop's, not the window's - so without this, Dark and Vista render onto
    /// nothing. Matches WPF-UI's dark ApplicationBackgroundBrush.
    /// </summary>
    private static Color _backdrop = Color.FromRgb(0x20, 0x20, 0x20);

    private static void Save(Visual window, string path, double w, double h, double scale)
    {
        var dpi = 96 * scale;
        var rtb = new RenderTargetBitmap(
            (int)Math.Round(w * scale), (int)Math.Round(h * scale),
            dpi, dpi, PixelFormats.Pbgra32);

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var area = new Rect(0, 0, w, h);
            dc.DrawRectangle(new SolidColorBrush(_backdrop), null, area);
            dc.DrawRectangle(
                new VisualBrush(window)
                {
                    Stretch = Stretch.None,
                    AlignmentX = AlignmentX.Left,
                    AlignmentY = AlignmentY.Top,
                },
                null,
                area);
        }

        rtb.Render(visual);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static IEnumerable<T> FindAll<T>(DependencyObject node) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
        {
            var child = VisualTreeHelper.GetChild(node, i);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var deeper in FindAll<T>(child))
            {
                yield return deeper;
            }
        }
    }

    /// <summary>
    /// Puts the real library into the view model the way a scan would, through
    /// its private ApplyLibrary. Read-only: the cache is loaded, never saved, and
    /// no scan runs, so neither library.json nor the drive is touched.
    /// </summary>
    private static void LoadRealLibrary(MainViewModel vm)
    {
        var cached = AudioFool.Core.Library.LibraryCache.Load();
        var library = AudioFool.Core.Library.LibraryScanner.Build(cached?.Tracks ?? SampleTracks());
        typeof(MainViewModel)
            .GetMethod("ApplyLibrary", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(vm, [library, false]);
        Console.WriteLine($"library: {library.AllTracks.Count:N0} tracks from {(cached is null ? "samples" : "library.json")}");
    }

    private static T? FindFirst<T>(System.Windows.DependencyObject node) where T : System.Windows.DependencyObject
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(node); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(node, i);
            if (child is T match)
            {
                return match;
            }

            if (FindFirst<T>(child) is { } deeper)
            {
                return deeper;
            }
        }

        return null;
    }

    /// <summary>Drains the dispatcher queue so layout, bindings and Loaded run.</summary>
    private static void Pump()
    {
        for (var i = 0; i < 6; i++)
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(
                DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
        }
    }

    /// <summary>
    /// Lets wall-clock time pass so one-shot storyboards land on their final
    /// frame before the bitmap is taken.
    /// </summary>
    private static void Settle(int milliseconds)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(milliseconds);
        while (DateTime.UtcNow < deadline)
        {
            Pump();
            Thread.Sleep(25);
        }

        Pump();
    }
}
