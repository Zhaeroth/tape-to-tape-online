#if HAS_INTEROP
using System;
using System.Collections.Generic;
using System.Linq;
using Il2CppSTS.Map;
using Il2CppTape2Tape.Hockey.UI;
using T2TOnline.Core.Campaign;
using T2TOnline.Core.Serialization;
using T2TOnline.Plugin.Diagnostics;
using T2TOnline.Plugin.Online;
using T2TOnline.Plugin.Streaming;

namespace T2TOnline.Plugin.Campaign.Scenes;

/// <summary>
/// An act's intro (its title card and the camera's fly-over, <c>WorldMapActIntro</c>) on every screen, together. The game
/// plays it when it makes a map (a new run, or the next act after a boss: <c>RogueEvents.NewActCreated</c>, raised by
/// <c>RunManager.Start</c>, whose only listener that starts it is <c>WorldMapActIntro.OnNewActCreated</c>); its
/// <c>Play</c> waits while the loading screen is up, then plays. A friend's game never makes a map, it loads the host's
/// (after the host's run checkpoint, a second or two later), so it never got the intro, and once it did, it played it at
/// its own map's load, behind the host's.
/// <list type="bullet">
/// <item>Host: with friends in the session, the intro's start is held
/// (<see cref="ActIntroBarrier"/>) until every friend reports following the map view that carries it, at most 6 s; its
/// first frame shows meanwhile (the game's own <c>Evaluate(0)</c> under the loading screen, as its <c>Play</c> starts), the
/// map's cursor is locked and <c>WorldMapActIntro.IsPlaying</c> says the intro plays, as during the intro (the campaign
/// tutorial's map step waits on it, and the mouse's left click is refused by it). Then the game's own
/// <c>OnNewActCreated</c> plays it, and the map view says so (<see cref="MapViewDetails.IntroStarted"/>). Each friend's wait is
/// logged.</item>
/// <item>Host: the intro starting is noted (a postfix), and the map view carries its act while the act's path is still
/// empty (<see cref="MapViewDetails.Intro"/>).</item>
/// <item>Friend: a map trip that loaded such a map plays the same intro once per run and act (the game's own
/// <c>OnNewActCreated</c>), when the host's view says the host's started (at most 8 s later:
/// <see cref="ActIntroFollow"/>); meanwhile the mirrored cursor and camera wait (<see cref="Playing"/>).</item>
/// </list>
/// The beaten node's explosion and the map's appear sound aren't held here: the game starts them at each map's own load
/// (<c>WorldMapMenusHandler.OnMapLoaded</c>, <c>MapView.ShowMap</c>) and they wait for that PC's own loading transition.
/// </summary>
internal static class ActIntro
{
    /// <summary><c>WorldMapActIntro.UNDER_LOADING_SCREEN_ORDER</c>, its canvas's order while it waits (0x31 on the Nov 6 build, as its <c>Play</c> sets it).</summary>
    private const int UnderLoadingScreenOrder = 0x31;

    private static readonly HashSet<string> Played = new(StringComparer.Ordinal);
    private static readonly ActIntroBarrier Barrier = new();
    private static readonly ActIntroFollow Follow = new();

    // The friends the held intro waits for (as the barrier began with them), so one who leaves is noticed without LINQ.
    private static readonly List<ulong> Waited = new();
    private static int _hostAct;
    private static int _hostScene;
    private static int _errors;
    private static bool _letThrough;
    private static WorldMapActIntro? _heldIntro;
    private static int _heldOrder;
    private static MapPlayerTracker? _lockedTracker;
    private static bool _trackerWasLocked;
    private static bool _startedPending;
    private static WorldMapActIntro? _waitingIntro;

    /// <summary>True while the game plays an act's intro (it moves the camera itself). False when that can't be told.</summary>
    public static bool Playing
    {
        get
        {
            try
            {
                return WorldMapActIntro.IsPlaying || _heldIntro != null || Follow.Pending != 0;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    /// <summary>
    /// Patches the intro's start: a prefix (host: held for friends) and a postfix (host: noted). Adds a line to
    /// <paramref name="failed"/> if it couldn't go in.
    /// </summary>
    public static void Install(HarmonyLib.Harmony harmony, List<string> failed) =>
        ScenePatches.Patch(harmony, failed, typeof(WorldMapActIntro), nameof(WorldMapActIntro.OnNewActCreated), typeof(ActIntro), nameof(OnNewActCreatedPrefix), nameof(OnNewActCreatedPostfix));

    /// <summary>A new following session: each act's intro plays once in it.</summary>
    public static void NewSession()
    {
        Played.Clear();
        Follow.Reset();
        _waitingIntro = null;
    }

    /// <summary>Host: the act whose intro played as the map at <paramref name="place"/> was made, while its path is empty; else null.</summary>
    public static int? HostIntro(MapPlace place)
    {
        ArgumentNullException.ThrowIfNull(place);
        // The same load of the map (a scene holds one run) at the same act, before its first node.
        return _hostAct > 0 && place.Path.Count == 0 && place.Act == _hostAct && WorldMap.SceneHandle == _hostScene
            ? _hostAct
            : null;
    }

    /// <summary>Host: whether the intro at <paramref name="place"/> has started here (false while held for friends); null with no intro.</summary>
    public static bool? HostIntroStarted(MapPlace place) => HostIntro(place) is int act ? !(Barrier.Holding && Barrier.Act == act) : null;

    /// <summary>Host: a map view went out (its <paramref name="epoch"/>): friends following it or a later one have the intro's map.</summary>
    public static void HostViewPublished(MapViewDetails details, uint epoch)
    {
        if (details?.Intro != null)
        {
            Barrier.ViewPublished(epoch);
        }
    }

    /// <summary>Host: true once after a held intro started (the map view is published again, saying so).</summary>
    public static bool TakeStarted()
    {
        bool started = _startedPending;
        _startedPending = false;
        return started;
    }

    /// <summary>
    /// Every frame (from the match presentations' tick, which runs whatever campaign module is on): a host's held intro and
    /// a friend's waiting one go on when they should; a held intro with no hosting session left plays at once. Never throws.
    /// </summary>
    public static void Tick()
    {
        CampaignRelay? relay = CampaignRelay.Current;
        if (relay is { Hosting: true })
        {
            HostTick(relay);
        }
        else if (Barrier.Holding)
        {
            HostStopped();
        }

        if (relay is { Following: true })
        {
            FriendTick(relay);
        }
        else if (Follow.Pending != 0)
        {
            // The session ended while this PC's intro waited: the map is being left; nothing plays.
            Follow.Reset();
            _waitingIntro = null;
        }
    }

    /// <summary>
    /// Host, every frame: a held intro starts once every friend's game has the map (their follow reports), or after 6 s,
    /// or when no friend is left. Never throws.
    /// </summary>
    private static void HostTick(CampaignRelay relay)
    {
        if (!Barrier.Holding)
        {
            return;
        }

        try
        {
            // Every frame of the hold: index loops over the lists (no enumerator, closure or copy).
            double now = FrameClock.Seconds;
            IReadOnlyList<ulong> friends = relay.Friends;
            for (int i = 0; i < friends.Count; i++)
            {
                if (relay.TryGetReport(friends[i], out FollowReport report))
                {
                    Barrier.Report(friends[i], report.Epoch, report.State == FollowState.Following, now);
                }
            }

            for (int i = Waited.Count - 1; i >= 0; i--)
            {
                if (!friends.Contains(Waited[i]))
                {
                    Barrier.FriendLeft(Waited[i]);
                    Waited.RemoveAt(i);
                }
            }

            LockTracker();
            if (!Alive(_heldIntro))
            {
                Barrier.Abandon();
                UnlockTracker();
                _heldIntro = null;
                SayPlaying(false);
                ModLog.Info("Scenes: the map whose act intro waited for friends went away before it played.");
                return;
            }

            if (!Barrier.ShouldRelease(now, out string why))
            {
                return;
            }

            ModLog.Info(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"Scenes: act {Barrier.Act}'s intro plays now ({why}; it waited {Barrier.Held * 1000:0} ms: {Barrier.DescribeWaits(LogNames.Tag)})."));
            ReleaseHeld();
        }
        catch (Exception ex)
        {
            if (_errors++ < 3)
            {
                ModLog.Error("Scenes: the act intro's wait for friends failed; it plays now", ex);
            }

            Barrier.Abandon();
            ReleaseHeld();
        }
    }

    /// <summary>Host: the session ended: a held intro plays now.</summary>
    public static void HostStopped()
    {
        if (Barrier.Holding)
        {
            Barrier.Abandon();
            ReleaseHeld();
            ModLog.Info("Scenes: the session ended while an act intro waited for friends; it plays now.");
        }
    }

    /// <summary>
    /// Friend: this game loaded the host's <paramref name="host"/> map; its act's intro plays here if the host's did, as
    /// soon as the host's has started. Never throws.
    /// </summary>
    public static void Play(MapViewDetails host)
    {
        if (host.Intro is not int act || !Played.Add($"{host.RunId}:{act}"))
        {
            return;
        }

        try
        {
            WorldMapActIntro? intro = UnityEngine.Object.FindObjectOfType<WorldMapActIntro>();
            if (intro == null)
            {
                ModLog.Warn($"Scenes: the host's game played act {act}'s intro, but this map has none to play.");
                return;
            }

            // An older host's view (no start flag) plays at once, as before.
            if (Follow.Arrived(act, host.IntroStarted != false, FrameClock.Seconds))
            {
                Start(intro, act, "as the host's game did when it made this map");
                return;
            }

            _waitingIntro = intro;
            ModLog.Info($"Scenes: act {act}'s intro waits for the host's to start (it waits for every friend's map), at most {ActIntroFollow.MaxWaitSeconds:0} s.");
        }
        catch (Exception ex)
        {
            if (_errors++ < 3)
            {
                ModLog.Error($"Scenes: playing act {act}'s intro here failed", ex);
            }
        }
    }

    /// <summary>Friend, every frame: a waiting intro plays once the host's view says the host's started (or after 8 s). Never throws.</summary>
    private static void FriendTick(CampaignRelay relay)
    {
        if (Follow.Pending == 0)
        {
            return;
        }

        try
        {
            bool started = relay.View is { Screen: Screens.Map } view && view.TryGet(SceneViewKeys.IntroStarted, out NetValue flag) &&
                flag.Kind == NetValueKind.Flag && flag.WholeValue != 0;
            if (!Follow.Due(started, FrameClock.Seconds, out bool timedOut))
            {
                return;
            }

            int act = Follow.Pending;
            Follow.Played();
            WorldMapActIntro? intro = _waitingIntro;
            _waitingIntro = null;
            if (!Alive(intro))
            {
                ModLog.Info($"Scenes: act {act}'s intro waited for the host's, but this map has gone.");
                return;
            }

            Start(intro!, act, timedOut
                ? $"the host's didn't say it started within {ActIntroFollow.MaxWaitSeconds:0} s"
                : "with the host's");
        }
        catch (Exception ex)
        {
            Follow.Reset();
            _waitingIntro = null;
            if (_errors++ < 3)
            {
                ModLog.Error("Scenes: playing the act intro with the host's failed", ex);
            }
        }
    }

    /// <summary>
    /// The intro's start: on a host with friends, held until their games have the map (the game's own call later). The
    /// mod's own call goes through.
    /// </summary>
    private static bool OnNewActCreatedPrefix(WorldMapActIntro __instance, int act)
    {
        if (_letThrough)
        {
            return true;
        }

        try
        {
            // Only a shared run's map is followed by friends (a solo run's isn't: they wait at the title).
            if (CampaignRelay.Current is not { Hosting: true } relay || __instance == null || Barrier.Holding ||
                RunSync.Instance?.HostOnCoopSlot != true || !Barrier.Start(act, relay.Friends, FrameClock.Seconds))
            {
                return true;
            }

            Waited.Clear();
            Waited.AddRange(relay.Friends);
            _heldIntro = __instance;
            // Its first frame, as its Play starts it (under the loading screen while that's up), so the map doesn't show bare.
            UnityEngine.Canvas? canvas = __instance._canvas;
            if (canvas != null)
            {
                _heldOrder = canvas.sortingOrder;
                canvas.sortingOrder = UnderLoadingScreenOrder;
            }

            __instance.Evaluate(0f);
            LockTracker();
            SayPlaying(true);
            ModLog.Info($"Scenes: act {act}'s intro waits for {relay.Friends.Count} friend(s) to have the map (at most {ActIntroBarrier.MaxWaitSeconds:0} s), so it plays on every screen together.");
            return false;
        }
        catch (Exception ex)
        {
            if (_errors++ < 3)
            {
                ModLog.Error("Scenes: holding the act intro for friends failed; it plays now", ex);
            }

            Barrier.Abandon();
            _heldIntro = null;
            UnlockTracker();
            SayPlaying(false); // the game's own call refuses to start while it says so
            return true;
        }
    }

    /// <summary>Host: an act's intro is starting (the game made a new map).</summary>
    private static void OnNewActCreatedPostfix(WorldMapActIntro __instance, int act)
    {
        try
        {
            if (CampaignRelay.Current?.Hosting != true || __instance == null)
            {
                return;
            }

            _hostAct = act;
            _hostScene = __instance.gameObject.scene.handle; // the map's own scene (it may not be the active one yet)
        }
        catch (Exception ex)
        {
            if (_errors++ < 3)
            {
                ModLog.Error("Scenes: noting the host's act intro failed", ex);
            }
        }
    }

    /// <summary>Host: plays the held intro with the game's own call (its canvas order and the cursor lock as they were first).</summary>
    private static void ReleaseHeld()
    {
        WorldMapActIntro? intro = _heldIntro;
        _heldIntro = null;
        UnlockTracker();
        SayPlaying(false); // first: the game's OnNewActCreated does nothing while it says an intro plays
        if (!Alive(intro))
        {
            return;
        }

        try
        {
            UnityEngine.Canvas? canvas = intro!._canvas;
            if (canvas != null)
            {
                canvas.sortingOrder = _heldOrder;
            }

            _letThrough = true;
            intro.OnNewActCreated(Barrier.Act > 0 ? Barrier.Act : _hostAct);
            _startedPending = true;
        }
        catch (Exception ex)
        {
            if (_errors++ < 3)
            {
                ModLog.Error("Scenes: starting the held act intro failed", ex);
            }
        }
        finally
        {
            _letThrough = false;
        }
    }

    /// <summary>Friend: the game's own start of the intro.</summary>
    private static void Start(WorldMapActIntro intro, int act, string how)
    {
        intro.OnNewActCreated(act);
        ModLog.Info($"Scenes: playing act {act}'s intro here, {how}.");
    }

    /// <summary>Host: the map's cursor is locked while the intro waits, as during the intro (put back before it plays).</summary>
    private static void LockTracker()
    {
        if (_lockedTracker != null)
        {
            return;
        }

        MapPlayerTracker? tracker = MapPlayerTracker.Instance;
        if (tracker == null)
        {
            return;
        }

        _trackerWasLocked = tracker.isLocked;
        tracker.isLocked = true;
        _lockedTracker = tracker;
    }

    private static void UnlockTracker()
    {
        MapPlayerTracker? tracker = _lockedTracker;
        _lockedTracker = null;
        if (Alive(tracker))
        {
            tracker!.isLocked = _trackerWasLocked;
        }
    }

    /// <summary>
    /// Host: <c>WorldMapActIntro.IsPlaying</c> while the intro is held, as during the intro. Its readers are the campaign
    /// tutorial's map step (<c>WorldMapIntroStep</c>'s wait: its first screen would come up under the intro) and the mouse's
    /// left click (<c>T2TUIInputPlayer.OnMouseLeftClick</c>). Never throws.
    /// </summary>
    private static void SayPlaying(bool playing)
    {
        try
        {
            WorldMapActIntro.IsPlaying = playing;
        }
        catch (Exception ex)
        {
            if (_errors++ < 3)
            {
                ModLog.Error("Scenes: setting WorldMapActIntro.IsPlaying failed", ex);
            }
        }
    }

    /// <summary>True for a live Unity object (not collected, not destroyed).</summary>
    private static bool Alive(UnityEngine.Object? value) => value is not null && !value.WasCollected && value != null;
}
#endif
