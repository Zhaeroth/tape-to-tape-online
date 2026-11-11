#if HAS_INTEROP
using System;
using System.Collections.Generic;
using Il2Cpp;
using Il2CppInterop.Runtime;
using T2TOnline.Core.Campaign;
using T2TOnline.Core.Serialization;
using T2TOnline.Core.Stream;
using T2TOnline.Plugin.Campaign.Scenes;
using T2TOnline.Plugin.Diagnostics;
using T2TOnline.Plugin.Streaming;

namespace T2TOnline.Plugin.Match;

/// <summary>
/// The match intro in step with the host's. Each PC's game plays the intro
/// itself (<c>NewNationalAnthem</c>'s skip sequence: the reset, the anthem's end, a Team Canada match's curse pick, the
/// reveal, the team presentation, then the faceoff), started when its own skaters are in
/// (<c>NewNationalAnthem.OnAllSkatersInitialized</c>, a listener on <c>GameplayEvents.AllSkatersInitialized</c>). Its
/// steps wait on scaled time, so the host's is frozen while the loading barrier holds its match for friends; a friend's
/// ran ahead of the host's by its load time.
/// <list type="bullet">
/// <item>Host: the HUD carries <see cref="PresentationKeys.IntroGo"/> (the intro runs: skaters in, no barrier holding),
/// <see cref="PresentationKeys.IntroStep"/> and <see cref="PresentationKeys.IntroCanada"/> (a Canada curse pick runs).</item>
/// <item>Friend (gate A): while this PC loads or plays a host's match (armed by the match relay), the anthem's start is
/// held, and the game's own <c>OnAllSkatersInitialized</c> is called on the held anthem when the host's HUD says its intro
/// runs (<see cref="IntroGate"/>, with its fallbacks). Both intros then run the same scaled steps under the same mirrored
/// time scale.</item>
/// <item>Friend (gate C): in a Team Canada match the anthem awaits the curse pick (<c>CanadaCursePicker.Run</c>): a friend's
/// game gets a <c>UnityEngine.WaitUntil</c> that ends once the host's pick is over (<see cref="CanadaWait"/>), and the
/// pick's load cover comes off then, as the game's own pick takes it off at its end.</item>
/// </list>
/// Logged on both PCs: when the intro runs, how long it was held, and when each team presentation starts, so the two logs
/// say how far apart they played. Nothing here throws into the game.
/// </summary>
internal static class MatchIntro
{
    private static readonly IntroGate Gate = new();
    private static readonly CanadaWait Canada = new();
    private static readonly HostIntroTracker Host = new();
    private static readonly ErrorLogLimit Errors = new(3);

    /// <summary>
    /// What the anthem's sequence raises right after the wait the hold keeps it in: before the hold, a friend's game raised
    /// it before its playback started; held, playback is on by then (its logic listeners detached, as for the other events
    /// of this sequence), and it isn't game logic getting past client mode.
    /// </summary>
    private const string AnthemResetEvent = "GameplayEvents.ResetMap";

    private static NewNationalAnthem? _held;
    private static NewNationalAnthem? _hostAnthem;
    private static bool _letThrough;
    private static bool _armed;
    private static double _lastAnthemSearch = double.MinValue;
    private static int _anthemSearches;
    private static Func<bool>? _canadaPredicate;
    private static Il2CppSystem.Func<bool>? _canadaDelegate;

    // Host: the barrier's hold, the intro's start and the presentation's start (seconds on the frame clock).
    private static double _barrierFrom = double.NaN;
    private static double _barrierHeld;
    private static double _hostGoAt = double.NaN;
    private static bool _hostPresentLogged;

    // Friend: this PC's intro start, the host's presentation as the stream shows it here, and this PC's.
    private static double _friendGoAt = double.NaN;
    private static double _hostPresentHeardAt = double.NaN;
    private static double _friendPresentAt = double.NaN;
    private static bool _friendPresentLogged;

    /// <summary>
    /// Host: true while the match relay's loading barrier holds the match for friends (the match relay sets it). The intro
    /// isn't running then, whatever the anthem says.
    /// </summary>
    public static bool HostBarrierHolding { get; private set; }

    /// <summary>Patches the anthem's start (a prefix: held on a friend while armed). Adds a line to <paramref name="failed"/> if it can't.</summary>
    public static void Install(HarmonyLib.Harmony harmony, List<string> failed) =>
        ScenePatches.Prefix(harmony, failed, typeof(NewNationalAnthem), nameof(NewNationalAnthem.OnAllSkatersInitialized), typeof(MatchIntro), nameof(StartPrefix));

    // ---------------------------------------------------------------- host

    /// <summary>Host: the loading barrier started (true) or stopped holding the match.</summary>
    public static void HostBarrier(bool holding)
    {
        if (holding && !HostBarrierHolding)
        {
            _barrierFrom = FrameClock.Seconds;
        }
        else if (!holding && HostBarrierHolding && !double.IsNaN(_barrierFrom))
        {
            _barrierHeld = FrameClock.Seconds - _barrierFrom;
        }

        HostBarrierHolding = holding;
    }

    /// <summary>Host: a new match's stream starts (the HUD recorder's start).</summary>
    public static void HostStart()
    {
        Host.Reset();
        _hostAnthem = null;
        _lastAnthemSearch = double.MinValue;
        _anthemSearches = 0;
        _hostGoAt = double.NaN;
        _hostPresentLogged = false;
        if (!HostBarrierHolding)
        {
            _barrierHeld = 0;
        }
    }

    /// <summary>Host, at each HUD sample: the intro's values. Never throws.</summary>
    public static void ReadHost(Dictionary<string, NetValue> values)
    {
        try
        {
            NewNationalAnthem? anthem = HostAnthem();
            bool presenting = Il2CppUI.TeamPresentation.IsPlaying;
            // No anthem in this match (looked for since the stream started): no intro to wait for, so friends' run at once.
            bool none = anthem == null && _anthemSearches > 0;
            bool initialized = none || (anthem != null && anthem._allSkatersInitialized);
            bool revealed = none || (anthem != null && anthem.IsReadyToReveal);
            IntroStep step = Host.Sample(initialized, HostBarrierHolding && !none, revealed, presenting);
            values[PresentationKeys.IntroGo] = NetValue.FromBool(step != IntroStep.None);
            values[PresentationKeys.IntroStep] = NetValue.FromInt((int)step);
            values[PresentationKeys.IntroCanada] = NetValue.FromBool(CanadaCursePicker.IsPickRunning);
            double now = FrameClock.Seconds;
            if (step != IntroStep.None && double.IsNaN(_hostGoAt))
            {
                _hostGoAt = now;
                ModLog.Info(none ? "Match intro: this match has no anthem here; friends' intros aren't held."
                    : step == IntroStep.Over
                    ? "Match intro: this match was past its intro when the stream started; friends who load it play theirs at once."
                    : string.Create(System.Globalization.CultureInfo.InvariantCulture,
                        $"Match intro: the intro runs here{(_barrierHeld > 0 ? $" (the loading barrier held it {_barrierHeld * 1000:0} ms)" : string.Empty)}; friends' intros start with it (HUD {PresentationKeys.IntroGo})."));
            }

            if (step == IntroStep.Presenting && !_hostPresentLogged)
            {
                _hostPresentLogged = true;
                ModLog.Info(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                    $"Match intro: the team presentation plays here, {(now - _hostGoAt) * 1000:0} ms after the intro started."));
            }
        }
        catch (Exception ex)
        {
            Failed("reading the host's intro", ex);
        }
    }

    /// <summary>The host's anthem: looked up once a second while there's none, kept while it lives.</summary>
    private static NewNationalAnthem? HostAnthem()
    {
        if (_hostAnthem != null)
        {
            return _hostAnthem;
        }

        double now = FrameClock.Seconds;
        if (now - _lastAnthemSearch < 1)
        {
            return null;
        }

        _lastAnthemSearch = now;
        _anthemSearches++;
        NewNationalAnthem? found = UnityEngine.Object.FindObjectOfType<NewNationalAnthem>();
        _hostAnthem = found != null ? found : null;
        return _hostAnthem;
    }

    // ---------------------------------------------------------------- friend

    /// <summary>Friend: a host's match is loading here (the match relay): this match's intro waits for the host's.</summary>
    public static void ArmFriend()
    {
        ReleaseFriend("the next match is loading");
        Gate.Reset();
        Canada.Reset();
        _armed = true;
        _friendGoAt = _hostPresentHeardAt = _friendPresentAt = double.NaN;
        _friendPresentLogged = false;
    }

    /// <summary>Friend: the session or the host's match went away: a held intro starts now (the game's own), and nothing is held again.</summary>
    public static void ReleaseFriend(string why)
    {
        if (!_armed)
        {
            return;
        }

        _armed = false;
        if (Gate.Release(FrameClock.Micros))
        {
            Open($"{why}; this PC's intro starts now so its match isn't left frozen");
        }
    }

    /// <summary>Friend: playback of the host's match started (the silence fallback counts from now).</summary>
    public static void Showing()
    {
        if (_armed)
        {
            Gate.Showing(FrameClock.Micros);
        }
    }

    /// <summary>Friend: one HUD message's presentation values. Never throws.</summary>
    public static void HostValues(PresentationValues values)
    {
        if (!_armed)
        {
            return;
        }

        try
        {
            long now = FrameClock.Micros;
            Canada.Host(values.CanadaNow, values.IntroStepNow);
            if (values.IntroStepNow == IntroStep.Presenting && double.IsNaN(_hostPresentHeardAt))
            {
                _hostPresentHeardAt = FrameClock.Seconds;
            }

            IntroOpen reason = Gate.Message(values.IntroGoNow, values.IntroStepNow, now);
            if (reason != IntroOpen.None)
            {
                Open(Explain(reason));
            }
        }
        catch (Exception ex)
        {
            Failed("following the host's intro", ex);
        }
    }

    /// <summary>Friend, every frame: the gate's fallbacks, and the team presentations' start for the log. Never throws.</summary>
    public static void Tick()
    {
        if (!_armed)
        {
            return;
        }

        try
        {
            IntroOpen reason = Gate.Tick(FrameClock.Micros);
            if (reason != IntroOpen.None)
            {
                Open(Explain(reason));
            }

            // This PC's team presentation, for the log: looked for in the half minute after its intro started.
            if (!_friendPresentLogged && Gate.Opened && !double.IsNaN(_friendGoAt) && FrameClock.Seconds - _friendGoAt < 30 &&
                Il2CppUI.TeamPresentation.IsPlaying)
            {
                _friendPresentLogged = true;
                _friendPresentAt = FrameClock.Seconds;
                string versus = double.IsNaN(_hostPresentHeardAt)
                    ? $"{LogNames.HostTag()}'s hasn't shown in the stream here yet"
                    : string.Create(System.Globalization.CultureInfo.InvariantCulture,
                        $"{Math.Abs(_friendPresentAt - _hostPresentHeardAt) * 1000:0} ms {(_friendPresentAt >= _hostPresentHeardAt ? "after" : "before")} {LogNames.HostTag()}'s as the stream shows it here");
                ModLog.Info(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                    $"Match intro: this PC's team presentation plays, {(double.IsNaN(_friendGoAt) ? 0 : (_friendPresentAt - _friendGoAt) * 1000):0} ms after its intro started; {versus}."));
            }
        }
        catch (Exception ex)
        {
            Failed("checking the held intro", ex);
        }
    }

    /// <summary>
    /// The anthem's start (<c>NewNationalAnthem.OnAllSkatersInitialized</c>): held on a friend while armed, until the host's
    /// intro runs. The mod's own call lets it through.
    /// </summary>
    private static bool StartPrefix(NewNationalAnthem __instance)
    {
        if (_letThrough || !_armed)
        {
            return true;
        }

        try
        {
            IntroOpen reason = Gate.Hold(FrameClock.Micros);
            if (reason != IntroOpen.None)
            {
                if (double.IsNaN(_friendGoAt))
                {
                    _friendGoAt = FrameClock.Seconds;
                    ModLog.Info($"Match intro: {Explain(reason)}; this PC's starts at once.");
                }

                return true;
            }

            bool first = _held == null;
            _held = __instance;
            if (first)
            {
                ModLog.Info($"Match intro: this PC's intro waits for {LogNames.HostTag()}'s (their game holds its match until every friend has loaded it).");
            }

            return false;
        }
        catch (Exception ex)
        {
            // Never leave the intro unstarted: the game's own call goes on.
            Failed("holding the intro", ex);
            return true;
        }
    }

    /// <summary>Starts the held intro with the game's own call, once. Never throws.</summary>
    private static void Open(string why)
    {
        NewNationalAnthem? anthem = _held;
        _held = null;
        _friendGoAt = FrameClock.Seconds;
        string held = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"held {Gate.HeldMicros / 1000.0:0} ms");
        if (anthem is null || anthem.WasCollected || anthem == null)
        {
            ModLog.Info($"Match intro: {why} ({held}); the anthem it held is gone (the match left), so nothing starts.");
            return;
        }

        try
        {
            _letThrough = true;
            EventChannel.ExpectLocalRaise(AnthemResetEvent);
            anthem.OnAllSkatersInitialized();
            ModLog.Info($"Match intro: {why}; this PC's intro starts now ({held}).");
        }
        catch (Exception ex)
        {
            Failed("starting the held intro", ex);
        }
        finally
        {
            _letThrough = false;
        }
    }

    private static string Explain(IntroOpen reason) => reason switch
    {
        IntroOpen.HostIntroRuns => $"{LogNames.HostTag()}'s intro runs",
        IntroOpen.HostPastIntro => $"{LogNames.HostTag()}'s intro was over when this PC's match loaded (joined late)",
        IntroOpen.HostSaysNothing => $"{LogNames.HostTag()}'s HUD values never said when its intro runs (an older version)",
        IntroOpen.NoHostValues => $"no HUD values from {LogNames.HostTag()} for a while ({IntroGate.SilenceMicros / 1_000_000} s, or {IntroGate.FirstValuesMicros / 1_000_000} s before the first)",
        IntroOpen.TimedOut => $"held {IntroGate.MaxHoldMicros / 1_000_000} s without {LogNames.HostTag()}'s intro",
        _ => "released",
    };

    // ---------------------------------------------------------------- the Canada curse pick (gate C)

    /// <summary>
    /// Friend: the anthem asks for the Canada curse pick (<c>CanadaCursePicker.Run</c>, the host's): an enumerator the
    /// anthem's sequence runs until the host's pick is over, or null when this PC doesn't follow a host's intro (the empty
    /// one then, as before). The pick's load cover is taken off when the wait ends.
    /// </summary>
    public static Il2CppSystem.Collections.IEnumerator? CanadaRun()
    {
        if (!_armed)
        {
            return null;
        }

        Canada.Start(FrameClock.Micros);
        _canadaPredicate ??= CanadaDone;
        _canadaDelegate = DelegateSupport.ConvertDelegate<Il2CppSystem.Func<bool>>(_canadaPredicate)
            ?? throw new InvalidOperationException("the wait's predicate couldn't be made");
        ModLog.Info($"Match intro: the Canada curse pick is {LogNames.HostTag()}'s; this PC's intro waits for it (at most {CanadaWait.MaxWaitMicros / 1_000_000} s).");
        return new UnityEngine.WaitUntil(_canadaDelegate).Cast<Il2CppSystem.Collections.IEnumerator>();
    }

    /// <summary>The wait's predicate, polled by the game every frame: true once this PC's intro may go on. Never throws.</summary>
    private static bool CanadaDone()
    {
        try
        {
            CanadaWaitEnd end = Canada.Check(FrameClock.Micros, _armed);
            if (end == CanadaWaitEnd.None)
            {
                return !Canada.Waiting;
            }

            CanadaCursePicker.RemoveLoadCover();
            string why = end switch
            {
                CanadaWaitEnd.HostPicked => $"{LogNames.HostTag()}'s Canada curse pick is over",
                CanadaWaitEnd.HostPastPick => $"{LogNames.HostTag()}'s intro is past the Canada curse pick",
                CanadaWaitEnd.TimedOut => $"waited {CanadaWait.MaxWaitMicros / 1_000_000} s for {LogNames.HostTag()}'s Canada curse pick",
                _ => "the session ended during the Canada curse pick",
            };
            ModLog.Info($"Match intro: {why}; this PC's intro goes on (the pick's load cover comes off, as the game's own pick takes it off at its end).");
            return true;
        }
        catch (Exception ex)
        {
            Failed("waiting for the host's Canada pick", ex);
            return true;
        }
    }

    /// <summary>Logs the first few failures of each kind.</summary>
    private static void Failed(string what, Exception ex)
    {
        if (Errors.Allow(what))
        {
            ModLog.Error($"Match intro: {what} failed", ex);
        }
    }
}
#endif
