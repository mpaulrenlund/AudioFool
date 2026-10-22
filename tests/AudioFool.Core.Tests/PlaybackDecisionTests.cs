using AudioFool.Core.Models;
using AudioFool.Core.Playback;

namespace AudioFool.Core.Tests;

/// <summary>
/// The engine's decisions pulled out of <see cref="AudioEngine"/> in session 67:
/// what happens when a track runs out, where a reopened track carries on from,
/// and when DSD goes out as DoP.
/// </summary>
public class PlaybackDecisionTests
{
    // ---------- the end of a track ----------

    private static TrackEndAction End(bool repeatOne = false, bool hasOutput = true, bool nextOpened = true,
                                      bool shared = true, bool sameRate = true, bool dop = false,
                                      bool midSave = false, bool hasNext = true) =>
        TrackEnd.Decide(repeatOne, hasOutput, nextOpened, shared, sameRate, dop, midSave, hasNext);

    [Fact]
    public void Repeat_one_plays_the_same_track_again()
    {
        Assert.Equal(TrackEndAction.RepeatOne, End(repeatOne: true));
        Assert.Equal(TrackEndAction.RepeatOne, End(repeatOne: true, nextOpened: false, hasNext: false));
    }

    [Fact]
    public void Shared_output_joins_any_rate()
    {
        Assert.Equal(TrackEndAction.Splice, End(shared: true, sameRate: false));
    }

    [Fact]
    public void Exclusive_output_joins_the_same_rate_and_reopens_for_another()
    {
        Assert.Equal(TrackEndAction.Splice, End(shared: false, sameRate: true));
        Assert.Equal(TrackEndAction.Reopen, End(shared: false, sameRate: false));
    }

    [Fact]
    public void Dop_joins_only_an_exclusive_output()
    {
        Assert.Equal(TrackEndAction.Splice, End(shared: false, sameRate: true, dop: true));
        Assert.Equal(TrackEndAction.Reopen, End(shared: true, sameRate: true, dop: true));
    }

    [Fact]
    public void A_next_track_that_wasnt_opened_is_opened_now_not_the_end_of_the_queue()
    {
        Assert.Equal(TrackEndAction.OpenNext, End(nextOpened: false));
    }

    [Fact]
    public void A_next_track_out_for_a_save_waits_for_it()
    {
        Assert.Equal(TrackEndAction.WaitForSave, End(nextOpened: false, midSave: true));
    }

    [Fact]
    public void The_end_of_the_order_finishes()
    {
        Assert.Equal(TrackEndAction.Finish, End(nextOpened: false, hasNext: false));
    }

    // ---------- where a reopen carries on ----------

    private static readonly Track Song = new() { FilePath = @"D:\Music\a.flac", Title = "a" };
    private static readonly Track Before = new() { FilePath = @"D:\Music\0.flac", Title = "0" };

    [Fact]
    public void A_reopen_carries_on_from_what_was_heard()
    {
        var at = ReopenPoint.Choose(Song, new HeardPoint(Song, TimeSpan.FromSeconds(41.8)), TimeSpan.FromSeconds(42));

        Assert.Equal(TimeSpan.FromSeconds(41.8), at);
    }

    [Fact]
    public void A_reopen_before_a_join_is_heard_starts_the_track()
    {
        Assert.Equal(TimeSpan.Zero,
            ReopenPoint.Choose(Song, new HeardPoint(Before, TimeSpan.FromSeconds(200)), TimeSpan.FromSeconds(0.15)));
    }

    [Fact]
    public void A_reopen_with_nothing_heard_to_go_by_uses_the_decoder()
    {
        Assert.Equal(TimeSpan.FromSeconds(3),
            ReopenPoint.Choose(Song, new HeardPoint(null, TimeSpan.Zero), TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public void A_reopen_never_goes_past_the_decoder_or_below_zero()
    {
        Assert.Equal(TimeSpan.FromSeconds(3),
            ReopenPoint.Choose(Song, new HeardPoint(Song, TimeSpan.FromSeconds(5)), TimeSpan.FromSeconds(3)));
        Assert.Equal(TimeSpan.Zero,
            ReopenPoint.Choose(Song, new HeardPoint(Song, TimeSpan.FromSeconds(-1)), TimeSpan.FromSeconds(3)));
    }

    // ---------- DoP ----------

    private static readonly int[] DeviceRates = [44100, 48000, 88200, 96000, 176400, 192000, 352800, 384000];

    [Fact]
    public void Dop_needs_bit_perfect_and_a_rate_the_device_clocks()
    {
        Assert.True(DopRule.MayOpen(OutputMode.Exclusive, 176400, DeviceRates, []));
        Assert.False(DopRule.MayOpen(OutputMode.Shared, 176400, DeviceRates, []));
        Assert.False(DopRule.MayOpen(OutputMode.Exclusive, 705600, DeviceRates, []));
        Assert.False(DopRule.MayOpen(OutputMode.Exclusive, 0, DeviceRates, []));
    }

    [Fact]
    public void Dop_is_never_opened_at_a_rate_exclusive_mode_refused()
    {
        Assert.False(DopRule.MayOpen(OutputMode.Exclusive, 176400, DeviceRates, [176400]));
        Assert.True(DopRule.MayOpen(OutputMode.Exclusive, 352800, DeviceRates, [176400]));
    }

    [Fact]
    public void Dop_plays_only_on_an_exclusive_output_at_its_rate()
    {
        Assert.True(DopRule.MayPlayOn(OutputMode.Exclusive, 176400, 176400));
        Assert.False(DopRule.MayPlayOn(OutputMode.Shared, 176400, 176400));
        Assert.False(DopRule.MayPlayOn(OutputMode.Exclusive, 88200, 176400));
    }
}
