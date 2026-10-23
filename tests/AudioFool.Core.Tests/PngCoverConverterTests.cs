using AudioFool.Core.Art;

namespace AudioFool.Core.Tests;

/// <summary>
/// Convert PNG Covers to JPEG: the embedded cover through the checked rewrite, and
/// .png files in a folder, with the Recycle Bin standing in as a list.
/// </summary>
public sealed class PngCoverConverterTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"AudioFool-pngconv-{Guid.NewGuid():N}");
    private readonly List<string> _recycled = [];

    public PngCoverConverterTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    // Laid out as a PNG is, chunk by chunk to IEND, so it reads as whole (session
    // 70); the filler is an IDAT chunk of random bytes. The checksums are zeros:
    // nothing here reads them.
    private static byte[] Png(int width, int height, int filler)
    {
        var data = new byte[filler];
        new Random(width).NextBytes(data);
        return
        [
            0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
            0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R',
            (byte)(width >> 24), (byte)(width >> 16), (byte)(width >> 8), (byte)width,
            (byte)(height >> 24), (byte)(height >> 16), (byte)(height >> 8), (byte)height,
            0x08, 0x02, 0x00, 0x00, 0x00, 0, 0, 0, 0,
            (byte)(filler >> 24), (byte)(filler >> 16), (byte)(filler >> 8), (byte)filler,
            (byte)'I', (byte)'D', (byte)'A', (byte)'T', .. data, 0, 0, 0, 0,
            0, 0, 0, 0, (byte)'I', (byte)'E', (byte)'N', (byte)'D', 0xAE, 0x42, 0x60, 0x82,
        ];
    }

    // A JPEG's frame header, a short scan and its end; the APP0 segment's padding is the filler.
    private static byte[] Jpeg(int width, int height, int filler)
    {
        var app0Length = filler + 2;
        return
        [
            0xFF, 0xD8, 0xFF, 0xE0, (byte)(app0Length >> 8), (byte)app0Length, .. new byte[filler],
            0xFF, 0xC0, 0x00, 0x11, 0x08,
            (byte)(height >> 8), (byte)height, (byte)(width >> 8), (byte)width, 0x03, .. new byte[9],
            0xFF, 0xDA, 0x00, 0x08, .. new byte[6], 0x12, 0x34,
            0xFF, 0xD9,
        ];
    }

    /// <summary>Stands in for the app's encoder: a JPEG of the PNG's size, a quarter of its bytes.</summary>
    private static byte[]? ToJpeg(byte[] png) =>
        ImageInfo.Read(png) is { Format: ImageFormat.Png } info ? Jpeg(info.Width, info.Height, png.Length / 4) : null;

    private bool Recycle(string path)
    {
        _recycled.Add(Path.GetFileName(path));
        File.Delete(path);
        return true;
    }

    private static TempAudioFile WithCover(string fixture, byte[] cover)
    {
        var file = new TempAudioFile(fixture);
        using var tagged = TagLib.File.Create(file.Path);
        tagged.Tag.Title = "Kept Title";
        tagged.Tag.Performers = ["Kept Artist"];
        tagged.Tag.Pictures = [new TagLib.Picture(new TagLib.ByteVector(cover)) { Type = TagLib.PictureType.FrontCover, MimeType = "image/png" }];
        tagged.Save();
        return file;
    }

    [Theory]
    [InlineData("sample.flac")]
    [InlineData("sample.mp3")]
    public void An_embedded_png_becomes_the_only_cover_as_a_jpeg(string fixture)
    {
        var png = Png(1400, 1400, 200_000);
        using var file = WithCover(fixture, png);
        var before = new FileInfo(file.Path).Length;

        var result = PngCoverConverter.ConvertEmbedded(file.Path, ToJpeg);

        Assert.Equal(CoverCleanOutcome.Cleaned, result.Outcome);
        Assert.Null(result.Error);
        Assert.Equal(before - new FileInfo(file.Path).Length, result.BytesFreed);
        Assert.True(result.BytesFreed > 100_000, $"freed {result.BytesFreed}");

        using var reread = TagLib.File.Create(file.Path);
        var only = Assert.Single(reread.Tag.Pictures);
        Assert.Equal(ToJpeg(png), only.Data.Data);
        Assert.Equal("image/jpeg", only.MimeType);
        Assert.Equal(TagLib.PictureType.FrontCover, only.Type);
        Assert.Equal("Kept Title", reread.Tag.Title);
        Assert.Equal(["Kept Artist"], reread.Tag.Performers);

        var finding = CoverCleaner.Survey(file.Path)!;
        Assert.False(finding.HasPng);
    }

    [Theory]
    [InlineData("sample.flac")]
    [InlineData("sample.mp3")]
    public void A_bigger_jpeg_is_still_written(string fixture)
    {
        var png = Png(800, 800, 2_000);
        using var file = WithCover(fixture, png);

        var result = PngCoverConverter.ConvertEmbedded(file.Path, p => Jpeg(800, 800, 10_000));

        Assert.Equal(CoverCleanOutcome.Cleaned, result.Outcome);
        using var reread = TagLib.File.Create(file.Path);
        Assert.Equal(Jpeg(800, 800, 10_000), Assert.Single(reread.Tag.Pictures).Data.Data);
    }

    [Fact]
    public void A_failed_conversion_leaves_the_file_as_it_was()
    {
        // In this test's own folder: other tests make working files in %TEMP% at the same time.
        using var source = WithCover("sample.flac", Png(1000, 1000, 50_000));
        var path = Path.Combine(_folder, "song.flac");
        File.Copy(source.Path, path);
        var bytes = File.ReadAllBytes(path);

        var none = PngCoverConverter.ConvertEmbedded(path, _ => null);
        var wrongSize = PngCoverConverter.ConvertEmbedded(path, _ => Jpeg(500, 500, 100));

        Assert.Equal(CoverCleanOutcome.Failed, none.Outcome);
        Assert.Equal(CoverCleanOutcome.Failed, wrongSize.Outcome);
        Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.Empty(Directory.EnumerateFiles(_folder, CoverCleaner.TempPrefix + "*"));
    }

    [Fact]
    public void A_cut_off_png_is_left_as_it_was()
    {
        var png = Png(1000, 1000, 50_000);
        var cut = png.AsSpan(0, png.Length / 2).ToArray();
        using var source = WithCover("sample.flac", cut);
        var path = Path.Combine(_folder, "song.flac");
        File.Copy(source.Path, path);
        File.WriteAllBytes(Path.Combine(_folder, "cover.png"), cut);
        var song = File.ReadAllBytes(path);

        var embedded = PngCoverConverter.ConvertEmbedded(path, ToJpeg);
        var folder = Assert.Single(PngCoverConverter.ConvertFolder(_folder, ToJpeg, Recycle));

        Assert.Equal((CoverCleanOutcome.Failed, PngCoverConverter.CutOffError), (embedded.Outcome, embedded.Error));
        Assert.Equal((PngFileOutcome.Failed, PngCoverConverter.CutOffError), (folder.Outcome, folder.Error));
        Assert.Equal(song, File.ReadAllBytes(path));
        Assert.Equal(cut, File.ReadAllBytes(Path.Combine(_folder, "cover.png")));
        Assert.Empty(_recycled);
    }

    [Fact]
    public void A_cut_off_jpeg_beside_a_png_gives_way_to_it()
    {
        var jpeg = Jpeg(1000, 1000, 30_000);
        File.WriteAllBytes(Path.Combine(_folder, "cover.jpg"), jpeg.AsSpan(0, jpeg.Length - 10).ToArray());
        var png = Png(1000, 1000, 90_000);
        File.WriteAllBytes(Path.Combine(_folder, "cover.png"), png);

        var result = Assert.Single(PngCoverConverter.ConvertFolder(_folder, ToJpeg, Recycle));

        Assert.Equal(PngFileOutcome.Converted, result.Outcome);
        Assert.Equal(ToJpeg(png), File.ReadAllBytes(Path.Combine(_folder, "cover.jpg")));
        Assert.Equal(["cover.jpg", "cover.png"], _recycled);
    }

    [Fact]
    public void A_song_without_a_png_is_left_alone()
    {
        using var file = WithCover("sample.flac", Jpeg(1000, 1000, 50_000));
        var bytes = File.ReadAllBytes(file.Path);

        Assert.Equal(CoverCleanOutcome.NothingToDo, PngCoverConverter.ConvertEmbedded(file.Path, ToJpeg).Outcome);
        Assert.Equal(bytes, File.ReadAllBytes(file.Path));
    }

    [Fact]
    public void A_png_file_becomes_a_jpeg_of_the_same_name_and_the_png_is_recycled()
    {
        var png = Png(1200, 1200, 40_000);
        File.WriteAllBytes(Path.Combine(_folder, "cover.png"), png);
        File.WriteAllBytes(Path.Combine(_folder, "Back.PNG"), Png(600, 600, 8_000));

        var results = PngCoverConverter.ConvertFolder(_folder, ToJpeg, Recycle);

        Assert.All(results, r => Assert.Equal(PngFileOutcome.Converted, r.Outcome));
        Assert.Equal(ToJpeg(png), File.ReadAllBytes(Path.Combine(_folder, "cover.jpg")));
        Assert.True(File.Exists(Path.Combine(_folder, "Back.jpg")));
        Assert.Equal(["Back.PNG", "cover.png"], _recycled.Order(StringComparer.OrdinalIgnoreCase));
        Assert.Empty(Directory.EnumerateFiles(_folder, "*.png"));
        Assert.Empty(Directory.EnumerateFiles(_folder, CoverCleaner.TempPrefix + "*"));
        Assert.Equal(png.Length - ToJpeg(png)!.Length, results.Single(r => r.Path.EndsWith("cover.png")).Saved);
    }

    [Fact]
    public void A_png_beside_a_jpeg_as_big_is_only_recycled()
    {
        var jpeg = Jpeg(1000, 1000, 30_000);
        File.WriteAllBytes(Path.Combine(_folder, "cover.jpg"), jpeg);
        File.WriteAllBytes(Path.Combine(_folder, "cover.png"), Png(1000, 1000, 90_000));

        var result = Assert.Single(PngCoverConverter.ConvertFolder(_folder, ToJpeg, Recycle));

        Assert.Equal(PngFileOutcome.Duplicate, result.Outcome);
        Assert.Equal(jpeg, File.ReadAllBytes(Path.Combine(_folder, "cover.jpg")));
        Assert.Equal(["cover.png"], _recycled);
    }

    [Fact]
    public void A_png_bigger_than_the_jpeg_beside_it_replaces_it()
    {
        File.WriteAllBytes(Path.Combine(_folder, "cover.jpg"), Jpeg(500, 500, 10_000));
        var png = Png(1500, 1500, 90_000);
        File.WriteAllBytes(Path.Combine(_folder, "cover.png"), png);

        var result = Assert.Single(PngCoverConverter.ConvertFolder(_folder, ToJpeg, Recycle));

        Assert.Equal(PngFileOutcome.Converted, result.Outcome);
        Assert.Equal(ToJpeg(png), File.ReadAllBytes(Path.Combine(_folder, "cover.jpg")));
        Assert.Equal(["cover.jpg", "cover.png"], _recycled);
    }

    [Fact]
    public void Without_a_recycle_bin_the_png_is_kept_and_no_jpeg_is_overwritten()
    {
        File.WriteAllBytes(Path.Combine(_folder, "cover.png"), Png(1000, 1000, 20_000));
        var small = Jpeg(400, 400, 1_000);
        File.WriteAllBytes(Path.Combine(_folder, "folder.jpg"), small);
        File.WriteAllBytes(Path.Combine(_folder, "folder.png"), Png(1000, 1000, 20_000));

        var results = PngCoverConverter.ConvertFolder(_folder, ToJpeg, _ => false);

        // cover.png: the JPEG is written, the PNG stays. folder.png: the smaller
        // folder.jpg can't go, so nothing is written over it.
        Assert.Equal(PngFileOutcome.Kept, results.Single(r => r.Path.EndsWith("cover.png")).Outcome);
        Assert.Equal(PngFileOutcome.Failed, results.Single(r => r.Path.EndsWith("folder.png")).Outcome);
        Assert.True(File.Exists(Path.Combine(_folder, "cover.png")));
        Assert.True(File.Exists(Path.Combine(_folder, "cover.jpg")));
        Assert.True(File.Exists(Path.Combine(_folder, "folder.png")));
        Assert.Equal(small, File.ReadAllBytes(Path.Combine(_folder, "folder.jpg")));
        Assert.Empty(Directory.EnumerateFiles(_folder, CoverCleaner.TempPrefix + "*"));
    }

    [Fact]
    public void A_png_that_cannot_be_converted_stays()
    {
        File.WriteAllBytes(Path.Combine(_folder, "cover.png"), Png(1000, 1000, 20_000));

        var result = Assert.Single(PngCoverConverter.ConvertFolder(_folder, _ => null, Recycle));

        Assert.Equal(PngFileOutcome.Failed, result.Outcome);
        Assert.True(File.Exists(Path.Combine(_folder, "cover.png")));
        Assert.False(File.Exists(Path.Combine(_folder, "cover.jpg")));
        Assert.Empty(_recycled);
    }

    [Fact]
    public void The_files_filter_drops_a_folder_taken_out_of_its_set()
    {
        File.WriteAllBytes(Path.Combine(_folder, "cover.png"), Png(500, 500, 100));
        var track = new Models.Track { FilePath = Path.Combine(_folder, "1.flac"), Album = "A", Artist = "B" };

        var filter = PngCoverStatistics.CountFiles([track]).Filter!;
        Assert.Equal(PngCoverKind.Files, filter.PngCovers);
        Assert.Single(filter.Apply([track]));

        filter.PngFolders!.Remove(_folder);
        Assert.Empty(filter.Apply([track]));
    }
}
