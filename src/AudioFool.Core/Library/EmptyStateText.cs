namespace AudioFool.Core.Library;

/// <summary>
/// What the Songs panel says when no album is selected. The old text always said
/// "Pick an artist", which was wrong whenever there was no artist to pick.
/// </summary>
public static class EmptyStateText
{
    /// <param name="hasAnyTracks">The library holds at least one track, ticked or not.</param>
    /// <param name="hasTickedTracks">At least one track is in a ticked folder.</param>
    /// <param name="isNarrowed">A search or a Statistics filter is active.</param>
    /// <param name="hasArtists">The Artists list shows at least one artist.</param>
    /// <param name="isScanning">A scan is running.</param>
    public static (string Title, string Detail) Describe(
        bool hasAnyTracks, bool hasTickedTracks, bool isNarrowed, bool hasArtists, bool isScanning)
    {
        if (!hasAnyTracks)
        {
            return isScanning
                ? ("Scanning the library", "Music appears here as it's found.")
                : ("No music yet", "Add a folder from the logo menu, under Libraries.");
        }

        if (!hasTickedTracks)
            return ("Every folder is unticked", "Tick one under Libraries in the logo menu.");

        if (!hasArtists && isNarrowed)
            return ("No matches", "Nothing in the library matches.");

        return ("No album selected", "Pick an artist, then one of their albums.");
    }
}
