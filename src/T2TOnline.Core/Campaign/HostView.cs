using System;
using System.Collections.Generic;
using System.Linq;
using T2TOnline.Core.Diagnostics;
using T2TOnline.Core.Serialization;

namespace T2TOnline.Core.Campaign;

/// <summary>
/// Where the host's game is: a screen (the title, the campaign map, a match, a menu) with named details (the map node,
/// the menu's id, the match's setup hash), and the run checkpoint a friend needs before following there. Each change is a
/// new epoch. The screen names and details belong to the game glue; Core only carries, numbers and compares them.
/// </summary>
public sealed class HostView
{
    /// <summary>Longest screen name, in UTF-8 bytes.</summary>
    public const int MaxScreenBytes = 48;

    /// <summary>Most details.</summary>
    public const int MaxValues = 64;

    /// <summary>Longest detail name, in UTF-8 bytes.</summary>
    public const int MaxKeyBytes = 64;

    /// <summary>A view. The details are copied; their order doesn't matter (they're compared by name).</summary>
    public HostView(uint epoch, string screen, uint checkpoint, IReadOnlyList<KeyValuePair<string, NetValue>> values)
    {
        ArgumentNullException.ThrowIfNull(screen);
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count > MaxValues)
        {
            throw new ArgumentException($"At most {MaxValues} details.", nameof(values));
        }

        Epoch = epoch;
        Screen = screen;
        Checkpoint = checkpoint;
        var sorted = new List<KeyValuePair<string, NetValue>>(values);
        sorted.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
        Values = sorted;
        ContentHash = HashOf(screen, sorted);
    }

    /// <summary>The host's number for this view; later views have later numbers (wrapping).</summary>
    public uint Epoch { get; }

    /// <summary>The screen's name, as the game glue names it.</summary>
    public string Screen { get; }

    /// <summary>The run checkpoint a friend must hold before following (0: none needed).</summary>
    public uint Checkpoint { get; }

    /// <summary>The details, sorted by name.</summary>
    public IReadOnlyList<KeyValuePair<string, NetValue>> Values { get; }

    /// <summary>A hash of the screen and its details (not the epoch or checkpoint): equal hashes, same place.</summary>
    public ulong ContentHash { get; }

    /// <summary>
    /// True when <paramref name="other"/> is the same screen with the same details, apart from those named in
    /// <paramref name="ignored"/> (e.g. where the cursor is): for telling a real move from a cursor moving.
    /// </summary>
    public bool SameExcept(HostView? other, IReadOnlyCollection<string> ignored)
    {
        ArgumentNullException.ThrowIfNull(ignored);
        if (other == null || other.Screen != Screen)
        {
            return false;
        }

        var mine = new List<KeyValuePair<string, NetValue>>(Values.Count);
        foreach (KeyValuePair<string, NetValue> pair in Values)
        {
            if (!ignored.Contains(pair.Key))
            {
                mine.Add(pair);
            }
        }

        int index = 0;
        foreach (KeyValuePair<string, NetValue> pair in other.Values)
        {
            if (ignored.Contains(pair.Key))
            {
                continue;
            }

            if (index >= mine.Count || mine[index].Key != pair.Key || mine[index].Value != pair.Value)
            {
                return false;
            }

            index++;
        }

        return index == mine.Count;
    }

    /// <summary>The detail called <paramref name="key"/>, or false.</summary>
    public bool TryGet(string key, out NetValue value)
    {
        foreach (KeyValuePair<string, NetValue> pair in Values)
        {
            if (pair.Key == key)
            {
                value = pair.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    /// <summary>The detail called <paramref name="key"/> as text (empty when absent or not text).</summary>
    public string Text(string key) => TryGet(key, out NetValue value) && value.Kind == NetValueKind.Text ? value.Text ?? string.Empty : string.Empty;

    /// <summary>The detail called <paramref name="key"/> as a whole number (<paramref name="fallback"/> when absent or not one).</summary>
    public long Whole(string key, long fallback = 0) => TryGet(key, out NetValue value) && value.Kind == NetValueKind.Whole ? value.WholeValue : fallback;

    /// <summary>
    /// For logs: the screen and its details, <c>map (act=1, node="0,1", run="9f0c...")</c>. A host sends them, so the screen,
    /// each name and each text value go by <see cref="HostText.KeyForLog(string?, int)"/>: as they are when shaped like the
    /// mod's names and the game's ids (every honest one is), else as their length.
    /// </summary>
    public override string ToString()
    {
        var parts = new List<string>(Values.Count);
        foreach (KeyValuePair<string, NetValue> pair in Values)
        {
            parts.Add($"{HostText.KeyForLog(pair.Key)}={ValueForLog(pair.Value)}");
        }

        string screen = HostText.KeyForLog(Screen);
        return parts.Count == 0 ? screen : $"{screen} ({string.Join(", ", parts)})";
    }

    /// <summary>A detail's value for a log line: a text by <see cref="HostText.KeyForLog(string?, int)"/> in quotes (none: <c>""</c>), any other as it is.</summary>
    internal static string ValueForLog(NetValue value) => value.Kind != NetValueKind.Text
        ? value.ToString()
        : string.IsNullOrEmpty(value.Text) ? "\"\"" : HostText.IsKey(value.Text) && value.Text.Length <= HostText.MaxValueLength
            ? $"\"{value.Text}\""
            : HostText.KeyForLog(value.Text, HostText.MaxValueLength);

    private static ulong HashOf(string screen, List<KeyValuePair<string, NetValue>> sorted)
    {
        var writer = new BitWriter(256);
        writer.WriteShortString(screen, MaxScreenBytes);
        foreach (KeyValuePair<string, NetValue> pair in sorted)
        {
            writer.WriteShortString(pair.Key, MaxKeyBytes);
            pair.Value.Write(writer);
        }

        return Fnv.Hash(writer.AsSpan());
    }
}

/// <summary>Host to friend (channel 1): a <see cref="HostView"/>.</summary>
public static class HostViewMessage
{
    /// <summary>Writes the payload (after the header).</summary>
    public static void Write(BitWriter writer, HostView view)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(view);
        writer.WriteUInt32(view.Epoch);
        writer.WriteShortString(view.Screen, HostView.MaxScreenBytes);
        writer.WriteUInt32(view.Checkpoint);
        writer.WriteVarUInt((ulong)view.Values.Count);
        foreach (KeyValuePair<string, NetValue> pair in view.Values)
        {
            writer.WriteShortString(pair.Key, HostView.MaxKeyBytes);
            pair.Value.Write(writer);
        }
    }

    /// <summary>Reads the payload. False for malformed data, an empty screen name, or repeated detail names. Never throws.</summary>
    public static bool TryRead(ref BitReader reader, out HostView? view)
    {
        view = null;
        uint epoch = reader.ReadUInt32();
        string screen = reader.ReadShortString(HostView.MaxScreenBytes);
        uint checkpoint = reader.ReadUInt32();
        ulong count = reader.ReadVarUInt();
        if (reader.IsOverrun || count > HostView.MaxValues || !IsName(screen))
        {
            return false;
        }

        var values = new List<KeyValuePair<string, NetValue>>((int)count);
        var keys = new HashSet<string>(StringComparer.Ordinal);
        for (ulong i = 0; i < count; i++)
        {
            string key = reader.ReadShortString(HostView.MaxKeyBytes);
            if (reader.IsOverrun || !IsName(key) || !keys.Add(key) || !NetValue.TryRead(ref reader, out NetValue value))
            {
                return false;
            }

            if (value.Kind == NetValueKind.Text)
            {
                value = NetValue.FromString(SafeText.Clean(value.Text, NetValue.MaxStringBytes));
            }

            values.Add(new KeyValuePair<string, NetValue>(key, value));
        }

        if (reader.IsOverrun)
        {
            return false;
        }

        view = new HostView(epoch, screen, checkpoint, values);
        return true;
    }

    /// <summary>Printable ASCII without spaces at either end: screen and detail names are the mod's own identifiers.</summary>
    internal static bool IsName(string name)
    {
        if (name.Length == 0 || name[0] == ' ' || name[^1] == ' ')
        {
            return false;
        }

        foreach (char c in name)
        {
            if (c is < ' ' or > '~')
            {
                return false;
            }
        }

        return true;
    }
}
