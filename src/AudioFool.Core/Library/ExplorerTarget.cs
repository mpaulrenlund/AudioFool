namespace AudioFool.Core.Library;

/// <summary>
/// What Windows Explorer should open to show a track's file: the file itself,
/// selected in its folder, or when the file has gone (moved, renamed, the drive
/// half there) the nearest folder above it that still exists.
/// </summary>
public static class ExplorerTarget
{
    /// <summary>
    /// The arguments for explorer.exe, or null when nothing on the path exists,
    /// which is what an unplugged drive looks like.
    /// </summary>
    public static string? ArgumentsFor(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return null;

        if (File.Exists(filePath))
            return $"/select,\"{filePath}\"";

        for (var folder = Path.GetDirectoryName(filePath); !string.IsNullOrEmpty(folder); folder = Path.GetDirectoryName(folder))
        {
            if (Directory.Exists(folder))
                return $"\"{folder}\"";
        }

        return null;
    }
}
