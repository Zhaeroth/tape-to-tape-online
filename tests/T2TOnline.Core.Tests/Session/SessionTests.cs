using System;
using System.Collections.Generic;
using System.Linq;
using T2TOnline.Core.Input;
using T2TOnline.Core.Protocol;
using T2TOnline.Core.Serialization;
using T2TOnline.Core.Session;
using Xunit.Abstractions;

namespace T2TOnline.Core.Tests.Session;

public class SessionTests
{
    private const ulong HostId = 100;
    private const ulong FriendId = 200;
    private const ulong StrangerId = 300;
    private const long Ms = 1000;
    private const long Second = 1_000_000;
    private const long Frame240 = Second / 240;
    private static readonly SessionIdentity Identity = new(ProtocolInfo.Version, "0.2.0-m1", 0x1122334455667788);
    private readonly ITestOutputHelper _output;

    public SessionTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void A_friend_joins_after_the_host_accepts()
    {
        var h = new Harness(new FakeNetwork { LatencyMicros = 20 * Ms });
        h.Friend.Start(h.Net.Now);
        h.Run(100 * Ms);

        HostEvent request = h.TakeHostEvents().Single();
        Assert.Equal(HostEventKind.JoinRequested, request.Kind);
        Assert.Equal(FriendId, request.SteamId);
        Assert.Equal("0.2.0-m1", request.Hello.ModVersion);
        Assert.Equal(7, request.Hello.DeviceType);
        Assert.Equal(FriendSessionState.Connecting, h.Friend.State);

        Assert.True(h.Host.Accept(FriendId, 3, h.HostNow));
        h.Run(100 * Ms);

        Assert.Equal(FriendSessionState.Joined, h.Friend.State);
        Assert.Equal(3, h.Friend.Welcome.Slot);
        Assert.Contains(h.TakeFriendEvents(), e => e.Kind == FriendEventKind.Joined);
        Assert.Contains(h.TakeHostEvents(), e => e.Kind == HostEventKind.Joined);
        Assert.True(h.Host.Find(FriendId)!.IsJoined);
    }

    [Theory]
    [InlineData(true, false, RejectReason.ProtocolMismatch)]
    [InlineData(false, true, RejectReason.GameBuildMismatch)]
    public void Mismatched_versions_are_refused(bool otherProtocol, bool otherBuild, RejectReason expected)
    {
        var friendIdentity = new SessionIdentity(
            (byte)(Identity.ProtocolVersion + (otherProtocol ? 1 : 0)), "other", Identity.GameBuild + (otherBuild ? 1UL : 0UL));
        var h = new Harness(new FakeNetwork { LatencyMicros = 10 * Ms }, friendIdentity);
        h.Friend.Start(h.Net.Now);
        h.Run(100 * Ms);

        Assert.Equal(FriendSessionState.Rejected, h.Friend.State);
        Assert.Equal(expected, h.Friend.RejectReason);
        HostEvent refused = h.TakeHostEvents().Single();
        Assert.Equal(HostEventKind.Refused, refused.Kind);
        Assert.Equal(expected, refused.Reject);
        Assert.Empty(h.Host.Friends);
    }

    [Fact]
    public void A_full_session_refuses_another_friend()
    {
        var net = new FakeNetwork { LatencyMicros = 10 * Ms };
        var host = new HostSession(Identity, net.For(HostId), maxFriends: 1);
        host.SetMembers(new[] { HostId, FriendId, StrangerId });
        var first = new FriendSession(Identity, HostId, InputKind.Gamepad, 0, net.For(FriendId));
        var second = new FriendSession(Identity, HostId, InputKind.Gamepad, 0, net.For(StrangerId));
        first.Start(0);
        net.Now = 50 * Ms;
        net.Deliver((from, to, ch, bytes) => host.Receive(from, ch, bytes, net.Now));
        second.Start(net.Now);
        net.Now = 100 * Ms;
        net.Deliver((from, to, ch, bytes) =>
        {
            if (to == HostId)
            {
                host.Receive(from, ch, bytes, net.Now);
            }
            else if (to == StrangerId)
            {
                second.Receive(from, ch, bytes, net.Now);
            }
        });
        net.Now = 200 * Ms;
        net.Deliver((from, to, ch, bytes) =>
        {
            if (to == StrangerId)
            {
                second.Receive(from, ch, bytes, net.Now);
            }
        });

        Assert.Equal(FriendSessionState.Rejected, second.State);
        Assert.Equal(RejectReason.SessionFull, second.RejectReason);
    }

    [Fact]
    public void Messages_from_outside_the_lobby_are_dropped_unread()
    {
        var net = new FakeNetwork();
        var host = new HostSession(Identity, net.For(HostId));
        host.SetMembers(new[] { HostId });
        var writer = new BitWriter();
        new MessageHeader(MessageType.Hello, 0, 0, 0).Write(writer);
        new HelloMessage(Identity.ProtocolVersion, "x", Identity.GameBuild, InputKind.Gamepad, 0).Write(writer);

        host.Receive(StrangerId, Channel.Events, writer.AsSpan(), 0);

        Assert.Equal(1, host.FromStrangers);
        Assert.Empty(host.Friends);
        Assert.False(host.TryTakeEvent(out _));
        Assert.Equal(0, net.Sent);
    }

    [Fact]
    public void Hello_is_retried_until_the_host_hears_it_and_answered_once()
    {
        var h = new Harness(new FakeNetwork { LatencyMicros = 10 * Ms });
        h.Host.SetMembers(new[] { HostId }); // the friend isn't in the lobby yet, as far as the host knows
        h.Friend.Start(h.Net.Now);
        h.Run(3 * Second);
        Assert.Empty(h.Host.Friends);

        h.Host.SetMembers(new[] { HostId, FriendId });
        h.Run(3 * Second);

        Assert.Single(h.TakeHostEvents(), e => e.Kind == HostEventKind.JoinRequested);
        h.Host.Accept(FriendId, 1, h.HostNow);
        h.Run(Second);
        Assert.Equal(FriendSessionState.Joined, h.Friend.State);
    }

    [Fact]
    public void A_friend_gives_up_on_a_host_that_never_answers()
    {
        var h = new Harness(new FakeNetwork());
        h.Net.Cut = true;
        h.Friend.Start(h.Net.Now);
        h.Run(SessionTiming.HelloGiveUpMicros + Second);

        Assert.Equal(FriendSessionState.Ended, h.Friend.State);
        Assert.Equal(LeaveReason.TimedOut, h.Friend.EndReason);
    }

    [Fact]
    public void Pings_measure_the_round_trip_and_the_host_clock()
    {
        var h = new Harness(new FakeNetwork { LatencyMicros = 25 * Ms }, hostClockOffset: 5 * Second);
        h.JoinNow();
        h.Run(10 * Second);

        RemoteFriend friend = h.Host.Find(FriendId)!;
        // 50 ms on the wire, plus up to one 240 Hz step of waiting at each end.
        Assert.InRange(friend.Link.LastRoundTripMicros, 50 * Ms, 59 * Ms);
        Assert.InRange(h.Friend.Link.LastRoundTripMicros, 50 * Ms, 59 * Ms);
        Assert.True(h.Friend.Clock.HasEstimate);
        Assert.InRange(h.Friend.Clock.OffsetMicros, (5 * Second) - (5 * Ms), (5 * Second) + (5 * Ms));
    }

    [Fact]
    public void Hold_times_survive_a_jittery_lossy_link()
    {
        var h = new Harness(new FakeNetwork(seed: 7) { LatencyMicros = 30 * Ms, JitterMicros = 20 * Ms, RealtimeLossFraction = 0.05 });
        h.JoinNow();
        var random = new Random(11);
        var friendHolds = new List<CompletedHold>();
        var hostHolds = new List<CompletedHold>();
        var friendTracker = new HoldTracker();
        var hostTracker = new HoldTracker();
        PadButtons held = PadButtons.None;
        long nextChange = h.Net.Now + (100 * Ms);
        long end = h.Net.Now + (30 * Second);

        while (h.Net.Now < end)
        {
            if (h.Net.Now >= nextChange)
            {
                held = held == PadButtons.None ? PadButtons.X : PadButtons.None;
                nextChange = h.Net.Now + (held == PadButtons.None ? random.Next(60, 300) : random.Next(40, 700)) * Ms;
            }

            h.Friend.SendPad(h.Net.Now, held, 0, 0, 0, 0, 0, 0);
            friendTracker.Observe(h.Net.Now, held, friendHolds);
            (PadState state, PadButtons taps) = h.Host.Find(FriendId)!.Input!.Update(h.HostNow);
            hostTracker.Observe(h.HostNow, state.Buttons | taps, hostHolds);
            h.Step(Frame240);
        }

        // Let the last hold finish on both ends.
        for (int i = 0; i < 240; i++)
        {
            h.Friend.SendPad(h.Net.Now, PadButtons.None, 0, 0, 0, 0, 0, 0);
            friendTracker.Observe(h.Net.Now, PadButtons.None, friendHolds);
            (PadState state, _) = h.Host.Find(FriendId)!.Input!.Update(h.HostNow);
            hostTracker.Observe(h.HostNow, state.Buttons, hostHolds);
            h.Step(Frame240);
        }

        PadInputReceiver input = h.Host.Find(FriendId)!.Input!;
        Assert.Equal(friendHolds.Count, hostHolds.Count);
        Assert.Equal(friendHolds.Count, input.Holds.Count);
        double[] errorsMs = friendHolds.Zip(hostHolds, (sent, got) => Math.Abs(got.Duration - sent.Duration) / 1000.0).ToArray();
        PadPlayoutStats playout = input.Playout.Stats;
        _output.WriteLine($"{friendHolds.Count} holds; |host - friend| mean {errorsMs.Average():0.0} ms, max {errorsMs.Max():0.0} ms; " +
            $"playout changed {input.Holds.OverOneFrame} by more than a frame (|error| {input.Holds.ErrorMs.Summarize()} ms); " +
            $"playout {playout.Played} played, {playout.Missing} missing, {playout.Underruns} underruns, {playout.Skipped} skipped; delay now {input.Playout.BufferedFrames} frames.");

        // The friend's own sampling (120 Hz frames, seen at 240 Hz here) bounds what can match: within a frame plus a poll.
        Assert.True(errorsMs.Count(e => e > 12.5) <= friendHolds.Count / 20, $"{errorsMs.Count(e => e > 12.5)} of {friendHolds.Count} holds off by more than a frame and a poll");
        Assert.True(input.Holds.OverOneFrame <= input.Holds.Count / 20, $"playout changed {input.Holds.OverOneFrame} of {input.Holds.Count} holds by more than one frame");
        Assert.InRange(h.Host.Find(FriendId)!.Link.RealtimeLoss.TakeWindowLossPercent(), 2, 9);
    }

    [Fact]
    public void A_tap_between_two_frames_reaches_the_host_once()
    {
        var h = new Harness(new FakeNetwork { LatencyMicros = 10 * Ms });
        h.JoinNow();
        h.Friend.SendPad(h.Net.Now, PadButtons.None, 0, 0, 0, 0, 0, 0);
        h.Step(Frame240);

        // Down and up within a millisecond, between two 120 Hz frames.
        h.Friend.SendPad(h.Net.Now, PadButtons.A, 0, 0, 0, 0, 0, 0);
        h.Net.Now += 500;
        h.Friend.SendPad(h.Net.Now, PadButtons.None, 0, 0, 0, 0, 0, 0);

        int presses = 0;
        for (int i = 0; i < 120; i++)
        {
            h.Friend.SendPad(h.Net.Now, PadButtons.None, 0, 0, 0, 0, 0, 0);
            (PadState state, PadButtons taps) = h.Host.Find(FriendId)!.Input!.Update(h.HostNow);
            presses += (taps & PadButtons.A) != 0 || state.IsHeld(PadButtons.A) ? 1 : 0;
            h.Step(Frame240);
        }

        Assert.Equal(1, presses);
    }

    [Fact]
    public void Silence_marks_the_peer_lagging_then_gone()
    {
        var h = new Harness(new FakeNetwork { LatencyMicros = 10 * Ms });
        h.JoinNow();
        h.TakeHostEvents();
        h.TakeFriendEvents();
        h.Net.Cut = true;

        h.Run(SessionTiming.LaggingAfterMicros + Second);
        Assert.Contains(h.TakeHostEvents(), e => e.Kind == HostEventKind.Lagging);
        Assert.Contains(h.TakeFriendEvents(), e => e.Kind == FriendEventKind.Lagging);

        h.Run(SessionTiming.GoneAfterMicros);
        Assert.Contains(h.TakeHostEvents(), e => e is { Kind: HostEventKind.Left, Reason: LeaveReason.TimedOut });
        Assert.Equal(FriendSessionState.Ended, h.Friend.State);
        Assert.Equal(LeaveReason.TimedOut, h.Friend.EndReason);
        Assert.Empty(h.Host.Friends);
    }

    [Fact]
    public void A_lagging_friend_recovers_when_heard_again()
    {
        var h = new Harness(new FakeNetwork { LatencyMicros = 10 * Ms });
        h.JoinNow();
        h.TakeHostEvents();
        h.Net.Cut = true;
        h.Run(SessionTiming.LaggingAfterMicros + Second);
        h.Net.Cut = false;
        h.Run(2 * Second);

        List<HostEvent> events = h.TakeHostEvents();
        Assert.Contains(events, e => e.Kind == HostEventKind.Lagging);
        Assert.Contains(events, e => e.Kind == HostEventKind.Recovered);
        Assert.True(h.Host.Find(FriendId)!.IsJoined);
    }

    [Fact]
    public void Leaving_and_closing_tell_the_other_side()
    {
        var h = new Harness(new FakeNetwork { LatencyMicros = 10 * Ms });
        h.JoinNow();
        h.TakeHostEvents();
        h.Friend.Leave();
        h.Run(100 * Ms);
        Assert.Contains(h.TakeHostEvents(), e => e is { Kind: HostEventKind.Left, Reason: LeaveReason.Quit });

        var h2 = new Harness(new FakeNetwork { LatencyMicros = 10 * Ms });
        h2.JoinNow();
        h2.Host.Close();
        h2.Run(100 * Ms);
        Assert.Equal(FriendSessionState.Ended, h2.Friend.State);
        Assert.Equal(LeaveReason.HostClosed, h2.Friend.EndReason);
    }

    [Fact]
    public void A_friend_missing_from_the_lobby_stays_while_heard_and_goes_once_silent()
    {
        var h = new Harness(new FakeNetwork { LatencyMicros = 10 * Ms });
        h.JoinNow();
        h.TakeHostEvents();

        // A lobby poll that misses them while they're still playing: kept, and still heard.
        h.Host.SetMembers(new[] { HostId });
        h.Run(2 * SessionTiming.LaggingAfterMicros);
        Assert.True(h.Host.Find(FriendId)!.IsJoined);
        Assert.Equal(0, h.Host.FromStrangers);
        Assert.DoesNotContain(h.TakeHostEvents(), e => e.Kind == HostEventKind.Left);

        // Out of the lobby and silent past the lagging time: they left, and they're told.
        h.Net.Cut = true;
        h.Run(SessionTiming.LaggingAfterMicros + (100 * Ms));
        Assert.Empty(h.Host.Friends);
        Assert.Contains(h.TakeHostEvents(), e => e is { Kind: HostEventKind.Left, Reason: LeaveReason.LeftLobby });
        h.Net.Cut = false;
        h.Run(100 * Ms);
        Assert.Equal(FriendSessionState.Ended, h.Friend.State);
        Assert.Equal(LeaveReason.LeftLobby, h.Friend.EndReason);
    }

    [Fact]
    public void Pad_unplugging_is_reported_and_stops_the_stream()
    {
        var h = new Harness(new FakeNetwork { LatencyMicros = 10 * Ms });
        h.JoinNow();
        h.TakeHostEvents();
        h.Friend.SetPadConnected(false);
        Assert.False(h.Friend.SendPad(h.Net.Now + Second, PadButtons.A, 0, 0, 0, 0, 0, 0));
        h.Run(100 * Ms);
        Assert.Contains(h.TakeHostEvents(), e => e.Kind == HostEventKind.PadDisconnected);
        Assert.False(h.Host.Find(FriendId)!.PadConnected);

        h.Friend.SetPadConnected(true);
        h.Run(100 * Ms);
        Assert.Contains(h.TakeHostEvents(), e => e.Kind == HostEventKind.PadReconnected);
    }

    [Fact]
    public void Garbage_from_a_member_is_counted_and_never_throws()
    {
        var h = new Harness(new FakeNetwork());
        h.JoinNow();
        var random = new Random(3);
        var buffer = new byte[64];
        int rejects = 0;
        for (int i = 0; i < 20_000; i++)
        {
            int length = random.Next(0, buffer.Length);
            random.NextBytes(buffer);
            if (length > 0 && buffer[0] is (byte)MessageType.Leave or (byte)MessageType.Hello)
            {
                // A goodbye would end the session early and leave nothing joined to fuzz, and so would a hello that no longer
                // matches (it's refused and the friend removed). A reject stays: once joined it's counted, not obeyed.
                buffer[0] = (byte)MessageType.PadInput;
            }

            rejects += length > 0 && buffer[0] == (byte)MessageType.Reject ? 1 : 0;
            var channel = (Channel)random.Next(0, 3);
            h.Host.Receive(FriendId, channel, buffer.AsSpan(0, length), h.HostNow);
            h.Friend.Receive(HostId, channel, buffer.AsSpan(0, length), h.Net.Now);
        }

        Assert.True(rejects > 0);
        Assert.NotNull(h.Host.Find(FriendId));
        Assert.True(h.Host.Find(FriendId)!.Link.Malformed > 0);
        Assert.Equal(FriendSessionState.Joined, h.Friend.State);
        Assert.True(h.Friend.Link.Malformed > rejects);
    }

    [Fact]
    public void A_waiting_friend_can_be_refused_and_a_joined_one_removed_each_told_why()
    {
        var h = new Harness(new FakeNetwork { LatencyMicros = 10 * Ms });
        h.Friend.Start(h.Net.Now);
        h.Run(50 * Ms);
        Assert.Equal(HostEventKind.JoinRequested, h.TakeHostEvents().Single().Kind);
        h.Host.Refuse(FriendId, RejectReason.NoControllerSlot);
        h.Run(50 * Ms);
        HostEvent refused = h.TakeHostEvents().Single();
        Assert.Equal((HostEventKind.Refused, RejectReason.NoControllerSlot), (refused.Kind, refused.Reject));
        Assert.Empty(h.Host.Friends);
        Assert.Equal(FriendSessionState.Rejected, h.Friend.State);
        Assert.Equal(RejectReason.NoControllerSlot, h.Friend.RejectReason);

        h.Run(SessionTiming.NewHelloIntervalMicros); // a first hello is looked at once per interval
        h.Friend = h.NewFriend();
        h.JoinNow();
        h.TakeHostEvents();
        h.Host.Refuse(FriendId, RejectReason.SessionFull); // only a waiting friend can be refused
        Assert.True(h.Host.Find(FriendId)!.IsJoined);
        h.Host.Remove(FriendId, LeaveReason.Removed);
        h.Run(50 * Ms);
        Assert.Contains(h.TakeHostEvents(), e => e is { Kind: HostEventKind.Left, Reason: LeaveReason.Removed });
        Assert.Empty(h.Host.Friends);
        Assert.Equal(FriendSessionState.Ended, h.Friend.State);
        Assert.Equal(LeaveReason.Removed, h.Friend.EndReason);
    }

    [Fact]
    public void An_input_kind_the_host_doesnt_know_is_refused_as_unsupported_input()
    {
        // A keyboard and mouse is welcome (KeyboardSessionTests); a later version's kind of input isn't.
        var net = new FakeNetwork { LatencyMicros = 10 * Ms };
        var host = new HostSession(Identity, net.For(HostId));
        host.SetMembers(new[] { HostId, FriendId });
        var friend = new FriendSession(Identity, HostId, (InputKind)2, 0, net.For(FriendId));
        friend.Start(0);
        for (int i = 0; i < 10; i++)
        {
            net.Now += 10 * Ms;
            net.Deliver((from, to, channel, bytes) =>
            {
                if (to == HostId)
                {
                    host.Receive(from, channel, bytes, net.Now);
                }
                else
                {
                    friend.Receive(from, channel, bytes, net.Now);
                }
            });
        }

        Assert.True(host.TryTakeEvent(out HostEvent refused));
        Assert.Equal((HostEventKind.Refused, RejectReason.UnsupportedInput, (InputKind)2), (refused.Kind, refused.Reject, refused.Hello.Input));
        Assert.Empty(host.Friends);
        Assert.Equal(FriendSessionState.Rejected, friend.State);
        Assert.Equal(RejectReason.UnsupportedInput, friend.RejectReason);
    }

    [Fact]
    public void A_joined_friend_whose_session_restarted_rejoins_with_a_fresh_pad_stream_and_loss_count()
    {
        var h = new Harness(new FakeNetwork { LatencyMicros = 10 * Ms, RealtimeLossFraction = 0.2 });
        h.JoinNow();
        for (int i = 0; i < 240; i++)
        {
            h.Friend.SendPad(h.Net.Now, PadButtons.None, 0, 0, 0, 0, 0, 0);
            h.Step(Frame240);
        }

        h.Friend.SetPadConnected(false);
        h.Run(50 * Ms);
        h.TakeHostEvents();
        RemoteFriend friend = h.Host.Find(FriendId)!;
        PadInputReceiver oldInput = friend.Input!;
        Assert.True(friend.Link.RealtimeLoss.Expected > 100);
        Assert.False(friend.PadConnected);

        // Their game restarted the session (F7 again, say) before the host noticed anything: a new hello, sequences from 0.
        h.Net.RealtimeLossFraction = 0;
        h.Friend = h.NewFriend(deviceType: 9);
        h.Friend.Start(h.Net.Now);
        h.Run(50 * Ms);
        Assert.Equal(FriendSessionState.Joined, h.Friend.State);
        List<HostEvent> events = h.TakeHostEvents();
        HostEvent rejoined = events.Single(e => e.Kind == HostEventKind.Rejoined);
        Assert.Equal(9, rejoined.Hello.DeviceType);
        Assert.Contains(events, e => e.Kind == HostEventKind.PadReconnected); // the new session's pad is plugged in
        Assert.DoesNotContain(events, e => e.Kind is HostEventKind.JoinRequested or HostEventKind.Joined or HostEventKind.Left);
        Assert.Same(friend, h.Host.Find(FriendId));
        Assert.True(friend.IsJoined && friend.PadConnected);
        Assert.Equal(9, friend.Hello.DeviceType);
        Assert.NotSame(oldInput, friend.Input);

        for (int i = 0; i < 240; i++)
        {
            h.Friend.SendPad(h.Net.Now, PadButtons.None, 0, 0, 0, 0, 0, 0);
            h.Step(Frame240);
        }

        // Counted from the new session's first packet: no phantom gap from the old numbers.
        Assert.InRange(friend.Link.RealtimeLoss.Expected, 110, 125);
        Assert.Equal(0, friend.Link.RealtimeLoss.TakeWindowLossPercent());
    }

    [Fact]
    public void A_reject_while_joined_is_counted_not_obeyed()
    {
        var h = new Harness(new FakeNetwork { LatencyMicros = 10 * Ms });
        h.JoinNow();
        var writer = new BitWriter();
        new MessageHeader(MessageType.Reject, 0, 0, 0).Write(writer);
        new RejectMessage(RejectReason.SessionFull).Write(writer);
        long before = h.Friend.Link.Malformed;

        h.Friend.Receive(HostId, Channel.Events, writer.AsSpan(), h.Net.Now);

        Assert.Equal(FriendSessionState.Joined, h.Friend.State);
        Assert.Equal(RejectReason.None, h.Friend.RejectReason);
        Assert.Equal(before + 1, h.Friend.Link.Malformed);
        Assert.DoesNotContain(h.TakeFriendEvents(), e => e.Kind == FriendEventKind.Rejected);
    }

    [Fact]
    public void A_pad_unplugged_before_the_welcome_is_reported_right_after_it()
    {
        var h = new Harness(new FakeNetwork { LatencyMicros = 10 * Ms });
        h.Friend.SetPadConnected(false);
        h.JoinNow();
        h.Run(50 * Ms);

        Assert.False(h.Host.Find(FriendId)!.PadConnected);
        Assert.Contains(h.TakeHostEvents(), e => e.Kind == HostEventKind.PadDisconnected);
        h.Friend.SetPadConnected(true);
        h.Run(50 * Ms);
        Assert.True(h.Host.Find(FriendId)!.PadConnected);
    }

    [Fact]
    public void Round_trips_are_timed_from_our_own_pings_and_unknown_pongs_are_malformed()
    {
        var net = new FakeNetwork();
        var host = new HostSession(Identity, net.For(HostId));
        host.SetMembers(new[] { HostId, FriendId });
        var hello = new BitWriter();
        new MessageHeader(MessageType.Hello, 0, 0, 0).Write(hello);
        new HelloMessage(Identity.ProtocolVersion, "x", Identity.GameBuild, InputKind.Gamepad, 0).Write(hello);
        host.Receive(FriendId, Channel.Events, hello.AsSpan(), 1_000 * Ms);
        Assert.True(host.Accept(FriendId, 1, 1_000 * Ms));
        host.Update(1_000 * Ms); // ping 0 leaves now
        RemoteFriend friend = host.Find(FriendId)!;

        // The pong claims its ping left 5 s earlier than it did: the host times it from its own record.
        host.Receive(FriendId, Channel.Realtime, Pong(0, sentAt: (1_000 * Ms) - (5 * Second)), 1_040 * Ms);
        Assert.Equal(40 * Ms, friend.Link.LastRoundTripMicros);
        Assert.Equal(0, friend.Link.Malformed);

        // Answered already, or never sent: counted, not timed.
        host.Receive(FriendId, Channel.Realtime, Pong(0, sentAt: 1_000 * Ms), 1_090 * Ms);
        host.Receive(FriendId, Channel.Realtime, Pong(77, sentAt: 1_000 * Ms), 1_090 * Ms);
        Assert.Equal(2, friend.Link.Malformed);
        Assert.Equal(40 * Ms, friend.Link.LastRoundTripMicros);
        Assert.Equal(1, friend.Link.RoundTripMs.Count);

        static byte[] Pong(uint id, long sentAt)
        {
            var writer = new BitWriter();
            new MessageHeader(MessageType.Pong, 0, 0, 0).Write(writer);
            new PongMessage(id, sentAt, 0).Write(writer);
            return writer.ToArray();
        }
    }

    [Fact]
    public void Sequence_loss_counts_gaps_once_and_starts_a_new_run_after_a_huge_jump()
    {
        var loss = new SequenceLoss();
        Assert.Equal(0, loss.TakeWindowLossPercent());
        foreach (ulong sequence in new ulong[] { 100_010, 100_011, 100_013, 100_014, 100_012, 100_012 })
        {
            loss.Observe(sequence); // 12 arrives late, then again
        }

        Assert.Equal(5, loss.Expected);
        Assert.Equal(6, loss.Received);
        Assert.Equal(0, loss.TakeWindowLossPercent());

        loss.Observe(100_019); // 15 to 18 lost
        Assert.Equal(10, loss.Expected);
        Assert.Equal(80, loss.TakeWindowLossPercent(), 3);
        loss.Observe(100_005); // a straggler from before the first one seen widens the run: 6 to 9 lost
        Assert.Equal(15, loss.Expected);
        Assert.Equal(80, loss.TakeWindowLossPercent(), 3);

        // The sender started over (a new session), or a number is corrupt: a new run, not billions lost.
        loss.Observe(0);
        loss.Observe(1);
        loss.Observe(ulong.MaxValue);
        loss.Observe(ulong.MaxValue - 1);
        Assert.Equal(15 + 2 + 2, loss.Expected);
        Assert.Equal(0, loss.TakeWindowLossPercent());

        loss.Reset();
        Assert.Equal((0, 0), (loss.Expected, loss.Received));
        loss.Observe(SequenceLoss.NewRunAfter);
        loss.Observe(0); // a jump of exactly NewRunAfter is still the same run
        Assert.Equal((long)SequenceLoss.NewRunAfter + 1, loss.Expected);
    }

    /// <summary>A host and one friend on a <see cref="FakeNetwork"/>; the host's clock can run ahead of the friend's.</summary>
    private sealed class Harness
    {
        private readonly long _hostClockOffset;
        private readonly SessionIdentity _friendIdentity;

        public Harness(FakeNetwork net, SessionIdentity? friendIdentity = null, long hostClockOffset = 0)
        {
            Net = net;
            _hostClockOffset = hostClockOffset;
            _friendIdentity = friendIdentity ?? Identity;
            Host = new HostSession(Identity, net.For(HostId));
            Host.SetMembers(new[] { HostId, FriendId });
            Friend = NewFriend();
        }

        public FakeNetwork Net { get; }

        public HostSession Host { get; }

        /// <summary>The friend's session; replace it with <see cref="NewFriend"/> to restart it.</summary>
        public FriendSession Friend { get; set; }

        public long HostNow => Net.Now + _hostClockOffset;

        public FriendSession NewFriend(byte deviceType = 7) => new(_friendIdentity, HostId, InputKind.Gamepad, deviceType, Net.For(FriendId));

        public void JoinNow()
        {
            Friend.Start(Net.Now);
            for (int i = 0; i < 1000 && Friend.State == FriendSessionState.Connecting; i++)
            {
                Step(Ms);
                foreach (HostEvent e in TakeHostEvents().Where(e => e.Kind == HostEventKind.JoinRequested))
                {
                    Host.Accept(e.SteamId, 1, HostNow);
                }
            }

            Assert.Equal(FriendSessionState.Joined, Friend.State);
        }

        public void Run(long duration)
        {
            long end = Net.Now + duration;
            while (Net.Now < end)
            {
                Step(Frame240);
            }
        }

        public void Step(long duration)
        {
            Net.Now += duration;
            Net.Deliver((from, to, channel, bytes) =>
            {
                if (to == HostId)
                {
                    Host.Receive(from, channel, bytes, HostNow);
                }
                else if (to == FriendId)
                {
                    Friend.Receive(from, channel, bytes, Net.Now);
                }
            });
            Host.Update(HostNow);
            Friend.Update(Net.Now);
        }

        public List<HostEvent> TakeHostEvents()
        {
            var events = new List<HostEvent>();
            while (Host.TryTakeEvent(out HostEvent e))
            {
                events.Add(e);
            }

            return events;
        }

        public List<FriendEvent> TakeFriendEvents()
        {
            var events = new List<FriendEvent>();
            while (Friend.TryTakeEvent(out FriendEvent e))
            {
                events.Add(e);
            }

            return events;
        }
    }
}
