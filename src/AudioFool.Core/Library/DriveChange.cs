namespace AudioFool.Core.Library;

/// <summary>
/// Whether a drive plugged in or pulled out while AudioFool runs is worth a
/// library refresh (session 68). Windows announces every drive, a phone or a USB
/// stick included, so a refresh happens only when one of the music folders now
/// differs from what the library says about it.
/// </summary>
public static class DriveChange
{
    /// <param name="musicFolders">The music folders, ticked or not.</param>
    /// <param name="unavailable">The folders the last scan couldn't reach.</param>
    /// <param name="arrived">Whether a drive was plugged in, rather than only pulled out.</param>
    /// <param name="folderExists">Whether a folder is there now.</param>
    public static bool WorthRefresh(
        IReadOnlyList<string> musicFolders,
        IReadOnlyList<string> unavailable,
        bool arrived,
        Func<string, bool> folderExists)
    {
        foreach (var folder in musicFolders)
        {
            var exists = folderExists(folder);
            var listedGone = unavailable.Any(gone => MusicFolderList.SameFolder(gone, folder));

            // Back under its own letter, or gone since the last scan.
            if (exists == listedGone)
                return true;

            // Still missing, but the new drive may hold it under another letter.
            if (arrived && !exists)
                return true;
        }

        return false;
    }
}
