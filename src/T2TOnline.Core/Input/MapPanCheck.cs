using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace T2TOnline.Core.Input;

/// <summary>
/// The words of a following friend's one check of the input lock: which keyboard and mouse maps the lock leaves on, and
/// whether any of them binds an action the campaign map's camera pans with (the game's <c>ScrollNonUI.Update</c> asks each
/// game player for <see cref="PanActions"/>). A pad friend's keyboard stays on its player, and the lock switches off only
/// its UI maps, so a map left on that binds one of these would let that keyboard pan this PC's map camera. Game-independent.
/// </summary>
public static class MapPanCheck
{
    /// <summary>The Rewired actions the map's camera pans with: UI_Back (27), UI_Scroll_Horizontal (30), UI_Tab_Left (37), UI_Tab_Right (38).</summary>
    public static IReadOnlyList<int> PanActions { get; } = new[] { 27, 30, 37, 38 };

    // The most categories a line names (a game has a handful).
    private const int MaxNamed = 12;

    /// <summary>
    /// The line: <c>Friend UI: after the lock, keyboard/mouse maps still on: Default (keyboard) 2, Default (mouse) 1; the map's
    /// pan actions (27, 30, 37, 38) are bound on none.</c>
    /// </summary>
    /// <param name="stillOn">Each map still on: its category's name and device, and whether it binds a pan action.</param>
    public static string Describe(IReadOnlyList<(string Category, bool BindsPan)> stillOn)
    {
        ArgumentNullException.ThrowIfNull(stillOn);
        string actions = string.Join(", ", PanActions.Select(a => a.ToString(CultureInfo.InvariantCulture)));
        string on = stillOn.Count == 0 ? "none" : Counted(stillOn.Select(m => m.Category));
        List<string> binding = stillOn.Where(m => m.BindsPan).Select(m => m.Category).ToList();
        string bound = binding.Count == 0 ? "none" : Counted(binding);
        return $"Friend UI: after the lock, keyboard/mouse maps still on: {on}; the map's pan actions ({actions}) are bound on {bound}.";
    }

    /// <summary>The line when the check couldn't be made (<paramref name="why"/>: an error's safe text, or what was missing).</summary>
    public static string Failed(string why) =>
        $"Friend UI: after the lock, the keyboard and mouse maps still on couldn't be checked ({why}); the map's pan actions are not known to be off.";

    private static string Counted(IEnumerable<string> names)
    {
        var counts = names.GroupBy(n => n, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal).ToList();
        string named = string.Join(", ", counts.Take(MaxNamed).Select(g => string.Create(CultureInfo.InvariantCulture, $"{g.Key} {g.Count()}")));
        return counts.Count > MaxNamed ? $"{named}, {counts.Count - MaxNamed} more" : named;
    }
}
