using System.Globalization;
using AudioFool.Core.Library;
using AudioFool.Core.Models;
using AudioFool.Core.Playback;

namespace AudioFool.Core.Tests;

/// <summary>
/// The playback bar's output readout and mute (spec 6.7), and the status bar's
/// library totals with their size in GB (spec 6.8).
/// </summary>
public class PlaybackBarTextTests
{
    private const long GB = 1024L * 1024 * 1024;
    private static readonly CultureInfo En = CultureInfo.GetCultureInfo("en-US");

    // ------------------------------------------------------------ library size

    [Theory]
    [InlineData(0L, "0 GB")]
    [InlineData(GB / 2 - 1, "0 GB")]
    [InlineData(GB / 2, "1 GB")]
    [InlineData(GB, "1 GB")]
    [InlineData(692 * GB + GB / 3, "692 GB")]
    [InlineData(999 * GB, "999 GB")]
    [InlineData(1204 * GB, "1,204 GB")]
    [InlineData(1204 * GB - GB / 2, "1,204 GB")]
    public void Size_is_whole_binary_gigabytes_rounded_to_nearest(long bytes, string expected) =>
        Assert.Equal(expected, LibrarySummary.Gigabytes(bytes, En));

    [Fact]
    public void A_decimal_gigabyte_is_not_a_gigabyte()
    {
        // 1,000,000,000 bytes is 0.93 binary GB: rounds to 1, but 600 decimal GB
        // is 558.8 binary GB, which is what Explorer would say.
        Assert.Equal("559 GB", LibrarySummary.Gigabytes(600_000_000_000, En));
    }

    [Fact]
    public void Totals_list_counts_and_size_with_middle_dots()
    {
        var tracks = Enumerable.Range(0, 2500)
            .Select(i => new Track { FilePath = $@"D:\Music\{i}.flac", FileSize = 300 * 1024 * 1024 })
            .ToList();

        // 2,500 × 300 MB = 732.4 GB.
        Assert.Equal("499 artists · 2,376 albums · 2,500 tracks · 732 GB",
            LibrarySummary.Totals(499, 2376, tracks, En));
    }

    // ---------------------------------------------------------- output readout

    [Fact]
    public void Shared_readout_names_rate_and_depth()
    {
        Assert.Equal("Shared · 96 kHz / 32-bit", OutputReadout.Describe(OutputMode.Shared, 96000, 32, 96000, false));
    }

    [Fact]
    public void Shared_readout_says_resampled_when_the_rates_differ()
    {
        Assert.Equal("Shared · 96 kHz / 32-bit (resampled)", OutputReadout.Describe(OutputMode.Shared, 96000, 32, 44100, false));
    }

    [Fact]
    public void Shared_readout_without_a_known_depth_or_source()
    {
        Assert.Equal("Shared · 48 kHz", OutputReadout.Describe(OutputMode.Shared, 48000, 0, 0, false));
    }

    [Fact]
    public void Exclusive_readout_is_bit_perfect_without_depth()
    {
        Assert.Equal("Exclusive · 44.1 kHz · bit-perfect", OutputReadout.Describe(OutputMode.Exclusive, 44100, 24, 44100, false));
    }

    [Fact]
    public void Exclusive_dop_readout_says_so()
    {
        Assert.Equal("Exclusive · 176.4 kHz · bit-perfect · DSD over PCM",
            OutputReadout.Describe(OutputMode.Exclusive, 176400, 24, 176400, true));
    }

    // -------------------------------------------------------------------- mute

    [Fact]
    public void Mute_shows_zero_and_unmute_restores_the_previous_level()
    {
        var volume = new VolumeState(0.82);

        volume.ToggleMute();
        Assert.True(volume.IsMuted);
        Assert.Equal(0, volume.Effective);

        volume.ToggleMute();
        Assert.False(volume.IsMuted);
        Assert.Equal(0.82, volume.Effective);
    }

    [Fact]
    public void Moving_the_slider_while_muted_unmutes_at_the_new_level()
    {
        var volume = new VolumeState(0.82);
        volume.ToggleMute();

        volume.SetLevel(0.4);

        Assert.False(volume.IsMuted);
        Assert.Equal(0.4, volume.Effective);
    }

    [Fact]
    public void The_muted_slider_writing_back_zero_keeps_mute_and_level()
    {
        var volume = new VolumeState(0.82);
        volume.ToggleMute();

        volume.SetLevel(0);

        Assert.True(volume.IsMuted);
        Assert.Equal(0.82, volume.Level);
    }
}
