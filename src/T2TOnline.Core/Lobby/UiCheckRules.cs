using System;
using System.Collections.Generic;
using System.Globalization;

namespace T2TOnline.Core.Lobby;

/// <summary>What the check tour flags on a screen.</summary>
public enum UiCheckIssue
{
    /// <summary>A text or navigable reaches outside the canvas by more than the slack.</summary>
    Offscreen,

    /// <summary>A text is wider than its rect with wrapping off, or was truncated.</summary>
    TextOver,

    /// <summary>An auto-sizing text sits at its minimum size: it may not fit.</summary>
    AutoSizeMin,

    /// <summary>Two sibling texts intersect.</summary>
    Overlap,

    /// <summary>A text or navigable reaches outside an ancestor mask.</summary>
    Clipped,

    /// <summary>A text has characters its font chain can't draw.</summary>
    NoGlyph,

    /// <summary>A navigable has no neighbour, or the default selection is off screen or inactive.</summary>
    NavHole,

    /// <summary>A sprite fallback was used.</summary>
    MissingArt,

    /// <summary>A <c>Localize</c>'s term has no text in this language.</summary>
    NoTerm,
}

/// <summary>A rectangle in canvas units (origin bottom left).</summary>
/// <param name="X">The left edge.</param>
/// <param name="Y">The bottom edge.</param>
/// <param name="Width">The width.</param>
/// <param name="Height">The height.</param>
public readonly record struct UiRect(float X, float Y, float Width, float Height)
{
    /// <summary>The right edge.</summary>
    public float Right => X + Width;

    /// <summary>The top edge.</summary>
    public float Top => Y + Height;

    /// <summary>The area two rectangles share (0 when they don't touch).</summary>
    public float SharedArea(UiRect other)
    {
        float width = Math.Min(Right, other.Right) - Math.Max(X, other.X);
        float height = Math.Min(Top, other.Top) - Math.Max(Y, other.Y);
        return width <= 0 || height <= 0 ? 0 : width * height;
    }

    /// <summary>How far this rectangle reaches outside <paramref name="outer"/> on its worst side (0 when it's inside).</summary>
    public float ReachOutside(UiRect outer) =>
        Math.Max(0, Math.Max(Math.Max(outer.X - X, outer.Y - Y), Math.Max(Right - outer.Right, Top - outer.Top)));
}

/// <summary>What a text shows and how it's sized, as the tour reads it from the game (plain data).</summary>
public sealed record UiTextFacts
{
    /// <summary>The text shown.</summary>
    public string Shown { get; init; } = string.Empty;

    /// <summary>The term of its <c>Localize</c>, or null for none.</summary>
    public string? Term { get; init; }

    /// <summary>True when the term has a text in this language.</summary>
    public bool TermHasText { get; init; } = true;

    /// <summary>The width the text wants.</summary>
    public float PreferredWidth { get; init; }

    /// <summary>The width it has.</summary>
    public float AvailableWidth { get; init; }

    /// <summary>True when it wraps.</summary>
    public bool Wraps { get; init; }

    /// <summary>True when it was cut short (<c>isTextTruncated</c>).</summary>
    public bool Truncated { get; init; }

    /// <summary>True when it auto-sizes.</summary>
    public bool AutoSizes { get; init; }

    /// <summary>The size in use.</summary>
    public float Size { get; init; }

    /// <summary>The auto-size minimum.</summary>
    public float SizeMin { get; init; }

    /// <summary>The characters its font chain lacks ("" for none).</summary>
    public string MissingGlyphs { get; init; } = string.Empty;
}

/// <summary>One active rect on a screen, as the tour reads it from the game (plain data, so the rules can be tested).</summary>
public sealed record UiElement
{
    /// <summary>The hierarchy path.</summary>
    public string Path { get; init; } = string.Empty;

    /// <summary>The index of its parent in the list, or -1 for the root.</summary>
    public int Parent { get; init; } = -1;

    /// <summary>Where it is on the canvas.</summary>
    public UiRect Rect { get; init; }

    /// <summary>The text on it, or null.</summary>
    public UiTextFacts? Text { get; init; }

    /// <summary>True when it's navigable (pad and keyboard can reach it).</summary>
    public bool Navigable { get; init; }

    /// <summary>The number of neighbours it links to (navigables only).</summary>
    public int Neighbors { get; init; }

    /// <summary>True when it masks its children (<c>Mask</c>, <c>RectMask2D</c>).</summary>
    public bool Masks { get; init; }

    /// <summary>True when it's a screen's default selection.</summary>
    public bool IsDefault { get; init; }

    /// <summary>A description of the fallback sprite in use, or null.</summary>
    public string? ArtFallback { get; init; }
}

/// <summary>One flag on one element.</summary>
/// <param name="Flag">The flag.</param>
/// <param name="Path">The element's path.</param>
/// <param name="Detail">The numbers behind it.</param>
public sealed record UiCheckHit(UiCheckIssue Flag, string Path, string Detail);

/// <summary>
/// The check tour's rules: what's wrong with a screen, from plain facts the plugin reads off the game.
/// TMP's own overflow flag isn't used: horizontal overflow doesn't set it, so widths are measured.
/// </summary>
public static class UiCheckRules
{
    /// <summary>How far past an edge counts, in canvas units.</summary>
    public const float Slack = 2f;

    /// <summary>How much two texts must share before they count as overlapping, in square canvas units.</summary>
    public const float OverlapArea = 4f;

    /// <summary>The name a flag goes by in the dumps.</summary>
    public static string Name(UiCheckIssue flag) => flag switch
    {
        UiCheckIssue.Offscreen => "OFFSCREEN",
        UiCheckIssue.TextOver => "TEXT_OVER",
        UiCheckIssue.AutoSizeMin => "AUTOSIZE_MIN",
        UiCheckIssue.Overlap => "OVERLAP",
        UiCheckIssue.Clipped => "CLIPPED",
        UiCheckIssue.NoGlyph => "NO_GLYPH",
        UiCheckIssue.NavHole => "NAV_HOLE",
        UiCheckIssue.MissingArt => "MISSING_ART",
        UiCheckIssue.NoTerm => "NO_TERM",
        _ => flag.ToString(),
    };

    /// <summary>Every flag on a screen whose canvas is <paramref name="canvas"/>, element by element.</summary>
    public static List<UiCheckHit> Compute(IReadOnlyList<UiElement> elements, UiRect canvas)
    {
        ArgumentNullException.ThrowIfNull(elements);
        var hits = new List<UiCheckHit>();
        for (int i = 0; i < elements.Count; i++)
        {
            UiElement element = elements[i];
            bool matters = element.Text is { Shown.Length: > 0 } || element.Navigable;
            if (matters)
            {
                float outside = element.Rect.ReachOutside(canvas);
                if (outside > Slack)
                {
                    hits.Add(new UiCheckHit(UiCheckIssue.Offscreen, element.Path, string.Create(CultureInfo.InvariantCulture, $"{outside:0.#} past the canvas")));
                }

                ClippedBy(elements, i, hits);
            }

            if (element.Text != null)
            {
                TextRules(element, hits);
            }

            if (element.Navigable && element.Neighbors == 0)
            {
                hits.Add(new UiCheckHit(UiCheckIssue.NavHole, element.Path, "no neighbours"));
            }

            if (element.IsDefault && (element.Rect.ReachOutside(canvas) > Slack || element.Rect.Width <= 0 || element.Rect.Height <= 0))
            {
                hits.Add(new UiCheckHit(UiCheckIssue.NavHole, element.Path, "the default selection is off screen"));
            }

            if (element.ArtFallback != null)
            {
                hits.Add(new UiCheckHit(UiCheckIssue.MissingArt, element.Path, element.ArtFallback));
            }
        }

        Overlaps(elements, hits);
        return hits;
    }

    /// <summary>The number of hits per flag, as "OFFSCREEN 1, TEXT_OVER 2" ("none" for none).</summary>
    public static string Counts(IEnumerable<UiCheckHit> hits)
    {
        ArgumentNullException.ThrowIfNull(hits);
        var counts = new SortedDictionary<UiCheckIssue, int>();
        foreach (UiCheckHit hit in hits)
        {
            counts[hit.Flag] = counts.GetValueOrDefault(hit.Flag) + 1;
        }

        if (counts.Count == 0)
        {
            return "none";
        }

        var parts = new List<string>(counts.Count);
        foreach (KeyValuePair<UiCheckIssue, int> count in counts)
        {
            parts.Add($"{Name(count.Key)} {count.Value}");
        }

        return string.Join(", ", parts);
    }

    /// <summary>
    /// A tour's layout flags as a table to diff two runs by (<c>flags.tsv</c>): a header, then one row per screen and flag
    /// (<c>screen&lt;TAB&gt;flag&lt;TAB&gt;count</c>), screens in the order first seen, flags in their order, and
    /// <c>none 0</c> for a screen with none. A screen seen twice adds up. Screen ids should leave out anything that differs
    /// between runs of the same tour (the capture's counter).
    /// </summary>
    public static string FlagsTable(IEnumerable<KeyValuePair<string, IReadOnlyList<UiCheckHit>>> screens)
    {
        ArgumentNullException.ThrowIfNull(screens);
        var order = new List<string>();
        var counts = new Dictionary<string, SortedDictionary<UiCheckIssue, int>>(StringComparer.Ordinal);
        foreach (KeyValuePair<string, IReadOnlyList<UiCheckHit>> screen in screens)
        {
            string id = (screen.Key ?? string.Empty).Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ');
            if (!counts.TryGetValue(id, out SortedDictionary<UiCheckIssue, int>? flags))
            {
                counts[id] = flags = new SortedDictionary<UiCheckIssue, int>();
                order.Add(id);
            }

            foreach (UiCheckHit hit in screen.Value ?? Array.Empty<UiCheckHit>())
            {
                flags[hit.Flag] = flags.GetValueOrDefault(hit.Flag) + 1;
            }
        }

        var table = new System.Text.StringBuilder("screen\tflag\tcount\n");
        foreach (string id in order)
        {
            if (counts[id].Count == 0)
            {
                table.Append(id).Append("\tnone\t0\n");
                continue;
            }

            foreach (KeyValuePair<UiCheckIssue, int> flag in counts[id])
            {
                table.Append(id).Append('\t').Append(Name(flag.Key)).Append('\t').Append(flag.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append('\n');
            }
        }

        return table.ToString();
    }

    private static void TextRules(UiElement element, List<UiCheckHit> hits)
    {
        UiTextFacts text = element.Text!;
        if (text.Truncated)
        {
            hits.Add(new UiCheckHit(UiCheckIssue.TextOver, element.Path, "truncated"));
        }
        else if (!text.Wraps && text.Shown.Length > 0 && text.PreferredWidth > text.AvailableWidth + 1f)
        {
            hits.Add(new UiCheckHit(UiCheckIssue.TextOver, element.Path, string.Create(CultureInfo.InvariantCulture, $"wants {text.PreferredWidth:0.#}, has {text.AvailableWidth:0.#}")));
        }

        if (text.AutoSizes && text.Shown.Length > 0 && text.Size <= text.SizeMin + 0.01f)
        {
            hits.Add(new UiCheckHit(UiCheckIssue.AutoSizeMin, element.Path, string.Create(CultureInfo.InvariantCulture, $"at its minimum, {text.SizeMin:0.#}")));
        }

        if (text.MissingGlyphs.Length > 0)
        {
            hits.Add(new UiCheckHit(UiCheckIssue.NoGlyph, element.Path, $"lacks \"{text.MissingGlyphs}\""));
        }

        if (text.Term != null && !text.TermHasText)
        {
            hits.Add(new UiCheckHit(UiCheckIssue.NoTerm, element.Path, text.Term));
        }
    }

    /// <summary>A flag for the nearest ancestor mask the element reaches outside of.</summary>
    private static void ClippedBy(IReadOnlyList<UiElement> elements, int index, List<UiCheckHit> hits)
    {
        UiElement element = elements[index];
        for (int at = element.Parent, steps = 0; at >= 0 && at < elements.Count && steps < elements.Count; at = elements[at].Parent, steps++)
        {
            UiElement ancestor = elements[at];
            if (!ancestor.Masks)
            {
                continue;
            }

            float outside = element.Rect.ReachOutside(ancestor.Rect);
            if (outside > Slack)
            {
                hits.Add(new UiCheckHit(UiCheckIssue.Clipped, element.Path, string.Create(CultureInfo.InvariantCulture, $"{outside:0.#} past the mask {ancestor.Path}")));
            }

            return;
        }
    }

    /// <summary>A flag for each pair of sibling texts that intersect.</summary>
    private static void Overlaps(IReadOnlyList<UiElement> elements, List<UiCheckHit> hits)
    {
        var byParent = new Dictionary<int, List<int>>();
        for (int i = 0; i < elements.Count; i++)
        {
            if (elements[i].Text is { Shown.Length: > 0 })
            {
                if (!byParent.TryGetValue(elements[i].Parent, out List<int>? siblings))
                {
                    byParent[elements[i].Parent] = siblings = new List<int>();
                }

                siblings.Add(i);
            }
        }

        foreach (List<int> siblings in byParent.Values)
        {
            for (int a = 0; a < siblings.Count; a++)
            {
                for (int b = a + 1; b < siblings.Count; b++)
                {
                    UiElement first = elements[siblings[a]];
                    UiElement second = elements[siblings[b]];
                    float shared = first.Rect.SharedArea(second.Rect);
                    if (shared > OverlapArea)
                    {
                        hits.Add(new UiCheckHit(UiCheckIssue.Overlap, first.Path, string.Create(CultureInfo.InvariantCulture, $"shares {shared:0.#} with {second.Path}")));
                    }
                }
            }
        }
    }
}
