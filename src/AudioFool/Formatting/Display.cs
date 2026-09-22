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
    /// "3/12" when the file names a total, plain "3" when it doesn't - about one
    /// file in five carries no total, and a bare number is better there than an
    /// invented one. Blank when the number itself is missing, matching
    /// <see cref="Number"/>.
    /// </summary>
    public static string NumberOfTotal(int? value, int? total)
    {
        if (value is not > 0)
            return "";

        return total is > 0 ? $"{value}/{total}" : value.Value.ToString();
    }
}
