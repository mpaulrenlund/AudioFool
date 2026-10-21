namespace AudioFool.Core.Library;

/// <summary>
/// Puts a rewritten copy of a song in the original's place, beside which it was
/// made. <see cref="File.Replace(string, string, string?)"/> does it in one step
/// on the drive and keeps the original's creation date (Recently Added sorts by
/// it), its attributes and its permissions.
/// <para>
/// The original is kept under a backup name until the copy is in place. Without
/// one, Windows can fail partway (ERROR_UNABLE_TO_MOVE_REPLACEMENT_2, the drive
/// going, say) with the original gone and only the copy left, which the caller
/// then deletes as a failed working copy. With one, a swap that breaks off is put
/// back; if even that can't be done, the backup is left, named after the song,
/// for <see cref="LibraryScanner"/> to report.
/// </para>
/// </summary>
public static class FileSwap
{
    /// <summary>Every working file AudioFool leaves beside a song starts with this; a scan ignores them as songs.</summary>
    public const string Prefix = ".audiofool-";

    /// <summary>A song's own bytes, kept while it is swapped: never deleted automatically.</summary>
    public const string BackupPrefix = Prefix + "backup-";

    /// <summary>A working copy is named this, a GUID and ".part"; one left by a crash is safe to delete.</summary>
    public const string WorkingSuffix = ".part";

    /// <summary>
    /// Replaces <paramref name="original"/> with <paramref name="copy"/>. Throws
    /// what <see cref="File.Replace(string, string, string?)"/> throws, with the
    /// original back in its place whenever that can be done.
    /// </summary>
    public static void Replace(string copy, string original)
    {
        var backup = Path.Combine(Path.GetDirectoryName(original)!,
            $"{BackupPrefix}{Guid.NewGuid():N}-{Path.GetFileName(original)}.bak");

        try
        {
            File.Replace(copy, original, backup);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Broke off with the original moved to the backup name: put it back.
            if (!File.Exists(original) && File.Exists(backup))
            {
                try { File.Move(backup, original); }
                catch (Exception moveEx) when (moveEx is IOException or UnauthorizedAccessException) { }
            }

            throw;
        }

        try { File.Delete(backup); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>
    /// How long a working copy goes unwritten before it is taken for one a crash
    /// left: a save can be under way during a scan, and its copy is written to
    /// throughout.
    /// </summary>
    public static readonly TimeSpan LeftoverAge = TimeSpan.FromMinutes(10);

    /// <summary>A working copy a save may have left beside a song: safe to delete once it is old enough.</summary>
    public static bool IsWorkingCopy(string path)
    {
        var name = Path.GetFileName(path);
        return name.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)
               && !name.StartsWith(BackupPrefix, StringComparison.OrdinalIgnoreCase)
               && name.EndsWith(WorkingSuffix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A song's own bytes, left by a swap that broke off and couldn't be put back.</summary>
    public static bool IsBackup(string path) =>
        Path.GetFileName(path).StartsWith(BackupPrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Deletes the working copies not written for <see cref="LeftoverAge"/>; the
    /// number deleted. Never touches a backup.
    /// </summary>
    public static int DeleteLeftovers(IEnumerable<string> paths, DateTime nowUtc)
    {
        var deleted = 0;
        foreach (var path in paths.Where(IsWorkingCopy))
        {
            try
            {
                if (nowUtc - File.GetLastWriteTimeUtc(path) < LeftoverAge)
                    continue;

                File.Delete(path);
                deleted++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        return deleted;
    }
}
