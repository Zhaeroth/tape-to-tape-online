#if HAS_INTEROP
using System;
using System.Collections.Generic;
using System.Globalization;
using Il2CppInterop.Runtime;
using Il2CppRogue;
using Il2CppSkaters;
using Il2CppSpine.Unity;
using Il2CppUltimateReplay;
using T2TOnline.Core.Serialization;
using T2TOnline.Core.Stream;
using T2TOnline.Plugin.Diagnostics;
using UnityEngine;

namespace T2TOnline.Plugin.Streaming;

/// <summary>
/// The ball and chain's chain on a friend's PC. The host's game makes a chain with each ball (<c>BallAndChainController.SpawnBall</c>:
/// the talent's <c>chainPrefab</c>, a <c>Rope</c> from the skater's skate to the ball): the ball is a replay object and reaches a friend
/// as a copy made for the stream, but the chain is in no stream, and the game code that makes it never runs on a friend's PC. So:
/// <list type="bullet">
/// <item>Host: <see cref="ReadHost"/> adds the chains to each regular HUD sample (<see cref="ChainKeys"/>): each active
/// <c>BallAndChainComponent</c>'s ball, skater, skate and length. The components are looked for twice a second, and at
/// once and again a tenth of a second later when one held goes (a faceoff's new ball, the prisoner boss's transfer to a
/// scorer).</item>
/// <item>Friend: the HUD channel hands every chain value to <see cref="Take"/> and calls <see cref="AfterMessage"/> after
/// each message; <see cref="Tick"/> runs every frame. Each chain the host lists gets the game's own chain prefab, fastened
/// as <c>BallAndChainComponent.Initialize</c> fastens it (<c>Rope.SetTotalLength</c> from the ball's own config,
/// <c>Rope.SetEndpoints</c> from the skate to the ball, the right skate's <c>BoneFollower</c> on for a second ball), and
/// the game's rope draws it (its ticks run in client mode: clientmode.txt, [presentation]). Its hit detector is off. A
/// chain shows while its ball and skater do, and goes when the host stops listing it as it is, when its ball or skater
/// is destroyed here, and when playback ends.</item>
/// </list>
/// Network input: at most <see cref="ChainKeys.MaxChains"/> chains, <see cref="MadePerSecond"/> made a second, and only
/// for objects of the stream (<see cref="ChainFollower"/>). A ball that has its own chain here (the recording's own match,
/// F4) is left alone. Nothing here throws into the game: the first failure is logged and that side turns itself off for
/// the rest of the game session, taking down the chains it made.
/// </summary>
internal static class ChainMirror
{
    /// <summary>Chains made a second, at most.</summary>
    public const int MadePerSecond = 8;

    /// <summary>Chains logged one by one per playback (and the host's per stream); the rest are counted.</summary>
    private const int LoggedChains = 4;

    /// <summary>How often the host looks for balls while nothing it holds has gone.</summary>
    private const double HostSearchSeconds = 0.5;

    /// <summary>
    /// How soon the host looks again after one it held went: a ball's next one can come a frame later (the prisoner boss's
    /// transfer to a scorer spawns it from the scorer's next talent update).
    /// </summary>
    private const double HostFollowUpSeconds = 0.1;

    // Host.
    private static readonly List<BallAndChainComponent> HostFound = new();
    private static readonly HashSet<IntPtr> HostLogged = new();
    private static double _nextHostSearch = double.MinValue;
    private static int _hostChains;
    private static int _hostMost;
    private static bool _hostOverLogged;
    private static bool _hostOff;

    // Friend.
    private static readonly ChainFollower Follower = new();
    private static readonly ChainSync Sync = new(MadePerSecond);
    private static readonly Dictionary<ChainLink, Made> Chains = new();
    private static readonly List<ChainLink> Gone = new();
    private static readonly List<ChainLink> Missing = new();
    private static IReadOnlyDictionary<ushort, ReplayObject> _objects = new Dictionary<ushort, ReplayObject>();
    private static BallAndChain? _talent;
    private static bool _talentLooked;
    private static bool _playing;
    private static bool _pending;
    private static bool _off;
    private static int _logged;
    private static int _most;
    private static int _removed;
    private static int _ownChains;
    private static int _unfastened;

    // ------------------------------------------------------------------ host

    /// <summary>Host: a new stream starts (the HUD recorder's start).</summary>
    public static void HostStart()
    {
        HostFound.Clear();
        HostLogged.Clear();
        _nextHostSearch = double.MinValue;
        _hostChains = _hostMost = 0;
        _hostOverLogged = false;
    }

    /// <summary>Host, at each regular HUD sample: the chains on the ice now. Never throws.</summary>
    public static void ReadHost(Dictionary<string, NetValue> values, Func<UnityEngine.Object, NetValue> refOf)
    {
        if (_hostOff)
        {
            return;
        }

        try
        {
            double now = FrameClock.Seconds;
            bool lost = HostFound.Exists(found => found == null || !found._active);
            if (lost || now >= _nextHostSearch)
            {
                _nextHostSearch = now + (lost ? HostFollowUpSeconds : HostSearchSeconds);
                FindHostChains();
            }

            int listed = 0;
            foreach (BallAndChainComponent component in HostFound)
            {
                if (component == null || !component._active || component._victim is not Forward victim || victim == null ||
                    component._chainRope is not Rope rope || rope == null)
                {
                    continue;
                }

                if (listed == ChainKeys.MaxChains)
                {
                    if (!_hostOverLogged)
                    {
                        _hostOverLogged = true;
                        ModLog.Warn($"Chains: more than {ChainKeys.MaxChains} balls and chains are on the ice here; friends see the first {ChainKeys.MaxChains} chains.");
                    }

                    break;
                }

                NetValue ball = refOf(component.gameObject);
                NetValue skater = refOf(victim);
                if (ball.Kind != NetValueKind.Reference || skater.Kind != NetValueKind.Reference)
                {
                    continue; // not in the stream (yet): friends can't name it
                }

                Transform? right = victim.chainAttachmentRight;
                Transform? start = rope.start;
                bool onRight = right != null && start != null && start.Pointer == right.Pointer;
                values[ChainKeys.Ball(listed)] = ball;
                values[ChainKeys.Skater(listed)] = skater;
                values[ChainKeys.Right(listed)] = NetValue.FromBool(onRight);
                values[ChainKeys.Human(listed)] = NetValue.FromBool(component._humanTeam);
                listed++;
                if (HostLogged.Add(component.Pointer))
                {
                    _hostChains++;
                    if (_hostChains <= LoggedChains)
                    {
                        ModLog.Info($"Chains: {LogNames.ObjectName(victim)}'s ball and chain ({(onRight ? "right" : "left")} skate) goes to friends with the HUD values (chain.*)" +
                            $"{(_hostChains == LoggedChains ? "; further ones are counted" : string.Empty)}.");
                    }
                }
            }

            values[ChainKeys.Count] = NetValue.FromInt(listed);
            _hostMost = Math.Max(_hostMost, listed);
        }
        catch (Exception ex)
        {
            _hostOff = true;
            ModLog.Warn($"Chains: reading this match's balls and chains failed ({ModLog.SafeErrorText(ex)}); friends don't get the chains for the rest of this game session.");
        }
    }

    /// <summary>Host: the stream ends (the HUD recorder's stop): what went to friends.</summary>
    public static void HostStop()
    {
        if (_hostChains > 0)
        {
            ModLog.Info($"Chains: {_hostChains} ball(s) and chain(s) went to friends this match ({_hostMost} at most at once).");
        }

        HostFound.Clear();
    }

    private static void FindHostChains()
    {
        HostFound.Clear();
        // The non-generic overload: it's in this build for certain, a generic instance may not be.
        foreach (UnityEngine.Object found in UnityEngine.Object.FindObjectsByType(Il2CppType.Of<BallAndChainComponent>(), FindObjectsInactive.Exclude, FindObjectsSortMode.None))
        {
            if (found?.TryCast<BallAndChainComponent>() is BallAndChainComponent component && component._active)
            {
                HostFound.Add(component);
            }
        }
    }

    // ------------------------------------------------------------------ friend

    /// <summary>Friend: playback of a host's match starts (the HUD channel's start). <paramref name="objects"/>: the stream's objects here.</summary>
    public static void Begin(IReadOnlyDictionary<ushort, ReplayObject> objects)
    {
        DestroyAll();
        _objects = objects;
        _playing = !_off;
        _pending = false;
        _talent = null;
        _talentLooked = false;
        Follower.Reset();
        Sync.Reset();
        _logged = _most = _removed = _ownChains = _unfastened = 0;
    }

    /// <summary>Friend: one HUD value. True when it's a chain value (taken here; the HUD channel writes it nowhere else).</summary>
    public static bool Take(string key, NetValue value)
    {
        if (!ChainKeys.IsKey(key))
        {
            return false;
        }

        if (_playing)
        {
            Follower.Take(key, value);
        }

        return true;
    }

    /// <summary>Friend: a HUD message has been read: the chains here follow the host's list. Never throws.</summary>
    public static void AfterMessage()
    {
        if (!_playing)
        {
            return;
        }

        bool changed = Follower.Changed;
        Follower.EndMessage();
        if (changed)
        {
            Guarded("following the host's chains", Follow);
        }
    }

    /// <summary>Friend, every frame: chains still to make (their ball not here yet), and each chain shown with its ball and skater. Never throws.</summary>
    public static void Tick()
    {
        if (!_playing)
        {
            return;
        }

        Guarded("keeping the chains with their balls", () =>
        {
            if (_pending)
            {
                Follow();
            }

            KeepWithObjects();
        });
    }

    /// <summary>Friend: playback ended (the match is still loaded, or gone): every chain made goes, and what was done is said. Never throws.</summary>
    public static void End(bool matchLoaded)
    {
        if (!_playing)
        {
            return;
        }

        _playing = false;
        int left = Chains.Count;
        DestroyAll();
        if (Sync.Made + _ownChains + _unfastened + Follower.Refused > 0)
        {
            ModLog.Info($"Chains: {Sync.Made} of the host's chains drawn here this match ({_most} at most at once, {_removed} went with their ball or skater, " +
                $"{left} at the end{(matchLoaded ? string.Empty : ", gone with the match")})" +
                $"{(_ownChains > 0 ? $"; {_ownChains} ball(s) had their own chain here" : string.Empty)}" +
                $"{(_unfastened > 0 ? $"; {_unfastened} couldn't be fastened (no skate or chain prefab here)" : string.Empty)}" +
                $"{(Sync.Deferred > 0 ? $"; {Sync.Deferred} waited for the budget of {MadePerSecond} a second" : string.Empty)}" +
                $"{(Follower.Refused > 0 ? $"; {Follower.Refused} chain value(s) from the host refused" : string.Empty)}.");
        }

        _objects = new Dictionary<ushort, ReplayObject>();
        _talent = null;
    }

    /// <summary>Takes down the chains the host no longer lists as they are, and makes the ones it does whose objects are here.</summary>
    private static void Follow()
    {
        IReadOnlyList<ChainLink> wanted = Follower.Links();
        ChainSync.Diff(Chains.Keys, wanted, Gone, Missing);
        foreach (ChainLink link in Gone)
        {
            Remove(link);
        }

        _pending = false;
        foreach (ChainLink link in Missing)
        {
            if (!TryMake(link))
            {
                _pending = true; // its ball's copy isn't made yet, or this second's budget is spent: next frame
            }
        }

        _most = Math.Max(_most, Chains.Count);
    }

    /// <summary>
    /// Makes <paramref name="link"/>'s chain if its ball and skater are here. False while one isn't (or the budget is spent):
    /// it's tried again. A chain that can't be fastened, or a ball with its own chain, is remembered without one, so it isn't
    /// tried every frame.
    /// </summary>
    private static bool TryMake(ChainLink link)
    {
        if (!_objects.TryGetValue(link.Ball, out ReplayObject? ballObject) || ballObject == null ||
            !_objects.TryGetValue(link.Skater, out ReplayObject? skaterObject) || skaterObject == null)
        {
            return false;
        }

        GameObject ball = ballObject.gameObject;
        GameObject skaterRoot = skaterObject.gameObject;
        Forward? forward = skaterRoot.GetComponent<Forward>();
        BallAndChainComponent? own = ball.GetComponent<BallAndChainComponent>();
        if (own != null && own._active)
        {
            // This PC's own game made and fastened it (the recording's own match): its own chain is there.
            _ownChains++;
            Chains[link] = new Made(null, ball, skaterRoot);
            return true;
        }

        Transform? anchor = forward == null ? null : link.Right && forward.chainAttachmentRight is Transform right && right != null ? right : forward.chainAttachment;
        GameObject? prefab = ChainPrefab();
        if (anchor == null || prefab == null)
        {
            if (_unfastened++ == 0)
            {
                ModLog.Warn($"Chains: the host's chain on {LogNames.ObjectName(skaterRoot)} can't be drawn here ({(prefab == null ? "this PC has no ball and chain talent loaded" : "the skater has no skate to fasten it to")}); " +
                    "further ones are counted.");
            }

            Chains[link] = new Made(null, ball, skaterRoot);
            return true;
        }

        if (!Sync.TryTake(FrameClock.Micros))
        {
            return false;
        }

        // The non-generic overload (a generic instance may not be in this build).
        GameObject? chain = UnityEngine.Object.Instantiate((UnityEngine.Object)prefab)?.TryCast<GameObject>();
        Rope? rope = chain?.GetComponentInChildren(Il2CppType.Of<Rope>(), true)?.TryCast<Rope>();
        if (chain == null || rope == null)
        {
            DestroyQuietly(chain);
            throw new InvalidOperationException("the game's chain prefab has no rope");
        }

        chain.name = string.Create(CultureInfo.InvariantCulture, $"T2TOnline chain (ball {link.Ball})");
        foreach (Component found in chain.GetComponentsInChildren(Il2CppType.Of<RopeHitDetector>(), true))
        {
            // Hits are the host's (and its FixedUpdate is held in client mode anyway).
            if (found?.TryCast<RopeHitDetector>() is RopeHitDetector hits)
            {
                hits.enabled = false;
            }
        }

        if (link.Right && forward!.chainAttachmentRight is Transform rightSkate && anchor.Pointer == rightSkate.Pointer &&
            anchor.GetComponent(Il2CppType.Of<BoneFollower>())?.TryCast<BoneFollower>() is BoneFollower follower)
        {
            // As SpawnBall does for a second ball: the right skate's follower is off until one needs it.
            follower.enabled = true;
        }

        // As BallAndChainComponent.Initialize does: the length from the ball's own config (the player's or the AI's), then
        // the ends, from the skate to the ball.
        BallAndChainConfig? config = own == null ? null : link.Human ? own.playerConfig : own.AIConfig;
        config ??= link.Human ? _talent?.playerConfig : _talent?.AIConfig;
        float length = config != null ? config.restLength : -1f;
        if (length >= 0f)
        {
            rope.SetTotalLength(length);
        }

        rope.SetEndpoints(anchor, ball.transform);
        bool show = ball.activeInHierarchy && skaterRoot.activeInHierarchy;
        chain.SetActive(show);
        Chains[link] = new Made(chain, ball, skaterRoot) { Shown = show };
        if (_logged++ < LoggedChains)
        {
            ModLog.Info($"Chains: the host's ball and chain on {LogNames.ObjectName(skaterRoot)} drawn here with the game's own chain ({(link.Right ? "right" : "left")} skate, " +
                $"{(link.Human ? "a player's" : "the AI's")} length{(length >= 0f ? string.Create(CultureInfo.InvariantCulture, $" {length:0.##}") : " left as the prefab's")})" +
                $"{(_logged == LoggedChains ? "; further ones are counted" : string.Empty)}.");
        }

        return true;
    }

    /// <summary>Each chain shown while its ball and skater are; one whose ball or skater was destroyed here goes (and is made again if a new copy comes).</summary>
    private static void KeepWithObjects()
    {
        Gone.Clear();
        foreach ((ChainLink link, Made made) in Chains)
        {
            if (made.Ball == null || made.Skater == null)
            {
                Gone.Add(link);
                continue;
            }

            if (made.Chain == null)
            {
                continue;
            }

            bool show = made.Ball.activeInHierarchy && made.Skater.activeInHierarchy;
            if (show != made.Shown)
            {
                // Off before its ball is put away: an inactive rope runs no tick, so it never reads a destroyed end.
                made.Chain.SetActive(show);
                made.Shown = show;
            }
        }

        foreach (ChainLink link in Gone)
        {
            Remove(link);
            _pending = true;
        }
    }

    /// <summary>The game's chain prefab, from the ball and chain talent (looked up once per playback).</summary>
    private static GameObject? ChainPrefab()
    {
        if (!_talentLooked)
        {
            _talentLooked = true;
            foreach (UnityEngine.Object found in Resources.FindObjectsOfTypeAll(Il2CppType.Of<BallAndChain>()))
            {
                if (found?.TryCast<BallAndChain>() is BallAndChain talent && talent.chainPrefab != null)
                {
                    _talent = talent;
                    break;
                }
            }
        }

        return _talent != null && _talent.chainPrefab is GameObject prefab && prefab != null ? prefab : null;
    }

    private static void Remove(ChainLink link)
    {
        if (Chains.Remove(link, out Made? made) && made.Chain != null)
        {
            _removed++;
            DestroyQuietly(made.Chain);
        }
    }

    private static void DestroyAll()
    {
        foreach (Made made in Chains.Values)
        {
            DestroyQuietly(made.Chain);
        }

        Chains.Clear();
    }

    private static void DestroyQuietly(GameObject? chain)
    {
        try
        {
            if (chain != null)
            {
                chain.SetActive(false);
                UnityEngine.Object.Destroy(chain);
            }
        }
        catch (Exception)
        {
            // Gone with its scene already.
        }
    }

    /// <summary>The friend side's work, guarded: the first failure turns the mirror off for the rest of the game session.</summary>
    private static void Guarded(string what, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            _off = true;
            _playing = false;
            ModLog.Warn($"Chains: {what} failed ({ModLog.SafeErrorText(ex)}); the host's chains aren't drawn here for the rest of this game session.");
            DestroyAll();
        }
    }

    /// <summary>A chain made here (or, with no chain, a link that can't have one here), and the objects it hangs between.</summary>
    private sealed class Made
    {
        public Made(GameObject? chain, GameObject ball, GameObject skater)
        {
            Chain = chain;
            Ball = ball;
            Skater = skater;
        }

        public GameObject? Chain { get; }

        public GameObject Ball { get; }

        public GameObject Skater { get; }

        public bool Shown { get; set; }
    }
}
#endif
