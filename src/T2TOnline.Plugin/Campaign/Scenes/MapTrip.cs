#if HAS_INTEROP
using System;
using System.Globalization;
using T2TOnline.Core.Campaign;
using T2TOnline.Plugin.Diagnostics;
using T2TOnline.Plugin.Match;
using T2TOnline.Plugin.Online;
using T2TOnline.Plugin.Streaming;

namespace T2TOnline.Plugin.Campaign.Scenes;

/// <summary>How a <see cref="MapTrip"/> went.</summary>
/// <param name="Ok">The friend's game shows the host's map.</param>
/// <param name="Applied">The run checkpoint the trip loaded into the game (0: it needed none).</param>
/// <param name="Reason">Why not.</param>
/// <param name="How">What it did, for the log.</param>
/// <param name="Superseded">The host moved on to a match before the trip got anywhere: nothing was done, nothing failed.</param>
/// <param name="Loaded">The trip loaded the world map (a new load of the scene, not a stay or a move in place).</param>
internal readonly record struct MapTripResult(bool Ok, uint Applied, string Reason, string How, bool Superseded = false, bool Loaded = false);

/// <summary>
/// One trip of a friend's game to the host's world map (D44), over as many frames as it takes: it waits for any scene load
/// and for a host's match here (a campaign match loading, playing or ending: the map unloads for it, and the world map is
/// never loaded over it), then, by <see cref="MapFollow.Decide"/>:
/// <list type="bullet">
/// <item>stays (the map already shows the host's run, act and path);</item>
/// <item>advances in place (the host moved on along the path: the run checkpoint goes in, then the map's current node
/// moves and the team's marker and relic bar refresh, as the game's own map does after a node), or reloads when that
/// can't be done safely (or when the preview's zoom into a match that never loaded here is still over the map);</item>
/// <item>reloads: the checkpoint goes in (<see cref="RunSync.Apply"/>), then <c>world_map</c> loads the way the game
/// loads it, and the trip waits for the map's <c>WorldMapInstanceLoaded</c> and checks it's the host's.</item>
/// </list>
/// Ends once, through its callback; <see cref="Cancel"/> drops it without one. A trip the host's next view overtook while a
/// match is here ends without doing anything, and not as a failure (<see cref="MapTripResult.Superseded"/>). Each wait is
/// timed from when it starts (a trip that waited out a long match still gets its full scene and run waits), and a trip
/// that ends on the map sets the map's music state as the game's own ways there do (<see cref="WorldMap.SetMapAudio"/>).
/// A trip that loads a run waits first while this PC's event screen is up or closing (<see cref="RunLoadHold"/>, at most
/// 3 s): the host's game saves the run during an event's exit, a moment before its menu closes.
/// </summary>
internal sealed class MapTrip
{
    /// <summary>Longest wait for a scene load to finish before the trip starts.</summary>
    private const double WaitSeconds = 30;

    /// <summary>Longest wait for the world map to load.</summary>
    private const double LoadSeconds = 90;

    private readonly MapViewDetails _host;
    private readonly uint _epoch;
    private readonly RunCheckpoint _run;
    private readonly MatchRelay _matches;
    private readonly Action<MapTripResult> _done;
    private static bool _audioLogged;
    private Phase _phase = Phase.Start;
    private MapMove _move;
    private string _why = string.Empty;
    private double _loadStarted;
    private double _waitStarted = double.NaN;
    private double _applyStarted;
    private int _sceneBefore;
    private uint _applied;
    private string _applyProblem = string.Empty;
    private bool _decided;
    private double _holdSince = double.NaN;

    /// <summary>
    /// A trip to the host's <paramref name="host"/> map (of view <paramref name="epoch"/>) with its <paramref name="run"/>;
    /// <paramref name="done"/> is called once.
    /// </summary>
    public MapTrip(MapViewDetails host, uint epoch, RunCheckpoint run, MatchRelay matches, Action<MapTripResult> done)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _epoch = epoch;
        _run = run ?? throw new ArgumentNullException(nameof(run));
        _matches = matches ?? throw new ArgumentNullException(nameof(matches));
        _done = done ?? throw new ArgumentNullException(nameof(done));
    }

    private enum Phase
    {
        Start,
        Applying,
        Applied,
        Loading,
        Over,
    }

    /// <summary>True once the trip has ended (or was cancelled).</summary>
    public bool Over => _phase == Phase.Over;

    /// <summary>True while a host's match is here: the match relay loads, plays or ends one (the world map unloads for it).</summary>
    private bool MatchHere => _matches.FriendBusy || MatchSetup.Loading;

    /// <summary>True when the host's newest view isn't the one this trip is for.</summary>
    private bool Overtaken => CampaignRelay.Current?.View is { } view && view.Epoch != _epoch;

    /// <summary>Drops the trip: no callback, and a run load still running is ignored when it answers.</summary>
    public void Cancel() => _phase = Phase.Over;

    /// <summary>The next step. Call every frame until <see cref="Over"/>.</summary>
    public void Step()
    {
        try
        {
            StepCore();
        }
        catch (Exception ex)
        {
            ModLog.Error("Scenes: following the host to its world map failed", ex);
            Finish(false, $"an error ({ModLog.SafeErrorText(ex)})");
        }
    }

    private void StepCore()
    {
        switch (_phase)
        {
            case Phase.Start:
                Start();
                break;
            case Phase.Applying when FrameClock.Seconds - _applyStarted > LoadSeconds:
                Finish(false, $"the host's run {_run.Sequence} didn't load within {LoadSeconds:0} s");
                break;
            case Phase.Applied:
                AfterApply();
                break;
            case Phase.Loading:
                WaitForMap();
                break;
        }
    }

    private void Start()
    {
        RunSync? runs = RunSync.Instance;
        if (runs == null)
        {
            Finish(false, FollowReasons.RunSyncNotRunning);
            return;
        }

        if (WaitForMatch())
        {
            _waitStarted = double.NaN; // the scene-load wait starts once the match is gone
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
                Finish(false, FollowReasons.SceneLoadUnfinished);
            }

            return;
        }

        if (!_decided)
        {
            MapPlace? here = runs.HoldingHostRun ? WorldMap.Place() : null;
            _move = MapFollow.Decide(here, _host, out _why);
            if (_move != MapMove.Reload && WorldMap.Zoomed)
            {
                // The preview's zoom into a match played over this map, and no match replaced it here: only a load undoes it.
                _move = MapMove.Reload;
                _why = "the preview's zoom into the host's match is still over this map";
            }

            // The host's General Manager rerolled this map's node rewards and some couldn't be built here in place
            // (WorldMap.MarkRewardsStale, NodeRewardsMirror): a load builds them from the host's newest run, an advance in
            // place would keep the old ones. Any move here takes the mark.
            if (_move != MapMove.Stay && WorldMap.TakeStaleRewards(out string stale) && _move == MapMove.Advance)
            {
                _move = MapMove.Reload;
                _why = stale;
            }

            if (_move == MapMove.Stay)
            {
                Finish(true, string.Empty, $"already on the host's map ({_why})");
                return;
            }

            _decided = true; // kept while the run waits for an event screen below
        }

        if (HoldForEventScreen())
        {
            return;
        }

        _phase = Phase.Applying;
        _applyStarted = FrameClock.Seconds;
        runs.Apply(_run, result =>
        {
            if (_phase != Phase.Applying)
            {
                return; // cancelled, or answered twice
            }

            _applied = result.Ok ? result.Checkpoint : 0;
            _applyProblem = result.Ok ? string.Empty : result.Reason;
            _phase = Phase.Applied; // the next step carries on: no scene work inside run sync's callback
        });
    }

    private void AfterApply()
    {
        if (_applyProblem.Length > 0)
        {
            Finish(false, $"couldn't load the host's run {_run.Sequence}: {_applyProblem}");
            return;
        }

        if (_move == MapMove.Advance)
        {
            if (WorldMap.Advance(_host.Path, out string problem))
            {
                string stale = WorldMap.RefreshAfterNode();
                Finish(true, string.Empty, $"moved the map's current node in place ({_why}){(stale.Length > 0 ? $"; couldn't refresh {stale}" : string.Empty)}");
                return;
            }

            ModLog.Info($"Scenes: can't move this map's current node in place ({problem}); reloading the world map instead.");
            _move = MapMove.Reload; // tried once: a reload waiting for a scene load mustn't try (and log) it again every frame
        }

        if (WaitForMatch() || WorldMap.Loading)
        {
            return;
        }

        // The game's loading transition first, as the host's game shows it before its loads (tried again next frame).
        if (!Menus.FriendTransitions.LoadReady("the world map", out _))
        {
            return;
        }

        _sceneBefore = WorldMap.SceneHandle;
        if (!WorldMap.Load(WorldMap.MapScene, out string loadProblem))
        {
            // The transition shown for this load goes with it (nothing else would take it down but the guard's limits).
            Menus.FriendTransitions.LoadGivenUp("the world map", $"the world map couldn't load: {loadProblem}");
            Finish(false, $"couldn't load the world map: {loadProblem}");
            return;
        }

        ModLog.Info($"Scenes: loading the world map with the host's run {_run.Sequence} ({_why}).");
        _loadStarted = FrameClock.Seconds;
        _phase = Phase.Loading;
    }

    private void WaitForMap()
    {
        if (WorldMap.SceneHandle == _sceneBefore || !WorldMap.MapLoaded)
        {
            if (WaitForMatch())
            {
                return; // a host's match started loading over the map: never a failure
            }

            if (FrameClock.Seconds - _loadStarted > LoadSeconds)
            {
                Finish(false, $"the world map didn't load within {LoadSeconds:0} s");
            }

            return;
        }

        MapMove check = MapFollow.Decide(WorldMap.Place(), _host, out string why);
        if (check == MapMove.Stay)
        {
            Finish(true, string.Empty, "loaded the world map", loaded: true);
        }
        else
        {
            Finish(false, $"the world map loaded, but not the host's ({why})");
        }
    }

    /// <summary>
    /// True while the run load waits for this PC's event screen to close (<see cref="Menus.FriendMenus.HoldsRunLoad"/>; at
    /// most <see cref="RunLoadHold.MaxWaitSeconds"/>), so the run's objects aren't swapped under a menu still showing the old
    /// ones nor the map loaded under it. Logged when it waits and when it goes.
    /// </summary>
    private bool HoldForEventScreen()
    {
        string what = string.Empty;
        bool held = Menus.MenuViews.Instance is { } views && views.Friend.HoldsRunLoad(out what);
        double waited = double.IsNaN(_holdSince) ? -1 : FrameClock.Seconds - _holdSince;
        switch (RunLoadHold.Decide(loads: true, held, waited))
        {
            case RunLoadStep.Wait:
                if (double.IsNaN(_holdSince))
                {
                    _holdSince = FrameClock.Seconds;
                    ModLog.Info($"Scenes: the host's run {_run.Sequence} waits for this PC's {what} to close (the host's game saved it during the event's exit).");
                }

                return true;
            case RunLoadStep.GoAfterWait:
                ModLog.Info(string.Create(CultureInfo.InvariantCulture, $"Scenes: the host's run {_run.Sequence} goes in {waited:0.00} s later, after this PC's event screen closed."));
                return false;
            case RunLoadStep.GoAfterTimeout:
                ModLog.Info($"Scenes: the host's run {_run.Sequence} goes in after {RunLoadHold.MaxWaitSeconds:0} s (this PC's {what} is still up).");
                return false;
            default:
                return false;
        }
    }

    /// <summary>
    /// True while a host's match is here, so the trip waits (however long the match takes: it's the host's): it ends
    /// quietly instead once the host's view has moved on, which the match's own view does.
    /// </summary>
    private bool WaitForMatch()
    {
        if (!MatchHere)
        {
            return false;
        }

        if (Overtaken)
        {
            Finish(true, string.Empty, "the host moved on to a match", superseded: true);
        }

        return true;
    }

    private void Finish(bool ok, string reason, string how = "", bool superseded = false, bool loaded = false)
    {
        if (_phase == Phase.Over)
        {
            return;
        }

        _phase = Phase.Over;
        if (ok && !superseded)
        {
            MapAudio();
        }

        _done(new MapTripResult(ok, ok ? _applied : 0, reason, how, superseded, ok && loaded));
    }

    /// <summary>The map's music state (<see cref="WorldMap.SetMapAudio"/>); a failure is logged once a game session and never stops the trip.</summary>
    private static void MapAudio()
    {
        try
        {
            Il2CppFMOD.RESULT result = WorldMap.SetMapAudio();
            if (result != Il2CppFMOD.RESULT.OK && !_audioLogged)
            {
                _audioLogged = true;
                ModLog.Warn($"Scenes: FMOD didn't take the world map's music state (location = worldmap: {result}); this game may play another screen's music on the map.");
            }
        }
        catch (Exception ex)
        {
            if (!_audioLogged)
            {
                _audioLogged = true;
                ModLog.Error("Scenes: setting the world map's music state failed", ex);
            }
        }
    }
}
#endif
