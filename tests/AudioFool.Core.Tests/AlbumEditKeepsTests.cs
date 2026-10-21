using AudioFool.Core.Library;
using AudioFool.Core.Models;

namespace AudioFool.Core.Tests;

/// <summary>
/// An album edit writes only the fields it sets. Until session 60 Artist, Album
/// Artist, Album and Year were always written, from the first track: a
/// compilation lost its artists to track 1's, and an untagged album was given the
/// words "Unknown Album".
/// </summary>
public class AlbumEditKeepsTests
{
    /// <summary>A track of a compilation, tagged with its own artist and year.</summary>
    private static Track Seeded(TempAudioFile file, string title, string artist, int year, string album = "Hits")
    {
        var result = TagWriter.WriteTrackTags(TagReader.Read(file.Path),
            new TrackTagEdit(title, artist, "Various Artists", album, year, 1, 2, 1, 1), art: null, folderArtPath: null);
        Assert.True(result.Success, result.ErrorMessage);
        return result.UpdatedTrack!;
    }

    private static readonly AlbumTagEdit GenreOnly = new() { Details = new TagDetailsEdit { Genre = "Pop" } };

    [Theory]
    [InlineData("sample.flac")]
    [InlineData("sample.mp3")]
    public void Changing_only_the_genre_leaves_each_tracks_artist_album_and_year(string fixture)
    {
        using var one = new TempAudioFile(fixture);
        using var two = new TempAudioFile(fixture);
        var first = Seeded(one, "One", "Artist A", 1994);
        var second = Seeded(two, "Two", "Artist B", 2001);

        foreach (var track in new[] { first, second })
            Assert.True(TagWriter.WriteAlbumTrackTags(track, GenreOnly, art: null, folderArtPath: null).Success);

        var a = TagReader.Read(one.Path);
        var b = TagReader.Read(two.Path);
        Assert.Equal(("Artist A", "Various Artists", "Hits", 1994), (a.Artist, a.AlbumArtist, a.Album, a.Year));
        Assert.Equal(("Artist B", "Various Artists", "Hits", 2001), (b.Artist, b.AlbumArtist, b.Album, b.Year));
        Assert.Equal("Pop", TagReader.ReadDetails(one.Path)!.Genre);
        Assert.Equal("Pop", TagReader.ReadDetails(two.Path)!.Genre);
    }

    [Fact]
    public void The_library_copy_keeps_what_the_edit_leaves_alone()
    {
        using var file = new TempAudioFile("sample.flac");
        var track = Seeded(file, "One", "Artist A", 1994);

        var result = TagWriter.WriteAlbumTrackTags(track, GenreOnly, art: null, folderArtPath: null);

        var updated = result.UpdatedTrack!;
        Assert.Equal(("Artist A", "Various Artists", "Hits", 1994), (updated.Artist, updated.AlbumArtist, updated.Album, updated.Year));
    }

    [Fact]
    public void An_album_with_no_album_tag_is_not_given_one()
    {
        using var file = new TempAudioFile("sample.flac");
        var track = Seeded(file, "One", "Artist A", 1994, album: "");
        Assert.Equal("", TagReader.Read(file.Path).Album);

        Assert.True(TagWriter.WriteAlbumTrackTags(track, GenreOnly, art: null, folderArtPath: null).Success);

        Assert.Equal("", TagReader.Read(file.Path).Album);
        Assert.Equal(LibraryScanner.UnknownAlbum, LibraryScanner.Build([TagReader.Read(file.Path)]).Artists[0].Albums[0].Title);
    }

    [Fact]
    public void A_value_typed_for_the_album_reaches_every_track()
    {
        using var one = new TempAudioFile("sample.flac");
        using var two = new TempAudioFile("sample.mp3");
        var tracks = new[] { Seeded(one, "One", "Artist A", 1994), Seeded(two, "Two", "Artist B", 2001) };
        var edit = new AlbumTagEdit { Album = "Greatest Hits", Date = new DateEdit(2010, null) };

        foreach (var track in tracks)
            Assert.True(TagWriter.WriteAlbumTrackTags(track, edit, art: null, folderArtPath: null).Success);

        foreach (var path in new[] { one.Path, two.Path })
        {
            var read = TagReader.Read(path);
            Assert.Equal(("Greatest Hits", 2010), (read.Album, read.Year));
        }
        Assert.Equal("Artist A", TagReader.Read(one.Path).Artist);
        Assert.Equal("Artist B", TagReader.Read(two.Path).Artist);
    }

    [Fact]
    public void An_artist_emptied_on_purpose_is_cleared()
    {
        using var file = new TempAudioFile("sample.flac");
        var track = Seeded(file, "One", "Artist A", 1994);

        Assert.True(TagWriter.WriteAlbumTrackTags(track, new AlbumTagEdit { AlbumArtist = "" }, art: null, folderArtPath: null).Success);

        var read = TagReader.Read(file.Path);
        Assert.Equal("", read.AlbumArtist);
        Assert.Equal("Artist A", read.Artist);
    }
}
