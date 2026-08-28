using AudioFool.Core.Library;
using AudioFool.Core.Models;

namespace AudioFool.Core.Tests;

public class LibrarySearchTests
{
    private static Track T(string title, string artist, string album, string? albumArtist = null) =>
        new()
        {
            FilePath = $@"C:\music\{artist}\{album}\{title}.flac",
            Title = title,
            Artist = artist,
            AlbumArtist = albumArtist ?? artist,
            Album = album,
        };

    private static readonly Track[] Library =
    [
        T("Come Together", "The Beatles", "Abbey Road"),
        T("Something", "The Beatles", "Abbey Road"),
        T("Taxman", "The Beatles", "Revolver"),
        T("Speak to Me", "Pink Floyd", "The Dark Side of the Moon"),
        T("Money", "Pink Floyd", "The Dark Side of the Moon"),
        T("Wish You Were Here", "Pink Floyd", "Wish You Were Here"),
    ];

    private static string[] Titles(IReadOnlyList<Track> tracks) =>
        [.. tracks.Select(t => t.DisplayTitle)];

    [Fact]
    public void An_empty_query_returns_everything()
    {
        Assert.Equal(Library.Length, LibrarySearch.Filter(Library, "").Count);
        Assert.Equal(Library.Length, LibrarySearch.Filter(Library, "   ").Count);
        Assert.Equal(Library.Length, LibrarySearch.Filter(Library, null).Count);
    }

    [Fact]
    public void Searching_an_artist_keeps_their_whole_discography()
    {
        var results = LibrarySearch.Filter(Library, "beatles");

        Assert.Equal(3, results.Count);
        Assert.All(results, t => Assert.Equal("The Beatles", t.Artist));
    }

    [Fact]
    public void Searching_an_album_keeps_all_of_its_tracks()
    {
        var results = LibrarySearch.Filter(Library, "abbey road");

        Assert.Equal(["Come Together", "Something"], Titles(results));
    }

    [Fact]
    public void Searching_a_song_narrows_to_that_track_alone()
    {
        var results = LibrarySearch.Filter(Library, "come together");

        Assert.Equal(["Come Together"], Titles(results));
    }

    [Fact]
    public void Matching_is_case_insensitive()
    {
        Assert.Single(LibrarySearch.Filter(Library, "TAXMAN"));
        Assert.Single(LibrarySearch.Filter(Library, "taxman"));
        Assert.Single(LibrarySearch.Filter(Library, "TaXmAn"));
    }

    [Fact]
    public void A_partial_word_matches()
    {
        // Typing is incremental, so the list has to narrow as you go rather than
        // staying empty until a whole word is entered.
        Assert.Equal(3, LibrarySearch.Filter(Library, "beat").Count);
        Assert.Equal(3, LibrarySearch.Filter(Library, "b").Count);
    }

    [Fact]
    public void Every_term_must_match_but_they_can_match_different_fields()
    {
        // "floyd" is the artist and "dark" is in the album title; neither field
        // contains both, so this only works if terms are matched independently.
        var results = LibrarySearch.Filter(Library, "floyd dark");

        Assert.Equal(["Speak to Me", "Money"], Titles(results));
    }

    [Fact]
    public void Terms_that_cannot_all_match_return_nothing()
    {
        Assert.Empty(LibrarySearch.Filter(Library, "beatles floyd"));
    }

    [Fact]
    public void Extra_whitespace_between_terms_is_ignored()
    {
        Assert.Equal(2, LibrarySearch.Filter(Library, "  floyd    dark  ").Count);
    }

    [Fact]
    public void Album_artist_is_searched_as_well_as_track_artist()
    {
        // Compilations credit each track to its performer, so searching the album
        // artist has to work even though no track's Artist field says it.
        Track[] compilation =
        [
            T("Song One", "Guest Performer", "Big Compilation", albumArtist: "Various Artists"),
        ];

        Assert.Single(LibrarySearch.Filter(compilation, "various"));
        Assert.Single(LibrarySearch.Filter(compilation, "guest"));
    }

    [Fact]
    public void Untagged_files_are_searchable_by_filename()
    {
        // DisplayTitle falls back to the file name, so an untagged file is still
        // findable by what it's called on disk.
        Track[] untagged = [new() { FilePath = @"C:\music\mystery track.mp3" }];

        Assert.Single(LibrarySearch.Filter(untagged, "mystery"));
    }

    [Fact]
    public void Filtered_tracks_rebuild_into_a_correctly_grouped_tree()
    {
        // The search relies on rebuilding the tree from filtered tracks, so the
        // grouping and sort rules must still hold on the subset.
        var library = LibraryScanner.Build(LibrarySearch.Filter(Library, "pink floyd"));

        var artist = Assert.Single(library.Artists);
        Assert.Equal("Pink Floyd", artist.Name);
        Assert.Equal(2, artist.Albums.Count);
        Assert.Equal(3, artist.TrackCount);
    }
}
