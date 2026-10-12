using System.Threading;

namespace AudioFool.ViewModels;

/// <summary>
/// Makes the Albums list's saved thumbnail for every album that has none, in the
/// background (session 47, the user's request), so the first visit to an artist is
/// as quick as a later one. About 4 KB an album, 10 MB for the whole library.
/// </summary>
public partial class MainViewModel
{
    private CancellationTokenSource? _artPrefillCts;
    private static int _prunedSavedArt;

    /// <summary>
    /// Started when a library check ends, so it never competes with a scan for the
    /// music drive; a new scan stops it, and the next check starts it again. One
    /// album at a time on a below-normal-priority thread. Albums that already
    /// have a copy cost one look in the local folder each.
    /// </summary>
    private void StartArtPrefill()
    {
        StopArtPrefill();
        var cts = _artPrefillCts = new CancellationTokenSource();
        var albums = _library.Artists.SelectMany(a => a.Albums).ToList();
        var art = _artService;

        var thread = new Thread(() =>
        {
            // Once a run: copies unused for 90 days.
            if (Interlocked.Exchange(ref _prunedSavedArt, 1) == 0)
                art.PruneSavedCopies();

            var made = 0;
            foreach (var album in albums)
            {
                if (cts.Token.IsCancellationRequested)
                    break;
                try
                {
                    if (art.EnsureSavedThumbnail(album))
                        made++;
                }
                catch (Exception)
                {
                    // One unreadable album must not stop the rest.
                }
            }

            // Reading the covers churns through buffers, as a tag scan does.
            if (made > 0)
                ReclaimScanMemory();
        })
        {
            IsBackground = true,
            Priority = ThreadPriority.BelowNormal,
            Name = "Art prefill",
        };
        thread.Start();
    }

    private void StopArtPrefill() => _artPrefillCts?.Cancel();
}
