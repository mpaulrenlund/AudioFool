namespace AudioFool.Core.Library;

/// <summary>
/// How the Artists list is ordered, chosen by clicking its header:
/// ARTISTS, ARTISTS · RECENT, ARTISTS · SIZE.
/// </summary>
public enum ArtistOrder
{
    /// <summary>A-Z, ignoring a leading article. What the app opens on.</summary>
    Name,

    /// <summary>Most recently added file first.</summary>
    Recent,

    /// <summary>Most space taken first.</summary>
    Size,
}
