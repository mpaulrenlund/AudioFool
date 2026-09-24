using System.Globalization;
using System.Text.RegularExpressions;

namespace AudioFool.Core.Library;

/// <summary>
/// What the tag dialog's Year box accepts: a year ("2026"), a month ("2026-10")
/// or a full date ("2026-10-02"), always in that ISO order - the one every tag
/// format's date field uses. The library itself still sorts and shows by year;
/// the rest of the date lives only in the file.
/// </summary>
public static partial class ReleaseDate
{
    /// <summary>
    /// Parses what was typed. <paramref name="normalized"/> is the text to write,
    /// with single-digit months and days padded ("2026-1-2" becomes "2026-01-02").
    /// </summary>
    public static bool TryParse(string? text, out string normalized, out int year)
    {
        normalized = "";
        year = 0;

        var match = Typed().Match(text?.Trim() ?? "");
        return match.Success && TryBuild(match, out normalized, out year);
    }

    /// <summary>
    /// Reads a date as a tag stores it, which can carry a time as well -
    /// "2014-05-01T07:00:00Z" in MP4, "2014-05-01T12:30" in ID3v2.4. Only the
    /// date is kept. Null when the tag holds nothing usable.
    /// </summary>
    public static string? FromTag(string? raw)
    {
        var match = Stored().Match(raw?.Trim() ?? "");
        return match.Success && TryBuild(match, out var normalized, out _) ? normalized : null;
    }

    /// <summary>True when the date names more than the year, and so needs a date field to hold it.</summary>
    public static bool HasMonth(string normalized) => normalized.Length > 4;

    private static bool TryBuild(Match match, out string normalized, out int year)
    {
        normalized = "";
        year = int.Parse(match.Groups["y"].Value, CultureInfo.InvariantCulture);

        // The same range TagReader accepts as a year.
        if (year is <= 1000 or >= 2200)
            return false;

        if (!match.Groups["m"].Success)
        {
            normalized = year.ToString("0000", CultureInfo.InvariantCulture);
            return true;
        }

        var month = int.Parse(match.Groups["m"].Value, CultureInfo.InvariantCulture);
        if (month is < 1 or > 12)
            return false;

        if (!match.Groups["d"].Success)
        {
            normalized = $"{year:0000}-{month:00}";
            return true;
        }

        var day = int.Parse(match.Groups["d"].Value, CultureInfo.InvariantCulture);
        if (day < 1 || day > DateTime.DaysInMonth(year, month))
            return false;

        normalized = $"{year:0000}-{month:00}-{day:00}";
        return true;
    }

    [GeneratedRegex(@"^(?<y>\d{4})(-(?<m>\d{1,2})(-(?<d>\d{1,2}))?)?$")]
    private static partial Regex Typed();

    [GeneratedRegex(@"^(?<y>\d{4})(-(?<m>\d{2})(-(?<d>\d{2}))?)?([T ].*)?$")]
    private static partial Regex Stored();
}
