using AudioFool.Core.Library;

namespace AudioFool.Core.Tests;

/// <summary>
/// A picture frame cut off right after its MIME type. Until session 69 reading
/// it threw ArgumentOutOfRangeException, which no save path caught: an album save
/// with a new cover stopped at that song without a word (session 60's review).
/// </summary>
public class TruncatedPictureTests
{
    private static byte[] Frame(string id, byte[] body) =>
    [
        .. System.Text.Encoding.ASCII.GetBytes(id),
        (byte)(body.Length >> 24), (byte)(body.Length >> 16), (byte)(body.Length >> 8), (byte)body.Length,
        0, 0,
        .. body,
    ];

    private static byte[] Id3v23(params byte[][] frames)
    {
        var size = frames.Sum(f => f.Length);
        return
        [
            .. "ID3"u8, 3, 0, 0,
            (byte)((size >> 21) & 0x7F), (byte)((size >> 14) & 0x7F), (byte)((size >> 7) & 0x7F), (byte)(size & 0x7F),
            .. frames.SelectMany(f => f),
        ];
    }

    private static byte[] Apic(byte[] image) => Frame("APIC", [0, .. "image/jpeg"u8, 0, 3, 0, .. image]);

    /// <summary>
    /// An MP3 with two ID3v2 tags in front, as some taggers leave them (TagLib
    /// rewrites only the first): the cover in the first, a cut-off frame in the second.
    /// </summary>
    private static string Mp3WithTruncatedPicture(byte[] cover)
    {
        var source = Path.Combine(AppContext.BaseDirectory, "TestData", "sample.mp3");
        var audio = File.ReadAllBytes(source).AsSpan((int)TagPadding.Read(source)!.AudioStart).ToArray();
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}_truncated.mp3");
        byte[] cutOff = [0, .. "image/jpeg"u8, 0];
        File.WriteAllBytes(path, [.. Id3v23(Apic(cover)), .. Id3v23(Frame("APIC", cutOff)), .. audio]);
        return path;
    }

    [Fact]
    public void Shrinking_around_a_cut_off_picture_declines_instead_of_throwing()
    {
        var cover = Enumerable.Range(0, 2000).Select(i => (byte)(i % 251 + 1)).ToArray();
        var path = Mp3WithTruncatedPicture(cover);
        var target = path + ".shrunk";
        try
        {
            Assert.Equal(2, TagPadding.Read(path)!.Id3Tags);

            Assert.False(TagPadding.TryShrink(path, target, keepOnlyPicture: cover));
            Assert.False(File.Exists(target));
        }
        finally
        {
            File.Delete(path);
            File.Delete(target);
        }
    }

    /// <summary>The album save's own call: the song is saved with the new cover, and nothing throws.</summary>
    [Fact]
    public void An_album_save_with_a_new_cover_goes_through()
    {
        var path = Mp3WithTruncatedPicture([9, 9, 9]);
        var cover = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "TestData", "cover.jpg"));
        try
        {
            var result = TagWriter.WriteAlbumTrackTags(TagReader.Read(path), new AlbumTagEdit { Album = "Saved" },
                new ArtPayload(cover, "image/jpeg"), folderArtPath: null);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.Equal("Saved", TagReader.Read(path).Album);
            Assert.Equal(cover, TagReader.ReadEmbeddedArt(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
