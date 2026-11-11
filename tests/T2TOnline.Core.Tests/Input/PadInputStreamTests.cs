using T2TOnline.Core.Input;
using T2TOnline.Core.Serialization;

namespace T2TOnline.Core.Tests.Input;

public class PadInputStreamTests
{
    private const long Second = 1_000_000;
    private const long Frame = Second / PadInputSender.FrameRate;

    [Fact]
    public void The_sender_emits_one_frame_per_120_Hz_tick()
    {
        var sender = new PadInputSender(Second);
        Assert.True(sender.Capture(0, PadButtons.None, 0, 0, 0, 0, 0, 0));
        Assert.False(sender.Capture(Frame / 2, PadButtons.A, 0, 0, 0, 0, 0, 0));
        Assert.False(sender.Capture(Frame, PadButtons.A, 0, 0, 0, 0, 0, 0)); // 8,333 us: just short of 1/120 s
        Assert.True(sender.Capture(Frame + 1, PadButtons.A, 0, 0, 0, 0, 0, 0));
        Assert.Equal(2u, sender.FramesCaptured);
        Assert.True(sender.Capture((4 * Frame) + 2, PadButtons.A, 0, 0, 0, 0, 0, 0));
        Assert.Equal(5u, sender.FramesCaptured);
    }

    [Fact]
    public void After_a_stall_the_sender_skips_frames_it_could_never_send()
    {
        var sender = new PadInputSender(Second);
        sender.Capture(0, PadButtons.None, 0, 0, 0, 0, 0, 0);
        sender.Capture(Second, PadButtons.B, 0, 0, 0, 0, 0, 0);

        Assert.Equal(121u, sender.FramesCaptured);
        var writer = new BitWriter();
        sender.WritePacket(writer);
        var reader = new BitReader(writer.AsSpan());
        var frames = new PadState[PadInputCodec.MaxFramesPerPacket];
        Assert.True(PadInputCodec.TryRead(ref reader, frames, out uint newest, out int count));
        Assert.Equal(120u, newest);
        Assert.Equal(PadInputCodec.MaxFramesPerPacket, count);
        Assert.All(frames, f => Assert.True(f.IsHeld(PadButtons.B)));
    }

    [Fact]
    public void The_receiver_is_neutral_until_the_first_packet()
    {
        var receiver = new PadInputReceiver(Second);
        Assert.Equal((PadState.Neutral, PadButtons.None), receiver.Update(0));
        Assert.Equal((PadState.Neutral, PadButtons.None), receiver.Update(Second));
    }

    [Fact]
    public void Malformed_packets_are_counted_and_change_nothing()
    {
        var receiver = new PadInputReceiver(Second);
        var reader = new BitReader(new byte[] { 1, 2 });
        Assert.False(receiver.Receive(ref reader));
        Assert.Equal(1, receiver.MalformedPackets);
        Assert.False(receiver.Playout.IsStarted);
    }

    [Fact]
    public void A_long_stall_on_the_host_drops_ticks_instead_of_bursting_them()
    {
        var sender = new PadInputSender(Second);
        var receiver = new PadInputReceiver(Second);
        var writer = new BitWriter();
        sender.Capture(0, PadButtons.None, 0, 0, 0, 0, 0, 0);
        Deliver(sender, receiver, writer);
        receiver.Update(0);

        receiver.Update(Second);

        Assert.Equal(PadInputSender.FrameRate - PadInputReceiver.MaxCatchUpTicks, receiver.DroppedTicks);
    }

    [Fact]
    public void The_hold_check_sees_a_late_frame_lengthen_a_hold()
    {
        var check = new PlayoutHoldCheck();

        // Friend frames 10-13 hold X (4 frames), but playout waited a tick for frame 12: 5 ticks on the host.
        Play(check, 0, 9, PadButtons.None, (9, PadButtons.None));
        Play(check, 1, 10, PadButtons.X, (10, PadButtons.X));
        Play(check, 2, 11, PadButtons.X, (11, PadButtons.X));
        Play(check, 3, 11, PadButtons.X); // underrun: still showing frame 11
        Play(check, 4, 12, PadButtons.X, (12, PadButtons.X));
        Play(check, 5, 13, PadButtons.X, (13, PadButtons.X));
        Play(check, 6, 14, PadButtons.None, (14, PadButtons.None));

        HoldComparison hold = Assert.Single(check.Recent);
        Assert.Equal(PadButtons.X, hold.Button);
        Assert.Equal(4, hold.FriendFrames);
        Assert.Equal(5, hold.PlayedTicks);
        Assert.Equal(1, hold.ErrorFrames);
        Assert.Equal(0, check.OverOneFrame);
    }

    [Fact]
    public void A_skip_over_the_release_is_measured_from_the_friends_real_release()
    {
        var check = new PlayoutHoldCheck();

        // X held in frames 10-13; after playing frame 12, playout skips 13-16 to cut delay.
        Play(check, 0, 9, PadButtons.None, (9, PadButtons.None));
        Play(check, 1, 10, PadButtons.X, (10, PadButtons.X));
        Play(check, 2, 11, PadButtons.X, (11, PadButtons.X));
        Play(check, 3, 12, PadButtons.X, (12, PadButtons.X), (13, PadButtons.X), (14, PadButtons.None), (15, PadButtons.None), (16, PadButtons.None));
        Play(check, 4, 17, PadButtons.None, (17, PadButtons.None));

        HoldComparison hold = Assert.Single(check.Recent);
        Assert.Equal(4, hold.FriendFrames);
        Assert.Equal(3, hold.PlayedTicks);
        Assert.Equal(-1, hold.ErrorFrames);
        Assert.Equal(0, check.OverOneFrame);
    }

    [Fact]
    public void A_press_skipped_over_entirely_counts_as_lost()
    {
        var check = new PlayoutHoldCheck();
        Play(check, 0, 0, PadButtons.None, (0, PadButtons.None));
        Play(check, 1, 1, PadButtons.None, (1, PadButtons.None), (2, PadButtons.Y), (3, PadButtons.Y), (4, PadButtons.None));
        Play(check, 2, 5, PadButtons.Y, (5, PadButtons.Y));
        Play(check, 3, 6, PadButtons.None, (6, PadButtons.None));

        Assert.Equal(1, check.Lost);
        HoldComparison hold = Assert.Single(check.Recent);
        Assert.Equal(0, hold.ErrorFrames);
    }

    [Fact]
    public void A_hold_cut_by_silence_counts_once_as_cut_short()
    {
        var check = new PlayoutHoldCheck();
        Play(check, 0, 0, PadButtons.None, (0, PadButtons.None));
        Play(check, 1, 1, PadButtons.B, (1, PadButtons.B));
        Play(check, 2, 1, PadButtons.None); // the stream went quiet: neutral while the friend still holds B
        Play(check, 3, 3, PadButtons.None, (2, PadButtons.B), (3, PadButtons.None));

        Assert.Equal(1, check.CutShort);
        Assert.Equal(0, check.Lost);
        Assert.Empty(check.Recent);
    }

    private static void Play(PlayoutHoldCheck check, long tick, uint shown, PadButtons held, params (uint Frame, PadButtons Held)[] passed)
    {
        foreach ((uint frame, PadButtons friendHeld) in passed)
        {
            check.ObserveFriendFrame(frame, friendHeld);
        }

        check.ObservePlayedTick(tick, shown, held);
    }

    private static void Deliver(PadInputSender sender, PadInputReceiver receiver, BitWriter writer)
    {
        writer.Reset();
        sender.WritePacket(writer);
        var reader = new BitReader(writer.AsSpan());
        Assert.True(receiver.Receive(ref reader));
    }
}

public class HoldMatcherTests
{
    [Fact]
    public void Holds_pair_by_button_in_order()
    {
        var matcher = new HoldMatcher();
        matcher.ObserveSent(0, PadButtons.A | PadButtons.B);
        matcher.ObserveSent(100, PadButtons.B);
        matcher.ObserveSent(250, PadButtons.None);
        matcher.ObservePlayed(30, PadButtons.A | PadButtons.B);
        matcher.ObservePlayed(132, PadButtons.B);
        matcher.ObservePlayed(279, PadButtons.None);

        Assert.Equal(2, matcher.Matched.Count);
        Assert.Equal(new MatchedHold(PadButtons.A, 100, 102), matcher.Matched[0]);
        Assert.Equal(new MatchedHold(PadButtons.B, 250, 249), matcher.Matched[1]);
        Assert.Equal(-1, matcher.Matched[1].Error);
    }

    [Fact]
    public void An_output_hold_with_no_input_hold_is_not_paired()
    {
        var matcher = new HoldMatcher();
        matcher.ObservePlayed(0, PadButtons.X);
        matcher.ObservePlayed(10, PadButtons.None);

        Assert.Empty(matcher.Matched);
    }
}
