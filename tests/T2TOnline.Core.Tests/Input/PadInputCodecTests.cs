using System;
using T2TOnline.Core.Input;
using T2TOnline.Core.Serialization;

namespace T2TOnline.Core.Tests.Input;

public class PadInputCodecTests
{
    private static PadState RandomState(Random random) => new(
        (PadButtons)random.Next(0, 1 << 14),
        (uint)random.Next(),
        (short)random.Next(-32767, 32768),
        (short)random.Next(-32767, 32768),
        (short)random.Next(-32767, 32768),
        (short)random.Next(-32767, 32768),
        (ushort)random.Next(0, 65536),
        (ushort)random.Next(0, 65536));

    [Fact]
    public void Eight_random_frames_round_trip()
    {
        var random = new Random(42);
        var frames = new PadState[PadInputCodec.MaxFramesPerPacket];
        for (int i = 0; i < frames.Length; i++)
        {
            frames[i] = RandomState(random);
        }

        var writer = new BitWriter();
        PadInputCodec.Write(writer, 123_456u, frames);

        var reader = new BitReader(writer.AsSpan());
        var decoded = new PadState[PadInputCodec.MaxFramesPerPacket];
        Assert.True(PadInputCodec.TryRead(ref reader, decoded, out uint newest, out int count));
        Assert.Equal(123_456u, newest);
        Assert.Equal(frames.Length, count);
        Assert.Equal(frames, decoded);
    }

    [Fact]
    public void Repeated_frames_cost_one_bit_each()
    {
        var held = new PadState(PadButtons.A, 1, 1000, -1000, 0, 0, 0, 65535);
        var frames = new PadState[8];
        Array.Fill(frames, held);

        var writer = new BitWriter();
        PadInputCodec.Write(writer, 1, frames);

        int fullFrameBits = 1 + 3 + 14 + 28 + 64 + 32;
        Assert.Equal(32 + 3 + fullFrameBits + 7, writer.BitLength);
    }

    [Fact]
    public void Typical_play_stays_well_under_the_mtu()
    {
        var random = new Random(7);
        var frames = new PadState[8];
        short x = 0;
        for (int i = 0; i < frames.Length; i++)
        {
            x = (short)Math.Clamp(x + random.Next(-800, 800), -32767, 32767);
            frames[i] = new PadState(i < 4 ? PadButtons.None : PadButtons.X, (uint)(i < 4 ? 0 : 4), x, 12000, 0, 0, 0, 0);
        }

        var writer = new BitWriter();
        PadInputCodec.Write(writer, 99, frames);
        Assert.True(writer.ByteLength < 120, $"{writer.ByteLength} bytes");
    }

    [Fact]
    public void Truncated_packets_are_rejected()
    {
        var writer = new BitWriter();
        PadInputCodec.Write(writer, 5, new[] { new PadState(PadButtons.B, 0, 5, 5, 5, 5, 5, 5) });
        byte[] bytes = writer.ToArray();

        var reader = new BitReader(bytes.AsSpan(0, bytes.Length - 3));
        var decoded = new PadState[8];
        Assert.False(PadInputCodec.TryRead(ref reader, decoded, out _, out int count));
        Assert.Equal(0, count);
    }

    [Fact]
    public void A_destination_that_is_too_small_is_rejected()
    {
        var writer = new BitWriter();
        PadInputCodec.Write(writer, 5, new PadState[4]);
        var reader = new BitReader(writer.AsSpan());
        Assert.False(PadInputCodec.TryRead(ref reader, new PadState[2], out _, out _));
    }

    [Fact]
    public void Frame_counts_outside_1_to_8_are_refused()
    {
        var writer = new BitWriter();
        Assert.Throws<ArgumentOutOfRangeException>(() => PadInputCodec.Write(writer, 1, ReadOnlySpan<PadState>.Empty));
        Assert.Throws<ArgumentOutOfRangeException>(() => PadInputCodec.Write(writer, 1, new PadState[9]));
    }

    [Fact]
    public void Frame_numbers_count_back_from_the_newest_across_wraparound()
    {
        Assert.Equal(uint.MaxValue, PadInputCodec.FrameNumberAt(1, 3, 0));
        Assert.Equal(0u, PadInputCodec.FrameNumberAt(1, 3, 1));
        Assert.Equal(1u, PadInputCodec.FrameNumberAt(1, 3, 2));
    }
}
