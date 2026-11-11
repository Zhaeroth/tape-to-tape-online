using System;
using System.Collections.Generic;
using System.Linq;
using T2TOnline.Core.Input;
using T2TOnline.Core.Protocol;
using T2TOnline.Core.Serialization;

namespace T2TOnline.Core.Tests.Protocol;

/// <summary>A friend's controls on the wire (<see cref="MessageType.InputRemaps"/>) and how a keyboard's key table is built.</summary>
public class InputMessagesTests
{
    private static readonly BindingElement Space = BindingElement.Key(32);
    private static readonly BindingElement W = BindingElement.Key(119);
    private static readonly BindingElement S = BindingElement.Key(115);
    private static readonly BindingElement ShiftR = BindingElement.Key(114, KeyModifiers.Shift);

    /// <summary>A keyboard set like the game's defaults, cut down: moves, shoot, a chord, a mouse button.</summary>
    internal static InputBindingSet KeyboardSet(byte generation = 1)
    {
        var builder = new KeyboardBindingTableBuilder(KeyboardBindingTableBuilder.BrawlKeys);
        builder.Add(new InputBinding(0, 1, W));
        builder.Add(new InputBinding(0, 1, S, Negative: true));
        builder.Add(new InputBinding(0, 2, Space));
        builder.Add(new InputBinding(2, 24, Space));
        builder.Add(new InputBinding(2, 49, ShiftR));
        builder.Add(new InputBinding(6, 59, Space));
        builder.Add(new InputBinding(2, 65, BindingElement.MouseButton(3)));
        return builder.Build(generation, BindingSource.OwnMaps, 2);
    }

    /// <summary>A remapped pad: Shoot on RB, moves on the left stick's halves.</summary>
    internal static InputBindingSet PadSet(byte generation = 4) => new(generation, InputKind.Gamepad, BindingSource.OwnMaps, 1, Array.Empty<BindingElement>(), new[]
    {
        new InputBinding(0, 2, BindingElement.PadButton(5)),
        new InputBinding(0, 0, BindingElement.Axis(PadAxis.LeftStickX), Range: BindingRange.Positive),
        new InputBinding(0, 0, BindingElement.Axis(PadAxis.LeftStickX), Negative: true, Range: BindingRange.Negative, Invert: true),
        new InputBinding(4, 45, BindingElement.PadButton(1)),
    });

    [Fact]
    public void A_keyboard_set_round_trips()
    {
        InputBindingSet set = KeyboardSet();
        InputBindingSet back = RoundTrip(set);
        Assert.Equal((set.Generation, set.Device, set.Source, set.Differences), (back.Generation, back.Device, back.Source, back.Differences));
        Assert.Equal(set.Elements, back.Elements);
        Assert.Equal(set.Bindings, back.Bindings);
        Assert.True(back.SameControlsAs(set));
        Assert.Equal(new byte[] { 0, 2, 6 }, back.Categories);
        Assert.Equal(3, back.CountIn(2));
    }

    [Fact]
    public void A_pad_set_round_trips_and_defaults_send_no_bindings()
    {
        InputBindingSet set = PadSet();
        InputBindingSet back = RoundTrip(set);
        Assert.Equal(set.Bindings, back.Bindings);
        Assert.Empty(back.Elements);
        Assert.False(back.KeepsTemplateMaps);

        InputBindingSet defaults = RoundTrip(InputBindingSet.PadDefaults(9));
        Assert.True(defaults.KeepsTemplateMaps);
        Assert.Equal((9, BindingSource.GameDefaults), (defaults.Generation, defaults.Source));

        var writer = new BitWriter();
        InputRemapsMessage.Write(writer, InputBindingSet.PadDefaults(1));
        Assert.Equal(5, writer.ByteLength); // generation, device, source, differences, no categories
    }

    [Fact]
    public void The_key_table_starts_with_q_and_e_and_holds_each_element_once()
    {
        InputBindingSet set = KeyboardSet();
        Assert.Equal(BindingElement.Key(BindingElement.KeyQ), set.Elements[0]);
        Assert.Equal(BindingElement.Key(BindingElement.KeyE), set.Elements[1]);
        Assert.Equal(new[] { W, S, Space, ShiftR, BindingElement.MouseButton(3) }, set.Elements.Skip(2));
        Assert.Equal(4, set.IndexOf(Space));
        Assert.Equal(-1, set.IndexOf(BindingElement.Key(1)));
        Assert.NotEqual(set.IndexOf(BindingElement.Key(114)), set.IndexOf(ShiftR)); // a chord is its own element
    }

    [Fact]
    public void A_full_table_skips_and_counts()
    {
        var builder = new KeyboardBindingTableBuilder(KeyboardBindingTableBuilder.BrawlKeys);
        for (int key = 1; key <= 70; key++)
        {
            builder.Add(new InputBinding(0, 3, BindingElement.Key(key + 200)));
        }

        Assert.False(builder.Add(new InputBinding(0, 1, BindingElement.Axis(PadAxis.LeftStickX))));
        Assert.False(builder.Add(new InputBinding(0, 1024, Space)));
        Assert.False(builder.Add(new InputBinding(0, 1, BindingElement.Key(0))));
        InputBindingSet set = builder.Build(1, BindingSource.GameDefaults, 0);
        Assert.Equal(64, set.Elements.Count);
        Assert.Equal(62, set.Bindings.Count);
        Assert.Equal(8, builder.SkippedTableFull);
        Assert.Equal(3, builder.SkippedInvalid);
        Assert.Equal(set.Bindings, RoundTrip(set).Bindings);
    }

    [Fact]
    public void Differences_count_moves_and_removals_once()
    {
        var defaults = new List<InputBinding> { new(0, 2, Space), new(0, 1, W), new(0, 1, S, Negative: true) };
        Assert.Equal(0, InputBindingSet.CountDifferences(defaults, defaults.AsEnumerable().Reverse()));
        var moved = new List<InputBinding> { new(0, 2, BindingElement.Key(102)), new(0, 1, W), new(0, 1, S, Negative: true) };
        Assert.Equal(1, InputBindingSet.CountDifferences(moved, defaults));
        Assert.Equal(new[] { new InputBinding(0, 2, BindingElement.Key(102)) }, InputBindingSet.NotIn(moved, defaults, 10));
        Assert.Equal(1, InputBindingSet.CountDifferences(defaults.Take(2), defaults));
        Assert.Equal(2, InputBindingSet.CountDifferences(defaults.Append(new InputBinding(3, 32, W)).Append(new InputBinding(3, 32, S)), defaults));
        Assert.Empty(InputBindingSet.NotIn(moved, defaults, 0));
    }

    [Fact]
    public void A_trigger_written_as_a_button_or_as_an_axis_is_the_same_input()
    {
        // A pad's own map binds a trigger as an axis with a range, the game's gamepad
        // template as a button, so the host named ten trigger bindings as changed for one remap.
        BindingElement leftTrigger = BindingElement.Axis(PadAxis.LeftTrigger);
        var template = new List<InputBinding> { new(0, 6, leftTrigger), new(0, 2, BindingElement.PadButton(0)), new(0, 0, BindingElement.Axis(PadAxis.LeftStickX)) };
        var own = new List<InputBinding>
        {
            new(0, 6, leftTrigger, Range: BindingRange.Positive),
            new(0, 4, BindingElement.PadButton(8)), // the one remap
            new(0, 0, BindingElement.Axis(PadAxis.LeftStickX), Invert: true),
        };

        Assert.Equal(new[] { new InputBinding(0, 4, BindingElement.PadButton(8)) },
            InputBindingSet.NotIn(own.Select(b => b.AsInput).ToList(), template.Select(b => b.AsInput).ToList(), 10));
        Assert.Equal(3, InputBindingSet.NotIn(own, template, 10).Count); // compared as written, every one differs
        InputBinding button = new(1, 3, BindingElement.PadButton(2), Negative: true);
        Assert.Equal(button, button.AsInput); // a button keeps everything
        Assert.True(new InputBinding(0, 1, leftTrigger, Negative: true, Range: BindingRange.Negative).AsInput.Negative); // the side stays
    }

    [Fact]
    public void Same_controls_ignore_the_generation()
    {
        Assert.True(KeyboardSet(1).SameControlsAs(KeyboardSet(2)));
        Assert.False(KeyboardSet().SameControlsAs(PadSet()));
        Assert.False(KeyboardSet().SameControlsAs(null));
    }

    [Fact]
    public void Sets_that_couldnt_be_sent_are_refused_when_made()
    {
        BindingElement[] none = Array.Empty<BindingElement>();
        var shoot = new[] { new InputBinding(0, 2, Space) };
        Assert.Throws<ArgumentException>(() => new InputBindingSet(0, InputKind.Gamepad, BindingSource.GameDefaults, 0, none, Array.Empty<InputBinding>()));
        Assert.Throws<ArgumentException>(() => new InputBindingSet(1, InputKind.Gamepad, BindingSource.OwnMaps, 0, none, shoot));
        Assert.Throws<ArgumentException>(() => new InputBindingSet(1, InputKind.KeyboardMouse, BindingSource.OwnMaps, 0, none, shoot));
        Assert.Throws<ArgumentException>(() => new InputBindingSet(1, InputKind.KeyboardMouse, BindingSource.OwnMaps, 0, new[] { W }, shoot));
        Assert.Throws<ArgumentException>(() => new InputBindingSet(1, InputKind.KeyboardMouse, BindingSource.OwnMaps, 0, new[] { Space, Space }, shoot));
        Assert.Throws<ArgumentException>(() => new InputBindingSet(1, (InputKind)2, BindingSource.OwnMaps, 0, none, Array.Empty<InputBinding>()));
        Assert.Throws<ArgumentException>(() => new InputBindingSet(1, InputKind.Gamepad, BindingSource.OwnMaps, 0, none,
            Enumerable.Range(0, 17).Select(c => new InputBinding((byte)c, 2, BindingElement.PadButton(0))).ToArray()));
        Assert.Throws<ArgumentException>(() => new InputBindingSet(1, InputKind.Gamepad, BindingSource.OwnMaps, 0, none,
            new[] { new InputBinding(0, 2, BindingElement.PadButton(0), Range: (BindingRange)3) }));
    }

    [Fact]
    public void Malformed_payloads_are_refused()
    {
        byte[] keyboard = Payload(KeyboardSet());
        byte[] pad = Payload(PadSet());

        Assert.False(Read(Change(keyboard, 0, 0))); // generation 0
        Assert.False(Read(Change(keyboard, 1, 2))); // unknown device
        Assert.False(Read(Change(keyboard, 2, 2))); // unknown source
        Assert.False(Read(Change(keyboard, 4, 0))); // a key table of 0
        Assert.False(Read(Change(keyboard, 4, 65))); // ... or 65 elements
        Assert.False(Read(Change(pad, 4, 17))); // 17 categories
        for (int cut = 0; cut < keyboard.Length; cut++)
        {
            Assert.False(Read(keyboard.AsSpan(0, cut).ToArray()));
        }

        // The same category twice.
        Assert.True(Read(PadCategories(3, 5)));
        Assert.False(Read(PadCategories(3, 3)));
    }

    [Fact]
    public void Out_of_range_fields_are_refused()
    {
        // Written by hand: a pad binding on button 15, an axis 6, a range of 3, a key table index past the table, a
        // repeated element, a key code 0, more than 1024 bindings.
        Assert.False(Read(PadBinding(isAxis: false, index: 15, range: 0)));
        Assert.False(Read(PadBinding(isAxis: true, index: 6, range: 0)));
        Assert.False(Read(PadBinding(isAxis: false, index: 0, range: 3)));
        Assert.True(Read(PadBinding(isAxis: true, index: 5, range: 2)));

        Assert.False(Read(KeyboardWith(elements: new[] { (false, 32, 0) }, tableIndex: 1)));
        Assert.True(Read(KeyboardWith(elements: new[] { (false, 32, 0) }, tableIndex: 0)));
        Assert.False(Read(KeyboardWith(elements: new[] { (false, 32, 0), (false, 32, 0) }, tableIndex: 0)));
        Assert.False(Read(KeyboardWith(elements: new[] { (false, 0, 0) }, tableIndex: 0)));
        Assert.False(Read(KeyboardWith(elements: new[] { (true, 3, 1) }, tableIndex: 0))); // a mouse button with a modifier

        var writer = new BitWriter();
        writer.WriteByte(1);
        writer.WriteByte(0);
        writer.WriteByte(1);
        writer.WriteByte(0);
        writer.WriteByte(5);
        for (int c = 0; c < 5; c++)
        {
            writer.WriteByte((byte)c);
            writer.WriteByte(255);
            for (int b = 0; b < 255; b++)
            {
                writer.WriteBits(2, 10);
                writer.WriteBits(0, 5);
                writer.WriteBits(0, 4);
            }
        }

        Assert.False(Read(writer.ToArray())); // 1275 bindings
    }

    [Fact]
    public void Garbage_never_throws()
    {
        var random = new Random(31);
        byte[] keyboard = Payload(KeyboardSet());
        for (int i = 0; i < 20_000; i++)
        {
            byte[] bytes;
            if (i % 2 == 0)
            {
                bytes = new byte[random.Next(0, 200)];
                random.NextBytes(bytes);
            }
            else
            {
                // A real payload with a few bytes flipped.
                bytes = (byte[])keyboard.Clone();
                for (int f = random.Next(1, 4); f > 0; f--)
                {
                    bytes[random.Next(bytes.Length)] ^= (byte)random.Next(1, 256);
                }
            }

            var reader = new BitReader(bytes);
            if (InputRemapsMessage.TryRead(ref reader, out InputBindingSet? set))
            {
                Assert.NotNull(set);
                Assert.True(set!.Bindings.All(b => b.IsValid));
                Assert.Equal(set.Bindings, RoundTrip(set).Bindings);
            }
            else
            {
                Assert.Null(set);
            }
        }
    }

    [Fact]
    public void The_largest_set_fits_the_payload_cap()
    {
        var builder = new KeyboardBindingTableBuilder(KeyboardBindingTableBuilder.BrawlKeys);
        for (int c = 0; c < 16; c++)
        {
            for (int b = 0; b < 64; b++)
            {
                builder.Add(new InputBinding((byte)c, 1023, BindingElement.Key(200 + b, KeyModifiers.All), true, BindingRange.Negative, true));
            }
        }

        InputBindingSet set = builder.Build(255, BindingSource.OwnMaps, 255);
        Assert.Equal(1024 - (16 * 2), set.Bindings.Count); // Q and E took two places
        Assert.True(Payload(set).Length <= InputRemapsMessage.MaxPayloadBytes, $"{Payload(set).Length} bytes");
    }

    internal static byte[] Payload(InputBindingSet set)
    {
        var writer = new BitWriter();
        InputRemapsMessage.Write(writer, set);
        return writer.ToArray();
    }

    private static InputBindingSet RoundTrip(InputBindingSet set)
    {
        var reader = new BitReader(Payload(set));
        Assert.True(InputRemapsMessage.TryRead(ref reader, out InputBindingSet? back));
        return back!;
    }

    private static bool Read(byte[] bytes)
    {
        var reader = new BitReader(bytes);
        return InputRemapsMessage.TryRead(ref reader, out _);
    }

    private static byte[] Change(byte[] bytes, int index, byte value)
    {
        byte[] copy = (byte[])bytes.Clone();
        copy[index] = value;
        return copy;
    }

    private static byte[] PadCategories(params byte[] categories)
    {
        var writer = new BitWriter();
        writer.WriteByte(1);
        writer.WriteByte((byte)InputKind.Gamepad);
        writer.WriteByte((byte)BindingSource.OwnMaps);
        writer.WriteByte(0);
        writer.WriteByte((byte)categories.Length);
        foreach (byte category in categories)
        {
            writer.WriteByte(category);
            writer.WriteByte(1);
            writer.WriteBits(2, 10);
            writer.WriteBits(0, 9);
        }

        return writer.ToArray();
    }

    private static byte[] PadBinding(bool isAxis, int index, int range)
    {
        var writer = new BitWriter();
        writer.WriteByte(1);
        writer.WriteByte((byte)InputKind.Gamepad);
        writer.WriteByte((byte)BindingSource.OwnMaps);
        writer.WriteByte(0);
        writer.WriteByte(1);
        writer.WriteByte(0);
        writer.WriteByte(1);
        writer.WriteBits(2, 10);
        writer.WriteBool(isAxis);
        writer.WriteBits((ulong)index, 4);
        writer.WriteBool(false);
        writer.WriteBits((ulong)range, 2);
        writer.WriteBool(false);
        return writer.ToArray();
    }

    private static byte[] KeyboardWith((bool Mouse, int Code, int Modifiers)[] elements, int tableIndex)
    {
        var writer = new BitWriter();
        writer.WriteByte(1);
        writer.WriteByte((byte)InputKind.KeyboardMouse);
        writer.WriteByte((byte)BindingSource.OwnMaps);
        writer.WriteByte(0);
        writer.WriteByte((byte)elements.Length);
        foreach ((bool mouse, int code, int modifiers) in elements)
        {
            writer.WriteBool(mouse);
            writer.WriteBits((ulong)code, 9);
            writer.WriteBits((ulong)modifiers, 4);
        }

        writer.WriteByte(1);
        writer.WriteByte(0);
        writer.WriteByte(1);
        writer.WriteBits(2, 10);
        writer.WriteBits((ulong)tableIndex, 6);
        writer.WriteBool(false);
        writer.WriteBits(0, 2);
        writer.WriteBool(false);
        return writer.ToArray();
    }
}
