namespace AudioFool.Core.Analysis;

/// <summary>
/// The window and the calibration every spectrum in the app uses, so Analyze and
/// the visualizer measure alike: a Hann window, and a scale that makes a
/// full-scale sine read 0 dB.
/// </summary>
public static class SpectrumScale
{
    /// <summary>A Hann window of <paramref name="size"/> points, the periodic form an FFT wants.</summary>
    public static double[] Hann(int size)
    {
        var window = new double[size];
        for (var i = 0; i < size; i++)
            window[i] = 0.5 - (0.5 * Math.Cos(2 * Math.PI * i / size));
        return window;
    }

    /// <summary>
    /// What a Hann-windowed FFT bin's |X|² is multiplied by so a full-scale sine's
    /// peak bin reads 1 (0 dB). The window halves a sine's peak, so |X| = A * N / 4;
    /// scaling by (4 / N)² brings it to A².
    /// </summary>
    public static double ZeroDb(int size) => 16.0 / ((double)size * size);

    /// <summary>
    /// The Hann window's width in bins: a sine spreads its power over 1.5 bins'
    /// worth, so a band summing bins scaled by <see cref="ZeroDb"/> divides by
    /// this to read a sine inside it as 0 dB.
    /// </summary>
    public const double HannBins = 1.5;

    /// <summary>Power to dB, no quieter than <paramref name="floorDb"/>.</summary>
    public static double ToDb(double power, double floorDb) =>
        power <= 0 ? floorDb : Math.Max(floorDb, 10 * Math.Log10(power));
}
