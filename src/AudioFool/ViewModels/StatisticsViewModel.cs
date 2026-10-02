using System.Windows;
using AudioFool.Core.Library;
using AudioFool.Formatting;

namespace AudioFool.ViewModels;

/// <summary>
/// Backs <see cref="StatisticsWindow"/>. A snapshot, not a live view: every value
/// is formatted once from a <see cref="LibraryStatistics"/> taken when the window
/// opens, so nothing here needs change notification.
/// </summary>
public sealed class StatisticsViewModel
{
    public StatisticsViewModel(LibraryStatistics stats, bool someFoldersHidden)
    {
        Scope = someFoldersHidden
            ? "Counting enabled libraries only - folders unchecked in Libraries are left out."
            : null;

        Tiles =
        [
            new StatTile($"{stats.TrackCount:N0}", "Tracks"),
            new StatTile($"{stats.AlbumCount:N0}", "Albums"),
            new StatTile($"{stats.ArtistCount:N0}", "Artists"),
            new StatTile(Display.LongDuration(stats.TotalDuration), "Play time"),
            new StatTile(Display.Size(stats.TotalBytes), "On disk"),
        ];

        // The leader fills its bar and the rest are drawn against it: five
        // artists who each hold 1-2% of a library would otherwise all be slivers.
        var leader = stats.TopArtists.Count > 0 ? stats.TopArtists[0].TrackCount : 1;
        TopArtists = [.. stats.TopArtists.Select((a, i) => new BarRow(
            $"{i + 1}.  {a.Name}",
            $"{a.TrackCount:N0} tracks · {Display.Percent(a.Share)}",
            (double)a.TrackCount / leader,
            $"{Plural(a.AlbumCount, "album")} · {Display.LongDuration(a.Duration)}")
        {
            ArtistName = a.Name,
            ToolTip = $"Show {a.Name} in the library",
        })];

        FileTypes = [.. stats.FileTypes.Select(s => SliceRow(s))];
        Quality = [.. stats.Quality.Select(s => SliceRow(s))];

        var busiestDecade = stats.Decades.Count > 0 ? stats.Decades.Max(d => d.Count) : 1;
        Decades = [.. stats.Decades.Select(s => new BarRow(
            s.Label,
            $"{s.Count:N0} · {Display.Percent(s.Share)}",
            (double)s.Count / busiestDecade)
        {
            Filter = s.Filter,
            ToolTip = ShowTracks(s.Count),
        })];

        // A complete field has nothing to show, so it is the one row left inert.
        TagGaps = [.. stats.TagGaps.Select(g => g.Missing == 0
            ? new BarRow(g.Field, "Complete", 0, g.Note, isComplete: true)
            : new BarRow(g.Field, $"{g.Missing:N0} missing · {Display.Percent(g.Share)}", g.Share, g.Note)
            {
                Filter = g.Filter,
                ToolTip = ShowTracks(g.Missing) + " to fix them",
            })];

        TagSummary = stats.TrackCount == 0
            ? "No tracks scanned yet."
            : $"{stats.FullyTaggedCount:N0} of {stats.TrackCount:N0} tracks " +
              $"({Display.Percent((double)stats.FullyTaggedCount / stats.TrackCount)}) have a title, artist, " +
              "album, year and track number.";
    }

    public string? Scope { get; }
    public bool HasScope => Scope is not null;

    public IReadOnlyList<StatTile> Tiles { get; }
    public IReadOnlyList<BarRow> TopArtists { get; }
    public IReadOnlyList<BarRow> FileTypes { get; }
    public IReadOnlyList<BarRow> Quality { get; }
    public IReadOnlyList<BarRow> Decades { get; }
    public IReadOnlyList<BarRow> TagGaps { get; }
    public string TagSummary { get; }

    /// <summary>The row the window was closed by clicking, if any.</summary>
    public BarRow? Chosen { get; set; }

    private static BarRow SliceRow(Slice s) => new(
        s.Label,
        $"{s.Count:N0} · {Display.Percent(s.Share)} · {Display.Size(s.Bytes)}",
        s.Share)
    {
        Filter = s.Filter,
        ToolTip = ShowTracks(s.Count),
    };

    private static string ShowTracks(int n) =>
        n == 1 ? "Show this track in the library" : $"Show these {n:N0} tracks in the library";

    private static string Plural(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n:N0} {noun}s";
}

public sealed record StatTile(string Value, string Label);

/// <summary>
/// One labelled bar. The bar is two star columns rather than a scaled rectangle,
/// so it stays pixel-crisp and keeps its rounded end at any fill.
/// </summary>
public sealed class BarRow
{
    public BarRow(string label, string value, double fill, string? detail = null, bool isComplete = false)
    {
        Label = label;
        Value = value;
        Detail = detail;
        IsComplete = isComplete;

        var f = Math.Clamp(double.IsFinite(fill) ? fill : 0, 0, 1);
        Fill = new GridLength(f, GridUnitType.Star);
        Rest = new GridLength(1 - f, GridUnitType.Star);
    }

    public string Label { get; }
    public string Value { get; }
    public string? Detail { get; }

    /// <summary>A tag no track is missing: the value reads "Complete" in the OK colour.</summary>
    public bool IsComplete { get; }

    public GridLength Fill { get; }
    public GridLength Rest { get; }

    /// <summary>Clicking narrows the library to these tracks.</summary>
    public TrackFilter? Filter { get; init; }

    /// <summary>Clicking selects this artist in the sidebar instead of filtering.</summary>
    public string? ArtistName { get; init; }

    public string? ToolTip { get; init; }

    public bool IsClickable => Filter is not null || ArtistName is not null;
}
