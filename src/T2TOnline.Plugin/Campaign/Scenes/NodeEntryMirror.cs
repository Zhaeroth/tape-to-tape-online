#if HAS_INTEROP
using System;
using System.Collections.Generic;
using Il2CppSTS.Map;
using Il2CppTape2Tape.Hockey.UI;
using T2TOnline.Core.Campaign;
using T2TOnline.Core.Diagnostics;
using T2TOnline.Plugin.Diagnostics;
using T2TOnline.Plugin.Online;
using T2TOnline.Plugin.Streaming;
using Point = Il2CppSTS.Map.Point;

namespace T2TOnline.Plugin.Campaign.Scenes;

/// <summary>
/// The game's entry into an event node on every screen. A click on an NPC or training node
/// doesn't open the event screen at once: <c>WorldMapMenusHandler.OnTryClickNode</c> calls <c>PushCameraToNode</c>, which
/// zooms the camera out, holds, then zooms it into the node's door while the team's zamboni drives there (its sound, the
/// "plunge"), and opens the node a frame later; the event screen's own <c>Show</c> waits for that arrival
/// (<c>WorldMapMenusHandler.PendingNodeArrival</c>) before its intro. A friend's event screen is opened by the menu mirror
/// with no map node and no push, so it popped up over a still map: none of the host's entry played there.
/// <list type="bullet">
/// <item>Host: the push is noted (a postfix) and the map view carries the node while the game holds the push
/// (<see cref="MapViewDetails.Entering"/>, numbered: <see cref="NodeEntryHost"/>), until its camera starts back.</item>
/// <item>Friend: each entry plays once (<see cref="NodeEntryFollow"/>) with this PC's own <c>PushCameraToNode</c> on the same
/// node, so the zoom, the drive and their sounds are the game's own; the push's last step, opening the node
/// (<c>MapPlayerTracker.SelectNode</c>), is the host's and is held back quietly by <see cref="MapGuards"/>, which leaves the
/// map locked for that frame as the game's own node access does (else the push would come straight back). The event screen
/// the host's menu then opens here waits for this PC's arrival, as the host's does. When the host's entry ends with no
/// event screen up here (a node that can't be entered), this camera comes back too; an event screen's own close brings it
/// back otherwise, and its map peek (<see cref="Peeked"/>, raised as the game's own toggle raises it) while it's aside.</item>
/// </list>
/// Nothing here throws into the game.
/// </summary>
internal static class NodeEntryMirror
{
    /// <summary>How long after this PC's push its own node-opening call is expected.</summary>
    private const double SelectWithinSeconds = 3;

    private const int MaxErrors = 3;

    private static readonly NodeEntryHost Host = new();
    private static readonly NodeEntryFollow Friend = new();
    private static IntPtr _expectedNode;
    private static double _expectedUntil;
    private static MapPlayerTracker? _locked;
    private static int _lockedFrame;
    private static int _played;
    private static int _held;
    private static int _returned;
    private static int _peeks;
    private static int _errors;

    /// <summary>Entries played here this session, and their node openings held back (for the end-of-session line).</summary>
    public static string Summary => $"{_played} node entr{(_played == 1 ? "y" : "ies")} played as the host's ({_held} node opening(s) held back, {_returned} camera(s) brought back), " +
        $"{_peeks} map peek(s) followed";

    /// <summary>Patches the push (host: noted). Adds a line to <paramref name="failed"/> if it couldn't go in.</summary>
    public static void Install(HarmonyLib.Harmony harmony, List<string> failed) =>
        ScenePatches.Postfix(harmony, failed, typeof(WorldMapMenusHandler), nameof(WorldMapMenusHandler.PushCameraToNode), typeof(NodeEntryMirror), nameof(PushCameraToNodePostfix));

    // ---- Host ----

    /// <summary>Host: hosting started or stopped; nothing is under way (the numbers carry on).</summary>
    public static void ClearHost() => Host.Clear();

    /// <summary>
    /// Host, each frame on the map: the entry under way while the game holds its push and its camera hasn't started back, or
    /// null. An entry whose camera is coming back (the event screen closing or put aside for the map, or a node that can't
    /// be entered) ends here, with a line.
    /// </summary>
    public static NodeEntry? HostEntering()
    {
        if (Host.Current is not { } current)
        {
            return null;
        }

        bool held;
        try
        {
            WorldMapMenusHandler? handler = WorldMapMenusHandler.s_Instance;
            held = handler != null && handler.m_IsNpcPushHeld && !handler.m_IsCameraReturning;
        }
        catch (Exception)
        {
            held = false;
        }

        if (held)
        {
            return current;
        }

        Host.End();
        ModLog.Info($"Scenes: the host's camera is coming back from node {current.Node} (entry {current.Number}); friends' cameras follow it (with their event screen where one is up).");
        return null;
    }

    /// <summary>Host: the game started its push into <paramref name="node"/> (a click on an event node).</summary>
    private static void PushCameraToNodePostfix(WorldMapMenusHandler __instance, MapNode node)
    {
        try
        {
            // Only a shared campaign's map goes to friends (a solo run's while hosting doesn't: they see the title).
            if (CampaignRelay.Current?.Hosting != true || RunSync.Instance?.HostOnCoopSlot != true || __instance == null || node == null)
            {
                return;
            }

            if (!__instance.m_IsNpcPushHeld)
            {
                // The game found no camera data for a push and went straight to the node: nothing for friends to play.
                return;
            }

            Point? at = node.Node?.point;
            if (at == null || at.x < 0 || at.y < 0 || at.x > MapPoint.MaxCoordinate || at.y > MapPoint.MaxCoordinate)
            {
                ModLog.Warn($"Scenes: the host's game is entering a node friends can't be told about ({(at == null ? "no point" : $"{at.x},{at.y}")}); their maps stay still.");
                return;
            }

            NodeEntry entry = Host.Begin(new MapPoint(at.x, at.y));
            ModLog.Info($"Scenes: the host's game is entering node {entry.Node} ({Describe(node)}): its camera push and the zamboni's drive go to friends (entry {entry.Number}).");
        }
        catch (Exception ex)
        {
            Failed("noting the host's node entry", ex);
        }
    }

    // ---- Friend ----

    /// <summary>Friend: following started or stopped; nothing played or held (a lock this mirror set is taken off).</summary>
    public static void ResetFriend()
    {
        Unlock();
        Friend.Reset();
        _expectedNode = IntPtr.Zero;
    }

    /// <summary>
    /// Friend: the host's map view <paramref name="host"/> is applied here (this PC shows the host's map). Plays a new entry,
    /// leaves out one this map can't play, or brings the camera back when the host's entry is over. Never throws.
    /// </summary>
    public static void Follow(MapViewDetails host)
    {
        try
        {
            NodeEntryStep step = Friend.Step(host.Entering, WorldMap.MapLoaded, ActIntro.Playing);
            switch (step.Action)
            {
                case NodeEntryAction.Play:
                    Play(step.Entry);
                    break;
                case NodeEntryAction.Skip:
                    ModLog.Info($"Scenes: {LogNames.HostTag()}'s game entered node {step.Entry.Node} (entry {step.Entry.Number}); not played here ({step.Why}).");
                    break;
                case NodeEntryAction.Return:
                    Return(step.Entry);
                    break;
            }
        }
        catch (Exception ex)
        {
            Friend.Ended();
            Failed("following the host's node entry", ex);
        }
    }

    /// <summary>
    /// Friend, each frame while the map follower is up: the one-frame map lock comes off, and an entry whose camera this
    /// PC's own event screen already brought back is no longer held. Never throws.
    /// </summary>
    public static void Tick()
    {
        try
        {
            // A managed check first (Unity's != is a call into the game, and this runs every frame on the map).
            if (_locked is not null && UnityEngine.Time.frameCount > _lockedFrame)
            {
                Unlock();
            }

            if (Friend.Playing != null && WorldMapMenusHandler.s_Instance is { } handler && !handler.m_IsNpcPushHeld && SubMenusOpen(handler) == 0)
            {
                Friend.Ended();
            }
        }
        catch (Exception ex)
        {
            _locked = null;
            Failed("checking this map's node entry", ex);
        }
    }

    /// <summary>Friend: the map follower leaves the map (another screen): an entry still held here with no event screen up comes back. Never throws.</summary>
    public static void Leave()
    {
        try
        {
            if (Friend.Playing is { } entry)
            {
                Friend.Ended();
                Return(entry);
            }

            Unlock();
        }
        catch (Exception ex)
        {
            Failed("ending this map's node entry", ex);
        }
    }

    /// <summary>
    /// Friend: the menu mirror put this PC's event screen aside for the map (<paramref name="peeking"/>) or showed it again,
    /// as the host's did. The game's own toggle (<c>EventChoiceMenu.OnToggleWorldMapInput</c>) then raises the map's peek event
    /// (<c>RogueEvents.WorldMapSubMenuPeek</c>), whose listeners bring a pushed camera back and let the map scroll while the
    /// screen is aside (<c>WorldMapMenusHandler.OnSubMenuPeek</c>, the zamboni's): raised here the same way. Never throws.
    /// </summary>
    public static void Peeked(bool peeking)
    {
        try
        {
            Il2CppEvents.RogueEvents.WorldMapSubMenuPeek?.Invoke(EventChoiceMenu.TYPE, peeking);
            _peeks += peeking ? 1 : 0;
            ModLog.Info(peeking
                ? $"Scenes: {LogNames.HostTag()} put the event screen aside to look at the map; this map does the same (its camera back from the node, the map free to scroll, the host's cursor and node details followed)."
                : $"Scenes: {LogNames.HostTag()} went back to the event screen from the map; so does this PC.");
        }
        catch (Exception ex)
        {
            Failed("raising the map's peek event", ex);
        }
    }

    /// <summary>Friend: true while this PC's event screen is put aside for the map (the game's own peek state), so the host's map cursor is followed then.</summary>
    public static bool Peeking
    {
        get
        {
            try
            {
                return WorldMapMenusHandler.s_Instance is { } handler && handler.m_IsPeeking;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    /// <summary>
    /// Friend (<see cref="MapGuards"/>, from <c>MapPlayerTracker.SelectNode</c>): true when <paramref name="node"/> is this
    /// PC's own push opening its node, which is the host's to do: held back, quietly. The map stays locked for this frame, as
    /// the game's own node access leaves it, so the push isn't brought straight back.
    /// </summary>
    public static bool TakeExpectedSelect(MapPlayerTracker tracker, MapNode node)
    {
        try
        {
            if (_expectedNode == IntPtr.Zero || node == null || node.Pointer != _expectedNode || FrameClock.Seconds > _expectedUntil)
            {
                return false;
            }

            _expectedNode = IntPtr.Zero;
            _held++;
            if (tracker != null && WorldMapMenusHandler.s_Instance is { } handler && handler.m_IsNpcPushHeld && !tracker.isLocked)
            {
                tracker.isLocked = true;
                _locked = tracker;
                _lockedFrame = UnityEngine.Time.frameCount;
            }

            ModLog.Trace("Map guard: the mirrored node entry's own node opening (MapPlayerTracker.SelectNode) is the host's: held back, as expected; the map stays locked for this frame as the game's own entry leaves it.");
            return true;
        }
        catch (Exception ex)
        {
            Failed("holding back the mirrored entry's node opening", ex);
            return false;
        }
    }

    private static void Play(NodeEntry entry)
    {
        WorldMapMenusHandler? handler = WorldMapMenusHandler.s_Instance;
        MapNode? node = WorldMap.NodeAt(entry.Node);
        string problem = handler == null ? "this map has no map menus handler"
            : node == null ? $"this map has no node {entry.Node}"
            : node.TryCast<MatchMapNode>() != null ? "it's a match node here (its preview is followed instead)"
            : string.Empty;
        if (problem.Length > 0)
        {
            Friend.Ended();
            ModLog.Warn($"Scenes: {LogNames.HostTag()}'s game entered node {entry.Node} (entry {entry.Number}); not played here: {problem}.");
            return;
        }

        _expectedNode = node!.Pointer;
        _expectedUntil = FrameClock.Seconds + SelectWithinSeconds;
        handler!.PushCameraToNode(node);
        if (!handler.m_IsNpcPushHeld)
        {
            // The game found no camera data here and went straight to the node (held back): nothing plays.
            Friend.Ended();
            _expectedNode = IntPtr.Zero;
            ModLog.Warn($"Scenes: {LogNames.HostTag()}'s game entered node {entry.Node} (entry {entry.Number}); this map's camera couldn't push into it (the game found no camera data).");
            return;
        }

        _played++;
        ModLog.Info($"Scenes: {LogNames.HostTag()}'s game is entering node {entry.Node} ({Describe(node)}): the same camera push and zamboni drive play here " +
            $"(entry {entry.Number}); the event screen opens here from this PC's own arrival, as on {LogNames.HostTag()}'s screen.");
    }

    /// <summary>The host's entry is over: this PC's camera comes back, unless an event screen is up here (its own close does that).</summary>
    private static void Return(NodeEntry entry)
    {
        WorldMapMenusHandler? handler = WorldMapMenusHandler.s_Instance;
        if (handler == null || !handler.m_IsNpcPushHeld || handler.m_IsCameraReturning)
        {
            // Nothing held here, or it's already on its way back (the game's own return, from an event screen's close or peek).
            return;
        }

        int open = SubMenusOpen(handler);
        if (open > 0)
        {
            ModLog.Trace($"Scenes: {LogNames.HostTag()}'s camera is coming back from node {entry.Node} (entry {entry.Number}); this PC's comes back with its event screen's close ({open} map screen(s) up here).");
            return;
        }

        handler.ReturnCameraFromPush(false);
        _returned++;
        ModLog.Info($"Scenes: {LogNames.HostTag()}'s camera is coming back from node {entry.Node} with no event screen (entry {entry.Number}); this camera comes back too.");
    }

    /// <summary>
    /// The map screens up on this map (the game's own list, <c>m_WorldMapSubMenus</c>: an event screen, the lineup, the map's
    /// menu), leaving out a menu already hidden (one closed without the map hearing of it).
    /// </summary>
    private static int SubMenusOpen(WorldMapMenusHandler handler)
    {
        Il2CppSystem.Collections.Generic.List<Il2CppBishopGames.UI.INavigationSource>? menus = handler.m_WorldMapSubMenus;
        int open = 0;
        for (int i = 0; menus != null && i < menus.Count; i++)
        {
            Il2CppBishopGames.UI.AbstractMenu? menu = menus[i]?.TryCast<Il2CppBishopGames.UI.AbstractMenu>();
            if (menus[i] != null && (menu == null || !menu.IsHidden))
            {
                open++;
            }
        }

        return open;
    }

    private static void Unlock()
    {
        MapPlayerTracker? tracker = _locked;
        _locked = null;
        try
        {
            if (tracker != null && !tracker.WasCollected && tracker.isLocked)
            {
                tracker.isLocked = false;
            }
        }
        catch (Exception)
        {
            // The map went with its scene: nothing left to unlock.
        }
    }

    private static string Describe(MapNode node)
    {
        try
        {
            return node.Blueprint != null ? $"{node.Blueprint.nodeType} node" : SafeText.Clean(node.name, 48);
        }
        catch (Exception)
        {
            return "a node";
        }
    }

    private static void Failed(string what, Exception ex)
    {
        if (_errors++ < MaxErrors)
        {
            ModLog.Error($"Scenes: {what} failed", ex);
        }
    }
}
#endif
