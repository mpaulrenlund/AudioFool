namespace AudioFool.Core.Equalizer;

/// <summary>
/// The presets AudioFool offers: Peace's (<c>*.peace</c> in Equalizer APO's config
/// folder, read in place and never written) and AudioFool's own folder, where
/// Peace presets and Equalizer APO / AutoEq text files (<c>*.txt</c>) can be dropped.
/// Only <c>.peace</c> files are read from Peace's folder: its <c>.txt</c> files are
/// Equalizer APO's live, system-wide configuration, not presets.
/// </summary>
public static class EqPresetLibrary
{
    /// <summary>
    /// Every preset in the two folders, Peace's first, each sorted by name. A folder
    /// that is missing or unreadable gives none; a file that can't be read is
    /// skipped. Unsupported presets are included, so they can be shown as such.
    /// </summary>
    public static IReadOnlyList<EqPreset> Load(string? peaceFolder, string? ownFolder)
    {
        var presets = new List<EqPreset>();
        presets.AddRange(ReadFolder(peaceFolder, includeText: false));
        presets.AddRange(ReadFolder(ownFolder, includeText: true));
        return presets;
    }

    private static IEnumerable<EqPreset> ReadFolder(string? folder, bool includeText)
    {
        if (string.IsNullOrWhiteSpace(folder))
            return [];

        string[] files;
        try
        {
            if (!Directory.Exists(folder))
                return [];
            files = Directory.GetFiles(folder);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        var presets = new List<EqPreset>();
        foreach (var file in files.OrderBy(f => Path.GetFileNameWithoutExtension(f), StringComparer.CurrentCultureIgnoreCase))
        {
            var extension = Path.GetExtension(file);
            try
            {
                if (extension.Equals(".peace", StringComparison.OrdinalIgnoreCase))
                    presets.Add(PeacePresetReader.Read(file));
                else if (includeText && extension.Equals(".txt", StringComparison.OrdinalIgnoreCase))
                    presets.Add(EqualizerApoTextReader.Read(file));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // a file being written, or locked: leave it out this time
            }
        }
        return presets;
    }
}
