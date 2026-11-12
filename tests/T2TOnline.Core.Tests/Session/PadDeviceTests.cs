using System.Collections.Generic;
using System.Linq;
using T2TOnline.Core.Protocol;
using T2TOnline.Core.Serialization;
using T2TOnline.Core.Session;

namespace T2TOnline.Core.Tests.Session;

/// <summary>
/// A friend who swaps pads mid-session (Xbox to DualSense) gets the right prompts on the host, and sees which pad
/// family the host reads them as: <see cref="MessageType.PadDevice"/> both ways.
/// </summary>
public class PadDeviceTests
{
    private const ulong HostId = 100;
    private const ulong FriendId = 200;
    private const long Ms = 1000;
    private const long Second = 1_000_000;
    private const byte Xbox = 1;
    private const byte PlayStation = 2;
    private const byte Nintendo = 3;
    private static readonly SessionIdentity Identity = new(ProtocolInfo.Version, "0.7.0-rc", 0x1122334455667788);

    [Fact]
    public void The_host_knows_the_family_from_the_hello()
    {
        var h = new Harness(Xbox);
        h.JoinNow();

        Assert.Equal(Xbox, h.Host.Find(FriendId)!.DeviceType);
        Assert.Equal(Xbox, h.Friend.DeviceType);
        Assert.Equal(-1, h.Friend.HostDeviceType); // the host hasn't said yet
    }

    [Fact]
    public void A_swapped_pad_reaches_the_host_as_a_change()
    {
        var h = new Harness(Xbox);
        h.JoinNow();
        h.TakeHostEvents();

        Assert.True(h.Friend.SetPadDevice(PlayStation));
        h.Run(50 * Ms);

        HostEvent changed = Assert.Single(h.TakeHostEvents(), e => e.Kind == HostEventKind.PadDeviceChanged);
        Assert.Equal((FriendId, PlayStation), (changed.SteamId, changed.DeviceType));
        Assert.Equal(PlayStation, h.Host.Find(FriendId)!.DeviceType);
        Assert.Equal(0, h.Host.Find(FriendId)!.Link.Malformed);
    }

    [Fact]
    public void The_same_family_again_sends_nothing()
    {
        var h = new Harness(Xbox);
        h.JoinNow();
        int sent = h.Net.Sent;

        Assert.False(h.Friend.SetPadDevice(Xbox));
        Assert.Equal(sent, h.Net.Sent);
    }

    [Fact]
    public void The_host_tells_the_friend_what_it_reads_their_pad_as()
    {
        var h = new Harness(Xbox);
        h.JoinNow();
        h.TakeFriendEvents();

        Assert.True(h.Host.SendPadDevice(FriendId, Xbox));
        h.Run(50 * Ms);
        FriendEvent seen = Assert.Single(h.TakeFriendEvents(), e => e.Kind == FriendEventKind.PadDeviceSeen);
        Assert.Equal(Xbox, seen.DeviceType);
        Assert.Equal(Xbox, h.Friend.HostDeviceType);

        // Said again unchanged (a rejoin's resend): no new event.
        h.Host.SendPadDevice(FriendId, Xbox);
        h.Run(50 * Ms);
        Assert.DoesNotContain(h.TakeFriendEvents(), e => e.Kind == FriendEventKind.PadDeviceSeen);
        Assert.Equal(0, h.Friend.Link.Malformed);
    }

    [Fact]
    public void A_pad_swapped_before_the_welcome_is_sent_right_after_it()
    {
        var h = new Harness(Xbox);
        h.Friend.Start(h.Net.Now);
        h.Friend.SetPadDevice(PlayStation); // the hello on its way still says Xbox
        h.Run(20 * Ms);
        HostEvent request = Assert.Single(h.TakeHostEvents(), e => e.Kind == HostEventKind.JoinRequested);
        Assert.Equal(Xbox, request.Hello.DeviceType);

        Assert.True(h.Host.Accept(FriendId, 1, h.Net.Now));
        h.Run(100 * Ms);

        Assert.Equal(FriendSessionState.Joined, h.Friend.State);
        Assert.Contains(h.TakeHostEvents(), e => e is { Kind: HostEventKind.PadDeviceChanged, DeviceType: PlayStation });
        Assert.Equal(PlayStation, h.Host.Find(FriendId)!.DeviceType);
    }

    [Fact]
    public void Changes_are_taken_at_most_once_a_second_and_the_newest_wins()
    {
        var h = new Harness(Xbox);
        h.JoinNow();
        h.TakeHostEvents();

        h.Friend.SetPadDevice(PlayStation);
        h.Run(10 * Ms);
        h.Friend.SetPadDevice(Nintendo);
        h.Run(10 * Ms);
        h.Friend.SetPadDevice(Xbox);
        h.Friend.SetPadDevice(PlayStation);
        h.Run(100 * Ms);

        // The first at once; the rest wait out the second, and only the newest is taken.
        Assert.Equal(new[] { PlayStation }, h.TakeHostEvents().Where(e => e.Kind == HostEventKind.PadDeviceChanged).Select(e => e.DeviceType));
        h.Friend.SetPadDevice(Nintendo);
        h.Run(SessionTiming.PadDeviceIntervalMicros);

        Assert.Equal(new[] { Nintendo }, h.TakeHostEvents().Where(e => e.Kind == HostEventKind.PadDeviceChanged).Select(e => e.DeviceType));
        Assert.Equal(Nintendo, h.Host.Find(FriendId)!.DeviceType);
    }

    [Fact]
    public void A_flood_of_changes_from_one_friend_raises_at_most_one_a_second()
    {
        var h = new Harness(Xbox);
        h.JoinNow();
        h.TakeHostEvents();

        for (int i = 0; i < 300; i++)
        {
            h.Friend.SetPadDevice(i % 2 == 0 ? PlayStation : Xbox);
            h.Run(10 * Ms);
        }

        int changes = h.TakeHostEvents().Count(e => e.Kind == HostEventKind.PadDeviceChanged);
        Assert.InRange(changes, 1, 4); // three seconds of flood
    }

    [Fact]
    public void A_host_flooding_families_is_seen_at_most_once_a_second_and_the_newest_comes_after()
    {
        // Every change the host sends was an event (a log line) on the friend.
        var h = new Harness(Xbox);
        h.JoinNow();
        h.TakeFriendEvents();

        for (int i = 0; i < 50; i++)
        {
            h.Host.SendPadDevice(FriendId, i % 2 == 0 ? PlayStation : Nintendo);
            h.Run(Ms);
        }

        h.Run(20 * Ms);
        FriendEvent first = Assert.Single(h.TakeFriendEvents(), e => e.Kind == FriendEventKind.PadDeviceSeen);
        Assert.Equal(PlayStation, first.DeviceType);
        Assert.Equal(Nintendo, h.Friend.HostDeviceType); // the status line is current at once

        h.Run(SessionTiming.PadDeviceIntervalMicros);
        FriendEvent newest = Assert.Single(h.TakeFriendEvents(), e => e.Kind == FriendEventKind.PadDeviceSeen);
        Assert.Equal(Nintendo, newest.DeviceType);
        h.Run(2 * Second);
        Assert.DoesNotContain(h.TakeFriendEvents(), e => e.Kind == FriendEventKind.PadDeviceSeen);
    }

    [Fact]
    public void A_rejoin_hello_resets_the_family_and_drops_a_waiting_change()
    {
        var h = new Harness(Xbox);
        h.JoinNow();
        h.Friend.SetPadDevice(PlayStation);
        h.Run(50 * Ms);
        h.Friend.SetPadDevice(Nintendo); // waits out the second on the host
        h.Run(50 * Ms);

        // Their game restarted with an Xbox pad: its hello is what the host goes by now.
        h.Friend = h.NewFriend(Xbox);
        h.Friend.Start(h.Net.Now);
        h.Run(100 * Ms);
        h.TakeHostEvents();
        h.Run(2 * Second);

        Assert.Equal(Xbox, h.Host.Find(FriendId)!.DeviceType);
        Assert.DoesNotContain(h.TakeHostEvents(), e => e.Kind == HostEventKind.PadDeviceChanged);
    }

    [Fact]
    public void A_change_from_someone_not_joined_or_cut_short_is_counted_not_taken()
    {
        var h = new Harness(Xbox);
        h.Friend.Start(h.Net.Now);
        h.Run(20 * Ms); // waiting for the host's answer

        h.Host.Receive(FriendId, Channel.Events, Message(MessageType.PadDevice, PlayStation), h.Net.Now);
        Assert.Equal(1, h.Host.Find(FriendId)!.Link.Malformed);
        Assert.Equal(Xbox, h.Host.Find(FriendId)!.DeviceType);

        h.Host.Accept(FriendId, 1, h.Net.Now);
        h.Run(50 * Ms);
        h.Host.Receive(FriendId, Channel.Events, Message(MessageType.PadDevice), h.Net.Now);
        Assert.Equal(2, h.Host.Find(FriendId)!.Link.Malformed);
        Assert.DoesNotContain(h.TakeHostEvents(), e => e.Kind == HostEventKind.PadDeviceChanged);

        // And the friend: a family from the host before its welcome, or cut short, is malformed too.
        var waiting = new Harness(Xbox);
        waiting.Friend.Start(waiting.Net.Now);
        waiting.Friend.Receive(HostId, Channel.Events, Message(MessageType.PadDevice, Xbox), waiting.Net.Now);
        Assert.Equal(1, waiting.Friend.Link.Malformed);
        Assert.Equal(-1, waiting.Friend.HostDeviceType);
        h.Friend.Receive(HostId, Channel.Events, Message(MessageType.PadDevice), h.Net.Now);
        Assert.Equal(1, h.Friend.Link.Malformed);
    }

    [Fact]
    public void The_host_tells_only_a_joined_friend()
    {
        var h = new Harness(Xbox);
        Assert.False(h.Host.SendPadDevice(FriendId, Xbox));
        h.Friend.Start(h.Net.Now);
        h.Run(20 * Ms);

        Assert.False(h.Host.SendPadDevice(FriendId, Xbox)); // waiting, not accepted
        Assert.False(h.Host.SendPadDevice(999, Xbox));
    }

    private static byte[] Message(MessageType type, params byte[] payload)
    {
        var writer = new BitWriter();
        new MessageHeader(type, 0, 0, 0).Write(writer);
        foreach (byte b in payload)
        {
            writer.WriteByte(b);
        }

        return writer.ToArray();
    }

    /// <summary>A host and one friend on a <see cref="FakeNetwork"/>.</summary>
    private sealed class Harness
    {
        public Harness(byte deviceType)
        {
            Net = new FakeNetwork { LatencyMicros = 10 * Ms };
            Host = new HostSession(Identity, Net.For(HostId));
            Host.SetMembers(new[] { HostId, FriendId });
            Friend = NewFriend(deviceType);
        }

        public FakeNetwork Net { get; }

        public HostSession Host { get; }

        public FriendSession Friend { get; set; }

        public FriendSession NewFriend(byte deviceType) => new(Identity, HostId, InputKind.Gamepad, deviceType, Net.For(FriendId));

        public void JoinNow()
        {
            Friend.Start(Net.Now);
            for (int i = 0; i < 1000 && Friend.State == FriendSessionState.Connecting; i++)
            {
                Run(Ms);
                foreach (HostEvent e in TakeHostEvents().Where(e => e.Kind == HostEventKind.JoinRequested))
                {
                    Host.Accept(e.SteamId, 1, Net.Now);
                }
            }

            Assert.Equal(FriendSessionState.Joined, Friend.State);
            Run(50 * Ms);
        }

        public void Run(long duration)
        {
            long end = Net.Now + duration;
            while (Net.Now < end)
            {
                Net.Now += Ms;
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
