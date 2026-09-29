using System.Text.Json;
using AudioFool.Core.Settings;

namespace AudioFool.Core.Tests;

public class WindowBoundsTests
{
    // A 1920x1080 primary with the taskbar at the bottom, and a second
    // 2560x1440 monitor to its right.
    private static readonly PixelRect Primary = new(0, 0, 1920, 1032);
    private static readonly PixelRect Right = new(1920, 0, 4480, 1392);

    [Fact]
    public void A_window_on_a_connected_screen_is_reachable() =>
        Assert.True(new WindowBounds(200, 100, 1400, 800, false).IsReachableOn([Primary]));

    [Fact]
    public void A_window_on_the_second_monitor_is_reachable_while_it_is_connected()
    {
        var onRight = new WindowBounds(2200, 150, 1600, 900, false);

        Assert.True(onRight.IsReachableOn([Primary, Right]));
        Assert.False(onRight.IsReachableOn([Primary]));
    }

    [Fact]
    public void A_window_hanging_off_an_edge_is_fine_while_enough_title_strip_shows() =>
        // Only 200 px of it is on the screen, but that's plenty to drag it back.
        Assert.True(new WindowBounds(1720, 300, 1400, 800, false).IsReachableOn([Primary]));

    [Fact]
    public void A_sliver_of_title_strip_is_not_enough() =>
        Assert.False(new WindowBounds(1900, 300, 1400, 800, false).IsReachableOn([Primary]));

    [Fact]
    public void A_title_strip_above_the_screen_is_not_reachable() =>
        // The body shows, but the part you grab to move it is off the top.
        Assert.False(new WindowBounds(200, -400, 1400, 800, false).IsReachableOn([Primary]));

    [Fact]
    public void A_title_strip_behind_the_taskbar_is_not_reachable() =>
        Assert.False(new WindowBounds(200, 1040, 1400, 800, false).IsReachableOn([Primary]));

    [Fact]
    public void An_empty_rectangle_is_never_used() =>
        Assert.False(new WindowBounds(200, 100, 0, 800, false).IsReachableOn([Primary]));

    [Fact]
    public void Survives_the_settings_file()
    {
        var settings = new AppSettings { Window = new WindowBounds(-1700, 40, 1500, 880, true) };

        var back = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))!;

        Assert.Equal(settings.Window, back.Window);
    }

    [Fact]
    public void A_settings_file_from_before_has_no_position() =>
        Assert.Null(JsonSerializer.Deserialize<AppSettings>("""{"Volume":1.0}""")!.Window);
}
