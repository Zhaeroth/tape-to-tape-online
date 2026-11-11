using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using T2TOnline.Core.Input;
using T2TOnline.Core.Serialization;

namespace T2TOnline.Core.Campaign;

/// <summary>One of a controller screen icon's three buttons as its player's own bindings name it.</summary>
/// <param name="Name">The raw input name the prompt's sprite is looked up by ("Button B", "Space"); empty when the player's bindings bind nothing there (the prompt is blank, as on their couch).</param>
/// <param name="Modifiers">The modifier keys held with it ("Left Shift"), in the matcher's order; usually none.</param>
public sealed record GlyphName(string Name, IReadOnlyList<string> Modifiers)
{
    /// <summary>The player's bindings bind nothing to the button.</summary>
    public static GlyphName Blank { get; } = new(string.Empty, Array.Empty<string>());

    /// <summary>True for <see cref="Blank"/>'s kind: nothing is bound.</summary>
    public bool IsBlank => Name.Length == 0;
}

/// <summary>
/// What one controller screen icon needs to draw its player's own button names: the device family the names
/// are for and the three buttons' names, in the order of <see cref="ControllerGlyphs.ActionNames"/>. A null name is one the host
/// doesn't know (the game's own prompt stays).
/// </summary>
/// <param name="Slot">The game player (slot) the icon is for.</param>
/// <param name="Family">The device family the names are for (the game's <c>EInputDeviceType</c>, 0 keyboard and mouse, 1 to 4 the pad families).</param>
/// <param name="Names">Three entries: the position lock, the colour and the jersey colour buttons.</param>
public sealed record ControllerGlyphRow(int Slot, int Family, IReadOnlyList<GlyphName?> Names)
{
    /// <summary>
    /// A hash of the row, equal for equal rows (to see whether it changed): FNV-1a over its slot, family and each name's UTF-8.
    /// Worked out without allocating (a friend's controller screen asks for it for every icon several times a second).
    /// </summary>
    public ulong Signature
    {
        get
        {
            ulong hash = Fnv.Add(Fnv.OffsetBasis, (byte)Slot);
            hash = Fnv.Add(hash, (byte)Family);
            for (int i = 0; i < Names.Count; i++)
            {
                GlyphName? name = Names[i];
                hash = Fnv.Add(hash, (byte)(name == null ? 0 : name.IsBlank ? 1 : 2));
                if (name != null)
                {
                    hash = AddUtf8(hash, name.Name);
                    for (int m = 0; m < name.Modifiers.Count; m++)
                    {
                        hash = Fnv.Add(hash, (byte)'+');
                        hash = AddUtf8(hash, name.Modifiers[m]);
                    }
                }

                hash = Fnv.Add(hash, (byte)';');
            }

            return hash;
        }
    }

    /// <summary>For the log: <c>P2 (Xbox 360): position lock "Button B", colour "Button X", jersey "Right Trigger"</c>.</summary>
    public string Describe()
    {
        string[] words = { "position lock", "colour", "jersey" };
        var parts = new List<string>();
        for (int i = 0; i < Names.Count && i < words.Length; i++)
        {
            GlyphName? name = Names[i];
            parts.Add($"{words[i]} {(name == null ? "(unknown)" : name.IsBlank ? "(nothing bound)" : $"\"{string.Join(" + ", name.Modifiers.Append(name.Name))}\"")}");
        }

        return string.Create(CultureInfo.InvariantCulture, $"P{Slot + 1} ({PromptPinState.DeviceName(Family)}): {string.Join(", ", parts)}");
    }

    /// <summary><paramref name="hash"/> with <paramref name="text"/>'s UTF-8 bytes added (a lone surrogate as U+FFFD, as the encoder writes it), allocating nothing.</summary>
    private static ulong AddUtf8(ulong hash, string text)
    {
        Span<byte> utf8 = stackalloc byte[4];
        foreach (Rune rune in text.EnumerateRunes())
        {
            hash = Fnv.Add(hash, utf8[..rune.EncodeToUtf8(utf8)]);
        }

        return hash;
    }
}

/// <summary>
/// Another player's own button names on a controller screen's icon. The game draws each icon's three
/// buttons (position lock, colour, jersey colour) from its player's bindings; a friend's PC knows only its own, so every other
/// player's icon showed their pad family with this PC's default names. The host knows every friend's names (their
/// <see cref="PromptNameTable"/>, message 22) and its own, and sends, with the screen's data, one small row a player of just
/// these three buttons (not their whole table): bounded at <see cref="MaxRows"/> rows of <see cref="ValueCount"/> values, sent again
/// whenever a name changes (a remap, a new pad, a player who joins), so a friend who joins later has the board's newest. Game-independent.
/// <code>
/// row "glyphs": slot (whole) · fam (whole) · n0 n1 n2 (text: the raw input name, "" for nothing bound, absent when not known)
///               · m00 m01 m02 m10 m11 m12 m20 m21 m22 (text: button i's modifier keys, absent when none)
/// </code>
/// </summary>
public static class ControllerGlyphs
{
    /// <summary>The rows' group in the menu's data.</summary>
    public const string Group = "glyphs";

    /// <summary>The icon's buttons that have a glyph: the position lock, the colour and the jersey colour.</summary>
    public const int Buttons = 3;

    /// <summary>Most rows (one a game player, as many as the screen has icons).</summary>
    public const int MaxRows = ControllerScreenState.MaxIcons;

    /// <summary>Most values in a row (within <see cref="MenuData.MaxItemValues"/>).</summary>
    public const int ValueCount = 2 + Buttons + (Buttons * PromptNameTable.MaxModifiers);

    /// <summary>The Rewired actions of the icon's three buttons (the controller screen prefab's <c>m_Inputs</c> <c>SpecialAction</c> "UI_UISpecial2", <c>ChangeColor</c> "UI_UISpecial", <c>ChangeJerseyColor</c> "UI_UIRightTrigger").</summary>
    public static IReadOnlyList<string> ActionNames { get; } = new[] { "UI Special 2", "UI Special", "UI Right Trigger" };

    /// <summary>Row value: the game player (slot).</summary>
    public const string SlotKey = "slot";

    /// <summary>Row value: the device family the names are for.</summary>
    public const string FamilyKey = "fam";

    /// <summary>The name of button <paramref name="index"/>'s row value ("n0").</summary>
    public static string NameKey(int index) => string.Create(CultureInfo.InvariantCulture, $"n{index}");

    /// <summary>The name of button <paramref name="index"/>'s <paramref name="modifier"/>th modifier row value ("m01").</summary>
    public static string ModifierKey(int index, int modifier) => string.Create(CultureInfo.InvariantCulture, $"m{index}{modifier}");

    /// <summary>
    /// The row for the player in <paramref name="slot"/>, from the names their PC made (<paramref name="table"/>): each button's
    /// name over the whole axis, which is what the screen's prompts ask for (<c>EInputAxisMode</c> 0 is the full axis,
    /// <c>RewiredInputUtils.GetAxisRange</c>); nothing bound reads as blank, an action id below 0 (the build has no such
    /// action) as not known. <paramref name="actionIds"/> are the buttons' Rewired actions (<see cref="FindAction"/>).
    /// Null for a slot out of range.
    /// </summary>
    public static ControllerGlyphRow? From(int slot, PromptNameTable table, IReadOnlyList<int> actionIds)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(actionIds);
        if (slot is < 0 or > ControllerScreenState.MaxSlot || actionIds.Count != Buttons)
        {
            return null;
        }

        var names = new GlyphName?[Buttons];
        for (int i = 0; i < Buttons; i++)
        {
            if (actionIds[i] < 0)
            {
                names[i] = null;
            }
            else if (table.TryGet(actionIds[i], BindingRange.Full, out PromptName? bound) && bound != null)
            {
                names[i] = new GlyphName(bound.Name, bound.Modifiers.ToArray());
            }
            else
            {
                names[i] = GlyphName.Blank;
            }
        }

        return new ControllerGlyphRow(slot, table.Family, names);
    }

    /// <summary>
    /// Button <paramref name="index"/>'s Rewired action among <paramref name="actions"/> (id and name): the one whose name is
    /// <see cref="ActionNames"/>[index] when letters and digits are compared alone ("UI Special 2" and "UISpecial2" are one),
    /// or -1 when this build has none or more than one.
    /// </summary>
    public static int FindAction(int index, IEnumerable<(int Id, string Name)> actions)
    {
        ArgumentNullException.ThrowIfNull(actions);
        if (index is < 0 or >= Buttons)
        {
            return -1;
        }

        string wanted = Squash(ActionNames[index]);
        int found = -1;
        foreach ((int id, string name) in actions)
        {
            if (string.Equals(Squash(name), wanted, StringComparison.Ordinal))
            {
                if (found >= 0)
                {
                    return -1;
                }

                found = id;
            }
        }

        return found;
    }

    /// <summary>True when <paramref name="actionName"/> (a Rewired action's name) is button <paramref name="index"/>'s action, spelt as <see cref="FindAction"/> reads it.</summary>
    public static bool IsAction(int index, string? actionName) =>
        index is >= 0 and < Buttons && actionName != null && string.Equals(Squash(actionName), Squash(ActionNames[index]), StringComparison.Ordinal);

    /// <summary>The row as a menu item. Throws (as <see cref="MenuItem"/> does) for one the protocol can't carry.</summary>
    public static MenuItem ToItem(ControllerGlyphRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (Problem(row) is string problem)
        {
            throw new ArgumentException(problem, nameof(row));
        }

        var values = new List<KeyValuePair<string, NetValue>>(ValueCount)
        {
            new(SlotKey, NetValue.FromInt(row.Slot)),
            new(FamilyKey, NetValue.FromInt(row.Family)),
        };
        for (int i = 0; i < Buttons; i++)
        {
            GlyphName? name = row.Names[i];
            if (name == null)
            {
                continue;
            }

            values.Add(new(NameKey(i), NetValue.FromString(name.Name)));
            for (int m = 0; m < name.Modifiers.Count; m++)
            {
                values.Add(new(ModifierKey(i, m), NetValue.FromString(name.Modifiers[m])));
            }
        }

        return new MenuItem(Group, values);
    }

    /// <summary>
    /// The glyph rows in a controller screen's data (none when it has none: an older host, or nobody with names to show). A row
    /// that doesn't read (a slot or family out of range, a name that isn't plain text, a modifier without its button, a
    /// slot twice) is left out and counted in <paramref name="refused"/>; at most <see cref="MaxRows"/> are taken. Never throws.
    /// </summary>
    public static IReadOnlyList<ControllerGlyphRow> TryRead(MenuData data, out int refused)
    {
        ArgumentNullException.ThrowIfNull(data);
        refused = 0;
        var rows = new List<ControllerGlyphRow>();
        var slots = new HashSet<int>();
        foreach (MenuItem item in data.Group(Group))
        {
            if (rows.Count >= MaxRows)
            {
                refused++;
                continue;
            }

            ControllerGlyphRow? row = Read(item);
            if (row == null || !slots.Add(row.Slot))
            {
                refused++;
                continue;
            }

            rows.Add(row);
        }

        return rows;
    }

    /// <summary>Why <paramref name="row"/> can't be carried, or null when it can.</summary>
    public static string? Problem(ControllerGlyphRow row)
    {
        if (row.Slot is < 0 or > ControllerScreenState.MaxSlot)
        {
            return $"slot {row.Slot} isn't a game player on the screen (0 to {ControllerScreenState.MaxSlot})";
        }

        if (row.Family is < 0 or > PromptNameTable.MaxFamily)
        {
            return $"family {row.Family} isn't a device the game draws prompts for (0 to {PromptNameTable.MaxFamily})";
        }

        if (row.Names.Count != Buttons)
        {
            return $"{row.Names.Count} button names (there are {Buttons})";
        }

        foreach (GlyphName? name in row.Names)
        {
            if (name == null)
            {
                continue;
            }

            if (!name.IsBlank && !PromptNameTable.IsText(name.Name, PromptNameTable.MaxNameBytes))
            {
                return "a button name that isn't plain text of 1 to 48 characters";
            }

            if (name.IsBlank ? name.Modifiers.Count > 0 : name.Modifiers.Count > PromptNameTable.MaxModifiers)
            {
                return "modifier keys on a button with no name, or more than three";
            }

            if (name.Modifiers.Any(m => !PromptNameTable.IsText(m, PromptNameTable.MaxModifierBytes)))
            {
                return "a modifier key that isn't plain text of 1 to 24 characters";
            }
        }

        return null;
    }

    private static ControllerGlyphRow? Read(MenuItem item)
    {
        if (!item.TryGet(SlotKey, out NetValue slot) || slot.Kind != NetValueKind.Whole || !item.TryGet(FamilyKey, out NetValue family) || family.Kind != NetValueKind.Whole ||
            slot.WholeValue is < 0 or > ControllerScreenState.MaxSlot || family.WholeValue is < 0 or > PromptNameTable.MaxFamily)
        {
            return null;
        }

        var names = new GlyphName?[Buttons];
        for (int i = 0; i < Buttons; i++)
        {
            bool present = item.TryGet(NameKey(i), out NetValue name);
            if (!present)
            {
                if (Enumerable.Range(0, PromptNameTable.MaxModifiers).Any(m => item.TryGet(ModifierKey(i, m), out _)))
                {
                    return null; // modifiers of a button with no name
                }

                continue;
            }

            if (name.Kind != NetValueKind.Text)
            {
                return null;
            }

            var modifiers = new List<string>(PromptNameTable.MaxModifiers);
            for (int m = 0; m < PromptNameTable.MaxModifiers; m++)
            {
                if (!item.TryGet(ModifierKey(i, m), out NetValue modifier))
                {
                    break;
                }

                if (modifier.Kind != NetValueKind.Text)
                {
                    return null;
                }

                modifiers.Add(modifier.Text ?? string.Empty);
            }

            string text = name.Text ?? string.Empty;
            if (text.Length == 0 && modifiers.Count > 0)
            {
                return null; // modifiers of a button its player's bindings leave blank (the host never writes one)
            }

            names[i] = text.Length == 0 ? GlyphName.Blank : new GlyphName(text, modifiers);
        }

        var row = new ControllerGlyphRow((int)slot.WholeValue, (int)family.WholeValue, names);
        return Problem(row) == null ? row : null;
    }

    /// <summary>Letters and digits of <paramref name="text"/>, lower case: how "UI Special 2" and "UISpecial2" are one name.</summary>
    private static string Squash(string text)
    {
        var letters = new char[text.Length];
        int count = 0;
        foreach (char c in text)
        {
            if (char.IsLetterOrDigit(c))
            {
                letters[count++] = char.ToLowerInvariant(c);
            }
        }

        return new string(letters, 0, count);
    }
}
