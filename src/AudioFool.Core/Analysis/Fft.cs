namespace AudioFool.Core.Analysis;

/// <summary>
/// An in-place radix-2 complex FFT of one fixed size. The twiddle factors and
/// the bit-reversal order are worked out once, since a track runs thousands of
/// transforms of the same size.
/// </summary>
public sealed class Fft
{
    private readonly int[] _reversed;
    private readonly double[] _cos;
    private readonly double[] _sin;

    public Fft(int size)
    {
        if (size < 2 || (size & (size - 1)) != 0)
            throw new ArgumentOutOfRangeException(nameof(size), size, "The size must be a power of two.");

        Size = size;
        var bits = System.Numerics.BitOperations.Log2((uint)size);

        _reversed = new int[size];
        for (var i = 0; i < size; i++)
        {
            var r = 0;
            for (var b = 0; b < bits; b++)
                r |= ((i >> b) & 1) << (bits - 1 - b);
            _reversed[i] = r;
        }

        _cos = new double[size / 2];
        _sin = new double[size / 2];
        for (var i = 0; i < size / 2; i++)
        {
            var angle = -2 * Math.PI * i / size;
            _cos[i] = Math.Cos(angle);
            _sin[i] = Math.Sin(angle);
        }
    }

    public int Size { get; }

    /// <summary>Transforms <paramref name="re"/> and <paramref name="im"/> in place.</summary>
    public void Transform(double[] re, double[] im)
    {
        var n = Size;

        for (var i = 0; i < n; i++)
        {
            var j = _reversed[i];
            if (j > i)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }

        for (var length = 2; length <= n; length <<= 1)
        {
            var half = length >> 1;
            var step = n / length;

            for (var start = 0; start < n; start += length)
            {
                for (var k = 0; k < half; k++)
                {
                    var wr = _cos[k * step];
                    var wi = _sin[k * step];
                    var a = start + k;
                    var b = a + half;

                    var tr = (re[b] * wr) - (im[b] * wi);
                    var ti = (re[b] * wi) + (im[b] * wr);

                    re[b] = re[a] - tr;
                    im[b] = im[a] - ti;
                    re[a] += tr;
                    im[a] += ti;
                }
            }
        }
    }
}
