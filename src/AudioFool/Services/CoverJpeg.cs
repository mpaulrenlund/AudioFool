using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using BitMiracle.LibJpeg.Classic;

namespace AudioFool.Services;

/// <summary>
/// Turns a PNG cover into a JPEG at the highest quality there is, for the one
/// case where "Save Embedded Art" must write a cover.jpg from a PNG. It is the
/// only place that button re-encodes a picture; JPEGs are copied as they are.
/// <para>
/// Quality 100 with full-resolution colour (4:4:4). The encoders built into
/// Windows can't do the second part: WPF's and GDI+'s both halve the colour
/// resolution (4:2:0) even at quality 100, which was measured, so the JPEG is
/// written by a managed libjpeg port (BitMiracle.LibJpeg.NET) instead.
/// </para>
/// <para>
/// JPEG has no transparency, so a PNG with an alpha channel is composited onto
/// white first, which is how a viewer would show it on a white page. GDI+ only
/// decodes; the compositing is done here, pixel for pixel. (Drawing the image
/// onto a white bitmap instead blended the bottom row with white, measured.)
/// </para>
/// </summary>
public static class CoverJpeg
{
    public static byte[]? FromPng(byte[] png)
    {
        try
        {
            using var source = new Bitmap(new MemoryStream(png));
            var (width, height) = (source.Width, source.Height);

            // Whatever the PNG's own format (palette, grey, 16-bit...), GDI+ hands
            // the pixels over as 8-bit B, G, R, A.
            var pixels = source.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                return Encode(pixels, width, height);
            }
            finally
            {
                source.UnlockBits(pixels);
            }
        }
        // The decoder's and encoder's own failures come in several types; any of
        // them means "couldn't convert", which the caller reports.
        catch (Exception)
        {
            return null;
        }
    }

    private static byte[] Encode(BitmapData pixels, int width, int height)
    {
        using var output = new MemoryStream();

        var jpeg = new jpeg_compress_struct(new jpeg_error_mgr())
        {
            Image_width = width,
            Image_height = height,
            Input_components = 3,
            In_color_space = J_COLOR_SPACE.JCS_RGB,
        };
        jpeg.jpeg_set_defaults();
        jpeg.jpeg_set_quality(100, force_baseline: true);

        // Defaults sample the luma at 2 x 2, i.e. 4:2:0. One sample per pixel in
        // every component is full colour resolution.
        jpeg.Component_info[0].H_samp_factor = 1;
        jpeg.Component_info[0].V_samp_factor = 1;

        // Smaller file, same pixels: optimal Huffman tables instead of the stock ones.
        jpeg.Optimize_coding = true;

        jpeg.jpeg_stdio_dest(output);
        jpeg.jpeg_start_compress(write_all_tables: true);

        var bgra = new byte[width * 4];
        var row = new byte[width * 3];
        var rows = new[] { row };
        for (var y = 0; y < height; y++)
        {
            System.Runtime.InteropServices.Marshal.Copy(pixels.Scan0 + (y * pixels.Stride), bgra, 0, bgra.Length);
            for (var x = 0; x < width; x++)
            {
                // Straight (not premultiplied) alpha over white, rounded to nearest.
                int alpha = bgra[(x * 4) + 3];
                row[x * 3] = Over(bgra[(x * 4) + 2], alpha);
                row[(x * 3) + 1] = Over(bgra[(x * 4) + 1], alpha);
                row[(x * 3) + 2] = Over(bgra[x * 4], alpha);
            }

            jpeg.jpeg_write_scanlines(rows, 1);
        }

        jpeg.jpeg_finish_compress();
        return output.ToArray();
    }

    /// <summary>A channel value over a white page at the given opacity (0-255).</summary>
    private static byte Over(byte channel, int alpha) =>
        alpha == 255 ? channel : (byte)(((channel * alpha) + (255 * (255 - alpha)) + 127) / 255);
}
