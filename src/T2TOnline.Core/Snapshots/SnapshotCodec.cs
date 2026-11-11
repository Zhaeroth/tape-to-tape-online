using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using T2TOnline.Core.Serialization;

namespace T2TOnline.Core.Snapshots;

/// <summary>
/// Encodes snapshots for the wire and the stream file. A keyframe carries every object in full. A delta names a base
/// snapshot the receiver already has and carries only what changed against it:
/// <list type="bullet">
/// <item>objects added, or whose state changed length, go in full;</item>
/// <item>objects of the same length go as their XOR against the base, as runs of zeros and literal bytes, so a state
/// that changed in two bytes costs about four;</item>
/// <item>objects gone since the base are listed by id.</item>
/// </list>
/// The body is then deflated when that makes it smaller. Measured on real matches, the game's ~1.6 KB of changed
/// object state per snapshot comes to ~200 B in play and ~400 B in a brawl.
/// </summary>
/// <remarks>
/// For the network the base is the newest snapshot the friend acknowledged, so a lost packet never breaks the
/// chain; for a stream file it's simply the previous snapshot, with a keyframe every few seconds so playback can start
/// anywhere. Decoding never throws: malformed input returns false.
/// </remarks>
public static class SnapshotCodec
{
    /// <summary>The latest host time a snapshot may carry (about 12 days of recording): anything beyond is corrupt.</summary>
    public const long MaxHostTimeMicros = 1L << 40;

    /// <summary>Largest body (before deflate), in bytes: the writer refuses a larger one, the reader rejects it.</summary>
    public const int MaxBodyBytes = 256 * 1024;

    /// <summary>
    /// Most bytes of object state one frame may hold: what a keyframe of it could carry (a friend starts on one, and gets one
    /// after a loss). A delta's XOR runs copy a whole state for a few bytes, so a frame isn't bounded by the size of its message;
    /// this is: the frames a friend keeps (its timeline, its history) can't come to more than this each.
    /// </summary>
    public const int MaxFrameStateBytes = MaxBodyBytes;

    private const byte FlagKeyframe = 1;
    private const byte FlagCompressed = 2;
    private const byte FlagCamera = 4;
    private const int ModeXor = 1;

    /// <summary>
    /// Writes <paramref name="frame"/> to <paramref name="writer"/>: as a delta against <paramref name="baseFrame"/>, or as a
    /// keyframe when the base is null. Whatever it writes, <see cref="TryRead"/> decodes: a camera pose or group scale that
    /// isn't finite is left out, and a time scale that isn't finite goes as 1. False, with nothing written, for a frame that
    /// can't go at all: its host time is outside 0 to <see cref="MaxHostTimeMicros"/>, its body would be over
    /// <see cref="MaxBodyBytes"/> (then it can't be a base either), or its states come to over <see cref="MaxFrameStateBytes"/>.
    /// </summary>
    /// <exception cref="ArgumentException">More than <see cref="SnapshotFrame.MaxObjects"/> objects or
    /// <see cref="SnapshotFrame.MaxGroupScales"/> group scales, or a state over <see cref="SnapshotFrame.MaxStateBytes"/>.</exception>
    public static bool Write(BitWriter writer, SnapshotFrame frame, SnapshotFrame? baseFrame)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(frame);
        if (frame.Objects.Count > SnapshotFrame.MaxObjects || frame.GroupScales.Count > SnapshotFrame.MaxGroupScales)
        {
            throw new ArgumentException("Too many objects or group scales for one snapshot.", nameof(frame));
        }

        if (frame.HostTimeMicros is < 0 or > MaxHostTimeMicros)
        {
            return false;
        }

        Scratch scratch = Scratch.ForThisThread;
        BitWriter body = scratch.Body;
        body.Reset();
        body.WriteVarUInt(frame.Sequence);
        if (baseFrame != null)
        {
            body.WriteVarUInt(unchecked(frame.Sequence - baseFrame.Sequence));
        }

        body.WriteVarInt(frame.HostTimeMicros);
        body.WriteSingle(float.IsFinite(frame.TimeScale) ? frame.TimeScale : 1f);
        int groups = 0;
        foreach ((_, float scale) in frame.GroupScales)
        {
            groups += float.IsFinite(scale) ? 1 : 0;
        }

        body.WriteByte((byte)groups);
        foreach ((byte group, float scale) in frame.GroupScales)
        {
            if (float.IsFinite(scale))
            {
                body.WriteByte(group);
                body.WriteSingle(scale);
            }
        }

        bool hasCamera = frame.Camera is CameraPose camera && IsFinite(camera);
        if (hasCamera)
        {
            WriteCamera(body, frame.Camera!.Value);
        }

        if (!WriteObjects(body, frame, baseFrame, scratch))
        {
            return false;
        }

        ReadOnlySpan<byte> raw = body.AsSpan();
        if (raw.Length > MaxBodyBytes)
        {
            return false;
        }

        Compression.Deflate(raw, scratch.Packed);
        ReadOnlySpan<byte> packed = scratch.Packed.GetBuffer().AsSpan(0, (int)scratch.Packed.Length);
        bool compress = packed.Length < raw.Length;
        byte flags = (byte)((baseFrame == null ? FlagKeyframe : 0) | (compress ? FlagCompressed : 0) | (hasCamera ? FlagCamera : 0));
        writer.WriteByte(flags);
        ReadOnlySpan<byte> payload = compress ? packed : raw;
        writer.WriteVarUInt((ulong)payload.Length);
        writer.WriteBytes(payload);
        return true;
    }

    /// <summary>
    /// Reads a snapshot. A delta's base is looked up in <paramref name="history"/>; false if it isn't there (ask for a
    /// keyframe) or the data is malformed. The decoded frame is not added to the history: the caller does that.
    /// </summary>
    public static bool TryRead(ref BitReader reader, SnapshotHistory history, out SnapshotFrame frame, out bool missingBase)
    {
        ArgumentNullException.ThrowIfNull(history);
        missingBase = false;
        byte flags = reader.ReadByte();
        ulong length = reader.ReadVarUInt();
        if (reader.IsOverrun || length > MaxBodyBytes || (long)length * 8 > reader.RemainingBits)
        {
            return Fail(out frame);
        }

        byte[] payload = new byte[(int)length];
        reader.ReadBytes(payload);
        byte[] raw = payload;
        if ((flags & FlagCompressed) != 0 && !Compression.TryInflate(payload, MaxBodyBytes, out raw))
        {
            return Fail(out frame);
        }

        var body = new BitReader(raw);
        ulong sequence = body.ReadVarUInt();
        if (sequence > uint.MaxValue)
        {
            return Fail(out frame);
        }

        SnapshotFrame? baseFrame = null;
        if ((flags & FlagKeyframe) == 0)
        {
            ulong back = body.ReadVarUInt();
            if (body.IsOverrun || back > uint.MaxValue)
            {
                return Fail(out frame); // a back-reference no writer makes: malformed, not a missing base
            }

            if (!history.TryGet(unchecked((uint)sequence - (uint)back), out baseFrame))
            {
                missingBase = true;
                return Fail(out frame);
            }
        }

        // A delta starts from its base's objects: copied whole (cheaper than one by one), then changed.
        frame = baseFrame == null ? new SnapshotFrame() : new SnapshotFrame(baseFrame);
        frame.Sequence = (uint)sequence;
        frame.HostTimeMicros = body.ReadVarInt();
        if (frame.HostTimeMicros is < 0 or > MaxHostTimeMicros)
        {
            return false;
        }

        frame.TimeScale = body.ReadSingle();
        int groups = body.ReadByte();
        if (groups > SnapshotFrame.MaxGroupScales)
        {
            return false;
        }

        for (int i = 0; i < groups; i++)
        {
            (byte group, float scale) = (body.ReadByte(), body.ReadSingle());
            if (!float.IsFinite(scale))
            {
                return false;
            }

            frame.GroupScales.Add((group, scale));
        }

        if ((flags & FlagCamera) != 0)
        {
            CameraPose camera = ReadCamera(ref body);
            if (!IsFinite(camera))
            {
                return false;
            }

            frame.Camera = camera;
        }

        return ReadObjects(ref body, frame, baseFrame) && !body.IsOverrun && float.IsFinite(frame.TimeScale);
    }

    private static bool Fail(out SnapshotFrame frame)
    {
        frame = new SnapshotFrame();
        return false;
    }

    /// <summary>Writes the objects; false when the frame's states together are over <see cref="MaxFrameStateBytes"/>.</summary>
    private static bool WriteObjects(BitWriter body, SnapshotFrame frame, SnapshotFrame? baseFrame, Scratch scratch)
    {
        // Removed since the base, then changed or added, each as ascending ids coded as gaps.
        List<ushort> removed = scratch.Removed;
        removed.Clear();
        if (baseFrame != null)
        {
            foreach (ushort id in baseFrame.Objects.Keys)
            {
                if (!frame.Objects.ContainsKey(id))
                {
                    removed.Add(id);
                }
            }
        }

        body.WriteVarUInt((ulong)removed.Count);
        int previous = -1;
        foreach (ushort id in removed)
        {
            body.WriteVarUInt((ulong)(id - previous - 1));
            previous = id;
        }

        List<KeyValuePair<ushort, byte[]>> changed = scratch.Changed;
        changed.Clear();
        long stateBytes = 0;
        foreach (KeyValuePair<ushort, byte[]> pair in frame.Objects)
        {
            if (pair.Value.Length > SnapshotFrame.MaxStateBytes)
            {
                throw new ArgumentException($"Object {pair.Key}'s state is over {SnapshotFrame.MaxStateBytes} bytes.", nameof(frame));
            }

            stateBytes += pair.Value.Length;
            if (baseFrame == null || !baseFrame.Objects.TryGetValue(pair.Key, out byte[]? old) || !old.AsSpan().SequenceEqual(pair.Value))
            {
                changed.Add(pair);
            }
        }

        if (stateBytes > MaxFrameStateBytes)
        {
            changed.Clear();
            return false;
        }

        body.WriteVarUInt((ulong)changed.Count);
        previous = -1;
        foreach ((ushort id, byte[] state) in changed)
        {
            // The id gap and the mode share one varint, so the body stays byte-aligned: deflate finds far more
            // repeats in aligned bytes.
            byte[]? old = null;
            bool xor = baseFrame != null && baseFrame.Objects.TryGetValue(id, out old) && old.Length == state.Length;
            body.WriteVarUInt(((ulong)(uint)(id - previous - 1) << 1) | (xor ? 1UL : 0UL));
            previous = id;
            if (xor)
            {
                XorRuns.Write(body, state, old!);
            }
            else
            {
                body.WriteVarUInt((ulong)state.Length);
                body.WriteBytes(state);
            }
        }

        changed.Clear(); // holds no states past the call
        return true;
    }

    /// <summary>Reads the objects into <paramref name="frame"/>, which already holds <paramref name="baseFrame"/>'s.</summary>
    private static bool ReadObjects(ref BitReader body, SnapshotFrame frame, SnapshotFrame? baseFrame)
    {
        ulong removedCount = body.ReadVarUInt();
        if (removedCount > SnapshotFrame.MaxObjects)
        {
            return false;
        }

        int id = -1;
        for (ulong i = 0; i < removedCount; i++)
        {
            ulong gap = body.ReadVarUInt();
            id += (int)Math.Min(gap, ushort.MaxValue) + 1;
            if (id > ushort.MaxValue || !frame.Objects.Remove((ushort)id))
            {
                return false;
            }
        }

        ulong changedCount = body.ReadVarUInt();
        if (changedCount > SnapshotFrame.MaxObjects)
        {
            return false;
        }

        id = -1;
        for (ulong i = 0; i < changedCount; i++)
        {
            ulong packed = body.ReadVarUInt();
            id += (int)Math.Min(packed >> 1, ushort.MaxValue) + 1;
            if (id > ushort.MaxValue || body.IsOverrun)
            {
                return false;
            }

            byte[] state;
            if ((packed & 1) == ModeXor)
            {
                if (baseFrame == null || !baseFrame.Objects.TryGetValue((ushort)id, out byte[]? old) || !XorRuns.TryRead(ref body, old, out state))
                {
                    return false;
                }
            }
            else
            {
                ulong length = body.ReadVarUInt();
                if (length > SnapshotFrame.MaxStateBytes || (long)length * 8 > body.RemainingBits)
                {
                    return false;
                }

                state = new byte[(int)length];
                body.ReadBytes(state);
            }

            frame.Objects[(ushort)id] = state;
        }

        if (frame.Objects.Count > SnapshotFrame.MaxObjects)
        {
            return false;
        }

        long stateBytes = 0;
        foreach (byte[] state in frame.Objects.Values)
        {
            stateBytes += state.Length;
        }

        return stateBytes <= MaxFrameStateBytes;
    }

    private static void WriteCamera(BitWriter body, in CameraPose camera)
    {
        body.WriteSingle(camera.X);
        body.WriteSingle(camera.Y);
        body.WriteSingle(camera.Z);
        body.WriteSingle(camera.QX);
        body.WriteSingle(camera.QY);
        body.WriteSingle(camera.QZ);
        body.WriteSingle(camera.QW);
        body.WriteSingle(camera.Lens);
        body.WriteByte(camera.Orthographic ? (byte)1 : (byte)0);
    }

    private static bool IsFinite(CameraPose pose) =>
        float.IsFinite(pose.X) && float.IsFinite(pose.Y) && float.IsFinite(pose.Z) && float.IsFinite(pose.QX) && float.IsFinite(pose.QY) &&
        float.IsFinite(pose.QZ) && float.IsFinite(pose.QW) && float.IsFinite(pose.Lens);

    private static CameraPose ReadCamera(ref BitReader body) => new(
        body.ReadSingle(), body.ReadSingle(), body.ReadSingle(),
        body.ReadSingle(), body.ReadSingle(), body.ReadSingle(), body.ReadSingle(),
        body.ReadSingle(), body.ReadByte() != 0);

    /// <summary>What encoding reuses from snapshot to snapshot, one set per thread (the host encodes up to one per friend a frame).</summary>
    private sealed class Scratch
    {
        [ThreadStatic]
        private static Scratch? _current;

        public static Scratch ForThisThread => _current ??= new Scratch();

        public BitWriter Body { get; } = new(4096);

        public MemoryStream Packed { get; } = new(4096);

        public List<ushort> Removed { get; } = new();

        public List<KeyValuePair<ushort, byte[]>> Changed { get; } = new();
    }
}

/// <summary>Zero-run coding of the XOR between two equal-length byte strings.</summary>
internal static class XorRuns
{
    /// <summary>Writes <paramref name="current"/> as (zero run, literal run, literal XOR bytes) pairs against <paramref name="old"/>.</summary>
    public static void Write(BitWriter writer, ReadOnlySpan<byte> current, ReadOnlySpan<byte> old)
    {
        int i = 0;
        while (i < current.Length)
        {
            int zeroStart = i;
            while (i < current.Length && current[i] == old[i])
            {
                i++;
            }

            int literalStart = i;
            while (i < current.Length && current[i] != old[i])
            {
                i++;
            }

            writer.WriteVarUInt((ulong)(literalStart - zeroStart));
            writer.WriteVarUInt((ulong)(i - literalStart));
            for (int k = literalStart; k < i; k++)
            {
                writer.WriteByte((byte)(current[k] ^ old[k]));
            }
        }
    }

    /// <summary>Rebuilds a state from its runs against <paramref name="old"/>. False if the runs don't fit.</summary>
    public static bool TryRead(ref BitReader reader, byte[] old, out byte[] state)
    {
        state = new byte[old.Length];
        int i = 0;
        while (i < old.Length)
        {
            ulong zeros = reader.ReadVarUInt();
            ulong literals = reader.ReadVarUInt();
            if (reader.IsOverrun || zeros + literals == 0 || zeros > (ulong)(old.Length - i) || literals > (ulong)(old.Length - i) - zeros)
            {
                return false;
            }

            for (ulong k = 0; k < zeros; k++, i++)
            {
                state[i] = old[i];
            }

            for (ulong k = 0; k < literals; k++, i++)
            {
                state[i] = (byte)(reader.ReadByte() ^ old[i]);
            }
        }

        return !reader.IsOverrun;
    }
}

/// <summary>Recently decoded snapshots by sequence, for resolving deltas.</summary>
public sealed class SnapshotHistory
{
    private readonly SnapshotFrame?[] _frames;

    /// <summary>Creates a history holding the newest <paramref name="capacity"/> snapshots.</summary>
    public SnapshotHistory(int capacity = 64)
    {
        if (capacity < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        _frames = new SnapshotFrame?[capacity];
    }

    /// <summary>Adds a decoded snapshot.</summary>
    public void Add(SnapshotFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        uint slot = frame.Sequence % (uint)_frames.Length;
        SnapshotFrame? held = _frames[slot];
        if (held == null || frame.Sequence > held.Sequence)
        {
            _frames[slot] = frame;
        }
    }

    /// <summary>The snapshot with <paramref name="sequence"/>, if still held.</summary>
    public bool TryGet(uint sequence, [NotNullWhen(true)] out SnapshotFrame? frame)
    {
        frame = _frames[sequence % (uint)_frames.Length];
        if (frame != null && frame.Sequence == sequence)
        {
            return true;
        }

        frame = null;
        return false;
    }

    /// <summary>Forgets everything.</summary>
    public void Clear() => Array.Clear(_frames);
}
