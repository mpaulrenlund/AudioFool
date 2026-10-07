namespace AudioFool.Core.Library;

/// <summary>
/// Something that opens audio files while it uses them - playback, which holds
/// the playing track and the next one. <see cref="TagWriter"/> makes every save
/// through it, since a save that resizes a file moves the audio under any
/// stream reading it, and a stream opened halfway through a save reads a
/// half-written file.
/// </summary>
public interface IFileHolder
{
    /// <summary>Whether <paramref name="path"/> is open right now.</summary>
    bool HoldsFile(string path);

    /// <summary>
    /// Makes <paramref name="save"/> with <paramref name="path"/> kept from being
    /// opened until it is done. If the file is open already,
    /// <paramref name="needsRelease"/> is asked whether the save would disturb
    /// the reader; if so, the file is let go for the save, then opened again and
    /// carried on from where it was.
    /// </summary>
    T Saving<T>(string path, Func<bool> needsRelease, Func<T> save);
}
