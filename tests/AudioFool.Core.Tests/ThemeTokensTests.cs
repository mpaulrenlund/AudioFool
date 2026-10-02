using AudioFool.Core.Theme;

namespace AudioFool.Core.Tests;

public class ThemeTokensTests
{
    private static readonly string TokenFile = Path.Combine(AppContext.BaseDirectory, "TestData", "theme-tokens.json");

    private static ThemeTokens Real() => ThemeTokens.Parse(File.ReadAllText(TokenFile));

    // ------------------------------------------------------------- colours

    [Theory]
    [InlineData("#BAB7B3", 255, 0xBA, 0xB7, 0xB3)]
    [InlineData("#ffda00", 255, 0xFF, 0xDA, 0x00)]
    [InlineData("rgba(255,255,255,0.45)", 115, 255, 255, 255)]
    [InlineData("rgba(0, 0, 0, 0.035)", 9, 0, 0, 0)]
    [InlineData("rgba(0,0,0,0)", 0, 0, 0, 0)]
    [InlineData("rgb(21,120,108)", 255, 21, 120, 108)]
    public void Colours_parse_to_argb(string text, int a, int r, int g, int b)
    {
        Assert.Equal(new ThemeColor((byte)a, (byte)r, (byte)g, (byte)b), ThemeColor.Parse(text));
    }

    [Theory]
    [InlineData("#BAB7B3FF")]   // CSS RRGGBBAA and WPF AARRGGBB disagree, so neither is accepted
    [InlineData("#FFF")]
    [InlineData("rgba(0,0,0,1.5)")]
    [InlineData("rgba(0,0,300,0.5)")]
    [InlineData("teal")]
    public void Ambiguous_or_bad_colours_are_refused(string text)
    {
        var ex = Assert.Throws<ThemeTokenException>(() => ThemeColor.Parse(text, "color.x"));
        Assert.Equal("color.x", ex.TokenPath);
    }

    // ------------------------------------------------------------- shadows

    [Fact]
    public void A_multi_layer_shadow_splits_on_commas_outside_rgba()
    {
        var layers = ShadowLayer.ParseList(
            "inset 0 -3px 0 rgba(0,0,0,0.12), inset 0 1px 0 rgba(255,255,255,0.8), 0 1px 3px rgba(0,0,0,0.25)");

        Assert.Equal(3, layers.Count);
        Assert.Equal(new ShadowLayer(true, 0, -3, 0, 0, new ThemeColor(31, 0, 0, 0)), layers[0]);
        Assert.Equal(new ShadowLayer(true, 0, 1, 0, 0, new ThemeColor(204, 255, 255, 255)), layers[1]);
        Assert.Equal(new ShadowLayer(false, 0, 1, 3, 0, new ThemeColor(64, 0, 0, 0)), layers[2]);
    }

    [Theory]
    [InlineData("0 4px rgba(0,0,0,0.2)", 0, 4, 0, 0)]
    [InlineData("2px 4px 10px 1px #000000", 2, 4, 10, 1)]
    public void Shadow_lengths_are_x_y_blur_spread(string text, double x, double y, double blur, double spread)
    {
        var l = Assert.Single(ShadowLayer.ParseList(text));
        Assert.Equal((x, y, blur, spread), (l.OffsetX, l.OffsetY, l.Blur, l.Spread));
    }

    [Theory]
    [InlineData("0 4 10px rgba(0,0,0,0.2)")]   // a non-zero length needs its unit
    [InlineData("4px rgba(0,0,0,0.2)")]        // one length is not a shadow
    [InlineData("0 4px 10px")]                 // no colour
    public void Bad_shadows_are_refused(string text)
    {
        Assert.Throws<ThemeTokenException>(() => ShadowLayer.ParseList(text, "shadow.x"));
    }

    // ------------------------------------------------------------ the walk

    [Fact]
    public void Leaves_are_keyed_by_path_and_typed_by_group()
    {
        var t = ThemeTokens.Parse("""
            {
              "$description": "ignored",
              "color": { "panel": { "bg": "#CAC7C3" } },
              "shadow": { "art": "0 0 6px rgba(0,0,0,0.25)" },
              "albums": { "rowHeight": 64, "align": "right" },
              "type": { "x": { "size": 12, "weight": 600, "color": "panel.bg", "uppercase": true } },
              "font": { "family": ["A", "B"] }
            }
            """);

        Assert.Equal(new ThemeColor(255, 0xCA, 0xC7, 0xC3), t.Colors["color.panel.bg"]);
        Assert.Equal(64, t.Numbers["albums.rowHeight"]);
        Assert.Equal("right", t.Texts["albums.align"]);
        Assert.True(t.Flags["type.x.uppercase"]);
        Assert.Single(t.Shadows["shadow.art"]);
        Assert.Equal(["A", "B"], t.FontFamily);
        Assert.DoesNotContain(t.Texts.Keys, k => k.Contains('$'));
    }

    [Fact]
    public void An_array_of_objects_is_addressed_by_id()
    {
        var t = ThemeTokens.Parse("""
            { "songTable": { "columns": [ { "id": "time", "width": 44, "align": "right" }, { "id": "song", "flex": 1.05 } ] } }
            """);

        Assert.Equal(44, t.Numbers["songTable.columns.time.width"]);
        Assert.Equal("right", t.Texts["songTable.columns.time.align"]);
        Assert.Equal(1.05, t.Numbers["songTable.columns.song.flex"]);
        Assert.DoesNotContain(t.Texts.Keys, k => k.EndsWith(".id"));
    }

    [Fact]
    public void A_bad_value_names_its_path()
    {
        var ex = Assert.Throws<ThemeTokenException>(() =>
            ThemeTokens.Parse("""{ "color": { "text": { "muted": "grey" } } }"""));
        Assert.Equal("color.text.muted", ex.TokenPath);
    }

    [Fact]
    public void A_type_style_must_name_a_real_colour()
    {
        var ex = Assert.Throws<ThemeTokenException>(() => ThemeTokens.Parse("""
            { "color": { "text": { "muted": "#4E4B48" } }, "type": { "x": { "size": 11, "color": "text.mute" } } }
            """));
        Assert.Equal("type.x.color", ex.TokenPath);
    }

    [Fact]
    public void Tabular_figures_must_name_a_type_style()
    {
        var ex = Assert.Throws<ThemeTokenException>(() => ThemeTokens.Parse("""
            { "font": { "tabularFiguresFor": ["times"] }, "color": { "t": { "a": "#000000" } }, "type": { "x": { "size": 11, "color": "t.a" } } }
            """));
        Assert.Equal("font.tabularFiguresFor", ex.TokenPath);
    }

    // ------------------------------------------------------ the real file

    [Fact]
    public void The_shipped_token_file_parses()
    {
        var t = Real();

        // 48, plus control.faceHover and status.lastFmOff, scanTrack and scanFill (session 4).
        Assert.Equal(52, t.Colors.Count);
        // 19, plus statValue (dialogs, Statistics).
        Assert.Equal(20, t.TextStyles.Count);
        Assert.Equal(8, t.Shadows.Count);
        Assert.Equal(["Segoe UI Variable Text", "Segoe UI"], t.FontFamily);
    }

    [Fact]
    public void The_shipped_file_matches_spot_values_in_the_spec()
    {
        var t = Real();

        Assert.Equal(ThemeColor.Parse("#BAB7B3"), t.Colors["color.window.bg"]);
        Assert.Equal(ThemeColor.Parse("#15786C"), t.Colors["color.accent.teal"]);
        Assert.Equal(new ThemeColor(115, 255, 255, 255), t.Colors["color.panel.highlight"]);
        Assert.Equal(4, t.Numbers["radius.surface"]);
        Assert.Equal(212, t.Numbers["layout.artistsPanelOuterWidth"]);
        Assert.Equal(27, t.Numbers["songTable.columns.number.width"]);
        Assert.Equal(1.05, t.Numbers["songTable.columns.song.flex"]);
        Assert.Equal("center", t.Texts["songTable.columns.disc.align"]);
    }

    [Fact]
    public void The_now_playing_art_shadow_has_no_offset()
    {
        var layer = Assert.Single(Real().Shadows["shadow.nowPlayingArt"]);
        Assert.Equal((0.0, 0.0, 6.0), (layer.OffsetX, layer.OffsetY, layer.Blur));
    }

    [Fact]
    public void The_shipped_type_styles_resolve()
    {
        var styles = Real().TextStyles.ToDictionary(s => s.Name);

        var album = styles["albumTitle"];
        Assert.Equal((13.5, 400, "color.text.primary", 2), (album.Size, album.Weight, album.ColorKey, album.MaxLines));
        Assert.Equal(17.55, album.LineHeightPixels!.Value, 6);

        var header = styles["panelHeader"];
        Assert.True(header.Uppercase);
        Assert.Equal(0.12, header.LetterSpacingEm);

        Assert.Equal("color.accent.blue", styles["headerArtist"].ColorKey);
        Assert.Equal(["statValue", "tableCell", "timeLabel", "trackNumber"],
            styles.Values.Where(s => s.TabularFigures).Select(s => s.Name).Order());
    }

    [Fact]
    public void Every_shipped_shadow_is_drawable_in_wpf()
    {
        // TokenResources refuses more than one outer layer and any spread; hold
        // the file to that here, where it is cheap to see.
        foreach (var (key, layers) in Real().Shadows)
        {
            Assert.True(layers.Count(l => !l.Inset) <= 1, key);
            Assert.All(layers, l => Assert.Equal(0, l.Spread));
        }
    }
}
