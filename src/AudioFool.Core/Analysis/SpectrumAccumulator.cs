namespace AudioFool.Core.Analysis;

/// <summary>
/// What a pass over a track's decoded audio measured: the picture the window
/// draws, and the full-resolution spectra the opinion is formed from.
/// </summary>
public sealed class SpectrumAnalysis
{
    public required int SampleRate { get; init; }
    public required int Channels { get; init; }
    public required TimeSpan Duration { get; init; }
    public required int FftSize { get; init; }

    /// <summary>Hz per FFT bin.</summary>
    public double BinWidth => (double)SampleRate / FftSize;

    /// <summary>
    /// Mean power of each FFT bin over the whole track, in dB relative to a
    /// full-scale sine (0 dB). Index 0 is DC; the last is the Nyquist frequency.
    /// </summary>
    public required double[] Average { get; init; }

    /// <summary>The loudest any single transform got in each bin, same scale.</summary>
    public required double[] Peak { get; init; }

    /// <summary>The spectrogram's time slices, left to right.</summary>
    public required int Columns { get; init; }

    /// <summary>The spectrogram's frequency bands, bottom (0 Hz) to top (Nyquist).</summary>
    public required int Rows { get; init; }

    /// <summary>
    /// The spectrogram in dB, column by column: <c>Picture[column * Rows + row]</c>,
    /// row 0 at the lowest frequency.
    /// </summary>
    public required float[] Picture { get; init; }

    /// <summary>The largest sample, in dB relative to full scale.</summary>
    public required double PeakSampleDb { get; init; }

    /// <summary>
    /// How many of a 24-bit file's bits the samples actually use: 16 for a CD
    /// master padded with zeros. Null when it wasn't checked (a lossy or 16-bit
    /// source) or the decoded samples don't sit on a 24-bit grid.
    /// </summary>
    public int? UsedBits { get; init; }

    /// <summary>
    /// Of the samples that aren't digital silence, the share using more than 16
    /// bits: about 99% for real 24-bit audio. A 16-bit master can have 24-bit
    /// fades or edits added later (measured on real albums), which makes
    /// <see cref="UsedBits"/> 24 while the music itself is 16-bit. Null when the
    /// bits weren't checked.
    /// </summary>
    public double? ExtraBitsShare { get; init; }

    /// <summary>Hz at the centre of a bin.</summary>
    public double FrequencyOf(int bin) => bin * BinWidth;

    /// <summary>The bin a frequency falls in, clamped to the spectrum.</summary>
    public int BinOf(double hz) => Math.Clamp((int)Math.Round(hz / BinWidth), 0, Average.Length - 1);
}

/// <summary>
/// Turns a stream of decoded samples into a <see cref="SpectrumAnalysis"/>.
/// Pure arithmetic with no BASS in it, so the tests can feed it synthetic audio.
/// <para>
/// Each transform is a Hann-windowed FFT per channel, the channels' powers
/// averaged (not the samples, so out-of-phase content can't cancel). Transforms
/// step through the track by <c>hop</c> frames, which is the FFT size for a
/// long track and less for a short one, so every picture column gets at least
/// one.
/// </para>
/// </summary>
public sealed class SpectrumAccumulator
{
    /// <summary>The quietest level kept, in dB. Below it is drawn as silence.</summary>
    public const double FloorDb = -160;

    private readonly int _sampleRate;
    private readonly int _channels;
    private readonly long _expectedFrames;
    private readonly int _columns;
    private readonly int _rows;
    private readonly bool _checkBits;

    private readonly Fft _fft;
    private readonly int _size;
    private readonly int _hop;
    private readonly double[] _window;
    private readonly double _scale;

    // The last _size frames of each channel, as a ring.
    private readonly float[][] _ring;
    private int _ringPos;
    private long _frames;

    // Frames in the ring since the last Restart: a transform waits for a full window.
    private long _filled;
    private long _sinceLast;

    private readonly double[] _re;
    private readonly double[] _im;
    private readonly double[] _power;

    private readonly double[] _sum;
    private readonly double[] _max;
    private long _transforms;

    private readonly double[] _columnSum;
    private readonly int[] _columnCount;
    private readonly int[] _rowStart;

    private double _peakSample;
    private long _bitsUsed;
    private bool _offGrid;
    private long _soundingSamples;
    private long _extraBitSamples;

    public SpectrumAccumulator(int sampleRate, int channels, long expectedFrames,
        int columns = 800, int rows = 512, bool checkBits = false)
    {
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (channels <= 0) throw new ArgumentOutOfRangeException(nameof(channels));

        _sampleRate = sampleRate;
        _channels = channels;
        _expectedFrames = Math.Max(1, expectedFrames);
        _columns = columns;
        _rows = rows;
        _checkBits = checkBits;

        _size = FftSizeFor(sampleRate);
        _fft = new Fft(_size);
        _hop = (int)Math.Clamp(_expectedFrames / columns, 64, _size);

        _window = new double[_size];
        for (var i = 0; i < _size; i++)
            _window[i] = 0.5 - (0.5 * Math.Cos(2 * Math.PI * i / _size));

        // A Hann window halves a sine's peak bin, so |X| = A * N / 4.
        // Scaling by (4 / N)^2 makes a full-scale sine read 0 dB.
        _scale = 16.0 / ((double)_size * _size);

        _ring = new float[channels][];
        for (var c = 0; c < channels; c++)
            _ring[c] = new float[_size];

        _re = new double[_size];
        _im = new double[_size];

        var bins = (_size / 2) + 1;
        _power = new double[bins];
        _sum = new double[bins];
        _max = new double[bins];

        _columnSum = new double[columns * rows];
        _columnCount = new int[columns];

        // Row r covers bins [_rowStart[r], _rowStart[r + 1]).
        _rowStart = new int[rows + 1];
        for (var r = 0; r <= rows; r++)
            _rowStart[r] = (int)((long)r * bins / rows);
    }

    /// <summary>
    /// About 11 Hz per bin at any rate: 4,096 at 44.1 and 48 kHz, doubling with
    /// each doubling of the rate.
    /// </summary>
    public static int FftSizeFor(int sampleRate)
    {
        var size = 4096;
        while (sampleRate / size > 12)
            size <<= 1;
        return size;
    }

    public int FftSize => _size;

    /// <summary>Feeds interleaved samples; a partial frame at the end is ignored.</summary>
    public void Add(ReadOnlySpan<float> interleaved)
    {
        var frames = interleaved.Length / _channels;

        for (var f = 0; f < frames; f++)
        {
            for (var c = 0; c < _channels; c++)
            {
                var s = interleaved[(f * _channels) + c];
                _ring[c][_ringPos] = s;

                var a = Math.Abs(s);
                if (a > _peakSample)
                    _peakSample = a;

                if (_checkBits && !_offGrid)
                    CheckBits(s);
            }

            _ringPos = (_ringPos + 1) % _size;
            _frames++;
            _filled++;
            _sinceLast++;

            if (_filled >= _size && _sinceLast >= _hop)
            {
                _sinceLast = 0;
                Transform();
            }
        }
    }

    /// <summary>
    /// Marks a jump in the audio, as between the slices of a sampled analysis.
    /// No transform spans it: a window across the join would hear it as a click,
    /// whose noise reaches every frequency and would fill in a lossy cutoff.
    /// </summary>
    public void Restart()
    {
        _filled = 0;
        _sinceLast = 0;
    }

    private void CheckBits(float s)
    {
        var v = s * 8388608.0;
        var r = Math.Round(v);
        if (Math.Abs(v - r) > 1e-6)
        {
            // A decoder that isn't handing back 24-bit integers; nothing to say.
            _offGrid = true;
            return;
        }

        var value = (long)Math.Abs(r);
        _bitsUsed |= value;
        if (value != 0)
        {
            _soundingSamples++;
            if ((value & 0xFF) != 0)
                _extraBitSamples++;
        }
    }

    private void Transform()
    {
        var bins = _power.Length;
        Array.Clear(_power);

        for (var c = 0; c < _channels; c++)
        {
            var ring = _ring[c];
            for (var i = 0; i < _size; i++)
            {
                // Oldest frame first: the ring's write position is the oldest.
                _re[i] = ring[(_ringPos + i) % _size] * _window[i];
                _im[i] = 0;
            }

            _fft.Transform(_re, _im);

            for (var k = 0; k < bins; k++)
                _power[k] += ((_re[k] * _re[k]) + (_im[k] * _im[k])) * _scale;
        }

        for (var k = 0; k < bins; k++)
        {
            var p = _power[k] / _channels;
            _power[k] = p;
            _sum[k] += p;
            if (p > _max[k])
                _max[k] = p;
        }

        _transforms++;

        // The column this transform's centre falls in.
        var centre = _frames - (_size / 2);
        var length = Math.Max(_expectedFrames, _frames);
        var column = (int)Math.Min(_columns - 1, centre * _columns / length);
        var offset = column * _rows;

        for (var r = 0; r < _rows; r++)
        {
            double band = 0;
            for (var k = _rowStart[r]; k < _rowStart[r + 1]; k++)
                band += _power[k];
            _columnSum[offset + r] += band / Math.Max(1, _rowStart[r + 1] - _rowStart[r]);
        }

        _columnCount[column]++;
    }

    public SpectrumAnalysis Finish()
    {
        // A track shorter than one transform still gets one, zero-padded.
        if (_transforms == 0 && _frames > 0)
        {
            _sinceLast = 0;
            Transform();
        }

        var bins = _power.Length;
        var average = new double[bins];
        var peak = new double[bins];
        for (var k = 0; k < bins; k++)
        {
            average[k] = ToDb(_transforms == 0 ? 0 : _sum[k] / _transforms);
            peak[k] = ToDb(_max[k]);
        }

        var picture = new float[_columns * _rows];
        Array.Fill(picture, (float)FloorDb);
        for (var c = 0; c < _columns; c++)
        {
            if (_columnCount[c] == 0)
                continue;
            var offset = c * _rows;
            for (var r = 0; r < _rows; r++)
                picture[offset + r] = (float)ToDb(_columnSum[offset + r] / _columnCount[c]);
        }

        // A column with no transform (only ever at the ends, or a track shorter
        // than the picture is wide) repeats its nearest neighbour.
        var first = Array.FindIndex(_columnCount, n => n > 0);
        if (first >= 0)
        {
            var source = first;
            for (var c = 0; c < _columns; c++)
            {
                if (_columnCount[c] > 0)
                    source = c;
                else
                    Array.Copy(picture, source * _rows, picture, c * _rows, _rows);
            }
        }

        int? usedBits = null;
        if (_checkBits && !_offGrid && _bitsUsed != 0)
            usedBits = 24 - System.Numerics.BitOperations.TrailingZeroCount(_bitsUsed);

        return new SpectrumAnalysis
        {
            SampleRate = _sampleRate,
            Channels = _channels,
            Duration = TimeSpan.FromSeconds((double)_frames / _sampleRate),
            FftSize = _size,
            Average = average,
            Peak = peak,
            Columns = _columns,
            Rows = _rows,
            Picture = picture,
            PeakSampleDb = ToDb(_peakSample * _peakSample),
            UsedBits = usedBits,
            ExtraBitsShare = usedBits is null || _soundingSamples == 0 ? null : (double)_extraBitSamples / _soundingSamples,
        };
    }

    private static double ToDb(double power) =>
        power <= 0 ? FloorDb : Math.Max(FloorDb, 10 * Math.Log10(power));
}
