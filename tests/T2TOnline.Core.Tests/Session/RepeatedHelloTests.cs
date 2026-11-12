using System;
using System.Collections.Generic;
using System.Linq;
using T2TOnline.Core.Protocol;
using T2TOnline.Core.Serialization;
using T2TOnline.Core.Session;

namespace T2TOnline.Core.Tests.Session;

/// <summary>What the host does with a hello from a friend it already has: checked like a first one, and answered without flooding.</summary>
public class RepeatedHelloTests
{
    private const ulong FriendId = 200;
    private const ulong Build = 0x1122334455667788;
    private const long Second = 1_000_000;
    private static readonly SessionIdentity Identity = new(ProtocolInfo.Version, "0.6.0-rc", Build);

    [Theory]
    [InlineData((byte)3, Build, InputKind.Gamepad, RejectReason.ProtocolMismatch)]
    [InlineData(ProtocolInfo.Version, 0xBBBBUL, InputKind.Gamepad, RejectReason.GameBuildMismatch)]
    [InlineData(ProtocolInfo.Version, Build, (InputKind)2, RejectReason.UnsupportedInput)]
    public void A_joined_friend_whose_new_hello_no_longer_matches_is_refused_and_removed(byte protocol, ulong build, InputKind input, RejectReason expected)
    {
        // Regression case: a repeat hello used to be answered as a rejoin whatever it said.
        var transport = new Capture();
        HostSession host = Joined(transport);
        host.Receive(FriendId, Channel.Events, Hello(protocol, build, input), 1 * Second);

        List<HostEvent> events = Take(host);
        Assert.Equal(new[] { HostEventKind.Refused, HostEventKind.Left }, events.Select(e => e.Kind));
        Assert.Equal(expected, events[0].Reject);
        Assert.Equal(protocol, events[0].Hello.ProtocolVersion);
        Assert.Equal(LeaveReason.Removed, events[1].Reason);
        Assert.Null(host.Find(FriendId));
        (Channel channel, byte[] reject) = transport.Sent.Single();
        Assert.Equal(Channel.Events, channel);
        Assert.Equal((byte)MessageType.Reject, reject[0]);
        Assert.Equal((byte)expected, reject[^1]);
    }

    [Fact]
    public void A_waiting_friend_whose_repeated_hello_no_longer_matches_is_refused()
    {
        var transport = new Capture();
        var host = new HostSession(Identity, transport);
        host.SetMembers(new[] { FriendId });
        host.Receive(FriendId, Channel.Events, Hello(ProtocolInfo.Version, Build), 0);
        Assert.Equal(HostEventKind.JoinRequested, Take(host).Single().Kind);

        host.Receive(FriendId, Channel.Events, Hello(ProtocolInfo.Version, 0xBBBB), Second);
        HostEvent refused = Take(host).Single();
        Assert.Equal((HostEventKind.Refused, RejectReason.GameBuildMismatch), (refused.Kind, refused.Reject));
        Assert.Null(host.Find(FriendId));
        Assert.False(host.Accept(FriendId, 1, Second));
    }

    [Fact]
    public void Repeated_hellos_raise_rejoined_at_most_once_per_interval_and_are_all_answered()
    {
        // Each Rejoined makes the host send the friend everything again (run, progress, tables, menus).
        var transport = new Capture();
        HostSession host = Joined(transport);
        RemoteFriend friend = host.Find(FriendId)!;
        long now = Second;
        for (int i = 0; i < 6; i++, now += Second / 2)
        {
            host.Receive(FriendId, Channel.Events, Hello(ProtocolInfo.Version, Build), now);
        }

        Assert.Single(Take(host), e => e.Kind == HostEventKind.Rejoined);
        Assert.Equal(5, friend.RejoinsHeldBack);
        Assert.Equal(6, transport.Sent.Count(s => s.Bytes[0] == (byte)MessageType.Welcome)); // every one answered
        Assert.True(friend.IsJoined);

        // A hello after the interval (counted from the last Rejoined) is a rejoin again.
        host.Receive(FriendId, Channel.Events, Hello(ProtocolInfo.Version, Build), Second + SessionTiming.RejoinIntervalMicros - 1);
        Assert.DoesNotContain(Take(host), e => e.Kind == HostEventKind.Rejoined);
        host.Receive(FriendId, Channel.Events, Hello(ProtocolInfo.Version, Build), Second + SessionTiming.RejoinIntervalMicros);
        Assert.Single(Take(host), e => e.Kind == HostEventKind.Rejoined);
        Assert.Equal(6, friend.RejoinsHeldBack);
    }

    private static HostSession Joined(Capture transport)
    {
        var host = new HostSession(Identity, transport);
        host.SetMembers(new[] { FriendId });
        host.Receive(FriendId, Channel.Events, Hello(ProtocolInfo.Version, Build), 0);
        Assert.True(host.Accept(FriendId, 1, 0));
        Take(host);
        transport.Sent.Clear();
        return host;
    }

    private static byte[] Hello(byte protocol, ulong build, InputKind input = InputKind.Gamepad)
    {
        var writer = new BitWriter();
        new MessageHeader(MessageType.Hello, 0, 0, 0).Write(writer);
        new HelloMessage(protocol, "x", build, input, 0).Write(writer);
        return writer.ToArray();
    }

    private static List<HostEvent> Take(HostSession host)
    {
        var events = new List<HostEvent>();
        while (host.TryTakeEvent(out HostEvent e))
        {
            events.Add(e);
        }

        return events;
    }

    private sealed class Capture : ISessionTransport
    {
        public List<(Channel Channel, byte[] Bytes)> Sent { get; } = new();

        public bool Send(ulong peer, Channel channel, ReadOnlySpan<byte> message)
        {
            Sent.Add((channel, message.ToArray()));
            return true;
        }
    }
}
