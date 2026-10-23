using AudioFool.Core.Art;
using AudioFool.Core.Library;

namespace AudioFool.Core.Tests;

public sealed class EmbeddedArtExtractorTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("AudioFool-extract-");

    public void Dispose() => _root.Delete(recursive: true);

    /// <summary>
    /// A JPEG (SOI, APP0, frame header of the given size, a scan of <paramref name="tail"/>
    /// bytes so each call can differ in length, the end), whole as session 70 checks.
    /// </summary>
    private static byte[] Jpeg(int width, int height, int tail = 0)
    {
        var bytes = new List<byte> { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10 };
        bytes.AddRange(new byte[14]);
        bytes.AddRange([0xFF, 0xC0, 0x00, 0x11, 0x08,
            (byte)(height >> 8), (byte)height, (byte)(width >> 8), (byte)width, 0x03]);
        bytes.AddRange(new byte[9]);
        bytes.AddRange([0xFF, 0xDA, 0x00, 0x08, 0, 0, 0, 0, 0, 0]);
        for (var i = 0; i < tail; i++)
            bytes.Add((byte)(i * 31 + 7 & 0x7F));   // never FF: that would start a marker
        bytes.AddRange([0xFF, 0xD9]);
        return [.. bytes];
    }

    /// <summary>A PNG's signature, IHDR and IEND (checksums zero): whole as session 70 checks.</summary>
    private static byte[] Png(int width, int height) =>
    [
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
        0x00, 0x00, 0x00, 0x0D, (byte)'I', (byte)'H', (byte)'D', (byte)'R',
        (byte)(width >> 24), (byte)(width >> 16), (byte)(width >> 8), (byte)width,
        (byte)(height >> 24), (byte)(height >> 16), (byte)(height >> 8), (byte)height,
        0x08, 0x06, 0x00, 0x00, 0x00, 0, 0, 0, 0,
        0, 0, 0, 0, (byte)'I', (byte)'E', (byte)'N', (byte)'D', 0xAE, 0x42, 0x60, 0x82,
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

    private static bool NotShared(string _) => false;

    [Fact]
    public void A_shared_folder_gets_no_cover_and_the_others_still_do()
    {
        var art = Jpeg(1200, 1200, tail: 50);
        var mine = Make("Blue", "01.flac", "sample.flac", art);
        var inSingles = Make("Singles", "02.flac", "sample.flac", art);
        var redCover = Path.Combine(_root.FullName, "Singles", "cover.jpg");
        File.WriteAllBytes(redCover, [5, 5, 5]);
        var singles = Path.GetDirectoryName(inSingles)!;

        var results = EmbeddedArtExtractor.ExtractToFolders([mine, inSingles], NoConverter,
            d => string.Equals(d, singles, StringComparison.OrdinalIgnoreCase));

        Assert.Equal(ExtractOutcome.Saved, results.Single(r => r.Directory == Path.GetDirectoryName(mine)).Outcome);
        Assert.Equal(ExtractOutcome.Shared, results.Single(r => r.Directory == singles).Outcome);
        Assert.Equal(art, File.ReadAllBytes(Cover("Blue")));
        Assert.Equal(new byte[] { 5, 5, 5 }, File.ReadAllBytes(redCover));
        Assert.Contains("No cover.jpg in Singles: other albums' songs are there too.", EmbeddedArtExtractor.Describe(results));
    }

    [Fact]
    public void Only_a_shared_folder_says_so()
    {
        var track = Make("Singles", "01.flac", "sample.flac", Jpeg(800, 800));

        var results = EmbeddedArtExtractor.ExtractToFolders([track], NoConverter, _ => true);

        Assert.Equal(ExtractOutcome.Shared, Assert.Single(results).Outcome);
        Assert.False(File.Exists(Cover("Singles")));
        Assert.Equal("Not saved as cover.jpg: other albums' songs are in this folder too.", EmbeddedArtExtractor.Describe(results));
    }

    /// <summary>The cover is swapped in, never written over in place: a read-only one stays whole, and nothing is left beside it.</summary>
    [Fact]
    public void A_failed_write_leaves_the_old_cover_whole()
    {
        var track = Make("album", "01.flac", "sample.flac", Jpeg(3000, 3000, tail: 30));
        File.WriteAllBytes(Cover("album"), Jpeg(600, 600));
        File.SetAttributes(Cover("album"), FileAttributes.ReadOnly);
        try
        {
            var results = EmbeddedArtExtractor.ExtractToFolders([track], NoConverter, NotShared);

            Assert.Equal(ExtractOutcome.Failed, Assert.Single(results).Outcome);
            Assert.Equal(Jpeg(600, 600), File.ReadAllBytes(Cover("album")));
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(track)!, FileSwap.Prefix + "*"));
        }
        finally
        {
            File.SetAttributes(Cover("album"), FileAttributes.Normal);
        }
    }

    [Fact]
    public void A_saved_cover_leaves_no_working_file()
    {
        var track = Make("album", "01.flac", "sample.flac", Jpeg(900, 900, tail: 30));
        File.WriteAllBytes(Cover("album"), Jpeg(600, 600));

        EmbeddedArtExtractor.ExtractToFolders([track], NoConverter, NotShared);

        Assert.Equal(Jpeg(900, 900, tail: 30), File.ReadAllBytes(Cover("album")));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(track)!, FileSwap.Prefix + "*"));
    }

    [Theory]
    [InlineData("sample.flac")]
    [InlineData("sample.mp3")]
    public void Jpeg_is_written_byte_for_byte(string fixture)
    {
        var art = Jpeg(3000, 3000, tail: 4000);
        var track = Make("album", "01" + Path.GetExtension(fixture), fixture, art);

        var results = EmbeddedArtExtractor.ExtractToFolders([track], NoConverter, NotShared);

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

        EmbeddedArtExtractor.ExtractToFolders([a, b, c], NoConverter, NotShared);

        Assert.Equal(big, File.ReadAllBytes(Cover("album")));
    }

    [Fact]
    public void The_larger_file_wins_a_tie_on_pixels()
    {
        var lean = Jpeg(1000, 1000, tail: 10);
        var rich = Jpeg(1000, 1000, tail: 5000);
        var a = Make("album", "01.flac", "sample.flac", lean);
        var b = Make("album", "02.flac", "sample.flac", rich);

        EmbeddedArtExtractor.ExtractToFolders([a, b], NoConverter, NotShared);

        Assert.Equal(rich, File.ReadAllBytes(Cover("album")));
    }

    [Fact]
    public void Each_folder_gets_its_own_cover()
    {
        var disc1 = Jpeg(1200, 1200, tail: 50);
        var disc2 = Jpeg(900, 900, tail: 60);
        var a = Make(Path.Combine("album", "Disc 1"), "01.flac", "sample.flac", disc1);
        var b = Make(Path.Combine("album", "Disc 2"), "01.flac", "sample.flac", disc2);

        var results = EmbeddedArtExtractor.ExtractToFolders([a, b], NoConverter, NotShared);

        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.Equal(ExtractOutcome.Saved, r.Outcome));
        Assert.Equal(disc1, File.ReadAllBytes(Cover(Path.Combine("album", "Disc 1"))));
        Assert.Equal(disc2, File.ReadAllBytes(Cover(Path.Combine("album", "Disc 2"))));
    }

    [Fact]
    public void A_folder_without_art_gets_nothing()
    {
        var track = Make("album", "01.flac", "sample.flac");

        var results = EmbeddedArtExtractor.ExtractToFolders([track], NoConverter, NotShared);

        Assert.Equal(ExtractOutcome.NoArt, Assert.Single(results).Outcome);
        Assert.False(File.Exists(Cover("album")));
    }

    [Fact]
    public void Art_that_is_neither_jpeg_nor_png_is_reported_not_called_missing()
    {
        byte[] gif = [.. "GIF89a"u8, 0x10, 0x00, 0x10, 0x00, 0x00, 0x00, 0x00];
        var track = Make("album", "01.flac", "sample.flac", gif);

        var results = EmbeddedArtExtractor.ExtractToFolders([track], NoConverter, NotShared);

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

        var results = EmbeddedArtExtractor.ExtractToFolders([a, b], NoConverter, NotShared);

        Assert.Equal(ExtractOutcome.Saved, Assert.Single(results).Outcome);
        Assert.Equal(jpeg, File.ReadAllBytes(Cover("album")));
    }

    [Fact]
    public void An_existing_cover_with_as_many_pixels_is_kept()
    {
        var existing = Jpeg(2000, 2000, tail: 20);
        Make("album", "01.flac", "sample.flac", Jpeg(2000, 2000, tail: 9000));
        File.WriteAllBytes(Cover("album"), existing);

        var results = EmbeddedArtExtractor.ExtractToFolders([Path.Combine(_root.FullName, "album", "01.flac")], NoConverter, NotShared);

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

        var results = EmbeddedArtExtractor.ExtractToFolders([track], NoConverter, NotShared);

        Assert.Equal(ExtractOutcome.Saved, Assert.Single(results).Outcome);
        Assert.Equal(embedded, File.ReadAllBytes(Cover("album")));
    }

    [Fact]
    public void An_unreadable_existing_cover_is_replaced()
    {
        var embedded = Jpeg(800, 800, tail: 30);
        var track = Make("album", "01.flac", "sample.flac", embedded);
        File.WriteAllBytes(Cover("album"), [1, 2, 3]);

        EmbeddedArtExtractor.ExtractToFolders([track], NoConverter, NotShared);

        Assert.Equal(embedded, File.ReadAllBytes(Cover("album")));
    }

    // Session 70: a cut-off picture is never written out, and a cut-off file gives way.
    [Fact]
    public void A_bigger_existing_cover_that_is_cut_off_is_replaced()
    {
        var embedded = Jpeg(800, 800, tail: 30);
        var track = Make("album", "01.flac", "sample.flac", embedded);
        var big = Jpeg(3000, 3000, tail: 500);
        File.WriteAllBytes(Cover("album"), big.AsSpan(0, big.Length - 100).ToArray());

        var results = EmbeddedArtExtractor.ExtractToFolders([track], NoConverter, NotShared);

        Assert.Equal(ExtractOutcome.Saved, Assert.Single(results).Outcome);
        Assert.Equal(embedded, File.ReadAllBytes(Cover("album")));
    }

    [Fact]
    public void A_cut_off_embedded_cover_is_not_written_out()
    {
        var whole = Jpeg(1200, 1200, tail: 300);
        var track = Make("album", "01.flac", "sample.flac", whole.AsSpan(0, whole.Length - 100).ToArray());

        var only = Assert.Single(EmbeddedArtExtractor.ExtractToFolders([track], NoConverter, NotShared));

        Assert.Equal(ExtractOutcome.Failed, only.Outcome);
        Assert.Equal("the cover is cut off", only.Error);
        Assert.False(File.Exists(Cover("album")));
    }

    [Fact]
    public void Other_cover_files_are_left_alone()
    {
        var track = Make("album", "01.flac", "sample.flac", Jpeg(800, 800));
        var folderJpg = Path.Combine(_root.FullName, "album", "folder.jpg");
        File.WriteAllBytes(folderJpg, [9, 9, 9]);

        EmbeddedArtExtractor.ExtractToFolders([track], NoConverter, NotShared);

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

        var results = EmbeddedArtExtractor.ExtractToFolders([track], p => { seen = p; return converted; }, NotShared);

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

        var results = EmbeddedArtExtractor.ExtractToFolders([track], _ => null, NotShared);

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

        EmbeddedArtExtractor.ExtractToFolders([a, b], NoConverter, NotShared);

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
