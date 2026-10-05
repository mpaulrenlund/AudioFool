using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AudioFool.Core.Analysis;
using AudioFool.Core.Theme;

namespace AudioFool.Services;

/// <summary>
/// Paints a <see cref="SpectrumAnalysis"/> as a heat map, one pixel per picture
/// cell: time left to right, frequency bottom to top. The scale runs through
/// the six <c>color.spectrogram.level*</c> tokens between <c>analysis.floorDb</c>
/// (black) and <c>analysis.topDb</c> (yellow).
/// </summary>
public static class SpectrogramImage
{
    public const int Levels = 6;

    /// <summary>Frozen, so it can be built off the UI thread.</summary>
    public static BitmapSource Render(SpectrumAnalysis analysis, ThemeTokens tokens)
    {
        var stops = Stops(tokens);
        var floor = tokens.Numbers["analysis.floorDb"];
        var top = tokens.Numbers["analysis.topDb"];

        var width = analysis.Columns;
        var height = analysis.Rows;
        var pixels = new byte[width * height * 4];

        for (var column = 0; column < width; column++)
        {
            for (var row = 0; row < height; row++)
            {
                var db = analysis.Picture[(column * height) + row];
                var t = Math.Clamp((db - floor) / (top - floor), 0, 1);
                var color = Blend(stops, t);

                // Row 0 is the lowest frequency, drawn at the bottom.
                var offset = (((height - 1 - row) * width) + column) * 4;
                pixels[offset] = color.B;
                pixels[offset + 1] = color.G;
                pixels[offset + 2] = color.R;
                pixels[offset + 3] = 255;
            }
        }

        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>The same scale as a gradient, quietest at the bottom, for the legend.</summary>
    public static LinearGradientBrush LegendBrush(ThemeTokens tokens)
    {
        var stops = Stops(tokens);
        var brush = new LinearGradientBrush { StartPoint = new Point(0, 1), EndPoint = new Point(0, 0) };
        for (var i = 0; i < stops.Length; i++)
            brush.GradientStops.Add(new GradientStop(stops[i], (double)i / (stops.Length - 1)));
        brush.Freeze();
        return brush;
    }

    private static Color[] Stops(ThemeTokens tokens)
    {
        var stops = new Color[Levels];
        for (var i = 0; i < Levels; i++)
        {
            var c = tokens.Colors[$"color.spectrogram.level{i}"];
            stops[i] = Color.FromArgb(255, c.R, c.G, c.B);
        }

        return stops;
    }

    private static Color Blend(Color[] stops, double t)
    {
        var position = t * (stops.Length - 1);
        var i = Math.Min(stops.Length - 2, (int)position);
        var f = position - i;
        var a = stops[i];
        var b = stops[i + 1];
        return Color.FromRgb(
            (byte)Math.Round(a.R + ((b.R - a.R) * f)),
            (byte)Math.Round(a.G + ((b.G - a.G) * f)),
            (byte)Math.Round(a.B + ((b.B - a.B) * f)));
    }
}
