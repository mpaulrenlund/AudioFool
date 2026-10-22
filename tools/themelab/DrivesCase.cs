using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using AudioFool;
using AudioFool.Core.Library;
using AudioFool.Core.Models;
using AudioFool.Core.Playback;
using AudioFool.Core.Settings;
using AudioFool.Services;
using AudioFool.ViewModels;

namespace ThemeLab;

/// <summary>
/// --window drives (session 68): a drive plugged in and pulled out while the app
/// runs, with subst standing in for the SSD. A scratch library of three songs on a
/// spare letter, the real main window off-screen (its DriveWatcher listening), one
/// song played silently and paused. Then: an unrelated drive comes and goes (no
/// refresh); the music drive is pulled (the warning); it comes back under another
/// letter (folder, lists, queue and now playing re-pointed, Next plays from the new
/// letter); pulled and back under the same letter (warning cleared, no move); and
/// F5 with the watcher detached, after another letter change. Windows' own notice
/// is used when subst sends one, else the same message is sent to the window.
/// settings.json and library.json are put back byte for byte.
/// </summary>
internal static partial class Program
{
    private const uint WmDeviceChange = 0x0219;

    private static int RunDrives()
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        ThemeService.Apply(_theme);

        var scratch = Path.Combine(Path.GetTempPath(), "ThemeLabDrives", Guid.NewGuid().ToString("N"));
        var stage = Path.Combine(scratch, "ssd");
        var album = Path.Combine(stage, "Music", "Drive Test", "Letters");
        var other = Path.Combine(scratch, "stick");
        Directory.CreateDirectory(album);
        Directory.CreateDirectory(other);

        var testData = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, @"..\..\..\..\..\..\tests\AudioFool.Core.Tests\TestData"));
        File.Copy(Path.Combine(testData, "sample.flac"), Path.Combine(album, "01 One.flac"));
        File.Copy(Path.Combine(testData, "sample.flac"), Path.Combine(album, "02 Two.flac"));
        File.Copy(Path.Combine(testData, "sample.mp3"), Path.Combine(album, "03 Three.mp3"));

        var cachePath = LibraryCache.CachePath;
        var saved = new Dictionary<string, byte[]?>
        {
            [AppSettings.SettingsPath] = File.Exists(AppSettings.SettingsPath) ? File.ReadAllBytes(AppSettings.SettingsPath) : null,
            [cachePath] = File.Exists(cachePath) ? File.ReadAllBytes(cachePath) : null,
        };

        var used = DriveInfo.GetDrives().Select(d => d.Name[0]).ToHashSet();
        var free = "QRSTUVWXY".Where(c => !used.Contains(c)).Take(5).Select(c => $"{c}:").ToArray();
        if (free.Length < 5)
        {
            Console.WriteLine("not enough free drive letters");
            return 1;
        }

        var (first, unrelated, second, third, _) = (free[0], free[1], free[2], free[3], free[4]);
        var mapped = new List<string>();
        var allOk = true;

        void Check(bool ok, string what)
        {
            allOk &= ok;
            Console.WriteLine($"  {(ok ? "OK" : "WRONG")}: {what}");
        }

        void Subst(string letter, string? target)
        {
            var psi = new ProcessStartInfo("subst", target is null ? $"{letter} /D" : $"{letter} \"{target}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
            };
            using var process = Process.Start(psi)!;
            process.WaitForExit();
            if (target is null)
                mapped.Remove(letter);
            else
                mapped.Add(letter);
        }

        bool WaitFor(Func<bool> condition, int milliseconds)
        {
            var clock = Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds < milliseconds)
            {
                if (condition())
                    return true;
                Settle(50);
            }

            return condition();
        }

        MainWindow? main = null;
        AudioEngine? engine = null;
        try
        {
            Subst(first, stage);
            var music = $@"{first}\Music";

            MainViewModel.PlaylistsPath = Path.Combine(scratch, "playlists.json");
            var settings = new AppSettings { MusicFolders = [music], ScanOnStartup = true, GlobalHotkeys = false };
            var runtime = new BassRuntime();
            engine = new AudioEngine(runtime);
            var vm = new MainViewModel(engine, runtime, new AlbumArtService(), settings)
            {
                QualityClearancesPath = Path.Combine(scratch, "quality-cleared.json"),
                CoverCachePath = Path.Combine(scratch, "covers.json"),
            };

            var scans = 0;
            vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(MainViewModel.IsScanning) && vm.IsScanning)
                    scans++;
            };

            main = new MainWindow(vm)
            {
                Width = 1300,
                Height = 700,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -20000,
                Top = -20000,
                ShowActivated = false,
                ShowInTaskbar = false,
            };
            Application.Current.MainWindow = main;
            main.Show();

            // Counts what reaches the window from Windows itself.
            var hwnd = new WindowInteropHelper(main).Handle;
            var notices = 0;
            HwndSource.FromHwnd(hwnd)!.AddHook((IntPtr window, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
            {
                if (msg == (int)WmDeviceChange)
                    notices++;
                return IntPtr.Zero;
            });

            // Waits for Windows' notice of a subst; sends the same one if none comes.
            void Notice(string letter, bool arrived)
            {
                var before = notices;
                if (WaitFor(() => notices > before, 1000))
                {
                    Console.WriteLine($"  (Windows sent {notices - before} notice(s) for {letter})");
                    return;
                }

                var volume = Marshal.AllocHGlobal(20);
                try
                {
                    Marshal.WriteInt32(volume, 0, 20);
                    Marshal.WriteInt32(volume, 4, 2); // DBT_DEVTYP_VOLUME
                    Marshal.WriteInt32(volume, 8, 0);
                    Marshal.WriteInt32(volume, 12, 1 << (letter[0] - 'A'));
                    Marshal.WriteInt16(volume, 16, 0);
                    NcSend(hwnd, WmDeviceChange, (IntPtr)(arrived ? 0x8000 : 0x8004), volume);
                    Settle(50);
                }
                finally
                {
                    Marshal.FreeHGlobal(volume);
                }

                Console.WriteLine($"  (no notice from Windows for {letter}; sent one)");
            }

            IEnumerable<string> LibraryPaths() =>
                ((MusicLibrary)typeof(MainViewModel).GetField("_library", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                    .GetValue(vm)!).AllTracks.Select(t => t.FilePath);
            IEnumerable<string> QueuePaths() =>
                ((List<Track>)typeof(AudioEngine).GetField("_queue", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                    .GetValue(engine)!).Select(t => t.FilePath);
            bool AllOn(IEnumerable<string> paths, string letter) =>
                paths.Any() && paths.All(p => p.StartsWith(letter + @"\", StringComparison.OrdinalIgnoreCase));
            bool Idle() => !vm.IsScanning && !vm.RefreshCommand.IsRunning;

            Console.WriteLine($"start: library on {first}");
            Check(WaitFor(() => Idle() && vm.Artists.Count > 0 && scans > 0, 20000), "the startup scan found the library");
            Check(AllOn(LibraryPaths(), first) && LibraryPaths().Count() == 3, $"3 songs on {first}: {string.Join(", ", LibraryPaths().Select(Path.GetFileName))}");

            engine.Volume = 0;
            engine.Play([.. vm.Tracks], 0);
            Check(WaitFor(() => vm.NowPlaying is not null && engine.State == PlaybackState.Playing, 5000), $"playing {vm.NowPlaying?.FilePath}");
            engine.Pause();
            Settle(300);

            Console.WriteLine($"an unrelated drive plugged in and pulled out ({unrelated})");
            var scansBefore = scans;
            var statusBefore = vm.StatusText;
            Subst(unrelated, other);
            Notice(unrelated, arrived: true);
            Settle(2500);
            Subst(unrelated, null);
            Notice(unrelated, arrived: false);
            Settle(2500);
            Check(scans == scansBefore && vm.StatusText == statusBefore, $"no refresh (scans {scans - scansBefore}, status '{vm.StatusText}')");

            Console.WriteLine($"the music drive pulled out ({first})");
            Subst(first, null);
            Notice(first, arrived: false);
            Check(WaitFor(() => Idle() && vm.UnavailableFolders.Count == 1, 8000), $"folder unavailable: {string.Join(", ", vm.UnavailableFolders)}");
            Check(LibraryPaths().Count() == 3 && vm.Tracks.Count == 3, "the songs stay listed");
            Check(vm.StatusText.StartsWith('⚠'), $"status: {vm.StatusText}");

            Console.WriteLine($"back under another letter ({second})");
            Subst(second, stage);
            Notice(second, arrived: true);
            var moved = $@"{second}\Music";
            Check(WaitFor(() => Idle() && vm.MusicFolders.SequenceEqual([moved]) && vm.UnavailableFolders.Count == 0, 10000),
                $"folder re-pointed: {string.Join(", ", vm.MusicFolders)}; unavailable: {vm.UnavailableFolders.Count}");
            Check(AllOn(LibraryPaths(), second), "library paths re-pointed");
            Check(AllOn(vm.Tracks.Select(t => t.FilePath), second), "the song rows on screen re-pointed");
            Check(AllOn(QueuePaths(), second), $"the play queue re-pointed: {string.Join(", ", QueuePaths())}");
            Check(vm.NowPlaying?.FilePath.StartsWith(second, StringComparison.OrdinalIgnoreCase) == true, $"now playing: {vm.NowPlaying?.FilePath}");
            Check(vm.StatusText.StartsWith($@"Music folder moved to {moved}"), $"status: {vm.StatusText}");
            var onDisk = LibraryCache.Load();
            Check(onDisk is not null && onDisk.Folders.SequenceEqual([moved]) && AllOn(onDisk.Tracks.Select(t => t.FilePath), second),
                $"library.json: folders {string.Join(", ", onDisk?.Folders ?? [])}");
            Check(AppSettings.Load().MusicFolders.SequenceEqual([moved]), "settings.json: the new folder");

            // The fixtures last under a second, so the queue may run on past the
            // song Next starts: every song started is recorded.
            var skipped = 0;
            var started = new List<string>();
            engine.TracksSkipped += (_, list) => skipped += list.Count;
            engine.TrackChanged += (_, track) => started.Add(track.FilePath);
            // The two FLACs carry the same tags, so either may come second.
            var expectedNext = QueuePaths().ElementAt(1);
            vm.NextCommand.Execute(null);
            Check(WaitFor(() => started.Count > 0, 5000) && started[0] == expectedNext && expectedNext.StartsWith(second) && skipped == 0,
                $"Next plays the next song from {second}: {string.Join(", ", started)} (skipped {skipped})");
            WaitFor(() => engine.State != PlaybackState.Playing, 5000);
            Check(started.All(p => p.StartsWith(second)) && skipped == 0, $"the queue ran on from {second}: {started.Count} song(s), skipped {skipped}");
            engine.Stop();

            Console.WriteLine($"pulled out and back under the same letter ({second})");
            Subst(second, null);
            Notice(second, arrived: false);
            Check(WaitFor(() => Idle() && vm.UnavailableFolders.Count == 1, 8000), "folder unavailable");
            Subst(second, stage);
            Notice(second, arrived: true);
            Check(WaitFor(() => Idle() && vm.UnavailableFolders.Count == 0, 8000), $"warning cleared; status: {vm.StatusText}");
            Check(vm.MusicFolders.SequenceEqual([moved]) && AllOn(LibraryPaths(), second), "still on the same letter");

            Console.WriteLine($"F5 after a letter change, watcher detached ({third})");
            var watcher = (DriveWatcher)typeof(MainWindow).GetField("_drives", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(main)!;
            watcher.Dispose();
            Subst(second, null);
            Subst(third, stage);
            Settle(2500);
            Check(vm.MusicFolders.SequenceEqual([moved]), "nothing happens before F5");
            var f5 = vm.RefreshCommand.ExecuteAsync(null);
            Check(WaitFor(() => f5.IsCompleted && Idle(), 10000), "F5 finished");
            var f5Folder = $@"{third}\Music";
            Check(vm.MusicFolders.SequenceEqual([f5Folder]) && AllOn(LibraryPaths(), third) && AllOn(QueuePaths(), third) && vm.UnavailableFolders.Count == 0,
                $"F5 re-pointed: {string.Join(", ", vm.MusicFolders)}; status: {vm.StatusText}");

            engine.Stop();
        }
        finally
        {
            main?.Close();
            Settle(300);
            foreach (var letter in mapped.ToList())
                Subst(letter, null);

            foreach (var (path, bytes) in saved)
            {
                if (bytes is not null)
                    File.WriteAllBytes(path, bytes);
                else if (File.Exists(path))
                    File.Delete(path);
            }

            try { Directory.Delete(scratch, recursive: true); }
            catch (IOException) { }
        }

        Console.WriteLine(allOk ? "ALL OK" : "SOMETHING WRONG");
        return allOk ? 0 : 1;
    }
}
