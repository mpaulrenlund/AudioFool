using System.Text.Json;
using AudioFool.Core.Settings;

namespace AudioFool.Core.Tests;

/// <summary>
/// The visualizer was removed (session 65); a settings.json saved while it was
/// there still holds VisualizerIntensity and VisualizerWindow, which must be passed over.
/// </summary>
public class VisualizerSettingTests
{
    [Fact]
    public void A_file_with_the_old_visualizer_settings_still_loads()
    {
        var settings = JsonSerializer.Deserialize<AppSettings>(
            """{"VisualizerIntensity":8,"VisualizerWindow":{"Left":10,"Top":20,"Width":960,"Height":540},"Volume":0.5}""")!;

        Assert.Equal(0.5, settings.Volume);
        Assert.DoesNotContain("Visualizer", JsonSerializer.Serialize(settings));
    }
}
