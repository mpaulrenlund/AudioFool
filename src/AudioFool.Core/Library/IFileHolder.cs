namespace AudioFool.Core.Library;

/// <summary>
/// Something that keeps audio files open while it uses them - playback, which
/// holds the playing track and the next one. <see cref="TagWriter"/> asks it
/// before a save, since a save that resizes a file moves the audio under any
/// stream still reading it.
/// </summary>
public interface IFileHolder
{
    /// <summary>Whether <paramref name="path"/> is open right now.</summary>
    bool HoldsFile(string path);

    /// <summary>
    /// Lets go of <paramref name="path"/> for the length of <paramref name="write"/>,
    /// then opens it again and carries on where it was.
    /// </summary>
    T WhileReleased<T>(string path, Func<T> write);
}
