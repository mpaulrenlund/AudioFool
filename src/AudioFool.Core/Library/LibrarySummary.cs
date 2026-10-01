using System.Globalization;
using AudioFool.Core.Models;

namespace AudioFool.Core.Library;

/// <summary>
/// The library totals in the status bar (spec 6.8):
/// "499 artists · 2,376 albums · 26,795 tracks · 692 GB".
/// </summary>
public static class LibrarySummary
{
    private const double BytesPerGigabyte = 1024d * 1024 * 1024;

    public static string Totals(int artists, int albums, IReadOnlyCollection<Track> tracks, IFormatProvider? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        return string.Format(culture, "{0:N0} artists · {1:N0} albums · {2:N0} tracks · {3}",
            artists, albums, tracks.Count, Gigabytes(TotalBytes(tracks), culture));
    }

    public static long TotalBytes(IEnumerable<Track> tracks) => tracks.Sum(t => t.FileSize);

    /// <summary>
    /// Whole gigabytes of 1,024³ bytes, the unit File Explorer calls GB, rounded
    /// to the nearest, with a thousands separator: "692 GB", "1,204 GB".
    /// </summary>
    public static string Gigabytes(long bytes, IFormatProvider? culture = null)
    {
        var gigabytes = Math.Round(Math.Max(0, bytes) / BytesPerGigabyte, MidpointRounding.AwayFromZero);
        return string.Format(culture ?? CultureInfo.CurrentCulture, "{0:N0} GB", gigabytes);
    }
}
