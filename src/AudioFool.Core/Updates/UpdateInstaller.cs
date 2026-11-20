using System.IO.Compression;

namespace AudioFool.Core.Updates;

/// <summary>
/// Puts a downloaded release in place of the running app (2026-11-20, the user's
/// request).
/// <para>
/// Windows won't let a running program's files be overwritten, but it does let them
/// be renamed. So each file the release replaces is renamed to <c>name.old</c>, the
/// new file is copied in under the old name, the new copy is started, and the old
/// one closes. The new copy, once the old has gone, removes the <c>.old</c> files
/// (<see cref="CleanUp"/>). If a file can't be swapped, every file swapped so far is
/// put back, so a failed update leaves the app as it was.
/// </para>
/// </summary>
public static class UpdateInstaller
{
    public const string ExeName = "AudioFool.exe";
    public const string OldSuffix = ".old";

    /// <summary>
    /// Unzips <paramref name="zipPath"/> into <paramref name="stagingFolder"/> (emptied
    /// first) and checks it holds the app. Throws <see cref="UpdateCheckException"/>
    /// when it doesn't, or when an entry would land outside the folder.
    /// </summary>
    public static void Extract(string zipPath, string stagingFolder)
    {
        if (Directory.Exists(stagingFolder))
            Directory.Delete(stagingFolder, recursive: true);
        Directory.CreateDirectory(stagingFolder);

        var root = Path.GetFullPath(stagingFolder);
        if (!root.EndsWith(Path.DirectorySeparatorChar))
            root += Path.DirectorySeparatorChar;

        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            foreach (var entry in zip.Entries)
            {
                var target = Path.GetFullPath(Path.Combine(root, entry.FullName));
                if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    throw new UpdateCheckException("The download holds a file in an unexpected place.");

                // A folder entry ends in a slash and has no name.
                if (entry.Name.Length == 0)
                {
                    Directory.CreateDirectory(target);
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                entry.ExtractToFile(target, overwrite: true);
            }
        }
        catch (InvalidDataException ex)
        {
            throw new UpdateCheckException("The download isn't a readable zip.", ex);
        }

        if (!File.Exists(Path.Combine(stagingFolder, ExeName)))
            throw new UpdateCheckException($"The download doesn't contain {ExeName}.");
    }

    /// <summary>
    /// Swaps every file in <paramref name="stagingFolder"/> into
    /// <paramref name="appFolder"/>: the one there is renamed to <c>.old</c> and the
    /// new one copied in. Files the release doesn't have are left alone. On any
    /// failure the files swapped so far are put back and the error rethrown.
    /// </summary>
    public static void Swap(string stagingFolder, string appFolder)
    {
        var done = new List<(string Target, string? Old)>();
        try
        {
            foreach (var source in Directory.EnumerateFiles(stagingFolder, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(stagingFolder, source);
                var target = Path.Combine(appFolder, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);

                string? old = null;
                if (File.Exists(target))
                {
                    old = FreeOldName(target);
                    File.Move(target, old);
                }

                // Recorded before the copy, so a failed copy is undone too.
                done.Add((target, old));
                File.Copy(source, target, overwrite: false);
            }
        }
        catch
        {
            Undo(done);
            throw;
        }
    }

    /// <summary>
    /// Removes the <c>.old</c> files an update left in <paramref name="appFolder"/>.
    /// One still in use is left for the next start.
    /// </summary>
    public static int CleanUp(string appFolder)
    {
        var removed = 0;
        if (!Directory.Exists(appFolder))
            return 0;

        foreach (var file in Directory.EnumerateFiles(appFolder, "*" + OldSuffix, SearchOption.AllDirectories))
        {
            try
            {
                File.Delete(file);
                removed++;
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return removed;
    }

    // name.old, or name.1.old and up when an earlier update's .old is still locked.
    private static string FreeOldName(string target)
    {
        var candidate = target + OldSuffix;
        for (var i = 1; File.Exists(candidate); i++)
        {
            try
            {
                File.Delete(candidate);
                break;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                candidate = $"{target}.{i}{OldSuffix}";
            }
        }

        return candidate;
    }

    private static void Undo(List<(string Target, string? Old)> done)
    {
        for (var i = done.Count - 1; i >= 0; i--)
        {
            var (target, old) = done[i];
            try
            {
                if (File.Exists(target))
                    File.Delete(target);
                if (old is not null && File.Exists(old))
                    File.Move(old, target);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Best effort: the rest are still put back.
            }
        }
    }
}
