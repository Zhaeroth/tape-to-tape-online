using T2TOnline.Core.Stream;

namespace T2TOnline.Core.Tests.Stream;

/// <summary>A friend's pad never buzzes without end: a burst is cut to a few, a flood to a rate, a long buzz rests.</summary>
public class RumbleLimiterTests
{
    private const long Ms = 1000;

    [Fact]
    public void Normal_play_is_never_limited()
    {
        // A pass, a shot, a check: one or two a second, each a fraction of a second, for ten minutes.
        var limiter = new RumbleLimiter();
        for (long t = 0; t < 600_000 * Ms; t += 700 * Ms)
        {
            Assert.Equal(RumbleVerdict.Play, limiter.Decide(t, 0.3f));
            Assert.Equal(RumbleVerdict.Play, limiter.Decide(t + (100 * Ms), 0.1f));
        }

        Assert.Equal(0, limiter.TooMany + limiter.TooLong);
    }

    [Fact]
    public void A_burst_delivered_together_plays_the_first_few()
    {
        // A hitch or a restarted playout clock delivers a second's rumbles in one frame.
        var limiter = new RumbleLimiter();
        int played = 0;
        for (int i = 0; i < 20; i++)
        {
            played += limiter.Decide(5_000 * Ms, 0.1f) == RumbleVerdict.Play ? 1 : 0;
        }

        Assert.Equal(RumbleLimiter.Burst, played);
        Assert.Equal(20 - RumbleLimiter.Burst, limiter.TooMany);
    }

    [Fact]
    public void A_flood_is_held_to_the_rate()
    {
        // 60 short rumbles a second for 10 s: about 8 a second play (plus the first burst).
        var limiter = new RumbleLimiter();
        int played = 0;
        for (long t = 0; t < 10_000 * Ms; t += 16_667)
        {
            played += limiter.Decide(t, 0.01f) == RumbleVerdict.Play ? 1 : 0;
        }

        Assert.InRange(played, (10 * RumbleLimiter.PerSecond) - 1, (10 * RumbleLimiter.PerSecond) + RumbleLimiter.Burst + 1);
    }

    [Fact]
    public void A_continuous_buzz_rests_after_three_seconds_until_the_pad_has_been_still()
    {
        // Rumbles of 0.5 s every 0.25 s (inside the rate) keep the pad buzzing without a break.
        var limiter = new RumbleLimiter();
        long t = 0;
        for (; t < RumbleLimiter.MaxContinuousMicros; t += 250 * Ms)
        {
            Assert.Equal(RumbleVerdict.Play, limiter.Decide(t, 0.5f));
        }

        // 3 s of buzzing: refused, and the pad stays still while the last one runs out (3.25 s) and 0.5 s more.
        Assert.Equal(RumbleVerdict.TooLong, limiter.Decide(t, 0.5f));
        Assert.Equal(RumbleVerdict.TooLong, limiter.Decide(t + (700 * Ms), 0.5f));
        Assert.Equal(RumbleVerdict.TooLong, limiter.Decide(3_749 * Ms, 0.5f));
        Assert.Equal(RumbleVerdict.Play, limiter.Decide(3_750 * Ms, 0.5f));
        Assert.Equal(3, limiter.TooLong);
    }

    [Fact]
    public void Separate_buzzes_never_add_up()
    {
        // 2 s on, a gap, 2 s on: each a buzz of its own.
        var limiter = new RumbleLimiter();
        for (int round = 0; round < 5; round++)
        {
            long start = round * 3_000 * Ms;
            for (long t = start; t < start + (2_000 * Ms); t += 250 * Ms)
            {
                Assert.Equal(RumbleVerdict.Play, limiter.Decide(t, 0.3f));
            }
        }

        Assert.Equal(0, limiter.TooLong);
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(1000f)]
    public void A_broken_or_endless_duration_counts_as_the_longest(float duration)
    {
        // Two of them in a row make one buzz until 3.9 s, not an endless one: refused at 3.8 s (over 3 s), resting until
        // 4.4 s, then playing again.
        var limiter = new RumbleLimiter();
        Assert.Equal(RumbleVerdict.Play, limiter.Decide(0, duration));
        Assert.Equal(RumbleVerdict.Play, limiter.Decide(1_900 * Ms, duration));
        Assert.Equal(RumbleVerdict.TooLong, limiter.Decide(3_800 * Ms, 0.1f));
        Assert.Equal(RumbleVerdict.TooLong, limiter.Decide(4_399 * Ms, 0.1f));
        Assert.Equal(RumbleVerdict.Play, limiter.Decide(4_400 * Ms, 0.1f));
    }

    [Fact]
    public void A_negative_duration_counts_as_none()
    {
        var limiter = new RumbleLimiter();
        Assert.Equal(RumbleVerdict.Play, limiter.Decide(0, -5f));
        Assert.Equal(RumbleVerdict.Play, limiter.Decide(10 * Ms, 0.1f));
        Assert.Equal(2, limiter.Played);
    }

    [Fact]
    public void Reset_starts_again()
    {
        var limiter = new RumbleLimiter();
        for (int i = 0; i < 10; i++)
        {
            limiter.Decide(0, 0.1f);
        }

        limiter.Reset();
        Assert.Equal((0L, 0L, 0L), (limiter.Played, limiter.TooMany, limiter.TooLong));
        Assert.Equal(RumbleVerdict.Play, limiter.Decide(0, 0.1f));
    }
}
