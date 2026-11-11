#if HAS_INTEROP
using System.Collections.Generic;
using Il2CppRewired;
using T2TOnline.Core.Input;
using T2TOnline.Plugin.Native;

namespace T2TOnline.Plugin.Input;

/// <summary>
/// Reads one controller's maps in one Rewired player as Core's <see cref="ControllerMapsPrint"/> (every map, any layout,
/// element map by element map), or just which categories have a map and which are on: what the maps guard compares
/// (<see cref="MapsGuard"/>, Online/OnlineSession.Maps.cs), and what a give-back's log shows. Reading changes nothing.
/// </summary>
internal static class ControllerMapsReader
{
    /// <summary>Every map of controller <paramref name="controllerId"/> (of <paramref name="type"/>) in <paramref name="player"/>, as a print.</summary>
    public static ControllerMapsPrint Read(Player player, ControllerType type, int controllerId)
    {
        var readings = new List<ControllerMapReading>();
        foreach (ControllerMap map in Il2CppLists.Items(player.controllers.maps.GetMaps(type, controllerId)))
        {
            if (map == null)
            {
                continue;
            }

            var elements = new List<MapElement>();
            foreach (ActionElementMap element in Il2CppLists.Items(map.AllMaps))
            {
                elements.Add(new MapElement(element.actionId, (int)element.elementType, element.elementIdentifierId, (int)element.axisContribution, (int)element.axisRange,
                    element.invert, (int)element.keyCode, (int)element.modifierKey1 | ((int)element.modifierKey2 << 4) | ((int)element.modifierKey3 << 8)));
            }

            readings.Add(new ControllerMapReading(map.categoryId, map.layoutId, map.enabled, elements));
        }

        return ControllerMapsPrint.Of(readings);
    }

    /// <summary>Each map of the controller in <paramref name="player"/>: its category and whether it's on (a cheap look).</summary>
    public static List<(int Category, bool Enabled)> Look(Player player, ControllerType type, int controllerId)
    {
        var maps = new List<(int, bool)>();
        foreach (ControllerMap map in Il2CppLists.Items(player.controllers.maps.GetMaps(type, controllerId)))
        {
            if (map != null)
            {
                maps.Add((map.categoryId, map.enabled));
            }
        }

        return maps;
    }
}
#endif
