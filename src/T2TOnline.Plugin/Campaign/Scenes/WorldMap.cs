#if HAS_INTEROP
using System.Collections.Generic;
using Il2CppBishopGames.Module;
using Il2CppBishopGames.UI;
using Il2CppMichsky.LSS;
using Il2CppSTS.Map;
using Il2CppState;
using Il2CppTape2Tape.Hockey.UI;
using T2TOnline.Core.Campaign;
using T2TOnline.Plugin.Diagnostics;
using UnityEngine.SceneManagement;
using Point = Il2CppSTS.Map.Point;

namespace T2TOnline.Plugin.Campaign.Scenes;

/// <summary>
/// What the scene glue reads from and does to the game's campaign scenes: which
/// scene is up, whether its world map has finished loading, the run, act and path it shows, the map cursor
/// and camera, and the game's own scene loads. Game thread only; the callers guard against exceptions.
/// </summary>
internal static class WorldMap
{
    /// <summary>The title screen's scene.</summary>
    public const string TitleScene = "start_screen";

    /// <summary>The world map's scene.</summary>
    public const string MapScene = "world_map";

    /// <summary>The match scene.</summary>
    public const string MatchScene = "standard_game";

    // The map's FMOD location parameter and its value there (the game's own strings).
    private const string LocationParameter = "location";
    private const string MapLocation = "worldmap";

    private static int _zoomedScene;
    private static string _staleRewards = string.Empty;

    /// <summary>The active scene's name ("" when there's none).</summary>
    public static string SceneName => SceneManager.GetActiveScene().name ?? string.Empty;

    /// <summary>The active scene's handle: a new load of the same scene has a new one.</summary>
    public static int SceneHandle => SceneManager.GetActiveScene().handle;

    /// <summary>
    /// True once the world map in the active scene has finished building: the game raised
    /// <c>RogueEvents.WorldMapInstanceLoaded</c>, whose listener <c>WorldMapMenusHandler.OnMapLoaded</c> sets
    /// <c>m_IsWorldMapLoaded</c>, and the map's objects are up.
    /// </summary>
    public static bool MapLoaded
    {
        get
        {
            if (SceneName != MapScene)
            {
                return false;
            }

            WorldMapMenusHandler? handler = WorldMapMenusHandler.s_Instance;
            return handler != null && handler.m_IsWorldMapLoaded && MapObject.Current != null && MapPlayerTracker.Instance != null &&
                MapPlayerTracker.Instance.mapManager?.CurrentMap != null;
        }
    }

    /// <summary>The campaign state the world map uses, or null off the map.</summary>
    public static CampaignState? State => MapPlayerTracker.Instance?.campaignState;

    /// <summary>The run, act and path the loaded world map shows, or null when no map is loaded.</summary>
    public static MapPlace? Place()
    {
        if (!MapLoaded)
        {
            return null;
        }

        Map map = MapPlayerTracker.Instance!.mapManager.CurrentMap;
        string runId = State?.runData?.runID ?? string.Empty;
        return runId.Length == 0 ? null : new MapPlace(runId, map.act, Path(map));
    }

    /// <summary>The act's path as the map holds it.</summary>
    public static List<MapPoint> Path(Map map)
    {
        var path = new List<MapPoint>();
        Il2CppSystem.Collections.Generic.List<Point>? points = map?.path;
        for (int i = 0; points != null && i < points.Count; i++)
        {
            Point? point = points[i];
            if (point != null)
            {
                path.Add(new MapPoint(point.x, point.y));
            }
        }

        return path;
    }

    /// <summary>The loaded map's node at <paramref name="point"/>, or null.</summary>
    public static MapNode? NodeAt(MapPoint point)
    {
        MapObject? map = MapObject.Current;
        Il2CppSystem.Collections.Generic.List<MapNode>? nodes = map?.MapNodes;
        for (int i = 0; nodes != null && i < nodes.Count; i++)
        {
            MapNode? node = nodes[i];
            Point? at = node?.Node?.point;
            if (at != null && at.x == point.X && at.y == point.Y)
            {
                return node;
            }
        }

        return null;
    }

    /// <summary>The map node whose navigable is <paramref name="navigable"/>, as a point, or null (not a map node).</summary>
    public static MapPoint? PointOf(AbstractNavigable? navigable)
    {
        if (navigable == null)
        {
            return null;
        }

        Il2CppSystem.Collections.Generic.List<MapNode>? nodes = MapObject.Current?.MapNodes;
        for (int i = 0; nodes != null && i < nodes.Count; i++)
        {
            MapNode? node = nodes[i];
            if (node != null && node.Navigable != null && node.Navigable.Pointer == navigable.Pointer)
            {
                Point? at = node.Node?.point;
                return at == null ? null : new MapPoint(at.x, at.y);
            }
        }

        return null;
    }

    /// <summary>The game's single UI cursor (the UI module's navigation controller), or null before the UI is up.</summary>
    public static NavigationController? Navigation => BishopModule.Get<UIModule>()?.NavigationController;

    /// <summary>The camera's scroll target on the map, or null off the map.</summary>
    public static float? CameraTarget => ScrollNonUI.Current?.xTarget;

    /// <summary>
    /// True when the world map on screen had the game's preview zoom into a match played over it (<see cref="MarkZoomed"/>):
    /// the camera is zoomed into the node and the screen faded, which only a new load of the map undoes.
    /// </summary>
    public static bool Zoomed => _zoomedScene != 0 && _zoomedScene == SceneHandle;

    /// <summary>The preview's zoom into a match is playing over this map (<c>MatchPreviewMenu.AnimateMatchZoom</c>).</summary>
    public static void MarkZoomed() => _zoomedScene = SceneHandle;

    /// <summary>
    /// Moves the map's current node along <paramref name="hostPath"/> in place, the way the game's own map does after a
    /// node: the new nodes go on the map's path, then the node and link states and the outgoing navigation are refreshed
    /// (<c>MapObject.RefreshNodeStates(false)</c>, <c>RefreshAllLinksStates</c>, <c>SetupOutgoingNavigation</c>). False,
    /// with why, when the map isn't a prefix of the host's. The map's own refreshes after a node follow
    /// (<see cref="RefreshAfterNode"/>).
    /// </summary>
    public static bool Advance(IReadOnlyList<MapPoint> hostPath, out string problem)
    {
        if (!MapLoaded)
        {
            problem = "the world map isn't loaded";
            return false;
        }

        Map map = MapPlayerTracker.Instance!.mapManager.CurrentMap;
        List<MapPoint> here = Path(map);
        if (!MapPath.StartsWith(hostPath, here))
        {
            problem = "the map's path isn't the start of the host's";
            return false;
        }

        for (int i = here.Count; i < hostPath.Count; i++)
        {
            if (NodeAt(hostPath[i]) == null)
            {
                problem = $"this map has no node {hostPath[i]}";
                return false;
            }
        }

        for (int i = here.Count; i < hostPath.Count; i++)
        {
            map.path.Add(new Point(hostPath[i].X, hostPath[i].Y));
        }

        MapObject mapObject = MapObject.Current!;
        mapObject.RefreshNodeStates(false);
        mapObject.RefreshAllLinksStates();
        MapNode? current = mapObject.GetCurrentMapNode();
        if (current != null)
        {
            mapObject.SetupOutgoingNavigation(current);
        }

        problem = string.Empty;
        return true;
    }

    /// <summary>
    /// Marks this map's node rewards as older than the host's run, with <paramref name="why"/>: the host's General Manager
    /// rerolls the rewards of the map's Fan, Chaos and training nodes in place (<c>GeneralManagerEvent.RerollSkaterNodeRewards</c>,
    /// a General Manager squad's run only), and an advance in place keeps the nodes this map was built with, so the next
    /// trip that would advance loads the map instead (the game builds it from the host's newest run). Cleared by
    /// <see cref="TakeStaleRewards"/> and <see cref="ClearStaleRewards"/>.
    /// </summary>
    public static void MarkRewardsStale(string why) => _staleRewards = why;

    /// <summary>True, with why, when this map's node rewards were marked stale; the mark is taken (a trip loads the map for it).</summary>
    public static bool TakeStaleRewards(out string why)
    {
        why = _staleRewards;
        _staleRewards = string.Empty;
        return why.Length > 0;
    }

    /// <summary>Forgets a stale-rewards mark (following started or stopped).</summary>
    public static void ClearStaleRewards() => _staleRewards = string.Empty;

    /// <summary>
    /// What the host's map refreshes when a node's menu closes (<c>RogueEvents.WorldMapSubMenuClosed</c>), which a friend's
    /// game never opens: the team's marker moves to the map's current node (<c>WorldMapZamboniBase.RefreshCurrentNodeAfterOneFrame</c>,
    /// as <c>WorldMapZamboniV2/V3.WorldMapSubMenuClosed</c> call it) and the relic bar shows the run's relics
    /// (<c>WorldMapMenusHandler.RefreshRelics</c>, from <c>PostSubMenuClosed</c>; the run save it may make is skipped on a
    /// friend). Returns what couldn't be refreshed ("" when both were).
    /// </summary>
    public static string RefreshAfterNode()
    {
        var failed = new List<string>();
        try
        {
            WorldMapZamboniBase? zamboni = UnityEngine.Object.FindObjectOfType<WorldMapZamboniBase>();
            if (zamboni != null)
            {
                zamboni.RefreshCurrentNodeAfterOneFrame();
            }
            else
            {
                failed.Add("the team's marker (no zamboni on this map)");
            }
        }
        catch (System.Exception ex)
        {
            failed.Add($"the team's marker ({ModLog.SafeErrorText(ex)})");
        }

        try
        {
            WorldMapMenusHandler? handler = WorldMapMenusHandler.s_Instance;
            if (handler != null)
            {
                handler.RefreshRelics();
            }
            else
            {
                failed.Add("the relic bar (no map menus handler)");
            }
        }
        catch (System.Exception ex)
        {
            failed.Add($"the relic bar ({ModLog.SafeErrorText(ex)})");
        }

        return string.Join("; ", failed);
    }

    /// <summary>
    /// The map's music state, as the game's own ways onto the map set it (<c>TitleScreen.GoToWorldMap</c>,
    /// <c>CampaignContinueStep.LoadWorldMap</c>): FMOD's global "location" is "worldmap". A friend's game loads the map
    /// without them, so it stayed at the title's or the match's. (The "act" parameter follows the run by itself:
    /// <c>RunData.set_CurrentAct</c>, which the run's load and the map's start call.) Returns FMOD's answer.
    /// </summary>
    public static Il2CppFMOD.RESULT SetMapAudio() =>
        Il2CppFMODUnity.RuntimeManager.StudioSystem.setParameterByNameWithLabel(LocationParameter, MapLocation, false);

    /// <summary>The game's loading screen in the active scene, or null.</summary>
    public static LoadingScreenManager? Loader => UnityEngine.Object.FindObjectOfType<LoadingScreenManager>();

    /// <summary>True while the game is starting a scene load.</summary>
    public static bool Loading => Loader is { isStartingLoad: true };

    /// <summary>
    /// Loads <paramref name="scene"/> the way the game's campaign does (<c>LoadingScreenManager.LoadScene(name, false)</c>).
    /// False, with why, when there's no loading screen here or a load is already starting.
    /// </summary>
    public static bool Load(string scene, out string problem)
    {
        LoadingScreenManager? loader = Loader;
        if (loader == null)
        {
            problem = "this scene has no loading screen";
            return false;
        }

        if (loader.isStartingLoad)
        {
            problem = "the game is already loading a scene";
            return false;
        }

        loader.LoadScene(scene, false);
        problem = string.Empty;
        return true;
    }
}
#endif
