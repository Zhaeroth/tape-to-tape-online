using System;
using System.Collections.Generic;
using T2TOnline.Core.Input;
using T2TOnline.Core.Serialization;

namespace T2TOnline.Core.Protocol;

/// <summary>
/// Friend to host (channel 1, <see cref="MessageType.InputRemaps"/>): the friend's controls, an <see cref="InputBindingSet"/>,
/// sent after every welcome. Layout: generation (u8, 1 to 255), device (u8: <see cref="InputKind"/>), source (u8:
/// <see cref="BindingSource"/>), differences (u8); for a keyboard, the key table: its size (u8, 1 to 64), then per element
/// 1 bit kind (key, mouse button), 9 bits code, 4 bits <see cref="KeyModifiers"/>; then the category count (u8, 0 to 16),
/// and per category its id (u8) and binding count (u8), then per binding 10 bits action id, the element (a pad's: 1 bit
/// axis, 4 bits index; a keyboard's: 6 bits table index), 1 bit negative contribution, 2 bits range, 1 bit invert.
/// </summary>
public static class InputRemapsMessage
{
    /// <summary>The largest payload a valid set encodes to, in bytes (the host keeps at most one this size per friend).</summary>
    public const int MaxPayloadBytes = 4 + 1 + (KeyboardMouseState.MaxElements * 14 / 8) + 1 + (InputBindingSet.MaxCategories * 2) + (InputBindingSet.MaxBindings * 20 / 8) + 1;

    private const int CodeBits = 9;
    private const int ModifierBits = 4;
    private const int ActionBits = 10;
    private const int PadIndexBits = 4;
    private const int TableIndexBits = 6;
    private const int RangeBits = 2;

    /// <summary>Writes <paramref name="set"/> (after the header).</summary>
    public static void Write(BitWriter writer, InputBindingSet set)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(set);

        writer.WriteByte(set.Generation);
        writer.WriteByte((byte)set.Device);
        writer.WriteByte((byte)set.Source);
        writer.WriteByte(set.Differences);
        if (set.Device == InputKind.KeyboardMouse)
        {
            writer.WriteByte((byte)set.Elements.Count);
            foreach (BindingElement element in set.Elements)
            {
                writer.WriteBool(element.Kind == BindingElementKind.MouseButton);
                writer.WriteBits(element.Code, CodeBits);
                writer.WriteBits((ulong)element.Modifiers, ModifierBits);
            }
        }

        writer.WriteByte((byte)set.Categories.Count);
        foreach (byte category in set.Categories)
        {
            writer.WriteByte(category);
            writer.WriteByte((byte)set.CountIn(category));
            foreach (InputBinding binding in set.Bindings)
            {
                if (binding.Category != category)
                {
                    continue;
                }

                writer.WriteBits(binding.ActionId, ActionBits);
                if (set.Device == InputKind.KeyboardMouse)
                {
                    writer.WriteBits((ulong)set.IndexOf(binding.Element), TableIndexBits);
                }
                else
                {
                    writer.WriteBool(binding.Element.Kind == BindingElementKind.PadAxis);
                    writer.WriteBits(binding.Element.Code, PadIndexBits);
                }

                writer.WriteBool(binding.Negative);
                writer.WriteBits((ulong)binding.Range, RangeBits);
                writer.WriteBool(binding.Invert);
            }
        }
    }

    /// <summary>
    /// Reads a set (after the header). False for truncated or malformed data: generation 0, an unknown device or source, a
    /// key table of 0 or more than 64 elements or with a bad or repeated element, more than 16 categories or a repeated
    /// one, more than 1024 bindings, a binding on an element that doesn't exist, a range of 3. Never throws.
    /// </summary>
    public static bool TryRead(ref BitReader reader, out InputBindingSet? set)
    {
        set = null;
        byte generation = reader.ReadByte();
        var device = (InputKind)reader.ReadByte();
        var source = (BindingSource)reader.ReadByte();
        byte differences = reader.ReadByte();
        if (reader.IsOverrun || generation == 0 || device is not (InputKind.Gamepad or InputKind.KeyboardMouse) ||
            source is not (BindingSource.GameDefaults or BindingSource.OwnMaps))
        {
            return false;
        }

        var elements = new List<BindingElement>();
        if (device == InputKind.KeyboardMouse)
        {
            int count = reader.ReadByte();
            if (reader.IsOverrun || count is < 1 or > KeyboardMouseState.MaxElements)
            {
                return false;
            }

            var seen = new HashSet<BindingElement>();
            for (int i = 0; i < count; i++)
            {
                BindingElementKind kind = reader.ReadBool() ? BindingElementKind.MouseButton : BindingElementKind.Key;
                var element = new BindingElement(kind, (ushort)reader.ReadBits(CodeBits), (KeyModifiers)reader.ReadBits(ModifierBits));
                if (reader.IsOverrun || !element.IsValid || !seen.Add(element))
                {
                    return false;
                }

                elements.Add(element);
            }
        }

        int categoryCount = reader.ReadByte();
        if (reader.IsOverrun || categoryCount > InputBindingSet.MaxCategories)
        {
            return false;
        }

        var categories = new HashSet<byte>();
        var bindings = new List<InputBinding>();
        for (int c = 0; c < categoryCount; c++)
        {
            byte category = reader.ReadByte();
            int bindingCount = reader.ReadByte();
            if (reader.IsOverrun || !categories.Add(category) || bindings.Count + bindingCount > InputBindingSet.MaxBindings)
            {
                return false;
            }

            for (int b = 0; b < bindingCount; b++)
            {
                var action = (ushort)reader.ReadBits(ActionBits);
                BindingElement element;
                if (device == InputKind.KeyboardMouse)
                {
                    int index = (int)reader.ReadBits(TableIndexBits);
                    if (index >= elements.Count)
                    {
                        return false;
                    }

                    element = elements[index];
                }
                else
                {
                    BindingElementKind kind = reader.ReadBool() ? BindingElementKind.PadAxis : BindingElementKind.PadButton;
                    element = new BindingElement(kind, (ushort)reader.ReadBits(PadIndexBits));
                }

                var binding = new InputBinding(category, action, element, reader.ReadBool(), (BindingRange)reader.ReadBits(RangeBits), reader.ReadBool());
                if (reader.IsOverrun || !binding.IsValid)
                {
                    return false;
                }

                bindings.Add(binding);
            }
        }

        if (reader.IsOverrun || !InputBindingSet.TryValidate(generation, device, source, elements, bindings, out _))
        {
            return false;
        }

        set = new InputBindingSet(generation, device, source, differences, elements, bindings);
        return true;
    }
}
