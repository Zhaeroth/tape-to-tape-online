using System;
using System.Collections.Generic;

namespace T2TOnline.Core.OnlineSettings;

/// <summary>
/// Where a field of an element of an IL2CPP array of structs lives, so a reference stored there can go through the runtime's write barrier one
/// field at a time. An IL2CPP array is an object header (class and monitor, two pointers), the bounds pointer and the length (a pointer-sized
/// integer), then the elements in place, each the struct's unboxed size; a struct's field offsets are given boxed (after the two-pointer object
/// header), so a field's place in an element is its boxed offset less that header. On the 64-bit build: elements from +0x20; a struct of four
/// references is 32 bytes with its fields at +0, +8, +16 and +24 (the game's own code reads the Settings menu's tab array exactly so).
/// </summary>
public static class ValueArrayLayout
{
    /// <summary>The bytes before an array's first element.</summary>
    public static int ArrayHeaderSize(int pointerSize) => 4 * CheckPointer(pointerSize);

    /// <summary>The bytes before a boxed object's first field.</summary>
    public static int ObjectHeaderSize(int pointerSize) => 2 * CheckPointer(pointerSize);

    /// <summary>The address of the field at <paramref name="boxedFieldOffset"/> in element <paramref name="index"/> of the array at <paramref name="array"/>.</summary>
    public static long ElementFieldAddress(long array, int index, int elementSize, int boxedFieldOffset, int pointerSize)
    {
        if (index < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(index), index, "an element index is never negative");
        }

        int inElement = boxedFieldOffset - ObjectHeaderSize(pointerSize);
        if (elementSize <= 0 || inElement < 0 || inElement + pointerSize > elementSize)
        {
            throw new ArgumentOutOfRangeException(nameof(boxedFieldOffset), boxedFieldOffset, "the field isn't inside the element");
        }

        return array + ArrayHeaderSize(pointerSize) + ((long)index * elementSize) + inElement;
    }

    /// <summary>
    /// What's wrong with a struct whose every field is a reference, for writing it field by field, or null: the element must be exactly the references,
    /// each pointer-aligned, inside it, and no two at one place.
    /// </summary>
    public static string? Problem(int elementSize, IReadOnlyList<int> boxedReferenceOffsets, int pointerSize)
    {
        ArgumentNullException.ThrowIfNull(boxedReferenceOffsets);
        CheckPointer(pointerSize);
        if (boxedReferenceOffsets.Count == 0)
        {
            return "no fields";
        }

        if (elementSize != boxedReferenceOffsets.Count * pointerSize)
        {
            return $"the element is {elementSize} bytes, not {boxedReferenceOffsets.Count} references of {pointerSize}";
        }

        var seen = new HashSet<int>();
        foreach (int boxed in boxedReferenceOffsets)
        {
            int inElement = boxed - ObjectHeaderSize(pointerSize);
            if (inElement < 0 || inElement + pointerSize > elementSize)
            {
                return $"a field at {boxed} (boxed) is outside the element";
            }

            if (inElement % pointerSize != 0)
            {
                return $"a field at {boxed} (boxed) isn't pointer-aligned";
            }

            if (!seen.Add(inElement))
            {
                return $"two fields at {boxed} (boxed)";
            }
        }

        return null;
    }

    private static int CheckPointer(int pointerSize) =>
        pointerSize is 4 or 8 ? pointerSize : throw new ArgumentOutOfRangeException(nameof(pointerSize), pointerSize, "a pointer is 4 or 8 bytes");
}
