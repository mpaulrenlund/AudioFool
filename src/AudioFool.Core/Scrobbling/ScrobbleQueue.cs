using System.Text.Json;

namespace AudioFool.Core.Scrobbling;

/// <summary>
/// Scrobbles waiting to be sent, persisted so none are lost to the network being
/// down, Last.fm being offline, or the app closing first. Every change is saved
/// straight away, flushed and swapped in with the save before kept as
/// <c>scrobbles.json.bak</c>, which is read if the file is missing or damaged
/// (<see cref="Library.SafeJsonFile"/>, session 71).
/// <para>
/// Entries older than <see cref="MaxAge"/> are dropped on load: Last.fm ignores
/// scrobbles more than two weeks old, so sending them would only waste requests.
/// </para>
/// </summary>
public sealed class ScrobbleQueue
{
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(14);

    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AudioFool",
        "scrobbles.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _path;
    private readonly List<ScrobbleEntry> _entries = [];
    private readonly Lock _gate = new();

    private ScrobbleQueue(string path) => _path = path;

    public int Count
    {
        get { lock (_gate) return _entries.Count; }
    }

    /// <summary>
    /// Never throws. The file, else the save before it; with neither, an empty
    /// queue: a lost queue costs a few plays, refusing to start would cost more.
    /// </summary>
    public static ScrobbleQueue Load(string path, DateTimeOffset now)
    {
        var queue = new ScrobbleQueue(path);
        var (saved, how) = Library.SafeJsonFile.Load<List<ScrobbleEntry>>(path, now.UtcDateTime, JsonOptions);
        if (saved is not null)
        {
            var oldest = (now - MaxAge).ToUnixTimeSeconds();
            queue._entries.AddRange(saved.Where(e => e is not null && e.Timestamp >= oldest));

            // From the backup: a file of its own again, so the next start reads it.
            if (queue._entries.Count != saved.Count || how == Library.SafeLoad.FromBackup)
                queue.Save();
        }

        return queue;
    }

    public void Add(ScrobbleEntry entry)
    {
        lock (_gate)
        {
            _entries.Add(entry);
            Save();
        }
    }

    /// <summary>The oldest entries, up to <paramref name="max"/>, without removing them.</summary>
    public IReadOnlyList<ScrobbleEntry> Peek(int max)
    {
        lock (_gate)
            return _entries.Take(max).ToList();
    }

    /// <summary>Removes these exact entries (by reference), leaving anything added since.</summary>
    public void Remove(IEnumerable<ScrobbleEntry> sent)
    {
        lock (_gate)
        {
            var set = sent.ToHashSet(ReferenceEqualityComparer.Instance);
            if (_entries.RemoveAll(e => set.Contains(e)) > 0)
                Save();
        }
    }

    private void Save()
    {
        try
        {
            Library.SafeJsonFile.Write(_path, JsonSerializer.SerializeToUtf8Bytes(_entries, JsonOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // Still held in memory, and sent from there if the network comes back.
        }
    }
}
