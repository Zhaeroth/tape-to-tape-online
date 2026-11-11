using System;
using T2TOnline.Core.Lobby;

namespace T2TOnline.Core.Tests.Lobby;

/// <summary>A column of blocks scrolled a whole block at a time (the panel's Help tab).</summary>
public class BlockScrollTests
{
    private static readonly float[] Blocks = { 4, 2, 2, 3, 1, 5 }; // 17 in all

    [Fact]
    public void Everything_fits_or_it_scrolls()
    {
        Assert.True(BlockScroll.AllFit(Blocks, 17));
        Assert.False(BlockScroll.AllFit(Blocks, 16.9f));
        Assert.True(BlockScroll.AllFit(ReadOnlySpan<float>.Empty, 0));
        Assert.True(BlockScroll.AllFit(new float[] { -3, 2 }, 2)); // a negative height counts as none
    }

    [Fact]
    public void Only_whole_blocks_are_shown_from_the_first()
    {
        Assert.Equal(3, BlockScroll.Shown(Blocks, 0, 8)); // 4 + 2 + 2
        Assert.Equal(3, BlockScroll.Shown(Blocks, 0, 9.5f)); // the 3 after them doesn't fit whole
        Assert.Equal(3, BlockScroll.Shown(Blocks, 2, 6)); // 2 + 3 + 1
        Assert.Equal(1, BlockScroll.Shown(Blocks, 5, 100)); // the last one
        Assert.Equal(6, BlockScroll.Shown(Blocks, 0, 17));
    }

    [Fact]
    public void A_block_taller_than_the_room_is_still_shown_alone()
    {
        Assert.Equal(1, BlockScroll.Shown(Blocks, 0, 3));
        Assert.Equal(1, BlockScroll.Shown(Blocks, 5, 1));
        Assert.Equal(0, BlockScroll.Shown(ReadOnlySpan<float>.Empty, 0, 10));
    }

    [Fact]
    public void A_first_block_out_of_range_is_kept_in_it()
    {
        Assert.Equal(1, BlockScroll.Shown(Blocks, 99, 100));
        Assert.Equal(3, BlockScroll.Shown(Blocks, -4, 8));
    }

    [Fact]
    public void Scrolling_ends_with_the_last_block_shown_whole()
    {
        Assert.Equal(4, BlockScroll.LastFirst(Blocks, 6)); // 1 + 5
        Assert.Equal(3, BlockScroll.LastFirst(Blocks, 9)); // 3 + 1 + 5
        Assert.Equal(0, BlockScroll.LastFirst(Blocks, 17));
        Assert.Equal(5, BlockScroll.LastFirst(Blocks, 2)); // even the last alone doesn't fit
        Assert.Equal(0, BlockScroll.LastFirst(ReadOnlySpan<float>.Empty, 5));
    }
}
