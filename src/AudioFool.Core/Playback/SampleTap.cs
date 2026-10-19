using System.Runtime.InteropServices;

namespace AudioFool.Core.Playback;

/// <summary>The layout of what <see cref="SampleTap"/> holds: float frames, interleaved.</summary>
public readonly record struct TapFormat(int SampleRate, int Channels);

/// <summary>
/// A copy of what the device is given, for the visualizer: the last second of it,
/// frame by frame, numbered as <see cref="HearingTimeline"/> numbers them, so the
/// frames being heard now can be read back (<see cref="AudioEngine.TryReadHeard"/>).
/// <para>
/// It only ever reads the device's block, so what reaches the DAC is untouched;
/// the copy is taken before the volume, so turning it down doesn't shrink the
/// picture. Off until the visualizer turns it on, and then nothing but a copy into
/// memory set aside in advance happens on the device's thread: no allocation, no
/// lock, no conversion. A DoP block is copied as DoP; making sense of it is the
/// reader's job.
/// </para>
/// <para>
/// One writer, the device's thread, and readers on any thread, without a lock: the
/// newest frame is published after its block is copied, and a reader checks
/// afterwards that the writer hasn't come round to the frames it copied.
/// </para>
/// </summary>
public sealed class SampleTap
{
    private sealed class Ring(TapFormat format)
    {
        public readonly TapFormat Format = format;

        /// <summary>One second of frames.</summary>
        public readonly int Capacity = Math.Max(1, format.SampleRate);

        public readonly float[] Samples = new float[Math.Max(1, format.SampleRate) * Math.Max(1, format.Channels)];

        /// <summary>The first frame this ring was given, and the frame after the newest.</summary>
        public long First = -1;
        public long End = -1;

        /// <summary>
        /// How far back a reader may start: the newest three quarters of the ring,
        /// so a block being written (up to a quarter: 250 ms, more than the longest
        /// pull) can't reach frames a reader is copying.
        /// </summary>
        public long Oldest(long end) => Math.Max(First, end - Capacity * 3L / 4);
    }

    private readonly object _switch = new();
    private Ring? _ring;
    private bool _enabled;
    private TapFormat _format = new(44100, 2);

    /// <summary>Whether blocks are being copied.</summary>
    public bool IsEnabled => Volatile.Read(ref _enabled);

    /// <summary>The layout being copied, or null while off.</summary>
    public TapFormat? Format => Volatile.Read(ref _ring)?.Format;

    /// <summary>The frame after the newest one copied; -1 while there is none.</summary>
    public long EndFrame => Volatile.Read(ref _ring) is { } ring ? Volatile.Read(ref ring.End) : -1;

    /// <summary>
    /// Turns copying on (a second of memory, set aside now) or off (let go). Any
    /// thread but the device's.
    /// </summary>
    public void Enable(bool on)
    {
        lock (_switch)
        {
            if (on == _enabled)
                return;

            _enabled = on;
            Volatile.Write(ref _ring, on ? new Ring(_format) : null);
        }
    }

    /// <summary>
    /// A new device connection: its layout, and an empty history. Called while the
    /// device isn't pulling, as <see cref="HearingTimeline.Reset"/> is.
    /// </summary>
    public void Reset(int sampleRate, int channels)
    {
        lock (_switch)
        {
            _format = new TapFormat(Math.Max(1, sampleRate), Math.Max(1, channels));
            if (_enabled)
                Volatile.Write(ref _ring, new Ring(_format));
        }
    }

    /// <summary>
    /// Copies <paramref name="frames"/> float frames from <paramref name="samples"/>,
    /// the first of them numbered <paramref name="firstFrame"/>. Blocks come in
    /// order; one that doesn't follow on (the first after turning on) starts the
    /// history again from itself. Device's thread only.
    /// </summary>
    public void Write(long firstFrame, IntPtr samples, int frames)
    {
        var ring = Volatile.Read(ref _ring);
        if (ring is null || frames <= 0)
            return;

        if (ring.End != firstFrame)
        {
            Volatile.Write(ref ring.End, -1);
            Volatile.Write(ref ring.First, firstFrame);
        }

        // A block longer than the ring: only its last second can be held.
        var channels = ring.Format.Channels;
        var count = Math.Min(frames, ring.Capacity);
        var skip = frames - count;
        var at = (int)((firstFrame + skip) % ring.Capacity);
        var head = Math.Min(count, ring.Capacity - at);

        Marshal.Copy(samples + skip * channels * sizeof(float), ring.Samples, at * channels, head * channels);
        if (count > head)
            Marshal.Copy(samples + (skip + head) * channels * sizeof(float), ring.Samples, 0, (count - head) * channels);

        Volatile.Write(ref ring.End, firstFrame + frames);
    }

    /// <summary>
    /// Copies the frames that end just before <paramref name="endFrame"/> into
    /// <paramref name="destination"/>, as many as it holds (its length over the
    /// channel count), interleaved. False when they aren't all here: not copied
    /// yet, too old, more than half a second, the tap off, or its layout no longer
    /// <paramref name="format"/>.
    /// </summary>
    public bool TryRead(long endFrame, float[] destination, TapFormat format)
    {
        var ring = Volatile.Read(ref _ring);
        if (ring is null || ring.Format != format)
            return false;

        var channels = format.Channels;
        var frames = destination.Length / channels;
        var from = endFrame - frames;
        var end = Volatile.Read(ref ring.End);
        if (frames <= 0 || frames > ring.Capacity / 2 || end < 0 || endFrame > end || from < ring.Oldest(end))
            return false;

        var at = (int)(from % ring.Capacity);
        var head = Math.Min(frames, ring.Capacity - at);
        Array.Copy(ring.Samples, at * channels, destination, 0, head * channels);
        if (frames > head)
            Array.Copy(ring.Samples, 0, destination, head * channels, (frames - head) * channels);

        // The writer may have come round while this copied.
        var after = Volatile.Read(ref ring.End);
        return after >= 0 && from >= ring.Oldest(after) && ReferenceEquals(ring, Volatile.Read(ref _ring));
    }
}
