using System.Globalization;
using System.Text.RegularExpressions;

namespace AudioFool.Core.Equalizer;

/// <summary>
/// Reads a Peace preset (<c>.peace</c>, an INI file; Peace is Equalizer APO's front
/// end). AudioFool runs the part Peace calls "All" (every speaker): the
/// <c>[Frequencies]</c>, <c>[Gains]</c> and <c>[Qualities]</c> sliders as peak
/// filters, the <c>PreAmp</c>, and <c>[Disabled]</c> sliders left out. Anything
/// else that would change the sound is reported in <see cref="EqPreset.Unsupported"/>
/// rather than guessed at: Peace's other filter types (<c>[Filters]</c>; their codes
/// aren't documented), per-speaker gains, Graphic EQ, routing and the surround effect.
/// </summary>
public static partial class PeacePresetReader
{
    /// <summary>Sections that only describe the preset or Peace's own window.</summary>
    private static readonly HashSet<string> Ignored = new(StringComparer.OrdinalIgnoreCase)
    {
        "Speakers", "Sliders", "Configuration",
    };

    /// <summary><c>[General]</c> keys that don't change the sound.</summary>
    private static readonly HashSet<string> HarmlessGeneral = new(StringComparer.OrdinalIgnoreCase)
    {
        "Description", "Configuration Icon",
    };

    public static EqPreset Read(string path)
    {
        var text = File.ReadAllText(path);
        return Parse(Path.GetFileNameWithoutExtension(path), text, path);
    }

    public static EqPreset Parse(string name, string text, string? sourcePath = null)
    {
        var sections = ParseIni(text);
        var unsupported = new List<string>();

        Dictionary<string, string> Section(string section) =>
            sections.TryGetValue(section, out var s) ? s : new(StringComparer.OrdinalIgnoreCase);

        var general = Section("General");
        var preamp = 0.0;
        foreach (var (key, value) in general)
        {
            if (key.Equals("PreAmp", StringComparison.OrdinalIgnoreCase))
            {
                if (!TryNumber(value, out preamp))
                    unsupported.Add($"a preamp it can't read ({value})");
            }
            else if (PerSpeakerPreamp().IsMatch(key))
            {
                if (!TryNumber(value, out var p) || p != 0)
                    unsupported.Add("a preamp for single speakers");
            }
            else if (key.Equals("GraphicEQ", StringComparison.OrdinalIgnoreCase))
            {
                if (value.Trim() != "0")
                    unsupported.Add("Graphic EQ");
            }
            else if (key.Equals("Routing", StringComparison.OrdinalIgnoreCase))
            {
                if (value.Trim().Length > 0)
                    unsupported.Add("channel routing");
            }
            else if (!HarmlessGeneral.Contains(key) && value.Trim().Length > 0)
            {
                unsupported.Add($"a setting AudioFool doesn't know ({key})");
            }
        }

        var frequencies = Numbered(Section("Frequencies"), "Frequency");
        var gains = Numbered(Section("Gains"), "Gain");
        var qualities = Numbered(Section("Qualities"), "Quality");
        var disabled = Numbered(Section("Disabled"), "Disabled");
        var filterTypes = Numbered(Section("Filters"), "Filter");

        var bands = new List<EqBand>();
        foreach (var (n, frequencyText) in frequencies.OrderBy(p => p.Key))
        {
            if (disabled.TryGetValue(n, out var off) && off.Trim() != "0")
                continue;

            var gain = 0.0;
            if (gains.TryGetValue(n, out var gainText) && !TryNumber(gainText, out gain))
            {
                unsupported.Add($"a gain it can't read (slider {n}: {gainText})");
                continue;
            }
            if (filterTypes.ContainsKey(n))
            {
                // Peace's filter-type codes aren't in its help or Equalizer APO's; the
                // stock presets put 14 on a low shelf and 15 on a high shelf, but
                // that's two data points, not a definition. At 0 dB any of these
                // filters changes nothing, so only a slider that's moved counts.
                if (gain != 0)
                    unsupported.Add($"a filter type other than peak (slider {n})");
                continue;
            }

            var frequencyRead = TryNumber(frequencyText, out var frequency) && frequency > 0;
            var q = 0.0;
            var qRead = qualities.TryGetValue(n, out var qText) && TryNumber(qText, out q) && q > 0;
            if (!frequencyRead || !qRead)
            {
                if (gain != 0)
                    unsupported.Add($"a slider it can't read (slider {n})");
                continue;
            }

            bands.Add(EqBand.Peak(frequency, gain, q));
        }

        foreach (var (section, values) in sections)
        {
            if (section.Length == 0 || Ignored.Contains(section) || PerSpeakerShape().IsMatch(section))
                continue;
            if (PerSpeakerGains().IsMatch(section))
            {
                if (values.Values.Any(v => !TryNumber(v, out var g) || g != 0))
                    unsupported.Add("gains for single speakers");
                continue;
            }
            if (section.Equals("Surround", StringComparison.OrdinalIgnoreCase))
            {
                if (values.TryGetValue("Surround On", out var on) && on.Trim() != "0")
                    unsupported.Add("the surround effect");
                continue;
            }
            if (IsKnown(section))
                continue;
            if (values.Count > 0)
                unsupported.Add($"a section AudioFool doesn't know ([{section}])");
        }

        if (bands.Count > EqDesign.MaxBands)
            unsupported.Add($"more than {EqDesign.MaxBands} filters");

        return new EqPreset(name, preamp, bands, unsupported.Distinct().ToList(), EqPresetFormat.Peace, sourcePath);
    }

    private static bool IsKnown(string section) =>
        section.Equals("General", StringComparison.OrdinalIgnoreCase)
        || section.Equals("Frequencies", StringComparison.OrdinalIgnoreCase)
        || section.Equals("Gains", StringComparison.OrdinalIgnoreCase)
        || section.Equals("Qualities", StringComparison.OrdinalIgnoreCase)
        || section.Equals("Disabled", StringComparison.OrdinalIgnoreCase)
        || section.Equals("Filters", StringComparison.OrdinalIgnoreCase);

    /// <summary>Sections by name (case-insensitive), each a key/value map. Keys before any section go under "".</summary>
    private static Dictionary<string, Dictionary<string, string>> ParseIni(string text)
    {
        var sections = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        var current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        sections[""] = current;

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == ';' || line[0] == '#')
                continue;
            if (line[0] == '[' && line[^1] == ']')
            {
                var name = line[1..^1].Trim();
                if (!sections.TryGetValue(name, out current!))
                    sections[name] = current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                continue;
            }

            var eq = line.IndexOf('=');
            if (eq > 0)
                current[line[..eq].Trim()] = line[(eq + 1)..].Trim();
        }

        return sections;
    }

    /// <summary><c>Gain3=1.5</c> under the prefix "Gain" becomes 3 → "1.5".</summary>
    private static Dictionary<int, string> Numbered(Dictionary<string, string> section, string prefix)
    {
        var result = new Dictionary<int, string>();
        foreach (var (key, value) in section)
        {
            if (key.Length > prefix.Length && key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                && int.TryParse(key.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var n))
                result[n] = value;
        }
        return result;
    }

    /// <summary>A number with a point or a comma for decimals (Peace writes the user's locale's).</summary>
    internal static bool TryNumber(string text, out double value) =>
        double.TryParse(text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value)
        && double.IsFinite(value);

    [GeneratedRegex(@"^PreAmp\d+$", RegexOptions.IgnoreCase)]
    private static partial Regex PerSpeakerPreamp();

    [GeneratedRegex(@"^Gains\d+$", RegexOptions.IgnoreCase)]
    private static partial Regex PerSpeakerGains();

    /// <summary>Per-speaker frequencies and qualities: the sliders' layout, which changes nothing without gains.</summary>
    [GeneratedRegex(@"^(Frequencies|Qualities|Filters|Disabled)\d+$", RegexOptions.IgnoreCase)]
    private static partial Regex PerSpeakerShape();
}
