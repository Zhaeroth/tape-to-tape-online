using System.Collections.Generic;
using System.Linq;
using T2TOnline.Core.Input;
using T2TOnline.Core.Protocol;
using T2TOnline.Core.Serialization;
using T2TOnline.Core.Session;
using T2TOnline.Core.Tests.Protocol;

namespace T2TOnline.Core.Tests.Session;

/// <summary>
/// A friend's pad (or keyboard) away from the session and why (<see cref="MessageType.PadDisconnected"/>'s reason byte), and
/// their controls changed while joined (the remapper while following), through real sessions on a <see cref="FakeNetwork"/>.
/// </summary>
public class PadAwayTests
{
    private const ulong HostId = 100;
    private const ulong FriendId = 200;
    private const long Ms = 1000;
    private const long Second = 1_000_000;
    private const long Frame240 = Second / 240;
    private static readonly SessionIdentity Identity = new(ProtocolInfo.Version, "0.8.0-rc", 0x1122334455667788);

    [Fact]
    public void A_friend_changing_controls_tells_the_host_why_and_when_they_are_back()
    {
        var h = new Harness(InputKind.Gamepad);
        h.JoinNow();
        h.TakeHostEvents();

        h.Friend.SetPadConnected(false, PadAwayReason.ChangingControls);
        Assert.False(h.Friend.PadConnected);
        Assert.Equal(PadAwayReason.ChangingControls, h.Friend.AwayReason);
        Assert.False(h.Friend.SendPad(h.Net.Now + Second, PadButtons.A, 0, 0, 0, 0, 0, 0));
        h.Run(50 * Ms);

        HostEvent away = Assert.Single(h.TakeHostEvents(), e => e.Kind == HostEventKind.PadDisconnected);
        Assert.Equal(PadAwayReason.ChangingControls, away.Away);
        RemoteFriend friend = h.Host.Find(FriendId)!;
        Assert.False(friend.PadConnected);
        Assert.Equal(PadAwayReason.ChangingControls, friend.AwayReason);

        h.Friend.SetPadConnected(true);
        Assert.Equal(PadAwayReason.Unplugged, h.Friend.AwayReason);
        h.Run(50 * Ms);
        HostEvent back = Assert.Single(h.TakeHostEvents(), e => e.Kind == HostEventKind.PadReconnected);
        Assert.Equal(PadAwayReason.ChangingControls, back.Away); // why they had been away
        Assert.True(friend.PadConnected);
        Assert.Equal(PadAwayReason.Unplugged, friend.AwayReason);
        Assert.Equal(0, friend.Link.Malformed);
    }

    [Fact]
    public void Another_reason_while_away_is_raised_again_and_the_same_one_is_not()
    {
        var h = new Harness(InputKind.Gamepad);
        h.JoinNow();
        h.TakeHostEvents();

        h.Friend.SetPadConnected(false, PadAwayReason.InSettings);
        h.Friend.SetPadConnected(false, PadAwayReason.InSettings); // nothing new: not sent
        h.Run(50 * Ms);
        Assert.Single(h.TakeHostEvents(), e => e is { Kind: HostEventKind.PadDisconnected, Away: PadAwayReason.InSettings });

        h.Friend.SetPadConnected(false, PadAwayReason.ChangingControls); // the remapper opened from their settings
        h.Run(50 * Ms);
        Assert.Single(h.TakeHostEvents(), e => e is { Kind: HostEventKind.PadDisconnected, Away: PadAwayReason.ChangingControls });

        // A repeat of the same state from the wire (a resend) changes nothing and isn't malformed.
        h.Host.Receive(FriendId, Channel.Events, Message(MessageType.PadDisconnected, new[] { (byte)PadAwayReason.ChangingControls }), h.Net.Now);
        Assert.Empty(h.TakeHostEvents());
        Assert.Equal(0, h.Host.Find(FriendId)!.Link.Malformed);
    }

    [Fact]
    public void A_message_with_no_reason_or_an_unknown_one_reads_as_unplugged()
    {
        var h = new Harness(InputKind.Gamepad);
        h.JoinNow();
        h.TakeHostEvents();

        h.Host.Receive(FriendId, Channel.Events, Message(MessageType.PadDisconnected, System.Array.Empty<byte>()), h.Net.Now);
        Assert.Single(h.TakeHostEvents(), e => e is { Kind: HostEventKind.PadDisconnected, Away: PadAwayReason.Unplugged });

        h.Host.Receive(FriendId, Channel.Events, Message(MessageType.PadReconnected, System.Array.Empty<byte>()), h.Net.Now);
        h.Host.Receive(FriendId, Channel.Events, Message(MessageType.PadDisconnected, new byte[] { 200 }), h.Net.Now);
        List<HostEvent> events = h.TakeHostEvents();
        Assert.Contains(events, e => e.Kind == HostEventKind.PadReconnected);
        Assert.Single(events, e => e is { Kind: HostEventKind.PadDisconnected, Away: PadAwayReason.Unplugged });
        Assert.Equal(0, h.Host.Find(FriendId)!.Link.Malformed);
    }

    [Fact]
    public void A_keyboard_away_sends_no_keys_and_its_keys_count_once_let_go_after()
    {
        var h = new Harness(InputKind.KeyboardMouse);
        h.Friend.SetBindings(InputMessagesTests.KeyboardSet(1));
        h.JoinNow();
        h.Run(50 * Ms);
        h.TakeHostEvents();

        h.Friend.SetPadConnected(false, PadAwayReason.ChangingControls);
        long sentBefore = h.Friend.KeyboardPackets;
        for (int i = 0; i < 30; i++)
        {
            Assert.False(h.Friend.SendKeyboard(h.Net.Now, 0b1, 0b1));
            h.Step(Frame240);
        }

        Assert.Equal(sentBefore, h.Friend.KeyboardPackets);
        Assert.Contains(h.TakeHostEvents(), e => e is { Kind: HostEventKind.PadDisconnected, Away: PadAwayReason.ChangingControls });

        h.Friend.SetPadConnected(true);
        for (int i = 0; i < 10; i++)
        {
            h.Friend.SendKeyboard(h.Net.Now, 0b1, 0);
            h.Step(Frame240);
        }

        Assert.True(h.Friend.KeyboardPackets > sentBefore);
        Assert.Contains(h.TakeHostEvents(), e => e.Kind == HostEventKind.PadReconnected);
    }

    [Fact]
    public void Away_before_the_welcome_is_reported_with_its_reason_right_after_it()
    {
        var h = new Harness(InputKind.Gamepad);
        h.Friend.SetPadConnected(false, PadAwayReason.InSettings);
        h.JoinNow();
        h.Run(50 * Ms);

        Assert.Contains(h.TakeHostEvents(), e => e is { Kind: HostEventKind.PadDisconnected, Away: PadAwayReason.InSettings });
        Assert.Equal(PadAwayReason.InSettings, h.Host.Find(FriendId)!.AwayReason);
    }

    [Fact]
    public void A_restarted_session_still_away_says_so_again_after_the_host_took_it_as_back()
    {
        var h = new Harness(InputKind.Gamepad);
        h.JoinNow();
        h.Friend.SetPadConnected(false, PadAwayReason.ChangingControls);
        h.Run(50 * Ms);
        h.TakeHostEvents();

        // The friend's session restarts (a drop) while their remapper is still open: the new session is told at once.
        h.Friend = h.NewFriend(InputKind.Gamepad);
        h.Friend.SetPadConnected(false, PadAwayReason.ChangingControls);
        h.Friend.Start(h.Net.Now);
        h.Run(200 * Ms);

        List<HostEvent> events = h.TakeHostEvents();
        int back = events.FindIndex(e => e.Kind == HostEventKind.PadReconnected);
        int away = events.FindLastIndex(e => e is { Kind: HostEventKind.PadDisconnected, Away: PadAwayReason.ChangingControls });
        Assert.True(back >= 0 && away > back, string.Join(", ", events.Select(e => e.Kind)));
        Assert.False(h.Host.Find(FriendId)!.PadConnected);
    }

    [Fact]
    public void Controls_changed_while_joined_reach_the_host_with_their_new_generation()
    {
        var h = new Harness(InputKind.Gamepad);
        h.Friend.SetBindings(InputMessagesTests.PadSet(3));
        h.JoinNow();
        h.Run(50 * Ms);
        h.TakeHostEvents();
        RemoteFriend friend = h.Host.Find(FriendId)!;
        Assert.Equal(3, friend.Bindings!.Generation);

        // Back from the remapper: the defaults again (generation 4), then a remap (5) within the same second.
        h.Friend.SetBindings(InputBindingSet.PadDefaults(4));
        h.Friend.SetBindings(InputMessagesTests.PadSet(5));
        h.Run(50 * Ms);
        Assert.DoesNotContain(h.TakeHostEvents(), e => e.Kind == HostEventKind.BindingsChanged); // once a second at most
        h.Run(Second);
        Assert.Single(h.TakeHostEvents(), e => e.Kind == HostEventKind.BindingsChanged);
        Assert.Equal(5, friend.Bindings.Generation); // the newest won
        Assert.Equal(3, h.Friend.BindingsSent);
    }

    private static byte[] Message(MessageType type, byte[] payload)
    {
        var writer = new BitWriter();
        new MessageHeader(type, 77, 0, 0).Write(writer);
        writer.WriteBytes(payload);
        return writer.ToArray();
    }

    /// <summary>A host and one friend on a <see cref="FakeNetwork"/>.</summary>
    private sealed class Harness
    {
        private readonly List<HostEvent> _kept = new();

        public Harness(InputKind input)
        {
            Net = new FakeNetwork { LatencyMicros = 10 * Ms };
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
            KeepHostEvents();
        }

        /// <summary>Every host event since the last call (a join request is accepted, not returned).</summary>
        public List<HostEvent> TakeHostEvents()
        {
            KeepHostEvents();
            var events = new List<HostEvent>(_kept);
            _kept.Clear();
            return events;
        }

        private void KeepHostEvents()
        {
            while (Host.TryTakeEvent(out HostEvent e))
            {
                if (e.Kind == HostEventKind.JoinRequested)
                {
                    Host.Accept(e.SteamId, 1, Net.Now);
                }
                else
                {
                    _kept.Add(e);
                }
            }
        }
    }
}
