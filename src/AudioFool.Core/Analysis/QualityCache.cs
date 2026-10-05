using System.Text.Json;
using System.Text.Json.Serialization;
using AudioFool.Core.Library;
using AudioFool.Core.Models;

namespace AudioFool.Core.Analysis;

/// <summary>What the library check concluded about one file.</summary>
public sealed record QualityResult(Verdict Verdict, IReadOnlyList<QualityFlag> Flags);

/// <summary>
/// The library check's results, saved beside the library cache as
/// <c>quality.json</c> so a check never has to be repeated for a file that
/// hasn't changed.
/// <para>
/// Keyed by file name, size and last-write time rather than by path: the
/// library's drive letter changes, and a result must follow its file to the new
/// letter. A file that is retagged or replaced gets a new key, so it is checked
/// again. Thread-safe, since the check runs on several threads.
/// </para>
/// </summary>
public sealed class QualityCache
{
    /// <summary>
    /// Bump when the rules in <see cref="QualityOpinion"/> change: an older file
    /// is ignored, so the next check reads every track again.
    /// </summary>
    public const int CurrentVersion = 1;

    public static string DefaultPath { get; } = Path.Combine(LibraryCache.CacheDirectory, "quality.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly Dictionary<string, QualityResult> _results;
    private readonly object _lock = new();

    private QualityCache(Dictionary<string, QualityResult> results) => _results = results;

    public static QualityCache Empty() => new([]);

    public int Count
    {
        get
        {
            lock (_lock)
                return _results.Count;
        }
    }

    /// <summary>
    /// Loads the saved results, or an empty cache when there are none, they were
    /// made by older rules, or the file is unreadable. Never throws.
    /// </summary>
    public static QualityCache Load(string? path = null)
    {
        try
        {
            path ??= DefaultPath;
            if (!File.Exists(path))
                return Empty();

            using var stream = File.OpenRead(path);
            var file = JsonSerializer.Deserialize<CacheFile>(stream, JsonOptions);
            if (file is null || file.Version != CurrentVersion)
                return Empty();

            return new QualityCache(file.Results.ToDictionary(
                r => r.Key, r => new QualityResult(r.Value.Verdict, r.Value.Flags)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                     or JsonException or NotSupportedException)
        {
            return Empty();
        }
    }

    /// <summary>Through a temporary file, so an interrupted write keeps the last good one.</summary>
    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        CacheFile file;
        lock (_lock)
        {
            file = new CacheFile
            {
                Version = CurrentVersion,
                Results = _results.ToDictionary(r => r.Key, r => new SavedResult(r.Value.Verdict, [.. r.Value.Flags])),
            };
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + ".tmp";
            using (var stream = File.Create(temp))
                JsonSerializer.Serialize(stream, file, JsonOptions);
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // Unsaved results only mean those files are checked again next time.
        }
    }

    public bool TryGet(Track track, out QualityResult result)
    {
        lock (_lock)
            return _results.TryGetValue(KeyOf(track), out result!);
    }

    public void Set(Track track, QualityResult result)
    {
        lock (_lock)
            _results[KeyOf(track)] = result;
    }

    /// <summary>The results for these tracks; tracks not yet checked are left out.</summary>
    public IReadOnlyDictionary<Track, QualityResult> For(IEnumerable<Track> tracks)
    {
        var found = new Dictionary<Track, QualityResult>(ReferenceEqualityComparer.Instance);
        lock (_lock)
        {
            foreach (var track in tracks)
            {
                if (_results.TryGetValue(KeyOf(track), out var result))
                    found[track] = result;
            }
        }

        return found;
    }

    /// <summary>"Song.flac|31457280|638946..." - the same file on any drive letter.</summary>
    public static string KeyOf(Track track) =>
        $"{Path.GetFileName(track.FilePath)}|{track.FileSize}|{track.ModifiedUtc.Ticks}";

    private sealed class CacheFile
    {
        public int Version { get; set; }
        public Dictionary<string, SavedResult> Results { get; set; } = [];
    }

    private sealed record SavedResult(Verdict Verdict, QualityFlag[] Flags);
}
