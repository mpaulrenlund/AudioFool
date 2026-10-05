using AudioFool.Core.Library;
using ManagedBass;
using ManagedBass.Dsd;

namespace AudioFool.Core.Analysis;

/// <summary>
/// Decodes a whole file and runs it through a <see cref="SpectrumAccumulator"/>.
/// <para>
/// Opens its own decode-only stream, so it never touches the output device or
/// the engine's streams and can run beside playback. BASS must already be
/// initialised (the app's <c>BassRuntime</c>, with its format plugins loaded).
/// </para>
/// </summary>
public static class TrackAnalyzer
{
    /// <summary>
    /// The rate DSD is converted to for analysis: DSD64 ÷ 16, DSD128 ÷ 32. High
    /// enough to show the noise DSD pushes above the audio band, which is where
    /// a DSD file shows what it was made from.
    /// </summary>
    public const int DsdAnalysisRate = 176400;

    public static SpectrumAnalysis Analyze(string path, IProgress<double>? progress = null,
        CancellationToken cancel = default, AnalysisSampling? sampling = null)
    {
        if (!File.Exists(path))
            throw new AnalysisException("The file isn't there. Is the drive connected?");

        var stream = Open(path, sampled: sampling is not null);
        if (stream == 0)
            throw new AnalysisException($"The file couldn't be decoded ({Bass.LastError}).");

        try
        {
            if (!Bass.ChannelGetInfo(stream, out var info) || info.Frequency <= 0 || info.Channels <= 0)
                throw new AnalysisException($"The file's format couldn't be read ({Bass.LastError}).");

            var bytes = Bass.ChannelGetLength(stream);
            var frames = bytes > 0 ? bytes / (sizeof(float) * info.Channels) : 0;

            // Only a lossless PCM source has bits worth counting; a lossy decoder
            // invents the low bits, and DSD has one.
            var checkBits = !AudioFormats.IsLossy(path) && !AudioFormats.IsDsd(path)
                && !AudioFormats.IsModule(path) && info.OriginalResolution > 16;

            var sliceFrames = sampling is null ? 0 : (long)(sampling.SliceLength.TotalSeconds * info.Frequency);
            var slices = sampling is null || frames <= 0 ? null : sampling.StartsFor(frames, sliceFrames);

            var accumulator = slices is null
                ? new SpectrumAccumulator(info.Frequency, info.Channels, frames, checkBits: checkBits)
                : new SpectrumAccumulator(info.Frequency, info.Channels, slices.Count * sliceFrames,
                    AnalysisSampling.PictureColumns, AnalysisSampling.PictureRows, checkBits);

            var buffer = new float[16384 * info.Channels];

            if (slices is null)
            {
                Read(stream, info.Channels, accumulator, buffer, long.MaxValue, frames, progress, cancel);
            }
            else
            {
                foreach (var start in slices)
                {
                    cancel.ThrowIfCancellationRequested();
                    Bass.ChannelSetPosition(stream, start * sizeof(float) * info.Channels);
                    accumulator.Restart();
                    Read(stream, info.Channels, accumulator, buffer, sliceFrames, 0, null, cancel);
                }
            }

            return accumulator.Finish();
        }
        finally
        {
            if (AudioFormats.IsModule(path))
                Bass.MusicFree(stream);
            else
                Bass.StreamFree(stream);
        }
    }

    /// <summary>Feeds up to <paramref name="limit"/> frames, reporting progress against <paramref name="total"/>.</summary>
    private static void Read(int stream, int channels, SpectrumAccumulator accumulator, float[] buffer,
        long limit, long total, IProgress<double>? progress, CancellationToken cancel)
    {
        long read = 0;
        var reported = -1;

        while (read < limit)
        {
            cancel.ThrowIfCancellationRequested();

            var want = (int)Math.Min(buffer.Length / channels, limit - read) * channels;
            var got = Bass.ChannelGetData(stream, buffer, want * sizeof(float));
            if (got <= 0)
                break;

            var samples = got / sizeof(float);
            accumulator.Add(buffer.AsSpan(0, samples));
            read += samples / channels;

            if (progress is not null && total > 0)
            {
                var percent = (int)Math.Min(100, read * 100 / total);
                if (percent != reported)
                {
                    reported = percent;
                    progress.Report(percent / 100.0);
                }
            }
        }
    }

    private static int Open(string path, bool sampled)
    {
        var flags = BassFlags.Decode | BassFlags.Float;

        if (AudioFormats.IsModule(path))
            return Bass.MusicLoad(path, 0, 0, flags | BassFlags.Prescan, 0);

        if (AudioFormats.IsDsd(path))
            return BassDsd.CreateStream(path, 0, 0, flags, DsdAnalysisRate);

        // Prescan gives a VBR MP3 an exact length, which the progress needs. A
        // sampled read skips it: it reads the whole file, and a slice landing a
        // second off doesn't matter.
        if (!sampled && Path.GetExtension(path).Equals(".mp3", StringComparison.OrdinalIgnoreCase))
            flags |= BassFlags.Prescan;

        return Bass.CreateStream(path, 0, 0, flags);
    }
}

/// <summary>
/// Reading a few slices of a track instead of all of it, for checking a whole
/// library: a cutoff, an upsampling gap or padded bits shows in any stretch of
/// music. The slices are spread evenly, clear of the ends, where fades and
/// silence would tell nothing.
/// </summary>
public sealed record AnalysisSampling(int Slices, TimeSpan SliceLength)
{
    /// <summary>Three 10-second slices: about a ninth of a typical track.</summary>
    public static AnalysisSampling Library { get; } = new(3, TimeSpan.FromSeconds(10));

    /// <summary>Nobody sees a sampled picture, so it's kept tiny.</summary>
    public const int PictureColumns = 16, PictureRows = 16;

    /// <summary>
    /// Where each slice starts, in frames; null when the track is so short that
    /// reading all of it costs little more.
    /// </summary>
    public IReadOnlyList<long>? StartsFor(long totalFrames, long sliceFrames)
    {
        if (totalFrames <= sliceFrames * Slices * 2)
            return null;

        return [.. Enumerable.Range(1, Slices)
            .Select(i => (totalFrames * i / (Slices + 1)) - (sliceFrames / 2))];
    }
}

public sealed class AnalysisException(string message) : Exception(message);
