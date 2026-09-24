namespace AudioFool.Formatting;

/// <summary>
/// How the technical columns are rendered. Kept in one place so the track grid,
/// the album header and the now-playing bar all agree.
/// </summary>
public static class Display
{
    /// <summary>"4:07", or "1:02:33" once past an hour.</summary>
    public static string Time(TimeSpan value)
    {
        if (value < TimeSpan.Zero)
            value = TimeSpan.Zero;

        return value.TotalHours >= 1
            ? $"{(int)value.TotalHours}:{value.Minutes:00}:{value.Seconds:00}"
            : $"{value.Minutes}:{value.Seconds:00}";
    }

    /// <summary>"320 kbps". Blank when unknown.</summary>
    public static string Bitrate(int? kbps) =>
        kbps is > 0 ? $"{kbps:N0} kbps" : "";

    /// <summary>
    /// "16 bit", or an em dash for lossy formats where the concept doesn't apply.
    /// DSD reports 1 bit, which is correct and worth showing.
    /// </summary>
    public static string BitDepth(int? bits) =>
        bits is > 0 ? $"{bits} bit" : "—";

    /// <summary>
    /// "44.1 kHz", "192 kHz", "2822.4 kHz" for DSD64. Trailing ".0" is dropped
    /// so the column doesn't read "48.0 kHz".
    /// </summary>
    public static string SampleRate(int? hz)
    {
        if (hz is not > 0)
            return "";

        var khz = hz.Value / 1000.0;
        return khz == Math.Floor(khz)
            ? $"{khz:0} kHz"
            : $"{khz:0.#} kHz";
    }

    /// <summary>Blank rather than "0" for untagged track and disc numbers.</summary>
    public static string Number(int? value) =>
        value is > 0 ? value.Value.ToString() : "";

    /// <summary>
    /// "62.4%", "100%", or "&lt;0.1%" for a share too small to round to anything -
    /// a lone WMA file in 26,000 tracks should not read as "0%".
    /// </summary>
    public static string Percent(double fraction)
    {
        if (fraction <= 0)
            return "0%";

        var percent = fraction * 100;
        return percent < 0.1 ? "<0.1%" : $"{percent:0.#}%";
    }

    /// <summary>
    /// "692 GB", "1.4 TB", "83 MB". Binary units under decimal names, the way
    /// Explorer reports them, so the figure matches what the drive's Properties
    /// dialog says.
    /// </summary>
    public static string Size(long bytes)
    {
        string[] units = ["bytes", "KB", "MB", "GB", "TB"];
        double value = Math.Max(0, bytes);
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{value:N0} {units[0]}"
             : value >= 100 ? $"{value:N0} {units[unit]}"
             : $"{value:0.#} {units[unit]}";
    }

    /// <summary>
    /// A span too long for a timecode: "74 d 3 h", "14 h 20 m", "52 m".
    /// Only the two largest units - nobody needs the seconds of a library.
    /// </summary>
    public static string LongDuration(TimeSpan value)
    {
        if (value.TotalDays >= 1)
            return $"{(int)value.TotalDays:N0} d {value.Hours} h";

        if (value.TotalHours >= 1)
            return $"{(int)value.TotalHours} h {value.Minutes} m";

        return $"{Math.Max(0, (int)value.TotalMinutes)} m";
    }
}
