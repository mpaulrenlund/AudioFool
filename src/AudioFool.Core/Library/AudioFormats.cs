namespace AudioFool.Core.Library;

/// <summary>
/// The single place that knows which files we consider music, what to call them
/// in the Kind column, and which of them have a meaningful bit depth.
/// </summary>
public static class AudioFormats
{
    /// <summary>Extension to Kind-column label.</summary>
    public static readonly IReadOnlyDictionary<string, string> KindByExtension =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            // Core four - what this player is built around.
            [".mp3"] = "MP3",
            [".flac"] = "FLAC",
            [".ogg"] = "Ogg Vorbis",
            [".oga"] = "Ogg Vorbis",
            [".dsf"] = "DSD",
            [".dff"] = "DSD",

            // Lossy
            [".m4a"] = "AAC",          // refined to ALAC at read time when detected
            [".m4b"] = "AAC",
            [".mp4"] = "AAC",
            [".aac"] = "AAC",
            [".opus"] = "Opus",
            [".wma"] = "WMA",
            [".mpc"] = "Musepack",
            [".mp+"] = "Musepack",
            [".mpp"] = "Musepack",
            [".ac3"] = "AC-3",
            [".dts"] = "DTS",

            // Lossless / uncompressed
            [".wav"] = "WAV",
            [".wave"] = "WAV",
            [".aiff"] = "AIFF",
            [".aif"] = "AIFF",
            [".aifc"] = "AIFF",
            [".ape"] = "APE",
            [".wv"] = "WavPack",
            [".tak"] = "TAK",
            [".alac"] = "ALAC",

            // Tracker / module formats
            [".mod"] = "MOD",
            [".s3m"] = "S3M",
            [".xm"] = "XM",
            [".it"] = "IT",
            [".mtm"] = "MTM",
            [".umx"] = "UMX",
            [".mo3"] = "MO3",
        };

    /// <summary>
    /// Formats that decode to float and therefore have no source bit depth.
    /// The Bit Depth column stays blank for these rather than inventing a 16.
    /// </summary>
    private static readonly HashSet<string> LossyExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ".mp3", ".ogg", ".oga", ".opus", ".m4a", ".m4b", ".mp4",
            ".aac", ".wma", ".mpc", ".mp+", ".mpp", ".ac3", ".dts",
        };

    /// <summary>
    /// Tracker formats. BASS loads these through MusicLoad rather than
    /// CreateStream, so playback has to branch on this.
    /// </summary>
    private static readonly HashSet<string> ModuleExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ".mod", ".s3m", ".xm", ".it", ".mtm", ".umx", ".mo3",
        };

    public static bool IsSupported(string path) =>
        KindByExtension.ContainsKey(Path.GetExtension(path));

    public static bool IsLossy(string path) =>
        LossyExtensions.Contains(Path.GetExtension(path));

    private static readonly HashSet<string> DsdExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".dsf", ".dff", ".dsd" };

    public static bool IsModule(string path) =>
        ModuleExtensions.Contains(Path.GetExtension(path));

    /// <summary>
    /// DSD containers. These get their own playback path: either converted to PCM,
    /// or passed straight through as DSD-over-PCM to a DAC that understands it.
    /// </summary>
    public static bool IsDsd(string path) =>
        DsdExtensions.Contains(Path.GetExtension(path));

    public static string KindFor(string path) =>
        KindByExtension.TryGetValue(Path.GetExtension(path), out var kind)
            ? kind
            : Path.GetExtension(path).TrimStart('.').ToUpperInvariant();

    /// <summary>Cover art file names looked for beside the audio, in priority order.</summary>
    public static readonly string[] FolderArtNames =
    [
        "cover.jpg", "cover.jpeg", "cover.png",
        "folder.jpg", "folder.jpeg", "folder.png",
        "front.jpg", "front.jpeg", "front.png",
        "album.jpg", "album.jpeg", "album.png",
        "albumart.jpg", "artwork.jpg", "thumb.jpg",
    ];
}
