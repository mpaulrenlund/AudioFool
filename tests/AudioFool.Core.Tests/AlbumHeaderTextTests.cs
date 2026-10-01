using AudioFool.Core.Models;

namespace AudioFool.Core.Tests;

/// <summary>
/// The album header's track-count line (spec 6.5): "21 tracks", "1 track".
/// </summary>
public class AlbumHeaderTextTests
{
    [Theory]
    [InlineData(1, "1 track")]
    [InlineData(2, "2 tracks")]
    [InlineData(21, "21 tracks")]
    public void Track_count_is_singular_for_one(int count, string expected)
    {
        var album = AlbumOf(count);
        Assert.Equal(expected, album.TrackCountDisplay);
    }

    private static Album AlbumOf(int count) => new()
    {
        Title = "Album",
        ArtistName = "Artist",
        Tracks = Enumerable.Range(0, count).Select(i => new Track
        {
            FilePath = $@"C:\x\{i}.flac",
            Duration = TimeSpan.FromMinutes(3),
        }).ToList(),
    };
}
