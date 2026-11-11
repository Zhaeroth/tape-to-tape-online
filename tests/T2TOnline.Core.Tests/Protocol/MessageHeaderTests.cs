using T2TOnline.Core.Protocol;
using T2TOnline.Core.Serialization;

namespace T2TOnline.Core.Tests.Protocol;

public class MessageHeaderTests
{
    [Fact]
    public void Header_round_trips()
    {
        var header = new MessageHeader(MessageType.Snapshot, 300, 123_456, 98_765);
        var writer = new BitWriter();
        header.Write(writer);
        Assert.Equal(1 + 2 + 4 + 4, writer.ByteLength);

        var reader = new BitReader(writer.AsSpan());
        Assert.True(MessageHeader.TryRead(ref reader, out MessageHeader read));
        Assert.Equal(header, read);
    }

    [Fact]
    public void Unknown_types_are_rejected()
    {
        var writer = new BitWriter();
        new MessageHeader((MessageType)250, 0, 0, 0).Write(writer);
        var reader = new BitReader(writer.AsSpan());
        Assert.False(MessageHeader.TryRead(ref reader, out _));
    }

    [Fact]
    public void Truncated_headers_are_rejected()
    {
        var writer = new BitWriter();
        new MessageHeader(MessageType.Ping, 1, 2, 3).Write(writer);
        var reader = new BitReader(writer.AsSpan()[..5]);
        Assert.False(MessageHeader.TryRead(ref reader, out _));
    }

    [Fact]
    public void Message_type_values_are_stable()
    {
        Assert.Equal(1, (byte)MessageType.Hello);
        Assert.Equal(16, (byte)MessageType.PadInput);
        Assert.Equal(32, (byte)MessageType.Snapshot);
        Assert.Equal(85, (byte)MessageType.RunCheckpoint);
        Assert.Equal(112, (byte)MessageType.AchievementUnlocked);
    }
}
