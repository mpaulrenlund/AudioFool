namespace AudioFool.Core.Models;

/// <summary>
/// A set of tracks sharing an album artist and album title.
/// </summary>
public sealed class Album
{
    public required string Title { get; init; }

    /// <summary>The album artist this album hangs under in the sidebar.</summary>
    public required string ArtistName { get; init; }

    /// <summary>Earliest year found across the album's tracks; null when untagged.</summary>
    public int? Year { get; init; }

    /// <summary>
    /// Earliest <see cref="Track.SortDate"/> across the tracks - a full date when
    /// the files carry one, else the year - so two albums from the same year
    /// still sort in release order. Null when untagged.
    /// </summary>
    public string? SortDate { get; init; }

    public IReadOnlyList<Track> Tracks { get; init; } = [];

    /// <summary>Cover image found alongside the files (cover.jpg, folder.jpg, ...).</summary>
    public string? FolderArtPath { get; init; }

    public int DiscCount => Tracks
        .Select(t => t.DiscNumber ?? 1)
        .DefaultIfEmpty(1)
        .Distinct()
        .Count();

    public TimeSpan TotalDuration =>
        TimeSpan.FromTicks(Tracks.Sum(t => t.Duration.Ticks));

    /// <summary>"1975" or "Year unknown", for the album list subtitle.</summary>
    public string YearDisplay => Year?.ToString() ?? "Year unknown";

    /// <summary>"1 track" / "21 tracks", for the album header.</summary>
    public string TrackCountDisplay => Tracks.Count == 1 ? "1 track" : $"{Tracks.Count} tracks";


    public override string ToString() => $"{ArtistName} - {Title}";
}
