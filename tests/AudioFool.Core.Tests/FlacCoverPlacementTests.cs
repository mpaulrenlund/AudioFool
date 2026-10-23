using AudioFool.Core.Library;

namespace AudioFool.Core.Tests;

/// <summary>
/// A FLAC can carry an ID3v2 tag in front of "fLaC". A new cover is written into
/// both, and the tidy-up after it keeps one copy. Until session 69 that was the
/// first found, the ID3 one, so the cover was gone from the FLAC's own blocks,
/// where every other player looks (session 60's review).
/// </summary>
public class FlacCoverPlacementTests
{
    private static readonly byte[] Cover =
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "TestData", "cover.jpg"));

    /// <summary>The FLAC fixture with an ID3v2 tag in front and an old cover in both tags.</summary>
    private static TempAudioFile FlacWithId3InFront()
    {
        var file = new TempAudioFile("sample.flac");
        using (var tagged = TagLib.File.Create(file.Path))
        {
            tagged.GetTag(TagLib.TagTypes.Id3v2, create: true);
            tagged.Tag.Title = "Song";
            tagged.Tag.Pictures =
            [
                new TagLib.Picture(new TagLib.ByteVector(Enumerable.Repeat((byte)7, 3000).ToArray()))
                {
                    Type = TagLib.PictureType.FrontCover, MimeType = "image/jpeg",
                },
            ];
            tagged.Save();
        }

        Assert.Equal(1, TagPadding.Read(file.Path)!.Id3Tags);
        return file;
    }

    [Fact]
    public void A_new_cover_ends_up_in_the_flacs_own_blocks()
    {
        using var file = FlacWithId3InFront();
        var track = TagReader.Read(file.Path);
        var edit = new TrackTagEdit(track.Title, track.Artist, track.AlbumArtist, track.Album, track.Year,
            track.TrackNumber, track.TrackCount, track.DiscNumber, track.DiscCount);

        var result = TagWriter.WriteTrackTags(track, edit, new ArtPayload(Cover, "image/jpeg"), folderArtPath: null);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Null(result.CoverWarning);
        using var reread = TagLib.File.Create(file.Path);
        var flac = reread.GetTag(TagLib.TagTypes.FlacMetadata, create: false);
        Assert.NotNull(flac);
        Assert.Equal(Cover, Assert.Single(flac.Pictures).Data.Data);
        Assert.Equal(1, TagPadding.Read(file.Path)!.Pictures);
    }
}
