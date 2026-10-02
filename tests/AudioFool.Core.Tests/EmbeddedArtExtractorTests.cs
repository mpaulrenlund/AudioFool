using AudioFool.Core.Art;
using AudioFool.Core.Library;

namespace AudioFool.Core.Tests;

public sealed class EmbeddedArtExtractorTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("AudioFool-extract-");

    public void Dispose() => _root.Delete(recursive: true);

    /// <summary>A JPEG head (SOI, APP0, frame header of the given size) plus a tail, so each call can differ in length.</summary>
    private static byte[] Jpeg(int width, int height, int tail = 0)
    {
        var bytes = new List<byte> { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10 };
        bytes.AddRange(new byte[14]);
        bytes.AddRange([0xFF, 0xC0, 0x00, 0x11, 0x08,
            (byte)(height >> 8), (byte)height, (byte)(width >> 8), (byte)width, 0x03]);
        for (var i = 0; i < tail; i++)
            bytes.Add((byte)(i * 31 + 7));
        return [.. bytes];
    }

    private static byte[] Png(int width, int height) =>
    [
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
        0x00, 0x00, 0x00, 0x0D, (byte)'I', (byte)'H', (byte)'D', (byte)'R',
        (byte)(width >> 24), (byte)(width >> 16), (byte)(width >> 8), (byte)width,
        (byte)(height >> 24), (byte)(height >> 16), (byte)(height >> 8), (byte)height,
        0x08, 0x06, 0x00, 0x00, 0x00,
    ];

    /// <summary>A copy of a fixture in <paramref name="folder"/>, with <paramref name="art"/> embedded if given.</summary>
    private string Make(string folder, string name, string fixture, byte[]? art = null)
    {
        var dir = Path.Combine(_root.FullName, folder);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "TestData", fixture), path);

        if (art is not null)
        {
            var mime = ImageInfo.Read(art)?.Format switch
            {
                ImageFormat.Png => "image/png",
                ImageFormat.Jpeg => "image/jpeg",
                _ => "image/gif",
            };
            var edit = new TrackTagEdit("T", "A", "AA", "Al", 2000, 1, 1, 1, 1);
            var result = TagWriter.WriteTrackTags(TagReader.Read(path), edit, new ArtPayload(art, mime), folderArtPath: null);
            Assert.True(result.Success, result.ErrorMessage);
        }

        return path;
    }

    private string Cover(string folder) => Path.Combine(_root.FullName, folder, "cover.jpg");

    private static byte[]? NoConverter(byte[] _) => throw new InvalidOperationException("a JPEG must not be converted");

    [Theory]
    [InlineData("sample.flac")]
    [InlineData("sample.mp3")]
    public void Jpeg_is_written_byte_for_byte(string fixture)
    {
        var art = Jpeg(3000, 3000, tail: 4000);
        var track = Make("album", "01" + Path.GetExtension(fixture), fixture, art);

        var results = EmbeddedArtExtractor.ExtractToFolders([track], NoConverter);

        var only = Assert.Single(results);
        Assert.Equal(ExtractOutcome.Saved, only.Outcome);
        Assert.False(only.Converted);
        Assert.Equal(art, File.ReadAllBytes(Cover("album")));
        Assert.Equal(art.Length, only.Bytes);
        Assert.Equal(new ImageInfo(ImageFormat.Jpeg, 3000, 3000), only.Info);
    }

    [Fact]
    public void The_largest_picture_in_a_folder_wins()
    {
        var small = Jpeg(500, 500, tail: 9000);
        var big = Jpeg(1400, 1400, tail: 100);
        var a = Make("album", "01.flac", "sample.flac", small);
        var b = Make("album", "02.flac", "sample.flac", big);
        var c = Make("album", "03.mp3", "sample.mp3", small);

        EmbeddedArtExtractor.ExtractToFolders([a, b, c], NoConverter);

        Assert.Equal(big, File.ReadAllBytes(Cover("album")));
    }

    [Fact]
    public void The_larger_file_wins_a_tie_on_pixels()
    {
        var lean = Jpeg(1000, 1000, tail: 10);
        var rich = Jpeg(1000, 1000, tail: 5000);
        var a = Make("album", "01.flac", "sample.flac", lean);
        var b = Make("album", "02.flac", "sample.flac", rich);

        EmbeddedArtExtractor.ExtractToFolders([a, b], NoConverter);

        Assert.Equal(rich, File.ReadAllBytes(Cover("album")));
    }

    [Fact]
    public void Each_folder_gets_its_own_cover()
    {
        var disc1 = Jpeg(1200, 1200, tail: 50);
        var disc2 = Jpeg(900, 900, tail: 60);
        var a = Make(Path.Combine("album", "Disc 1"), "01.flac", "sample.flac", disc1);
        var b = Make(Path.Combine("album", "Disc 2"), "01.flac", "sample.flac", disc2);

        var results = EmbeddedArtExtractor.ExtractToFolders([a, b], NoConverter);

        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.Equal(ExtractOutcome.Saved, r.Outcome));
        Assert.Equal(disc1, File.ReadAllBytes(Cover(Path.Combine("album", "Disc 1"))));
        Assert.Equal(disc2, File.ReadAllBytes(Cover(Path.Combine("album", "Disc 2"))));
    }

    [Fact]
    public void A_folder_without_art_gets_nothing()
    {
        var track = Make("album", "01.flac", "sample.flac");

        var results = EmbeddedArtExtractor.ExtractToFolders([track], NoConverter);

        Assert.Equal(ExtractOutcome.NoArt, Assert.Single(results).Outcome);
        Assert.False(File.Exists(Cover("album")));
    }

    [Fact]
    public void Art_that_is_neither_jpeg_nor_png_is_reported_not_called_missing()
    {
        byte[] gif = [.. "GIF89a"u8, 0x10, 0x00, 0x10, 0x00, 0x00, 0x00, 0x00];
        var track = Make("album", "01.flac", "sample.flac", gif);

        var results = EmbeddedArtExtractor.ExtractToFolders([track], NoConverter);

        Assert.Equal(ExtractOutcome.Unsupported, Assert.Single(results).Outcome);
        Assert.False(File.Exists(Cover("album")));
        Assert.Contains("isn't a JPEG or PNG", EmbeddedArtExtractor.Describe(results));
    }

    [Fact]
    public void A_usable_picture_beats_an_unsupported_one()
    {
        byte[] gif = [.. "GIF89a"u8, 0x10, 0x00, 0x10, 0x00, 0x00, 0x00, 0x00];
        var jpeg = Jpeg(700, 700, tail: 12);
        var a = Make("album", "01.flac", "sample.flac", gif);
        var b = Make("album", "02.flac", "sample.flac", jpeg);

        var results = EmbeddedArtExtractor.ExtractToFolders([a, b], NoConverter);

        Assert.Equal(ExtractOutcome.Saved, Assert.Single(results).Outcome);
        Assert.Equal(jpeg, File.ReadAllBytes(Cover("album")));
    }

    [Fact]
    public void An_existing_cover_with_as_many_pixels_is_kept()
    {
        var existing = Jpeg(2000, 2000, tail: 20);
        Make("album", "01.flac", "sample.flac", Jpeg(2000, 2000, tail: 9000));
        File.WriteAllBytes(Cover("album"), existing);

        var results = EmbeddedArtExtractor.ExtractToFolders([Path.Combine(_root.FullName, "album", "01.flac")], NoConverter);

        var only = Assert.Single(results);
        Assert.Equal(ExtractOutcome.KeptExisting, only.Outcome);
        Assert.Equal(Cover("album"), only.CoverPath);
        Assert.Equal(existing, File.ReadAllBytes(Cover("album")));
    }

    [Fact]
    public void An_existing_cover_with_fewer_pixels_is_replaced()
    {
        var embedded = Jpeg(3000, 3000, tail: 30);
        var track = Make("album", "01.flac", "sample.flac", embedded);
        File.WriteAllBytes(Cover("album"), Jpeg(600, 600));

        var results = EmbeddedArtExtractor.ExtractToFolders([track], NoConverter);

        Assert.Equal(ExtractOutcome.Saved, Assert.Single(results).Outcome);
        Assert.Equal(embedded, File.ReadAllBytes(Cover("album")));
    }

    [Fact]
    public void An_unreadable_existing_cover_is_replaced()
    {
        var embedded = Jpeg(800, 800, tail: 30);
        var track = Make("album", "01.flac", "sample.flac", embedded);
        File.WriteAllBytes(Cover("album"), [1, 2, 3]);

        EmbeddedArtExtractor.ExtractToFolders([track], NoConverter);

        Assert.Equal(embedded, File.ReadAllBytes(Cover("album")));
    }

    [Fact]
    public void Other_cover_files_are_left_alone()
    {
        var track = Make("album", "01.flac", "sample.flac", Jpeg(800, 800));
        var folderJpg = Path.Combine(_root.FullName, "album", "folder.jpg");
        File.WriteAllBytes(folderJpg, [9, 9, 9]);

        EmbeddedArtExtractor.ExtractToFolders([track], NoConverter);

        Assert.Equal(new byte[] { 9, 9, 9 }, File.ReadAllBytes(folderJpg));
        Assert.True(File.Exists(Cover("album")));
    }

    [Fact]
    public void A_png_goes_through_the_converter_and_is_flagged()
    {
        var png = Png(2400, 2400);
        var track = Make("album", "01.flac", "sample.flac", png);
        byte[] converted = [0xFF, 0xD8, 0xFF, 0xD9];
        byte[]? seen = null;

        var results = EmbeddedArtExtractor.ExtractToFolders([track], p => { seen = p; return converted; });

        var only = Assert.Single(results);
        Assert.Equal(ExtractOutcome.Saved, only.Outcome);
        Assert.True(only.Converted);
        Assert.Equal(png, seen);
        Assert.Equal(converted, File.ReadAllBytes(Cover("album")));
        Assert.Equal(converted.Length, only.Bytes);
    }

    [Fact]
    public void A_failed_conversion_writes_nothing()
    {
        var track = Make("album", "01.flac", "sample.flac", Png(2400, 2400));

        var results = EmbeddedArtExtractor.ExtractToFolders([track], _ => null);

        var only = Assert.Single(results);
        Assert.Equal(ExtractOutcome.Failed, only.Outcome);
        Assert.Contains("PNG", only.Error);
        Assert.False(File.Exists(Cover("album")));
    }

    [Fact]
    public void A_bigger_jpeg_beats_a_smaller_png_without_conversion()
    {
        var jpeg = Jpeg(2000, 2000, tail: 40);
        var a = Make("album", "01.flac", "sample.flac", Png(1000, 1000));
        var b = Make("album", "02.flac", "sample.flac", jpeg);

        EmbeddedArtExtractor.ExtractToFolders([a, b], NoConverter);

        Assert.Equal(jpeg, File.ReadAllBytes(Cover("album")));
    }

    [Fact]
    public void Describe_says_what_happened()
    {
        var info = new ImageInfo(ImageFormat.Jpeg, 3000, 3000);
        FolderExtract Saved(string d, bool converted = false) =>
            new(d, ExtractOutcome.Saved, d + "/cover.jpg", info, 922_550, converted, null, null);

        Assert.Equal("Saved cover.jpg in the folder (3000 × 3000, 901 KB).",
            EmbeddedArtExtractor.Describe([Saved("a")]));
        Assert.Contains("converted to JPEG at maximum quality",
            EmbeddedArtExtractor.Describe([Saved("a", converted: true)]));
        Assert.StartsWith("Saved cover.jpg in 2 of 2 folders",
            EmbeddedArtExtractor.Describe([Saved("a"), Saved("b")]));
        Assert.Equal("No embedded art found in these tracks.",
            EmbeddedArtExtractor.Describe([new FolderExtract("a", ExtractOutcome.NoArt, null, null, 0, false, null, null)]));
        Assert.Contains("Kept the existing cover.jpg (4000 × 4000)",
            EmbeddedArtExtractor.Describe([new FolderExtract("a", ExtractOutcome.KeptExisting, "a/cover.jpg", info, 1, false,
                new ImageInfo(ImageFormat.Jpeg, 4000, 4000), null)]));
    }
}

public class TrackWithFolderArtTests
{
    [Fact]
    public void Changes_only_the_folder_art_path()
    {
        using var file = new TempAudioFile("sample.flac");
        var original = TagReader.Read(file.Path);

        var copy = original.WithFolderArt(@"D:\Music\Album\cover.jpg");

        Assert.Equal(@"D:\Music\Album\cover.jpg", copy.FolderArtPath);
        foreach (var property in typeof(Models.Track).GetProperties()
                     .Where(p => p.CanRead && p.Name != nameof(Models.Track.FolderArtPath)
                                 && p.GetIndexParameters().Length == 0))
        {
            Assert.True(Equals(property.GetValue(original), property.GetValue(copy)),
                        $"{property.Name} was not carried over");
        }
    }
}
