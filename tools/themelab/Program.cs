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
internal static partial class Program
{
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern IntPtr NcSend(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "PostMessageW")]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

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

        _brushDump = Arg(args, "--brushdump") is not null;

        // --theme light|dark: the theme to start in; light unless asked, so every
        // recipe renders as it always has. --switch light|dark: switch to that one
        // live, as the Theme button does, just before the first render, once every
        // window in the case is built - a live switch must look exactly like a
        // start in that theme.
        _theme = ThemeArg(args, "--theme") ?? AudioFool.Core.Theme.AppTheme.Light;
        _switchTo = ThemeArg(args, "--switch");

        var app = new LabApp();
        app.InitializeComponent();

        // --singleinstance <files...>: the app's one-copy handover under a test name,
        // never the real app's. This copy becomes the one copy, starts others of
        // ThemeLab with --singlechild, and prints what reaches it.
        if (Arg(args, "--singleinstance") is "exit")
            return RunSingleExit();
        if (Arg(args, "--singleinstance") is not null)
            return RunSingleInstance(args);
        if (Arg(args, "--singlechild") is { } childName)
        {
            var files = args.SkipWhile(a => a != "--files").Skip(1).ToArray();
            var takeOver = Arg(args, "--takeover") is "1";
            var started = AudioFool.Services.SingleInstance.Start(files, childName);
            if (started is null)
            {
                Console.WriteLine(takeOver ? "handed over (WRONG: the first copy was closing)" : "handed over");
                return 0;
            }

            // A copy that became the one copy stays a while, as the app would, so
            // a fault on its listener shows. With --takeover it reports what it
            // was handed: its own files, and those of a copy started after it.
            var received = new List<string>();
            started.FilesOpened += f => received.AddRange(f);
            Settle(int.TryParse(Arg(args, "--stay"), out var stay) ? stay : 1500);
            Console.WriteLine(takeOver
                ? $"took over; files: {string.Join(", ", received.Select(Path.GetFileName))}"
                : "became the one copy (WRONG for a child)");
            started.Dispose();
            return 0;
        }

        if (dumpPath is not null)
        {
            // After Apply, so the dump shows what the theme and
            // ApplicationAccentColorManager actually leave in place.
            ThemeService.Apply(_theme);
            Dump(dumpPath, Arg(args, "--dumpkeys"));
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
            ThemeService.Apply(_theme);
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

        if (which == "drives")
            return RunDrives();

        // A throwaway settings object: never Load()ed and never Save()d, so the
        // real settings.json is untouched. No music folders means InitialiseAsync
        // short-circuits before any scan.
        var settings = new AppSettings
        {
            ScanOnStartup = false,
            GlobalHotkeys = false,
        };

        // --lastplayed "Artist|Album|path": what the last session was playing, for
        // --window click to open on (see the check after LoadRealLibrary).
        if (Arg(args, "--lastplayed")?.Split('|') is [var lpArtist, var lpAlbum, var lpPath])
            settings.LastPlayed = new AudioFool.Core.Settings.LastPlayedTrack(lpPath, lpArtist, lpAlbum);

        // --window queue plays for real, and the engine posts its events to the
        // context it is built on - as the app's does - so it needs one first.
        if (which is "queue" or "clicks" or "seek" or "rowclick" or "openfiles" or "analysis" or "clear" or "waveform" or "device" or "splice" or "ratechange" or "lend" or "playbackfixes" or "themeswitch" || Arg(args, "--qualitycheck") is not null
            || Arg(args, "--restoreplay") is not null)
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));

        // Playlists go to a scratch file, never the real playlists.json.
        MainViewModel.PlaylistsPath = Path.Combine(Path.GetTempPath(), "ThemeLabPlaylists", Guid.NewGuid().ToString("N"), "playlists.json");

        var runtime = new BassRuntime();
        var engine = new AudioEngine(runtime);
        var vm = new MainViewModel(engine, runtime, new AlbumArtService(), settings);

        // Clearances go to --clearances <file> or a fresh scratch file, never the real quality-cleared.json.
        vm.QualityClearancesPath = Arg(args, "--clearances")
            ?? Path.Combine(Path.GetTempPath(), "ThemeLabClearances", Guid.NewGuid().ToString("N"), "quality-cleared.json");

        // Cover check results go to --covercache <file> or a fresh scratch file, never the real covers.json.
        vm.CoverCachePath = Arg(args, "--covercache")
            ?? Path.Combine(Path.GetTempPath(), "ThemeLabCovers", Guid.NewGuid().ToString("N"), "covers.json");

        // Same order as App.OnStartup: theme first, then the window, because a
        // DynamicResource Style is resolved as the element initialises.
        ThemeService.Apply(_theme);

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
            _dpi = new DpiScale(d, d);
            VisualTreeHelper.SetRootDpi(main, _dpi.Value);
            scale = d;
            _direct = true;
            main.UpdateLayout();
            Console.WriteLine($"dpi {VisualTreeHelper.GetDpi(main).PixelsPerDip}");
        }

        if (which == "openfiles")
        {
            RunOpenFiles(main, vm, engine, runtime);
            return 0;
        }

        if (which == "headerpress")
            return RunHeaderPress(main);

        if (which == "statusclick")
            return RunStatusClick(main, vm, outPath, w, h, scale);

        if (which == "nowplayinglines")
            return RunNowPlayingLines(main, vm, outPath, w, h, scale);

        if (which == "rowclick")
        {
            RunRowClick(main, vm, engine, Arg(args, "--artist") ?? "Nhato");
            return 0;
        }

        // --window findreplaceperf [--find <text>]: Find and Replace's window over the
        // real library, read-only (Replace is never pressed), timed (session 63).
        if (which == "findreplaceperf")
        {
            LoadRealLibrary(vm);
            RunFindReplacePerf(main, vm, Arg(args, "--find"), outPath, scale);
            return 0;
        }

        if (which == "click" || Arg(args, "--typeahead") is not null)
        {
            LoadRealLibrary(vm);

            if (settings.LastPlayed is not null)
            {
                Pump();
                var lpGrid = (System.Windows.Controls.DataGrid)main.FindName("TrackGrid");
                var artistList = (System.Windows.Controls.ListBox)main.FindName("ArtistList");
                Console.WriteLine($"opened on: artist '{vm.SelectedArtist?.Name}', album '{vm.SelectedAlbum?.Album.Title}', " +
                                  $"song '{(lpGrid.SelectedItem as Track)?.DisplayTitle}' (row {lpGrid.SelectedIndex + 1} of {lpGrid.Items.Count}), " +
                                  $"artist row {artistList.SelectedIndex + 1} of {artistList.Items.Count}, now playing: {vm.NowPlaying?.DisplayTitle ?? "nothing"}");
                Settle(800);
                Console.WriteLine($"after settling: song row {lpGrid.SelectedIndex + 1}, selected rows {lpGrid.SelectedItems.Count}, " +
                                  $"VM selected tracks {vm.SelectedTracks.Count}");
                var flagged = Enumerable.Range(0, lpGrid.Items.Count)
                    .Where(i => lpGrid.ItemContainerGenerator.ContainerFromIndex(i) is System.Windows.Controls.DataGridRow { IsSelected: true })
                    .Select(i => i + 1);
                Console.WriteLine($"rows whose container IsSelected: {string.Join(",", flagged)}");
                Console.WriteLine($"now-playing bar: '{vm.NowPlaying?.DisplayTitle}' / '{vm.NowPlaying?.Artist}' / '{vm.NowPlayingFormat}' " +
                                  $"duration {vm.DurationDisplay}, art {(vm.NowPlayingArt is null ? "none" : $"{vm.NowPlayingArt.PixelWidth}px")}, " +
                                  $"playing {vm.IsPlaying}, engine {engine.State}, waveform {vm.NowPlayingWaveform?.Length.ToString() ?? "none"}");

                // --restoreplay 1: press Play (silently, engine volume 0) and report
                // which track the engine starts and where it sits in its queue.
                if (Arg(args, "--restoreplay") is not null)
                {
                    // Pick another album first: Play must still start the restored track.
                    if (Arg(args, "--restoreplayfrom") is { } otherArtist
                        && vm.Artists.FirstOrDefault(a => a.Name == otherArtist) is { } other)
                    {
                        vm.SelectedArtist = other;
                        Pump();
                        Console.WriteLine($"moved to: '{vm.SelectedArtist?.Name}' / '{vm.SelectedAlbum?.Album.Title}'");
                    }

                    engine.Volume = 0;
                    vm.TogglePlayCommand.Execute(null);
                    var clock = System.Diagnostics.Stopwatch.StartNew();
                    while (engine.CurrentTrack is null && clock.ElapsedMilliseconds < 5000)
                        Settle(10);
                    Settle(300);
                    Console.WriteLine($"Play -> engine '{engine.CurrentTrack?.DisplayTitle}' ({engine.State}), " +
                                      $"bar '{vm.NowPlaying?.DisplayTitle}', playing {vm.IsPlaying}");
                    vm.NextCommand.Execute(null);
                    Settle(500);
                    Console.WriteLine($"Next -> engine '{engine.CurrentTrack?.DisplayTitle}', bar '{vm.NowPlaying?.DisplayTitle}'");
                    engine.Stop();
                    Settle(100);
                }
            }
        }
        else if (which == "playlists")
        {
            return RunPlaylists(main, vm, outPath, w, h, scale);
        }
        else if (which == "reorder")
        {
            return RunReorder(main, vm, outPath, w, h, scale);
        }
        else if (which == "recent")
        {
            return RunRecent(main, vm, outPath, w, h, scale);
        }
        else if (which is "multiedit" or "albumeditor" or "trackeditor")
        {
            LoadMultiLibrary(vm);
        }
        else if (which is "covers" or "pngconvert")
        {
            LoadCoversLibrary(vm, Arg(args, "--album") ?? @"D:\Music\Arch Echo\[2017] Arch Echo");
        }
        else if (which is "edit" or "queue" or "clicks" or "nowplayingart")
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

        // --artistsort recent|size: the most recently added, or largest, artists
        // first, as after one or two clicks on the header (the app itself always
        // opens A-Z).
        if (Arg(args, "--artistsort") is "recent" or "size")
            vm.ArtistOrder = Arg(args, "--artistsort") == "size" ? AudioFool.Core.Library.ArtistOrder.Size : AudioFool.Core.Library.ArtistOrder.Recent;

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
                    vm.OutputDescription = "Exclusive · 44.1 kHz";
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
                + $"label='{vm.LastFmLabel}' helptext='{vm.LastFmHelpText}'");
        }

        // --clickartists N: click the Artists header N times (default once)
        // through its automation peer and print the order before and after each.
        // The toggle saves settings, so the settings file is copied aside first
        // and put back afterwards.
        if (Arg(args, "--clickartists") is { } headerClicks)
        {
            string Top() => string.Join(" | ", vm.Artists.Take(8).Select(a => $"{a.Name} ({a.LastAddedUtc:yyyy-MM-dd}, {a.AlbumSummary})"));
            var header = (System.Windows.Controls.Button)main.FindName("ArtistSortHeader");
            var settingsBackup = File.Exists(AppSettings.SettingsPath) ? File.ReadAllBytes(AppSettings.SettingsPath) : null;
            var artistList = (System.Windows.Controls.ListBox)main.FindName("ArtistList");
            double Offset() => FindFirst<System.Windows.Controls.ScrollViewer>(artistList)?.VerticalOffset ?? -1;
            string Label() => string.Join(",", FindAll<System.Windows.Controls.TextBlock>(header)
                .Where(t => t.Visibility == Visibility.Visible).Select(t => string.Concat(t.Inlines.OfType<System.Windows.Documents.Run>().Select(r => r.Text))));
            try
            {
                // --clickfrom N: select the N-th artist first, as if browsed to.
                if (Arg(args, "--clickfrom") is { } from)
                {
                    vm.SelectedArtist = vm.Artists[int.Parse(from, CultureInfo.InvariantCulture)];
                    Settle(300);
                }

                Console.WriteLine($"artists before: order={vm.ArtistOrder} label='{Label()}' selected='{vm.SelectedArtist?.Name}' "
                    + $"(#{vm.Artists.IndexOf(vm.SelectedArtist!)}) scroll={Offset():0} tip='{header.ToolTip}'");
                Console.WriteLine($"  {Top()}");
                var peer = new System.Windows.Automation.Peers.ButtonAutomationPeer(header);
                var clicks = int.TryParse(headerClicks, out var n) && n > 0 ? n : 1;
                for (var i = 1; i <= clicks; i++)
                {
                    ((System.Windows.Automation.Provider.IInvokeProvider)peer.GetPattern(System.Windows.Automation.Peers.PatternInterface.Invoke)!).Invoke();
                    Settle(300);
                    Console.WriteLine($"artists after click {i}: order={vm.ArtistOrder} label='{Label()}' selected='{vm.SelectedArtist?.Name}' "
                        + $"(#{vm.Artists.IndexOf(vm.SelectedArtist!)}) scroll={Offset():0} album='{vm.SelectedAlbum?.Album.Title}' tip='{header.ToolTip}'");
                    Console.WriteLine($"  {Top()}");
                }
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

        // --typeahead King [--typeaheadlist ArtistList] [--tabfrom ShuffleButton]
        // [--keys "Down,Down"]: type-ahead over the real library, as if the pointer
        // were over the list (hover can't be faked, so the target and the typed
        // text are set on the window directly), with focus starting elsewhere.
        // Then real key presses through InputManager; prints each selection.
        if (Arg(args, "--typeahead") is { } aheadText)
        {
            main.UpdateLayout();
            var list = (System.Windows.Controls.ListBox)main.FindName(Arg(args, "--typeaheadlist") ?? "ArtistList");
            System.Windows.Input.Keyboard.Focus((IInputElement)main.FindName(Arg(args, "--tabfrom") ?? "ShuffleButton"));
            Settle(100);
            const System.Reflection.BindingFlags own = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
            string Here()
            {
                var focused = System.Windows.Input.Keyboard.FocusedElement as FrameworkElement;
                var name = list.SelectedItem switch
                {
                    ArtistGroup a => a.Name,
                    AudioFool.ViewModels.AlbumItemViewModel al => al.Title,
                    _ => "<none>",
                };
                var focusName = focused is System.Windows.Controls.ListBoxItem { DataContext: ArtistGroup fa } ? fa.Name
                    : focused is System.Windows.Controls.ListBoxItem { DataContext: AudioFool.ViewModels.AlbumItemViewModel fal } ? fal.Title
                    : focused?.Name;
                return $"selected '{name}', focus {focused?.GetType().Name} '{focusName}'";
            }
            Console.WriteLine($"typeahead: before: {Here()}");
            typeof(MainWindow).GetField("_typeAheadTarget", own)!.SetValue(main, list);
            typeof(MainWindow).GetField("_typeAheadBuffer", own)!.SetValue(main, aheadText);
            typeof(MainWindow).GetMethod("SelectTypeAheadMatch", own)!.Invoke(main, null);
            Settle(300);
            Console.WriteLine($"typeahead: '{aheadText}': {Here()}");
            foreach (var key in (Arg(args, "--keys") ?? "Down").Split(',').Select(Enum.Parse<System.Windows.Input.Key>))
            {
                System.Windows.Input.InputManager.Current.ProcessInput(new System.Windows.Input.KeyEventArgs(
                    System.Windows.Input.Keyboard.PrimaryDevice, PresentationSource.FromVisual(main), Environment.TickCount, key)
                { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent });
                Settle(100);
                Console.WriteLine($"typeahead: {key}: {Here()}");
            }
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

        // --clickfocus 1: a mouse click on a playback control must leave keyboard
        // focus where it was (so no focus ring is drawn on the control), still
        // click, and leave the control focusable for Tab. Down and up are raised
        // through the buttons' own handlers. Commands are detached for the click,
        // so nothing plays and no setting (Shuffle, Repeat) is saved.
        if (Arg(args, "--shortcuts") is not null)
        {
            RunShortcuts(main, vm);
            return 0;
        }

        if (Arg(args, "--clickfocus") is not null)
        {
            main.UpdateLayout();
            var clickCount = typeof(System.Windows.Input.MouseButtonEventArgs).GetProperty("ClickCount")!;
            void Click(UIElement target)
            {
                foreach (var routed in new[]
                         {
                             System.Windows.Input.Mouse.PreviewMouseDownEvent, System.Windows.Input.Mouse.MouseDownEvent,
                             System.Windows.Input.Mouse.PreviewMouseUpEvent, System.Windows.Input.Mouse.MouseUpEvent,
                         })
                {
                    var a = new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount,
                        System.Windows.Input.MouseButton.Left) { RoutedEvent = routed };
                    clickCount.SetValue(a, 1);
                    target.RaiseEvent(a);
                }
                Settle(30);
            }

            string Focused() => System.Windows.Input.Keyboard.FocusedElement switch
            {
                System.Windows.Controls.DataGridCell c => $"song {(c.DataContext as Track)?.TrackNumber}",
                FrameworkElement f => f.Name is { Length: > 0 } n ? n : f.GetType().Name,
                _ => "nothing",
            };

            var songGrid = (System.Windows.Controls.DataGrid)main.FindName("TrackGrid");
            var all = true;
            foreach (var name in new[] { "ShuffleButton", "PreviousButton", "PlayPauseButton", "NextButton", "RepeatButton", "MuteButton" })
            {
                var button = (System.Windows.Controls.Primitives.ButtonBase)main.FindName(name);
                // WPF presses a button only while the real mouse button is down, so a
                // raised release never clicks. In Press mode the click comes from the
                // press handler itself, after its Focus() call, which shows the press
                // still goes through with focus skipped.
                var command = button.Command;
                var mode = button.ClickMode;
                button.Command = null;
                button.ClickMode = System.Windows.Controls.ClickMode.Press;
                var clicks = 0;
                System.Windows.RoutedEventHandler count = (_, _) => clicks++;
                button.Click += count;

                // From the song table: focus must stay there.
                if (songGrid.Items.Count > 0)
                {
                    songGrid.SelectedIndex = 0;
                    songGrid.UpdateLayout();
                    var row = (System.Windows.Controls.DataGridRow)songGrid.ItemContainerGenerator.ContainerFromIndex(0);
                    System.Windows.Input.Keyboard.Focus(FindFirst<System.Windows.Controls.DataGridCell>(row));
                }
                else
                    System.Windows.Input.Keyboard.Focus((IInputElement)main.FindName("SearchBox"));
                Settle(30);
                var before = Focused();
                Click(button);
                var after = Focused();
                var focusedAfter = button.IsKeyboardFocused;
                var ok = after == before && !focusedAfter && button.Focusable && clicks == 1;

                // Already focused by Tab: a click keeps it focused, as before.
                System.Windows.Input.Keyboard.Focus(button);
                Settle(30);
                Click(button);
                var keeps = button.IsKeyboardFocused && button.Focusable && clicks == 2;

                Console.WriteLine($"click focus: {name}: focus {before} -> {after}, button focused={focusedAfter} focusable={button.Focusable} "
                    + $"clicks={clicks} {(ok ? "OK" : "BROKEN")}; when Tab-focused, keeps focus={keeps} {(keeps ? "OK" : "BROKEN")}");
                all &= ok && keeps;

                button.Click -= count;
                button.Command = command;
                button.ClickMode = mode;
            }
            Console.WriteLine($"click focus: {(all ? "all OK" : "BROKEN")}");
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

        // --artmenu 1: opens the first hand-cursor art with a context menu off-screen
        // and prints its items. Since session 40 that is the playlist header picture:
        // the album header art has no menu (a right-click opens Edit Album Tags).
        if (Arg(args, "--artmenu") is not null)
        {
            var art = FindAll<System.Windows.Controls.Border>(main)
                .FirstOrDefault(b => b.ContextMenu is not null && b.Cursor == System.Windows.Input.Cursors.Hand);
            if (art?.ContextMenu is not { } menu)
            {
                Console.WriteLine("artmenu: no context menu on the header art (none since session 40: a right-click opens Edit Album Tags itself)");
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
                    // The item's highlight radius (spec: 3).
                    if (entry.Template?.FindName("Border", entry) is System.Windows.Controls.Border hl)
                    {
                        Console.WriteLine($"artmenu: '{entry.Header}' highlight corner={hl.CornerRadius.TopLeft:0.#}");
                    }
                }
                menu.IsOpen = false;
            }
        }

        // --trackmenu 1: the song row's context menu, opened off-screen, with each
        // item's command and parameter. Nothing is invoked: Analyze would show a
        // real window, and showing one takes focus.
        if (Arg(args, "--trackmenu") is not null)
        {
            var row = FindAll<System.Windows.Controls.DataGridRow>(main).FirstOrDefault(r => r.ContextMenu is not null);
            if (row?.ContextMenu is not { } menu)
            {
                Console.WriteLine("trackmenu: no context menu on a song row");
            }
            else
            {
                menu.PlacementTarget = row;
                menu.IsOpen = true;
                Settle(300);
                foreach (var entry in menu.Items.OfType<System.Windows.Controls.MenuItem>())
                {
                    var track = entry.CommandParameter as AudioFool.Core.Models.Track;
                    Console.WriteLine($"trackmenu: '{entry.Header}' command={entry.Command is not null} "
                        + $"canExecute={entry.Command?.CanExecute(entry.CommandParameter)} enabled={entry.IsEnabled} "
                        + $"param='{track?.Title}' row='{(row.Item as AudioFool.Core.Models.Track)?.Title}'");
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

                // --menucorners 1: the corner radius of each template part that WPF-UI
                // fixes, for the drop-down, its items, and the Libraries submenu
                // (opened too, and its folder rows). Spec: boxes 4, highlights 3.
                if (Arg(args, "--menucorners") is not null)
                {
                    // Two folder rows, so the data-bound items (which carry an
                    // ItemContainerStyle with no BasedOn) are measured too.
                    vm.FolderFilters.Add(new AudioFool.ViewModels.FolderFilterItem(@"C:\Users\Example\Music", enabled: true));
                    vm.FolderFilters.Add(new AudioFool.ViewModels.FolderFilterItem(@"D:\Music", enabled: false));

                    void Corners(System.Windows.Controls.MenuItem mi, string indent)
                    {
                        var parts = new List<string>();
                        foreach (var part in new[] { "SubmenuBorder", "Border", "CheckBoxIconBorder" })
                        {
                            if (mi.Template?.FindName(part, mi) is System.Windows.Controls.Border b)
                                parts.Add($"{part}={b.CornerRadius.TopLeft:0.#}");
                        }

                        Console.WriteLine($"menu: corners {indent}{mi.Header ?? "<logo>"} ({mi.Role}): {string.Join(", ", parts)}");
                    }

                    // A PNG path as the value renders the open drop-down itself (the
                    // popup's content, at 4x), with the Libraries submenu open too.
                    if (Arg(args, "--menucorners") is { } png && png.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                        && item.Template?.FindName("Popup", item) is System.Windows.Controls.Primitives.Popup dropDown
                        && dropDown.Child is FrameworkElement dropRoot)
                    {
                        var libs = item.Items.OfType<System.Windows.Controls.MenuItem>()
                            .First(m => m.Role == System.Windows.Controls.MenuItemRole.SubmenuHeader);
                        libs.IsSubmenuOpen = true;
                        Settle(200);
                        var (pw, ph) = ((int)Math.Ceiling(dropRoot.ActualWidth), (int)Math.Ceiling(dropRoot.ActualHeight));
                        var bmp = new RenderTargetBitmap(pw * 4, ph * 4, 384, 384, PixelFormats.Pbgra32);
                        bmp.Render(dropRoot);
                        var enc = new PngBitmapEncoder();
                        enc.Frames.Add(BitmapFrame.Create(bmp));
                        using (var fs = File.Create(png))
                        {
                            enc.Save(fs);
                        }

                        Console.WriteLine($"menu: drop-down rendered {pw}x{ph} -> {png}");
                        libs.IsSubmenuOpen = false;
                    }

                    Corners(item, "");
                    foreach (var child in item.Items.OfType<System.Windows.Controls.MenuItem>())
                    {
                        Corners(child, "  ");
                        if (child.Role == System.Windows.Controls.MenuItemRole.SubmenuHeader)
                        {
                            child.IsSubmenuOpen = true;
                            Settle(200);
                            Corners(child, "  ");
                            // Containers, not Items: the folder rows are bound view models.
                            for (var i = 0; i < child.Items.Count; i++)
                            {
                                if (child.ItemContainerGenerator.ContainerFromIndex(i) is System.Windows.Controls.MenuItem sub)
                                {
                                    Corners(sub, "    ");
                                    if (sub.Role == System.Windows.Controls.MenuItemRole.SubmenuHeader)
                                    {
                                        sub.IsSubmenuOpen = true;
                                        Settle(200);
                                        Corners(sub, "    ");
                                        for (var j = 0; j < sub.Items.Count; j++)
                                        {
                                            if (sub.ItemContainerGenerator.ContainerFromIndex(j) is System.Windows.Controls.MenuItem leaf)
                                            {
                                                Corners(leaf, "      ");
                                            }
                                        }

                                        sub.IsSubmenuOpen = false;
                                    }
                                }
                            }

                            child.IsSubmenuOpen = false;
                        }
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

        // --window waveform --file <audio> [--at 0.4]: plays it silently, waits
        // for the seekbar's waveform, moves to that fraction of the track and
        // renders the window, plus the seekbar alone at 3x next to it.
        // --window splice --long "a.flac;b.flac": plays a silently from 2 s before its
        // end into b and prints the log's gapless line (session 51). For the exact
        // join the two must share the shared mix rate (96 kHz here).
        if (which == "splice")
        {
            var logPath = Path.Combine(Path.GetTempPath(), "themelab-splice-" + Guid.NewGuid().ToString("N")[..8] + ".log");
            AudioFool.Core.Playback.PlaybackDiagnostics.LogPath = logPath;
            var files = (Arg(args, "--long") ?? throw new ArgumentException("--window splice needs --long \"a;b\""))
                .Split(';', StringSplitOptions.RemoveEmptyEntries);
            var tracks = files.Select(AudioFool.Core.Library.TagReader.Read).ToList();
            var ready = runtime.Initialise();
            engine.Volume = 0;
            var played = engine.Play(tracks, 0);
            Console.WriteLine($"init {ready} {runtime.InitError}, play {played}, warning '{engine.OutputWarning}', tracks {string.Join(" / ", tracks.Select(t => t.Title + " " + t.Duration))}");
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (!engine.HoldsFile(files[1]) && clock.ElapsedMilliseconds < 5000)
                Settle(20);
            // Off the 10 ms pull grid, so the track ends inside a pull.
            engine.Seek(engine.Duration - TimeSpan.FromSeconds(2.0037));
            Settle(4000);
            Console.WriteLine($"mix rate {runtime.SharedMixRate}, now playing '{engine.CurrentTrack?.Title}' at {engine.Position.TotalSeconds:0.00} s, output '{engine.OutputDescription}'");
            engine.Stop();
            Settle(500);
            if (File.Exists(logPath)) foreach (var line in File.ReadLines(logPath).Where(l => l.Contains("gapless") || l.Contains("track:") || l.Contains("device")))
                Console.WriteLine(line);
            return 0;
        }

        // --window ratechange: a Bit-Perfect change of sample rate between tracks
        // through the real engine and device, on two files of digital silence
        // (Bit-Perfect plays at full volume, so nothing else may be played). The
        // first ends 1 s after the seek; prints playback.log's "rate change:" line,
        // which says how much of its end the reopen dropped.
        if (which == "ratechange")
        {
            var logPath = Path.Combine(Path.GetTempPath(), "themelab-ratechange-" + Guid.NewGuid().ToString("N")[..8] + ".log");
            AudioFool.Core.Playback.PlaybackDiagnostics.LogPath = logPath;
            var ready = runtime.Initialise();
            Console.WriteLine($"init {ready} {runtime.InitError}, exclusive {runtime.SupportsExclusive}, rates {string.Join(",", runtime.ExclusiveRates)}");
            var rates = new[] { 44100, 96000 }.Where(runtime.ExclusiveRates.Contains).ToArray();
            if (!runtime.SupportsExclusive || rates.Length < 2)
            {
                Console.WriteLine("the default device can't play 44.1 and 96 kHz exclusively: nothing to check");
                return 1;
            }

            var dir = Path.Combine(Path.GetTempPath(), "themelab-ratechange-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(dir);
            var files = rates.Select((r, i) => WriteSilentWav(Path.Combine(dir, $"{i + 1} Silence {r}.wav"), r, seconds: 4)).ToArray();
            var tracks = files.Select(AudioFool.Core.Library.TagReader.Read).ToList();

            engine.OutputMode = OutputMode.Exclusive;
            var played = engine.Play(tracks, 0);
            Console.WriteLine($"play {played}, bit-perfect {engine.IsBitPerfect}, output '{engine.OutputDescription}', warning '{engine.OutputWarning}'");
            var ok = false;
            if (played && engine.IsBitPerfect)
            {
                var clock = System.Diagnostics.Stopwatch.StartNew();
                while (!engine.HoldsFile(files[1]) && clock.ElapsedMilliseconds < 5000)
                    Settle(20);
                // Off the 10 ms pull grid, so the track ends inside a pull.
                engine.Seek(engine.Duration - TimeSpan.FromSeconds(1.0037));
                if (Arg(args, "--stopinwait") is "1")
                {
                    // Stop while the old track's end plays out (its stream is gone,
                    // the new one not yet opened): the next track must not start.
                    clock.Restart();
                    while (engine.HoldsFile(files[0]) && clock.ElapsedMilliseconds < 3000)
                        Thread.Sleep(2);
                    engine.Stop();
                    Console.WriteLine($"stopped {clock.ElapsedMilliseconds} ms after the seek, as the first track's stream went");
                    Settle(2000);
                    Console.WriteLine($"state {engine.State}, holds the second track: {engine.HoldsFile(files[1])}");
                    var noLine = !File.Exists(logPath) || !File.ReadLines(logPath).Any(l => l.Contains("rate change:"));
                    var stoppedOk = engine.State == PlaybackState.Stopped && !engine.HoldsFile(files[1]) && noLine;
                    Console.WriteLine(stoppedOk ? "stayed stopped -> OK" : "WRONG: played on after Stop");
                    engine.OutputMode = OutputMode.Shared;
                    Directory.Delete(dir, recursive: true);
                    return stoppedOk ? 0 : 1;
                }

                Settle(3000);
                Console.WriteLine($"now '{engine.CurrentTrack?.Title}', output '{engine.OutputDescription}'");
                ok = engine.CurrentTrack?.FilePath == files[1];
            }

            engine.Stop();
            engine.OutputMode = OutputMode.Shared;
            Settle(300);
            var lines = File.Exists(logPath) ? File.ReadLines(logPath).Where(l => l.Contains("rate change:")).ToList() : [];
            foreach (var line in lines)
                Console.WriteLine(line);
            Console.WriteLine(ok && lines.Count == 1 ? "played through to the second track" : "WRONG: didn't reach the second track once");
            Directory.Delete(dir, recursive: true);
            return ok ? 0 : 1;
        }

        // --window lend: the About intro's loan of the device (session 64), through the
        // real engine and default device in Bit-Perfect, on digital silence. Playing,
        // paused, lent: the device must open exclusively for someone else; returned:
        // still paused at the same place, Bit-Perfect again, and Resume plays on.
        // Then the same from stopped. "ALL OK". Takes the DAC for a few seconds.
        if (which == "lend")
            return RunLend(engine, runtime);

        // --window playbackfixes (session 67): the playback batch through the real
        // engine and default device, on digital silence only (WAVs and a DSF of
        // DSD's idle pattern). Shared: songs that won't open skipped by the end of
        // a track, Next and Previous; a queue ending on one. Exclusive: Stop and the
        // end of a queue let go of the device; a reopen carries on from what was
        // heard; DoP never on a shared output. "ALL OK". Takes the DAC ~30 s.
        if (which == "playbackfixes")
            return RunPlaybackFixes(engine, runtime);

        // --window closex: the Statistics dialog, modal and off-screen, with mouse
        // messages for its close button posted to the thread's queue as real input
        // arrives (WPF-UI clicks title-bar buttons from WM_NC* messages). Cases: a
        // normal click; clicks WPF-UI loses, which the safety net must click
        // (CloseButtonSafetyNet, session 66): a WM_NCMOUSELEAVE between press and
        // release, a hit test off the X between them sent from another thread as
        // another program's would be, the second press of a double-click, a client
        // click while the mouse is captured; a close the window cancels, which must
        // stay cancelled whoever clicked; a press on X released on the caption,
        // which must do nothing. Nothing moves the real mouse. Prints the title-bar
        // click log, the hit tests between press and release included.
        if (which == "closex")
        {
            // The app's title-bar click log, to a scratch file printed at the end;
            // pointed there before anything is logged, so the real playback.log is
            // never written (see HANDOFF, Claude's shells and AppData).
            var clickLog = Path.Combine(Path.GetTempPath(), "themelab-closex-" + Guid.NewGuid().ToString("N")[..8] + ".log");
            AudioFool.Core.Playback.PlaybackDiagnostics.LogPath = clickLog;
            AudioFool.Services.TitleBarClickLog.Install();
            AudioFool.Services.CloseButtonSafetyNet.Install();

            LoadRealLibrary(vm);
            var libraryField = typeof(MainViewModel).GetField("_folderFilteredLibrary",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            var library = (AudioFool.Core.Library.MusicLibrary)libraryField.GetValue(vm)!;
            const uint NcHitTest = 0x84, NcDown = 0xA1, NcUp = 0xA2, NcDouble = 0xA3, NcLeave = 0x2A2, ClientDown = 0x201, ClientUp = 0x202;
            // Not a message: a hit test for the caption, sent from a worker thread.
            const uint ForeignHitTest = 0x10000 | NcHitTest;
            const int HtClose = 20, HtCaption = 2, Client = -1;

            var cases = new (string Name, (uint Msg, int Hit)[] Steps, bool CancelFirstClose, bool ShouldClose, int NetClicks)[]
            {
                ("normal click", [(NcDown, HtClose), (NcUp, HtClose)], false, true, 0),
                ("click lost to a mouse leave", [(NcDown, HtClose), (NcLeave, 0), (NcUp, HtClose)], false, true, 1),
                ("click lost to another program's hit test", [(NcDown, HtClose), (ForeignHitTest, HtCaption), (NcUp, HtClose)], false, true, 1),
                ("second press of a double-click", [(NcDouble, HtClose), (NcUp, HtClose)], false, true, 1),
                ("close the window cancels", [(NcDown, HtClose), (NcUp, HtClose)], true, false, 0),
                ("lost click, close the window cancels", [(NcDown, HtClose), (NcLeave, 0), (NcUp, HtClose)], true, false, 1),
                ("press on X, release on caption", [(NcDown, HtClose), (NcUp, HtCaption)], false, false, 0),
                // While something in the window holds the mouse captured, Windows
                // sends a click on the X as a plain client click.
                ("click on X as a client click (mouse captured)", [(ClientDown, Client), (ClientUp, Client)], false, true, 1),
            };
            var allOk = true;

            foreach (var test in cases)
            {
                var statsVm = new StatisticsViewModel(AudioFool.Core.Library.LibraryStatistics.Compute(library), false);
                var dialog = new StatisticsWindow(statsVm, main) { ShowActivated = false, ShowInTaskbar = false };
                dialog.Loaded += (_, _) => { dialog.Left = -20000; dialog.Top = -20000; };
                var closed = false;
                var reported = false;
                var titleBarClicks = 0;
                var cancelled = 0;
                dialog.Closed += (_, _) => closed = true;
                dialog.AddHandler(Wpf.Ui.Controls.TitleBar.CloseClickedEvent,
                    new RoutedEventHandler((_, _) => titleBarClicks++));
                if (test.CancelFirstClose)
                    dialog.Closing += (_, e) => { if (cancelled++ == 0) e.Cancel = true; };

                dialog.Loaded += async (_, _) =>
                {
                    await Task.Delay(400);
                    var netBefore = SafetyNetClicks(clickLog);
                    var close = FindAll<Wpf.Ui.Controls.TitleBarButton>(dialog)
                        .First(b => b.ButtonType == Wpf.Ui.Controls.TitleBarButtonType.Close);
                    var hwnd = new System.Windows.Interop.WindowInteropHelper(dialog).Handle;
                    var at = close.PointToScreen(new Point(close.ActualWidth / 2, close.ActualHeight / 2));
                    var onClose = (IntPtr)(((int)at.Y << 16) | ((int)at.X & 0xFFFF));
                    var caption = close.PointToScreen(new Point(-300, close.ActualHeight / 2));
                    var onCaption = (IntPtr)(((int)caption.Y << 16) | ((int)caption.X & 0xFFFF));
                    var inClient = close.TranslatePoint(new Point(close.ActualWidth / 2, close.ActualHeight / 2), dialog);
                    var onCloseClient = (IntPtr)(((int)inClient.Y << 16) | ((int)inClient.X & 0xFFFF));

                    NcSend(hwnd, NcHitTest, IntPtr.Zero, onClose);   // hover, as the pointer arriving does
                    foreach (var (msg, hit) in test.Steps)
                    {
                        if (msg == ForeignHitTest)
                            await Task.Run(() => NcSend(hwnd, NcHitTest, IntPtr.Zero, onCaption));
                        else
                            PostMessage(hwnd, msg, hit == Client ? (IntPtr)1 : (IntPtr)hit,
                                hit == Client ? onCloseClient : hit == HtCaption ? onCaption : onClose);
                        await Task.Delay(30);
                    }

                    await Task.Delay(500);
                    var netClicks = SafetyNetClicks(clickLog) - netBefore;
                    var ok = closed == test.ShouldClose && netClicks == test.NetClicks;
                    allOk &= ok;
                    Console.WriteLine($"{test.Name}: closed {closed} (want {test.ShouldClose}), CloseClicked {titleBarClicks}, "
                        + $"safety net clicked {netClicks} (want {test.NetClicks}), "
                        + $"closes cancelled {(test.CancelFirstClose ? cancelled : 0)} {(ok ? "OK" : "WRONG")}");
                    if (!closed)
                    {
                        cancelled = 1;
                        dialog.Close();
                    }
                    reported = true;
                };

                dialog.ShowDialog();
                // A click the safety net makes closes the dialog before its case has
                // reported, and the next case's clicks mustn't count towards this one.
                while (!reported)
                    Settle(20);
                Settle(100);
            }

            Settle(400);   // the log's writer flushes every few ms
            Console.WriteLine(allOk ? "ALL OK" : "SOME WRONG");
            Console.WriteLine("--- title-bar click log ---");
            foreach (var line in File.Exists(clickLog) ? File.ReadAllLines(clickLog) : [])
                Console.WriteLine(line);
            return allOk ? 0 : 1;
        }
        if (which == "device")
        {
            RunDeviceSwitch(vm, engine, runtime, Arg(args, "--long") ?? throw new ArgumentException("--window device needs --long \"a.flac;b.flac\""));
            return 0;
        }

        // --window reclaim --file <audio>: the post-scan memory clean-up while
        // music plays (silently): it must wait, the feed must not stall, and it
        // must run once playback pauses. Also piles up garbage while playing, as a
        // scan does, to see the GC's own collections. Log to a scratch file.
        if (which == "reclaim")
        {
            var logPath = Path.Combine(Path.GetTempPath(), "themelab-reclaim-" + Guid.NewGuid().ToString("N")[..8] + ".log");
            AudioFool.Core.Playback.PlaybackDiagnostics.LogPath = logPath;
            LoadRealLibrary(vm);
            var file = Arg(args, "--file") ?? throw new ArgumentException("--window reclaim needs --file <audio file>");
            engine.Volume = 0;
            engine.Play([AudioFool.Core.Library.TagReader.Read(file)], 0);
            Settle(3000);
            Console.WriteLine($"playing: {engine.State}, GC latency mode {System.Runtime.GCSettings.LatencyMode}");

            var reclaim = typeof(MainViewModel).GetMethod("ReclaimScanMemory", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            var pauseBefore = GC.GetTotalPauseDuration();
            reclaim.Invoke(vm, null);
            Settle(1500);
            Console.WriteLine($"clean-up asked for while playing: GC paused the app {(GC.GetTotalPauseDuration() - pauseBefore).TotalMilliseconds:0} ms in 1.5 s");

            // A scan's worth of churn: many short-lived buffers and strings.
            var churnClock = System.Diagnostics.Stopwatch.StartNew();
            pauseBefore = GC.GetTotalPauseDuration();
            var gen2 = GC.CollectionCount(2);
            var survivors = new List<object>();
            for (var i = 0; i < 3000; i++)
            {
                var block = new object[2_000];
                for (var j = 0; j < block.Length; j++) block[j] = new string('y', 32 + (j % 64));
                if (i % 3 == 0) survivors.Add(block);
                if (i % 200 == 0) Settle(1);
            }
            Settle(500);
            Console.WriteLine($"churn while playing: {churnClock.ElapsedMilliseconds} ms, {GC.CollectionCount(2) - gen2} full collections, "
                + $"GC paused the app {(GC.GetTotalPauseDuration() - pauseBefore).TotalMilliseconds:0} ms in all, "
                + $"longest single pause {GC.GetGCMemoryInfo(GCKind.Any).PauseDurations.ToArray().DefaultIfEmpty().Max().TotalMilliseconds:0} ms");
            survivors.Clear();

            engine.Pause();
            Settle(2000);
            Console.WriteLine($"paused: {engine.State}, GC latency mode {System.Runtime.GCSettings.LatencyMode}");
            engine.Stop();
            Settle(500);
            Console.WriteLine("---- playback log ----");
            Console.WriteLine(File.ReadAllText(logPath));
            File.Delete(logPath);
            return 0;
        }

        if (which == "waveform")
        {
            RunWaveform(main, vm, engine, Arg(args, "--file") ?? throw new ArgumentException("--window waveform needs --file <audio file>"),
                double.Parse(Arg(args, "--at") ?? "0.4", CultureInfo.InvariantCulture), outPath, w, h, scale);
            return 0;
        }

        if (which == "themeswitch")
            return RunThemeSwitch(main, vm, engine,
                Arg(args, "--file") ?? throw new ArgumentException("--window themeswitch needs --file <audio file>"),
                Arg(args, "--artfile"), outPath, w, h, scale);

        if (which is "edit" or "queue" or "clicks" or "nowplayingart" or "multiedit" or "albumeditor" or "trackeditor" or "covers" or "pngconvert" or "safesave" or "albumkeep" or "folderart" or "findreplace")
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
                if (which == "albumkeep")
                    RunAlbumKeep(vm);
                else if (which == "findreplace")
                    RunFindReplace(main, vm, outPath, scale);
                else if (which == "folderart")
                    RunFolderArt(vm);
                else if (which == "safesave")
                    RunSafeSave(main, vm, Arg(args, "--long") ?? throw new ArgumentException("--window safesave needs --long \"a;b;...\""));
                else if (which == "pngconvert")
                    RunPngConvert(main, vm);
                else if (which == "covers")
                    RunCovers(main, vm);
                else if (which == "trackeditor")
                    RunTrackEditor(main, vm);
                else if (which == "albumeditor")
                    RunAlbumEditor(main, vm, outPath, scale);
                else if (which == "multiedit")
                    RunMultiEdit(main, vm, outPath, w, h, scale);
                else if (which == "clicks")
                    RunClicks(main, vm);
                else if (which == "nowplayingart")
                    RunNowPlayingArt(vm);
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

        if (which == "intro")
            return RunIntro(main, outPath, w, h, scale);

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

            // Rendered at the size SizeToArt picked. Shown off-screen like the main
            // window, unactivated, so WPF-UI's title bar loads: it copies each
            // window button's glyph colour across only once the window is loaded.
            var (vw, vh) = (Math.Ceiling(viewer.Width), Math.Ceiling(viewer.Height));
            viewer.ShowActivated = false;
            viewer.ShowInTaskbar = false;
            viewer.Left = -20000;
            viewer.Top = -20000;
            viewer.Show();
            Settle(300);
            var viewerRoot = (FrameworkElement)viewer.Content;
            viewerRoot.Measure(new Size(vw, vh));
            viewerRoot.Arrange(new Rect(0, 0, vw, vh));
            viewerRoot.UpdateLayout();
            Settle(300);
            var image = (FrameworkElement)viewer.FindName("ArtImage");
            var at = image.TranslatePoint(new Point(0, 0), viewerRoot);
            Console.WriteLine($"artview: window {vw}x{vh}  image at {at.X:0.#},{at.Y:0.#} size {image.ActualWidth:0.#}x{image.ActualHeight:0.#}");
            PrintTitleBar((Wpf.Ui.Controls.TitleBar)viewer.FindName("Bar"), viewerRoot);
            Save(viewerRoot, outPath, vw, vh, scale);
            viewer.Close();
            Console.WriteLine("wrote " + outPath);
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
            IReadOnlyList<AudioFool.Core.Models.Track>? scratchTracks = null;
            var albumName = Arg(args, "--album");
            var trackName = Arg(args, "--track");
            if (albumName is not null || trackName is not null)
            {
                var cached = AudioFool.Core.Library.LibraryCache.Load();

                // --scratch <folder>: the album is built from COPIES of the audio files
                // in that folder (and its sub-folders), made in a new temp folder, so
                // a button that writes beside the files writes there and not on the
                // real drive. --album then only needs to be present.
                var scratch = Arg(args, "--scratch");
                if (scratch is not null)
                {
                    var copyRoot = Path.Combine(Path.GetTempPath(), "AudioFool-scratch-" + Guid.NewGuid().ToString("N")[..8]);
                    var copies = new List<AudioFool.Core.Models.Track>();
                    foreach (var file in Directory.EnumerateFiles(scratch, "*", SearchOption.AllDirectories)
                                 .Where(f => AudioFool.Core.Library.AudioFormats.IsSupported(f)).Take(60))
                    {
                        var copyTarget = Path.Combine(copyRoot, Path.GetRelativePath(scratch, file));
                        Directory.CreateDirectory(Path.GetDirectoryName(copyTarget)!);
                        File.Copy(file, copyTarget);
                        copies.Add(AudioFool.Core.Library.TagReader.Read(copyTarget));
                    }

                    Console.WriteLine($"scratch: {copies.Count} files copied to {copyRoot}");
                    cached = null;
                    scratchTracks = copies;
                }

                var library = AudioFool.Core.Library.LibraryScanner.Build(scratchTracks ?? cached?.Tracks ?? SampleTracks());
                var clock = System.Diagnostics.Stopwatch.StartNew();
                if (albumName is not null)
                {
                    var album = scratch is not null
                        ? library.Artists.SelectMany(a => a.Albums).First()
                        : library.Artists.SelectMany(a => a.Albums)
                            .First(a => string.Equals(a.Title, albumName, StringComparison.OrdinalIgnoreCase));
                    // --pick "1-9" opens the dialog for those tracks of the album
                    // (1-based, in grid order) as if they were selected in the grid.
                    if (Arg(args, "--pick") is { } pick)
                    {
                        var bounds = pick.Split('-');
                        var (from, to) = (int.Parse(bounds[0]), int.Parse(bounds[^1]));
                        var picked = album.Tracks.Skip(from - 1).Take(to - from + 1).ToList();
                        SynchronizationContext.SetSynchronizationContext(
                            new System.Windows.Threading.DispatcherSynchronizationContext(System.Windows.Threading.Dispatcher.CurrentDispatcher));
                        editVm = new TagEditViewModel(picked);
                        WaitForDetails(editVm);
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
                        WaitForDetails(editVm);
                        Console.WriteLine($"album: {album.Title} ({album.Tracks.Count} tracks), opened in {clock.ElapsedMilliseconds} ms");
                        Console.WriteLine($"  artist    '{editVm.Artist}' [{editVm.ArtistPlaceholder}]  album artist '{editVm.AlbumArtist}' [{editVm.AlbumArtistPlaceholder}]");
                        Console.WriteLine($"  album     '{editVm.AlbumTitle}' [{editVm.AlbumTitlePlaceholder}]  year '{editVm.Year}' [{editVm.YearPlaceholder}]  can save={editVm.CanSave}");
                        Settle(1500);
                        Console.WriteLine($"  art       '{editVm.ArtSizeText}' '{editVm.ArtFormatText}' [{editVm.ArtSourceText}]  preview={editVm.ArtPreview?.PixelWidth}px");

                        // --saveembedded 1: press "Save Embedded Art" and report. Meant for
                        // --scratch (it writes cover.jpg beside the files).
                        if (Arg(args, "--saveembedded") is not null)
                        {
                            Console.WriteLine($"  button    enabled={editVm.SaveEmbeddedArtCommand.CanExecute(null)}");
                            var pressed = System.Diagnostics.Stopwatch.StartNew();
                            editVm.SaveEmbeddedArtCommand.Execute(null);
                            while (editVm.IsExtractingArt && pressed.Elapsed < TimeSpan.FromSeconds(60))
                                Settle(50);
                            Console.WriteLine($"  extracted in {pressed.ElapsedMilliseconds} ms, message: {editVm.ArtMessage}");
                            Console.WriteLine($"  footer    '{editVm.FooterMessage}'");
                            Console.WriteLine($"  button    enabled again={editVm.SaveEmbeddedArtCommand.CanExecute(null)}");

                            var embeddedHashes = album.Tracks
                                .Select(t => AudioFool.Core.Library.TagReader.ReadEmbeddedArt(t.FilePath))
                                .OfType<byte[]>()
                                .Select(b => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(b)))
                                .ToHashSet();
                            foreach (var dir in album.Tracks.Select(t => Path.GetDirectoryName(t.FilePath)!).Distinct(StringComparer.OrdinalIgnoreCase))
                            {
                                var cover = Path.Combine(dir, "cover.jpg");
                                if (!File.Exists(cover))
                                {
                                    Console.WriteLine($"  folder    {Path.GetFileName(dir)}: no cover.jpg");
                                    continue;
                                }

                                var bytes = File.ReadAllBytes(cover);
                                var info = AudioFool.Core.Art.ImageInfo.Read(bytes);
                                var same = embeddedHashes.Contains(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)));
                                Console.WriteLine($"  folder    {Path.GetFileName(dir)}: cover.jpg {bytes.Length} B {info?.SizeText} {info?.FormatText}, "
                                    + $"identical to an embedded picture={same}");
                            }
                        }

                        // --useart <file>: hand the dialog a new cover as Search Internet does.
                        if (Arg(args, "--useart") is { } artFile)
                        {
                            editVm.UseDownloadedArt(File.ReadAllBytes(artFile));
                            Console.WriteLine($"  new art   '{editVm.ArtSizeText}' '{editVm.ArtFormatText}' [{editVm.ArtSourceText}]");
                        }

                        // --pickimage <file>: as Choose Image… with that file picked, then
                        // the file is moved away, as before a Save, and Save's cover read.
                        if (Arg(args, "--pickimage") is { } pickedFile)
                        {
                            editVm.UseImageFile(pickedFile);
                            Console.WriteLine($"  picked    '{editVm.ArtSizeText}' '{editVm.ArtFormatText}' [{editVm.ArtSourceText}]  footer '{editVm.FooterMessage}'");
                            var moved = pickedFile + ".moved";
                            File.Move(pickedFile, moved);
                            var payload = editVm.PickedArtPayload();
                            File.Move(moved, pickedFile);
                            Console.WriteLine($"  on Save   {(payload is null ? "no new cover" : $"{payload.MimeType} {payload.Bytes.Length} B")} (file moved away first)");
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
            var tagsBar = Descendants<Wpf.Ui.Controls.TitleBar>(root).First();
            RaiseLoaded(tagsBar);
            Settle(400);
            Save(root, outPath, w, height, scale);
            Console.WriteLine($"  size {w}x{height} (content measured {root.DesiredSize.Height:0.##})");
            PrintTitleBar(tagsBar, root);
            foreach (var box in Descendants<System.Windows.Controls.TextBox>(root).Where(t => t.ActualWidth > 0))
            {
                var p = box.TranslatePoint(new Point(0, 0), root);
                Console.WriteLine($"  field {System.Windows.Automation.AutomationProperties.GetName(box),-13} at {p.X:0.#},{p.Y:0.#} size {box.ActualWidth:0.#}x{box.ActualHeight:0.#}"
                    + $"  text '{box.Text}' placeholder '{AudioFool.Theming.Placeholder.GetText(box)}'");
            }
            foreach (var b in Descendants<System.Windows.Controls.Button>(root).Where(b => b.ActualWidth > 0 && b is not Wpf.Ui.Controls.TitleBarButton))
            {
                var p = b.TranslatePoint(new Point(0, 0), root);
                var name = b.Content as string ?? System.Windows.Automation.AutomationProperties.GetName(b);
                Console.WriteLine($"  button '{name}' at {p.X:0.#},{p.Y:0.#} size {b.ActualWidth:0.#}x{b.ActualHeight:0.#} weight {b.FontWeight} enabled={b.IsEnabled}");
            }
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
            Console.WriteLine($"click: '{chosenRow.Label}' enabled={rowButton.IsEnabled} "
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
            // The header line, and the album Edit Album Tags would open on: the
            // same lookup EditAlbumTags makes, read through the private library.
            if (vm.SelectedAlbum is { } headerAlbum
                && typeof(MainViewModel).GetField("_folderFilteredLibrary", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                       ?.GetValue(vm) is AudioFool.Core.Library.MusicLibrary wholeLibrary)
            {
                var whole = wholeLibrary.WholeAlbumOf(headerAlbum.Album);
                Console.WriteLine($"main: header='{vm.AlbumHeaderTrackCount}' shown={headerAlbum.Album.Tracks.Count} "
                    + $"album dialog would edit={whole.Tracks.Count} "
                    + $"(#{string.Join(",", whole.Tracks.Select(t => t.TrackNumber))})");
            }
            Console.WriteLine($"main: chip visible={chip.IsVisible} size={chip.ActualWidth:0}x{chip.ActualHeight:0} "
                + $"uia='{System.Windows.Automation.AutomationProperties.GetName(chip)}'");

            Save(main, outPath, w, h, scale);

            // --fix: an album-tag edit on the selected album, fed through the same
            // private ReplaceTracksInLibrary a real save ends with - in memory only,
            // no file is written - to check the fixed tracks leave the filter.
            if (Arg(args, "--fix") is { } year && vm.SelectedAlbum is { } fixAlbum)
            {
                var edit = new AudioFool.Core.Library.AlbumTagEdit
                {
                    Artist = fixAlbum.Album.ArtistName,
                    AlbumArtist = fixAlbum.Album.ArtistName,
                    Album = fixAlbum.Album.Title,
                    Date = new AudioFool.Core.Library.DateEdit(int.Parse(year, CultureInfo.InvariantCulture), null),
                };
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
            var searchBar = Descendants<Wpf.Ui.Controls.TitleBar>(root).First();
            RaiseLoaded(searchBar);
            Settle(400);
            Save(root, outPath, w, h, scale);
            PrintTitleBar(searchBar, root);
            foreach (var box in Descendants<System.Windows.Controls.TextBox>(root).Where(t => t.ActualWidth > 0))
            {
                var p = box.TranslatePoint(new Point(0, 0), root);
                Console.WriteLine($"  textbox {box.Name} at {p.X:0.#},{p.Y:0.#} size {box.ActualWidth:0.#}x{box.ActualHeight:0.#} text '{box.Text}'");
            }
            foreach (var b in Descendants<System.Windows.Controls.Button>(root).Where(b => b.ActualWidth > 0 && b is not Wpf.Ui.Controls.TitleBarButton))
            {
                var p = b.TranslatePoint(new Point(0, 0), root);
                Console.WriteLine($"  button '{System.Windows.Automation.AutomationProperties.GetName(b)}{b.Content as string}' at {p.X:0.#},{p.Y:0.#} size {b.ActualWidth:0.#}x{b.ActualHeight:0.#} weight {b.FontWeight} enabled={b.IsEnabled}");
            }
            var items = Descendants<System.Windows.Controls.ListBoxItem>(root).ToList();
            foreach (var item in items.Take(3))
            {
                var frame = Descendants<System.Windows.Controls.Border>(item).First(x => x.Width > 0 && !double.IsNaN(x.Width));
                var p = frame.TranslatePoint(new Point(0, 0), root);
                Console.WriteLine($"  cover frame at {p.X:0.#},{p.Y:0.#} size {frame.ActualWidth:0.#}  border {frame.BorderBrush}  selected={item.IsSelected}");
            }
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
            var lfmBar = Descendants<Wpf.Ui.Controls.TitleBar>(root).First();
            RaiseLoaded(lfmBar);
            Settle(400);
            Save(root, outPath, w, height, scale);
            Console.WriteLine($"  size {w}x{height}");
            PrintTitleBar(lfmBar, root);
            foreach (var b in Descendants<System.Windows.Controls.Button>(root).Where(b => b.Visibility == Visibility.Visible && b.ActualWidth > 0 && b is not Wpf.Ui.Controls.TitleBarButton))
            {
                var p = b.TranslatePoint(new Point(0, 0), root);
                Console.WriteLine($"  button '{b.Content}' at {p.X:0.#},{p.Y:0.#} size {b.ActualWidth:0.#}x{b.ActualHeight:0.#} weight {b.FontWeight} enabled={b.IsEnabled}");
            }
            foreach (var t in Descendants<System.Windows.Controls.TextBox>(root).Where(t => t.ActualWidth > 0))
            {
                var p = t.TranslatePoint(new Point(0, 0), root);
                Console.WriteLine($"  textbox {t.Name} at {p.X:0.#},{p.Y:0.#} size {t.ActualWidth:0.#}x{t.ActualHeight:0.#} enabled={t.IsEnabled}");
            }
        }
        else if (which == "analysis")
        {
            // --window analysis --file <audio> [--state working|error]: the Analyze
            // window on a real file, read only. Never shown (it centres itself over
            // its owner on Loaded), so the analysis is run here instead, with the
            // dispatcher pumped until it finishes. --state working renders the
            // progress line at 40% without running; --state error a missing file.
            var file = Arg(args, "--file") ?? throw new ArgumentException("--window analysis needs --file <audio file>");
            var state = Arg(args, "--state");
            runtime.Initialise();
            var track = state == "error"
                ? new AudioFool.Core.Models.Track { FilePath = file + ".missing", Title = "Missing file", Kind = "FLAC", BitDepth = 16, SampleRate = 44100 }
                : AudioFool.Core.Library.TagReader.Read(file);

            var analysisVm = new AnalysisViewModel(track);
            var dialog = new AnalysisWindow(analysisVm, main);
            dialog.ApplyTemplate();

            var clock = System.Diagnostics.Stopwatch.StartNew();
            if (state == "working")
            {
                analysisVm.Progress = 0.4;
                analysisVm.Status = "Reading the file… 40%";
            }
            else
            {
                var run = analysisVm.RunAsync(CancellationToken.None);
                while (!run.IsCompleted)
                    Settle(50);
            }

            Console.WriteLine($"analysis: {track.FilePath}");
            Console.WriteLine($"  {analysisVm.Subtitle}  ({clock.ElapsedMilliseconds} ms)");
            if (analysisVm.Error is { } error)
                Console.WriteLine($"  error: {error}");
            if (analysisVm.Opinion is { } opinion)
            {
                Console.WriteLine($"  {opinion.Verdict}: {opinion.Headline}");
                foreach (var e in analysisVm.Explanations)
                    Console.WriteLine($"    - {e}");
                Console.WriteLine($"  facts: {analysisVm.Facts}");
            }

            var root = (FrameworkElement)dialog.Content;
            var aw = Arg(args, "--w") is null ? Math.Ceiling(dialog.Width) : w;
            root.Measure(new Size(aw, double.PositiveInfinity));
            var height = Math.Ceiling(root.DesiredSize.Height);
            root.Arrange(new Rect(0, 0, aw, height));
            root.UpdateLayout();
            var analysisBar = Descendants<Wpf.Ui.Controls.TitleBar>(root).First();
            RaiseLoaded(analysisBar);
            Settle(400);
            root.UpdateLayout();
            Save(root, outPath, aw, height, scale);
            Console.WriteLine($"  size {aw}x{height}");
            PrintTitleBar(analysisBar, root);

            var frame = (FrameworkElement)dialog.FindName("PictureFrame");
            var frameAt = frame.TranslatePoint(new Point(0, 0), root);
            Console.WriteLine($"  picture frame at {frameAt.X:0.#},{frameAt.Y:0.#} size {frame.ActualWidth:0.#}x{frame.ActualHeight:0.#}");
            foreach (var name in new[] { "FrequencyAxis", "TimeAxis", "LegendAxis", "Overlay" })
            {
                var canvas = (System.Windows.Controls.Canvas)dialog.FindName(name);
                var labels = canvas.Children.OfType<System.Windows.Controls.TextBlock>()
                    .Select(t => $"{t.Text}@{System.Windows.Controls.Canvas.GetLeft(t):0},{System.Windows.Controls.Canvas.GetTop(t):0}");
                var lines = canvas.Children.OfType<System.Windows.Shapes.Line>().Select(l => $"line y {l.Y1:0.#}");
                Console.WriteLine($"  {name}: {string.Join("  ", labels.Concat(lines))}");
            }
            foreach (var b in Descendants<System.Windows.Controls.Button>(root).Where(b => b.ActualWidth > 0 && b is not Wpf.Ui.Controls.TitleBarButton))
            {
                var p = b.TranslatePoint(new Point(0, 0), root);
                Console.WriteLine($"  button '{b.Content}' at {p.X:0.#},{p.Y:0.#} size {b.ActualWidth:0.#}x{b.ActualHeight:0.#}");
            }
        }
        else if (which == "clear")
        {
            // --window clear --qualitycache <file> [--clearances <file>]: clearing
            // songs from Quality Check rows, over the real library (read-only) with
            // the check's results and the clearances in scratch files. Picks the
            // first row with songs, opens the song and album menus off-screen,
            // clears an album then a song, puts one back from "Cleared by you",
            // then the Analyze window's buttons on a flagged file (read, silently).
            var qualityPath = Arg(args, "--qualitycache") ?? throw new ArgumentException("--window clear needs --qualitycache <file>");
            LoadRealLibrary(vm);
            vm.QualityCachePath = qualityPath;
            runtime.Initialise();
            main.UpdateLayout();

            void Section(string title)
            {
                using var q = new QualitySectionViewModel(vm);
                Console.WriteLine($"[{title}] {q.Summary}");
                foreach (var r in q.Rows.Concat(q.ClearedRows)) Console.WriteLine($"    {r.Label,-30} {r.Value}");
            }

            void Menus(string when)
            {
                main.UpdateLayout();
                Settle(200);
                var songRow = FindAll<System.Windows.Controls.DataGridRow>(main).FirstOrDefault(r => r.ContextMenu is not null);
                var albumRow = FindAll<System.Windows.Controls.ListBoxItem>(main)
                    .FirstOrDefault(r => r.DataContext is AlbumItemViewModel && r.ContextMenu is not null && r.IsVisible);
                foreach (var (name, row) in new (string, FrameworkElement?)[] { ("song", songRow), ("album", albumRow) })
                {
                    if (row?.ContextMenu is not { } menu)
                    {
                        Console.WriteLine($"  {when} {name} menu: none");
                        continue;
                    }

                    menu.PlacementTarget = row;
                    menu.IsOpen = true;
                    Settle(200);
                    var items = menu.Items.OfType<System.Windows.Controls.MenuItem>()
                        .Select(m => $"'{m.Header}'{(m.Visibility == Visibility.Visible ? "" : " (hidden)")}{(m.Command?.CanExecute(m.CommandParameter) == false ? " (disabled)" : "")}");
                    Console.WriteLine($"  {when} {name} menu: {string.Join(", ", items)}");
                    menu.IsOpen = false;
                }
            }

            Section("before");
            Menus("no filter:");

            using (var q = new QualitySectionViewModel(vm))
            {
                var row = q.Rows.FirstOrDefault(r => r.IsClickable)
                    ?? throw new InvalidOperationException("no Quality Check row has songs; run a check first");
                vm.ApplyStatisticsChoice(row);
                Console.WriteLine($"filter on: '{vm.LibraryFilter}' flag={vm.LibraryFilter?.QualityFlag}  header='{vm.QualityClearHeader}' canClear={vm.CanClearQuality} canPutBack={vm.CanPutBackQuality}");
                Console.WriteLine($"  status: {vm.StatusText}");
            }
            Menus("filtered:");

            var album = vm.SelectedAlbum ?? throw new InvalidOperationException("no album selected under the filter");
            var albumSongs = album.Album.Tracks.Count;
            vm.ClearQualityForAlbumsCommand.Execute(album);
            Console.WriteLine($"cleared album '{album.Title}' ({albumSongs} shown): {vm.StatusText}");
            Console.WriteLine($"  album still listed: {vm.Albums.Any(a => a.Title == album.Title && a.Album.ArtistName == album.Album.ArtistName)}");

            if (vm.Tracks.FirstOrDefault() is { } song)
            {
                vm.SelectedTracks = [song];
                vm.ClearQualityForSongsCommand.Execute(null);
                Console.WriteLine($"cleared song '{song.DisplayTitle}': {vm.StatusText}");
                vm.ClearQualityForSongsCommand.Execute(null);
                Console.WriteLine($"  again (stale selection): {vm.StatusText}");
            }

            Section("after clearing");

            using (var q = new QualitySectionViewModel(vm))
            {
                var cleared = q.ClearedRows.Single();
                vm.ApplyStatisticsChoice(cleared);
                Console.WriteLine($"filter on: '{vm.LibraryFilter}' header='{vm.QualityClearHeader}' canClear={vm.CanClearQuality} canPutBack={vm.CanPutBackQuality}");
                Console.WriteLine($"  status: {vm.StatusText}");
            }
            Menus("cleared:");

            if (vm.Tracks.FirstOrDefault() is { } back)
            {
                vm.SelectedTracks = [back];
                vm.PutBackQualityForSongsCommand.Execute(null);
                Console.WriteLine($"put back '{back.DisplayTitle}': {vm.StatusText}");
            }

            Section("after put back");
            Console.WriteLine($"clearances file: {vm.QualityClearancesPath}");
            Console.WriteLine(File.Exists(vm.QualityClearancesPath) ? File.ReadAllText(vm.QualityClearancesPath) : "(none)");

            // Playlist mode hides the items whatever the filter.
            vm.IsPlaylistMode = true;
            Console.WriteLine($"playlist mode: canClear={vm.CanClearQuality} canPutBack={vm.CanPutBackQuality}");
            vm.IsPlaylistMode = false;

            // The Analyze window on a song still flagged: a row's first song.
            vm.LibraryFilter = null;
            var stats = vm.ComputeQualityStatistics();
            var flagged = stats.Gaps.Where(g => g.Tracks > 0)
                .Select(g => g.Filter.Apply((AudioFool.Core.Library.LibraryCache.Load()?.Tracks ?? [])).FirstOrDefault(t => File.Exists(t.FilePath)))
                .FirstOrDefault(t => t is not null);
            if (flagged is null)
            {
                Console.WriteLine("analysis: no flagged song on the drive");
            }
            else
            {
                var analysisVm = new AnalysisViewModel(flagged, vm);
                var dialog = new AnalysisWindow(analysisVm, main);
                dialog.ApplyTemplate();
                var run = analysisVm.RunAsync(CancellationToken.None);
                while (!run.IsCompleted)
                    Settle(50);

                void Buttons(string when) => Console.WriteLine(
                    $"analysis {when}: [{string.Join(", ", analysisVm.ClearActions.Select(a => a.Label))}] note='{analysisVm.ClearedNote}'");

                Console.WriteLine($"analysis: {flagged.FilePath}");
                Console.WriteLine($"  {analysisVm.Opinion?.Verdict}: {analysisVm.Opinion?.Headline}  check flags: {string.Join(",", vm.CheckedQualityFlags(flagged))}");
                Buttons("before");
                analysisVm.ClearActions[0].Command.Execute(null);
                Buttons("after clearing");
                Console.WriteLine($"  status: {vm.StatusText}");

                var root = (FrameworkElement)dialog.Content;
                var aw = Math.Ceiling(dialog.Width);
                root.Measure(new Size(aw, double.PositiveInfinity));
                var height = Math.Ceiling(root.DesiredSize.Height);
                root.Arrange(new Rect(0, 0, aw, height));
                root.UpdateLayout();
                RaiseLoaded(Descendants<Wpf.Ui.Controls.TitleBar>(root).First());
                Settle(400);
                root.UpdateLayout();
                Save(root, outPath, aw, height, scale);
                Console.WriteLine($"  size {aw}x{height}");
                foreach (var b in Descendants<System.Windows.Controls.Button>(root).Where(b => b.ActualWidth > 0 && b is not Wpf.Ui.Controls.TitleBarButton))
                {
                    var p = b.TranslatePoint(new Point(0, 0), root);
                    Console.WriteLine($"  button '{b.Content}' at {p.X:0.#},{p.Y:0.#} size {b.ActualWidth:0.#}x{b.ActualHeight:0.#}");
                }

                analysisVm.ClearActions[0].Command.Execute(null);
                Buttons("after put back");
                dialog.Close();
            }
        }
        else if (which == "stats")
        {
            // The real library, read from the cache and never written back:
            // LibraryCache.Load only opens the file for reading. Falls back to
            // the sample tracks when there is no cache on this machine.
            var cached = AudioFool.Core.Library.LibraryCache.Load();
            // --cutoffdemo 1: scratch albums with cut-off covers instead (session 70).
            var cutOffDemo = Arg(args, "--cutoffdemo") is not null;
            var tracks = cutOffDemo ? LoadCutOffLibrary(vm) : cached?.Tracks ?? SampleTracks();
            var library = AudioFool.Core.Library.LibraryScanner.Build(tracks);
            if (cutOffDemo)
                RunCutOffChecks(vm, library);

            var clock = System.Diagnostics.Stopwatch.StartNew();
            var stats = AudioFool.Core.Library.LibraryStatistics.Compute(library);
            Console.WriteLine($"stats: {tracks.Count:N0} tracks from {(cached is null ? "samples" : "library.json")}, "
                + $"computed in {clock.ElapsedMilliseconds} ms");

            // --qualitycache <file>: the quality section over the real library with
            // results kept in that file (never the app's own quality.json).
            // --qualitycheck 1 runs a real check first, reading the music drive;
            // --qualitystop <seconds> stops it part-way, to check a resume.
            if (Arg(args, "--qualitycache") is { } qualityPath)
            {
                LoadRealLibrary(vm);
                vm.QualityCachePath = qualityPath;

                // --fakeflags 1: results made up for the first 300 tracks (every
                // tenth one flagged, rows taken in turn), written to that scratch
                // file, for the section's rows and Clear All without a real check.
                if (Arg(args, "--fakeflags") is not null)
                {
                    var made = AudioFool.Core.Analysis.QualityCache.Empty();
                    var flags = AudioFool.Core.Analysis.QualityStatistics.Rows.Select(r => r.Flag).ToList();
                    foreach (var (track, i) in vm.StatisticsTracks.Take(300).Select((t, i) => (t, i)))
                        made.Set(track, i % 10 == 0
                            ? new AudioFool.Core.Analysis.QualityResult(AudioFool.Core.Analysis.Verdict.Inconsistent, [flags[i / 10 % flags.Count]])
                            : new AudioFool.Core.Analysis.QualityResult(AudioFool.Core.Analysis.Verdict.Consistent, []));
                    made.Save(qualityPath);
                }
                if (Arg(args, "--qualitycheck") is not null)
                {
                    var stopAfter = Arg(args, "--qualitystop") is { } s ? TimeSpan.FromSeconds(double.Parse(s, CultureInfo.InvariantCulture)) : (TimeSpan?)null;
                    var qualityClock = System.Diagnostics.Stopwatch.StartNew();
                    vm.StartQualityCheck();
                    Settle(500);
                    var lastReport = TimeSpan.Zero;
                    while (vm.IsCheckingQuality)
                    {
                        Settle(500);
                        if (stopAfter is { } limit && qualityClock.Elapsed >= limit)
                        {
                            vm.StopQualityCheck();
                            stopAfter = null;
                        }
                        if (qualityClock.Elapsed - lastReport >= TimeSpan.FromSeconds(60))
                        {
                            lastReport = qualityClock.Elapsed;
                            var p = vm.QualityProgress;
                            Console.WriteLine($"  quality: {p.Done:N0} of {p.Total:N0}, {p.Flagged:N0} flagged, at {qualityClock.Elapsed:mm\\:ss}  status '{vm.StatusText}'");
                        }
                    }
                    Settle(300);
                    Console.WriteLine($"quality check: '{vm.StatusText}' in {qualityClock.Elapsed:mm\\:ss}");
                }
            }

            // --covercache <file>: the cover section with results from that file.
            if (Arg(args, "--covercache") is not null && Arg(args, "--qualitycache") is null)
                LoadRealLibrary(vm);

            // --covercheck 1 (with --covercache <scratch file>): run the real cover
            // check over the library first, so both cover sections show real results.
            if (Arg(args, "--covercheck") is not null && Arg(args, "--covercache") is not null)
            {
                var coverClock = System.Diagnostics.Stopwatch.StartNew();
                vm.StartCoverCheck();
                Settle(500);
                while (vm.IsCheckingCovers)
                    Settle(500);
                Settle(300);
                Console.WriteLine($"cover check: '{vm.StatusText}' in {coverClock.Elapsed:mm\\:ss}");
            }

            var quality = new QualitySectionViewModel(vm);
            var covers = new CoverSectionViewModel(vm);
            var pngs = new PngSectionViewModel(vm);
            var pngClock = System.Diagnostics.Stopwatch.StartNew();
            while (pngs.Rows.LastOrDefault()?.Value.StartsWith("Looking") == true && pngClock.Elapsed < TimeSpan.FromMinutes(2))
                Settle(50);
            Console.WriteLine($"  [png covers] {pngs.Summary}  (folders looked in {pngClock.ElapsedMilliseconds} ms)");
            foreach (var r in pngs.Rows) Console.WriteLine($"    {r.Label,-32} {r.Value,-40} clickable={r.IsClickable}");
            if (Arg(args, "--pngfilter") is { } pngRow && pngs.Rows.FirstOrDefault(r => r.Label.StartsWith(pngRow, StringComparison.OrdinalIgnoreCase))?.Filter is { } pngFilter)
            {
                var matched = pngFilter.Apply(vm.StatisticsTracks);
                Console.WriteLine($"  filter '{pngFilter.Description}': {matched.Count:N0} tracks, e.g.");
                foreach (var folder in matched.Select(t => Path.GetDirectoryName(t.FilePath)).Distinct().Take(8))
                    Console.WriteLine($"    {folder}");
            }
            var cutOffs = new CutOffSectionViewModel(vm);
            var cutOffClock = System.Diagnostics.Stopwatch.StartNew();
            while (cutOffs.Rows.LastOrDefault()?.Value.StartsWith("Reading") == true && cutOffClock.Elapsed < TimeSpan.FromMinutes(2))
                Settle(50);
            Console.WriteLine($"  [cut-off covers] {cutOffs.Summary}  (cover files read in {cutOffClock.ElapsedMilliseconds} ms)");
            foreach (var r in cutOffs.Rows) Console.WriteLine($"    {r.Label,-32} {r.Value,-40} clickable={r.IsClickable}");
            // --cutofffilter <row start>: the albums that row's filter shows.
            if (Arg(args, "--cutofffilter") is { } cutOffRow && cutOffs.Rows.FirstOrDefault(r => r.Label.StartsWith(cutOffRow, StringComparison.OrdinalIgnoreCase))?.Filter is { } cutOffFilter)
            {
                var matched = cutOffFilter.Apply(vm.StatisticsTracks);
                Console.WriteLine($"  filter '{cutOffFilter.Description}': {matched.Count:N0} tracks");
                foreach (var folder in matched.Select(t => Path.GetDirectoryName(t.FilePath)).Distinct().Take(20))
                    Console.WriteLine($"    {folder}");
            }
            var statsVm = new StatisticsViewModel(stats, someFoldersHidden: Arg(args, "--hidden") is not null) { QualityCheck = quality, CoverCheck = covers, PngCovers = pngs, CutOffCovers = cutOffs, Shortcuts = ShortcutGuide.For(Arg(args, "--nohotkeys") is null) };
            Console.WriteLine($"  [extra covers] {covers.Summary}  button '{covers.ButtonText}' enabled={covers.CanToggle}");
            foreach (var r in covers.Rows) Console.WriteLine($"    {r.Label,-32} {r.Value,-30} clickable={r.IsClickable}  ({r.Detail})");
            Console.WriteLine($"  [quality check] {quality.Summary}  button '{quality.ButtonText}' enabled={quality.CanToggle}");
            foreach (var r in quality.Rows) Console.WriteLine($"    {r.Label,-32} {r.Value,-30} clickable={r.IsClickable}");
            foreach (var r in quality.ClearedRows) Console.WriteLine($"    {r.Label,-32} {r.Value,-30} clickable={r.IsClickable}  ({r.Detail})");
            foreach (var t in statsVm.Tiles) Console.WriteLine($"  tile  {t.Label,-10} {t.Value}");
            void Rows(string name, IEnumerable<BarRow> rows)
            {
                Console.WriteLine($"  [{name}]");
                foreach (var r in rows) Console.WriteLine($"    {r.Label,-32} {r.Value,-30} {r.Detail}");
            }
            Rows("file types", statsVm.FileTypes);
            Rows("quality", statsVm.Quality);
            Rows("missing tags", statsVm.TagGaps);
            Console.WriteLine($"    {statsVm.TagSummary}");
            Rows("albums that disagree", statsVm.AlbumGaps);
            Rows("decades", statsVm.Decades);

            // Never shown, for the same reason as the tag dialog above.
            var dialog = new StatisticsWindow(statsVm, main);
            dialog.ApplyTemplate();
            var root = (FrameworkElement)dialog.Content;
            root.Measure(new Size(w, double.PositiveInfinity));
            var height = Math.Ceiling(root.DesiredSize.Height);
            root.Arrange(new Rect(0, 0, w, height));
            root.UpdateLayout();
            var statsBar = Descendants<Wpf.Ui.Controls.TitleBar>(root).First();
            RaiseLoaded(statsBar);
            Settle(400);
            Save(root, outPath, w, height, scale);
            Console.WriteLine($"  size {w}x{height} (content measured {root.DesiredSize.Height:0.##}, rows {Descendants<System.Windows.Controls.Button>(root).Count(b => b.DataContext is BarRow)})");
            PrintTitleBar(statsBar, root);
            DependencyObject? up = dialog.FindName("CutOffList") as DependencyObject;
            while (up is not null and not System.Windows.Controls.HeaderedContentControl)
                up = VisualTreeHelper.GetParent(up);
            if (up is System.Windows.Controls.HeaderedContentControl cutOffSection)
            {
                var at = cutOffSection.TranslatePoint(new Point(0, 0), root);
                Console.WriteLine($"  cut-off section at {at.X:0},{at.Y:0} {cutOffSection.ActualWidth:0}x{cutOffSection.ActualHeight:0}, {cutOffSection.Visibility}");
            }
            if (cutOffDemo && _scratchDir is not null)
                Directory.Delete(_scratchDir, recursive: true);

            // --clearall 1 (with --qualitycache and --clearances scratch files): the
            // Quality Check's Clear All, its prompt answered yes without opening it,
            // then rendered again to <out>-cleared.png.
            if (Arg(args, "--clearall") is not null)
            {
                var checkButton = (System.Windows.Controls.Button)dialog.FindName("QualityCheckButton");
                var clearAll = (System.Windows.Controls.Button)dialog.FindName("QualityClearAllButton");
                string Where(FrameworkElement e) => e.IsVisible || e.Visibility == Visibility.Visible
                    ? $"at {e.TranslatePoint(new Point(0, 0), root).X:0},{e.TranslatePoint(new Point(0, 0), root).Y:0} {e.ActualWidth:0}x{e.ActualHeight:0}, {e.Visibility}"
                    : e.Visibility.ToString();
                Console.WriteLine($"  clear all: before, {quality.Flagged:N0} flagged, cleared row '{quality.ClearedRows.FirstOrDefault()?.Value}'; " +
                                  $"'{checkButton.Content}' {Where(checkButton)}; 'Clear All' {Where(clearAll)}");

                var asked = new List<int>();
                QualitySectionViewModel.ConfirmClearAll = n => { asked.Add(n); return true; };
                clearAll.Command.Execute(null);
                Settle(300);
                root.Measure(new Size(w, double.PositiveInfinity));
                var after = Math.Ceiling(root.DesiredSize.Height);
                root.Arrange(new Rect(0, 0, w, after));
                root.UpdateLayout();
                Save(root, Path.ChangeExtension(outPath, null) + "-cleared.png", w, after, scale);

                var allNone = quality.Rows.All(r => r.Value == "None");
                Console.WriteLine($"  clear all: asked [{string.Join(", ", asked)}], status '{vm.StatusText}'; after, {quality.Flagged} flagged, " +
                                  $"rows all None {allNone}, cleared row '{quality.ClearedRows.FirstOrDefault()?.Value}', 'Clear All' {clearAll.Visibility} " +
                                  $"-> {(asked.Count == 1 && allNone && quality.Flagged == 0 && clearAll.Visibility == Visibility.Collapsed ? "OK" : "WRONG")}");
            }
            // No tooltips anywhere in the window (the user's call, session 46).
            var tipped = Descendants<FrameworkElement>(root).Where(f => f.ToolTip is not null).ToList();
            Console.WriteLine($"  tooltips: {tipped.Count} {(tipped.Count == 0 ? "OK" : "WRONG: " + string.Join(", ", tipped.Select(f => $"{f.GetType().Name} '{f.Name}' {f.ToolTip}")))}");
            foreach (var panel in Descendants<System.Windows.Controls.HeaderedContentControl>(root).Where(p => p.ActualWidth > 0))
            {
                var p = panel.TranslatePoint(new Point(0, 0), root);
                var label = (panel.Header as System.Windows.Controls.TextBlock)?.Text ?? "(tile)";
                Console.WriteLine($"  panel {label,-22} at {p.X:0.#},{p.Y:0.#} size {panel.ActualWidth:0.#}x{panel.ActualHeight:0.#}");
            }
            var firstRow = Descendants<System.Windows.Controls.Button>(root).First(b => b.DataContext is BarRow);
            var rowAt = firstRow.TranslatePoint(new Point(0, 0), root);
            var rowText = Descendants<System.Windows.Controls.TextBlock>(firstRow).First();
            var textAt = rowText.TranslatePoint(new Point(0, 0), root);
            Console.WriteLine($"  first row at {rowAt.X:0.#},{rowAt.Y:0.#} size {firstRow.ActualWidth:0.#}x{firstRow.ActualHeight:0.#}  label at x {textAt.X:0.#} {rowText.FontSize}px");
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

    /// <summary>
    /// A dialog's title bar: its height, what its header holds and where the
    /// title text lands, and each window button's size and glyph colour.
    /// </summary>
    private static void PrintTitleBar(Wpf.Ui.Controls.TitleBar bar, FrameworkElement root)
    {
        Console.WriteLine($"titlebar: height {bar.ActualHeight:0.#}  header {bar.Header?.GetType().Name ?? "null"}");
        // The header's ancestors up to the bar: a long title must be offered a
        // limited width somewhere here, or it pushes the window buttons out.
        if (bar.Header is FrameworkElement headerElement)
        {
            Console.WriteLine($"  header width {headerElement.ActualWidth:0.#} (bar {bar.ActualWidth:0.#})");
            for (DependencyObject? n = VisualTreeHelper.GetParent(headerElement); n is not null && n != bar; n = VisualTreeHelper.GetParent(n))
            {
                var fe = n as FrameworkElement;
                var cols = n is System.Windows.Controls.Grid g
                    ? " cols [" + string.Join(", ", g.ColumnDefinitions.Select(c => $"{c.Width}={c.ActualWidth:0}")) + "]"
                    : "";
                var col = fe is not null ? $" in col {System.Windows.Controls.Grid.GetColumn(fe)}" : "";
                Console.WriteLine($"    {n.GetType().Name} '{fe?.Name}' {fe?.ActualWidth:0.#}{col}{cols}");
            }
        }
        foreach (var text in Descendants<System.Windows.Controls.TextBlock>(bar).Where(t => t.Text.Length > 0))
        {
            var p = text.TranslatePoint(new Point(0, 0), root);
            Console.WriteLine($"  text '{text.Text}' at {p.X:0.#},{p.Y:0.#}  {text.FontSize}px {text.Foreground}");
        }
        foreach (var image in Descendants<System.Windows.Controls.Image>(bar))
        {
            var p = image.TranslatePoint(new Point(0, 0), root);
            Console.WriteLine($"  icon at {p.X:0.#},{p.Y:0.#} size {image.ActualWidth:0.#}x{image.ActualHeight:0.#}");
        }
        foreach (var button in Descendants<Wpf.Ui.Controls.TitleBarButton>(bar))
        {
            var p = button.TranslatePoint(new Point(0, 0), root);
            Console.WriteLine($"  button {button.ButtonType,-8} at {p.X:0.#} size {button.ActualWidth:0.#}x{button.ActualHeight:0.#}  "
                + $"glyph {button.ButtonsForeground}  name '{System.Windows.Automation.AutomationProperties.GetName(button)}'");
        }
    }

    /// <summary>
    /// A dialog laid out by hand is never loaded, and WPF-UI copies each window
    /// button's glyph colour across only on Loaded, so the buttons render black.
    /// Raising Loaded on the title bar and everything in it runs those handlers
    /// without showing the window.
    /// </summary>
    private static void RaiseLoaded(FrameworkElement element)
    {
        element.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent, element));
        foreach (var child in Descendants<FrameworkElement>(element))
            child.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent, child));
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) yield return match;
            foreach (var deeper in Descendants<T>(child)) yield return deeper;
        }
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
    /// <summary>
    /// Prints what each key resolves to from the application's resources. The
    /// keys come from <paramref name="keysPath"/> (one per line) when given,
    /// otherwise from the list below.
    /// </summary>
    private static void Dump(string path, string? keysPath)
    {
        string[] keys = keysPath is not null
            ? File.ReadAllLines(keysPath).Select(k => k.Trim()).Where(k => k.Length > 0).ToArray()
            :
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

        // The Songs panel's outer left edge against Previous's left edge: they should coincide.
        var albumsPanel = Named<FrameworkElement>("AlbumsPanel");
        FrameworkElement? songsPanel = null;
        for (DependencyObject? up = grid; up is not null; up = VisualTreeHelper.GetParent(up))
        {
            if (up is System.Windows.Controls.HeaderedContentControl hcc && hcc.Name != "PlaybackBar")
            {
                songsPanel = hcc;
                break;
            }
        }

        if (songsPanel is not null)
        {
            var albumsRight = albumsPanel.TranslatePoint(new Point(albumsPanel.ActualWidth, 0), main).X;
            var songsLeft = songsPanel.TranslatePoint(new Point(0, 0), main).X;
            var previousBox = Named<FrameworkElement>("PreviousButton");
            var previousLeft = previousBox.TranslatePoint(new Point(0, 0), main).X;
            Console.WriteLine($"  align: Albums ends {albumsRight:0.##}, Songs panel starts {songsLeft:0.##}; "
                + $"Previous starts {previousLeft:0.##}; off by {previousLeft - songsLeft:0.##} px");
        }

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

        foreach (var name in new[] { "PreviousButton", "PlayPauseButton", "NextButton", "RepeatButton", "ShuffleButton", "MuteButton" })
        {
            var b = Named<System.Windows.Controls.Primitives.ButtonBase>(name);
            var face = Descendants(b).OfType<AudioFool.Theming.MoldedFace>().FirstOrDefault();
            var icon = Descendants(b).OfType<System.Windows.Controls.Viewbox>().First();
            var pressed = b is System.Windows.Controls.Primitives.ToggleButton tb ? $" checked={tb.IsChecked}" : "";
            Console.WriteLine($"  {name}: {Box(b)} name='{System.Windows.Automation.AutomationProperties.GetName(b)}'{pressed} fg={Ink(b.Foreground)} "
                + $"face={Ink(face?.Face)} stroke={Ink(face?.Stroke)} shadow={face?.Shadow} icon {Box(icon)}");
        }
        var badge = Named<System.Windows.Controls.TextBlock>("RepeatOneBadge");
        var disc = Named<FrameworkElement>("RepeatOneDisc");
        Console.WriteLine($"  repeat-one badge visible={badge.IsVisible}: disc {Box(disc)} {Text(badge)}");

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
        // --output "Exclusive · 176.4 kHz · DSD over PCM" for the longest.
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

    /// <summary>
    /// Set by --dpi: the scale the main window is laid out at, and with it every
    /// dialog, prompt, menu and tooltip Save draws (see LayOutAt).
    /// </summary>
    private static DpiScale? _dpi;

    /// <summary>
    /// --dpi for anything other than the main window: a dialog's content, a
    /// prompt, a menu's drop-down or a free-standing tooltip is laid out again at
    /// the scale before it is drawn, as SetRootDpi on the main window does, rather
    /// than drawn enlarged from its 100% layout. The DPI goes on the tree's root
    /// (the dialog's window, or the drop-down itself); the visual and everything
    /// in it is measured again, from the size it was last measured at, since
    /// layout rounding moves with the scale.
    /// </summary>
    private static void LayOutAt(Visual visual, DpiScale dpi)
    {
        var root = visual;
        while (VisualTreeHelper.GetParent(root) is Visual parent)
            root = parent;
        if (VisualTreeHelper.GetDpi(root).PixelsPerDip == dpi.PixelsPerDip)
            return;
        VisualTreeHelper.SetRootDpi(root, dpi);
        foreach (var element in Descendants(visual).Prepend(visual).OfType<UIElement>())
            element.InvalidateMeasure();
        (visual as UIElement)?.UpdateLayout();
    }

    /// <summary>--theme: the theme every window is built in.</summary>
    private static AudioFool.Core.Theme.AppTheme _theme;

    /// <summary>--switch: the theme to switch to before the first render; cleared once done.</summary>
    private static AudioFool.Core.Theme.AppTheme? _switchTo;

    private static AudioFool.Core.Theme.AppTheme? ThemeArg(string[] args, string name) => Arg(args, name) switch
    {
        null => null,
        var s when Enum.TryParse<AudioFool.Core.Theme.AppTheme>(s, ignoreCase: true, out var t) && Enum.IsDefined(t) => t,
        var s => throw new ArgumentException($"{name} takes light or dark, not \"{s}\""),
    };

    /// <summary>
    /// --switch: the live switch, through ThemeService as the app does it, with
    /// every window of the case open. Times the switch itself (the resources) and
    /// the redraw after it. Nothing is laid out again by hand: layout re-runs on
    /// whatever the switch invalidated, with the sizes the case gave it, as in the
    /// app (measuring a hand-laid dialog again at the bitmap's whole-pixel size
    /// moved it a pixel, and squeezed a menu measured unconstrained).
    /// </summary>
    private static void SwitchIfAsked()
    {
        if (_switchTo is not { } target)
            return;

        _switchTo = null;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        ThemeService.Switch(target);
        var swapped = clock.Elapsed.TotalMilliseconds;
        Application.Current.MainWindow?.UpdateLayout();
        Pump();
        Console.WriteLine($"switch: to {ThemeService.Current}, resources {swapped:0} ms, laid out and drawn {clock.Elapsed.TotalMilliseconds:0} ms");
    }

    private static void Save(Visual window, string path, double w, double h, double scale)
    {
        SwitchIfAsked();
        if (_dpi is { } dpiScale && window is UIElement element)
        {
            var before = element.RenderSize;
            LayOutAt(window, dpiScale);
            // Measured to its content, it can come out shorter or longer once
            // layout rounding moves with the scale (Statistics by 41 pixels at
            // 125%): drawn at the size it was given, the rest of the bitmap would
            // show as an unpainted strip.
            if (Math.Abs(before.Width - w) < 0.5 && Math.Abs(before.Height - h) < 0.5)
                (w, h) = (element.RenderSize.Width, element.RenderSize.Height);
        }

        // A window, or something inside one, is placed by its layout box: Dark's
        // cover shadow reaches past the art viewer's left edge, and placed by what
        // it draws the whole render moved 2 px right. A menu's drop-down, drawn
        // without ever being opened, still carries its opening animation's offset,
        // so it, like a free-standing tooltip, is placed by what it draws.
        var inWindow = false;
        for (var v = window; v is not null && !inWindow; v = VisualTreeHelper.GetParent(v) as Visual)
            inWindow = v is Window;

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
                        Viewbox = inWindow ? new Rect(0, 0, w, h) : new Rect(0, 0, 1, 1),
                        ViewboxUnits = inWindow ? BrushMappingMode.Absolute : BrushMappingMode.RelativeToBoundingBox,
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

        if (_brushDump)
            DumpBrushes(window, Path.ChangeExtension(path, ".brushes.txt"));
    }

    /// <summary>
    /// Set by --brushdump: next to each PNG, write every brush and corner radius
    /// held by every element in the rendered tree. A render shows only the state
    /// it was drawn in; this also shows what a control holds ready for hover,
    /// press, disabled or selected text, so two builds can be compared for
    /// states no off-screen render reaches.
    /// </summary>
    private static bool _brushDump;

    private static readonly Dictionary<Type, DependencyProperty[]> BrushProperties = [];

    private static void DumpBrushes(Visual root, string path)
    {
        var sb = new StringBuilder();
        void Walk(DependencyObject node, string trail)
        {
            var type = node.GetType();
            if (!BrushProperties.TryGetValue(type, out var props))
            {
                props = type.GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static
                                       | System.Reflection.BindingFlags.FlattenHierarchy)
                    .Where(f => f.FieldType == typeof(DependencyProperty))
                    .Select(f => (DependencyProperty)f.GetValue(null)!)
                    .Where(p => typeof(Brush).IsAssignableFrom(p.PropertyType) || p.PropertyType == typeof(CornerRadius))
                    .Distinct()
                    .OrderBy(p => p.Name, StringComparer.Ordinal)
                    .ToArray();
                BrushProperties[type] = props;
            }

            var name = node is FrameworkElement { Name.Length: > 0 } fe ? $"{type.Name}#{fe.Name}" : type.Name;
            var here = trail.Length == 0 ? name : trail + "/" + name;
            foreach (var p in props)
            {
                var value = node.GetValue(p);
                if (value is not null)
                    sb.AppendLine($"{here} {p.Name} = {Describe(value)}");
            }

            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
                Walk(VisualTreeHelper.GetChild(node, i), here + $"[{i}]");
        }

        Walk(root, "");
        File.WriteAllText(path, sb.ToString());
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
    /// The close button safety net's clicks so far, counted in the click log
    /// (--window closex). Opened shared: the log's writer may be appending.
    /// </summary>
    private static int SafetyNetClicks(string log)
    {
        if (!File.Exists(log))
            return 0;

        using var stream = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        var clicks = 0;
        while (reader.ReadLine() is { } line)
        {
            if (line.Contains("the safety net clicked it"))
                clicks++;
        }
        return clicks;
    }

    /// <summary>
    /// --window playlists: the Like column and the Playlists panel on the real
    /// library (read-only), with playlists in a scratch file (see PlaylistsPath).
    /// Renders the album view with hearts, Road trip, Liked, Liked after an
    /// unlike, an empty playlist and the name prompt, as out-*.png, printing
    /// what each shows.
    /// </summary>
    private static int RunPlaylists(MainWindow main, MainViewModel vm, string outPath, double w, double h, double scale)
    {
        LoadRealLibrary(vm);
        Pump();
        Settle(400);

        var grid = (System.Windows.Controls.DataGrid)main.FindName("TrackGrid");
        string Shot(string name) => Path.ChangeExtension(outPath, null) + $"-{name}.png";

        void Rows(string label)
        {
            Pump();
            Settle(200);
            Console.WriteLine($"{label}: {grid.Items.Count} rows");
            for (var i = 0; i < Math.Min(grid.Items.Count, 6); i++)
            {
                if (grid.ItemContainerGenerator.ContainerFromIndex(i) is not System.Windows.Controls.DataGridRow row)
                    continue;
                var heart = Descendants<LikeToggle>(row).FirstOrDefault();
                var number = Descendants<System.Windows.Controls.TextBlock>(row).FirstOrDefault()?.Text;
                var at = heart?.TranslatePoint(new Point(0, 0), main);
                Console.WriteLine($"  #{number,-3} '{(row.Item as Track)?.DisplayTitle}' liked={heart?.IsLiked} "
                    + $"heart at {at?.X:0.#},{at?.Y:0.#} size {heart?.ActualWidth:0.#}x{heart?.ActualHeight:0.#} "
                    + $"name='{(heart is null ? "" : System.Windows.Automation.AutomationProperties.GetName(heart))}'");
            }
        }

        void Header()
        {
            Console.WriteLine($"  header: '{vm.PlaylistHeaderTitle}' / '{vm.PlaylistHeaderModified}' / '{vm.PlaylistHeaderTrackCount}' / "
                + $"'{vm.PlaylistHeaderDuration}' art={(vm.SelectedPlaylistArt is null ? "none" : $"{vm.SelectedPlaylistArt.PixelWidth}px")} "
                + $"albumHeader={vm.ShowsAlbumHeader} playlistHeader={vm.ShowsPlaylistHeader} empty={vm.ShowsEmptyState} "
                + $"('{vm.EmptyStateTitle}' / '{vm.EmptyStateDetail}')");
            Console.WriteLine($"  playlists: {string.Join(" | ", vm.Playlists.Select(p => $"{p.Name} ({p.TrackSummary}){(ReferenceEquals(p, vm.SelectedPlaylist) ? " *" : "")}"))}");
        }

        var logo = (FrameworkElement)main.FindName("LogoSlot");
        var albumList = (FrameworkElement)main.FindName("AlbumList");
        var artistLabel = (FrameworkElement)main.FindName("ArtistsHeaderLabel");
        void Layout(string label) =>
            Console.WriteLine($"  {label}: logo x {logo.TranslatePoint(new Point(0, 0), main).X:0.#}, ARTISTS label {artistLabel.ActualWidth:0.#} wide "
                + $"(visible={artistLabel.IsVisible}), albums list {albumList.Visibility}");

        // 1. Albums: like two songs through the command, a third by pressing its heart.
        vm.ToggleLikeCommand.Execute(vm.Tracks[0]);
        vm.ToggleLikeCommand.Execute(vm.Tracks[2]);
        Pump();
        Settle(200);
        var selectedBefore = grid.SelectedIndex;
        var thirdHeart = Descendants<LikeToggle>(grid.ItemContainerGenerator.ContainerFromIndex(1)).First();
        var press = new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Left)
        {
            // The tunnelling PreviewMouseDown, as real input sends it: WPF raises
            // PreviewMouseLeftButtonDown from it on each element on the way down.
            RoutedEvent = System.Windows.Input.Mouse.PreviewMouseDownEvent,
            Source = thirdHeart,
        };
        var likedBefore = vm.LikedPaths.Count;
        thirdHeart.RaiseEvent(press);
        Console.WriteLine($"heart press: handled={press.Handled} liked {likedBefore} -> {vm.LikedPaths.Count}, heart DataContext {thirdHeart.DataContext?.GetType().Name}");
        Rows($"album '{vm.SelectedAlbum?.Album.Title}' after liking rows 1-3 (row 2 by a heart press; selection {selectedBefore} -> {grid.SelectedIndex})");
        Layout("albums");
        Save(main, Shot("albums"), w, h, scale);

        // 2. A playlist of songs from four artists, made through the store.
        var store = (AudioFool.Core.Playlists.PlaylistStore)typeof(MainViewModel)
            .GetField("_playlistStore", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(vm)!;
        var picks = vm.Artists.Where((_, i) => i % 40 == 7).Take(4)
            .Select(a => a.Albums[0].Tracks[0]).ToList();
        var road = store.Create("Road trip", DateTime.UtcNow);
        store.Add(road, picks, DateTime.UtcNow);
        Console.WriteLine($"road trip: {string.Join(" | ", picks.Select(t => $"{t.Artist} - {t.DisplayTitle}"))}");

        vm.IsPlaylistMode = true;
        Rows("playlist mode, opened on");
        Header();
        Layout("playlists");
        Save(main, Shot("roadtrip"), w, h, scale);

        // Add the first two songs again: nothing goes in. Then remove row 2.
        vm.SelectedTracks = [vm.Tracks[0], vm.Tracks[1]];
        vm.AddToPlaylistCommand.Execute(vm.SelectedPlaylist);
        Console.WriteLine($"  add again: '{vm.StatusText}'");
        vm.SelectedTracks = [vm.Tracks[1]];
        vm.RemoveFromPlaylistCommand.Execute(null);
        Console.WriteLine($"  remove row 2: '{vm.StatusText}'");
        Rows("road trip after removing row 2");
        Header();

        // 3. Liked, then an unlike: the row stays, with no place and an empty heart.
        vm.SelectedPlaylist = vm.Playlists.First(p => p.IsLiked);
        Rows("liked");
        Header();
        Save(main, Shot("liked"), w, h, scale);
        vm.ToggleLikeCommand.Execute(vm.Tracks[0]);
        Rows("liked after unliking row 1");
        Header();
        Save(main, Shot("liked-unliked"), w, h, scale);

        // 4. An empty playlist.
        var empty = store.Create("Empty one", DateTime.UtcNow);
        typeof(MainViewModel).GetMethod("RefreshPlaylists", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(vm, null);
        vm.SelectedPlaylist = vm.Playlists.First(p => ReferenceEquals(p.Playlist, empty));
        Rows("empty playlist");
        Header();
        Save(main, Shot("empty"), w, h, scale);

        // 5. Typing a search goes back to Artists, on the album it left.
        vm.SearchQuery = "a";
        Pump();
        Console.WriteLine($"after typing a search: playlistMode={vm.IsPlaylistMode} album='{vm.SelectedAlbum?.Album.Title}' rows={grid.Items.Count}");
        vm.SearchQuery = "";
        Settle(400);

        // 5b. Clicking the PLAYLISTS label goes back to Artists.
        vm.IsPlaylistMode = true;
        Pump();
        var playlistsHeader = (System.Windows.Controls.Button)main.FindName("PlaylistsHeader");
        var labelX = playlistsHeader.TranslatePoint(new Point(0, 0), main).X;
        var artistsX = artistLabel.TranslatePoint(new Point(0, 0), main).X;
        var headerPeer = new System.Windows.Automation.Peers.ButtonAutomationPeer(playlistsHeader);
        ((System.Windows.Automation.Provider.IInvokeProvider)headerPeer.GetPattern(System.Windows.Automation.Peers.PatternInterface.Invoke)!).Invoke();
        Pump();
        Console.WriteLine($"PLAYLISTS label at x {labelX:0.#} (ARTISTS at {artistsX:0.#}), name '{System.Windows.Automation.AutomationProperties.GetName(playlistsHeader)}'; "
            + $"after clicking it: playlistMode={vm.IsPlaylistMode} album='{vm.SelectedAlbum?.Album.Title}' rows={grid.Items.Count} logo x {logo.TranslatePoint(new Point(0, 0), main).X:0.#}");

        // 6. The name prompt, with the error a taken name gives.
        var prompt = (PromptWindow)Activator.CreateInstance(typeof(PromptWindow),
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic, null,
            [main, "New Playlist", "", "Create", "Liked", (Func<string, string?>)(t => store.NameProblem(t))], null)!;
        prompt.Left = -20000;
        prompt.Top = -20000;
        prompt.ShowActivated = false;
        prompt.ShowInTaskbar = false;
        prompt.Show();
        Pump();
        typeof(PromptWindow).GetMethod("Ok_Click", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(prompt, [null, new RoutedEventArgs()]);
        Pump();
        Settle(200);
        Save(prompt, Shot("prompt"), prompt.ActualWidth, prompt.ActualHeight, scale);
        Console.WriteLine($"prompt: {prompt.ActualWidth:0}x{prompt.ActualHeight:0}");
        prompt.Close();

        Console.WriteLine($"saved: {File.ReadAllText(MainViewModel.PlaylistsPath).Length} bytes in {MainViewModel.PlaylistsPath}");
        return 0;
    }

    /// <summary>
    /// --window recent: Recently Added over the real library (read-only; playlists
    /// in a scratch file). Prints the Playlists order, the Albums panel, the rows
    /// and both headers for every recent song, one album picked, Recently Added's
    /// row clicked again, a like, Liked, and back to Artists; renders the first two.
    /// </summary>
    private static int RunRecent(MainWindow main, MainViewModel vm, string outPath, double w, double h, double scale)
    {
        LoadRealLibrary(vm);
        Pump();
        Settle(400);

        var grid = (System.Windows.Controls.DataGrid)main.FindName("TrackGrid");
        var albumList = (FrameworkElement)main.FindName("AlbumList");
        var recentList = (System.Windows.Controls.ListBox)main.FindName("RecentAlbumList");
        var playlistList = (System.Windows.Controls.ListBox)main.FindName("PlaylistList");
        string Shot(string name) => Path.ChangeExtension(outPath, null) + $"-{name}.png";

        void Rows(string label)
        {
            Pump();
            Settle(200);
            Console.WriteLine($"{label}: {grid.Items.Count} rows");
            for (var i = 0; i < Math.Min(grid.Items.Count, 5); i++)
            {
                if (grid.ItemContainerGenerator.ContainerFromIndex(i) is not System.Windows.Controls.DataGridRow { Item: Track t } row)
                    continue;
                var number = Descendants<System.Windows.Controls.TextBlock>(row).FirstOrDefault()?.Text;
                Console.WriteLine($"  #{number,-3} {t.GroupingArtist} / {t.Album} / '{t.DisplayTitle}' added {t.AddedUtc:yyyy-MM-dd HH:mm} canReorder={vm.CanReorder(t)}");
            }
        }

        void State()
        {
            Console.WriteLine($"  playlist header={vm.ShowsPlaylistHeader} ('{vm.PlaylistHeaderTitle}' / '{vm.PlaylistHeaderModified}' / '{vm.PlaylistHeaderTrackCount}' / '{vm.PlaylistHeaderDuration}')");
            Console.WriteLine($"  album header={vm.ShowsAlbumHeader} ('{vm.AlbumHeaderTitle}' / '{vm.AlbumHeaderArtist}' / '{vm.AlbumHeaderYear}' / '{vm.AlbumHeaderTrackCount}' / '{vm.AlbumHeaderDuration}') "
                + $"art={(vm.SelectedAlbumArt is null ? "none" : "yes")} playlistArt={(vm.SelectedPlaylistArt is null ? "none" : "yes")}");
            Console.WriteLine($"  panels: AlbumList {albumList.Visibility}, RecentAlbumList {recentList.Visibility} ({recentList.Items.Count} albums, picked '{vm.SelectedRecentAlbum?.Album}'), "
                + $"empty={vm.ShowsEmptyState} ('{vm.EmptyStateTitle}'), remove-from-playlist={vm.CanRemoveFromShownPlaylist}");
        }

        vm.IsPlaylistMode = true;
        Pump();
        Console.WriteLine($"playlists: {string.Join(" | ", vm.Playlists.Select(p => $"{p.Name} ({p.TrackSummary}){(ReferenceEquals(p, vm.SelectedPlaylist) ? " *" : "")}"))}");

        var menu = (System.Windows.Data.CollectionViewSource)main.FindResource("PlaylistsMenuSource");
        Console.WriteLine($"Add to Playlist lists: {string.Join(" | ", menu.View.Cast<PlaylistItemViewModel>().Select(p => p.Name))}");

        var recent = vm.Playlists.First(p => p.IsRecentlyAdded);
        Console.WriteLine($"recently added row: rename/delete={recent.CanRenameOrDelete} editable={recent.IsEditable}");
        vm.SelectedPlaylist = recent;
        Pump();
        Settle(500);
        Rows("recently added, every song");
        State();
        foreach (var album in vm.RecentAlbums.Take(6))
            Console.WriteLine($"  album row: {album.Album} | artist '{album.ArtistLine}' meta '{album.YearDisplay} · {album.TrackSummary}' newest {album.Album.Tracks.Max(t => t.AddedUtc):yyyy-MM-dd HH:mm}");
        Save(main, Shot("all"), w, h, scale);

        // Does each row's text fit? The stack's wanted height against the row's content box.
        foreach (var album in vm.RecentAlbums.Take(10))
        {
            if (recentList.ItemContainerGenerator.ContainerFromItem(album) is not System.Windows.Controls.ListBoxItem item)
                continue;
            var stack = Descendants<System.Windows.Controls.StackPanel>(item).First();
            stack.Measure(new Size(stack.ActualWidth, double.PositiveInfinity));
            var title = (System.Windows.Controls.TextBlock)stack.Children[0];
            var room = item.ActualHeight - item.Padding.Top - item.Padding.Bottom;
            Console.WriteLine($"  fit: '{album.Title}' row {item.ActualHeight:0.#} room {room:0.#} text wants {stack.DesiredSize.Height:0.#} "
                + $"(title {title.ActualHeight:0.#}, {stack.Children.Count} lines) thumb {Descendants<System.Windows.Controls.Border>(item).First(b => b.Height > 0 && b.Height < 100).ActualHeight:0.#}");
        }

        if (vm.RecentAlbums.Count > 1)
        {
            vm.SelectedRecentAlbum = vm.RecentAlbums[1];
            Pump();
            Settle(500);
            Rows("second album picked");
            State();
            Save(main, Shot("album"), w, h, scale);

            // A like leaves the pick and the rows alone.
            vm.ToggleLikeCommand.Execute(vm.Tracks[0]);
            Pump();
            Console.WriteLine($"after a like: picked '{vm.SelectedRecentAlbum?.Album}', rows {grid.Items.Count}, liked {vm.LikedPaths.Contains(vm.Tracks[0].FilePath)}");
            vm.ToggleLikeCommand.Execute(vm.Tracks[0]);

            // Recently Added's row clicked again, as real input sends it (a tunnelling PreviewMouseDown).
            var container = (System.Windows.Controls.ListBoxItem)playlistList.ItemContainerGenerator.ContainerFromItem(recent);
            var text = Descendants<System.Windows.Controls.TextBlock>(container).First();
            var press = new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Left)
            {
                RoutedEvent = System.Windows.Input.Mouse.PreviewMouseDownEvent,
                Source = text,
            };
            text.RaiseEvent(press);
            Pump();
            Rows($"after clicking Recently Added again (handled={press.Handled})");
            State();
        }

        // Double-click an album row (silently): its first song plays, and the album is picked.
        if (vm.RecentAlbums.Count > 2)
        {
            vm.Volume = 0;
            var target = vm.RecentAlbums[2];
            recentList.ScrollIntoView(target);
            Pump();
            var row = (System.Windows.Controls.ListBoxItem)recentList.ItemContainerGenerator.ContainerFromItem(target);
            var title = Descendants<System.Windows.Controls.TextBlock>(row).First();
            var click = new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount, System.Windows.Input.MouseButton.Left)
            {
                RoutedEvent = System.Windows.Controls.Control.MouseDoubleClickEvent,
                Source = title,
            };
            recentList.RaiseEvent(click);   // a direct event: WPF raises it on the list, the source the row's text
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (vm.NowPlaying is null && clock.ElapsedMilliseconds < 5000)
                Settle(20);
            Settle(300);
            var first = target.Album.Tracks[0];
            Console.WriteLine($"double-click '{target.Album}': picked '{vm.SelectedRecentAlbum?.Album}', rows {grid.Items.Count}, "
                + $"playing '{vm.NowPlaying?.DisplayTitle}' (first is '{first.DisplayTitle}', same={ReferenceEquals(vm.NowPlaying, first)}), isPlaying={vm.IsPlaying}");
            typeof(MainViewModel).GetMethod("Stop", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(vm, null);
            Settle(100);
        }

        vm.SelectedPlaylist = vm.Playlists.First(p => p.IsLiked);
        Pump();
        Console.WriteLine("liked:");
        State();

        vm.IsPlaylistMode = false;
        Pump();
        Settle(400);
        Console.WriteLine($"back to artists: album '{vm.SelectedAlbum?.Album}', rows {grid.Items.Count}");
        State();
        return 0;
    }

    /// <summary>
    /// --window reorder: drag to reorder in a playlist of eight real songs plus
    /// one that isn't in the library (playlists in a scratch file). Synthetic
    /// mouse events read the real pointer, so the window's own steps are called
    /// with points worked out from the rows: BeginReorderPress, ReorderMoveTo,
    /// ReorderRelease. Prints the order after each case and renders the line.
    /// </summary>
    private static int RunReorder(MainWindow main, MainViewModel vm, string outPath, double w, double h, double scale)
    {
        LoadRealLibrary(vm);
        Pump();
        Settle(400);

        const System.Reflection.BindingFlags Private = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var grid = (System.Windows.Controls.DataGrid)main.FindName("TrackGrid");
        string Shot(string name) => Path.ChangeExtension(outPath, null) + $"-{name}.png";

        var begin = typeof(MainWindow).GetMethod("BeginReorderPress", Private, [typeof(DependencyObject), typeof(Point)])!;
        var moveTo = typeof(MainWindow).GetMethod("ReorderMoveTo", Private)!;
        var release = typeof(MainWindow).GetMethod("ReorderRelease", Private)!;
        var end = typeof(MainWindow).GetMethod("EndReorder", Private)!;

        // The window's own mouse handlers read the real button, which is up, and
        // capturing raises a move at once, so they would end every press. They
        // only check the button and pass the point on, which this calls directly.
        T Handler<T>(string name) where T : Delegate =>
            (T)Delegate.CreateDelegate(typeof(T), main, typeof(MainWindow).GetMethod(name, Private)!);
        grid.LostMouseCapture -= Handler<System.Windows.Input.MouseEventHandler>("TrackGrid_ReorderLostCapture");
        grid.PreviewMouseMove -= Handler<System.Windows.Input.MouseEventHandler>("TrackGrid_ReorderMouseMove");
        grid.PreviewMouseLeftButtonUp -= Handler<System.Windows.Input.MouseButtonEventHandler>("TrackGrid_ReorderMouseUp");

        var store = (AudioFool.Core.Playlists.PlaylistStore)typeof(MainViewModel)
            .GetField("_playlistStore", Private)!.GetValue(vm)!;
        var picks = vm.Artists.Where((_, i) => i % 30 == 5).Take(8).Select(a => a.Albums[0].Tracks[0]).ToList();
        var mix = store.Create("Reorder", DateTime.UtcNow.AddDays(-1));
        store.Add(mix, picks.Take(4), DateTime.UtcNow.AddDays(-1));
        mix.Entries.Add(new AudioFool.Core.Playlists.PlaylistEntry(@"Z:\Gone\missing.flac", "Nobody", "Nothing"));
        store.Add(mix, picks.Skip(4), DateTime.UtcNow.AddDays(-1));
        mix.ModifiedUtc = DateTime.UtcNow.AddDays(-1);
        var letter = picks.Select((t, i) => (t.FilePath, (char)('A' + i))).ToDictionary(p => p.FilePath, p => p.Item2, StringComparer.OrdinalIgnoreCase);

        string Saved() => string.Concat(mix.Entries.Select(e => letter.TryGetValue(e.FilePath, out var c) ? c : 'm'));
        string Shown() => string.Concat(grid.Items.OfType<Track>().Select(t => letter.TryGetValue(t.FilePath, out var c) ? c : '?'));
        string Selected() => string.Concat(grid.SelectedItems.OfType<Track>().Select(t => letter[t.FilePath]).Order());
        string Numbers() => string.Join(",", Enumerable.Range(0, grid.Items.Count).Select(i =>
            grid.ItemContainerGenerator.ContainerFromIndex(i) is System.Windows.Controls.DataGridRow r
                ? Descendants<System.Windows.Controls.TextBlock>(r).FirstOrDefault()?.Text : "-"));

        vm.IsPlaylistMode = true;
        vm.SelectedPlaylist = vm.Playlists.First(p => ReferenceEquals(p.Playlist, mix));
        Pump();
        Settle(300);
        Console.WriteLine($"start: saved {Saved()} shown {Shown()} #s {Numbers()} header '{vm.PlaylistHeaderTrackCount}' first in list '{vm.Playlists[0].Name}'");

        System.Windows.Controls.DataGridRow Row(int i) => (System.Windows.Controls.DataGridRow)grid.ItemContainerGenerator.ContainerFromIndex(i);
        Point At(int i, double fraction) => Row(i).TranslatePoint(new Point(200, Row(i).ActualHeight * fraction), grid);
        DependencyObject TextIn(int i) => Descendants<System.Windows.Controls.TextBlock>(Row(i)).Skip(1).First();

        void Select(params int[] rows)
        {
            grid.SelectedItems.Clear();
            foreach (var i in rows)
                grid.SelectedItems.Add(grid.Items[i]);
            Pump();
        }

        void Drag(string label, int from, int toRow, double fraction, string? shot = null, bool cancel = false)
        {
            var kept = (bool)begin.Invoke(main, [TextIn(from), At(from, 0.5)])!;
            var started = (bool)moveTo.Invoke(main, [At(from, 0.5) + new Vector(0, 1)])!;
            var past = (bool)moveTo.Invoke(main, [At(from, 0.5) + new Vector(0, 12)])!;
            moveTo.Invoke(main, [At(toRow, fraction)]);
            Pump();
            if (shot is not null)
            {
                var line = Descendants<System.Windows.Documents.AdornerLayer>(main).SelectMany(l => l.GetAdorners(grid) ?? []).OfType<DropLineAdorner>().FirstOrDefault();
                Console.WriteLine($"  drop line: {(line is null ? "none" : "shown")}");
                Save(main, Shot(shot), w, h, scale);
            }

            bool dropped;
            if (cancel)
            {
                end.Invoke(main, null);
                dropped = false;
            }
            else
            {
                dropped = (bool)release.Invoke(main, null)!;
            }

            Pump();
            Settle(200);
            Console.WriteLine($"{label}: press kept from grid={kept}, 1px started={started}, 12px started={past}, dropped={dropped} -> saved {Saved()} shown {Shown()} #s {Numbers()} selected {Selected()} current '{(grid.CurrentCell.Item is Track t ? letter[t.FilePath] : '-')}' focus={grid.IsKeyboardFocusWithin}");
        }

        // 1. One song (F, row 5) up to before B (upper half of row 1).
        Select(5);
        Drag("F before B", 5, 1, 0.25, shot: "line");

        // 2. Two songs (rows 0 and 2) to the end: lower half of the last row.
        Select(0, 2);
        Drag("rows 0+2 to end", 0, grid.Items.Count - 1, 0.75);

        // 3. A click (no movement) on a row of a multiple selection: it becomes the selection.
        Select(1, 3);
        var keptClick = (bool)begin.Invoke(main, [TextIn(3), At(3, 0.5)])!;
        moveTo.Invoke(main, [At(3, 0.5) + new Vector(1, 1)]);
        release.Invoke(main, null);
        Pump();
        Console.WriteLine($"click on a selected row: kept={keptClick} -> selected {Selected()} saved {Saved()}");

        // 4. Esc halfway: nothing moves.
        Select(4);
        Drag("Esc", 4, 0, 0.25, cancel: true);

        // 5. Onto itself: nothing moves, and the date stays.
        var before = mix.ModifiedUtc;
        Select(2);
        Drag("onto itself", 2, 2, 0.25);
        Select(2);
        Drag("just below itself", 2, 3, 0.25);
        Console.WriteLine($"  date unchanged: {mix.ModifiedUtc == before}");

        // 6. Sorted by Song: no drag.
        grid.Items.SortDescriptions.Add(new System.ComponentModel.SortDescription("DisplayTitle", System.ComponentModel.ListSortDirection.Ascending));
        Pump();
        Select(1);
        Console.WriteLine($"sorted: press starts a drag = {(bool)begin.Invoke(main, [TextIn(1), At(1, 0.5)])!}, move = {(bool)moveTo.Invoke(main, [At(1, 0.5) + new Vector(0, 20)])!}");
        release.Invoke(main, null);
        grid.Items.SortDescriptions.Clear();
        Pump();

        // 7. The album view: no drag.
        vm.IsPlaylistMode = false;
        Pump();
        Settle(200);
        Select(1);
        Console.WriteLine($"album view: press starts a drag = {(bool)begin.Invoke(main, [TextIn(1), At(1, 0.5)])!}");
        release.Invoke(main, null);

        var reloaded = AudioFool.Core.Playlists.PlaylistStore.Load(MainViewModel.PlaylistsPath, DateTime.UtcNow).Playlists.First(p => p.Name == "Reorder");
        Console.WriteLine($"on disk: {string.Concat(reloaded.Entries.Select(e => letter.TryGetValue(e.FilePath, out var c) ? c : 'm'))}, modified today: {reloaded.ModifiedUtc.Date == DateTime.UtcNow.Date}, first in list '{vm.Playlists[0].Name}'");
        return 0;
    }

    /// <summary>
    /// Puts the real library into the view model the way a scan would, through
    /// its private ApplyLibrary. Read-only: the cache is loaded, never saved, and
    /// no scan runs, so neither library.json nor the drive is touched.
    /// </summary>
    /// <summary>
    /// --shortcuts 1: the main window's keyboard shortcuts (session 46), through
    /// MainWindow.ResolveShortcut and HandleShortcut with the modifiers and focus
    /// passed in, since the real keyboard is off limits. Every key is resolved in
    /// each place focus can be. Only the ones that open no window, play nothing and
    /// write nothing but the scratch playlists are run; Shuffle, Repeat, Bit-Perfect
    /// and the volume save settings, so the real file is put back afterwards.
    /// </summary>
    private static void RunShortcuts(MainWindow main, MainViewModel vm)
    {
        LoadRealLibrary(vm);
        Pump();
        main.UpdateLayout();
        Settle(300);

        const System.Reflection.BindingFlags own = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var ctrl = System.Windows.Input.ModifierKeys.Control;
        var none = System.Windows.Input.ModifierKeys.None;
        string Resolve(System.Windows.Input.Key key, System.Windows.Input.ModifierKeys mods, DependencyObject? at) =>
            typeof(MainWindow).GetMethod("ResolveShortcut", own)!.Invoke(main, [key, mods, at])!.ToString()!;
        bool Handle(System.Windows.Input.Key key, bool repeat = false) =>
            (bool)typeof(MainWindow).GetMethod("HandleShortcut", own)!.Invoke(main, [key, ctrl, repeat, null])!;

        var all = true;
        void Expect(string label, object? actual, object? expected)
        {
            var ok = Equals(actual, expected);
            all &= ok;
            Console.WriteLine($"{label}: {actual} {(ok ? "OK" : $"WRONG (expected {expected})")}");
        }

        var grid = (System.Windows.Controls.DataGrid)main.FindName("TrackGrid");
        var search = (System.Windows.Controls.TextBox)main.FindName("SearchBox");
        var shuffleButton = (DependencyObject)main.FindName("ShuffleButton");
        var artistList = (DependencyObject)main.FindName("ArtistList");
        var songColumn = (System.Windows.Controls.DataGridColumn)main.FindName("SongColumn");

        // 1. What each key is, wherever focus is.
        var keys = new (System.Windows.Input.Key Key, string Expected)[]
        {
            (System.Windows.Input.Key.W, "Statistics"), (System.Windows.Input.Key.Q, "Analyze"), (System.Windows.Input.Key.D, "Art"),
            (System.Windows.Input.Key.S, "Shuffle"), (System.Windows.Input.Key.R, "Scan"), (System.Windows.Input.Key.E, "Repeat"),
            (System.Windows.Input.Key.B, "BitPerfect"), (System.Windows.Input.Key.F, "Search"), (System.Windows.Input.Key.L, "Like"),
            (System.Windows.Input.Key.M, "Mute"), (System.Windows.Input.Key.J, "GoToPlaying"), (System.Windows.Input.Key.Left, "Previous"),
            (System.Windows.Input.Key.Right, "Next"), (System.Windows.Input.Key.Up, "VolumeUp"), (System.Windows.Input.Key.Down, "VolumeDown"),
            (System.Windows.Input.Key.T, "Theme"), (System.Windows.Input.Key.A, "None"), (System.Windows.Input.Key.C, "None"),
        };
        foreach (var (where, at) in new (string, DependencyObject?)[] { ("window", null), ("song table", grid), ("Artists", artistList), ("Shuffle button", shuffleButton) })
        {
            Expect($"{where}, Space", Resolve(System.Windows.Input.Key.Space, none, at), "PlayPause");
            foreach (var (key, expected) in keys)
                Expect($"{where}, Ctrl+{key}", Resolve(key, ctrl, at), expected);
        }
        Expect("Shift+Space", Resolve(System.Windows.Input.Key.Space, System.Windows.Input.ModifierKeys.Shift, null), "None");
        Expect("Ctrl+Shift+S", Resolve(System.Windows.Input.Key.S, ctrl | System.Windows.Input.ModifierKeys.Shift, null), "None");
        Expect("Ctrl+Alt+S (AltGr)", Resolve(System.Windows.Input.Key.S, ctrl | System.Windows.Input.ModifierKeys.Alt, null), "None");
        Expect("plain S", Resolve(System.Windows.Input.Key.S, none, null), "None");

        // In the search box: Space types, Ctrl+arrows move by word, Ctrl+letters work.
        Expect("search box, Space", Resolve(System.Windows.Input.Key.Space, none, search), "None");
        foreach (var key in new[] { System.Windows.Input.Key.Left, System.Windows.Input.Key.Right, System.Windows.Input.Key.Up, System.Windows.Input.Key.Down })
            Expect($"search box, Ctrl+{key}", Resolve(key, ctrl, search), "None");
        Expect("search box, Ctrl+S", Resolve(System.Windows.Input.Key.S, ctrl, search), "Shuffle");
        Expect("search box, Ctrl+B", Resolve(System.Windows.Input.Key.B, ctrl, search), "BitPerfect");
        Expect("search box, Ctrl+T", Resolve(System.Windows.Input.Key.T, ctrl, search), "Theme");

        // In an open menu: nothing.
        var menuItem = new System.Windows.Controls.MenuItem();
        Expect("menu item, Space", Resolve(System.Windows.Input.Key.Space, none, menuItem), "None");
        Expect("menu item, Ctrl+S", Resolve(System.Windows.Input.Key.S, ctrl, menuItem), "None");
        Expect("menu item, Ctrl+T", Resolve(System.Windows.Input.Key.T, ctrl, menuItem), "None");

        // A type-ahead under way: Space is part of the name.
        var buffer = typeof(MainWindow).GetField("_typeAheadBuffer", own)!;
        buffer.SetValue(main, "pink");
        Expect("type-ahead 'pink', Space", Resolve(System.Windows.Input.Key.Space, none, artistList), "None");
        buffer.SetValue(main, "");
        Expect("type-ahead over, Space", Resolve(System.Windows.Input.Key.Space, none, artistList), "PlayPause");

        // A song-table cell being edited (F2's command, then cancelled, so nothing saves).
        var songs = grid.Items.OfType<Track>().ToList();
        if (songs.Count > 0)
        {
            grid.SelectedItem = songs[0];
            grid.CurrentCell = new System.Windows.Controls.DataGridCellInfo(songs[0], songColumn);
            grid.Focus();
            System.Windows.Controls.DataGrid.BeginEditCommand.Execute(null, grid);
            Pump();
            var editor = FindVisual<System.Windows.Controls.Primitives.TextBoxBase>(grid);
            Console.WriteLine($"cell edit open: {editor is not null}");
            all &= editor is not null;
            if (editor is not null)
            {
                Expect("cell edit, Space", Resolve(System.Windows.Input.Key.Space, none, editor), "None");
                foreach (var key in new[] { System.Windows.Input.Key.S, System.Windows.Input.Key.W, System.Windows.Input.Key.T, System.Windows.Input.Key.Left, System.Windows.Input.Key.Up })
                    Expect($"cell edit, Ctrl+{key}", Resolve(key, ctrl, editor), "None");
            }
            grid.CancelEdit();
            Pump();
        }

        // 2. Ctrl+Q's song and Ctrl+D's art.
        var target = typeof(MainWindow).GetMethod("AnalyzeTarget", own)!;
        var artPlaying = typeof(MainWindow).GetProperty("ArtShowsPlaying", own)!;
        var playing = songs.Count > 1 ? songs[^1] : null;
        vm.NowPlaying = null;
        if (songs.Count > 2)
        {
            grid.SelectedItems.Clear();
            grid.SelectedItem = songs[0];
            Expect("Ctrl+Q, one song selected", (target.Invoke(main, null) as Track)?.Title, songs[0].Title);
            grid.SelectedItems.Add(songs[1]);
            grid.CurrentCell = new System.Windows.Controls.DataGridCellInfo(songs[1], songColumn);
            Expect("Ctrl+Q, two selected, focus on the second", (target.Invoke(main, null) as Track)?.Title, songs[1].Title);
            grid.SelectedItems.Clear();
            Expect("Ctrl+Q, none selected, nothing playing", target.Invoke(main, null), null);
            Expect("Ctrl+D, nothing playing: selected album", artPlaying.GetValue(main), false);
            vm.NowPlaying = playing;
            Expect("Ctrl+Q, none selected, one playing", (target.Invoke(main, null) as Track)?.Title, playing!.Title);
            Expect("Ctrl+D, one playing: playing album", artPlaying.GetValue(main), true);
        }

        // 3. Run the safe ones and watch the state change.
        var settingsKept = File.Exists(AppSettings.SettingsPath) ? File.ReadAllBytes(AppSettings.SettingsPath) : null;
        try
        {
            var shuffle = vm.IsShuffle;
            Handle(System.Windows.Input.Key.S);
            Expect("Ctrl+S toggles Shuffle", vm.IsShuffle, !shuffle);
            Handle(System.Windows.Input.Key.S, repeat: true);
            Expect("Ctrl+S held: once only", vm.IsShuffle, !shuffle);
            Handle(System.Windows.Input.Key.S);

            var repeats = new List<string> { vm.Repeat.ToString() };
            for (var i = 0; i < 3; i++)
            {
                Handle(System.Windows.Input.Key.E);
                repeats.Add(vm.Repeat.ToString());
            }
            Expect("Ctrl+E cycles Repeat", string.Join(" > ", repeats), "Off > All > One > Off");

            vm.Volume = 0.5;
            Handle(System.Windows.Input.Key.Up);
            Expect("Ctrl+Up", Math.Round(vm.Volume, 2), 0.55);
            Handle(System.Windows.Input.Key.Up, repeat: true);
            Expect("Ctrl+Up held: keeps going", Math.Round(vm.Volume, 2), 0.6);
            Handle(System.Windows.Input.Key.Down);
            Handle(System.Windows.Input.Key.Down);
            Expect("Ctrl+Down twice", Math.Round(vm.Volume, 2), 0.5);
            Handle(System.Windows.Input.Key.M);
            Expect("Ctrl+M mutes", (vm.IsMuted, vm.Volume), (true, 0.0));
            Handle(System.Windows.Input.Key.M);
            Expect("Ctrl+M unmutes", (vm.IsMuted, Math.Round(vm.Volume, 2)), (false, 0.5));
            Handle(System.Windows.Input.Key.M);
            Handle(System.Windows.Input.Key.Up);
            Expect("Ctrl+Up while muted: unmutes one step up", (vm.IsMuted, Math.Round(vm.Volume, 2)), (false, 0.55));
            vm.Volume = 0;

            var bitPerfect = vm.IsExclusiveOutput;
            Handle(System.Windows.Input.Key.B);
            Expect($"Ctrl+B (can use: {vm.CanUseExclusiveOutput})", vm.IsExclusiveOutput, vm.CanUseExclusiveOutput ? !bitPerfect : bitPerfect);
            Handle(System.Windows.Input.Key.B);
            Expect("Ctrl+B again", vm.IsExclusiveOutput, bitPerfect);
            Expect("  nothing started playing", engine(vm).State, PlaybackState.Stopped);

            if (playing is not null)
            {
                bool Liked() => vm.LikedPaths.Contains(playing.FilePath);
                var liked = Liked();
                Handle(System.Windows.Input.Key.L);
                Expect("Ctrl+L likes the playing song", Liked(), !liked);
                Handle(System.Windows.Input.Key.L);
                Expect("Ctrl+L again unlikes it", Liked(), liked);

                vm.SearchQuery = "";
                Handle(System.Windows.Input.Key.J);
                Settle(300);
                Expect("Ctrl+J shows the playing song's album", vm.SelectedAlbum?.Album.Title, playing.Album);
            }

            search.Text = "abc";
            Handle(System.Windows.Input.Key.F);
            Settle(100);
            Expect("Ctrl+F: search box focused, text selected", (search.IsKeyboardFocusWithin, search.SelectedText), (true, "abc"));
            search.Text = "";
        }
        finally
        {
            if (settingsKept is not null)
                File.WriteAllBytes(AppSettings.SettingsPath, settingsKept);
            else if (File.Exists(AppSettings.SettingsPath))
                File.Delete(AppSettings.SettingsPath);
        }

        // 3b. Ctrl+D with the art viewer open closes it: from the main window (a
        //     viewer that is never shown, so nothing appears), and in the viewer.
        var artField = typeof(MainWindow).GetField("_artWindow", own)!;
        var pixels = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgr32, null, new byte[16], 8);
        artField.SetValue(main, new ArtWindow(pixels, "test", main));
        Handle(System.Windows.Input.Key.D);
        Settle(100);
        Expect("Ctrl+D, viewer open: closed", artField.GetValue(main), null);
        var closesOn = typeof(ArtWindow).GetMethod("ClosesOn", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        bool Closes(System.Windows.Input.Key key, System.Windows.Input.ModifierKeys mods, bool repeat) => (bool)closesOn.Invoke(null, [key, mods, repeat])!;
        Expect("in the viewer, Ctrl+D", Closes(System.Windows.Input.Key.D, ctrl, false), true);
        Expect("in the viewer, Ctrl+D held (repeat)", Closes(System.Windows.Input.Key.D, ctrl, true), false);
        Expect("in the viewer, Escape", Closes(System.Windows.Input.Key.Escape, none, false), true);
        Expect("in the viewer, plain D", Closes(System.Windows.Input.Key.D, none, false), false);
        Expect("in the viewer, Ctrl+Shift+D", Closes(System.Windows.Input.Key.D, ctrl | System.Windows.Input.ModifierKeys.Shift, false), false);

        // 4. Leaving the search box: Escape, or a click elsewhere, takes focus back
        //    to where it was (else the selected song) and keeps the search.
        string Where() => System.Windows.Input.Keyboard.FocusedElement switch
        {
            System.Windows.Controls.DataGridCell c => $"song '{(c.DataContext as Track)?.Title}'",
            FrameworkElement f when f == search || search.IsAncestorOf(f) => "search box",
            FrameworkElement f => f.Name is { Length: > 0 } n ? n : f.GetType().Name,
            _ => "nothing",
        };
        void Escape()
        {
            search.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice,
                PresentationSource.FromVisual(search), 0, System.Windows.Input.Key.Escape) { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent });
            Settle(100);
        }
        void ClickOn(UIElement target)
        {
            target.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount,
                System.Windows.Input.MouseButton.Left) { RoutedEvent = System.Windows.Input.Mouse.PreviewMouseDownEvent });
            Settle(100);
        }
        var before = typeof(MainWindow).GetField("_focusBeforeSearch", own)!;
        var shuffleElement = (UIElement)shuffleButton;
        var label = (UIElement)main.FindName("ArtistsHeaderLabel");

        if (songs.Count > 0)
        {
            grid.SelectedItems.Clear();
            grid.SelectedItem = songs[0];
            typeof(MainWindow).GetMethod("FocusCell", own)!.Invoke(main, [songs[0], songColumn]);
            Settle(50);
            var songWhere = Where();
            search.Focus();
            search.Text = "show";
            Settle(400);
            Expect($"search box focused from {songWhere}", Where(), "search box");
            Escape();
            Expect("Escape: back to the song", Where(), songWhere);
            Expect("  search kept", (search.Text, vm.SearchQuery), ("show", "show"));

            shuffleElement.Focus();
            search.Focus();
            Escape();
            Expect("Escape, from the Shuffle button: back to it", Where(), "ShuffleButton");

            search.Focus();
            before.SetValue(main, null);
            Escape();
            Expect("Escape, nowhere before: the selected song", Where(), songWhere);

            search.Focus();
            ClickOn(search);
            Expect("click in the search box: stays", Where(), "search box");
            ClickOn(label);
            Expect("click on the ARTISTS label: leaves", Where(), songWhere);
            Expect("  search kept", search.Text, "show");

            search.Focus();
            ClickOn(shuffleElement);
            Expect("click on Shuffle (skips focus): leaves", Where() != "search box", true);
            search.Text = "";
            Settle(400);
        }

        // 5. OwnerActivation: the non-modal windows hand activation back to their
        //    owner as they close. Activation itself can't be tried without taking
        //    the foreground from the user, so only the wiring is checked, on
        //    windows that are built and never shown.
        bool Hooked(Window window)
        {
            var key = typeof(Window).GetField("EVENT_CLOSING", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!.GetValue(null)!;
            var events = (System.ComponentModel.EventHandlerList)typeof(Window).GetProperty("Events", own)!.GetValue(window)!;
            return events[key]?.GetInvocationList().Any(d => d.Method.DeclaringType == typeof(AudioFool.Services.OwnerActivation)) == true;
        }
        Expect("art viewer hands activation back", Hooked(new ArtWindow(pixels, "test", main)), true);
        if (songs.Count > 0)
        {
            Expect("song tag window hands activation back", Hooked(new TagEditWindow(new TagEditViewModel(songs[0]), main)), true);
            Expect("Analyze hands activation back", Hooked(new AnalysisWindow(new AnalysisViewModel(songs[0], vm), main)), true);
        }

        Console.WriteLine(all ? "shortcuts: all OK" : "shortcuts: SOMETHING WRONG");

        static AudioEngine engine(MainViewModel m) =>
            (AudioEngine)typeof(MainViewModel).GetField("_engine", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(m)!;
    }

    private static T? FindVisual<T>(DependencyObject node) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
        {
            var child = VisualTreeHelper.GetChild(node, i);
            if (child is T match)
                return match;
            if (FindVisual<T>(child) is { } deeper)
                return deeper;
        }
        return null;
    }

    /// <summary>
    /// --singleinstance 1: the one-copy handover (SingleInstance) under a test name.
    /// This copy becomes the one copy with a file of its own, then starts three
    /// ThemeLab children at once with a file each, as Explorer does for three
    /// songs, and later one with none, as the shortcut clicked again. All four
    /// must hand over and quit; the four files must arrive as one batch; every
    /// handover must ask for the window.
    /// </summary>
    private static int RunSingleInstance(string[] args)
    {
        var name = "AudioFoolThemeLab-" + Guid.NewGuid().ToString("N")[..8];
        var dir = Path.Combine(Path.GetTempPath(), name);
        Directory.CreateDirectory(dir);
        string File(string fileName) => Path.Combine(dir, fileName);

        var primary = AudioFool.Services.SingleInstance.Start([File("1 Own.flac")], name)
            ?? throw new InvalidOperationException("the first copy handed over");
        var activations = 0;
        var batches = new List<IReadOnlyList<string>>();
        primary.Activated += () => activations++;
        primary.FilesOpened += files => batches.Add(files);

        System.Diagnostics.Process Child(params string[] files)
        {
            var info = new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!)
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            info.ArgumentList.Add("--singlechild");
            info.ArgumentList.Add(name);
            info.ArgumentList.Add("--files");
            foreach (var file in files)
                info.ArgumentList.Add(file);
            return System.Diagnostics.Process.Start(info)!;
        }

        void Await(params System.Diagnostics.Process[] children)
        {
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (children.Any(c => !c.HasExited) && DateTime.UtcNow < deadline)
                Settle(50);
            foreach (var child in children)
                Console.WriteLine($"  child {child.Id}: {child.StandardOutput.ReadToEnd().Trim()}");
        }

        Console.WriteLine("three songs opened at once:");
        Await(Child(File("10 Ten.flac")), Child(File("2 Two.flac")), Child(File("3 Three.flac")));
        Settle(1500);

        Console.WriteLine("started again with no song:");
        Await(Child());
        Settle(1500);

        for (var i = 0; i < batches.Count; i++)
            Console.WriteLine($"batch {i + 1}: {string.Join(", ", batches[i].Select(Path.GetFileName))}" +
                              $" -> plays as {string.Join(", ", AudioFool.Core.Library.OpenedFiles.InPlayOrder(batches[i]).Select(Path.GetFileName))}");

        var ok = batches.Count == 1 && batches[0].Count == 4 && activations == 4;
        Console.WriteLine($"{batches.Count} batch(es), {activations} activations -> {(ok ? "OK" : "WRONG")}");
        primary.Dispose();
        Directory.Delete(dir, recursive: true);
        return ok ? 0 : 1;
    }

    /// <summary>
    /// --singleinstance exit: a copy started while the one copy is closing. The
    /// first copy lets go as App.OnExit does (Dispose) and stays alive a while, as
    /// the app does while it saves; a copy started then must take over with its
    /// song, and not fault.
    /// </summary>
    private static int RunSingleExit()
    {
        var name = "AudioFoolThemeLab-" + Guid.NewGuid().ToString("N")[..8];
        var dir = Path.Combine(Path.GetTempPath(), name);
        Directory.CreateDirectory(dir);
        string File(string fileName) => Path.Combine(dir, fileName);

        System.Diagnostics.Process Child(bool takeOver, params string[] files)
        {
            var info = new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var arg in (string[])["--singlechild", name, "--takeover", takeOver ? "1" : "0", "--stay", "4000", "--files", .. files])
                info.ArgumentList.Add(arg);
            return System.Diagnostics.Process.Start(info)!;
        }

        bool Report(System.Diagnostics.Process child, string expected)
        {
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (!child.HasExited && DateTime.UtcNow < deadline)
                Settle(50);
            var output = child.StandardOutput.ReadToEnd().Trim();
            var error = child.StandardError.ReadToEnd().Trim();
            var ok = child.HasExited && child.ExitCode == 0 && output == expected && error.Length == 0;
            var cpu = child.HasExited ? $" cpu={child.TotalProcessorTime.TotalMilliseconds:F0} ms" : "";
            Console.WriteLine($"  child: '{output}' exit={(child.HasExited ? child.ExitCode.ToString("X") : "still running")}{cpu}"
                + (error.Length > 0 ? $" stderr: {error.Split('\n')[0].Trim()}" : "") + $" -> {(ok ? "OK" : "WRONG")}");
            if (!child.HasExited)
                child.Kill();
            return ok;
        }

        Console.WriteLine("started while the first copy is closing (let go, still saving):");
        var first = AudioFool.Services.SingleInstance.Start([], name)
            ?? throw new InvalidOperationException("the first copy handed over");
        first.Dispose();
        // The next copy takes over; one more, started while the first is still
        // saving, must reach the new one copy, not the one closing.
        var taker = Child(true, File("1 One.flac"));
        Settle(1000);
        var later = Child(false, File("2 Two.flac"));
        var ok = Report(later, "handed over");
        ok &= Report(taker, "took over; files: 1 One.flac, 2 Two.flac");

        Console.WriteLine("started after the window closed, before the first copy let go:");
        first = AudioFool.Services.SingleInstance.Start([], name)
            ?? throw new InvalidOperationException("the first copy handed over");
        var lost = new List<string>();
        first.FilesOpened += f => lost.AddRange(f);
        first.StopListening();
        var waiter = Child(true, File("3 Three.flac"));
        Settle(2000);
        first.Dispose();
        ok &= Report(waiter, "took over; files: 3 Three.flac");
        Settle(700);
        Console.WriteLine($"  reached the closing copy: {(lost.Count == 0 ? "nothing -> OK" : string.Join(", ", lost) + " -> WRONG")}");
        ok &= lost.Count == 0;

        Console.WriteLine("handed over while the first copy's UI thread is busy for 1 s:");
        first = AudioFool.Services.SingleInstance.Start([], name)
            ?? throw new InvalidOperationException("the first copy handed over");
        var received = new List<string>();
        first.FilesOpened += f => received.AddRange(f);
        var busy = Child(false, File("4 Four.flac"));
        Thread.Sleep(300);
        Thread.Sleep(1000);
        ok &= Report(busy, "handed over");
        Settle(700);
        Console.WriteLine($"  the one copy got: {string.Join(", ", received.Select(Path.GetFileName))}"
            + (received.Count == 1 ? " -> OK" : " -> WRONG"));
        ok &= received.Count == 1;
        first.Dispose();

        Directory.Delete(dir, recursive: true);
        Console.WriteLine(ok ? "ALL OK" : "WRONG");
        return ok ? 0 : 1;
    }

    /// <summary>
    /// --window openfiles: songs opened from Explorer (MainViewModel.OpenFiles),
    /// real library read-only, the engine silent. A song from outside the library
    /// opened before the engine is ready, then once it is; a library song opened
    /// before the library is shown, then from a search in playlist mode; several
    /// songs at once; a text file.
    /// </summary>
    private static void RunOpenFiles(MainWindow main, MainViewModel vm, AudioEngine engine, BassRuntime runtime)
    {
        var scratch = Path.Combine(Path.GetTempPath(), "themelab-open-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(scratch);
        var sample = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "tests", "AudioFool.Core.Tests", "TestData", "sample.flac");
        string Copy(string fileName)
        {
            var path = Path.Combine(scratch, fileName);
            File.Copy(sample, path);
            return path;
        }

        var queueField = typeof(AudioEngine).GetField("_queue", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        List<Track> QueueTracks() => (List<Track>)queueField.GetValue(engine)!;
        string Queue() => string.Join(", ", QueueTracks().Select(t => Path.GetFileName(t.FilePath)));
        var grid = (System.Windows.Controls.DataGrid)main.FindName("TrackGrid");
        var all = true;
        void Expect(string what, bool ok)
        {
            all &= ok;
            Console.WriteLine($"  {what} -> {(ok ? "OK" : "WRONG")}");
        }

        // The samples last about a second, so what started is recorded as it starts.
        var started = new List<string>();
        engine.TrackChanged += (_, t) => started.Add(Path.GetFileName(t.FilePath));
        string Started()
        {
            var text = started.Count == 0 ? "nothing" : string.Join(", ", started);
            started.Clear();
            return text;
        }

        vm.Volume = 0;
        engine.Volume = 0;

        // 1. At startup, before the engine is set up: waits, then plays. The
        //    window's Loaded has set the engine up already, so it is marked not
        //    ready again, as it is before InitialiseAsync.
        var engineReady = typeof(MainViewModel).GetField("_engineReady", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        engineReady.SetValue(vm, false);
        var outside = Copy("Outside Song.flac");
        Console.WriteLine("1. a song from outside the library, opened before the engine is ready:");
        vm.OpenFiles([outside]);
        Settle(500);
        Expect($"waits: started {started.Count}", started.Count == 0);
        var init = vm.InitialiseAsync();
        while (!init.IsCompleted)
            Settle(50);
        Settle(500);
        var firstStarted = Started();
        Expect($"then plays: started {firstStarted}, queue [{Queue()}]", firstStarted == "Outside Song.flac" && Queue() == "Outside Song.flac");
        Settle(1500);
        Started();

        // 2. A library song opened before the library is shown: waits for it, then
        //    shows its album and plays from it.
        var cached = AudioFool.Core.Library.LibraryCache.Load()!;
        var library = AudioFool.Core.Library.LibraryScanner.Build(cached.Tracks);
        var album = library.Artists.SelectMany(a => a.Albums).First(a => a.Tracks.Count >= 5 && a.Tracks.All(t => File.Exists(t.FilePath)));
        var song = album.Tracks[2];
        var folder = Path.GetDirectoryName(song.FilePath)!;
        Console.WriteLine($"2. '{song.DisplayTitle}' ({album.ArtistName} / {album.Title}), opened before the library is shown:");
        vm.MusicFolders.Add(folder);
        vm.OpenFiles([song.FilePath]);
        Settle(500);
        Expect($"waits: started {started.Count}", started.Count == 0);
        LoadRealLibrary(vm);
        Settle(1000);
        Expect($"then shows artist '{vm.SelectedArtist?.Name}', album '{vm.SelectedAlbum?.Album.Title}', row '{(grid.SelectedItem as Track)?.DisplayTitle}'",
            vm.SelectedAlbum?.Album.Title == album.Title && (grid.SelectedItem as Track)?.FilePath == song.FilePath);
        Expect($"plays it with its album: started {Started()}, {QueueTracks().Count} in the queue of {album.Tracks.Count}",
            vm.NowPlaying?.FilePath == song.FilePath && QueueTracks().Count == album.Tracks.Count);
        vm.MusicFolders.Remove(folder);

        // 3. Another song of it, from a search in playlist mode: back to the library.
        Console.WriteLine("3. a library song opened while searching, in playlist mode:");
        vm.SearchQuery = "zzzz no such thing";
        Settle(600);
        vm.IsPlaylistMode = true;
        Pump();
        var other = album.Tracks[0];
        vm.OpenFiles([other.FilePath]);
        Settle(1000);
        Expect($"search '{vm.SearchQuery}', playlist mode {vm.IsPlaylistMode}, album '{vm.SelectedAlbum?.Album.Title}', " +
               $"row '{(grid.SelectedItem as Track)?.DisplayTitle}', started {Started()}",
            vm.SearchQuery == "" && !vm.IsPlaylistMode && (grid.SelectedItem as Track)?.FilePath == other.FilePath
            && vm.NowPlaying?.FilePath == other.FilePath);

        // 4. Several songs at once, in no order: one queue, as Explorer lists them; the view stays.
        Console.WriteLine("4. three songs from outside the library at once:");
        var shownAlbum = vm.SelectedAlbum?.Album.Title;
        vm.OpenFiles([Copy("10 Ten.flac"), Copy("2 Two.flac"), Copy("1 One.flac")]);
        Settle(5000);
        Expect($"queue [{Queue()}], started {Started()}, album still '{vm.SelectedAlbum?.Album.Title}'",
            Queue() == "1 One.flac, 2 Two.flac, 10 Ten.flac" && vm.SelectedAlbum?.Album.Title == shownAlbum);

        // 5. Not music.
        Console.WriteLine("5. a text file:");
        var text = Path.Combine(scratch, "notes.txt");
        File.WriteAllText(text, "not music");
        vm.OpenFiles([text]);
        Settle(500);
        var afterText = Started();
        Expect($"status '{vm.StatusText}', started {afterText}", vm.StatusText.Contains("notes.txt") && afterText == "nothing");

        engine.Stop();
        Settle(300);
        Console.WriteLine(all ? "all OK" : "SOME WRONG");
        try { Directory.Delete(scratch, recursive: true); } catch (IOException) { }
    }

    /// <summary>
    /// --window rowclick [--artist Nhato]: a double-click on the row cut off at the
    /// bottom of each list (Artists, Albums, the song table, Playlists, Recently
    /// Added's albums), delivered as the mouse delivers it: two presses at the same
    /// point, the second with ClickCount 2, each on whatever is under that point at
    /// the time. A press must not scroll the list, and the double-click must land
    /// on the row that was under the pointer for the first press. The app's
    /// double-click handlers run too; ThemeLab can't decode the music, so nothing
    /// plays.
    /// </summary>
    private static void RunRowClick(MainWindow main, MainViewModel vm, AudioEngine engine, string artistName)
    {
        LoadRealLibrary(vm);
        vm.Volume = 0;
        engine.Volume = 0;
        var clickCount = typeof(System.Windows.Input.MouseButtonEventArgs).GetProperty("ClickCount")!;
        var all = true;

        static string Label(object? item) => item switch
        {
            ArtistGroup a => a.Name,
            AlbumItemViewModel a => a.Title,
            PlaylistItemViewModel p => p.Name,
            Track t => t.DisplayTitle,
            null => "nothing",
            _ => item.ToString() ?? "?",
        };

        static FrameworkElement? RowOf(DependencyObject? node)
        {
            while (node is not null and not System.Windows.Controls.ListBoxItem and not System.Windows.Controls.DataGridRow)
                node = VisualTreeHelper.GetParent(node);
            return node as FrameworkElement;
        }

        void Check(string name, System.Windows.Controls.Primitives.Selector list)
        {
            Pump();
            Settle(400);
            var scroller = FindFirst<System.Windows.Controls.ScrollViewer>(list)!;

            // The row that is only partly in view at the bottom.
            FrameworkElement? cut = null;
            double cutTop = 0;
            for (var i = 0; i < list.Items.Count && cut is null; i++)
            {
                if (list.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement row || !row.IsVisible)
                    continue;
                var top = row.TranslatePoint(new Point(0, 0), scroller).Y;
                if (top < scroller.ActualHeight - 4 && top + row.ActualHeight > scroller.ActualHeight)
                    (cut, cutTop) = (row, top);
            }
            if (cut is null)
            {
                Console.WriteLine($"{name}: no row is cut off at the bottom ({list.Items.Count} rows), skipped");
                return;
            }

            var point = new Point(Math.Min(cut.ActualWidth / 2, 120), cutTop + 2);
            string Under() => Label(RowOf(scroller.InputHitTest(point) as DependencyObject)?.DataContext);

            void Press(int count)
            {
                var target = (UIElement)scroller.InputHitTest(point);
                foreach (var routed in new[]
                         {
                             System.Windows.Input.Mouse.PreviewMouseDownEvent, System.Windows.Input.Mouse.MouseDownEvent,
                             System.Windows.Input.Mouse.PreviewMouseUpEvent, System.Windows.Input.Mouse.MouseUpEvent,
                         })
                {
                    var a = new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount,
                        System.Windows.Input.MouseButton.Left) { RoutedEvent = routed };
                    clickCount.SetValue(a, count);
                    target.RaiseEvent(a);
                }
                Pump();
                Settle(300);
            }

            // The row the app's handlers look up: the one holding the press.
            var doubleClicked = new List<string>();
            System.Windows.Input.MouseButtonEventHandler record = (_, e) => doubleClicked.Add(Label(RowOf(e.OriginalSource as DependencyObject)?.DataContext));
            ((System.Windows.Controls.Control)list).MouseDoubleClick += record;

            var clicked = Under();
            var offset = scroller.VerticalOffset;
            Press(1);
            var moved = scroller.VerticalOffset != offset;
            var selected = Label(list.SelectedItem);
            Press(2);
            ((System.Windows.Controls.Control)list).MouseDoubleClick -= record;
            var afterClicks = scroller.VerticalOffset;

            // Down from the clicked row still scrolls: the next row is out of view.
            System.Windows.Input.InputManager.Current.ProcessInput(new System.Windows.Input.KeyEventArgs(
                System.Windows.Input.Keyboard.PrimaryDevice, PresentationSource.FromVisual(main), Environment.TickCount, System.Windows.Input.Key.Down)
            { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent });
            Pump();
            Settle(300);
            var keyScrolled = scroller.VerticalOffset > afterClicks;

            var ok = !moved && selected == clicked && doubleClicked.SequenceEqual([clicked]) && keyScrolled;
            all &= ok;
            Console.WriteLine($"{name}: cut-off row '{clicked}'; first press selected '{selected}', " +
                              $"offset {offset} -> {afterClicks}{(moved ? " (scrolled)" : "")}; " +
                              $"double-click on [{string.Join(", ", doubleClicked)}]; " +
                              $"Down selects '{Label(list.SelectedItem)}', offset -> {scroller.VerticalOffset}{(keyScrolled ? "" : " (DIDN'T SCROLL)")} -> {(ok ? "OK" : "WRONG")}");
        }

        var artistList = (System.Windows.Controls.ListBox)main.FindName("ArtistList");
        vm.SelectedArtist = vm.Artists.FirstOrDefault(a => a.Name == artistName)
            ?? throw new InvalidOperationException($"no artist '{artistName}'");
        Check("Artists", artistList);

        vm.SelectedArtist = vm.Artists.FirstOrDefault(a => a.Name == artistName);
        Check("Albums", (System.Windows.Controls.ListBox)main.FindName("AlbumList"));

        // The album with the most songs, for a song table taller than its panel.
        var longest = vm.Artists.SelectMany(a => a.Albums).MaxBy(a => a.Tracks.Count)!;
        vm.SelectedArtist = vm.Artists.First(a => a.Albums.Contains(longest));
        Pump();
        vm.SelectedAlbum = vm.Albums.First(a => ReferenceEquals(a.Album, longest));
        Check("Songs", (System.Windows.Controls.DataGrid)main.FindName("TrackGrid"));

        vm.IsPlaylistMode = true;
        Check("Playlists", (System.Windows.Controls.ListBox)main.FindName("PlaylistList"));

        vm.SelectedPlaylist = vm.Playlists.First(p => p.IsRecentlyAdded);
        Check("Recently Added albums", (System.Windows.Controls.ListBox)main.FindName("RecentAlbumList"));

        engine.Stop();
        Console.WriteLine(all ? "all OK" : "SOME WRONG");
    }

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

    /// <summary>
    /// --window covers' library: a scratch copy of one real album folder
    /// (--album, read only), so Keep Best Cover rewrites the copies and never
    /// the music drive. Deleted afterwards with the other scratch folders.
    /// </summary>
    private static void LoadCoversLibrary(MainViewModel vm, string albums)
    {
        var dir = Path.Combine(Path.GetTempPath(), "themelab-covers-" + Guid.NewGuid().ToString("N")[..8]);
        _scratchDir = dir;
        var tracks = new List<Track>();

        // --album "a;b": several folders, each copied to a folder of its own.
        foreach (var album in albums.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var albumDir = Path.Combine(dir, Path.GetFileName(Path.GetDirectoryName(album))!, Path.GetFileName(album));
            Directory.CreateDirectory(albumDir);
            foreach (var file in Directory.GetFiles(album))
            {
                var copy = Path.Combine(albumDir, Path.GetFileName(file));
                File.Copy(file, copy);
                File.SetCreationTimeUtc(copy, File.GetCreationTimeUtc(file));
            }

            tracks.AddRange(Directory.GetFiles(albumDir)
                .Where(f => AudioFool.Core.Library.AudioFormats.IsSupported(f))
                .Select(f => AudioFool.Core.Library.TagReader.Read(f)));
        }

        typeof(MainViewModel)
            .GetMethod("ApplyLibrary", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(vm, [AudioFool.Core.Library.LibraryScanner.Build(tracks), false]);
        Console.WriteLine($"library: {tracks.Count} scratch copies of {albums} in {dir}");
    }

    /// <summary>
    /// --window stats --cutoffdemo 1 (session 70): five scratch albums made from the
    /// test fixtures, each a way a cover can be cut off, so the CUT-OFF COVERS
    /// section and the art shown can be checked without the music drive.
    /// </summary>
    private static List<Track> LoadCutOffLibrary(MainViewModel vm)
    {
        // tools\themelab\bin\<config>\net10.0-windows\win-x64 -> the repo.
        var fixtures = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, @"..\..\..\..\..\..\tests\AudioFool.Core.Tests\TestData"));
        var whole = File.ReadAllBytes(Path.Combine(fixtures, "noise-64.jpg"));
        var cut = whole.AsSpan(0, whole.Length / 3).ToArray();
        var small = File.ReadAllBytes(Path.Combine(fixtures, "cover.jpg"));

        var dir = Path.Combine(Path.GetTempPath(), "themelab-cutoffs-" + Guid.NewGuid().ToString("N")[..8]);
        _scratchDir = dir;
        var tracks = new List<Track>();

        // Album name -> each track's embedded pictures, and the folder's cover.jpg.
        (string Album, byte[][][] Pictures, byte[]? CoverJpg)[] albums =
        [
            ("First Track Cut", [[cut], [whole], [whole]], null),
            ("Folder File Cut", [[], []], cut),
            ("Whole", [[whole], [whole]], whole),
            ("Only Cut", [[cut], [cut]], null),
            ("Cut Beside Whole", [[cut, small]], null),
        ];

        foreach (var (album, pictures, coverJpg) in albums)
        {
            var albumDir = Path.Combine(dir, "Cut-Off Demo", album);
            Directory.CreateDirectory(albumDir);
            if (coverJpg is not null)
                File.WriteAllBytes(Path.Combine(albumDir, "cover.jpg"), coverJpg);

            for (var i = 0; i < pictures.Length; i++)
            {
                var path = Path.Combine(albumDir, $"{i + 1:00} Track.flac");
                File.Copy(Path.Combine(fixtures, "sample.flac"), path);
                using (var file = TagLib.File.Create(path))
                {
                    file.Tag.Title = $"Track {i + 1}";
                    file.Tag.Performers = ["Cut-Off Demo"];
                    file.Tag.AlbumArtists = ["Cut-Off Demo"];
                    file.Tag.Album = album;
                    file.Tag.Track = (uint)(i + 1);
                    file.Tag.Pictures = [.. pictures[i].Select(p => new TagLib.Picture(new TagLib.ByteVector(p))
                        { Type = TagLib.PictureType.FrontCover, MimeType = "image/jpeg" })];
                    file.Save();
                }
                tracks.Add(AudioFool.Core.Library.TagReader.Read(path));
            }
        }

        typeof(MainViewModel)
            .GetMethod("ApplyLibrary", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(vm, [AudioFool.Core.Library.LibraryScanner.Build(tracks), false]);
        vm.CoverCachePath = Path.Combine(dir, "covers.json");
        Console.WriteLine($"library: {tracks.Count} scratch tracks in {albums.Length} albums in {dir}");
        return tracks;
    }

    /// <summary>
    /// The cover check over the cut-off demo, then each album's art as the app
    /// draws it (the header width): grey (#808080) along the bottom row means the
    /// cut-off picture was shown. Prints OK or BROKEN for each expectation.
    /// </summary>
    private static void RunCutOffChecks(MainViewModel vm, AudioFool.Core.Library.MusicLibrary library)
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        vm.StartCoverCheck();
        Settle(300);
        while (vm.IsCheckingCovers)
            Settle(200);
        Console.WriteLine($"cover check: '{vm.StatusText}'");

        var art = new AlbumArtService();
        var allOk = true;
        void Expect(string what, bool ok)
        {
            allOk &= ok;
            Console.WriteLine($"  {(ok ? "OK    " : "BROKEN")} {what}");
        }

        bool ShowsCut(Album album)
        {
            var task = art.GetAlbumArtAsync(album, AlbumArtService.HeaderWidth);
            while (!task.IsCompleted)
                Settle(20);
            if (task.Result is not { } bitmap)
                return false;
            var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgr24, null, 0);
            var stride = converted.PixelWidth * 3;
            var row = new byte[stride];
            converted.CopyPixels(new Int32Rect(0, converted.PixelHeight - 1, converted.PixelWidth, 1), row, stride, 0);
            return row.All(b => Math.Abs(b - 128) <= 2);
        }

        var albums = library.Artists.SelectMany(a => a.Albums).ToDictionary(a => a.Title);
        Expect("First Track Cut shows track 2's whole cover", !ShowsCut(albums["First Track Cut"]));
        Expect("Folder File Cut shows its cut-off file, nothing else to show", ShowsCut(albums["Folder File Cut"]));
        Expect("Whole shows its cover", !ShowsCut(albums["Whole"]));
        Expect("Only Cut shows its cut-off cover, nothing else to show", ShowsCut(albums["Only Cut"]));
        // cover.jpg is plain #808080 itself, so this one is judged by the bytes picked.
        Expect("Cut Beside Whole shows the small whole cover",
            AudioFool.Core.Library.TagReader.ReadEmbeddedArt(albums["Cut Beside Whole"].Tracks[0].FilePath) is { } picked
            && picked.Length == 220 && !AudioFool.Core.Art.CutOffImage.Is(picked));

        var stats = vm.ComputeCutOffStatistics(AudioFool.Core.Art.CutOffCoverStatistics.CountFiles(vm.StatisticsTracks));
        Expect($"songs row: {stats.EmbeddedAlbums} albums, {stats.EmbeddedTracks} tracks (3, 4)", (stats.EmbeddedAlbums, stats.EmbeddedTracks) == (3, 4));
        Expect($"files row: {stats.Files!.Albums} album, {stats.Files.Files} of {stats.Files.AllFiles} files (1, 1 of 2)",
            (stats.Files.Albums, stats.Files.Files, stats.Files.AllFiles) == (1, 1, 2));
        Console.WriteLine(allOk ? "cut-off checks: ALL OK" : "cut-off checks: SOMETHING BROKE");
    }

    /// <summary>
    /// --window covers: the cover check, the Extra covers row, its filter, the
    /// album menu and Keep Best Cover, through the app's own code on the scratch
    /// album above. Prints each file's pictures, size and creation date before
    /// and after, and the folder's cover.jpg. Nothing is shown on screen.
    /// </summary>
    private static void RunCovers(MainWindow main, MainViewModel vm)
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        var albumDirs = Directory.GetDirectories(_scratchDir!, "*", SearchOption.AllDirectories)
            .Where(d => Directory.GetFiles(d).Length > 0).Order().ToList();

        void Files(string when)
        {
            Console.WriteLine($"[{when}]");
            foreach (var albumDir in albumDirs)
            {
                Console.WriteLine($"  {Path.GetFileName(Path.GetDirectoryName(albumDir))} / {Path.GetFileName(albumDir)}");
                foreach (var f in Directory.GetFiles(albumDir).Where(f => AudioFool.Core.Library.AudioFormats.IsSupported(f)).Order())
                {
                    var finding = AudioFool.Core.Art.CoverCleaner.Survey(f);
                    Console.WriteLine($"    {Path.GetFileName(f),-48} {new FileInfo(f).Length,12:N0} B  pictures {finding?.Pictures}  "
                        + $"padding {AudioFool.Core.Library.TagPadding.Read(f)?.Padding:N0}  created {File.GetCreationTimeUtc(f):u}");
                }
                var cover = Path.Combine(albumDir, "cover.jpg");
                Console.WriteLine(File.Exists(cover)
                    ? $"    cover.jpg {new FileInfo(cover).Length:N0} B {AudioFool.Core.Art.ImageInfo.Read(File.ReadAllBytes(cover))?.SizeText}"
                    : "    cover.jpg: none");
            }
        }

        // Runs Keep Best Cover from the artist menu over every artist, as Ctrl+A
        // then the menu would; with stopAfter, chooses the menu again (now "Stop
        // Keeping Best Covers") once that many songs are done.
        void KeepAll(string when, int? stopAfter = null)
        {
            vm.SelectedArtists = [.. vm.Artists];
            Console.WriteLine($"{when}: {vm.Artists.Count} artists selected, menu '{vm.KeepBestCoverHeader}' visible={vm.CanKeepBestCover}");
            var clock = System.Diagnostics.Stopwatch.StartNew();
            vm.KeepBestCoverForArtistsCommand.Execute(vm.Artists[0]);
            Settle(50);
            Console.WriteLine($"  running: menu '{vm.KeepBestCoverHeader}' visible={vm.CanKeepBestCover} progress shown={vm.ShowsProgress}");
            while (vm.IsKeepingCovers)
            {
                if (stopAfter is { } n && vm.KeepProgress.Done >= n)
                {
                    vm.KeepBestCoverForArtistsCommand.Execute(vm.Artists.FirstOrDefault());
                    Console.WriteLine($"  stop chosen at {vm.KeepProgress.Done} of {vm.KeepProgress.Total}");
                    stopAfter = null;
                }
                Settle(50);
            }
            Settle(300);
            Console.WriteLine($"  done in {clock.Elapsed.TotalSeconds:0.0} s: {vm.StatusText}");
            Console.WriteLine($"  menu now '{vm.KeepBestCoverHeader}', artists left under the filter: {vm.Artists.Count}, progress shown={vm.ShowsProgress}");
        }

        void Section(string when)
        {
            using var s = new CoverSectionViewModel(vm);
            Console.WriteLine($"[{when}] {s.Summary}  button '{s.ButtonText}' enabled={s.CanToggle}");
            foreach (var r in s.Rows) Console.WriteLine($"    {r.Label,-14} {r.Value,-46} clickable={r.IsClickable}  ({r.Detail})");
        }

        void AlbumMenu(string when)
        {
            main.UpdateLayout();
            Settle(200);
            var row = FindAll<System.Windows.Controls.ListBoxItem>(main)
                .FirstOrDefault(r => r.DataContext is AlbumItemViewModel && r.ContextMenu is not null && r.IsVisible);
            if (row?.ContextMenu is not { } menu)
            {
                Console.WriteLine($"  {when} album menu: none");
                return;
            }

            menu.PlacementTarget = row;
            menu.IsOpen = true;
            Settle(200);
            var items = menu.Items.OfType<System.Windows.Controls.MenuItem>()
                .Select(m => $"'{m.Header}'{(m.Visibility == Visibility.Visible ? "" : " (hidden)")}");
            Console.WriteLine($"  {when} album menu: {string.Join(", ", items)}");
            menu.IsOpen = false;
        }

        Files("before");
        Section("before the check");
        vm.StartCoverCheck();
        Settle(300);
        while (vm.IsCheckingCovers)
            Settle(200);
        Settle(300);
        Console.WriteLine($"check: '{vm.StatusText}'");
        Section("after the check");
        AlbumMenu("no filter:");

        using (var s = new CoverSectionViewModel(vm))
        {
            var row = s.Rows.FirstOrDefault(r => r.IsClickable)
                ?? throw new InvalidOperationException("no extra covers found in this album");
            vm.ApplyStatisticsChoice(row);
        }
        Console.WriteLine($"filter on: '{vm.LibraryFilter}' canKeep={vm.CanKeepBestCover}  status: {vm.StatusText}");
        AlbumMenu("filtered:");

        // --stopafter N: stop the first run after N songs, then run again for the rest.
        if (Arg(Environment.GetCommandLineArgs(), "--stopafter") is { } stop)
        {
            KeepAll("first run", int.Parse(stop, CultureInfo.InvariantCulture));
            Section("after stopping");
            Files("after stopping");
            KeepAll("second run");
        }
        else
        {
            KeepAll("run");
        }

        Section("after keeping");
        Files("after");
        Console.WriteLine($"leftover working files: {Directory.GetFiles(_scratchDir!, ".audiofool-cover-*", SearchOption.AllDirectories).Length}");
    }

    /// <summary>
    /// --window pngconvert: the cover check, both PNG COVERS rows, their filters, the
    /// album menu and Convert PNG Covers to JPEG, through the app's own code on
    /// scratch copies of real albums (--album "a;b"). Prints each song's pictures
    /// and each folder's image files before and after. The replaced PNGs go to the
    /// Recycle Bin, as in the app (the scratch folder is on a fixed drive).
    /// </summary>
    private static void RunPngConvert(MainWindow main, MainViewModel vm)
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        var albumDirs = Directory.GetDirectories(_scratchDir!, "*", SearchOption.AllDirectories)
            .Where(d => Directory.GetFiles(d).Length > 0).Order().ToList();

        void Files(string when)
        {
            Console.WriteLine($"[{when}]");
            foreach (var albumDir in albumDirs)
            {
                Console.WriteLine($"  {Path.GetFileName(Path.GetDirectoryName(albumDir))} / {Path.GetFileName(albumDir)}");
                foreach (var f in Directory.GetFiles(albumDir).Where(f => AudioFool.Core.Library.AudioFormats.IsSupported(f)).Order())
                {
                    using var t = TagLib.File.Create(f);
                    var pics = string.Join(", ", t.Tag.Pictures.Select(p =>
                        AudioFool.Core.Art.ImageInfo.Read(p.Data.Data) is { } i ? $"{i.FormatText} {i.SizeText} {p.Data.Count:N0} B ({p.MimeType})" : "unreadable"));
                    Console.WriteLine($"    {Path.GetFileName(f),-44} {new FileInfo(f).Length,12:N0} B  created {File.GetCreationTimeUtc(f):u}  [{pics}]");
                }
                foreach (var img in Directory.GetFiles(albumDir).Where(f => Path.GetExtension(f).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg").Order())
                {
                    var i = AudioFool.Core.Art.ImageInfo.Read(File.ReadAllBytes(img));
                    Console.WriteLine($"    file {Path.GetFileName(img),-30} {new FileInfo(img).Length,10:N0} B  {i?.FormatText} {i?.SizeText}");
                }
            }
            var art = vm.StatisticsTracks.Select(t => t.FolderArtPath).Distinct().Select(p => p is null ? "none" : Path.GetFileName(p));
            Console.WriteLine($"  library's folder art: {string.Join(", ", art)}");
        }

        void Rows(string when)
        {
            using var s = new PngSectionViewModel(vm);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (s.Rows.LastOrDefault()?.Value.StartsWith("Looking") == true && clock.Elapsed < TimeSpan.FromSeconds(30))
                Settle(50);
            Console.WriteLine($"[{when}] {s.Summary}");
            foreach (var r in s.Rows) Console.WriteLine($"    {r.Label,-22} {r.Value,-40} clickable={r.IsClickable}");
        }

        void AlbumMenu(string when)
        {
            main.UpdateLayout();
            Settle(200);
            var row = FindAll<System.Windows.Controls.ListBoxItem>(main)
                .FirstOrDefault(r => r.DataContext is AlbumItemViewModel && r.ContextMenu is not null && r.IsVisible);
            if (row?.ContextMenu is not { } menu)
            {
                Console.WriteLine($"  {when} album menu: none");
                return;
            }
            menu.PlacementTarget = row;
            menu.IsOpen = true;
            Settle(200);
            var items = menu.Items.OfType<System.Windows.Controls.MenuItem>()
                .Select(m => $"'{m.Header}'{(m.Visibility == Visibility.Visible ? "" : " (hidden)")}");
            Console.WriteLine($"  {when} album menu: {string.Join(", ", items)}");
            menu.IsOpen = false;
        }

        void Convert(string label)
        {
            using (var s = new PngSectionViewModel(vm))
            {
                var clock = System.Diagnostics.Stopwatch.StartNew();
                while (s.Rows.LastOrDefault()?.Value.StartsWith("Looking") == true && clock.Elapsed < TimeSpan.FromSeconds(30))
                    Settle(50);
                var row = s.Rows.FirstOrDefault(r => r.Label == label && r.IsClickable);
                if (row is null)
                {
                    Console.WriteLine($"{label}: no clickable row");
                    return;
                }
                vm.ApplyStatisticsChoice(row);
            }
            Console.WriteLine($"filter on: '{vm.LibraryFilter}' canConvert={vm.CanConvertPngs} canKeep={vm.CanKeepBestCover}, {vm.Artists.Count} artists");
            AlbumMenu("filtered:");
            vm.SelectedArtists = [.. vm.Artists];
            var run = System.Diagnostics.Stopwatch.StartNew();
            vm.ConvertPngsForArtistsCommand.Execute(vm.Artists[0]);
            Settle(50);
            Console.WriteLine($"  running: menu '{vm.ConvertPngHeader}' visible={vm.CanConvertPngs} progress shown={vm.ShowsProgress} canKeep={vm.CanKeepBestCover}");
            while (vm.IsConvertingPngs)
                Settle(50);
            Settle(300);
            Console.WriteLine($"  done in {run.Elapsed.TotalSeconds:0.0} s: {vm.StatusText}");
            Console.WriteLine($"  menu now '{vm.ConvertPngHeader}', artists left under the filter: {vm.Artists.Count}, progress shown={vm.ShowsProgress}");
            vm.ClearLibraryFilterCommand.Execute(null);
            Settle(100);
        }

        Files("before");
        vm.StartCoverCheck();
        Settle(300);
        while (vm.IsCheckingCovers)
            Settle(200);
        Settle(300);
        Console.WriteLine($"check: '{vm.StatusText}'");
        Rows("after the check");
        AlbumMenu("no filter:");

        Convert(AudioFool.Core.Art.PngCoverStatistics.EmbeddedLabel);
        Rows("after embedded");
        Convert(AudioFool.Core.Art.PngCoverStatistics.FilesLabel);
        Rows("after files");
        Files("after");
        Console.WriteLine($"leftover working files: {Directory.GetFiles(_scratchDir!, ".audiofool-cover-*", SearchOption.AllDirectories).Length}");
    }

    /// <summary>
    /// --window multiedit's library: one album split across three artist rows,
    /// and the same band's album under two stray names, as scratch copies of the
    /// test fixtures. No album artist, so each track files under its artist.
    /// </summary>
    private static void LoadMultiLibrary(MainViewModel vm)
    {
        var fixtures = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            @"..\..\..\..\..\..\tests\AudioFool.Core.Tests\TestData"));
        var dir = Path.Combine(Path.GetTempPath(), "themelab-multi-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        _scratchDir = dir;

        (string Title, string Artist, string Album, int Number, string Ext)[] songs =
        [
            ("One", "Lab Band", "Lab Album", 1, ".flac"),
            ("Two", "Lab Band", "Lab Album", 2, ".flac"),
            ("Three", "Lab Band feat. Guest", "Lab Album", 3, ".flac"),
            ("Four", "Lab Band & Friend", "Lab Album", 4, ".mp3"),
            ("Five", "Lab Band", "Lab Album (Disc 2)", 5, ".flac"),
            ("Six", "Lab Band", "Lab Album [Bonus]", 6, ".mp3"),
            ("Seven", "Other Band", "Other Album", 1, ".flac"),
        ];

        var tracks = new List<Track>();
        foreach (var song in songs)
        {
            var path = Path.Combine(dir, $"{song.Number:00} {song.Title}{song.Ext}");
            File.Copy(Path.Combine(fixtures, "sample" + song.Ext), path);
            var seeded = AudioFool.Core.Library.TagWriter.WriteTrackTags(
                AudioFool.Core.Library.TagReader.Read(path),
                new AudioFool.Core.Library.TrackTagEdit(song.Title, song.Artist, "", song.Album, 2020, song.Number, 6, 1, 1),
                art: null, folderArtPath: null);
            tracks.Add(seeded.UpdatedTrack!);
        }

        typeof(MainViewModel)
            .GetMethod("ApplyLibrary", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(vm, [AudioFool.Core.Library.LibraryScanner.Build(tracks), false]);
        Console.WriteLine($"library: {tracks.Count} scratch tracks in {dir}");
    }

    /// <summary>
    /// --window multiedit: several artists, then several albums, picked in the
    /// real lists as Ctrl-click would, edited through the several-tracks dialog's
    /// view model and the app's own save, on the scratch library above. Prints
    /// what the view model saw of each selection, the dialog's title and boxes,
    /// the files afterwards, and where the selection lands. The modal dialog
    /// itself is never shown: its view model is built as the command builds it.
    /// </summary>
    private static void RunMultiEdit(MainWindow main, MainViewModel vm, string outPath, double w, double h, double scale)
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var artistList = (System.Windows.Controls.ListBox)main.FindName("ArtistList");
        var albumList = (System.Windows.Controls.ListBox)main.FindName("AlbumList");
        void Log(string s) => Console.WriteLine(s);
        string Names<T>(IEnumerable<T> items, Func<T, string> name) => string.Join(" | ", items.Select(name));
        string State() => $"selected artist '{vm.SelectedArtist?.Name}', albums [{Names(vm.Albums, a => a.Title)}], "
            + $"selected album '{vm.SelectedAlbum?.Title}', songs [{Names(vm.Tracks, t => t.Title)}]";
        ArtistGroup Artist(string name) => vm.Artists.First(a => a.Name == name);
        AlbumItemViewModel Album(string title) => vm.Albums.First(a => a.Title == title);

        void SaveEdit(TagEditViewModel edit, IReadOnlyList<Track> tracks)
        {
            var before = vm.StatusText;
            var task = (Task)typeof(MainViewModel).GetMethod("ApplySelectedTracksEditAsync", flags)!
                .Invoke(vm, [tracks, edit.BuildTracksEdit(), true])!;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (!task.IsCompleted && clock.ElapsedMilliseconds < 10000)
                Settle(50);
            Settle(300);
            Log($"  status: {vm.StatusText}");
        }

        TagEditViewModel Open(AudioFool.Core.Library.TagSelection selection)
        {
            var edit = new TagEditViewModel(selection.Tracks, selection.Description);
            WaitForDetails(edit);
            Log($"  dialog: '{edit.WindowTitle}'");
            Log($"    tracks: {Names(selection.Tracks, t => $"{t.Title} ({t.Artist} / {t.Album})")}");
            Log($"    artist '{edit.Artist}' [{edit.ArtistPlaceholder}]  album artist '{edit.AlbumArtist}' [{edit.AlbumArtistPlaceholder}]");
            Log($"    album '{edit.AlbumTitle}' [{edit.AlbumTitlePlaceholder}]  year '{edit.Year}' [{edit.YearPlaceholder}]  art shown={edit.ShowsArt}");
            return edit;
        }

        Settle(300);
        Log($"artists: {Names(vm.Artists, a => a.Name)}");

        // 1. Several artists: a click, then two Ctrl-clicks.
        artistList.SelectedItem = Artist("Lab Band feat. Guest");
        Settle(100);
        artistList.SelectedItems.Add(Artist("Lab Band & Friend"));
        artistList.SelectedItems.Add(Artist("Lab Band"));
        Settle(200);
        Log($"artists picked: list {artistList.SelectedItems.Count}, view model [{Names(vm.SelectedArtists, a => a.Name)}]");
        Log($"  {State()}");
        Save(main, Path.ChangeExtension(outPath, null) + "-artists.png", w, h, scale);

        var artistsSelection = (AudioFool.Core.Library.TagSelection)typeof(MainViewModel).GetMethod("ArtistsSelection", flags)!
            .Invoke(vm, [Artist("Lab Band & Friend")])!;
        var artistEdit = Open(artistsSelection);
        var single = (AudioFool.Core.Library.TagSelection)typeof(MainViewModel).GetMethod("ArtistsSelection", flags)!
            .Invoke(vm, [Artist("Other Band")])!;
        Log($"  an unselected row's menu edits only it: '{single.Description}'");

        artistEdit.Artist = "Lab Band";
        artistEdit.AlbumArtist = "Lab Band";
        Log("save: artist and album artist 'Lab Band'");
        SaveEdit(artistEdit, artistsSelection.Tracks);
        Log($"  artists now: {Names(vm.Artists, a => $"{a.Name} ({a.AlbumSummary})")}");
        Log($"  {State()}");

        // 2. Several albums of the merged artist.
        albumList.SelectedItem = Album("Lab Album (Disc 2)");
        Settle(100);
        albumList.SelectedItems.Add(Album("Lab Album [Bonus]"));
        Settle(200);
        Log($"albums picked: list {albumList.SelectedItems.Count}, view model [{Names(vm.SelectedAlbums, a => a.Title)}]");
        Log($"  {State()}");
        Save(main, Path.ChangeExtension(outPath, null) + "-albums.png", w, h, scale);

        AudioFool.Core.Library.TagSelection? Albums(string title) =>
            (AudioFool.Core.Library.TagSelection?)typeof(MainViewModel).GetMethod("AlbumsSelection", flags)!.Invoke(vm, [Album(title)]);
        Log($"  menu on an unselected album opens the one-album dialog: {Albums("Lab Album") is null}");
        var albumsSelection = Albums("Lab Album [Bonus]")!;
        var albumEdit = Open(albumsSelection);
        albumEdit.AlbumTitle = "Lab Album";
        Log("save: album 'Lab Album'");
        SaveEdit(albumEdit, albumsSelection.Tracks);
        Log($"  {State()}");

        albumList.SelectedItem = vm.Albums.First();
        Settle(100);
        Log($"menu with one album selected opens the one-album dialog: {Albums(vm.Albums.First().Title) is null}");

        Log("files:");
        foreach (var file in Directory.EnumerateFiles(_scratchDir!).Order())
        {
            var t = AudioFool.Core.Library.TagReader.Read(file);
            Log($"  {Path.GetFileName(file)}: '{t.Title}' / '{t.Artist}' / '{t.AlbumArtist}' / '{t.Album}' #{t.TrackNumber}/{t.TrackCount} disc {t.DiscNumber}/{t.DiscCount} year {t.Year}");
        }
        Save(main, outPath, w, h, scale);
    }

    /// <summary>
    /// --window albumeditor: the Edit Album Tags window following the Albums list,
    /// on --window multiedit's scratch library. The window is shown off-screen and
    /// never activated, and the Save / Don't Save question is answered from a
    /// script (both through MainViewModel's seams), so nothing reaches the desktop.
    /// Prints what the window shows after each step and what reached the files.
    /// </summary>
    private static void RunAlbumEditor(MainWindow main, MainViewModel vm, string outPath, double scale)
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        void Log(string s) => Console.WriteLine(s);

        static void OffScreen(Window w)
        {
            w.ShowActivated = false;
            w.ShowInTaskbar = false;
            // After the window's own Loaded handler, which centres it over its owner
            // and clamps that onto a monitor, and before anything is drawn.
            w.Loaded += (_, _) => { w.Left = -20000; w.Top = -20000; };
        }

        MainViewModel.ShowAlbumEditorWindow = w => { OffScreen(w); w.Show(); };
        var answers = new Queue<UnsavedChoice>();
        MainViewModel.AskSaveChanges = (_, title, message) =>
        {
            var answer = answers.Dequeue();
            Log($"  asked: '{title}' / '{message}' -> {answer}");
            return answer;
        };

        TagEditWindow? Editor() => (TagEditWindow?)typeof(MainViewModel).GetField("_albumEditor", flags)!.GetValue(vm);
        int Windows() => Application.Current.Windows.OfType<TagEditWindow>().Count(w => w.IsVisible);
        void Wait(int ms = 600)
        {
            Settle(ms);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (vm.StatusText.StartsWith("Saving") && clock.ElapsedMilliseconds < 5000)
                Settle(50);
            Settle(300);
        }
        void State(string step)
        {
            var e = Editor();
            Log($"{step}");
            Log($"  list: '{vm.SelectedArtist?.Name}' / '{vm.SelectedAlbum?.Title}'   windows open: {Windows()}");
            Log(e is null
                ? "  window: closed"
                : $"  window: '{e.Title}' album '{e.ViewModel.AlbumTitle}' genre '{e.ViewModel.Genre}' changes={e.ViewModel.HasChanges} "
                  + $"close button '{((System.Windows.Controls.Button)e.FindName("CancelButton")).Content}'");
        }
        AlbumItemViewModel Album(string title) => vm.Albums.First(a => a.Title == title);
        string Genre(string file) => AudioFool.Core.Library.TagReader.ReadDetails(Path.Combine(_scratchDir!, file))?.Genre ?? "?";

        Settle(300);
        vm.SelectedArtist = vm.Artists.First(a => a.Name == "Lab Band");
        vm.SelectedAlbum = Album("Lab Album");
        Settle(200);

        vm.EditAlbumTagsCommand.Execute(vm.SelectedAlbum);
        Wait();
        State("1. opened from the album row");

        vm.SelectedAlbum = Album("Lab Album (Disc 2)");
        Wait();
        State("2. another album clicked, nothing typed (no question)");

        Editor()!.ViewModel.Genre = "Rock";
        answers.Enqueue(UnsavedChoice.Cancel);
        vm.SelectedAlbum = Album("Lab Album [Bonus]");
        Wait();
        State("3. Genre typed, another album clicked, Cancel");

        answers.Enqueue(UnsavedChoice.Discard);
        vm.SelectedAlbum = Album("Lab Album [Bonus]");
        Wait();
        State("4. clicked again, Don't Save");
        Log($"  file 05 Five.flac genre '{Genre("05 Five.flac")}' (expect empty)");

        Editor()!.ViewModel.Genre = "Jazz";
        answers.Enqueue(UnsavedChoice.Save);
        vm.SelectedAlbum = Album("Lab Album");
        Wait();
        State("5. Genre typed, another album clicked, Save");
        Log($"  file 06 Six.mp3 genre '{Genre("06 Six.mp3")}' (expect Jazz)");

        Editor()!.ViewModel.AlbumTitle = "Lab Album Renamed";
        typeof(TagEditWindow).GetMethod("Save_Click", flags)!.Invoke(Editor(), [null, new RoutedEventArgs()]);
        Wait(1000);
        State("6. renamed with the window's Save: stays open, list follows");
        Log($"  albums: {string.Join(" | ", vm.Albums.Select(a => $"{a.Title} ({a.Album.Tracks.Count})"))}");

        vm.SelectedArtist = vm.Artists.First(a => a.Name == "Other Band");
        Wait();
        State("7. another artist clicked: the window takes its first album");

        vm.EditAlbumTagsCommand.Execute(vm.SelectedAlbum);
        Wait();
        State("8. Edit Album Tags again: the same window, not a second");

        var seven = vm.Tracks.First();
        _ = vm.ApplyInlineEditAsync(seven, AudioFool.Core.Library.InlineField.Title, "Seven B");
        Wait();
        var shown = (Album)typeof(MainViewModel).GetField("_albumEditorAlbum", flags)!.GetValue(vm)!;
        Log($"9. a title edited in the grid underneath: the window's album now holds '{shown.Tracks[0].Title}' (expect Seven B)");

        vm.IsPlaylistMode = true;
        Wait();
        State("10. playlists shown: the window stays");
        vm.IsPlaylistMode = false;
        Wait();

        var editor = Editor()!;
        Save(editor, Path.ChangeExtension(outPath, null) + "-window.png", editor.ActualWidth, editor.ActualHeight, scale);

        var prompt = (PromptWindow)Activator.CreateInstance(typeof(PromptWindow), flags, null,
            [editor, "Unsaved Changes", "Save your changes to Lab Album [Bonus] before editing Lab Album?", "Save", null, null], null)!;
        ((UIElement)prompt.FindName("DiscardButton")).Visibility = Visibility.Visible;
        OffScreen(prompt);
        prompt.Show();
        Settle(300);
        Save(prompt, Path.ChangeExtension(outPath, null) + "-prompt.png", prompt.ActualWidth, prompt.ActualHeight, scale);
        Log($"prompt: {prompt.ActualWidth:0}x{prompt.ActualHeight:0}");
        prompt.Close();

        typeof(TagEditWindow).GetMethod("Cancel_Click", flags)!.Invoke(editor, [null, new RoutedEventArgs()]);
        Wait();
        State("11. Close");

        // Session 67: Escape is Cancel, asking first when something was typed.
        void Escape()
        {
            var target = System.Windows.Input.Keyboard.FocusedElement as UIElement ?? Editor()!;
            if (!ReferenceEquals(Window.GetWindow(target), Editor()))
                target = Editor()!;
            target.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, PresentationSource.FromVisual(Editor()!), 0, System.Windows.Input.Key.Escape)
                { RoutedEvent = System.Windows.Input.Keyboard.KeyDownEvent });
        }

        vm.EditAlbumTagsCommand.Execute(vm.SelectedAlbum);
        Wait();
        Escape();
        Wait();
        State("12. reopened, Escape with nothing typed: closes, no question");

        vm.EditAlbumTagsCommand.Execute(vm.SelectedAlbum);
        Wait();
        Editor()!.ViewModel.Genre = "Blues";
        answers.Enqueue(UnsavedChoice.Cancel);
        Escape();
        Wait();
        State("13. Genre typed, Escape, Cancel: stays open, Blues kept");

        var escaped = ((Album)typeof(MainViewModel).GetField("_albumEditorAlbum", flags)!.GetValue(vm)!).Tracks[0].FilePath;
        answers.Enqueue(UnsavedChoice.Save);
        Escape();
        Wait(1000);
        State("14. Escape, Save: saved, then closed");
        Log($"  file {Path.GetFileName(escaped)} genre '{AudioFool.Core.Library.TagReader.ReadDetails(escaped)?.Genre}' (expect Blues)");

        vm.EditAlbumTagsCommand.Execute(vm.SelectedAlbum);
        Wait();
        Editor()!.ViewModel.Genre = "Folk";
        answers.Enqueue(UnsavedChoice.Discard);
        Escape();
        Wait();
        State("15. Genre typed, Escape, Don't Save: closed");
        Log($"  file {Path.GetFileName(escaped)} genre '{AudioFool.Core.Library.TagReader.ReadDetails(escaped)?.Genre}' (expect Blues)");
        Log($"unused answers: {answers.Count}");

        Log("files:");
        foreach (var file in Directory.EnumerateFiles(_scratchDir!).Order())
        {
            var t = AudioFool.Core.Library.TagReader.Read(file);
            Log($"  {Path.GetFileName(file)}: '{t.Title}' / '{t.Artist}' / '{t.Album}' genre '{Genre(Path.GetFileName(file))}'");
        }
    }

    /// <summary>
    /// --window trackeditor: the one-song Edit Tags window following the Songs
    /// table, on --window multiedit's scratch library, shown off-screen and never
    /// activated, with the Save / Don't Save question answered from a script.
    /// Rows are selected through the real grid, so the window's own
    /// SelectionChanged handler feeds the view model, as a click would.
    /// </summary>
    private static void RunTrackEditor(MainWindow main, MainViewModel vm)
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var grid = (System.Windows.Controls.DataGrid)main.FindName("TrackGrid");
        void Log(string s) => Console.WriteLine(s);

        MainViewModel.ShowAlbumEditorWindow = w =>
        {
            w.ShowActivated = false;
            w.ShowInTaskbar = false;
            w.Loaded += (_, _) => { w.Left = -20000; w.Top = -20000; };
            w.Show();
        };
        var answers = new Queue<UnsavedChoice>();
        MainViewModel.AskSaveChanges = (_, title, message) =>
        {
            var answer = answers.Dequeue();
            Log($"  asked: '{message}' -> {answer}");
            return answer;
        };

        TagEditWindow? Editor() => (TagEditWindow?)typeof(MainViewModel).GetField("_trackEditor", flags)!.GetValue(vm);
        void Wait(int ms = 600)
        {
            Settle(ms);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (vm.StatusText.StartsWith("Saving") && clock.ElapsedMilliseconds < 5000)
                Settle(50);
            Settle(300);
        }
        void Select(string title)
        {
            grid.SelectedItems.Clear();
            grid.SelectedItem = vm.Tracks.First(t => t.Title == title);
            Wait();
        }
        void State(string step)
        {
            var e = Editor();
            Log(step);
            Log($"  table: [{string.Join(" | ", vm.Tracks.Select(t => t.Title))}] selected [{string.Join(" | ", grid.SelectedItems.OfType<Track>().Select(t => t.Title))}]   windows open: "
                + Application.Current.Windows.OfType<TagEditWindow>().Count(w => w.IsVisible));
            Log(e is null
                ? "  window: closed"
                : $"  window: '{e.Title}' title '{e.ViewModel.Title}' album '{e.ViewModel.AlbumTitle}' genre '{e.ViewModel.Genre}' changes={e.ViewModel.HasChanges} "
                  + $"button '{((System.Windows.Controls.Button)e.FindName("CancelButton")).Content}'");
        }
        string File(string name)
        {
            var path = Path.Combine(_scratchDir!, name);
            var t = AudioFool.Core.Library.TagReader.Read(path);
            return $"'{t.Title}' / '{t.Album}' genre '{AudioFool.Core.Library.TagReader.ReadDetails(path)?.Genre}'";
        }

        Settle(300);
        vm.SelectedArtist = vm.Artists.First(a => a.Name == "Lab Band");
        vm.SelectedAlbum = vm.Albums.First(a => a.Title == "Lab Album");
        Wait();
        Select("One");
        vm.EditTrackTagsCommand.Execute(vm.Tracks.First(t => t.Title == "One"));
        Wait();
        State("1. opened on One");

        Select("Two");
        State("2. Two clicked, nothing typed (no question)");

        Editor()!.ViewModel.Genre = "Pop";
        answers.Enqueue(UnsavedChoice.Cancel);
        Select("One");
        State("3. Genre typed, One clicked, Cancel (the table goes back to Two)");

        answers.Enqueue(UnsavedChoice.Discard);
        Select("One");
        State("4. One clicked again, Don't Save");
        Log($"  file 02: {File("02 Two.flac")} (expect no genre)");

        Editor()!.ViewModel.Genre = "Pop";
        answers.Enqueue(UnsavedChoice.Save);
        Select("Two");
        State("5. Genre typed on One, Two clicked, Save");
        Log($"  file 01: {File("01 One.flac")} (expect Pop)");

        Editor()!.ViewModel.Title = "Two Renamed";
        typeof(TagEditWindow).GetMethod("Save_Click", flags)!.Invoke(Editor(), [null, new RoutedEventArgs()]);
        Wait(1000);
        State("6. retitled with the window's Save: stays open, row kept");

        grid.SelectedItems.Add(vm.Tracks.First(t => t.Title == "One"));
        Wait();
        State("7. One Ctrl-clicked too: two rows, the window stays");

        Select("Two Renamed");
        _ = vm.ApplyInlineEditAsync(vm.Tracks.First(t => t.Title == "Two Renamed"), AudioFool.Core.Library.InlineField.Title, "Two Again");
        Wait();
        State("8. retitled in the grid underneath: the window reads it again");

        Editor()!.ViewModel.AlbumTitle = "Elsewhere";
        typeof(TagEditWindow).GetMethod("Save_Click", flags)!.Invoke(Editor(), [null, new RoutedEventArgs()]);
        Wait(1000);
        State("9. moved to another album with the window's Save: it leaves the table, the window stays on it");

        vm.SelectedAlbum = vm.Albums.First(a => a.Title == "Lab Album (Disc 2)");
        Wait();
        State("10. another album: nothing selected yet, the window stays");

        vm.EditTrackTagsCommand.Execute(vm.Tracks.First());
        Wait();
        State("11. Edit Tags on a row: the same window moves");

        typeof(TagEditWindow).GetMethod("Cancel_Click", flags)!.Invoke(Editor(), [null, new RoutedEventArgs()]);
        Wait();
        State("12. Close");
        Log($"unused answers: {answers.Count}");
        foreach (var file in Directory.EnumerateFiles(_scratchDir!).Order())
            Log($"  {Path.GetFileName(file)}: {File(Path.GetFileName(file))}");
    }

    /// <summary>
    /// --window nowplayingart: the scratch album's second track in the now-playing
    /// bar (as last time's track is restored, nothing played), then a new cover
    /// saved album-wide through the album tag window's own save. The bar's picture
    /// must follow without a track change: it was only loaded when a track started.
    /// </summary>
    private static void RunNowPlayingArt(MainViewModel vm)
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        const System.Reflection.BindingFlags Private = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;

        var library = typeof(MainViewModel).GetField("_library", Private)!.GetValue(vm)!;
        IReadOnlyList<Track> AllTracks() =>
            ((IEnumerable<Track>)library.GetType().GetProperty("AllTracks")!.GetValue(library)!).ToList();
        var playing = AllTracks().First(t => t.Title == "Two");

        typeof(MainViewModel).GetMethod("ShowRestoredTrack", Private)!.Invoke(vm, [playing]);
        Settle(500);
        Console.WriteLine($"before: now playing '{vm.NowPlaying?.Title}', picture {(vm.NowPlayingArt is { } a ? $"{a.PixelWidth}px" : "none")}");

        // A plain red 300 px JPEG.
        var red = new System.Windows.Media.Imaging.WriteableBitmap(300, 300, 96, 96, System.Windows.Media.PixelFormats.Bgr32, null);
        var pixels = Enumerable.Repeat(unchecked((int)0xFFE02020), 300 * 300).ToArray();
        red.WritePixels(new Int32Rect(0, 0, 300, 300), pixels, 300 * 4, 0);
        var encoder = new System.Windows.Media.Imaging.JpegBitmapEncoder { QualityLevel = 95 };
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(red));
        using var jpeg = new MemoryStream();
        encoder.Save(jpeg);

        var album = AudioFool.Core.Library.LibraryScanner.Build(AllTracks().ToList()).Artists[0].Albums[0];
        var edit = new AudioFool.Core.Library.AlbumTagEdit();
        var save = (Task)typeof(MainViewModel).GetMethod("ApplyAlbumEditAsync", Private)!
            .Invoke(vm, [album, edit, new AudioFool.Core.Library.ArtPayload(jpeg.ToArray(), "image/jpeg"), false])!;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (!save.IsCompleted && clock.ElapsedMilliseconds < 10000) Settle(50);
        while (vm.NowPlayingArt is null && clock.ElapsedMilliseconds < 5000) Settle(50);
        Settle(300);

        var embedded = AudioFool.Core.Library.TagReader.ReadEmbeddedArt(playing.FilePath);
        var art = vm.NowPlayingArt;
        var redOk = false;
        if (art is not null)
        {
            var px = new byte[4];
            new System.Windows.Media.Imaging.FormatConvertedBitmap(art, System.Windows.Media.PixelFormats.Bgr32, null, 0)
                .CopyPixels(new Int32Rect(art.PixelWidth / 2, art.PixelHeight / 2, 1, 1), px, 4, 0);
            redOk = px[2] > 180 && px[1] < 80 && px[0] < 80;
            Console.WriteLine($"  centre pixel R{px[2]} G{px[1]} B{px[0]}");
        }
        Console.WriteLine($"after: status='{vm.StatusText}' cover in file={(embedded is { Length: > 0 } ? $"{embedded.Length:N0} B" : "none")} "
            + $"now playing '{vm.NowPlaying?.Title}' same file={vm.NowPlaying?.FilePath == playing.FilePath}, picture {(art is null ? "none" : $"{art.PixelWidth}px")} "
            + $"{(redOk ? "OK" : "BROKEN")}");
    }

    /// <summary>
    /// --window safesave --long "a.flac;b.flac;...": copies of real songs (never
    /// the songs themselves) in a scratch folder, given a new 1000 px cover
    /// album-wide through the view model's own album save, which doesn't fit the
    /// tags' room, so each is saved on a copy and swapped in (session 60). The
    /// main window is closed straight after the save starts: it must stay open
    /// until the save is done, then close itself. Then each copy is checked
    /// against its original: the audio byte for byte, the one cover, the creation
    /// date, and nothing left beside them. settings.json is put back afterwards
    /// (closing saves the window's place).
    /// </summary>
    private static void RunSafeSave(MainWindow main, MainViewModel vm, string files)
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        const System.Reflection.BindingFlags Private = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var settingsKept = File.Exists(AppSettings.SettingsPath) ? File.ReadAllBytes(AppSettings.SettingsPath) : null;

        var dir = Path.Combine(Path.GetTempPath(), "themelab-safesave-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        _scratchDir = dir;
        try
        {
            var originals = files.Split(';', StringSplitOptions.RemoveEmptyEntries);
            var copies = new List<Track>();
            var created = new Dictionary<string, DateTime>();
            foreach (var original in originals)
            {
                var copy = Path.Combine(dir, Path.GetFileName(original));
                File.Copy(original, copy);
                created[copy] = File.GetCreationTimeUtc(copy);
                copies.Add(AudioFool.Core.Library.TagReader.Read(copy));
            }

            typeof(MainViewModel).GetMethod("ApplyLibrary", Private)!
                .Invoke(vm, [AudioFool.Core.Library.LibraryScanner.Build(copies), false]);
            var album = AudioFool.Core.Library.LibraryScanner.Build(copies).Artists[0].Albums[0];
            var first = album.Tracks[0];

            // A 1000 px JPEG of noise, so it is a few hundred kilobytes and no tag's padding takes it.
            var random = new Random(60);
            var pixels = new byte[1000 * 1000 * 4];
            random.NextBytes(pixels);
            var noise = System.Windows.Media.Imaging.BitmapSource.Create(1000, 1000, 96, 96,
                System.Windows.Media.PixelFormats.Bgr32, null, pixels, 1000 * 4);
            var encoder = new System.Windows.Media.Imaging.JpegBitmapEncoder { QualityLevel = 90 };
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(noise));
            using var jpeg = new MemoryStream();
            encoder.Save(jpeg);
            var cover = jpeg.ToArray();
            Console.WriteLine($"{copies.Count} copies in {dir}, album '{album.Title}', cover {cover.Length:N0} B");

            var closedAt = (long?)null;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            main.Closed += (_, _) => closedAt = clock.ElapsedMilliseconds;

            var edit = new AudioFool.Core.Library.AlbumTagEdit();
            var save = (Task)typeof(MainViewModel).GetMethod("ApplyAlbumEditAsync", Private)!
                .Invoke(vm, [album, edit, new AudioFool.Core.Library.ArtPayload(cover, "image/jpeg"), false])!;
            Settle(30);

            main.Close();
            Settle(30);
            var openAfterClose = closedAt is null;
            var statusWhileWaiting = vm.StatusText;

            while (!save.IsCompleted && clock.ElapsedMilliseconds < 120_000) Settle(50);
            var savedAt = clock.ElapsedMilliseconds;
            while (closedAt is null && clock.ElapsedMilliseconds < savedAt + 5000) Settle(50);

            Console.WriteLine($"close asked during the save: window still open {openAfterClose} ({(openAfterClose ? "OK" : "WRONG")}), status '{statusWhileWaiting}'");
            Console.WriteLine($"save done at {savedAt} ms, window closed at {closedAt?.ToString() ?? "never"} ms ({(closedAt >= savedAt - 50 ? "OK" : "WRONG")}), save {(save.IsFaulted ? "FAULTED " + save.Exception?.GetBaseException().Message : "completed")}");

            var allOk = true;
            foreach (var original in originals)
            {
                var copy = Path.Combine(dir, Path.GetFileName(original));
                var sameAudio = AudioBytesHash(original) is { } a && AudioBytesHash(copy) is { } b && a == b;
                using var tagged = TagLib.File.Create(copy);
                using var before = TagLib.File.Create(original);
                var oneCover = tagged.Tag.Pictures is [var only] && only.Data.Data.AsSpan().SequenceEqual(cover);
                var sameLength = tagged.Properties.Duration == before.Properties.Duration;
                var sameCreated = File.GetCreationTimeUtc(copy) == created[copy];
                var ok = sameAudio && oneCover && sameLength && sameCreated;
                allOk &= ok;
                Console.WriteLine($"  {Path.GetFileName(copy)}: {new FileInfo(original).Length:N0} -> {new FileInfo(copy).Length:N0} B, "
                    + $"audio identical {sameAudio}, one cover {oneCover}, length {sameLength}, created kept {sameCreated} {(ok ? "OK" : "WRONG")}");
            }

            var leftovers = Directory.GetFiles(dir, AudioFool.Core.Library.FileSwap.Prefix + "*");
            Console.WriteLine($"leftover working files: {leftovers.Length}; status '{vm.StatusText}'");
            Console.WriteLine(allOk && leftovers.Length == 0 && openAfterClose && closedAt >= savedAt - 50 ? "ALL OK" : "SOMETHING WRONG");
        }
        finally
        {
            if (settingsKept is not null)
                File.WriteAllBytes(AppSettings.SettingsPath, settingsKept);
            else if (File.Exists(AppSettings.SettingsPath))
                File.Delete(AppSettings.SettingsPath);
        }
    }

    /// <summary>
    /// --window albumkeep (session 60): the album tag window on two scratch albums
    /// made from the test fixtures, through the view model's own album save. A
    /// compilation (album artist "Various Artists", three artists, two years):
    /// the boxes it opens with, then only the genre changed, and every track must
    /// keep its own artist and year. Then an album with no album tag: it opens with
    /// an empty Album box that can be saved, and no "Unknown Album" is written.
    /// </summary>
    private static void RunAlbumKeep(MainViewModel vm)
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        const System.Reflection.BindingFlags Private = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var fixtures = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            @"..\..\..\..\..\..\tests\AudioFool.Core.Tests\TestData"));
        var dir = Path.Combine(Path.GetTempPath(), "themelab-albumkeep-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        _scratchDir = dir;

        Track Make(string name, string ext, string artist, string albumArtist, string album, int year, int number)
        {
            var path = Path.Combine(dir, name + ext);
            File.Copy(Path.Combine(fixtures, "sample" + ext), path);
            return AudioFool.Core.Library.TagWriter.WriteTrackTags(AudioFool.Core.Library.TagReader.Read(path),
                new AudioFool.Core.Library.TrackTagEdit(name, artist, albumArtist, album, year, number, 3, 1, 1),
                art: null, folderArtPath: null).UpdatedTrack!;
        }

        var compilation = new List<Track>
        {
            Make("01 First", ".flac", "Artist A", "Various Artists", "Hits", 1994, 1),
            Make("02 Second", ".mp3", "Artist B", "Various Artists", "Hits", 1994, 2),
            Make("03 Third", ".flac", "Artist C", "Various Artists", "Hits", 2001, 3),
        };
        var untagged = new List<Track>
        {
            Make("04 Loose", ".flac", "Solo", "Solo", "", 2005, 1),
            Make("05 Loose", ".flac", "Solo", "Solo", "", 2005, 2),
        };

        typeof(MainViewModel).GetMethod("ApplyLibrary", Private)!
            .Invoke(vm, [AudioFool.Core.Library.LibraryScanner.Build([.. compilation, .. untagged]), false]);

        var allOk = true;
        void Check(string what, bool ok)
        {
            allOk &= ok;
            Console.WriteLine($"  {what}: {(ok ? "OK" : "WRONG")}");
        }

        TagEditViewModel Open(IReadOnlyList<Track> tracks)
        {
            var album = AudioFool.Core.Library.LibraryScanner.Build(tracks).Artists[0].Albums[0];
            var edit = new TagEditViewModel(album, new AlbumArtService());
            WaitForDetails(edit);
            Console.WriteLine($"'{edit.WindowTitle}'");
            Console.WriteLine($"  artist '{edit.Artist}' [{edit.ArtistPlaceholder}]  album artist '{edit.AlbumArtist}' [{edit.AlbumArtistPlaceholder}]");
            Console.WriteLine($"  album '{edit.AlbumTitle}' [{edit.AlbumTitlePlaceholder}]  year '{edit.Year}' [{edit.YearPlaceholder}]  can save={edit.CanSave}  search for '{edit.SearchArtist}'");
            return edit;
        }

        void Save(IReadOnlyList<Track> tracks, TagEditViewModel edit)
        {
            var album = AudioFool.Core.Library.LibraryScanner.Build(tracks).Artists[0].Albums[0];
            var task = (Task)typeof(MainViewModel).GetMethod("ApplyAlbumEditAsync", Private)!
                .Invoke(vm, [album, edit.BuildAlbumEdit(), null, false])!;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (!task.IsCompleted && clock.ElapsedMilliseconds < 10000)
                Settle(50);
            Settle(200);
            Console.WriteLine($"  saved: '{vm.StatusText}'");
        }

        var edit = Open(compilation);
        Check("artist varies, so empty with the placeholder", edit.Artist == "" && edit.ArtistPlaceholder.Length > 0);
        Check("album artist and album shared, so filled", edit.AlbumArtist == "Various Artists" && edit.AlbumTitle == "Hits");
        Check("year varies", edit.Year == "" && edit.YearPlaceholder.Length > 0);
        edit.Genre = "Pop";
        var built = edit.BuildAlbumEdit();
        Check("only the genre is in the edit", built is { Artist: null, AlbumArtist: null, Album: null, Date: null } && built.Details.Genre == "Pop");
        Save(compilation, edit);
        foreach (var track in compilation)
        {
            var read = AudioFool.Core.Library.TagReader.Read(track.FilePath);
            var genre = AudioFool.Core.Library.TagReader.ReadDetails(track.FilePath)?.Genre;
            Check($"{Path.GetFileName(track.FilePath)}: {read.Artist} / {read.AlbumArtist} / {read.Album} / {read.Year} / {genre}",
                read.Artist == track.Artist && read.Year == track.Year && read.Album == "Hits" && genre == "Pop");
        }

        edit = Open(untagged);
        Check("no album tag: an empty Album box, and Save allowed", edit.AlbumTitle == "" && edit.CanSave);
        edit.Comment = "kept";
        Save(untagged, edit);
        foreach (var track in untagged)
        {
            var read = AudioFool.Core.Library.TagReader.Read(track.FilePath);
            Check($"{Path.GetFileName(track.FilePath)}: album '{read.Album}'", read.Album == "");
        }

        edit = Open(compilation);
        edit.AlbumTitle = "Greatest Hits";
        Save(compilation, edit);
        Check("a typed album reaches every track, artists kept",
            compilation.All(t => AudioFool.Core.Library.TagReader.Read(t.FilePath) is { Album: "Greatest Hits" } read && read.Artist == t.Artist));

        Console.WriteLine(allOk ? "ALL OK" : "SOMETHING WRONG");
    }

    /// <summary>
    /// --window findreplace (session 63): Find and Replace through its own window
    /// and the main view model's saves, on scratch songs made from the test
    /// fixtures in a scratch "Music" folder, the window shown off-screen and never
    /// activated. Find "(Remastered)"; the options; the header tick box; a row
    /// unticked; a replacement that changes nothing (its rows leave the view) and
    /// one that does (AFTER follows); a song retagged elsewhere after Find; Replace,
    /// with the prompt answered from here and rendered on its own; Undo. Reads every
    /// file back at each step: the unticked tag and the song retagged elsewhere are
    /// never touched, the song with two artists keeps both. Then Ctrl+H. Then
    /// (session 67) two songs selected in the Songs table: only theirs found, the
    /// title says so, Replace, Find again and Undo keep to them; one selected is the
    /// library. Renders -empty, -found, -prompt, -done, -undone and -selection PNGs
    /// beside --out.
    /// </summary>
    private static void RunFindReplace(MainWindow main, MainViewModel vm, string outPath, double scale)
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        const System.Reflection.BindingFlags Private = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var fixtures = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            @"..\..\..\..\..\..\tests\AudioFool.Core.Tests\TestData"));
        var dir = Path.Combine(Path.GetTempPath(), "themelab-findreplace-" + Guid.NewGuid().ToString("N")[..8]);
        var music = Path.Combine(dir, "Music");
        _scratchDir = dir;

        var paths = new Dictionary<string, string>();
        void Make(string folder, string name, string ext, string title, string artist, string album, int number)
        {
            var path = Path.Combine(music, folder, name + ext);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.Copy(Path.Combine(fixtures, "sample" + ext), path);
            var made = AudioFool.Core.Library.TagWriter.WriteTrackTags(AudioFool.Core.Library.TagReader.Read(path),
                new AudioFool.Core.Library.TrackTagEdit(title, artist, artist, album, 1981, number, 5, 1, 1),
                art: null, folderArtPath: null);
            if (!made.Success)
                throw new InvalidOperationException(made.ErrorMessage);
            paths[name] = path;
        }

        Make(@"Rush\Moving Pictures", "01 Tom Sawyer", ".flac", "Tom Sawyer (Remastered)", "Rush", "Moving Pictures (Remastered)", 1);
        Make(@"Rush\Moving Pictures", "02 Red Barchetta", ".mp3", "Red Barchetta", "Rush", "Moving Pictures (Remastered)", 2);
        Make(@"Rush\Moving Pictures", "03 YYZ", ".flac", "YYZ (Remastered)", "Rush", "Moving Pictures (Remastered)", 3);
        Make(@"Duo\Pairs", "04 Duet", ".flac", "Duet", "Duo (Remastered)", "Pairs", 4);
        Make(@"Solo\Singles", "05 Elsewhere", ".mp3", "Elsewhere (Remastered)", "Solo", "Singles", 5);

        // 04 has two artists; the library keeps only the first.
        using (var duet = TagLib.File.Create(paths["04 Duet"]))
        {
            duet.Tag.Performers = ["Duo (Remastered)", "Guest (Remastered)"];
            duet.Save();
        }

        var songs = paths.Values.Select(p => AudioFool.Core.Library.TagReader.Read(p)).ToList();
        typeof(MainViewModel).GetMethod("ApplyLibrary", Private)!
            .Invoke(vm, [AudioFool.Core.Library.LibraryScanner.Build(songs), false]);
        Settle(200);

        var allOk = true;
        void Check(string what, bool ok)
        {
            allOk &= ok;
            Console.WriteLine($"  {what}: {(ok ? "OK" : "WRONG")}");
        }

        string Tags(string name)
        {
            using var file = TagLib.File.Create(paths[name]);
            return $"'{file.Tag.Title}' / {string.Join(" + ", file.Tag.Performers)} / {string.Join(" + ", file.Tag.AlbumArtists)} / '{file.Tag.Album}'";
        }

        var before = paths.Keys.ToDictionary(name => name, Tags);
        void PrintFiles(string when)
        {
            Console.WriteLine($"  files {when}:");
            foreach (var name in paths.Keys)
                Console.WriteLine($"    {name}: {Tags(name)}");
        }

        // The window's own wiring, pointed at the scratch Music folder for its FILE column.
        var fr = (FindReplaceViewModel)typeof(MainViewModel).GetMethod("NewFindReplace", Private)!.Invoke(vm, null)!;
        typeof(FindReplaceViewModel).GetField("_musicFolders", Private)!.SetValue(fr, new List<string> { music });

        var window = new FindReplaceWindow(fr, main) { ShowActivated = false, ShowInTaskbar = false };
        // After the window's own Loaded handler, which centres it over its owner and
        // clamps that onto a monitor, and before anything is drawn.
        window.Loaded += (_, _) => { window.Left = -20000; window.Top = -20000; };
        window.Show();
        Settle(300);

        var grid = (System.Windows.Controls.DataGrid)window.FindName("PreviewGrid");
        var shot = Path.ChangeExtension(outPath, null);
        void Render(string name)
        {
            Settle(300);
            Save(window, $"{shot}-{name}.png", window.ActualWidth, window.ActualHeight, scale);
            Console.WriteLine($"  rendered {Path.GetFileName(shot)}-{name}.png ({window.ActualWidth:0}x{window.ActualHeight:0})");
        }

        void WaitWhileBusy()
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            Settle(100);
            while ((fr.IsBusy || fr.ReplaceCommand.IsRunning || fr.UndoCommand.IsRunning) && clock.ElapsedMilliseconds < 20000)
                Settle(50);
            Settle(200);
        }

        void Find(string text)
        {
            fr.FindText = text;
            fr.FindCommand.Execute(null);
            Settle(100);
        }

        ReplaceRow Row(string file, AudioFool.Core.Library.ReplaceField field) =>
            fr.Rows.Single(r => r.File.EndsWith(file, StringComparison.Ordinal) && r.Match.Field == field);

        Console.WriteLine("1. opened");
        Check("no rows, no footer, Find and Replace off, no Undo, the header box clear",
            grid.Items.Count == 0 && fr.FooterText == "" && !fr.FindCommand.CanExecute(null)
            && !fr.ReplaceCommand.CanExecute(null) && !fr.CanUndo && !fr.AllTicked);
        // No wider than the screen: under --dpi the main window can be wider than the
        // real monitor is at that scale.
        var screenWidth = AudioFool.Services.WindowPlacement.WorkAreaOf(main) is { } area
            ? (area.Right - area.Left) / VisualTreeHelper.GetDpi(main).DpiScaleX
            : SystemParameters.WorkArea.Width;
        Check($"as wide as the main window less 48 px either side: {window.ActualWidth:0} in {main.ActualWidth:0} (screen {screenWidth:0})",
            Math.Abs(window.ActualWidth - Math.Min(main.ActualWidth - 96, screenWidth)) < 0.5);

        // The placement itself, on made-up screens: (left, width) of the window.
        var widthOf = typeof(FindReplaceWindow).GetMethod("OpeningWidth", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        var cornerOf = typeof(FindReplaceWindow).GetMethod("CentreOver", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        (double Left, double Width) Place(Rect frame, Rect work)
        {
            var width = (double)widthOf.Invoke(null, [frame, work, window.MinWidth, 48.0])!;
            return (((Point)cornerOf.Invoke(null, [frame, work, width, 620.0])!).X, width);
        }

        var screen = new Rect(0, 0, 1920, 1032);
        Check($"maximised: {Place(screen, screen)}", Place(screen, screen) == (48, 1824));
        Check($"a 1560-wide main window at 180: {Place(new Rect(180, 60, 1560, 900), screen)}",
            Place(new Rect(180, 60, 1560, 900), screen) == (228, 1464));
        Check($"a narrow one keeps the minimum, centred: {Place(new Rect(100, 60, 700, 600), screen)}",
            Place(new Rect(100, 60, 700, 600), screen) == (130, window.MinWidth));
        Check($"one hanging off the right edge stays on screen: {Place(new Rect(1000, 0, 1560, 900), screen)}",
            Place(new Rect(1000, 0, 1560, 900), screen) == (456, 1464));
        Check($"a screen narrower than the minimum: {Place(new Rect(0, 0, 600, 500), new Rect(0, 0, 600, 500))}",
            Place(new Rect(0, 0, 600, 500), new Rect(0, 0, 600, 500)) == (0, 600));
        Render("empty");

        Console.WriteLine("2. the options");
        Find("(remastered)");
        Check($"case ignored: {fr.Rows.Count} rows", fr.Rows.Count == 8);
        fr.MatchCase = true;
        Check($"Match case finds again: '{fr.FooterText}'", fr.Rows.Count == 0 && fr.FooterText == "No matches.");
        fr.MatchCase = false;
        Find("Remaster");
        Check($"inside a word: {fr.Rows.Count} rows", fr.Rows.Count == 8);
        fr.WholeWords = true;
        Check($"Whole words: {fr.Rows.Count} rows", fr.Rows.Count == 0);
        fr.WholeWords = false;

        Console.WriteLine("3. Find \"(Remastered)\"");
        Find("(Remastered)");
        foreach (var row in fr.Rows)
            Console.WriteLine($"    [{(row.IsTicked ? "x" : " ")}] {row.Field,-12} '{row.Before}' -> '{row.After}'  {row.File}");
        Check($"8 rows over 5 songs, all ticked: '{fr.FooterText}'",
            grid.Items.Count == 8 && fr.Counts == (5, 8) && fr.AllTicked && fr.FooterText == "5 songs · 8 tags");
        Check("sorted by file, then tag",
            fr.Rows.Zip(fr.Rows.Skip(1)).All(p =>
                StringComparer.CurrentCultureIgnoreCase.Compare(p.First.File, p.Second.File) < 0
                || (p.First.File == p.Second.File && p.First.FieldOrder < p.Second.FieldOrder)));
        Check("AFTER is the deletion, tidied", Row("01 Tom Sawyer.flac", AudioFool.Core.Library.ReplaceField.Title).After == "Tom Sawyer");
        Check("FILE is inside the music folder", Row("01 Tom Sawyer.flac", AudioFool.Core.Library.ReplaceField.Title).File == @"Rush\Moving Pictures\01 Tom Sawyer.flac");
        Check("Replace on", fr.ReplaceCommand.CanExecute(null));

        Console.WriteLine("4. the header tick box, and a row unticked");
        fr.ToggleAllCommand.Execute(null);
        Check($"all unticked: {fr.Counts}, Replace off", fr.Counts == (0, 0) && !fr.AllTicked && !fr.ReplaceCommand.CanExecute(null));
        fr.ToggleAllCommand.Execute(null);
        Check($"all ticked again: {fr.Counts}", fr.Counts == (5, 8) && fr.AllTicked);
        Row("03 YYZ.flac", AudioFool.Core.Library.ReplaceField.Title).IsTicked = false;
        Check($"one unticked: '{fr.FooterText}'", fr.Counts == (5, 7) && !fr.AllTicked && fr.FooterText == "5 songs · 7 tags");

        Console.WriteLine("5. the replacement");
        fr.ReplaceText = "(Remastered)";
        Check($"one that changes nothing: rows out of view, '{fr.FooterText}'", grid.Items.Count == 0 && fr.Counts == (0, 0));
        fr.ReplaceText = "[2011]";
        Check($"AFTER follows: '{Row("01 Tom Sawyer.flac", AudioFool.Core.Library.ReplaceField.Title).After}', {grid.Items.Count} rows",
            Row("01 Tom Sawyer.flac", AudioFool.Core.Library.ReplaceField.Title).After == "Tom Sawyer [2011]" && grid.Items.Count == 8);
        Check("the unticked row stays unticked", !Row("03 YYZ.flac", AudioFool.Core.Library.ReplaceField.Title).IsTicked);
        Check("asks with both texts", fr.ConfirmationText == "Update 5 songs? \"(Remastered)\" will be replaced with \"[2011]\" in 7 tags.");
        fr.ReplaceText = "";
        Check("back to deleting", Row("01 Tom Sawyer.flac", AudioFool.Core.Library.ReplaceField.Title).After == "Tom Sawyer" && fr.Counts == (5, 7));
        Render("found");

        Console.WriteLine("6. 05 retagged elsewhere after Find");
        var elsewhere = AudioFool.Core.Library.TagWriter.WriteSelectedTrackTags(
            AudioFool.Core.Library.TagReader.Read(paths["05 Elsewhere"]),
            new AudioFool.Core.Library.TracksTagEdit { Title = "Elsewhere (Remastered) [Live]" });
        Check("retagged", elsewhere.Success);
        var elsewhereBytes = File.ReadAllBytes(paths["05 Elsewhere"]);

        Console.WriteLine("7. Replace");
        var prompt = (PromptWindow)Activator.CreateInstance(typeof(PromptWindow), Private, null,
            [window, "Find and Replace", fr.ConfirmationText, "Replace", null, null], null)!;
        prompt.ShowActivated = false;
        prompt.ShowInTaskbar = false;
        prompt.Loaded += (_, _) => { prompt.Left = -20000; prompt.Top = -20000; };
        prompt.Show();
        Settle(300);
        Save(prompt, $"{shot}-prompt.png", prompt.ActualWidth, prompt.ActualHeight, scale);
        Console.WriteLine($"  rendered {Path.GetFileName(shot)}-prompt.png ({prompt.ActualWidth:0}x{prompt.ActualHeight:0})");
        prompt.Close();

        string? asked = null;
        FindReplaceViewModel.ConfirmReplace = message => { asked = message; return true; };
        fr.ReplaceCommand.Execute(null);
        WaitWhileBusy();
        Console.WriteLine($"  asked:      '{asked}'");
        Console.WriteLine($"  footer:     '{fr.FooterText}' (error={fr.FooterIsError})");
        Console.WriteLine($"  status bar: '{vm.StatusText}'");
        PrintFiles("after Replace");
        Check("asked with the counts", asked == "Update 5 songs? \"(Remastered)\" will be deleted from 7 tags.");
        Check("01: title and album", Tags("01 Tom Sawyer") == "'Tom Sawyer' / Rush / Rush / 'Moving Pictures'");
        Check("02: its album", Tags("02 Red Barchetta") == "'Red Barchetta' / Rush / Rush / 'Moving Pictures'");
        Check("03: the album, and the unticked title kept", Tags("03 YYZ") == "'YYZ (Remastered)' / Rush / Rush / 'Moving Pictures'");
        Check("04: both artists", Tags("04 Duet") == "'Duet' / Duo + Guest / Duo / 'Pairs'");
        Check("05: retagged elsewhere, so not a byte changed", File.ReadAllBytes(paths["05 Elsewhere"]).AsSpan().SequenceEqual(elsewhereBytes));
        Check("the footer names it",
            fr.FooterText == "Updated 4 of 5 songs. 1 had changed since Find and was left alone: 05 Elsewhere.mp3." && !fr.FooterIsError);
        Check("rows cleared, Undo shown", grid.Items.Count == 0 && fr.CanUndo && fr.UndoCommand.CanExecute(null));
        var library = ((AudioFool.Core.Library.MusicLibrary)typeof(MainViewModel).GetField("_library", Private)!.GetValue(vm)!).AllTracks;
        var tom = library.Single(t => t.FilePath == paths["01 Tom Sawyer"]);
        var duo = library.Single(t => t.FilePath == paths["04 Duet"]);
        Check($"the library's copies: '{tom.Title}' / '{tom.Album}', '{duo.Artist}'",
            tom is { Title: "Tom Sawyer", Album: "Moving Pictures" } && duo.Artist == "Duo"
            && tom.MatchesFile(new FileInfo(tom.FilePath).Length, File.GetLastWriteTimeUtc(tom.FilePath)));
        Render("done");

        Console.WriteLine("8. Undo");
        fr.UndoCommand.Execute(null);
        WaitWhileBusy();
        Console.WriteLine($"  footer:     '{fr.FooterText}'");
        PrintFiles("after Undo");
        Check("01 to 04 as they were", paths.Keys.Take(4).All(name => Tags(name) == before[name]));
        Check("05 still not a byte changed", File.ReadAllBytes(paths["05 Elsewhere"]).AsSpan().SequenceEqual(elsewhereBytes));
        Check("footer, and Undo gone", fr.FooterText == "Put back 4 songs." && !fr.CanUndo);
        Render("undone");

        Console.WriteLine("9. Ctrl+H");
        var shortcut = typeof(MainWindow).GetMethod("ResolveShortcut", Private)!
            .Invoke(main, [System.Windows.Input.Key.H, System.Windows.Input.ModifierKeys.Control, null])!.ToString();
        Check($"Ctrl+H is {shortcut}", shortcut == "FindReplace");
        Check($"the library's title: '{window.Title}'", window.Title == "Find and Replace");
        window.Close();

        // Session 67: two songs selected in the Songs table limit it to those.
        Console.WriteLine("10. two songs selected");
        AudioFool.Core.Models.Track Song(string name) =>
            ((AudioFool.Core.Library.MusicLibrary)typeof(MainViewModel).GetField("_library", Private)!.GetValue(vm)!)
                .AllTracks.Single(t => t.FilePath == paths[name]);
        vm.SelectedTracks = [Song("01 Tom Sawyer"), Song("03 YYZ")];
        fr = (FindReplaceViewModel)typeof(MainViewModel).GetMethod("NewFindReplace", Private)!.Invoke(vm, null)!;
        typeof(FindReplaceViewModel).GetField("_musicFolders", Private)!.SetValue(fr, new List<string> { music });
        window = new FindReplaceWindow(fr, main) { ShowActivated = false, ShowInTaskbar = false };
        window.Loaded += (_, _) => { window.Left = -20000; window.Top = -20000; };
        window.Show();
        Settle(300);
        grid = (System.Windows.Controls.DataGrid)window.FindName("PreviewGrid");
        Check($"the title: '{window.Title}'", window.Title == "Find and Replace · 2 tracks");
        Find("(Remastered)");
        foreach (var row in fr.Rows)
            Console.WriteLine($"    [{(row.IsTicked ? "x" : " ")}] {row.Field,-12} '{row.Before}' -> '{row.After}'  {row.File}");
        Check($"only 01 and 03: '{fr.FooterText}'",
            fr.Counts == (2, 4) && fr.Rows.All(r => r.File.EndsWith("01 Tom Sawyer.flac") || r.File.EndsWith("03 YYZ.flac")));
        Render("selection");
        fr.ReplaceCommand.Execute(null);
        WaitWhileBusy();
        Console.WriteLine($"  footer:     '{fr.FooterText}'");
        PrintFiles("after Replace on the selection");
        Check("01 and 03 changed", Tags("01 Tom Sawyer") == "'Tom Sawyer' / Rush / Rush / 'Moving Pictures'"
                                    && Tags("03 YYZ") == "'YYZ' / Rush / Rush / 'Moving Pictures'");
        Check("02 and 04 not", Tags("02 Red Barchetta") == before["02 Red Barchetta"] && Tags("04 Duet") == before["04 Duet"]);
        Find("(Remastered)");
        Check($"Find again reads the new copies: '{fr.FooterText}'", fr.Rows.Count == 0 && fr.FooterText == "No matches.");
        fr.UndoCommand.Execute(null);
        WaitWhileBusy();
        Check("Undo puts them back", Tags("01 Tom Sawyer") == before["01 Tom Sawyer"] && Tags("03 YYZ") == before["03 YYZ"]);
        Find("(Remastered)");
        Check($"and Find sees them again, still only those two: {fr.Counts}", fr.Counts == (2, 4));
        window.Close();

        Console.WriteLine("11. one song selected: the library");
        vm.SelectedTracks = [Song("01 Tom Sawyer")];
        fr = (FindReplaceViewModel)typeof(MainViewModel).GetMethod("NewFindReplace", Private)!.Invoke(vm, null)!;
        Check($"the title: '{fr.WindowTitle}'", fr.WindowTitle == "Find and Replace");
        fr.FindText = "(Remastered)";
        fr.FindCommand.Execute(null);
        Check($"every song: {fr.Counts}", fr.Counts.Songs == 5);
        vm.SelectedTracks = [];

        Console.WriteLine(allOk ? "ALL OK" : "SOMETHING WRONG");
    }

    /// <summary>
    /// --window findreplaceperf (session 63): the Find and Replace window over the
    /// real library.json, read-only: Replace is never pressed (its prompt throws if
    /// asked). Times Find for a very common text, a common one and a rare one, the
    /// replacement typed a letter at a time over the biggest result, the header box,
    /// and a sort, each with the table laid out again; renders a page of real rows
    /// (<c>--find</c>, else "Remaster") as <c>-real.png</c>.
    /// </summary>
    private static void RunFindReplacePerf(MainWindow main, MainViewModel vm, string? find, string outPath, double scale)
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        const System.Reflection.BindingFlags Private = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        FindReplaceViewModel.ConfirmReplace = _ => throw new InvalidOperationException("findreplaceperf never replaces");

        var fr = (FindReplaceViewModel)typeof(MainViewModel).GetMethod("NewFindReplace", Private)!.Invoke(vm, null)!;
        var window = new FindReplaceWindow(fr, main) { ShowActivated = false, ShowInTaskbar = false };
        window.Loaded += (_, _) => { window.Left = -20000; window.Top = -20000; };
        window.Show();
        Settle(300);
        var grid = (System.Windows.Controls.DataGrid)window.FindName("PreviewGrid");

        double Time(Action action)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            action();
            window.UpdateLayout();
            return clock.Elapsed.TotalMilliseconds;
        }

        void Find(string text)
        {
            var ms = Time(() =>
            {
                fr.FindText = text;
                fr.FindCommand.Execute(null);
            });
            Console.WriteLine($"Find '{text}': {fr.Rows.Count:N0} rows, '{fr.FooterText}', {ms:0} ms");
        }

        foreach (var text in new[] { "e", "the", "(Remastered)" })
            Find(text);

        Find("e");
        var typed = "";
        foreach (var letter in "xyz")
        {
            typed += letter;
            Console.WriteLine($"  replacement '{typed}': {Time(() => fr.ReplaceText = typed):0} ms");
        }

        Console.WriteLine($"  replacement cleared: {Time(() => fr.ReplaceText = ""):0} ms");
        Console.WriteLine($"  header box off: {Time(() => fr.ToggleAllCommand.Execute(null)):0} ms, '{fr.FooterText}'");
        Console.WriteLine($"  header box on: {Time(() => fr.ToggleAllCommand.Execute(null)):0} ms, '{fr.FooterText}'");
        Console.WriteLine($"  sorted by BEFORE: {Time(() => grid.Items.SortDescriptions.Add(new System.ComponentModel.SortDescription("Before", System.ComponentModel.ListSortDirection.Ascending))):0} ms");

        Find(find ?? "Remaster");
        Settle(300);
        var shot = Path.ChangeExtension(outPath, null) + "-real.png";
        Save(window, shot, window.ActualWidth, window.ActualHeight, scale);
        Console.WriteLine($"rendered {Path.GetFileName(shot)}");
        window.Close();
    }

    /// <summary>
    /// --window folderart (session 62): an album's new cover through the view
    /// model's own album save, on scratch songs made from the test fixtures. The
    /// album "Blue" has two songs alone in "Blue CD1" (with an old cover.png) and
    /// one in "Singles", beside "Red"'s song and Red's cover.jpg. The PNG must go
    /// to the real Recycle Bin once cover.jpg is in; Singles' cover.jpg must stay
    /// Red's, with the status bar saying why, while Blue's song there still gets
    /// the cover inside it. Then a second JPEG over the first; then Save Embedded
    /// Art from the album window, and Keep Best Cover over all four songs, with
    /// Red's cover made smaller than Blue's, so only the shared-folder rule keeps
    /// it. No working files may be left anywhere.
    /// </summary>
    private static void RunFolderArt(MainViewModel vm)
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        const System.Reflection.BindingFlags Private = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var fixtures = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            @"..\..\..\..\..\..\tests\AudioFool.Core.Tests\TestData"));
        var dir = Path.Combine(Path.GetTempPath(), "themelab-folderart-" + Guid.NewGuid().ToString("N")[..8]);
        var cd1 = Path.Combine(dir, "Blue CD1");
        var singles = Path.Combine(dir, "Singles");
        Directory.CreateDirectory(cd1);
        Directory.CreateDirectory(singles);
        _scratchDir = dir;

        byte[] Jpeg(byte shade, int size = 300)
        {
            var pixels = Enumerable.Repeat(shade, size * size * 4).ToArray();
            var image = System.Windows.Media.Imaging.BitmapSource.Create(size, size, 96, 96,
                System.Windows.Media.PixelFormats.Bgr32, null, pixels, size * 4);
            var encoder = new System.Windows.Media.Imaging.JpegBitmapEncoder { QualityLevel = 90 };
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(image));
            using var stream = new MemoryStream();
            encoder.Save(stream);
            return stream.ToArray();
        }

        string Make(string folder, string name, string ext, string album, int number)
        {
            var path = Path.Combine(folder, name + ext);
            File.Copy(Path.Combine(fixtures, "sample" + ext), path);
            AudioFool.Core.Library.TagWriter.WriteTrackTags(AudioFool.Core.Library.TagReader.Read(path),
                new AudioFool.Core.Library.TrackTagEdit(name, "Band", "Band", album, 2001, number, 3, 1, 1),
                art: null, folderArtPath: null);
            return path;
        }

        var blue = new[] { Make(cd1, "01 One", ".flac", "Blue", 1), Make(cd1, "02 Two", ".mp3", "Blue", 2), Make(singles, "03 Three", ".flac", "Blue", 3) };
        var red = Make(singles, "01 Red", ".flac", "Red", 1);
        var oldPng = Path.Combine(cd1, "cover.png");
        File.WriteAllBytes(oldPng, File.ReadAllBytes(Path.Combine(fixtures, "sample.flac"))[..64]);
        var redCover = Jpeg(200);
        File.WriteAllBytes(Path.Combine(singles, "cover.jpg"), redCover);

        // Read after the covers exist, so each song knows its folder's file.
        var library = AudioFool.Core.Library.LibraryScanner.Build([.. blue.Append(red).Select(p => AudioFool.Core.Library.TagReader.Read(p))]);
        typeof(MainViewModel).GetMethod("ApplyLibrary", Private)!.Invoke(vm, [library, false]);

        var allOk = true;
        void Check(string what, bool ok)
        {
            allOk &= ok;
            Console.WriteLine($"  {what}: {(ok ? "OK" : "WRONG")}");
        }

        void Save(byte[] cover)
        {
            var album = vm.GetType().GetField("_library", Private)!.GetValue(vm) is AudioFool.Core.Library.MusicLibrary current
                ? current.Artists.SelectMany(a => a.Albums).Single(a => a.Title == "Blue")
                : throw new InvalidOperationException("no library");
            var task = (Task)typeof(MainViewModel).GetMethod("ApplyAlbumEditAsync", Private)!
                .Invoke(vm, [album, new AudioFool.Core.Library.AlbumTagEdit(), new AudioFool.Core.Library.ArtPayload(cover, "image/jpeg"), false])!;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (!task.IsCompleted && clock.ElapsedMilliseconds < 20000)
                Settle(50);
            Settle(200);
            Console.WriteLine($"  saved{(task.IsFaulted ? " FAULTED " + task.Exception?.GetBaseException().Message : "")}: '{vm.StatusText}'");
        }

        bool EmbeddedIs(string path, byte[] cover)
        {
            using var file = TagLib.File.Create(path);
            return file.Tag.Pictures is [var only] && only.Data.Data.AsSpan().SequenceEqual(cover);
        }

        bool HasNoPicture(string path)
        {
            using var file = TagLib.File.Create(path);
            return file.Tag.Pictures.Length == 0;
        }

        Console.WriteLine($"scratch: {dir}");
        Console.WriteLine("1. a JPEG cover over Blue CD1's cover.png");
        var first = Jpeg(40);
        Save(first);
        var cd1Jpg = Path.Combine(cd1, "cover.jpg");
        Check("Blue CD1/cover.jpg is the new cover", File.Exists(cd1Jpg) && File.ReadAllBytes(cd1Jpg).AsSpan().SequenceEqual(first));
        Check("Blue CD1/cover.png gone (to the Recycle Bin)", !File.Exists(oldPng));
        Check("Singles/cover.jpg still Red's", File.ReadAllBytes(Path.Combine(singles, "cover.jpg")).AsSpan().SequenceEqual(redCover));
        Check("status names Singles", vm.StatusText.Contains("No folder cover file in Singles"));
        foreach (var path in blue)
            Check($"{Path.GetFileName(path)} holds the new cover", EmbeddedIs(path, first));
        Check("Red's song untouched", HasNoPicture(red));

        Console.WriteLine("2. a second JPEG over the first");
        var second = Jpeg(90);
        Save(second);
        Check("Blue CD1/cover.jpg replaced", File.ReadAllBytes(cd1Jpg).AsSpan().SequenceEqual(second));
        Check("Singles/cover.jpg still Red's", File.ReadAllBytes(Path.Combine(singles, "cover.jpg")).AsSpan().SequenceEqual(redCover));
        Check("Blue CD1 has one cover file", Directory.GetFiles(cd1, "cover.*").Length == 1);

        // Red's cover made smaller than Blue's embedded one, so only the shared-folder
        // rule keeps Save Embedded Art and Keep Best Cover from replacing it.
        var smallRed = Jpeg(200, 100);
        File.WriteAllBytes(Path.Combine(singles, "cover.jpg"), smallRed);

        Console.WriteLine("3. Save Embedded Art from the album window (Blue CD1's cover.jpg deleted first)");
        File.Delete(cd1Jpg);
        var blueAlbum = ((AudioFool.Core.Library.MusicLibrary)vm.GetType().GetField("_library", Private)!.GetValue(vm)!)
            .Artists.SelectMany(a => a.Albums).Single(a => a.Title == "Blue");
        var window = (TagEditViewModel)typeof(MainViewModel).GetMethod("NewAlbumEditorViewModel", Private)!.Invoke(vm, [blueAlbum])!;
        var extraction = window.SaveEmbeddedArtCommand.ExecuteAsync(null);
        var waited = System.Diagnostics.Stopwatch.StartNew();
        while (!extraction.IsCompleted && waited.ElapsedMilliseconds < 20000)
            Settle(50);
        Console.WriteLine($"  message: '{window.ArtMessage}'");
        Check("Blue CD1/cover.jpg is the embedded cover", File.Exists(cd1Jpg) && File.ReadAllBytes(cd1Jpg).AsSpan().SequenceEqual(second));
        Check("Singles/cover.jpg still Red's", File.ReadAllBytes(Path.Combine(singles, "cover.jpg")).AsSpan().SequenceEqual(smallRed));
        Check("message names Singles", window.ArtMessage.Contains("No cover.jpg in Singles"));

        Console.WriteLine("4. Keep Best Cover over all four songs, each given a bigger front cover and a back cover");
        var bigFront = Jpeg(60, 400);
        var back = Jpeg(120, 200);
        foreach (var path in blue.Append(red))
        {
            using var file = TagLib.File.Create(path);
            file.Tag.Pictures =
            [
                new TagLib.Picture(new TagLib.ByteVector(bigFront)) { Type = TagLib.PictureType.FrontCover, MimeType = "image/jpeg" },
                new TagLib.Picture(new TagLib.ByteVector(back)) { Type = TagLib.PictureType.BackCover, MimeType = "image/jpeg" },
            ];
            file.Save();
        }

        var keep = (Task)typeof(MainViewModel).GetMethod("KeepBestCoversAsync", Private)!
            .Invoke(vm, [blue.Append(red).Select(p => AudioFool.Core.Library.TagReader.Read(p)).ToList()])!;
        waited.Restart();
        while (!keep.IsCompleted && waited.ElapsedMilliseconds < 60000)
            Settle(50);
        Settle(200);
        Console.WriteLine($"  status{(keep.IsFaulted ? " FAULTED " + keep.Exception?.GetBaseException().Message : "")}: '{vm.StatusText}'");
        Check("Blue CD1/cover.jpg is the bigger front cover", File.ReadAllBytes(cd1Jpg).AsSpan().SequenceEqual(bigFront));
        Check("Singles/cover.jpg still Red's", File.ReadAllBytes(Path.Combine(singles, "cover.jpg")).AsSpan().SequenceEqual(smallRed));
        Check("status counts the shared folder", vm.StatusText.Contains("no cover.jpg in 1 folder shared with other albums"));
        foreach (var path in blue.Append(red))
            Check($"{Path.GetFileName(path)} kept only its front cover", EmbeddedIs(path, bigFront));

        var leftovers = Directory.GetFiles(dir, AudioFool.Core.Library.FileSwap.Prefix + "*", SearchOption.AllDirectories);
        Check($"no working files left ({leftovers.Length})", leftovers.Length == 0);
        Console.WriteLine(allOk ? "ALL OK" : "SOMETHING WRONG");
    }

    /// <summary>SHA-256 of a FLAC, MP3 or DSF's audio bytes, from where TagPadding says the tags end; null for other layouts.</summary>
    private static string? AudioBytesHash(string path)
    {
        if (AudioFool.Core.Library.TagPadding.Read(path) is not { } layout)
            return null;

        using var stream = File.OpenRead(path);
        var end = layout.AudioEnd ?? stream.Length;
        stream.Position = layout.AudioStart;
        using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
        var buffer = new byte[1 << 20];
        for (var left = end - layout.AudioStart; left > 0;)
        {
            var read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, left));
            if (read == 0)
                break;
            hash.AppendData(buffer, 0, read);
            left -= read;
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

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
    /// --window themeswitch --file <audio> [--artfile <image>]: the live switch as
    /// the user makes it (PS2 Dark spec 3.2). The real library in the lists, an
    /// artist, album and song picked far enough down that each list has scrolled,
    /// the file playing on the real engine (silently: volume 0, shared mode), and
    /// two dialogs open: the art viewer, shown off-screen like the main window,
    /// and the Analyze window, laid out unshown as --window analysis does. Then a
    /// switch with the Theme button (its automation peer, as a click would) and
    /// one back with Ctrl+T (HandleShortcut), each timed and followed by checks of
    /// what must not change: the same song playing on, the selections, the scroll
    /// positions, the dialogs still open. The view model saves the theme at each
    /// switch, so settings.json is put back afterwards. Renders the main window and
    /// the viewer as they end, which is the theme they started in.
    /// </summary>
    private static int RunThemeSwitch(MainWindow main, MainViewModel vm, AudioEngine engine, string file, string? artFile,
        string outPath, double w, double h, double scale)
    {
        var settingsKept = File.Exists(AppSettings.SettingsPath) ? File.ReadAllBytes(AppSettings.SettingsPath) : null;
        var failures = 0;
        void Check(string what, bool ok, string detail = "")
        {
            if (!ok) failures++;
            Console.WriteLine($"  {(ok ? "ok   " : "WRONG")} {what}{(detail.Length > 0 ? ": " + detail : "")}");
        }

        string Hex(Brush? brush) => brush is SolidColorBrush s ? s.Color.ToString() : brush?.GetType().Name ?? "none";
        double Offset(DependencyObject list) =>
            Descendants<System.Windows.Controls.ScrollViewer>(list).First().VerticalOffset;

        AudioFool.ArtWindow? viewer = null;
        try
        {
            LoadRealLibrary(vm);
            Pump();
            var artists = (System.Windows.Controls.ListBox)main.FindName("ArtistList");
            var albums = (System.Windows.Controls.ListBox)main.FindName("AlbumList");
            var grid = (System.Windows.Controls.DataGrid)main.FindName("TrackGrid");
            var button = (System.Windows.Controls.Button)main.FindName("ThemeButton");

            // Past the middle of Artists, with enough albums that picking the last
            // one scrolls the Albums list too.
            vm.SelectedArtist = vm.Artists.Skip(vm.Artists.Count / 2).FirstOrDefault(a => a.Albums.Count >= 12) ?? vm.Artists[^1];
            Settle(300);
            if (vm.Albums.Count > 0)
                vm.SelectedAlbum = vm.Albums[^1];
            Settle(300);
            if (grid.Items.Count > 0)
            {
                grid.SelectedItem = grid.Items[^1];
                grid.ScrollIntoView(grid.SelectedItem);
            }
            Settle(300);

            // Playing, silently.
            var track = AudioFool.Core.Library.TagReader.Read(file);
            vm.Volume = 0;
            engine.Play([track], 0);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (engine.State != PlaybackState.Playing && clock.ElapsedMilliseconds < 5000)
                Settle(50);
            Settle(1000);

            // The dialogs.
            if (artFile is not null)
            {
                var album = new Album { Title = "Scratch", ArtistName = "ThemeLab", FolderArtPath = artFile };
                var art = new AlbumArtService().GetFullAlbumArtAsync(album).GetAwaiter().GetResult()
                    ?? throw new InvalidOperationException($"no art in {artFile}");
                viewer = new AudioFool.ArtWindow(art, "ThemeLab - Scratch", main)
                {
                    ShowActivated = false,
                    ShowInTaskbar = false,
                    Left = -20000,
                    Top = -20000,
                };
                viewer.Show();
            }

            var analysis = new AudioFool.AnalysisWindow(new AnalysisViewModel(track), main);
            analysis.ApplyTemplate();
            var analysisRoot = (FrameworkElement)analysis.Content;
            analysisRoot.Measure(new Size(880, 600));
            analysisRoot.Arrange(new Rect(0, 0, 880, 600));
            Settle(300);

            var viewerRoot = viewer?.Content as System.Windows.Controls.Panel;
            var picked = (vm.SelectedArtist, vm.SelectedAlbum, grid.SelectedItem);
            var scrolled = (Artists: Offset(artists), Albums: Offset(albums), Songs: Offset(grid));
            var started = ThemeService.Current;
            Console.WriteLine($"start: {started}, playing '{engine.CurrentTrack?.Title}' {engine.State} at {engine.Position.TotalSeconds:0.00} s; "
                + $"artist '{vm.SelectedArtist?.Name}', album '{vm.SelectedAlbum?.Album.Title}', song '{(grid.SelectedItem as Track)?.Title}'; "
                + $"scrolled {scrolled.Artists:0} / {scrolled.Albums:0} / {scrolled.Songs:0}; "
                + $"button '{System.Windows.Automation.AutomationProperties.GetName(button)}'; windows open {Application.Current.Windows.Count}");

            void After(string how, AudioFool.Core.Theme.AppTheme expected, TimeSpan position, double ms)
            {
                var windowBg = (Brush)main.FindResource("color.window.bg");
                Console.WriteLine($"{how}: {ThemeService.Current} in {ms:0} ms (to the next idle, layout and render included)");
                Check("theme", ThemeService.Current == expected, ThemeService.Current.ToString());
                Check("button name", System.Windows.Automation.AutomationProperties.GetName(button) == $"Theme: {expected}",
                    System.Windows.Automation.AutomationProperties.GetName(button));
                Check("still playing the same song", engine.State == PlaybackState.Playing && engine.CurrentTrack?.FilePath == track.FilePath,
                    $"{engine.State} '{engine.CurrentTrack?.Title}'");
                Check("position moved on", engine.Position > position, $"{position.TotalSeconds:0.00} -> {engine.Position.TotalSeconds:0.00} s");
                Check("same artist, album and song selected",
                    ReferenceEquals(picked.SelectedArtist, vm.SelectedArtist) && ReferenceEquals(picked.SelectedAlbum, vm.SelectedAlbum)
                    && ReferenceEquals(picked.SelectedItem, grid.SelectedItem));
                Check("lists where they were scrolled",
                    Offset(artists) == scrolled.Artists && Offset(albums) == scrolled.Albums && Offset(grid) == scrolled.Songs,
                    $"{Offset(artists):0} / {Offset(albums):0} / {Offset(grid):0}");
                Check("art viewer open, in the new colours", viewer is null || (viewer.IsVisible && Hex(viewerRoot?.Background) == Hex(windowBg)),
                    viewer is null ? "(no --artfile)" : Hex(viewerRoot?.Background));
                Check("Analyze window in the new colours", Hex(((System.Windows.Controls.Panel)analysisRoot).Background) == Hex(windowBg),
                    Hex(((System.Windows.Controls.Panel)analysisRoot).Background));
                var saved = File.Exists(AppSettings.SettingsPath) ? File.ReadAllText(AppSettings.SettingsPath) : "";
                Check("saved in settings.json", saved.Contains($"\"Theme\": \"{expected}\""),
                    System.Text.RegularExpressions.Regex.Match(saved, "\"Theme\": \"\\w+\"").Value);
            }

            var other = started == AudioFool.Core.Theme.AppTheme.Dark ? AudioFool.Core.Theme.AppTheme.Light : AudioFool.Core.Theme.AppTheme.Dark;

            // 1. The Theme button, as a click would press it.
            var before = engine.Position;
            clock.Restart();
            var peer = new System.Windows.Automation.Peers.ButtonAutomationPeer(button);
            ((System.Windows.Automation.Provider.IInvokeProvider)peer.GetPattern(System.Windows.Automation.Peers.PatternInterface.Invoke)).Invoke();
            Pump();
            After("Theme button", other, before, clock.Elapsed.TotalMilliseconds);
            Settle(500);

            // 2. Ctrl+T back.
            const System.Reflection.BindingFlags own = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            before = engine.Position;
            clock.Restart();
            var handled = (bool)typeof(MainWindow).GetMethod("HandleShortcut", own)!
                .Invoke(main, [System.Windows.Input.Key.T, System.Windows.Input.ModifierKeys.Control, false, null])!;
            Pump();
            Check("Ctrl+T handled", handled);
            After("Ctrl+T", started, before, clock.Elapsed.TotalMilliseconds);

            engine.Pause();
            Settle(300);
            Save(main, outPath, w, h, scale);
            if (viewer is not null && viewerRoot is not null)
                Save(viewerRoot, Path.ChangeExtension(outPath, null) + "-viewer.png", viewerRoot.ActualWidth, viewerRoot.ActualHeight, scale);
            Console.WriteLine("wrote " + outPath);
        }
        finally
        {
            engine.Stop();
            viewer?.Close();
            if (settingsKept is not null)
                File.WriteAllBytes(AppSettings.SettingsPath, settingsKept);
            else if (File.Exists(AppSettings.SettingsPath))
                File.Delete(AppSettings.SettingsPath);
        }

        Console.WriteLine(failures == 0 ? "RESULT: all checks passed" : $"RESULT: {failures} WRONG");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// --window seek --file <audio>: a click on the seek bar's track, with the real
    /// engine playing silently (volume 0, shared mode). The window is parked to
    /// the right of every monitor, so the slider's click-to-point maps the real
    /// pointer to 0:00 without anything appearing on the desktop. Mouse down and
    /// up are raised on the slider 400 ms apart, long enough for position ticks to
    /// land in between, and the position is sampled throughout: it should go to
    /// 0 and stay there, not bounce back to where it was.
    /// </summary>
    private static void RunWaveform(MainWindow main, MainViewModel vm, AudioEngine engine, string file, double at,
        string outPath, double w, double h, double scale)
    {
        vm.Volume = 0;
        var bar = (System.Windows.Controls.Slider)main.FindName("SeekBar");
        var volume = (System.Windows.Controls.Slider)main.FindName("VolumeSlider");
        bool Shown(System.Windows.Controls.Slider s) => AudioFool.Theming.SeekWaveform.GetIsShown(s);

        engine.Play([AudioFool.Core.Library.TagReader.Read(file)], 0);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        Settle(50);
        Console.WriteLine($"at start: waveform {(vm.NowPlayingWaveform is null ? "none" : "already there")}, groove shown {!Shown(bar)}");
        while (vm.NowPlayingWaveform is null && clock.ElapsedMilliseconds < 15000)
            Settle(20);
        Console.WriteLine(vm.NowPlayingWaveform is { } levels
            ? $"waveform after {clock.ElapsedMilliseconds} ms: {levels.Length} columns, seekbar shows it {Shown(bar)}, volume slider {Shown(volume)}"
            : "waveform: NONE after 15 s");

        engine.Seek(TimeSpan.FromSeconds(engine.Duration.TotalSeconds * at));
        Settle(700);

        // How the split moves while playing: where it was last drawn, read every
        // ~16 ms for 2 s. Stepping on the 250 ms timer gives ~8 positions with
        // big jumps; gliding gives many, each a fraction of a pixel.
        // --noglide 1: the old stepping, for comparison.
        if (Arg(Environment.GetCommandLineArgs(), "--noglide") is not null)
        {
            System.Windows.Data.BindingOperations.ClearBinding(bar, AudioFool.Theming.SeekWaveform.IsPlayingProperty);
            AudioFool.Theming.SeekWaveform.SetIsPlaying(bar, false);
        }
        var view = Descendants(bar).OfType<AudioFool.Theming.WaveformView>().First();
        var drawn = typeof(AudioFool.Theming.WaveformView).GetField("_drawnSplit",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var splits = new List<double>();
        var frames = 0;
        var renders = 0;
        EventHandler countFrame = (_, _) =>
        {
            frames++;
            var now = (double)drawn.GetValue(view)!;
            if (splits.Count == 0 || now != splits[^1])
                renders++;
            splits.Add(now);
        };
        CompositionTarget.Rendering += countFrame;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < 2000)
            Settle(5);
        CompositionTarget.Rendering -= countFrame;
        Console.WriteLine($"WPF frames: {frames / watch.Elapsed.TotalSeconds:0} a second; the split was in a new place on {renders / watch.Elapsed.TotalSeconds:0} a second");
        var steps = splits.Zip(splits.Skip(1), (a, b) => b - a).Where(d => d != 0).ToList();
        Console.WriteLine($"glide over 2 s ({view.ActualWidth:0} px wide, {view.ActualWidth / engine.Duration.TotalSeconds:0.00} px/s): " +
                          $"{steps.Count} moves, largest {(steps.Count == 0 ? 0 : steps.Max()):0.00} px, " +
                          $"backward {steps.Count(d => d < 0)} (largest {(steps.Any(d => d < 0) ? -steps.Min() : 0):0.00} px)");

        engine.Pause();
        Settle(300);
        Console.WriteLine($"position {vm.PositionDisplay} of {vm.DurationDisplay}");

        Save(main, outPath, w, h, scale);

        bar.UpdateLayout();
        const double zoom = 3;
        var rtb = new RenderTargetBitmap((int)(bar.ActualWidth * zoom), (int)(bar.ActualHeight * zoom), 96 * zoom, 96 * zoom, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle((Brush)main.FindResource("color.panel.bg"), null, new Rect(0, 0, bar.ActualWidth, bar.ActualHeight));
            dc.DrawRectangle(new VisualBrush(bar) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top },
                null, new Rect(0, 0, bar.ActualWidth, bar.ActualHeight));
        }
        rtb.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));
        using (var stream = File.Create(Path.ChangeExtension(outPath, ".seekbar.png")))
            encoder.Save(stream);

        engine.Stop();
        Settle(200);
        Console.WriteLine($"after Stop: waveform {(vm.NowPlayingWaveform is null ? "cleared" : "kept")}");
    }

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

    /// <summary>
    /// --window device: what happens when the Windows default output changes,
    /// without changing it. SwitchDevice is what the change calls, so it is
    /// called directly, on copies of real tracks played silently in shared mode.
    /// </summary>
    private static void RunDeviceSwitch(MainViewModel vm, AudioEngine engine, BassRuntime runtime, string files)
    {
        void Log(string s) => Console.WriteLine(s);
        var dir = Path.Combine(Path.GetTempPath(), "themelab-device-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var tracks = files.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(source =>
            {
                var copy = Path.Combine(dir, Path.GetFileName(source));
                File.Copy(source, copy);
                return AudioFool.Core.Library.TagReader.Read(copy);
            }).ToList();

            runtime.Initialise();
            vm.Volume = 0;
            var notify = typeof(ManagedBass.Wasapi.WasapiNotificationType);
            Log($"notifications: {string.Join(", ", Enum.GetNames(notify).Select(n => $"{n}={(int)Enum.Parse(notify, n)}"))}");
            Log($"device: '{runtime.OutputDeviceName}' exclusive={runtime.SupportsExclusive} rates={string.Join("/", runtime.ExclusiveRates)} mix={runtime.SharedMixRate}");

            var again = runtime.ReprobeDefaultDevice();
            Log($"same device announced again: changed={again}, exclusive still {runtime.SupportsExclusive} {(again ? "WRONG" : "OK")}");

            bool Until(Func<bool> condition)
            {
                var clock = System.Diagnostics.Stopwatch.StartNew();
                while (clock.ElapsedMilliseconds < 5000)
                {
                    if (condition())
                        return true;
                    Settle(10);
                }
                return false;
            }

            // Playing.
            engine.Play(tracks, 0);
            engine.Seek(TimeSpan.FromSeconds(30));
            Settle(500);
            var before = engine.Heard.Position;   // a reopen carries on from what was heard (session 67)
            var clock = System.Diagnostics.Stopwatch.StartNew();
            engine.SwitchDevice(OutputMode.Shared);
            var took = clock.ElapsedMilliseconds;
            var back = engine.Position;
            Settle(400);
            var later = engine.Position;
            var playingOk = engine.State == PlaybackState.Playing && Math.Abs((back - before).TotalMilliseconds) < 25
                            && later - back > TimeSpan.FromMilliseconds(250) && engine.HasOutput;
            Log($"switch while playing: {took} ms, {before.TotalMilliseconds:0} -> {back.TotalMilliseconds:0} ms, 400 ms on {later.TotalMilliseconds:0}, "
                + $"state={engine.State} output '{engine.OutputDescription}' {(playingOk ? "OK" : "WRONG")}");

            // The next track was opened ahead; it is opened again on the new device.
            if (tracks.Count > 1)
            {
                var next = tracks[1].FilePath;
                Until(() => engine.HoldsFile(next));
                engine.SwitchDevice(OutputMode.Shared);
                var reopened = Until(() => engine.HoldsFile(next));
                Log($"next track after a switch: opened ahead again={reopened} {(reopened ? "OK" : "WRONG")}");
            }

            // Paused.
            engine.Pause();
            Settle(100);
            var pausedAt = engine.Heard.Position;   // held still while paused; the reopen carries on from it
            engine.SwitchDevice(OutputMode.Shared);
            var pausedOk = engine.State == PlaybackState.Paused && Math.Abs((engine.Position - pausedAt).TotalMilliseconds) < 25;
            Log($"switch while paused: {pausedAt.TotalMilliseconds:0} -> {engine.Position.TotalMilliseconds:0} ms, state={engine.State} {(pausedOk ? "OK" : "WRONG")}");

            // Bit-Perfect turned on and off while paused: stays paused, same place.
            // On the engine, not the view model, so settings.json isn't written.
            foreach (var mode in new[] { OutputMode.Exclusive, OutputMode.Shared })
            {
                engine.OutputMode = mode;
                Settle(200);
                var stillPaused = engine.State == PlaybackState.Paused && Math.Abs((engine.Position - pausedAt).TotalMilliseconds) < 25;
                Log($"bit-perfect {mode} while paused: {engine.Position.TotalMilliseconds:0} ms, state={engine.State} {(stillPaused ? "OK" : "WRONG")}");
            }
            engine.Resume();
            Settle(400);
            Log($"  resumed: {engine.Position.TotalMilliseconds:0} ms, state={engine.State} {(engine.Position - pausedAt > TimeSpan.FromMilliseconds(250) ? "OK" : "WRONG")}");

            // Stopped: the connection is closed, and the next play opens one.
            engine.Stop();
            foreach (var mode in new[] { OutputMode.Exclusive, OutputMode.Shared })
            {
                engine.OutputMode = mode;
                Settle(200);
                Log($"bit-perfect {mode} while stopped: state={engine.State} {(engine.State == PlaybackState.Stopped ? "OK" : "WRONG")}");
            }
            engine.SwitchDevice(OutputMode.Shared);
            var closed = !engine.HasOutput;
            var played = engine.Play(tracks, 0);
            Settle(300);
            Log($"switch while stopped: closed={closed}, plays after={played && engine.State == PlaybackState.Playing && engine.Position > TimeSpan.Zero} "
                + $"{(closed && played ? "OK" : "WRONG")}");

            // The view model's side: what the change itself runs on the UI thread.
            var follow = (Task)typeof(MainViewModel).GetMethod("FollowDefaultDeviceAsync",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(vm, null)!;
            Until(() => follow.IsCompleted);
            follow.GetAwaiter().GetResult();
            Log($"view model: status '{vm.StatusText}', bit-perfect {vm.IsExclusiveOutput}, can use {vm.CanUseExclusiveOutput}, "
                + $"state={engine.State} {(engine.State == PlaybackState.Playing ? "OK" : "WRONG")}");

            engine.Stop();
            Settle(200);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>The engine, timing how long a save keeps the file away from playback.</summary>
    private sealed class TimedHolder(AudioEngine engine) : AudioFool.Core.Library.IFileHolder
    {
        public (TimeSpan At, long Ms)? Released { get; private set; }

        public bool HoldsFile(string path) => engine.HoldsFile(path);

        public T Saving<T>(string path, Func<bool> needsRelease, Func<T> save)
        {
            // Timed from the moment the engine is told to let go: the trial save
            // before it runs with the track still playing.
            TimeSpan at = default;
            System.Diagnostics.Stopwatch? clock = null;
            var result = engine.Saving(path,
                () =>
                {
                    var release = needsRelease();
                    if (release)
                    {
                        at = engine.Position;
                        clock = System.Diagnostics.Stopwatch.StartNew();
                    }
                    return release;
                },
                save);
            if (clock is not null)
                Released = (at, clock.ElapsedMilliseconds);
            return result;
        }
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
                     && vm.WindowTitle == $"{expectArtist} · {expectTitle}"
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

        // A save that would grow the paused playing file goes through while the
        // engine lets go of it, and the track comes back paused where it was.
        var path = Row("04").FilePath;
        var sizeBefore = new FileInfo(path).Length;
        var pausedAt = engine.Heard.Position;   // the reopen carries on from what was heard (session 67)
        Await(vm.ApplyInlineEditAsync(Row("04"), AudioFool.Core.Library.InlineField.Title, new string('x', 300_000)));
        var pausedOk = new FileInfo(path).Length > sizeBefore && vm.NowPlaying?.Title?.Length == 300_000
                       && engine.State == PlaybackState.Paused && engine.HoldsFile(path)
                       && Math.Abs((engine.Position - pausedAt).TotalMilliseconds) < 30;
        Log($"resizing edit, paused -> 04: grew {sizeBefore:N0} -> {new FileInfo(path).Length:N0} B, "
            + $"position {pausedAt.TotalMilliseconds:0} -> {engine.Position.TotalMilliseconds:0} ms, state={engine.State} "
            + $"{(pausedOk ? "OK" : "WRONG")}");

        // The stream still plays after the saves: position moves on.
        var at = engine.Position;
        engine.TogglePause();
        Settle(300);
        Log($"after saves, playback resumes: {at.TotalMilliseconds:0} ms -> {engine.Position.TotalMilliseconds:0} ms, state={engine.State}");

        // The next track: dropped and opened again, silently.
        vm.PlayTrackCommand.Execute(Row("01"));
        var two = Row("02").FilePath;
        bool Until(Func<bool> condition)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds < 5000)
            {
                if (condition())
                    return true;
                Settle(10);
            }
            return false;
        }
        Until(() => engine.HoldsFile(two));
        var twoSize = new FileInfo(two).Length;
        Await(vm.ApplyInlineEditAsync(Row("02"), AudioFool.Core.Library.InlineField.Title, new string('y', 300_000)));
        var refetched = Until(() => engine.HoldsFile(two));
        Log($"resizing edit, next -> 02: grew {twoSize:N0} -> {new FileInfo(two).Length:N0} B, reopened ahead={refetched}, "
            + $"still on '{engine.CurrentTrack?.Title}' {engine.State} {(refetched && new FileInfo(two).Length > twoSize && engine.State == PlaybackState.Playing ? "OK" : "WRONG")}");

        // And if the playing track ends while the next one's file is out, the
        // handover waits for the save rather than stopping.
        engine.Saving(two, () => true, () =>
        {
            engine.Seek(engine.Duration - TimeSpan.FromMilliseconds(200));
            Thread.Sleep(1000);
            return 0;
        });
        var handedOver = WaitFor("02");
        Log($"track ends during next's save: now '{Path.GetFileName(vm.NowPlaying?.FilePath)}' state={engine.State} "
            + $"{(handedOver && engine.State == PlaybackState.Playing ? "OK" : "WRONG")}");
        engine.Stop();
        Settle(200);

        // Jumping onto a track while a save is writing it: the open waits for the
        // save. The save runs on a worker, stretched to 600 ms, as a real one would
        // run beside the UI thread; the jump is made from the UI thread.
        void JumpDuringSave(string label, string file, int index, bool release)
        {
            vm.PlayTrackCommand.Execute(Row("01"));
            Until(() => engine.HoldsFile(two));
            var path = Row(file).FilePath;
            var started = new ManualResetEventSlim();
            long savedAt = 0;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var save = Task.Run(() => engine.Saving(path, () => release, () =>
            {
                started.Set();
                Thread.Sleep(600);
                savedAt = clock.ElapsedMilliseconds;
                return 0;
            }));
            started.Wait();
            var jumpedFrom = clock.ElapsedMilliseconds;
            var played = engine.JumpTo(index);
            var openedAt = clock.ElapsedMilliseconds;
            save.Wait();
            var arrived = WaitFor(file);
            var ok = played && arrived && openedAt >= savedAt && engine.State == PlaybackState.Playing && engine.HoldsFile(path);
            Log($"{label}: jump at {jumpedFrom} ms, save done at {savedAt} ms, jump returned at {openedAt} ms, "
                + $"now '{Path.GetFileName(vm.NowPlaying?.FilePath)}' {engine.State} {(ok ? "OK" : "WRONG")}");
            engine.Stop();
            Settle(200);
        }

        JumpDuringSave("next onto a track being saved", "03", 2, release: false);
        JumpDuringSave("next onto the opened-ahead track while it is let go", "02", 1, release: true);

        // --long "a.flac;b.mp3;c.dsf": real tracks, long enough to save while
        // playing. Each is copied into the scratch folder first; only the copy
        // is played and saved.
        foreach (var source in (Arg(Environment.GetCommandLineArgs(), "--long") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var copy = Path.Combine(_scratchDir!, "long-" + Path.GetFileName(source));
            File.Copy(source, copy);
            var track = AudioFool.Core.Library.TagReader.Read(copy);
            engine.Play([track], 0);
            engine.Seek(TimeSpan.FromSeconds(30));
            Settle(500);

            var size = new FileInfo(copy).Length;
            var holder = new TimedHolder(engine);
            var commentLength = int.Parse(Arg(Environment.GetCommandLineArgs(), "--longcomment") ?? "300000", CultureInfo.InvariantCulture);
            var result = AudioFool.Core.Library.TagWriter.WriteSelectedTrackTags(track,
                new AudioFool.Core.Library.TracksTagEdit { Details = new AudioFool.Core.Library.TagDetailsEdit { Comment = new string('z', commentLength) } },
                holder);
            var back = engine.Position;
            Settle(400);
            var after = engine.Position;
            var comment = AudioFool.Core.Library.TagReader.ReadDetails(copy)?.Comment.Length ?? 0;
            var ok = result.Success && comment == commentLength && engine.State == PlaybackState.Playing && engine.HoldsFile(copy)
                     && (holder.Released is not { } r || Math.Abs((back - r.At).TotalMilliseconds) < 25)
                     && after - back > TimeSpan.FromMilliseconds(250);
            Log($"save while playing {Path.GetExtension(copy)}: {(result.Success ? "saved" : result.ErrorMessage)}, {size:N0} -> {new FileInfo(copy).Length:N0} B, comment {comment:N0}, "
                + (holder.Released is { } rel
                    ? $"released for {rel.Ms} ms at {rel.At.TotalMilliseconds:0} ms, back at {back.TotalMilliseconds:0} ms, "
                    : "not released (saved in place), ")
                + $"400 ms on {after.TotalMilliseconds:0}, output '{engine.OutputDescription}' {(ok ? "OK" : "WRONG")}");
            engine.Stop();
            Settle(200);
        }
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

        // 1b. F2 on #: two and three digits must fit in the box, not scroll inside it.
        //     A three-digit number only appears on a 100+ track album, which widens
        //     the column, so that case gets the wide column. Each opens its own edit.
        void NumberWidth(bool wide) =>
            Column("TrackNumberColumn").Width = new System.Windows.Controls.DataGridLength((double)typeof(MainWindow)
                .GetMethod("TrackNumberColumnWidth", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(null, [wide])!);
        foreach (var digits in new[] { "12", "123" })
        {
            NumberWidth(digits.Length == 3);
            main.UpdateLayout();
            Select(two, "TrackNumberColumn");
            Press(Cell(two, "TrackNumberColumn"), System.Windows.Input.Key.F2);
            var numBox = Box(two, "TrackNumberColumn")!;
            numBox.Text = digits;
            numBox.CaretIndex = digits.Length;
            main.UpdateLayout();
            var cell = Cell(two, "TrackNumberColumn");
            var at = numBox.TranslatePoint(new Point(0, 0), cell);
            var fits = numBox.ExtentWidth <= numBox.ViewportWidth + 0.01 && numBox.HorizontalOffset == 0;
            Log($"number box: '{digits}' box {numBox.ActualWidth:0.#}x{numBox.ActualHeight:0.#} at x {at.X:0.#} in cell {cell.ActualWidth:0.#} "
                + $"text {numBox.ExtentWidth:0.#} in viewport {numBox.ViewportWidth:0.#} offset {numBox.HorizontalOffset:0.#} {(fits ? "OK" : "CLIPPED")}");
            if (digits == "12")
                Save(main, Path.ChangeExtension(outPath, null) + "-number.png", w, h, scale);
            Press(numBox, System.Windows.Input.Key.Escape);
            Log($"number box: Esc -> editing={Cell(two, "TrackNumberColumn").IsEditing} #{Row("Two").TrackNumber}");
        }
        NumberWidth(false);
        main.UpdateLayout();

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

        // 5b2. A click outside the box saves and closes it, even on something that
        //      takes no focus (the now-playing title); a click inside leaves it open.
        {
            var nowTitle = (UIElement)main.FindName("NowPlayingTitle");
            three = Row("Three");
            Select(three, "ArtistColumn");
            Press(Cell(three, "ArtistColumn"), System.Windows.Input.Key.F2);
            var artistBox = Box(three, "ArtistColumn")!;
            artistBox.Text = "Clicked Away";
            Click(artistBox);
            var insideOk = Cell(three, "ArtistColumn").IsEditing && artistBox.IsKeyboardFocused;
            Log($"click outside: a click inside the box keeps it open? {insideOk} {(insideOk ? "OK" : "BROKEN")}");
            before = vm.StatusText;
            Click(nowTitle);
            AwaitSave(before);
            var closed = !Cell(Row("Three"), "ArtistColumn").IsEditing;
            var saved = AudioFool.Core.Library.TagReader.Read(three.FilePath).Artist == "Clicked Away";
            Log($"click outside: on the now-playing title (focusable={nowTitle.Focusable}) -> closed={closed} "
                + $"status='{vm.StatusText}' saved={saved} {(closed && saved ? "OK" : "BROKEN")}");
            Log("click outside: " + FileTags(three.FilePath));

            // Unchanged: closes, writes nothing.
            three = Row("Three");
            Select(three, "SongColumn");
            Press(Cell(three, "SongColumn"), System.Windows.Input.Key.F2);
            var stamp = File.GetLastWriteTimeUtc(three.FilePath);
            Click(nowTitle);
            Settle(300);
            var untouched = File.GetLastWriteTimeUtc(three.FilePath) == stamp;
            var closedUnchanged = !Cell(Row("Three"), "SongColumn").IsEditing;
            Log($"click outside: unchanged -> closed={closedUnchanged} file untouched={untouched} "
                + $"{(closedUnchanged && untouched ? "OK" : "BROKEN")}");
        }

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

        // 5d. Tab walks the column like Enter, and never leaves the table: down
        //     after a save, down unchanged, and on the last row it saves and
        //     stays. Shift+Tab (up) is called directly: WPF reads the real Shift
        //     key, which can't be faked here.
        {
            var rowsNow = grid.Items.OfType<Track>().ToList();
            var tabFrom = rowsNow[0];
            var tabTo = rowsNow[1].FilePath;
            Select(tabFrom, "TrackNumberColumn");
            Press(Cell(tabFrom, "TrackNumberColumn"), System.Windows.Input.Key.F2);
            Box(tabFrom, "TrackNumberColumn")!.Text = tabFrom.TrackNumber == 1 ? "11" : "1";
            before = vm.StatusText;
            var tabHandled = Press(Box(tabFrom, "TrackNumberColumn")!, System.Windows.Input.Key.Tab);
            AwaitSave(before);
            var tabNext = vm.Tracks.First(t => t.FilePath == tabTo);
            var tabBox = Box(tabNext, "TrackNumberColumn");
            var tabOk = tabHandled && (grid.SelectedItem as Track)?.FilePath == tabTo
                        && Cell(tabNext, "TrackNumberColumn").IsEditing && tabBox?.IsKeyboardFocused == true;
            Log($"tab walk: saved -> status='{vm.StatusText}' '{(grid.SelectedItem as Track)?.DisplayTitle}' editing #="
                + $"{Cell(tabNext, "TrackNumberColumn").IsEditing} focused={tabBox?.IsKeyboardFocused} {(tabOk ? "OK" : "BROKEN")}");
            Log("tab walk: " + FileTags(tabFrom.FilePath));

            var tabThird = grid.Items.OfType<Track>().SkipWhile(t => t.FilePath != tabTo).Skip(1).FirstOrDefault();
            if (tabBox is not null && tabThird is not null)
            {
                Press(tabBox, System.Windows.Input.Key.Tab);
                Settle(300);
                var ok = (grid.SelectedItem as Track)?.FilePath == tabThird.FilePath && Cell(tabThird, "TrackNumberColumn").IsEditing;
                Log($"tab walk: unchanged Tab -> '{(grid.SelectedItem as Track)?.DisplayTitle}' {(ok ? "OK" : "BROKEN")}");

                // Up a row, as Shift+Tab does.
                typeof(MainWindow).GetMethod("SaveAndEditNextRow", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                    .Invoke(main, [-1]);
                Settle(300);
                var back = vm.Tracks.First(t => t.FilePath == tabTo);
                var upOk = (grid.SelectedItem as Track)?.FilePath == tabTo && Cell(back, "TrackNumberColumn").IsEditing;
                Log($"tab walk: up (Shift+Tab) -> '{(grid.SelectedItem as Track)?.DisplayTitle}' {(upOk ? "OK" : "BROKEN")}");
                if (Box(back, "TrackNumberColumn") is { } backBox)
                    Press(backBox, System.Windows.Input.Key.Escape);
            }

            var tabLast = grid.Items.OfType<Track>().Last();
            Select(tabLast, "TrackNumberColumn");
            Press(Cell(tabLast, "TrackNumberColumn"), System.Windows.Input.Key.F2);
            var lastHandled = Press(Box(tabLast, "TrackNumberColumn")!, System.Windows.Input.Key.Tab);
            Settle(300);
            var lastOk = lastHandled && grid.IsKeyboardFocusWithin
                         && !grid.Items.OfType<Track>().Any(t => Cell(t, "TrackNumberColumn").IsEditing);
            Log($"tab walk: last row Tab -> stays in table={grid.IsKeyboardFocusWithin} "
                + $"focus={System.Windows.Input.Keyboard.FocusedElement?.GetType().Name} {(lastOk ? "OK" : "BROKEN")}");
        }

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

    /// <summary>
    /// Album and selection tag windows read their detail fields in the background
    /// (session 47). Pumps until they arrive and reports whether the fields opened
    /// greyed, how long the read took, and that Save came back.
    /// </summary>
    private static void WaitForDetails(TagEditViewModel vm)
    {
        var greyed = vm.IsReadingDetails;
        var savable = vm.CanSave;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (!vm.DetailsRead.IsCompleted && clock.ElapsedMilliseconds < 60_000)
            Settle(25);
        Console.WriteLine($"  details: greyed at open={greyed} save at open={savable}, read in {clock.ElapsedMilliseconds} ms, now greyed={vm.IsReadingDetails} save={vm.CanSave}");
    }

    /// <summary>A 16-bit stereo WAV of digital silence, for checks that must make no sound.</summary>
    private static string WriteSilentWav(string path, int rate, int seconds)
    {
        const short channels = 2, bits = 16;
        var dataBytes = rate * seconds * channels * (bits / 8);
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write("RIFF"u8);
        writer.Write(36 + dataBytes);
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write(channels);
        writer.Write(rate);
        writer.Write(rate * channels * (bits / 8));
        writer.Write((short)(channels * (bits / 8)));
        writer.Write(bits);
        writer.Write("data"u8);
        writer.Write(dataBytes);
        writer.Write(new byte[dataBytes]);
        return path;
    }

    /// <summary>
    /// --window intro: the About intro (session 64) at moments along its timeline,
    /// one render each (&lt;out&gt;-&lt;ms&gt;.png). Never shown, so its sound never opens:
    /// laid out by hand at the main window's size and stepped through ShowAt. Prints
    /// each screen's opacity and the colour at the centre and a corner.
    /// </summary>
    private static int RunIntro(MainWindow main, string outPath, double w, double h, double scale)
    {
        // Drawn straight into the bitmap: through the VisualBrush an unshown plain
        // Window (not a FluentWindow) draws nothing.
        _direct = true;
        var intro = new IntroWindow(main, 0);
        intro.ApplyTemplate();
        var root = (FrameworkElement)intro.Content;
        root.Measure(new Size(w, h));
        root.Arrange(new Rect(0, 0, w, h));
        root.UpdateLayout();

        var showAt = typeof(IntroWindow).GetMethod("ShowAt", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var sony = (FrameworkElement)intro.FindName("SonyScreen");
        var playStation = (FrameworkElement)intro.FindName("PlayStationScreen");
        Console.WriteLine($"intro: window would cover {intro.Left:0.#},{intro.Top:0.#} {intro.Width:0.#}x{intro.Height:0.#} (main window off-screen)");
        Console.WriteLine($"intro: content {root.ActualWidth:0.#}x{root.ActualHeight:0.#}, screens {sony.ActualWidth:0.#} / {playStation.ActualWidth:0.#}, backdrops {((System.Windows.Controls.Panel)sony).Background} / {((System.Windows.Controls.Panel)playStation).Background}");
        foreach (var name in new[] { "SonyVersion", "PlayStationVersion" })
        {
            var line = (System.Windows.Controls.TextBlock)intro.FindName(name);
            var p = line.TranslatePoint(new Point(0, 0), root);
            Console.WriteLine($"intro: {name} '{line.Text}' at {p.X:0.#},{p.Y:0.#} size {line.ActualWidth:0.#}x{line.ActualHeight:0.#}, {line.FontSize} px, {line.Foreground}, {root.ActualHeight - p.Y - line.ActualHeight:0.#} px above the bottom");
        }

        foreach (var ms in new[] { 0, 200, 3000, 7100, 7300, 7500, 10000, 14450, 14900 })
        {
            showAt.Invoke(intro, [TimeSpan.FromMilliseconds(ms)]);
            root.UpdateLayout();
            Settle(50);
            var path = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(outPath))!,
                $"{Path.GetFileNameWithoutExtension(outPath)}-{ms}.png");
            Save(root, path, w, h, scale);

            BitmapSource bitmap;
            using (var file = File.OpenRead(path))
                bitmap = new FormatConvertedBitmap(BitmapFrame.Create(file, BitmapCreateOptions.None, BitmapCacheOption.OnLoad), PixelFormats.Bgra32, null, 0);
            string At(int x, int y)
            {
                var px = new byte[4];
                bitmap.CopyPixels(new Int32Rect(x, y, 1, 1), px, 4, 0);
                return $"#{px[2]:X2}{px[1]:X2}{px[0]:X2}";
            }
            Console.WriteLine($"intro {ms,5} ms: sony {sony.Opacity:0.00}  playstation {playStation.Opacity:0.00}  centre {At(bitmap.PixelWidth / 2, bitmap.PixelHeight / 2)}  corner {At(2, 2)}  -> {Path.GetFileName(path)}");
        }

        // The sound: unpacked from the exe and opened as the intro opens it, never played.
        var soundFile = (string?)typeof(IntroWindow).GetMethod("SoundFile", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(null, null);
        Console.WriteLine($"intro sound: {soundFile} ({(soundFile is null ? 0 : new FileInfo(soundFile).Length):N0} B)");
        if (soundFile is not null)
        {
            var player = new MediaPlayer { Volume = 0 };
            string? outcome = null;
            player.MediaOpened += (_, _) => outcome = $"opened, {player.NaturalDuration}";
            player.MediaFailed += (_, e) => outcome = $"FAILED: {e.ErrorException.Message}";
            player.Open(new Uri(soundFile));
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (outcome is null && clock.ElapsedMilliseconds < 5000)
                Settle(50);
            Console.WriteLine($"intro sound: {outcome ?? "not opened in 5 s"} in {clock.ElapsedMilliseconds} ms");
            player.Close();
        }

        intro.Close();
        return 0;
    }

    private static int RunLend(AudioEngine engine, BassRuntime runtime)
    {
        // Never the real playback.log (see HANDOFF, Claude's shells and AppData).
        AudioFool.Core.Playback.PlaybackDiagnostics.LogPath =
            Path.Combine(Path.GetTempPath(), "themelab-lend-" + Guid.NewGuid().ToString("N")[..8] + ".log");
        var ready = runtime.Initialise();
        Console.WriteLine($"init {ready} {runtime.InitError}, exclusive {runtime.SupportsExclusive}, rates {string.Join(",", runtime.ExclusiveRates)}");
        if (!runtime.SupportsExclusive || !runtime.ExclusiveRates.Contains(44100))
        {
            Console.WriteLine("the default device can't play 44.1 kHz exclusively: nothing to check");
            return 1;
        }

        var dir = Path.Combine(Path.GetTempPath(), "themelab-lend-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var file = WriteSilentWav(Path.Combine(dir, "Silence 44100.wav"), 44100, seconds: 20);
        var tracks = new List<Track> { AudioFool.Core.Library.TagReader.Read(file) };
        var failures = new List<string>();
        void Expect(string what, object? actual, object? expected)
        {
            var ok = Equals(actual, expected);
            Console.WriteLine($"  {what}: {actual} {(ok ? "OK" : $"WRONG (expected {expected})")}");
            if (!ok)
                failures.Add(what);
        }

        // Someone else opening the device exclusively, as the intro's sound needs
        // it free (OutputChain is internal: reached by reflection, a dev tool).
        var chainType = typeof(AudioEngine).Assembly.GetType("AudioFool.Core.Playback.OutputChain")!;
        var tryCreate = chainType.GetMethod("TryCreate")!;
        bool OthersCanOpen()
        {
            var call = new object?[] { OutputMode.Exclusive, 44100, 2, null };
            var chain = tryCreate.Invoke(null, call) as IDisposable;
            chain?.Dispose();
            if (chain is null)
                Console.WriteLine($"  (open refused: {call[3]})");
            return chain is not null;
        }

        try
        {
            engine.OutputMode = OutputMode.Exclusive;
            Console.WriteLine($"play {engine.Play(tracks, 0)}, bit-perfect {engine.IsBitPerfect}, output '{engine.OutputDescription}'");
            Settle(1500);

            Console.WriteLine("paused, then lent:");
            engine.Pause();
            Settle(200);
            var at = engine.Heard.Position;   // held still while paused; a reopen carries on from it (session 67)
            Expect("others can open it before the loan", OthersCanOpen(), false);
            var loan = engine.LendDevice();
            Expect("output open while lent", engine.HasOutput, false);
            Expect("others can open it while lent", OthersCanOpen(), true);
            Expect("state while lent", engine.State, PlaybackState.Paused);
            Expect("position held while lent", Math.Abs((engine.Position - at).TotalMilliseconds) < 1, true);
            Settle(500);

            Console.WriteLine("returned:");
            loan.Dispose();
            loan.Dispose();
            Expect("state", engine.State, PlaybackState.Paused);
            Expect("bit-perfect", engine.IsBitPerfect, true);
            Expect("same place (within 50 ms)", Math.Abs((engine.Position - at).TotalMilliseconds) < 50, true);
            Expect("others can open it after", OthersCanOpen(), false);

            Console.WriteLine("resumed:");
            engine.Resume();
            Settle(1000);
            var moved = (engine.Position - at).TotalMilliseconds;
            Console.WriteLine($"  moved {moved:0} ms in 1 s");
            Expect("state", engine.State, PlaybackState.Playing);
            Expect("plays on (600 to 1400 ms in 1 s)", moved is > 600 and < 1400, true);

            Console.WriteLine("stopped, then lent:");
            engine.Stop();
            Settle(200);
            var stoppedLoan = engine.LendDevice();
            Expect("others can open it while lent", OthersCanOpen(), true);
            stoppedLoan.Dispose();
            Expect("state after", engine.State, PlaybackState.Stopped);
            Expect("nothing reopened", engine.HasOutput, false);
        }
        finally
        {
            engine.Stop();
            engine.OutputMode = OutputMode.Shared;
            Settle(300);
            Directory.Delete(dir, recursive: true);
        }

        Console.WriteLine(failures.Count == 0 ? "ALL OK" : "FAILED: " + string.Join(", ", failures));
        return failures.Count == 0 ? 0 : 1;
    }

    /// <summary>A DSD64 stereo DSF of DSD's idle pattern (0x69), for checks that must make no sound.</summary>
    private static string WriteSilentDsf(string path, int seconds)
    {
        const int rate = 2_822_400, block = 4096, channels = 2;
        var samples = (long)rate * seconds;
        var blocks = (int)Math.Ceiling(samples / 8.0 / block);
        var dataBytes = (long)blocks * block * channels;
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write("DSD "u8);
        writer.Write(28L);
        writer.Write(28L + 52 + 12 + dataBytes);
        writer.Write(0L);                  // no metadata
        writer.Write("fmt "u8);
        writer.Write(52L);
        writer.Write(1);                   // format version
        writer.Write(0);                   // raw DSD
        writer.Write(2);                   // stereo
        writer.Write(channels);
        writer.Write(rate);
        writer.Write(1);                   // bits per sample
        writer.Write(samples);
        writer.Write(block);
        writer.Write(0);
        writer.Write("data"u8);
        writer.Write(12L + dataBytes);
        var idle = new byte[block * channels];
        Array.Fill(idle, (byte)0x69);
        for (var i = 0; i < blocks; i++)
            writer.Write(idle);
        return path;
    }

    private static int RunPlaybackFixes(AudioEngine engine, BassRuntime runtime)
    {
        // Never the real playback.log (see HANDOFF, Claude's shells and AppData).
        var logPath = Path.Combine(Path.GetTempPath(), "themelab-playbackfixes-" + Guid.NewGuid().ToString("N")[..8] + ".log");
        AudioFool.Core.Playback.PlaybackDiagnostics.LogPath = logPath;
        var ready = runtime.Initialise();
        Console.WriteLine($"init {ready} {runtime.InitError}, exclusive {runtime.SupportsExclusive}, rates {string.Join(",", runtime.ExclusiveRates)}");

        var dir = Path.Combine(Path.GetTempPath(), "themelab-playbackfixes-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        Track Wav(string name, int seconds, int rate = 44100) =>
            AudioFool.Core.Library.TagReader.Read(WriteSilentWav(Path.Combine(dir, name + ".wav"), rate, seconds));
        Track Gone(string name)
        {
            var track = Wav(name, 1);
            File.Delete(track.FilePath);
            return track;
        }

        var failures = new List<string>();
        void Expect(string what, object? actual, object? expected)
        {
            var ok = Equals(actual, expected);
            Console.WriteLine($"  {what}: {actual} {(ok ? "OK" : $"WRONG (expected {expected})")}");
            if (!ok)
                failures.Add(what);
        }

        var skipped = new List<string>();
        var finished = 0;
        engine.TracksSkipped += (_, tracks) => skipped.AddRange(tracks.Select(t => Path.GetFileNameWithoutExtension(t.FilePath)));
        engine.PlaybackFinished += (_, _) => finished++;
        string Now() => Path.GetFileNameWithoutExtension(engine.CurrentTrack?.FilePath ?? "(none)");
        void WaitFor(Func<bool> done, int ms)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (!done() && clock.ElapsedMilliseconds < ms)
                Settle(25);
            Settle(100);
        }

        var chainType = typeof(AudioEngine).Assembly.GetType("AudioFool.Core.Playback.OutputChain")!;
        var tryCreate = chainType.GetMethod("TryCreate")!;
        IDisposable? OpenExclusive(int rate)
        {
            var call = new object?[] { OutputMode.Exclusive, rate, 2, null };
            return tryCreate.Invoke(null, call) as IDisposable;
        }

        bool OthersCanOpen()
        {
            using var chain = OpenExclusive(44100);
            return chain is not null;
        }

        try
        {
            engine.OutputMode = OutputMode.Shared;
            engine.Volume = 0.05;
            engine.Repeat = RepeatMode.Off;
            engine.Shuffle = false;

            Console.WriteLine("1. shared: the end of a track over a song that won't open");
            engine.Play([Wav("A", 2), Gone("B"), Wav("C", 20)], 0);
            WaitFor(() => Now() == "C", 5000);
            Expect("playing", Now(), "C");
            Expect("skipped", string.Join(",", skipped), "B");

            Console.WriteLine("2. Next and Previous over it");
            skipped.Clear();
            engine.Play([Wav("D", 20), Gone("E"), Wav("F", 20)], 0);
            Settle(500);
            engine.Next();
            Settle(300);
            Expect("Next", Now(), "F");
            engine.Previous();
            Settle(300);
            Expect("Previous", Now(), "D");
            Expect("skipped", string.Join(",", skipped), "E,E");
            skipped.Clear();
            engine.Play([Wav("G", 20), Gone("H"), Gone("I")], 0);
            Settle(300);
            Expect("Next with nothing after that opens", engine.Next(), false);
            Settle(200);
            Expect("still playing", Now(), "G");
            Expect("skipped", string.Join(",", skipped), "H,I");

            Console.WriteLine("3. a queue ending on songs that won't open");
            skipped.Clear();
            finished = 0;
            engine.Play([Wav("J", 2), Gone("K")], 0);
            WaitFor(() => engine.State == PlaybackState.Stopped, 5000);
            Expect("state", engine.State, PlaybackState.Stopped);
            Expect("finished raised", finished, 1);
            Expect("skipped", string.Join(",", skipped), "K");
            Expect("device let go", engine.HasOutput, false);

            if (!runtime.SupportsExclusive || !runtime.ExclusiveRates.Contains(44100))
            {
                Console.WriteLine("the default device can't play 44.1 kHz exclusively: the rest needs it");
                failures.Add("no exclusive device");
                return 1;
            }

            engine.OutputMode = OutputMode.Exclusive;

            Console.WriteLine("4. Bit-Perfect: Stop lets go of the device");
            engine.Play([Wav("L", 20)], 0);
            Settle(800);
            Expect("bit-perfect", engine.IsBitPerfect, true);
            Expect("others can open it while playing", OthersCanOpen(), false);
            engine.Pause();
            Settle(200);
            Expect("others can open it while paused (held, as before)", OthersCanOpen(), false);
            engine.Stop();
            Expect("output after Stop", engine.HasOutput, false);
            Expect("others can open it after Stop", OthersCanOpen(), true);
            Expect("volume off while stopped in Bit-Perfect", engine.SupportsVolume, false);
            engine.Play([Wav("M", 20)], 0);
            Settle(500);
            Expect("plays again, bit-perfect", engine.IsBitPerfect && engine.State == PlaybackState.Playing, true);

            Console.WriteLine("5. Bit-Perfect: the end of the queue lets go of it, after the last of it is heard");
            finished = 0;
            engine.Play([Wav("N", 2)], 0);
            WaitFor(() => engine.State == PlaybackState.Stopped, 5000);
            Expect("finished raised", finished, 1);
            Expect("others can open it after", OthersCanOpen(), true);
            var finishLine = File.ReadLines(logPath).LastOrDefault(l => l.Contains("queue finished")) ?? "(none)";
            Console.WriteLine($"  log: {finishLine}");

            Console.WriteLine("6. a reopen carries on from what was heard");
            engine.Play([Wav("O", 20)], 0);
            Settle(3000);
            var heard = engine.Heard.Position;
            var decoded = engine.Position;
            engine.SwitchDevice(OutputMode.Exclusive);
            var reopened = engine.Position;
            Console.WriteLine($"  playing: heard {heard.TotalMilliseconds:0} ms, decoder {decoded.TotalMilliseconds:0} ms, reopened at {reopened.TotalMilliseconds:0} ms");
            Expect("reopened near what was heard (within 40 ms)", Math.Abs((reopened - heard).TotalMilliseconds) < 40, true);
            Expect("not at the decoder (100 ms or more before it)", (decoded - reopened).TotalMilliseconds >= 100, true);
            Settle(500);
            engine.Pause();
            Settle(300);
            heard = engine.Heard.Position;
            decoded = engine.Position;
            engine.SwitchDevice(OutputMode.Exclusive);
            reopened = engine.Position;
            Console.WriteLine($"  paused: heard {heard.TotalMilliseconds:0} ms, decoder {decoded.TotalMilliseconds:0} ms, reopened at {reopened.TotalMilliseconds:0} ms");
            Expect("paused reopen near what was heard (within 40 ms)", Math.Abs((reopened - heard).TotalMilliseconds) < 40, true);
            Expect("still paused", engine.State, PlaybackState.Paused);
            engine.Stop();

            Console.WriteLine("7. DoP never on a shared output");
            var dsf = AudioFool.Core.Library.TagReader.Read(WriteSilentDsf(Path.Combine(dir, "P.dsf"), 6));
            engine.Play([dsf], 0);
            Settle(800);
            Console.WriteLine($"  DAC free: '{engine.OutputDescription}'");
            Expect("DoP, bit-perfect", engine.IsDopActive && engine.IsBitPerfect, true);
            var dopRate = engine.IsDopActive ? double.Parse(System.Text.RegularExpressions.Regex.Match(engine.OutputDescription, @"([\d.]+) kHz").Groups[1].Value,
                System.Globalization.CultureInfo.InvariantCulture) : 0;
            engine.Stop();

            // Exclusive refusals as the engine remembers them (a DAC held elsewhere
            // refuses shared mode too, so a real refusal can't be staged here).
            var refusedField = typeof(AudioEngine).GetField("_exclusiveRefusedAt", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            void Refuse(params int[] rates)
            {
                engine.SwitchDevice(OutputMode.Exclusive);   // clears earlier ones
                var refused = (HashSet<int>)refusedField.GetValue(engine)!;
                foreach (var rate in rates)
                    refused.Add(rate);
            }

            var dopHz = (int)Math.Round(dopRate * 1000.0);
            Console.WriteLine($"  DoP rate {dopHz} Hz refused:");
            Refuse(dopHz);
            engine.Play([dsf], 0);
            Settle(800);
            Console.WriteLine($"  '{engine.OutputDescription}'");
            Expect("not DoP", engine.IsDopActive, false);
            Expect("playing", engine.State, PlaybackState.Playing);
            engine.Stop();

            Console.WriteLine("  44.1 kHz refused (a shared output), then a DSF opened ahead as DoP:");
            Refuse(44100);
            engine.Play([Wav("Q", 2), dsf], 0);
            Settle(500);
            Console.WriteLine($"  first track: '{engine.OutputDescription}' bit-perfect {engine.IsBitPerfect}");
            Expect("first track shared", engine.IsBitPerfect, false);
            WaitFor(() => Path.GetExtension(engine.CurrentTrack?.FilePath ?? "") == ".dsf", 5000);
            Settle(800);
            Console.WriteLine($"  then the DSF: '{engine.OutputDescription}'");
            Expect("on the DSF", Path.GetExtension(engine.CurrentTrack?.FilePath ?? ""), ".dsf");
            Expect("DoP only if bit-perfect", !engine.IsDopActive || engine.IsBitPerfect, true);
            Expect("reopened exclusive for DoP, not joined onto the shared output", engine.IsDopActive && engine.IsBitPerfect, true);
            engine.Stop();
        }
        finally
        {
            engine.Stop();
            engine.OutputMode = OutputMode.Shared;
            Settle(300);
            foreach (var line in File.Exists(logPath) ? File.ReadLines(logPath).Where(l => l.Contains("skipped") || l.Contains("DoP refused") || l.Contains("queue finished") || l.Contains("rate change")) : [])
                Console.WriteLine($"  log: {line}");
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }

        Console.WriteLine(failures.Count == 0 ? "ALL OK" : "FAILED: " + string.Join(", ", failures));
        return failures.Count == 0 ? 0 : 1;
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
