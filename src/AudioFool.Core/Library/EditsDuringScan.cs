using AudioFool.Core.Models;

namespace AudioFool.Core.Library;

/// <summary>
/// Songs saved in the app while a scan was running. The scan works from the
/// library as it was when it started, and its result replaces the whole library,
/// so without this a tag save, an adopted folder cover or a Keep Best Cover made
/// in the meantime would be put back to the scan's older copy.
/// </summary>
public static class EditsDuringScan
{
    /// <summary>
    /// The scanned tracks, each replaced by its edited copy unless the scan found
    /// the file changed later still (retagged in another program after the save).
    /// A tie goes to the edit: it may carry what no stamp shows, like a folder cover.
    /// A song the scan no longer has stays gone.
    /// </summary>
    public static List<Track> Apply(IEnumerable<Track> scanned, IReadOnlyDictionary<string, Track> edited) =>
        [.. scanned.Select(track =>
            edited.TryGetValue(track.FilePath, out var saved) && saved.ModifiedUtc >= track.ModifiedUtc
                ? saved
                : track)];
}
