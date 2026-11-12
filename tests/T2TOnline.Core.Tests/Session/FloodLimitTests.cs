using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using T2TOnline.Core.Protocol;
using T2TOnline.Core.Serialization;
using T2TOnline.Core.Session;
using T2TOnline.Core.Stream;

namespace T2TOnline.Core.Tests.Session;

/// <summary>What a peer's flood costs: bounded per sender and per piece, whatever they send.</summary>
public class FloodLimitTests
{
    private const ulong Stranger = 300;
    private const ulong Build = 0x1122334455667788;
    private const long Second = 1_000_000;
    private static readonly SessionIdentity Identity = new(ProtocolInfo.Version, "0.9.0-rc", Build);

    [Fact]
    public void First_hellos_are_looked_at_once_per_interval_per_sender()
    {
        // Each one costs the host a friend check, log lines and perhaps a virtual controller.
        var host = new HostSession(Identity, new NullTransport());
        host.SetMembers(new[] { Stranger, 301UL });
        host.Receive(Stranger, Channel.Events, Hello(), Second);
        Assert.Equal(HostEventKind.JoinRequested, Take(host).Single().Kind);
        host.Refuse(Stranger, RejectReason.NotAFriend);
        Take(host);

        // A refused member saying hello as fast as they can.
        for (int i = 1; i <= 1000; i++)
        {
            host.Receive(Stranger, Channel.Events, Hello(), Second + i);
        }

        Assert.Empty(Take(host));
        Assert.Equal(1000, host.HellosHeldBack);
        Assert.Null(host.Find(Stranger));

        // Someone else isn't held back by them, and they're looked at again once it's been long enough.
        host.Receive(301, Channel.Events, Hello(), Second + 2000);
        Assert.Equal(HostEventKind.JoinRequested, Take(host).Single().Kind);
        host.Receive(Stranger, Channel.Events, Hello(), Second + SessionTiming.NewHelloIntervalMicros);
        Assert.Equal(HostEventKind.JoinRequested, Take(host).Single().Kind);
    }

    [Fact]
    public void A_real_friends_retries_are_never_held_back()
    {
        Assert.True(SessionTiming.HelloRetryMicros >= SessionTiming.NewHelloIntervalMicros * 2);
    }

    [Fact]
    public void Leaving_and_saying_hello_again_is_held_to_the_interval_too()
    {
        var host = new HostSession(Identity, new NullTransport());
        host.SetMembers(new[] { Stranger });
        long now = Second;
        int joins = 0;
        for (int i = 0; i < 100; i++, now += 1000)
        {
            host.Receive(Stranger, Channel.Events, Hello(), now);
            foreach (HostEvent e in Take(host))
            {
                if (e.Kind == HostEventKind.JoinRequested)
                {
                    joins++;
                    Assert.True(host.Accept(Stranger, 1, now));
                }
            }

            host.Receive(Stranger, Channel.Events, Header(MessageType.Leave).Concat(new[] { (byte)LeaveReason.Quit }).ToArray(), now);
            Take(host);
        }

        Assert.InRange(joins, 1, 1 + (100 * 1000 / SessionTiming.NewHelloIntervalMicros));
    }

    [Fact]
    public void A_piece_shorter_than_the_sender_makes_is_refused()
    {
        // A first piece makes room for the whole item: a few bytes must not be able to cost a friend 4 MB, message after message.
        byte[] tiny = Chunk(hash: 1, total: CustomItem.MaxLogoBytes, offset: 0, length: 1);
        var reader = new BitReader(tiny);
        Assert.False(CustomContentMessage.TryRead(ref reader, out _));

        // Only what Write makes passes: a whole piece, or the end of the item.
        byte[] whole = Chunk(hash: 2, total: CustomItem.MaxLogoBytes, offset: 0, length: CustomContentMessage.MaxChunkBytes);
        reader = new BitReader(whole);
        Assert.True(CustomContentMessage.TryRead(ref reader, out _));
        byte[] end = Chunk(hash: 3, total: 1000, offset: 900, length: 100);
        reader = new BitReader(end);
        Assert.True(CustomContentMessage.TryRead(ref reader, out _));
        byte[] shortEnd = Chunk(hash: 3, total: 1000, offset: 900, length: 99);
        reader = new BitReader(shortEnd);
        Assert.False(CustomContentMessage.TryRead(ref reader, out _));
    }

    [Fact]
    public void What_write_makes_always_reads()
    {
        foreach (int size in new[] { 1, 100, CustomContentMessage.MaxChunkBytes - 1, CustomContentMessage.MaxChunkBytes, CustomContentMessage.MaxChunkBytes + 1, (CustomContentMessage.MaxChunkBytes * 3) + 7 })
        {
            var item = new CustomItem(CustomContentKind.Logo, "custom_a.png", new byte[size]);
            int offset = 0;
            while (offset < size)
            {
                var writer = new BitWriter(1024);
                offset = CustomContentMessage.Write(writer, item, offset);
                var reader = new BitReader(writer.AsSpan());
                Assert.True(CustomContentMessage.TryRead(ref reader, out _));
            }
        }
    }

    [Theory]
    [InlineData("{\"id\":\"a\",\"colors\":{\"home\":{\"r\":1,\"list\":[1,2,3]}}}", true)]
    [InlineData("{\"id\":\"[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[\"}", true)] // in a text
    [InlineData("{\"id\":\"\\\"[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[\"}", true)] // past an escaped quote, still in the text
    [InlineData("", true)] // not JSON: Unity refuses that itself
    [InlineData("]]]]]]]]]]]]]]]]]]]]", true)]
    public void A_model_as_the_game_writes_it_is_shallow(string json, bool shallow)
    {
        Assert.Equal(shallow, CustomItem.IsShallowJson(Encoding.UTF8.GetBytes(json)));
    }

    [Theory]
    [InlineData('[')]
    [InlineData('{')]
    public void A_model_nested_past_the_limit_never_reaches_unity(char bracket)
    {
        Assert.True(CustomItem.IsShallowJson(Encoding.UTF8.GetBytes(new string(bracket, CustomItem.MaxModelDepth))));
        Assert.False(CustomItem.IsShallowJson(Encoding.UTF8.GetBytes(new string(bracket, CustomItem.MaxModelDepth + 1))));
        Assert.False(CustomItem.IsShallowJson(Encoding.UTF8.GetBytes(new string(bracket, CustomItem.MaxModelBytes))));
    }

    private static byte[] Hello()
    {
        var writer = new BitWriter(64);
        new MessageHeader(MessageType.Hello, 0, 0, 0).Write(writer);
        new HelloMessage(ProtocolInfo.Version, "0.9.0-rc", Build, InputKind.Gamepad, 0).Write(writer);
        return writer.ToArray();
    }

    private static byte[] Header(MessageType type)
    {
        var writer = new BitWriter(16);
        new MessageHeader(type, 0, 0, 0).Write(writer);
        return writer.ToArray();
    }

    // Written by hand: Write itself makes only pieces that read.
    private static byte[] Chunk(ulong hash, int total, int offset, int length)
    {
        var writer = new BitWriter(1024);
        writer.WriteUInt64(hash);
        writer.WriteByte((byte)CustomContentKind.Logo);
        writer.WriteShortString("custom_a.png", CustomItem.MaxKeyBytes);
        writer.WriteVarUInt((ulong)total);
        writer.WriteVarUInt((ulong)offset);
        writer.WriteVarUInt((ulong)length);
        writer.WriteBytes(new byte[length]);
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

    private sealed class NullTransport : ISessionTransport
    {
        public bool Send(ulong peer, Channel channel, ReadOnlySpan<byte> message) => true;
    }
}
