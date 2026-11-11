using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace T2TOnline.Core.Input;

/// <summary>
/// One element map of a controller map in Rewired's own numbers (its action, element type and id, axis contribution,
/// range and inversion, and a keyboard's key and modifiers), without its id, which changes each time it's made.
/// </summary>
/// <param name="ActionId">The game action.</param>
/// <param name="ElementType">Rewired's element type (axis or button).</param>
/// <param name="ElementId">Rewired's element identifier id.</param>
/// <param name="Contribution">Rewired's axis contribution (positive or negative).</param>
/// <param name="Range">Rewired's axis range.</param>
/// <param name="Invert">Whether the axis is inverted.</param>
/// <param name="KeyCode">A keyboard map's key (0 for anything else).</param>
/// <param name="Modifiers">A keyboard map's modifier keys, packed (0 for none).</param>
public readonly record struct MapElement(int ActionId, int ElementType, int ElementId, int Contribution, int Range, bool Invert, int KeyCode = 0, int Modifiers = 0);

/// <summary>One controller map as read: its category, its layout, whether it's on, and its element maps.</summary>
/// <param name="Category">The map category id.</param>
/// <param name="Layout">The layout id.</param>
/// <param name="Enabled">Whether the map is on.</param>
/// <param name="Elements">Its element maps.</param>
public sealed record ControllerMapReading(int Category, int Layout, bool Enabled, IReadOnlyList<MapElement> Elements);

/// <summary>What one category of a controller's maps holds: how many maps, how many element maps, and their fingerprint.</summary>
/// <param name="Maps">Maps in the category (any layout).</param>
/// <param name="Bindings">Element maps in them.</param>
/// <param name="Hash">Their fingerprint: the same element maps in any order give the same one; a repeat counts.</param>
public readonly record struct CategoryPrint(int Maps, int Bindings, ulong Hash)
{
    /// <summary>The fingerprint in 8 hex digits, for the log.</summary>
    public string Tag => ControllerMapsPrint.TagOf(Hash);
}

/// <summary>
/// A controller's maps, category by category (how many maps, how many element maps, a fingerprint of them), and which
/// categories are on. What a friend's virtual controller should have is one of these, taken right after the mod built its
/// maps; what it has now is another, compared with <see cref="MapsDrift"/>. Whether a map is on is kept apart and isn't in
/// the fingerprint: the game switches categories on and off as it plays (menus, matches, goalie control, brawls).
/// </summary>
public sealed class ControllerMapsPrint
{
    private readonly SortedDictionary<int, CategoryPrint> _categories;

    private ControllerMapsPrint(SortedDictionary<int, CategoryPrint> categories, int[] enabled)
    {
        _categories = categories;
        Enabled = enabled;
        foreach (KeyValuePair<int, CategoryPrint> category in categories)
        {
            Maps += category.Value.Maps;
            Bindings += category.Value.Bindings;
            Hash += Mix(category.Value.Hash + ((ulong)(uint)category.Key * 0x9E3779B97F4A7C15UL) + ((ulong)(uint)category.Value.Maps * 0xC2B2AE3D27D4EB4FUL));
        }
    }

    /// <summary>No maps at all.</summary>
    public static ControllerMapsPrint Empty { get; } = new(new SortedDictionary<int, CategoryPrint>(), Array.Empty<int>());

    /// <summary>Each category with a map, by id.</summary>
    public IReadOnlyDictionary<int, CategoryPrint> Categories => _categories;

    /// <summary>The categories with a map that's on, in order.</summary>
    public IReadOnlyList<int> Enabled { get; }

    /// <summary>Maps in every category.</summary>
    public int Maps { get; }

    /// <summary>Element maps in every category.</summary>
    public int Bindings { get; }

    /// <summary>The fingerprint of every category's maps together (whether they're on aside).</summary>
    public ulong Hash { get; }

    /// <summary><see cref="Hash"/> in 8 hex digits, for the log.</summary>
    public string Tag => TagOf(Hash);

    /// <summary>The print of <paramref name="maps"/>.</summary>
    public static ControllerMapsPrint Of(IEnumerable<ControllerMapReading> maps)
    {
        ArgumentNullException.ThrowIfNull(maps);
        var categories = new SortedDictionary<int, CategoryPrint>();
        var enabled = new SortedSet<int>();
        foreach (ControllerMapReading map in maps)
        {
            if (map == null)
            {
                continue;
            }

            ulong hash = 0;
            foreach (MapElement element in map.Elements ?? Array.Empty<MapElement>())
            {
                hash += HashOf(map.Layout, element);
            }

            CategoryPrint before = categories.TryGetValue(map.Category, out CategoryPrint print) ? print : default;
            categories[map.Category] = new CategoryPrint(before.Maps + 1, before.Bindings + (map.Elements?.Count ?? 0), before.Hash + hash);
            if (map.Enabled)
            {
                enabled.Add(map.Category);
            }
        }

        return new ControllerMapsPrint(categories, enabled.ToArray());
    }

    /// <summary>The category's print, or an empty one (no map) when it has none.</summary>
    public CategoryPrint In(int category) => _categories.TryGetValue(category, out CategoryPrint print) ? print : default;

    /// <summary>True when both hold the same maps with the same element maps, category by category (whether they're on aside).</summary>
    public bool SameMapsAs(ControllerMapsPrint? other) =>
        other != null && other._categories.Count == _categories.Count && _categories.All(c => other.In(c.Key) == c.Value);

    /// <summary>Bindings per category by name, for the log: "Default 30, UI 35, Fighting 7" ("none" with no map).</summary>
    public string Counts(Func<int, string> categoryName)
    {
        ArgumentNullException.ThrowIfNull(categoryName);
        return _categories.Count == 0
            ? "none"
            : string.Join(", ", _categories.Select(c => string.Create(CultureInfo.InvariantCulture, $"{categoryName(c.Key)} {c.Value.Bindings}{(c.Value.Maps == 1 ? string.Empty : $" in {c.Value.Maps} maps")}")));
    }

    /// <summary>The categories that are on, by name: "[Default, Fighting]" ("[]" with none).</summary>
    public static string NameSet(IEnumerable<int> categories, Func<int, string> categoryName)
    {
        ArgumentNullException.ThrowIfNull(categories);
        ArgumentNullException.ThrowIfNull(categoryName);
        return "[" + string.Join(", ", categories.Select(categoryName)) + "]";
    }

    /// <summary>A fingerprint in 8 hex digits: both halves folded together.</summary>
    internal static string TagOf(ulong hash) => ((uint)(hash ^ (hash >> 32))).ToString("x8", CultureInfo.InvariantCulture);

    private static ulong HashOf(int layout, in MapElement element)
    {
        // FNV-1a over the element's numbers, then mixed, so that summing elements (order-free) still separates them.
        ulong hash = 14695981039346656037UL;
        hash = Add(hash, layout);
        hash = Add(hash, element.ActionId);
        hash = Add(hash, element.ElementType);
        hash = Add(hash, element.ElementId);
        hash = Add(hash, element.Contribution);
        hash = Add(hash, element.Range);
        hash = Add(hash, element.Invert ? 1 : 0);
        hash = Add(hash, element.KeyCode);
        hash = Add(hash, element.Modifiers);
        return Mix(hash);
    }

    private static ulong Add(ulong hash, int value)
    {
        for (int shift = 0; shift < 32; shift += 8)
        {
            hash ^= (byte)(value >> shift);
            hash *= 1099511628211UL;
        }

        return hash;
    }

    private static ulong Mix(ulong value)
    {
        // splitmix64's finalizer
        value ^= value >> 30;
        value *= 0xBF58476D1CE4E5B9UL;
        value ^= value >> 27;
        value *= 0x94D049BB133111EBUL;
        return value ^ (value >> 31);
    }
}

/// <summary>
/// How a controller's maps differ from what they should be, category by category: maps missing (a category that should
/// have one has none, or fewer), extra maps (one where none should be, or more), and maps with other element maps.
/// Whether a map is on isn't drift (the game switches categories).
/// </summary>
public sealed class MapsDrift
{
    private MapsDrift(int[] missing, int[] extra, int[] changed)
    {
        Missing = missing;
        Extra = extra;
        Changed = changed;
        Kind = None
            ? "none"
            : string.Join("; ", new[] { Part("missing", missing), Part("extra", extra), Part("changed", changed) }.Where(p => p.Length > 0));
    }

    /// <summary>Categories with fewer maps than they should have (none, usually).</summary>
    public IReadOnlyList<int> Missing { get; }

    /// <summary>Categories with more maps than they should have.</summary>
    public IReadOnlyList<int> Extra { get; }

    /// <summary>Categories with the maps they should have, holding other element maps.</summary>
    public IReadOnlyList<int> Changed { get; }

    /// <summary>True when nothing differs.</summary>
    public bool None => Missing.Count == 0 && Extra.Count == 0 && Changed.Count == 0;

    /// <summary>Which categories drifted how, as ids ("missing 0,2; changed 7"): the same kind is logged once, then counted.</summary>
    public string Kind { get; }

    /// <summary>The difference between <paramref name="expected"/> and <paramref name="now"/>.</summary>
    public static MapsDrift Between(ControllerMapsPrint expected, ControllerMapsPrint now)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(now);
        var missing = new List<int>();
        var extra = new List<int>();
        var changed = new List<int>();
        foreach (int category in expected.Categories.Keys.Union(now.Categories.Keys).OrderBy(c => c))
        {
            CategoryPrint should = expected.In(category);
            CategoryPrint has = now.In(category);
            if (has.Maps < should.Maps)
            {
                missing.Add(category);
            }
            else if (has.Maps > should.Maps)
            {
                extra.Add(category);
            }
            else if (has != should)
            {
                changed.Add(category);
            }
        }

        return new MapsDrift(missing.ToArray(), extra.ToArray(), changed.ToArray());
    }

    /// <summary>What drifted, by name, for the log: "Default: 30 bindings (1a2b3c4d), now 0 (00000000); UI: its map is missing (35 bindings)".</summary>
    public string Describe(ControllerMapsPrint expected, ControllerMapsPrint now, Func<int, string> categoryName)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(now);
        ArgumentNullException.ThrowIfNull(categoryName);
        var parts = new List<string>();
        foreach (int category in Missing)
        {
            CategoryPrint should = expected.In(category);
            parts.Add(now.In(category).Maps == 0
                ? string.Create(CultureInfo.InvariantCulture, $"{categoryName(category)}: its map is missing ({should.Bindings} bindings)")
                : string.Create(CultureInfo.InvariantCulture, $"{categoryName(category)}: {now.In(category).Maps} of its {should.Maps} maps there"));
        }

        foreach (int category in Extra)
        {
            CategoryPrint has = now.In(category);
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{categoryName(category)}: {has.Maps} maps ({has.Bindings} bindings) where {expected.In(category).Maps} should be"));
        }

        foreach (int category in Changed)
        {
            CategoryPrint should = expected.In(category);
            CategoryPrint has = now.In(category);
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{categoryName(category)}: {should.Bindings} bindings ({should.Tag}), now {has.Bindings} ({has.Tag})"));
        }

        return parts.Count == 0 ? "nothing" : string.Join("; ", parts);
    }

    private static string Part(string what, int[] categories) =>
        categories.Length == 0 ? string.Empty : $"{what} {string.Join(",", categories.Select(c => c.ToString(CultureInfo.InvariantCulture)))}";
}
