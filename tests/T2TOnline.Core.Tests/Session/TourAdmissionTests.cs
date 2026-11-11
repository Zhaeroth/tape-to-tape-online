using System;
using System.Collections.Generic;
using System.Linq;
using T2TOnline.Core.Lobby;
using T2TOnline.Core.Protocol;
using T2TOnline.Core.Serialization;
using T2TOnline.Core.Session;

namespace T2TOnline.Core.Tests.Session;

/// <summary>While the check tour runs, its host lets in only the one-PC stand-in (this same account): anyone else is refused.</summary>
public class TourAdmissionTests
{
    private const ulong SelfId = 76561199000000001;
    private const ulong FriendId = 76561199000000002;
    private const ulong StrangerId = 76561199000000003;
    private const ulong Build = 0x1122334455667788;
    private const long Second = 1_000_000;
    private static readonly SessionIdentity Identity = new(ProtocolInfo.Version, "0.12.0-beta", Build);

    [Fact]
    public void During_a_tour_a_friends_hello_is_refused_and_nobody_waits_for_a_seat()
    {
        var transport = new Capture();
        var host = new HostSession(Identity, transport) { AdmitOnly = SelfId };
        host.SetMembers(new[] { SelfId, FriendId });

        host.Receive(FriendId, Channel.Events, Hello(), 0);

        HostEvent refused = Take(host).Single();
        Assert.Equal((HostEventKind.Refused, RejectReason.HostUnavailable, FriendId), (refused.Kind, refused.Reject, refused.SteamId));
        Assert.Null(host.Find(FriendId));
        Assert.False(host.Accept(FriendId, 1, 0));
        (ulong peer, byte[] reject) = transport.Sent.Single();
        Assert.Equal(FriendId, peer);
        Assert.Equal((byte)MessageType.Reject, reject[0]);
        Assert.Equal((byte)RejectReason.HostUnavailable, reject[^1]);
    }

    [Fact]
    public void The_tours_refusal_comes_before_any_other_so_it_is_always_the_tours()
    {
        var host = new HostSession(Identity, new Capture(), maxFriends: 1) { AdmitOnly = SelfId };
        host.SetMembers(new[] { SelfId, FriendId, StrangerId });
        host.Receive(StrangerId, Channel.Events, Hello(protocol: 3), 0);
        Assert.Equal(RejectReason.HostUnavailable, Take(host).Single().Reject);
    }

    [Fact]
    public void During_a_tour_the_one_pc_stand_in_is_let_in()
    {
        var host = new HostSession(Identity, new Capture()) { AdmitOnly = SelfId };
        host.SetMembers(new[] { SelfId });

        host.Receive(SelfId, Channel.Events, Hello(), 0);

        Assert.Equal(HostEventKind.JoinRequested, Take(host).Single().Kind);
        Assert.True(host.Accept(SelfId, 1, 0));
        Assert.Equal(HostEventKind.Joined, Take(host).Single().Kind);
    }

    [Fact]
    public void With_no_tour_everyone_the_other_rules_allow_is_let_in()
    {
        var host = new HostSession(Identity, new Capture());
        Assert.Equal(0UL, host.AdmitOnly);
        host.SetMembers(new[] { SelfId, FriendId });
        host.Receive(FriendId, Channel.Events, Hello(), 0);
        Assert.Equal(HostEventKind.JoinRequested, Take(host).Single().Kind);
    }

    [Fact]
    public void A_joined_friend_who_isnt_the_stand_in_is_refused_and_dropped_at_their_next_hello()
    {
        var transport = new Capture();
        var host = new HostSession(Identity, transport);
        host.SetMembers(new[] { SelfId, FriendId });
        host.Receive(FriendId, Channel.Events, Hello(), 0);
        Assert.True(host.Accept(FriendId, 1, 0));
        Take(host);

        host.AdmitOnly = SelfId;
        host.Receive(FriendId, Channel.Events, Hello(), Second);

        List<HostEvent> events = Take(host);
        Assert.Equal(new[] { HostEventKind.Refused, HostEventKind.Left }, events.Select(e => e.Kind));
        Assert.Equal(RejectReason.HostUnavailable, events[0].Reject);
        Assert.Null(host.Find(FriendId));
    }

    [Theory]
    [InlineData(false, 7UL, 0UL)]
    [InlineData(true, 7UL, 7UL)]
    [InlineData(true, 0UL, ulong.MaxValue)]
    public void The_host_admits_only_this_account_while_a_tour_runs_and_nobody_when_it_isnt_known(bool tour, ulong self, ulong admitOnly)
    {
        Assert.Equal(admitOnly, UiCheckHolds.AdmitOnly(tour, self));
    }

    [Theory]
    [InlineData(false, 5UL, 9UL, false)]
    [InlineData(true, 5UL, 9UL, true)]
    [InlineData(true, 9UL, 9UL, false)]
    [InlineData(true, 0UL, 0UL, true)]
    public void During_a_tour_only_its_own_lobby_that_isnt_there_is_joined(bool tour, ulong lobby, ulong toursOwn, bool refused)
    {
        Assert.Equal(refused, UiCheckHolds.RefusesJoin(tour, lobby, toursOwn));
    }

    [Fact]
    public void During_a_tour_the_lobby_is_private_and_no_presence_is_shown()
    {
        Assert.True(UiCheckHolds.PrivateLobby(true));
        Assert.True(UiCheckHolds.HoldsPresence(true));
        Assert.False(UiCheckHolds.PrivateLobby(false));
        Assert.False(UiCheckHolds.HoldsPresence(false));
    }

    private static byte[] Hello(byte protocol = ProtocolInfo.Version)
    {
        var writer = new BitWriter();
        new MessageHeader(MessageType.Hello, 0, 0, 0).Write(writer);
        new HelloMessage(protocol, "x", Build, InputKind.Gamepad, 0).Write(writer);
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
        public List<(ulong Peer, byte[] Bytes)> Sent { get; } = new();

        public bool Send(ulong peer, Channel channel, ReadOnlySpan<byte> message)
        {
            Sent.Add((peer, message.ToArray()));
            return true;
        }
    }
}
