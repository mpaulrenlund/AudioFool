using AudioFool.Core.Library;
using ManagedBass;
using ManagedBass.Dsd;

namespace AudioFool.Core.Analysis;

/// <summary>
/// The loudness of a track across its length, for the seekbar's waveform: one
/// RMS level per column, scaled so the loudest column is 1.
/// <para>
/// RMS rather than peak, because the point is quiet against loud: a modern
/// master peaks near full scale nearly everywhere, so its peaks draw a solid
/// brick. The scale is linear, so a quiet passage really looks quiet.
/// </para>
/// </summary>
public static class Waveform
{
    /// <summary>Enough for about two pixels a column on the widest seekbar.</summary>
    public const int Columns = 600;

    /// <summary>
    /// The rate DSD is converted to: plenty for loudness, and much quicker than
    /// <see cref="TrackAnalyzer.DsdAnalysisRate"/>.
    /// </summary>
    public const int DsdRate = 44100;

    /// <summary>
    /// Decodes the whole file on its own decode-only stream, so it can run beside
    /// playback. Null when the file isn't there or can't be decoded: the seekbar
    /// then just stays plain. BASS must already be initialised.
    /// </summary>
    public static float[]? Read(string path, CancellationToken cancel = default)
    {
        if (!File.Exists(path))
            return null;

        var stream = Open(path);
        if (stream == 0)
            return null;

        try
        {
            if (!Bass.ChannelGetInfo(stream, out var info) || info.Frequency <= 0 || info.Channels <= 0)
                return null;

            var bytes = Bass.ChannelGetLength(stream);
            var frames = bytes > 0 ? bytes / (sizeof(float) * info.Channels) : 0;

            var levels = new WaveformLevels(info.Channels, frames, Columns);
            var buffer = new float[16384 * info.Channels];

            while (true)
            {
                cancel.ThrowIfCancellationRequested();

                var got = Bass.ChannelGetData(stream, buffer, buffer.Length * sizeof(float));
                if (got <= 0)
                    break;

                levels.Add(buffer.AsSpan(0, got / sizeof(float)));
            }

            return levels.Finish();
        }
        finally
        {
            if (AudioFormats.IsModule(path))
                Bass.MusicFree(stream);
            else
                Bass.StreamFree(stream);
        }
    }

    private static int Open(string path)
    {
        var flags = BassFlags.Decode | BassFlags.Float;

        if (AudioFormats.IsModule(path))
            return Bass.MusicLoad(path, 0, 0, flags | BassFlags.Prescan, 0);

        if (AudioFormats.IsDsd(path))
        {
            var dsd = BassDsd.CreateStream(path, 0, 0, flags, DsdRate);
            return dsd != 0 ? dsd : BassDsd.CreateStream(path, 0, 0, flags, TrackAnalyzer.DsdAnalysisRate);
        }

        // No prescan: a VBR MP3's estimated length only decides the block size,
        // and the blocks are counted, not trusted, at the end.
        return Bass.CreateStream(path, 0, 0, flags);
    }
}

/// <summary>
/// Turns interleaved samples into <see cref="Waveform"/> levels. Samples are
/// summed into small blocks as they arrive, and the blocks are shared out among
/// the columns at the end, so a length that was only an estimate (or unknown)
/// still fills every column evenly.
/// </summary>
public sealed class WaveformLevels
{
    private const int UnknownLengthBlockFrames = 2048;

    private readonly int _channels;
    private readonly int _columns;
    private readonly long _blockFrames;
    private readonly List<double> _blockSums = [];
    private readonly List<long> _blockCounts = [];

    private double _sum;
    private long _count;

    /// <param name="totalFrames">The expected length, or 0 if unknown.</param>
    public WaveformLevels(int channels, long totalFrames, int columns)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(channels, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(columns, 1);

        _channels = channels;
        _columns = columns;

        // Four blocks a column, so an estimate a little short still has slack.
        _blockFrames = totalFrames > 0
            ? Math.Max(1, totalFrames / (columns * 4L))
            : UnknownLengthBlockFrames;
    }

    public void Add(ReadOnlySpan<float> interleaved)
    {
        var blockSamples = _blockFrames * _channels;

        foreach (var sample in interleaved)
        {
            _sum += (double)sample * sample;
            if (++_count == blockSamples)
                CloseBlock();
        }
    }

    /// <summary>
    /// One level per column, 0 to 1. Fewer than the columns asked for when the
    /// track was shorter than that many blocks; empty when nothing was read.
    /// </summary>
    public float[] Finish()
    {
        if (_count > 0)
            CloseBlock();

        var blocks = _blockSums.Count;
        var columns = Math.Min(_columns, blocks);
        var levels = new float[columns];

        for (var c = 0; c < columns; c++)
        {
            var first = (int)((long)c * blocks / columns);
            var end = (int)((long)(c + 1) * blocks / columns);

            double sum = 0;
            long count = 0;
            for (var b = first; b < end; b++)
            {
                sum += _blockSums[b];
                count += _blockCounts[b];
            }

            levels[c] = count == 0 ? 0 : (float)Math.Sqrt(sum / count);
        }

        var loudest = levels.Length == 0 ? 0 : levels.Max();
        if (loudest > 0)
        {
            for (var c = 0; c < levels.Length; c++)
                levels[c] /= loudest;
        }

        return levels;
    }

    private void CloseBlock()
    {
        _blockSums.Add(_sum);
        _blockCounts.Add(_count);
        _sum = 0;
        _count = 0;
    }
}
