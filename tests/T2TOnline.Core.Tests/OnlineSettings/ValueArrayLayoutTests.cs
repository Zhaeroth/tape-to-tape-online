using System;
using T2TOnline.Core.OnlineSettings;

namespace T2TOnline.Core.Tests.OnlineSettings;

public class ValueArrayLayoutTests
{
    // The Settings menu's tab struct on the 64-bit build: four references, boxed at 0x10, 0x18, 0x20, 0x28; 32 bytes in an array.
    private static readonly int[] SubMenu = { 0x10, 0x18, 0x20, 0x28 };

    [Fact]
    public void The_headers_are_the_runtimes()
    {
        Assert.Equal(0x20, ValueArrayLayout.ArrayHeaderSize(8));
        Assert.Equal(0x10, ValueArrayLayout.ObjectHeaderSize(8));
        Assert.Equal(0x10, ValueArrayLayout.ArrayHeaderSize(4));
    }

    [Fact]
    public void The_games_own_reads_of_the_tab_array_land_where_the_layout_says()
    {
        const long array = 0x2_0000_1000;

        // GoToTab reads element i's Button at array + 0x20 * (i + 1); CacheSubMenuComponents its Panel at element + 8.
        Assert.Equal(array + 0x20, ValueArrayLayout.ElementFieldAddress(array, 0, 32, SubMenu[0], 8));
        Assert.Equal(array + (0x20 * 8), ValueArrayLayout.ElementFieldAddress(array, 7, 32, SubMenu[0], 8));
        Assert.Equal(array + 0x20 + (0x20 * 3) + 8, ValueArrayLayout.ElementFieldAddress(array, 3, 32, SubMenu[1], 8));
        Assert.Equal(array + 0x20 + (0x20 * 7) + 24, ValueArrayLayout.ElementFieldAddress(array, 7, 32, SubMenu[3], 8));
    }

    [Fact]
    public void Four_references_in_32_bytes_is_a_layout_to_write_field_by_field()
    {
        Assert.Null(ValueArrayLayout.Problem(32, SubMenu, 8));
    }

    [Theory]
    [InlineData(40)]
    [InlineData(24)]
    public void An_element_with_more_than_its_references_is_refused(int size)
    {
        Assert.NotNull(ValueArrayLayout.Problem(size, SubMenu, 8));
    }

    [Fact]
    public void Fields_outside_overlapping_or_unaligned_are_refused()
    {
        Assert.NotNull(ValueArrayLayout.Problem(32, new[] { 0x08, 0x18, 0x20, 0x28 }, 8));
        Assert.NotNull(ValueArrayLayout.Problem(32, new[] { 0x10, 0x10, 0x20, 0x28 }, 8));
        Assert.NotNull(ValueArrayLayout.Problem(32, new[] { 0x10, 0x14, 0x20, 0x28 }, 8));
        Assert.NotNull(ValueArrayLayout.Problem(32, new[] { 0x10, 0x18, 0x20, 0x30 }, 8));
        Assert.NotNull(ValueArrayLayout.Problem(0, Array.Empty<int>(), 8));
    }

    [Fact]
    public void An_address_outside_its_element_or_a_negative_index_throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ValueArrayLayout.ElementFieldAddress(0x1000, -1, 32, 0x10, 8));
        Assert.Throws<ArgumentOutOfRangeException>(() => ValueArrayLayout.ElementFieldAddress(0x1000, 0, 32, 0x08, 8));
        Assert.Throws<ArgumentOutOfRangeException>(() => ValueArrayLayout.ElementFieldAddress(0x1000, 0, 32, 0x30, 8));
        Assert.Throws<ArgumentOutOfRangeException>(() => ValueArrayLayout.ArrayHeaderSize(2));
    }
}
