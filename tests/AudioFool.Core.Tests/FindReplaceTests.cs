using AudioFool.Core.Library;
using AudioFool.Core.Models;

namespace AudioFool.Core.Tests;

/// <summary>
/// Find and Replace (session 63): what matches and how the result is tidied, then
/// the writes and Undo on copies of the fixtures. A song is written whole or not at
/// all, every artist it holds gets the replace, and Undo puts back exactly what
/// was there.
/// </summary>
public class FindReplaceTests
{
    // ------------------------------------------------------------ matching

    [Fact]
    public void Case_is_ignored_unless_asked()
    {
        Assert.Equal("Song (Live)", new TextReplace("remastered", "Live").Apply("Song (Remastered)"));

        var exact = new TextReplace("remastered", "Live", MatchCase: true);
        Assert.False(exact.Matches("Song (Remastered)"));
        Assert.Null(exact.Apply("Song (Remastered)"));
    }

    [Fact]
    public void Whole_words_keep_a_short_word_out_of_longer_ones()
    {
        var whole = new TextReplace("and", "&", WholeWords: true);
        Assert.Equal("Rock & Roll Band", whole.Apply("Rock and Roll Band"));
        Assert.Null(whole.Apply("Andy Sandman"));

        Assert.Equal("&y S&man", new TextReplace("and", "&").Apply("Andy Sandman"));
    }

    [Fact]
    public void Punctuation_at_an_end_of_the_text_is_a_boundary_of_its_own()
    {
        var whole = new TextReplace("(Live)", "", WholeWords: true);
        Assert.Equal("Song Mix", whole.Apply("Song (Live) Mix"));
        Assert.Equal("Song", whole.Apply("Song(Live)"));
    }

    [Fact]
    public void Accents_always_count()
    {
        Assert.Null(new TextReplace("Motley Crue", "X").Apply("Mötley Crüe"));
        Assert.Equal("M CRÜE", new TextReplace("mötley", "M").Apply("MÖTLEY CRÜE"));
    }

    [Fact]
    public void A_deletion_leaves_one_space_and_no_space_at_the_ends()
    {
        var delete = new TextReplace("(Live)", "");
        Assert.Equal("Song Mix", delete.Apply("Song (Live) Mix"));
        Assert.Equal("Song", delete.Apply("Song (Live)"));
        Assert.Equal("Song", delete.Apply("(Live) Song"));
    }

    [Fact]
    public void Only_the_double_spaces_a_change_makes_are_tidied()
    {
        Assert.Equal("A  B C", new TextReplace("x", "").Apply("A  B x C"));
    }

    [Fact]
    public void A_replacement_with_spaces_of_its_own_meets_its_neighbours_once()
    {
        Assert.Equal("A and B", new TextReplace("&", " and ").Apply("A & B"));
    }

    [Fact]
    public void Every_match_is_replaced_once_from_left_to_right()
    {
        Assert.Equal("abab-abab", new TextReplace("ab", "abab").Apply("ab-ab"));
    }

    [Fact]
    public void A_replacement_that_only_repeats_the_match_changes_nothing()
    {
        var replace = new TextReplace("rush", "Rush");
        Assert.Null(replace.Apply("Rush"));
        Assert.Equal("Rush", replace.Apply("RUSH"));
    }

    [Fact]
    public void Nothing_to_find_matches_nothing()
    {
        var replace = new TextReplace("", "x");
        Assert.False(replace.Matches("abc"));
        Assert.Null(replace.Apply("abc"));
        Assert.Empty(FindReplace.Find([new Track { FilePath = "a.flac", Title = "abc" }], replace));
    }

    [Fact]
    public void Find_lists_each_tag_that_holds_the_text_song_by_song()
    {
        Track[] tracks =
        [
            new() { FilePath = "b.flac", Title = "Two (Live)", Artist = "Band", AlbumArtist = "Band", Album = "Live (Live)" },
            new() { FilePath = "a.flac", Title = "One", Artist = "Live Band", AlbumArtist = "", Album = "Studio" },
            new() { FilePath = "c.flac", Title = "", Artist = "", AlbumArtist = "", Album = "" },
        ];

        var matches = FindReplace.Find(tracks, new TextReplace("live", ""));

        Assert.Equal(
            [("b.flac", ReplaceField.Title, "Two (Live)"), ("b.flac", ReplaceField.Album, "Live (Live)"),
             ("a.flac", ReplaceField.Artist, "Live Band")],
            matches.Select(m => (m.Track.FilePath, m.Field, m.Before)));
    }

    // ------------------------------------------------------------ writes

    private static Track Seed(TempAudioFile file, string title, string artist, string album)
    {
        var edit = new TrackTagEdit(title, artist, artist, album, 2020, 3, 12, 1, 1)
        {
            Details = new TagDetailsEdit { Genre = "Rock", Comment = "Keep me" },
        };
        var result = TagWriter.WriteTrackTags(TagReader.Read(file.Path), edit, art: null, folderArtPath: null);
        Assert.True(result.Success, result.ErrorMessage);
        return result.UpdatedTrack!;
    }

    private static void SetArtists(string path, string[] artists, string[] albumArtists)
    {
        using (var file = TagLib.File.Create(path))
        {
            file.Tag.Performers = artists;
            file.Tag.AlbumArtists = albumArtists;
            file.Save();
        }
    }

    private static (string[] Artists, string[] AlbumArtists) ArtistsOf(string path)
    {
        using var file = TagLib.File.Create(path);
        return (file.Tag.Performers, file.Tag.AlbumArtists);
    }

    private static ReplaceMatch Match(Track track, ReplaceField field) =>
        new(track, field, FindReplace.ValueOf(track, field));

    [Theory]
    [InlineData("sample.flac")]
    [InlineData("sample.mp3")]
    public void A_replace_writes_the_tags_it_is_given_and_nothing_else(string fixture)
    {
        using var file = new TempAudioFile(fixture);
        var track = Seed(file, "Song (Remastered)", "Band (Remastered)", "Album (Remastered)");

        var result = TagWriter.WriteReplacement(track,
            [Match(track, ReplaceField.Title), Match(track, ReplaceField.Album)], new TextReplace("(Remastered)", ""));

        Assert.Equal(ReplaceOutcome.Saved, result.Outcome);
        var reread = TagReader.Read(file.Path);
        Assert.Equal("Song", reread.Title);
        Assert.Equal("Album", reread.Album);
        Assert.Equal("Band (Remastered)", reread.Artist);
        Assert.Equal("Band (Remastered)", reread.AlbumArtist);
        Assert.Equal(2020, reread.Year);
        Assert.Equal(3, reread.TrackNumber);
        Assert.Equal(12, reread.TrackCount);
        var details = TagReader.ReadDetails(file.Path)!;
        Assert.Equal("Rock", details.Genre);
        Assert.Equal("Keep me", details.Comment);

        // The library's copy reads as the file does, stamp included.
        var updated = result.UpdatedTrack!;
        Assert.Equal(("Song", "Album", "Band (Remastered)"), (updated.Title, updated.Album, updated.Artist));
        Assert.True(updated.MatchesFile(new FileInfo(file.Path).Length, File.GetLastWriteTimeUtc(file.Path)));

        Assert.Equal(
            [(ReplaceField.Title, "Song (Remastered)", "Song"), (ReplaceField.Album, "Album (Remastered)", "Album")],
            result.Written.Select(w => (w.Field, string.Join("|", w.Before), string.Join("|", w.After))));
    }

    [Fact]
    public void Every_artist_the_song_holds_gets_the_replace()
    {
        // The library keeps only the first artist; written from it, the guest would go.
        using var file = new TempAudioFile("sample.flac");
        Seed(file, "Song", "x", "Album");
        SetArtists(file.Path, ["Duo (Remastered)", "Guest (Remastered)", "Plain"], ["Duo (Remastered)"]);
        var track = TagReader.Read(file.Path);
        Assert.Equal("Duo (Remastered)", track.Artist);

        var result = TagWriter.WriteReplacement(track,
            [Match(track, ReplaceField.Artist), Match(track, ReplaceField.AlbumArtist)], new TextReplace("(Remastered)", ""));

        Assert.Equal(ReplaceOutcome.Saved, result.Outcome);
        var (artists, albumArtists) = ArtistsOf(file.Path);
        Assert.Equal(["Duo", "Guest", "Plain"], artists);
        Assert.Equal(["Duo"], albumArtists);
        Assert.Equal(("Duo", "Duo"), (result.UpdatedTrack!.Artist, result.UpdatedTrack.AlbumArtist));
    }

    [Fact]
    public void An_artist_the_replace_empties_is_removed()
    {
        using var file = new TempAudioFile("sample.flac");
        Seed(file, "Song", "x", "Album");
        SetArtists(file.Path, ["Duo", "feat. Guest"], ["Duo"]);
        var track = TagReader.Read(file.Path);

        // "Duo" is the artist the library shows; the second value holds the text too.
        var result = TagWriter.WriteReplacement(track,
            [new ReplaceMatch(track, ReplaceField.Artist, "Duo")], new TextReplace("feat. Guest", ""));

        Assert.Equal(ReplaceOutcome.Saved, result.Outcome);
        Assert.Equal(["Duo"], ArtistsOf(file.Path).Artists);
    }

    [Theory]
    [InlineData("sample.flac")]
    [InlineData("sample.mp3")]
    public void A_song_changed_since_Find_is_left_untouched(string fixture)
    {
        using var file = new TempAudioFile(fixture);
        var track = Seed(file, "Song (Remastered)", "Band", "Album (Remastered)");
        var found = new[] { Match(track, ReplaceField.Title), Match(track, ReplaceField.Album) };

        // Retagged elsewhere after Find; the library still has the old title.
        var elsewhere = TagWriter.WriteSelectedTrackTags(track, new TracksTagEdit { Title = "Song (Remastered) [Live]" });
        Assert.True(elsewhere.Success, elsewhere.ErrorMessage);
        var bytes = File.ReadAllBytes(file.Path);
        var written = File.GetLastWriteTimeUtc(file.Path);

        var result = TagWriter.WriteReplacement(track, found, new TextReplace("(Remastered)", ""));

        Assert.Equal(ReplaceOutcome.Changed, result.Outcome);
        Assert.Equal(bytes, File.ReadAllBytes(file.Path));
        Assert.Equal(written, File.GetLastWriteTimeUtc(file.Path));
        Assert.Empty(result.Written);
    }

    [Fact]
    public void A_song_with_no_artist_of_its_own_is_not_given_one()
    {
        // The library shows the album artist as the artist, so Find lists both.
        using var file = new TempAudioFile("sample.flac");
        Seed(file, "Song", "x", "Album");
        SetArtists(file.Path, [], ["Band (Remastered)"]);
        var track = TagReader.Read(file.Path);
        Assert.Equal(("Band (Remastered)", "Band (Remastered)"), (track.Artist, track.AlbumArtist));

        var result = TagWriter.WriteReplacement(track,
            [Match(track, ReplaceField.Artist), Match(track, ReplaceField.AlbumArtist)], new TextReplace("(Remastered)", ""));

        Assert.Equal(ReplaceOutcome.Saved, result.Outcome);
        var (artists, albumArtists) = ArtistsOf(file.Path);
        Assert.Empty(artists);
        Assert.Equal(["Band"], albumArtists);
        Assert.Equal(("Band", "Band"), (result.UpdatedTrack!.Artist, result.UpdatedTrack.AlbumArtist));
    }

    [Fact]
    public void Its_artist_alone_is_written_as_an_artist_of_its_own()
    {
        using var file = new TempAudioFile("sample.flac");
        Seed(file, "Song", "x", "Album");
        SetArtists(file.Path, [], ["Band (Remastered)"]);
        var track = TagReader.Read(file.Path);

        var result = TagWriter.WriteReplacement(track, [Match(track, ReplaceField.Artist)], new TextReplace("(Remastered)", ""));

        Assert.Equal(ReplaceOutcome.Saved, result.Outcome);
        var (artists, albumArtists) = ArtistsOf(file.Path);
        Assert.Equal(["Band"], artists);
        Assert.Equal(["Band (Remastered)"], albumArtists);

        var undo = TagWriter.UndoReplacement(result.UpdatedTrack!, result.Written);
        Assert.Equal(ReplaceOutcome.Saved, undo.Outcome);
        Assert.Empty(ArtistsOf(file.Path).Artists);
    }

    [Theory]
    [InlineData("sample.flac")]
    [InlineData("sample.mp3")]
    public void Undo_puts_back_every_value_as_it_was(string fixture)
    {
        using var file = new TempAudioFile(fixture);
        var track = Seed(file, "Live Song (Live)", "Band", "Live");

        var result = TagWriter.WriteReplacement(track,
            [Match(track, ReplaceField.Title), Match(track, ReplaceField.Album)], new TextReplace("live", ""));
        Assert.Equal(ReplaceOutcome.Saved, result.Outcome);

        // "Live" was the whole album, so the album tag went.
        var replaced = TagReader.Read(file.Path);
        Assert.Equal(("Song ()", ""), (replaced.Title, replaced.Album));

        var undo = TagWriter.UndoReplacement(result.UpdatedTrack!, result.Written);

        Assert.Equal(ReplaceOutcome.Saved, undo.Outcome);
        var reread = TagReader.Read(file.Path);
        Assert.Equal(("Live Song (Live)", "Live", "Band"), (reread.Title, reread.Album, reread.Artist));
        Assert.Equal(("Live Song (Live)", "Live"), (undo.UpdatedTrack!.Title, undo.UpdatedTrack.Album));
    }

    [Fact]
    public void Undo_leaves_alone_a_song_changed_since_the_replace()
    {
        using var file = new TempAudioFile("sample.flac");
        var track = Seed(file, "Song (Remastered)", "Band", "Album");
        var result = TagWriter.WriteReplacement(track, [Match(track, ReplaceField.Title)], new TextReplace("(Remastered)", ""));
        Assert.Equal(ReplaceOutcome.Saved, result.Outcome);

        var elsewhere = TagWriter.WriteSelectedTrackTags(result.UpdatedTrack!, new TracksTagEdit { Title = "Song (2024 Mix)" });
        Assert.True(elsewhere.Success, elsewhere.ErrorMessage);
        var bytes = File.ReadAllBytes(file.Path);

        var undo = TagWriter.UndoReplacement(result.UpdatedTrack!, result.Written);

        Assert.Equal(ReplaceOutcome.Changed, undo.Outcome);
        Assert.Equal(bytes, File.ReadAllBytes(file.Path));
    }

    [Fact]
    public void A_missing_file_fails_without_throwing()
    {
        var ghost = new Track { FilePath = @"C:\does\not\exist\ghost.flac", Title = "Ghost (Live)" };

        var result = TagWriter.WriteReplacement(ghost, [Match(ghost, ReplaceField.Title)], new TextReplace("(Live)", ""));

        Assert.Equal(ReplaceOutcome.Failed, result.Outcome);
        Assert.NotNull(result.ErrorMessage);
    }
}
