using System.IO;
using System.Windows;
using System.Windows.Controls;
using AudioFool;
using AudioFool.Core.Models;
using AudioFool.Core.Playback;
using AudioFool.ViewModels;

namespace ThemeLab;

/// <summary>
/// --window queueend (session 72): the end of a queue through the real engine and
/// the view model, shared mode at 5% volume, on silent WAVs in a scratch folder
/// with a cover.jpg. The now-playing bar must go back to the queue's first song:
/// 0:00, its length, its cover, stopped, the device let go. Play starts the queue
/// from there; with shuffle on, still from the first song in queue order. Renders
/// &lt;out&gt;-end.png. "ALL OK".
/// </summary>
internal static partial class Program
{
    private static int RunQueueEnd(MainWindow main, MainViewModel vm, AudioEngine engine, BassRuntime runtime,
        string outPath, double w, double h, double scale)
    {
        // Never the real playback.log (see HANDOFF, Claude's shells and AppData).
        PlaybackDiagnostics.LogPath = Path.Combine(Path.GetTempPath(), "themelab-queueend-" + Guid.NewGuid().ToString("N")[..8] + ".log");
        var ready = runtime.Initialise();
        Console.WriteLine($"init {ready} {runtime.InitError}");

        var dir = Path.Combine(Path.GetTempPath(), "themelab-queueend-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        Track Wav(string name, int seconds) =>
            AudioFool.Core.Library.TagReader.Read(WriteSilentWav(Path.Combine(dir, name + ".wav"), 44100, seconds));

        // A plain blue 300 px cover beside the songs.
        var blue = new System.Windows.Media.Imaging.WriteableBitmap(300, 300, 96, 96, System.Windows.Media.PixelFormats.Bgr32, null);
        blue.WritePixels(new Int32Rect(0, 0, 300, 300), Enumerable.Repeat(unchecked((int)0xFF2040E0), 300 * 300).ToArray(), 300 * 4, 0);
        var encoder = new System.Windows.Media.Imaging.JpegBitmapEncoder { QualityLevel = 95 };
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(blue));
        using (var file = File.Create(Path.Combine(dir, "cover.jpg")))
            encoder.Save(file);

        var allOk = true;
        void Check(bool ok, string what)
        {
            allOk &= ok;
            Console.WriteLine($"  {(ok ? "OK" : "WRONG")}: {what}");
        }

        void WaitFor(Func<bool> done, int ms)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (!done() && clock.ElapsedMilliseconds < ms)
                Settle(25);
            Settle(300);
        }

        string Text(string name) => ((TextBlock)main.FindName(name)).Text;
        string Name(Track? t) => Path.GetFileNameWithoutExtension(t?.FilePath ?? "(none)");

        try
        {
            engine.OutputMode = OutputMode.Shared;
            engine.Volume = 0.05;
            engine.Repeat = RepeatMode.Off;
            engine.Shuffle = false;

            var queue = new List<Track> { Wav("01 First", 3), Wav("02 Second", 1), Wav("03 Third", 1) };

            Console.WriteLine("1. played from the second song to the end");
            engine.Play(queue, 1);
            WaitFor(() => engine.State == PlaybackState.Playing, 3000);
            Check(Name(vm.NowPlaying) == "02 Second", $"playing '{Name(vm.NowPlaying)}'");
            WaitFor(() => engine.State == PlaybackState.Stopped, 8000);
            WaitFor(() => vm.NowPlayingArt is not null && Name(vm.NowPlaying) == "01 First", 3000);

            Check(engine.State == PlaybackState.Stopped && !vm.IsPlaying, $"stopped: engine {engine.State}, IsPlaying {vm.IsPlaying}");
            Check(!engine.HasOutput, "device let go");
            Check(Name(vm.NowPlaying) == "01 First", $"now playing '{Name(vm.NowPlaying)}'");
            // The WAVs carry no title tag, so the bar shows the file's name.
            Check(Text("NowPlayingTitle") == "01 First", $"title shown '{Text("NowPlayingTitle")}'");
            Check(Text("PositionText") == "0:00", $"position '{Text("PositionText")}'");
            Check(Text("DurationText") == "0:03", $"length '{Text("DurationText")}'");
            var px = new byte[4];
            if (vm.NowPlayingArt is { } art)
                new System.Windows.Media.Imaging.FormatConvertedBitmap(art, System.Windows.Media.PixelFormats.Bgr32, null, 0)
                    .CopyPixels(new Int32Rect(art.PixelWidth / 2, art.PixelHeight / 2, 1, 1), px, 4, 0);
            Check(px[0] > 180 && px[2] < 80, $"cover {(vm.NowPlayingArt is { } a ? a.PixelWidth + " px" : "none")}, centre R{px[2]} G{px[1]} B{px[0]}");
            Save(main, Path.ChangeExtension(outPath, null) + "-end.png", w, h, scale);

            Console.WriteLine("2. Play starts the queue from the first song");
            vm.TogglePlayCommand.Execute(null);
            WaitFor(() => engine.State == PlaybackState.Playing, 3000);
            Check(engine.State == PlaybackState.Playing, $"engine {engine.State}");
            Check(Name(engine.CurrentTrack) == "01 First", $"engine on '{Name(engine.CurrentTrack)}'");
            Check(engine.Queue.Count == 3, $"queue of {engine.Queue.Count}");
            engine.Stop();
            Settle(300);

            Console.WriteLine("3. shuffle on: still back to the first song in queue order");
            engine.Shuffle = true;
            engine.Play(queue, 2);
            WaitFor(() => engine.State == PlaybackState.Playing, 3000);
            WaitFor(() => engine.State == PlaybackState.Stopped, 10000);
            WaitFor(() => Name(vm.NowPlaying) == "01 First", 3000);
            Check(engine.State == PlaybackState.Stopped, $"engine {engine.State}");
            Check(Name(vm.NowPlaying) == "01 First", $"now playing '{Name(vm.NowPlaying)}'");
            engine.Shuffle = false;

            Console.WriteLine("4. a one-song queue");
            var lone = Wav("Lone", 1);
            engine.Play([lone], 0);
            WaitFor(() => engine.State == PlaybackState.Playing, 3000);
            WaitFor(() => engine.State == PlaybackState.Stopped, 6000);
            Settle(300);
            Check(Name(vm.NowPlaying) == "Lone", $"now playing '{Name(vm.NowPlaying)}'");
            Check(Text("PositionText") == "0:00", $"position '{Text("PositionText")}'");
        }
        finally
        {
            engine.Stop();
            Settle(200);
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }

        Console.WriteLine(allOk ? "ALL OK" : "FAILURES");
        return allOk ? 0 : 1;
    }
}
