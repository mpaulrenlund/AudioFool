using AudioFool.Core.Models;

namespace AudioFool.Core.Library;

/// <summary>
/// Rules for the list of watched music folders: when two entries are the same
/// folder, and what a removal takes out of the library. Pure - it opens nothing.
/// <para>
/// Duplicates matter because each entry has its own tick in the Libraries menu,
/// and a track is hidden when <em>any</em> entry covering it is unticked. Two
/// entries for one folder therefore have to be ticked together, which is easy to
/// get wrong when the menu closes after every click.
/// </para>
/// </summary>
public static class MusicFolderList
{
    /// <summary>
    /// A folder path in comparable form: trimmed, without a trailing separator.
    /// A drive root keeps its separator, since "E:" alone means the current
    /// directory on E:, not the root.
    /// </summary>
    public static string Normalize(string path)
    {
        var trimmed = path.Trim();
        var withoutSeparator = trimmed.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (withoutSeparator.Length == 0)
            return trimmed;

        return withoutSeparator.EndsWith(Path.VolumeSeparatorChar)
            ? withoutSeparator + Path.DirectorySeparatorChar
            : withoutSeparator;
    }

    /// <summary>Whether two entries name the same folder, ignoring case and a trailing separator.</summary>
    public static bool SameFolder(string a, string b) =>
        string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The folders with blanks and repeats removed, first occurrence kept, in order.
    /// Each kept entry is normalised.
    /// </summary>
    public static List<string> Distinct(IEnumerable<string> folders)
    {
        var result = new List<string>();
        foreach (var folder in folders)
        {
            if (string.IsNullOrWhiteSpace(folder))
                continue;

            var normalized = Normalize(folder);
            if (!result.Any(kept => SameFolder(kept, normalized)))
                result.Add(normalized);
        }

        return result;
    }

    /// <summary>
    /// The tracks left once <paramref name="removed"/> stops being watched. A track
    /// under the removed folder stays when another remaining folder still covers
    /// it - a parent or a child of the removed one.
    /// </summary>
    public static List<Track> WithoutFolder(
        IEnumerable<Track> tracks, string removed, IReadOnlyCollection<string> remaining)
    {
        var gone = Normalize(removed);
        var kept = remaining.Select(Normalize).ToList();

        return [.. tracks.Where(track =>
            !LibraryRelocator.IsUnder(track.FilePath, gone)
            || kept.Any(folder => LibraryRelocator.IsUnder(track.FilePath, folder)))];
    }
}
