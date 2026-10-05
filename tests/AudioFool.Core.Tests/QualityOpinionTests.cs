using AudioFool.Core.Analysis;

namespace AudioFool.Core.Tests;

/// <summary>
/// The opinion's rules, on synthetic spectra shaped like the real files they
/// were set against (see <see cref="QualityOpinion"/>).
/// </summary>
public class QualityOpinionTests
{
    private static readonly QualityClaim CdFlac = new("FLAC", 16, 44100, 900, false, false, false);
    private static readonly QualityClaim HiResFlac = new("FLAC", 24, 96000, 2800, false, false, false);
    private static readonly QualityClaim Mp3At320 = new("MP3", null, 44100, 320, true, false, false);

    /// <summary>Music falling 2 dB per kHz from -40 dB at 1 kHz.</summary>
    private static double Music(double hz) => -40 - (2 * (hz - 1000) / 1000);

    /// <summary>A lossy encoder's lowpass: the music, then decoder silence.</summary>
    private static Func<double, double> CutAt(double cutoff) => hz => hz < cutoff ? Music(hz) : -140;

    [Fact]
    public void A_gradual_rolloff_is_consistent_with_lossless()
    {
        var opinion = QualityOpinion.Form(Spectrum(44100, Music), CdFlac);

        Assert.Equal(Verdict.Consistent, opinion.Verdict);
        Assert.Equal("Consistent with FLAC · 16-bit · 44.1 kHz", opinion.Headline);
        Assert.Null(opinion.CutoffHz);
    }

    [Fact]
    public void A_cliff_at_16_kHz_in_a_flac_is_a_lossy_source()
    {
        var opinion = QualityOpinion.Form(Spectrum(44100, CutAt(16000)), CdFlac);

        Assert.Equal(Verdict.Inconsistent, opinion.Verdict);
        Assert.Equal("Made from a lossy file", opinion.Headline);
        Assert.InRange(opinion.CutoffHz!.Value, 15800, 16200);
        Assert.Contains("about 128 kbps", opinion.Findings[0].Explanation);
    }

    [Fact]
    public void A_cliff_at_20_kHz_is_only_suspect_since_some_masters_have_one()
    {
        var opinion = QualityOpinion.Form(Spectrum(44100, CutAt(20200)), CdFlac);

        Assert.Equal(Verdict.Suspect, opinion.Verdict);
        Assert.Equal("Possibly from a lossy file", opinion.Headline);
    }

    [Fact]
    public void A_converter_filter_at_21_kHz_is_consistent()
    {
        var opinion = QualityOpinion.Form(Spectrum(44100, CutAt(21200)), CdFlac);

        Assert.Equal(Verdict.Consistent, opinion.Verdict);
        Assert.InRange(opinion.CutoffHz!.Value, 21000, 21400);
        Assert.Contains("converter", opinion.Findings[0].Explanation);
    }

    [Fact]
    public void A_single_loud_tone_does_not_make_a_cliff()
    {
        // FM synthesis: quiet treble with one loud partial in it.
        double Tonal(double hz) => Math.Abs(hz - 16750) < 100 ? -58 : -82;
        var opinion = QualityOpinion.Form(Spectrum(44100, Tonal), CdFlac);

        Assert.Null(opinion.CutoffHz);
        Assert.Equal(Verdict.Consistent, opinion.Verdict);
    }

    [Fact]
    public void A_cliff_that_recovers_is_not_a_cutoff()
    {
        double Dip(double hz) => hz is > 17000 and < 17600 ? -140 : Music(hz);
        Assert.Null(QualityOpinion.FindCliff(Spectrum(44100, Dip)));
    }

    [Fact]
    public void Treble_too_faint_to_judge_is_inconclusive()
    {
        // A dull old recording: fading 6 dB per kHz into the noise, no cliff anywhere.
        var opinion = QualityOpinion.Form(Spectrum(44100, hz => Math.Max(-125, -40 - (6 * (hz - 1000) / 1000))), CdFlac);

        Assert.Equal(Verdict.Inconclusive, opinion.Verdict);
        Assert.Equal("Can't tell", opinion.Headline);
    }

    [Fact]
    public void A_320_kbps_mp3_cut_at_16_kHz_was_re_encoded()
    {
        var opinion = QualityOpinion.Form(Spectrum(44100, CutAt(16000)), Mp3At320);

        Assert.Equal(Verdict.Inconsistent, opinion.Verdict);
        Assert.Equal("Re-encoded from a lower bitrate", opinion.Headline);
    }

    [Fact]
    public void A_320_kbps_mp3_cut_at_20_kHz_is_consistent()
    {
        var opinion = QualityOpinion.Form(Spectrum(44100, CutAt(20300)), Mp3At320);

        Assert.Equal(Verdict.Consistent, opinion.Verdict);
        Assert.Equal("Consistent with MP3 · 320 kbps · 44.1 kHz", opinion.Headline);
    }

    [Fact]
    public void A_low_rate_mp3_is_not_expected_to_reach_past_its_own_range()
    {
        var claim = new QualityClaim("MP3", null, 32000, 320, true, false, false);
        var opinion = QualityOpinion.Form(Spectrum(32000, CutAt(15100)), claim);

        Assert.Equal(Verdict.Consistent, opinion.Verdict);
    }

    [Fact]
    public void Hi_res_with_real_ultrasonic_content_is_consistent()
    {
        var opinion = QualityOpinion.Form(Spectrum(96000, Music, usedBits: 24), HiResFlac);

        Assert.Equal(Verdict.Consistent, opinion.Verdict);
        Assert.Equal("Consistent with FLAC · 24-bit · 96 kHz", opinion.Headline);
        Assert.Contains(opinion.Findings, f => f.Explanation.Contains("above 25 kHz"));
    }

    [Fact]
    public void Hi_res_with_nothing_above_cd_range_was_upsampled()
    {
        // A resampler's gentle filter from 20 kHz into silence by 24 kHz.
        double Upsampled(double hz) => hz < 20000 ? Music(hz) : Math.Max(-143, Music(20000) - ((hz - 20000) / 4000 * 64));
        var opinion = QualityOpinion.Form(Spectrum(96000, Upsampled, usedBits: 24), HiResFlac);

        Assert.Equal(Verdict.Inconsistent, opinion.Verdict);
        Assert.Equal("Not true hi-res", opinion.Headline);
    }

    [Fact]
    public void Hi_res_with_a_mirror_image_above_22_kHz_was_upsampled()
    {
        // Treble mirrored about 22.05 kHz, as a weak interpolation filter leaves it.
        double Imaged(double hz) => hz switch
        {
            < 21000 => Music(hz),
            < 23200 => -102,
            < 30000 => Music(44100 - hz) - 6,
            _ => -115,
        };
        var opinion = QualityOpinion.Form(Spectrum(96000, Imaged, usedBits: 24), HiResFlac);

        Assert.Equal(Verdict.Inconsistent, opinion.Verdict);
        Assert.Contains(opinion.Findings, f => f.Explanation.Contains("mirror image"));
    }

    [Fact]
    public void Hi_res_with_only_faint_noise_up_high_is_suspect()
    {
        // Fading 8 dB per kHz past 22 kHz into a noise floor: no cliff, little content.
        double Faint(double hz) => hz < 22000 ? Music(hz) : Math.Max(-118, Music(22000) - (8 * (hz - 22000) / 1000));
        var opinion = QualityOpinion.Form(Spectrum(96000, Faint, usedBits: 24), HiResFlac);

        Assert.Equal(Verdict.Suspect, opinion.Verdict);
        Assert.Equal("Possibly not true hi-res", opinion.Headline);
    }

    [Fact]
    public void Hi_res_made_from_a_lossy_file_says_so()
    {
        var opinion = QualityOpinion.Form(Spectrum(96000, CutAt(18800), usedBits: 24), HiResFlac);

        Assert.Equal(Verdict.Inconsistent, opinion.Verdict);
        Assert.Equal("Made from a lossy file", opinion.Headline);
    }

    [Fact]
    public void Padded_24_bit_is_not_true_24_bit()
    {
        var claim = new QualityClaim("FLAC", 24, 44100, 1500, false, false, false);
        var opinion = QualityOpinion.Form(Spectrum(44100, Music, usedBits: 16), claim);

        Assert.Equal(Verdict.Inconsistent, opinion.Verdict);
        Assert.Equal("Not true 24-bit", opinion.Headline);
        Assert.Contains("8 zero bits", opinion.Findings[0].Explanation); // the reason comes first
    }

    [Fact]
    public void Twenty_bits_of_twenty_four_are_fine()
    {
        var claim = new QualityClaim("FLAC", 24, 44100, 1500, false, false, false);
        var opinion = QualityOpinion.Form(Spectrum(44100, Music, usedBits: 20), claim);

        Assert.Equal(Verdict.Consistent, opinion.Verdict);
        Assert.Contains("20 of the 24 bits", opinion.Findings.Last().Explanation);
    }

    [Fact]
    public void Sixteen_bit_music_with_24_bit_fades_is_not_true_24_bit()
    {
        // Measured on a real album: 24 bits in the fades, 7% of samples in all.
        var claim = new QualityClaim("FLAC", 24, 48000, 1200, false, false, false);
        var opinion = QualityOpinion.Form(Spectrum(48000, Music, usedBits: 24, extraBitsShare: 0.07), claim);

        Assert.Equal(Verdict.Inconsistent, opinion.Verdict);
        Assert.Equal("Not true 24-bit", opinion.Headline);
        Assert.Contains("7%", opinion.Findings[0].Explanation);
        Assert.Equal([QualityFlag.Padded24Bit], opinion.Flags);
    }

    [Fact]
    public void A_cliff_far_below_any_encoder_is_the_music_not_a_lossy_source()
    {
        // An N64 soundtrack's low-rate samples: nothing above 6.4 kHz.
        var opinion = QualityOpinion.Form(Spectrum(44100, CutAt(6400)), CdFlac);

        Assert.Equal(Verdict.Inconclusive, opinion.Verdict);
        Assert.Empty(opinion.Flags);
        Assert.Contains("far below", opinion.Findings[0].Explanation);
    }

    [Fact]
    public void A_192_kbps_mp3_cut_at_16_kHz_is_only_possibly_re_encoded()
    {
        var claim = new QualityClaim("MP3", null, 44100, 192, true, false, false);
        var opinion = QualityOpinion.Form(Spectrum(44100, CutAt(16100)), claim);

        Assert.Equal(Verdict.Suspect, opinion.Verdict);
        Assert.Equal("Possibly re-encoded from a lower bitrate", opinion.Headline);
        Assert.Equal([QualityFlag.PossiblyReEncodedLossy], opinion.Flags);
    }

    [Fact]
    public void A_320_kbps_mp3_cut_at_18_kHz_is_only_possibly_re_encoded()
    {
        var opinion = QualityOpinion.Form(Spectrum(44100, CutAt(18300)), Mp3At320);
        Assert.Equal([QualityFlag.PossiblyReEncodedLossy], opinion.Flags);
    }

    [Theory]
    [InlineData(16000, QualityFlag.LossySource)]
    [InlineData(20200, QualityFlag.PossiblyLossySource)]
    public void Lossless_cutoffs_carry_their_flag(double cutoff, QualityFlag flag) =>
        Assert.Equal([flag], QualityOpinion.Form(Spectrum(44100, CutAt(cutoff)), CdFlac).Flags);

    [Fact]
    public void Upsampled_hi_res_carries_its_flag_and_a_clean_file_none()
    {
        double Faint(double hz) => hz < 22000 ? Music(hz) : Math.Max(-118, Music(22000) - (8 * (hz - 22000) / 1000));
        Assert.Equal([QualityFlag.PossiblyUpsampled], QualityOpinion.Form(Spectrum(96000, Faint, usedBits: 24), HiResFlac).Flags);
        Assert.Empty(QualityOpinion.Form(Spectrum(96000, Music, usedBits: 24), HiResFlac).Flags);
    }

    [Fact]
    public void The_re_encoded_flag_is_set()
    {
        Assert.Equal([QualityFlag.ReEncodedLossy], QualityOpinion.Form(Spectrum(44100, CutAt(16000)), Mp3At320).Flags);
    }

    [Fact]
    public void A_quiet_track_is_inconclusive()
    {
        var opinion = QualityOpinion.Form(Spectrum(44100, CutAt(16000), peakDb: -70), CdFlac);
        Assert.Equal(Verdict.Inconclusive, opinion.Verdict);
    }

    [Fact]
    public void A_tracker_module_has_nothing_to_check()
    {
        var claim = new QualityClaim("XM", null, 44100, null, false, false, true);
        var opinion = QualityOpinion.Form(Spectrum(44100, Music), claim);

        Assert.Equal(Verdict.Inconclusive, opinion.Verdict);
        Assert.Equal("Nothing to check", opinion.Headline);
    }

    [Theory]
    [InlineData("FLAC", 16, 44100, 900, false, false, "FLAC · 16-bit · 44.1 kHz")]
    [InlineData("FLAC", 24, 96000, 2800, false, false, "FLAC · 24-bit · 96 kHz")]
    [InlineData("MP3", null, 48000, 128, true, false, "MP3 · 128 kbps · 48 kHz")]
    [InlineData("DSD", 1, 5644800, 11356, false, true, "DSD128")]
    [InlineData("DSD", 1, 2822400, 5645, false, true, "DSD64")]
    public void The_claim_reads_like_the_grid(string kind, int? bits, int rate, int kbps, bool lossy, bool dsd, string expected) =>
        Assert.Equal(expected, new QualityClaim(kind, bits, rate, kbps, lossy, dsd, false).Describe());

    [Theory]
    [InlineData(96000, "96 kHz")]
    [InlineData(44100, "44.1 kHz")]
    [InlineData(22050, "22.05 kHz")]
    [InlineData(20187.5, "20.2 kHz")]
    public void Frequencies_read_naturally(double hz, string expected) =>
        Assert.Equal(expected, QualityOpinion.Khz(hz));

    /// <summary>A finished analysis whose average spectrum follows <paramref name="db"/>.</summary>
    private static SpectrumAnalysis Spectrum(int rate, Func<double, double> db, int? usedBits = null, double peakDb = -0.5,
        double? extraBitsShare = null)
    {
        var size = SpectrumAccumulator.FftSizeFor(rate);
        var bins = (size / 2) + 1;
        var average = new double[bins];
        for (var k = 0; k < bins; k++)
            average[k] = Math.Max(SpectrumAccumulator.FloorDb, db((double)k * rate / size));

        return new SpectrumAnalysis
        {
            SampleRate = rate,
            Channels = 2,
            Duration = TimeSpan.FromMinutes(4),
            FftSize = size,
            Average = average,
            Peak = average,
            Columns = 1,
            Rows = 1,
            Picture = [-60f],
            PeakSampleDb = peakDb,
            UsedBits = usedBits,
            ExtraBitsShare = extraBitsShare ?? (usedBits > 16 ? 0.99 : usedBits is null ? null : 0),
        };
    }
}
