namespace AudioFool.Core.Library;

/// <summary>
/// A file's size and last-write time - the two cheap facts that decide whether
/// cached tags are still valid.
/// </summary>
public readonly record struct FileStamp(long Length, DateTime ModifiedUtc)
{
    public static readonly FileStamp Unknown = new(0, default);

    /// <summary>
    /// When the file was created on this drive, i.e. when it was added: a copy
    /// sets it to the time of copying, and saving tags leaves it alone. Comes
    /// free with the same stat. Not part of deciding whether tags are stale.
    /// </summary>
    public DateTime CreatedUtc { get; init; }

    public static FileStamp For(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists
                ? new FileStamp(info.Length, info.LastWriteTimeUtc) { CreatedUtc = info.CreationTimeUtc }
                : Unknown;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return Unknown;
        }
    }
}
