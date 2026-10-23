using System.Text.Json;

namespace AudioFool.Core.Library;

/// <summary>How <see cref="SafeJsonFile.Load{T}"/> got on.</summary>
public enum SafeLoad
{
    /// <summary>The file itself was read.</summary>
    Read,

    /// <summary>No file and no backup: a first run.</summary>
    Missing,

    /// <summary>The file was missing or damaged, and the backup from the save before it was read. A damaged file is moved aside.</summary>
    FromBackup,

    /// <summary>The file was damaged and moved aside, with no backup to read: starting empty.</summary>
    Damaged,

    /// <summary>The file is there but couldn't be opened (held by another program): nothing may be saved over it this session. The backup is read if it can be.</summary>
    Locked,
}

/// <summary>
/// A small JSON file saved the way settings.json is (session 60), for the ones
/// that can't be rebuilt from the music: playlists and Quality Check clearances
/// (session 67), plays waiting for Last.fm (session 71), and library.json, which a
/// scan rebuilds but only with the drive there (session 71). <see cref="Write"/> makes the new file beside the old one, makes
/// sure it is on the disk, and only then swaps it in, keeping the old one as
/// <c>.bak</c>; <see cref="Load{T}"/> reads that backup when the file is missing
/// or damaged. Before, a save moved an unflushed file over the old one, so a crash
/// in the seconds after could leave an empty or garbage file and no copy.
/// </summary>
public static class SafeJsonFile
{
    /// <summary>The copy of the save before the last, kept by <see cref="Write"/>.</summary>
    public static string BackupPathFor(string path) => path + ".bak";

    /// <summary>
    /// Writes <paramref name="bytes"/> to a working file, flushed to the disk, then
    /// swaps it in with <see cref="File.Replace(string, string, string)"/>, the old
    /// file becoming the backup. Throws as file calls do; the file on disk is then
    /// still the last good one.
    /// </summary>
    public static void Write(string path, byte[] bytes) => Write(path, stream => stream.Write(bytes));

    /// <summary>
    /// The same, with the file written by <paramref name="write"/>: for a file too
    /// big to want in memory twice, library.json (session 71).
    /// </summary>
    public static void Write(string path, Action<Stream> write)
    {
        var temp = path + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                write(stream);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(path))
                File.Replace(temp, path, BackupPathFor(path));
            else
                File.Move(temp, path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
    }

    /// <summary>
    /// Never throws. The file, else its backup; a damaged file (not JSON, or "null")
    /// is moved aside as <c>name.unreadable-yyyyMMddHHmmss.json</c> rather than
    /// saved over, so it can be recovered.
    /// </summary>
    public static (T? Value, SafeLoad How) Load<T>(string path, DateTime nowUtc, JsonSerializerOptions options) where T : class
    {
        var (value, state) = Read<T>(path, options);
        if (state == ReadState.Read)
            return (value, SafeLoad.Read);

        if (state == ReadState.Broken)
            MoveAside(path, nowUtc);

        var (backup, backupState) = Read<T>(BackupPathFor(path), options);
        return state switch
        {
            ReadState.Locked => (backup, SafeLoad.Locked),
            _ when backupState == ReadState.Read => (backup, SafeLoad.FromBackup),
            ReadState.Broken => (null, SafeLoad.Damaged),
            _ => (null, SafeLoad.Missing),
        };
    }

    private enum ReadState { Missing, Read, Broken, Locked }

    private static (T? Value, ReadState State) Read<T>(string path, JsonSerializerOptions options) where T : class
    {
        try
        {
            if (!File.Exists(path))
                return (null, ReadState.Missing);

            // Read as it streams in: library.json is 15 MB.
            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize<T>(stream, options) is { } value
                ? (value, ReadState.Read)
                : (null, ReadState.Broken);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            return (null, ReadState.Broken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (null, ReadState.Locked);
        }
    }

    private static void MoveAside(string path, DateTime nowUtc)
    {
        try
        {
            File.Move(path, Path.ChangeExtension(path, $".unreadable-{nowUtc:yyyyMMddHHmmss}.json"), overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
