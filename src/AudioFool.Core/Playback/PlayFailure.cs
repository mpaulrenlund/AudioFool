using ManagedBass;

namespace AudioFool.Core.Playback;

/// <summary>Why a play didn't start, as the status bar tells it.</summary>
public enum PlayFailureKind
{
    /// <summary>The audio engine itself didn't start.</summary>
    Engine,

    /// <summary>The file's drive isn't there: unplugged, or under another letter.</summary>
    DriveMissing,

    /// <summary>The drive is there but the file isn't: moved, renamed or deleted.</summary>
    FileMissing,

    /// <summary>The file is there but couldn't be opened: another program has it, or no permission.</summary>
    FileUnopenable,

    /// <summary>Opened, but not something BASS and its add-ons can decode (or damaged).</summary>
    Format,

    /// <summary>The song opened; the output device didn't.</summary>
    Output,
}

/// <summary>
/// Why a play failed (session 71). Until then every failure said "The format
/// may need an add-on", the drive unplugged and the device busy alike (session
/// 60's review). <see cref="AudioEngine"/> records one in
/// <see cref="AudioEngine.LastPlayFailure"/>; <see cref="Describe"/> words it.
/// </summary>
public sealed record PlayFailure(PlayFailureKind Kind, string FilePath, string? Detail = null)
{
    /// <summary>
    /// A song that wouldn't open: missing (its drive gone, or just the file), or
    /// there, in which case BASS's error says which. Call right after the failed
    /// open, on the same thread, so <paramref name="error"/> is its error.
    /// </summary>
    public static PlayFailure ForFile(string path, bool fileExists, bool driveThere, Errors error) =>
        new(!fileExists ? (driveThere ? PlayFailureKind.FileMissing : PlayFailureKind.DriveMissing)
            : error == Errors.FileOpen ? PlayFailureKind.FileUnopenable
            : PlayFailureKind.Format,
            path);

    /// <summary><see cref="ForFile(string, bool, bool, Errors)"/> for a file on disk now.</summary>
    public static PlayFailure ForFile(string path, Errors error) =>
        ForFile(path, File.Exists(path), DriveThere(path), error);

    /// <summary>Whether the root of <paramref name="path"/> (its drive or share) is reachable.</summary>
    private static bool DriveThere(string path)
    {
        try
        {
            return Path.GetPathRoot(path) is { Length: > 0 } root && Directory.Exists(root);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>The status bar's line for it.</summary>
    public string Describe()
    {
        var name = Path.GetFileName(FilePath);
        return Kind switch
        {
            PlayFailureKind.DriveMissing => $"Can't play {name}: the drive isn't connected.",
            PlayFailureKind.FileMissing => $"Can't play {name}: the file isn't there any more. Refresh to update the library.",
            PlayFailureKind.FileUnopenable => $"Couldn't play {name}: the file couldn't be opened. Another program may have it open.",
            PlayFailureKind.Output => $"Couldn't play {name}: {Detail ?? "the output device wouldn't open."}",
            PlayFailureKind.Engine => $"Couldn't play {name}: the audio engine didn't start{(Detail is null ? "." : $" ({Detail.TrimEnd('.')}).")}",
            _ => $"Couldn't play {name}. The format may need an add-on that isn't installed.",
        };
    }
}
