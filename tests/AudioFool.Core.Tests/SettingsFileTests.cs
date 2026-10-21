using System.Text.Json;
using AudioFool.Core.Settings;

namespace AudioFool.Core.Tests;

/// <summary>
/// settings.json holds the music folders and the Last.fm login. Losing it starts
/// the app on defaults, and the next scan then drops every track in the real
/// folders, so a save must never leave it half-written and a failed read must
/// never be saved back over it.
/// </summary>
public sealed class SettingsFileTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "audiofool-settings-" + Guid.NewGuid().ToString("N"));
    private readonly string _path;
    private static readonly DateTime Now = new(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc);

    public SettingsFileTests()
    {
        Directory.CreateDirectory(_folder);
        _path = Path.Combine(_folder, "settings.json");
    }

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private static string Json(string folder, double volume = 0.5) =>
        JsonSerializer.Serialize(new AppSettings { MusicFolders = [folder], Volume = volume, LastFmUserName = "someone" });

    [Fact]
    public void A_save_reads_back_and_keeps_the_one_before_as_the_backup()
    {
        var settings = AppSettings.Load(_path, Now);
        settings.MusicFolders = [@"D:\Music"];
        settings.Save();

        settings.MusicFolders = [@"E:\Music"];
        settings.Save();

        Assert.Equal([@"E:\Music"], AppSettings.Load(_path, Now).MusicFolders);
        Assert.Contains(@"D:\\Music", File.ReadAllText(AppSettings.BackupPathFor(_path)));
        Assert.False(File.Exists(_path + ".tmp"));
    }

    [Fact]
    public void A_first_run_has_no_file_and_saves_normally()
    {
        var settings = AppSettings.Load(_path, Now);

        Assert.False(settings.IsReadOnly);
        settings.Volume = 0.25;
        settings.Save();

        Assert.Equal(0.25, AppSettings.Load(_path, Now).Volume);
    }

    [Theory]
    [InlineData("{ \"MusicFolders\": [\"D:\\\\Mu")]   // cut off mid-write
    [InlineData("")]
    [InlineData("null")]
    [InlineData("{ \"MusicFolders\": null }")]
    public void A_damaged_file_falls_back_to_the_backup_and_is_kept_aside(string damaged)
    {
        File.WriteAllText(AppSettings.BackupPathFor(_path), Json(@"D:\Music"));
        File.WriteAllText(_path, damaged);

        var settings = AppSettings.Load(_path, Now);

        Assert.Equal([@"D:\Music"], settings.MusicFolders);
        Assert.Equal("someone", settings.LastFmUserName);
        Assert.False(settings.IsReadOnly);
        Assert.Single(Directory.GetFiles(_folder, "settings.unreadable-*.json"));
    }

    [Fact]
    public void A_locked_file_is_never_written_over_with_defaults()
    {
        File.WriteAllText(_path, Json(@"D:\Music"));

        AppSettings settings;
        using (new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            settings = AppSettings.Load(_path, Now);
            Assert.True(settings.IsReadOnly);
            Assert.DoesNotContain(@"D:\Music", settings.MusicFolders);
        }

        settings.Volume = 0.9;
        settings.Save();

        var after = AppSettings.Load(_path, Now);
        Assert.Equal([@"D:\Music"], after.MusicFolders);
        Assert.Equal(0.5, after.Volume);
        Assert.Equal("someone", after.LastFmUserName);
    }

    [Fact]
    public void A_locked_file_with_a_backup_runs_on_the_backup_without_saving()
    {
        File.WriteAllText(_path, Json(@"E:\Music"));
        File.WriteAllText(AppSettings.BackupPathFor(_path), Json(@"D:\Music"));

        using (new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var settings = AppSettings.Load(_path, Now);

            Assert.Equal([@"D:\Music"], settings.MusicFolders);
            Assert.True(settings.IsReadOnly);
        }

        Assert.Equal([@"E:\Music"], AppSettings.Load(_path, Now).MusicFolders);
    }

    [Fact]
    public void Changing_one_value_skips_the_write_when_the_file_cant_be_read_just_then()
    {
        File.WriteAllText(_path, Json(@"D:\Music"));

        var inUse = AppSettings.Load(_path, Now);

        using (new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.False(AppSettings.TryLoadCurrent(_path, inUse, out var unread));
            unread.Volume = 0.1;
            unread.Save();
        }

        Assert.Equal(0.5, AppSettings.Load(_path, Now).Volume);

        Assert.True(AppSettings.TryLoadCurrent(_path, inUse, out var current));
        Assert.NotSame(inUse, current);
        current.Volume = 0.1;
        current.Save();

        var after = AppSettings.Load(_path, Now);
        Assert.Equal(0.1, after.Volume);
        Assert.Equal([@"D:\Music"], after.MusicFolders);
    }

    [Fact]
    public void With_no_file_yet_changing_one_value_saves_the_settings_in_use()
    {
        var inUse = AppSettings.Load(_path, Now);
        inUse.MusicFolders = [@"D:\Music"];

        Assert.True(AppSettings.TryLoadCurrent(_path, inUse, out var current));
        Assert.Same(inUse, current);
        current.Volume = 0.1;
        current.Save();

        var after = AppSettings.Load(_path, Now);
        Assert.Equal([@"D:\Music"], after.MusicFolders);
        Assert.Equal(0.1, after.Volume);
    }

    [Fact]
    public void Changing_one_value_skips_the_write_when_the_file_is_damaged()
    {
        File.WriteAllText(_path, "{ \"MusicFolders\": [");
        var inUse = new AppSettings { MusicFolders = [@"D:\Music"] };

        Assert.False(AppSettings.TryLoadCurrent(_path, inUse, out var unread));
        unread.Save();

        Assert.Equal("{ \"MusicFolders\": [", File.ReadAllText(_path));
    }
}
