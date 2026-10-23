using AudioFool.Core.Library;

namespace AudioFool.Core.Tests;

/// <summary>
/// The song window shows one artist where a file can hold several. Until session
/// 69 every save wrote the box as the only value, so fixing a title dropped a
/// song's second and later artists (session 60's review).
/// </summary>
public class TrackEditArtistsTests
{
    private static void Tag(string path, string[] performers, string[] albumArtists)
    {
        using var file = TagLib.File.Create(path);
        file.Tag.Performers = performers;
        file.Tag.AlbumArtists = albumArtists;
        file.Save();
    }

    private static (string[] Performers, string[] AlbumArtists) Artists(string path)
    {
        using var file = TagLib.File.Create(path);
        return (file.Tag.Performers, file.Tag.AlbumArtists);
    }

    /// <summary>The song window's edit as Save builds it, the boxes as they opened with only the title changed.</summary>
    private static TrackTagEdit TitleOnly(Models.Track track) =>
        new("A New Title", track.Artist, track.AlbumArtist, track.Album, track.Year,
            track.TrackNumber, track.TrackCount, track.DiscNumber, track.DiscCount);

    [Theory]
    [InlineData("sample.flac")]
    [InlineData("sample.mp3")]
    public void A_title_fix_keeps_every_artist(string fixture)
    {
        using var file = new TempAudioFile(fixture);
        Tag(file.Path, ["Artist A", "Artist B"], ["Band X", "Band Y"]);
        var track = TagReader.Read(file.Path);

        var result = TagWriter.WriteTrackTags(track, TitleOnly(track), art: null, folderArtPath: null);

        Assert.True(result.Success, result.ErrorMessage);
        var (performers, albumArtists) = Artists(file.Path);
        Assert.Equal(["Artist A", "Artist B"], performers);
        Assert.Equal(["Band X", "Band Y"], albumArtists);
        Assert.Equal("A New Title", TagReader.Read(file.Path).Title);
    }

    [Theory]
    [InlineData("sample.flac")]
    [InlineData("sample.mp3")]
    public void A_changed_artist_is_written_as_the_one_value(string fixture)
    {
        using var file = new TempAudioFile(fixture);
        Tag(file.Path, ["Artist A", "Artist B"], ["Band X"]);
        var track = TagReader.Read(file.Path);

        var result = TagWriter.WriteTrackTags(track, TitleOnly(track) with { Artist = "Artist C" },
            art: null, folderArtPath: null);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(["Artist C"], Artists(file.Path).Performers);
        Assert.Equal(["Band X"], Artists(file.Path).AlbumArtists);
        Assert.Equal("Artist C", result.UpdatedTrack!.Artist);
    }

    /// <summary>A song with only an album artist shows it in the Artist box; saving doesn't make it an artist tag.</summary>
    [Fact]
    public void A_song_with_only_an_album_artist_is_not_given_an_artist()
    {
        using var file = new TempAudioFile("sample.flac");
        Tag(file.Path, [], ["Band X"]);
        var track = TagReader.Read(file.Path);
        Assert.Equal("Band X", track.Artist);

        var result = TagWriter.WriteTrackTags(track, TitleOnly(track), art: null, folderArtPath: null);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Empty(Artists(file.Path).Performers);
        Assert.Equal(["Band X"], Artists(file.Path).AlbumArtists);
    }

    /// <summary>
    /// There, a changed album artist with the Artist box left alone: no artist tag
    /// is written, and the library's copy shows the new name, as a scan would.
    /// </summary>
    [Fact]
    public void The_library_copy_follows_the_album_artist_the_artist_box_showed()
    {
        using var file = new TempAudioFile("sample.flac");
        Tag(file.Path, [], ["Band X"]);
        var track = TagReader.Read(file.Path);

        var result = TagWriter.WriteTrackTags(track, TitleOnly(track) with { AlbumArtist = "Band Z" },
            art: null, folderArtPath: null);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Empty(Artists(file.Path).Performers);
        var reread = TagReader.Read(file.Path);
        Assert.Equal(("Band Z", "Band Z"), (reread.Artist, reread.AlbumArtist));
        Assert.Equal(("Band Z", "Band Z"), (result.UpdatedTrack!.Artist, result.UpdatedTrack.AlbumArtist));
    }

    [Fact]
    public void An_artist_emptied_on_purpose_is_cleared()
    {
        using var file = new TempAudioFile("sample.mp3");
        Tag(file.Path, ["Artist A", "Artist B"], ["Band X"]);
        var track = TagReader.Read(file.Path);

        var result = TagWriter.WriteTrackTags(track, TitleOnly(track) with { AlbumArtist = "" },
            art: null, folderArtPath: null);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Empty(Artists(file.Path).AlbumArtists);
        Assert.Equal(["Artist A", "Artist B"], Artists(file.Path).Performers);
    }
}
