#if HAS_INTEROP
using System.Collections.Generic;
using System.Linq;
using Il2CppRewired;
using T2TOnline.Plugin.Diagnostics;
using T2TOnline.Plugin.Native;
using KeyCode = UnityEngine.KeyCode;

namespace T2TOnline.Plugin.Input;

/// <summary>
/// One controller's maps in one Rewired player, element map by element map, taken before the mod takes the controller
/// away (a friend's keyboard and mouse, or their pad) and checked after it gives it back: whether Rewired reassigning a
/// controller reloads its default maps (dropping saved remaps until the game restarts) isn't known, so any map that came
/// back different, or not at all, is rebuilt from here, and whether each was on is put back.
/// </summary>
internal sealed class MapSnapshot
{
    private readonly List<(int Category, bool Enabled, List<RawElementMap> Elements)> _maps = new();

    private MapSnapshot(int playerId, ControllerType type, int controllerId)
    {
        PlayerId = playerId;
        Type = type;
        ControllerId = controllerId;
    }

    /// <summary>The Rewired player.</summary>
    public int PlayerId { get; }

    /// <summary>The controller's type.</summary>
    public ControllerType Type { get; }

    /// <summary>The controller's id.</summary>
    public int ControllerId { get; }

    /// <summary>Element maps in the snapshot.</summary>
    public int Bindings => _maps.Sum(m => m.Elements.Count);

    /// <summary>
    /// "117 bindings in the 5 maps it holds (Default, UI, ...)": the snapshot's count is of the maps the player holds, not
    /// of every category the start-up read counts through the saved or default maps (123 at the start, 117
    /// given back, the same maps).
    /// </summary>
    public string Describe() =>
        $"{Bindings} bindings in the {_maps.Count} map(s) it holds ({string.Join(", ", _maps.Select(m => RewiredBindings.CategoryName(m.Category)))})";

    /// <summary>Takes every map of controller <paramref name="controllerId"/> in <paramref name="player"/>.</summary>
    public static MapSnapshot Take(Player player, ControllerType type, int controllerId)
    {
        var snapshot = new MapSnapshot(player.id, type, controllerId);
        foreach (ControllerMap map in Il2CppLists.Items(player.controllers.maps.GetMaps(type, controllerId)))
        {
            if (map.layoutId == 0)
            {
                snapshot._maps.Add((map.categoryId, map.enabled, Read(map)));
            }
        }

        return snapshot;
    }

    /// <summary>
    /// Compares the player's maps of the controller with the snapshot, now that it's back: a map missing or with other
    /// element maps is loaded or rebuilt as it was, and every map's on/off is put back. Returns how many maps were rebuilt.
    /// </summary>
    public int Restore(Player player, int controllerId)
    {
        Player.ControllerHelper.MapHelper maps = player.controllers.maps;
        int rebuilt = 0;
        foreach ((int category, bool enabled, List<RawElementMap> elements) in _maps)
        {
            ControllerMap? map = maps.GetMap(Type, controllerId, category, 0);
            if (map == null)
            {
                maps.LoadMap(Type, controllerId, category, 0, enabled);
                map = maps.GetMap(Type, controllerId, category, 0);
                if (map == null)
                {
                    ModLog.Warn($"Your {Type} maps: category {RewiredBindings.CategoryName(category)} couldn't be loaded again on Rewired player {player.id}.");
                    continue;
                }
            }

            List<RawElementMap> now = Read(map);
            if (!SameElements(now, elements))
            {
                RewiredBindings.Clear(map);
                foreach (RawElementMap element in elements)
                {
                    element.CreateIn(map);
                }

                rebuilt++;
            }

            map.enabled = enabled;
        }

        return rebuilt;
    }

    private static List<RawElementMap> Read(ControllerMap map)
    {
        var elements = new List<RawElementMap>();
        foreach (ActionElementMap element in Il2CppLists.Items(map.AllMaps))
        {
            elements.Add(new RawElementMap(element.actionId, element.elementType, element.elementIdentifierId, element.axisContribution, element.axisRange, element.invert,
                element.keyCode, element.modifierKey1, element.modifierKey2, element.modifierKey3));
        }

        return elements;
    }

    /// <summary>True when both hold the same element maps, in any order (as multisets).</summary>
    private static bool SameElements(List<RawElementMap> a, List<RawElementMap> b)
    {
        if (a.Count != b.Count)
        {
            return false;
        }

        var counts = new Dictionary<RawElementMap, int>();
        foreach (RawElementMap element in a)
        {
            counts[element] = counts.GetValueOrDefault(element) + 1;
        }

        foreach (RawElementMap element in b)
        {
            if (!counts.TryGetValue(element, out int left) || left == 0)
            {
                return false;
            }

            counts[element] = left - 1;
        }

        return true;
    }

    /// <summary>One element map as Rewired has it: a key (with modifiers) on a keyboard, an element by id on anything else.</summary>
    private readonly record struct RawElementMap(int ActionId, ControllerElementType Type, int ElementId, Pole Contribution, AxisRange Range, bool Invert,
        KeyCode Key, ModifierKey Modifier1, ModifierKey Modifier2, ModifierKey Modifier3)
    {
        public void CreateIn(ControllerMap map)
        {
            if (map.controllerType == ControllerType.Keyboard && Key != KeyCode.None)
            {
                map.CreateElementMap(ActionId, Contribution, Key, Modifier1, Modifier2, Modifier3);
            }
            else
            {
                map.CreateElementMap(ActionId, Contribution, ElementId, Type, Range, Invert);
            }
        }
    }
}
#endif
