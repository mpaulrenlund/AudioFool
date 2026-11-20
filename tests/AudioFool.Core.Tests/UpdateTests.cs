using System.IO.Compression;
using AudioFool.Core.Updates;

namespace AudioFool.Core.Tests;

/// <summary>The About row's update check (2026-11-20): versions, GitHub's answer, and the file swap.</summary>
public class UpdateTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "AudioFoolUpdateTests", Guid.NewGuid().ToString("N"));

    public UpdateTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Theory]
    [InlineData("v0.12.0", "0.12.0.0")]
    [InlineData("0.12.0", "0.12.0.0")]
    [InlineData("V1.2", "1.2.0.0")]
    [InlineData(" v0.11.3 ", "0.11.3.0")]
    public void Tags_read_as_versions(string tag, string expected)
    {
        Assert.Equal(Version.Parse(expected), UpdateCheck.ParseVersion(tag));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("latest")]
    [InlineData("v")]
    [InlineData("release-0.12")]
    public void Other_tags_are_not_versions(string? tag)
    {
        Assert.Null(UpdateCheck.ParseVersion(tag));
    }

    [Theory]
    [InlineData("0.12.0", "0.11.0", true)]
    [InlineData("0.11.1", "0.11.0", true)]
    [InlineData("1.0.0", "0.99.9", true)]
    [InlineData("0.11.0", "0.11.0", false)]
    [InlineData("0.11", "0.11.0", false)]
    [InlineData("0.10.9", "0.11.0", false)]
    public void Newer_compares_by_version_not_text(string latest, string current, bool newer)
    {
        Assert.Equal(newer, UpdateCheck.IsNewer(Version.Parse(latest), Version.Parse(current)));
    }

    [Fact]
    public void The_zip_name_matches_the_release_skill()
    {
        Assert.Equal("AudioFool-0.12.0-win-x64.zip", UpdateCheck.ZipNameFor(new Version(0, 12)));
    }

    private const string LatestJson = """
        {
          "tag_name": "v0.12.0",
          "draft": false,
          "assets": [
            { "name": "notes.txt", "size": 10, "browser_download_url": "https://github.com/x/y/releases/download/v0.12.0/notes.txt" },
            { "name": "AudioFool-0.12.0-win-x64.zip", "size": 61000000,
              "browser_download_url": "https://github.com/mpaulrenlund/AudioFool/releases/download/v0.12.0/AudioFool-0.12.0-win-x64.zip" }
          ]
        }
        """;

    [Fact]
    public void The_latest_release_and_its_zip_are_read()
    {
        var release = UpdateCheck.ParseLatest(LatestJson);

        Assert.NotNull(release);
        Assert.Equal(new Version(0, 12, 0, 0), release.Version);
        Assert.Equal("v0.12.0", release.Tag);
        Assert.Equal("AudioFool-0.12.0-win-x64.zip", release.ZipName);
        Assert.Equal(61000000, release.ZipSize);
        Assert.EndsWith("/AudioFool-0.12.0-win-x64.zip", release.ZipUrl.AbsoluteUri);
    }

    [Fact]
    public void A_release_without_the_zip_is_not_offered()
    {
        var json = LatestJson.Replace("AudioFool-0.12.0-win-x64.zip", "Something-else.zip");
        Assert.Null(UpdateCheck.ParseLatest(json));
    }

    [Fact]
    public void A_zip_that_isnt_https_is_not_offered()
    {
        var json = LatestJson.Replace("https://github.com/mpaulrenlund", "http://github.com/mpaulrenlund");
        Assert.Null(UpdateCheck.ParseLatest(json));
    }

    [Fact]
    public void A_draft_is_not_offered()
    {
        Assert.Null(UpdateCheck.ParseLatest(LatestJson.Replace("\"draft\": false", "\"draft\": true")));
    }

    [Fact]
    public void An_unreadable_answer_says_so()
    {
        var ex = Assert.Throws<UpdateCheckException>(() => UpdateCheck.ParseLatest("<html>"));
        Assert.Contains("couldn't be read", ex.Message);
    }

    private string Folder(string name)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private static void Write(string folder, string relative, string text)
    {
        var path = Path.Combine(folder, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private static string Read(string folder, string relative) => File.ReadAllText(Path.Combine(folder, relative));

    [Fact]
    public void Swap_puts_the_new_files_in_and_keeps_the_old_ones_aside()
    {
        var app = Folder("app");
        Write(app, "AudioFool.exe", "old exe");
        Write(app, "bass.dll", "old bass");
        Write(app, "settings-not-in-release.txt", "mine");

        var staging = Folder("staging");
        Write(staging, "AudioFool.exe", "new exe");
        Write(staging, "bass.dll", "new bass");
        Write(staging, "bassflac.dll", "new flac");

        UpdateInstaller.Swap(staging, app);

        Assert.Equal("new exe", Read(app, "AudioFool.exe"));
        Assert.Equal("new bass", Read(app, "bass.dll"));
        Assert.Equal("new flac", Read(app, "bassflac.dll"));
        Assert.Equal("old exe", Read(app, "AudioFool.exe.old"));
        Assert.Equal("old bass", Read(app, "bass.dll.old"));
        Assert.Equal("mine", Read(app, "settings-not-in-release.txt"));
        Assert.False(File.Exists(Path.Combine(app, "bassflac.dll.old")));
    }

    [Fact]
    public void Swap_works_while_the_exe_is_open_as_a_running_program_holds_it()
    {
        var app = Folder("app");
        Write(app, "AudioFool.exe", "old exe");
        var staging = Folder("staging");
        Write(staging, "AudioFool.exe", "new exe");

        // A running exe is open for reading and allows delete (rename) but not writes.
        using (new FileStream(Path.Combine(app, "AudioFool.exe"), FileMode.Open, FileAccess.Read,
                              FileShare.Read | FileShare.Delete))
        {
            UpdateInstaller.Swap(staging, app);
        }

        Assert.Equal("new exe", Read(app, "AudioFool.exe"));
        Assert.Equal("old exe", Read(app, "AudioFool.exe.old"));
    }

    [Fact]
    public void A_failed_swap_puts_everything_back()
    {
        var app = Folder("app");
        Write(app, "a.dll", "old a");
        Write(app, "b.dll", "old b");
        var staging = Folder("staging");
        Write(staging, "a.dll", "new a");
        Write(staging, "b.dll", "new b");
        Write(staging, "c.dll", "new c");

        // b.dll can't be renamed: open without FileShare.Delete.
        using (new FileStream(Path.Combine(app, "b.dll"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.ThrowsAny<IOException>(() => UpdateInstaller.Swap(staging, app));
        }

        Assert.Equal("old a", Read(app, "a.dll"));
        Assert.Equal("old b", Read(app, "b.dll"));
        Assert.False(File.Exists(Path.Combine(app, "a.dll.old")));
        Assert.False(File.Exists(Path.Combine(app, "c.dll")));
    }

    [Fact]
    public void A_second_update_replaces_a_leftover_old_file()
    {
        var app = Folder("app");
        Write(app, "AudioFool.exe", "v2");
        Write(app, "AudioFool.exe.old", "v1");
        var staging = Folder("staging");
        Write(staging, "AudioFool.exe", "v3");

        UpdateInstaller.Swap(staging, app);

        Assert.Equal("v3", Read(app, "AudioFool.exe"));
        Assert.Equal("v2", Read(app, "AudioFool.exe.old"));
    }

    [Fact]
    public void A_locked_leftover_old_file_gets_a_numbered_name()
    {
        var app = Folder("app");
        Write(app, "AudioFool.exe", "v2");
        Write(app, "AudioFool.exe.old", "v1");
        var staging = Folder("staging");
        Write(staging, "AudioFool.exe", "v3");

        using (new FileStream(Path.Combine(app, "AudioFool.exe.old"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            UpdateInstaller.Swap(staging, app);
        }

        Assert.Equal("v3", Read(app, "AudioFool.exe"));
        Assert.Equal("v1", Read(app, "AudioFool.exe.old"));
        Assert.Equal("v2", Read(app, "AudioFool.exe.1.old"));
    }

    [Fact]
    public void CleanUp_removes_the_old_files_only()
    {
        var app = Folder("app");
        Write(app, "AudioFool.exe", "new");
        Write(app, "AudioFool.exe.old", "old");
        Write(app, "x64\\bass.dll.old", "old");
        Write(app, "settings.json", "{}");

        Assert.Equal(2, UpdateInstaller.CleanUp(app));
        Assert.True(File.Exists(Path.Combine(app, "AudioFool.exe")));
        Assert.True(File.Exists(Path.Combine(app, "settings.json")));
        Assert.Empty(Directory.EnumerateFiles(app, "*.old", SearchOption.AllDirectories));
    }

    private string Zip(params (string Name, string Text)[] entries)
    {
        var path = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".zip");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, text) in entries)
        {
            using var writer = new StreamWriter(zip.CreateEntry(name).Open());
            writer.Write(text);
        }

        return path;
    }

    [Fact]
    public void Extract_unzips_the_release()
    {
        var zip = Zip(("AudioFool.exe", "exe"), ("bass.dll", "bass"));
        var staging = Path.Combine(_root, "staging");
        Write(_root, "staging\\stale.txt", "from before");

        UpdateInstaller.Extract(zip, staging);

        Assert.Equal("exe", Read(staging, "AudioFool.exe"));
        Assert.Equal("bass", Read(staging, "bass.dll"));
        Assert.False(File.Exists(Path.Combine(staging, "stale.txt")));
    }

    [Fact]
    public void Extract_refuses_a_zip_without_the_app()
    {
        var zip = Zip(("readme.txt", "hi"));
        var ex = Assert.Throws<UpdateCheckException>(() => UpdateInstaller.Extract(zip, Path.Combine(_root, "staging")));
        Assert.Contains("AudioFool.exe", ex.Message);
    }

    [Fact]
    public void Extract_refuses_an_entry_outside_the_folder()
    {
        var zip = Zip(("AudioFool.exe", "exe"), ("..\\escaped.dll", "bad"));
        Assert.Throws<UpdateCheckException>(() => UpdateInstaller.Extract(zip, Path.Combine(_root, "staging")));
        Assert.False(File.Exists(Path.Combine(_root, "escaped.dll")));
    }

    [Fact]
    public void Extract_refuses_a_file_that_isnt_a_zip()
    {
        var path = Path.Combine(_root, "notzip.zip");
        File.WriteAllText(path, "not a zip");
        Assert.Throws<UpdateCheckException>(() => UpdateInstaller.Extract(path, Path.Combine(_root, "staging")));
    }
}
