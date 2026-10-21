using AudioFool.Core.Library;

namespace AudioFool.ViewModels;

/// <summary>
/// Saves to the music files that are under way, so closing can wait for them:
/// every tag save, Keep Best Cover and Convert PNG Covers. A save is made safely
/// either way (TagWriter, CoverCleaner), but one stopped halfway through an album
/// leaves it half saved, and BASS would be shut down under playback's part in it.
/// All on the UI thread, so the count needs no lock.
/// </summary>
public sealed partial class MainViewModel
{
    private int _writesRunning;
    private TaskCompletionSource? _writesDone;

    /// <summary>While a save is running; closing waits for it (<see cref="FinishWritesBeforeClosing"/>).</summary>
    public bool IsWriting => _writesRunning > 0;

    /// <summary>Counts a save from now until the scope is disposed, at the end of the method making it.</summary>
    private WriteScope BeginWrite()
    {
        _writesRunning++;
        return new WriteScope(this);
    }

    private readonly struct WriteScope(MainViewModel owner) : IDisposable
    {
        public void Dispose() => owner.EndWrite();
    }

    private void EndWrite()
    {
        if (--_writesRunning > 0)
            return;

        _writesDone?.TrySetResult();
        _writesDone = null;
    }

    /// <summary>
    /// For closing: stops Keep Best Cover and Convert PNG Covers after the song in
    /// hand, and completes when every save has finished. Says so in the status bar
    /// while it waits.
    /// </summary>
    public Task FinishWritesBeforeClosing()
    {
        _keepCts?.Cancel();
        _pngCts?.Cancel();

        if (_writesRunning == 0)
            return Task.CompletedTask;

        StatusText = "Finishing saving - AudioFool will close when it's done.";
        return (_writesDone ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
    }

    /// <summary>
    /// After a scan: deletes the working copies a crash left beside songs, and
    /// returns a note for the status bar when a song's own copy, left by a swap
    /// that broke off, was found. That one is never deleted: it may be all there
    /// is of the song.
    /// </summary>
    private static async Task<string?> ClearLeftoversAsync(IReadOnlyList<string> leftovers)
    {
        if (leftovers.Count == 0)
            return null;

        await Task.Run(() => FileSwap.DeleteLeftovers(leftovers, DateTime.UtcNow));

        var backups = leftovers.Where(FileSwap.IsBackup).ToList();
        if (backups.Count == 0)
            return null;

        var more = backups.Count > 1 ? $" (and {backups.Count - 1} more)" : "";
        return $"⚠ A save that broke off left a song's own copy: {backups[0]}{more}. Check the song plays, then delete it.";
    }
}
