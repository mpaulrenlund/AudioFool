using System.Globalization;
using System.Text.RegularExpressions;

namespace AudioFool.Core.Equalizer;

/// <summary>
/// Reads Equalizer APO's own configuration text, the format AutoEq and Room EQ
/// Wizard write: <c>Preamp: -6.2 dB</c> and <c>Filter 1: ON PK Fc 105 Hz Gain 2.4 dB
/// Q 0.70</c> lines. Each line is read the way Equalizer APO reads it
/// (BiQuadFilterFactory.cpp: the same patterns, the same default widths, a comma
/// taken as a decimal point, Room EQ Wizard's "1.000 Hz" taken as 1000 Hz).
/// Peak and shelf filters run; any other filter type or command is reported in
/// <see cref="EqPreset.Unsupported"/>. <c>OFF</c> filters and <c>#</c> comments are skipped.
/// </summary>
public static partial class EqualizerApoTextReader
{
    public static EqPreset Read(string path)
    {
        var text = File.ReadAllText(path);
        return Parse(Path.GetFileNameWithoutExtension(path), text, path);
    }

    public static EqPreset Parse(string name, string text, string? sourcePath = null)
    {
        var preamp = 0.0;
        var bands = new List<EqBand>();
        var unsupported = new List<string>();

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#')
                continue;

            var colon = line.IndexOf(':');
            if (colon <= 0)
                continue; // Equalizer APO ignores a line that isn't a command

            var command = line[..colon].Trim();
            var parameters = line[(colon + 1)..];

            if (command.Equals("Preamp", StringComparison.OrdinalIgnoreCase))
            {
                var m = PreampValue().Match(parameters.Replace(',', '.'));
                if (m.Success && TryNumber(m.Groups[1].Value, out var db))
                    preamp += db; // several preamps add up (Equalizer APO 0.8 and later)
                else
                    unsupported.Add($"a preamp it can't read ({line})");
            }
            else if (command.StartsWith("Filter", StringComparison.OrdinalIgnoreCase)
                     && FilterCommand().IsMatch(command))
            {
                ReadFilter(line, parameters.Replace(',', '.'), bands, unsupported);
            }
            else if (command.Equals("Channel", StringComparison.OrdinalIgnoreCase)
                     && parameters.Trim().Equals("all", StringComparison.OrdinalIgnoreCase))
            {
                // the default: what follows applies to every channel
            }
            else
            {
                unsupported.Add($"{command}: lines");
            }
        }

        if (bands.Count > EqDesign.MaxBands)
            unsupported.Add($"more than {EqDesign.MaxBands} filters");

        return new EqPreset(name, preamp, bands, unsupported.Distinct().ToList(), EqPresetFormat.EqualizerApo, sourcePath);
    }

    private static void ReadFilter(string line, string parameters, List<EqBand> bands, List<string> unsupported)
    {
        var typeMatch = FilterType().Match(parameters);
        if (!typeMatch.Success)
            return; // OFF, or no type: Equalizer APO adds nothing

        var type = typeMatch.Groups[1].Value;
        EqFilterKind kind;
        switch (type)
        {
            case "PK" or "PEQ" or "Modal":
                kind = EqFilterKind.Peak;
                break;
            case "LS" or "LSC":
                kind = EqFilterKind.LowShelf;
                break;
            case "HS" or "HSC":
                kind = EqFilterKind.HighShelf;
                break;
            case "None":
                return;
            default:
                unsupported.Add($"{type} filters");
                return;
        }

        var rest = parameters[(typeMatch.Index + typeMatch.Length)..];
        var shelf = kind != EqFilterKind.Peak;

        var freqMatch = Frequency().Match(rest);
        var frequency = freqMatch.Success ? ReadFrequency(freqMatch.Groups[1].Value) : -1;
        if (!(frequency > 0))
        {
            unsupported.Add($"a filter with no frequency ({line})");
            return;
        }

        var gainMatch = Gain().Match(rest);
        if (!gainMatch.Success || !TryNumber(gainMatch.Groups[1].Value, out var gain))
        {
            unsupported.Add($"a filter with no gain ({line})");
            return;
        }

        var width = 0.0;
        var widthKind = EqWidth.Q;
        if (QValue().Match(rest) is { Success: true } qm && TryNumber(qm.Groups[1].Value, out var q))
            width = q;
        if (!shelf && Bandwidth().Match(rest) is { Success: true } bm && TryNumber(bm.Groups[1].Value, out var bw))
        {
            width = bw;
            widthKind = EqWidth.Bandwidth;
        }
        if (shelf && Slope().Match(rest) is { Success: true } sm && TryNumber(sm.Groups[1].Value, out var slopeDb))
        {
            width = slopeDb;
            widthKind = EqWidth.Slope;
        }

        var corner = false;
        if (width == 0)
        {
            if (!shelf)
            {
                unsupported.Add($"a peak filter with no Q ({line})");
                return;
            }
            width = 0.9; // Equalizer APO: "found out by experimentation with RoomEQWizard"
            widthKind = EqWidth.Slope;
        }
        else if (shelf)
        {
            if (widthKind == EqWidth.Slope)
                width /= 12.0; // a 12 dB slope is S = 1
            corner = !type.EndsWith('C');
        }

        bands.Add(new EqBand(kind, frequency, gain, width, widthKind, corner));
    }

    /// <summary>
    /// Equalizer APO's getFreq: a five-or-more-character number with a point four from
    /// the end and no exponent is Room EQ Wizard's thousands separator ("1.000" is 1000).
    /// </summary>
    private static double ReadFrequency(string text)
    {
        var s = text.Replace(" ", "");
        if (!TryNumber(s, out var value))
            return -1;
        if (s.Length >= 5 && s.IndexOfAny(['e', 'E']) < 0 && s[^4] == '.')
            value *= 1000.0;
        return value;
    }

    private static bool TryNumber(string text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);

    [GeneratedRegex(@"^Filter\s*\d*$", RegexOptions.IgnoreCase)]
    private static partial Regex FilterCommand();

    [GeneratedRegex(@"^\s*([-+0-9.eE]+)\s*dB")]
    private static partial Regex PreampValue();

    [GeneratedRegex(@"^\s*ON\s+([A-Za-z]+)")]
    private static partial Regex FilterType();

    [GeneratedRegex(@"\s+Fc\s*([-+0-9.eE ]+)\s*H\s*z")]
    private static partial Regex Frequency();

    [GeneratedRegex(@"\s+Gain\s*([-+0-9.eE]+)\s*dB")]
    private static partial Regex Gain();

    [GeneratedRegex(@"\s+Q\s*([-+0-9.eE]+)")]
    private static partial Regex QValue();

    [GeneratedRegex(@"\s+BW\s+Oct\s*([-+0-9.eE]+)")]
    private static partial Regex Bandwidth();

    [GeneratedRegex(@"^\s*([-+0-9.eE]+)\s*dB")]
    private static partial Regex Slope();
}
