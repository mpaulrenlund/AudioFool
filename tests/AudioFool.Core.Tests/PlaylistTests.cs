using AudioFool.Core.Models;
using AudioFool.Core.Playlists;

namespace AudioFool.Core.Tests;

/// <summary>
/// Playlists and Liked: what is saved, the order they're listed in, finding the
/// songs again after a drive-letter change, and never losing a song whose file
/// can't be found.
/// </summary>
public class PlaylistTests
{
    private static readonly DateTime Monday = new(2026, 10, 5, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Tuesday = Monday.AddDays(1);
    private static readonly DateTime Wednesday = Monday.AddDays(2);

    private static string NewPath() =>
        Path.Combine(Path.GetTempPath(), "AudioFoolTests", Guid.NewGuid().ToString("N"), "playlists.json");

    private static Track T(string path, string artist = "Jingoro", string album = "Back", int seconds = 60) => new()
    {
        FilePath = path,
        Artist = artist,
        AlbumArtist = artist,
        Album = album,
        Title = Path.GetFileNameWithoutExtension(path),
        Duration = TimeSpan.FromSeconds(seconds),
    };

    [Fact]
    public void A_new_store_has_Liked_and_saves_it()
    {
        var path = NewPath();

        var store = PlaylistStore.Load(path, Monday);

        var liked = Assert.Single(store.Playlists);
        Assert.True(liked.IsLiked);
        Assert.Equal("Liked", liked.Name);
        Assert.Same(liked, store.Liked);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void Liking_adds_to_the_end_and_a_round_trip_keeps_everything()
    {
        var path = NewPath();
        var store = PlaylistStore.Load(path, Monday);

        Assert.Equal(1, store.Add(store.Liked, [T(@"D:\Music\a.flac")], Tuesday));
        Assert.Equal(1, store.Add(store.Liked, [T(@"D:\Music\b.flac")], Tuesday));
        var mix = store.Create("Road trip", Wednesday);
        store.Add(mix, [T(@"D:\Music\b.flac"), T(@"D:\Music\a.flac")], Wednesday);

        var again = PlaylistStore.Load(path, Wednesday);

        Assert.Equal([@"D:\Music\a.flac", @"D:\Music\b.flac"], again.Liked.Entries.Select(e => e.FilePath));
        Assert.Equal(Tuesday, again.Liked.ModifiedUtc);
        var loaded = again.Playlists.Single(p => !p.IsLiked);
        Assert.Equal("Road trip", loaded.Name);
        Assert.Equal(mix.Id, loaded.Id);
        Assert.Equal([@"D:\Music\b.flac", @"D:\Music\a.flac"], loaded.Entries.Select(e => e.FilePath));
        Assert.Equal(new PlaylistEntry(@"D:\Music\b.flac", "Jingoro", "Back"), loaded.Entries[0]);
    }

    [Fact]
    public void A_song_already_there_is_not_added_twice_and_the_date_stays()
    {
        var store = PlaylistStore.Load(NewPath(), Monday);
        store.Add(store.Liked, [T(@"D:\Music\a.flac")], Tuesday);

        Assert.Equal(0, store.Add(store.Liked, [T(@"d:\music\A.FLAC")], Wednesday));

        Assert.Single(store.Liked.Entries);
        Assert.Equal(Tuesday, store.Liked.ModifiedUtc);
    }

    [Fact]
    public void Unliking_removes_the_song_and_stamps_the_date()
    {
        var store = PlaylistStore.Load(NewPath(), Monday);
        store.Add(store.Liked, [T(@"D:\Music\a.flac"), T(@"D:\Music\b.flac")], Tuesday);

        Assert.Equal(1, store.Remove(store.Liked, [@"D:\MUSIC\a.flac"], Wednesday));

        Assert.Equal([@"D:\Music\b.flac"], store.Liked.Entries.Select(e => e.FilePath));
        Assert.Equal(Wednesday, store.Liked.ModifiedUtc);
        Assert.Equal(0, store.Remove(store.Liked, [@"D:\Music\zzz.flac"], Wednesday.AddDays(1)));
        Assert.Equal(Wednesday, store.Liked.ModifiedUtc);
    }

    [Fact]
    public void Playlists_are_listed_most_recently_modified_first()
    {
        var store = PlaylistStore.Load(NewPath(), Monday);
        var older = store.Create("Older", Tuesday);
        var newer = store.Create("Newer", Wednesday);

        Assert.Equal([newer, older, store.Liked], store.ByRecent());

        store.Add(store.Liked, [T(@"D:\Music\a.flac")], Wednesday.AddHours(1));
        Assert.Equal([store.Liked, newer, older], store.ByRecent());

        store.Rename(older, "Oldest", Wednesday.AddHours(2));
        Assert.Equal(older, store.ByRecent()[0]);
    }

    [Fact]
    public void Names_must_be_given_and_unique_and_Liked_cannot_be_renamed_or_deleted()
    {
        var store = PlaylistStore.Load(NewPath(), Monday);
        var mix = store.Create("Mix", Monday);

        Assert.NotNull(store.NameProblem("   "));
        Assert.NotNull(store.NameProblem("liked"));
        Assert.NotNull(store.NameProblem(" MIX "));
        Assert.Null(store.NameProblem("Mix", renaming: mix));
        Assert.Null(store.NameProblem("Other"));
        Assert.Throws<ArgumentException>(() => store.Create("mix", Tuesday));

        Assert.Throws<InvalidOperationException>(() => store.Rename(store.Liked, "Loved", Tuesday));
        Assert.False(store.Delete(store.Liked));
        Assert.True(store.Delete(mix));
        Assert.Single(store.Playlists);
    }

    [Fact]
    public void An_unreadable_file_is_moved_aside_rather_than_saved_over()
    {
        var path = NewPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ not json");

        var store = PlaylistStore.Load(path, Monday);

        Assert.Single(store.Playlists);
        var aside = Directory.GetFiles(Path.GetDirectoryName(path)!, "playlists.unreadable-*.json");
        Assert.Equal("{ not json", File.ReadAllText(Assert.Single(aside)));
    }

    [Fact]
    public void A_picture_is_copied_in_and_replaced_and_cleared()
    {
        var path = NewPath();
        var store = PlaylistStore.Load(path, Monday);
        var mix = store.Create("Mix", Monday);
        var source = Path.Combine(Path.GetDirectoryName(path)!, "photo.PNG");
        File.WriteAllBytes(source, [1, 2, 3]);

        store.SetPicture(mix, source, Tuesday);
        var first = store.PicturePath(mix)!;
        Assert.True(File.Exists(first));
        Assert.EndsWith(".png", first);
        Assert.Equal(Tuesday, mix.ModifiedUtc);

        File.Delete(source);
        Assert.True(File.Exists(first));

        File.WriteAllBytes(source, [4, 5]);
        store.SetPicture(mix, source, Wednesday);
        Assert.False(File.Exists(first));
        Assert.Equal([4, 5], File.ReadAllBytes(store.PicturePath(mix)!));

        var second = store.PicturePath(mix)!;
        store.ClearPicture(mix, Wednesday.AddHours(1));
        Assert.Null(mix.Picture);
        Assert.False(File.Exists(second));
    }

    [Fact]
    public void Songs_are_found_by_path_in_playlist_order()
    {
        var a = T(@"D:\Music\Jingoro\Back\01.flac", seconds: 100);
        var b = T(@"D:\Music\Jingoro\Back\02.flac", seconds: 50);
        var store = PlaylistStore.Load(NewPath(), Monday);
        store.Add(store.Liked, [b, a], Monday);

        var resolved = new PlaylistResolver([a, b]).Resolve(store.Liked);

        Assert.Equal([b, a], resolved.Tracks);
        Assert.Equal(0, resolved.Missing);
        Assert.False(resolved.Changed);
        Assert.Equal(TimeSpan.FromSeconds(150), resolved.Duration);
    }

    [Fact]
    public void After_a_drive_letter_change_songs_are_found_and_re_pointed()
    {
        var store = PlaylistStore.Load(NewPath(), Monday);
        store.Add(store.Liked, [T(@"D:\Music\Jingoro\Back\01.flac"), T(@"D:\Music\Other\X\01.flac", "Other", "X")], Monday);

        var moved = new[]
        {
            T(@"E:\Music\Jingoro\Back\01.flac"),
            T(@"E:\Music\Other\X\01.flac", "Other", "X"),
        };
        var resolved = new PlaylistResolver(moved).Resolve(store.Liked);

        Assert.Equal(moved, resolved.Tracks);
        Assert.True(resolved.Changed);
        Assert.Equal([@"E:\Music\Jingoro\Back\01.flac", @"E:\Music\Other\X\01.flac"],
                     store.Liked.Entries.Select(e => e.FilePath));
        Assert.Equal(Monday, store.Liked.ModifiedUtc);
    }

    [Fact]
    public void The_same_file_name_on_another_album_is_not_taken_for_the_song()
    {
        var store = PlaylistStore.Load(NewPath(), Monday);
        store.Add(store.Liked, [T(@"D:\Music\Jingoro\Back\01.flac")], Monday);

        var resolved = new PlaylistResolver([T(@"E:\Music\Jingoro\Forward\01.flac", album: "Forward")]).Resolve(store.Liked);

        Assert.Empty(resolved.Tracks);
        Assert.Equal(1, resolved.Missing);
    }

    [Fact]
    public void A_song_not_in_the_library_stays_in_the_playlist()
    {
        var path = NewPath();
        var store = PlaylistStore.Load(path, Monday);
        var kept = T(@"D:\Music\a.flac");
        var gone = T(@"D:\Music\gone.flac");
        store.Add(store.Liked, [kept, gone], Monday);

        var resolved = new PlaylistResolver([kept]).Resolve(store.Liked);
        store.Save();

        Assert.Equal([kept], resolved.Tracks);
        Assert.Equal(1, resolved.Missing);
        Assert.Equal(2, PlaylistStore.Load(path, Tuesday).Liked.Entries.Count);
    }

    [Fact]
    public void A_retagged_song_is_still_found_and_its_entry_takes_the_new_names()
    {
        var store = PlaylistStore.Load(NewPath(), Monday);
        store.Add(store.Liked, [T(@"D:\Music\a.flac", album: "Old title")], Monday);

        var retagged = T(@"D:\Music\a.flac", album: "New title");
        var resolved = new PlaylistResolver([retagged]).Resolve(store.Liked);

        Assert.Equal([retagged], resolved.Tracks);
        Assert.True(resolved.Changed);
        Assert.Equal("New title", store.Liked.Entries[0].Album);
    }

    /// <summary>A playlist of a.flac, b.flac, … in that order, made on Monday.</summary>
    private static (PlaylistStore Store, Playlist Mix, string Path) Letters(string letters)
    {
        var path = NewPath();
        var store = PlaylistStore.Load(path, Monday);
        var mix = store.Create("Mix", Monday);
        store.Add(mix, letters.Select(c => T($@"D:\Music\{c}.flac")), Monday);
        return (store, mix, path);
    }

    private static string Order(Playlist playlist) =>
        string.Concat(playlist.Entries.Select(e => Path.GetFileNameWithoutExtension(e.FilePath)));

    private static string P(char c) => $@"D:\Music\{c}.flac";

    [Theory]
    [InlineData("abcde", "d", 'b', "adbce")]    // up
    [InlineData("abcde", "b", 'e', "acdbe")]    // down, before e
    [InlineData("abcde", "b", null, "acdeb")]   // to the end
    [InlineData("abcde", "a", 'a', "abcde")]    // onto itself
    [InlineData("abcde", "eb", 'a', "beacd")]   // a block keeps playlist order, not selection order
    [InlineData("abcde", "bd", 'c', "abdce")]   // dropped on one of its own: before the next one left
    [InlineData("abcde", "bd", 'd', "acbde")]
    [InlineData("abcde", "ae", 'c', "baecd")]
    public void Songs_move_as_a_block_to_just_before_the_target(string start, string moving, char? before, string expected)
    {
        var (store, mix, path) = Letters(start);

        var moved = store.Move(mix, moving.Select(P), before is { } b ? P(b) : null, Tuesday);

        Assert.Equal(expected, Order(mix));
        Assert.Equal(expected != start, moved);
        Assert.Equal(expected, Order(PlaylistStore.Load(path, Wednesday).Playlists.Single(p => p.Name == "Mix")));
    }

    [Fact]
    public void A_move_stamps_the_date_and_one_that_changes_nothing_does_not()
    {
        var (store, mix, _) = Letters("abc");

        Assert.False(store.Move(mix, [P('b')], P('c'), Tuesday));
        Assert.False(store.Move(mix, [@"D:\Music\zzz.flac"], P('a'), Tuesday));
        Assert.Equal(Monday, mix.ModifiedUtc);

        Assert.True(store.Move(mix, [@"D:\MUSIC\C.FLAC"], P('a'), Wednesday));
        Assert.Equal("cab", Order(mix));
        Assert.Equal(Wednesday, mix.ModifiedUtc);
    }

    [Fact]
    public void Songs_not_found_keep_their_place_among_the_rest()
    {
        // m isn't in the library, so the table shows a, b, c, d.
        var (store, mix, _) = Letters("abmcd");

        store.Move(mix, [P('d')], P('c'), Tuesday);
        Assert.Equal("abmdc", Order(mix));

        store.Move(mix, [P('a')], null, Tuesday);
        Assert.Equal("bmdca", Order(mix));
        Assert.Equal(5, mix.Entries.Count);
    }

    [Fact]
    public void Header_text()
    {
        Assert.Equal("1 track", PlaylistText.TrackCount(1));
        Assert.Equal("1,204 tracks", PlaylistText.TrackCount(1204));
        Assert.Equal("12 tracks", PlaylistText.HeaderTrackCount(12, 0));
        Assert.Equal("12 tracks · 2 not found", PlaylistText.HeaderTrackCount(12, 2));

        var local = new DateTime(2026, 10, 7, 23, 30, 0, DateTimeKind.Local);
        Assert.Equal("Modified 2026-10-07", PlaylistText.Modified(local.ToUniversalTime()));

        Assert.Equal("No liked songs yet", PlaylistText.Empty(isLiked: true, missing: 0).Title);
        Assert.Equal("This playlist is empty", PlaylistText.Empty(isLiked: false, missing: 0).Title);
        Assert.Contains("3 songs", PlaylistText.Empty(isLiked: false, missing: 3).Detail);
    }
}
