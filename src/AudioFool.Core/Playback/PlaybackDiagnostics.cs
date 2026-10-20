using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using ManagedBass;
using ManagedBass.Wasapi;

namespace AudioFool.Core.Playback;

/// <summary>
/// A plain-text log of how well the device is being kept fed, for tracking down
/// stutters. Nothing here changes what is played.
/// <para>
/// Three things are recorded. Every device pull (<see cref="OutputChain"/>'s Fill)
/// is timed; a slow one is logged with how much of that time the thread actually
/// computed (CPU cycles) and how much the GC had it paused, so a slow disk read
/// (waiting, little CPU), heavy decoding (busy) and a GC pause can be told apart.
/// A gap between pulls longer than usual means the thread was not scheduled at all.
/// A watcher thread samples how much audio the device has queued and logs each
/// time it runs low. And once a minute while playing, a summary line says what
/// normal looked like.
/// </para>
/// </summary>
public static class PlaybackDiagnostics
{
    /// <summary>A pull slower than this is logged.</summary>
    private const double SlowFillMs = 15;

    /// <summary>
    /// A gap this much longer than the usual one is logged. The usual gap depends
    /// on the mode: about 10 ms in shared, a whole 200 ms buffer in exclusive.
    /// </summary>
    private const double LateByMs = 20;

    /// <summary>The watcher logs itself as starved past this.</summary>
    private const double StarvedMs = 60;

    /// <summary>Queued audio below this counts as running low.</summary>
    private const double LowBufferMs = 25;

    private const int WatchIntervalMs = 5;
    private const long MaxLogBytes = 4 * 1024 * 1024;

    /// <summary>Where the log goes. Settable so a probe can write somewhere else.</summary>
    public static string LogPath { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AudioFool", "playback.log");

    private static readonly ConcurrentQueue<string> Pending = new();
    private static readonly object StartGate = new();
    private static Thread? _watcher;
    private static double _cyclesPerMs;

    // The device being watched, set by OutputChain. Only one is open at a time.
    private static volatile DeviceInfo? _device;

    // Written only by the device thread inside Fill: when the last pull began,
    // a running average of the gap between pulls (0 until the first gap), and the
    // GC's total pause time when the last pull ended.
    private static long _lastFillStart;
    private static double _usualGapMs;
    private static TimeSpan? _lastFillGcPause;

    // The minute's figures. Fill writes them; the watcher swaps in a fresh set.
    private static Stats _stats = new();

    // The watcher's last reading of queued audio, for the slow-fill lines.
    private static double _lastBufferMs = -1;

    // Set when the device (re)starts; the watcher ignores a low queue until it
    // has seen the buffer fill once.
    private static volatile bool _restarted;

    private sealed record DeviceInfo(OutputMode Mode, int Rate, int Depth, double BufferMs, int BufferBytes);

    private sealed class Stats
    {
        public int Fills;
        public double GapSum;
        public double GapMax;
        public double FillMax;
        public double BufferMin = double.MaxValue;
        public double GcMs;
        public int SlowFills;
        public int LowBuffers;
    }

    public readonly struct FillToken
    {
        internal readonly long Start;
        internal readonly ulong Cycles;
        internal readonly TimeSpan GcPause;

        internal FillToken(long start, ulong cycles, TimeSpan gcPause)
        {
            Start = start;
            Cycles = cycles;
            GcPause = gcPause;
        }
    }

    /// <summary>Called by a chain once its device is open.</summary>
    internal static void DeviceOpened(OutputMode mode, int rate, int depth, double bufferSeconds, string style)
    {
        EnsureWatcher();
        var bytes = BassWasapi.GetInfo(out var info) ? info.BufferLength : 0;
        _device = new DeviceInfo(mode, rate, depth, bufferSeconds * 1000, bytes);
        _lastFillStart = 0;
        _lastFillGcPause = null;
        _usualGapMs = 0;
        Write($"device opened: {mode} ({style}), {rate} Hz, {(depth > 0 ? $"{depth}-bit" : "depth unknown")}, " +
              $"buffer {bufferSeconds * 1000:0} ms ({bytes} bytes)");
    }

    /// <summary>
    /// Called just before the device starts or resumes. Its buffer starts empty
    /// and the time since the last pull is the pause, so neither is a stall.
    /// </summary>
    internal static void DeviceStarting()
    {
        _lastFillStart = 0;
        _lastFillGcPause = null;
        _restarted = true;
    }

    internal static void DeviceClosed()
    {
        if (_device is null)
            return;
        _device = null;
        Write("device closed");
    }

    /// <summary>
    /// A line from the app, such as a postponed memory clean-up. Starts the
    /// watcher, which writes the file, if nothing has played yet.
    /// </summary>
    public static void Note(string line)
    {
        Write(line);
        EnsureWatcher();
    }

    /// <summary>A track has started, by gapless handover or otherwise.</summary>
    public static void TrackStarted(string path) =>
        Write($"track: {Path.GetExtension(path).TrimStart('.').ToUpperInvariant()}  {path}");

    internal static FillToken FillBegin()
    {
        QueryThreadCycleTime(GetCurrentThread(), out var cycles);
        return new FillToken(Stopwatch.GetTimestamp(), cycles, GC.GetTotalPauseDuration());
    }

    internal static void FillEnd(in FillToken token)
    {
        var end = Stopwatch.GetTimestamp();
        QueryThreadCycleTime(GetCurrentThread(), out var cycles);
        var gcMs = (GC.GetTotalPauseDuration() - token.GcPause).TotalMilliseconds;

        var tookMs = Ms(end - token.Start);
        var gapMs = _lastFillStart == 0 ? 0 : Ms(token.Start - _lastFillStart);
        _lastFillStart = token.Start;

        // A GC that suspends the app between two pulls holds the next one back at
        // the door (the callback is managed code), so it shows in the gap, not in
        // the fill: counted from the end of the previous fill.
        var gapGcMs = _lastFillGcPause is { } previous ? (token.GcPause - previous).TotalMilliseconds : 0;
        _lastFillGcPause = GC.GetTotalPauseDuration();
        gcMs += gapGcMs;

        var stats = Volatile.Read(ref _stats);
        stats.Fills++;
        stats.GapSum += gapMs;
        stats.GapMax = Math.Max(stats.GapMax, gapMs);
        stats.FillMax = Math.Max(stats.FillMax, tookMs);
        stats.GcMs += gcMs;

        var usual = _usualGapMs;
        if (gapMs > 0)
            _usualGapMs = usual == 0 ? gapMs : usual + (gapMs - usual) * 0.02;

        var late = usual > 0 && gapMs > usual + LateByMs;
        if (tookMs < SlowFillMs && !late)
            return;

        stats.SlowFills++;
        var busy = _cyclesPerMs > 0 ? $"busy ~{(cycles - token.Cycles) / _cyclesPerMs:0.0} ms" : "busy unknown";
        var buffer = _lastBufferMs >= 0 ? $"{_lastBufferMs:0} ms queued" : "queue unknown";
        Write($"{(late ? "LATE FILL" : "SLOW FILL")}  took {tookMs:0.0} ms ({busy}), " +
              $"{gapMs:0.0} ms since the previous fill (usually {usual:0.0}), {buffer}, " +
              $"GC paused the app {gcMs:0.0} ms of it");
    }

    private static void EnsureWatcher()
    {
        lock (StartGate)
        {
            if (_watcher is not null)
                return;

            _watcher = new Thread(Watch)
            {
                IsBackground = true,
                Name = "Playback diagnostics",
            };
            _watcher.Start();
        }
    }

    private static void Watch()
    {
        Calibrate();
        Write($"--- AudioFool {Environment.ProcessId} started watching playback " +
              $"(CPU ~{_cyclesPerMs / 1000:0} MHz, Windows {Environment.OSVersion.Version}) ---");

        var nextSummary = DateTime.Now.AddMinutes(1);
        var lowSince = 0L;
        var lowMin = double.MaxValue;
        var filled = false;

        while (true)
        {
            // This thread runs at normal priority, like most of what else is on
            // the machine. If its own short sleep overruns badly, the app as a
            // whole was not getting the CPU; a slow fill at the same moment is
            // then the scheduler, not the disk.
            // A blocking GC suspends this thread too, so its pause time across the
            // sleep says which: AudioFool's own memory clean-up, or the PC.
            var slept = Stopwatch.GetTimestamp();
            var pausedBefore = GC.GetTotalPauseDuration();
            var gen2Before = GC.CollectionCount(2);
            Thread.Sleep(WatchIntervalMs);
            var sleptMs = Ms(Stopwatch.GetTimestamp() - slept);
            if (sleptMs > StarvedMs && _device is not null && BassWasapi.IsStarted)
            {
                var gcMs = (GC.GetTotalPauseDuration() - pausedBefore).TotalMilliseconds;
                var gen2 = GC.CollectionCount(2) - gen2Before;
                Write(gcMs >= (sleptMs - WatchIntervalMs) / 2
                    ? $"GC PAUSE  AudioFool's memory clean-up froze the app: a {WatchIntervalMs} ms sleep took {sleptMs:0} ms, " +
                      $"{gcMs:0} ms of it GC ({gen2} full collection{(gen2 == 1 ? "" : "s")}), {Memory()}"
                    : $"APP STARVED  a {WatchIntervalMs} ms sleep took {sleptMs:0} ms, GC only {gcMs:0} ms of it: " +
                      $"the PC didn't give AudioFool the CPU");
            }

            if (_restarted)
            {
                _restarted = false;
                filled = false;
                lowSince = 0;
                lowMin = double.MaxValue;
            }

            var device = _device;
            if (device is not null && device.BufferBytes > 0 && BassWasapi.IsStarted)
            {
                var queued = BassWasapi.GetData(IntPtr.Zero, (int)DataFlags.Available);
                if (queued >= 0)
                {
                    var ms = queued * device.BufferMs / device.BufferBytes;
                    _lastBufferMs = ms;
                    filled |= ms >= LowBufferMs * 2;

                    var stats = Volatile.Read(ref _stats);
                    if (filled)
                        stats.BufferMin = Math.Min(stats.BufferMin, ms);

                    if (filled && ms < LowBufferMs)
                    {
                        if (lowSince == 0)
                            lowSince = Stopwatch.GetTimestamp();
                        lowMin = Math.Min(lowMin, ms);
                    }
                    else if (lowSince != 0)
                    {
                        stats.LowBuffers++;
                        Write($"BUFFER LOW  fell to {lowMin:0} ms queued, below {LowBufferMs:0} ms for " +
                              $"{Ms(Stopwatch.GetTimestamp() - lowSince):0} ms{(lowMin < 1 ? "  (ran dry: audible gap)" : "")}");
                        lowSince = 0;
                        lowMin = double.MaxValue;
                    }
                }
            }
            else
            {
                _lastBufferMs = -1;
                lowSince = 0;
                lowMin = double.MaxValue;
            }

            if (DateTime.Now >= nextSummary)
            {
                nextSummary = DateTime.Now.AddMinutes(1);
                var done = Interlocked.Exchange(ref _stats, new Stats());
                if (done.Fills > 0)
                {
                    Write($"minute: {done.Fills} fills, gap avg {done.GapSum / done.Fills:0.0} / max {done.GapMax:0.0} ms, " +
                          $"fill max {done.FillMax:0.0} ms, queued min " +
                          $"{(done.BufferMin == double.MaxValue ? "?" : done.BufferMin.ToString("0"))} ms, " +
                          $"GC paused {done.GcMs:0} ms, {done.SlowFills} slow, {done.LowBuffers} low");
                }
            }

            Flush();
        }
    }

    /// <summary>How many CPU cycles this thread runs per millisecond, from a short spin.</summary>
    private static void Calibrate()
    {
        var thread = GetCurrentThread();
        QueryThreadCycleTime(thread, out var c0);
        var t0 = Stopwatch.GetTimestamp();
        while (Ms(Stopwatch.GetTimestamp() - t0) < 30) { }
        QueryThreadCycleTime(thread, out var c1);
        _cyclesPerMs = (c1 - c0) / Ms(Stopwatch.GetTimestamp() - t0);
    }

    private static void Write(string line) =>
        Pending.Enqueue($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {line}");

    private static void Flush()
    {
        if (Pending.IsEmpty)
            return;

        try
        {
            var path = LogPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (File.Exists(path) && new FileInfo(path).Length > MaxLogBytes)
                File.Move(path, path + ".old", overwrite: true);

            using var writer = new StreamWriter(path, append: true);
            while (Pending.TryDequeue(out var line))
                writer.WriteLine(line);
        }
        catch (IOException)
        {
            // Diagnostics only: try again on the next pass.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

    private static string Memory()
    {
        var info = GC.GetGCMemoryInfo();
        return $"heap {info.HeapSizeBytes / 1048576.0:0} MB, committed {info.TotalCommittedBytes / 1048576.0:0} MB, " +
               $"compacted {(info.Compacted ? "yes" : "no")}";
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentThread();

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryThreadCycleTime(IntPtr thread, out ulong cycles);
}
