namespace AudioFool.Core.Visualizer;

/// <summary>
/// Where the visualizer's MilkDrop presets live and which of them may play: every
/// <c>.milk</c> under <see cref="PresetsFolder"/>, less those the preset screen
/// found crashing or failing (<see cref="ScreenedOutPath"/>) and those the user
/// skipped with Delete (<see cref="SkippedPath"/>). Both lists hold paths relative
/// to the presets folder, one a line; anything after a tab is a note.
/// <para>
/// Two folders: the pack (presets, textures, the screen's list), which ships with
/// the app beside its exe, and the user's own list, in their AppData, which the
/// app writes. (The pack started in AppData, but files Claude's tools create
/// there go into the Claude app's private copy of AppData, invisible to
/// AudioFool: session 58.)
/// </para>
/// </summary>
public sealed class PresetLibrary(string packFolder, string userFolder)
{
    /// <summary><c>%LOCALAPPDATA%\AudioFool\visualizer</c>: the user's skip list.</summary>
    public static string UserFolderDefault { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AudioFool", "visualizer");

    /// <summary>
    /// The pack beside the exe (<c>visualizer</c> next to AudioFool.exe); where
    /// there's none, as in a development build, the user folder's.
    /// </summary>
    public static PresetLibrary Default()
    {
        var besideExe = Path.Combine(AppContext.BaseDirectory, "visualizer");
        return new PresetLibrary(Directory.Exists(Path.Combine(besideExe, "presets")) ? besideExe : UserFolderDefault, UserFolderDefault);
    }

    public string PackFolder { get; } = packFolder;
    public string UserFolder { get; } = userFolder;
    public string PresetsFolder => Path.Combine(PackFolder, "presets");
    public string TexturesFolder => Path.Combine(PackFolder, "textures");
    public string ScreenedOutPath => Path.Combine(PackFolder, "screened-out.txt");
    public string SkippedPath => Path.Combine(UserFolder, "skipped.txt");

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
        Directory.CreateDirectory(UserFolder);
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
