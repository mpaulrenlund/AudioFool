using AudioFool.Core.Library;
using AudioFool.Core.Models;

namespace AudioFool.Core.Tests;

public class ArtistSortKeyTests
{
    [Theory]
    [InlineData("The Beatles", "Beatles")]
    [InlineData("the beatles", "beatles")]
    [InlineData("THE WHO", "WHO")]
    [InlineData("The  Doors", "Doors")]      // extra space after the article
    [InlineData("  The Clash  ", "Clash")]   // surrounding whitespace
    public void Strips_a_leading_article(string input, string expected) =>
        Assert.Equal(expected, SortRules.ArtistSortKey(input));

    [Theory]
    [InlineData("Thelonious Monk")]          // "The" is not a whole word here
    [InlineData("Theory of a Deadman")]
    [InlineData("Therapy?")]
    [InlineData("The")]                      // a band literally named "The"
    [InlineData("Radiohead")]
    public void Leaves_everything_else_alone(string input) =>
        Assert.Equal(input, SortRules.ArtistSortKey(input));

    [Fact]
    public void The_The_sorts_under_T_not_as_an_empty_string()
    {
        // Stripping one article off "The The" leaves "The", which is what we want:
        // it still files under T rather than sorting above every other artist.
        Assert.Equal("The", SortRules.ArtistSortKey("The The"));
    }

    [Fact]
    public void An_article_with_nothing_after_it_keeps_the_original()
    {
        Assert.Equal("The", SortRules.ArtistSortKey("The "));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Missing_names_produce_an_empty_key(string? input) =>
        Assert.Equal("", SortRules.ArtistSortKey(input));

    [Fact]
    public void Extra_articles_can_be_opted_into()
    {
        string[] articles = ["The", "A", "An"];
        Assert.Equal("Perfect Circle", SortRules.ArtistSortKey("A Perfect Circle", articles));
        Assert.Equal("Tribe Called Quest", SortRules.ArtistSortKey("A Tribe Called Quest", articles));

        // Still word-boundary aware with the wider list.
        Assert.Equal("Anthrax", SortRules.ArtistSortKey("Anthrax", articles));
        Assert.Equal("Air", SortRules.ArtistSortKey("Air", articles));
    }
}

public class ArtistOrderingTests
{
    [Fact]
    public void The_bands_file_under_their_second_word()
    {
        string[] input = ["Zebra", "The Beatles", "ABBA", "Beck", "The Cure", "Cake"];

        var sorted = SortRules.SortArtistNames(input).ToArray();

        Assert.Equal(
            ["ABBA", "The Beatles", "Beck", "Cake", "The Cure", "Zebra"],
            sorted);
    }

    [Fact]
    public void Thelonious_sorts_after_The_Beatles_because_only_one_is_an_article()
    {
        string[] input = ["Thelonious Monk", "The Beatles", "The Zombies"];

        // Keys are Beatles, Thelonious Monk, Zombies.
        Assert.Equal(
            ["The Beatles", "Thelonious Monk", "The Zombies"],
            SortRules.SortArtistNames(input).ToArray());
    }

    [Fact]
    public void Ordering_is_stable_for_names_sharing_a_sort_key()
    {
        string[] input = ["The Beatles", "Beatles"];

        var sorted = SortRules.SortArtistNames(input).ToArray();

        // Same key, so the tie-break on the display name decides: "Beatles" < "The Beatles".
        Assert.Equal(["Beatles", "The Beatles"], sorted);
    }
}

public class AlbumOrderingTests
{
    private static Album MakeAlbum(string title, int? year) =>
        new() { Title = title, ArtistName = "Test", Year = year };

    [Fact]
    public void Oldest_first_newest_last()
    {
        Album[] input =
        [
            MakeAlbum("Abbey Road", 1969),
            MakeAlbum("Please Please Me", 1963),
            MakeAlbum("Revolver", 1966),
        ];

        Assert.Equal(
            ["Please Please Me", "Revolver", "Abbey Road"],
            SortRules.SortAlbums(input).Select(a => a.Title).ToArray());
    }

    [Fact]
    public void Albums_without_a_year_go_to_the_bottom()
    {
        Album[] input =
        [
            MakeAlbum("Unknown Bootleg", null),
            MakeAlbum("Revolver", 1966),
            MakeAlbum("Another Bootleg", null),
            MakeAlbum("Abbey Road", 1969),
        ];

        Assert.Equal(
            ["Revolver", "Abbey Road", "Another Bootleg", "Unknown Bootleg"],
            SortRules.SortAlbums(input).Select(a => a.Title).ToArray());
    }

    [Fact]
    public void Albums_from_the_same_year_fall_back_to_title()
    {
        Album[] input =
        [
            MakeAlbum("Zuma", 1975),
            MakeAlbum("Tonight's the Night", 1975),
        ];

        Assert.Equal(
            ["Tonight's the Night", "Zuma"],
            SortRules.SortAlbums(input).Select(a => a.Title).ToArray());
    }
}

public class TrackOrderingTests
{
    private static Track MakeTrack(string title, int? track, int? disc = null) =>
        new() { FilePath = $@"C:\music\{title}.flac", Title = title, TrackNumber = track, DiscNumber = disc };

    [Fact]
    public void Ordered_by_track_number()
    {
        Track[] input = [MakeTrack("Third", 3), MakeTrack("First", 1), MakeTrack("Second", 2)];

        Assert.Equal(
            ["First", "Second", "Third"],
            SortRules.SortTracks(input).Select(t => t.Title).ToArray());
    }

    [Fact]
    public void Track_numbers_sort_numerically_not_as_text()
    {
        Track[] input = [MakeTrack("Ten", 10), MakeTrack("Two", 2), MakeTrack("One", 1)];

        Assert.Equal(
            ["One", "Two", "Ten"],
            SortRules.SortTracks(input).Select(t => t.Title).ToArray());
    }

    [Fact]
    public void Disc_number_takes_priority_over_track_number()
    {
        Track[] input =
        [
            MakeTrack("D2T1", 1, disc: 2),
            MakeTrack("D1T2", 2, disc: 1),
            MakeTrack("D1T1", 1, disc: 1),
            MakeTrack("D2T2", 2, disc: 2),
        ];

        Assert.Equal(
            ["D1T1", "D1T2", "D2T1", "D2T2"],
            SortRules.SortTracks(input).Select(t => t.Title).ToArray());
    }

    [Fact]
    public void Untagged_disc_is_treated_as_disc_one()
    {
        Track[] input = [MakeTrack("OnDiscTwo", 1, disc: 2), MakeTrack("NoDiscTag", 1, disc: null)];

        Assert.Equal(
            ["NoDiscTag", "OnDiscTwo"],
            SortRules.SortTracks(input).Select(t => t.Title).ToArray());
    }

    [Fact]
    public void Untagged_track_numbers_sink_to_the_end()
    {
        Track[] input = [MakeTrack("Mystery", null), MakeTrack("Two", 2), MakeTrack("One", 1)];

        Assert.Equal(
            ["One", "Two", "Mystery"],
            SortRules.SortTracks(input).Select(t => t.Title).ToArray());
    }
}
