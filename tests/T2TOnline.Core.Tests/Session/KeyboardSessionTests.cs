using System;
using System.Collections.Generic;
using System.Linq;
using T2TOnline.Core.Input;
using T2TOnline.Core.Protocol;
using T2TOnline.Core.Serialization;
using T2TOnline.Core.Session;
using T2TOnline.Core.Tests.Protocol;

namespace T2TOnline.Core.Tests.Session;

/// <summary>
/// Keyboard-and-mouse friends (<see cref="MessageType.KeyboardMouseInput"/>) and every friend's own controls
/// (<see cref="MessageType.InputRemaps"/>), through real sessions on a <see cref="FakeNetwork"/>.
/// </summary>
public class KeyboardSessionTests
{
    private const ulong HostId = 100;
    private const ulong FriendId = 200;
    private const ulong SecondFriendId = 300;
    private const long Ms = 1000;
    private const long Second = 1_000_000;
    private const long Frame240 = Second / 240;
    private static readonly SessionIdentity Identity = new(ProtocolInfo.Version, "0.7.0-v1", 0x1122334455667788);

    [Fact]
    public void A_keyboard_friend_joins_and_its_controls_reach_the_host()
    {
        var h = new Harness(InputKind.KeyboardMouse);
        h.Friend.SetBindings(InputMessagesTests.KeyboardSet(5));
        h.JoinNow();
        h.Run(50 * Ms);

        RemoteFriend friend = h.Host.Find(FriendId)!;
        Assert.Equal(InputKind.KeyboardMouse, friend.Hello.Input);
        Assert.Null(friend.Input);
        Assert.NotNull(friend.Keyboard);
        Assert.Same(friend.Keyboard, friend.Received);
        Assert.Contains(h.TakeHostEvents(), e => e.Kind == HostEventKind.BindingsChanged && e.SteamId == FriendId);
        Assert.True(friend.Bindings!.SameControlsAs(h.Friend.Bindings));
        Assert.Equal((5, InputMessagesTests.KeyboardSet().Elements.Count), (friend.Keyboard!.Generation, friend.Keyboard.ElementCount));
        Assert.Equal(1, h.Friend.BindingsSent);
        Assert.Equal(0, friend.Link.Malformed);
    }

    [Fact]
    public void A_keyboard_friend_sends_no_pad_and_a_pad_friend_no_keys()
    {
        var keyboard = new Harness(InputKind.KeyboardMouse);
        keyboard.Friend.SetBindings(InputMessagesTests.KeyboardSet());
        keyboard.JoinNow();
        Assert.False(keyboard.Friend.SendPad(keyboard.Net.Now, PadButtons.A, 0, 0, 0, 0, 0, 0));
        Assert.True(keyboard.Friend.SendKeyboard(keyboard.Net.Now, 0b1, 0b1));

        var pad = new Harness(InputKind.Gamepad);
        pad.JoinNow();
        Assert.False(pad.Friend.SendKeyboard(pad.Net.Now, 0b1, 0b1));
        Assert.Throws<ArgumentException>(() => pad.Friend.SetBindings(InputMessagesTests.KeyboardSet()));
    }

    [Fact]
    public void Keys_reach_the_host_within_a_frame_over_a_jittery_lossy_link()
    {
        var h = new Harness(InputKind.KeyboardMouse, new FakeNetwork(seed: 5) { LatencyMicros = 30 * Ms, JitterMicros = 20 * Ms, RealtimeLossFraction = 0.05 });
        h.Friend.SetBindings(InputMessagesTests.KeyboardSet());
        h.JoinNow();
        h.Run(200 * Ms);
        var random = new Random(3);
        int[] keys = { 0, 2, 4, 6 };
        var next = new long[keys.Length];
        ulong held = 0;
        var friendHolds = new Dictionary<int, List<long>>();
        var hostHolds = new Dictionary<int, List<long>>();
        var friendSince = new long[64];
        var hostSince = new long[64];
        ulong hostHeld = 0;
        long end = h.Net.Now + (30 * Second);
        while (h.Net.Now < end + Second)
        {
            ulong before = held;
            for (int k = 0; k < keys.Length; k++)
            {
                if (h.Net.Now >= next[k])
                {
                    held ^= 1UL << keys[k];
                    next[k] = h.Net.Now + (random.Next(40, 600) * Ms);
                }
            }

            held = h.Net.Now >= end ? 0 : held;
            Track(before, held, h.Net.Now, friendSince, friendHolds);
            h.Friend.SendKeyboard(h.Net.Now, held, held & ~before);
            (KeyboardMouseState state, ulong taps) = h.Host.Find(FriendId)!.Keyboard!.Update(h.Net.Now);
            ulong now = state.Held | taps;
            Track(hostHeld, now, h.Net.Now, hostSince, hostHolds);
            hostHeld = now;
            h.Step(Frame240);
        }

        KeyboardMouseReceiver input = h.Host.Find(FriendId)!.Keyboard!;
        var errorsMs = new List<double>();
        foreach (int key in keys)
        {
            Assert.Equal(friendHolds[key].Count, hostHolds[key].Count);
            errorsMs.AddRange(friendHolds[key].Zip(hostHolds[key], (sent, got) => Math.Abs(got - sent) / 1000.0));
        }

        Assert.True(errorsMs.Count > 150);
        Assert.True(errorsMs.Count(e => e > 12.5) <= errorsMs.Count / 20, $"{errorsMs.Count(e => e > 12.5)} of {errorsMs.Count} holds off by more than a frame and a poll");
        Assert.True(input.Holds.OverOneFrame <= input.Holds.Count / 20);
        Assert.Equal(0, input.StaleGeneration);
        Assert.InRange(h.Host.Find(FriendId)!.Link.RealtimeLoss.TakeWindowLossPercent(), 2, 9);
        Assert.True(h.Friend.KeyboardPackets > 30 * 100);
    }

    [Fact]
    public void Controls_are_taken_at_most_once_a_second_the_newest_winning_and_frames_of_a_newer_table_wait()
    {
        var h = new Harness(InputKind.KeyboardMouse);
        h.Friend.SetBindings(InputMessagesTests.KeyboardSet(1));
        h.JoinNow();
        h.Run(50 * Ms);
        h.TakeHostEvents();

        h.Friend.SetBindings(InputMessagesTests.KeyboardSet(2));
        h.Friend.SetBindings(InputMessagesTests.KeyboardSet(3));
        for (int i = 0; i < 60; i++)
        {
            h.Friend.SendKeyboard(h.Net.Now, 0b100, 0);
            h.Step(Frame240);
        }

        // Within the second since generation 1 was taken: 3 waits, and its frames are stale meanwhile.
        RemoteFriend friend = h.Host.Find(FriendId)!;
        Assert.Equal(1, friend.Keyboard!.Generation);
        Assert.True(friend.Keyboard.StaleGeneration > 0);
        Assert.DoesNotContain(h.TakeHostEvents(), e => e.Kind == HostEventKind.BindingsChanged);

        h.Run(Second);
        Assert.Single(h.TakeHostEvents(), e => e.Kind == HostEventKind.BindingsChanged);
        Assert.Equal(3, friend.Keyboard.Generation);
        Assert.Equal(3, friend.Bindings!.Generation);
        for (int i = 0; i < 60; i++)
        {
            h.Friend.SendKeyboard(h.Net.Now, 0b100, 0);
            friend.Keyboard.Update(h.Net.Now);
            h.Step(Frame240);
        }

        Assert.True(friend.Keyboard.Current.IsHeld(2));
        Assert.Equal(0, friend.Link.Malformed);
    }

    [Fact]
    public void Bad_controls_are_counted_and_dropped()
    {
        var h = new Harness(InputKind.KeyboardMouse);
        h.Friend.SetBindings(InputMessagesTests.KeyboardSet());
        h.JoinNow();
        h.Run(2 * Second);
        h.TakeHostEvents();
        RemoteFriend friend = h.Host.Find(FriendId)!;
        InputBindingSet before = friend.Bindings!;

        // Garbage, a pad's controls from a keyboard friend, and controls on the realtime channel.
        h.Host.Receive(FriendId, Channel.Events, Message(MessageType.InputRemaps, new byte[] { 9, 9, 9 }), h.Net.Now);
        h.Run(1100 * Ms);
        h.Host.Receive(FriendId, Channel.Events, Message(MessageType.InputRemaps, InputMessagesTests.Payload(InputMessagesTests.PadSet())), h.Net.Now);
        h.Run(1100 * Ms);
        h.Host.Receive(FriendId, Channel.Realtime, Message(MessageType.InputRemaps, InputMessagesTests.Payload(InputMessagesTests.KeyboardSet())), h.Net.Now);
        h.Host.Receive(FriendId, Channel.Events, Message(MessageType.InputRemaps, new byte[InputRemapsMessage.MaxPayloadBytes + 1]), h.Net.Now);

        Assert.Equal(4, friend.BadRemaps);
        Assert.Equal(4, friend.Link.Malformed);
        Assert.Same(before, friend.Bindings);
        Assert.DoesNotContain(h.TakeHostEvents(), e => e.Kind == HostEventKind.BindingsChanged);
    }

    [Fact]
    public void A_pad_friend_sends_its_controls_too()
    {
        var h = new Harness(InputKind.Gamepad);
        h.Friend.SetBindings(InputMessagesTests.PadSet());
        h.JoinNow();
        h.Run(50 * Ms);

        RemoteFriend friend = h.Host.Find(FriendId)!;
        Assert.Contains(h.TakeHostEvents(), e => e.Kind == HostEventKind.BindingsChanged);
        Assert.Equal(InputKind.Gamepad, friend.Bindings!.Device);
        Assert.Equal(InputMessagesTests.PadSet().Bindings, friend.Bindings.Bindings);
        Assert.Null(friend.Keyboard);
        Assert.Same(friend.Input, friend.Received);
    }

    [Fact]
    public void A_repeated_welcome_sends_the_controls_again()
    {
        var h = new Harness(InputKind.KeyboardMouse);
        h.Friend.SetBindings(InputMessagesTests.KeyboardSet());
        h.JoinNow();
        h.Run(2 * Second);
        h.TakeHostEvents();

        // A hello retry that crossed the welcome: the host answers it again and starts their controls over.
        h.Host.Receive(FriendId, Channel.Events, Hello(InputKind.KeyboardMouse), h.Net.Now);
        Assert.Null(h.Host.Find(FriendId)!.Bindings);
        h.Run(100 * Ms);

        Assert.Equal(2, h.Friend.BindingsSent);
        Assert.NotNull(h.Host.Find(FriendId)!.Bindings);
        Assert.Contains(h.TakeHostEvents(), e => e.Kind == HostEventKind.BindingsChanged);
    }

    [Fact]
    public void A_friend_back_with_the_other_kind_of_input_rejoins_with_that_stream()
    {
        var h = new Harness(InputKind.Gamepad);
        h.JoinNow();
        h.Run(50 * Ms);
        h.TakeHostEvents();
        Assert.NotNull(h.Host.Find(FriendId)!.Input);

        // Their game restarted with the keyboard: a new session says hello.
        h.Friend = h.NewFriend(InputKind.KeyboardMouse);
        h.Friend.SetBindings(InputMessagesTests.KeyboardSet());
        h.JoinNow();
        h.Run(50 * Ms);

        List<HostEvent> events = h.TakeHostEvents();
        HostEvent rejoined = Assert.Single(events, e => e.Kind == HostEventKind.Rejoined);
        Assert.Equal(InputKind.KeyboardMouse, rejoined.Hello.Input);
        Assert.Contains(events, e => e.Kind == HostEventKind.BindingsChanged);
        RemoteFriend friend = h.Host.Find(FriendId)!;
        Assert.Null(friend.Input);
        Assert.NotNull(friend.Keyboard);
        Assert.Equal(InputKind.KeyboardMouse, friend.Bindings!.Device);
    }

    [Fact]
    public void A_pad_friend_and_a_keyboard_friend_play_side_by_side()
    {
        var net = new FakeNetwork { LatencyMicros = 15 * Ms };
        var host = new HostSession(Identity, net.For(HostId));
        host.SetMembers(new[] { HostId, FriendId, SecondFriendId });
        var pad = new FriendSession(Identity, HostId, InputKind.Gamepad, 1, net.For(FriendId));
        var keyboard = new FriendSession(Identity, HostId, InputKind.KeyboardMouse, 0, net.For(SecondFriendId));
        keyboard.SetBindings(InputMessagesTests.KeyboardSet());
        pad.Start(0);
        keyboard.Start(0);
        byte slot = 1;
        for (int i = 0; i < 480; i++)
        {
            net.Now += Frame240;
            net.Deliver((from, to, channel, bytes) =>
            {
                if (to == HostId)
                {
                    host.Receive(from, channel, bytes, net.Now);
                }
                else
                {
                    (to == FriendId ? pad : keyboard).Receive(from, channel, bytes, net.Now);
                }
            });
            while (host.TryTakeEvent(out HostEvent e))
            {
                if (e.Kind == HostEventKind.JoinRequested)
                {
                    Assert.True(host.Accept(e.SteamId, slot++, net.Now));
                }
            }

            host.Update(net.Now);
            pad.Update(net.Now);
            keyboard.Update(net.Now);
            pad.SendPad(net.Now, PadButtons.X, 0, 0, 0, 0, 0, 0);
            keyboard.SendKeyboard(net.Now, 0b1000, 0);
            host.Find(FriendId)?.Input?.Update(net.Now);
            host.Find(SecondFriendId)?.Keyboard?.Update(net.Now);
        }

        Assert.True(host.Find(FriendId)!.Input!.Current.IsHeld(PadButtons.X));
        Assert.True(host.Find(SecondFriendId)!.Keyboard!.Current.IsHeld(3));
        Assert.Null(host.Find(FriendId)!.Keyboard);
        Assert.Null(host.Find(SecondFriendId)!.Input);
        Assert.Equal(0, host.Find(FriendId)!.Link.Malformed + host.Find(SecondFriendId)!.Link.Malformed);
    }

    [Fact]
    public void Keyboard_frames_from_a_pad_friend_or_a_stranger_are_not_read()
    {
        var h = new Harness(InputKind.Gamepad);
        h.JoinNow();
        var writer = new BitWriter();
        var sender = new KeyboardMouseSender(1_000_000, 1, 8);
        sender.Capture(0, 1, 1);
        sender.WritePacket(writer);
        h.Host.Receive(FriendId, Channel.Realtime, Message(MessageType.KeyboardMouseInput, writer.ToArray()), h.Net.Now);
        Assert.Equal(1, h.Host.Find(FriendId)!.Link.Malformed);

        h.Host.Receive(999, Channel.Realtime, Message(MessageType.KeyboardMouseInput, writer.ToArray()), h.Net.Now);
        Assert.Equal(1, h.Host.FromStrangers);
    }

    private static void Track(ulong before, ulong after, long now, long[] since, Dictionary<int, List<long>> holds)
    {
        ulong changed = before ^ after;
        for (int i = 0; i < 64; i++)
        {
            if ((changed & (1UL << i)) == 0)
            {
                continue;
            }

            if ((after & (1UL << i)) != 0)
            {
                since[i] = now;
            }
            else
            {
                if (!holds.TryGetValue(i, out List<long>? list))
                {
                    holds[i] = list = new List<long>();
                }

                list.Add(now - since[i]);
            }
        }
    }

    private static byte[] Message(MessageType type, byte[] payload)
    {
        var writer = new BitWriter();
        new MessageHeader(type, 77, 0, 0).Write(writer);
        writer.WriteBytes(payload);
        return writer.ToArray();
    }

    private static byte[] Hello(InputKind input)
    {
        var writer = new BitWriter();
        new MessageHeader(MessageType.Hello, 50, 0, 0).Write(writer);
        new HelloMessage(Identity.ProtocolVersion, Identity.ModVersion, Identity.GameBuild, input, 0).Write(writer);
        return writer.ToArray();
    }

    /// <summary>A host and one friend on a <see cref="FakeNetwork"/>.</summary>
    private sealed class Harness
    {
        private readonly List<HostEvent> _kept = new();

        public Harness(InputKind input, FakeNetwork? net = null)
        {
            Net = net ?? new FakeNetwork { LatencyMicros = 10 * Ms };
            Host = new HostSession(Identity, Net.For(HostId));
            Host.SetMembers(new[] { HostId, FriendId });
            Friend = NewFriend(input);
        }

        public FakeNetwork Net { get; }

        public HostSession Host { get; }

        public FriendSession Friend { get; set; }

        public FriendSession NewFriend(InputKind input) => new(Identity, HostId, input, 0, Net.For(FriendId));

        public void JoinNow()
        {
            Friend.Start(Net.Now);
            for (int i = 0; i < 1000 && Friend.State == FriendSessionState.Connecting; i++)
            {
                Step(Ms);
                while (Host.TryTakeEvent(out HostEvent e))
                {
                    if (e.Kind == HostEventKind.JoinRequested)
                    {
                        Host.Accept(e.SteamId, 1, Net.Now);
                    }
                    else
                    {
                        _kept.Add(e); // for TakeHostEvents
                    }
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
                    Host.Receive(from, channel, bytes, Net.Now);
                }
                else if (to == FriendId)
                {
                    Friend.Receive(from, channel, bytes, Net.Now);
                }
            });
            Host.Update(Net.Now);
            Friend.Update(Net.Now);
        }

        public List<HostEvent> TakeHostEvents()
        {
            var events = new List<HostEvent>(_kept);
            _kept.Clear();
            while (Host.TryTakeEvent(out HostEvent e))
            {
                events.Add(e);
            }

            return events;
        }
    }
}
