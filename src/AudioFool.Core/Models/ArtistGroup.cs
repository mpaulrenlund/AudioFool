namespace AudioFool.Core.Models;

/// <summary>
/// One row in the artist sidebar, holding that artist's albums already ordered
/// oldest to newest.
/// </summary>
public sealed class ArtistGroup
{
    /// <summary>The name as displayed, articles intact ("The Beatles").</summary>
    public required string Name { get; init; }

    /// <summary>The name as sorted, leading article stripped ("Beatles").</summary>
    public required string SortKey { get; init; }

    public IReadOnlyList<Album> Albums { get; init; } = [];

    public int TrackCount => Albums.Sum(a => a.Tracks.Count);

    /// <summary>"1 album" or "12 albums" - shown beneath the artist name.</summary>
    public string AlbumSummary => Albums.Count == 1 ? "1 album" : $"{Albums.Count:N0} albums";

    public override string ToString() => Name;
}
