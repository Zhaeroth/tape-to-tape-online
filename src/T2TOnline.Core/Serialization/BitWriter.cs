using System;
using System.Text;

namespace T2TOnline.Core.Serialization;

/// <summary>
/// Writes values into a growable buffer at bit granularity, least significant bit first.
/// One writer is reused per outgoing stream; call <see cref="Reset"/> between messages.
/// </summary>
public sealed class BitWriter
{
    private byte[] _buffer;
    private int _bitPosition;

    /// <summary>Creates a writer with room for <paramref name="initialCapacityBytes"/> bytes before it grows.</summary>
    public BitWriter(int initialCapacityBytes = 256)
    {
        if (initialCapacityBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(initialCapacityBytes), "Capacity must be positive.");
        }

        _buffer = new byte[initialCapacityBytes];
    }

    /// <summary>Most UTF-8 bytes <see cref="WriteShortString"/> writes.</summary>
    public const int MaxShortStringBytes = 255;

    /// <summary>Number of bits written so far.</summary>
    public int BitLength => _bitPosition;

    /// <summary>Number of bytes needed to hold everything written so far.</summary>
    public int ByteLength => (_bitPosition + 7) >> 3;

    /// <summary>Discards everything written, keeping the buffer for reuse.</summary>
    public void Reset()
    {
        Array.Clear(_buffer, 0, ByteLength);
        _bitPosition = 0;
    }

    /// <summary>Writes the low <paramref name="bitCount"/> bits of <paramref name="value"/>.</summary>
    public void WriteBits(ulong value, int bitCount)
    {
        if ((uint)bitCount > 64)
        {
            throw new ArgumentOutOfRangeException(nameof(bitCount), "Between 0 and 64 bits can be written at once.");
        }

        EnsureCapacity(bitCount);
        while (bitCount > 0)
        {
            int byteIndex = _bitPosition >> 3;
            int bitOffset = _bitPosition & 7;
            int take = Math.Min(8 - bitOffset, bitCount);
            int mask = (1 << take) - 1;
            int bits = (int)(value & (ulong)mask);
            _buffer[byteIndex] = (byte)((_buffer[byteIndex] & ~(mask << bitOffset)) | (bits << bitOffset));
            value >>= take;
            bitCount -= take;
            _bitPosition += take;
        }
    }

    /// <summary>Writes one bit.</summary>
    public void WriteBool(bool value) => WriteBits(value ? 1UL : 0UL, 1);

    /// <summary>Writes 8 bits.</summary>
    public void WriteByte(byte value) => WriteBits(value, 8);

    /// <summary>Writes 16 bits.</summary>
    public void WriteUInt16(ushort value) => WriteBits(value, 16);

    /// <summary>Writes 16 bits.</summary>
    public void WriteInt16(short value) => WriteBits(unchecked((ushort)value), 16);

    /// <summary>Writes 32 bits.</summary>
    public void WriteUInt32(uint value) => WriteBits(value, 32);

    /// <summary>Writes a 64-bit unsigned integer.</summary>
    public void WriteUInt64(ulong value) => WriteBits(value, 64);

    /// <summary>Writes a 64-bit signed integer, two's complement.</summary>
    public void WriteInt64(long value) => WriteBits(unchecked((ulong)value), 64);

    /// <summary>
    /// Writes a string as a length byte and up to <paramref name="maxBytes"/> (at most
    /// <see cref="MaxShortStringBytes"/>) bytes of UTF-8. Longer strings are cut at a character boundary, so the result
    /// always decodes.
    /// </summary>
    public void WriteShortString(string value, int maxBytes = MaxShortStringBytes)
    {
        ArgumentNullException.ThrowIfNull(value);
        if ((uint)maxBytes > MaxShortStringBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(maxBytes), $"At most {MaxShortStringBytes} bytes.");
        }

        Span<byte> bytes = stackalloc byte[MaxShortStringBytes];
        int length = 0;
        foreach (Rune rune in value.EnumerateRunes())
        {
            if (length + rune.Utf8SequenceLength > maxBytes)
            {
                break;
            }

            length += rune.EncodeToUtf8(bytes[length..]);
        }

        WriteByte((byte)length);
        WriteBytes(bytes[..length]);
    }

    /// <summary>
    /// <paramref name="value"/> as <see cref="WriteShortString"/> writes it and a reader gets it back: cut at a character
    /// boundary to at most <see cref="MaxShortStringBytes"/> bytes of UTF-8. A friend comparing its own strings with a
    /// host's (object keys) cuts them the same way first.
    /// </summary>
    public static string CutShortString(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        // Rune by rune, as the writer does (a lone surrogate becomes U+FFFD there too).
        var cut = new StringBuilder(value.Length);
        int length = 0;
        foreach (Rune rune in value.EnumerateRunes())
        {
            if (length + rune.Utf8SequenceLength > MaxShortStringBytes)
            {
                break;
            }

            length += rune.Utf8SequenceLength;
            cut.Append(rune.ToString());
        }

        return cut.ToString();
    }

    /// <summary>Writes an IEEE 754 single as 32 bits.</summary>
    public void WriteSingle(float value) => WriteBits(unchecked((uint)BitConverter.SingleToInt32Bits(value)), 32);

    /// <summary>Writes a float as a 16-bit IEEE 754 half. Precision drops to about three significant digits.</summary>
    public void WriteHalf(float value) => WriteBits(unchecked((ushort)BitConverter.HalfToInt16Bits((Half)value)), 16);

    /// <summary>Writes an unsigned integer in 7-bit groups, smallest first; values under 128 take one byte.</summary>
    public void WriteVarUInt(ulong value)
    {
        while (value >= 0x80)
        {
            WriteBits((value & 0x7F) | 0x80, 8);
            value >>= 7;
        }

        WriteBits(value, 8);
    }

    /// <summary>Writes a signed integer zigzag-encoded, so small negative numbers stay small.</summary>
    public void WriteVarInt(long value) => WriteVarUInt(unchecked((ulong)((value << 1) ^ (value >> 63))));

    /// <summary>Writes <paramref name="value"/> clamped to [min, max] using <paramref name="bitCount"/> bits.</summary>
    public void WriteQuantized(float value, float min, float max, int bitCount) =>
        WriteBits(Quantization.Quantize(value, min, max, bitCount), bitCount);

    /// <summary>Writes raw bytes, byte-aligned (a straight copy) or not.</summary>
    public void WriteBytes(ReadOnlySpan<byte> bytes)
    {
        if ((_bitPosition & 7) == 0)
        {
            EnsureCapacity(bytes.Length * 8);
            bytes.CopyTo(_buffer.AsSpan(_bitPosition >> 3));
            _bitPosition += bytes.Length * 8;
            return;
        }

        foreach (byte b in bytes)
        {
            WriteBits(b, 8);
        }
    }

    /// <summary>The bytes written so far. Valid until the next write or reset.</summary>
    public ReadOnlySpan<byte> AsSpan() => new(_buffer, 0, ByteLength);

    /// <summary>A copy of the bytes written so far.</summary>
    public byte[] ToArray() => AsSpan().ToArray();

    private void EnsureCapacity(int extraBits)
    {
        int neededBytes = (_bitPosition + extraBits + 7) >> 3;
        if (neededBytes <= _buffer.Length)
        {
            return;
        }

        int newSize = Math.Max(neededBytes, _buffer.Length * 2);
        Array.Resize(ref _buffer, newSize);
    }
}
