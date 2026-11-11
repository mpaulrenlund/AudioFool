namespace AudioFool.Core.Art;

/// <summary>
/// The colour a cover is in, and that colour fitted to a theme, for the playback
/// bar (session 77, the user's request: the phone's Now Playing does it). Ported
/// from AudioFool-Android's <c>core/.../art/CoverColor.kt</c> with its numbers and
/// tests, so the two apps give a cover the same colour.
/// <para>
/// Worked out in OKLab (Björn Ottosson, 2020), where a colour is a lightness, a
/// chroma (how far from grey) and a hue, and a hue keeps its name as it is made
/// lighter or darker; in HSL a blue made lighter drifts to purple. Colours are
/// opaque 0xAARRGGBB, as WPF's Bgra32 pixels read as one <see cref="uint"/> each.
/// </para>
/// </summary>
public static class CoverColor
{
    /// <summary>A cover is judged from this many pixels a side: its middle square, scaled down.</summary>
    public const int SampleSide = 64;

    /// <summary>
    /// Below this chroma a pixel is grey, black or white, and says nothing of the
    /// cover's colour. A dark pixel needs less to be a colour (a dark brown has
    /// little chroma and is plainly brown; a cream with as much is off-white): a
    /// tenth of its lightness, no less than <see cref="GreyBelowDark"/>.
    /// </summary>
    public const double GreyBelow = 0.04;
    public const double GreyBelowDark = 0.02;
    private const double GreyPerLightness = 0.1;

    /// <summary>A cover with a smaller share of coloured pixels than this has no colour of its own.</summary>
    public const double MinShare = 0.02;

    /// <summary>Hues are counted in slices of 10 degrees; the winner is the best three neighbours.</summary>
    private const int Slices = 36;

    /// <summary>
    /// The cover's main colour: the hue that most of its colour is in, counting each
    /// pixel by how colourful it is, so a red logo on a black cover is red and a blue
    /// sky with a small sun is blue. The colour given is the average of the pixels of
    /// that hue. Null when the cover has no colour to speak of (black and white, or
    /// under <see cref="MinShare"/> coloured); see-through pixels are not part of the picture.
    /// </summary>
    public static uint? Main(ReadOnlySpan<uint> pixels)
    {
        Span<double> weight = stackalloc double[Slices];
        Span<double> l = stackalloc double[Slices];
        Span<double> a = stackalloc double[Slices];
        Span<double> b = stackalloc double[Slices];
        weight.Clear(); l.Clear(); a.Clear(); b.Clear();
        var seen = 0;
        var coloured = 0;
        foreach (var pixel in pixels)
        {
            if (pixel >> 24 < 128) continue;
            seen++;
            var lab = Lab.Of(pixel);
            var chroma = lab.Chroma;
            if (chroma < Math.Clamp(GreyPerLightness * lab.L, GreyBelowDark, GreyBelow)) continue;
            coloured++;
            var slice = Math.Clamp((int)(lab.HueDegrees / 360.0 * Slices), 0, Slices - 1);
            weight[slice] += chroma;
            l[slice] += chroma * lab.L;
            a[slice] += chroma * lab.A;
            b[slice] += chroma * lab.B;
        }
        if (seen == 0 || coloured < seen * MinShare) return null;

        // A hue that straddles two slices isn't split: each slice is counted with its neighbours.
        static double Around(ReadOnlySpan<double> values, int i) =>
            values[(i + Slices - 1) % Slices] + values[i] + values[(i + 1) % Slices];

        var best = 0;
        var most = double.MinValue;
        for (var i = 0; i < Slices; i++)
        {
            var around = Around(weight, i);
            if (around > most) (best, most) = (i, around);
        }
        return new Lab(Around(l, best) / most, Around(a, best) / most, Around(b, best) / most).ToArgb();
    }

    /// <summary>
    /// <paramref name="main"/> fitted to the theme: the same hue, at a lightness
    /// within <paramref name="range"/> of the theme colour it <paramref name="standsInFor"/>,
    /// on the side away from the background: no darker than it on Dark (and up to
    /// <paramref name="range"/> lighter), no lighter than it on Light (and down to
    /// <paramref name="range"/> darker). So it is never harder to see than the colour
    /// the theme was designed with.
    /// <para>
    /// Made lighter, the colour keeps its saturation (its chroma for its lightness),
    /// so a deep red cover gives a red, not a greyish pink; made darker it keeps its
    /// chroma, so a pastel stays a colour. Where the screen can't show that much
    /// chroma at that lightness, it gets as much as there is. Opaque.
    /// </para>
    /// </summary>
    public static uint Accent(uint main, uint standsInFor, bool dark, double range)
    {
        var from = Lab.Of(main);
        var anchor = Lab.Of(standsInFor).L;
        var lightness = dark ? Math.Clamp(from.L, anchor, anchor + range) : Math.Clamp(from.L, anchor - range, anchor);
        var chroma = lightness > from.L && from.L > 0 ? from.Chroma * lightness / from.L : from.Chroma;
        var hue = from.HueRadians;

        Lab At(double c) => new(lightness, c * Math.Cos(hue), c * Math.Sin(hue));
        if (At(chroma).Showable) return At(chroma).ToArgb();

        // The most chroma the screen can show at this lightness and hue.
        var low = 0.0;
        var high = chroma;
        for (var i = 0; i < 20; i++)
        {
            var middle = (low + high) / 2;
            if (At(middle).Showable) low = middle; else high = middle;
        }
        return At(low).ToArgb();
    }

    /// <summary>A colour's OKLab lightness, 0 (black) to 1 (white).</summary>
    public static double Lightness(uint argb) => Lab.Of(argb).L;

    /// <summary>A colour's OKLab chroma: 0 for a grey.</summary>
    public static double Chroma(uint argb) => Lab.Of(argb).Chroma;

    /// <summary>A colour's OKLab hue in degrees, 0 to 360.</summary>
    public static double Hue(uint argb) => Lab.Of(argb).HueDegrees;

    /// <summary>A colour in OKLab: <see cref="L"/> lightness, <see cref="A"/> green to red, <see cref="B"/> blue to yellow.</summary>
    private readonly record struct Lab(double L, double A, double B)
    {
        private const double Slack = 0.0005;

        public double Chroma => Math.Sqrt(A * A + B * B);
        public double HueRadians => Math.Atan2(B, A);
        public double HueDegrees => (HueRadians * 180 / Math.PI + 360.0) % 360.0;

        /// <summary>Whether a screen can show it, give or take a rounding.</summary>
        public bool Showable
        {
            get
            {
                var (r, g, b) = Linear();
                return Within(r) && Within(g) && Within(b);
                static bool Within(double c) => c >= -Slack && c <= 1 + Slack;
            }
        }

        public uint ToArgb()
        {
            var (r, g, b) = Linear();
            static uint Channel(double c) => (uint)Math.Round(Encode(Math.Clamp(c, 0.0, 1.0)) * 255, MidpointRounding.AwayFromZero);
            return 0xFF000000u | Channel(r) << 16 | Channel(g) << 8 | Channel(b);
        }

        /// <summary>Linear red, green and blue; outside 0 to 1 where sRGB has no such colour.</summary>
        private (double R, double G, double B) Linear()
        {
            var l = L + 0.3963377774 * A + 0.2158037573 * B;
            var m = L - 0.1055613458 * A - 0.0638541728 * B;
            var s = L - 0.0894841775 * A - 1.2914855480 * B;
            var (l3, m3, s3) = (l * l * l, m * m * m, s * s * s);
            return (
                4.0767416621 * l3 - 3.3077115913 * m3 + 0.2309699292 * s3,
                -1.2684380046 * l3 + 2.6097574011 * m3 - 0.3413193965 * s3,
                -0.0041960863 * l3 - 0.7034186147 * m3 + 1.7076147010 * s3);
        }

        public static Lab Of(uint argb)
        {
            var r = Decode(((argb >> 16) & 0xFF) / 255.0);
            var g = Decode(((argb >> 8) & 0xFF) / 255.0);
            var b = Decode((argb & 0xFF) / 255.0);
            var l = Math.Cbrt(0.4122214708 * r + 0.5363325363 * g + 0.0514459929 * b);
            var m = Math.Cbrt(0.2119034982 * r + 0.6806995451 * g + 0.1073969566 * b);
            var s = Math.Cbrt(0.0883024619 * r + 0.2817188376 * g + 0.6299787005 * b);
            return new Lab(
                0.2104542553 * l + 0.7936177850 * m - 0.0040720468 * s,
                1.9779984951 * l - 2.4285922050 * m + 0.4505937099 * s,
                0.0259040371 * l + 0.7827717662 * m - 0.8086757660 * s);
        }

        /// <summary>sRGB's curve: a stored channel value to light, and back.</summary>
        private static double Decode(double c) => c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        private static double Encode(double c) => c <= 0.0031308 ? c * 12.92 : 1.055 * Math.Pow(c, 1 / 2.4) - 0.055;
    }
}
