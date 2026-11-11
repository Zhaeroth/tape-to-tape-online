using System;
using System.Collections.Generic;
using T2TOnline.Core.Diagnostics;

namespace T2TOnline.Core.Tests.Diagnostics;

public class RepeatLimiterTests
{
    [Fact]
    public void The_first_few_of_a_key_are_written_then_counted()
    {
        var limiter = new RepeatLimiter(writtenPerKey: 3, summaryEveryMs: 60_000, maxKeys: 10);
        for (int i = 0; i < 3; i++)
        {
            Assert.Equal(RepeatDecision.Write, limiter.Next("tool failed", i, out int counted));
            Assert.Equal(0, counted);
        }

        Assert.Equal(RepeatDecision.Count, limiter.Next("tool failed", 10, out _));
        Assert.Equal(RepeatDecision.Count, limiter.Next("tool failed", 20, out _));
    }

    [Fact]
    public void Remaining_says_how_many_more_are_written_in_full()
    {
        var limiter = new RepeatLimiter(writtenPerKey: 2, summaryEveryMs: 60_000, maxKeys: 1);
        Assert.Equal(2, limiter.Remaining("a"));
        limiter.Next("a", 0, out _);
        Assert.Equal(1, limiter.Remaining("a"));
        limiter.Next("a", 0, out _);
        Assert.Equal(0, limiter.Remaining("a"));
        limiter.Next("a", 0, out _);
        Assert.Equal(0, limiter.Remaining("a"));
        Assert.Equal(2, limiter.Remaining("b")); // past the key limit: the shared budget, untouched so far
        limiter.Next("b", 0, out _);
        Assert.Equal(1, limiter.Remaining("c"));
    }

    [Fact]
    public void A_summary_comes_once_per_interval_with_what_was_counted()
    {
        var limiter = new RepeatLimiter(writtenPerKey: 1, summaryEveryMs: 1_000, maxKeys: 10);
        Assert.Equal(RepeatDecision.Write, limiter.Next("k", 0, out _));
        for (long now = 16; now < 1_000; now += 16)
        {
            Assert.Equal(RepeatDecision.Count, limiter.Next("k", now, out _));
        }

        Assert.Equal(RepeatDecision.Summarize, limiter.Next("k", 1_000, out int counted));
        Assert.Equal(63, counted); // 62 counted between 16 and 992 ms, and this one
        Assert.Equal(RepeatDecision.Count, limiter.Next("k", 1_500, out _));
        Assert.Equal(RepeatDecision.Summarize, limiter.Next("k", 2_000, out counted));
        Assert.Equal(2, counted);
    }

    [Fact]
    public void Keys_have_their_own_budgets()
    {
        var limiter = new RepeatLimiter(writtenPerKey: 1, summaryEveryMs: 60_000, maxKeys: 10);
        Assert.Equal(RepeatDecision.Write, limiter.Next("a", 0, out _));
        Assert.Equal(RepeatDecision.Count, limiter.Next("a", 1, out _));
        Assert.Equal(RepeatDecision.Write, limiter.Next("b", 2, out _));
    }

    [Fact]
    public void Keys_past_the_limit_share_one_budget()
    {
        var limiter = new RepeatLimiter(writtenPerKey: 1, summaryEveryMs: 60_000, maxKeys: 2);
        Assert.Equal(RepeatDecision.Write, limiter.Next("a", 0, out _));
        Assert.Equal(RepeatDecision.Write, limiter.Next("b", 0, out _));
        Assert.Equal(RepeatDecision.Write, limiter.Next("c 1", 0, out _));
        Assert.Equal(RepeatDecision.Count, limiter.Next("c 2", 0, out _));
        Assert.Equal(RepeatDecision.Count, limiter.Next("c 3", 0, out _));
        KeyValuePair<string, int> pending = Assert.Single(limiter.Drain());
        Assert.Equal(RepeatLimiter.OverflowKey, pending.Key);
        Assert.Equal(2, pending.Value);
    }

    [Fact]
    public void Drain_gives_what_was_counted_once()
    {
        var limiter = new RepeatLimiter(writtenPerKey: 1, summaryEveryMs: 60_000, maxKeys: 10);
        limiter.Next("a", 0, out _);
        limiter.Next("a", 1, out _);
        limiter.Next("a", 2, out _);
        limiter.Next("b", 3, out _);
        KeyValuePair<string, int> pending = Assert.Single(limiter.Drain());
        Assert.Equal(new KeyValuePair<string, int>("a", 2), pending);
        Assert.Empty(limiter.Drain());
    }

    [Theory]
    [InlineData(0, 0, 1)]
    [InlineData(1, -1, 1)]
    [InlineData(1, 0, 0)]
    public void Bad_limits_are_refused(int written, long every, int keys) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new RepeatLimiter(written, every, keys));
}
