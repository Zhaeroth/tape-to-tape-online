using System;
using T2TOnline.Core.Serialization;

namespace T2TOnline.Core.Tests.Serialization;

public class BitPackingTests
{
    [Fact]
    public void Mixed_values_round_trip()
    {
        var writer = new BitWriter(4);
        writer.WriteBits(0b101, 3);
        writer.WriteBool(true);
        writer.WriteByte(0xAB);
        writer.WriteUInt16(0xBEEF);
        writer.WriteInt16(-12345);
        writer.WriteUInt32(0xDEADBEEF);
        writer.WriteSingle(3.14159f);
        writer.WriteHalf(-1.5f);
        writer.WriteBits(ulong.MaxValue, 64);
        writer.WriteBits(0, 0);

        var reader = new BitReader(writer.AsSpan());
        Assert.Equal(0b101UL, reader.ReadBits(3));
        Assert.True(reader.ReadBool());
        Assert.Equal(0xAB, reader.ReadByte());
        Assert.Equal(0xBEEF, reader.ReadUInt16());
        Assert.Equal(-12345, reader.ReadInt16());
        Assert.Equal(0xDEADBEEFu, reader.ReadUInt32());
        Assert.Equal(3.14159f, reader.ReadSingle());
        Assert.Equal(-1.5f, reader.ReadHalf());
        Assert.Equal(ulong.MaxValue, reader.ReadBits(64));
        Assert.False(reader.IsOverrun);
        Assert.True(reader.RemainingBits < 8);
    }

    [Theory]
    [InlineData(0UL, 1)]
    [InlineData(127UL, 1)]
    [InlineData(128UL, 2)]
    [InlineData(16_383UL, 2)]
    [InlineData(16_384UL, 3)]
    [InlineData(ulong.MaxValue, 10)]
    public void VarUInt_round_trips_with_expected_length(ulong value, int expectedBytes)
    {
        var writer = new BitWriter();
        writer.WriteVarUInt(value);
        Assert.Equal(expectedBytes, writer.ByteLength);

        var reader = new BitReader(writer.AsSpan());
        Assert.Equal(value, reader.ReadVarUInt());
        Assert.False(reader.IsOverrun);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    [InlineData(1L)]
    [InlineData(-64L)]
    [InlineData(63L)]
    [InlineData(long.MinValue)]
    [InlineData(long.MaxValue)]
    public void VarInt_round_trips(long value)
    {
        var writer = new BitWriter();
        writer.WriteVarInt(value);
        var reader = new BitReader(writer.AsSpan());
        Assert.Equal(value, reader.ReadVarInt());
    }

    [Fact]
    public void Small_negative_varints_take_one_byte()
    {
        var writer = new BitWriter();
        writer.WriteVarInt(-64);
        Assert.Equal(1, writer.ByteLength);
    }

    [Fact]
    public void Reading_past_the_end_sets_overrun_instead_of_throwing()
    {
        var reader = new BitReader(new byte[] { 0xFF });
        Assert.Equal(0xFFUL, reader.ReadBits(8));
        Assert.Equal(0UL, reader.ReadBits(1));
        Assert.True(reader.IsOverrun);
        Assert.Equal(0u, reader.ReadUInt32());
    }

    [Fact]
    public void Overlong_varuint_is_rejected()
    {
        byte[] data = new byte[12];
        Array.Fill(data, (byte)0x80);
        var reader = new BitReader(data);
        Assert.Equal(0UL, reader.ReadVarUInt());
        Assert.True(reader.IsOverrun);
    }

    [Fact]
    public void Reset_clears_previous_bits()
    {
        var writer = new BitWriter(2);
        writer.WriteUInt16(0xFFFF);
        writer.Reset();
        writer.WriteBits(0, 3);
        writer.WriteBits(1, 1);

        Assert.Equal(1, writer.ByteLength);
        Assert.Equal(0b1000, writer.AsSpan()[0]);
    }

    [Fact]
    public void Writer_grows_past_its_initial_capacity()
    {
        var writer = new BitWriter(1);
        for (int i = 0; i < 1000; i++)
        {
            writer.WriteUInt32((uint)i);
        }

        var reader = new BitReader(writer.AsSpan());
        for (int i = 0; i < 1000; i++)
        {
            Assert.Equal((uint)i, reader.ReadUInt32());
        }
    }

    [Fact]
    public void Bit_widths_outside_0_to_64_are_refused()
    {
        var writer = new BitWriter();
        Assert.Throws<ArgumentOutOfRangeException>(() => writer.WriteBits(0, 65));
        Assert.Throws<ArgumentOutOfRangeException>(() => writer.WriteBits(0, -1));
    }

    [Fact]
    public void Bytes_round_trip_when_not_byte_aligned()
    {
        byte[] payload = { 1, 2, 3, 250, 255 };
        var writer = new BitWriter();
        writer.WriteBits(1, 3);
        writer.WriteBytes(payload);

        var reader = new BitReader(writer.AsSpan());
        reader.ReadBits(3);
        byte[] read = new byte[payload.Length];
        reader.ReadBytes(read);
        Assert.Equal(payload, read);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(8)]
    public void Byte_runs_come_out_the_same_aligned_or_not(int leadBits)
    {
        var data = new byte[300];
        new Random(leadBits).NextBytes(data);
        var fast = new BitWriter(4);
        var slow = new BitWriter(4);
        foreach (BitWriter writer in new[] { fast, slow })
        {
            writer.WriteBits(0x5A, leadBits);
        }

        fast.WriteBytes(data);
        foreach (byte b in data)
        {
            slow.WriteByte(b);
        }

        fast.WriteBool(true);
        slow.WriteBool(true);
        Assert.Equal(slow.ToArray(), fast.ToArray());

        var reader = new BitReader(fast.AsSpan());
        Assert.Equal(0x5AUL & ((1UL << leadBits) - 1), reader.ReadBits(leadBits));
        var read = new byte[data.Length];
        reader.ReadBytes(read);
        Assert.Equal(data, read);
        Assert.True(reader.ReadBool());
        Assert.False(reader.IsOverrun);

        // Asking for more than is left is an overrun, aligned or not.
        reader = new BitReader(fast.AsSpan());
        reader.ReadBits(leadBits);
        reader.ReadBytes(new byte[data.Length + 2]);
        Assert.True(reader.IsOverrun);
    }

    [Theory]
    [InlineData(10)]
    [InlineData(85)]
    [InlineData(254)]
    [InlineData(255)]
    [InlineData(256)]
    [InlineData(400)]
    public void Cut_short_string_is_what_a_reader_gets_back(int length)
    {
        // Two-byte and four-byte characters straddle the 255-byte limit at different points.
        foreach (string unit in new[] { "a", "é", "🏒" })
        {
            var text = new System.Text.StringBuilder();
            while (System.Text.Encoding.UTF8.GetByteCount(text.ToString()) < length)
            {
                text.Append(unit);
            }

            string value = text.ToString();
            var writer = new BitWriter();
            writer.WriteShortString(value);
            var reader = new BitReader(writer.AsSpan());
            Assert.Equal(reader.ReadShortString(), BitWriter.CutShortString(value));
        }
    }
}
