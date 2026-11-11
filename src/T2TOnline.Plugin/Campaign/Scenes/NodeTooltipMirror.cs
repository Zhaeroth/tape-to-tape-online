#if HAS_INTEROP
using System;
using Il2CppSTS.Map;
using T2TOnline.Core.Campaign;
using T2TOnline.Plugin.Diagnostics;
using UnityEngine;

namespace T2TOnline.Plugin.Campaign.Scenes;

/// <summary>
/// A map node's details (its tooltip: the game's <c>MapNode.m_TooltipContainer</c>, with the node's title and what it
/// gives, written in each PC's language by <c>MapNode.SetupTooltip</c>) on every screen as on the host's. The game shows a
/// node's tooltip while the node is selected (<c>MapNode.OnNavigableSelectionChanged</c>), when its blueprint has one and the
/// player's "world map node tooltips" setting is on (<c>GameplaySettingsData.showWorldMapNodeTooltips</c>).
/// <list type="bullet">
/// <item>Host: the map view says whether the cursor's node shows its tooltip (<see cref="MapViewDetails.TooltipShown"/>).</item>
/// <item>Friend: the same node's tooltip is shown or hidden as on the host's screen, whatever this PC's selection or its own
/// setting did; one forced up is put down when the host's cursor moves on. A node whose tooltip this PC never wrote (its
/// own setting was off when the map was built) is left alone, with one line saying so.</item>
/// </list>
/// Nothing here throws into the game.
/// </summary>
internal static class NodeTooltipMirror
{
    private const int NamedLines = 5;

    private static MapNode? _cached;
    private static MapPoint _cachedPoint;
    private static int _cachedScene;
    private static MapNode? _forced;
    private static int _shown;
    private static int _hidden;
    private static int _lines;
    private static bool _settingLogged;
    private static int _errors;

    /// <summary>For the end-of-session line.</summary>
    public static string Summary => $"{_shown} node tooltip(s) shown and {_hidden} hidden as on the host's screen";

    /// <summary>Host: whether the node at <paramref name="selected"/> shows its tooltip now (null: no node, or it can't be told).</summary>
    public static bool? HostShows(MapPoint? selected)
    {
        try
        {
            MapNode? node = Node(selected);
            GameObject? box = node == null ? null : node.m_TooltipContainer;
            return box == null ? null : box.activeSelf;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Friend: following started or stopped.</summary>
    public static void Reset()
    {
        _cached = null;
        _forced = null;
        _settingLogged = false;
    }

    /// <summary>
    /// Friend, as the host's cursor is held on this map: the node at <paramref name="selected"/> shows its tooltip exactly when
    /// the host's does (<paramref name="hostShows"/>; null: the host didn't say, nothing is changed). Never throws.
    /// </summary>
    public static void Follow(MapPoint? selected, bool? hostShows)
    {
        try
        {
            MapNode? node = Node(selected);
            if (_forced != null && (node == null || _forced.WasCollected || _forced.Pointer != node.Pointer))
            {
                // The host's cursor moved on from the node whose tooltip was forced up here.
                MapNode before = _forced;
                _forced = null;
                if (!before.WasCollected && before.m_TooltipContainer is { } old && old.activeSelf && !IsSelectedHere(before))
                {
                    old.SetActive(false);
                }
            }

            if (node == null || hostShows is not bool shows || node.m_TooltipContainer is not { } box || box.activeSelf == shows)
            {
                return;
            }

            if (shows && !node.m_ShowTooltip)
            {
                if (!_settingLogged)
                {
                    _settingLogged = true;
                    ModLog.Info($"Scenes: {LogNames.HostTag()}'s map shows node {selected} details (its tooltip); this map has none written for it (this PC's \"world map node tooltips\" setting was off when the map was built, or the node has none here), so it stays hidden.");
                }

                return;
            }

            box.SetActive(shows);
            _forced = shows ? node : null;
            if (shows)
            {
                _shown++;
            }
            else
            {
                _hidden++;
            }

            if (_lines++ < NamedLines)
            {
                ModLog.Info($"Scenes: node {selected}'s details (its tooltip) {(shows ? "shown" : "hidden")} here as on {LogNames.HostTag()}'s screen{(_lines == NamedLines ? " (further ones are only counted)" : string.Empty)}.");
            }
        }
        catch (Exception ex)
        {
            _forced = null;
            if (_errors++ < 3)
            {
                ModLog.Error("Scenes: showing the host's node details failed", ex);
            }
        }
    }

    private static bool IsSelectedHere(MapNode node)
    {
        try
        {
            return node.Navigable != null && node.Navigable.IsSelected;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>The loaded map's node at <paramref name="point"/> (looked up again only when the point or the scene changes).</summary>
    private static MapNode? Node(MapPoint? point)
    {
        if (point is not MapPoint at)
        {
            return null;
        }

        int scene = WorldMap.SceneHandle;
        if (_cached != null && !_cached.WasCollected && _cachedPoint == at && _cachedScene == scene)
        {
            return _cached;
        }

        _cached = WorldMap.MapLoaded ? WorldMap.NodeAt(at) : null;
        _cachedPoint = at;
        _cachedScene = scene;
        return _cached;
    }
}
#endif
