namespace AudioFool.Core.Visualizer;

/// <summary>
/// The visualizer's intensity for projectM (session 58): the sound handed to
/// projectM, never the DAC's, with its beats made punchier or smoother.
/// <para>
/// projectM's presets react to <c>bass</c>, <c>mid</c> and <c>treb</c>: each band's
/// level in the last 13 ms against its own average of the last few seconds (1 is
/// usual). Its beat sensitivity only decides hard cuts, which the visualizer turns
/// off, so it can't change what a preset sees (measured). This changes it from the
/// sound's side. Each of projectM's three bands is measured against its own loudest
/// of the last few seconds, and scaled by <c>(now / loudest) ^ (swing − 1)</c>: with
/// a swing above 1 the quiet between beats is turned down, so the beats stand out
/// further; below 1 it is turned up, so they stand out less. 1 leaves the sound as
/// it is. Nothing is ever turned up past the band's own recent loudest, so there's
/// no clipping to blur the bands into one another.
/// </para>
/// <para>
/// The bands are projectM's: it reads its 512-bin spectrum as bins 0–85, 85–170 and
/// 170–256, so they end at 85/1024 and 170/1024 of the rate. Each band's level is
/// measured through clean fourth-order filters; the gains are applied to a split
/// that adds back up to the sound exactly. Calls continue one another: hand it the
/// frames in order. Allocates only when a call needs more room than any before.
/// </para>
/// </summary>
public sealed class BeatShaper
{
    /// <summary>The swing at the default intensity: beats clearly punchier than the music's own.</summary>
    public const double DefaultSwing = 2.0;

    /// <summary>Each step of intensity multiplies the swing by this.</summary>
    public const double SwingStep = 1.25;

    private const double FastAttackSeconds = 0.001;
    private const double FastReleaseSeconds = 0.040;

    /// <summary>How long a band's loudest moment is remembered, as a decay time.</summary>
    private const double LoudestSeconds = 2.0;

    /// <summary>−80 dBFS: below this a band counts as silent and is left alone.</summary>
    private const double Floor = 1e-4;

    private const double MaxGain = 16;

    // Per channel, two sections each (fourth order, Linkwitz–Riley): [channel * 2], [channel * 2 + 1].
    private readonly Biquad[] _lowPass = new Biquad[4];      // at the low edge: the low band
    private readonly Biquad[] _midHighPass = new Biquad[4];  // at the low edge, then
    private readonly Biquad[] _midLowPass = new Biquad[4];   //   at the high edge: the mid band, measured
    private readonly Biquad[] _highPass = new Biquad[4];     // at the high edge, on what the low band leaves: the high band

    private readonly double[] _fast = new double[3];
    private readonly double[] _loudest = new double[3];
    private readonly double _attack;
    private readonly double _release;
    private readonly double _loudestRelease;
    private int _intensity = 5;
    private double _exponent = DefaultSwing - 1;

    public BeatShaper(int sampleRate)
    {
        if (sampleRate <= 0)
            throw new ArgumentOutOfRangeException(nameof(sampleRate));

        SampleRate = sampleRate;
        var lowEdge = sampleRate * 85.0 / 1024;
        var highEdge = sampleRate * 170.0 / 1024;
        for (var s = 0; s < 4; s++)
        {
            _lowPass[s] = Biquad.LowPass(sampleRate, lowEdge);
            _midHighPass[s] = Biquad.HighPass(sampleRate, lowEdge);
            _midLowPass[s] = Biquad.LowPass(sampleRate, highEdge);
            _highPass[s] = Biquad.HighPass(sampleRate, highEdge);
        }
        _attack = Math.Exp(-1 / (FastAttackSeconds * sampleRate));
        _release = Math.Exp(-1 / (FastReleaseSeconds * sampleRate));
        _loudestRelease = Math.Exp(-1 / (LoudestSeconds * sampleRate));
    }

    public int SampleRate { get; }

    /// <summary>The shaped frames from the last <see cref="Shape"/>, interleaved stereo, from index 0.</summary>
    public float[] Output { get; private set; } = [];

    /// <summary>1 (smoothest) to 10 (punchiest); 5 by default.</summary>
    public int Intensity
    {
        get => _intensity;
        set
        {
            _intensity = Math.Clamp(value, 1, 10);
            _exponent = SwingFor(_intensity) - 1;
        }
    }

    /// <summary>How far each band's level is pushed from its recent loudest, as a power: 1 leaves it be.</summary>
    public static double SwingFor(int intensity) => DefaultSwing * Math.Pow(SwingStep, Math.Clamp(intensity, 1, 10) - 5);

    /// <summary>
    /// Shapes <paramref name="frames"/> frames of interleaved stereo from
    /// <paramref name="firstFrame"/> into <see cref="Output"/>.
    /// </summary>
    public void Shape(float[] source, int firstFrame, int frames)
    {
        if (frames <= 0)
            return;
        if (Output.Length < frames * 2)
            Output = new float[frames * 2];

        var output = Output;
        Span<double> band = stackalloc double[6];     // low, mid, high to apply, left then right
        Span<double> measured = stackalloc double[6]; // the same bands, cleanly filtered, to measure
        for (var f = 0; f < frames; f++)
        {
            var i = (firstFrame + f) * 2;
            for (var c = 0; c < 2; c++)
            {
                double x = source[i + c];
                var low = _lowPass[c * 2 + 1].Next(_lowPass[c * 2].Next(x));
                var rest = x - low;
                var high = _highPass[c * 2 + 1].Next(_highPass[c * 2].Next(rest));
                band[c * 3] = low;
                band[c * 3 + 1] = rest - high;
                band[c * 3 + 2] = high;

                measured[c * 3] = low;
                measured[c * 3 + 1] = _midLowPass[c * 2 + 1].Next(_midLowPass[c * 2].Next(
                    _midHighPass[c * 2 + 1].Next(_midHighPass[c * 2].Next(x))));
                measured[c * 3 + 2] = high;
            }

            for (var b = 0; b < 3; b++)
            {
                var level = Math.Max(Math.Abs(measured[b]), Math.Abs(measured[3 + b]));
                _fast[b] = level + (_fast[b] - level) * (level > _fast[b] ? _attack : _release);
                _loudest[b] = Math.Max(_fast[b], _loudest[b] * _loudestRelease);

                var gain = _fast[b] < Floor
                    ? 1
                    : Math.Clamp(Math.Pow(_fast[b] / _loudest[b], _exponent), 1 / MaxGain, MaxGain);
                band[b] *= gain;
                band[3 + b] *= gain;
            }

            output[f * 2] = (float)Math.Clamp(band[0] + band[1] + band[2], -1, 1);
            output[f * 2 + 1] = (float)Math.Clamp(band[3] + band[4] + band[5], -1, 1);
        }
    }

    /// <summary>A second-order Butterworth section (the RBJ cookbook's), direct form I; two make a Linkwitz–Riley filter.</summary>
    private struct Biquad
    {
        private double _b0, _b1, _b2, _a1, _a2;
        private double _x1, _x2, _y1, _y2;

        public static Biquad LowPass(int rate, double hz) => Make(rate, hz, low: true);

        public static Biquad HighPass(int rate, double hz) => Make(rate, hz, low: false);

        private static Biquad Make(int rate, double hz, bool low)
        {
            var w = 2 * Math.PI * Math.Min(hz, rate * 0.45) / rate;
            var alpha = Math.Sin(w) / (2 * Math.Sqrt(0.5));
            var cos = Math.Cos(w);
            var a0 = 1 + alpha;
            var b1 = low ? 1 - cos : -(1 + cos);
            var b0 = low ? (1 - cos) / 2 : (1 + cos) / 2;
            return new Biquad
            {
                _b0 = b0 / a0, _b1 = b1 / a0, _b2 = b0 / a0,
                _a1 = -2 * cos / a0, _a2 = (1 - alpha) / a0,
            };
        }

        public double Next(double x)
        {
            var y = _b0 * x + _b1 * _x1 + _b2 * _x2 - _a1 * _y1 - _a2 * _y2;
            (_x2, _x1, _y2, _y1) = (_x1, x, _y1, y);
            return y;
        }
    }
}
