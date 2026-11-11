using System;
using System.Collections.Generic;
using T2TOnline.Core.Serialization;

namespace T2TOnline.Core.Campaign;

/// <summary>What one part of a <see cref="RunCheckpoint"/> holds. Values are part of the protocol: only add.</summary>
public enum RunCheckpointPart : byte
{
    /// <summary>Not a valid part.</summary>
    None = 0,

    /// <summary>The run, as the host's game saved it (the run file's bytes).</summary>
    Run = 1,

    /// <summary>The run's statistics, as the host's game saved them (the run-stats file's bytes).</summary>
    RunStats = 2,
}

/// <summary>
/// The host's whole run at one moment: the bytes the host's game wrote for its run and run-stats files, numbered in the
/// order the host made them. A friend's game loads them through the game's own loader instead of its own files, so the
/// run on every screen is the host's.
/// </summary>
public sealed class RunCheckpoint
{
    /// <summary>Most parts in one checkpoint.</summary>
    public const int MaxParts = 8;

    /// <summary>Largest part, uncompressed (a run file is tens of KB; the map string is the bulk of it).</summary>
    public const int MaxPartBytes = 4 * 1024 * 1024;

    private readonly SortedDictionary<RunCheckpointPart, byte[]> _parts;

    /// <summary>A checkpoint of <paramref name="parts"/>. Throws for no parts, unknown kinds, or a part over the limit.</summary>
    public RunCheckpoint(uint sequence, IReadOnlyDictionary<RunCheckpointPart, byte[]> parts)
    {
        ArgumentNullException.ThrowIfNull(parts);
        if (parts.Count == 0 || parts.Count > MaxParts)
        {
            throw new ArgumentException($"A checkpoint has 1 to {MaxParts} parts.", nameof(parts));
        }

        _parts = new SortedDictionary<RunCheckpointPart, byte[]>();
        foreach (KeyValuePair<RunCheckpointPart, byte[]> part in parts)
        {
            if (!IsKnown(part.Key))
            {
                throw new ArgumentException($"Unknown part {part.Key}.", nameof(parts));
            }

            ArgumentNullException.ThrowIfNull(part.Value, nameof(parts));
            if (part.Value.Length > MaxPartBytes)
            {
                throw new ArgumentException($"Part {part.Key} is {part.Value.Length} bytes; at most {MaxPartBytes}.", nameof(parts));
            }

            _parts[part.Key] = part.Value;
        }

        Sequence = sequence;
        Hash = ComputeHash(_parts);
    }

    /// <summary>The host's number for this checkpoint; later checkpoints have later numbers (wrapping).</summary>
    public uint Sequence { get; }

    /// <summary>FNV-1a over every part's kind, length and bytes, in kind order: equal hashes, equal runs.</summary>
    public ulong Hash { get; }

    /// <summary>The parts, by kind.</summary>
    public IReadOnlyDictionary<RunCheckpointPart, byte[]> Parts => _parts;

    /// <summary>Total uncompressed bytes.</summary>
    public int RawBytes
    {
        get
        {
            int total = 0;
            foreach (byte[] bytes in _parts.Values)
            {
                total += bytes.Length;
            }

            return total;
        }
    }

    /// <summary>The bytes of <paramref name="part"/>, or null when this checkpoint has none.</summary>
    public byte[]? Get(RunCheckpointPart part) => _parts.TryGetValue(part, out byte[]? bytes) ? bytes : null;

    /// <summary>True for a part kind this build knows.</summary>
    public static bool IsKnown(RunCheckpointPart part) => part is RunCheckpointPart.Run or RunCheckpointPart.RunStats;

    /// <summary>The checkpoint hash of <paramref name="parts"/> (see <see cref="Hash"/>).</summary>
    public static ulong ComputeHash(IEnumerable<KeyValuePair<RunCheckpointPart, byte[]>> parts)
    {
        ArgumentNullException.ThrowIfNull(parts);
        var sorted = new List<KeyValuePair<RunCheckpointPart, byte[]>>(parts);
        sorted.Sort((a, b) => a.Key.CompareTo(b.Key));
        ulong hash = Fnv.OffsetBasis;
        Span<byte> length = stackalloc byte[4];
        foreach (KeyValuePair<RunCheckpointPart, byte[]> part in sorted)
        {
            hash = Fnv.Add(hash, (byte)part.Key);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(length, part.Value.Length);
            hash = Fnv.Add(hash, length);
            hash = Fnv.Add(hash, part.Value);
        }

        return hash;
    }
}

/// <summary>
/// Host to friend (channel 2): a <see cref="RunCheckpoint"/>. Each part is deflated; the hash is of the uncompressed
/// parts, so a friend can tell it has exactly the host's run. Everything about it is checked before it's used: part
/// count, known and distinct kinds, sizes (before anything is allocated), the inflated length and the hash.
/// </summary>
public static class RunCheckpointMessage
{
    /// <summary>
    /// Largest message, in bytes: Steam's reliable messages go up to 512 KB; this leaves room for the header. A run whose
    /// compressed parts come to more isn't sent (the host logs it).
    /// </summary>
    public const int MaxMessageBytes = 500 * 1024;

    /// <summary>
    /// Writes the payload (after the header). False, with nothing usable written, when the compressed checkpoint wouldn't
    /// fit in <see cref="MaxMessageBytes"/>.
    /// </summary>
    public static bool Write(BitWriter writer, RunCheckpoint checkpoint)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(checkpoint);
        writer.WriteUInt32(checkpoint.Sequence);
        writer.WriteByte((byte)checkpoint.Parts.Count);
        foreach (KeyValuePair<RunCheckpointPart, byte[]> part in checkpoint.Parts)
        {
            byte[] deflated = Compression.Deflate(part.Value);
            writer.WriteByte((byte)part.Key);
            writer.WriteVarUInt((ulong)part.Value.Length);
            writer.WriteVarUInt((ulong)deflated.Length);
            writer.WriteBytes(deflated);
            if (writer.ByteLength > MaxMessageBytes)
            {
                return false;
            }
        }

        writer.WriteUInt64(checkpoint.Hash);
        return writer.ByteLength <= MaxMessageBytes;
    }

    /// <summary>
    /// Reads the payload. False for anything malformed, oversized, or whose hash doesn't match, and for sequence 0 (the
    /// host never numbers a checkpoint 0: it means "no checkpoint" in a view and "none applied" on a friend). Never throws.
    /// </summary>
    public static bool TryRead(ref BitReader reader, out RunCheckpoint? checkpoint)
    {
        checkpoint = null;
        uint sequence = reader.ReadUInt32();
        int count = reader.ReadByte();
        if (reader.IsOverrun || sequence == 0 || count is 0 or > RunCheckpoint.MaxParts)
        {
            return false;
        }

        var parts = new Dictionary<RunCheckpointPart, byte[]>(count);
        for (int i = 0; i < count; i++)
        {
            var kind = (RunCheckpointPart)reader.ReadByte();
            ulong rawLength = reader.ReadVarUInt();
            ulong deflatedLength = reader.ReadVarUInt();
            if (reader.IsOverrun || !RunCheckpoint.IsKnown(kind) || parts.ContainsKey(kind) ||
                rawLength > RunCheckpoint.MaxPartBytes || deflatedLength > MaxMessageBytes ||
                (long)deflatedLength * 8 > reader.RemainingBits)
            {
                return false;
            }

            byte[] deflated = new byte[(int)deflatedLength];
            reader.ReadBytes(deflated);
            if (reader.IsOverrun || !Compression.TryInflate(deflated, (int)rawLength, out byte[] raw) || raw.Length != (int)rawLength)
            {
                return false;
            }

            parts[kind] = raw;
        }

        ulong hash = reader.ReadUInt64();
        if (reader.IsOverrun || RunCheckpoint.ComputeHash(parts) != hash)
        {
            return false;
        }

        checkpoint = new RunCheckpoint(sequence, parts);
        return true;
    }
}
