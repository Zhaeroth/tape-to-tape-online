using System;
using T2TOnline.Core.OnlineSettings;

namespace T2TOnline.Core.Tests.OnlineSettings;

/// <summary>
/// The address rule the event and Nurse views store by when they write a fresh string or row into a game array of
/// references through the write barrier: an element is one reference, so it is the struct rule with one pointer-sized field at
/// the element's start.
/// </summary>
public class ReferenceArrayLayoutTests
{
    private const int Pointer = 8;

    private static long Element(long array, int index) =>
        ValueArrayLayout.ElementFieldAddress(array, index, Pointer, ValueArrayLayout.ObjectHeaderSize(Pointer), Pointer);

    [Fact]
    public void A_reference_element_is_at_its_index_after_the_array_header()
    {
        // On the 64-bit build: class, monitor, bounds, length (0x20 in all), then one pointer per element.
        const long array = 0x7FF6_1000_0000;
        Assert.Equal(array + 0x20, Element(array, 0));
        Assert.Equal(array + 0x28, Element(array, 1));
        Assert.Equal(array + 0x20 + (8 * 15), Element(array, 15));
        Assert.Equal(array + 0x20 + (8L * int.MaxValue), Element(array, int.MaxValue));
    }

    [Fact]
    public void An_index_below_zero_or_a_field_past_the_one_reference_is_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Element(0x1000, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => ValueArrayLayout.ElementFieldAddress(0x1000, 0, Pointer, ValueArrayLayout.ObjectHeaderSize(Pointer) + Pointer, Pointer));
        Assert.Throws<ArgumentOutOfRangeException>(() => ValueArrayLayout.ElementFieldAddress(0x1000, 0, Pointer, ValueArrayLayout.ObjectHeaderSize(Pointer) - 1, Pointer));
    }
}
