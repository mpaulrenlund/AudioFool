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
    public required int Total { get; init; }
    public required int Checked { get; init; }
    public required IReadOnlyList<QualityGap> Gaps { get; init; }

    public int Unchecked => Total - Checked;

    /// <summary>
    /// The rows in display order, each worst case before its "possibly", with
    /// the user's own names for them.
    /// </summary>
    public static IReadOnlyList<(QualityFlag Flag, string Label, string Note)> Rows { get; } =
    [
        (QualityFlag.LossySource, "Likely transcoded lossless", "Stops dead below 19 kHz, as an MP3 or AAC does"),
        (QualityFlag.PossiblyLossySource, "Possibly transcoded lossless", "Stops at 19–20.6 kHz: a high-bitrate MP3, or a master filtered there"),
        (QualityFlag.ReEncodedLossy, "Upscaled MP3", "Stops well short of what its bitrate keeps"),
        (QualityFlag.PossiblyReEncodedLossy, "Possibly upscaled MP3", "A little short; some older encoders cut there anyway"),
        (QualityFlag.Upsampled, "Fake hi-res", "Nothing a 44.1 or 48 kHz original couldn't hold"),
        (QualityFlag.PossiblyUpsampled, "Possibly fake hi-res", "Only faint noise above 25 kHz"),
        (QualityFlag.Padded24Bit, "Fake 24-bit", "The music uses only 16 of its 24 bits"),
    ];

    public static QualityStatistics Compute(IReadOnlyList<Track> tracks, QualityCache cache)
    {
        var results = cache.For(tracks);

        var gaps = Rows.Select(row =>
        {
            // By cache key, not by track: a rebuilt library has new Track objects
            // for the same files, and a retagged file gets a new key, so it drops
            // out of the filter until it is checked again.
            var keys = results.Where(r => r.Value.Flags.Contains(row.Flag))
                .Select(r => QualityCache.KeyOf(r.Key))
                .ToHashSet(StringComparer.Ordinal);

            return new QualityGap(row.Label, row.Flag, keys.Count,
                tracks.Count == 0 ? 0 : (double)keys.Count / tracks.Count, row.Note)
            {
                Filter = new TrackFilter(row.Label, t => keys.Contains(QualityCache.KeyOf(t))),
            };
        }).ToList();

        return new QualityStatistics { Total = tracks.Count, Checked = results.Count, Gaps = gaps };
    }
}
