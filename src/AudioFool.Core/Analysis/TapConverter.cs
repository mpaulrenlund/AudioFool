using AudioFool.Core.Playback;

namespace AudioFool.Core.Analysis;

/// <summary>
/// Turns a window of the tap's copy (<see cref="AudioEngine.TryReadHeard"/>) into
/// plain PCM at 44.1 or 48 kHz for the visualizer: hi-res halved until it gets
/// there, and DSD over PCM unwrapped back to its bits and filtered to PCM.
/// <para>
/// Everything above 20 kHz is left out, and nothing above it is let fold back
/// below it: hi-res carries content up there and DSD a lot of noise by design
/// (noise shaping moves its quantisation noise above hearing), so without the
/// filtering the same album would look busier as DSD than as a CD. Between 20 and
/// ~24 kHz some may remain; the analysis stops at 20 kHz.
/// </para>
/// <para>
/// One window at a time, with nothing carried between them, so a seek, a track
/// change or a reopened device needs no care. Built for one layout and window
/// size; <see cref="Convert"/> allocates nothing. Fill <see cref="Input"/>, call
/// <see cref="Convert"/>, read <see cref="Output"/>. The filters delay the result
/// by under a millisecond.
/// </para>
/// </summary>
public sealed class TapConverter
{
    /// <summary>The lowest rate the output is halved to: the 44.1 kHz family stops at 44.1, the 48 at 48.</summary>
    private const int LowestRate = 44100;

    /// <summary>
    /// DSD's 0 dB is half modulation (the SACD convention, and what BASSDSD's
    /// conversion makes full scale), so DSD plays as loud as the PCM of the same
    /// master.
    /// </summary>
    private const float DsdGain = 2f;

    /// <summary>The frames before a DoP frame that its filtered value draws on (128 bits = 8 frames).</summary>
    private const int DopHistory = DopTaps / 16 - 1;

    private const int DopTaps = 128;

    /// <summary>
    /// The last halving's filter: flat to 20 kHz, at least 90 dB down from the
    /// point that would fold back to 20 kHz (24.1 kHz at 88.2 in). A half-band
    /// filter: every other tap is zero.
    /// </summary>
    private static readonly float[] Last = HalfBand(127);

    /// <summary>
    /// The earlier halvings', which only have to keep what would fold below
    /// 24.1 kHz out, with a wide band to do it in.
    /// </summary>
    private static readonly float[] Early = HalfBand(27);

    /// <summary>
    /// DSD's filter over the bit stream, as sixteen tables of a byte's worth of
    /// taps each: <c>DopTables[j][b]</c> is byte <c>b</c>'s contribution as the
    /// <c>j</c>-th newest byte. Flat to 20 kHz; 90 dB down from where DoP's rate
    /// would fold onto 24.1 kHz (152 kHz for DSD64, and twice as far out at
    /// DSD128, where the same filter scales with the bit rate).
    /// </summary>
    private static readonly float[][] DopTables = BuildDopTables();

    private readonly int _channels;
    private readonly int _halvings;
    private readonly float[] _scratchA;
    private readonly float[] _scratchB;
    private readonly int[] _words;

    public TapConverter(TapFormat format, int outputFrames)
    {
        if (outputFrames <= 0)
            throw new ArgumentOutOfRangeException(nameof(outputFrames));

        Format = format;
        _channels = Math.Max(1, format.Channels);

        var rate = format.SampleRate;
        while (rate % 2 == 0 && rate / 2 >= LowestRate)
        {
            rate /= 2;
            _halvings++;
        }
        OutputRate = rate;
        OutputFrames = outputFrames;

        // Backwards from the output: what each halving needs to give its frames.
        var frames = outputFrames;
        for (var stage = _halvings - 1; stage >= 0; stage--)
            frames = 2 * (frames - 1) + Filter(stage).Length;
        InputFrames = frames + DopHistory;

        Input = new float[InputFrames * _channels];
        Output = new float[outputFrames * _channels];
        _scratchA = new float[InputFrames * _channels];
        _scratchB = new float[InputFrames * _channels];
        _words = new int[InputFrames * _channels];
    }

    /// <summary>The layout <see cref="Input"/> is in: the tap's.</summary>
    public TapFormat Format { get; }

    /// <summary>The frames to read into <see cref="Input"/>: enough for the filters to fill <see cref="OutputFrames"/>.</summary>
    public int InputFrames { get; }

    public int OutputFrames { get; }

    /// <summary>44.1 or 48 kHz for the usual rates; an odd rate that can't be halved to either stays as it is.</summary>
    public int OutputRate { get; }

    /// <summary>The window to convert, interleaved as the tap gives it, newest last.</summary>
    public float[] Input { get; }

    /// <summary>The converted window, interleaved, newest last.</summary>
    public float[] Output { get; }

    /// <summary>Whether the last window was DSD over PCM.</summary>
    public bool WasDop { get; private set; }

    /// <summary>Converts <see cref="Input"/> into <see cref="Output"/>.</summary>
    public void Convert()
    {
        var source = Input;
        var frames = InputFrames;

        WasDop = ReadWords(Input, frames, _words, _channels);
        if (WasDop)
        {
            frames = Unwrap(_words, frames, _scratchA, _channels);
            source = _scratchA;
        }

        for (var stage = 0; stage < _halvings; stage++)
        {
            var target = ReferenceEquals(source, _scratchA) ? _scratchB : _scratchA;
            frames = Halve(source, frames, target, _channels, Filter(stage));
            source = target;
        }

        // The newest frames; a PCM window has DoP's few frames of history to spare.
        Array.Copy(source, (frames - OutputFrames) * _channels, Output, 0, OutputFrames * _channels);
    }

    private float[] Filter(int stage) => stage == _halvings - 1 ? Last : Early;

    // ------------------------------------------------------------- DSD over PCM

    /// <summary>
    /// Reads each sample as the 24-bit word it carries, into <paramref name="words"/>,
    /// and says whether the window is DoP: nine frames in ten carrying a marker
    /// (0x05 or 0xFA in the top byte, the same in every channel). PCM music doesn't;
    /// a marker byte would mean the same few large values over and over. A window
    /// can straddle the end of a DSD track, hence not every frame. A word without a
    /// marker is stored as DSD's idle pattern, silence, for <see cref="Unwrap"/>.
    /// </summary>
    private static bool ReadWords(float[] samples, int frames, int[] words, int channels)
    {
        var marked = 0;
        for (var f = 0; f < frames; f++)
        {
            var first = Word(samples[f * channels]) >> 16;
            var same = first is 0x05 or 0xFA;
            for (var c = 0; c < channels; c++)
            {
                var word = Word(samples[f * channels + c]);
                if (word >> 16 != first)
                    same = false;
                words[f * channels + c] = (word >> 16) is 0x05 or 0xFA ? word : 0x6969;
            }
            if (same)
                marked++;
        }

        return marked * 10 >= frames * 9;
    }

    /// <summary>A float sample as the 24-bit word it carries.</summary>
    private static int Word(float sample) => (int)MathF.Round(sample * 8388608f) & 0xFFFFFF;

    /// <summary>
    /// The bits back out of DoP and through the filter: one PCM frame per DoP
    /// frame, at the DoP rate. Each word carries 16 bits, oldest first.
    /// </summary>
    private static int Unwrap(int[] words, int frames, float[] target, int channels)
    {
        var produced = frames - DopHistory;
        for (var c = 0; c < channels; c++)
        {
            for (var f = 0; f < produced; f++)
            {
                // Frames f .. f + 7, newest last; byte j = 0 is the newest.
                var value = 0f;
                for (var back = 0; back <= DopHistory; back++)
                {
                    var word = words[(f + DopHistory - back) * channels + c];
                    value += DopTables[back * 2][word & 0xFF] + DopTables[back * 2 + 1][(word >> 8) & 0xFF];
                }
                target[f * channels + c] = value;
            }
        }

        return produced;
    }

    private static float[][] BuildDopTables()
    {
        // A windowed-sinc low-pass over the bit stream, cut midway between 20 kHz
        // and the 152 kHz it must be 90 dB down by, at DSD64's 2.8224 MHz.
        const double bitRate = 2822400;
        var cut = (20000 + 152300) / 2 / bitRate;
        var taps = Kaiser(DopTaps, 9.3);
        var middle = (DopTaps - 1) / 2.0;
        var sum = 0.0;
        for (var k = 0; k < DopTaps; k++)
        {
            var x = k - middle;
            taps[k] *= 2 * cut * Sinc(2 * cut * x);
            sum += taps[k];
        }

        // Tap k is the bit k places back from the newest; bits are +1 or -1.
        var tables = new float[DopTaps / 8][];
        for (var j = 0; j < tables.Length; j++)
        {
            tables[j] = new float[256];
            for (var b = 0; b < 256; b++)
            {
                var value = 0.0;
                for (var t = 0; t < 8; t++)   // bit 0, the lowest, is the newest in its byte
                    value += taps[j * 8 + t] / sum * (((b >> t) & 1) == 1 ? 1 : -1);
                tables[j][b] = (float)(value * DsdGain);
            }
        }

        return tables;
    }

    // -------------------------------------------------------------- halving

    /// <summary>
    /// Filters and keeps every other frame: <paramref name="filter"/> is a
    /// half-band, so only its middle tap and the odd ones either side count. Only
    /// whole results are kept, lined up so the last one ends at the input's end.
    /// </summary>
    private static int Halve(float[] source, int frames, float[] target, int channels, float[] filter)
    {
        var length = filter.Length;
        if (frames < length)
            return 0;

        var middle = length / 2;
        var skip = (frames - length) % 2;
        var produced = (frames - skip - length) / 2 + 1;

        for (var k = 0; k < produced; k++)
        {
            var centre = skip + 2 * k + middle;
            for (var c = 0; c < channels; c++)
            {
                var sum = filter[middle] * source[centre * channels + c];
                for (var t = 1; t <= middle; t += 2)
                    sum += filter[middle + t] * (source[(centre + t) * channels + c] + source[(centre - t) * channels + c]);
                target[k * channels + c] = sum;
            }
        }

        return produced;
    }

    /// <summary>
    /// A half-band low-pass (cut at a quarter of the input rate) of
    /// <paramref name="length"/> taps, 4n + 3 so its outermost taps aren't zero,
    /// Kaiser-windowed for about 90 dB, unity at 0 Hz.
    /// </summary>
    private static float[] HalfBand(int length)
    {
        var window = Kaiser(length, 8.96);
        var middle = length / 2;
        var taps = new double[length];
        var sum = 0.0;
        for (var n = 0; n < length; n++)
        {
            var x = n - middle;
            taps[n] = x != 0 && x % 2 == 0 ? 0 : 0.5 * Sinc(x / 2.0) * window[n];
            sum += taps[n];
        }

        return taps.Select(t => (float)(t / sum)).ToArray();
    }

    private static double Sinc(double x) => x == 0 ? 1 : Math.Sin(Math.PI * x) / (Math.PI * x);

    private static double[] Kaiser(int length, double beta)
    {
        var window = new double[length];
        var i0Beta = BesselI0(beta);
        for (var n = 0; n < length; n++)
        {
            var r = 2.0 * n / (length - 1) - 1;
            window[n] = BesselI0(beta * Math.Sqrt(Math.Max(0, 1 - r * r))) / i0Beta;
        }
        return window;
    }

    private static double BesselI0(double x)
    {
        double sum = 1, term = 1;
        for (var k = 1; k < 50; k++)
        {
            term *= x / (2 * k) * (x / (2 * k));
            sum += term;
            if (term < 1e-12 * sum)
                break;
        }
        return sum;
    }
}
