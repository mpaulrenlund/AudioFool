using AudioFool.Core.Visualizer;

namespace AudioFool.Core.Tests;

public sealed class PresetLibraryTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "AudioFoolPresetTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder))
            Directory.Delete(_folder, recursive: true);
    }

    private PresetLibrary WithPresets(params string[] relative)
    {
        var library = new PresetLibrary(_folder, Path.Combine(_folder, "user"));
        foreach (var path in relative)
        {
            var full = Path.Combine(library.PresetsFolder, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, "[preset00]");
        }
        return library;
    }

    [Fact]
    public void Every_milk_file_in_every_folder_sorted()
    {
        var library = WithPresets(@"Hypnotic\b.milk", @"Fractal\a.milk", @"Fractal\deeper\c.milk", "readme.txt");

        Assert.Equal(
            [@"Fractal\a.milk", @"Fractal\deeper\c.milk", @"Hypnotic\b.milk"],
            library.All().Select(library.Relative));
    }

    [Fact]
    public void The_pack_and_the_user_s_skips_live_apart()
    {
        // The pack ships beside the exe; the user's list is in their AppData.
        var library = WithPresets(@"Fractal\a.milk");
        library.Skip(Path.Combine(library.PresetsFolder, @"Fractal\a.milk"));

        Assert.StartsWith(library.PackFolder, library.ScreenedOutPath);
        Assert.Equal(Path.Combine(_folder, "user", "skipped.txt"), library.SkippedPath);
        Assert.True(File.Exists(library.SkippedPath));
        Assert.Empty(library.Playable());
    }

    [Fact]
    public void No_folder_no_presets()
    {
        Assert.Empty(new PresetLibrary(_folder, Path.Combine(_folder, "user")).All());
        Assert.Empty(new PresetLibrary(_folder, Path.Combine(_folder, "user")).Playable());
    }

    [Fact]
    public void The_screened_out_and_the_skipped_dont_play()
    {
        var library = WithPresets(@"Fractal\a.milk", @"Fractal\b.milk", @"Hypnotic\c.milk", @"Hypnotic\d.milk");
        File.WriteAllLines(library.ScreenedOutPath,
        [
            "# found by the preset screen",
            "Fractal\\b.milk\tcrashed",
            "",
        ]);

        library.Skip(Path.Combine(library.PresetsFolder, @"Hypnotic\c.milk"));

        Assert.Equal([@"Fractal\a.milk", @"Hypnotic\d.milk"], library.Playable().Select(library.Relative));
    }

    [Fact]
    public void Skips_add_up_and_ignore_case()
    {
        var library = WithPresets(@"Fractal\a.milk", @"Fractal\b.milk", @"Fractal\c.milk");

        library.Skip(Path.Combine(library.PresetsFolder, @"Fractal\a.milk"));
        File.AppendAllText(library.SkippedPath, "FRACTAL\\B.MILK" + Environment.NewLine);

        Assert.Equal([@"Fractal\c.milk"], library.Playable().Select(library.Relative));
        Assert.Equal(2, PresetLibrary.ReadList(library.SkippedPath).Count());
    }
}
