using System.Text.Json;
using System.Text.Json.Serialization;
using AudioFool.Core.Library;
using AudioFool.Core.Models;

namespace AudioFool.Core.Analysis;

/// <summary>
/// One song the user has vouched for against one Quality Check row: "this file
/// is real 24-bit", whatever the check thinks.
/// <para>
/// The path finds the song; the artist and album, with the file name, find it
/// again under another drive letter, as a playlist entry does. The format is
/// what the user vouched for: a file replaced by a different one (another
/// download of the album, say) no longer matches and is checked as usual. Tags
/// are not part of it, so a retag keeps the clearance.
/// </para>
/// </summary>
/// <param name="Flag">The row, as its first flag (see <see cref="QualityClearances.RowOf"/>).</param>
/// <param name="Bitrate">Only for lossy files: a lossless file's bitrate moves with its tags and art.</param>
public sealed record QualityClearance(
    string FilePath, string Artist, string Album, QualityFlag Flag,
    string Kind, int? BitDepth, int? SampleRate, int? Bitrate, DateTime ClearedUtc)
{
    public static QualityClearance For(Track track, QualityFlag flag, DateTime nowUtc) => new(
        track.FilePath, track.GroupingArtist, track.Album, QualityClearances.RowOf(flag),
        track.Kind, track.BitDepth, track.SampleRate,
        AudioFormats.IsLossy(track.FilePath) ? track.Bitrate : null, nowUtc);

    /// <summary>The same file as when it was cleared, as far as its format can tell.</summary>
    public bool FormatMatches(Track track) =>
        string.Equals(Kind, track.Kind, StringComparison.OrdinalIgnoreCase)
        && BitDepth == track.BitDepth
        && SampleRate == track.SampleRate
        && Bitrate == (AudioFormats.IsLossy(track.FilePath) ? track.Bitrate : null);
}

/// <summary>
/// The songs the user has cleared from Quality Check rows, in
/// <c>quality-cleared.json</c> beside the check's own results.
/// <para>
/// Kept apart from <see cref="QualityCache"/> on purpose: that file is keyed by
/// write time, so a retag would lose a clearance, and it is thrown away whenever
/// the rules change. A clearance is the user's word and outlives both. Each change
/// is saved straight away, through a temporary file. Nothing is written to the
/// music files. Thread-safe: the library check reads it on its own threads.
/// </para>
/// </summary>
public sealed class QualityClearances
{
    public static string DefaultPath { get; } = Path.Combine(LibraryCache.CacheDirectory, "quality-cleared.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path;
    private readonly List<QualityClearance> _entries;
    private readonly object _lock = new();
    private Dictionary<string, List<QualityClearance>> _byPath = [];
    private Dictionary<string, List<QualityClearance>> _byFileName = [];
    private bool _readOnly;

    private QualityClearances(string path, List<QualityClearance> entries)
    {
        _path = path;
        _entries = entries;
        Reindex();
    }

    public static QualityClearances Empty(string path) => new(path, []);

    /// <summary>
    /// Never throws. A missing file is no clearances. An unreadable one is moved
    /// aside rather than overwritten by the next save, so it can be recovered.
    /// </summary>
    public static QualityClearances Load(string path, DateTime nowUtc)
    {
        try
        {
            if (!File.Exists(path))
                return Empty(path);

            var entries = JsonSerializer.Deserialize<List<QualityClearance>>(File.ReadAllText(path), JsonOptions) ?? [];
            return new QualityClearances(path, [.. entries.Where(e => e is not null && e.FilePath is not null)]);
        }
        catch (JsonException)
        {
            try
            {
                File.Move(path, Path.ChangeExtension(path, $".unreadable-{nowUtc:yyyyMMddHHmmss}.json"), overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }

            return Empty(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // Unreadable for now (locked, say): start empty but don't save over it.
            return new QualityClearances(path, []) { _readOnly = true };
        }
    }

    public int Count
    {
        get
        {
            lock (_lock)
                return _entries.Count;
        }
    }

    /// <summary>
    /// The row a flag belongs to, as one flag. A song carries at most one flag of
    /// each pair, and which of the two can change (the Analyze window reads the
    /// whole file, the check three slices of it), so clearing "Possibly fake
    /// hi-res" also clears "Fake hi-res": both say the hi-res isn't real.
    /// </summary>
    public static QualityFlag RowOf(QualityFlag flag) => flag switch
    {
        QualityFlag.PossiblyLossySource => QualityFlag.LossySource,
        QualityFlag.PossiblyReEncodedLossy => QualityFlag.ReEncodedLossy,
        QualityFlag.PossiblyUpsampled => QualityFlag.Upsampled,
        _ => flag,
    };

    /// <summary>"Not Fake 24-bit": what clearing a row is called in a menu or on a button.</summary>
    public static string ClearLabel(QualityFlag flag) => RowOf(flag) switch
    {
        QualityFlag.Padded24Bit => "Not Fake 24-bit",
        QualityFlag.Upsampled => "Not Fake Hi-Res",
        QualityFlag.LossySource => "Not Transcoded",
        _ => "Not Upscaled",
    };

    /// <summary>"Fake 24-bit": the row's name, the "possibly" row included.</summary>
    public static string RowName(QualityFlag flag) => RowOf(flag) switch
    {
        QualityFlag.Padded24Bit => "Fake 24-bit",
        QualityFlag.Upsampled => "Fake hi-res",
        QualityFlag.LossySource => "Transcoded lossless",
        _ => "Upscaled MP3",
    };

    /// <summary>Whether the user has cleared this song from <paramref name="flag"/>'s row.</summary>
    public bool IsCleared(Track track, QualityFlag flag)
    {
        var row = RowOf(flag);
        lock (_lock)
            return Find(track).Any(e => e.Flag == row);
    }

    /// <summary>Whether the user has cleared this song from any row.</summary>
    public bool IsClearedFromAny(Track track)
    {
        lock (_lock)
            return Find(track).Any();
    }

    /// <summary>The rows this song is cleared from, as <see cref="RowOf"/> flags.</summary>
    public IReadOnlyList<QualityFlag> RowsClearedFor(Track track)
    {
        lock (_lock)
            return [.. Find(track).Select(e => e.Flag).Distinct()];
    }

    /// <summary>
    /// Clears these songs from <paramref name="flag"/>'s row. Returns how many
    /// weren't cleared from it already.
    /// </summary>
    public int Clear(IEnumerable<Track> tracks, QualityFlag flag, DateTime nowUtc)
    {
        var row = RowOf(flag);
        var added = 0;
        lock (_lock)
        {
            foreach (var track in tracks.Distinct(ReferenceEqualityComparer.Instance).Cast<Track>())
            {
                // An entry the format no longer matches is a different file: replace it.
                _entries.RemoveAll(e => e.Flag == row && SameSong(e, track) && !e.FormatMatches(track));
                if (Find(track).Any(e => e.Flag == row))
                    continue;

                _entries.Add(QualityClearance.For(track, row, nowUtc));
                added++;
            }

            if (added > 0)
                Reindex();
        }

        if (added > 0)
            Save();
        return added;
    }

    /// <summary>
    /// Puts these songs back in <paramref name="flag"/>'s row, or in every row
    /// when it is null. Returns how many songs had a clearance taken away.
    /// </summary>
    public int PutBack(IEnumerable<Track> tracks, QualityFlag? flag = null)
    {
        var row = flag is { } f ? RowOf(f) : (QualityFlag?)null;
        var restored = 0;
        lock (_lock)
        {
            foreach (var track in tracks.Distinct(ReferenceEqualityComparer.Instance).Cast<Track>())
            {
                if (_entries.RemoveAll(e => (row is null || e.Flag == row) && SameSong(e, track)) > 0)
                    restored++;
            }

            if (restored > 0)
                Reindex();
        }

        if (restored > 0)
            Save();
        return restored;
    }

    /// <summary>
    /// Brings entries found under another drive letter up to date with the
    /// song's current path, artist and album, and saves if any were. Without it
    /// a second move (a new letter, then an album retag) would lose them.
    /// </summary>
    public bool Repoint(IReadOnlyList<Track> tracks)
    {
        var changed = false;
        lock (_lock)
        {
            if (_entries.Count == 0)
                return false;

            // Only an entry whose own file isn't in the library moves: with the same
            // album on two drives, each copy keeps its own clearance.
            var paths = tracks.Select(t => t.FilePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var lost = _entries.Where(e => !paths.Contains(e.FilePath)).ToList();
            if (lost.Count == 0)
                return false;

            var byName = tracks.ToLookup(t => Path.GetFileName(t.FilePath), StringComparer.OrdinalIgnoreCase);
            foreach (var entry in lost)
            {
                var track = byName[Path.GetFileName(entry.FilePath)]
                    .FirstOrDefault(t => SameSong(entry, t) && entry.FormatMatches(t));
                if (track is null)
                    continue;

                _entries[_entries.IndexOf(entry)] = entry with
                {
                    FilePath = track.FilePath,
                    Artist = track.GroupingArtist,
                    Album = track.Album,
                };
                changed = true;
            }

            if (changed)
                Reindex();
        }

        if (changed)
            Save();
        return changed;
    }

    /// <summary>Writes the file. Unsaved, a clearance is still held for this session.</summary>
    public void Save()
    {
        if (_readOnly)
            return;

        string json;
        lock (_lock)
            json = JsonSerializer.Serialize(_entries, JsonOptions);

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temp = _path + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // Still held in memory, and written with the next change.
        }
    }

    /// <summary>The entries that apply to this song: by path, else by name, artist and album; format matching.</summary>
    private IEnumerable<QualityClearance> Find(Track track)
    {
        if (_byPath.TryGetValue(track.FilePath, out var byPath))
            return byPath.Where(e => e.FormatMatches(track));

        return _byFileName.TryGetValue(Path.GetFileName(track.FilePath), out var candidates)
            ? candidates.Where(e => SameSong(e, track) && e.FormatMatches(track))
            : [];
    }

    /// <summary>The entry's song: the same path, or the same file name, artist and album.</summary>
    private static bool SameSong(QualityClearance entry, Track track) =>
        string.Equals(entry.FilePath, track.FilePath, StringComparison.OrdinalIgnoreCase)
        || (string.Equals(Path.GetFileName(entry.FilePath), Path.GetFileName(track.FilePath), StringComparison.OrdinalIgnoreCase)
            && SortRules.NameComparer.Equals(entry.Artist, track.GroupingArtist)
            && SortRules.NameComparer.Equals(entry.Album, track.Album));

    private void Reindex()
    {
        _byPath = new Dictionary<string, List<QualityClearance>>(StringComparer.OrdinalIgnoreCase);
        _byFileName = new Dictionary<string, List<QualityClearance>>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in _entries)
        {
            Add(_byPath, entry.FilePath, entry);
            Add(_byFileName, Path.GetFileName(entry.FilePath), entry);
        }

        static void Add(Dictionary<string, List<QualityClearance>> index, string key, QualityClearance entry)
        {
            if (!index.TryGetValue(key, out var list))
                index[key] = list = [];
            list.Add(entry);
        }
    }
}
