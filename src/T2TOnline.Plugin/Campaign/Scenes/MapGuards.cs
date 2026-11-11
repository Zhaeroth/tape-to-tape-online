#if HAS_INTEROP
using System;
using System.Collections.Generic;
using Il2Cpp;
using Il2CppSTS.Map;
using Il2CppTape2Tape.Hockey.UI;
using T2TOnline.Plugin.Diagnostics;
using T2TOnline.Plugin.Online;

namespace T2TOnline.Plugin.Campaign.Scenes;

/// <summary>
/// The world map's commands and rolls, refused on a friend's PC while it follows a host (D40, D44): its game shows the
/// host's map and never acts on it. Three kinds:
/// <list type="bullet">
/// <item>map generation (<c>MapManager.GenerateNewMap</c>): reaching it means the friend loaded a map from a checkpoint
/// saved before the host's map existed; refused and logged as an error. Every way to a new map goes through it (the debug
/// <c>JumpToAct</c>, which nothing calls, included);</item>
/// <item>map commands (node clicks and selection, entering a node, starting or cancelling a match preview): refused
/// always (the mirror closes a preview through <see cref="Allow"/>). A preview starts only through its shortcut's handler
/// <c>OnStartInput</c> (the private <c>StartMatch</c> is inlined there and nothing calls it). The preview's debug
/// <c>SkipMatch</c> isn't patched: it returns a struct, and nothing on a friend's PC calls it;</item>
/// <item>map input handlers that open menus (the map menu, the lineup, the relic bar): refused unless the mirror runs
/// them inside <see cref="FriendUi.Apply"/>.</item>
/// </list>
/// Every unexpected refusal is logged as "Map guard: refused" (by name, at most <see cref="NamedPerMinute"/> a minute, the
/// rest counted; a refused map roll is an error, at most <see cref="ErrorsPerMinute"/> a minute). The refusals the mirror causes itself, the end of a mirrored preview zoom entering its node
/// (<see cref="ExpectZoomEnd"/>) and a mirrored event node entry opening its node (<see cref="NodeEntryMirror"/>), are held
/// back quietly: a trace line in the session file and a count. The host's game is never touched.
/// </summary>
internal static class MapGuards
{
    private const int NamedPerMinute = 20;
    private const int ErrorsPerMinute = 5;

    private static readonly LineBudget Named = new("Map guard", NamedPerMinute);
    private static readonly LineBudget Errors = new("Map guard errors", ErrorsPerMinute);
    private static int _allowed;
    private static int _refusals;
    private static int _expected;
    private static int _zoomScene;

    /// <summary>Unexpected refusals since the game started.</summary>
    public static int Refusals => _refusals;

    /// <summary>Mirrored preview zooms whose last step (entering the node) was held back, since the game started.</summary>
    public static int ExpectedRefusals => _expected;

    /// <summary>
    /// The mirror played the game's preview zoom into a match node in this scene (<c>MatchPreviewMenu.AnimateMatchZoom</c>):
    /// its last step, <c>SendPlayerToNode</c>, is the host's and is refused, but quietly (it's expected).
    /// </summary>
    public static void ExpectZoomEnd() => _zoomScene = WorldMap.SceneHandle;

    /// <summary>True while this PC follows a host (the guards refuse only then).</summary>
    // Also while a friend whose session ended leaves the host's map for its own title screen (SessionExit).
    private static bool Following => CampaignRelay.Current?.Following == true || SessionExit.Leaving;

    /// <summary>Runs <paramref name="action"/> with the map commands allowed: for the mirror's own preview close.</summary>
    public static void Allow(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        _allowed++;
        try
        {
            action();
        }
        finally
        {
            _allowed--;
        }
    }

    /// <summary>Patches every guard. Adds a line to <paramref name="failed"/> per patch that couldn't go in.</summary>
    public static void Install(HarmonyLib.Harmony harmony, List<string> failed)
    {
        ScenePatches.Prefix(harmony, failed, typeof(MapManager), nameof(MapManager.GenerateNewMap), typeof(MapGuards), nameof(GenerateNewMapPrefix));
        ScenePatches.Prefix(harmony, failed, typeof(WorldMapMenusHandler), nameof(WorldMapMenusHandler.OnTryClickNode), typeof(MapGuards), nameof(TryClickNodePrefix));
        ScenePatches.Prefix(harmony, failed, typeof(MapPlayerTracker), nameof(MapPlayerTracker.SelectNode), typeof(MapGuards), nameof(SelectNodePrefix));
        ScenePatches.Prefix(harmony, failed, typeof(MapPlayerTracker), nameof(MapPlayerTracker.AccessNode), typeof(MapGuards), nameof(AccessNodePrefix));
        ScenePatches.Prefix(harmony, failed, typeof(MapPlayerTracker), nameof(MapPlayerTracker.SendPlayerToNode), typeof(MapGuards), nameof(SendPlayerToNodePrefix));
        ScenePatches.Prefix(harmony, failed, typeof(MatchPreviewMenu), nameof(MatchPreviewMenu.OnStartInput), typeof(MapGuards), nameof(StartInputPrefix));
        ScenePatches.Prefix(harmony, failed, typeof(MatchPreviewMenu), nameof(MatchPreviewMenu.OnCancelInput), typeof(MapGuards), nameof(CancelInputPrefix));
        ScenePatches.Prefix(harmony, failed, typeof(MatchPreviewMenu), nameof(MatchPreviewMenu.BackToMap), typeof(MapGuards), nameof(BackToMapPrefix));
        ScenePatches.Prefix(harmony, failed, typeof(WorldMapMenusHandler), nameof(WorldMapMenusHandler.OnOpenWorldMapMenu), typeof(MapGuards), nameof(OpenWorldMapMenuPrefix));
        ScenePatches.Prefix(harmony, failed, typeof(WorldMapMenusHandler), nameof(WorldMapMenusHandler.OnOpenLineupMenu), typeof(MapGuards), nameof(OpenLineupMenuPrefix));
        ScenePatches.Prefix(harmony, failed, typeof(WorldMapMenusHandler), nameof(WorldMapMenusHandler.OnToggleRelicsNavigation), typeof(MapGuards), nameof(ToggleRelicsPrefix));
    }

    /// <summary>True when a map command must be refused (following, and not the mirror's own call).</summary>
    private static bool RefuseCommand(string what)
    {
        if (!Following || _allowed > 0)
        {
            return false;
        }

        Refused(what, error: false);
        return true;
    }

    /// <summary>True when a map input handler must be refused (following, and not inside <see cref="FriendUi.Apply"/>).</summary>
    private static bool RefuseInput(string what)
    {
        if (!Following || FriendUi.Lifted)
        {
            return false;
        }

        Refused(what, error: false);
        return true;
    }

    private static bool GenerateNewMapPrefix(int act)
    {
        if (!Following)
        {
            return true;
        }

        Refused($"MapManager.GenerateNewMap (act {act}): this game was about to roll a map of its own, so the run it loaded is older than the host's map. " +
            "The map stays wrong until the next checkpoint from the host's map (the follower retries)", error: true);
        return false;
    }

    private static bool TryClickNodePrefix() => !RefuseCommand("a map node click (WorldMapMenusHandler.OnTryClickNode)");

    private static bool SelectNodePrefix(MapPlayerTracker __instance, MapNode mapNode)
    {
        if (!Following || _allowed > 0)
        {
            return true;
        }

        // The mirrored node entry's own last step (opening the node) is the host's: held back quietly, not a refusal.
        return !NodeEntryMirror.TakeExpectedSelect(__instance, mapNode) && !RefuseCommand("MapPlayerTracker.SelectNode");
    }

    private static bool AccessNodePrefix() => !RefuseCommand("MapPlayerTracker.AccessNode");

    private static bool SendPlayerToNodePrefix()
    {
        if (!Following || _allowed > 0)
        {
            return true;
        }

        try
        {
            if (_zoomScene != 0 && _zoomScene == WorldMap.SceneHandle)
            {
                // The mirrored preview zoom's own end: expected, so not a "refused" line (the match relay loads the match).
                _zoomScene = 0;
                _expected++;
                ModLog.Trace("Map guard: the mirrored preview zoom ended; entering the node is the host's (MapPlayerTracker.SendPlayerToNode held back, as expected).");
                return false;
            }
        }
        catch (Exception)
        {
            // Not knowing the scene, it's refused below like any other entry.
        }

        Refused("MapPlayerTracker.SendPlayerToNode (entering a node: the host does that)", error: false);
        return false;
    }

    private static bool StartInputPrefix(MatchPreviewMenu __instance) => HostStarts(__instance) || !RefuseCommand("MatchPreviewMenu.OnStartInput");

    /// <summary>
    /// A host's start from its preview (its shortcut's handler goes straight into the zoom, <c>AnimateMatchZoom</c>): friends
    /// are told, so their zoom plays alongside. False on a friend's game (its start is refused below). Never throws.
    /// </summary>
    private static bool HostStarts(MatchPreviewMenu menu)
    {
        if (Following)
        {
            return false;
        }

        PreviewMirror.HostStarted(menu);
        return true;
    }

    private static bool CancelInputPrefix() => !RefuseCommand("MatchPreviewMenu.OnCancelInput");

    private static bool BackToMapPrefix() => !RefuseCommand("MatchPreviewMenu.BackToMap");

    private static bool OpenWorldMapMenuPrefix() => !RefuseInput("the map menu (WorldMapMenusHandler.OnOpenWorldMapMenu)");

    private static bool OpenLineupMenuPrefix() => !RefuseInput("the lineup (WorldMapMenusHandler.OnOpenLineupMenu)");

    // Only the mirror's own toggle, following the host's relic bar (RelicBarMirror), gets through on a friend's map.
    private static bool ToggleRelicsPrefix() => RelicBarMirror.Toggling || !RefuseCommand("the relic bar (WorldMapMenusHandler.OnToggleRelicsNavigation)");

    private static void Refused(string what, bool error)
    {
        try
        {
            _refusals++;
            if (error)
            {
                if (Errors.TrySpend())
                {
                    ModLog.Error($"Map guard: refused {what}.");
                }
            }
            else if (Named.TrySpend())
            {
                ModLog.Info($"Map guard: refused {what} on this friend's game.");
            }
        }
        catch (Exception)
        {
            // Refused all the same; a log line isn't worth an exception in the game's call.
        }
    }
}
#endif
