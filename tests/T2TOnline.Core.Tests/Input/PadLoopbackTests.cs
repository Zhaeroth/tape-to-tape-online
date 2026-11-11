using System.Collections.Generic;
using T2TOnline.Core.Input;

namespace T2TOnline.Core.Tests.Input;

public class PadLoopbackTests
{
    // Timestamps in microseconds; the game polls at about 60 Hz.
    private const long Second = 1_000_000;
    private const long Frame60 = Second / 60;

    [Fact]
    public void Hold_time_survives_the_round_trip_within_one_frame()
    {
        var loop = new PadLoopback(Second);
        var input = new HoldTracker();
        var output = new HoldTracker();
        var pressed = new List<CompletedHold>();
        var played = new List<CompletedHold>();

        for (long t = 0; t <= Second; t += Frame60)
        {
            PadButtons held = t is >= 100_000 and < 400_000 ? PadButtons.X : PadButtons.None;
            loop.Capture(t, held, 0, 0, 0, 0, 0, 0);
            (PadState state, PadButtons taps) = loop.Update(t);
            input.Observe(t, held, pressed);
            output.Observe(t, state.Buttons | taps, played);
        }

        CompletedHold sent = Assert.Single(pressed);
        CompletedHold got = Assert.Single(played);
        Assert.Equal(PadButtons.X, got.Button);
        Assert.InRange(got.Duration - sent.Duration, -Frame60, Frame60);
    }

    [Fact]
    public void A_tap_between_two_frames_still_presses_once()
    {
        var loop = new PadLoopback(Second);
        loop.Capture(0, PadButtons.None, 0, 0, 0, 0, 0, 0);
        loop.Update(0);

        // Down and up again before the next 120 Hz frame is due.
        loop.Capture(2_000, PadButtons.A, 0, 0, 0, 0, 0, 0);
        loop.Capture(4_000, PadButtons.None, 0, 0, 0, 0, 0, 0);
        loop.Capture(9_000, PadButtons.None, 0, 0, 0, 0, 0, 0);

        PadButtons taps = PadButtons.None;
        PadState last = default;
        for (long t = 9_000; t < 60_000; t += 1_000)
        {
            (PadState state, PadButtons tap) = loop.Update(t);
            taps |= tap;
            last = state;
        }

        Assert.Equal(PadButtons.A, taps);
        Assert.Equal(PadButtons.None, last.Buttons);
    }

    [Fact]
    public void Delay_holds_input_back_until_it_arrives()
    {
        var loop = new PadLoopback(Second, delayTicks: 50_000);
        long firstSeen = -1;
        for (long t = 0; t <= 200_000; t += 1_000)
        {
            loop.Capture(t, t >= 10_000 ? PadButtons.B : PadButtons.None, 0, 0, 0, 0, 0, 0);
            if (firstSeen < 0 && loop.Update(t).State.IsHeld(PadButtons.B))
            {
                firstSeen = t;
            }
        }

        // Pressed at 10 ms, 50 ms on the link, then a frame or two of playout margin.
        Assert.InRange(firstSeen, 60_000, 80_000);
    }

    [Fact]
    public void Axes_arrive_intact()
    {
        var loop = new PadLoopback(Second);
        for (long t = 0; t <= 100_000; t += Frame60)
        {
            loop.Capture(t, PadButtons.None, 0.5f, -1f, 0.25f, 0f, 1f, 0f);
            loop.Update(t);
        }

        PadState state = loop.Update(100_000).State;
        Assert.Equal(0.5f, state.GetAxis(PadAxis.LeftStickX), 3);
        Assert.Equal(-1f, state.GetAxis(PadAxis.LeftStickY), 3);
        Assert.Equal(0.25f, state.GetAxis(PadAxis.RightStickX), 3);
        Assert.Equal(1f, state.GetAxis(PadAxis.LeftTrigger), 3);
    }

    [Fact]
    public void Counts_what_it_sends()
    {
        var loop = new PadLoopback(Second);
        for (int i = 0; i < 60; i++)
        {
            loop.Capture(i * Frame60, PadButtons.None, 0, 0, 0, 0, 0, 0);
        }

        // Every 60 Hz poll reaches at least one new 120 Hz frame, so each sends one packet.
        Assert.Equal(60, loop.PacketsSent);
        Assert.True(loop.BytesSent > 0);
    }
}
