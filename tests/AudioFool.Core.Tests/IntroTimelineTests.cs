using AudioFool.Core.Theme;

namespace AudioFool.Core.Tests;

/// <summary>The About intro's two screens against the time since its sound started (session 64).</summary>
public class IntroTimelineTests
{
    private static readonly IntroTimeline Timeline = IntroTimeline.FromTokens(
        ThemeTokens.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "theme-tokens.json"))).Numbers);

    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    [Fact]
    public void The_tokens_put_the_logo_on_the_chime()
    {
        Assert.Equal(S(7.3), Timeline.LogoAt);
        Assert.Equal(S(14.9), Timeline.Length);
    }

    [Fact]
    public void It_starts_black_and_the_Sony_screen_fades_in()
    {
        Assert.Equal((0.0, 0.0), Timeline.Opacities(TimeSpan.Zero));
        Assert.Equal(0.5, Timeline.Opacities(Timeline.Fade / 2).Sony, 6);
        Assert.Equal((1.0, 0.0), Timeline.Opacities(S(3)));
    }

    [Fact]
    public void The_screens_meet_in_black_at_the_chime()
    {
        Assert.Equal((1.0, 0.0), Timeline.Opacities(Timeline.LogoAt - Timeline.Fade));
        Assert.Equal((0.0, 0.0), Timeline.Opacities(Timeline.LogoAt));
        Assert.Equal((0.0, 1.0), Timeline.Opacities(Timeline.LogoAt + Timeline.Fade));
    }

    [Fact]
    public void The_PlayStation_screen_fades_out_as_the_sound_ends()
    {
        Assert.Equal((0.0, 1.0), Timeline.Opacities(Timeline.Length - Timeline.EndFade));
        Assert.Equal(0.5, Timeline.Opacities(Timeline.Length - (Timeline.EndFade / 2)).PlayStation, 6);
        Assert.Equal((0.0, 0.0), Timeline.Opacities(Timeline.Length));
        Assert.False(Timeline.IsOver(Timeline.Length - TimeSpan.FromMilliseconds(1)));
        Assert.True(Timeline.IsOver(Timeline.Length));
    }

    [Fact]
    public void Never_both_screens_at_once()
    {
        for (var ms = 0; ms <= 16000; ms += 10)
        {
            var (sony, playStation) = Timeline.Opacities(TimeSpan.FromMilliseconds(ms));
            Assert.InRange(sony, 0, 1);
            Assert.InRange(playStation, 0, 1);
            Assert.True(sony == 0 || playStation == 0, $"both at {ms} ms");
        }
    }
}
