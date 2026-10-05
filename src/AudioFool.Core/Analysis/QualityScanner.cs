using System.Collections.Concurrent;
using AudioFool.Core.Models;

namespace AudioFool.Core.Analysis;

/// <summary>How far a library check has got.</summary>
public readonly record struct QualityScanProgress(int Done, int Total, int Flagged);

/// <summary>How a library check ended.</summary>
public sealed record QualityScanSummary(int Checked, int AlreadyKnown, int Flagged, int Unreadable, int Missing, bool Cancelled);

/// <summary>
/// Checks every track not already in the <see cref="QualityCache"/>, a few
/// slices of each (<see cref="AnalysisSampling.Library"/>), on a handful of
/// below-normal-priority threads so playback and the UI keep their share.
/// <para>
/// Measured on the real library: 4 threads check about 42 tracks a second off
/// the USB SSD, about 11 minutes for 26,800 tracks; sampled and full reads gave
/// the same verdict on 30 of 30 test files. Results are saved every 30 seconds
/// and at the end, so a check stopped part-way (or the app closed) resumes
/// where it was. A file that's gone (the drive unplugged) is skipped and not
/// recorded, so it is checked once it's back.
/// </para>
/// </summary>
public static class QualityScanner
{
    /// <summary>A quarter of the cores, at most four: about 11 minutes for the library.</summary>
    public static int DefaultWorkers => Math.Clamp(Environment.ProcessorCount / 4, 1, 4);

    private static readonly TimeSpan SaveEvery = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ReportEvery = TimeSpan.FromMilliseconds(250);

    /// <param name="analyze">How one file is read; the tests pass a fake, the app the sampled <see cref="TrackAnalyzer"/>.</param>
    /// <param name="save">Called every 30 seconds and at the end; null saves nothing.</param>
    public static Task<QualityScanSummary> RunAsync(
        IReadOnlyList<Track> tracks,
        QualityCache cache,
        IProgress<QualityScanProgress>? progress,
        CancellationToken cancel,
        Action? save = null,
        int? workers = null,
        Func<string, SpectrumAnalysis>? analyze = null,
        Func<string, bool>? exists = null)
    {
        analyze ??= path => TrackAnalyzer.Analyze(path, sampling: AnalysisSampling.Library);
        exists ??= File.Exists;

        return Task.Run(() =>
        {
            var todo = tracks.Where(t => !cache.TryGet(t, out _)).ToList();
            var known = tracks.Count - todo.Count;
            var queue = new ConcurrentQueue<Track>(todo);
            int done = 0, flagged = 0, unreadable = 0, missing = 0;

            void Work()
            {
                while (!cancel.IsCancellationRequested && queue.TryDequeue(out var track))
                {
                    if (!exists(track.FilePath))
                    {
                        Interlocked.Increment(ref missing);
                        Interlocked.Increment(ref done);
                        continue;
                    }

                    QualityResult result;
                    try
                    {
                        var opinion = QualityOpinion.Form(analyze(track.FilePath), QualityClaim.From(track));
                        result = new QualityResult(opinion.Verdict, opinion.Flags);
                        if (result.Flags.Count > 0)
                            Interlocked.Increment(ref flagged);
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    {
                        // Recorded, so a file that can't be decoded isn't retried every time.
                        result = new QualityResult(Verdict.Inconclusive, []);
                        Interlocked.Increment(ref unreadable);
                    }

                    cache.Set(track, result);
                    Interlocked.Increment(ref done);
                }
            }

            var threads = Enumerable.Range(0, Math.Max(1, workers ?? DefaultWorkers))
                .Select(i => new Thread(Work)
                {
                    IsBackground = true,
                    Priority = ThreadPriority.BelowNormal,
                    Name = $"Quality check {i + 1}",
                })
                .ToList();
            threads.ForEach(t => t.Start());

            var lastSave = DateTime.UtcNow;
            while (threads.Any(t => t.IsAlive))
            {
                threads.First(t => t.IsAlive).Join(ReportEvery);
                progress?.Report(new QualityScanProgress(Volatile.Read(ref done), todo.Count, Volatile.Read(ref flagged)));

                if (save is not null && DateTime.UtcNow - lastSave >= SaveEvery)
                {
                    save();
                    lastSave = DateTime.UtcNow;
                }
            }

            save?.Invoke();
            progress?.Report(new QualityScanProgress(done, todo.Count, flagged));

            return new QualityScanSummary(done - missing - unreadable, known, flagged, unreadable, missing,
                cancel.IsCancellationRequested);
        });
    }
}
