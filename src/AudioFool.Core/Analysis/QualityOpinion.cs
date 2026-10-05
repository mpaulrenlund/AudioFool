using System.Globalization;
using AudioFool.Core.Library;
using AudioFool.Core.Models;

namespace AudioFool.Core.Analysis;

/// <summary>How well the measured audio matches what the file says it is.</summary>
public enum Verdict
{
    /// <summary>Nothing in the audio contradicts the file's format.</summary>
    Consistent,

    /// <summary>The audio is too quiet or too dull to judge.</summary>
    Inconclusive,

    /// <summary>A sign of a worse source, but one an honest file can also show.</summary>
    Suspect,

    /// <summary>The audio can't have come from what the format promises.</summary>
    Inconsistent,
}

/// <summary>What the file says about itself: the grid's Kind, Bit Depth, Sample Rate and Bitrate.</summary>
public sealed record QualityClaim(string Kind, int? BitDepth, int? SampleRate, int? Bitrate,
    bool IsLossy, bool IsDsd, bool IsModule)
{
    public static QualityClaim From(Track track) => new(
        track.Kind, track.BitDepth, track.SampleRate, track.Bitrate,
        AudioFormats.IsLossy(track.FilePath), AudioFormats.IsDsd(track.FilePath), AudioFormats.IsModule(track.FilePath));

    /// <summary>"FLAC · 24-bit · 96 kHz", "MP3 · 320 kbps · 44.1 kHz", "DSD128".</summary>
    public string Describe()
    {
        if (IsDsd)
            return SampleRate is > 0 and var dsd ? $"DSD{dsd / 44100}" : "DSD";

        var parts = new List<string> { Kind };
        if (IsLossy)
        {
            if (Bitrate is > 0)
                parts.Add($"{Bitrate} kbps");
        }
        else if (BitDepth is > 0)
        {
            parts.Add($"{BitDepth}-bit");
        }

        if (SampleRate is > 0 and var rate)
            parts.Add(QualityOpinion.Khz(rate));

        return string.Join(" · ", parts.Where(p => p.Length > 0));
    }
}

/// <summary>One thing the analysis found, in a sentence or two.</summary>
public sealed record Finding(Verdict Verdict, string Headline, string Explanation)
{
    /// <summary>Which problem this is, for counting across the library; null when there is none.</summary>
    public QualityFlag? Flag { get; init; }
}

/// <summary>The problems a library-wide check counts, one Statistics row each.</summary>
public enum QualityFlag
{
    /// <summary>A lossless file cut off below 19 kHz: made from an MP3 or AAC.</summary>
    LossySource,

    /// <summary>A lossless file cut off at 19-20.6 kHz, where a high-bitrate encoder cuts.</summary>
    PossiblyLossySource,

    /// <summary>A lossy file cut off below what its bitrate keeps: re-encoded from a lower one.</summary>
    ReEncodedLossy,

    /// <summary>A lossy file cut off a little below what its bitrate keeps; some encoders do that anyway.</summary>
    PossiblyReEncodedLossy,

    /// <summary>Hi-res or DSD with nothing a CD-quality original couldn't have.</summary>
    Upsampled,

    /// <summary>Hi-res or DSD with only faint noise above 25 kHz.</summary>
    PossiblyUpsampled,

    /// <summary>A 24-bit file whose music uses only 16 bits.</summary>
    Padded24Bit,
}

/// <summary>AudioFool's opinion: the worst finding's verdict and headline, then every finding.</summary>
public sealed record Opinion(Verdict Verdict, string Headline, IReadOnlyList<Finding> Findings, double? CutoffHz)
{
    /// <summary>Every problem found, worst first.</summary>
    public IReadOnlyList<QualityFlag> Flags => [.. Findings.Where(f => f.Flag is not null).Select(f => f.Flag!.Value)];
}

/// <summary>
/// Compares what a <see cref="SpectrumAnalysis"/> measured with what the file
/// claims. Pure, so it is tested on synthetic spectra.
/// <para>
/// The rules were set against real files from the library and fakes made from
/// them (an MP3 decoded to a WAV, a CD track upsampled to 96 kHz, 16-bit audio
/// padded to 24):
/// </para>
/// <list type="bullet">
/// <item>A lossy encoder cuts everything above its lowpass, which shows as a
/// cliff: 20 dB or more lost within half a kilohertz, never recovering. MP3s put
/// it at 16-20.5 kHz depending on bitrate. Honest CD masters can have one too,
/// from the converter's filter, but at 21 kHz or above.</item>
/// <item>Upsampled hi-res has nothing above the original's limit (22.05 or
/// 24 kHz), or a mirror image of the treble just above it where the
/// interpolation filter let it through.</item>
/// <item>Padded 24-bit audio has eight zero bits under every sample.</item>
/// </list>
/// </summary>
public static class QualityOpinion
{
    /// <summary>The grid the spectrum is read on, in Hz.</summary>
    private const double Step = 125;

    /// <summary>Each grid point averages this band, in Hz.</summary>
    private const double Band = 250;

    /// <summary>A cliff loses at least this much within <see cref="CliffWidth"/>.</summary>
    private const double CliffDrop = 20;
    private const double CliffWidth = 500;

    /// <summary>After a cliff, nothing comes back within this much of the level before it.</summary>
    private const double StaysDown = 15;

    /// <summary>A cliff at or above this is a converter's filter, not a lossy encoder's.</summary>
    public const double LossyCeilingHz = 20600;

    /// <summary>Below this, a lossy cliff is unmistakable; between it and the ceiling, it's a high bitrate.</summary>
    private const double ClearlyLossyHz = 19000;

    /// <summary>
    /// No encoder cuts lower than this, even at 64 kbps. A cliff below it is the
    /// music's own edge: an N64 soundtrack's low-rate samples, a lo-fi piano
    /// (both seen in the library at 2-7 kHz).
    /// </summary>
    public const double LowestEncoderCutoffHz = 11000;

    /// <summary>
    /// An MP3 this far short of its bitrate's usual cutoff was re-encoded. Less
    /// than this is only possibly so: older iTunes and Xing encoders cut at
    /// 16 kHz whatever the bitrate.
    /// </summary>
    private const double ClearlyReEncodedMargin = 2000;

    /// <summary>The treble the other bands are compared with.</summary>
    private const double TrebleFrom = 14000, TrebleTo = 19000;

    /// <summary>Treble quieter than this is too faint to judge a cutoff by.</summary>
    private const double NoTrebleDb = -110;

    /// <summary>The band only a real hi-res source fills.</summary>
    private const double UltrasonicFrom = 25000, UltrasonicTo = 32000;

    /// <summary>A decoder's output for "nothing here" is far below any recording's noise.</summary>
    private const double SilenceDb = -125;

    public static Opinion Form(SpectrumAnalysis analysis, QualityClaim claim)
    {
        var findings = new List<Finding>();
        var nyquist = analysis.SampleRate / 2.0;
        var cliff = FindCliff(analysis);
        var cutoff = cliff;

        if (claim.IsModule)
        {
            findings.Add(new Finding(Verdict.Inconclusive, "Nothing to check",
                "A tracker module isn't a recording: AudioFool renders it from its instruments as it plays, so it has no source quality to compare."));
            return Conclude(findings, cutoff, claim);
        }

        if (analysis.PeakSampleDb < -60 || analysis.Duration < TimeSpan.FromSeconds(3))
        {
            findings.Add(new Finding(Verdict.Inconclusive, "Can't tell",
                "The track is too short or too quiet to judge its source from the spectrum."));
            return Conclude(findings, cutoff, claim);
        }

        // A 22 or 32 kHz file has no 14-19 kHz; its own top quarter stands in.
        var trebleTo = Math.Min(TrebleTo, nyquist - 1000);
        var trebleFrom = Math.Min(TrebleFrom, trebleTo * 0.75);
        var treble = Level(analysis, trebleFrom, trebleTo);
        var hasTreble = treble > NoTrebleDb;
        var isHiRes = claim.IsDsd || analysis.SampleRate > 48000;

        if (cliff is { } edge && edge < LowestEncoderCutoffHz)
        {
            findings.Add(new Finding(Verdict.Inconclusive, "Can't tell",
                $"The audio stops at {Khz(edge)}, far below where any encoder cuts: the recording itself has nothing higher (low-rate samples, lo-fi, or filtered on purpose), so its source can't be judged from the spectrum."));
        }
        else if (claim.IsLossy)
            findings.Add(LossyFinding(claim, nyquist, cliff, hasTreble ? null : trebleFrom));
        else
        {
            findings.Add(CutoffFinding(claim, cliff, hasTreble ? null : trebleFrom));

            if (isHiRes && hasTreble && (cliff is null || cliff >= LossyCeilingHz))
                findings.Add(HiResFinding(analysis, claim, cliff, treble));
        }

        if (!claim.IsLossy && !claim.IsDsd && claim.BitDepth is > 16 && analysis.UsedBits is { } used)
            findings.Add(BitsFinding(claim.BitDepth.Value, used, analysis.ExtraBitsShare));

        return Conclude(findings, cutoff, claim);
    }

    /// <summary>
    /// The worst finding sets the verdict and headline. When everything checks
    /// out, the headline names the claim instead of the first check.
    /// </summary>
    private static Opinion Conclude(List<Finding> findings, double? cutoff, QualityClaim claim)
    {
        var worst = findings.MaxBy(f => f.Verdict)!;
        var headline = worst.Verdict == Verdict.Consistent ? $"Consistent with {claim.Describe()}" : worst.Headline;
        // The reasons for the verdict first; LINQ's sort is stable, so ties keep their order.
        return new Opinion(worst.Verdict, headline, findings.OrderByDescending(f => f.Verdict).ToList(), cutoff);
    }

    private static Finding LossyFinding(QualityClaim claim, double nyquist, double? cliff, double? noTrebleAbove)
    {
        // A 32 kHz MP3 can't keep 19.5 kHz whatever its bitrate.
        var expected = ExpectedLossyCutoff(claim.Bitrate) is { } e ? Math.Min(e, nyquist - 1000) : (double?)null;
        var rate = claim.Bitrate is > 0 ? $"{claim.Bitrate} kbps" : "its bitrate";

        if (cliff is { } hz && expected is { } min && hz < min - ClearlyReEncodedMargin)
        {
            return new Finding(Verdict.Inconsistent, "Re-encoded from a lower bitrate",
                $"The audio stops at {Khz(hz)}, which is typical of {BitrateFor(hz)}. A real {rate} file keeps about {Khz(min)} or more, so this was probably made from a lower-quality file.")
            { Flag = QualityFlag.ReEncodedLossy };
        }

        if (cliff is { } shortOf && expected is { } usual && shortOf < usual - 300)
        {
            return new Finding(Verdict.Suspect, "Possibly re-encoded from a lower bitrate",
                $"The audio stops at {Khz(shortOf)}, short of the {Khz(usual)} or so a {rate} file usually keeps. It may have been made from {BitrateFor(shortOf)}, though some older encoders (iTunes, Xing) cut at 16 kHz whatever the bitrate.")
            { Flag = QualityFlag.PossiblyReEncodedLossy };
        }

        if (cliff is { } ok)
        {
            return new Finding(Verdict.Consistent, $"Consistent with {claim.Describe()}",
                $"The audio stops at {Khz(ok)}, which is normal for {rate}. Nothing suggests it was made from a lower-quality file.");
        }

        if (noTrebleAbove is { } faint)
        {
            return new Finding(Verdict.Inconclusive, "Can't tell",
                $"There is almost nothing above {Khz(faint)}, so the encoder's cutoff can't be seen.");
        }

        return new Finding(Verdict.Consistent, $"Consistent with {claim.Describe()}",
            "The encoder kept the full frequency range with no cutoff, which some encoders do at high bitrates. Nothing suggests a lower-quality source.");
    }

    private static Finding CutoffFinding(QualityClaim claim, double? cliff, double? noTrebleAbove)
    {
        if (cliff is { } hz && hz < ClearlyLossyHz)
        {
            return new Finding(Verdict.Inconsistent, "Made from a lossy file",
                $"The audio stops dead at {Khz(hz)}, the way an MP3 or AAC encoder cuts it, typical of {BitrateFor(hz)}. Lossless audio from a real master doesn't stop like that, so this was probably converted from a lossy file.")
            { Flag = QualityFlag.LossySource };
        }

        if (cliff is { } high && high < LossyCeilingHz)
        {
            return new Finding(Verdict.Suspect, "Possibly from a lossy file",
                $"The audio stops sharply at {Khz(high)}, where a high-bitrate encoder ({BitrateFor(high)}) cuts it. A few masters are filtered there too, so this isn't certain.")
            { Flag = QualityFlag.PossiblyLossySource };
        }

        if (noTrebleAbove is { } faint)
        {
            return new Finding(Verdict.Inconclusive, "Can't tell",
                $"There is almost nothing above {Khz(faint)}, so a lossy encoder's cutoff wouldn't show. The recording may simply be dull, or very old.");
        }

        var edge = cliff is { } wall
            ? $"No sign of a lossy encoder: the audio runs to {Khz(wall)}, where the converter's own filter ends it."
            : "No sign of a lossy encoder: the audio fades out gradually at the top rather than stopping dead.";

        return new Finding(Verdict.Consistent, "No sign of a lossy source", edge);
    }

    private static Finding HiResFinding(SpectrumAnalysis analysis, QualityClaim claim, double? cliff, double treble)
    {
        var what = claim.IsDsd ? "DSD" : "hi-res";

        if (cliff is { } wall && wall < 24500)
        {
            return new Finding(Verdict.Inconsistent, $"Not true {what}",
                $"Nothing comes above {Khz(wall)}, which is where a 44.1 or 48 kHz original ends. It was probably upsampled, and holds nothing a CD-quality file couldn't.")
            { Flag = QualityFlag.Upsampled };
        }

        foreach (var (boundary, source) in new[] { (22050.0, "44.1 kHz"), (24000.0, "48 kHz") })
        {
            if (HasImageNotch(analysis, boundary))
            {
                return new Finding(Verdict.Inconsistent, $"Not true {what}",
                    $"The spectrum has a gap at {Khz(boundary)} with a mirror image of the treble above it. That is the mark of {source} audio upsampled to a higher rate.")
            { Flag = QualityFlag.Upsampled };
            }
        }

        // Hi-res rates are 88.2 kHz and up, but stay inside an odd one's range.
        var ultrasonicTo = Math.Min(UltrasonicTo, (analysis.SampleRate / 2.0) - 1000);
        if (ultrasonicTo < UltrasonicFrom + 2000)
        {
            return new Finding(Verdict.Inconclusive, "Can't tell",
                $"At {Khz(analysis.SampleRate)} there is too little room above CD's range to check for hi-res content.");
        }

        var ultrasonic = Level(analysis, UltrasonicFrom, ultrasonicTo);
        var below = treble - ultrasonic;

        if (ultrasonic <= SilenceDb && below >= 30)
        {
            return new Finding(Verdict.Inconsistent, $"Not true {what}",
                $"There is nothing at all above about 24 kHz, where a 44.1 or 48 kHz original ends. It was probably upsampled, and holds nothing a CD-quality file couldn't.")
            { Flag = QualityFlag.Upsampled };
        }

        if (below >= 35)
        {
            return new Finding(Verdict.Suspect, $"Possibly not true {what}",
                $"Above 25 kHz there is only faint noise, {below:0} dB below the treble. It may have been upsampled from CD quality, or the recording may simply have little up there.")
            { Flag = QualityFlag.PossiblyUpsampled };
        }

        return new Finding(Verdict.Consistent, $"Consistent with {claim.Describe()}",
            $"There is real content above 25 kHz ({below:0} dB below the treble), which a CD-quality original couldn't have.");
    }

    /// <summary>
    /// Real 24-bit audio uses the extra bits in nearly every sample (99% in the
    /// library's). Under half means the music is 16-bit, whatever a fade or an
    /// edit added later.
    /// </summary>
    public const double ExtraBitsMinimum = 0.5;

    private static Finding BitsFinding(int stated, int used, double? extraShare)
    {
        if (used <= 16)
        {
            return new Finding(Verdict.Inconsistent, $"Not true {stated}-bit",
                $"Every sample is a 16-bit value with {stated - 16} zero bits added underneath. This is CD-resolution audio in a {stated}-bit file.")
            { Flag = QualityFlag.Padded24Bit };
        }

        if (extraShare is { } share && share < ExtraBitsMinimum)
        {
            return new Finding(Verdict.Inconsistent, $"Not true {stated}-bit",
                $"The music is 16-bit with zero bits added underneath: only {share:0.#%} of samples use more, likely fades or edits made after. Real {stated}-bit audio uses them in nearly every sample.")
            { Flag = QualityFlag.Padded24Bit };
        }

        return new Finding(Verdict.Consistent, $"{stated}-bit is real",
            used == stated
                ? $"The samples use all {stated} bits."
                : $"The samples use {used} of the {stated} bits, finer than CD's 16.");
    }

    /// <summary>
    /// The lowest point where the spectrum falls off a cliff and stays down, as
    /// the frequency halfway down it; null when there is none.
    /// <para>
    /// The level before a candidate is the median over the kilohertz below it,
    /// so a single loud tone (chiptune, FM synthesis) can't fake one.
    /// </para>
    /// </summary>
    public static double? FindCliff(SpectrumAnalysis analysis)
    {
        var top = (analysis.SampleRate / 2.0) - 150;
        var levels = new List<(double Hz, double Db)>();
        for (var hz = 1000.0; hz <= top; hz += Step)
            levels.Add((hz, Level(analysis, hz - (Band / 2), hz + (Band / 2))));

        var ahead = (int)(CliffWidth / Step);
        var behind = (int)(1000 / Step);

        for (var i = behind; i + ahead < levels.Count; i++)
        {
            var before = Median(levels, i - behind, i);
            var here = levels[i].Db;
            var after = levels[i + ahead].Db;

            if (here < before - 6 || before - after < CliffDrop)
                continue;

            var rest = levels.Skip(i + ahead).Max(l => l.Db);
            if (rest > before - StaysDown)
                continue;

            // Halfway between the level before and the floor after.
            var floor = Median(levels, i + ahead, Math.Min(levels.Count - 1, i + ahead + behind));
            var target = (before + floor) / 2;
            for (var j = i; j < levels.Count - 1; j++)
            {
                if (levels[j + 1].Db <= target)
                    return levels[j].Hz + (Step / 2);
            }

            return levels[i].Hz;
        }

        return null;
    }

    /// <summary>
    /// A gap just around <paramref name="boundary"/> with louder content both
    /// below and above it: the treble of a lower-rate source, mirrored about its
    /// Nyquist frequency by upsampling.
    /// </summary>
    public static bool HasImageNotch(SpectrumAnalysis analysis, double boundary)
    {
        if (analysis.SampleRate / 2.0 < boundary + 3000)
            return false;

        double notch = double.MaxValue;
        for (var hz = boundary - 500; hz <= boundary + 500; hz += Step)
            notch = Math.Min(notch, Level(analysis, hz - (Band / 2), hz + (Band / 2)));

        var below = Level(analysis, boundary - 2500, boundary - 1500);
        var above = Level(analysis, boundary + 1500, boundary + 2500);

        return below - notch >= 15 && above - notch >= 10;
    }

    /// <summary>The mean power between two frequencies, in dB.</summary>
    public static double Level(SpectrumAnalysis analysis, double fromHz, double toHz)
    {
        var lo = analysis.BinOf(fromHz);
        var hi = Math.Max(lo + 1, analysis.BinOf(toHz));
        double sum = 0;
        for (var k = lo; k < hi; k++)
            sum += Math.Pow(10, analysis.Average[k] / 10);
        var mean = sum / (hi - lo);
        return mean <= 0 ? SpectrumAccumulator.FloorDb : Math.Max(SpectrumAccumulator.FloorDb, 10 * Math.Log10(mean));
    }

    private static double Median(List<(double Hz, double Db)> levels, int from, int to)
    {
        var values = new List<double>();
        for (var i = from; i <= to; i++)
            values.Add(levels[i].Db);
        values.Sort();
        return values[values.Count / 2];
    }

    /// <summary>
    /// The lowest cutoff an honest file at this bitrate has, from LAME's
    /// defaults and what the library's own MP3s show, with some slack.
    /// </summary>
    public static double? ExpectedLossyCutoff(int? kbps) => kbps switch
    {
        >= 300 => 19500,
        >= 250 => 19000,
        >= 200 => 18000,
        >= 170 => 17000,
        >= 140 => 16000,
        >= 110 => 15000,
        _ => null,
    };

    /// <summary>The bitrate a lossy encoder cutting at this frequency typically runs at.</summary>
    public static string BitrateFor(double hz) => hz switch
    {
        < 15500 => "128 kbps or lower",
        < 16500 => "about 128 kbps",
        < 17500 => "about 128–160 kbps",
        < 18600 => "about 160–192 kbps",
        < 19600 => "about 192–256 kbps",
        _ => "about 256–320 kbps",
    };

    /// <summary>
    /// "96 kHz", "44.1 kHz", "22.05 kHz" for a sample rate or boundary; a
    /// measured frequency to one decimal, "20.2 kHz".
    /// </summary>
    public static string Khz(double hz)
    {
        var format = hz % 1000 == 0 ? "0" : hz % 50 == 0 ? "0.0#" : "0.0";
        return (hz / 1000).ToString(format, CultureInfo.InvariantCulture) + " kHz";
    }
}
