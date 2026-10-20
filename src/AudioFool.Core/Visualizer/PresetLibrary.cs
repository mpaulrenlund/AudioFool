namespace AudioFool.Core.Visualizer;

/// <summary>
/// Where the visualizer's MilkDrop presets live and which of them may play: every
/// <c>.milk</c> under <see cref="PresetsFolder"/>, less those the preset screen
/// found crashing or failing (<see cref="ScreenedOutPath"/>) and those the user
/// skipped with Delete (<see cref="SkippedPath"/>). Both lists hold paths relative
/// to the presets folder, one a line; anything after a tab is a note.
/// </summary>
public sealed class PresetLibrary(string folder)
{
    /// <summary><c>%LOCALAPPDATA%\AudioFool\visualizer</c>: outside the music, outside the repo.</summary>
    public static string DefaultFolder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AudioFool", "visualizer");

    public string Folder { get; } = folder;
    public string PresetsFolder => Path.Combine(Folder, "presets");
    public string TexturesFolder => Path.Combine(Folder, "textures");
    public string ScreenedOutPath => Path.Combine(Folder, "screened-out.txt");
    public string SkippedPath => Path.Combine(Folder, "skipped.txt");

    /// <summary>Every preset in the pack, sorted, full paths.</summary>
    public IReadOnlyList<string> All()
    {
        if (!Directory.Exists(PresetsFolder))
            return [];

        return Directory.EnumerateFiles(PresetsFolder, "*.milk", SearchOption.AllDirectories)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>The presets that may play: all less the screened-out and the skipped.</summary>
    public IReadOnlyList<string> Playable()
    {
        var excluded = new HashSet<string>(ReadList(ScreenedOutPath), StringComparer.OrdinalIgnoreCase);
        excluded.UnionWith(ReadList(SkippedPath));
        return All().Where(path => !excluded.Contains(Relative(path))).ToList();
    }

    /// <summary>Remembers that the user never wants <paramref name="presetPath"/> again.</summary>
    public void Skip(string presetPath)
    {
        Directory.CreateDirectory(Folder);
        File.AppendAllText(SkippedPath, Relative(presetPath) + Environment.NewLine);
    }

    /// <summary>The path relative to the presets folder, as the lists hold it.</summary>
    public string Relative(string presetPath) => Path.GetRelativePath(PresetsFolder, presetPath);

    /// <summary>A list's entries: blank lines and lines starting with # are ignored, and anything after a tab.</summary>
    public static IEnumerable<string> ReadList(string path)
    {
        if (!File.Exists(path))
            return [];

        return File.ReadAllLines(path)
            .Select(line => line.Split('\t')[0].Trim())
            .Where(entry => entry.Length > 0 && !entry.StartsWith('#'));
    }
}
