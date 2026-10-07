using AudioFool.Core.Library;
using AudioFool.Core.Models;

namespace AudioFool.Core.Analysis;

/// <summary>One Statistics row of the library check: a problem and the tracks that have it.</summary>
public sealed record QualityGap(string Label, QualityFlag Flag, int Tracks, double Share, string Note)
{
    /// <summary>Narrows the library to these tracks.</summary>
    public required TrackFilter Filter { get; init; }
}

/// <summary>
/// The library check's results, counted for the Statistics window: how many of
/// the tracks have been checked, and one row per problem.
/// </summary>
public sealed class QualityStatistics
{
    public const string ClearedLabel = "Cleared by you";

    public required int Total { get; init; }
    public required int Checked { get; init; }
    public required IReadOnlyList<QualityGap> Gaps { get; init; }

    /// <summary>Songs the user has cleared from at least one row.</summary>
    public int Cleared { get; init; }

    /// <summary>Narrows the library to the cleared songs, so they can be put back.</summary>
    public TrackFilter? ClearedFilter { get; init; }

    public int Unchecked => Total - Checked;

    /// <summary>
    /// The rows in display order, from the highest claimed quality to the lowest
    /// (24-bit, hi-res, lossless, MP3; the user's order), each worst case before
    /// its "possibly", with the user's own names for them.
    /// </summary>
    public static IReadOnlyList<(QualityFlag Flag, string Label, string Note)> Rows { get; } =
    [
        (QualityFlag.Padded24Bit, "Fake 24-bit", "The music uses only 16 of its 24 bits"),
        (QualityFlag.Upsampled, "Fake hi-res", "Nothing a 44.1 or 48 kHz original couldn't hold"),
        (QualityFlag.PossiblyUpsampled, "Possibly fake hi-res", "Only faint noise above 25 kHz"),
        (QualityFlag.LossySource, "Likely transcoded lossless", "Stops dead below 19 kHz, as an MP3 or AAC does"),
        (QualityFlag.PossiblyLossySource, "Possibly transcoded lossless", "Stops at 19–20.6 kHz: a high-bitrate MP3, or a master filtered there"),
        (QualityFlag.ReEncodedLossy, "Upscaled MP3", "Stops well short of what its bitrate keeps"),
        (QualityFlag.PossiblyReEncodedLossy, "Possibly upscaled MP3", "A little short; some older encoders cut there anyway"),
    ];

    public static QualityStatistics Compute(IReadOnlyList<Track> tracks, QualityCache cache) =>
        Compute(tracks, cache, null);

    /// <summary>
    /// The rows leave out what the user has cleared. Their filters ask
    /// <paramref name="clearances"/> again each time they're applied, so a song
    /// cleared while a row's filter is on leaves the library view at once.
    /// </summary>
    public static QualityStatistics Compute(IReadOnlyList<Track> tracks, QualityCache cache, QualityClearances? clearances)
    {
        var results = cache.For(tracks);

        var gaps = Rows.Select(row =>
        {
            // By cache key, not by track: a rebuilt library has new Track objects
            // for the same files, and a retagged file gets a new key, so it drops
            // out of the filter until it is checked again.
            var keys = results.Where(r => r.Value.Flags.Contains(row.Flag)
                                          && clearances?.IsCleared(r.Key, row.Flag) != true)
                .Select(r => QualityCache.KeyOf(r.Key))
                .ToHashSet(StringComparer.Ordinal);

            return new QualityGap(row.Label, row.Flag, keys.Count,
                tracks.Count == 0 ? 0 : (double)keys.Count / tracks.Count, row.Note)
            {
                Filter = new TrackFilter(row.Label,
                    t => keys.Contains(QualityCache.KeyOf(t)) && clearances?.IsCleared(t, row.Flag) != true)
                {
                    QualityFlag = row.Flag,
                },
            };
        }).ToList();

        var cleared = clearances is null ? 0 : tracks.Count(clearances.IsClearedFromAny);

        return new QualityStatistics
        {
            Total = tracks.Count,
            Checked = results.Count,
            Gaps = gaps,
            Cleared = cleared,
            ClearedFilter = clearances is null
                ? null
                : new TrackFilter(ClearedLabel, clearances.IsClearedFromAny) { ShowsQualityClearances = true },
        };
    }
}
