namespace AudioFool.Core.Library;

/// <summary>
/// A file's size and last-write time - the two cheap facts that decide whether
/// cached tags are still valid.
/// </summary>
public readonly record struct FileStamp(long Length, DateTime ModifiedUtc)
{
    public static readonly FileStamp Unknown = new(0, default);

    public static FileStamp For(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? new FileStamp(info.Length, info.LastWriteTimeUtc) : Unknown;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return Unknown;
        }
    }
}
