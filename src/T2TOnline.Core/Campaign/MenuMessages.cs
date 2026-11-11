using System;
using System.Collections.Generic;
using System.Linq;
using T2TOnline.Core.Diagnostics;
using T2TOnline.Core.Protocol;
using T2TOnline.Core.Serialization;
using T2TOnline.Core.Timing;

namespace T2TOnline.Core.Campaign;

/// <summary>How a menu element is named the same way on two PCs. Values are part of the protocol: only add.</summary>
public enum MenuTargetKind : byte
{
    /// <summary>Nothing (no selection in the menu).</summary>
    None = 0,

    /// <summary>
    /// The element's place under the menu's root: each step's sibling index, and the element's own object name as a
    /// check. Stable when both PCs built the menu from the same data (its children made in data order).
    /// </summary>
    Path = 1,

    /// <summary>
    /// The element's place in the menu's data: a group (the menu's list, or a part of its state) and an index into it.
    /// For pooled lists, which recycle their children, and for anything that isn't an object at all.
    /// </summary>
    Item = 2,
}

/// <summary>
/// A menu element (a button, a card, a list selector, a part of the menu's state) named so that the friend's copy of the
/// menu can find its own: see <see cref="MenuTargetKind"/>. Immutable; compared by value.
/// </summary>
public sealed class MenuTarget : IEquatable<MenuTarget>
{
    /// <summary>Deepest path, in steps from the menu's root.</summary>
    public const int MaxDepth = 32;

    /// <summary>Largest sibling index in a path.</summary>
    public const int MaxSiblingIndex = 4095;

    /// <summary>Longest leaf name, in UTF-8 bytes (longer names are cut when written).</summary>
    public const int MaxLeafBytes = 64;

    /// <summary>Longest group name, in UTF-8 bytes.</summary>
    public const int MaxGroupBytes = 32;

    /// <summary>Largest item index.</summary>
    public const int MaxIndex = 1023;

    private readonly int[] _path;

    private MenuTarget(MenuTargetKind kind, int[] path, string leaf, string group, int index)
    {
        Kind = kind;
        _path = path;
        Leaf = leaf;
        Group = group;
        Index = index;
    }

    /// <summary>No element.</summary>
    public static MenuTarget None { get; } = new(MenuTargetKind.None, Array.Empty<int>(), string.Empty, string.Empty, 0);

    /// <summary>What kind of name this is.</summary>
    public MenuTargetKind Kind { get; }

    /// <summary><see cref="MenuTargetKind.Path"/>: the sibling index of each step from the menu's root.</summary>
    public IReadOnlyList<int> Path => _path;

    /// <summary><see cref="MenuTargetKind.Path"/>: the element's object name, a check that the path found the same element.</summary>
    public string Leaf { get; }

    /// <summary><see cref="MenuTargetKind.Item"/>: which list or part of the menu.</summary>
    public string Group { get; }

    /// <summary><see cref="MenuTargetKind.Item"/>: the index in it.</summary>
    public int Index { get; }

    /// <summary>
    /// The element at <paramref name="path"/> (sibling indices from the menu's root) named <paramref name="leaf"/>. Throws
    /// for an empty or too deep path or an index out of range (the leaf name is cut to <see cref="MaxLeafBytes"/> when
    /// written).
    /// </summary>
    public static MenuTarget AtPath(IReadOnlyList<int> path, string leaf)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(leaf);
        if (path.Count == 0 || path.Count > MaxDepth)
        {
            throw new ArgumentException($"A path has 1 to {MaxDepth} steps.", nameof(path));
        }

        int[] steps = path.ToArray();
        if (steps.Any(s => s < 0 || s > MaxSiblingIndex))
        {
            throw new ArgumentException($"Sibling indices are 0 to {MaxSiblingIndex}.", nameof(path));
        }

        return new MenuTarget(MenuTargetKind.Path, steps, leaf, string.Empty, 0);
    }

    /// <summary>Item <paramref name="index"/> of <paramref name="group"/>. Throws for a group that isn't a name, or an index out of range.</summary>
    public static MenuTarget AtItem(string group, int index)
    {
        ArgumentNullException.ThrowIfNull(group);
        if (!MenuNames.IsName(group, MaxGroupBytes))
        {
            throw new ArgumentException($"A group is printable ASCII, 1 to {MaxGroupBytes} bytes.", nameof(group));
        }

        if (index < 0 || index > MaxIndex)
        {
            throw new ArgumentOutOfRangeException(nameof(index), $"Indices are 0 to {MaxIndex}.");
        }

        return new MenuTarget(MenuTargetKind.Item, Array.Empty<int>(), string.Empty, group, index);
    }

    /// <inheritdoc />
    public bool Equals(MenuTarget? other) =>
        other is not null && Kind == other.Kind && Index == other.Index && Leaf == other.Leaf && Group == other.Group &&
        _path.AsSpan().SequenceEqual(other._path);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is MenuTarget other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = default(HashCode);
        hash.Add(Kind);
        hash.Add(Index);
        hash.Add(Leaf, StringComparer.Ordinal);
        hash.Add(Group, StringComparer.Ordinal);
        foreach (int step in _path)
        {
            hash.Add(step);
        }

        return hash.ToHashCode();
    }

    /// <summary>
    /// For logs: "none", "3/0/2 (Card)" or "relic#1"; the leaf and the group are a host's names, so they go by
    /// <see cref="HostText.KeyForLog(string?)"/> (an element's own name, <c>RelicDetails(Clone)</c>, as it is).
    /// </summary>
    public override string ToString() => Kind switch
    {
        MenuTargetKind.Path => $"{string.Join("/", _path)} ({(Leaf.Length == 0 ? string.Empty : HostText.KeyForLog(Leaf))})",
        MenuTargetKind.Item => $"{HostText.KeyForLog(Group)}#{Index}",
        _ => "none",
    };

    /// <summary>Writes the target.</summary>
    internal void Write(BitWriter writer)
    {
        writer.WriteByte((byte)Kind);
        switch (Kind)
        {
            case MenuTargetKind.Path:
                writer.WriteVarUInt((ulong)_path.Length);
                foreach (int step in _path)
                {
                    writer.WriteVarUInt((ulong)step);
                }

                writer.WriteShortString(Leaf, MaxLeafBytes);
                break;
            case MenuTargetKind.Item:
                writer.WriteShortString(Group, MaxGroupBytes);
                writer.WriteVarUInt((ulong)Index);
                break;
        }
    }

    /// <summary>Reads a target. False for an unknown kind, a path out of range, a bad group, or truncated data. Never throws.</summary>
    internal static bool TryRead(ref BitReader reader, out MenuTarget target)
    {
        target = None;
        var kind = (MenuTargetKind)reader.ReadByte();
        switch (kind)
        {
            case MenuTargetKind.None:
                return !reader.IsOverrun;
            case MenuTargetKind.Path:
                ulong depth = reader.ReadVarUInt();
                if (reader.IsOverrun || depth == 0 || depth > MaxDepth)
                {
                    return false;
                }

                int[] path = new int[(int)depth];
                for (int i = 0; i < path.Length; i++)
                {
                    ulong step = reader.ReadVarUInt();
                    if (reader.IsOverrun || step > MaxSiblingIndex)
                    {
                        return false;
                    }

                    path[i] = (int)step;
                }

                string leaf = SafeText.Clean(reader.ReadShortString(MaxLeafBytes), MaxLeafBytes);
                if (reader.IsOverrun)
                {
                    return false;
                }

                target = new MenuTarget(MenuTargetKind.Path, path, leaf, string.Empty, 0);
                return true;
            case MenuTargetKind.Item:
                string group = reader.ReadShortString(MaxGroupBytes);
                ulong index = reader.ReadVarUInt();
                if (reader.IsOverrun || !MenuNames.IsName(group, MaxGroupBytes) || index > MaxIndex)
                {
                    return false;
                }

                target = new MenuTarget(MenuTargetKind.Item, Array.Empty<int>(), string.Empty, group, (int)index);
                return true;
            default:
                return false;
        }
    }
}

/// <summary>One row of a menu's data (an offer, a choice, a line of the host's screen): a group name and named values.</summary>
public sealed class MenuItem
{
    /// <summary>An item of <paramref name="group"/> with <paramref name="values"/> (copied). Throws for bad names, repeated or too many values.</summary>
    public MenuItem(string group, IReadOnlyList<KeyValuePair<string, NetValue>> values)
    {
        ArgumentNullException.ThrowIfNull(group);
        if (!MenuNames.IsName(group, MenuData.MaxKeyBytes))
        {
            throw new ArgumentException($"A group is printable ASCII, 1 to {MenuData.MaxKeyBytes} bytes.", nameof(group));
        }

        Group = group;
        Values = MenuNames.Checked(values, MenuData.MaxItemValues, nameof(values));
    }

    /// <summary>Which list of the menu's data this row is in.</summary>
    public string Group { get; }

    /// <summary>The row's values, in the order given.</summary>
    public IReadOnlyList<KeyValuePair<string, NetValue>> Values { get; }

    /// <summary>The value called <paramref name="key"/>, or false.</summary>
    public bool TryGet(string key, out NetValue value) => MenuNames.TryGet(Values, key, out value);

    /// <summary>The value called <paramref name="key"/> as text (empty when absent or not text).</summary>
    public string Text(string key) => TryGet(key, out NetValue value) && value.Kind == NetValueKind.Text ? value.Text ?? string.Empty : string.Empty;

    /// <summary>The value called <paramref name="key"/> as a whole number (<paramref name="fallback"/> when absent or not one).</summary>
    public long Whole(string key, long fallback = 0) => TryGet(key, out NetValue value) && value.Kind == NetValueKind.Whole ? value.WholeValue : fallback;

    /// <summary>The value called <paramref name="key"/> as a flag (<paramref name="fallback"/> when absent or not one).</summary>
    public bool Flag(string key, bool fallback = false) => TryGet(key, out NetValue value) && value.Kind == NetValueKind.Flag ? value.WholeValue != 0 : fallback;
}

/// <summary>
/// A menu's data as the host's game opened it: named values (the menu's settings) and rows (its offers, choices or the
/// lines of the host's screen). The names belong to the game glue; Core only carries, limits and compares them.
/// </summary>
public sealed class MenuData
{
    /// <summary>Most values.</summary>
    public const int MaxValues = 32;

    /// <summary>Most rows.</summary>
    public const int MaxItems = 64;

    /// <summary>
    /// Most values in one row (a controller screen's player row has <see cref="ControllerScreenRow.ValueCount"/>; the limit was 8 in
    /// earlier versions, which that row never fit).
    /// </summary>
    public const int MaxItemValues = 16;

    /// <summary>Longest value name or row group, in UTF-8 bytes.</summary>
    public const int MaxKeyBytes = 32;

    /// <summary>Menu data with nothing in it.</summary>
    public static MenuData Empty { get; } = new(Array.Empty<KeyValuePair<string, NetValue>>(), Array.Empty<MenuItem>());

    /// <summary>The data. Values and rows are copied, in order. Throws for bad or repeated names, or too many of anything.</summary>
    public MenuData(IReadOnlyList<KeyValuePair<string, NetValue>> values, IReadOnlyList<MenuItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        Values = MenuNames.Checked(values, MaxValues, nameof(values));
        if (items.Count > MaxItems)
        {
            throw new ArgumentException($"At most {MaxItems} rows.", nameof(items));
        }

        var rows = new List<MenuItem>(items.Count);
        foreach (MenuItem item in items)
        {
            rows.Add(item ?? throw new ArgumentException("A row is null.", nameof(items)));
        }

        Items = rows;
        var writer = new BitWriter(512);
        Write(writer);
        ContentHash = Fnv.Hash(writer.AsSpan());
        ByteLength = writer.ByteLength;
    }

    /// <summary>The values, in the order given.</summary>
    public IReadOnlyList<KeyValuePair<string, NetValue>> Values { get; }

    /// <summary>The rows, in the order given.</summary>
    public IReadOnlyList<MenuItem> Items { get; }

    /// <summary>A hash of everything in it: equal hashes, same data.</summary>
    public ulong ContentHash { get; }

    /// <summary>The bytes it takes in a message (what sending it again costs the link).</summary>
    public int ByteLength { get; }

    /// <summary>The rows of <paramref name="group"/>, in order.</summary>
    public IEnumerable<MenuItem> Group(string group) => Items.Where(i => i.Group == group);

    /// <summary>The value called <paramref name="key"/>, or false.</summary>
    public bool TryGet(string key, out NetValue value) => MenuNames.TryGet(Values, key, out value);

    /// <summary>The value called <paramref name="key"/> as text (empty when absent or not text).</summary>
    public string Text(string key) => TryGet(key, out NetValue value) && value.Kind == NetValueKind.Text ? value.Text ?? string.Empty : string.Empty;

    /// <summary>The value called <paramref name="key"/> as a whole number (<paramref name="fallback"/> when absent or not one).</summary>
    public long Whole(string key, long fallback = 0) => TryGet(key, out NetValue value) && value.Kind == NetValueKind.Whole ? value.WholeValue : fallback;

    /// <summary>The value called <paramref name="key"/> as a flag (<paramref name="fallback"/> when absent or not one).</summary>
    public bool Flag(string key, bool fallback = false) => TryGet(key, out NetValue value) && value.Kind == NetValueKind.Flag ? value.WholeValue != 0 : fallback;

    /// <summary>Writes the data.</summary>
    internal void Write(BitWriter writer)
    {
        MenuNames.WriteValues(writer, Values);
        writer.WriteVarUInt((ulong)Items.Count);
        foreach (MenuItem item in Items)
        {
            writer.WriteShortString(item.Group, MaxKeyBytes);
            MenuNames.WriteValues(writer, item.Values);
        }
    }

    /// <summary>Reads data. False for bad or repeated names, too many of anything, or truncated data. Never throws.</summary>
    internal static bool TryRead(ref BitReader reader, out MenuData data)
    {
        data = Empty;
        if (!MenuNames.TryReadValues(ref reader, MaxValues, out List<KeyValuePair<string, NetValue>> values))
        {
            return false;
        }

        ulong count = reader.ReadVarUInt();
        if (reader.IsOverrun || count > MaxItems)
        {
            return false;
        }

        var items = new List<MenuItem>((int)count);
        for (ulong i = 0; i < count; i++)
        {
            string group = reader.ReadShortString(MaxKeyBytes);
            if (reader.IsOverrun || !MenuNames.IsName(group, MaxKeyBytes) ||
                !MenuNames.TryReadValues(ref reader, MaxItemValues, out List<KeyValuePair<string, NetValue>> row))
            {
                return false;
            }

            items.Add(new MenuItem(group, row));
        }

        data = new MenuData(values, items);
        return true;
    }
}

/// <summary>
/// A menu open on the host's screen: the host's number for it (one per opening), its id (the game's menu definition
/// name), its class, the scene it opened in, and its data, at a version that goes up with each change.
/// </summary>
public sealed class MenuState
{
    /// <summary>Longest menu id, class name or scene name, in UTF-8 bytes.</summary>
    public const int MaxNameBytes = 64;

    /// <summary>The state. Throws for a zero serial, an id or class that isn't a name, or a scene that isn't empty or a name.</summary>
    public MenuState(uint serial, string id, string type, string scene, uint version, MenuData data)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(data);
        if (serial == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(serial), "Serial 0 is no menu.");
        }

        if (!MenuNames.IsName(id, MaxNameBytes) || !MenuNames.IsName(type, MaxNameBytes) || (scene.Length > 0 && !MenuNames.IsName(scene, MaxNameBytes)))
        {
            throw new ArgumentException($"Ids, classes and scenes are printable ASCII, at most {MaxNameBytes} bytes.");
        }

        Serial = serial;
        Id = id;
        Type = type;
        Scene = scene;
        Version = version;
        Data = data;
    }

    /// <summary>The host's number for this opening of the menu (never 0).</summary>
    public uint Serial { get; }

    /// <summary>The menu's id: its definition's name (<c>RelicRewardMenu</c>, <c>EventChoice</c>, ...).</summary>
    public string Id { get; }

    /// <summary>The menu's class name.</summary>
    public string Type { get; }

    /// <summary>The scene the host's game had loaded when the menu opened (empty when unknown).</summary>
    public string Scene { get; }

    /// <summary>The data's version: 0 when opened, one more with each change (wrapping).</summary>
    public uint Version { get; }

    /// <summary>The menu's data.</summary>
    public MenuData Data { get; }

    /// <summary>The same menu with new data, one version on.</summary>
    public MenuState With(MenuData data) => new(Serial, Id, Type, Scene, Version + 1, data);

    /// <summary>For logs: "RelicRewardMenu #3"; the id is the host's, so it goes by <see cref="HostText.KeyForLog(string?)"/>.</summary>
    public override string ToString() => $"{HostText.KeyForLog(Id)} #{Serial}";
}

/// <summary>What one menu message says. Values are part of the protocol: only add.</summary>
public enum MenuEventKind : byte
{
    /// <summary>Not a menu event.</summary>
    None = 0,

    /// <summary>A menu opened, with its data (also resent to a friend who joins while it's open).</summary>
    Opened = 1,

    /// <summary>An open menu's data changed (its offers were rerolled, its choices moved on).</summary>
    Updated = 2,

    /// <summary>A menu closed.</summary>
    Closed = 3,

    /// <summary>The selection moved to an element of an open menu (the game has one shared cursor).</summary>
    Selected = 4,

    /// <summary>A list selector (or a part of the menu's state) took a value.</summary>
    ValueSet = 5,

    /// <summary>An element was clicked, with the game's result and effect (feedback only: the friend never acts).</summary>
    Clicked = 6,

    /// <summary>
    /// The host's open menus are exactly these (the first thing a friend who joins or rejoins gets): any other menu a
    /// friend still holds closed while it was away.
    /// </summary>
    Synced = 7,
}

/// <summary>Why the host's menu closed (with <see cref="MenuEventKind.Closed"/>). Values are part of the protocol: only add.</summary>
public enum MenuCloseReason : byte
{
    /// <summary>Any close but the ones below (a pick, a confirm, a scene change, the session's end).</summary>
    Other = 0,

    /// <summary>The host's back input closed it (the host's game played its back sound).</summary>
    Back = 1,
}

/// <summary>
/// The transition a host menu's own exit showed (with <see cref="MenuEventKind.Closed"/>): the game's three "Woosh" menu
/// transitions (<c>EMenuTransition</c>'s statics). Values are part of the protocol: only add.
/// </summary>
public enum MenuTransition : byte
{
    /// <summary>None.</summary>
    None = 0,

    /// <summary><c>EMenuTransition.WooshProgress</c>: on to the next screen.</summary>
    Progress = 1,

    /// <summary><c>EMenuTransition.WooshLoading</c>: a load comes next (it blocks the UI's input while it's up).</summary>
    Loading = 2,

    /// <summary><c>EMenuTransition.WooshRegress</c>: back to the screen before.</summary>
    Regress = 3,
}

/// <summary>
/// One change to the host's menus, as one host-to-friend message (channel 1): <see cref="MenuEventKind.Opened"/>,
/// <see cref="MenuEventKind.Updated"/>, <see cref="MenuEventKind.Closed"/> and <see cref="MenuEventKind.Synced"/> as
/// <see cref="MessageType.MenuOpen"/>, <see cref="MenuEventKind.Selected"/> as <see cref="MessageType.MenuNav"/>,
/// <see cref="MenuEventKind.ValueSet"/> as <see cref="MessageType.MenuSelect"/>, <see cref="MenuEventKind.Clicked"/> as
/// <see cref="MessageType.MenuClick"/>. Immutable.
/// </summary>
public sealed class MenuEvent
{
    private MenuEvent(MenuEventKind kind, uint serial, MenuState? state, MenuTarget? target, int index, byte result, byte effect, uint[]? open = null,
        MenuCloseReason reason = MenuCloseReason.Other, MenuTransition transition = MenuTransition.None)
    {
        Kind = kind;
        Serial = serial;
        State = state;
        Target = target ?? MenuTarget.None;
        Index = index;
        Result = result;
        Effect = effect;
        OpenSerials = open ?? Array.Empty<uint>();
        Reason = reason;
        Transition = transition;
    }

    /// <summary>What happened.</summary>
    public MenuEventKind Kind { get; }

    /// <summary>The host's number for the menu it happened in (0 for <see cref="MenuEventKind.Synced"/>, which is about all of them).</summary>
    public uint Serial { get; }

    /// <summary><see cref="MenuEventKind.Synced"/>: the serials of every menu open on the host's screen, oldest first.</summary>
    public IReadOnlyList<uint> OpenSerials { get; }

    /// <summary><see cref="MenuEventKind.Opened"/> and <see cref="MenuEventKind.Updated"/>: the menu as it is now.</summary>
    public MenuState? State { get; }

    /// <summary>Selected, ValueSet and Clicked: the element.</summary>
    public MenuTarget Target { get; }

    /// <summary><see cref="MenuEventKind.ValueSet"/>: the value (an index into the selector's list, or a state number).</summary>
    public int Index { get; }

    /// <summary><see cref="MenuEventKind.Clicked"/>: the game's click result (its <c>EClickResult</c>).</summary>
    public byte Result { get; }

    /// <summary><see cref="MenuEventKind.Clicked"/>: the game's click effect (its <c>EClickFx</c>).</summary>
    public byte Effect { get; }

    /// <summary><see cref="MenuEventKind.Closed"/>: why the menu closed.</summary>
    public MenuCloseReason Reason { get; }

    /// <summary><see cref="MenuEventKind.Closed"/>: the transition the menu's own exit showed on the host's screen.</summary>
    public MenuTransition Transition { get; }

    /// <summary>The message type this event travels as.</summary>
    public MessageType Type => Kind switch
    {
        MenuEventKind.Selected => MessageType.MenuNav,
        MenuEventKind.ValueSet => MessageType.MenuSelect,
        MenuEventKind.Clicked => MessageType.MenuClick,
        _ => MessageType.MenuOpen,
    };

    /// <summary>A menu opened.</summary>
    public static MenuEvent Opened(MenuState state) => new(MenuEventKind.Opened, (state ?? throw new ArgumentNullException(nameof(state))).Serial, state, null, 0, 0, 0);

    /// <summary>An open menu's data changed.</summary>
    public static MenuEvent Updated(MenuState state) => new(MenuEventKind.Updated, (state ?? throw new ArgumentNullException(nameof(state))).Serial, state, null, 0, 0, 0);

    /// <summary>
    /// Menu <paramref name="serial"/> closed, because of <paramref name="reason"/>, its own exit showing
    /// <paramref name="transition"/>. Throws for a reason or transition this version doesn't know.
    /// </summary>
    public static MenuEvent Closed(uint serial, MenuCloseReason reason = MenuCloseReason.Other, MenuTransition transition = MenuTransition.None)
    {
        if (!IsKnown(reason) || !IsKnown(transition))
        {
            throw new ArgumentException("An unknown close reason or transition.");
        }

        return new(MenuEventKind.Closed, NonZero(serial), null, null, 0, 0, 0, null, reason, transition);
    }

    /// <summary>True for a close reason this version knows.</summary>
    public static bool IsKnown(MenuCloseReason reason) => reason is MenuCloseReason.Other or MenuCloseReason.Back;

    /// <summary>True for a transition this version knows.</summary>
    public static bool IsKnown(MenuTransition transition) => transition is >= MenuTransition.None and <= MenuTransition.Regress;

    /// <summary>The selection moved to <paramref name="target"/> in menu <paramref name="serial"/>.</summary>
    public static MenuEvent Selected(uint serial, MenuTarget target) =>
        new(MenuEventKind.Selected, NonZero(serial), null, target ?? throw new ArgumentNullException(nameof(target)), 0, 0, 0);

    /// <summary><paramref name="target"/> in menu <paramref name="serial"/> took value <paramref name="index"/> (0 to <see cref="MenuTarget.MaxIndex"/>).</summary>
    public static MenuEvent ValueSet(uint serial, MenuTarget target, int index)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (target.Kind == MenuTargetKind.None || index < 0 || index > MenuTarget.MaxIndex)
        {
            throw new ArgumentException($"A value needs an element and an index of 0 to {MenuTarget.MaxIndex}.");
        }

        return new(MenuEventKind.ValueSet, NonZero(serial), null, target, index, 0, 0);
    }

    /// <summary><paramref name="target"/> in menu <paramref name="serial"/> was clicked, with the game's result and effect.</summary>
    public static MenuEvent Clicked(uint serial, MenuTarget target, byte result, byte effect)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (target.Kind == MenuTargetKind.None)
        {
            throw new ArgumentException("A click needs an element.", nameof(target));
        }

        return new(MenuEventKind.Clicked, NonZero(serial), null, target, 0, result, effect);
    }

    /// <summary>
    /// The host's open menus are exactly <paramref name="open"/> (oldest first; at most <see cref="MenuBoard.MaxOpen"/>,
    /// none 0, none twice).
    /// </summary>
    public static MenuEvent Synced(IReadOnlyList<uint> open)
    {
        ArgumentNullException.ThrowIfNull(open);
        if (open.Count > MenuBoard.MaxOpen || open.Contains(0u) || open.Distinct().Count() != open.Count)
        {
            throw new ArgumentException($"At most {MenuBoard.MaxOpen} serials, none 0, each once.", nameof(open));
        }

        return new(MenuEventKind.Synced, 0, null, null, 0, 0, 0, open.ToArray());
    }

    /// <summary>For logs.</summary>
    public override string ToString() => Kind switch
    {
        MenuEventKind.Opened or MenuEventKind.Updated => $"{Kind} {State} v{State!.Version}",
        MenuEventKind.ValueSet => $"{Kind} #{Serial} {Target}={Index}",
        MenuEventKind.Clicked => $"{Kind} #{Serial} {Target} ({Result}/{Effect})",
        MenuEventKind.Selected => $"{Kind} #{Serial} {Target}",
        MenuEventKind.Synced => $"{Kind} ({OpenSerials.Count} open)",
        MenuEventKind.Closed when Reason != MenuCloseReason.Other || Transition != MenuTransition.None => $"{Kind} #{Serial} ({Reason}, {Transition})",
        _ => $"{Kind} #{Serial}",
    };

    private static uint NonZero(uint serial) => serial != 0 ? serial : throw new ArgumentOutOfRangeException(nameof(serial), "Serial 0 is no menu.");
}

/// <summary>
/// The menu messages' layouts (all after the header, all host to friend on channel 1):
/// <list type="bullet">
/// <item><see cref="MessageType.MenuOpen"/>: kind (1 opened, 2 updated, 3 closed, 7 synced); for synced, the count
/// (var-uint) and each open serial (32 bits); for the others the serial (32 bits); for closed then its reason and its
/// transition (a byte each: <see cref="MenuCloseReason"/>, <see cref="MenuTransition"/>); for opened and updated the id,
/// class and scene (short strings), the version (var-uint) and the data: its values (count, then name and
/// <see cref="NetValue"/> each) and rows (count, then group and values each).</item>
/// <item><see cref="MessageType.MenuNav"/>: serial, target.</item>
/// <item><see cref="MessageType.MenuSelect"/>: serial, target, value (var-uint).</item>
/// <item><see cref="MessageType.MenuClick"/>: serial, target, result (byte), effect (byte).</item>
/// </list>
/// A target is its kind (byte), then for a path its depth and sibling indices (var-uints) and leaf name, for an item its
/// group and index. Decoding checks every count, index, name and length, and never throws.
/// </summary>
public static class MenuMessages
{
    /// <summary>Largest menu message, in bytes (a full screen of the host's text lines fits easily).</summary>
    public const int MaxMessageBytes = 48 * 1024;

    /// <summary>Writes <paramref name="menuEvent"/>'s payload (after the header). False when it would be over <see cref="MaxMessageBytes"/>.</summary>
    public static bool Write(BitWriter writer, MenuEvent menuEvent)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(menuEvent);
        int start = writer.ByteLength;
        switch (menuEvent.Kind)
        {
            case MenuEventKind.Opened:
            case MenuEventKind.Updated:
                MenuState state = menuEvent.State!;
                writer.WriteByte((byte)menuEvent.Kind);
                writer.WriteUInt32(state.Serial);
                writer.WriteShortString(state.Id, MenuState.MaxNameBytes);
                writer.WriteShortString(state.Type, MenuState.MaxNameBytes);
                writer.WriteShortString(state.Scene, MenuState.MaxNameBytes);
                writer.WriteVarUInt(state.Version);
                state.Data.Write(writer);
                break;
            case MenuEventKind.Closed:
                writer.WriteByte((byte)MenuEventKind.Closed);
                writer.WriteUInt32(menuEvent.Serial);
                writer.WriteByte((byte)menuEvent.Reason);
                writer.WriteByte((byte)menuEvent.Transition);
                break;
            case MenuEventKind.Synced:
                writer.WriteByte((byte)MenuEventKind.Synced);
                writer.WriteVarUInt((ulong)menuEvent.OpenSerials.Count);
                foreach (uint serial in menuEvent.OpenSerials)
                {
                    writer.WriteUInt32(serial);
                }

                break;
            case MenuEventKind.Selected:
                writer.WriteUInt32(menuEvent.Serial);
                menuEvent.Target.Write(writer);
                break;
            case MenuEventKind.ValueSet:
                writer.WriteUInt32(menuEvent.Serial);
                menuEvent.Target.Write(writer);
                writer.WriteVarUInt((ulong)menuEvent.Index);
                break;
            case MenuEventKind.Clicked:
                writer.WriteUInt32(menuEvent.Serial);
                menuEvent.Target.Write(writer);
                writer.WriteByte(menuEvent.Result);
                writer.WriteByte(menuEvent.Effect);
                break;
            default:
                return false;
        }

        return writer.ByteLength - start <= MaxMessageBytes;
    }

    /// <summary>
    /// Reads a menu message of <paramref name="type"/> (its header already read). False for another type, a payload over
    /// <see cref="MaxMessageBytes"/> (the host never writes one), malformed data, serial 0, names that aren't names, values
    /// out of range, or anything left over. Never throws.
    /// </summary>
    public static bool TryRead(MessageType type, ref BitReader reader, out MenuEvent? menuEvent)
    {
        menuEvent = null;
        if (reader.RemainingBits > MaxMessageBytes * 8)
        {
            return false;
        }

        switch (type)
        {
            case MessageType.MenuOpen:
                return TryReadOpen(ref reader, out menuEvent);
            case MessageType.MenuNav:
            {
                uint serial = reader.ReadUInt32();
                if (serial == 0 || !MenuTarget.TryRead(ref reader, out MenuTarget target) || !AtEnd(ref reader))
                {
                    return false;
                }

                menuEvent = MenuEvent.Selected(serial, target);
                return true;
            }

            case MessageType.MenuSelect:
            {
                uint serial = reader.ReadUInt32();
                if (serial == 0 || !MenuTarget.TryRead(ref reader, out MenuTarget target) || target.Kind == MenuTargetKind.None)
                {
                    return false;
                }

                ulong index = reader.ReadVarUInt();
                if (reader.IsOverrun || index > MenuTarget.MaxIndex || !AtEnd(ref reader))
                {
                    return false;
                }

                menuEvent = MenuEvent.ValueSet(serial, target, (int)index);
                return true;
            }

            case MessageType.MenuClick:
            {
                uint serial = reader.ReadUInt32();
                if (serial == 0 || !MenuTarget.TryRead(ref reader, out MenuTarget target) || target.Kind == MenuTargetKind.None)
                {
                    return false;
                }

                byte result = reader.ReadByte();
                byte effect = reader.ReadByte();
                if (reader.IsOverrun || !AtEnd(ref reader))
                {
                    return false;
                }

                menuEvent = MenuEvent.Clicked(serial, target, result, effect);
                return true;
            }

            default:
                return false;
        }
    }

    private static bool TryReadOpen(ref BitReader reader, out MenuEvent? menuEvent)
    {
        menuEvent = null;
        var kind = (MenuEventKind)reader.ReadByte();
        if (kind == MenuEventKind.Synced)
        {
            return TryReadSynced(ref reader, out menuEvent);
        }

        uint serial = reader.ReadUInt32();
        if (reader.IsOverrun || serial == 0)
        {
            return false;
        }

        if (kind == MenuEventKind.Closed)
        {
            var reason = (MenuCloseReason)reader.ReadByte();
            var transition = (MenuTransition)reader.ReadByte();
            if (reader.IsOverrun || !MenuEvent.IsKnown(reason) || !MenuEvent.IsKnown(transition) || !AtEnd(ref reader))
            {
                return false;
            }

            menuEvent = MenuEvent.Closed(serial, reason, transition);
            return true;
        }

        if (kind is not (MenuEventKind.Opened or MenuEventKind.Updated))
        {
            return false;
        }

        string id = reader.ReadShortString(MenuState.MaxNameBytes);
        string menuType = reader.ReadShortString(MenuState.MaxNameBytes);
        string scene = reader.ReadShortString(MenuState.MaxNameBytes);
        ulong version = reader.ReadVarUInt();
        if (reader.IsOverrun || version > uint.MaxValue || !MenuNames.IsName(id, MenuState.MaxNameBytes) ||
            !MenuNames.IsName(menuType, MenuState.MaxNameBytes) || (scene.Length > 0 && !MenuNames.IsName(scene, MenuState.MaxNameBytes)) ||
            !MenuData.TryRead(ref reader, out MenuData data) || !AtEnd(ref reader))
        {
            return false;
        }

        var state = new MenuState(serial, id, menuType, scene, (uint)version, data);
        menuEvent = kind == MenuEventKind.Opened ? MenuEvent.Opened(state) : MenuEvent.Updated(state);
        return true;
    }

    private static bool TryReadSynced(ref BitReader reader, out MenuEvent? menuEvent)
    {
        menuEvent = null;
        ulong count = reader.ReadVarUInt();
        if (reader.IsOverrun || count > MenuBoard.MaxOpen)
        {
            return false;
        }

        var open = new List<uint>((int)count);
        for (ulong i = 0; i < count; i++)
        {
            uint serial = reader.ReadUInt32();
            if (reader.IsOverrun || serial == 0 || open.Contains(serial))
            {
                return false;
            }

            open.Add(serial);
        }

        if (!AtEnd(ref reader))
        {
            return false;
        }

        menuEvent = MenuEvent.Synced(open);
        return true;
    }

    /// <summary>True when nothing but the last byte's padding is left (a longer message isn't one of ours).</summary>
    private static bool AtEnd(ref BitReader reader) => !reader.IsOverrun && reader.RemainingBits < 8;
}

/// <summary>
/// The host's side of the menu views: the menus open on the host's screen (each numbered by its opening), their data, the
/// selection and each list selector's value. Each change gives the <see cref="MenuEvent"/> to send (null when nothing
/// changed, or for a menu it doesn't know); <see cref="Snapshot"/> gives a friend who joins everything open, in order.
/// Game-independent. Not thread-safe (one game thread).
/// </summary>
public sealed class MenuBoard
{
    /// <summary>Most menus open at once (a menu, a prompt over it, a menu over that...). More are not tracked.</summary>
    public const int MaxOpen = 12;

    /// <summary>Most list selectors remembered per menu.</summary>
    public const int MaxValuesPerMenu = 32;

    private readonly List<Entry> _open = new();
    private uint _lastSerial;

    /// <summary>The menus open now, oldest first.</summary>
    public IReadOnlyList<MenuState> Open => _open.Select(e => e.State).ToList();

    /// <summary>
    /// A menu opened: it gets the next serial. Returns its <see cref="MenuEventKind.Opened"/> event, or null when
    /// <see cref="MaxOpen"/> are already open.
    /// </summary>
    public MenuEvent? Opened(string id, string type, string scene, MenuData data)
    {
        if (_open.Count >= MaxOpen)
        {
            return null;
        }

        _lastSerial++;
        if (_lastSerial == 0)
        {
            _lastSerial = 1;
        }

        var state = new MenuState(_lastSerial, id, type, scene, 0, data);
        _open.Add(new Entry(state));
        return MenuEvent.Opened(state);
    }

    /// <summary>Menu <paramref name="serial"/>'s data is now <paramref name="data"/>. Null when unknown or unchanged.</summary>
    public MenuEvent? Updated(uint serial, MenuData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        Entry? entry = Find(serial);
        if (entry == null || entry.State.Data.ContentHash == data.ContentHash)
        {
            return null;
        }

        entry.State = entry.State.With(data);
        return MenuEvent.Updated(entry.State);
    }

    /// <summary>
    /// Menu <paramref name="serial"/> closed, because of <paramref name="reason"/>, its own exit showing
    /// <paramref name="transition"/>. Null when unknown.
    /// </summary>
    public MenuEvent? Closed(uint serial, MenuCloseReason reason = MenuCloseReason.Other, MenuTransition transition = MenuTransition.None)
    {
        Entry? entry = Find(serial);
        if (entry == null)
        {
            return null;
        }

        _open.Remove(entry);
        return MenuEvent.Closed(serial, reason, transition);
    }

    /// <summary>The selection moved to <paramref name="target"/> in menu <paramref name="serial"/>. Null when unknown or where it was.</summary>
    public MenuEvent? Selected(uint serial, MenuTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        Entry? entry = Find(serial);
        if (entry == null || target.Equals(entry.Selection))
        {
            return null;
        }

        entry.Selection = target;
        return MenuEvent.Selected(serial, target);
    }

    /// <summary>
    /// <paramref name="target"/> in menu <paramref name="serial"/> took value <paramref name="index"/>. Null when unknown,
    /// unchanged, or the menu has too many. Throws, with nothing kept, for no element or an index outside 0 to
    /// <see cref="MenuTarget.MaxIndex"/> (a value kept would make every later <see cref="Snapshot"/> throw).
    /// </summary>
    public MenuEvent? ValueSet(uint serial, MenuTarget target, int index)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (target.Kind == MenuTargetKind.None || index < 0 || index > MenuTarget.MaxIndex)
        {
            throw new ArgumentException($"A value needs an element and an index of 0 to {MenuTarget.MaxIndex}.", nameof(index));
        }

        Entry? entry = Find(serial);
        if (entry == null || (entry.Values.TryGetValue(target, out int held) && held == index) ||
            (!entry.Values.ContainsKey(target) && entry.Values.Count >= MaxValuesPerMenu))
        {
            return null;
        }

        if (!entry.Values.ContainsKey(target))
        {
            entry.ValueOrder.Add(target);
        }

        entry.Values[target] = index;
        return MenuEvent.ValueSet(serial, target, index);
    }

    /// <summary><paramref name="target"/> in menu <paramref name="serial"/> was clicked. Null when unknown (clicks aren't kept).</summary>
    public MenuEvent? Clicked(uint serial, MenuTarget target, byte result, byte effect) =>
        Find(serial) == null ? null : MenuEvent.Clicked(serial, target, result, effect);

    /// <summary>The menu open with <paramref name="serial"/>, or false.</summary>
    public bool TryGet(uint serial, out MenuState state)
    {
        state = Find(serial)?.State!;
        return state != null;
    }

    /// <summary>
    /// Everything a friend who joins now needs, in order: which menus are open (a friend who rejoins closes any other it
    /// still holds), then each open menu (oldest first) as opened with its current data, its values and its selection.
    /// </summary>
    public IReadOnlyList<MenuEvent> Snapshot()
    {
        var events = new List<MenuEvent> { MenuEvent.Synced(_open.Select(e => e.State.Serial).ToList()) };
        foreach (Entry entry in _open)
        {
            events.Add(MenuEvent.Opened(entry.State));
            foreach (MenuTarget target in entry.ValueOrder)
            {
                events.Add(MenuEvent.ValueSet(entry.State.Serial, target, entry.Values[target]));
            }

            if (entry.Selection != null)
            {
                events.Add(MenuEvent.Selected(entry.State.Serial, entry.Selection));
            }
        }

        return events;
    }

    /// <summary>Forgets every menu (hosting stopped). Serials go on from where they were.</summary>
    public void Clear() => _open.Clear();

    private Entry? Find(uint serial) => serial == 0 ? null : _open.Find(e => e.State.Serial == serial);

    private sealed class Entry
    {
        public Entry(MenuState state) => State = state;

        public MenuState State { get; set; }

        public MenuTarget? Selection { get; set; }

        public Dictionary<MenuTarget, int> Values { get; } = new();

        public List<MenuTarget> ValueOrder { get; } = new();
    }
}

/// <summary>
/// Paces the host's menu updates by their size: a budget of message bytes that refills at <see cref="BytesPerSecond"/> up
/// to <see cref="BurstBytes"/>. A menu whose text changes all the time (a clock, a count running up) can't flood the link,
/// while a small menu's changes still go out at once. Updates that must go out (new offers) are charged without being
/// held, so they delay the next held ones instead. Game-independent. Not thread-safe (one game thread).
/// </summary>
public sealed class MenuPacer
{
    /// <summary>Most bytes that go out back to back.</summary>
    public const int BurstBytes = 32 * 1024;

    /// <summary>The budget's refill: what a menu changing all the time costs each friend's link at most (64 kbps).</summary>
    public const int BytesPerSecond = 8 * 1024;

    private double _budget = BurstBytes;
    private double _at = double.NaN;

    /// <summary>Updates held back so far (each one tried again later with whatever the menu says then).</summary>
    public long Held { get; private set; }

    /// <summary>
    /// True when an update of <paramref name="bytes"/> may go out at <paramref name="nowSeconds"/> (charged); false when it
    /// should be held. With a full budget anything goes, even an update bigger than the burst.
    /// </summary>
    public bool TrySend(int bytes, double nowSeconds)
    {
        Refill(nowSeconds);
        if (_budget < Math.Min(Math.Max(bytes, 0), BurstBytes))
        {
            Held++;
            return false;
        }

        _budget -= Math.Max(bytes, 0);
        return true;
    }

    /// <summary>An update of <paramref name="bytes"/> that goes out whatever the budget (the budget may go below zero).</summary>
    public void Charge(int bytes, double nowSeconds)
    {
        Refill(nowSeconds);
        _budget -= Math.Max(bytes, 0);
    }

    private void Refill(double nowSeconds)
    {
        if (!double.IsNaN(_at) && nowSeconds > _at)
        {
            _budget = Math.Min(BurstBytes, _budget + ((nowSeconds - _at) * BytesPerSecond));
        }

        if (double.IsNaN(_at) || nowSeconds > _at)
        {
            _at = nowSeconds;
        }
    }
}

/// <summary>What <see cref="MenuMirror.Apply"/> did with an event.</summary>
public enum MenuMirrorChange
{
    /// <summary>Nothing: an unknown menu, an older version, or too many menus open.</summary>
    Ignored,

    /// <summary>A menu this PC didn't know opened.</summary>
    Opened,

    /// <summary>A known menu was sent again as opened (a rejoin): its state was replaced.</summary>
    Refreshed,

    /// <summary>A known menu's data changed.</summary>
    Updated,

    /// <summary>A known menu closed (it's no longer in <see cref="MenuMirror.Open"/>).</summary>
    Closed,

    /// <summary>A known menu's selection moved.</summary>
    Selected,

    /// <summary>A known menu's list selector took a value.</summary>
    ValueSet,

    /// <summary>An element of a known menu was clicked.</summary>
    Clicked,

    /// <summary>The host said which menus are open: every other one was dropped (it closed while this PC was away).</summary>
    Synced,
}

/// <summary>A menu open on the host's screen, as a friend's PC holds it: its state, selection and list selectors' values.</summary>
public sealed class MirroredMenu
{
    private readonly Dictionary<MenuTarget, int> _values = new();

    internal MirroredMenu(MenuState state) => State = state;

    /// <summary>The menu and its data, as the host's game has it now.</summary>
    public MenuState State { get; internal set; }

    /// <summary>The host's selection in it, or null before the first.</summary>
    public MenuTarget? Selection { get; internal set; }

    /// <summary>Each list selector's value.</summary>
    public IReadOnlyDictionary<MenuTarget, int> Values => _values;

    /// <summary>The newest click, or null.</summary>
    public MenuEvent? LastClick { get; internal set; }

    internal void SetValue(MenuTarget target, int index) => _values[target] = index;
}

/// <summary>
/// A friend's side of the menu views: the host's open menus as the <see cref="MenuEvent"/>s describe them, in the order
/// they opened. Each event says what changed for the glue to show. Game-independent. Not thread-safe (one game thread).
/// </summary>
public sealed class MenuMirror
{
    private readonly List<MirroredMenu> _open = new();

    /// <summary>The host's open menus, oldest first.</summary>
    public IReadOnlyList<MirroredMenu> Open => _open;

    /// <summary>Applies an event from the host; says what changed.</summary>
    public MenuMirrorChange Apply(MenuEvent menuEvent)
    {
        ArgumentNullException.ThrowIfNull(menuEvent);
        MirroredMenu? menu = Find(menuEvent.Serial);
        switch (menuEvent.Kind)
        {
            case MenuEventKind.Opened:
                if (menu != null)
                {
                    menu.State = menuEvent.State!;
                    return MenuMirrorChange.Refreshed;
                }

                if (_open.Count >= MenuBoard.MaxOpen)
                {
                    return MenuMirrorChange.Ignored;
                }

                _open.Add(new MirroredMenu(menuEvent.State!));
                return MenuMirrorChange.Opened;
            case MenuEventKind.Updated:
                if (menu == null || !SequenceMath.IsNewer(menuEvent.State!.Version, menu.State.Version))
                {
                    return MenuMirrorChange.Ignored;
                }

                menu.State = menuEvent.State;
                return MenuMirrorChange.Updated;
            case MenuEventKind.Closed:
                return menu != null && _open.Remove(menu) ? MenuMirrorChange.Closed : MenuMirrorChange.Ignored;
            case MenuEventKind.Selected when menu != null:
                menu.Selection = menuEvent.Target;
                return MenuMirrorChange.Selected;
            case MenuEventKind.ValueSet when menu != null:
                if (!menu.Values.ContainsKey(menuEvent.Target) && menu.Values.Count >= MenuBoard.MaxValuesPerMenu)
                {
                    return MenuMirrorChange.Ignored;
                }

                menu.SetValue(menuEvent.Target, menuEvent.Index);
                return MenuMirrorChange.ValueSet;
            case MenuEventKind.Clicked when menu != null:
                menu.LastClick = menuEvent;
                return MenuMirrorChange.Clicked;
            case MenuEventKind.Synced:
                _open.RemoveAll(m => !menuEvent.OpenSerials.Contains(m.State.Serial));
                return MenuMirrorChange.Synced;
            default:
                return MenuMirrorChange.Ignored;
        }
    }

    /// <summary>The menu open with <paramref name="serial"/>, or false.</summary>
    public bool TryGet(uint serial, out MirroredMenu menu)
    {
        menu = Find(serial)!;
        return menu != null;
    }

    /// <summary>Forgets every menu (following stopped).</summary>
    public void Clear() => _open.Clear();

    private MirroredMenu? Find(uint serial) => _open.Find(m => m.State.Serial == serial);
}

/// <summary>Name rules and value lists shared by the menu messages.</summary>
internal static class MenuNames
{
    /// <summary>Printable ASCII, no space at either end, 1 to <paramref name="maxBytes"/> long: the game's and the mod's identifiers.</summary>
    public static bool IsName(string name, int maxBytes) => name.Length <= maxBytes && HostViewMessage.IsName(name);

    /// <summary><paramref name="values"/>, copied, after checking the count and that every name is a name and appears once.</summary>
    public static IReadOnlyList<KeyValuePair<string, NetValue>> Checked(IReadOnlyList<KeyValuePair<string, NetValue>> values, int max, string parameter)
    {
        ArgumentNullException.ThrowIfNull(values, parameter);
        if (values.Count > max)
        {
            throw new ArgumentException($"At most {max} values.", parameter);
        }

        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (KeyValuePair<string, NetValue> pair in values)
        {
            if (pair.Key == null || !IsName(pair.Key, MenuData.MaxKeyBytes) || !keys.Add(pair.Key))
            {
                throw new ArgumentException($"Value names are printable ASCII, 1 to {MenuData.MaxKeyBytes} bytes, each once.", parameter);
            }
        }

        return values.ToList();
    }

    /// <summary>The value called <paramref name="key"/> in <paramref name="values"/>, or false.</summary>
    public static bool TryGet(IReadOnlyList<KeyValuePair<string, NetValue>> values, string key, out NetValue value)
    {
        foreach (KeyValuePair<string, NetValue> pair in values)
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

    /// <summary>Writes a value list: count, then name and value each.</summary>
    public static void WriteValues(BitWriter writer, IReadOnlyList<KeyValuePair<string, NetValue>> values)
    {
        writer.WriteVarUInt((ulong)values.Count);
        foreach (KeyValuePair<string, NetValue> pair in values)
        {
            writer.WriteShortString(pair.Key, MenuData.MaxKeyBytes);
            pair.Value.Write(writer);
        }
    }

    /// <summary>Reads a value list of at most <paramref name="max"/>; text is made safe to show. False for bad, repeated or too many names.</summary>
    public static bool TryReadValues(ref BitReader reader, int max, out List<KeyValuePair<string, NetValue>> values)
    {
        values = new List<KeyValuePair<string, NetValue>>();
        ulong count = reader.ReadVarUInt();
        if (reader.IsOverrun || count > (ulong)max)
        {
            return false;
        }

        var keys = new HashSet<string>(StringComparer.Ordinal);
        for (ulong i = 0; i < count; i++)
        {
            string key = reader.ReadShortString(MenuData.MaxKeyBytes);
            if (reader.IsOverrun || !IsName(key, MenuData.MaxKeyBytes) || !keys.Add(key) || !NetValue.TryRead(ref reader, out NetValue value))
            {
                return false;
            }

            if (value.Kind == NetValueKind.Text)
            {
                value = NetValue.FromString(SafeText.Clean(value.Text, NetValue.MaxStringBytes));
            }

            values.Add(new KeyValuePair<string, NetValue>(key, value));
        }

        return !reader.IsOverrun;
    }
}
