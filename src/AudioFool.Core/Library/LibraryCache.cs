using System.Text.Json;
using System.Text.Json.Serialization;
using AudioFool.Core.Models;

namespace AudioFool.Core.Library;

/// <summary>
/// The scanned library, persisted so startup doesn't have to re-read every tag.
/// <para>
/// Reading tags from 26,000 files takes about 18 seconds; loading them back from
/// this file takes about a third of a second. Nothing else about a scan is
/// expensive - enumerating the files is under 0.2s and grouping them is 0.04s -
/// so the tags are the only thing worth storing.
/// </para>
/// <para>
/// Lives under LocalApplicationData rather than beside the settings in Roaming:
/// it's a rebuildable cache, and there's no sense syncing 14 MB of it between
/// machines whose file paths won't even match.
/// </para>
/// </summary>
public sealed class LibraryCache
{
    /// <summary>
    /// Bump when <see cref="Track"/> changes shape in a way that makes old files
    /// unreadable or wrong. A mismatch is treated as "no cache", not an error.
    /// <para>
    /// Version 2 added <see cref="Track.TrackCount"/> and <see cref="Track.DiscCount"/>.
    /// Adding a field would normally deserialise as null and need no bump, but
    /// null is exactly the problem here: every cached track still matches its
    /// file on size and write time, so the incremental scan would never re-read a
    /// tag and the two new columns would stay blank for good. Discarding the
    /// cache costs one ~18 s full scan and is the only thing that fills them.
    /// </para>
    /// </summary>
    public const int CurrentVersion = 2;

    public int Version { get; set; } = CurrentVersion;

    /// <summary>Folders this cache was built from, to spot a changed library root.</summary>
    public List<string> Folders { get; set; } = [];

    public DateTime SavedUtc { get; set; }

    public List<Track> Tracks { get; set; } = [];

    [JsonIgnore]
    public static string CacheDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AudioFool");

    [JsonIgnore]
    public static string CachePath { get; } = Path.Combine(CacheDirectory, "library.json");

    // Indented output would add several megabytes for no benefit; nobody reads this
    // by hand, and the writer is on the UI's critical path at shutdown.
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Loads the cache, or null when there isn't a usable one. Never throws: a
    /// corrupt or half-written cache should cost a slow startup, not a crash.
    /// </summary>
    public static LibraryCache? Load()
    {
        try
        {
            if (!File.Exists(CachePath))
                return null;

            using var stream = File.OpenRead(CachePath);
            var cache = JsonSerializer.Deserialize<LibraryCache>(stream, JsonOptions);

            if (cache is null || cache.Version != CurrentVersion || cache.Tracks.Count == 0)
                return null;

            return cache;
        }
        catch (Exception ex) when (ex is IOException
                                     or UnauthorizedAccessException
                                     or JsonException
                                     or NotSupportedException)
        {
            return null;
        }
    }

    public static LibraryCache From(IEnumerable<string> folders, IEnumerable<Track> tracks) =>
        new()
        {
            Version = CurrentVersion,
            Folders = [.. folders],
            SavedUtc = DateTime.UtcNow,
            Tracks = [.. tracks],
        };

    /// <summary>
    /// Writes via a temporary file and then moves it into place, so an interrupted
    /// write leaves the previous good cache rather than a truncated one.
    /// </summary>
    public void Save()
    {
        try
        {
            Directory.CreateDirectory(CacheDirectory);

            var temp = CachePath + ".tmp";
            using (var stream = File.Create(temp))
                JsonSerializer.Serialize(stream, this, JsonOptions);

            File.Move(temp, CachePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException
                                     or UnauthorizedAccessException
                                     or NotSupportedException)
        {
            // A cache that fails to save just means the next start is slow.
        }
    }

    public static void Delete()
    {
        try
        {
            if (File.Exists(CachePath))
                File.Delete(CachePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nothing useful to do about it.
        }
    }

    /// <summary>
    /// Whether the cache was built from the same set of folders, ignoring order and
    /// case. When it wasn't, the cached tracks are still useful as a tag source but
    /// shouldn't be shown as-is - they may cover folders that are no longer watched.
    /// </summary>
    public bool CoversSameFolders(IEnumerable<string> folders) =>
        Folders.ToHashSet(StringComparer.OrdinalIgnoreCase)
               .SetEquals(folders.ToHashSet(StringComparer.OrdinalIgnoreCase));

    /// <summary>Cached tracks keyed by path, for the incremental scan to look up.</summary>
    public Dictionary<string, Track> ByPath()
    {
        var map = new Dictionary<string, Track>(Tracks.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var track in Tracks)
            map[track.FilePath] = track;

        return map;
    }
}
