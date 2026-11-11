using System;
using T2TOnline.Core.Protocol;
using T2TOnline.Core.Serialization;

namespace T2TOnline.Core.Tests.Protocol;

/// <summary>Message 17's reason byte (<see cref="PadDisconnectedMessage"/>): optional, and never refused.</summary>
public class PadDisconnectedMessageTests
{
    [Theory]
    [InlineData(PadAwayReason.Unplugged)]
    [InlineData(PadAwayReason.ChangingControls)]
    [InlineData(PadAwayReason.InSettings)]
    public void Every_reason_round_trips_in_one_byte(PadAwayReason reason)
    {
        var writer = new BitWriter();
        new PadDisconnectedMessage(reason).Write(writer);
        byte[] bytes = writer.ToArray();
        Assert.Equal(new[] { (byte)reason }, bytes);

        var reader = new BitReader(bytes);
        Assert.True(PadDisconnectedMessage.TryRead(ref reader, out PadDisconnectedMessage read));
        Assert.Equal(reason, read.Reason);
        Assert.False(reader.IsOverrun);
    }

    [Fact]
    public void No_reason_reads_as_unplugged()
    {
        // The layout before the reason byte (and a friend that sends none): an empty payload.
        var reader = new BitReader(ReadOnlySpan<byte>.Empty);
        Assert.True(PadDisconnectedMessage.TryRead(ref reader, out PadDisconnectedMessage read));
        Assert.Equal(PadAwayReason.Unplugged, read.Reason);

        // Fewer than 8 bits left (a header that ended mid-byte) is no reason either.
        var writer = new BitWriter();
        writer.WriteBits(1, 3);
        reader = new BitReader(writer.ToArray());
        reader.ReadBits(3);
        Assert.True(PadDisconnectedMessage.TryRead(ref reader, out read));
        Assert.Equal(PadAwayReason.Unplugged, read.Reason);
    }

    [Fact]
    public void Every_byte_reads_without_throwing_and_an_unknown_reason_is_unplugged()
    {
        for (int value = 0; value <= byte.MaxValue; value++)
        {
            var reader = new BitReader(new[] { (byte)value, (byte)0xFF, (byte)0x00 });
            Assert.True(PadDisconnectedMessage.TryRead(ref reader, out PadDisconnectedMessage read));
            PadAwayReason expected = value <= (byte)PadDisconnectedMessage.LastKnown ? (PadAwayReason)value : PadAwayReason.Unplugged;
            Assert.Equal(expected, read.Reason);
            Assert.True(Enum.IsDefined(read.Reason));
        }
    }

    [Fact]
    public void Random_payloads_never_throw()
    {
        var random = new Random(17);
        var buffer = new byte[8];
        for (int i = 0; i < 2000; i++)
        {
            int length = random.Next(buffer.Length + 1);
            random.NextBytes(buffer);
            var reader = new BitReader(buffer.AsSpan(0, length));
            Assert.True(PadDisconnectedMessage.TryRead(ref reader, out PadDisconnectedMessage read));
            Assert.True(Enum.IsDefined(read.Reason));
        }
    }
}
