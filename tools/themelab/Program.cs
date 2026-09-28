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
        else if (which == "edit")
        {
            LoadTempLibrary(vm);
        }
        else
        {
            Populate(vm);
        }

        Settle(300);

        // --lastfm scrobbling|off|failing|reconnect: the status-bar indicator.
        // The view model's own scrobbler reads the real queue file, so it is
        // swapped for one with a scratch queue and a canned HTTP answer.
        if (Arg(args, "--lastfm") is { } lfmState)
        {
            var answer = lfmState switch
            {
                "failing" => (System.Net.HttpStatusCode.ServiceUnavailable, """{"error":11,"message":"Service offline."}"""),
                "reconnect" => (System.Net.HttpStatusCode.Forbidden, """{"error":9,"message":"Invalid session key - Please re-authenticate"}"""),
                _ => (System.Net.HttpStatusCode.OK, "{}"),
            };
            var http = new System.Net.Http.HttpClient(new CannedHandler(answer));
            var queue = AudioFool.Core.Scrobbling.ScrobbleQueue.Load(
                Path.Combine(Path.GetTempPath(), "ThemeLabLastFm", Guid.NewGuid().ToString("N"), "q.json"), DateTimeOffset.UtcNow);
            if (lfmState is "failing" or "reconnect")
                for (var i = 0; i < 3; i++)
                    queue.Add(new AudioFool.Core.Scrobbling.ScrobbleEntry
                    {
                        Artist = "Rush", Track = $"Track {i}", Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    });
            var swapped = new AudioFool.Core.Scrobbling.LastFmScrobbler(queue, null,
                (k, s) => new AudioFool.Core.Scrobbling.LastFmApi(k, s, http));

            var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
            typeof(MainViewModel).GetField("_scrobbler", flags)!.SetValue(vm, swapped);
            var refresh = typeof(MainViewModel).GetMethod("RefreshLastFmIndicator", flags)!;

            swapped.Connect("key", "secret", new AudioFool.Core.Scrobbling.LastFmSession("marcusrenlund", "sk"));
            if (lfmState == "off")
                swapped.Enabled = false;
            var until = DateTime.UtcNow.AddSeconds(3);
            while (DateTime.UtcNow < until && queue.Count > 0 && swapped.LastError is null)
                Settle(50);

            refresh.Invoke(vm, null);
            Settle(200);
            Console.WriteLine($"lastfm indicator: state={vm.LastFmState} shown={vm.IsLastFmShown} "
                + $"label='{vm.LastFmLabel}' tooltip='{vm.LastFmToolTip}'");
        }

        if (switching)
        {
            ThemeService.Apply(theme);
            Settle(300);
        }

        if (main.FindName("TrackGrid") is System.Windows.Controls.DataGrid grid)
        {
            grid.SelectedIndex = int.Parse(Arg(args, "--row") ?? "4", CultureInfo.InvariantCulture);

            // --rows "1,2,3" selects several rows, as Ctrl-click would, and prints
            // what the view model saw of it.
            if (Arg(args, "--rows") is { } rows)
            {
                grid.SelectedItems.Clear();
                foreach (var index in rows.Split(',').Select(int.Parse))
                    grid.SelectedItems.Add(grid.Items[index]);
                Settle(100);
                Console.WriteLine($"grid selected {grid.SelectedItems.Count}; view model SelectedTracks {vm.SelectedTracks.Count}: "
                    + string.Join(", ", vm.SelectedTracks.Select(t => t.Title)));
            }
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

        if (which == "edit")
        {
            // Each save persists the library, and LibraryCache.CachePath is the
            // real library.json - so it is put back byte for byte afterwards.
            var cachePath = AudioFool.Core.Library.LibraryCache.CachePath;
            var backup = cachePath + ".themelab-bak";
            var hadCache = File.Exists(cachePath);
            if (hadCache)
                File.Copy(cachePath, backup, overwrite: true);
            try
            {
                RunInlineEdit(main, vm, outPath, w, h, scale);
                Settle(500);
            }
            finally
            {
                if (hadCache)
                {
                    File.Copy(backup, cachePath, overwrite: true);
                    File.Delete(backup);
                }
                else if (File.Exists(cachePath))
                {
                    File.Delete(cachePath);
                }
                Console.WriteLine($"library.json restored ({(hadCache ? new FileInfo(cachePath).Length.ToString("N0") + " B" : "none before")})");
                if (_scratchDir is not null)
                    Directory.Delete(_scratchDir, recursive: true);
            }
            return 0;
        }

        if (which == "artview")
        {
            // --window artview --album "<title>": the art viewer's caption for a
            // real album, through the same full-size load the app uses. --artfile
            // <image> instead views a lone folder cover, e.g. one over the 2,000 px
            // decode cap. Never shown; only the caption is printed.
            var service = new AlbumArtService();
            AudioFool.Core.Models.Album album;
            if (Arg(args, "--artfile") is { } artFile)
            {
                album = new AudioFool.Core.Models.Album { Title = "Scratch", ArtistName = "ThemeLab", FolderArtPath = artFile };
            }
            else
            {
                var library = AudioFool.Core.Library.LibraryScanner.Build(AudioFool.Core.Library.LibraryCache.Load()?.Tracks ?? SampleTracks());
                var albumName = Arg(args, "--album") ?? "Saturn Return";
                album = library.Artists.SelectMany(a => a.Albums)
                    .First(a => string.Equals(a.Title, albumName, StringComparison.OrdinalIgnoreCase));
            }

            var art = service.GetFullAlbumArtAsync(album).GetAwaiter().GetResult();
            if (art is null)
            {
                Console.WriteLine("artview: no art");
                return 1;
            }

            var viewer = new ArtWindow(art, $"{album.ArtistName} - {album.Title}", main);
            Console.WriteLine($"artview: bitmap {art.PixelWidth}x{art.PixelHeight}  caption '{((System.Windows.Controls.TextBlock)viewer.FindName("Caption")).Text}'");
            viewer.Close();
            return 0;
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
                    // --pick "1-9" opens the dialog for those tracks of the album
                    // (1-based, in grid order) as if they were selected in the grid.
                    if (Arg(args, "--pick") is { } pick)
                    {
                        var bounds = pick.Split('-');
                        var (from, to) = (int.Parse(bounds[0]), int.Parse(bounds[^1]));
                        var picked = album.Tracks.Skip(from - 1).Take(to - from + 1).ToList();
                        editVm = new TagEditViewModel(picked);
                        Console.WriteLine($"selection: {picked.Count} of {album.Title}'s {album.Tracks.Count} tracks, opened in {clock.ElapsedMilliseconds} ms");
                        foreach (var t in picked)
                            Console.WriteLine($"    {t.DiscNumber}/{t.DiscCount} #{t.TrackNumber}/{t.TrackCount}  {t.Title}");
                        Console.WriteLine($"  artist    '{editVm.Artist}' [{editVm.ArtistPlaceholder}]  album artist '{editVm.AlbumArtist}' [{editVm.AlbumArtistPlaceholder}]");
                        Console.WriteLine($"  album     '{editVm.AlbumTitle}' [{editVm.AlbumTitlePlaceholder}]  year '{editVm.Year}' [{editVm.YearPlaceholder}]  art shown={editVm.ShowsArt}");
                    }
                    else
                    {
                        // The cover's size and format load after an await; without a
                        // context the continuation would run on the thread pool.
                        SynchronizationContext.SetSynchronizationContext(
                            new System.Windows.Threading.DispatcherSynchronizationContext(System.Windows.Threading.Dispatcher.CurrentDispatcher));
                        editVm = new TagEditViewModel(album, new AlbumArtService());
                        Console.WriteLine($"album: {album.Title} ({album.Tracks.Count} tracks), opened in {clock.ElapsedMilliseconds} ms");
                        Settle(1500);
                        Console.WriteLine($"  art       '{editVm.ArtSizeText}' '{editVm.ArtFormatText}' [{editVm.ArtSourceText}]  preview={editVm.ArtPreview?.PixelWidth}px");

                        // --useart <file>: hand the dialog a new cover as Search Internet does.
                        if (Arg(args, "--useart") is { } artFile)
                        {
                            editVm.UseDownloadedArt(File.ReadAllBytes(artFile));
                            Console.WriteLine($"  new art   '{editVm.ArtSizeText}' '{editVm.ArtFormatText}' [{editVm.ArtSourceText}]");
                        }
                    }
                }
                else
                {
                    var track = library.AllTracks
                        .First(t => string.Equals(t.Title, trackName, StringComparison.OrdinalIgnoreCase));
                    editVm = new TagEditViewModel(track);
                    Console.WriteLine($"track: {track.FilePath}, opened in {clock.ElapsedMilliseconds} ms");
                }

                Console.WriteLine($"  counts    '{editVm.TrackCount}' [{editVm.TrackCountPlaceholder}]  disc '{editVm.DiscNumber}' [{editVm.DiscNumberPlaceholder}] of '{editVm.DiscCount}' [{editVm.DiscCountPlaceholder}]");
                if (editVm.ShowsRemoveLeadingZeros)
                    Console.WriteLine($"  zeros     '{editVm.LeadingZerosText}' enabled={editVm.RemoveLeadingZerosCommand.CanExecute(null)}");
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
                        // "^Zeros" presses Remove Leading Zeros.
                        if (pair == "^Zeros")
                        {
                            Console.WriteLine($"  zeros button enabled={editVm.RemoveLeadingZerosCommand.CanExecute(null)}");
                            editVm.RemoveLeadingZerosCommand.Execute(null);
                            Console.WriteLine($"  after     '{editVm.LeadingZerosText}' enabled={editVm.RemoveLeadingZerosCommand.CanExecute(null)}");
                            continue;
                        }

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
                var details = editVm.IsSelectionMode ? editVm.BuildTracksEdit().Details
                    : albumName is not null ? editVm.BuildAlbumEdit().Details
                    : editVm.BuildTrackEdit().Details;
                if (editVm.IsSelectionMode)
                {
                    var tracksEdit = editVm.BuildTracksEdit();
                    var date = tracksEdit.Date is { } d ? $"'{d.Year}' '{d.Date}'" : "(keep)";
                    Console.WriteLine($"save: artist {Q(tracksEdit.Artist)} album artist {Q(tracksEdit.AlbumArtist)} album {Q(tracksEdit.Album)} year {date}");
                    Console.WriteLine($"save: tracks {N(tracksEdit.TrackCount)} disc {N(tracksEdit.DiscNumber)} of {N(tracksEdit.DiscCount)}");
                }
                else if (albumName is not null)
                {
                    var albumEdit = editVm.BuildAlbumEdit();
                    Console.WriteLine($"save: tracks {N(albumEdit.TrackCount)} disc {N(albumEdit.DiscNumber)} of {N(albumEdit.DiscCount)}  remove zeros={albumEdit.RemoveLeadingZeros}");
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
        else if (which == "artsearch")
        {
            // --window artsearch --artist "Rush" --album "Moving Pictures": a live
            // search against the real sources, rendered once every result and
            // preview is in. --use 1 then downloads the first cover and passes it
            // to an album tag dialog the way Use Image does, printing the payload
            // Save would write. Nothing is saved.
            var artist = Arg(args, "--artist") ?? "Rush";
            var albumName = Arg(args, "--album") ?? "Moving Pictures";
            var searchVm = new ArtSearchViewModel(new AudioFool.Core.Art.OnlineArtSearch(null), artist, albumName);
            var dialog = new ArtSearchWindow(searchVm, main);
            dialog.ApplyTemplate();
            var root = (FrameworkElement)dialog.Content;
            root.Measure(new Size(w, h));
            root.Arrange(new Rect(0, 0, w, h));

            var clock = System.Diagnostics.Stopwatch.StartNew();
            var search = searchVm.SearchAsync();
            while (!search.IsCompleted) Settle(100);
            var searched = clock.ElapsedMilliseconds;
            while (searchVm.Results.Any(r => r.Preview is null) && clock.ElapsedMilliseconds < searched + 15000) Settle(100);
            Console.WriteLine($"artsearch: {searchVm.Results.Count} results in {searched} ms, previews by {clock.ElapsedMilliseconds} ms");
            Console.WriteLine($"  status: {searchVm.Status}  busy={searchVm.IsBusy}");
            if (search.Exception is { } failed)
                Console.WriteLine($"  FAULTED: {failed.InnerException}");
            foreach (var r in searchVm.Results)
                Console.WriteLine($"  r{r.Candidate.Relevance} {r.SizeText,-13} {r.Source,-18} preview={(r.Preview is null ? "none" : $"{r.Preview.PixelWidth}px")}  {r.Title}");

            searchVm.Selected = searchVm.Results.FirstOrDefault();
            if (Arg(args, "--use") is not null && searchVm.Selected is not null)
            {
                var download = searchVm.DownloadSelectedAsync();
                while (!download.IsCompleted) Settle(100);
                Console.WriteLine($"use: ok={download.Result} bytes={searchVm.ChosenBytes?.Length:N0} status='{searchVm.Status}'");

                if (searchVm.ChosenBytes is { } bytes)
                {
                    var sampleAlbum = AudioFool.Core.Library.LibraryScanner.Build(SampleTracks()).Artists[0].Albums[0];
                    var editVm = new TagEditViewModel(sampleAlbum, new AlbumArtService());
                    editVm.UseDownloadedArt(bytes);
                    var payload = editVm.PickedArtPayload();
                    var probe = AudioFool.Core.Art.JpegSize.TryRead(payload!.Bytes, out var pw, out var ph);
                    Console.WriteLine($"  dialog: preview={editVm.ArtPreview?.PixelWidth}px payload={payload.MimeType} {payload.Bytes.Length:N0} B {probe} {pw}x{ph}");
                }
            }

            root.UpdateLayout();
            Settle(400);
            Save(root, outPath, w, h, scale);
        }
        else if (which == "lastfm")
        {
            // --window lastfm --state setup|waiting|connected|failing|rejected.
            // A scratch queue and a fake HTTP handler: nothing reaches Last.fm,
            // and the throwaway settings above are never saved.
            var state = Arg(args, "--state") ?? "setup";
            var queuePath = Path.Combine(Path.GetTempPath(), "ThemeLabLastFm", Guid.NewGuid().ToString("N"), "scrobbles.json");
            var queue = AudioFool.Core.Scrobbling.ScrobbleQueue.Load(queuePath, DateTimeOffset.UtcNow);
            var answer = state switch
            {
                "failing" => (System.Net.HttpStatusCode.ServiceUnavailable, """{"error":16,"message":"The service is temporarily unavailable, please try again."}"""),
                "rejected" => (System.Net.HttpStatusCode.Forbidden, """{"error":9,"message":"Invalid session key - Please re-authenticate"}"""),
                _ => (System.Net.HttpStatusCode.OK, "{}"),
            };
            var http = new System.Net.Http.HttpClient(new CannedHandler(answer));
            var scrobbler = new AudioFool.Core.Scrobbling.LastFmScrobbler(queue, null,
                (k, s) => new AudioFool.Core.Scrobbling.LastFmApi(k, s, http));

            if (state is "failing" or "rejected")
                for (var i = 0; i < 3; i++)
                    queue.Add(new AudioFool.Core.Scrobbling.ScrobbleEntry
                    {
                        Artist = "Rush", Track = $"Track {i}", Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    });

            if (state is not ("setup" or "waiting"))
            {
                settings.LastFmApiKey = "0123456789abcdef0123456789abcdef";
                settings.LastFmApiSecret = "fedcba9876543210fedcba9876543210";
                scrobbler.Connect(settings.LastFmApiKey, settings.LastFmApiSecret,
                    new AudioFool.Core.Scrobbling.LastFmSession("marcusrenlund", "sk"));
                var until = DateTime.UtcNow.AddSeconds(3);
                while (DateTime.UtcNow < until && scrobbler.Pending > 0 && scrobbler.LastError is null)
                    Settle(50);
            }

            var lfmVm = new LastFmViewModel(scrobbler, settings);
            if (state == "waiting")
            {
                lfmVm.ApiKey = "0123456789abcdef0123456789abcdef";
                lfmVm.ApiSecret = "fedcba9876543210fedcba9876543210";
                lfmVm.IsWaitingForApproval = true;
            }

            Console.WriteLine($"lastfm {state}: setup={lfmVm.ShowsSetup} connected={lfmVm.ShowsConnected} "
                + $"pending={scrobbler.Pending} reconnect={scrobbler.NeedsReconnect} canConnect={lfmVm.ConnectCommand.CanExecute(null)}");
            Console.WriteLine($"  {lfmVm.ConnectedAs} | {lfmVm.QueueStatus} | error={lfmVm.Error}");

            var dialog = new LastFmWindow(lfmVm, main);
            dialog.ApplyTemplate();
            var root = (FrameworkElement)dialog.Content;
            root.Measure(new Size(w, double.PositiveInfinity));
            var height = Math.Ceiling(root.DesiredSize.Height);
            root.Arrange(new Rect(0, 0, w, height));
            root.UpdateLayout();
            Settle(400);
            Save(root, outPath, w, height, scale);
            Console.WriteLine($"  size {w}x{height}");
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

    /// <summary>
    /// --window edit: a library of four scratch copies of the test fixtures, so
    /// an in-place edit writes real files without going near the music drive.
    /// A save still rewrites library.json (LibraryCache.CachePath is fixed), so
    /// Main backs it up first and restores it afterwards, then deletes the copies.
    /// </summary>
    private static string? _scratchDir;

    private static void LoadTempLibrary(MainViewModel vm)
    {
        var fixtures = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            @"..\..\..\..\..\..\tests\AudioFool.Core.Tests\TestData"));
        var dir = Path.Combine(Path.GetTempPath(), "themelab-edit-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        _scratchDir = dir;

        string[] titles = ["One", "Two", "Three", "Four"];
        var tracks = new List<Track>();
        for (var i = 0; i < titles.Length; i++)
        {
            var ext = i == 3 ? ".mp3" : ".flac";
            var path = Path.Combine(dir, $"0{i + 1} {titles[i]}{ext}");
            File.Copy(Path.Combine(fixtures, "sample" + ext), path);
            var seeded = AudioFool.Core.Library.TagWriter.WriteTrackTags(
                AudioFool.Core.Library.TagReader.Read(path),
                new AudioFool.Core.Library.TrackTagEdit(titles[i], "Lab Artist", "Lab Artist", "Lab Album", 2020, i + 1, 4, 1, 1),
                art: null, folderArtPath: null);
            tracks.Add(seeded.UpdatedTrack!);
        }

        typeof(MainViewModel)
            .GetMethod("ApplyLibrary", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(vm, [AudioFool.Core.Library.LibraryScanner.Build(tracks), false]);
        Console.WriteLine($"library: {tracks.Count} scratch tracks in {dir}");
    }

    /// <summary>
    /// Drives an in-place edit through the real grid: which gestures may open
    /// one, what the edit box looks like, and what reaches the file.
    /// </summary>
    private static void RunInlineEdit(MainWindow main, MainViewModel vm, string outPath, double w, double h, double scale)
    {
        // As in the real app: without it, the save's code after await runs on the
        // thread pool and cannot touch the track list.
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));

        var grid = (System.Windows.Controls.DataGrid)main.FindName("TrackGrid");
        System.Windows.Controls.DataGridColumn Column(string name) => (System.Windows.Controls.DataGridColumn)main.FindName(name);
        Track Row(string title) => vm.Tracks.First(t => t.Title == title);
        string Rows() => string.Join(" | ", vm.Tracks.Select(t => $"{t.TrackNumber} {t.DisplayTitle} / {t.Artist} / {t.Album}"));
        void Log(string s) => Console.WriteLine(s);

        System.Windows.Controls.DataGridCell Cell(Track track, string column)
        {
            grid.ScrollIntoView(track);
            grid.UpdateLayout();
            var row = (System.Windows.Controls.DataGridRow)grid.ItemContainerGenerator.ContainerFromItem(track);
            return (System.Windows.Controls.DataGridCell)Column(column).GetCellContent(row)!.Parent;
        }

        void Select(Track track, string column)
        {
            grid.SelectedItems.Clear();
            grid.SelectedItem = track;
            grid.CurrentCell = new System.Windows.Controls.DataGridCellInfo(track, Column(column));
            System.Windows.Input.Keyboard.Focus(Cell(track, column));
            Settle(100);
        }

        bool Press(UIElement target, System.Windows.Input.Key key)
        {
            var source = PresentationSource.FromVisual(target);
            var args = new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, source, 0, key)
            { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent };
            target.RaiseEvent(args);
            if (!args.Handled)
            {
                args = new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, source, 0, key)
                { RoutedEvent = System.Windows.Input.Keyboard.KeyDownEvent };
                target.RaiseEvent(args);
            }
            Settle(100);
            return args.Handled;
        }

        System.Windows.Controls.TextBox? Box(Track track, string column) =>
            FindFirst<System.Windows.Controls.TextBox>(Cell(track, column));

        void AwaitSave(string before)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while ((vm.StatusText == before || vm.StatusText.StartsWith("Saving")) && clock.ElapsedMilliseconds < 5000)
                Settle(50);
            Settle(300);
        }

        string FileTags(string path)
        {
            var t = AudioFool.Core.Library.TagReader.Read(path);
            return $"file: #{t.TrackNumber}/{t.TrackCount} '{t.Title}' / '{t.Artist}' / '{t.AlbumArtist}' / '{t.Album}' disc {t.DiscNumber}/{t.DiscCount} year {t.Year}";
        }

        Settle(300);
        Log($"rows: {Rows()}");
        grid.BeginningEdit += (_, e) => Log($"  [BeginningEdit {e.Column.Header} via {e.EditingEventArgs?.GetType().Name} "
            + $"key={(e.EditingEventArgs as System.Windows.Input.KeyEventArgs)?.Key} cancel={e.Cancel} selected={grid.SelectedItems.Count}]");

        // 1. Only F2 and the slow click may open an edit.
        var two = Row("Two");
        Select(two, "SongColumn");
        Log($"gate: grid focused={grid.IsKeyboardFocusWithin} grid.IsReadOnly={grid.IsReadOnly} song col ro={Column("SongColumn").IsReadOnly} "
            + $"cell ro={Cell(two, "SongColumn").IsReadOnly} current={grid.CurrentCell.Column?.Header}/{(grid.CurrentCell.Item as Track)?.Title} "
            + $"view={grid.Items.GetType().Name} canEdit={(grid.Items as System.ComponentModel.IEditableCollectionView)?.IsEditingItem}");
        grid.BeginEdit(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Left));
        Log($"gate: grid's own click on a selected cell opens an edit? {Cell(two, "SongColumn").IsEditing}");
        grid.BeginEdit(new System.Windows.Input.TextCompositionEventArgs(System.Windows.Input.Keyboard.PrimaryDevice,
            new System.Windows.Input.TextComposition(System.Windows.Input.InputManager.Current, grid, "x")));
        Log($"gate: typing opens an edit? {Cell(two, "SongColumn").IsEditing}");
        Select(two, "TimeColumn");
        Press(Cell(two, "TimeColumn"), System.Windows.Input.Key.F2);
        Log($"gate: F2 on Time (read-only) opens an edit? {Cell(two, "TimeColumn").IsEditing}");
        grid.SelectedItems.Add(Row("Three"));
        grid.CurrentCell = new System.Windows.Controls.DataGridCellInfo(two, Column("SongColumn"));
        Press(Cell(two, "SongColumn"), System.Windows.Input.Key.F2);
        Log($"gate: F2 with two rows selected opens an edit? {Cell(two, "SongColumn").IsEditing}");

        // 2. F2 on Song, rendered open.
        Select(two, "SongColumn");
        Press(Cell(two, "SongColumn"), System.Windows.Input.Key.F2);
        var box = Box(two, "SongColumn");
        Log($"song: F2 opens={Cell(two, "SongColumn").IsEditing} box='{box?.Text}' selected={box?.SelectionLength} focused={box?.IsKeyboardFocused} "
            + $"box {box?.ActualWidth:0}x{box?.ActualHeight:0} in row {grid.RowHeight:0} style={(box?.Style == main.TryFindResource("AfCellEditBox") ? "AfCellEditBox" : "other")}");
        main.UpdateLayout();
        Save(main, outPath, w, h, scale);

        // 3. Enter saves, and does not play the track.
        box!.Text = "  Two (renamed) ";
        var before = vm.StatusText;
        Press(box, System.Windows.Input.Key.Enter);
        AwaitSave(before);
        Log($"song: Enter -> status='{vm.StatusText}' playing={vm.NowPlaying?.Title ?? "nothing"}");
        Log("song: " + FileTags(two.FilePath));
        Log($"rows: {Rows()}");

        // 4. Track #: a bad number is refused, a good one saved.
        var renamed = Row("Two (renamed)");
        Select(renamed, "TrackNumberColumn");
        Press(Cell(renamed, "TrackNumberColumn"), System.Windows.Input.Key.F2);
        Box(renamed, "TrackNumberColumn")!.Text = "abc";
        Press(Box(renamed, "TrackNumberColumn")!, System.Windows.Input.Key.Enter);
        Settle(200);
        Log($"number: 'abc' -> status='{vm.StatusText}'");
        Log("number: " + FileTags(renamed.FilePath));
        renamed = Row("Two (renamed)");
        Select(renamed, "TrackNumberColumn");
        Press(Cell(renamed, "TrackNumberColumn"), System.Windows.Input.Key.F2);
        Box(renamed, "TrackNumberColumn")!.Text = "9";
        before = vm.StatusText;
        Press(Box(renamed, "TrackNumberColumn")!, System.Windows.Input.Key.Enter);
        AwaitSave(before);
        Log($"number: '9' -> status='{vm.StatusText}'");
        Log("number: " + FileTags(renamed.FilePath));
        Log($"rows: {Rows()}");

        // 5. The slow click opens Artist; moving focus to the search box saves it.
        var three = Row("Three");
        Select(three, "ArtistColumn");
        typeof(MainWindow).GetMethod("ArmSlowClick", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(main, [Cell(three, "ArtistColumn")]);
        Log($"slow click: editing at once? {Cell(three, "ArtistColumn").IsEditing}");
        Settle(900);
        Log($"slow click: editing after the double-click time? {Cell(three, "ArtistColumn").IsEditing}");
        Box(three, "ArtistColumn")!.Text = "Guest";
        before = vm.StatusText;
        System.Windows.Input.Keyboard.Focus((System.Windows.IInputElement)main.FindName("SearchBox"));
        AwaitSave(before);
        Log($"focus away: status='{vm.StatusText}'");
        Log("focus away: " + FileTags(three.FilePath));

        // 6. Album: emptying is refused; a rebuild mid-edit cancels cleanly;
        //    a real change moves the track to its new album.
        var four = Row("Four");
        Select(four, "AlbumColumn");
        Press(Cell(four, "AlbumColumn"), System.Windows.Input.Key.F2);
        Box(four, "AlbumColumn")!.Text = " ";
        Press(Box(four, "AlbumColumn")!, System.Windows.Input.Key.Enter);
        Settle(200);
        Log($"album: ' ' -> status='{vm.StatusText}'");

        four = Row("Four");
        Select(four, "AlbumColumn");
        Press(Cell(four, "AlbumColumn"), System.Windows.Input.Key.F2);
        Box(four, "AlbumColumn")!.Text = "Half typed";
        try
        {
            typeof(MainViewModel)
                .GetMethod("ReplaceTracksInLibrary", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(vm, [new Dictionary<string, Track>()]);
            Settle(200);
            Log($"album: rebuild mid-edit ok; still editing={grid.CurrentCell.Column is not null && Cell(Row("Four"), "AlbumColumn").IsEditing}");
        }
        catch (Exception ex)
        {
            Log($"album: rebuild mid-edit THREW {ex.InnerException?.GetType().Name}: {ex.InnerException?.Message}");
        }
        Log("album: " + FileTags(four.FilePath));

        four = Row("Four");
        Select(four, "AlbumColumn");
        Press(Cell(four, "AlbumColumn"), System.Windows.Input.Key.F2);
        Box(four, "AlbumColumn")!.Text = "Other Album";
        before = vm.StatusText;
        Press(Box(four, "AlbumColumn")!, System.Windows.Input.Key.Enter);
        AwaitSave(before);
        Log($"album: 'Other Album' -> status='{vm.StatusText}'");
        Log("album: " + FileTags(four.FilePath));
        Log($"albums now: {string.Join(" | ", vm.Albums.Select(a => $"{a.Album.Title} ({a.Album.Tracks.Count})"))}");
        Log($"rows: {Rows()}");
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

/// <summary>Answers every request with the same status and body, for --window lastfm.</summary>
internal sealed class CannedHandler((System.Net.HttpStatusCode Status, string Body) answer) : System.Net.Http.HttpMessageHandler
{
    protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, CancellationToken ct) =>
        Task.FromResult(new System.Net.Http.HttpResponseMessage(answer.Status) { Content = new System.Net.Http.StringContent(answer.Body) });
}
