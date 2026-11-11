using System;
using System.Linq;
using T2TOnline.Core.Input;
using T2TOnline.Core.Protocol;
using T2TOnline.Core.Serialization;

namespace T2TOnline.Core.Tests.Protocol;

/// <summary>A friend's button prompt names on the wire (<see cref="MessageType.PromptNames"/>) and the table the host looks them up in.</summary>
public class PromptNamesMessageTests
{
    /// <summary>A remapped pad's names: Shoot on RB, a stick's halves, a keyboard-style chord's modifiers on one entry.</summary>
    internal static PromptNameTable Sample(byte family = 1, ushort generation = 3)
    {
        var builder = new PromptNameTableBuilder();
        Assert.True(builder.Add(2, BindingRange.Full, "Right Shoulder", null));
        Assert.True(builder.Add(0, BindingRange.Positive, "Left Stick Right", null));
        Assert.True(builder.Add(0, BindingRange.Negative, "Left Stick Left", Array.Empty<string>()));
        Assert.True(builder.Add(49, BindingRange.Full, "R", new[] { "Left Shift", "Left Control" }));
        Assert.True(builder.Add(1023, BindingRange.Full, "Right Trigger", null));
        return builder.Build(family, generation);
    }

    [Fact]
    public void A_table_round_trips()
    {
        PromptNameTable table = Sample();
        PromptNameTable back = RoundTrip(table);
        Assert.Equal((table.Family, table.Generation), (back.Family, back.Generation));
        Assert.Equal(table.Entries.Count, back.Entries.Count);
        for (int i = 0; i < table.Entries.Count; i++)
        {
            Assert.Equal((table.Entries[i].ActionId, table.Entries[i].Range, table.Entries[i].Name), (back.Entries[i].ActionId, back.Entries[i].Range, back.Entries[i].Name));
            Assert.Equal(table.Entries[i].Modifiers, back.Entries[i].Modifiers);
        }

        Assert.Equal(2, back.ModifierCount);
    }

    [Fact]
    public void The_host_looks_names_up_by_action_and_range()
    {
        PromptNameTable table = RoundTrip(Sample());
        Assert.True(table.TryGet(0, BindingRange.Negative, out PromptName? left));
        Assert.Equal("Left Stick Left", left!.Name);
        Assert.True(table.TryGet(49, BindingRange.Full, out PromptName? chord));
        Assert.Equal(new[] { "Left Shift", "Left Control" }, chord!.Modifiers);
        Assert.False(table.TryGet(0, BindingRange.Full, out _)); // only its halves are bound
        Assert.False(table.TryGet(3, BindingRange.Full, out _)); // nothing bound: blank, as the couch's "no mapping found"
        Assert.False(table.TryGet(-1, BindingRange.Full, out _));
    }

    [Fact]
    public void The_size_the_builder_counts_is_the_payload_written()
    {
        var builder = new PromptNameTableBuilder();
        builder.Add(2, BindingRange.Full, "Right Shoulder", null);
        builder.Add(49, BindingRange.Full, "R", new[] { "Left Shift" });
        var writer = new BitWriter();
        PromptNamesMessage.Write(writer, builder.Build(0, 1));
        // The header counts the varuint as its largest (2 bytes); two entries take 1.
        Assert.Equal(builder.Bytes - 1, writer.ByteLength);
    }

    [Fact]
    public void The_builder_skips_and_counts_what_a_table_cant_carry()
    {
        var builder = new PromptNameTableBuilder();
        Assert.False(builder.Add(2, BindingRange.Full, null, null));
        Assert.False(builder.Add(2, BindingRange.Full, string.Empty, null));
        Assert.False(builder.Add(2, BindingRange.Full, "Bouton à droite", null)); // not ASCII
        Assert.False(builder.Add(2, BindingRange.Full, new string('x', 49), null));
        Assert.False(builder.Add(2, BindingRange.Full, "Line\nbreak", null));
        Assert.False(builder.Add(1024, BindingRange.Full, "A", null));
        Assert.False(builder.Add(-1, BindingRange.Full, "A", null));
        Assert.False(builder.Add(2, (BindingRange)3, "A", null));
        Assert.Equal(8, builder.SkippedText);

        Assert.True(builder.Add(2, BindingRange.Full, new string('x', 48), new[] { "Shift", "Ctrl", "Alt", "Cmd", "Ä", new string('m', 25) }));
        Assert.Equal(3, builder.ModifiersDropped); // the fourth, a non-ASCII one, one too long
        Assert.False(builder.Add(2, BindingRange.Full, "A", null));
        Assert.Equal(1, builder.SkippedRepeat);
        PromptNameTable table = builder.Build(4, 65535);
        Assert.Equal(new[] { "Shift", "Ctrl", "Alt" }, Assert.Single(table.Entries).Modifiers);
    }

    [Fact]
    public void A_full_table_stops_at_its_entry_cap_and_stays_under_the_payload_cap()
    {
        var builder = new PromptNameTableBuilder();
        for (int action = 0; action <= InputBinding.MaxActionId; action++)
        {
            builder.Add(action, BindingRange.Full, "A", null);
        }

        Assert.Equal(PromptNameTable.MaxEntries, builder.Count);
        Assert.Equal(1024 - PromptNameTable.MaxEntries, builder.SkippedFull);

        var big = new PromptNameTableBuilder();
        string name = new('n', PromptNameTable.MaxNameBytes);
        string[] modifiers = Enumerable.Repeat(new string('m', PromptNameTable.MaxModifierBytes), PromptNameTable.MaxModifiers).ToArray();
        for (int action = 0; action <= InputBinding.MaxActionId; action++)
        {
            big.Add(action, BindingRange.Full, name, modifiers);
        }

        Assert.True(big.Bytes <= PromptNamesMessage.MaxPayloadBytes);
        Assert.True(big.SkippedFull > 0);
        var writer = new BitWriter();
        PromptNamesMessage.Write(writer, big.Build(1, 1));
        Assert.True(writer.ByteLength <= PromptNamesMessage.MaxPayloadBytes);
        Assert.Equal(big.Count, RoundTrip(big.Build(1, 1)).Entries.Count);
    }

    [Fact]
    public void A_table_refuses_what_a_decoder_would()
    {
        PromptName ok = new(1, BindingRange.Full, "A", Array.Empty<string>());
        Assert.False(PromptNameTable.TryCreate(5, 1, new[] { ok }, out _));
        Assert.False(PromptNameTable.TryCreate(1, 0, new[] { ok }, out _));
        Assert.False(PromptNameTable.TryCreate(1, 1, new[] { ok, ok }, out _));
        Assert.False(PromptNameTable.TryCreate(1, 1, new[] { ok with { ActionId = 1024 } }, out _));
        Assert.False(PromptNameTable.TryCreate(1, 1, new[] { ok with { Modifiers = new[] { "a", "b", "c", "d" } } }, out _));
        Assert.False(PromptNameTable.TryCreate(1, 1, Enumerable.Range(0, 513).Select(i => ok with { ActionId = (ushort)i }), out _));
        Assert.True(PromptNameTable.TryCreate(0, 1, Array.Empty<PromptName>(), out PromptNameTable? empty));
        Assert.Empty(empty!.Entries);
        Assert.Throws<ArgumentOutOfRangeException>(() => new PromptNameTableBuilder().Build(5, 1));
    }

    [Fact]
    public void Malformed_payloads_are_refused()
    {
        byte[] good = Payload(Sample());
        Assert.True(Read(good, out _));

        // Every cut short.
        for (int length = 0; length < good.Length; length++)
        {
            Assert.False(Read(good.AsSpan(0, length).ToArray(), out _));
        }

        Assert.False(Read(good.Append((byte)0).ToArray(), out _)); // a byte left over
        Assert.False(Read(With(good, 0, 2), out _)); // another format
        Assert.False(Read(With(good, 1, 5), out _)); // family past 4
        Assert.False(Read(With(With(good, 2, 0), 3, 0), out _)); // generation 0

        // An entry count past the cap, a range of 3, an action past 1023, too many modifiers, a non-ASCII name, a repeat.
        Assert.False(Read(Raw(w => { Head(w, 513); }), out _));
        Assert.False(Read(Raw(w => { Head(w, 1); Entry(w, 1, 3, "A"); }), out _));
        Assert.False(Read(Raw(w => { Head(w, 1); Entry(w, 1024, 0, "A"); }), out _));
        Assert.False(Read(Raw(w => { Head(w, 1); w.WriteUInt16(1); w.WriteByte(0); w.WriteShortString("A"); w.WriteByte(4); }), out _));
        Assert.False(Read(Raw(w => { Head(w, 1); Entry(w, 1, 0, "é"); }), out _));
        Assert.False(Read(Raw(w => { Head(w, 1); Entry(w, 1, 0, new string('x', 49)); }), out _));
        Assert.False(Read(Raw(w => { Head(w, 2); Entry(w, 1, 0, "A"); Entry(w, 1, 0, "B"); }), out _));
        Assert.False(Read(Raw(w => { Head(w, 1); w.WriteUInt16(1); w.WriteByte(0); w.WriteShortString(string.Empty); w.WriteByte(0); }), out _));
        Assert.True(Read(Raw(w => { Head(w, 2); Entry(w, 1, 0, "A"); Entry(w, 1, 1, "B"); }), out PromptNameTable? two));
        Assert.Equal(2, two!.Entries.Count);
    }

    [Fact]
    public void Garbage_never_throws()
    {
        var random = new Random(22);
        byte[] good = Payload(Sample());
        int read = 0;
        for (int i = 0; i < 5000; i++)
        {
            byte[] bytes = i % 2 == 0 ? new byte[random.Next(0, 300)] : (byte[])good.Clone();
            if (i % 2 == 0)
            {
                random.NextBytes(bytes);
            }
            else
            {
                bytes[random.Next(bytes.Length)] ^= (byte)(1 << random.Next(8));
            }

            if (Read(bytes, out PromptNameTable? table))
            {
                read++;
                Assert.True(table!.Entries.Count <= PromptNameTable.MaxEntries);
                Assert.All(table.Entries, e => Assert.True(PromptNameTable.IsValid(e)));
            }
        }

        Assert.True(read < 5000);
    }

    private static PromptNameTable RoundTrip(PromptNameTable table)
    {
        Assert.True(Read(Payload(table), out PromptNameTable? back));
        return back!;
    }

    private static byte[] Payload(PromptNameTable table)
    {
        var writer = new BitWriter();
        PromptNamesMessage.Write(writer, table);
        return writer.ToArray();
    }

    private static bool Read(byte[] bytes, out PromptNameTable? table)
    {
        var reader = new BitReader(bytes);
        return PromptNamesMessage.TryRead(ref reader, out table);
    }

    private static byte[] With(byte[] bytes, int index, byte value)
    {
        byte[] copy = (byte[])bytes.Clone();
        copy[index] = value;
        return copy;
    }

    private static byte[] Raw(Action<BitWriter> write)
    {
        var writer = new BitWriter();
        write(writer);
        return writer.ToArray();
    }

    private static void Head(BitWriter writer, ulong count)
    {
        writer.WriteByte(PromptNamesMessage.Format);
        writer.WriteByte(1);
        writer.WriteUInt16(1);
        writer.WriteVarUInt(count);
    }

    private static void Entry(BitWriter writer, ushort action, byte range, string name)
    {
        writer.WriteUInt16(action);
        writer.WriteByte(range);
        writer.WriteShortString(name);
        writer.WriteByte(0);
    }
}
