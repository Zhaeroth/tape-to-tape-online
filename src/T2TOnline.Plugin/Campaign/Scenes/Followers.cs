#if HAS_INTEROP
using System;
using System.Collections.Generic;
using System.Linq;
using Il2Cpp;
using Il2CppBishopGames.UI;
using Il2CppSTS.Map;
using T2TOnline.Core.Campaign;
using T2TOnline.Plugin.Diagnostics;
using T2TOnline.Plugin.Match;
using T2TOnline.Plugin.Online;
using T2TOnline.Plugin.Streaming;

namespace T2TOnline.Plugin.Campaign.Scenes;

/// <summary>
/// A friend's way to the host's title screen (<see cref="Screens.Title"/>: the host is outside the campaign). A game in
/// the campaign (holding the host's run, or on a world map) puts its own run back (<see cref="RunSync.RestoreOwnRun"/>)
/// and loads its own title screen, as the game loads it (on the title screen already, run sync refreshes its campaign
/// buttons instead); anywhere else it's already there. A host's match still loading or playing here (the match relay's)
/// is waited for first, however long it takes (never a failure); a finished one held on its last frame is left at once.
/// The title screen is loaded before the own run is put back, and shared progress that needs the host's run (the run's
/// end: squad record, unlock checks, history) gets up to <see cref="ProgressSeconds"/> to apply in between. Before the load,
/// this player's own new-unlocks screen at a run's end the host's game showed none for is waited for
/// (<see cref="Menus.OwnUnlocks"/>, with its own limits), unless the host goes on to its next game. Once there, this
/// title follows the host's button group and highlighted button (<see cref="TitleCursor"/>); a view that only moves those
/// is followed in place.
/// </summary>
internal sealed class TitleFollower : IScreenFollower
{
    private const double WaitSeconds = 30;
    private const double LoadSeconds = 60;
    private const double ProgressSeconds = 10;

    private readonly MatchRelay _matches;
    private Action<FollowResult>? _done;
    private uint _epoch;
    private double _waitStarted = double.NaN;
    private double _loadStarted;
    private int _sceneBefore;
    private bool _loading;
    private bool _restoring;
    private bool _ownUnlocks;
    private double _restoreSince;
    private HostView? _view;
    private HostView? _shown;

    /// <summary>A title follower that waits for <paramref name="matches"/> to finish a host's match first.</summary>
    public TitleFollower(MatchRelay matches) => _matches = matches ?? throw new ArgumentNullException(nameof(matches));

    /// <inheritdoc />
    public string Screen => Screens.Title;

    /// <inheritdoc />
    public void Go(HostView view, RunCheckpoint? run, Action<FollowResult> done)
    {
        TitleCursor.Want(TitleViewDetails.Read(view));
        if (_shown != null && view.SameExcept(_shown, TitleViewDetails.Keys) && !_matches.FriendBusy && WorldMap.SceneName == WorldMap.TitleScene)
        {
            // The same title with another group or cursor (the host moved it): followed in place.
            _shown = view;
            done(new FollowResult(true, 0, string.Empty, Applied: false));
            return;
        }

        _shown = null;
        _view = view;
        _done = done;
        _epoch = view.Epoch;
        _waitStarted = double.NaN;
        _loading = false;
        _restoring = false;
        _ownUnlocks = false;
        Update();
    }

    /// <inheritdoc />
    public void Update()
    {
        if (_done == null)
        {
            if (_shown != null)
            {
                TitleCursor.Tick();
            }

            return;
        }

        try
        {
            Step();
        }
        catch (Exception ex)
        {
            ModLog.Error("Scenes: following the host to the title screen failed", ex);
            Finish(new FollowResult(false, 0, $"an error ({ModLog.SafeErrorText(ex)})"), string.Empty);
        }
    }

    /// <inheritdoc />
    public void Leave()
    {
        _done = null;
        _shown = null;
        TitleCursor.Leave();
    }

    private void Step()
    {
        if (_loading)
        {
            if (WorldMap.SceneName == WorldMap.TitleScene && WorldMap.SceneHandle != _sceneBefore)
            {
                _loading = false;
                StartRestoring();
            }
            else if (FrameClock.Seconds - _loadStarted > LoadSeconds)
            {
                Finish(new FollowResult(false, 0, $"the title screen didn't load within {LoadSeconds:0} s"), string.Empty);
            }

            return;
        }

        if (_restoring)
        {
            // Shared progress of the run's end applies while this game still holds the host's run (Progress needs it);
            // then this game's own run comes back, and the title screen refreshes its campaign buttons with it.
            double waited = FrameClock.Seconds - _restoreSince;
            if (Progress.ProgressModule.RunBoundProgressWaiting && waited < ProgressSeconds)
            {
                return;
            }

            if (Progress.ProgressModule.RunBoundProgressWaiting)
            {
                ModLog.Warn($"Scenes: shared progress of the host's run still waits after {ProgressSeconds:0} s; putting this game's own run back anyway (see the progress lines).");
            }

            RunSync.Instance?.RestoreOwnRun("the host went back to the title screen");
            Finish(new FollowResult(true, 0, string.Empty, Applied: false), "back on this game's own title screen with its own run");
            return;
        }

        if (_matches.FriendBusy || MatchSetup.Loading)
        {
            // A host's match here ends by itself (the host left it); a newer view (the host's next match) takes over.
            if (CampaignRelay.Current?.View is { } latest && latest.Epoch != _epoch)
            {
                Finish(new FollowResult(true, 0, string.Empty, Applied: false), "the host went on to a match");
            }

            _waitStarted = double.NaN; // the scene-load wait starts once the match is gone
            return;
        }

        if (_ownUnlocks)
        {
            // This player's own new-unlocks screen holds the way home (below), as long as its player takes: only it is asked
            // each frame (the scene's checks look through the scene's objects). Nothing loads a scene here meanwhile but a
            // host's match, asked above; the scene is looked at again on the step after it has closed.
            _ownUnlocks = WaitForOwnUnlocks();
            return;
        }

        if (WorldMap.Loading)
        {
            if (double.IsNaN(_waitStarted))
            {
                _waitStarted = FrameClock.Seconds;
            }
            else if (FrameClock.Seconds - _waitStarted > WaitSeconds)
            {
                Finish(new FollowResult(false, 0, FollowReasons.SceneLoadUnfinished), string.Empty);
            }

            return;
        }

        RunSync? runs = RunSync.Instance;
        string scene = WorldMap.SceneName;
        if (!MapFollow.LeaveForTitle(runs?.HoldingHostRun == true, scene))
        {
            Finish(new FollowResult(true, 0, string.Empty, Applied: false), $"already outside the campaign ({scene})");
            return;
        }

        if (scene == WorldMap.TitleScene)
        {
            // Already on the title screen: this game's own run comes back (run sync refreshes its campaign buttons).
            StartRestoring();
            return;
        }

        // This player's own new-unlocks screen, at a run's end the host's game showed none for, comes first (Menus.OwnUnlocks):
        // the way home waits for its close, unless the host goes on to its next game meanwhile.
        if (WaitForOwnUnlocks())
        {
            _ownUnlocks = true;
            return;
        }

        if (_done == null)
        {
            return; // the host went on to its next game
        }

        // The game's loading transition first, as the host's game shows it before its loads (tried again next step).
        if (!Menus.FriendTransitions.LoadReady("the title screen", out _))
        {
            return;
        }

        _sceneBefore = WorldMap.SceneHandle;
        if (!WorldMap.Load(WorldMap.TitleScene, out string problem))
        {
            // The transition shown for this load goes with it (nothing else would take it down but the guard's limits).
            Menus.FriendTransitions.LoadGivenUp("the title screen", $"the title screen couldn't load: {problem}");
            Finish(new FollowResult(false, 0, $"couldn't load the title screen: {problem}"), string.Empty);
            return;
        }

        ModLog.Info($"Scenes: the host is at the title screen; this game is loading its title screen (from {scene}), then puts its own run back.");
        _loading = true;
        _loadStarted = FrameClock.Seconds;
    }

    /// <summary>
    /// True while the way home waits for this player's own new-unlocks screen (<see cref="Menus.OwnUnlocks.AtWayHome"/>).
    /// False once it has closed (or there's none), and when the host went on to its next game meanwhile: that ends this
    /// follow (<c>_done</c> is null then).
    /// </summary>
    private bool WaitForOwnUnlocks()
    {
        if (!Menus.OwnUnlocks.AtWayHome())
        {
            return false;
        }

        if (CampaignRelay.Current?.View is { } latest && latest.Epoch != _epoch && latest.Screen != Screens.Title)
        {
            Menus.OwnUnlocks.Abort("the host went on to its next game");
            Finish(new FollowResult(true, 0, string.Empty, Applied: false), "the host went on to its next game");
            return false;
        }

        return true;
    }

    private void StartRestoring()
    {
        _restoring = true;
        _restoreSince = FrameClock.Seconds;
        Step();
    }

    private void Finish(FollowResult result, string how)
    {
        Action<FollowResult>? done = _done;
        _done = null;
        _shown = result.Ok ? _view : null;
        if (how.Length > 0)
        {
            ModLog.Info($"Scenes: following to the title: {how}.");
        }

        done?.Invoke(result);
    }
}

/// <summary>
/// A friend's way to the host's world map (<see cref="Screens.Map"/>): a <see cref="MapTrip"/> there, then the host's
/// cursor (the node it's on, the camera's scroll target). While the map is showing, the cursor follows each new view, and
/// is held on the host's node if the game moves it (its own first-node selection after a load, say), unless the friend's
/// cursor is in a mirrored menu. A run the host saved without moving (a relic or lineup change) waits for the next move:
/// each load rebuilds the team, and menus built on the old one would go stale.
/// </summary>
internal sealed class MapFollower : IScreenFollower
{
    private const double HoldSeconds = 0.1;

    private static int _cursorErrors;
    private readonly MatchRelay _matches;
    private MapTrip? _trip;
    private MapViewDetails? _shown;
    private float? _cameraApplied;
    private double _nextHold;
    private uint _deferred;
    private MapPoint? _targetPoint;
    private AbstractNavigable? _target;
    private int _targetScene;

    // Whether the navigable the friend's cursor is on is a map node, looked up once per navigable and scene (the map's
    // nodes are made with the scene).
    private IntPtr _cursorOn;
    private int _cursorScene;
    private bool _cursorOnNode;

    /// <summary>A map follower that waits for <paramref name="matches"/> to finish a host's match first.</summary>
    public MapFollower(MatchRelay matches) => _matches = matches ?? throw new ArgumentNullException(nameof(matches));

    /// <summary>Cursor moves applied (for the end-of-session line).</summary>
    public int CursorMoves { get; private set; }

    /// <inheritdoc />
    public string Screen => Screens.Map;

    /// <inheritdoc />
    public void Go(HostView view, RunCheckpoint? run, Action<FollowResult> done)
    {
        if (!MapViewDetails.TryRead(view, out MapViewDetails? host, out string problem))
        {
            done(new FollowResult(false, 0, $"the host's map view is malformed ({problem})"));
            return;
        }

        if (run == null)
        {
            done(new FollowResult(false, 0, FollowReasons.MapWithoutRun));
            return;
        }

        // The same place with another cursor (the host moved it): no trip, just the cursor.
        if (_shown != null && _shown.SamePlace(host!) && WorldMap.MapLoaded)
        {
            _shown = host;
            ApplyCursor(host!);
            done(new FollowResult(true, 0, string.Empty, Applied: false));
            return;
        }

        _shown = null;
        _trip?.Cancel();
        _trip = new MapTrip(host!, view.Epoch, run, _matches, result =>
        {
            _trip = null;
            if (!result.Ok)
            {
                done(new FollowResult(false, 0, result.Reason));
                return;
            }

            if (result.Superseded)
            {
                ModLog.Info("Scenes: the host went on to a match before this game reached its map; following it there.");
                done(new FollowResult(true, 0, string.Empty, Applied: false));
                return;
            }

            ModLog.Info($"Scenes: on the host's world map (act {host!.Act}, node {host.Node}): {result.How}.");
            _shown = host;
            _cameraApplied = null;
            if (result.Loaded)
            {
                ActIntro.Play(host); // a new run's or act's map: its intro, as the host's game played it
            }

            ApplyCursor(host);
            done(new FollowResult(true, result.Applied, string.Empty, Applied: result.Applied != 0));
        });
        _trip.Step();
    }

    /// <inheritdoc />
    public void UpdateRun(RunCheckpoint run, Action<FollowResult> done)
    {
        if (_deferred != run.Sequence)
        {
            _deferred = run.Sequence;
            ModLog.Info($"Scenes: the host saved its run ({run.Sequence}) without moving on the map; it's loaded here at the next move.");
        }

        done(new FollowResult(true, run.Sequence, string.Empty, Applied: false));
    }

    /// <inheritdoc />
    public void Update()
    {
        _trip?.Step();
        NodeEntryMirror.Tick();
        if (_shown != null && FrameClock.Seconds >= _nextHold)
        {
            _nextHold = FrameClock.Seconds + HoldSeconds;
            HoldCursor(_shown);
        }
    }

    /// <inheritdoc />
    public void Leave()
    {
        _trip?.Cancel();
        _trip = null;
        _shown = null;
        _target = null;
        _cursorOn = IntPtr.Zero;
        NodeEntryMirror.Leave();
    }

    /// <summary>
    /// The host's cursor on this map: the node it's on (and its details), the camera's target when the host's moved, and the
    /// host's entry into an event node (<see cref="NodeEntryMirror"/>). Never throws.
    /// </summary>
    private void ApplyCursor(MapViewDetails host)
    {
        HoldCursor(host);
        NodeEntryMirror.Follow(host);
        try
        {
            // An act's intro moves the camera itself, and a node entry pushes it into the node: the host's camera is followed
            // again once they're over (the entry's own return puts it back where the host's goes).
            if (host.CameraX is float camera && _cameraApplied != camera && !ActIntro.Playing && host.Entering == null)
            {
                _cameraApplied = camera;
                ScrollNonUI.Current?.SetTargetX(camera);
            }
        }
        catch (Exception ex)
        {
            CursorFailed(ex);
        }
    }

    /// <summary>
    /// Selects the host's node unless the friend's cursor is already there or in something that isn't the map (a mirrored
    /// menu), and shows its details (tooltip) as the host's screen does. Never throws.
    /// </summary>
    private void HoldCursor(MapViewDetails host)
    {
        try
        {
            // The host's relic bar first (its own toggle, then its relic): while the host's cursor is in it, its node isn't held.
            if (!RelicBarMirror.Follow(host.Relic, host.RelicAt))
            {
                HoldCursorCore(host);
            }
        }
        catch (Exception ex)
        {
            CursorFailed(ex);
        }

        NodeTooltipMirror.Follow(host.Selected, host.TooltipShown);
    }

    private static void CursorFailed(Exception ex)
    {
        if (_cursorErrors++ < 3)
        {
            ModLog.Error("Scenes: mirroring the host's map cursor failed", ex);
        }
    }

    private void HoldCursorCore(MapViewDetails host)
    {
        if (host.Selected is not MapPoint selected || !WorldMap.MapLoaded || ActIntro.Playing)
        {
            return;
        }

        // The node's navigable is looked up again only when the host's node or the scene changes.
        if (_target == null || _target.WasCollected || _targetPoint != selected || _targetScene != WorldMap.SceneHandle)
        {
            _target = WorldMap.NodeAt(selected)?.Navigable;
            _targetPoint = selected;
            _targetScene = WorldMap.SceneHandle;
        }

        NavigationController? navigation = WorldMap.Navigation;
        AbstractNavigable? target = _target;
        AbstractNavigable? current = navigation?.CurrentNavigable;
        // A cursor in a mirrored menu stays there, unless that menu is an event screen put aside for the map (the host's
        // cursor is on the map then, with its node details).
        if (navigation == null || target == null || (current != null && current.Pointer == target.Pointer) ||
            (current != null && !OnMapNode(current) && !NodeEntryMirror.Peeking))
        {
            return;
        }

        FriendUi.Select(navigation, target);
        CursorMoves++;
    }

    /// <summary>
    /// True when <paramref name="current"/> is one of this map's nodes. The search through the map's nodes runs once per
    /// navigable the cursor is on: held ten times a second, a cursor in a menu (an event screen's whole visit) would search
    /// them each time.
    /// </summary>
    private bool OnMapNode(AbstractNavigable current)
    {
        if (current.Pointer != _cursorOn || _cursorScene != _targetScene)
        {
            _cursorOn = current.Pointer;
            _cursorScene = _targetScene;
            _cursorOnNode = WorldMap.PointOf(current) != null;
        }

        return _cursorOnNode;
    }
}

/// <summary>
/// A friend's way to the host's match preview (<see cref="Screens.Preview"/>): a <see cref="MapTrip"/> to the host's map,
/// the host's run in (the preview's home skater and the substitutes come from it), then the game's own preview of the
/// same node with the host's opponent and rewards (<see cref="PreviewMirror"/>). Leaving closes it the way the game
/// closes it (<c>BackToMap</c>'s outro and camera), or, when the host started the match, plays the game's zoom into the
/// node once this game's copy of the match starts loading (<see cref="Tick"/>; its final step, entering the node, is held
/// back here: the match relay loads the match). A match that doesn't start loading here gets the preview closed instead,
/// so the map is never left zoomed and faded under nothing (and a map that was zoomed is reloaded by the next trip). A
/// new preview while one is showing closes the old one first, the way the host's game did.
/// </summary>
internal sealed class PreviewFollower : IScreenFollower
{
    /// <summary>Longest wait for this game's copy of the host's match to start loading before the preview closes instead of zooming.</summary>
    private const double ZoomWaitSeconds = 30;

    /// <summary>After a preview closes here for another, the game's close (outro and camera) gets this long before the next opens.</summary>
    private const double ReopenSeconds = 0.6;

    private readonly MatchRelay _matches;
    private MapTrip? _trip;
    private PreviewViewDetails? _pending;
    private PreviewViewDetails? _shown;
    private RunCheckpoint? _run;
    private Action<FollowResult>? _done;
    private uint _epoch;
    private uint _applied;
    private bool _runReady;
    private string _runProblem = string.Empty;
    private bool _showing;
    private double _openAfter;

    // The host started the match from its preview: the zoom into the node plays with the host's (the preview view says so),
    // or, when that view didn't come in time, once this game's copy of the match is loading.
    private MatchPreviewMenu? _zoomMenu;
    private double _zoomSince;
    private int _zoomScene;
    private bool _zoomed;

    /// <summary>A preview follower that waits for <paramref name="matches"/> to finish a host's match first.</summary>
    public PreviewFollower(MatchRelay matches) => _matches = matches ?? throw new ArgumentNullException(nameof(matches));

    /// <inheritdoc />
    public string Screen => Screens.Preview;

    /// <inheritdoc />
    public void Go(HostView view, RunCheckpoint? run, Action<FollowResult> done)
    {
        if (!PreviewViewDetails.TryRead(view, out PreviewViewDetails? host, out string problem))
        {
            done(new FollowResult(false, 0, $"the host's preview view is malformed ({problem})"));
            return;
        }

        if (run == null)
        {
            done(new FollowResult(false, 0, FollowReasons.PreviewWithoutRun));
            return;
        }

        _trip?.Cancel();
        _trip = null;
        DropZoom("the host opened a preview again", close: true);
        if (_showing && PreviewMirror.Menu is { IsShown: true } open)
        {
            if (_shown != null && SamePreview(_shown, host!) && run.Sequence == _run?.Sequence)
            {
                // The same preview again under a new number: it's showing already. Now zooming: the host started the match.
                _pending = null;
                _epoch = view.Epoch;
                if (host!.Zooming && !_zoomed)
                {
                    Zoom(open, "playing the same zoom here as the host's");
                }

                done(new FollowResult(true, 0, string.Empty, Applied: false));
                return;
            }

            // Another preview while this one shows (this game skipped the host's map view in between): closed the way the
            // host's game closed it, and the next one opens once that's done, never over it.
            _showing = false;
            _shown = null;
            MapGuards.Allow(open.BackToMap);
            _openAfter = FrameClock.Seconds + ReopenSeconds;
            ModLog.Info("Scenes: the host opened another preview; closed the one showing here first.");
        }

        _zoomed = false;
        _pending = host;
        _epoch = view.Epoch;
        _run = run;
        _done = done;
        _applied = 0;
        _runReady = false;
        _runProblem = string.Empty;
        _trip = new MapTrip(host!.Map, view.Epoch, run, _matches, OnTrip);
        _trip.Step();
    }

    /// <inheritdoc />
    public void UpdateRun(RunCheckpoint run, Action<FollowResult> done) => done(new FollowResult(true, run.Sequence, string.Empty, Applied: false));

    /// <inheritdoc />
    public void Update()
    {
        _trip?.Step();
        if (_done == null || !_runReady)
        {
            return;
        }

        if (_runProblem.Length > 0)
        {
            Finish(new FollowResult(false, 0, $"couldn't load the host's run: {_runProblem}"));
            return;
        }

        if (_matches.FriendBusy || MatchSetup.Loading)
        {
            // A host's match is loading here (the map unloads for it): the preview waits, or gives way to the match's view.
            if (CampaignRelay.Current?.View is { } latest && latest.Epoch != _epoch)
            {
                Finish(new FollowResult(true, 0, string.Empty, Applied: false));
            }

            return;
        }

        if (FrameClock.Seconds < _openAfter)
        {
            return; // the preview closed for this one is still closing
        }

        try
        {
            Open();
        }
        catch (Exception ex)
        {
            ModLog.Error("Scenes: opening the host's preview failed", ex);
            Finish(new FollowResult(false, 0, $"an error ({ModLog.SafeErrorText(ex)})"));
        }
    }

    /// <inheritdoc />
    public void Leave()
    {
        _trip?.Cancel();
        _trip = null;
        _done = null;
        _pending = null;
        if (!_showing)
        {
            return;
        }

        _showing = false;
        _shown = null;
        MatchPreviewMenu? menu = PreviewMirror.Menu;
        if (menu == null || !menu.IsShown)
        {
            return;
        }

        if (_zoomed)
        {
            return; // the zoom played with the host's; this game's copy of the match loads over it
        }

        if (CampaignRelay.Current?.View?.Screen == Screens.Match)
        {
            // The host started the match: the same zoom into the node, once this game's copy of the match is on its way
            // (the zoom ends the map: played for a match that never loads here, it would leave the map zoomed and faded).
            _zoomMenu = menu;
            _zoomSince = FrameClock.Seconds;
            _zoomScene = WorldMap.SceneHandle;
            Tick();
        }
        else
        {
            MapGuards.Allow(menu.BackToMap);
            ModLog.Info("Scenes: the host closed its preview; closed it here.");
        }
    }

    /// <summary>
    /// Every frame while following (outside the relay's screen updates: the "match" screen is the relay's by then): the
    /// zoom into the host's match plays once this game's copy of it starts loading (the zoom's last step, entering the
    /// node, is held back: <see cref="MapGuards.ExpectZoomEnd"/>). If it doesn't start within <see cref="ZoomWaitSeconds"/>,
    /// or the host is somewhere else meanwhile, the preview closes instead. Never throws.
    /// </summary>
    public void Tick()
    {
        // Every frame of a session: a managed check (Unity's == is a call into the game); a destroyed menu is caught below.
        MatchPreviewMenu? menu = _zoomMenu;
        if (menu is null)
        {
            return;
        }

        try
        {
            if (menu.WasCollected || WorldMap.SceneHandle != _zoomScene || !menu.IsShown)
            {
                DropZoom(string.Empty, close: false); // the map went (the match loaded over it) or the preview closed
                return;
            }

            if (_matches.FriendBusy || MatchSetup.Loading)
            {
                _zoomMenu = null;
                Zoom(menu, "playing the preview's zoom here while this game loads the match (the host's start came with its match)");
                return;
            }

            if (CampaignRelay.Current?.View?.Screen != Screens.Match)
            {
                DropZoom("the host is no longer in that match", close: true);
            }
            else if (FrameClock.Seconds - _zoomSince > ZoomWaitSeconds)
            {
                DropZoom($"this game's copy of the host's match didn't start loading within {ZoomWaitSeconds:0} s", close: true);
            }
        }
        catch (Exception ex)
        {
            _zoomMenu = null;
            ModLog.Error("Scenes: following the host's preview into its match failed", ex);
        }
    }

    /// <summary>
    /// The game's zoom from <paramref name="menu"/> into its node, as the host's game played it: inputs off, the zoom's last
    /// step (entering the node) held back, and the map marked as zoomed (the next map view loads it afresh).
    /// </summary>
    private void Zoom(MatchPreviewMenu menu, string how)
    {
        _zoomed = true;
        menu.UnregisterInputs();
        MapGuards.ExpectZoomEnd();
        WorldMap.MarkZoomed();
        menu.AnimateMatchZoom();
        ModLog.Info($"Scenes: the host started the match from its preview; {how}.");
    }

    /// <summary>Forgets a zoom still waiting for the host's match (the session ended): the scene goes with the session.</summary>
    public void ForgetZoom() => _zoomMenu = null;

    /// <summary>A zoom still waiting for the host's match won't play; <paramref name="close"/>: the preview closes the game's way instead.</summary>
    private void DropZoom(string why, bool close)
    {
        MatchPreviewMenu? menu = _zoomMenu;
        _zoomMenu = null;
        if (!close || menu == null || menu.WasCollected || !menu.IsShown)
        {
            return;
        }

        MapGuards.Allow(menu.BackToMap);
        ModLog.Info($"Scenes: closed the host's preview here instead of zooming into its match ({why}).");
    }

    private void OnTrip(MapTripResult result)
    {
        _trip = null;
        if (!result.Ok)
        {
            Finish(new FollowResult(false, 0, result.Reason));
            return;
        }

        if (result.Superseded)
        {
            ModLog.Info("Scenes: the host went on to a match before this game showed its preview; following it there.");
            Finish(new FollowResult(true, 0, string.Empty, Applied: false));
            return;
        }

        if (result.How.Length > 0)
        {
            ModLog.Info($"Scenes: on the host's world map for its preview: {result.How}.");
        }

        // The preview's home skater and substitutes come from the run: it goes in now if the trip didn't load it.
        RunSync? runs = RunSync.Instance;
        if (runs == null)
        {
            _runProblem = "run sync isn't running";
            _runReady = true;
            return;
        }

        _applied = result.Applied;
        runs.Apply(_run!, applied =>
        {
            _applied = applied.Ok ? applied.Checkpoint : _applied;
            _runProblem = applied.Ok ? string.Empty : applied.Reason;
            _runReady = true;
        });
    }

    private void Open()
    {
        PreviewViewDetails host = _pending!;
        MatchPreviewMenu? menu = PreviewMirror.Menu;
        MatchMapNode? node = WorldMap.NodeAt(host.Target)?.TryCast<MatchMapNode>();
        if (menu == null)
        {
            Finish(new FollowResult(false, 0, FollowReasons.NoMatchPreview));
            return;
        }

        if (node == null || (int)node.NodeType != host.NodeType)
        {
            Finish(new FollowResult(false, 0, $"this map has no {NodeName(host.NodeType)} node at {host.Target}"));
            return;
        }

        List<string> differences = PreviewMirror.Open(menu, node, host);
        _showing = true;
        _shown = host;
        ModLog.Info($"Scenes: showing the host's preview of the {NodeName(host.NodeType)} node {host.Target}, its panel opening with the game's intro " +
            $"(opponent {string.Join(", ", host.Opponent.Select(PreviewMirror.ShowHost))}; goalie {PreviewMirror.ShowHost(host.Goalie)}; rewards shown: {Rewards(host)})" +
            (differences.Count == 0 ? ", the same as the host's." : $"; different here: {string.Join("; ", differences)}."));
        Finish(new FollowResult(true, _applied, string.Empty, Applied: _applied != 0));
    }

    private void Finish(FollowResult result)
    {
        Action<FollowResult>? done = _done;
        _done = null;
        _runReady = false;
        done?.Invoke(result);
    }

    /// <summary>True when both previews show the same node, opponent and rewards on the same map.</summary>
    private static bool SamePreview(PreviewViewDetails a, PreviewViewDetails b) =>
        a.Target == b.Target && a.NodeType == b.NodeType && a.Goalie == b.Goalie && a.RewardRelic == b.RewardRelic && a.RewardTalent == b.RewardTalent &&
        a.RewardAbility == b.RewardAbility && a.Opponent.SequenceEqual(b.Opponent) && a.Map.SamePlace(b.Map);

    private static string NodeName(int type) => type switch
    {
        0 => "Challenge",
        1 => "Elite",
        2 => "Boss",
        _ => $"type {type}",
    };

    private static string Rewards(PreviewViewDetails host)
    {
        var shown = new List<string>();
        foreach (string id in new[] { host.RewardRelic, host.RewardTalent, host.RewardAbility })
        {
            if (id.Length > 0)
            {
                shown.Add(PreviewMirror.ShowHost(id));
            }
        }

        return shown.Count == 0 ? "none" : string.Join(", ", shown);
    }
}
#endif
