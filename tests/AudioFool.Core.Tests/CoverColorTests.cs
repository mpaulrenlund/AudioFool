using AudioFool.Core.Art;

namespace AudioFool.Core.Tests;

/// <summary>
/// The cover's colour in the playback bar (session 77). Ported from
/// AudioFool-Android's CoverColorTest with the same colours and numbers, so the
/// two apps are held to one rule.
/// </summary>
public sealed class CoverColorTests
{
    // design/ps2-dark/theme-tokens.dark.json and design/theme-tokens.json: color.accent.playback.
    private const uint DarkPlayback = 0xFF4C8DFF;
    private const uint LightPlayback = 0xFF15786C;

    /// <summary>Both token files: playbackBar.coverAccent.lightnessRange.</summary>
    private const double Range = 0.15;

    /// <summary>Both token files: playbackBar.coverAccent.greyOnDark.</summary>
    private const double GreyOnDark = 0.9;

    private const uint Red = 0xC0392B;
    private const uint Sky = 0x4A90D9;
    private const uint Grass = 0x5B8C3A;
    private const uint Orange = 0xF39C12;
    private const uint Olive = 0x6B6B47;
    private const uint DullBlue = 0x39506B;
    private const uint Crimson = 0xD6336C;
    private const uint MagentaRed = 0xD63384;
    private const uint Cream = 0xF0E0D0;
    private const uint DarkBrown = 0x2A1A10;
    private const uint Navy = 0x101830;
    private const uint DeepRed = 0x400808;
    private const uint Yellow = 0xF2C500;
    private const uint PaleYellow = 0xFFE9A8;
    private const uint PalePink = 0xFFD0E0;
    private const uint Terracotta = 0xE07A5F;
    private const uint LightBlue = 0x00519A;
    private const uint Gold = 0xC8A040;
    private const uint BluishBlack = 0x000814;
    private const uint BlueGrey = 0x5A6E8C;

    private static uint Opaque(uint rgb) => 0xFF000000u | rgb;

    /// <summary>A picture of so many pixels of each colour.</summary>
    private static uint[] Picture(params (uint Rgb, int Count)[] parts) =>
        parts.SelectMany(p => Enumerable.Repeat(Opaque(p.Rgb), p.Count)).ToArray();

    private static void AssertHue(uint expected, uint? actual, double within = 6.0)
    {
        Assert.NotNull(actual);
        var apart = Math.Abs(CoverColor.Hue(Opaque(expected)) - CoverColor.Hue(actual.Value));
        Assert.True(Math.Min(apart, 360 - apart) <= within,
            $"hue {CoverColor.Hue(actual.Value):0.0}, expected {CoverColor.Hue(Opaque(expected)):0.0}");
    }

    // ---- the cover's main colour ----

    [Fact]
    public void A_flat_cover_is_its_own_colour() =>
        Assert.Equal(Opaque(Red), CoverColor.Main(Picture((Red, 100))));

    [Fact]
    public void A_red_logo_on_a_black_cover_is_red() =>
        Assert.Equal(Opaque(Red), CoverColor.Main(Picture((0x000000, 95), (Red, 5))));

    /// <summary>Since 2026-11-12 (the user's call, as the phone) a cover without a colour is a grey, at its average lightness; it was null.</summary>
    private static void AssertGrey(double lightness, uint? actual)
    {
        Assert.NotNull(actual);
        Assert.True(CoverColor.Chroma(actual.Value) < 0.002, $"chroma {CoverColor.Chroma(actual.Value)}");
        Assert.Equal(lightness, CoverColor.Lightness(actual.Value), 0.01);
    }

    private static double AverageLightness(params (uint Rgb, int Count)[] parts) =>
        parts.Sum(p => CoverColor.Lightness(Opaque(p.Rgb)) * p.Count) / parts.Sum(p => p.Count);

    [Fact]
    public void Black_and_white_is_a_grey_of_its_lightness()
    {
        (uint, int)[] parts = [(0x000000, 40), (0xFFFFFF, 20), (0x808080, 30), (0x8A8580, 10)];
        AssertGrey(AverageLightness(parts), CoverColor.Main(Picture(parts)));
    }

    [Fact]
    public void A_black_cover_is_a_dark_grey_and_a_white_one_a_light_grey()
    {
        AssertGrey(0.0, CoverColor.Main(Picture((0x000000, 100))));
        AssertGrey(1.0, CoverColor.Main(Picture((0xFFFFFF, 100))));
    }

    [Fact]
    public void An_off_white_cover_is_a_grey() =>
        AssertGrey(AverageLightness((Cream, 90), (0x000000, 10)), CoverColor.Main(Picture((Cream, 90), (0x000000, 10))));

    [Fact]
    public void A_dark_brown_cover_is_brown_not_grey() =>
        AssertHue(DarkBrown, CoverColor.Main(Picture((DarkBrown, 70), (0x0C0A08, 30))));

    /// <summary>Under half a percent (2% until 2026-11-12, the user's call on the phone: "take any predominant color").</summary>
    [Fact]
    public void A_speck_of_colour_is_not_the_covers_colour()
    {
        AssertGrey(CoverColor.Lightness(Opaque(0x808080)), CoverColor.Main(Picture((0x808080, 299), (Red, 1))));
        Assert.Equal(Opaque(Red), CoverColor.Main(Picture((0x808080, 99), (Red, 1))));
    }

    /// <summary>A JPEG's black has a cast; made as light as the playback colour it was a strong blue (the phone, 2026-11-11).</summary>
    [Fact]
    public void A_black_covers_faint_tint_is_not_its_colour()
    {
        AssertHue(Gold, CoverColor.Main(Picture((BluishBlack, 95), (Gold, 5))));
        AssertGrey(AverageLightness((BluishBlack, 60), (0xFFFFFF, 40)), CoverColor.Main(Picture((BluishBlack, 60), (0xFFFFFF, 40))));
    }

    /// <summary>Each pixel counts by its chroma squared (2026-11-12): a grey's faint cast doesn't outweigh the colour in it.</summary>
    [Fact]
    public void A_faint_cast_over_most_of_a_cover_loses_to_a_vivid_part() =>
        AssertHue(Orange, CoverColor.Main(Picture((BlueGrey, 85), (Orange, 15))));

    [Fact]
    public void The_hue_with_the_most_colour_wins() =>
        AssertHue(Sky, CoverColor.Main(Picture((Sky, 60), (Grass, 30), (Orange, 10))));

    [Fact]
    public void A_colourful_part_counts_for_more_than_a_dull_one_of_its_size() =>
        AssertHue(Red, CoverColor.Main(Picture((Olive, 50), (Red, 50))));

    [Fact]
    public void A_large_dull_part_beats_a_small_vivid_one() =>
        AssertHue(DullBlue, CoverColor.Main(Picture((DullBlue, 80), (0x000000, 15), (Red, 5))));

    /// <summary>Crimson and a magenta red lie either side of where the hue circle joins.</summary>
    [Fact]
    public void Reds_either_side_of_the_hue_circles_join_are_one_colour()
    {
        var found = CoverColor.Main(Picture((Crimson, 25), (MagentaRed, 25), (Sky, 50)));
        Assert.NotNull(found);
        var hue = CoverColor.Hue(found.Value);
        Assert.True(hue > 350 || hue < 10, $"hue {hue}");
    }

    [Fact]
    public void See_through_pixels_are_not_part_of_the_picture()
    {
        const uint clearRed = 0x00C0392B;
        AssertHue(Sky, CoverColor.Main([.. Enumerable.Repeat(clearRed, 90), .. Picture((Sky, 10))]));
        // Nothing to see is no colour, not a grey.
        Assert.Null(CoverColor.Main(Enumerable.Repeat(clearRed, 100).ToArray()));
    }

    [Fact]
    public void An_empty_picture_has_no_colour() => Assert.Null(CoverColor.Main([]));

    // ---- that colour fitted to the theme ----

    private static readonly double DarkAnchor = CoverColor.Lightness(DarkPlayback);
    private static readonly double LightAnchor = CoverColor.Lightness(LightPlayback);

    private static uint OnDark(uint rgb) => CoverColor.Accent(Opaque(rgb), DarkPlayback, dark: true, Range);
    private static uint OnLight(uint rgb) => CoverColor.Accent(Opaque(rgb), LightPlayback, dark: false, Range);

    [Fact]
    public void On_dark_a_deep_colour_is_raised_to_the_themes_lightness()
    {
        var accent = OnDark(Navy);
        Assert.Equal(DarkAnchor, CoverColor.Lightness(accent), 0.01);
        AssertHue(Navy, accent);
    }

    [Fact]
    public void On_dark_a_pale_colour_comes_down_to_the_top_of_the_band()
    {
        var accent = OnDark(PaleYellow);
        Assert.Equal(DarkAnchor + Range, CoverColor.Lightness(accent), 0.01);
        AssertHue(PaleYellow, accent);
    }

    [Fact]
    public void On_light_a_bright_colour_is_lowered_to_the_themes_lightness()
    {
        var accent = OnLight(Yellow);
        Assert.Equal(LightAnchor, CoverColor.Lightness(accent), 0.01);
        AssertHue(Yellow, accent);
    }

    [Fact]
    public void On_light_a_deep_colour_comes_up_to_the_bottom_of_the_band()
    {
        var accent = OnLight(Navy);
        Assert.Equal(LightAnchor - Range, CoverColor.Lightness(accent), 0.01);
        AssertHue(Navy, accent);
    }

    /// <summary>Terracotta's lightness (0.69) is inside Dark's band (0.66 to 0.81); a mid blue's (0.44) inside Light's (0.37 to 0.52).</summary>
    [Fact]
    public void A_colour_already_in_the_band_is_left_as_it_is()
    {
        Assert.Equal(Opaque(Terracotta), OnDark(Terracotta));
        Assert.Equal(Opaque(LightBlue), OnLight(LightBlue));
    }

    [Fact]
    public void A_deep_colour_made_lighter_keeps_its_saturation()
    {
        var accent = OnDark(DeepRed);
        Assert.True(CoverColor.Chroma(accent) > 2 * CoverColor.Chroma(Opaque(DeepRed)), $"chroma {CoverColor.Chroma(accent)}");
        AssertHue(DeepRed, accent);
    }

    [Fact]
    public void A_pastel_made_darker_keeps_its_chroma()
    {
        var accent = OnLight(PalePink);
        Assert.Equal(CoverColor.Chroma(Opaque(PalePink)), CoverColor.Chroma(accent), 0.01);
        AssertHue(PalePink, accent);
    }

    /// <summary>A black and white cover's grey on Dark (the phone's 2026-11-11 change, the user's call: "Brighter on Dark").</summary>
    [Fact]
    public void On_dark_a_grey_is_the_grey_lightness_whatever_the_cover()
    {
        foreach (var cover in new uint[] { 0x000000, 0x404040, 0xFFFFFF })
        {
            var grey = CoverColor.Main(Picture((cover, 100)))!.Value;
            var accent = CoverColor.Accent(grey, DarkPlayback, dark: true, Range, greyOnDark: GreyOnDark);
            Assert.True(Math.Abs(GreyOnDark - CoverColor.Lightness(accent)) <= 0.005, $"#{cover:X6}: lightness {CoverColor.Lightness(accent)}");
            Assert.True(CoverColor.Chroma(accent) < 0.002);
        }
    }

    [Fact]
    public void On_light_a_grey_is_fitted_as_a_colour_is()
    {
        var grey = CoverColor.Main(Picture((0xFFFFFF, 100)))!.Value;
        var accent = CoverColor.Accent(grey, LightPlayback, dark: false, Range, greyOnDark: GreyOnDark);
        Assert.Equal(LightAnchor, CoverColor.Lightness(accent), 0.01);
    }

    [Fact]
    public void The_grey_lightness_leaves_colours_alone() =>
        Assert.Equal(OnDark(Red), CoverColor.Accent(Opaque(Red), DarkPlayback, dark: true, Range, greyOnDark: GreyOnDark));

    /// <summary>Every sixth step of each channel: 216 colours, the greys left out.</summary>
    [Fact]
    public void Any_colour_lands_in_the_band_opaque_with_its_hue()
    {
        uint[] steps = [0, 51, 102, 153, 204, 255];
        foreach (var r in steps)
        foreach (var g in steps)
        foreach (var b in steps)
        {
            var rgb = r << 16 | g << 8 | b;
            if (CoverColor.Chroma(Opaque(rgb)) < CoverColor.GreyBelow) continue;
            foreach (var (accent, low, high) in new[]
                     {
                         (OnDark(rgb), DarkAnchor, DarkAnchor + Range),
                         (OnLight(rgb), LightAnchor - Range, LightAnchor),
                     })
            {
                var name = $"#{rgb:X6} as #{accent:X8}";
                Assert.True(accent >> 24 == 0xFF, name);
                var lightness = CoverColor.Lightness(accent);
                Assert.True(lightness >= low - 0.01 && lightness <= high + 0.01, $"{name}: lightness {lightness}");
                AssertHue(rgb, accent, within: 8.0);
            }
        }
    }
}
