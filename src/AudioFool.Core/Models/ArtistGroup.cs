using System.Globalization;
using AudioFool.Core.Library;

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

    /// <summary>
    /// When the artist's newest file arrived on the drive. What "recently added"
    /// sorts on; tag edits don't move it. <see cref="DateTime.MinValue"/> when no
    /// track has a date yet.
    /// </summary>
    public DateTime LastAddedUtc =>
        Albums.SelectMany(a => a.Tracks).Select(t => t.AddedUtc ?? DateTime.MinValue).DefaultIfEmpty().Max();

    /// <summary>
    /// "1 album · 0.4 GB" or "12 albums · 14.2 GB" - shown beneath the artist name.
    /// The size covers the albums listed, so it follows the folder ticks.
    /// </summary>
    public string AlbumSummary => SummaryText(null);

    public string SummaryText(IFormatProvider? culture)
    {
        culture ??= CultureInfo.CurrentCulture;
        var albums = Albums.Count == 1 ? "1 album" : string.Format(culture, "{0:N0} albums", Albums.Count);
        return $"{albums} · {LibrarySummary.GigabytesOneDecimal(TotalBytes, culture)}";
    }

    /// <summary>The space the listed albums take; what ARTISTS · SIZE sorts on.</summary>
    public long TotalBytes => Albums.Sum(a => a.Tracks.Sum(t => t.FileSize));

    public override string ToString() => Name;
}
