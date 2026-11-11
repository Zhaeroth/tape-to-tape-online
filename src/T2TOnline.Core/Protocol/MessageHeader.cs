using System;
using T2TOnline.Core.Serialization;

namespace T2TOnline.Core.Protocol;

/// <summary>
/// The common start of every message: type, per-channel sequence number, host tick and match time.
/// About 10 bytes; the sequence is a varint, so it costs one byte for the first 128 messages of a stream.
/// </summary>
/// <param name="Type">What follows.</param>
/// <param name="Sequence">Per-sender, per-type counter: shows each stream's loss and lets receivers drop stale unreliable messages.</param>
/// <param name="HostTick">Host simulation tick the message refers to (for stream messages: the newest snapshot's sequence).</param>
/// <param name="MatchTimeMillis">Host match time in milliseconds (for stream messages: the stream clock snapshots carry, so
/// a friend shows an event, sound or HUD change when its render time reaches it).</param>
public readonly record struct MessageHeader(MessageType Type, ulong Sequence, uint HostTick, uint MatchTimeMillis)
{
    // Every defined type, by value, so reading a header doesn't box an enum per message.
    private static readonly bool[] KnownTypes = FindKnownTypes();

    // One fixed layout for every type, never trimmed per type: a newer host must still read an older friend's Hello.

    /// <summary>Writes the header.</summary>
    public void Write(BitWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.WriteByte((byte)Type);
        writer.WriteVarUInt(Sequence);
        writer.WriteUInt32(HostTick);
        writer.WriteUInt32(MatchTimeMillis);
    }

    /// <summary>Reads a header. Returns false for truncated data or an unknown type.</summary>
    public static bool TryRead(ref BitReader reader, out MessageHeader header)
    {
        var type = (MessageType)reader.ReadByte();
        ulong sequence = reader.ReadVarUInt();
        uint tick = reader.ReadUInt32();
        uint matchTime = reader.ReadUInt32();
        header = new MessageHeader(type, sequence, tick, matchTime);
        return !reader.IsOverrun && KnownTypes[(byte)type];
    }

    private static bool[] FindKnownTypes()
    {
        var known = new bool[256];
        foreach (MessageType type in Enum.GetValues<MessageType>())
        {
            known[(byte)type] = type != MessageType.None;
        }

        return known;
    }
}
