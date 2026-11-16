using System.Numerics;

namespace AudioFool.Core.Equalizer;

/// <summary>
/// One second-order filter's coefficients, normalised so a0 is 1. Worked out the
/// way Equalizer APO does (its BiQuad.cpp and BiQuadFilter.cpp, which are Robert
/// Bristow-Johnson's <em>Audio EQ Cookbook</em>), so a preset sounds the same here
/// as there. Doubles throughout: a 10 Hz band at 192 kHz is badly behaved in float.
/// </summary>
public readonly record struct Biquad(double B0, double B1, double B2, double A1, double A2)
{
    /// <summary>
    /// The filter for <paramref name="band"/> at <paramref name="sampleRate"/>, or null
    /// when it can't run there: its frequency is at or above half the rate (the highest
    /// a stream at that rate can carry), or its numbers don't make a filter.
    /// </summary>
    public static Biquad? Design(EqBand band, double sampleRate)
    {
        var nyquist = sampleRate / 2;
        if (!(band.FrequencyHz > 0) || !(band.Width > 0) || !double.IsFinite(band.GainDb)
            || band.FrequencyHz >= nyquist)
            return null;

        var shelf = band.Kind is EqFilterKind.LowShelf or EqFilterKind.HighShelf;
        var a = Math.Pow(10, band.GainDb / 40);
        var frequency = band.FrequencyHz;

        if (shelf && band.CornerFrequency)
        {
            var s = band.Width;
            if (band.WidthKind == EqWidth.Q)
            {
                var q = band.Width;
                s = 1.0 / ((1.0 / (q * q) - 2.0) / (a + 1.0 / a) + 1.0);
            }

            var centreFactor = Math.Pow(10.0, Math.Abs(band.GainDb) / 80.0 / s);
            frequency = band.Kind == EqFilterKind.LowShelf ? frequency * centreFactor : frequency / centreFactor;
            if (!(frequency > 0) || frequency >= nyquist)
                return null;
        }

        var omega = 2 * Math.PI * frequency / sampleRate;
        var sn = Math.Sin(omega);
        var cs = Math.Cos(omega);

        var alpha = band.WidthKind switch
        {
            EqWidth.Q => sn / (2 * band.Width),
            EqWidth.Slope when shelf => sn / 2 * Math.Sqrt((a + 1 / a) * (1 / band.Width - 1) + 2),
            EqWidth.Bandwidth when !shelf => sn * Math.Sinh(Math.Log(2) / 2 * band.Width * omega / sn),
            _ => double.NaN,
        };
        if (!double.IsFinite(alpha) || alpha <= 0)
            return null;

        var beta = 2 * Math.Sqrt(a) * alpha;
        double b0, b1, b2, a0, a1, a2;
        switch (band.Kind)
        {
            case EqFilterKind.Peak:
                b0 = 1 + alpha * a;
                b1 = -2 * cs;
                b2 = 1 - alpha * a;
                a0 = 1 + alpha / a;
                a1 = -2 * cs;
                a2 = 1 - alpha / a;
                break;
            case EqFilterKind.LowShelf:
                b0 = a * ((a + 1) - (a - 1) * cs + beta);
                b1 = 2 * a * ((a - 1) - (a + 1) * cs);
                b2 = a * ((a + 1) - (a - 1) * cs - beta);
                a0 = (a + 1) + (a - 1) * cs + beta;
                a1 = -2 * ((a - 1) + (a + 1) * cs);
                a2 = (a + 1) + (a - 1) * cs - beta;
                break;
            case EqFilterKind.HighShelf:
                b0 = a * ((a + 1) + (a - 1) * cs + beta);
                b1 = -2 * a * ((a - 1) + (a + 1) * cs);
                b2 = a * ((a + 1) + (a - 1) * cs - beta);
                a0 = (a + 1) - (a - 1) * cs + beta;
                a1 = 2 * ((a - 1) - (a + 1) * cs);
                a2 = (a + 1) - (a - 1) * cs - beta;
                break;
            default:
                return null;
        }

        var filter = new Biquad(b0 / a0, b1 / a0, b2 / a0, a1 / a0, a2 / a0);
        return filter.IsFinite ? filter : null;
    }

    private bool IsFinite =>
        double.IsFinite(B0) && double.IsFinite(B1) && double.IsFinite(B2) && double.IsFinite(A1) && double.IsFinite(A2);

    /// <summary>How much the filter raises (or lowers) a sine at <paramref name="frequencyHz"/>, in dB.</summary>
    public double GainDbAt(double frequencyHz, double sampleRate)
    {
        var z1 = Complex.FromPolarCoordinates(1, -2 * Math.PI * frequencyHz / sampleRate);
        var z2 = z1 * z1;
        var h = (B0 + B1 * z1 + B2 * z2) / (1 + A1 * z1 + A2 * z2);
        return 20 * Math.Log10(h.Magnitude);
    }
}
