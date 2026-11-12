using System;
using System.Collections.Generic;
using System.Linq;
using T2TOnline.Core.Campaign;
using T2TOnline.Core.Input;
using T2TOnline.Core.Protocol;
using T2TOnline.Core.Serialization;

namespace T2TOnline.Core.Tests.Campaign;

/// <summary>Another player's own button names on their controller screen icon.</summary>
public class ControllerGlyphsTests
{
    private static readonly int[] Actions = { 33, 32, 45 }; // (this build's ids: the position lock, colour and jersey buttons)

    private static PromptNameTable Table(byte family, params PromptName[] entries)
    {
        Assert.True(PromptNameTable.TryCreate(family, 1, entries, out PromptNameTable? table));
        return table!;
    }

    private static PromptName Name(int action, string name, params string[] modifiers) => new((ushort)action, BindingRange.Full, name, modifiers);

    private static MenuData Data(IEnumerable<MenuItem> rows) =>
        new(new[] { new KeyValuePair<string, NetValue>(ControllerScreenState.ModeKey, NetValue.FromInt(2)) }, rows.ToList());

    /// <summary>The data as a friend reads it off the wire.</summary>
    private static MenuData OverTheWire(MenuData data)
    {
        var state = new MenuState(3, "ControllerAssignmentCampaign", "ControllerAssignmentMenu", "standard_game", 0, data);
        var writer = new BitWriter();
        Assert.True(MenuMessages.Write(writer, MenuEvent.Opened(state)));
        var reader = new BitReader(writer.ToArray());
        Assert.True(MenuMessages.TryRead(MessageType.MenuOpen, ref reader, out MenuEvent? read));
        return read!.State!.Data;
    }

    [Fact]
    public void A_friends_names_become_the_three_buttons_of_their_icons_row()
    {
        PromptNameTable table = Table(2, Name(33, "Left Bumper"), Name(32, "Button Y"), Name(10, "Right Stick Button"));
        ControllerGlyphRow? row = ControllerGlyphs.From(3, table, Actions);
        Assert.NotNull(row);
        Assert.Equal((3, 2), (row!.Slot, row.Family));
        Assert.Equal("Left Bumper", row.Names[0]!.Name);
        Assert.Equal("Button Y", row.Names[1]!.Name);
        Assert.True(row.Names[2]!.IsBlank); // their bindings bind nothing to the jersey button: blank, as on their couch
        Assert.Contains("P4", row.Describe(), StringComparison.Ordinal);
        Assert.Contains("\"Left Bumper\"", row.Describe(), StringComparison.Ordinal);
        Assert.Contains("(nothing bound)", row.Describe(), StringComparison.Ordinal);

        // A build without one of the actions leaves that button to the game's own prompt.
        ControllerGlyphRow? partial = ControllerGlyphs.From(0, table, new[] { 33, -1, 45 });
        Assert.Null(partial!.Names[1]);
        Assert.Contains("(unknown)", partial.Describe(), StringComparison.Ordinal);

        Assert.Null(ControllerGlyphs.From(16, table, Actions)); // not a slot the screen has
        Assert.Null(ControllerGlyphs.From(0, table, new[] { 1, 2 }));
    }

    [Fact]
    public void Rows_round_trip_over_the_wire_with_modifiers_blanks_and_unknowns()
    {
        PromptNameTable keyboard = Table(0, Name(33, "Space"), Name(32, "E", "Left Shift", "Left Control"), Name(45, "Mouse Button 1"));
        ControllerGlyphRow typist = ControllerGlyphs.From(1, keyboard, Actions)!;
        ControllerGlyphRow pad = ControllerGlyphs.From(2, Table(4, Name(33, "Button A")), Actions)!;
        ControllerGlyphRow partial = ControllerGlyphs.From(5, Table(3), new[] { 33, -1, 45 })!;
        MenuData data = OverTheWire(Data(new[] { ControllerGlyphs.ToItem(typist), ControllerGlyphs.ToItem(pad), ControllerGlyphs.ToItem(partial) }));

        IReadOnlyList<ControllerGlyphRow> read = ControllerGlyphs.TryRead(data, out int refused);
        Assert.Equal(0, refused);
        Assert.Equal(3, read.Count);
        Assert.Equal(typist.Signature, read[0].Signature);
        Assert.Equal(new[] { "Left Shift", "Left Control" }, read[0].Names[1]!.Modifiers);
        Assert.Equal("Mouse Button 1", read[0].Names[2]!.Name);
        Assert.Equal(pad.Signature, read[1].Signature);
        Assert.True(read[1].Names[1]!.IsBlank);
        Assert.Equal(partial.Signature, read[2].Signature);
        Assert.Null(read[2].Names[1]);
        Assert.True(read[2].Names[0]!.IsBlank);

        // A different name is a different row (a remap goes out again).
        ControllerGlyphRow remapped = ControllerGlyphs.From(2, Table(4, Name(33, "Button B")), Actions)!;
        Assert.NotEqual(pad.Signature, remapped.Signature);
        ControllerGlyphRow other = ControllerGlyphs.From(2, Table(3, Name(33, "Button A")), Actions)!;
        Assert.NotEqual(pad.Signature, other.Signature); // another family is another row
    }

    [Fact]
    public void A_rows_signature_is_its_names_utf8_and_costs_no_allocation()
    {
        // A friend's controller screen asks for every icon's signature several times a second while it's up.
        ControllerGlyphRow typist = ControllerGlyphs.From(1, Table(0, Name(33, "Space"), Name(32, "E", "Left Shift", "Left Control"), Name(45, "Mouse Button 1")), Actions)!;
        ControllerGlyphRow partial = ControllerGlyphs.From(5, Table(3), new[] { 33, -1, 45 })!;
        var unusual = new ControllerGlyphRow(7, 2, new GlyphName?[] { new("Bouton Ä", new[] { "\uD83C\uDFAE" }), new("lone \uD800 half", Array.Empty<string>()), GlyphName.Blank });
        foreach (ControllerGlyphRow row in new[] { typist, partial, unusual })
        {
            Assert.Equal(ByteByByte(row), row.Signature);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        ulong sum = 0;
        for (int i = 0; i < 200; i++)
        {
            sum = unchecked(sum + typist.Signature + partial.Signature + unusual.Signature);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.NotEqual(0UL, sum);

        // The same hash as each text's encoded bytes, written out (a lone surrogate encodes as U+FFFD).
        static ulong ByteByByte(ControllerGlyphRow row)
        {
            ulong hash = Fnv.Add(Fnv.Add(Fnv.OffsetBasis, (byte)row.Slot), (byte)row.Family);
            foreach (GlyphName? name in row.Names)
            {
                hash = Fnv.Add(hash, (byte)(name == null ? 0 : name.IsBlank ? 1 : 2));
                if (name != null)
                {
                    hash = Fnv.Add(hash, System.Text.Encoding.UTF8.GetBytes(name.Name));
                    foreach (string modifier in name.Modifiers)
                    {
                        hash = Fnv.Add(Fnv.Add(hash, (byte)'+'), System.Text.Encoding.UTF8.GetBytes(modifier));
                    }
                }

                hash = Fnv.Add(hash, (byte)';');
            }

            return hash;
        }
    }

    [Fact]
    public void A_screen_full_of_the_longest_names_fits_a_row_and_the_menus_limits()
    {
        string longest = new('n', PromptNameTable.MaxNameBytes);
        string[] modifiers = Enumerable.Repeat(new string('m', PromptNameTable.MaxModifierBytes), PromptNameTable.MaxModifiers).ToArray();
        var rows = new List<MenuItem>();
        for (int slot = 0; slot < ControllerGlyphs.MaxRows; slot++)
        {
            var names = new GlyphName[] { new(longest, modifiers), new(longest, modifiers), new(longest, modifiers) };
            MenuItem item = ControllerGlyphs.ToItem(new ControllerGlyphRow(slot, 1 + (slot % 4), names));
            Assert.Equal(ControllerGlyphs.ValueCount, item.Values.Count);
            Assert.InRange(item.Values.Count, 1, MenuData.MaxItemValues);
            rows.Add(item);
        }

        // Beside a full controller screen's player rows, its teams, its status line: under the menu's 64 rows.
        for (int slot = 0; slot < ControllerScreenState.MaxIcons; slot++)
        {
            rows.Add(ControllerScreenRowTests.Row(slot, slot == 0 ? 0UL : 76561199000000000UL + (ulong)slot));
        }

        Assert.InRange(rows.Count, 1, MenuData.MaxItems);
        MenuData read = OverTheWire(Data(rows));
        Assert.Equal(ControllerGlyphs.MaxRows, ControllerGlyphs.TryRead(read, out int refused).Count);
        Assert.Equal(0, refused);
    }

    [Fact]
    public void Rows_that_dont_read_are_left_out_and_counted_and_never_throw()
    {
        static MenuItem Row(params (string Key, NetValue Value)[] values) => new(ControllerGlyphs.Group, values.Select(v => new KeyValuePair<string, NetValue>(v.Key, v.Value)).ToList());
        NetValue S(string text) => NetValue.FromString(text);
        NetValue I(long value) => NetValue.FromInt(value);

        // Every bad row is for slot 3 and the good one for slot 2: a bad row read by mistake would be a second row.
        var bad = new[]
        {
            Row((ControllerGlyphs.SlotKey, I(16)), (ControllerGlyphs.FamilyKey, I(1))),                                    // no such slot
            Row((ControllerGlyphs.SlotKey, I(-1)), (ControllerGlyphs.FamilyKey, I(1))),
            Row((ControllerGlyphs.SlotKey, I(3)), (ControllerGlyphs.FamilyKey, I(5))),                                     // not a device the game draws
            Row((ControllerGlyphs.SlotKey, S("3")), (ControllerGlyphs.FamilyKey, I(1))),                                   // a slot that isn't a number
            Row((ControllerGlyphs.SlotKey, I(3))),                                                                         // no family
            Row((ControllerGlyphs.SlotKey, I(3)), (ControllerGlyphs.FamilyKey, I(1)), ("n0", S("café"))),              // not plain ASCII
            Row((ControllerGlyphs.SlotKey, I(3)), (ControllerGlyphs.FamilyKey, I(1)), ("n0", S(new string('x', 49)))),     // too long
            Row((ControllerGlyphs.SlotKey, I(3)), (ControllerGlyphs.FamilyKey, I(1)), ("n0", I(7))),                       // a name that isn't text
            Row((ControllerGlyphs.SlotKey, I(3)), (ControllerGlyphs.FamilyKey, I(1)), ("m01", S("Left Shift"))),           // a modifier of a button with no name
            Row((ControllerGlyphs.SlotKey, I(3)), (ControllerGlyphs.FamilyKey, I(1)), ("n0", S("")), ("m00", S("Left Shift"))), // a modifier of a blank
            Row((ControllerGlyphs.SlotKey, I(3)), (ControllerGlyphs.FamilyKey, I(1)), ("n0", S("A")), ("m00", S(new string('m', 25)))), // a modifier too long
        };
        var good = ControllerGlyphs.ToItem(new ControllerGlyphRow(2, 1, new GlyphName?[] { new("Button A", Array.Empty<string>()), null, null }));
        MenuData data = Data(bad.Append(good).Append(good)); // the same slot twice
        IReadOnlyList<ControllerGlyphRow> read = ControllerGlyphs.TryRead(data, out int refused);
        Assert.Single(read);
        Assert.Equal(2, read[0].Slot);
        Assert.Equal(bad.Length + 1, refused);

        // Some other menu's data, or an older host's controller screen, has none.
        Assert.Empty(ControllerGlyphs.TryRead(MenuData.Empty, out refused));
        Assert.Equal(0, refused);

        // The host never makes a row it couldn't read back.
        Assert.Throws<ArgumentException>(() => ControllerGlyphs.ToItem(new ControllerGlyphRow(2, 9, new GlyphName?[3])));
        Assert.Throws<ArgumentException>(() => ControllerGlyphs.ToItem(new ControllerGlyphRow(2, 1, new GlyphName?[] { new("café", Array.Empty<string>()), null, null })));
        Assert.Throws<ArgumentException>(() => ControllerGlyphs.ToItem(new ControllerGlyphRow(2, 1, new GlyphName?[2])));
    }

    [Fact]
    public void More_than_a_screens_icons_of_rows_are_not_taken()
    {
        var rows = new List<MenuItem>();
        for (int i = 0; i < 40; i++)
        {
            rows.Add(new MenuItem(ControllerGlyphs.Group, new[]
            {
                new KeyValuePair<string, NetValue>(ControllerGlyphs.SlotKey, NetValue.FromInt(i % 16)),
                new KeyValuePair<string, NetValue>(ControllerGlyphs.FamilyKey, NetValue.FromInt(1)),
            }));
        }

        IReadOnlyList<ControllerGlyphRow> read = ControllerGlyphs.TryRead(Data(rows), out int refused);
        Assert.Equal(ControllerGlyphs.MaxRows, read.Count);
        Assert.Equal(40 - ControllerGlyphs.MaxRows, refused);
    }

    [Fact]
    public void The_buttons_actions_are_found_by_their_names_in_any_spelling_and_never_guessed()
    {
        var actions = new List<(int Id, string Name)> { (30, "UI Confirm"), (31, "UI Back"), (32, "UI Special"), (33, "UI Special 2"), (45, "UI Right Trigger"), (46, "UI Left Trigger") };
        Assert.Equal(33, ControllerGlyphs.FindAction(0, actions));
        Assert.Equal(32, ControllerGlyphs.FindAction(1, actions));
        Assert.Equal(45, ControllerGlyphs.FindAction(2, actions));
        Assert.Equal(-1, ControllerGlyphs.FindAction(3, actions));
        Assert.Equal(-1, ControllerGlyphs.FindAction(-1, actions));

        // The names with underscores or run together are the same names ("UI_Special_2", "UISpecial2").
        var renamed = new List<(int, string)> { (7, "UI_Special_2"), (8, "uispecial"), (9, "UIRightTrigger") };
        Assert.Equal(new[] { 7, 8, 9 }, Enumerable.Range(0, 3).Select(i => ControllerGlyphs.FindAction(i, renamed)));

        Assert.True(ControllerGlyphs.IsAction(0, "UI Special 2"));
        Assert.True(ControllerGlyphs.IsAction(2, "UI_Right_Trigger"));
        Assert.False(ControllerGlyphs.IsAction(1, "UI Special 2"));
        Assert.False(ControllerGlyphs.IsAction(3, "UI Special"));
        Assert.False(ControllerGlyphs.IsAction(0, null));

        // A build with none, or two that read alike, has no such action: the game's own prompt stays.
        Assert.Equal(-1, ControllerGlyphs.FindAction(0, new List<(int, string)> { (1, "UI Special") }));
        Assert.Equal(-1, ControllerGlyphs.FindAction(0, new List<(int, string)> { (1, "UI Special 2"), (2, "UI_Special_2") }));
        Assert.Equal(-1, ControllerGlyphs.FindAction(0, new List<(int, string)>()));
    }

    [Fact]
    public void Fuzzed_rows_never_throw()
    {
        var random = new Random(7);
        string[] keys = { "slot", "fam", "n0", "n1", "n2", "m00", "m01", "m02", "m10", "m21", "m22", "x" };
        for (int round = 0; round < 500; round++)
        {
            var values = new List<KeyValuePair<string, NetValue>>();
            foreach (string key in keys.OrderBy(_ => random.Next()).Take(random.Next(0, keys.Length)))
            {
                NetValue value = random.Next(3) switch
                {
                    0 => NetValue.FromInt(random.Next(-3, 20)),
                    1 => NetValue.FromString(new string((char)random.Next(32, 127), random.Next(0, 60))),
                    _ => NetValue.FromBool(random.Next(2) == 0),
                };
                values.Add(new KeyValuePair<string, NetValue>(key, value));
            }

            MenuData data = Data(new[] { new MenuItem(ControllerGlyphs.Group, values) });
            IReadOnlyList<ControllerGlyphRow> read = ControllerGlyphs.TryRead(data, out int refused);
            Assert.Equal(1, read.Count + refused);
            foreach (ControllerGlyphRow row in read)
            {
                Assert.Null(ControllerGlyphs.Problem(row));
            }
        }
    }
}
