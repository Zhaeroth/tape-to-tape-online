using T2TOnline.Core.Stream;

namespace T2TOnline.Core.Tests.Stream;

public class OneShotLimiterTests
{
    [Fact]
    public void A_matchs_own_sounds_all_play()
    {
        // A whistle or a goalie's save every few seconds, and a handful together after a hitch.
        var limiter = new OneShotLimiter();
        long now = 5_000_000;
        for (int i = 0; i < 200; i++)
        {
            Assert.True(limiter.TryTake(now));
            now += 400_000;
        }

        for (int i = 0; i < 6; i++)
        {
            Assert.True(limiter.TryTake(now));
        }

        Assert.Equal(206, limiter.Played);
        Assert.Equal(0, limiter.Refused);
    }

    [Fact]
    public void A_flood_in_one_frame_plays_a_burst_and_no_more()
    {
        var limiter = new OneShotLimiter();
        int played = 0;
        for (int i = 0; i < 256; i++)
        {
            played += limiter.TryTake(1_000_000) ? 1 : 0;
        }

        Assert.Equal(OneShotLimiter.Burst, played);
        Assert.Equal(OneShotLimiter.Burst, limiter.Played);
        Assert.Equal(256 - OneShotLimiter.Burst, limiter.Refused);
    }

    [Fact]
    public void A_sustained_flood_plays_the_rate_and_no_more()
    {
        // 256 a frame at 60 frames a second, for ten seconds.
        var limiter = new OneShotLimiter();
        long now = 0;
        long played = 0;
        for (int frame = 0; frame < 600; frame++)
        {
            for (int i = 0; i < 256; i++)
            {
                played += limiter.TryTake(now) ? 1 : 0;
            }

            now += 16_667;
        }

        Assert.InRange(played, OneShotLimiter.PerSecond * 9, OneShotLimiter.Burst + (OneShotLimiter.PerSecond * 10) + 1);
        Assert.Equal((600 * 256) - played, limiter.Refused);
    }

    [Fact]
    public void A_clock_that_stands_still_or_steps_back_gives_nothing_more()
    {
        var limiter = new OneShotLimiter();
        for (int i = 0; i < OneShotLimiter.Burst; i++)
        {
            Assert.True(limiter.TryTake(2_000_000));
        }

        Assert.False(limiter.TryTake(2_000_000));
        Assert.False(limiter.TryTake(1_000_000));
        Assert.False(limiter.TryTake(long.MinValue + 1));
        Assert.True(limiter.TryTake(2_000_000 + (1_000_000 / OneShotLimiter.PerSecond) + 1));
    }

    [Fact]
    public void A_new_playback_starts_with_a_full_bucket()
    {
        var limiter = new OneShotLimiter();
        for (int i = 0; i < 100; i++)
        {
            limiter.TryTake(3_000_000);
        }

        limiter.Reset();
        Assert.Equal(0, limiter.Played);
        Assert.Equal(0, limiter.Refused);
        for (int i = 0; i < OneShotLimiter.Burst; i++)
        {
            Assert.True(limiter.TryTake(3_000_000));
        }

        Assert.False(limiter.TryTake(3_000_000));
    }
}
