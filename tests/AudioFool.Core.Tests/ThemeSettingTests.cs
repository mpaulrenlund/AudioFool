using System.Text.Json;
using AudioFool.Core.Settings;
using AudioFool.Core.Theme;

namespace AudioFool.Core.Tests;

/// <summary>
/// AppSettings.Theme (PS2 Dark spec 3.2): saved by name, Dark when missing, and an
/// old or odd value never costs the rest of the file.
/// </summary>
public class ThemeSettingTests
{
    [Fact]
    public void A_file_without_a_theme_opens_dark()
    {
        Assert.Equal(AppTheme.Dark, JsonSerializer.Deserialize<AppSettings>("""{"Volume":1.0}""")!.Theme);
        Assert.Equal(AppTheme.Dark, new AppSettings().Theme);
    }

    [Theory]
    [InlineData(AppTheme.Light)]
    [InlineData(AppTheme.Dark)]
    public void The_theme_is_saved_by_name_and_read_back(AppTheme theme)
    {
        var json = JsonSerializer.Serialize(new AppSettings { Theme = theme });

        Assert.Contains($"\"Theme\":\"{theme}\"", json);
        Assert.Equal(theme, JsonSerializer.Deserialize<AppSettings>(json)!.Theme);
    }

    [Theory]
    [InlineData("\"light\"", AppTheme.Light)]
    [InlineData("\"DARK\"", AppTheme.Dark)]
    public void The_name_is_read_in_any_case(string value, AppTheme expected)
    {
        Assert.Equal(expected, JsonSerializer.Deserialize<AppSettings>($$"""{"Theme":{{value}}}""")!.Theme);
    }

    [Theory]
    [InlineData("\"Vista\"")]        // the pre-session-18 themes
    [InlineData("\"PS1\"")]
    [InlineData("\"\"")]
    [InlineData("\"1\"")]           // Enum.TryParse would take this as Dark's number
    [InlineData("0")]
    [InlineData("null")]
    [InlineData("true")]
    [InlineData("""{"Name":"Light"}""")]
    [InlineData("""["Light"]""")]
    public void Anything_else_reads_as_dark_and_the_rest_of_the_file_survives(string value)
    {
        var json = $$"""
            {
              "MusicFolders": ["D:\\Music"],
              "Theme": {{value}},
              "Volume": 0.5,
              "LastFmUserName": "someone"
            }
            """;

        var settings = JsonSerializer.Deserialize<AppSettings>(json)!;

        Assert.Equal(AppTheme.Dark, settings.Theme);
        Assert.Equal([@"D:\Music"], settings.MusicFolders);
        Assert.Equal(0.5, settings.Volume);
        Assert.Equal("someone", settings.LastFmUserName);
    }
}
