using System;
using T2TOnline.Core.Protocol;
using T2TOnline.Core.Serialization;

namespace T2TOnline.Core.Tests.Protocol;

/// <summary>A friend's pad family on the wire (<see cref="MessageType.PadDevice"/>).</summary>
public class PadDeviceMessageTests
{
    [Fact]
    public void Every_family_round_trips()
    {
        for (int value = 0; value <= byte.MaxValue; value++)
        {
            var message = new PadDeviceMessage((byte)value);
            var writer = new BitWriter();
            message.Write(writer);
            var reader = new BitReader(writer.AsSpan());

            Assert.True(PadDeviceMessage.TryRead(ref reader, out PadDeviceMessage read));
            Assert.Equal(message, read);
        }
    }

    [Fact]
    public void Its_layout_is_one_byte_after_the_header()
    {
        var writer = new BitWriter();
        new MessageHeader(MessageType.PadDevice, 3, 0, 0).Write(writer);
        new PadDeviceMessage(2).Write(writer);

        Assert.Equal(new byte[] { 8, 3, 0, 0, 0, 0, 0, 0, 0, 0, 2 }, writer.ToArray());
    }

    [Fact]
    public void A_truncated_message_is_refused()
    {
        var reader = new BitReader(ReadOnlySpan<byte>.Empty);

        Assert.False(PadDeviceMessage.TryRead(ref reader, out _));
    }

    [Fact]
    public void Random_bytes_never_throw()
    {
        var random = new Random(11);
        var buffer = new byte[4];
        for (int i = 0; i < 1000; i++)
        {
            int length = random.Next(0, buffer.Length + 1);
            random.NextBytes(buffer);
            var reader = new BitReader(buffer.AsSpan(0, length));
            bool ok = PadDeviceMessage.TryRead(ref reader, out PadDeviceMessage read);
            Assert.Equal(length > 0, ok);
            if (ok)
            {
                Assert.Equal(buffer[0], read.DeviceType);
            }
        }
    }
}
