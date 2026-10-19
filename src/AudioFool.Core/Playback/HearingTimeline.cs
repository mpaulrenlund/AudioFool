using AudioFool.Core.Models;

namespace AudioFool.Core.Playback;

/// <summary>
/// What the device has been handed, block by block, so the app can tell what is
/// coming out of the speakers rather than what the decoder has reached.
/// <para>
/// The device holds about 200 ms of audio it has already been given, so the
/// decode position runs that far ahead of what you hear (session 45). Each pull
/// is appended here with the track it came from and where in that track it
/// started; the frame being heard is <see cref="TotalFrames"/> less what the
/// device still holds, and <see cref="TryLookup"/> turns that frame back into a
/// track and a position.
/// </para>
/// <para>
/// One writer, the device's thread, and any number of readers, without a lock:
/// the device's thread must never wait for the UI. Entries sit in a ring; the
/// count is published after an entry is written, and a reader checks afterwards
/// that nothing it read was overwritten, or a <see cref="Reset"/> happened.
/// </para>
/// </summary>
public sealed class HearingTimeline
{
    /// <summary>
    /// Enough history for the device's whole buffer many times over: 10-20 ms
    /// pulls give several seconds, and even a 200 ms event-driven pull a minute.
    /// </summary>
    private const int Capacity = 512;

    /// <summary>Where the last seek's audio begins, and what it begins with.</summary>
    private sealed record SeekMark(long Frame, Track? Track, double Seconds);

    private readonly long[] _start = new long[Capacity];
    private readonly int[] _frames = new int[Capacity];
    private readonly double[] _seconds = new double[Capacity];
    private readonly Track?[] _tracks = new Track?[Capacity];

    private long _count;
    private long _total;
    private int _generation;
    private int _sampleRate = 44100;
    private SeekMark? _seek;

    /// <summary>Changes on every <see cref="Reset"/>, so a reader can tell its reading spans one.</summary>
    public int Generation => Volatile.Read(ref _generation);

    /// <summary>The rate the frames are counted at: the output's.</summary>
    public int SampleRate => Volatile.Read(ref _sampleRate);

    /// <summary>Every frame handed to the device since the last <see cref="Reset"/>.</summary>
    public long TotalFrames => Volatile.Read(ref _total);

    /// <summary>
    /// Starts again for a new device connection, at its rate. Called while the
    /// device isn't pulling (its connection is being opened).
    /// </summary>
    public void Reset(int sampleRate)
    {
        Interlocked.Increment(ref _generation);
        Volatile.Write(ref _sampleRate, Math.Max(1, sampleRate));
        Volatile.Write(ref _count, 0);
        Volatile.Write(ref _total, 0);
        Volatile.Write(ref _seek, null);
        Array.Clear(_tracks);
    }

    /// <summary>
    /// Records <paramref name="frames"/> handed to the device, from
    /// <paramref name="track"/> starting <paramref name="startSeconds"/> in (null:
    /// silence, nothing playing). Device's thread only.
    /// </summary>
    public void Append(Track? track, double startSeconds, int frames)
    {
        if (frames <= 0)
            return;

        var n = _count;
        var slot = (int)(n % Capacity);
        _start[slot] = _total;
        _frames[slot] = frames;
        _seconds[slot] = startSeconds;
        _tracks[slot] = track;

        Volatile.Write(ref _count, n + 1);
        Volatile.Write(ref _total, _total + frames);
    }

    /// <summary>
    /// Notes a seek the device has just made: everything handed over from now on
    /// starts at <paramref name="targetSeconds"/> in <paramref name="track"/>. Until
    /// the speakers reach it, <see cref="TryLookup"/> gives the target, so the seek
    /// bar doesn't jump back to the old spot for the length of the buffer. A later
    /// seek replaces it. Device's thread only, between two pulls.
    /// </summary>
    public void MarkSeek(Track? track, double targetSeconds) =>
        Volatile.Write(ref _seek, new SeekMark(_total, track, targetSeconds));

    /// <summary>
    /// The track and position at <paramref name="frame"/>, counted as
    /// <see cref="TotalFrames"/> is. A frame past the end gives the end of the last
    /// block; one before a seek not yet heard gives the seek's target. False when
    /// there is nothing to go on: no blocks, a frame older than the history, or a
    /// reading torn by the writer.
    /// </summary>
    public bool TryLookup(long frame, out Track? track, out double seconds)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var generation = Volatile.Read(ref _generation);

            if (Volatile.Read(ref _seek) is { } seek && frame < seek.Frame)
            {
                track = seek.Track;
                seconds = seek.Seconds;
                if (Volatile.Read(ref _generation) == generation)
                    return true;
                continue;
            }

            var count = Volatile.Read(ref _count);
            if (Find(frame, count, out track, out seconds)
                && Volatile.Read(ref _generation) == generation
                && Volatile.Read(ref _count) - count < Capacity / 2)
                return true;
        }

        track = null;
        seconds = 0;
        return false;
    }

    private bool Find(long frame, long count, out Track? track, out double seconds)
    {
        track = null;
        seconds = 0;
        if (count <= 0)
            return false;

        var rate = (double)Volatile.Read(ref _sampleRate);
        var oldest = Math.Max(0, count - Capacity / 2);

        for (var n = count - 1; n >= oldest; n--)
        {
            var slot = (int)(n % Capacity);
            var start = _start[slot];
            if (start > frame)
                continue;

            track = _tracks[slot];
            seconds = _seconds[slot] + Math.Min(frame - start, _frames[slot]) / rate;
            return true;
        }

        return false;
    }
}
