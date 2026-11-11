using System;
using T2TOnline.Core.Serialization;
using T2TOnline.Core.Stream;

namespace T2TOnline.Core.Protocol;

/// <summary>One piece of a <see cref="CustomItem"/> as it arrived (<see cref="CustomContentMessage"/>).</summary>
/// <param name="Hash">The whole item's <see cref="CustomItem.Hash"/>.</param>
/// <param name="Kind">What the item is.</param>
/// <param name="Key">The item's key.</param>
/// <param name="TotalBytes">The whole item's length.</param>
/// <param name="Offset">Where this piece starts in the item.</param>
/// <param name="Bytes">This piece.</param>
public readonly record struct CustomChunk(ulong Hash, CustomContentKind Kind, string Key, int TotalBytes, int Offset, byte[] Bytes);

/// <summary>
/// Host to friend (channel 2, <see cref="MessageType.CustomContent"/>, after the header): one piece of a
/// <see cref="CustomItem"/>, in order. An item goes in pieces of at most <see cref="MaxChunkBytes"/>, so a large logo never
/// comes near Steam's limit for one reliable message. Everything is checked before it's used: the kind, the key, the
/// sizes (before anything is allocated) and that the piece lies inside the item. An older friend counts the type as
/// malformed and drops it (its load then stops at the custom team, as before).
/// </summary>
public static class CustomContentMessage
{
    /// <summary>Largest piece, in bytes (Steam's reliable messages go up to 512 KB).</summary>
    public const int MaxChunkBytes = 192 * 1024;

    /// <summary>
    /// Writes the piece of <paramref name="item"/> that starts at <paramref name="offset"/> (up to
    /// <see cref="MaxChunkBytes"/>); returns where the next one starts (the item's length after the last).
    /// </summary>
    public static int Write(BitWriter writer, CustomItem item, int offset)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(item);
        if (offset < 0 || offset >= item.Bytes.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(offset), $"An offset inside the item's {item.Bytes.Length} bytes.");
        }

        int length = Math.Min(MaxChunkBytes, item.Bytes.Length - offset);
        writer.WriteUInt64(item.Hash);
        writer.WriteByte((byte)item.Kind);
        writer.WriteShortString(item.Key, CustomItem.MaxKeyBytes);
        writer.WriteVarUInt((ulong)item.Bytes.Length);
        writer.WriteVarUInt((ulong)offset);
        writer.WriteVarUInt((ulong)length);
        writer.WriteBytes(item.Bytes.AsSpan(offset, length));
        return offset + length;
    }

    /// <summary>
    /// Reads the payload. False for anything malformed, out of range or not a safe item, and for a piece shorter than <see cref="Write"/>
    /// makes: a friend makes room for a whole item at its first piece, so a few bytes could otherwise cost it 4 MB, message after
    /// message. Never throws.
    /// </summary>
    public static bool TryRead(ref BitReader reader, out CustomChunk chunk)
    {
        chunk = default;
        ulong hash = reader.ReadUInt64();
        var kind = (CustomContentKind)reader.ReadByte();
        string key = reader.ReadShortString(CustomItem.MaxKeyBytes);
        ulong total = reader.ReadVarUInt();
        ulong offset = reader.ReadVarUInt();
        ulong length = reader.ReadVarUInt();
        if (reader.IsOverrun || !CustomItem.IsKnown(kind) || total == 0 || total > (ulong)CustomItem.MaxBytesOf(kind) ||
            offset >= total || length != Math.Min(MaxChunkBytes, total - offset) ||
            (long)length * 8 > reader.RemainingBits || CustomItem.Check(kind, key, (int)total) != null)
        {
            return false;
        }

        byte[] bytes = new byte[(int)length];
        reader.ReadBytes(bytes);
        if (reader.IsOverrun)
        {
            return false;
        }

        chunk = new CustomChunk(hash, kind, key, (int)total, (int)offset, bytes);
        return true;
    }
}
