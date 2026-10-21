using AudioFool.Core.Library;
using AudioFool.Core.Models;

namespace AudioFool.Core.Tests;

/// <summary>
/// A scan works from the library as it was when it started; a song saved in the
/// app meanwhile must not be put back to the scan's older copy.
/// </summary>
public class EditsDuringScanTests
{
    private static readonly DateTime Before = new(2026, 10, 20, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Saved = Before.AddMinutes(5);

    private static Track Song(string title, DateTime modified, string? folderArt = null,
                              string path = @"D:\Music\Artist\Album\01.flac") => new()
    {
        FilePath = path,
        Title = title,
        ModifiedUtc = modified,
        FolderArtPath = folderArt,
    };

    private static Dictionary<string, Track> Edited(Track track) =>
        new(StringComparer.OrdinalIgnoreCase) { [track.FilePath] = track };

    [Fact]
    public void A_save_made_during_the_scan_wins_over_the_older_copy()
    {
        var merged = EditsDuringScan.Apply([Song("Old title", Before)], Edited(Song("New title", Saved)));

        Assert.Equal("New title", Assert.Single(merged).Title);
    }

    [Fact]
    public void A_tie_goes_to_the_save_which_may_carry_a_folder_cover_no_stamp_shows()
    {
        // Adopting a folder cover doesn't touch the audio file, so the stamps match.
        var merged = EditsDuringScan.Apply([Song("Title", Before)], Edited(Song("Title", Before, @"D:\Music\Artist\Album\cover.jpg")));

        Assert.Equal(@"D:\Music\Artist\Album\cover.jpg", Assert.Single(merged).FolderArtPath);
    }

    [Fact]
    public void A_file_changed_later_still_keeps_what_the_scan_read()
    {
        // Retagged in another program after the save; the scan read that.
        var merged = EditsDuringScan.Apply([Song("From Mp3tag", Saved.AddMinutes(1))], Edited(Song("Saved here", Saved)));

        Assert.Equal("From Mp3tag", Assert.Single(merged).Title);
    }

    [Fact]
    public void A_song_the_scan_no_longer_has_is_not_brought_back()
    {
        var other = Song("Other", Before, path: @"D:\Music\Artist\Album\02.flac");

        var merged = EditsDuringScan.Apply([other], Edited(Song("Deleted since", Saved)));

        Assert.Equal("Other", Assert.Single(merged).Title);
    }

    [Fact]
    public void Paths_match_in_any_case()
    {
        var scanned = Song("Old title", Before, path: @"d:\music\artist\album\01.flac");

        var merged = EditsDuringScan.Apply([scanned], Edited(Song("New title", Saved)));

        Assert.Equal("New title", Assert.Single(merged).Title);
    }
}
