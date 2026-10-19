using System.Text.Json;
using AudioFool.Core.Settings;

namespace AudioFool.Core.Tests;

/// <summary>AppSettings.VisualizerIntensity: 5 when missing, saved as a number, kept between 1 and 10.</summary>
public class VisualizerSettingTests
{
    [Fact]
    public void A_file_without_it_is_at_5()
    {
        Assert.Equal(5, JsonSerializer.Deserialize<AppSettings>("""{"Volume":1.0}""")!.VisualizerIntensity);
        Assert.Equal(5, new AppSettings().VisualizerIntensity);
    }

    [Fact]
    public void It_is_saved_and_read_back()
    {
        var json = JsonSerializer.Serialize(new AppSettings { VisualizerIntensity = 8 });

        Assert.Contains("\"VisualizerIntensity\":8", json);
        Assert.Equal(8, JsonSerializer.Deserialize<AppSettings>(json)!.VisualizerIntensity);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-3, 1)]
    [InlineData(11, 10)]
    [InlineData(99, 10)]
    public void A_number_out_of_range_is_brought_in(int saved, int expected)
    {
        var settings = JsonSerializer.Deserialize<AppSettings>($$"""{"VisualizerIntensity":{{saved}},"Volume":0.5}""")!;

        Assert.Equal(expected, settings.VisualizerIntensity);
        Assert.Equal(0.5, settings.Volume);
    }
}
