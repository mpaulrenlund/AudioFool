using System.Globalization;
using System.Text.Json;

namespace AudioFool.Core.Theme;

/// <summary>
/// The PS1 theme's design tokens, read from <c>design/theme-tokens.json</c>.
/// <para>
/// Every leaf of the file is kept under its JSON path - <c>color.panel.bg</c>,
/// <c>albums.rowHeight</c>, <c>songTable.columns.time.width</c> - and that path is
/// the resource name the app uses, so the spec, the file and the XAML share one
/// vocabulary. An array of objects with an <c>id</c> is addressed by id rather than
/// by index, so a reordered column keeps its name.
/// </para>
/// <para>
/// Strings are typed by where they live: under <c>color</c> they must be colours,
/// under <c>shadow</c> CSS box shadows, and anywhere else they are kept as text
/// (alignments, and the prose rules the code implements as calculations). A bad
/// value throws <see cref="ThemeTokenException"/> naming its path, rather than
/// leaving a key quietly missing.
/// </para>
/// No WPF here: the app turns these into brushes, styles and effects.
/// </summary>
public sealed class ThemeTokens
{
    public required IReadOnlyDictionary<string, ThemeColor> Colors { get; init; }
    public required IReadOnlyDictionary<string, double> Numbers { get; init; }
    public required IReadOnlyDictionary<string, bool> Flags { get; init; }
    public required IReadOnlyDictionary<string, string> Texts { get; init; }
    public required IReadOnlyDictionary<string, IReadOnlyList<string>> Lists { get; init; }
    public required IReadOnlyDictionary<string, IReadOnlyList<ShadowLayer>> Shadows { get; init; }

    /// <summary>The <c>type.*</c> groups, one per text role, with their colour resolved to a full path.</summary>
    public required IReadOnlyList<TextStyleToken> TextStyles { get; init; }

    /// <summary><c>font.family</c>, in fallback order.</summary>
    public IReadOnlyList<string> FontFamily => Lists.TryGetValue("font.family", out var f) ? f : [];

    public static ThemeTokens Parse(string json)
    {
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });

        var colors = new Dictionary<string, ThemeColor>(StringComparer.Ordinal);
        var numbers = new Dictionary<string, double>(StringComparer.Ordinal);
        var flags = new Dictionary<string, bool>(StringComparer.Ordinal);
        var texts = new Dictionary<string, string>(StringComparer.Ordinal);
        var lists = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        var shadows = new Dictionary<string, IReadOnlyList<ShadowLayer>>(StringComparer.Ordinal);

        void Walk(JsonElement e, string path)
        {
            switch (e.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (var p in e.EnumerateObject())
                    {
                        // "$description" and the like are notes about the file.
                        if (p.Name.StartsWith('$'))
                            continue;
                        Walk(p.Value, Join(path, p.Name));
                    }
                    break;

                case JsonValueKind.Array when e.EnumerateArray().All(x => x.ValueKind == JsonValueKind.String):
                    lists[path] = e.EnumerateArray().Select(x => x.GetString()!).ToList();
                    break;

                case JsonValueKind.Array:
                    foreach (var item in e.EnumerateArray())
                    {
                        if (item.ValueKind != JsonValueKind.Object
                            || !item.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String)
                            throw new ThemeTokenException(path, "an array must hold strings, or objects that each have a string \"id\"");
                        var itemPath = Join(path, id.GetString()!);
                        foreach (var p in item.EnumerateObject().Where(p => p.Name != "id"))
                            Walk(p.Value, Join(itemPath, p.Name));
                    }
                    break;

                case JsonValueKind.Number:
                    numbers[path] = e.GetDouble();
                    break;

                case JsonValueKind.True or JsonValueKind.False:
                    flags[path] = e.GetBoolean();
                    break;

                case JsonValueKind.String:
                    var s = e.GetString()!;
                    if (path.StartsWith("color.", StringComparison.Ordinal))
                        colors[path] = ThemeColor.Parse(s, path);
                    else if (path.StartsWith("shadow.", StringComparison.Ordinal))
                        shadows[path] = ShadowLayer.ParseList(s, path);
                    else
                        texts[path] = s;
                    break;

                default:
                    throw new ThemeTokenException(path, $"unexpected {e.ValueKind}");
            }
        }

        Walk(doc.RootElement, "");

        var tokens = new ThemeTokens
        {
            Colors = colors,
            Numbers = numbers,
            Flags = flags,
            Texts = texts,
            Lists = lists,
            Shadows = shadows,
            TextStyles = ReadTextStyles(numbers, flags, texts, colors, lists),
        };
        return tokens;
    }

    private static List<TextStyleToken> ReadTextStyles(
        Dictionary<string, double> numbers, Dictionary<string, bool> flags, Dictionary<string, string> texts,
        Dictionary<string, ThemeColor> colors, Dictionary<string, IReadOnlyList<string>> lists)
    {
        var names = numbers.Keys.Concat(texts.Keys).Concat(flags.Keys)
            .Where(k => k.StartsWith("type.", StringComparison.Ordinal))
            .Select(k => k.Split('.')[1])
            .Distinct()
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        var tabular = lists.TryGetValue("font.tabularFiguresFor", out var t) ? t : [];
        foreach (var name in tabular.Where(n => !names.Contains(n)))
            throw new ThemeTokenException("font.tabularFiguresFor", $"\"{name}\" is not a type.* entry");

        var styles = new List<TextStyleToken>();
        foreach (var name in names)
        {
            var p = "type." + name;
            double? Num(string leaf) => numbers.TryGetValue($"{p}.{leaf}", out var v) ? v : null;

            var size = Num("size") ?? throw new ThemeTokenException(p, "has no size");
            var weight = Num("weight") ?? 400;
            if (weight is < 1 or > 999 || weight != Math.Floor(weight))
                throw new ThemeTokenException(p + ".weight", $"{weight} is not an OpenType weight (1-999)");

            // "text.muted" names a colour token; it is stored as the full path.
            if (!texts.TryGetValue(p + ".color", out var colorRef))
                throw new ThemeTokenException(p, "has no color");
            var colorKey = "color." + colorRef;
            if (!colors.ContainsKey(colorKey))
                throw new ThemeTokenException(p + ".color", $"\"{colorRef}\" is not a colour token");

            var maxLines = Num("maxLines");
            if (maxLines is { } m && (m < 1 || m != Math.Floor(m)))
                throw new ThemeTokenException(p + ".maxLines", $"{m} is not a whole number of lines");

            styles.Add(new TextStyleToken(
                Name: name,
                Size: size,
                Weight: (int)weight,
                ColorKey: colorKey,
                LineHeight: Num("lineHeight"),
                MaxLines: maxLines is { } ml ? (int)ml : null,
                LetterSpacingEm: Num("letterSpacingEm"),
                Uppercase: flags.TryGetValue(p + ".uppercase", out var up) && up,
                TabularFigures: tabular.Contains(name)));
        }
        return styles;
    }

    private static string Join(string path, string name) => path.Length == 0 ? name : path + "." + name;
}

/// <summary>One text role from <c>type.*</c>. <see cref="LineHeight"/> is a multiple of the size, as in CSS.</summary>
public sealed record TextStyleToken(
    string Name,
    double Size,
    int Weight,
    string ColorKey,
    double? LineHeight,
    int? MaxLines,
    double? LetterSpacingEm,
    bool Uppercase,
    bool TabularFigures)
{
    /// <summary>The resource name of the style, e.g. <c>type.albumTitle</c>.</summary>
    public string Key => "type." + Name;

    /// <summary>The line height in pixels, when the token sets one.</summary>
    public double? LineHeightPixels => LineHeight * Size;
}

/// <summary>A colour as four bytes. Parsed from <c>#RRGGBB</c>, <c>rgb()</c> or <c>rgba()</c>.</summary>
public readonly record struct ThemeColor(byte A, byte R, byte G, byte B)
{
    public static ThemeColor Parse(string text, string path = "")
    {
        var s = text.Trim();
        if (s.StartsWith('#'))
        {
            // Only #RRGGBB. CSS's 8-digit form is RRGGBBAA and WPF's is AARRGGBB,
            // so accepting either would make one of them silently wrong.
            if (s.Length == 7 && uint.TryParse(s.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
                return new ThemeColor(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
            throw new ThemeTokenException(path, $"\"{text}\" is not a #RRGGBB colour (use rgba() for transparency)");
        }

        var open = s.IndexOf('(');
        if (open > 0 && s.EndsWith(')'))
        {
            var fn = s[..open].Trim().ToLowerInvariant();
            var parts = s[(open + 1)..^1].Split(',').Select(x => x.Trim()).ToArray();
            if ((fn == "rgb" && parts.Length == 3) || (fn == "rgba" && parts.Length == 4))
            {
                var channels = new byte[3];
                for (var i = 0; i < 3; i++)
                {
                    if (!byte.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out channels[i]))
                        throw new ThemeTokenException(path, $"\"{parts[i]}\" in \"{text}\" is not a channel value 0-255");
                }
                var alpha = 1.0;
                if (fn == "rgba" && (!double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out alpha) || alpha is < 0 or > 1))
                    throw new ThemeTokenException(path, $"\"{parts[3]}\" in \"{text}\" is not an alpha 0-1");
                return new ThemeColor((byte)Math.Round(alpha * 255), channels[0], channels[1], channels[2]);
            }
        }

        throw new ThemeTokenException(path, $"\"{text}\" is not a colour");
    }

    /// <summary>Opacity 0-1.</summary>
    public double Alpha => A / 255.0;

    public override string ToString() => $"#{A:X2}{R:X2}{G:X2}{B:X2}";
}

/// <summary>
/// One layer of a CSS <c>box-shadow</c>: <c>[inset] x y [blur [spread]] color</c>.
/// Lengths are pixels; a bare <c>0</c> needs no unit.
/// </summary>
public sealed record ShadowLayer(bool Inset, double OffsetX, double OffsetY, double Blur, double Spread, ThemeColor Color)
{
    /// <summary>A comma-separated list of layers. Commas inside <c>rgba(...)</c> are not separators.</summary>
    public static IReadOnlyList<ShadowLayer> ParseList(string text, string path = "")
    {
        var layers = new List<ShadowLayer>();
        var depth = 0;
        var start = 0;
        for (var i = 0; i <= text.Length; i++)
        {
            if (i == text.Length || (text[i] == ',' && depth == 0))
            {
                layers.Add(Parse(text[start..i], path));
                start = i + 1;
            }
            else if (text[i] == '(') depth++;
            else if (text[i] == ')') depth--;
        }
        return layers;
    }

    private static ShadowLayer Parse(string layer, string path)
    {
        var s = layer.Trim();
        var inset = false;
        if (s.StartsWith("inset ", StringComparison.OrdinalIgnoreCase))
        {
            inset = true;
            s = s[6..].Trim();
        }

        // The colour is whatever follows the lengths: rgba(...), rgb(...) or #hex.
        var colorAt = s.IndexOfAny(['#', 'r', 'R']);
        if (colorAt < 0)
            throw new ThemeTokenException(path, $"\"{layer.Trim()}\" has no colour");
        var color = ThemeColor.Parse(s[colorAt..], path);

        var lengths = s[..colorAt].Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(x => Length(x, layer, path)).ToArray();
        if (lengths.Length is < 2 or > 4)
            throw new ThemeTokenException(path, $"\"{layer.Trim()}\" needs 2-4 lengths before the colour");

        return new ShadowLayer(inset, lengths[0], lengths[1],
            lengths.Length > 2 ? lengths[2] : 0,
            lengths.Length > 3 ? lengths[3] : 0,
            color);
    }

    private static double Length(string token, string layer, string path)
    {
        var number = token.EndsWith("px", StringComparison.OrdinalIgnoreCase) ? token[..^2] : token;
        if (!double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            || (number == token && value != 0))
            throw new ThemeTokenException(path, $"\"{token}\" in \"{layer.Trim()}\" is not a px length");
        return value;
    }
}

public sealed class ThemeTokenException(string path, string problem)
    : Exception(path.Length == 0 ? problem : $"{path}: {problem}")
{
    public string TokenPath { get; } = path;
}
