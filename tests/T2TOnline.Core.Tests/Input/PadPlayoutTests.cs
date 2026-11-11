using System;
using System.Collections.Generic;
using T2TOnline.Core.Input;

namespace T2TOnline.Core.Tests.Input;

public class PadPlayoutTests
{
    /// <summary>
    /// Simulates a friend sending one packet per tick (the newest frame plus up to 7 before it) and the host
    /// calling <see cref="InputPlayout{T}.Next"/> once per tick. <paramref name="arrivalDelay"/> decides, per sent packet,
    /// how many ticks later it arrives; null drops it.
    /// </summary>
    private static List<PadState> Run(PadPlayout playout, int ticks, Func<int, int?> arrivalDelay, Func<int, PadState>? frameAt = null)
    {
        frameAt ??= Numbered;
        var inFlight = new SortedDictionary<int, List<(uint Newest, PadState[] Frames)>>();
        var output = new List<PadState>();
        for (int tick = 0; tick < ticks; tick++)
        {
            int count = Math.Min(PadInputCodec.MaxFramesPerPacket, tick + 1);
            var frames = new PadState[count];
            for (int i = 0; i < count; i++)
            {
                frames[i] = frameAt(tick - (count - 1 - i));
            }

            int? delay = arrivalDelay(tick);
            if (delay is int d)
            {
                if (!inFlight.TryGetValue(tick + d, out var list))
                {
                    inFlight[tick + d] = list = new();
                }

                list.Add(((uint)(1000 + tick), frames));
            }

            if (inFlight.Remove(tick, out var arriving))
            {
                foreach (var (newest, packet) in arriving)
                {
                    playout.Receive(newest, packet);
                }
            }

            output.Add(playout.Next());
        }

        return output;
    }

    /// <summary>A frame whose left stick X encodes its index, so tests can see which frame was played.</summary>
    private static PadState Numbered(int frameIndex) => new(PadButtons.None, 0, (short)frameIndex, 0, 0, 0, 0, 0);

    [Fact]
    public void Steady_stream_plays_every_frame_in_order()
    {
        var playout = new PadPlayout();
        List<PadState> output = Run(playout, 600, _ => 1);

        var played = new List<int>();
        foreach (PadState state in output)
        {
            played.Add(state.LeftStickX);
        }

        for (int i = 300; i < played.Count - 1; i++)
        {
            Assert.True(played[i + 1] - played[i] is 0 or 1 or 2, $"jump at {i}: {played[i]} -> {played[i + 1]}");
        }

        Assert.Equal(0, playout.Stats.Missing);
        Assert.Equal(0, playout.Stats.Underruns);
    }

    [Fact]
    public void A_straggler_from_far_behind_is_dropped_never_replayed()
    {
        // The packet sent at tick 100 turns up 60 ticks late (a route change): its frames were played long ago.
        var playout = new PadPlayout();
        List<PadState> output = Run(playout, 400, tick => tick == 100 ? 60 : 1);

        Assert.Equal(1, playout.Stats.Stragglers);
        Assert.Equal(0, playout.Stats.Resyncs);
        for (int i = 50; i < output.Count - 1; i++)
        {
            Assert.True(output[i + 1].LeftStickX >= output[i].LeftStickX, $"playout went back at {i}: {output[i].LeftStickX} -> {output[i + 1].LeftStickX}");
        }
    }

    [Fact]
    public void A_stream_that_really_moved_back_is_followed_after_a_quarter_second()
    {
        // Every packet from tick 200 on is stamped 100 frames earlier (a sender whose counter really went back).
        var playout = new PadPlayout();
        for (int tick = 0; tick < 300; tick++)
        {
            uint newest = (uint)(1000 + tick - (tick >= 200 ? 100 : 0));
            playout.Receive(newest, new[] { Numbered(tick) });
            playout.Next();
        }

        Assert.InRange(playout.Stats.Stragglers, 25, 35);
        Assert.Equal(1, playout.Stats.Resyncs);
    }

    [Fact]
    public void Steady_stream_settles_at_the_minimum_spare()
    {
        var playout = new PadPlayout(minSpareFrames: 1);
        List<PadState> output = Run(playout, 2400, _ => 2);

        Assert.Equal(1, playout.BufferedFrames);
        Assert.Equal(0, playout.Stats.Underruns);
        int lastSent = 2399;
        int lastPlayed = output[^1].LeftStickX;
        Assert.InRange(lastSent - lastPlayed, 2, 4);
    }

    [Fact]
    public void Redundancy_covers_heavy_packet_loss()
    {
        var playout = new PadPlayout();
        Run(playout, 1200, tick => tick % 4 == 0 ? 1 : null);
        Assert.Equal(0, playout.Stats.Missing);
    }

    [Fact]
    public void A_long_outage_repeats_the_last_state_then_moves_on()
    {
        var playout = new PadPlayout(staleAfterTicks: 1000);
        List<PadState> output = Run(playout, 400, tick => tick is >= 200 and < 220 ? null : 1);
        Assert.True(playout.Stats.Missing > 0 || playout.Stats.Underruns > 0);
        Assert.InRange(output[^1].LeftStickX, 390, 399);
    }

    [Fact]
    public void Jitter_raises_the_delay_until_underruns_stop()
    {
        var playout = new PadPlayout(minSpareFrames: 1);
        Run(playout, 2400, tick => 1 + (tick % 3));

        Assert.InRange(playout.BufferedFrames, 0, 4);
        Assert.True(playout.Stats.Underruns < 20, $"{playout.Stats.Underruns} underruns");
        Assert.Equal(0, playout.Stats.Missing);
    }

    [Fact]
    public void A_backlog_after_a_stall_is_cut_quickly()
    {
        var playout = new PadPlayout(maxBacklogFrames: 8);
        List<PadState> output = Run(playout, 600, tick => tick < 300 ? 1 : tick < 330 ? 330 - tick : 1);

        Assert.InRange(playout.BufferedFrames, 1, 3);
        Assert.InRange(599 - output[^1].LeftStickX, 2, 4);
        Assert.True(playout.Stats.Skipped > 0);
    }

    [Fact]
    public void Silence_goes_neutral_but_keeps_press_counters()
    {
        var playout = new PadPlayout(staleAfterTicks: 10);
        var held = new PadState(PadButtons.A, 0b01, 30000, 0, 0, 0, 0, 0);
        List<PadState> output = Run(playout, 100, tick => tick < 50 ? 1 : null, _ => held);

        Assert.True(playout.IsStale);
        PadState last = output[^1];
        Assert.Equal(PadButtons.None, last.Buttons);
        Assert.Equal(0, last.LeftStickX);
        Assert.Equal(held.PressCounters, last.PressCounters);
    }

    [Fact]
    public void A_far_jump_in_frame_numbers_resyncs()
    {
        var playout = new PadPlayout();
        playout.Receive(100, new[] { Numbered(1) });
        playout.Next();
        playout.Receive(100_000, new[] { Numbered(2) });

        Assert.Equal(1, playout.Stats.Resyncs);
        PadState state = PadState.Neutral;
        for (int i = 0; i < 5; i++)
        {
            state = playout.Next();
        }

        Assert.Equal(2, state.LeftStickX);
    }

    [Fact]
    public void Nothing_plays_before_the_first_packet()
    {
        var playout = new PadPlayout();
        Assert.Equal(PadState.Neutral, playout.Next());
        Assert.False(playout.IsStarted);
    }

    [Fact]
    public void Invalid_settings_are_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PadPlayout(minSpareFrames: 3, maxBacklogFrames: 3));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PadPlayout(maxBacklogFrames: 40));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PadPlayout(minSpareFrames: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PadPlayout(windowFrames: 0));
    }

    [Theory]
    [InlineData(0x8000_0000u, false)] // half the sequence space away reads as behind: a straggler
    [InlineData(0x7FFF_FFFFu, false)]
    [InlineData(0x8000_0001u, false)]
    [InlineData(32u, true)] // far ahead: playout restarts there
    public void A_huge_or_wrapping_frame_jump_restarts_or_is_dropped_never_throwing(uint jump, bool restarts)
    {
        var playout = new PadPlayout();
        var frames = new PadState[] { default, default };
        playout.Receive(100, frames);
        playout.Receive(unchecked(100 + jump), frames);
        playout.Receive(100, frames);

        Assert.Equal(restarts ? 1 : 0, playout.Stats.Resyncs);
        Assert.Equal(restarts ? 0 : 1, playout.Stats.Stragglers);
        Assert.True(playout.IsStarted);
        playout.Next();
    }
}
