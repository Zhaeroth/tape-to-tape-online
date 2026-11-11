using T2TOnline.Core.Diagnostics;

namespace T2TOnline.Core.Tests.Diagnostics;

/// <summary>
/// The host's per-friend stream rates (the first report after every match's start divided the session's
/// totals by 10 s, "6117/s snapshots, 31457 kbps").
/// </summary>
public class RateWindowTests
{
    private const ulong Riley = 76561199000000001;
    private const ulong Sam = 76561199000000002;

    [Fact]
    public void The_first_window_after_a_match_starts_counts_only_what_was_sent_in_it()
    {
        var window = new RateWindow<ulong>();
        // A session's earlier matches sent 61164 snapshots; the new match starts.
        window.Rebase(100, new[] { (Riley, 61164L, 30_000_000L) });
        RateSample sample = window.Take(Riley, 61164 + 600, 30_000_000 + 450_000, 110);
        Assert.Equal(600, sample.Count);
        Assert.Equal(60, sample.CountPerSecond, 6);
        Assert.Equal(360, sample.Kbps, 6);
        Assert.Equal(750, sample.BytesPerCount);
    }

    [Fact]
    public void Each_window_follows_on_from_the_last_take()
    {
        var window = new RateWindow<ulong>();
        window.Rebase(0, new[] { (Riley, 0L, 0L) });
        window.Take(Riley, 600, 300_000, 10);
        RateSample second = window.Take(Riley, 1150, 600_000, 20);
        Assert.Equal(550, second.Count);
        Assert.Equal(10, second.Seconds, 6);
    }

    [Fact]
    public void A_report_forced_early_is_divided_by_the_time_it_took()
    {
        var window = new RateWindow<ulong>();
        window.Rebase(50, new[] { (Riley, 1000L, 0L) });
        RateSample sample = window.Take(Riley, 1180, 0, 53);
        Assert.Equal(3, sample.Seconds, 6);
        Assert.Equal(60, sample.CountPerSecond, 6);
    }

    [Fact]
    public void A_friend_added_mid_window_counts_from_zero_at_the_window_s_start()
    {
        var window = new RateWindow<ulong>();
        window.Rebase(0, new[] { (Riley, 10L, 0L) });
        window.Take(Riley, 610, 0, 10);
        window.EndWindow(10);

        // Sam joined at 14 s; his stream's totals are his own, from zero.
        RateSample sam = window.Take(Sam, 360, 180_000, 20);
        Assert.Equal(360, sam.Count);
        Assert.Equal(10, sam.Seconds, 6);
    }

    [Fact]
    public void A_friend_removed_and_back_counts_their_new_totals_from_zero()
    {
        var window = new RateWindow<ulong>();
        window.Rebase(0, new[] { (Riley, 5000L, 2_000_000L) });
        window.Remove(Riley);
        window.EndWindow(5);
        RateSample back = window.Take(Riley, 120, 50_000, 10);
        Assert.Equal(120, back.Count);
        Assert.Equal(50_000, back.Bytes);
    }

    [Fact]
    public void Totals_that_went_down_under_the_same_key_read_from_zero_never_negative()
    {
        var window = new RateWindow<ulong>();
        window.Rebase(0, new[] { (Riley, 5000L, 2_000_000L) });
        RateSample sample = window.Take(Riley, 300, 100_000, 10);
        Assert.Equal(300, sample.Count);
        Assert.Equal(100_000, sample.Bytes);
    }

    [Fact]
    public void A_rebase_forgets_friends_no_longer_listed_and_a_first_take_without_any_window_is_never_a_division_by_zero()
    {
        var window = new RateWindow<ulong>();
        RateSample first = window.Take(Riley, 10, 10, 7);
        Assert.True(first.Seconds > 0);
        window.Rebase(8, new[] { (Sam, 0L, 0L) });
        Assert.Equal(1, window.Keys);
        window.Clear();
        Assert.Equal(0, window.Keys);
    }
}
