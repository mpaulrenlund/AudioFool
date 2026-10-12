using System.Security.Cryptography;
using System.Text;
using AudioFool.Core.Analysis;
using AudioFool.Core.Models;

namespace AudioFool.Core.Art;

/// <summary>
/// Small copies of album covers, already shrunk to the size they are shown at,
/// kept on the computer's own drive in <c>%LOCALAPPDATA%\AudioFool\art</c>
/// (session 47). Showing a cover otherwise means opening an audio file on the music
/// drive and shrinking a picture of typically 300 KB, sometimes 12 MB, after
/// every launch; a slow drive or processor makes the Albums list slow to fill.
/// <para>
/// A copy is named by a hash of the width and the first five tracks' file names,
/// sizes and write times (the tracks <c>AlbumArtService</c> looks in), so it is
/// found again after a drive-letter change, and a retag or a new cover gives a new
/// name rather than a stale picture. A zero-length copy records "no cover". Not
/// noticed: a cover file in the folder changed by another program while no track
/// changed.
/// </para>
/// <para>
/// Best effort throughout: any file error reads as "no copy" or is skipped.
/// </para>
/// </summary>
public sealed class ArtDiskCache
{
    private const string Extension = ".art";

    /// <summary>Copies used within this long are left alone by <see cref="Prune"/>.</summary>
    public static readonly TimeSpan KeepFor = TimeSpan.FromDays(90);

    /// <summary>A copy older than this is re-stamped when used, so pruning sees it as in use.</summary>
    private static readonly TimeSpan TouchAfter = TimeSpan.FromDays(30);

    public ArtDiskCache(string folder) => Folder = folder;

    public string Folder { get; }

    public static string DefaultFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AudioFool", "art");

    /// <summary>"96|Song.flac|31457280|638946...|..." - the same album on any drive letter.</summary>
    public static string KeyFor(Album album, int width) =>
        $"{width}|{string.Join("|", album.Tracks.Take(5).Select(QualityCache.KeyOf))}";

    public string PathFor(string key) =>
        Path.Combine(Folder, Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes(key))) + Extension);

    public bool Contains(string key) => File.Exists(PathFor(key));

    /// <summary>
    /// False when there is no copy. True with <paramref name="image"/> null when the
    /// album was found to have no cover, or with the copy's bytes.
    /// </summary>
    public bool TryRead(string key, out byte[]? image) => TryRead(key, DateTime.UtcNow, out image);

    public bool TryRead(string key, DateTime nowUtc, out byte[]? image)
    {
        image = null;
        var path = PathFor(key);
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
                return false;

            var bytes = File.ReadAllBytes(path);
            if (nowUtc - info.LastWriteTimeUtc > TouchAfter)
                File.SetLastWriteTimeUtc(path, nowUtc);

            image = bytes.Length == 0 ? null : bytes;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Saves a copy; null records "no cover". Written to a temporary file and moved
    /// into place, so a copy is never half there.
    /// </summary>
    public void Write(string key, byte[]? image)
    {
        var path = PathFor(key);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllBytes(temp, image ?? []);
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(temp);
        }
    }

    public void Remove(string key) => TryDelete(PathFor(key));

    /// <summary>
    /// Deletes copies not used for <see cref="KeepFor"/>, and temporary files a
    /// closed app left behind. Returns how many went.
    /// </summary>
    public int Prune(DateTime nowUtc)
    {
        var removed = 0;
        try
        {
            if (!Directory.Exists(Folder))
                return 0;

            foreach (var path in Directory.EnumerateFiles(Folder))
            {
                var age = nowUtc - File.GetLastWriteTimeUtc(path);
                var stale = path.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
                    ? age > TimeSpan.FromDays(1)
                    : path.EndsWith(Extension, StringComparison.OrdinalIgnoreCase) && age > KeepFor;
                if (stale && TryDelete(path))
                    removed++;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return removed;
    }

    private static bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
