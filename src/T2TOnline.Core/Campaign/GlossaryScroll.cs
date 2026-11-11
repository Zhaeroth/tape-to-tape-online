using System;
using System.Globalization;

namespace T2TOnline.Core.Campaign;

/// <summary>
/// Where the glossary's highlighted row sits in its list: the row's top below the list's visible area's top, in thousandths
/// of that area's height (0: at the top, 1000: at the bottom; below 0 or above 1000 when the host scrolled it out of sight
/// with the wheel), as the menu value <c>gl.rowY</c>. The host's game measures its row; a friend's moves its own list so
/// its row of the same entry sits there (the rest of its list in its own language's order). Only the arithmetic; the game
/// glue measures and moves.
/// </summary>
public static class GlossaryScroll
{
    /// <summary>Furthest a row is said to sit from the visible area's top, either way, in thousandths of its height.</summary>
    public const int Limit = 5000;

    /// <summary>Smallest change worth sending again, in thousandths (half a percent of the list's height).</summary>
    public const int Threshold = 5;

    /// <summary>
    /// The row's place in thousandths of the visible area's height, from the area's top and bottom and the row's top (world
    /// heights, up being more), clamped to ±<see cref="Limit"/>; null when the area has no height or a number isn't finite.
    /// </summary>
    public static int? Milli(float areaTop, float areaBottom, float rowTop)
    {
        float height = areaTop - areaBottom;
        if (!float.IsFinite(areaTop) || !float.IsFinite(areaBottom) || !float.IsFinite(rowTop) || height <= 0)
        {
            return null;
        }

        double milli = (areaTop - rowTop) / (double)height * 1000;
        return (int)Math.Round(Math.Clamp(milli, -Limit, Limit), MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// How far (world height, up being more) to move a list's content so its row, now at <paramref name="here"/>, sits at
    /// <paramref name="host"/> (both in thousandths of an area <paramref name="areaHeight"/> high). 0 when the area has no
    /// height.
    /// </summary>
    public static float Shift(int host, int here, float areaHeight) =>
        !float.IsFinite(areaHeight) || areaHeight <= 0 ? 0 : (float)((here - (double)host) * areaHeight / 1000);

    /// <summary>True when the row has moved enough since <paramref name="sent"/> (null: nothing sent yet) to send it again.</summary>
    public static bool Moved(int? sent, int now) => sent is not int last || Math.Abs(now - last) >= Threshold;

    /// <summary>A value as the host sent it: a whole number within ±<see cref="Limit"/>. False for anything else.</summary>
    public static bool TryRead(long value, out int milli)
    {
        milli = value is >= -Limit and <= Limit ? (int)value : 0;
        return value is >= -Limit and <= Limit;
    }

    /// <summary>For logs: "62% of the list's height" ("above the list" or "below the list" when out of sight).</summary>
    public static string Describe(int milli) => milli < 0 ? "above the list" : milli > 1000 ? "below the list"
        : string.Create(CultureInfo.InvariantCulture, $"{(int)Math.Round(milli / 10.0, MidpointRounding.AwayFromZero)}% of the list's height");
}
