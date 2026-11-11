using System;

namespace T2TOnline.Core.Lobby;

/// <summary>
/// A column of blocks of different heights (a heading, a paragraph, a key's row) scrolled a whole block at a time, as the
/// mod panel's long tabs scroll: which blocks fit in the room from a first one, and the first block of the last view, where
/// scrolling ends with the last block shown whole. A block is never cut, so nothing drawn needs a clip. Allocates nothing.
/// </summary>
public static class BlockScroll
{
    /// <summary>True when every block fits in <paramref name="room"/> from the first (nothing to scroll).</summary>
    public static bool AllFit(ReadOnlySpan<float> heights, float room)
    {
        float total = 0;
        foreach (float height in heights)
        {
            total += Math.Max(0, height);
        }

        return total <= room;
    }

    /// <summary>
    /// How many blocks are shown from <paramref name="first"/> (kept in range): as many as fit whole in
    /// <paramref name="room"/>, and at least one, so a block taller than the room is still shown (cut at the bottom). Zero
    /// when there are no blocks.
    /// </summary>
    public static int Shown(ReadOnlySpan<float> heights, int first, float room)
    {
        if (heights.Length == 0)
        {
            return 0;
        }

        first = Math.Clamp(first, 0, heights.Length - 1);
        float used = 0;
        int shown = 0;
        for (int i = first; i < heights.Length; i++)
        {
            used += Math.Max(0, heights[i]);
            if (used > room && shown > 0)
            {
                break;
            }

            shown++;
        }

        return shown;
    }

    /// <summary>
    /// The first block of the last view: the smallest first block from which every block to the end fits in
    /// <paramref name="room"/> (0 when they all do; the last block when even it alone doesn't).
    /// </summary>
    public static int LastFirst(ReadOnlySpan<float> heights, float room)
    {
        if (heights.Length == 0)
        {
            return 0;
        }

        float used = 0;
        for (int i = heights.Length - 1; i >= 0; i--)
        {
            used += Math.Max(0, heights[i]);
            if (used > room)
            {
                return Math.Min(i + 1, heights.Length - 1);
            }
        }

        return 0;
    }
}
