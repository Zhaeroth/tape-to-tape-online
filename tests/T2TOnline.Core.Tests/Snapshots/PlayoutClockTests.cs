using System;
using T2TOnline.Core.Snapshots;

namespace T2TOnline.Core.Tests.Snapshots;

public class PlayoutClockTests
{
    private const long Interval = 16_667; // host snapshots at 60 Hz
    private const long Frame = 6_944;     // friend renders at 144 fps

    /// <summary>Plays a simulated link: the host sends every <see cref="Interval"/>, the friend renders every <see cref="Frame"/>.</summary>
    private static Result Run(Func<long, long?> arrivalDelay, double seconds, double hostRate = 1.0, Func<long, long>? frameLength = null, Action<long, long, PlayoutClock>? eachFrame = null)
    {
        var clock = new PlayoutClock();
        long hostNext = 0;
        long previousRender = long.MinValue;
        var pending = new System.Collections.Generic.List<(long Arrival, long Host)>();
        var result = new Result();
        long end = (long)(seconds * 1_000_000);
        for (long local = 0; local < end; local += frameLength?.Invoke(local) ?? Frame)
        {
            // The host's clock may run at a slightly different speed from the friend's.
            while (hostNext <= local * hostRate)
            {
                long? delay = arrivalDelay(hostNext);
                if (delay is long d)
                {
                    pending.Add(((long)(hostNext / hostRate) + d, hostNext));
                }

                hostNext += Interval;
            }

            pending.Sort((a, b) => a.Arrival.CompareTo(b.Arrival));
            while (pending.Count > 0 && pending[0].Arrival <= local)
            {
                // Stamped with the real arrival, as the network layer reports it, not with this frame.
                clock.OnSnapshot(pending[0].Host, pending[0].Arrival);
                pending.RemoveAt(0);
            }

            long render = clock.Advance(local);
            if (render == long.MinValue)
            {
                continue;
            }

            Assert.True(render >= previousRender, "render time went backwards");
            Assert.True(render <= clock.NewestHostTime, "render time passed the newest snapshot");
            previousRender = render;
            eachFrame?.Invoke(local, render, clock);
            if (local > 2_000_000)
            {
                result.Frames++;
                result.Underruns += render == clock.NewestHostTime ? 1 : 0;
                result.MaxBehind = Math.Max(result.MaxBehind, (long)(local * hostRate) - render);
            }
        }

        result.Clock = clock;
        return result;
    }

    [Fact]
    public void A_clean_link_renders_one_interval_and_a_frame_behind()
    {
        Result run = Run(_ => 30_000, 10);
        Assert.Equal(0, run.Underruns);
        Assert.Equal(1, run.Clock.Snaps);
        Assert.InRange(run.Clock.TargetDelay, Interval, Interval + Frame + PlayoutClock.Margin + 1_000);
    }

    [Fact]
    public void A_jittery_link_grows_the_delay_to_cover_it()
    {
        var random = new Random(7);
        Result run = Run(_ => 30_000 + random.Next(0, 20_000), 20);
        Assert.InRange(run.Clock.TargetDelay, Interval + 15_000, Interval + 20_000 + Frame + PlayoutClock.Margin + 2_000);
        Assert.True(run.Underruns < run.Frames / 50, $"{run.Underruns} underruns in {run.Frames} frames");
        Assert.Equal(1, run.Clock.Snaps);
    }

    [Fact]
    public void Lost_snapshots_are_interpolated_across()
    {
        var random = new Random(3);
        Result run = Run(_ => random.Next(100) < 10 ? null : 30_000, 10);
        Assert.True(run.Underruns < run.Frames / 20, $"{run.Underruns} underruns in {run.Frames} frames");
        Assert.Equal(1, run.Clock.Snaps);
    }

    [Fact]
    public void A_host_clock_running_fast_is_followed_without_jumps()
    {
        Result run = Run(_ => 30_000, 30, hostRate: 1.003);
        Assert.Equal(1, run.Clock.Snaps);
        Assert.True(run.Underruns < run.Frames / 50, $"{run.Underruns} underruns in {run.Frames} frames");
        Assert.InRange(run.Clock.Rate, 1.0, 1.0 + PlayoutClock.MaxWarp);
    }

    [Fact]
    public void A_stall_holds_then_catches_up()
    {
        // Nothing arrives between 4 s and 5 s of host time, then everything that was held up arrives at once.
        Result run = Run(host => host is >= 4_000_000 and < 5_000_000 ? 5_000_000 - host + 30_000 : 30_000, 10);
        Assert.True(run.Underruns > 0, "the stall should show as held frames");
        Assert.InRange(run.Clock.TargetDelay, Interval, PlayoutClock.MaxDelay);
    }

    [Fact]
    public void A_long_drop_is_one_hold_and_one_jump_back_to_live()
    {
        // Every snapshot from 4 s to 19 s is lost (a 15 s drop), then the link is back.
        Result run = Run(host => host is >= 4_000_000 and < 19_000_000 ? null : 30_000, 25);
        Assert.Equal(2, run.Clock.Snaps); // the start, and once back to live: not once per held frame
        Assert.InRange(run.Clock.Holds, 1, 3);
        Assert.True(run.Underruns > 15 * 1_000_000 / Frame * 9 / 10, $"{run.Underruns} underruns: the drop should show as held frames");
        Assert.True(run.MaxBehind > 14_000_000, "render time should have held through the drop");
    }

    [Fact]
    public void Long_frames_on_this_PC_neither_grow_the_delay_nor_jump()
    {
        // A 300 ms frame every 2 s (a menu opening, a garbage collection): this PC's hitch, not the network's.
        Result run = Run(_ => 30_000, 20, frameLength: local => local % 2_000_000 < Frame ? 300_000 : Frame);
        Assert.Equal(1, run.Clock.Snaps);
        Assert.InRange(run.Clock.TargetDelay, Interval, Interval + PlayoutClock.Margin + (3 * PlayoutClock.CushionStep));
    }

    [Fact]
    public void Holds_teach_a_cushion_that_fades_when_they_stop()
    {
        // Every 10th snapshot is lost for 5 s, then none: the cushion grows, then shrinks back.
        var lossy = Run(host => host < 5_000_000 && (host / Interval) % 10 == 0 ? null : 30_000, 5);
        Assert.True(lossy.Clock.Cushion > 0, "holds should have grown the cushion");
        var calm = Run(host => host < 5_000_000 && (host / Interval) % 10 == 0 ? null : 30_000, 30);
        Assert.True(calm.Clock.Cushion < lossy.Clock.Cushion, "the cushion should fade once holds stop");
    }

    [Fact]
    public void A_link_that_suddenly_gets_much_slower_is_waited_for_not_jumped_back_to()
    {
        // From 4 s on, the link's delay steps up by 500 ms (over SnapThreshold) for four snapshots in five; the fifth still
        // takes the fast path, so render time isn't held back by the newest snapshot and ends up far ahead of the new target.
        // (A plain step can't get there: render time holds at the newest snapshot until the slow ones arrive.)
        long firstStop = long.MaxValue;
        long lastStop = long.MinValue;
        long previousRender = long.MinValue;
        Result run = Run(host => host < 4_000_000 || (host / Interval) % 5 == 0 ? 30_000 : 530_000, 12, eachFrame: (local, render, clock) =>
        {
            if (clock.Rate == 0)
            {
                // Waiting: render time stands still rather than going back.
                Assert.True(previousRender == long.MinValue || render == previousRender || render == clock.NewestHostTime);
                firstStop = Math.Min(firstStop, local);
                lastStop = Math.Max(lastStop, local);
            }

            previousRender = render;
        });

        Assert.InRange(firstStop, 4_000_000, 6_000_000);
        Assert.True(lastStop < 8_000_000, "after waiting, it plays on");
        Assert.Equal(1, run.Clock.Snaps); // never jumped
        Assert.InRange(run.Clock.Rate, 1 - PlayoutClock.MaxWarp, 1 + PlayoutClock.MaxWarp);
    }

    [Fact]
    public void A_lasting_jump_in_the_hosts_stamps_is_followed_after_a_second()
    {
        // Regression case: stamps that jump 6 s ahead and stay there used to be ignored for ever (a frozen match).
        var clock = new PlayoutClock();
        long local = 1_000_000_000;
        long host = 0;
        long jumpedAt = 0;
        long render = 0;
        for (int i = 0; i < 60 * 15; i++)
        {
            if (i == 60 * 5)
            {
                host += 6_000_000;
                jumpedAt = local;
            }

            host += Interval;
            local += Interval;
            clock.OnSnapshot(host, local + 20_000);
            render = clock.Advance(local);
            if (i >= 60 * 5 && local - jumpedAt < PlayoutClock.ReanchorAfter)
            {
                Assert.True(render < host - 5_000_000, "a second of outliers isn't believed yet");
            }
        }

        Assert.Equal(1, clock.Reanchors);
        Assert.InRange(clock.Outliers, PlayoutClock.ReanchorCount, 61);
        Assert.Equal(host, clock.NewestHostTime);
        Assert.InRange(host - render, 0, 100_000); // playing just behind the host again
    }

    [Fact]
    public void A_wrong_first_stamp_is_given_up_after_a_second_of_true_ones()
    {
        // Regression case: an unchecked first stamp far ahead used to pin render time there for ever.
        var clock = new PlayoutClock();
        long local = 1_000_000_000;
        long host = 0;
        clock.OnSnapshot(SnapshotCodec.MaxHostTimeMicros, local);
        long render = 0;
        for (int i = 0; i < 60 * 20; i++)
        {
            host += Interval;
            local += Interval;
            clock.OnSnapshot(host, local + 20_000);
            render = clock.Advance(local);
        }

        Assert.Equal(1, clock.Reanchors);
        Assert.Equal(host, clock.NewestHostTime);
        Assert.InRange(host - render, 0, 100_000);
        Assert.InRange(clock.Rate, 1 - PlayoutClock.MaxWarp, 1 + PlayoutClock.MaxWarp);
    }

    [Fact]
    public void Outliers_between_good_snapshots_or_for_under_a_second_are_only_ignored()
    {
        var clock = new PlayoutClock();
        long local = 0;
        long host = 0;
        for (int i = 0; i < 60 * 20; i++)
        {
            host += Interval;
            local += Interval;
            bool bad = (i >= 300 && i < 330) || (i > 600 && i % 3 == 0); // half a second of them, then one in three
            long stamp = bad ? (i % 2 == 0 ? host + 9_000_000 : Math.Max(0, host - 9_000_000)) : host;
            Assert.Equal(!bad, clock.OnSnapshot(stamp, local + 20_000));
            clock.Advance(local);
        }

        Assert.Equal(0, clock.Reanchors);
        Assert.Equal(host, clock.NewestHostTime);
    }

    [Fact]
    public void Nothing_renders_before_the_first_snapshot()
    {
        var clock = new PlayoutClock();
        Assert.Equal(long.MinValue, clock.Advance(1_000));
        Assert.False(clock.Started);
    }

    private sealed class Result
    {
        public PlayoutClock Clock { get; set; } = null!;

        public int Frames { get; set; }

        public int Underruns { get; set; }

        public long MaxBehind { get; set; }
    }
}
