using System;
using System.Collections.Generic;
using System.Linq;
using T2TOnline.Core.Protocol;
using T2TOnline.Core.Session;
using T2TOnline.Core.Stream;

namespace T2TOnline.Core.Tests.Session;

/// <summary>
/// Custom content through the host's real reliable path: Steam takes about 512 KB before it refuses, the
/// retrying transport keeps at most 4 MB for a friend and drops a friend it can't keep up with, so a match's logos (up to
/// 4 MB each) must go out paced by what's waiting, never in one burst.
/// </summary>
public class CustomContentPacingTests
{
    private const ulong Friend = 200;
    private const ulong Other = 300;
    private const int SteamBuffer = 512 * 1024;

    [Fact]
    public void Two_largest_logos_reach_a_friend_without_overflowing_the_reliable_backlog()
    {
        var steam = new SendBuffer(SteamBuffer);
        var transport = new RetryingTransport(steam);
        var sender = new CustomContentSender(transport, transport.QueuedBytes);
        CustomItem[] items = { Logo("custom_home.png", 1), Logo("custom_away.png", 2) };

        // The first call sends only what Steam takes (and at most one piece kept behind it): nothing is complete yet.
        Assert.Equal(0, sender.Send(Friend, items));
        Assert.True(transport.QueuedBytes(Friend) <= CustomContentMessage.MaxChunkBytes + 512);
        Assert.True(sender.IsSending(Friend));

        var deliveries = new List<CustomContentDelivery>();
        for (int frame = 0; frame < 400 && (sender.IsSending(Friend) || transport.QueuedBytes(Friend) > 0); frame++)
        {
            steam.Drain(64 * 1024); // the wire takes some each frame
            transport.Flush();
            sender.Update();
            Assert.True(transport.QueuedBytes(Friend) <= CustomContentMessage.MaxChunkBytes + 512);
            while (sender.TryTakeDelivery(out CustomContentDelivery delivery))
            {
                deliveries.Add(delivery);
            }
        }

        Assert.Equal(0, transport.Overflows);
        Assert.Equal(0, transport.QueuedBytes(Friend));
        Assert.False(sender.IsSending(Friend));
        Assert.All(items, i => Assert.True(sender.HasSent(Friend, i)));
        CustomContentDelivery done = Assert.Single(deliveries);
        Assert.Equal((Friend, 2, false), (done.Friend, done.Items, done.Refused));
        Assert.True(done.Bytes > 2 * (CustomItem.MaxLogoBytes - 64));

        // Everything Steam took, in order on the bulk channel, puts both logos back together.
        var receiver = new CustomContentReceiver();
        foreach (byte[] message in steam.Delivered.Where(d => d.Peer == Friend).Select(d => d.Bytes))
        {
            Assert.True(Chunk(message, out CustomChunk chunk));
            Assert.True(receiver.Receive(chunk));
        }

        Assert.All(items, i => Assert.True(receiver.TryGet(i.Hash, out _)));
    }

    [Fact]
    public void Without_pacing_the_same_logos_overflow_the_backlog_and_the_friend_would_be_dropped()
    {
        // What the sender did before pacing: every piece at once. Kept to show why pacing is needed.
        var steam = new SendBuffer(SteamBuffer);
        var transport = new RetryingTransport(steam);
        var sender = new CustomContentSender(transport);
        Assert.Equal(1, sender.Send(Friend, new[] { Logo("custom_home.png", 1) }));
        Assert.Equal(0, sender.Send(Friend, new[] { Logo("custom_away.png", 2) }));
        Assert.Equal(1, transport.Overflows);
        Assert.True(transport.TryTakeOverflow(out ulong dropped));
        Assert.Equal(Friend, dropped);
    }

    [Fact]
    public void Each_friend_is_paced_on_its_own_and_a_rejoin_starts_over()
    {
        var steam = new SendBuffer(SteamBuffer, perPeer: true);
        var transport = new RetryingTransport(steam);
        var sender = new CustomContentSender(transport, transport.QueuedBytes);
        CustomItem logo = Logo("custom_home.png", 1);
        CustomItem team = new(CustomContentKind.Team, "team-1", new byte[300]);
        sender.Send(Friend, new[] { logo, team });
        sender.Send(Other, new[] { team });

        // The small item for the other friend isn't held up by the first friend's large one.
        Assert.True(sender.HasSent(Other, team));
        Assert.False(sender.HasSent(Friend, team));
        Assert.True(sender.TryTakeDelivery(out CustomContentDelivery other));
        Assert.Equal((Other, 1), (other.Friend, other.Items));

        // A rejoin (Forget) drops what was under way; the next send starts the items over from their first piece.
        sender.Forget(Friend);
        Assert.False(sender.IsSending(Friend));
        transport.Forget(Friend);
        steam.Clear(Friend);
        int before = steam.Delivered.Count(d => d.Peer == Friend);
        sender.Send(Friend, new[] { team });
        Assert.True(sender.HasSent(Friend, team));
        (ulong _, Channel _, byte[] first) = steam.Delivered.Where(d => d.Peer == Friend).Skip(before).First();
        Assert.True(Chunk(first, out CustomChunk chunk));
        Assert.Equal((team.Hash, 0), (chunk.Hash, chunk.Offset));
    }

    [Fact]
    public void A_refusal_drops_what_was_under_way_says_so_once_and_the_next_send_starts_over()
    {
        var capture = new Refusing();
        var sender = new CustomContentSender(capture, _ => 0);
        CustomItem logo = Logo("custom_home.png", 1);
        capture.RefuseAfter = 3; // the fourth piece is refused (the transport gave up on this friend)
        Assert.Equal(0, sender.Send(Friend, new[] { logo }));
        Assert.Equal(1, sender.Refused);
        Assert.False(sender.IsSending(Friend));
        Assert.False(sender.HasSent(Friend, logo));
        Assert.True(sender.TryTakeDelivery(out CustomContentDelivery refused));
        Assert.Equal((Friend, 0, true), (refused.Friend, refused.Items, refused.Refused));
        Assert.False(sender.TryTakeDelivery(out _));

        capture.RefuseAfter = int.MaxValue;
        Assert.Equal(1, sender.Send(Friend, new[] { logo }));
        Assert.True(sender.HasSent(Friend, logo));
        Assert.True(Chunk(capture.Sent[3], out CustomChunk restart));
        Assert.Equal(0, restart.Offset);
    }

    /// <summary>A logo of the largest size, its PNG header first.</summary>
    private static CustomItem Logo(string key, byte fill)
    {
        byte[] png = CustomContentTests.Png(256, 256, extra: CustomItem.MaxLogoBytes - 33);
        png.AsSpan(40).Fill(fill);
        return new CustomItem(CustomContentKind.Logo, key, png);
    }

    private static bool Chunk(byte[] message, out CustomChunk chunk)
    {
        var reader = new T2TOnline.Core.Serialization.BitReader(message);
        if (!MessageHeader.TryRead(ref reader, out MessageHeader header) || header.Type != MessageType.CustomContent)
        {
            chunk = default;
            return false;
        }

        return CustomContentMessage.TryRead(ref reader, out chunk);
    }

    /// <summary>Steam's send buffer: a send that would take what's pending over the capacity is refused.</summary>
    private sealed class SendBuffer : ISessionTransport
    {
        private readonly int _capacity;
        private readonly bool _perPeer;
        private readonly Dictionary<ulong, long> _pending = new();

        public SendBuffer(int capacity, bool perPeer = false)
        {
            _capacity = capacity;
            _perPeer = perPeer;
        }

        public List<(ulong Peer, Channel Channel, byte[] Bytes)> Delivered { get; } = new();

        public bool Send(ulong peer, Channel channel, ReadOnlySpan<byte> message)
        {
            ulong key = _perPeer ? peer : 0;
            long pending = _pending.GetValueOrDefault(key);
            if (pending + message.Length > _capacity)
            {
                return false;
            }

            _pending[key] = pending + message.Length;
            Delivered.Add((peer, channel, message.ToArray()));
            return true;
        }

        /// <summary>The wire took <paramref name="bytes"/> (from each peer's buffer).</summary>
        public void Drain(int bytes)
        {
            foreach (ulong key in _pending.Keys.ToList())
            {
                _pending[key] = Math.Max(0, _pending[key] - bytes);
            }
        }

        public void Clear(ulong peer) => _pending.Remove(_perPeer ? peer : 0);
    }

    private sealed class Refusing : ISessionTransport
    {
        public int RefuseAfter { get; set; } = int.MaxValue;

        public List<byte[]> Sent { get; } = new();

        public bool Send(ulong peer, Channel channel, ReadOnlySpan<byte> message)
        {
            if (Sent.Count >= RefuseAfter)
            {
                return false;
            }

            Sent.Add(message.ToArray());
            return true;
        }
    }
}
