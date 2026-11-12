using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using T2TOnline.Core.Protocol;
using T2TOnline.Core.Serialization;
using T2TOnline.Core.Session;
using T2TOnline.Core.Snapshots;
using T2TOnline.Core.Stream;

namespace T2TOnline.Core.Tests.Session;

/// <summary>Reliable sends the transport refuses (a full Steam send buffer) are kept and resent, in order.</summary>
public class RetryingTransportTests
{
    private const ulong HostId = 100;
    private const ulong FriendId = 200;
    private const ulong OtherId = 300;
    private const long Ms = 1000;
    private static readonly SessionIdentity Identity = new(ProtocolInfo.Version, "0.6.0-rc", 0x1122334455667788);

    [Fact]
    public void A_refused_reliable_send_is_kept_and_goes_out_first_on_the_next_flush()
    {
        var inner = new ScriptedTransport { Refuse = (_, _, _) => true };
        var transport = new RetryingTransport(inner);

        Assert.True(transport.Send(FriendId, Channel.Events, Message(1, 100)));
        Assert.True(transport.Send(FriendId, Channel.Events, Message(2, 100)));
        Assert.True(transport.Send(FriendId, Channel.Bulk, Message(3, 1000)));
        Assert.False(transport.Send(FriendId, Channel.Realtime, Message(4, 50))); // realtime: refused is lost, and said so
        Assert.Equal(3, transport.Held);
        Assert.Equal((1200L, 3), (transport.QueuedBytes(FriendId), transport.QueuedMessages(FriendId)));
        Assert.Empty(inner.Delivered);

        // The buffer has room again, but a new message on a channel with a backlog waits behind it: order holds.
        inner.Refuse = (_, _, _) => false;
        Assert.True(transport.Send(FriendId, Channel.Events, Message(5, 100)));
        Assert.Empty(inner.Delivered);
        transport.Flush();
        Assert.Equal(new[] { 1, 2, 5 }, inner.Indices(FriendId, Channel.Events));
        Assert.Equal(new[] { 3 }, inner.Indices(FriendId, Channel.Bulk));
        Assert.Equal((0L, 0), (transport.QueuedBytes(FriendId), transport.QueuedMessages(FriendId)));
        Assert.Equal((4L, 4L), (transport.Held, transport.Resent));

        // Nothing waiting: straight through.
        Assert.True(transport.Send(FriendId, Channel.Events, Message(6, 100)));
        Assert.Equal(new[] { 1, 2, 5, 6 }, inner.Indices(FriendId, Channel.Events));
    }

    [Fact]
    public void A_backlog_holds_up_only_its_own_peer_and_channel()
    {
        var inner = new ScriptedTransport();
        var transport = new RetryingTransport(inner);
        inner.Refuse = (peer, channel, _) => peer == FriendId && channel == Channel.Events;
        Assert.True(transport.Send(FriendId, Channel.Events, Message(1, 10)));
        inner.Refuse = (_, _, _) => false;

        Assert.True(transport.Send(FriendId, Channel.Events, Message(2, 10))); // behind 1
        Assert.True(transport.Send(FriendId, Channel.Bulk, Message(3, 10)));
        Assert.True(transport.Send(FriendId, Channel.Realtime, Message(4, 10)));
        Assert.True(transport.Send(OtherId, Channel.Events, Message(5, 10)));
        Assert.Equal(new[] { 3, 4, 5 }, inner.Delivered.Select(d => Index(d.Bytes)));

        transport.Flush();
        Assert.Equal(new[] { 1, 2 }, inner.Indices(FriendId, Channel.Events));
    }

    [Fact]
    public void Kept_messages_go_out_in_order_as_the_send_buffer_drains_however_long_it_takes()
    {
        // Steam's rule: a send that would take the pending bytes over the buffer is refused. The wire takes 16 KB a frame.
        var inner = new SendBuffer(capacity: 64 * 1024);
        var transport = new RetryingTransport(inner);
        int next = 0;
        var sentEvents = new List<int>();
        var sentBulk = new List<int>();
        for (int frame = 0; frame < 400; frame++)
        {
            transport.Flush();
            if (frame < 20)
            {
                // A burst: four 10 KB checkpoints or batches and ten small messages a frame, far more than the wire takes.
                for (int i = 0; i < 4; i++)
                {
                    sentBulk.Add(next);
                    Assert.True(transport.Send(FriendId, Channel.Bulk, Message(next++, 10 * 1024)));
                }

                for (int i = 0; i < 10; i++)
                {
                    sentEvents.Add(next);
                    Assert.True(transport.Send(FriendId, Channel.Events, Message(next++, 64)));
                }
            }

            inner.Drain(16 * 1024);
        }

        Assert.Equal(sentEvents, inner.Indices(FriendId, Channel.Events));
        Assert.Equal(sentBulk, inner.Indices(FriendId, Channel.Bulk));
        Assert.True(transport.Held > 0 && transport.Held == transport.Resent, $"{transport.Held} kept, {transport.Resent} resent");
        Assert.InRange(transport.PeakQueuedBytes, 64 * 1024, RetryingTransport.DefaultMaxQueuedBytesPerPeer);
        Assert.Equal((0L, 0, 0L), (transport.QueuedBytes(FriendId), transport.Overflows, transport.Discarded));
    }

    [Fact]
    public void A_peer_that_falls_too_far_behind_is_reported_once_and_refused_until_forgotten()
    {
        var inner = new ScriptedTransport { Refuse = (peer, _, _) => peer == FriendId };
        var transport = new RetryingTransport(inner, maxQueuedBytesPerPeer: 1000);
        Assert.True(transport.Send(FriendId, Channel.Events, Message(1, 400)));
        Assert.True(transport.Send(FriendId, Channel.Bulk, Message(2, 400)));
        Assert.True(transport.Send(OtherId, Channel.Events, Message(3, 400)));

        // The third would make 1200 bytes kept: the backlog goes, and the peer is in trouble.
        Assert.False(transport.Send(FriendId, Channel.Events, Message(4, 400)));
        Assert.True(transport.HasOverflowed(FriendId));
        Assert.Equal((1, 2L, 1L), (transport.Overflows, transport.Discarded, transport.RefusedAfterOverflow));
        Assert.Equal(0L, transport.QueuedBytes(FriendId));
        Assert.True(transport.TryTakeOverflow(out ulong overflowed));
        Assert.Equal(FriendId, overflowed);
        Assert.False(transport.TryTakeOverflow(out _));

        // Every reliable send to them is refused now (their channels have a gap), even with room on the wire.
        inner.Refuse = (_, _, _) => false;
        Assert.False(transport.Send(FriendId, Channel.Events, Message(5, 10)));
        Assert.Equal(2L, transport.RefusedAfterOverflow);
        transport.Flush();
        Assert.Empty(inner.Indices(FriendId, Channel.Events));
        Assert.True(transport.Send(FriendId, Channel.Realtime, Message(6, 10))); // realtime isn't the backlog's business
        Assert.True(transport.Send(OtherId, Channel.Events, Message(7, 10))); // nor is anyone else

        // They left (the session dropped them): a fresh start if they come back.
        transport.Forget(FriendId);
        Assert.False(transport.HasOverflowed(FriendId));
        Assert.True(transport.Send(FriendId, Channel.Events, Message(8, 10)));
        Assert.Equal(new[] { 8 }, inner.Indices(FriendId, Channel.Events));
    }

    [Fact]
    public void Forget_and_clear_drop_what_was_kept()
    {
        var inner = new ScriptedTransport { Refuse = (_, _, _) => true };
        var transport = new RetryingTransport(inner);
        transport.Send(FriendId, Channel.Events, Message(1, 10));
        transport.Send(FriendId, Channel.Events, Message(2, 10));
        transport.Send(OtherId, Channel.Bulk, Message(3, 10));
        transport.Forget(FriendId);
        Assert.Equal(2L, transport.Discarded);
        transport.Clear();
        Assert.Equal(3L, transport.Discarded);

        inner.Refuse = (_, _, _) => false;
        transport.Flush();
        Assert.Empty(inner.Delivered);
        Assert.Throws<ArgumentOutOfRangeException>(() => new RetryingTransport(inner, 0));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Refused_tables_ready_achievements_and_pad_news_still_arrive_through_real_sessions(bool retrying)
    {
        // Regression case: one refusal of each of these used to lose it for good. Each is refused once here.
        var net = new FakeNetwork { LatencyMicros = 20 * Ms };
        var h = new Pair(net, retrying);
        h.JoinNow();
        h.Stream.AddFriend(FriendId);
        h.Stream.BeginMatch(new List<KeyValuePair<string, NetValue>> { new("mode", NetValue.FromInt(1)) });
        h.Run(100 * Ms);
        var objectTable = new BitWriter();
        new MessageHeader(MessageType.ObjectTable, 0, 0, 0).Write(objectTable);
        ObjectTableMessage.Write(objectTable, new[] { new ObjectEntry(5, 55, "F:H:a", "Skater") });
        h.Stream.Message(MessageType.ObjectTable, Channel.Events, objectTable.AsSpan()); // kept until the friend is ready

        var refuseOnce = new HashSet<MessageType> { MessageType.ObjectTable, MessageType.MatchReady, MessageType.AchievementUnlocked, MessageType.PadDisconnected };
        net.Refuse = (_, _, _, bytes) => refuseOnce.Remove((MessageType)bytes[0]);
        Assert.Equal(retrying, h.Client.SendReady(h.Client.SetupHash, true));
        h.Run(200 * Ms);
        for (uint sequence = 1; sequence <= 60; sequence++)
        {
            var frame = new SnapshotFrame { Sequence = sequence, HostTimeMicros = sequence * 16_667L };
            frame.Objects[5] = new[] { (byte)sequence };
            h.Stream.Snapshot(frame);
            h.Step(16_667);
        }

        h.Host.SendAchievement(FriendId, new AchievementMoment("MERCY", 1, false));
        h.Friend.SetPadConnected(false);
        h.Run(200 * Ms);

        if (retrying)
        {
            Assert.Empty(refuseOnce); // every one was refused once
            Assert.True(h.Stream.Find(FriendId)!.Streaming);
            Assert.True(h.Client.Objects.ContainsKey(5));
            Assert.True(h.Client.Receiver.Decoded > 0);
            Assert.Contains(h.FriendEvents, e => e.Kind == FriendEventKind.AchievementEarned && e.Achievement == "MERCY");
            Assert.Contains(h.HostEvents, e => e.Kind == HostEventKind.PadDisconnected);
            Assert.Equal(0, h.Stream.Find(FriendId)!.SendFailures + h.Host.Find(FriendId)!.Link.SendFailures + h.Friend.Link.SendFailures);
            Assert.True(h.HostTransport!.Resent >= 2 && h.FriendTransport!.Resent >= 2);
        }
        else
        {
            // Without it, what's refused is gone: the host never hears the friend is ready, and the rest is lost too.
            Assert.False(h.Stream.Find(FriendId)!.Streaming);
            Assert.DoesNotContain(h.FriendEvents, e => e.Kind == FriendEventKind.AchievementEarned);
            Assert.DoesNotContain(h.HostEvents, e => e.Kind == HostEventKind.PadDisconnected);
        }
    }

    private static byte[] Message(int index, int length)
    {
        byte[] bytes = new byte[Math.Max(length, 4)];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, index);
        return bytes;
    }

    private static int Index(byte[] bytes) => BinaryPrimitives.ReadInt32LittleEndian(bytes);

    /// <summary>A transport that refuses what <see cref="Refuse"/> says and records the rest, in order.</summary>
    private class ScriptedTransport : ISessionTransport
    {
        public Func<ulong, Channel, int, bool> Refuse { get; set; } = (_, _, _) => false;

        public List<(ulong Peer, Channel Channel, byte[] Bytes)> Delivered { get; } = new();

        public virtual bool Send(ulong peer, Channel channel, ReadOnlySpan<byte> message)
        {
            if (Refuse(peer, channel, message.Length))
            {
                return false;
            }

            Delivered.Add((peer, channel, message.ToArray()));
            return true;
        }

        public List<int> Indices(ulong peer, Channel channel) =>
            Delivered.Where(d => d.Peer == peer && d.Channel == channel).Select(d => Index(d.Bytes)).ToList();
    }

    /// <summary>Steam's send buffer: a send that would take what's pending over the capacity is refused.</summary>
    private sealed class SendBuffer : ScriptedTransport
    {
        private readonly int _capacity;
        private long _pending;

        public SendBuffer(int capacity) => _capacity = capacity;

        public override bool Send(ulong peer, Channel channel, ReadOnlySpan<byte> message)
        {
            if (_pending + message.Length > _capacity)
            {
                return false;
            }

            _pending += message.Length;
            return base.Send(peer, channel, message);
        }

        /// <summary>The wire took <paramref name="bytes"/>.</summary>
        public void Drain(int bytes) => _pending = Math.Max(0, _pending - bytes);
    }

    /// <summary>A host and a friend with the match stream, each over a <see cref="RetryingTransport"/> (or not), flushed every step.</summary>
    private sealed class Pair
    {
        public Pair(FakeNetwork net, bool retrying)
        {
            Net = net;
            HostTransport = retrying ? new RetryingTransport(net.For(HostId)) : null;
            FriendTransport = retrying ? new RetryingTransport(net.For(FriendId)) : null;
            ISessionTransport host = HostTransport ?? net.For(HostId);
            ISessionTransport friend = FriendTransport ?? net.For(FriendId);
            Host = new HostSession(Identity, host);
            Host.SetMembers(new[] { HostId, FriendId });
            Stream = new MatchStreamHost(host);
            Host.StreamMessages = Stream.Receive;
            Client = new MatchStreamClient(friend, HostId);
            Friend = new FriendSession(Identity, HostId, InputKind.Gamepad, 7, friend) { StreamMessages = Client.Receive };
        }

        public FakeNetwork Net { get; }

        public RetryingTransport? HostTransport { get; }

        public RetryingTransport? FriendTransport { get; }

        public HostSession Host { get; }

        public MatchStreamHost Stream { get; }

        public FriendSession Friend { get; }

        public MatchStreamClient Client { get; }

        public List<HostEvent> HostEvents { get; } = new();

        public List<FriendEvent> FriendEvents { get; } = new();

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
                Step(Ms);
            }
        }

        public void Step(long duration)
        {
            Net.Now += duration;
            HostTransport?.Flush();
            FriendTransport?.Flush();
            Net.Deliver((from, to, channel, bytes) =>
            {
                if (to == HostId)
                {
                    Host.Receive(from, channel, bytes, Net.Now);
                }
                else
                {
                    Friend.Receive(from, channel, bytes, Net.Now);
                }
            });
            Host.Update(Net.Now);
            Friend.Update(Net.Now);
            Client.Update(Net.Now);
            while (Host.TryTakeEvent(out HostEvent e))
            {
                HostEvents.Add(e);
                if (e.Kind == HostEventKind.JoinRequested)
                {
                    Host.Accept(e.SteamId, 1, Net.Now);
                }
            }

            while (Friend.TryTakeEvent(out FriendEvent e))
            {
                FriendEvents.Add(e);
            }

            while (Client.TryTakeSnapshot(out _))
            {
            }
        }
    }
}
