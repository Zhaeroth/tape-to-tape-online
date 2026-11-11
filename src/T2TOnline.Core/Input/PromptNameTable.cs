using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace T2TOnline.Core.Input;

/// <summary>
/// One button prompt's name in a friend's own controls: what the game's own matcher (<c>GetRawInputNameFromMaps</c>)
/// named action <see cref="ActionId"/> over <see cref="Range"/> from the friend's maps, and the modifier keys it added.
/// </summary>
/// <param name="ActionId">The game's Rewired action id.</param>
/// <param name="Range">The part of the action asked for (Rewired's <c>AxisRange</c>).</param>
/// <param name="Name">The raw input name ("Right Trigger", "Space"): the prompt's sprite is looked up by it.</param>
/// <param name="Modifiers">The modifier keys' raw input names ("Left Shift"), in the matcher's order; usually none.</param>
public sealed record PromptName(ushort ActionId, BindingRange Range, string Name, IReadOnlyList<string> Modifiers);

/// <summary>
/// A friend's button prompt names for every action of the game's their own maps bind, on their own device family
/// (<see cref="Protocol.MessageType.PromptNames"/>): the host's prompts for that friend's game player are named from it,
/// as the friend's own couch would name them (their remaps, their pad). Made on the friend's PC with the game's own
/// matcher; on the host only looked up. Validated whole (<see cref="TryCreate"/>), so a table that exists is within every
/// cap.
/// </summary>
public sealed class PromptNameTable
{
    /// <summary>Most entries in a table (the game has 63 actions, three ranges each: about 190 when everything is bound).</summary>
    public const int MaxEntries = 512;

    /// <summary>Longest name, in bytes of printable ASCII.</summary>
    public const int MaxNameBytes = 48;

    /// <summary>Most modifier keys per entry.</summary>
    public const int MaxModifiers = 3;

    /// <summary>Longest modifier name, in bytes of printable ASCII.</summary>
    public const int MaxModifierBytes = 24;

    /// <summary>The highest device family a table may be for (the game's <c>EInputDeviceType</c>: 0 keyboard and mouse, 1-4 pad families).</summary>
    public const byte MaxFamily = 4;

    private readonly Dictionary<int, PromptName> _byKey;

    private PromptNameTable(byte family, ushort generation, List<PromptName> entries, Dictionary<int, PromptName> byKey)
    {
        Family = family;
        Generation = generation;
        Entries = entries;
        _byKey = byKey;
    }

    /// <summary>The device family the names are for (the game's <c>EInputDeviceType</c>).</summary>
    public byte Family { get; }

    /// <summary>The table's number on the friend's PC (1 and up, a new one each time the names are made again), for the logs on both PCs.</summary>
    public ushort Generation { get; }

    /// <summary>Every entry, in the order made.</summary>
    public IReadOnlyList<PromptName> Entries { get; }

    /// <summary>The entries' modifier keys in all.</summary>
    public int ModifierCount
    {
        get
        {
            int count = 0;
            foreach (PromptName entry in Entries)
            {
                count += entry.Modifiers.Count;
            }

            return count;
        }
    }

    /// <summary>The name of <paramref name="actionId"/> over <paramref name="range"/>, or false when the friend's maps bind nothing there.</summary>
    public bool TryGet(int actionId, BindingRange range, out PromptName? name) => _byKey.TryGetValue(Key(actionId, range), out name);

    /// <summary>
    /// A table of <paramref name="entries"/>, or false when any is invalid: a family past <see cref="MaxFamily"/>, generation
    /// 0, more than <see cref="MaxEntries"/> entries, an action id past <see cref="InputBinding.MaxActionId"/>, a range past
    /// Negative, a name or modifier that isn't 1 to its cap bytes of printable ASCII, more than <see cref="MaxModifiers"/>
    /// modifiers, or the same action and range twice.
    /// </summary>
    public static bool TryCreate(byte family, ushort generation, IEnumerable<PromptName> entries, out PromptNameTable? table)
    {
        ArgumentNullException.ThrowIfNull(entries);
        table = null;
        if (family > MaxFamily || generation == 0)
        {
            return false;
        }

        var list = new List<PromptName>();
        var byKey = new Dictionary<int, PromptName>();
        foreach (PromptName entry in entries)
        {
            if (entry == null || list.Count >= MaxEntries || !IsValid(entry) || !byKey.TryAdd(Key(entry.ActionId, entry.Range), entry))
            {
                return false;
            }

            list.Add(entry);
        }

        table = new PromptNameTable(family, generation, list, byKey);
        return true;
    }

    /// <summary>True for an entry within every cap (see <see cref="TryCreate"/>).</summary>
    public static bool IsValid(PromptName entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.ActionId > InputBinding.MaxActionId || entry.Range > BindingRange.Negative || !IsText(entry.Name, MaxNameBytes) ||
            entry.Modifiers == null || entry.Modifiers.Count > MaxModifiers)
        {
            return false;
        }

        foreach (string modifier in entry.Modifiers)
        {
            if (!IsText(modifier, MaxModifierBytes))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>True for 1 to <paramref name="maxBytes"/> characters of printable ASCII (space to tilde): what a raw input name is.</summary>
    public static bool IsText([NotNullWhen(true)] string? text, int maxBytes)
    {
        if (string.IsNullOrEmpty(text) || text.Length > maxBytes)
        {
            return false;
        }

        foreach (char c in text)
        {
            if (c is < ' ' or > '~')
            {
                return false;
            }
        }

        return true;
    }

    private static int Key(int actionId, BindingRange range) => (actionId << 2) | (int)range;
}

/// <summary>
/// Builds a <see cref="PromptNameTable"/> from the names the game's matcher gives, one by one (the friend's PC): what a
/// table can't carry is skipped and counted, never thrown, and the table stays within <see cref="Protocol.PromptNamesMessage.MaxPayloadBytes"/>.
/// </summary>
public sealed class PromptNameTableBuilder
{
    private readonly List<PromptName> _entries = new();
    private readonly HashSet<int> _keys = new();
    private int _bytes = Protocol.PromptNamesMessage.HeaderBytes;

    /// <summary>Entries kept so far.</summary>
    public int Count => _entries.Count;

    /// <summary>Names skipped because they aren't 1 to 48 bytes of printable ASCII (or their action id or range is out of range).</summary>
    public int SkippedText { get; private set; }

    /// <summary>Names skipped because the table was full (<see cref="PromptNameTable.MaxEntries"/>, or the payload cap).</summary>
    public int SkippedFull { get; private set; }

    /// <summary>Names skipped because the same action and range was named already.</summary>
    public int SkippedRepeat { get; private set; }

    /// <summary>Modifier keys left off an entry (past <see cref="PromptNameTable.MaxModifiers"/>, or not printable ASCII).</summary>
    public int ModifiersDropped { get; private set; }

    /// <summary>The payload's size so far, in bytes.</summary>
    public int Bytes => _bytes;

    /// <summary>Adds one name; false (counted) when it's skipped.</summary>
    public bool Add(int actionId, BindingRange range, string? name, IEnumerable<string>? modifiers)
    {
        if (actionId is < 0 or > InputBinding.MaxActionId || range > BindingRange.Negative || !PromptNameTable.IsText(name, PromptNameTable.MaxNameBytes))
        {
            SkippedText++;
            return false;
        }

        var kept = new List<string>();
        foreach (string modifier in modifiers ?? Array.Empty<string>())
        {
            if (kept.Count < PromptNameTable.MaxModifiers && PromptNameTable.IsText(modifier, PromptNameTable.MaxModifierBytes))
            {
                kept.Add(modifier);
            }
            else
            {
                ModifiersDropped++;
            }
        }

        var entry = new PromptName((ushort)actionId, range, name, kept);
        int size = Protocol.PromptNamesMessage.EntryBytes(entry);
        if (_entries.Count >= PromptNameTable.MaxEntries || _bytes + size > Protocol.PromptNamesMessage.MaxPayloadBytes)
        {
            SkippedFull++;
            return false;
        }

        if (!_keys.Add((actionId << 2) | (int)range))
        {
            SkippedRepeat++;
            return false;
        }

        _entries.Add(entry);
        _bytes += size;
        return true;
    }

    /// <summary>The table, for device family <paramref name="family"/> (0-4), numbered <paramref name="generation"/> (1 and up).</summary>
    public PromptNameTable Build(byte family, ushort generation)
    {
        if (!PromptNameTable.TryCreate(family, generation, _entries, out PromptNameTable? table))
        {
            throw new ArgumentOutOfRangeException(nameof(family), "A family of 0 to 4 and a generation from 1.");
        }

        return table!;
    }
}
