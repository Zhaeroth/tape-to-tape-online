using System;
using System.Collections.Generic;
using T2TOnline.Core.Input;
using T2TOnline.Core.Serialization;

namespace T2TOnline.Core.Protocol;

/// <summary>
/// Friend to host (channel 1, <see cref="MessageType.PromptNames"/>): a friend's button prompt names, a
/// <see cref="PromptNameTable"/>, sent after every welcome (after their controls) and again whenever their device or
/// controls change. Layout: format (u8, <see cref="Format"/>), family (u8, 0 to 4), generation (u16, from 1), the entry
/// count (varuint, at most 512), then per entry its action id (u16, at most 1023), range (u8, 0 to 2), name (a short
/// string of 1 to 48 bytes of printable ASCII), modifier count (u8, at most 3) and each modifier (1 to 24 bytes of printable
/// ASCII). The payload is at most <see cref="MaxPayloadBytes"/>.
/// </summary>
public static class PromptNamesMessage
{
    /// <summary>The layout's format.</summary>
    public const byte Format = 1;

    /// <summary>The largest payload a host takes, in bytes.</summary>
    public const int MaxPayloadBytes = 16 * 1024;

    /// <summary>The payload's bytes before the entries, at most (format, family, generation, the count's varuint for up to 512).</summary>
    public const int HeaderBytes = 1 + 1 + 2 + 2;

    /// <summary>The bytes <paramref name="entry"/> takes in the payload.</summary>
    public static int EntryBytes(PromptName entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        int bytes = 2 + 1 + 1 + entry.Name.Length + 1;
        foreach (string modifier in entry.Modifiers)
        {
            bytes += 1 + modifier.Length;
        }

        return bytes;
    }

    /// <summary>Writes <paramref name="table"/> (after the header).</summary>
    public static void Write(BitWriter writer, PromptNameTable table)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(table);

        writer.WriteByte(Format);
        writer.WriteByte(table.Family);
        writer.WriteUInt16(table.Generation);
        writer.WriteVarUInt((ulong)table.Entries.Count);
        foreach (PromptName entry in table.Entries)
        {
            writer.WriteUInt16(entry.ActionId);
            writer.WriteByte((byte)entry.Range);
            writer.WriteShortString(entry.Name, PromptNameTable.MaxNameBytes);
            writer.WriteByte((byte)entry.Modifiers.Count);
            foreach (string modifier in entry.Modifiers)
            {
                writer.WriteShortString(modifier, PromptNameTable.MaxModifierBytes);
            }
        }
    }

    /// <summary>
    /// Reads a table (after the header). False for truncated or malformed data: another format, a family past 4, generation
    /// 0, more than 512 entries, an action id past 1023, a range of 3 or more, a name or modifier that isn't 1 to its cap
    /// bytes of printable ASCII, more than 3 modifiers, the same action and range twice, or bytes left over. Never throws.
    /// </summary>
    public static bool TryRead(ref BitReader reader, out PromptNameTable? table)
    {
        table = null;
        byte format = reader.ReadByte();
        byte family = reader.ReadByte();
        ushort generation = reader.ReadUInt16();
        ulong count = reader.ReadVarUInt();
        if (reader.IsOverrun || format != Format || family > PromptNameTable.MaxFamily || generation == 0 || count > PromptNameTable.MaxEntries)
        {
            return false;
        }

        var entries = new List<PromptName>((int)count);
        for (int i = 0; i < (int)count; i++)
        {
            ushort action = reader.ReadUInt16();
            byte range = reader.ReadByte();
            string name = reader.ReadShortString(PromptNameTable.MaxNameBytes);
            int modifierCount = reader.ReadByte();
            if (reader.IsOverrun || modifierCount > PromptNameTable.MaxModifiers)
            {
                return false;
            }

            var modifiers = new string[modifierCount];
            for (int m = 0; m < modifierCount; m++)
            {
                modifiers[m] = reader.ReadShortString(PromptNameTable.MaxModifierBytes);
            }

            var entry = new PromptName(action, (BindingRange)range, name, modifiers);
            if (reader.IsOverrun || !PromptNameTable.IsValid(entry))
            {
                return false;
            }

            entries.Add(entry);
        }

        // Whole bytes are written, so anything past the last one is junk.
        return !reader.IsOverrun && reader.RemainingBits < 8 && PromptNameTable.TryCreate(family, generation, entries, out table);
    }
}
