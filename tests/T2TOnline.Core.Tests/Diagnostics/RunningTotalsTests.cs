using System;
using T2TOnline.Core.Diagnostics;

namespace T2TOnline.Core.Tests.Diagnostics;

/// <summary>The net overlay's arithmetic (F9): rates, growth and loss between samples of running totals.</summary>
public class RunningTotalsTests
{
    [Fact]
    public void The_first_sample_reads_zero_and_the_next_one_a_rate()
    {
        var totals = new RunningTotals();
        totals.Begin(TimeSpan.FromSeconds(10));
        Assert.Equal(0, totals.Seconds);
        Assert.Equal(0, totals.PerSecond("pad", 1000));
        Assert.Equal(0, totals.Delta("bytes", 5000));

        totals.Begin(TimeSpan.FromSeconds(10.5));
        Assert.Equal(0.5, totals.Seconds, 6);
        Assert.Equal(120, totals.PerSecond("pad", 1060), 6);
        Assert.Equal(700, totals.Delta("bytes", 5700));
    }

    [Fact]
    public void A_total_that_went_down_reads_zero_once_then_counts_from_there()
    {
        var totals = new RunningTotals();
        totals.Begin(TimeSpan.Zero);
        totals.PerSecond("pad", 5400);
        totals.Delta("acks", 90);

        // A rejoining friend's pad stream starts over at 0: never a negative rate.
        totals.Begin(TimeSpan.FromSeconds(1));
        Assert.Equal(0, totals.PerSecond("pad", 12));
        Assert.Equal(0, totals.Delta("acks", 3));

        totals.Begin(TimeSpan.FromSeconds(2));
        Assert.Equal(108, totals.PerSecond("pad", 120), 6);
        Assert.Equal(7, totals.Delta("acks", 10));
    }

    [Fact]
    public void Loss_is_the_share_of_what_was_expected_that_didnt_arrive()
    {
        var totals = new RunningTotals();
        totals.Begin(TimeSpan.Zero);
        Assert.Equal(0, totals.LossPercent("host", 100, 100));

        totals.Begin(TimeSpan.FromSeconds(0.5));
        Assert.Equal(5, totals.LossPercent("host", 300, 290), 6);

        // Nothing expected: no loss; more arriving than expected (a duplicate): never below 0.
        totals.Begin(TimeSpan.FromSeconds(1));
        Assert.Equal(0, totals.LossPercent("host", 300, 290));
        totals.Begin(TimeSpan.FromSeconds(1.5));
        Assert.Equal(0, totals.LossPercent("host", 310, 305));
        totals.Begin(TimeSpan.FromSeconds(2));
        Assert.Equal(100, totals.LossPercent("host", 320, 305));
    }

    [Fact]
    public void A_clock_that_went_backwards_counts_as_a_millisecond_and_a_reset_starts_over()
    {
        var totals = new RunningTotals();
        totals.Begin(TimeSpan.FromSeconds(5));
        totals.PerSecond("x", 0);
        totals.Begin(TimeSpan.FromSeconds(4));
        Assert.Equal(0.001, totals.Seconds, 9);
        Assert.Equal(1000, totals.PerSecond("x", 1), 6);

        totals.Reset();
        Assert.Equal(TimeSpan.MinValue, totals.SampledAt);
        totals.Begin(TimeSpan.FromSeconds(6));
        Assert.Equal(0, totals.PerSecond("x", 50));
    }

    [Theory]
    [InlineData(-1L, "-")]
    [InlineData(0L, "0.0")]
    [InlineData(9_549L, "9.5")]
    [InlineData(10_000L, "10")]
    [InlineData(49_400L, "49")]
    [InlineData(1_234_567L, "1235")]
    public void Milliseconds_show_one_decimal_only_under_ten(long micros, string shown)
    {
        Assert.Equal(shown, RunningTotals.Ms(micros));
    }
}
