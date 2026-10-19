namespace AudioFool.Core.Analysis;

/// <summary>
/// What the visualizer draws from: the sound being heard, as levels in bands
/// from 20 Hz to 20 kHz and as bass, mid and treble, smoothed so they rise
/// quickly and fall slowly, like a meter's needle.
/// <para>
/// One window per video frame: <see cref="TapConverter.Output"/>, read centred on
/// what is being heard, and the time since the last frame. Measured as Analyze
/// measures (<see cref="SpectrumScale"/>): a sine inside a band reads its level
/// in dBFS, a full-scale one 0 dB; channels' powers are averaged, so out-of-phase
/// content can't cancel. Nothing is allocated per frame.
/// </para>
/// <para>
/// <see cref="Intensity"/>, 1 to 10, is how lively it is: how fast the levels
/// rise and fall, and how far bass, mid and treble swing either side of usual.
/// </para>
/// </summary>
public sealed class LiveSpectrum
{
    public const double FloorDb = -100;

    public const int LowestIntensity = 1;
    public const int HighestIntensity = 10;
    public const int DefaultIntensity = 5;

    public const double LowestHz = 20;
    public const double HighestHz = 20000;

    // The three ranges bass, mid and treble are measured over.
    private const double BassTop = 250;
    private const double MidTop = 4000;

    /// <summary>What counts as "usual" for bass, mid and treble: their average over about the last 3 s.</summary>
    private const double UsualSeconds = 3;

    private readonly Fft _fft;
    private readonly double[] _window;
    private readonly double _scale;
    private readonly double[] _re;
    private readonly double[] _im;
    private readonly double[] _power;
    private readonly int[] _bandFirst;
    private readonly int[] _bandEnd;
    private readonly int _bassEnd;
    private readonly int _midEnd;
    private readonly int _trebleEnd;

    private readonly double[] _usual = new double[3];
    private readonly double[] _relative = new double[3];
    private bool _started;
    private int _intensity = DefaultIntensity;

    public LiveSpectrum(int sampleRate, int fftSize = 2048, int bandCount = 32)
    {
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (bandCount <= 0) throw new ArgumentOutOfRangeException(nameof(bandCount));

        SampleRate = sampleRate;
        FftSize = fftSize;
        _fft = new Fft(fftSize);
        _window = SpectrumScale.Hann(fftSize);
        _scale = SpectrumScale.ZeroDb(fftSize);
        _re = new double[fftSize];
        _im = new double[fftSize];
        _power = new double[fftSize / 2 + 1];

        // Log-spaced edges, and the bins whose centres fall between them. A band
        // narrower than a bin (the lowest ones) takes the bin nearest its middle,
        // so no band is ever empty.
        BandEdges = new double[bandCount + 1];
        for (var b = 0; b <= bandCount; b++)
            BandEdges[b] = LowestHz * Math.Pow(HighestHz / LowestHz, (double)b / bandCount);

        _bandFirst = new int[bandCount];
        _bandEnd = new int[bandCount];
        for (var b = 0; b < bandCount; b++)
        {
            var first = Bin(BandEdges[b]);
            var end = Bin(BandEdges[b + 1]);
            if (end <= first)
            {
                first = Math.Clamp((int)Math.Round(Math.Sqrt(BandEdges[b] * BandEdges[b + 1]) / BinHz), 1, _power.Length - 1);
                end = first + 1;
            }
            _bandFirst[b] = first;
            _bandEnd[b] = end;
        }

        _bassEnd = Bin(BassTop);
        _midEnd = Bin(MidTop);
        _trebleEnd = Bin(HighestHz);

        BandDb = new float[bandCount];
        Bands = new float[bandCount];
        Reset();
    }

    public int SampleRate { get; }
    public int FftSize { get; }
    public int BandCount => Bands.Length;
    private double BinHz => (double)SampleRate / FftSize;

    /// <summary>The bands' edges in Hz, lowest first: <see cref="BandCount"/> + 1 of them.</summary>
    public double[] BandEdges { get; }

    /// <summary>Each band's level in this window, in dBFS, unsmoothed.</summary>
    public float[] BandDb { get; }

    /// <summary>Each band's level, smoothed: quick to rise, slow to fall. dBFS.</summary>
    public float[] Bands { get; }

    /// <summary>The whole of 20 Hz to 20 kHz in this window, in dBFS.</summary>
    public double LevelDb { get; private set; }

    /// <summary>
    /// Bass (20-250 Hz), mid (250 Hz-4 kHz) and treble (4-20 kHz), each against its
    /// own usual level over the last few seconds: 1 is usual, 2 twice the power, 0
    /// silence. Smoothed, and swung more or less by <see cref="Intensity"/>.
    /// </summary>
    public double Bass => _relative[0];
    public double Mid => _relative[1];
    public double Treble => _relative[2];

    /// <summary>1 (calm) to 10 (lively); out-of-range values are brought in.</summary>
    public int Intensity
    {
        get => _intensity;
        set => _intensity = Math.Clamp(value, LowestIntensity, HighestIntensity);
    }

    /// <summary>How quickly a level rises: 50 ms at intensity 1 down to 12 ms at 10.</summary>
    public double AttackSeconds => 0.050 * Math.Pow(0.85, _intensity - 1);

    /// <summary>How slowly it falls: 600 ms at intensity 1 down to 80 ms at 10.</summary>
    public double ReleaseSeconds => 0.600 * Math.Pow(0.80, _intensity - 1);

    /// <summary>
    /// How far bass, mid and treble swing from usual, as the power they are raised
    /// to: 0.5 at intensity 1, 0.9 at 5, 1.4 at 10. Four times the usual power then
    /// reads 2, 3.5 or 7; usual stays 1 and silence 0 at any intensity.
    /// </summary>
    public double Reaction => 0.5 + 0.1 * (_intensity - 1);

    /// <summary>Back to silence, with no idea of what's usual.</summary>
    public void Reset()
    {
        Array.Fill(BandDb, (float)FloorDb);
        Array.Fill(Bands, (float)FloorDb);
        Array.Clear(_usual);
        Array.Clear(_relative);
        LevelDb = FloorDb;
        _started = false;
    }

    /// <summary>
    /// Takes the newest <see cref="FftSize"/> frames of <paramref name="interleaved"/>
    /// (<paramref name="channels"/> to a frame), <paramref name="seconds"/> after the
    /// last call.
    /// </summary>
    public void Update(ReadOnlySpan<float> interleaved, int channels, double seconds)
    {
        var size = FftSize;
        var frames = interleaved.Length / channels;
        if (frames < size)
            return;

        var from = (frames - size) * channels;
        Array.Clear(_power);
        for (var c = 0; c < channels; c++)
        {
            for (var i = 0; i < size; i++)
            {
                _re[i] = interleaved[from + i * channels + c] * _window[i];
                _im[i] = 0;
            }

            _fft.Transform(_re, _im);
            for (var k = 0; k < _power.Length; k++)
                _power[k] += (_re[k] * _re[k] + _im[k] * _im[k]) * _scale / channels;
        }

        var step = Math.Clamp(seconds, 0, 0.5);
        var rise = 1 - Math.Exp(-step / AttackSeconds);
        var fall = 1 - Math.Exp(-step / ReleaseSeconds);

        for (var b = 0; b < Bands.Length; b++)
        {
            var db = (float)SpectrumScale.ToDb(Sum(_bandFirst[b], _bandEnd[b]), FloorDb);
            BandDb[b] = db;
            var smoothed = Bands[b];
            Bands[b] = smoothed + (db - smoothed) * (float)(db > smoothed ? rise : fall);
        }

        var low = Bin(LowestHz);
        LevelDb = SpectrumScale.ToDb(Sum(low, _trebleEnd), FloorDb);

        UpdateRelative(0, Sum(low, _bassEnd), step, rise, fall);
        UpdateRelative(1, Sum(_bassEnd, _midEnd), step, rise, fall);
        UpdateRelative(2, Sum(_midEnd, _trebleEnd), step, rise, fall);
        _started = true;
    }

    private void UpdateRelative(int which, double power, double step, double rise, double fall)
    {
        // What's usual follows slowly, and starts at the first window's level.
        _usual[which] = _started
            ? _usual[which] + (power - _usual[which]) * (1 - Math.Exp(-step / UsualSeconds))
            : power;

        var silent = Math.Pow(10, FloorDb / 10);
        var ratio = _usual[which] > silent ? power / _usual[which] : 0;
        var target = ratio > 0 ? Math.Pow(ratio, Reaction) : 0;

        var current = _relative[which];
        _relative[which] = _started ? current + (target - current) * (target > current ? rise : fall) : target;
    }

    /// <summary>The band's power, read as a sine inside it would be: the bins' sum over the window's width.</summary>
    private double Sum(int first, int end)
    {
        var sum = 0.0;
        for (var k = first; k < end && k < _power.Length; k++)
            sum += _power[k];
        return sum / SpectrumScale.HannBins;
    }

    /// <summary>The first bin whose centre is at or above <paramref name="hz"/>.</summary>
    private int Bin(double hz) => Math.Clamp((int)Math.Ceiling(hz / BinHz), 1, _power.Length);
}
