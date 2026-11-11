using System;
using System.Collections.Generic;
using T2TOnline.Core.Diagnostics;
using T2TOnline.Core.Serialization;

namespace T2TOnline.Core.Protocol;

/// <summary>One replay object: the network id the stream uses, the host's own id, and a key that finds it locally.</summary>
/// <param name="NetId">The id snapshots and event references use.</param>
/// <param name="HostReplayId">The host's UltimateReplay identity, for logs and same-match playback.</param>
/// <param name="Key">A stable key, the same on every PC that loads the same match (for example "F:H:&lt;asset id&gt;"),
/// or empty when the object has none.</param>
/// <param name="Name">The object's name on the host, for logs.</param>
/// <param name="Components">The host's replay ids of the object's observed components, in order. Recorded state bytes
/// address components by these ids, so a friend's freshly loaded copy (whose ids are random) takes them over before
/// playing. Empty when not sent.</param>
/// <param name="ComponentTypes">One per component id, in the same order: each component's type
/// (<see cref="Stream.ComponentAlignment.TypeHash"/>), so a friend whose copy has other components (one added at run time
/// on one PC only) still matches the rest by type (<see cref="Stream.ComponentAlignment.Align"/>). Null when not sent (an
/// older host, a recording made before).</param>
public readonly record struct ObjectEntry(ushort NetId, int HostReplayId, string Key, string Name, IReadOnlyList<ushort>? Components = null, IReadOnlyList<uint>? ComponentTypes = null)
{
    /// <summary>Most component ids per object.</summary>
    public const int MaxComponents = 64;
}

/// <summary>
/// Host to friend (channel 1): new entries for the object table. Sent at match start and when objects appear, and again
/// for a skater whose component list changed during play (a talent added or removed a component): an entry for a network
/// id already announced replaces only its components and types (<see cref="Stream.MatchStreamClient"/>). After the
/// entries, when any entry has component types, a section with each entry's types in entry order (a count, 0 or the
/// entry's component count, then each type as 32 bits): a reader that stops after the entries (an older build) reads the
/// entries as before.
/// </summary>
public static class ObjectTableMessage
{
    /// <summary>Most entries per message.</summary>
    public const int MaxEntries = 512;

    /// <summary>Writes the payload (after the header).</summary>
    public static void Write(BitWriter writer, IReadOnlyList<ObjectEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count > MaxEntries)
        {
            throw new ArgumentException($"At most {MaxEntries} entries per message.", nameof(entries));
        }

        writer.WriteVarUInt((ulong)entries.Count);
        foreach (ObjectEntry entry in entries)
        {
            writer.WriteVarUInt(entry.NetId);
            writer.WriteVarInt(entry.HostReplayId);
            writer.WriteShortString(entry.Key);
            writer.WriteShortString(entry.Name);
            IReadOnlyList<ushort> components = entry.Components ?? Array.Empty<ushort>();
            if (components.Count > ObjectEntry.MaxComponents)
            {
                throw new ArgumentException($"Object {entry.Name} has over {ObjectEntry.MaxComponents} components.", nameof(entries));
            }

            writer.WriteVarUInt((ulong)components.Count);
            foreach (ushort id in components)
            {
                writer.WriteVarUInt(id);
            }

            if (entry.ComponentTypes is { Count: > 0 } types && types.Count != components.Count)
            {
                throw new ArgumentException($"Object {entry.Name} has {types.Count} component types for {components.Count} components.", nameof(entries));
            }
        }

        bool anyTypes = false;
        foreach (ObjectEntry entry in entries)
        {
            anyTypes |= entry.ComponentTypes is { Count: > 0 };
        }

        if (!anyTypes)
        {
            return;
        }

        foreach (ObjectEntry entry in entries)
        {
            IReadOnlyList<uint> types = entry.ComponentTypes ?? Array.Empty<uint>();
            writer.WriteVarUInt((ulong)types.Count);
            foreach (uint type in types)
            {
                writer.WriteUInt32(type);
            }
        }
    }

    /// <summary>Reads the payload. False for malformed data.</summary>
    public static bool TryRead(ref BitReader reader, List<ObjectEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        int first = entries.Count;
        ulong count = reader.ReadVarUInt();
        if (count > MaxEntries)
        {
            return false;
        }

        for (ulong i = 0; i < count; i++)
        {
            ulong netId = reader.ReadVarUInt();
            long replayId = reader.ReadVarInt();
            string key = reader.ReadShortString();

            // For logs and the copy's object name only: a host can't forge a log line with it.
            string name = SafeText.Clean(reader.ReadShortString(), BitWriter.MaxShortStringBytes);
            ulong componentCount = reader.ReadVarUInt();
            if (reader.IsOverrun || netId > ushort.MaxValue || replayId is < int.MinValue or > int.MaxValue || componentCount > ObjectEntry.MaxComponents)
            {
                return false;
            }

            var components = new ushort[(int)componentCount];
            for (int c = 0; c < components.Length; c++)
            {
                ulong id = reader.ReadVarUInt();
                if (id > ushort.MaxValue)
                {
                    return false;
                }

                components[c] = (ushort)id;
            }

            entries.Add(new ObjectEntry((ushort)netId, (int)replayId, key, name, components.Length == 0 ? null : components));
        }

        if (reader.IsOverrun)
        {
            return false;
        }

        // The types section, when the host sent one (anything less than a byte left is the payload's padding).
        if (reader.RemainingBits < 8)
        {
            return true;
        }

        for (int i = first; i < entries.Count; i++)
        {
            ObjectEntry entry = entries[i];
            int components = entry.Components?.Count ?? 0;
            ulong typeCount = reader.ReadVarUInt();
            if (reader.IsOverrun || (typeCount != 0 && typeCount != (ulong)components))
            {
                return false;
            }

            if (typeCount == 0)
            {
                continue;
            }

            var types = new uint[(int)typeCount];
            for (int t = 0; t < types.Length; t++)
            {
                types[t] = reader.ReadUInt32();
            }

            entries[i] = entry with { ComponentTypes = types };
        }

        return !reader.IsOverrun;
    }
}

/// <summary>One game event as the stream knows it.</summary>
/// <param name="EventId">The id <see cref="GameEventMessage"/> uses.</param>
/// <param name="Name">Bus and field, for example "GameplayEvents.Goal".</param>
/// <param name="ArgFields">The argument's field names in the order values are sent; empty for no argument.</param>
public sealed record EventEntry(ushort EventId, string Name, IReadOnlyList<string> ArgFields);

/// <summary>
/// Host to friend (channel 1): the event table, read from the running build. Both PCs run the identical build, so ids
/// line up; the names make a mismatch visible instead of silent.
/// </summary>
public static class EventTableMessage
{
    /// <summary>Most events per message.</summary>
    public const int MaxEntries = 1024;

    /// <summary>Most argument fields per event.</summary>
    public const int MaxFields = 64;

    /// <summary>Writes the payload (after the header).</summary>
    public static void Write(BitWriter writer, IReadOnlyList<EventEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count > MaxEntries)
        {
            throw new ArgumentException($"At most {MaxEntries} entries per message.", nameof(entries));
        }

        writer.WriteVarUInt((ulong)entries.Count);
        foreach (EventEntry entry in entries)
        {
            if (entry.ArgFields.Count > MaxFields)
            {
                throw new ArgumentException($"Event {entry.Name} has over {MaxFields} argument fields.", nameof(entries));
            }

            writer.WriteVarUInt(entry.EventId);
            writer.WriteShortString(entry.Name);
            writer.WriteVarUInt((ulong)entry.ArgFields.Count);
            foreach (string field in entry.ArgFields)
            {
                writer.WriteShortString(field);
            }
        }
    }

    /// <summary>Reads the payload. False for malformed data.</summary>
    public static bool TryRead(ref BitReader reader, List<EventEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ulong count = reader.ReadVarUInt();
        if (count > MaxEntries)
        {
            return false;
        }

        for (ulong i = 0; i < count; i++)
        {
            ulong id = reader.ReadVarUInt();
            string name = reader.ReadShortString();
            ulong fieldCount = reader.ReadVarUInt();
            if (reader.IsOverrun || id > ushort.MaxValue || fieldCount > MaxFields)
            {
                return false;
            }

            var fields = new string[(int)fieldCount];
            for (int f = 0; f < fields.Length; f++)
            {
                fields[f] = reader.ReadShortString();
            }

            entries.Add(new EventEntry((ushort)id, name, fields));
        }

        return !reader.IsOverrun;
    }
}

/// <summary>Host to friend (channel 1): a game event fired on the host, with its argument's field values.</summary>
public static class GameEventMessage
{
    /// <summary>Writes the payload (after the header).</summary>
    public static void Write(BitWriter writer, ushort eventId, IReadOnlyList<NetValue> args)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteVarUInt(eventId);
        NetValue.WriteList(writer, args);
    }

    /// <summary>Reads the payload. False for malformed data.</summary>
    public static bool TryRead(ref BitReader reader, out ushort eventId, List<NetValue> args)
    {
        ulong id = reader.ReadVarUInt();
        eventId = (ushort)Math.Min(id, ushort.MaxValue);
        return !reader.IsOverrun && id <= ushort.MaxValue && NetValue.TryReadList(ref reader, args, EventTableMessage.MaxFields);
    }
}

/// <summary>A sound played directly by gameplay code on the host (not through an event), or a looping sound's start or stop.</summary>
/// <param name="Handle">0 for a one-shot; for a loop, the id that pairs its start and stop.</param>
/// <param name="Start">For a loop: true to start, false to stop. Ignored for a one-shot.</param>
/// <param name="EventGuid">The FMOD event's GUID (16 bytes).</param>
/// <param name="HasPosition">True when the sound plays at a world position.</param>
/// <param name="X">Position X.</param>
/// <param name="Y">Position Y.</param>
/// <param name="Z">Position Z.</param>
/// <param name="Source">What played it on the host, for logs (for example "SoundManager.PlayOneShotAt").</param>
public readonly record struct SoundCall(uint Handle, bool Start, Guid EventGuid, bool HasPosition, float X, float Y, float Z, string Source)
{
    /// <summary>Writes the payload (after the header).</summary>
    public void Write(BitWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteVarUInt(Handle);
        writer.WriteBool(Start);
        writer.WriteBool(HasPosition);
        Span<byte> guid = stackalloc byte[16];
        EventGuid.TryWriteBytes(guid);
        writer.WriteBytes(guid);
        if (HasPosition)
        {
            writer.WriteSingle(X);
            writer.WriteSingle(Y);
            writer.WriteSingle(Z);
        }

        writer.WriteShortString(Source, 64);
    }

    /// <summary>Reads the payload. False for malformed data.</summary>
    public static bool TryRead(ref BitReader reader, out SoundCall call)
    {
        ulong handle = reader.ReadVarUInt();
        bool start = reader.ReadBool();
        bool hasPosition = reader.ReadBool();
        Span<byte> guid = stackalloc byte[16];
        reader.ReadBytes(guid);
        float x = 0, y = 0, z = 0;
        if (hasPosition)
        {
            x = reader.ReadSingle();
            y = reader.ReadSingle();
            z = reader.ReadSingle();
        }

        string source = reader.ReadShortString(64);
        call = new SoundCall((uint)Math.Min(handle, uint.MaxValue), start, new Guid(guid), hasPosition, x, y, z, source);
        return !reader.IsOverrun && handle <= uint.MaxValue;
    }
}

/// <summary>
/// Host to friend (channel 1): HUD values that changed (clock, period, scores, stamina, penalties, ability charge), by
/// key. Sent on change, at most 20 times a second; a full set every few seconds so a friend joining late catches up.
/// </summary>
public static class HudStateMessage
{
    /// <summary>Most values per message.</summary>
    public const int MaxEntries = 256;

    /// <summary>Writes the payload (after the header).</summary>
    public static void Write(BitWriter writer, IReadOnlyList<KeyValuePair<string, NetValue>> values)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count > MaxEntries)
        {
            throw new ArgumentException($"At most {MaxEntries} values per message.", nameof(values));
        }

        writer.WriteVarUInt((ulong)values.Count);
        foreach (KeyValuePair<string, NetValue> pair in values)
        {
            writer.WriteShortString(pair.Key, 96);
            pair.Value.Write(writer);
        }
    }

    /// <summary>Reads the payload. False for malformed data.</summary>
    public static bool TryRead(ref BitReader reader, List<KeyValuePair<string, NetValue>> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        ulong count = reader.ReadVarUInt();
        if (count > MaxEntries)
        {
            return false;
        }

        for (ulong i = 0; i < count; i++)
        {
            string key = reader.ReadShortString(96);
            if (!NetValue.TryRead(ref reader, out NetValue value))
            {
                return false;
            }

            values.Add(new KeyValuePair<string, NetValue>(key, value));
        }

        return !reader.IsOverrun;
    }
}

/// <summary>Friend to host (channel 0): the newest snapshot decoded, and whether a keyframe is needed (a delta's base was missing).</summary>
public static class SnapshotAckMessage
{
    /// <summary>Writes the payload (after the header).</summary>
    public static void Write(BitWriter writer, uint newest, bool needKeyframe)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteVarUInt(newest);
        writer.WriteBool(needKeyframe);
    }

    /// <summary>Reads the payload. False for malformed data.</summary>
    public static bool TryRead(ref BitReader reader, out uint newest, out bool needKeyframe)
    {
        ulong value = reader.ReadVarUInt();
        needKeyframe = reader.ReadBool();
        newest = (uint)Math.Min(value, uint.MaxValue);
        return !reader.IsOverrun && value <= uint.MaxValue;
    }
}

/// <summary>
/// Host to friend (channel 1, in order with the match's tables, events and end): the match about to load, as named values
/// (mode, rules, both teams by asset id, each slot's forward by its asset id, goalies, referee, arena), so a friend's
/// game can load the very same match. Values are by name so a field the friend's build
/// doesn't know is skipped, not misread; the setup hash makes any difference visible.
/// </summary>
public static class MatchSetupMessage
{
    /// <summary>Most values per setup.</summary>
    public const int MaxEntries = 256;

    /// <summary>Writes the payload (after the header).</summary>
    public static void Write(BitWriter writer, ulong setupHash, IReadOnlyList<KeyValuePair<string, NetValue>> values)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count > MaxEntries)
        {
            throw new ArgumentException($"At most {MaxEntries} values.", nameof(values));
        }

        writer.WriteUInt64(setupHash);
        writer.WriteVarUInt((ulong)values.Count);
        foreach (KeyValuePair<string, NetValue> pair in values)
        {
            writer.WriteShortString(pair.Key, 96);
            pair.Value.Write(writer);
        }
    }

    /// <summary>Reads the payload. False for malformed data.</summary>
    public static bool TryRead(ref BitReader reader, out ulong setupHash, List<KeyValuePair<string, NetValue>> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        setupHash = reader.ReadUInt64();
        ulong count = reader.ReadVarUInt();
        if (reader.IsOverrun || count > MaxEntries)
        {
            return false;
        }

        for (ulong i = 0; i < count; i++)
        {
            string key = reader.ReadShortString(96);
            if (!NetValue.TryRead(ref reader, out NetValue value))
            {
                return false;
            }

            values.Add(new KeyValuePair<string, NetValue>(key, value));
        }

        return !reader.IsOverrun;
    }

    /// <summary>
    /// A stable hash of a setup's values (FNV-1a over the sorted keys and their written bytes), so both sides can tell
    /// they loaded the same match.
    /// </summary>
    public static ulong Hash(IEnumerable<KeyValuePair<string, NetValue>> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var writer = new BitWriter(512);
        foreach (KeyValuePair<string, NetValue> pair in SortedByKey(values))
        {
            writer.WriteShortString(pair.Key, 96);
            pair.Value.Write(writer);
        }

        return Fnv.Hash(writer.AsSpan());
    }

    private static List<KeyValuePair<string, NetValue>> SortedByKey(IEnumerable<KeyValuePair<string, NetValue>> values)
    {
        var sorted = new List<KeyValuePair<string, NetValue>>(values);
        sorted.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
        return sorted;
    }
}

/// <summary>Friend to host (channel 1): the friend's match is loaded and in client mode, with the hash of the setup it loaded.</summary>
public static class MatchReadyMessage
{
    /// <summary>Longest reason, in UTF-8 bytes.</summary>
    public const int MaxReasonBytes = 160;

    /// <summary>
    /// Writes the payload (after the header): the setup hash of the match this PC has loaded, whether it's ready, and
    /// (not ready) why, for the host's log: empty when it simply stopped playing, a reason when it couldn't load.
    /// </summary>
    public static void Write(BitWriter writer, ulong setupHash, bool ready, string reason = "")
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(reason);
        writer.WriteUInt64(setupHash);
        writer.WriteBool(ready);
        writer.WriteShortString(reason, MaxReasonBytes);
    }

    /// <summary>Reads the payload. False for malformed data.</summary>
    public static bool TryRead(ref BitReader reader, out ulong setupHash, out bool ready, out string reason)
    {
        setupHash = reader.ReadUInt64();
        ready = reader.ReadBool();
        reason = SafeText.Clean(reader.ReadShortString(MaxReasonBytes), MaxReasonBytes);
        return !reader.IsOverrun;
    }
}

/// <summary>
/// Host to friend (channel 1): the match with this setup hash is over, or the host left it. Hash 0 means "no match":
/// whatever match the friend has is over (sent to a friend who comes back after the host left the match).
/// </summary>
public static class MatchEndMessage
{
    /// <summary>Writes the payload (after the header).</summary>
    public static void Write(BitWriter writer, ulong setupHash)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteUInt64(setupHash);
    }

    /// <summary>Reads the payload. False for malformed data.</summary>
    public static bool TryRead(ref BitReader reader, out ulong setupHash)
    {
        setupHash = reader.ReadUInt64();
        return !reader.IsOverrun;
    }
}

/// <summary>
/// A friend's playout of the host's stream over the last report interval (<see cref="StreamReportMessage"/>): how far
/// behind the newest snapshot it rendered, what it aimed for, the jitter it measured, how often it ran dry, and what
/// arrived. Times in microseconds, counts for the interval.
/// </summary>
/// <param name="RenderBehindMicros">Mean time render time was behind the newest snapshot.</param>
/// <param name="TargetDelayMicros">The delay the playout aimed for at the end of the interval.</param>
/// <param name="JitterMicros">The measured arrival jitter (95th percentile).</param>
/// <param name="Frames">Frames rendered.</param>
/// <param name="HeldFrames">Frames with nothing newer to show.</param>
/// <param name="Holds">Runs of held frames.</param>
/// <param name="Snapshots">Snapshots decoded.</param>
/// <param name="MissingBase">Snapshots that couldn't be decoded (their base was missing).</param>
public readonly record struct StreamReport(
    uint RenderBehindMicros, uint TargetDelayMicros, uint JitterMicros, uint Frames, uint HeldFrames, uint Holds, uint Snapshots, uint MissingBase);

/// <summary>
/// Friend to host (channel 0, about once a second while playing): the friend's <see cref="StreamReport"/>, for the
/// host's net overlay and for timing the fight grace window (how late the friend sees the match).
/// </summary>
public static class StreamReportMessage
{
    /// <summary>Writes the payload (after the header).</summary>
    public static void Write(BitWriter writer, in StreamReport report)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteVarUInt(report.RenderBehindMicros);
        writer.WriteVarUInt(report.TargetDelayMicros);
        writer.WriteVarUInt(report.JitterMicros);
        writer.WriteVarUInt(report.Frames);
        writer.WriteVarUInt(report.HeldFrames);
        writer.WriteVarUInt(report.Holds);
        writer.WriteVarUInt(report.Snapshots);
        writer.WriteVarUInt(report.MissingBase);
    }

    /// <summary>Reads the payload. False for malformed data (or a value too large to be real).</summary>
    public static bool TryRead(ref BitReader reader, out StreamReport report)
    {
        Span<ulong> values = stackalloc ulong[8];
        bool ok = true;
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = reader.ReadVarUInt();
            ok &= values[i] <= uint.MaxValue;
        }

        report = ok
            ? new StreamReport((uint)values[0], (uint)values[1], (uint)values[2], (uint)values[3], (uint)values[4], (uint)values[5], (uint)values[6], (uint)values[7])
            : default;
        return ok && !reader.IsOverrun && report.HeldFrames <= report.Frames;
    }
}
