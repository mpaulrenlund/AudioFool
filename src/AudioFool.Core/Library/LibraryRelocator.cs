using AudioFool.Core.Models;

namespace AudioFool.Core.Library;

/// <summary>
/// Copes with a music folder that has moved to a different drive letter.
/// <para>
/// Windows hands out drive letters in the order things are plugged in, so a
/// portable drive that is E: today can be F: tomorrow with nothing else changed.
/// Every cached path would then point nowhere, and the library would look deleted.
/// </para>
/// <para>
/// Rather than store volume GUIDs, this looks for the same folder under a different
/// letter and then <em>proves</em> it is the same content by checking that files the
/// cache knows about are actually there. That keeps it dependency-free while still
/// refusing to remap onto some unrelated drive that happens to have a Music folder.
/// </para>
/// </summary>
public static class LibraryRelocator
{
    /// <summary>How many known files to test before accepting a candidate drive.</summary>
    private const int SampleSize = 25;

    /// <summary>Fraction of the sample that must exist for a match to be accepted.</summary>
    private const double RequiredHitRate = 0.8;

    public sealed record Relocation(string OldFolder, string NewFolder);

    /// <summary>
    /// Looks for <paramref name="missingFolder"/> on another drive. Returns null when
    /// the folder is present after all, or when no drive can be confirmed.
    /// </summary>
    public static Relocation? FindRelocation(string missingFolder, IReadOnlyList<Track> knownTracks)
    {
        if (string.IsNullOrWhiteSpace(missingFolder) || Directory.Exists(missingFolder))
            return null;

        string? root;
        string tail;
        try
        {
            root = Path.GetPathRoot(missingFolder);
            if (string.IsNullOrEmpty(root))
                return null;

            tail = missingFolder[root.Length..];
        }
        catch (ArgumentException)
        {
            return null;
        }

        // A UNC or root-only path gives us nothing to match on.
        if (string.IsNullOrEmpty(tail))
            return null;

        var sample = knownTracks
            .Where(t => IsUnder(t.FilePath, missingFolder))
            .Take(SampleSize)
            .Select(t => t.FilePath)
            .ToList();

        var candidates = new List<string>();

        foreach (var drive in SafeDrives())
        {
            if (string.Equals(drive, root, StringComparison.OrdinalIgnoreCase))
                continue;

            var candidate = Path.Combine(drive, tail);
            if (!DirectoryExists(candidate))
                continue;

            // With nothing cached to verify against, only an unambiguous single
            // candidate is safe to accept.
            if (sample.Count == 0)
            {
                candidates.Add(candidate);
                continue;
            }

            var hits = sample.Count(path => FileExists(Rebase(path, missingFolder, candidate)));
            if (hits >= Math.Max(1, (int)(sample.Count * RequiredHitRate)))
                return new Relocation(missingFolder, candidate);
        }

        return sample.Count == 0 && candidates.Count == 1
            ? new Relocation(missingFolder, candidates[0])
            : null;
    }

    /// <summary>
    /// Re-points every cached track from one root to another, so a drive-letter
    /// change costs nothing instead of a full rescan.
    /// </summary>
    public static List<Track> Rebase(IReadOnlyList<Track> tracks, string oldRoot, string newRoot)
    {
        var result = new List<Track>(tracks.Count);

        foreach (var track in tracks)
        {
            if (!IsUnder(track.FilePath, oldRoot))
            {
                result.Add(track);
                continue;
            }

            result.Add(track.Relocated(
                Rebase(track.FilePath, oldRoot, newRoot),
                track.FolderArtPath is null ? null : Rebase(track.FolderArtPath, oldRoot, newRoot)));
        }

        return result;
    }

    /// <summary>Swaps one path prefix for another, preserving the rest verbatim.</summary>
    public static string Rebase(string path, string oldRoot, string newRoot) =>
        IsUnder(path, oldRoot)
            ? Path.Combine(newRoot, path[oldRoot.Length..].TrimStart(Path.DirectorySeparatorChar,
                                                                    Path.AltDirectorySeparatorChar))
            : path;

    /// <summary>
    /// Whether a path sits inside a folder. Compares on a separator boundary so
    /// "C:\MusicOld" is not treated as being inside "C:\Music".
    /// </summary>
    public static bool IsUnder(string path, string folder)
    {
        if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(folder))
            return false;

        var trimmed = folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (!path.StartsWith(trimmed, StringComparison.OrdinalIgnoreCase))
            return false;

        // Equal, or the next character starts a new path segment.
        return path.Length == trimmed.Length
               || path[trimmed.Length] == Path.DirectorySeparatorChar
               || path[trimmed.Length] == Path.AltDirectorySeparatorChar;
    }

    private static IEnumerable<string> SafeDrives()
    {
        DriveInfo[] drives;
        try
        {
            drives = DriveInfo.GetDrives();
        }
        catch (IOException)
        {
            yield break;
        }

        foreach (var drive in drives)
        {
            var ready = false;
            try
            {
                // Touching IsReady on a disconnected network drive can throw.
                ready = drive.IsReady;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }

            if (ready)
                yield return drive.Name;
        }
    }

    private static bool DirectoryExists(string path)
    {
        try { return Directory.Exists(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    private static bool FileExists(string path)
    {
        try { return File.Exists(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }
}
