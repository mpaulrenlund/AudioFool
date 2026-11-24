namespace AudioFool.Core.Library;

/// <summary>
/// What a tag window says when none of its songs could be opened, most often
/// because the music drive is unplugged. Without it the window showed blank boxes,
/// which read as empty tags, and Save stayed on until the write failed (UI/UX
/// review, 2026-11-22).
/// </summary>
public static class TagReadProblem
{
    /// <param name="paths">The songs the window was opened on.</param>
    /// <param name="rootExists">Whether a drive root such as "D:\" is there (Directory.Exists).</param>
    public static string Describe(IReadOnlyList<string> paths, Func<string, bool> rootExists)
    {
        var one = paths.Count == 1;
        var driveGone = paths.Any(p => Path.GetPathRoot(p) is { Length: > 0 } root && !rootExists(root));

        if (driveGone)
        {
            return one
                ? "The music drive isn't connected, so this song can't be read or saved."
                : "The music drive isn't connected, so these songs can't be read or saved.";
        }

        return one
            ? "This song couldn't be read, so it can't be saved."
            : "These songs couldn't be read, so they can't be saved.";
    }
}
