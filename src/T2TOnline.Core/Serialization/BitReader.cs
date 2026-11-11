using System;
using System.Text;

namespace T2TOnline.Core.Serialization;

/// <summary>
/// Reads values written by <see cref="BitWriter"/>. Network input is untrusted, so reading past the end
/// never throws: it returns zeros and sets <see cref="IsOverrun"/>, which decoders check once at the end.
/// </summary>
public ref struct BitReader
{
    private readonly ReadOnlySpan<byte> _data;
    private int _bitPosition;

    /// <summary>Creates a reader over <paramref name="data"/>.</summary>
    public BitReader(ReadOnlySpan<byte> data)
    {
        _data = data;
        _bitPosition = 0;
        IsOverrun = false;
    }

    /// <summary>True once any read went past the end of the data. Everything read after that is zero.</summary>
    public bool IsOverrun { get; private set; }

    /// <summary>Bits left to read.</summary>
    public readonly int RemainingBits => Math.Max(0, (_data.Length * 8) - _bitPosition);

    /// <summary>Number of bits consumed so far.</summary>
    public readonly int BitPosition => _bitPosition;

    /// <summary>Reads <paramref name="bitCount"/> bits (0 to 64).</summary>
    public ulong ReadBits(int bitCount)
    {
        if ((uint)bitCount > 64)
        {
            throw new ArgumentOutOfRangeException(nameof(bitCount), "Between 0 and 64 bits can be read at once.");
        }

        if (bitCount > RemainingBits)
        {
            IsOverrun = true;
            _bitPosition = _data.Length * 8;
            return 0;
        }

        ulong result = 0;
        int written = 0;
        while (written < bitCount)
        {
            int byteIndex = _bitPosition >> 3;
            int bitOffset = _bitPosition & 7;
            int take = Math.Min(8 - bitOffset, bitCount - written);
            ulong bits = (ulong)((_data[byteIndex] >> bitOffset) & ((1 << take) - 1));
            result |= bits << written;
            written += take;
            _bitPosition += take;
        }

        return result;
    }

    /// <summary>Reads one bit.</summary>
    public bool ReadBool() => ReadBits(1) != 0;

    /// <summary>Reads 8 bits.</summary>
    public byte ReadByte() => (byte)ReadBits(8);

    /// <summary>Reads 16 bits.</summary>
    public ushort ReadUInt16() => (ushort)ReadBits(16);

    /// <summary>Reads 16 bits as a signed value.</summary>
    public short ReadInt16() => unchecked((short)(ushort)ReadBits(16));

    /// <summary>Reads 32 bits.</summary>
    public uint ReadUInt32() => (uint)ReadBits(32);

    /// <summary>Reads an IEEE 754 single.</summary>
    public float ReadSingle() => BitConverter.Int32BitsToSingle(unchecked((int)(uint)ReadBits(32)));

    /// <summary>Reads a 64-bit unsigned integer.</summary>
    public ulong ReadUInt64() => ReadBits(64);

    /// <summary>Reads a 64-bit signed integer.</summary>
    public long ReadInt64() => unchecked((long)ReadBits(64));

    /// <summary>
    /// Reads a string written by <see cref="BitWriter.WriteShortString"/>. A length over <paramref name="maxBytes"/>
    /// sets <see cref="IsOverrun"/> and returns an empty string; invalid UTF-8 decodes to replacement characters.
    /// </summary>
    public string ReadShortString(int maxBytes = BitWriter.MaxShortStringBytes)
    {
        int length = ReadByte();
        if (length > maxBytes || length * 8 > RemainingBits)
        {
            IsOverrun = true;
            return string.Empty;
        }

        Span<byte> bytes = stackalloc byte[length];
        ReadBytes(bytes);
        return IsOverrun ? string.Empty : Encoding.UTF8.GetString(bytes);
    }

    /// <summary>Reads a 16-bit IEEE 754 half.</summary>
    public float ReadHalf() => (float)BitConverter.Int16BitsToHalf(unchecked((short)(ushort)ReadBits(16)));

    /// <summary>Reads a value written by <see cref="BitWriter.WriteVarUInt"/>. Overlong encodings set <see cref="IsOverrun"/>.</summary>
    public ulong ReadVarUInt()
    {
        ulong result = 0;
        for (int shift = 0; shift < 64; shift += 7)
        {
            ulong b = ReadBits(8);
            if (shift == 63 && (b & 0x7E) != 0)
            {
                // Bits past 64 would be dropped silently.
                IsOverrun = true;
                return 0;
            }

            result |= (b & 0x7F) << shift;
            if ((b & 0x80) == 0 || IsOverrun)
            {
                return result;
            }
        }

        IsOverrun = true;
        return 0;
    }

    /// <summary>Reads a value written by <see cref="BitWriter.WriteVarInt"/>.</summary>
    public long ReadVarInt()
    {
        ulong raw = ReadVarUInt();
        return unchecked((long)(raw >> 1) ^ -(long)(raw & 1));
    }

    /// <summary>Reads a value written by <see cref="BitWriter.WriteQuantized"/>.</summary>
    public float ReadQuantized(float min, float max, int bitCount) =>
        Quantization.Dequantize(ReadBits(bitCount), min, max, bitCount);

    /// <summary>Fills <paramref name="destination"/> with raw bytes (a straight copy when byte-aligned).</summary>
    public void ReadBytes(scoped Span<byte> destination)
    {
        if ((_bitPosition & 7) == 0 && (long)destination.Length * 8 <= RemainingBits)
        {
            _data.Slice(_bitPosition >> 3, destination.Length).CopyTo(destination);
            _bitPosition += destination.Length * 8;
            return;
        }

        for (int i = 0; i < destination.Length; i++)
        {
            destination[i] = (byte)ReadBits(8);
        }
    }
}
