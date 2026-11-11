#if HAS_INTEROP
using System;
using System.Collections.Generic;
using System.Linq;
using Il2CppFMODUnity;
using Il2CppSTS.Map;
using Il2CppState;
using Il2CppTape2Tape.Hockey.UI;
using T2TOnline.Core.Campaign;
using T2TOnline.Plugin.Diagnostics;
using T2TOnline.Plugin.Online;
using T2TOnline.Plugin.Streaming;
using UnityEngine;

namespace T2TOnline.Plugin.Campaign.Scenes;

/// <summary>
/// The beaten node's explosion and the map's appear sound, on every screen together. Each PC's game plays them
/// at its own map's reveal: <c>WorldMapMenusHandler.&lt;PlayMatchExplosion&gt;d__62</c> calls <c>MatchMapNode.PlayExplosionAnim</c>
/// once the map's loading transition has hidden (or a second later with none), and <c>MapView.ShowMap</c> starts its
/// inlined <c>PlayMapAppearSound</c>, which waits for the same transition. A friend's map loads after the host's view
/// of it, so the host's come first (<see cref="MapRevealHold"/>).
/// <list type="bullet">
/// <item>Host, only those two wait, and only until friends' maps are in, at most <see cref="MapRevealHold.MaxHoldSeconds"/> past
/// when the game would have played them. The loading screen, the transition and the map's input aren't touched: its hide
/// (<c>&lt;PrepareWorldMapMenus&gt;d__94</c>) comes first and the game's state machines go on as if they'd played.</item>
/// <item>The explosion: a prefix on <c>PlayExplosionAnim</c> (not a skip-to-last-frame's, which draws an earlier node) keeps the
/// node and refuses the call; the game's own call later. Nothing awaits it, nothing else calls it (checked across the game's code:
/// one caller).</item>
/// <item>The sound: <c>ShowMap</c>'s prefix empties the view's <c>mapAppearSound</c> so its inlined start finds nothing to play
/// and returns, and its postfix puts it back; once this PC's transition is where the game's wait would have it, it
/// plays with the game's own <c>PlayOneShot</c>.</item>
/// <item>Any failure, a session's end, a map that's gone or a friend gone never leaves either held: they play at once or go with
/// their scene. On a friend these patches only say when its explosion plays.</item>
/// </list>
/// </summary>
internal static class MapReveal
{
    private static readonly MapRevealHold Hold = new();
    private static readonly List<ulong> Waited = new();
    private static MatchMapNode? _node;
    private static CampaignState? _state;
    private static double _nodeDue;
    private static MapView? _view;
    private static EventReference _sound;
    private static bool _soundTaken;
    private static bool _soundHeld;
    private static double _soundDue = double.NaN;
    private static WooshTransition? _woosh;
    private static bool _waitForHidden;
    private static bool _letThrough;
    private static bool _friendLogged;
    private static int _errors;

    /// <summary>Patches the explosion's call and the map's show. Adds a line to <paramref name="failed"/> for each that couldn't go in: that one then plays as before.</summary>
    public static void Install(HarmonyLib.Harmony harmony, List<string> failed)
    {
        ScenePatches.Prefix(harmony, failed, typeof(MatchMapNode), nameof(MatchMapNode.PlayExplosionAnim), typeof(MapReveal), nameof(ExplosionPrefix));
        ScenePatches.Patch(harmony, failed, typeof(MapView), nameof(MapView.ShowMap), typeof(MapReveal), nameof(ShowMapPrefix), nameof(ShowMapPostfix));
    }

    /// <summary>Host: a map view went out: the first one after a map was made is what friends' reports count from.</summary>
    public static void HostViewPublished(uint epoch) => Hold.ViewPublished(epoch, FrameClock.Seconds);

    /// <summary>Host: the session ended: whatever waits plays now.</summary>
    public static void HostStopped()
    {
        if (Hold.Active || _node != null || _soundHeld)
        {
            ReleaseAll("the session ended");
        }

        Hold.End();
    }

    /// <summary>Every frame (the match presentations' tick, which always runs). Never throws.</summary>
    public static void Tick()
    {
        if (!Hold.Active)
        {
            return;
        }

        try
        {
            if (CampaignRelay.Current is not { Hosting: true } relay)
            {
                HostStopped();
                return;
            }

            RestoreSound();
            double now = FrameClock.Seconds;
            IReadOnlyList<ulong> friends = relay.Friends;
            for (int i = 0; i < friends.Count; i++)
            {
                // An index loop: every frame of the hold (a foreach over the interface would box its enumerator).
                if (relay.TryGetReport(friends[i], out FollowReport report))
                {
                    Hold.Report(friends[i], report.Epoch, report.State == FollowState.Following, now);
                }
            }

            for (int i = Waited.Count - 1; i >= 0; i--)
            {
                if (!friends.Contains(Waited[i]))
                {
                    Hold.FriendLeft(Waited[i]);
                    Waited.RemoveAt(i);
                }
            }

            TickSound(relay, now);
            TickNode(relay, now);
            if (Hold.Expired(now) && _node == null && !_soundHeld)
            {
                Hold.End();
            }
        }
        catch (Exception ex)
        {
            Failed("the wait for friends' maps", ex);
            ReleaseAll("the wait for friends' maps failed");
            Hold.End();
        }
    }

    /// <summary>The held sound plays once this PC's transition is where the game's wait would have it and friends' maps are in.</summary>
    private static void TickSound(CampaignRelay relay, double now)
    {
        if (!_soundHeld)
        {
            return;
        }

        if (double.IsNaN(_soundDue))
        {
            if (!TransitionDone() && !Hold.Expired(now))
            {
                return;
            }

            _soundDue = now;
        }

        if (Hold.Release(_soundDue, now, out string why))
        {
            double held = now - _soundDue;
            PlaySound();
            ModLog.Info(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"Scenes: the map's appear sound plays now ({why}; held {held * 1000:0} ms: {Hold.DescribeWaits(LogNames.Tag)})."));
        }
    }

    /// <summary>The held explosion plays once friends' maps are in, or the node has gone with its scene.</summary>
    private static void TickNode(CampaignRelay relay, double now)
    {
        if (_node == null)
        {
            return;
        }

        if (!Alive(_node))
        {
            _node = null;
            _state = null;
            ModLog.Info("Scenes: the map whose beaten node's explosion waited for friends went away before it played.");
            return;
        }

        if (Hold.Release(_nodeDue, now, out string why))
        {
            double held = now - _nodeDue;
            PlayNode();
            ModLog.Info(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"Scenes: the beaten node's explosion plays now ({why}; held {held * 1000:0} ms: {Hold.DescribeWaits(LogNames.Tag)})."));
        }
    }

    /// <summary>
    /// The map's show. Host, with friends in a shared run: this map's hold begins, and its appear sound is taken out for the
    /// call (its inlined start returns at once with none set).
    /// </summary>
    private static void ShowMapPrefix(MapView __instance, Map m)
    {
        try
        {
            _friendLogged = false;
            if (CampaignRelay.Current is not { Hosting: true } relay || __instance == null || m == null || RunSync.Instance?.HostOnCoopSlot != true)
            {
                return;
            }

            // Checked across the game's code: one show per map load (RunManager.Start's two ways there exclude each other): a sound
            // still held is an old map's.
            _soundHeld = false;
            if (_node != null)
            {
                ReleaseAll("a new map was made");
            }

            Waited.Clear();
            Waited.AddRange(relay.Friends);
            if (!Hold.Begin(relay.Friends, FrameClock.Seconds))
            {
                return;
            }

            EventReference sound = __instance.mapAppearSound;
            if (sound.Guid.Data1 != 0 || sound.Guid.Data2 != 0 || sound.Guid.Data3 != 0 || sound.Guid.Data4 != 0)
            {
                _view = __instance;
                _sound = sound;
                _soundTaken = true;
                _soundHeld = true;
                _soundDue = double.NaN;
                _waitForHidden = __instance.waitForWooshHidingCompleted;
                _woosh = UnityEngine.Object.FindObjectOfType<WooshLoadingTransition>();
                __instance.mapAppearSound = default;
            }

            ModLog.Info($"Scenes: the map's appear sound and any beaten node's explosion wait for {relay.Friends.Count} friend(s) to have this map (at most {MapRevealHold.MaxHoldSeconds:0} s past their moment), so they play on every screen together.");
        }
        catch (Exception ex)
        {
            Failed("holding the map's appear sound", ex);
            ReleaseAll("holding it failed");
            Hold.End();
        }
    }

    /// <summary>The map's show is over: its appear sound goes back in the view (it plays from here, when it's due).</summary>
    private static void ShowMapPostfix()
    {
        try
        {
            RestoreSound();
        }
        catch (Exception ex)
        {
            Failed("putting the map's appear sound back", ex);
        }
    }

    /// <summary>
    /// The game plays a beaten node's explosion. Host, holding this map for friends: kept, and refused for now (a skip to the
    /// last frame, an earlier node's, isn't held). Friend: said once a map, so both logs tell how far apart they ran.
    /// </summary>
    private static bool ExplosionPrefix(MatchMapNode __instance, CampaignState state, bool skipToLastFrame)
    {
        try
        {
            if (_letThrough || skipToLastFrame || __instance == null)
            {
                return true;
            }

            if (CampaignRelay.Current is { Following: true })
            {
                if (!_friendLogged)
                {
                    _friendLogged = true;
                    ModLog.Info("Scenes: the beaten node's explosion plays here, at this map's reveal (the host's waits for this PC's map to play with it).");
                }

                return true;
            }

            if (!Hold.Active || _node != null || CampaignRelay.Current is not { Hosting: true })
            {
                return true;
            }

            _node = __instance;
            _state = state;
            _nodeDue = FrameClock.Seconds;
            return false;
        }
        catch (Exception ex)
        {
            Failed("holding the beaten node's explosion", ex);
            _node = null;
            _state = null;
            return true;
        }
    }

    /// <summary>The held explosion's own call (with the game's arguments), once. Never throws.</summary>
    private static void PlayNode()
    {
        MatchMapNode? node = _node;
        CampaignState? state = _state;
        _node = null;
        _state = null;
        if (!Alive(node))
        {
            return;
        }

        _letThrough = true;
        try
        {
            node!.PlayExplosionAnim(state!, false);
        }
        catch (Exception ex)
        {
            Failed("playing the beaten node's explosion", ex);
        }
        finally
        {
            _letThrough = false;
        }
    }

    /// <summary>The held sound's own call, once. Never throws.</summary>
    private static void PlaySound()
    {
        _soundHeld = false;
        _woosh = null;
        try
        {
            RestoreSound();
            RuntimeManager.PlayOneShot(_sound, Vector3.zero);
        }
        catch (Exception ex)
        {
            Failed("playing the map's appear sound", ex);
        }
    }

    /// <summary>Whatever is held plays now; <paramref name="why"/> goes in the log. Never throws.</summary>
    private static void ReleaseAll(string why)
    {
        bool any = _node != null || _soundHeld;
        if (_soundHeld)
        {
            PlaySound();
        }

        if (_node != null)
        {
            PlayNode();
        }

        if (any)
        {
            ModLog.Info($"Scenes: what waited for friends' maps plays now ({why}).");
        }
    }

    /// <summary>The view's sound back in its field, the first chance there is (the postfix, or the next frame if the show threw).</summary>
    private static void RestoreSound()
    {
        if (!_soundTaken)
        {
            return;
        }

        _soundTaken = false;
        MapView? view = _view;
        _view = null;
        if (Alive(view))
        {
            view!.mapAppearSound = _sound;
        }
    }

    /// <summary>
    /// True once this PC's loading transition is where the game's own wait would let the sound play: none up when the map was
    /// shown; else hidden (<c>m_IsShown</c> off), or hidden and done hiding when the map's prefab asks for that
    /// (<c>waitForWooshHidingCompleted</c>).
    /// </summary>
    private static bool TransitionDone()
    {
        WooshTransition? woosh = _woosh;
        if (!Alive(woosh) || !woosh!.gameObject.activeInHierarchy)
        {
            return true;
        }

        return woosh.IsHidden && !(_waitForHidden && woosh.IsHiding);
    }

    private static bool Alive(UnityEngine.Object? value) => value is not null && !value.WasCollected && value != null;

    private static void Failed(string what, Exception ex)
    {
        if (_errors++ < 3)
        {
            ModLog.Error($"Scenes: {what} failed; it plays as the game plays it", ex);
        }
    }
}
#endif
