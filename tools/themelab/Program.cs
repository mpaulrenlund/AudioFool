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
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [STAThread]
    private static int Main(string[] args)
    {
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
            // After Apply, so the dump shows what the theme and
            // ApplicationAccentColorManager actually leave in place.
            ThemeService.Apply();
            Dump(dumpPath);
            return 0;
        }

        // --window placement: WindowPlacement's round trip on a window that gets
        // a handle but is never shown, so nothing appears on the desktop.
        if (which == "placement")
        {
            var areas = AudioFool.Services.WindowPlacement.WorkAreas();
            Console.WriteLine($"work areas: {string.Join(" | ", areas.Select(a => $"({a.Left},{a.Top})-({a.Right},{a.Bottom})"))}");

            var primary = areas.First(a => a.Left <= 0 && a.Top <= 0 && a.Right > 0 && a.Bottom > 0);
            AudioFool.Core.Settings.WindowBounds[] cases =
            [
                new(primary.Left + 137, primary.Top + 91, 1203, 707, false),
                new(primary.Right - 300, primary.Top + 50, 1100, 650, false),
                new(primary.Left + 60, primary.Top + 40, 1300, 760, true),
                new(primary.Right + 50_000, primary.Top + 40, 1300, 760, false),
                // One on every monitor: a different scaling rescales the window on arrival.
                .. areas.Select(a => new AudioFool.Core.Settings.WindowBounds(a.Left + 101, a.Top + 67, 1250, 720, false)),
            ];

            foreach (var wanted in cases)
            {
                var probe = new Window { ShowActivated = false, ShowInTaskbar = false, Width = 400, Height = 300, Left = 0, Top = 0 };
                var hwnd = new System.Windows.Interop.WindowInteropHelper(probe).EnsureHandle();
                var applied = AudioFool.Services.WindowPlacement.Apply(probe, wanted);
                var got = AudioFool.Services.WindowPlacement.Capture(probe);
                var visible = IsWindowVisible(hwnd);
                var ok = applied
                    ? got is not null && got.Left == wanted.Left && got.Top == wanted.Top && got.Width == wanted.Width
                      && got.Height == wanted.Height && probe.WindowState == (wanted.Maximized ? WindowState.Maximized : WindowState.Normal)
                    : !wanted.IsReachableOn(areas);
                Console.WriteLine($"{wanted} -> applied={applied} got=({got?.Left},{got?.Top} {got?.Width}x{got?.Height}) "
                    + $"state={probe.WindowState} shown={visible} {(ok && !visible ? "OK" : "WRONG")}");
                probe.WindowState = WindowState.Normal;
                probe.Close();
            }

            return 0;
        }

        // --window folders: the music-folder list against the real library cache and
        // whatever drives are plugged in. Merging duplicates, a drive-letter move onto
        // a folder that is already listed, the all-unticked status, and Remove. The
        // view model saves settings and library.json, so both are copied aside first
        // and put back byte for byte. No window is built.
        if (which == "folders")
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            ThemeService.Apply();
            var cachePath = AudioFool.Core.Library.LibraryCache.CachePath;
            var saved = new Dictionary<string, byte[]?>
            {
                [AppSettings.SettingsPath] = File.Exists(AppSettings.SettingsPath) ? File.ReadAllBytes(AppSettings.SettingsPath) : null,
                [cachePath] = File.Exists(cachePath) ? File.ReadAllBytes(cachePath) : null,
            };
            var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
            var folderRuntime = new BassRuntime();
            var folderEngine = new AudioEngine(folderRuntime);

            MainViewModel Make(List<string> folders, List<string> disabled) =>
                new(folderEngine, folderRuntime, new AlbumArtService(),
                    new AppSettings { MusicFolders = folders, DisabledFolders = disabled, ScanOnStartup = false, GlobalHotkeys = false });
            string Describe(MainViewModel m) =>
                string.Join(" | ", m.FolderFilters.Select(f => $"{f.FolderPath} [{(f.IsEnabled ? "ticked" : "unticked")}]"));
            void Await(Task task)
            {
                while (!task.IsCompleted)
                    Settle(50);
                task.GetAwaiter().GetResult();
            }
            int Tracks(MainViewModel m) =>
                ((AudioFool.Core.Library.MusicLibrary)typeof(MainViewModel).GetField("_library", flags)!.GetValue(m)!).AllTracks.Count;

            try
            {
                var merged = Make([@"E:\Music", @"e:\music\", @"E:\Music"], [@"E:\Music"]);
                Console.WriteLine($"merge (one copy ticked): {Describe(merged)}");
                var allOff = Make([@"E:\Music", @"E:\Music"], [@"E:\Music"]);
                Console.WriteLine($"merge (no copy ticked): {Describe(allOff)}");

                // The user's case: D:\Music from the start, E:\Music added by hand
                // while the SSD was away, then a start with only the card in.
                var vmFolders = Make([@"D:\Music", @"E:\Music"], []);
                var clock = System.Diagnostics.Stopwatch.StartNew();
                Await((Task)typeof(MainViewModel).GetMethod("LoadLibraryAsync", flags)!.Invoke(vmFolders, null)!);
                Console.WriteLine($"after start ({clock.Elapsed.TotalSeconds:0.0} s): folders={string.Join(", ", vmFolders.MusicFolders)} "
                    + $"filters={Describe(vmFolders)} tracks={Tracks(vmFolders):N0}");
                Console.WriteLine($"  status: {vmFolders.StatusText}");

                foreach (var f in vmFolders.FolderFilters)
                    f.IsEnabled = false;
                Settle(200);
                Console.WriteLine($"all unticked: status: {vmFolders.StatusText}");
                foreach (var f in vmFolders.FolderFilters)
                    f.IsEnabled = true;
                Settle(200);
                Console.WriteLine($"ticked again: status: {vmFolders.StatusText}");

                var toRemove = vmFolders.FolderFilters.First();
                Await(vmFolders.RemoveFolderCommand.ExecuteAsync(toRemove));
                Console.WriteLine($"after remove: folders=[{string.Join(", ", vmFolders.MusicFolders)}] filters=[{Describe(vmFolders)}] tracks={Tracks(vmFolders):N0}");
                Console.WriteLine($"  status: {vmFolders.StatusText}");
                var onDisk = AudioFool.Core.Library.LibraryCache.Load();
                Console.WriteLine($"  cache written: folders=[{string.Join(", ", onDisk?.Folders ?? [])}] tracks={onDisk?.Tracks.Count}");
            }
            finally
            {
                foreach (var (path, bytes) in saved)
                {
                    if (bytes is not null)
                        File.WriteAllBytes(path, bytes);
                    else if (File.Exists(path))
                        File.Delete(path);
                }
            }

            return 0;
        }

        // A throwaway settings object: never Load()ed and never Save()d, so the
        // real settings.json is untouched. No music folders means InitialiseAsync
        // short-circuits before any scan.
        var settings = new AppSettings
        {
            ScanOnStartup = false,
            GlobalHotkeys = false,
        };

        // --window queue plays for real, and the engine posts its events to the
        // context it is built on - as the app's does - so it needs one first.
        if (which is "queue" or "clicks" or "seek")
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));

        var runtime = new BassRuntime();
        var engine = new AudioEngine(runtime);
        var vm = new MainViewModel(engine, runtime, new AlbumArtService(), settings);

        // Same order as App.OnStartup: theme first, then the window, because a
        // DynamicResource Style is resolved as the element initialises.
        ThemeService.Apply();

        var main = new MainWindow(vm);
        Application.Current.MainWindow = main;

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

        // --dpi 1.25: lay the window out at that DPI (layout rounding and pixel
        // snapping as on a 125% monitor) and render at it (spec 8). It replaces
        // --scale, which only enlarges the bitmap.
        if (Arg(args, "--dpi") is { } dpiArg)
        {
            var d = double.Parse(dpiArg, CultureInfo.InvariantCulture);
            VisualTreeHelper.SetRootDpi(main, new DpiScale(d, d));
            scale = d;
            _direct = true;
            main.UpdateLayout();
            Console.WriteLine($"dpi {VisualTreeHelper.GetDpi(main).PixelsPerDip}");
        }

        if (which == "click")
        {
            LoadRealLibrary(vm);
        }
        else if (which is "edit" or "queue" or "clicks")
        {
            LoadTempLibrary(vm);
        }
        else if (Arg(args, "--empty") is { } emptyCase)
        {
            // --empty library: nothing loaded at all. --empty search: the real
            // library (read-only) with a search that matches nothing. Either way
            // no album is selected, so the Songs panel shows the empty state.
            if (emptyCase == "search")
            {
                LoadRealLibrary(vm);
                vm.SearchQuery = "zzqxw no such thing";
                Settle(400);
            }
            Console.WriteLine($"empty: '{vm.EmptyStateTitle}' / '{vm.EmptyStateDetail}' album={vm.SelectedAlbum is null}");
        }
        else
        {
            Populate(vm);
        }

        // --artistsort recent: the most recently added artists first, as after a
        // click on the header (the app itself always opens A-Z).
        if (Arg(args, "--artistsort") == "recent")
            vm.ArtistsByRecent = true;

        // --paused 1: the sample track loaded but not playing, so the transport
        // key shows Play rather than Pause.
        if (Arg(args, "--paused") is not null)
            vm.IsPlaying = false;

        // --shuffle 1, --repeat all|one, --exclusive 1, --muted 1: the playback
        // and status bar states (spec 6.7, 6.8). The first three save settings,
        // so the real settings file is put back afterwards.
        if (Arg(args, "--shuffle") is not null || Arg(args, "--repeat") is not null || Arg(args, "--exclusive") is not null)
        {
            var settingsKept = File.Exists(AppSettings.SettingsPath) ? File.ReadAllBytes(AppSettings.SettingsPath) : null;
            try
            {
                if (Arg(args, "--shuffle") is not null)
                    vm.IsShuffle = true;
                if (Arg(args, "--repeat") is { } repeat)
                    for (var i = 0; i < (repeat == "one" ? 2 : 1); i++) vm.CycleRepeatCommand.Execute(null);
                if (Arg(args, "--exclusive") is not null)
                {
                    vm.IsExclusiveOutput = true;
                    vm.OutputDescription = "Exclusive · 44.1 kHz · bit-perfect";
                    vm.IsOutputActive = true;
                }
            }
            finally
            {
                if (settingsKept is not null)
                    File.WriteAllBytes(AppSettings.SettingsPath, settingsKept);
                else if (File.Exists(AppSettings.SettingsPath))
                    File.Delete(AppSettings.SettingsPath);
            }
        }

        // Mute leaves the level alone, so nothing new reaches the settings file.
        if (Arg(args, "--muted") is not null)
            vm.ToggleMuteCommand.Execute(null);

        // --scanning 0.4: the status bar mid-scan, with its progress bar.
        if (Arg(args, "--scanning") is { } fraction)
        {
            vm.IsScanning = true;
            vm.ScanFraction = double.Parse(fraction, CultureInfo.InvariantCulture);
        }

        Settle(300);

        // --window tokens: the central theme, checked from inside MainWindow, plus
        // a specimen sheet. See TokenSheet.
        if (which == "tokens")
        {
            var result = TokenSheet.Run(main, outPath, scale);
            var settingsKept = File.Exists(AppSettings.SettingsPath) ? File.ReadAllBytes(AppSettings.SettingsPath) : null;
            main.Close();
            if (settingsKept is not null)
                File.WriteAllBytes(AppSettings.SettingsPath, settingsKept);
            else if (File.Exists(AppSettings.SettingsPath))
                File.Delete(AppSettings.SettingsPath);
            return result;
        }

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

        // --clickartists 1: click the Artists header through its automation peer
        // and print the order before and after. The toggle saves settings, so the
        // settings file is copied aside first and put back afterwards.
        if (Arg(args, "--clickartists") is not null)
        {
            string Top() => string.Join(" | ", vm.Artists.Take(8).Select(a => $"{a.Name} ({a.LastAddedUtc:yyyy-MM-dd})"));
            var header = (System.Windows.Controls.Button)main.FindName("ArtistSortHeader");
            var settingsBackup = File.Exists(AppSettings.SettingsPath) ? File.ReadAllBytes(AppSettings.SettingsPath) : null;
            var artistList = (System.Windows.Controls.ListBox)main.FindName("ArtistList");
            double Offset() => FindFirst<System.Windows.Controls.ScrollViewer>(artistList)?.VerticalOffset ?? -1;
            try
            {
                // --clickfrom N: select the N-th artist first, as if browsed to.
                if (Arg(args, "--clickfrom") is { } from)
                {
                    vm.SelectedArtist = vm.Artists[int.Parse(from, CultureInfo.InvariantCulture)];
                    Settle(300);
                }

                Console.WriteLine($"artists before: recent={vm.ArtistsByRecent} selected='{vm.SelectedArtist?.Name}' "
                    + $"(#{vm.Artists.IndexOf(vm.SelectedArtist!)}) scroll={Offset():0} tip='{header.ToolTip}'");
                Console.WriteLine($"  {Top()}");
                var peer = new System.Windows.Automation.Peers.ButtonAutomationPeer(header);
                ((System.Windows.Automation.Provider.IInvokeProvider)peer.GetPattern(System.Windows.Automation.Peers.PatternInterface.Invoke)!).Invoke();
                Settle(300);
                Console.WriteLine($"artists after: recent={vm.ArtistsByRecent} selected='{vm.SelectedArtist?.Name}' "
                    + $"(#{vm.Artists.IndexOf(vm.SelectedArtist!)}) scroll={Offset():0} album='{vm.SelectedAlbum?.Album.Title}' tip='{header.ToolTip}'");
                Console.WriteLine($"  {Top()}");
            }
            finally
            {
                if (settingsBackup is not null)
                    File.WriteAllBytes(AppSettings.SettingsPath, settingsBackup);
                else
                    File.Delete(AppSettings.SettingsPath);
            }
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


        // --songprobe 1: measure the album header and song table (spec 6.5, 6.6)
        // and print each realised row's state, colours and cell positions.
        if (Arg(args, "--songprobe") is not null)
        {
            Settle(200);
            PrintSongProbe(main);
        }

        // --playprobe 1: measure the playback and status bars (spec 6.7, 6.8).
        // --albumswidth 400 first widens the Albums panel, as a splitter drag would.
        if (Arg(args, "--playprobe") is not null)
        {
            if (Arg(args, "--albumswidth") is { } albumsWidth)
            {
                var songsColumn = (System.Windows.Controls.ColumnDefinition)main.FindName("SongsColumn");
                var columns = ((System.Windows.Controls.Grid)songsColumn.Parent).ColumnDefinitions;
                columns[2].Width = new GridLength(double.Parse(albumsWidth, CultureInfo.InvariantCulture));
                Settle(200);
            }
            Settle(200);
            PrintPlaybackProbe(main);
        }

        // --albumprobe 1: each realised album title's clamp, as laid out in the list.
        if (Arg(args, "--albumprobe") is not null)
        {
            Settle(200);
            var albums = (System.Windows.Controls.ListBox)main.FindName("AlbumList");
            foreach (var t in Descendants(albums).OfType<System.Windows.Controls.TextBlock>().Where(t => t.Name == "AlbumTitleText"))
            {
                var p = t.TranslatePoint(new Point(0, 0), main);
                Console.WriteLine($"album title '{t.Text}': x {p.X:0.##} y {p.Y:0.##} actual {t.ActualWidth:0.###} x {t.ActualHeight:0.###} "
                    + $"maxHeight={t.MaxHeight:0.###} lineHeight={t.LineHeight:0.###} stacking={t.LineStackingStrategy} wrap={t.TextWrapping} "
                    + $"trim={t.TextTrimming} rounding={t.UseLayoutRounding} dpi={VisualTreeHelper.GetDpi(t).PixelsPerDip}");
            }

            // --clampprobe "35.1,36,44": render the longest title at each MaxHeight.
            if (Arg(args, "--clampprobe") is { } heights)
            {
                var longest = Descendants(albums).OfType<System.Windows.Controls.TextBlock>()
                    .Where(t => t.Name == "AlbumTitleText").OrderByDescending(t => t.Text.Length).First();
                // --clampstack MaxHeight: try the other line-stacking strategy.
                if (Arg(args, "--clampstack") is { } stack)
                    longest.LineStackingStrategy = Enum.Parse<LineStackingStrategy>(stack);
                // --clampround 0: layout rounding off on the title block.
                if (Arg(args, "--clampround") == "0")
                    longest.UseLayoutRounding = false;
                foreach (var mh in heights.Split(',').Select(v => double.Parse(v, CultureInfo.InvariantCulture)))
                {
                    longest.MaxHeight = mh;
                    main.UpdateLayout();
                    Settle(50);
                    Console.WriteLine($"clamp {mh}: actual {longest.ActualHeight:0.###}");
                    Save(main, Path.ChangeExtension(outPath, null) + $"_clamp{mh}.png", w, h, scale);
                }
            }
        }

        // --albumtips 1: raise ToolTipOpening on every realised album row and print
        // whether the full-title tooltip would show (only when the title is trimmed).
        if (Arg(args, "--albumtips") is not null)
            PrintAlbumTips(main);

        if (Arg(args, "--focus") is { } focusName
            && main.FindName(focusName) is System.Windows.IInputElement target)
        {
            System.Windows.Input.Keyboard.Focus(target);
            Console.WriteLine($"focus {focusName}: keyboard={(target as UIElement)?.IsKeyboardFocused}");
        }

        // --winhover Minimize|Maximize|Close: WPF-UI's own hover on one window
        // button (the method its non-client hit test calls), to see the hover
        // colours without a mouse.
        if (Arg(args, "--winhover") is { } hoverName
            && main.FindName("AppTitleBar") is Wpf.Ui.Controls.TitleBar titleBar
            && titleBar.Template.FindName($"PART_{hoverName}Button", titleBar) is Wpf.Ui.Controls.TitleBarButton hoverButton)
        {
            typeof(Wpf.Ui.Controls.TitleBarButton)
                .GetMethod("Hover", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(hoverButton, null);
            Settle(200);
            Console.WriteLine($"winhover {hoverName}: background={hoverButton.Background} icon={hoverButton.RenderButtonsForeground}");
        }

        // --logohover 1: the logo menu item highlighted, as a pointer over it
        // leaves it (IsHighlighted has a private setter), plus its tooltip.
        if (Arg(args, "--logohover") is "1"
            && main.FindName("LogoMenuItem") is System.Windows.Controls.MenuItem logoHoverItem)
        {
            typeof(System.Windows.Controls.MenuItem).GetProperty("IsHighlighted")!
                .SetValue(logoHoverItem, true);
            Settle(200);
            Console.WriteLine($"logohover: highlighted={logoHoverItem.IsHighlighted} tooltip='{logoHoverItem.ToolTip}'");
            // The template's own triggers, and what each brush key resolves to on
            // this item: hover itself (IsMouseOver) can't be forced off-screen.
            foreach (var tb in logoHoverItem.Template.Triggers)
            {
                var cond = tb switch
                {
                    Trigger t => $"{t.Property.Name}={t.Value}",
                    MultiTrigger mt => string.Join(" & ", mt.Conditions.Select(c => $"{c.Property.Name}={c.Value}")),
                    _ => tb.GetType().Name,
                };
                var setters = tb switch { Trigger t => t.Setters, MultiTrigger mt => mt.Setters, _ => null };
                foreach (var st in setters?.OfType<Setter>() ?? [])
                {
                    var val = st.Value is DynamicResourceExtension dr
                        ? $"{{{dr.ResourceKey}}} = {logoHoverItem.TryFindResource(dr.ResourceKey)}"
                        : st.Value?.ToString();
                    Console.WriteLine($"  trigger {cond}: {st.TargetName}.{st.Property.Name} <- {val}");
                }
            }
        }

        // --hitthumb SeekBar,VolumeSlider: hit-test the centre of each slider's
        // handle and print what a press there would land on, innermost first.
        if (Arg(args, "--hitthumb") is { } hitNames)
        {
            main.UpdateLayout();
            foreach (var name in hitNames.Split(','))
            {
                if (main.FindName(name) is not System.Windows.Controls.Slider slider
                    || Descendants(slider).OfType<System.Windows.Controls.Primitives.Thumb>().FirstOrDefault() is not { } thumb)
                {
                    Console.WriteLine($"hitthumb {name}: no slider or thumb");
                    continue;
                }
                var centre = thumb.TranslatePoint(new Point(thumb.ActualWidth / 2, thumb.ActualHeight / 2), main);
                var hit = main.InputHitTest(centre) as DependencyObject;
                var chain = new List<string>();
                for (var node = hit; node is not null && chain.Count < 8; node = VisualTreeHelper.GetParent(node))
                    chain.Add(node is FrameworkElement { Name.Length: > 0 } fe ? $"{node.GetType().Name}#{fe.Name}" : node.GetType().Name);
                var onThumb = hit is not null && (ReferenceEquals(hit, thumb) || ((Visual)hit).IsDescendantOf(thumb));
                Console.WriteLine($"hitthumb {name}: thumb {thumb.ActualWidth}x{thumb.ActualHeight} at {centre.X:0.0},{centre.Y:0.0} enabled={slider.IsEnabled} hitVisible={thumb.IsHitTestVisible} -> {(onThumb ? "THUMB" : "NOT the thumb")}: {string.Join(" < ", chain)}");

                // A drag as the Thumb reports one: started, 40 px left in steps, completed.
                var before = slider.Value;
                thumb.RaiseEvent(new System.Windows.Controls.Primitives.DragStartedEventArgs(0, 0));
                for (var step = 0; step < 4; step++)
                {
                    thumb.RaiseEvent(new System.Windows.Controls.Primitives.DragDeltaEventArgs(-10, 0));
                    Settle(30);
                    Console.WriteLine($"  drag step {step + 1}: value={slider.Value:0.000} vm.Volume={vm.Volume:0.000}");
                }
                thumb.RaiseEvent(new System.Windows.Controls.Primitives.DragCompletedEventArgs(-40, 0, false));
                Settle(300);
                Console.WriteLine($"  drag {name}: {before:0.000} -> {slider.Value:0.000} after completing (vm.Volume={vm.Volume:0.000})");

                // A press on the track, not the handle: the value jumps (the real
                // pointer is right of this off-screen window, so to the maximum) and,
                // with SliderDrag, the handle should now be dragging from there.
                var pressBefore = slider.Value;
                var track = Descendants(slider).OfType<System.Windows.Controls.Primitives.Track>().First();
                Console.WriteLine($"  before press: thumb.IsMouseOver={thumb.IsMouseOver} IsDragging={thumb.IsDragging} moveToPoint={slider.IsMoveToPointEnabled} FromTrack={AudioFool.Theming.SliderDrag.GetFromTrack(slider)}");
                slider.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(
                    System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount, System.Windows.Input.MouseButton.Left)
                { RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent, Source = slider });
                var draggingAfterPress = thumb.IsDragging;
                var jumped = slider.Value;
                thumb.RaiseEvent(new System.Windows.Controls.Primitives.DragDeltaEventArgs(-20, 0));
                var afterMove = slider.Value;
                thumb.CancelDrag();
                Settle(100);
                Console.WriteLine($"  track press {name}: {pressBefore:0.000} -> jumped {jumped:0.000}, dragging={draggingAfterPress}, then 20 px left -> {afterMove:0.000}, now dragging={thumb.IsDragging}");
            }
        }

        // --type "<text>": text in the search box, as if typed, to check where it
        // sits against the placeholder and that the clear button appears.
        if (Arg(args, "--type") is { } typed
            && main.FindName("SearchBox") is System.Windows.Controls.TextBox searchBox)
        {
            searchBox.Text = typed;
            Settle(400);
            Console.WriteLine($"type '{typed}': artists={vm.Artists.Count:N0}");
        }

        // --focus on a list focuses the ListBox, which draws no row ring; this
        // focuses its selected row, the state a click leaves behind.
        if (Arg(args, "--focusrow") is { } rowListName
            && main.FindName(rowListName) is System.Windows.Controls.ListBox rowList)
        {
            main.UpdateLayout();
            if (rowList.ItemContainerGenerator.ContainerFromItem(rowList.SelectedItem) is UIElement row)
            {
                System.Windows.Input.Keyboard.Focus(row);
                Console.WriteLine($"focusrow {rowListName}: keyboard={row.IsKeyboardFocused}");
            }
            else
            {
                Console.WriteLine($"focusrow {rowListName}: no selected row realised");
            }
        }

        // --focusvisual 1, after --focus or --focusrow: draw the focused element's
        // FocusVisualStyle. WPF draws it only after keyboard input, so Keyboard.Focus
        // alone shows nothing; this sets WPF's internal "always show" flag first.
        if (Arg(args, "--focusvisual") is not null)
        {
            Settle(100);
            const System.Reflection.BindingFlags hidden = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
            var nav = typeof(System.Windows.Input.KeyboardNavigation);
            nav.GetProperty("AlwaysShowFocusVisual", hidden)!.SetValue(null, true);
            nav.GetMethod("ShowFocusVisual", hidden, Type.EmptyTypes)!.Invoke(null, null);
            var fe = System.Windows.Input.Keyboard.FocusedElement as FrameworkElement;
            Console.WriteLine($"focusvisual: {fe?.GetType().Name} '{fe?.Name}' style={(fe?.FocusVisualStyle is null ? "none" : "set")}");
        }

        // --tabwalk 20 [--tabfrom SearchBox] [--keys "Tab,Tab,Down"]: real key presses
        // through InputManager, as the keyboard delivers them, so WPF's own Tab
        // handling runs (raising KeyDown on an element skips it). Prints where focus
        // goes after each. Shift can't be faked: WPF reads the real keyboard's state.
        if (Arg(args, "--tabwalk") is { } walkCount)
        {
            main.UpdateLayout();
            System.Windows.Input.Keyboard.Focus((IInputElement)main.FindName(Arg(args, "--tabfrom") ?? "SearchBox"));
            Settle(50);
            var keys = Arg(args, "--keys") is { } list
                ? list.Split(',').Select(Enum.Parse<System.Windows.Input.Key>).ToList()
                : Enumerable.Repeat(System.Windows.Input.Key.Tab, int.Parse(walkCount, CultureInfo.InvariantCulture)).ToList();
            var steps = new List<string>();
            foreach (var key in keys)
            {
                System.Windows.Input.InputManager.Current.ProcessInput(new System.Windows.Input.KeyEventArgs(
                    System.Windows.Input.Keyboard.PrimaryDevice, PresentationSource.FromVisual(main), Environment.TickCount, key)
                { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent });
                Settle(30);
                var focused = System.Windows.Input.Keyboard.FocusedElement as FrameworkElement;
                var label = focused switch
                {
                    System.Windows.Controls.DataGridCell c => $"song {(c.DataContext as Track)?.TrackNumber}, column {c.Column?.DisplayIndex}",
                    System.Windows.Controls.ListBoxItem { DataContext: ArtistGroup a } => a.Name,
                    System.Windows.Controls.ListBoxItem { DataContext: AudioFool.ViewModels.AlbumItemViewModel al } => al.Title,
                    _ => focused?.Name,
                };
                steps.Add($"{(key == System.Windows.Input.Key.Tab ? "" : key + ": ")}{focused?.GetType().Name} {label}");
            }
            Console.WriteLine("tab walk: " + string.Join(" > ", steps));
            Console.WriteLine($"search box: '{vm.SearchQuery}'");
        }

        // --peers 1: the automation tree as a screen reader sees it (spec 8):
        // each control's type, name, id and toggle state. Lists show three items.
        if (Arg(args, "--peers") is not null)
        {
            Settle(200);
            PrintPeers(System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(main), 0);
        }

        Settle(1200);
        main.UpdateLayout();

        // --menushot <png>: renders the logo menu's drop-down without opening it.
        // A popup is its own window and is clamped onto a monitor, so opening it
        // could flash on the desktop; its content, though, is an ordinary visual
        // in the item's template and can be measured and rendered on its own.
        if (Arg(args, "--menushot") is { } menuShot
            && FindFirst<System.Windows.Controls.MenuItem>(main) is { } logoItem
            && FindFirst<System.Windows.Controls.Primitives.Popup>(logoItem)?.Child is FrameworkElement drop)
        {
            drop.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            drop.Arrange(new Rect(drop.DesiredSize));
            drop.UpdateLayout();
            Settle(200);
            drop.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            drop.Arrange(new Rect(drop.DesiredSize));
            Console.WriteLine($"menushot: {drop.GetType().Name} {drop.ActualWidth:0}x{drop.ActualHeight:0} "
                + $"bg={Describe((drop as System.Windows.Controls.Border)?.Background)}");
            Save(drop, menuShot, Math.Max(1, drop.ActualWidth), Math.Max(1, drop.ActualHeight), scale);
        }

        // --popupshot <png>: a context menu (its second item highlighted, as on
        // hover), a tooltip, and the logo drop-down with its first item highlighted,
        // each built and drawn as an ordinary visual, so no popup window opens.
        // Prints the colours each one resolved.
        if (Arg(args, "--popupshot") is { } popupShot)
        {
            var highlight = typeof(System.Windows.Controls.MenuItem).GetProperty("IsHighlighted")!;
            var context = new System.Windows.Controls.ContextMenu();
            context.Items.Add(new System.Windows.Controls.MenuItem { Header = "Edit Tags…" });
            var hovered = new System.Windows.Controls.MenuItem { Header = "Edit Album Tags…" };
            context.Items.Add(hovered);
            context.Items.Add(new System.Windows.Controls.MenuItem { Header = "Disabled item", IsEnabled = false });
            var tip = new System.Windows.Controls.ToolTip
            {
                Content = "Send DSD to the DAC untouched, wrapped as DSD-over-PCM, instead of decoding it to PCM first.",
            };
            // Neither may have a parent, so each is laid out as a root of its own.
            void Layout(FrameworkElement e)
            {
                e.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                e.Arrange(new Rect(e.DesiredSize));
                e.UpdateLayout();
            }
            Layout(context);
            Layout(tip);
            highlight.SetValue(hovered, true);
            Settle(200);
            Layout(context);
            Layout(tip);
            System.Windows.Controls.Border? Box(DependencyObject o) => FindAll<System.Windows.Controls.Border>(o).FirstOrDefault(b => b.BorderThickness.Left > 0);
            var hoverFill = FindAll<System.Windows.Controls.Border>(hovered).Select(b => b.Background).OfType<SolidColorBrush>().FirstOrDefault(b => b.Color.A > 0);
            Console.WriteLine($"popupshot: context bg={Describe(context.Background)} border={Describe(context.BorderBrush)} "
                + $"radius={Box(context)?.CornerRadius.TopLeft} fg={Describe(context.Foreground)} hover={Describe(hoverFill)} item fg={Describe(hovered.Foreground)} font={hovered.FontSize}");
            Console.WriteLine($"popupshot: tooltip bg={Describe(tip.Background)} border={Describe(tip.BorderBrush)} "
                + $"radius={Box(tip)?.CornerRadius.TopLeft} fg={Describe(tip.Foreground)} font={tip.FontSize} {tip.FontFamily} pad={tip.Padding} {tip.ActualWidth:0}x{tip.ActualHeight:0}");
            Save(context, popupShot, Math.Max(1, context.ActualWidth), Math.Max(1, context.ActualHeight), scale);
            Save(tip, Path.ChangeExtension(popupShot, null) + "-tip.png", Math.Max(1, tip.ActualWidth), Math.Max(1, tip.ActualHeight), scale);

            if (FindFirst<System.Windows.Controls.MenuItem>(main) is { } logo
                && FindFirst<System.Windows.Controls.Primitives.Popup>(logo)?.Child is FrameworkElement dropDown)
            {
                Console.WriteLine($"popupshot: logo hover key={Describe(logo.TryFindResource("MenuBarItemBackgroundSelected") as Brush)} "
                    + $"flyout={Describe(logo.TryFindResource("FlyoutBackground") as Brush)}");
                dropDown.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                dropDown.Arrange(new Rect(dropDown.DesiredSize));
                dropDown.UpdateLayout();
                if (logo.Items.OfType<System.Windows.Controls.MenuItem>().FirstOrDefault() is { } first)
                {
                    highlight.SetValue(first, true);
                    Console.WriteLine($"popupshot: drop-down item hover key={Describe(first.TryFindResource("MenuBarItemBackgroundSelected") as Brush)} fg={Describe(first.Foreground)}");
                }
                Settle(200);
                dropDown.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                dropDown.Arrange(new Rect(dropDown.DesiredSize));
                Console.WriteLine($"popupshot: drop-down bg={Describe((dropDown as System.Windows.Controls.Border)?.Background)}");
                Save(dropDown, Path.ChangeExtension(popupShot, null) + "-logo.png", Math.Max(1, dropDown.ActualWidth), Math.Max(1, dropDown.ActualHeight), scale);
            }
        }

        // --libshot <png>: renders the Libraries submenu the same way, with the first
        // folder ticked and the rest unticked, and prints each item's checked state.
        // Setting a folder's tick saves settings, so the file is put back afterwards.
        if (Arg(args, "--libshot") is { } libShot
            && FindFirst<System.Windows.Controls.MenuItem>(main) is { } libLogo
            && FindFirst<System.Windows.Controls.Primitives.Popup>(libLogo)?.Child is FrameworkElement libDrop)
        {
            var settingsBackup = File.Exists(AppSettings.SettingsPath) ? File.ReadAllBytes(AppSettings.SettingsPath) : null;
            try
            {
                // Items added here aren't wired to the view model's save handler,
                // so ticking them writes nothing.
                vm.FolderFilters.Add(new AudioFool.ViewModels.FolderFilterItem(@"C:\Users\Example\Music", enabled: true));
                vm.FolderFilters.Add(new AudioFool.ViewModels.FolderFilterItem(@"E:\Music", enabled: false));

                libDrop.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                libDrop.Arrange(new Rect(libDrop.DesiredSize));
                libDrop.UpdateLayout();
                Settle(200);

                var libraries = libLogo.Items.OfType<object>()
                    .Select(item => libLogo.ItemContainerGenerator.ContainerFromItem(item))
                    .OfType<System.Windows.Controls.MenuItem>()
                    .First(m => m.Header is string h && h.StartsWith("Libraries", StringComparison.Ordinal));
                libraries.ApplyTemplate();
                if (FindFirst<System.Windows.Controls.Primitives.Popup>(libraries)?.Child is FrameworkElement sub)
                {
                    for (var pass = 0; pass < 2; pass++)
                    {
                        sub.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                        sub.Arrange(new Rect(sub.DesiredSize));
                        sub.UpdateLayout();
                        Settle(200);
                    }

                    foreach (var item in libraries.Items)
                    {
                        if (libraries.ItemContainerGenerator.ContainerFromItem(item) is System.Windows.Controls.MenuItem m)
                            Console.WriteLine($"libshot item: header='{m.Header}' checkable={m.IsCheckable} checked={m.IsChecked} "
                                + $"style={(m.Style is null ? "none" : ReferenceEquals(m.Style, libraries.ItemContainerStyle) ? "ItemContainerStyle" : "other")}");
                    }

                    Save(sub, libShot, Math.Max(1, sub.ActualWidth), Math.Max(1, sub.ActualHeight), scale);

                    // The Remove folder rows: each must carry the command and its folder.
                    var remove = libraries.Items.OfType<System.Windows.Controls.MenuItem>()
                        .First(m => m.Header is string h && h.StartsWith("Remove", StringComparison.Ordinal));
                    remove.ApplyTemplate();
                    if (FindFirst<System.Windows.Controls.Primitives.Popup>(remove)?.Child is FrameworkElement removeSub)
                    {
                        removeSub.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                        removeSub.Arrange(new Rect(removeSub.DesiredSize));
                        removeSub.UpdateLayout();
                        Settle(200);
                        foreach (var item in remove.Items)
                        {
                            if (remove.ItemContainerGenerator.ContainerFromItem(item) is System.Windows.Controls.MenuItem m)
                                Console.WriteLine($"libshot remove: header='{m.Header}' command={(ReferenceEquals(m.Command, vm.RemoveFolderCommand) ? "RemoveFolder" : m.Command?.ToString() ?? "none")} "
                                    + $"parameter={(m.CommandParameter as FolderFilterItem)?.FolderPath ?? "none"} canExecute={m.Command?.CanExecute(m.CommandParameter)}");
                        }
                    }
                }
                else
                {
                    Console.WriteLine("libshot: Libraries submenu popup not found");
                }
            }
            finally
            {
                if (settingsBackup is not null)
                    File.WriteAllBytes(AppSettings.SettingsPath, settingsBackup);
                else if (File.Exists(AppSettings.SettingsPath))
                    File.Delete(AppSettings.SettingsPath);
            }
        }

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

        if (which == "seek")
        {
            RunSeek(main, vm, engine, Arg(args, "--file") ?? throw new ArgumentException("--window seek needs --file <audio file>"));
            return 0;
        }

        if (which is "edit" or "queue" or "clicks")
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
                if (which == "clicks")
                    RunClicks(main, vm);
                else if (which == "queue")
                    RunQueueEdit(vm, engine);
                else
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

            if (Arg(args, "--albumtips") is not null)
                PrintAlbumTips(main);
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

        // Closing saves the window's position into the settings file - here the
        // off-screen spot the harness uses - so the file is put back afterwards.
        var settingsBefore = File.Exists(AppSettings.SettingsPath) ? File.ReadAllBytes(AppSettings.SettingsPath) : null;
        main.Close();
        if (settingsBefore is not null)
            File.WriteAllBytes(AppSettings.SettingsPath, settingsBefore);
        else if (File.Exists(AppSettings.SettingsPath))
            File.Delete(AppSettings.SettingsPath);

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
            "AfSurfacePanel", "AfSurfaceShell", "AfRadiusPanel", "AfFontMono",
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
        Make("Low Poly Sunrise", "Aphelion Drive", "Second Sight", 4, 1, 1997, "DSD", 5645, 1, 2822400, 289),
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

    private static IEnumerable<DependencyObject> Descendants(DependencyObject node)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
        {
            var child = VisualTreeHelper.GetChild(node, i);
            yield return child;
            foreach (var deeper in Descendants(child))
                yield return deeper;
        }
    }

    /// <summary>
    /// --window clicks: on the scratch album, silently (volume 0), a single click
    /// on a song row selects it without playing, and a double-click plays it
    /// (spec 6.6). The mouse events are raised on the row's own cell text, as
    /// WPF raises them, so the grid's real handlers run.
    /// </summary>
    private static void RunClicks(MainWindow main, MainViewModel vm)
    {
        vm.Volume = 0;
        var grid = (System.Windows.Controls.DataGrid)main.FindName("TrackGrid");
        main.UpdateLayout();
        Settle(200);

        System.Windows.Controls.TextBlock SongText(int index)
        {
            var row = (System.Windows.Controls.DataGridRow)grid.ItemContainerGenerator.ContainerFromIndex(index);
            var cell = Descendants(row).OfType<System.Windows.Controls.DataGridCell>().First(c => c.Column.DisplayIndex == 1);
            return Descendants(cell).OfType<System.Windows.Controls.TextBlock>().First();
        }

        void Raise(UIElement target, RoutedEvent e, UIElement source)
        {
            var args = new System.Windows.Input.MouseButtonEventArgs(
                System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount, System.Windows.Input.MouseButton.Left)
            { RoutedEvent = e, Source = source };
            target.RaiseEvent(args);
            Console.WriteLine($"  raised {e.Name} on {target.GetType().Name} source={(source as System.Windows.Controls.TextBlock)?.Text} handled={args.Handled} original={args.OriginalSource?.GetType().Name}");
            Settle(50);
        }

        string State() => $"selected={grid.SelectedIndex} ({(grid.SelectedItem as Track)?.Title}) count={grid.SelectedItems.Count} "
            + $"playing={vm.NowPlaying?.Title ?? "nothing"} isPlaying={vm.IsPlaying}";

        Console.WriteLine($"before: {State()}");

        // One click on row 2: down and up on the song text.
        var two = SongText(2);
        Raise(two, UIElement.MouseLeftButtonDownEvent, two);
        Raise(two, UIElement.MouseLeftButtonUpEvent, two);
        Settle(900); // past the double-click time, so a slow-click edit would have opened
        Console.WriteLine($"single click row 2: {State()} editing={grid.CurrentCell.Column is not null && Descendants(grid).OfType<System.Windows.Controls.DataGridCell>().Any(c => c.IsEditing)}");

        // Double-click on row 1: Control raises MouseDoubleClick on the grid with
        // the clicked element as its source.
        var one = SongText(1);
        Raise(one, UIElement.MouseLeftButtonDownEvent, one);
        Raise(one, UIElement.MouseLeftButtonUpEvent, one);
        Raise(grid, System.Windows.Controls.Control.MouseDoubleClickEvent, one);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (vm.NowPlaying is null && clock.ElapsedMilliseconds < 5000)
            Settle(5);
        Console.WriteLine($"  first to start: {vm.NowPlaying?.Title ?? "nothing"} after {clock.ElapsedMilliseconds} ms");
        Settle(300);
        Console.WriteLine($"double-click row 1: {State()}");
        var playingRow = (System.Windows.Controls.DataGridRow)grid.ItemContainerGenerator.ContainerFromIndex(1);
        for (var i = 0; i < grid.Items.Count; i++)
            Console.WriteLine($"  row {i} {((Track)grid.Items[i]).Title} nowPlaying={AudioFool.TrackRow.GetIsNowPlaying((System.Windows.Controls.DataGridRow)grid.ItemContainerGenerator.ContainerFromIndex(i))}");
        vm.TogglePlayCommand.Execute(null);
        Settle(300);
        Console.WriteLine($"after pause: {State()}");
    }

    /// <summary>
    /// The playback and status bars (spec 6.7, 6.8): every zone, button, slider
    /// part and label, in window coordinates, with sizes and colours read back.
    /// </summary>
    private static void PrintPlaybackProbe(Window main)
    {
        string Box(FrameworkElement e)
        {
            var p = e.TranslatePoint(new Point(0, 0), main);
            return $"x {p.X:0.##}-{p.X + e.ActualWidth:0.##} y {p.Y:0.##}-{p.Y + e.ActualHeight:0.##} ({e.ActualWidth:0.##} x {e.ActualHeight:0.##})";
        }
        static string Ink(object? brush) => brush is SolidColorBrush b ? b.Color.ToString() : brush?.ToString() ?? "null";
        T Named<T>(string name) where T : class => (T)main.FindName(name);
        string Text(System.Windows.Controls.TextBlock t) =>
            $"'{t.Text}' size={t.FontSize} weight={t.FontWeight.ToOpenTypeWeight()} ink={Ink(t.Foreground)} {Box(t)}";

        Console.WriteLine($"window: {main.ActualWidth} x {main.ActualHeight} minWidth={main.MinWidth}");
        var bar = Named<FrameworkElement>("PlaybackBar");
        Console.WriteLine($"playback bar: {Box(bar)}");
        var shell = Descendants(bar).OfType<System.Windows.Controls.Border>().First();
        Console.WriteLine($"  shell bg={Ink(shell.Background)} border={Ink(shell.BorderBrush)} {shell.BorderThickness} radius={shell.CornerRadius}");
        Console.WriteLine($"  zones: {Box(Named<FrameworkElement>("PlaybackZones"))} nowPlayingZone={Named<System.Windows.Controls.ColumnDefinition>("NowPlayingZone").ActualWidth}");

        var grid = Named<System.Windows.Controls.DataGrid>("TrackGrid");
        var firstCell = Descendants(grid).OfType<System.Windows.Controls.DataGridCell>()
            .OrderBy(c => c.TranslatePoint(new Point(0, 0), main).X).FirstOrDefault();
        if (firstCell is not null)
        {
            var content = Descendants(firstCell).OfType<FrameworkElement>().First(e => e is System.Windows.Controls.ContentPresenter);
            Console.WriteLine($"  song list first cell {Box(firstCell)}; content {Box(content)}");
        }

        foreach (var art in Descendants(bar).OfType<System.Windows.Controls.Border>().Where(b => b.Effect is not null))
            Console.WriteLine($"  art: {Box(art)} border={Ink(art.BorderBrush)} effect={((System.Windows.Media.Effects.DropShadowEffect)art.Effect).BlurRadius}/{((System.Windows.Media.Effects.DropShadowEffect)art.Effect).ShadowDepth}");
        foreach (var name in new[] { "NowPlayingTitle", "NowPlayingArtist", "NowPlayingFormat" })
            Console.WriteLine($"  {name}: {Text(Named<System.Windows.Controls.TextBlock>(name))}");

        foreach (var name in new[] { "ShuffleButton", "PreviousButton", "PlayPauseButton", "NextButton", "RepeatButton", "MuteButton" })
        {
            var b = Named<System.Windows.Controls.Primitives.ButtonBase>(name);
            var face = Descendants(b).OfType<AudioFool.Theming.MoldedFace>().FirstOrDefault();
            var icon = Descendants(b).OfType<System.Windows.Controls.Viewbox>().First();
            var pressed = b is System.Windows.Controls.Primitives.ToggleButton tb ? $" checked={tb.IsChecked}" : "";
            Console.WriteLine($"  {name}: {Box(b)} name='{System.Windows.Automation.AutomationProperties.GetName(b)}'{pressed} fg={Ink(b.Foreground)} "
                + $"face={Ink(face?.Face)} stroke={Ink(face?.Stroke)} shadow={face?.Shadow} icon {Box(icon)}");
        }
        var badge = Named<System.Windows.Controls.TextBlock>("RepeatOneBadge");
        Console.WriteLine($"  repeat-one badge visible={badge.IsVisible}: {Text(badge)}");

        foreach (var name in new[] { "PositionText", "DurationText", "OutputReadout" })
            Console.WriteLine($"  {name}: {Text(Named<System.Windows.Controls.TextBlock>(name))}");
        foreach (var name in new[] { "SeekBar", "VolumeSlider" })
        {
            var s = Named<System.Windows.Controls.Slider>(name);
            var track = Descendants(s).OfType<System.Windows.Controls.Border>().First();
            var thumb = Descendants(s).OfType<System.Windows.Controls.Primitives.Thumb>().First();
            var decrease = Descendants(s).OfType<System.Windows.Controls.Primitives.RepeatButton>().First();
            var fill = Descendants(decrease).OfType<System.Windows.Controls.Border>().First();
            Console.WriteLine($"  {name}: {Box(s)} value={s.Value:0.##}/{s.Maximum:0.##} enabled={s.IsEnabled}");
            Console.WriteLine($"    track {Box(track)} bg={Ink(track.Background)} radius={track.CornerRadius}");
            Console.WriteLine($"    fill {Box(fill)} bg={Ink(fill.Background)}");
            Console.WriteLine($"    thumb {Box(thumb)} centre x={thumb.TranslatePoint(new Point(thumb.ActualWidth / 2, 0), main).X:0.##}");
        }
        Console.WriteLine($"  volume group opacity={Named<FrameworkElement>("VolumeGroup").Opacity}");

        var status = Named<FrameworkElement>("StatusBar");
        Console.WriteLine($"status bar: {Box(status)}");
        Console.WriteLine($"  refresh: {Box(Named<FrameworkElement>("RefreshButton"))} name='{System.Windows.Automation.AutomationProperties.GetName(Named<FrameworkElement>("RefreshButton"))}'");
        Console.WriteLine($"  library dot: {Box(Named<FrameworkElement>("LibraryDot"))} fill={Ink(Named<System.Windows.Shapes.Ellipse>("LibraryDot").Fill)}");
        Console.WriteLine($"  line: {Text(Named<System.Windows.Controls.TextBlock>("StatusLine"))}");
        var describe = typeof(MainViewModel).GetMethod("DescribeLibrary", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        Console.WriteLine($"  library totals: '{describe.Invoke(main.DataContext, null)}'");
        foreach (var name in new[] { "LastFmIndicator", "BitPerfectToggle" })
        {
            var chip = Named<System.Windows.Controls.Control>(name);
            var dot = Descendants(chip).OfType<System.Windows.Shapes.Ellipse>().FirstOrDefault();
            if (dot is null) { Console.WriteLine($"  {name}: visible={chip.IsVisible} (not laid out)"); continue; }
            var pressed = chip is System.Windows.Controls.Primitives.ToggleButton tb ? $" checked={tb.IsChecked}" : "";
            Console.WriteLine($"  {name}: visible={chip.IsVisible}{pressed} {Box(chip)} bg={Ink(chip.Background)} dot {Box(dot)} fill={Ink(dot.Fill)} ring={Ink(dot.Stroke)}");
        }
    }

    private static void PrintSongProbe(Window main)
    {
        string Box(FrameworkElement e)
        {
            var p = e.TranslatePoint(new Point(0, 0), main);
            return $"x {p.X:0.##}-{p.X + e.ActualWidth:0.##} y {p.Y:0.##}-{p.Y + e.ActualHeight:0.##} ({e.ActualWidth:0.##} x {e.ActualHeight:0.##})";
        }
        static string Ink(object? brush) => brush is SolidColorBrush b ? b.Color.ToString() : brush?.ToString() ?? "null";

        var grid = (System.Windows.Controls.DataGrid)main.FindName("TrackGrid");
        var panel = Descendants(main).OfType<System.Windows.Controls.HeaderedContentControl>().Last(p => p.Name != "PlaybackBar");
        Console.WriteLine($"songs panel: {Box(panel)}");
        Console.WriteLine($"grid: {Box(grid)} rowHeaderWidth={grid.RowHeaderWidth} rowHeaderActual={grid.RowHeaderActualWidth} cellsOffset={grid.CellsPanelHorizontalOffset} nonFrozen={grid.NonFrozenColumnsViewportHorizontalOffset}");

        // Album header: the art and each line of text beside it.
        var header = (FrameworkElement)panel.Header;
        foreach (var e in Descendants(header).OfType<FrameworkElement>()
                     .Where(e => e is System.Windows.Controls.Border { Effect: not null } or System.Windows.Controls.TextBlock { Text.Length: > 0 }))
        {
            var label = e is System.Windows.Controls.TextBlock t
                ? $"text '{t.Text}' size={t.FontSize} weight={t.FontWeight.ToOpenTypeWeight()} ink={Ink(t.Foreground)} trim={t.TextTrimming} wrap={t.TextWrapping}"
                : $"art border={Ink(((System.Windows.Controls.Border)e).BorderBrush)}";
            Console.WriteLine($"  header {label}: {Box(e)}");
        }
        var strip = Descendants(panel).OfType<System.Windows.Controls.Border>().First(b => b.Name == "HeaderStrip");
        Console.WriteLine($"  header strip: {Box(strip)} divider={Ink(strip.BorderBrush)} {strip.BorderThickness}");

        var sv = Descendants(grid).OfType<System.Windows.Controls.ScrollViewer>().First();
        Console.WriteLine($"scroll: vbar={sv.VerticalScrollBarVisibility} hbar={sv.HorizontalScrollBarVisibility} computedV={sv.ComputedVerticalScrollBarVisibility} scrollable={sv.ScrollableHeight:0.##} viewport={sv.ViewportWidth:0.##}");

        foreach (var column in grid.Columns.OrderBy(c => c.DisplayIndex))
            Console.WriteLine($"  column {column.Header,-12} width={column.ActualWidth:0.##} ({column.Width})");

        var headersPresenter = Descendants(grid).OfType<System.Windows.Controls.Primitives.DataGridColumnHeadersPresenter>().First();
        Console.WriteLine($"headers presenter: {Box(headersPresenter)}");
        foreach (var h in Descendants(headersPresenter).OfType<System.Windows.Controls.Primitives.DataGridColumnHeader>().Where(h => h.Column is not null))
        {
            var text = Descendants(h).OfType<System.Windows.Controls.TextBlock>().FirstOrDefault();
            Console.WriteLine($"  header '{h.Column.Header}': cell {Box(h)} label {(text is null ? "-" : Box(text))} size={text?.FontSize} weight={text?.FontWeight.ToOpenTypeWeight()} ink={Ink(text?.Foreground)}");
        }

        foreach (var row in Descendants(grid).OfType<System.Windows.Controls.DataGridRow>().OrderBy(r => r.GetIndex()).Take(6))
        {
            var fill = Descendants(row).OfType<System.Windows.Controls.Border>().First(b => b.Name == "Fill");
            Console.WriteLine($"row {row.GetIndex()}: selected={row.IsSelected} nowPlaying={AudioFool.TrackRow.GetIsNowPlaying(row)} fill={Ink(fill.Background)} {Box(fill)} radius={fill.CornerRadius}");
            foreach (var cell in Descendants(row).OfType<System.Windows.Controls.DataGridCell>().OrderBy(c => c.Column.DisplayIndex))
            {
                var text = Descendants(cell).OfType<System.Windows.Controls.TextBlock>().First();
                var tri = Descendants(cell).OfType<System.Windows.Controls.Viewbox>().FirstOrDefault(v => v.Name == "Triangle");
                var triText = tri is { Visibility: Visibility.Visible }
                    ? $" triangle {Box(tri)} fill={Ink(Descendants(tri).OfType<System.Windows.Shapes.Path>().First().Fill)}"
                    : "";
                Console.WriteLine($"    {cell.Column.Header,-12} '{text.Text}' text {Box(text)} align={text.TextAlignment} size={text.FontSize} weight={text.FontWeight.ToOpenTypeWeight()} ink={Ink(text.Foreground)}{triText}");
            }
        }
    }

    private static void Populate(MainViewModel vm)
    {
        var tracks = SampleTracks();

        // --longalbum 120: Second Sight with that many tracks, numbered 1..N, for
        // the song table's three-digit track numbers (spec 6.6).
        if (Arg(Environment.GetCommandLineArgs(), "--longalbum") is { } longCount)
        {
            var count = int.Parse(longCount, CultureInfo.InvariantCulture);
            tracks = Enumerable.Range(1, count)
                .Select(i => Make($"Long Track {i}", "Aphelion Drive", "Second Sight", i, 1, 1997, "FLAC", 1000, 24, 96000, 200, count, 1))
                .ToList();
        }

        var aphelion = Artist(
            "Aphelion Drive",
            Alb("First Light", "Aphelion Drive", 1995, tracks.Take(6).ToList()),
            // --longtitle "<title>": the album header's two-line clamp (spec 6.5).
            Alb(Arg(Environment.GetCommandLineArgs(), "--longtitle") ?? "Second Sight", "Aphelion Drive", 1997, tracks),
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
        vm.StatusText = "499 artists · 2,376 albums · 26,795 tracks · 692 GB";
        // The format OutputReadout.Describe produces. --output overrides it, e.g.
        // --output "Exclusive · 176.4 kHz · bit-perfect · DSD over PCM" for the longest.
        vm.OutputDescription = Arg(Environment.GetCommandLineArgs(), "--output") ?? "Shared · 96 kHz / 32-bit (resampled)";
        vm.IsOutputActive = true;
    }

    // -------------------------------------------------------------- rendering

    /// <summary>
    /// Colour painted behind the window before it is drawn. PS1 is opaque, so
    /// none of it should show: pass --bg "#FF00FF" and any magenta pixel in the
    /// render is a gap the theme failed to paint.
    /// </summary>
    private static Color _backdrop = Color.FromRgb(0x20, 0x20, 0x20);

    /// <summary>
    /// Set by --dpi: draw the window straight into the bitmap after the backdrop,
    /// rather than through a VisualBrush. At an emulated DPI the brush softens every
    /// vertical edge across two pixels (session 5: borders read #B2AFAA / #B5B2AD
    /// where the app draws one #A5A29D pixel), which looks like a scaling bug and isn't.
    /// </summary>
    private static bool _direct;

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
            if (!_direct)
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
        if (_direct)
            rtb.Render(window);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static void PrintPeers(System.Windows.Automation.Peers.AutomationPeer peer, int depth)
    {
        var type = peer.GetAutomationControlType();
        var quiet = type is System.Windows.Automation.Peers.AutomationControlType.Text
            or System.Windows.Automation.Peers.AutomationControlType.Image
            or System.Windows.Automation.Peers.AutomationControlType.Pane
            or System.Windows.Automation.Peers.AutomationControlType.Custom
            or System.Windows.Automation.Peers.AutomationControlType.Group
            or System.Windows.Automation.Peers.AutomationControlType.Separator
            or System.Windows.Automation.Peers.AutomationControlType.Thumb;
        if (!quiet || peer.IsKeyboardFocusable())
        {
            var toggle = peer.GetPattern(System.Windows.Automation.Peers.PatternInterface.Toggle)
                is System.Windows.Automation.Provider.IToggleProvider t ? $" toggle={t.ToggleState}" : "";
            Console.WriteLine($"{new string(' ', depth * 2)}{type} '{peer.GetName()}' id={peer.GetAutomationId()} "
                + $"class={peer.GetClassName()} tab={peer.IsKeyboardFocusable()}{toggle}");
        }

        var shown = 0;
        var isList = type is System.Windows.Automation.Peers.AutomationControlType.List
            or System.Windows.Automation.Peers.AutomationControlType.DataGrid;
        foreach (var child in peer.GetChildren() ?? [])
        {
            if (isList && ++shown > 3)
            {
                Console.WriteLine($"{new string(' ', depth * 2 + 2)}...");
                break;
            }
            PrintPeers(child, depth + 1);
        }
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
    /// --window queue: plays the scratch album on the real engine, silently
    /// (shared mode, volume 0, and the fixtures are silence anyway), edits tracks
    /// while they wait in the queue, and prints what the now-playing bar, the
    /// window title and the scrobbler get as each comes up - once through a
    /// gapless handover and once through Next.
    /// </summary>
    /// <summary>
    /// --window seek --file <audio>: a click on the seek bar's track, with the real
    /// engine playing silently (volume 0, shared mode). The window is parked to
    /// the right of every monitor, so the slider's click-to-point maps the real
    /// pointer to 0:00 without anything appearing on the desktop. Mouse down and
    /// up are raised on the slider 400 ms apart, long enough for position ticks to
    /// land in between, and the position is sampled throughout: it should go to
    /// 0 and stay there, not bounce back to where it was.
    /// </summary>
    private static void RunSeek(MainWindow main, MainViewModel vm, AudioEngine engine, string file)
    {
        main.Left = 30000;
        Pump();
        vm.Volume = 0;
        var bar = (System.Windows.Controls.Slider)main.FindName("SeekBar");

        engine.Play([AudioFool.Core.Library.TagReader.Read(file)], 0);
        Settle(1000);
        engine.Seek(TimeSpan.FromSeconds(60));
        Settle(700);

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var samples = new List<(long Ms, string Label, double Slider, double Engine)>();
        void Sample(string label) => samples.Add((clock.ElapsedMilliseconds, label, bar.Value, engine.Position.TotalSeconds));
        void Raise(RoutedEvent e)
        {
            var args = new System.Windows.Input.MouseButtonEventArgs(
                System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount, System.Windows.Input.MouseButton.Left)
            { RoutedEvent = e, Source = bar };
            bar.RaiseEvent(args);
        }
        void Watch(int ms)
        {
            var end = clock.ElapsedMilliseconds + ms;
            while (clock.ElapsedMilliseconds < end) { Settle(40); Sample(""); }
        }

        Sample("start");
        Raise(UIElement.PreviewMouseLeftButtonDownEvent);
        Sample("down");
        Watch(400);
        Raise(UIElement.PreviewMouseLeftButtonUpEvent);
        // The press started a drag (SliderDrag); a real button-up ends it through
        // the handle's capture, which a raised event doesn't reach.
        var handle = Descendants(bar).OfType<System.Windows.Controls.Primitives.Thumb>().First();
        Console.WriteLine($"press started a drag: {handle.IsDragging}");
        handle.CancelDrag();
        Sample("up");
        Watch(700);
        foreach (var s in samples)
            Console.WriteLine($"{s.Ms,5} ms {s.Label,-5} slider={s.Slider,6:0.00} engine={s.Engine,6:0.00}");

        // From the press on, the slider should never read the old spot again.
        var bounced = samples.SkipWhile(s => s.Label != "down").Any(s => s.Slider > 30);
        Console.WriteLine(bounced ? "seek: BOUNCED back to the old position" : "seek: no bounce");

        // The keyboard: real key presses through InputManager, so the slider's own
        // commands move it. Each step should land and stay, the engine following.
        engine.Seek(TimeSpan.FromSeconds(60));
        Settle(600);
        System.Windows.Input.Keyboard.Focus(bar);
        Settle(50);
        foreach (var (key, step) in new[] { (System.Windows.Input.Key.Right, 5.0), (System.Windows.Input.Key.PageUp, 30.0), (System.Windows.Input.Key.Left, -5.0) })
        {
            var from = bar.Value;
            System.Windows.Input.InputManager.Current.ProcessInput(new System.Windows.Input.KeyEventArgs(
                System.Windows.Input.Keyboard.PrimaryDevice, PresentationSource.FromVisual(main), Environment.TickCount, key)
            { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent });
            var pressed = bar.Value;
            double lowest = double.MaxValue, highest = double.MinValue;
            for (var i = 0; i < 15; i++) { Settle(40); lowest = Math.Min(lowest, bar.Value); highest = Math.Max(highest, bar.Value); }
            // Over the 600 ms watched, playing on moves it under a second past the target.
            var target = from + step;
            var held = Math.Abs(pressed - target) < 0.01 && lowest >= target - 0.01 && highest <= target + 1
                && Math.Abs(engine.Position.TotalSeconds - bar.Value) < 0.5;
            Console.WriteLine($"key {key}: {from:0.00} -> {pressed:0.00}, then {lowest:0.00}-{highest:0.00}, engine {engine.Position.TotalSeconds:0.00}: {(held ? "held" : "BOUNCED")}");
        }
        engine.Stop();
        Settle(200);
    }

    private static void RunQueueEdit(MainViewModel vm, AudioEngine engine)
    {
        var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;

        // Not connected, and on a scratch queue: nothing can reach the real
        // scrobbles.json or Last.fm. TrackStarted still runs, so the tracker's
        // copy of the track can be read back.
        var scrobbler = new AudioFool.Core.Scrobbling.LastFmScrobbler(
            AudioFool.Core.Scrobbling.ScrobbleQueue.Load(
                Path.Combine(_scratchDir!, "scrobbles.json"), DateTimeOffset.UtcNow));
        typeof(MainViewModel).GetField("_scrobbler", flags)!.SetValue(vm, scrobbler);
        var tracker = (AudioFool.Core.Scrobbling.PlayTracker)typeof(AudioFool.Core.Scrobbling.LastFmScrobbler)
            .GetField("_tracker", flags)!.GetValue(scrobbler)!;

        vm.Volume = 0;

        Track Row(string file) => vm.Tracks.First(t => Path.GetFileName(t.FilePath).Contains(file));
        void Log(string s) => Console.WriteLine(s);

        void Await(Task task)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (!task.IsCompleted && clock.ElapsedMilliseconds < 5000)
                Settle(20);
            task.GetAwaiter().GetResult();
            Log($"  status: {vm.StatusText}");
        }

        bool WaitFor(string file)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds < 5000)
            {
                if (vm.NowPlaying is { } p && Path.GetFileName(p.FilePath).Contains(file))
                    return true;
                Settle(10);
            }
            return false;
        }

        void Report(string how, string file, string expectTitle, string expectArtist)
        {
            var arrived = WaitFor(file);
            var p = vm.NowPlaying;
            var row = Row(file);
            var ok = arrived && p is not null && p.Title == expectTitle && p.Artist == expectArtist
                     && vm.WindowTitle == $"{expectArtist} â€“ {expectTitle}"
                     && tracker.Current?.Title == expectTitle && tracker.Current?.Artist == expectArtist;
            Log($"{how} -> {file}: arrived={arrived} title='{p?.Title}' artist='{p?.Artist}' window='{vm.WindowTitle}' "
                + $"scrobbler='{tracker.Current?.Artist} / {tracker.Current?.Title}' note={ReferenceEquals(p, row)} "
                + $"engine='{engine.CurrentTrack?.Title}' {(ok ? "OK" : "STALE")}");
        }

        Log($"rows: {string.Join(" | ", vm.Tracks.Select(t => $"{t.TrackNumber} {t.Title} / {t.Artist}"))}");

        // Start the album and pause at once, so the edits land while One holds
        // the device, Two is open for the gapless handover, and Three and Four
        // are only queue entries.
        vm.PlayTrackCommand.Execute(Row("01"));
        engine.Pause();
        Log($"playing One, paused: state={engine.State} mode={engine.OutputMode} "
            + $"holds One={engine.HoldsFile(Row("01").FilePath)} Two={engine.HoldsFile(Row("02").FilePath)} Three={engine.HoldsFile(Row("03").FilePath)}");

        Await(vm.ApplyInlineEditAsync(Row("02"), AudioFool.Core.Library.InlineField.Title, "Two Edited"));
        Await(vm.ApplyInlineEditAsync(Row("03"), AudioFool.Core.Library.InlineField.Title, "Three Edited"));
        Await(vm.ApplyInlineEditAsync(Row("04"), AudioFool.Core.Library.InlineField.Artist, "Edited Artist"));
        Log($"edited: {string.Join(" | ", vm.Tracks.Select(t => $"{t.TrackNumber} {t.Title} / {t.Artist}"))}");

        // One runs out and hands over gaplessly to Two, saved while open.
        engine.TogglePause();
        Report("gapless", "02", "Two Edited", "Lab Artist");

        // Next goes through JumpTo, the other way a queued track comes up.
        vm.NextCommand.Execute(null);
        Report("next", "03", "Three Edited", "Lab Artist");

        // And a gapless handover into the MP3, whose artist changed.
        Report("gapless", "04", "Four", "Edited Artist");

        // Edit the track that is playing: the bar and the scrobbler both follow.
        engine.Pause();
        Await(vm.ApplyInlineEditAsync(Row("04"), AudioFool.Core.Library.InlineField.Title, "Four Edited"));
        var playingOk = vm.NowPlaying?.Title == "Four Edited" && tracker.Current?.Title == "Four Edited"
                        && tracker.Current?.Artist == "Edited Artist" && ReferenceEquals(vm.NowPlaying, Row("04"));
        Log($"playing edit -> 04: title='{vm.NowPlaying?.Title}' scrobbler='{tracker.Current?.Artist} / {tracker.Current?.Title}' "
            + $"{(playingOk ? "OK" : "STALE")}");

        // A save that would grow the playing file is refused, and the file is
        // left exactly as it was.
        var path = Row("04").FilePath;
        string Hash() => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)));
        var hashBefore = Hash();
        Await(vm.ApplyInlineEditAsync(Row("04"), AudioFool.Core.Library.InlineField.Title, new string('x', 300_000)));
        var refusedOk = vm.StatusText.Contains("playing or up next") && Hash() == hashBefore
                        && vm.NowPlaying?.Title == "Four Edited";
        Log($"resizing edit -> 04: file unchanged={Hash() == hashBefore} title='{vm.NowPlaying?.Title}' {(refusedOk ? "OK" : "WRONG")}");

        // The stream still plays after both saves: position moves on.
        var at = engine.Position;
        engine.TogglePause();
        Settle(200);
        Log($"after saves, playback resumes: {at.TotalMilliseconds:0} ms -> {engine.Position.TotalMilliseconds:0} ms, state={engine.State}");

        engine.Stop();
        Settle(200);
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
            + $"box {box?.ActualWidth:0}x{box?.ActualHeight:0} in row {grid.RowHeight:0} style={(box?.Style == main.TryFindResource("theme.cellEditBox.title") ? "theme.cellEditBox.title" : "other")} "
            + $"font {box?.FontSize} bg {(box?.Background as System.Windows.Media.SolidColorBrush)?.Color} border {(box?.BorderBrush as System.Windows.Media.SolidColorBrush)?.Color}");
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

        // 5b. The slow click as the mouse delivers it: down and up routed through
        //     the grid's own handlers, on the cell that already has focus - the
        //     grid then tries (and is refused) an edit of its own.
        void Click(UIElement target)
        {
            var clickCount = typeof(System.Windows.Input.MouseButtonEventArgs).GetProperty("ClickCount")!;
            foreach (var routed in new[]
                     {
                         System.Windows.Input.Mouse.PreviewMouseDownEvent, System.Windows.Input.Mouse.MouseDownEvent,
                         System.Windows.Input.Mouse.PreviewMouseUpEvent, System.Windows.Input.Mouse.MouseUpEvent,
                     })
            {
                var args = new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0,
                    System.Windows.Input.MouseButton.Left) { RoutedEvent = routed };
                clickCount.SetValue(args, 1);
                target.RaiseEvent(args);
            }
        }

        three = Row("Three");
        Select(three, "SongColumn");
        var songText = FindFirst<System.Windows.Controls.TextBlock>(Cell(three, "SongColumn"))!;
        Click(songText);
        Log($"real click: editing at once? {Cell(three, "SongColumn").IsEditing}");
        Settle(900);
        var slowOk = Cell(three, "SongColumn").IsEditing;
        Log($"real click: editing after the double-click time? {slowOk} {(slowOk ? "OK" : "BROKEN")}");
        if (Box(three, "SongColumn") is { } threeBox)
            Press(threeBox, System.Windows.Input.Key.Escape);
        Log($"real click: Esc cancels -> editing={Cell(three, "SongColumn").IsEditing} title='{Row("Three").Title}'");

        // 5c. Enter walks down the column: after the save rebuilds the rows, the
        //     next row is selected with # open for editing, and Enter goes on.
        var ordered = grid.Items.OfType<Track>().ToList();
        var first = ordered[0];
        var nextPath = ordered[1].FilePath;
        Select(first, "TrackNumberColumn");
        Press(Cell(first, "TrackNumberColumn"), System.Windows.Input.Key.F2);
        Box(first, "TrackNumberColumn")!.Text = first.TrackNumber == 1 ? "11" : "1";
        before = vm.StatusText;
        Press(Box(first, "TrackNumberColumn")!, System.Windows.Input.Key.Enter);
        AwaitSave(before);
        var sel = grid.SelectedItem as Track;
        var cur = grid.CurrentCell;
        var focusedCell = FindAncestorOf<System.Windows.Controls.DataGridCell>(System.Windows.Input.Keyboard.FocusedElement as DependencyObject);
        Log($"enter walk: status='{vm.StatusText}' selected='{sel?.DisplayTitle}' ({grid.SelectedItems.Count}) "
            + $"current={cur.Column?.Header}/{(cur.Item as Track)?.DisplayTitle} focus={focusedCell?.Column?.Header}/{(focusedCell?.DataContext as Track)?.DisplayTitle}");
        var next = vm.Tracks.First(t => t.FilePath == nextPath);
        var nextBox = Box(next, "TrackNumberColumn");
        var fe = System.Windows.Input.Keyboard.FocusedElement as DependencyObject;
        var chain = new List<string>();
        for (var n = fe; n is System.Windows.Media.Visual && chain.Count < 6; n = System.Windows.Media.VisualTreeHelper.GetParent(n)) chain.Add(n.GetType().Name);
        Log($"enter walk: focused element chain: {string.Join(" < ", chain)} (null={fe is null})");
        var walkOk = sel?.FilePath == nextPath && cur.Column == Column("TrackNumberColumn")
                     && Cell(next, "TrackNumberColumn").IsEditing && nextBox?.IsKeyboardFocused == true;
        Log($"enter walk: # open on the next row? {Cell(next, "TrackNumberColumn").IsEditing} box='{nextBox?.Text}' "
            + $"focused={nextBox?.IsKeyboardFocused} {(walkOk ? "OK" : "BROKEN")}");
        Log("enter walk: " + FileTags(first.FilePath));

        // Unchanged text writes nothing and so never rebuilds; Enter still moves on.
        var third = grid.Items.OfType<Track>().SkipWhile(t => t.FilePath != nextPath).Skip(1).FirstOrDefault();
        if (nextBox is not null && third is not null)
        {
            Press(nextBox, System.Windows.Input.Key.Enter);
            Settle(300);
            var unchangedOk = (grid.SelectedItem as Track)?.FilePath == third.FilePath && Cell(third, "TrackNumberColumn").IsEditing;
            Log($"enter walk: unchanged Enter -> '{(grid.SelectedItem as Track)?.DisplayTitle}' editing={Cell(third, "TrackNumberColumn").IsEditing} "
                + $"{(unchangedOk ? "OK" : "BROKEN")}");
            if (Box(third, "TrackNumberColumn") is { } thirdBox)
                Press(thirdBox, System.Windows.Input.Key.Escape);
            Log($"enter walk: Esc stops -> editing={Cell(third, "TrackNumberColumn").IsEditing}");
        }

        // Enter on the last row saves and stays put.
        var last = grid.Items.OfType<Track>().Last();
        Select(last, "SongColumn");
        Press(Cell(last, "SongColumn"), System.Windows.Input.Key.F2);
        Press(Box(last, "SongColumn")!, System.Windows.Input.Key.Enter);
        Settle(300);
        Log($"enter walk: last row Enter -> selected='{(grid.SelectedItem as Track)?.DisplayTitle}' any editing="
            + $"{grid.Items.OfType<Track>().Any(t => Cell(t, "SongColumn").IsEditing)}");

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
            var survived = grid.CurrentCell.Column is not null && Cell(Row("Four"), "AlbumColumn").IsEditing;
            var kept = Box(Row("Four"), "AlbumColumn");
            var keptOk = survived && kept?.Text == "Half typed" && kept.IsKeyboardFocused;
            Log($"album: rebuild mid-edit ok; still editing={survived} text='{kept?.Text}' focused={kept?.IsKeyboardFocused} "
                + $"{(keptOk ? "OK" : "BROKEN")}");
            if (kept is not null)
                Press(kept, System.Windows.Input.Key.Escape);
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

    private static T? FindAncestorOf<T>(System.Windows.DependencyObject? node) where T : System.Windows.DependencyObject
    {
        while (node is System.Windows.Media.Visual and not T)
            node = System.Windows.Media.VisualTreeHelper.GetParent(node);

        return node as T;
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
    private static void PrintAlbumTips(System.Windows.FrameworkElement main)
    {
        if (main.FindName("AlbumList") is not System.Windows.Controls.ListBox albums)
            return;
        albums.UpdateLayout();
        foreach (var item in albums.Items)
        {
            if (albums.ItemContainerGenerator.ContainerFromItem(item) is not System.Windows.Controls.ListBoxItem row)
                continue;
            var rowGrid = FindDescendant<System.Windows.Controls.Grid>(row, g => g.Tag is System.Windows.Controls.TextBlock);
            if (rowGrid is null)
                continue;
            var opening = (System.Windows.Controls.ToolTipEventArgs)Activator.CreateInstance(
                typeof(System.Windows.Controls.ToolTipEventArgs),
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance,
                null, [true], null)!;
            opening.RoutedEvent = System.Windows.FrameworkElement.ToolTipOpeningEvent;
            rowGrid.RaiseEvent(opening);
            var title = (System.Windows.Controls.TextBlock)rowGrid.Tag;
            Console.WriteLine($"{(opening.Handled ? "hidden" : "SHOWN ")}  {title.ActualWidth,6:0.0}px  {title.Text}");
        }
    }

    private static T? FindDescendant<T>(System.Windows.DependencyObject root, Func<T, bool> match)
        where T : System.Windows.DependencyObject
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T found && match(found))
                return found;
            if (FindDescendant(child, match) is { } deeper)
                return deeper;
        }
        return null;
    }

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
