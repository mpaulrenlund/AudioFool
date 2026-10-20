// The projectM preset screens (session 58), in hidden windows that are never shown.
// Nothing reaches the audio device: the sound is made here or decoded on BASS's
// no-sound device, and handed to projectM the way ProjectMHost does (the real
// VisualizerFeed and BeatShaper).
//
//   beat [--workers 3] [--seconds 4] [--limit N] [--pack <folder>] [--work <folder>]
//       Every preset in the pack (less the screened-out) against a 120 BPM drum loop
//       at the default intensity, judged by how much of its picture's change follows
//       the beat (R2, see Fold). Workers run side by side, each preset in a child
//       process that is restarted after a crash. Resumes where it stopped (the work
//       folder, %TEMP%\AudioFoolBeatScreen by default). Writes beat-screen.tsv and
//       no-beat.txt into the pack.
//   cutoff <R2> [--pack <folder>]
//       Rewrites no-beat.txt from beat-screen.tsv with another cutoff.
//   meter [--music <file>] [--intensities 0,1,5,10] [--seconds 8]
//       projectM's own bass/mid/treb, read back through meter.milk (and bass_att etc.
//       through meteratt.milk): the drum loop by default, or 40 s of a track.
//       Intensity 0 hands projectM the sound unshaped.
using System.Diagnostics;
using System.Globalization;
using AudioFool.Core.Analysis;
using AudioFool.Core.Playback;
using AudioFool.Core.Visualizer;

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

return (args.Length > 0 ? args[0] : "") switch
{
    "beat" => BeatScreen.Parent(args),
    "beat-child" => BeatScreen.Child(args),
    "cutoff" => BeatScreen.Cutoff(args),
    "meter" => Meter.Run(args),
    _ => Usage(),
};

static int Usage()
{
    Console.WriteLine("PresetScreen beat [--workers 3] [--seconds 4] [--limit N] [--pack <folder>] [--work <folder>]");
    Console.WriteLine("PresetScreen cutoff <R2> [--pack <folder>]");
    Console.WriteLine("PresetScreen meter [--music <file>] [--intensities 0,1,5,10] [--seconds 8]");
    return 2;
}

static class Common
{
    public static string? Arg(string[] args, string name) =>
        Array.IndexOf(args, name) is var i and >= 0 && i + 1 < args.Length ? args[i + 1] : null;

    /// <summary>The pack beside the installed AudioFool.</summary>
    public static string DefaultPack { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "AudioFool", "visualizer");

    public static PresetLibrary Library(string[] args) =>
        new(Arg(args, "--pack") ?? DefaultPack, PresetLibrary.UserFolderDefault);
}

/// <summary>A drum loop at 120 BPM over a quiet chord: a kick with a beater click on each beat, a hi-hat on each off-beat.</summary>
static class DrumLoop
{
    public const double BeatsPerSecond = 2;

    private static readonly float[] Noise = MakeNoise();

    private static float[] MakeNoise()
    {
        var random = new Random(1);
        var noise = new float[48000];
        for (var i = 0; i < noise.Length; i++)
            noise[i] = (float)(random.NextDouble() - 0.5);
        return noise;
    }

    public static float Sample(long frame, int rate)
    {
        var t = frame / (double)rate;
        var n = Noise[frame % Noise.Length];
        var hiss = n - Noise[(frame + 1) % Noise.Length];
        var v = 0.04 * (Math.Sin(2 * Math.PI * 220 * t) + Math.Sin(2 * Math.PI * 277 * t) + Math.Sin(2 * Math.PI * 330 * t));
        var kick = (t * BeatsPerSecond - Math.Floor(t * BeatsPerSecond)) / BeatsPerSecond;
        v += 0.6 * Math.Exp(-kick * 12) * Math.Sin(2 * Math.PI * (45 * kick + 40 * (1 - Math.Exp(-kick * 30)) / 30));
        v += 0.5 * Math.Exp(-kick * 400) * n;
        var hat = (t * BeatsPerSecond + 0.5 - Math.Floor(t * BeatsPerSecond + 0.5)) / BeatsPerSecond;
        v += 0.25 * Math.Exp(-hat * 60) * hiss;
        return (float)v;
    }

    /// <summary>Where <paramref name="seconds"/> falls in its beat: 0 on the kick, up to 1.</summary>
    public static double Phase(double seconds) => seconds * BeatsPerSecond - Math.Floor(seconds * BeatsPerSecond);
}

/// <summary>projectM in a hidden window, one preset at a time, locked.</summary>
sealed class Rig : IDisposable
{
    public const int Width = 320, Height = 180;

    private readonly IntPtr _window;
    private long _lastFrame = Stopwatch.GetTimestamp();

    private Rig(IntPtr window, GlContext context, ProjectMSession session)
    {
        _window = window;
        Context = context;
        Session = session;
        Session.WatchDirectLoads();
        Session.PresetFailed += (_, message) => Failed = message;
    }

    public GlContext Context { get; }

    public ProjectMSession Session { get; }

    /// <summary>Why the last preset loaded didn't, or null.</summary>
    public string? Failed { get; private set; }

    public static Rig? TryStart(PresetLibrary library, out string? error)
    {
        var window = GlContext.CreateWindow(IntPtr.Zero, Width, Height);
        var context = GlContext.TryCreate(window, out error);
        if (context is null)
            return null;
        var session = ProjectMSession.TryCreate([], [library.TexturesFolder], Width, Height, out error);
        return session is null ? null : new Rig(window, context, session);
    }

    public void Load(string path)
    {
        Failed = null;
        Session.Load(path);
        Session.Locked = true;
    }

    /// <summary>Shows the frame and waits for the next: the compositor's pace, and never faster than ~66 a second.</summary>
    public void Present()
    {
        Context.SwapBuffers();
        GlContext.WaitForCompositor();
        while (Stopwatch.GetTimestamp() - _lastFrame < Stopwatch.Frequency / 66)
            Thread.Sleep(1);
        _lastFrame = Stopwatch.GetTimestamp();
    }

    public void Dispose()
    {
        Session.Dispose();
        Context.Dispose();
        GlContext.DestroyWindow(_window);
    }
}

/// <summary>
/// Hands projectM its sound as ProjectMHost does: a tap of <see cref="Rate"/> kHz
/// stereo playing <c>sample</c> in real time, read through the real
/// <see cref="VisualizerFeed"/>, the frames since the last call ending at now,
/// shaped by a <see cref="BeatShaper"/> (intensity 0: unshaped).
/// </summary>
sealed class Feeder
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Func<long, float> _sample;
    private readonly VisualizerFeed _feed;
    private readonly BeatShaper? _shaper;
    private TimeSpan _last;

    public Feeder(int rate, Func<long, float> sample, int intensity)
    {
        Rate = rate;
        _sample = sample;
        _feed = new VisualizerFeed(() => new TapFormat(rate, 2), Read);
        _shaper = intensity == 0 ? null : new BeatShaper(rate) { Intensity = intensity };
    }

    public int Rate { get; }

    /// <summary>Hands over the frames since the last call; returns the time being heard, in seconds.</summary>
    public double Feed(ProjectMSession session)
    {
        var now = _clock.Elapsed;
        var seconds = Math.Clamp((now - _last).TotalSeconds, 0, 0.25);
        _last = now;
        _feed.Next(seconds, true);
        var frames = Math.Clamp((int)Math.Round(seconds * Math.Max(1, _feed.PcmRate)), 0, _feed.PcmNow);
        if (frames > 0 && _feed.Heard && _feed.Pcm is { } pcm)
        {
            if (_shaper is null)
            {
                session.AddPcm(pcm, _feed.PcmNow - frames, frames);
            }
            else
            {
                _shaper.Shape(pcm, _feed.PcmNow - frames, frames);
                session.AddPcm(_shaper.Output, 0, frames);
            }
        }
        return now.TotalSeconds;
    }

    private bool Read(float[] destination, TapFormat format, int framesAhead)
    {
        var frames = destination.Length / 2;
        var newest = (long)(_clock.Elapsed.TotalSeconds * Rate) + framesAhead;
        for (var i = 0; i < frames; i++)
        {
            var frame = newest - frames + 1 + i;
            destination[i * 2] = destination[i * 2 + 1] = frame < 0 ? 0 : _sample(frame);
        }
        return true;
    }
}

static class BeatScreen
{
    /// <summary>R2 below which a preset counts as not following the beat; ~0.06 is chance with 4 s.</summary>
    public const double DefaultCutoff = 0.25;

    private const int Bins = 10;
    private const double WarmSeconds = 0.5;

    private static string WorkFolder(string[] args) =>
        Common.Arg(args, "--work") ?? Path.Combine(Path.GetTempPath(), "AudioFoolBeatScreen");

    /// <summary>Keeps Windows from idle-sleeping while this thread lives (a closed lid still sleeps).</summary>
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern uint SetThreadExecutionState(uint flags);

    private const uint StayAwake = 0x80000001;   // ES_CONTINUOUS | ES_SYSTEM_REQUIRED

    public static int Parent(string[] args)
    {
        SetThreadExecutionState(StayAwake);
        var library = Common.Library(args);
        var workers = int.Parse(Common.Arg(args, "--workers") ?? "3");
        var seconds = double.Parse(Common.Arg(args, "--seconds") ?? "4");
        var work = WorkFolder(args);
        Directory.CreateDirectory(work);

        var screenedOut = new HashSet<string>(PresetLibrary.ReadList(library.ScreenedOutPath), StringComparer.OrdinalIgnoreCase);
        var list = library.All().Select(library.Relative).Where(p => !screenedOut.Contains(p)).ToList();
        if (Common.Arg(args, "--limit") is { } limit)
        {
            var step = Math.Max(1, list.Count / int.Parse(limit));
            list = list.Where((_, i) => i % step == 0).Take(int.Parse(limit)).ToList();
        }
        Console.WriteLine($"beat screen: {list.Count} presets from {library.PresetsFolder}, {workers} workers, {seconds} s each; work in {work}");

        var slices = Enumerable.Range(0, workers).Select(w => list.Where((_, i) => i % workers == w).ToList()).ToArray();
        var tasks = slices.Select((slice, w) => Task.Run(() => Worker(w, slice, seconds, library.PackFolder, work))).ToArray();
        var clock = Stopwatch.StartNew();
        var startedWith = Done(work, workers);
        while (!Task.WaitAll(tasks, TimeSpan.FromMinutes(1)))
        {
            var done = Done(work, workers);
            var rate = (done - startedWith) / clock.Elapsed.TotalMinutes;
            var left = rate > 0 ? TimeSpan.FromMinutes((list.Count - done) / rate) : TimeSpan.Zero;
            Console.WriteLine($"{DateTime.Now:HH:mm} {done} of {list.Count}, {rate:0} a minute, about {left.TotalHours:0.0} h left");
        }

        var results = Enumerable.Range(0, workers)
            .SelectMany(w => ReadResults(Path.Combine(work, $"results-{w}.tsv")))
            .GroupBy(r => r.Preset, StringComparer.OrdinalIgnoreCase).Select(g => g.Last())
            .OrderBy(r => r.Preset, StringComparer.OrdinalIgnoreCase).ToList();
        var tsv = Path.Combine(library.PackFolder, "beat-screen.tsv");
        File.WriteAllLines(tsv, new[] { "# preset\tR2\tR2 brightness\tR2 motion\tmotion swing %\tmean brightness\tfps\tstatus" }
            .Concat(results.Select(r => r.Line)));
        Console.WriteLine($"done in {clock.Elapsed.TotalHours:0.0} h: {results.Count} results in {tsv}");
        WriteNoBeat(library, results, DefaultCutoff);
        return 0;
    }

    private static int Done(string work, int workers) =>
        Enumerable.Range(0, workers).Sum(w => ReadResults(Path.Combine(work, $"results-{w}.tsv")).Count);

    /// <summary>One worker: its slice through child processes, restarted after a crash.</summary>
    private static void Worker(int w, List<string> slice, double seconds, string pack, string work)
    {
        var listFile = Path.Combine(work, $"list-{w}.txt");
        var results = Path.Combine(work, $"results-{w}.tsv");
        var progress = Path.Combine(work, $"progress-{w}.txt");
        File.WriteAllLines(listFile, slice);
        string? lastCrash = null;
        while (true)
        {
            var done = new HashSet<string>(ReadResults(results).Select(r => r.Preset), StringComparer.OrdinalIgnoreCase);
            if (slice.All(done.Contains))
                return;

            File.WriteAllText(progress, "");
            using var child = Process.Start(new ProcessStartInfo(Environment.ProcessPath!,
                $"beat-child \"{listFile}\" \"{results}\" \"{progress}\" {seconds.ToString(CultureInfo.InvariantCulture)} \"{pack}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            })!;
            child.OutputDataReceived += (_, e) => { if (e.Data is { Length: > 0 } line) Console.WriteLine($"[{w}] {line}"); };
            child.ErrorDataReceived += (_, e) => { };
            child.BeginOutputReadLine();
            child.BeginErrorReadLine();
            child.WaitForExit();
            if (child.ExitCode == 0)
                continue;

            // A crash: the preset it was on counts as one.
            var crashed = File.ReadAllText(progress).Trim();
            if (crashed.Length == 0 || crashed == lastCrash)
            {
                Console.WriteLine($"[{w}] the child stopped (exit {child.ExitCode}) without starting a preset; this worker gives up");
                return;
            }
            lastCrash = crashed;
            File.AppendAllText(results, $"{crashed}\t0\t0\t0\t0\t0\t0\tCRASH {child.ExitCode}{Environment.NewLine}");
            Console.WriteLine($"[{w}] crashed on {crashed}");
        }
    }

    public static int Child(string[] args)
    {
        var (listFile, resultsFile, progress) = (args[1], args[2], args[3]);
        var seconds = double.Parse(args[4], CultureInfo.InvariantCulture);
        var library = new PresetLibrary(args[5], PresetLibrary.UserFolderDefault);
        var done = new HashSet<string>(ReadResults(resultsFile).Select(r => r.Preset), StringComparer.OrdinalIgnoreCase);
        using var rig = Rig.TryStart(library, out var error);
        if (rig is null)
        {
            Console.WriteLine($"projectM didn't start: {error}");
            return 3;
        }

        const int rate = 48000;
        var feeder = new Feeder(rate, frame => DrumLoop.Sample(frame, rate), LiveSpectrum.DefaultIntensity);
        using var results = new StreamWriter(resultsFile, append: true) { AutoFlush = true };
        foreach (var preset in File.ReadAllLines(listFile).Where(p => p.Length > 0 && !done.Contains(p)))
        {
            File.WriteAllText(progress, preset);
            rig.Load(Path.Combine(library.PresetsFolder, preset));

            var start = feeder.Feed(rig.Session);
            while (feeder.Feed(rig.Session) - start < WarmSeconds)
            {
                rig.Session.Render();
                rig.Present();
            }

            var brightness = new List<double>();
            var motion = new List<double>();
            var phase = new List<double>();
            byte[]? previous = null;
            var recordFrom = feeder.Feed(rig.Session);
            double now;
            while ((now = feeder.Feed(rig.Session)) - recordFrom < seconds)
            {
                rig.Session.Render();
                var pixels = GlContext.ReadPixels(Rig.Width, Rig.Height);
                if (previous is not null)
                {
                    var (b, m) = Measure(pixels, previous);
                    brightness.Add(b);
                    motion.Add(m);
                    phase.Add(DrumLoop.Phase(now));
                }
                previous = pixels;
                rig.Present();
            }

            var fps = (brightness.Count + 1) / seconds;
            if (rig.Failed is { } failed)
            {
                results.WriteLine($"{preset}\t0\t0\t0\t0\t0\t{fps:0}\tFAIL {failed.Replace('\t', ' ')}");
                continue;
            }
            var (r2b, _) = Fold(brightness, phase);
            var (r2m, swing) = Fold(motion, phase);
            results.WriteLine($"{preset}\t{Math.Max(r2b, r2m):0.000}\t{r2b:0.000}\t{r2m:0.000}\t{swing:0}\t{brightness.Average():0.0}\t{fps:0}\tOK");
        }
        return 0;
    }

    /// <summary>A frame's mean brightness (0–255) and its mean change from the frame before, on every 4th pixel.</summary>
    private static (double Brightness, double Motion) Measure(byte[] pixels, byte[] previous)
    {
        double sum = 0, change = 0;
        var count = 0;
        for (var y = 0; y < Rig.Height; y += 4)
            for (var x = 0; x < Rig.Width; x += 4)
            {
                var i = (y * Rig.Width + x) * 4;
                var now = (pixels[i] + pixels[i + 1] + pixels[i + 2]) / 3.0;
                sum += now;
                change += Math.Abs(now - (previous[i] + previous[i + 1] + previous[i + 2]) / 3.0);
                count++;
            }
        return (sum / count, change / count);
    }

    /// <summary>
    /// How much of a measure's change follows the beat: the frames grouped by where
    /// they fall in the beat (tenths), and the share of the variance between the
    /// groups' means (R2; with N frames, about 9/N by chance). Also the swing between
    /// the highest and lowest group, as % of the mean.
    /// </summary>
    public static (double R2, double Swing) Fold(IReadOnlyList<double> values, IReadOnlyList<double> phase)
    {
        if (values.Count < Bins * 2)
            return (0, 0);
        var mean = values.Average();
        var total = values.Sum(v => (v - mean) * (v - mean));
        var sums = new double[Bins];
        var counts = new int[Bins];
        for (var i = 0; i < values.Count; i++)
        {
            var b = Math.Min(Bins - 1, (int)(phase[i] * Bins));
            sums[b] += values[i];
            counts[b]++;
        }
        var between = 0.0;
        var means = new List<double>();
        for (var b = 0; b < Bins; b++)
        {
            if (counts[b] == 0)
                continue;
            var m = sums[b] / counts[b];
            means.Add(m);
            between += counts[b] * (m - mean) * (m - mean);
        }
        return (total > 0 ? between / total : 0, mean > 0 ? 100 * (means.Max() - means.Min()) / mean : 0);
    }

    public sealed record Result(string Preset, double R2, string Status, string Line);

    public static List<Result> ReadResults(string path)
    {
        if (!File.Exists(path))
            return [];

        // Shared: a worker's child may be writing it (the progress count reads it every minute).
        using var reader = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
        var results = new List<Result>();
        while (reader.ReadLine() is { } line)
        {
            var cells = line.Split('\t');
            if (line.StartsWith('#') || cells.Length < 8 || !line.EndsWith("\tOK") && !cells[7].StartsWith("FAIL") && !cells[7].StartsWith("CRASH"))
                continue;
            if (!double.TryParse(cells[1], CultureInfo.InvariantCulture, out var r2))
                continue;
            results.Add(new Result(cells[0], r2, cells[7], line));
        }
        return results;
    }

    public static int Cutoff(string[] args)
    {
        var library = Common.Library(args);
        var results = ReadResults(Path.Combine(library.PackFolder, "beat-screen.tsv"));
        if (results.Count == 0)
        {
            Console.WriteLine("no beat-screen.tsv in the pack: run the beat screen first");
            return 1;
        }
        WriteNoBeat(library, results, double.Parse(args[1], CultureInfo.InvariantCulture));
        return 0;
    }

    private static void WriteNoBeat(PresetLibrary library, List<Result> results, double cutoff)
    {
        var below = results.Where(r => r.Status != "OK" || r.R2 < cutoff).ToList();
        File.WriteAllLines(library.NoBeatPath,
            new[] { $"# found by the beat screen, {DateTime.Now:yyyy-MM-dd HH:mm}: presets whose picture follows a 120 BPM drum loop less than R2 {cutoff:0.00}, or that failed ({below.Count} of {results.Count})" }
                .Concat(below.Select(r => $"{r.Preset}\t{(r.Status == "OK" ? $"R2 {r.R2:0.000}" : r.Status)}")));
        Console.WriteLine($"no-beat.txt: {below.Count} of {results.Count} left out at R2 < {cutoff:0.00}; {results.Count - below.Count} keep playing");
        foreach (var edge in new[] { 0.1, 0.15, 0.2, 0.25, 0.3, 0.4, 0.5 })
            Console.WriteLine($"  R2 >= {edge:0.00}: {results.Count(r => r.Status == "OK" && r.R2 >= edge)}");
        foreach (var group in results.Where(r => r.Status == "OK").GroupBy(r => r.Preset.Split('\\')[0]).OrderBy(g => g.Key))
            Console.WriteLine($"  {group.Key}: {group.Count(r => r.R2 >= cutoff)} of {group.Count()} follow the beat");
    }
}

static class Meter
{
    public static int Run(string[] args)
    {
        var library = Common.Library(args);
        var seconds = double.Parse(Common.Arg(args, "--seconds") ?? "8");
        var intensities = (Common.Arg(args, "--intensities") ?? "0,5").Split(',').Select(int.Parse).ToArray();

        var rate = 48000;
        Func<long, float> sample = frame => DrumLoop.Sample(frame, 48000);
        var what = "the drum loop";
        if (Common.Arg(args, "--music") is { } file)
        {
            var (music, musicRate) = Decode(file);
            rate = musicRate;
            sample = frame => music[(int)(frame % (music.Length / 2)) * 2];
            what = Path.GetFileName(file);
        }

        using var rig = Rig.TryStart(library, out var error);
        if (rig is null)
        {
            Console.WriteLine($"projectM didn't start: {error}");
            return 3;
        }
        Console.WriteLine($"libprojectM {ProjectMSession.LibraryVersion()} on {rig.Context.Renderer}; {what}");

        foreach (var intensity in intensities)
            foreach (var name in new[] { "meter", "meteratt" })
            {
                var feeder = new Feeder(rate, sample, intensity);
                rig.Load(Path.Combine(AppContext.BaseDirectory, name + ".milk"));
                var start = feeder.Feed(rig.Session);
                var rows = new List<(double Phase, double Bass, double Mid, double Treb)>();
                double now;
                while ((now = feeder.Feed(rig.Session)) - start < seconds + 1)
                {
                    rig.Session.Render();
                    if (now - start >= 1)
                    {
                        // The meters paint the border bass, mid, treb x 0.2 as red, green, blue.
                        var pixels = GlContext.ReadPixels(Rig.Width, Rig.Height);
                        var i = (Rig.Height / 2 * Rig.Width + 2) * 4;
                        rows.Add((DrumLoop.Phase(now), pixels[i] * 5 / 255.0, pixels[i + 1] * 5 / 255.0, pixels[i + 2] * 5 / 255.0));
                    }
                    rig.Present();
                }

                Console.WriteLine($"intensity {(intensity == 0 ? "0 (unshaped)" : intensity)}, {(name == "meter" ? "bass mid treb" : "bass_att mid_att treb_att")}{(rig.Failed is null ? "" : $", LOAD FAILED: {rig.Failed}")}, {rows.Count} frames");
                Console.WriteLine($"    bass {Spread(rows.Select(r => r.Bass))}");
                Console.WriteLine($"    mid  {Spread(rows.Select(r => r.Mid))}");
                Console.WriteLine($"    treb {Spread(rows.Select(r => r.Treb))}");
                if (Common.Arg(args, "--music") is null)
                    Console.WriteLine("    bass by tenth of a beat: " + string.Join(" ", Enumerable.Range(0, 10)
                        .Select(b => rows.Where(r => (int)(r.Phase * 10) == b).Select(r => r.Bass).DefaultIfEmpty(0).Average().ToString("0.00"))));
            }
        return 0;
    }

    private static string Spread(IEnumerable<double> values)
    {
        var a = values.Order().ToArray();
        return a.Length == 0
            ? "(nothing)"
            : $"p10 {a[a.Length / 10]:0.00}  median {a[a.Length / 2]:0.00}  p90 {a[a.Length * 9 / 10]:0.00}  max {a[^1]:0.00}  (5 = off the scale)";
    }

    /// <summary>40 s of a track from 60 s in, as mono-in-stereo floats, on BASS's no-sound device.</summary>
    private static (float[] Samples, int Rate) Decode(string file)
    {
        ManagedBass.Bass.Init(0);
        foreach (var plugin in new[] { "bassflac.dll", "bassdsd.dll", "bassape.dll", "basswv.dll", "bassopus.dll" })
            ManagedBass.Bass.PluginLoad(Path.Combine(AppContext.BaseDirectory, plugin));
        var stream = ManagedBass.Bass.CreateStream(file, 0, 0, ManagedBass.BassFlags.Decode | ManagedBass.BassFlags.Float);
        if (stream == 0)
            throw new InvalidOperationException($"BASS couldn't open {file}: {ManagedBass.Bass.LastError}");
        var info = ManagedBass.Bass.ChannelGetInfo(stream);
        ManagedBass.Bass.ChannelSetPosition(stream, ManagedBass.Bass.ChannelSeconds2Bytes(stream, 60));
        var buffer = new float[info.Frequency * 40 * info.Channels];
        var bytes = ManagedBass.Bass.ChannelGetData(stream, buffer, buffer.Length * 4 | (int)ManagedBass.DataFlags.Float);
        var frames = Math.Max(0, bytes) / 4 / info.Channels;
        var mono = new float[frames * 2];
        for (var f = 0; f < frames; f++)
        {
            var sum = 0f;
            for (var c = 0; c < info.Channels; c++)
                sum += buffer[f * info.Channels + c];
            mono[f * 2] = mono[f * 2 + 1] = sum / info.Channels;
        }
        ManagedBass.Bass.StreamFree(stream);
        return (mono, info.Frequency);
    }
}
