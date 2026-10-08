using System.Text.Json;
using AudioFool.Core.Analysis;
using AudioFool.Core.Library;
using AudioFool.Core.Models;

namespace AudioFool.Core.Art;

/// <summary>
/// The cover check's results, saved beside the library cache as
/// <c>covers.json</c>, so a file is read again only once it changes. Keyed as
/// <see cref="QualityCache"/> is (file name, size, write time), so a result
/// follows its file to a new drive letter and a cleaned or retagged file is
/// read again. Thread-safe, since the check runs on several threads.
/// </summary>
public sealed class CoverCache
{
    /// <summary>Bump when what <see cref="CoverCleaner.Survey"/> records changes: an older file is ignored.</summary>
    public const int CurrentVersion = 1;

    public static string DefaultPath { get; } = Path.Combine(LibraryCache.CacheDirectory, "covers.json");

    private readonly Dictionary<string, CoverFinding> _results;
    private readonly object _lock = new();

    private CoverCache(Dictionary<string, CoverFinding> results) => _results = results;

    public static CoverCache Empty() => new([]);

    /// <summary>The saved results, or none when there are none, they're from an older version, or the file is unreadable. Never throws.</summary>
    public static CoverCache Load(string? path = null)
    {
        try
        {
            path ??= DefaultPath;
            if (!File.Exists(path))
                return Empty();

            using var stream = File.OpenRead(path);
            var file = JsonSerializer.Deserialize<CacheFile>(stream);
            return file is null || file.Version != CurrentVersion ? Empty() : new CoverCache(file.Results);
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
            file = new CacheFile { Version = CurrentVersion, Results = new(_results) };

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + ".tmp";
            using (var stream = File.Create(temp))
                JsonSerializer.Serialize(stream, file);
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // Unsaved results only mean those files are read again next time.
        }
    }

    public bool TryGet(Track track, out CoverFinding finding)
    {
        lock (_lock)
            return _results.TryGetValue(QualityCache.KeyOf(track), out finding!);
    }

    public void Set(Track track, CoverFinding finding)
    {
        lock (_lock)
            _results[QualityCache.KeyOf(track)] = finding;
    }

    private sealed class CacheFile
    {
        public int Version { get; set; }
        public Dictionary<string, CoverFinding> Results { get; set; } = [];
    }
}

/// <summary>How far a cover check has got.</summary>
public readonly record struct CoverScanProgress(int Done, int Total, int Found);

/// <summary>How a cover check ended.</summary>
public sealed record CoverScanSummary(int Checked, int Found, int Unreadable, int Missing, bool Cancelled);

/// <summary>
/// Reads the pictures of every track not already in the <see cref="CoverCache"/>,
/// on a few below-normal-priority threads, as <see cref="QualityScanner"/> does.
/// Results are saved every 30 seconds and at the end, so a stopped check resumes.
/// A file that's gone (the drive unplugged) isn't recorded, so it is read once
/// it's back; one that can't be read is recorded as having no pictures.
/// </summary>
public static class CoverScanner
{
    private static readonly TimeSpan SaveEvery = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ReportEvery = TimeSpan.FromMilliseconds(250);

    public static Task<CoverScanSummary> RunAsync(
        IReadOnlyList<Track> tracks,
        CoverCache cache,
        IProgress<CoverScanProgress>? progress,
        CancellationToken cancel,
        Action? save = null,
        int? workers = null,
        Func<string, CoverFinding?>? survey = null,
        Func<string, bool>? exists = null)
    {
        survey ??= CoverCleaner.Survey;
        exists ??= File.Exists;

        return Task.Run(() =>
        {
            var todo = new System.Collections.Concurrent.ConcurrentQueue<Track>(tracks.Where(t => !cache.TryGet(t, out _)));
            var total = todo.Count;
            int done = 0, found = 0, unreadable = 0, missing = 0;

            void Work()
            {
                while (!cancel.IsCancellationRequested && todo.TryDequeue(out var track))
                {
                    if (!exists(track.FilePath))
                    {
                        Interlocked.Increment(ref missing);
                        Interlocked.Increment(ref done);
                        continue;
                    }

                    var finding = survey(track.FilePath);
                    if (finding is null)
                        Interlocked.Increment(ref unreadable);
                    else if (finding.HasExtras && CoverCleaner.CanClean(track.FilePath))
                        Interlocked.Increment(ref found);

                    cache.Set(track, finding ?? new CoverFinding(0, 0));
                    Interlocked.Increment(ref done);
                }
            }

            var threads = Enumerable.Range(0, Math.Max(1, workers ?? QualityScanner.DefaultWorkers))
                .Select(i => new Thread(Work)
                {
                    IsBackground = true,
                    Priority = ThreadPriority.BelowNormal,
                    Name = $"Cover check {i + 1}",
                })
                .ToList();
            threads.ForEach(t => t.Start());

            var lastSave = DateTime.UtcNow;
            while (threads.Any(t => t.IsAlive))
            {
                threads.FirstOrDefault(t => t.IsAlive)?.Join(ReportEvery);
                progress?.Report(new CoverScanProgress(Volatile.Read(ref done), total, Volatile.Read(ref found)));

                if (save is not null && DateTime.UtcNow - lastSave >= SaveEvery)
                {
                    save();
                    lastSave = DateTime.UtcNow;
                }
            }

            save?.Invoke();
            progress?.Report(new CoverScanProgress(done, total, found));
            return new CoverScanSummary(done - missing - unreadable, found, unreadable, missing, cancel.IsCancellationRequested);
        });
    }
}
