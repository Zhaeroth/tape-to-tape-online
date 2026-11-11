#if HAS_INTEROP
using System;
using System.Collections.Generic;
using System.Globalization;
using Il2Cpp;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppState.GolferOutro;
using Il2CppTape2Tape.Customization.UI;
using Il2CppTape2Tape.Speech;
using T2TOnline.Core.Campaign;
using T2TOnline.Core.Serialization;
using T2TOnline.Core.Stream;
using T2TOnline.Plugin.Campaign.Menus;
using T2TOnline.Plugin.Diagnostics;
using T2TOnline.Plugin.Streaming;

namespace T2TOnline.Plugin.Match;

/// <summary>
/// The golfer outro (a won campaign match against the Golfers) on every screen. The host's game begins it at the match's end
/// (<c>EndOfGameFlowController.OnEndOfGame</c> → <c>GolferOutroDirector.Begin</c>) and plays it as an end-of-match step
/// (<c>PlayToEnd</c>: a fade to black, the staged celebration with confetti, crowd and music, the skip prompt, then the comic
/// with its photo, strips, dialogue and cup). A friend's end-of-match pipeline is the host's, so its game never played it.
/// <list type="bullet">
/// <item>Host: the HUD carries the outro's phase (<see cref="OutroPhase"/>, null while there's none), its stage seed,
/// whether the celebration was skipped, and the comic's continue presses (counted from the page's waits).</item>
/// <item>Friend: this PC's own director plays (the game's <c>Begin</c> with this match's skip shortcut, then
/// <c>PlayToEnd</c>) when the host's starts, staged with the host's seed (written into the outro's settings for the
/// staging, put back after); the host's skip runs the game's own skip here, each continue press the game's own
/// <c>GolferComicPage.Continue</c> once this PC's comic waits for one (<see cref="OutroSteps"/>); a few seconds after the
/// host's is over, one still running here is ended (its object destroyed: the game's own clean-up). The stream keeps
/// driving the skaters and the camera; this PC's director adds what the stream can't carry.</item>
/// </list>
/// The comic is this player's own (D42 amendment): its strips and cups are this profile's, as the
/// new-unlocks menu shows each player's own. The game marks the strips it showed seen in this PC's profile
/// (<c>RunComicSequence</c> inlines <c>GolferComicProgress.MarkSeen</c> and saves the profile); while following, that save
/// is held by the save guard like every other, and the marks reach the disk with this PC's next profile save that gets
/// through (shared progress at the run's end saves the profile, this PC's own squad and quest counts put back first). The
/// comic's speech bubbles are manual lines (every line's <c>AdvanceMode</c> is Manual in the comic prefab) on the comic
/// page's own <c>SpeechBubbleController</c> (<c>GolferComicPage.m_Speech</c>, anchors <c>GolferComicStrip.m_Speakers</c>):
/// every speaker on both sides is a speaker of the speech mirror (<see cref="SpeechMirror"/>, its outside-menu calls), the
/// host's speech goes as one progress in the HUD (<see cref="PresentationKeys.OutroSpeech"/>), and each friend's own dialogue,
/// which may be another one, follows it line for line and runs out when the host's is over (<see cref="ComicSpeech"/>), so
/// a friend's comic never stalls on a line. The continue presses are carried here.
/// </summary>
internal static class GolferOutroFollow
{
    private const int GamePhaseCelebrating = 1;
    private const int GamePhaseComic = 2;
    private const int GamePhaseDone = 3;

    /// <summary>The speech mirror's name for the comic's speakers (speech outside any menu).</summary>
    private const string SpeechContext = "golfer comic";

    private static readonly OutroSteps Steps = new();
    private static readonly ComicPressCounter HostPresses = new();
    private static readonly ComicSpeech Speech = new();
    private static readonly ErrorLogLimit Errors = new(3);

    // Host
    private static IntPtr _hostDirector;
    private static GolferComicPage? _hostPage;
    private static double _lastHostPageSearch = double.MinValue;
    private static OutroPhase? _hostLogged;
    private static int _hostSpeakers;
    private static SpeechProgress _hostLines;

    // Friend
    private static OutroPhase? _host;
    private static int _hostSeed;
    private static bool _hostSkip;
    private static int _hostNext;
    private static SpeechProgress _hostSpeech;
    private static bool _hostWaits;
    private static GolferOutroDirector? _director;
    private static GolferComicPage? _page;
    private static double _lastPageSearch = double.MinValue;
    private static IntPtr _lastContinued;
    private static GolferOutroSettings? _seeded;
    private static int _seedBefore;
    private static int _seenBefore = -1;
    private static int _cupsHere;
    private static bool _stagedLogged;
    private static bool _ended;
    private static int _speakers;
    private static bool _ranOutLogged;

    // ---------------------------------------------------------------- host

    /// <summary>Host: a new match's stream starts.</summary>
    public static void HostStart()
    {
        _hostDirector = IntPtr.Zero;
        _hostPage = null;
        _hostLogged = null;
        HostPresses.Reset();
        EndHostSpeech();
        _hostLines = SpeechProgress.None;
    }

    /// <summary>Host, at each regular HUD sample: the outro's values (its phase null while there's none). Never throws.</summary>
    public static void ReadHost(Dictionary<string, NetValue> values)
    {
        try
        {
            GolferOutroDirector? director = GolferOutroDirector.Instance;
            if (director == null)
            {
                values[PresentationKeys.OutroPhase] = NetValue.Null;
                EndHostSpeech();
                return;
            }

            if (director.Pointer != _hostDirector)
            {
                // A new outro: its presses count from 0.
                _hostDirector = director.Pointer;
                _hostPage = null;
                HostPresses.Reset();
                EndHostSpeech();
                _hostLines = SpeechProgress.None;
            }

            int game = (int)director.CurrentPhase;
            UnityEngine.CanvasGroup? blackout = director._blackout;
            OutroPhase phase = game switch
            {
                GamePhaseCelebrating => OutroPhase.Celebrating,
                GamePhaseComic => OutroPhase.Comic,
                GamePhaseDone => OutroPhase.Done,
                // Waiting until PlayToEnd's reveal starts fading to black.
                _ => blackout != null && blackout.alpha > 0f ? OutroPhase.Revealing : OutroPhase.Waiting,
            };
            values[PresentationKeys.OutroPhase] = NetValue.FromInt((int)phase);
            values[PresentationKeys.OutroSeed] = NetValue.FromInt(director._stageSeed);
            values[PresentationKeys.OutroSkip] = NetValue.FromBool(director._skipCelebration);
            values[PresentationKeys.OutroNext] = NetValue.FromInt(CountPresses(director, phase, out bool waits));
            values[PresentationKeys.OutroWaits] = NetValue.FromBool(waits);
            values[PresentationKeys.OutroSpeech] = NetValue.FromInt(HostSpeech(phase));
            if (_hostLogged != phase)
            {
                _hostLogged = phase;
                ModLog.Info($"Golfer outro: here it's {Name(phase)} ({(phase >= OutroPhase.Celebrating ? $"stage seed {director._stageSeed}, " : string.Empty)}" +
                    $"{(director._skipCelebration ? "celebration skipped, " : string.Empty)}{HostPresses.Count} comic page(s) moved on); friends' games follow it (HUD outro.*).");
            }
        }
        catch (Exception ex)
        {
            Failed("reading the host's golfer outro", ex);
        }
    }

    /// <summary>
    /// Host: the comic's continue presses so far (a wait of its page seen completed counts once), and whether its page waits
    /// for one now (<paramref name="waits"/>). The page's speakers begin in the speech mirror when the page is first seen,
    /// before its first line (its photo and strips come first).
    /// </summary>
    private static int CountPresses(GolferOutroDirector director, OutroPhase phase, out bool waits)
    {
        waits = false;
        if (phase != OutroPhase.Comic)
        {
            return HostPresses.Count;
        }

        if (_hostPage == null && FrameClock.Seconds - _lastHostPageSearch >= 0.25)
        {
            _lastHostPageSearch = FrameClock.Seconds;
            _hostPage = PageOf(director);
            if (_hostPage != null && _hostSpeakers == 0)
            {
                _hostSpeakers = BeginSpeakers(_hostPage, host: true);
                ModLog.Info($"Golfer outro: the comic's {_hostSpeakers} speaker(s) go to friends' comics (HUD {PresentationKeys.OutroSpeech}).");
            }
        }

        Il2CppCysharp.Threading.Tasks.UniTaskCompletionSource? wait = _hostPage != null ? _hostPage._continue : null;
        waits = wait != null && wait.intStatus == 0;
        return HostPresses.Sample(wait == null ? 0 : wait.Pointer.ToInt64(), wait != null && wait.intStatus != 0);
    }

    /// <summary>
    /// The comic page under the director, or null. The non-generic overload: it's in this build for certain, a
    /// generic instance may not be.
    /// </summary>
    private static GolferComicPage? PageOf(GolferOutroDirector director)
    {
        GolferComicPage? page = director.GetComponentInChildren(Il2CppType.Of<GolferComicPage>(), true)?.TryCast<GolferComicPage>();
        return page != null ? page : null;
    }

    /// <summary>Host: the comic's speech as one progress, for friends (it never goes back: its speakers go with the page).</summary>
    private static int HostSpeech(OutroPhase phase)
    {
        if (_hostSpeakers > 0)
        {
            Span<int> speakers = stackalloc int[SpeechProgress.MaxSpeakers];
            for (int i = 0; i < _hostSpeakers; i++)
            {
                speakers[i] = SpeechMirror.HostValue(SpeechContext, i);
            }

            SpeechProgress lines = ComicSpeech.Combine(speakers[.._hostSpeakers]);
            if (lines.Key > _hostLines.Key || (lines.Key == _hostLines.Key && lines.Stage > _hostLines.Stage))
            {
                _hostLines = lines;
            }

            if (phase == OutroPhase.Done)
            {
                EndHostSpeech();
            }
        }

        return _hostLines.Value;
    }

    /// <summary>Host: the comic's speakers leave the speech mirror (each with its summary).</summary>
    private static void EndHostSpeech()
    {
        if (_hostSpeakers > 0)
        {
            _hostSpeakers = 0;
            SpeechMirror.HostEnd(SpeechContext);
        }
    }

    /// <summary>
    /// Every speaker of the page's strips begins in the speech mirror, each in a slot of its own, on the page's own speech
    /// controller (only the strip in focus speaks, and on a friend that can be another strip than the host's: the lines are
    /// counted whoever speaks, <see cref="ComicSpeech"/>). The comic prefab's three strips have 7 speakers in all (asset:
    /// <c>m_Speakers</c> 2, 2, 3). Returns how many began.
    /// </summary>
    private static int BeginSpeakers(GolferComicPage page, bool host)
    {
        SpeechBubbleController? speech = page.m_Speech;
        Il2CppReferenceArray<GolferComicStrip>? strips = page.m_Strips;
        if (speech == null || strips == null)
        {
            ModLog.Warn($"Golfer outro: this comic page has no speech bubbles or strips; {(host ? "friends' comics go on when this one's over" : "its lines can't follow the host's")}.");
            return 0;
        }

        int slot = 0;
        for (int i = 0; i < strips.Length; i++)
        {
            Il2CppReferenceArray<SpeechBubbleAnchor>? anchors = strips[i] != null ? strips[i].m_Speakers : null;
            for (int j = 0; anchors != null && j < anchors.Length && slot < SpeechProgress.MaxSpeakers; j++)
            {
                SpeechBubbleAnchor anchor = anchors[j];
                if (anchor == null)
                {
                    continue;
                }

                string who = $"strip {i + 1}'s speaker {j + 1}";
                if (host)
                {
                    SpeechMirror.HostBegin(SpeechContext, slot, speech, anchor, who);
                }
                else
                {
                    SpeechMirror.FriendBegin(SpeechContext, slot, speech, anchor, who);
                }

                slot++;
            }
        }

        return slot;
    }

    // ---------------------------------------------------------------- friend

    /// <summary>Friend: playback of a host's match starts: no outro yet.</summary>
    public static void Begin()
    {
        Steps.Reset();
        _host = null;
        _hostSeed = _hostNext = 0;
        _hostSkip = false;
        _hostSpeech = SpeechProgress.None;
        _hostWaits = false;
        Speech.Reset();
        _speakers = 0;
        _ranOutLogged = false;
        _director = null;
        _page = null;
        _lastContinued = IntPtr.Zero;
        _seenBefore = -1;
        _stagedLogged = false;
        _ended = false;
    }

    /// <summary>Friend: the host's values after a HUD message.</summary>
    public static void HostValues(PresentationValues values)
    {
        _host = values.Outro;
        _hostSeed = values.OutroSeed;
        _hostSkip = values.OutroSkip;
        _hostNext = values.OutroNext;
        _hostSpeech = values.OutroSpeech;
        _hostWaits = values.OutroWaits;
    }

    /// <summary>Friend, every frame while playing: follows the host's outro (a few game calls at most a frame). Never throws.</summary>
    public static void Tick()
    {
        if (_ended || (_host == null && !Steps.Started))
        {
            return;
        }

        try
        {
            RestoreSeedOnceStaged();
            for (int i = 0; i < 4; i++)
            {
                bool started = Steps.Started;
                bool localDone = started && LocalDone();
                bool pageWaits = started && !localDone && PageWaits();
                // Asked only when it matters (a playing outro on the host, none here yet): a few game calls.
                bool canStart = !started && _host is (OutroPhase.Revealing or OutroPhase.Celebrating or OutroPhase.Comic) && CanStart();
                OutroAction action = Steps.Next(_host, _hostSkip, _hostNext, localDone, pageWaits, canStart, FrameClock.Micros);
                if (action == OutroAction.None)
                {
                    break;
                }

                Do(action);
                Steps.Did(action);
            }

            if (Steps.Finished && !_ended)
            {
                Finish(cutShort: false);
            }
            else if (Steps.Started && !_ended)
            {
                FollowSpeech();
            }
        }
        catch (Exception ex)
        {
            Failed("following the host's golfer outro", ex);
            Finish(cutShort: true);
        }
    }

    /// <summary>Friend: playback ended. The director goes with the match's scene; the outro's settings get their seed back.</summary>
    public static void End(bool matchLoaded)
    {
        if (Steps.Started && !_ended)
        {
            Finish(cutShort: true, why: matchLoaded ? "playback of the host's match ended" : "the match was unloaded");
        }

        RestoreSeed();
        _director = null;
        _page = null;
    }

    /// <summary>A host's match is shown here and this PC's game has no director of its own running.</summary>
    private static bool CanStart() => MatchSetup.HostMatchHere && GolferOutroDirector.Instance == null;

    private static void Do(OutroAction action)
    {
        switch (action)
        {
            case OutroAction.Start:
                Start();
                break;
            case OutroAction.Skip:
                if (Alive(_director))
                {
                    _director!.DebugSkipCelebration();
                    ModLog.Info(_hostSkip
                        ? $"Golfer outro: {LogNames.HostTag()}'s celebration was skipped; skipped here too (the game's own skip)."
                        : $"Golfer outro: {LogNames.HostTag()}'s outro is past its celebration; this PC's goes on to its comic.");
                }

                break;
            case OutroAction.Continue:
                GolferComicPage page = _page!;
                Il2CppCysharp.Threading.Tasks.UniTaskCompletionSource wait = page._continue;
                _lastContinued = wait.Pointer;
                page.Continue();
                ModLog.Info(Steps.Continued < _hostNext
                    ? $"Golfer outro: {LogNames.HostTag()} moved their comic on (page {Steps.Continued + 1}); this PC's moved on too."
                    : $"Golfer outro: {LogNames.HostTag()}'s outro is over with no more presses; this PC's comic goes on to its own end.");
                break;
            case OutroAction.CutShort:
                Finish(cutShort: true, why: $"{LogNames.HostTag()}'s outro is over and this PC's was still {LocalPhaseName()} {OutroSteps.GraceMicros / 1_000_000} s later");
                break;
        }
    }

    /// <summary>Begins this PC's own outro: the game's Begin and PlayToEnd, staged with the host's seed.</summary>
    private static void Start()
    {
        try
        {
            ProfileData? profile = ProfileData.Instance;
            _seenBefore = profile != null ? profile.golferComicStripsSeen : -1;
            UIInputShortcutBase? skip = SkipShortcut();
            GolferOutroDirector.Begin(skip);
            GolferOutroDirector? director = GolferOutroDirector.Instance;
            if (director == null)
            {
                ModLog.Warn("Golfer outro: couldn't start here (the game's Begin made no director); this PC shows the stream's staging only.");
                _ended = true;
                return;
            }

            _director = director;
            _cupsHere = director._cupsBeforeWin;
            SeedBeforeStaging();
            director.PlayToEnd();
            ModLog.Info($"Golfer outro: {LogNames.HostTag()}'s outro {(Steps.FirstSeen is OutroPhase first && first > OutroPhase.Revealing ? $"is under way ({Name(first)})" : "started")}; " +
                $"this PC plays the game's own (this profile's {_cupsHere} cup(s) before this win" +
                $"{(skip == null ? "; no skip shortcut found, the host's skip still applies" : string.Empty)}).");
        }
        catch (Exception ex)
        {
            Failed("starting this PC's golfer outro", ex);
            RestoreSeed();
            _ended = true;
            if (Alive(_director))
            {
                // A director that couldn't play goes: it would only wait here.
                UnityEngine.Object.Destroy(_director!.gameObject);
            }

            _director = null;
        }
    }

    /// <summary>This match's skip shortcut, as the end-of-match pipeline passes it (<c>ArenaComponents.PenaltyEscortSkipShortcut</c>).</summary>
    private static UIInputShortcutBase? SkipShortcut()
    {
        HockeyGameplayDependenciesInstaller? installer = UnityEngine.Object.FindObjectOfType<HockeyGameplayDependenciesInstaller>();
        UIInputShortcutBase? shortcut = installer != null ? installer.penaltyEscortSkipShortcut : null;
        return shortcut != null ? shortcut : null;
    }

    /// <summary>True once this PC's outro is over (its phase Done) or its director has gone.</summary>
    private static bool LocalDone() => !Alive(_director) || (int)_director!.CurrentPhase == GamePhaseDone;

    /// <summary>True while this PC's comic waits for a continue it hasn't had from the host.</summary>
    private static bool PageWaits()
    {
        if (!Alive(_director) || (int)_director!.CurrentPhase != GamePhaseComic)
        {
            return false;
        }

        if (!Alive(_page) && FrameClock.Seconds - _lastPageSearch >= 0.25)
        {
            _lastPageSearch = FrameClock.Seconds;
            _page = PageOf(_director);
        }

        Il2CppCysharp.Threading.Tasks.UniTaskCompletionSource? wait = Alive(_page) ? _page!._continue : null;
        return wait != null && wait.intStatus == 0 && wait.Pointer != _lastContinued;
    }

    /// <summary>
    /// Friend, every frame of its comic: each speaker follows the host's speech, line for line whoever speaks
    /// (<see cref="ComicSpeech"/>). The speech mirror reads this PC's bubbles and presses them with the game's own skip; a
    /// value given before a speaker's begin is lost, so the newest goes every frame.
    /// </summary>
    private static void FollowSpeech()
    {
        if (!Alive(_page))
        {
            return;
        }

        if (_speakers == 0)
        {
            _speakers = BeginSpeakers(_page!, host: false);
            if (_speakers == 0)
            {
                _speakers = -1; // said once; the continue and the cut-short still end it
            }
            else
            {
                ModLog.Info($"Golfer outro: this PC's comic's {_speakers} speaker(s) follow {LogNames.HostTag()}'s presses, line for line (its dialogue is this profile's own).");
            }
        }

        if (_speakers < 0)
        {
            return;
        }

        int own = 0;
        for (int slot = 0; slot < _speakers; slot++)
        {
            own += SpeechMirror.FriendLines(SpeechContext, slot);
        }

        ComicHost state = _host is OutroPhase.Done or null || _hostNext > 0 ? ComicHost.MovedOn : _hostWaits ? ComicHost.Waits : ComicHost.Speaking;
        SpeechStage stage = Speech.StageFor(own, _hostSpeech, state, FrameClock.Seconds);
        for (int slot = 0; slot < _speakers; slot++)
        {
            int key = SpeechMirror.FriendLines(SpeechContext, slot);
            SpeechMirror.FriendHostValue(SpeechContext, slot, key == 0 ? 0 : SpeechProgress.Line(key, stage).Value);
        }

        if (Speech.RanOut > 0 && !_ranOutLogged)
        {
            _ranOutLogged = true;
            ModLog.Info($"Golfer outro: this PC's comic has more lines than {LogNames.HostTag()}'s (line {own} here, {_hostSpeech} there); they go on " +
                (state == ComicHost.MovedOn ? "now, as their comic has moved on." : string.Create(CultureInfo.InvariantCulture, $"at a reading pace ({ComicSpeech.ExtraLineSeconds:0.0} s a line) while their page waits.")));
        }
    }

    /// <summary>
    /// Until this PC's celebration is staged (its fade to black first), the host's stage seed goes into the outro's settings,
    /// which its <c>Stage</c> reads: the host's is only known once the host has staged (a moment before this PC does).
    /// </summary>
    private static void SeedBeforeStaging()
    {
        if (_hostSeed == 0 || !Alive(_director) || (int)_director!.CurrentPhase >= GamePhaseCelebrating)
        {
            return;
        }

        GolferOutroSettings? settings = _seeded ?? _director._settings;
        if (settings == null || settings.seed == _hostSeed)
        {
            return;
        }

        if (_seeded == null)
        {
            _seeded = settings;
            _seedBefore = settings.seed;
        }

        settings.seed = _hostSeed;
    }

    /// <summary>The seed was written for the staging only: put back once this PC's celebration is staged (logged once).</summary>
    private static void RestoreSeedOnceStaged()
    {
        if (!_stagedLogged && Alive(_director) && (int)_director!.CurrentPhase >= GamePhaseCelebrating)
        {
            _stagedLogged = true;
            int seed = _director._stageSeed;
            ModLog.Info(_hostSeed != 0 && seed == _hostSeed
                ? $"Golfer outro: this PC's celebration is staged with {LogNames.HostTag()}'s seed ({seed}), as on their screen."
                : $"Golfer outro: this PC's celebration is staged with its own seed ({seed}; {LogNames.HostTag()}'s {(_hostSeed == 0 ? "hadn't come" : $"was {_hostSeed}")}); the stream still shows their skaters and camera.");
            RestoreSeed();
        }
        else
        {
            SeedBeforeStaging();
        }
    }

    private static void RestoreSeed()
    {
        GolferOutroSettings? settings = _seeded;
        _seeded = null;
        try
        {
            if (settings is not null && !settings.WasCollected && settings != null)
            {
                settings.seed = _seedBefore;
            }
        }
        catch (Exception ex)
        {
            Failed("putting the outro settings' seed back", ex);
        }
    }

    /// <summary>This PC's outro is over (by itself, or ended here). Logged once, with what this profile's comic marked.</summary>
    private static void Finish(bool cutShort, string? why = null)
    {
        if (_ended)
        {
            return;
        }

        _ended = true;
        RestoreSeed();
        if (_speakers > 0)
        {
            _speakers = 0;
            SpeechMirror.FriendEnd(SpeechContext);
        }

        if (cutShort && Alive(_director))
        {
            try
            {
                UnityEngine.Object.Destroy(_director!.gameObject);
            }
            catch (Exception ex)
            {
                Failed("ending this PC's golfer outro", ex);
            }
        }

        int seen = ProfileData.Instance is ProfileData profile && profile != null ? profile.golferComicStripsSeen : -1;
        string strips = _seenBefore < 0 || seen < 0 ? "this profile's comic marks not read"
            : seen == _seenBefore ? "no new comic strip marked seen on this profile"
            : $"comic strips now marked seen on this profile: {Strips(seen)} (were {Strips(_seenBefore)}; the game's own save of them is held while following, " +
              "so they're written with this PC's next profile save that gets through: shared progress at the run's end)";
        ModLog.Info($"Golfer outro: {(cutShort ? $"ended here ({why ?? "cut short"})" : "done here, in step")}; {Steps.Continued} comic page(s) moved on for {LogNames.HostTag()}" +
            $"{(Speech.RanOut > 0 ? $", {Speech.RanOut} line(s) of this PC's dialogue went on after theirs" : string.Empty)}; {strips}.");
        _director = null;
        _page = null;
    }

    private static string Strips(int bits)
    {
        var list = new List<string>();
        for (int i = 0; i < 31; i++)
        {
            if ((bits & (1 << i)) != 0)
            {
                list.Add((i + 1).ToString(CultureInfo.InvariantCulture));
            }
        }

        return list.Count == 0 ? "none" : string.Join(", ", list);
    }

    private static string LocalPhaseName() =>
        !Alive(_director) ? "gone" : (int)_director!.CurrentPhase switch
        {
            GamePhaseCelebrating => "celebrating",
            GamePhaseComic => "in its comic",
            GamePhaseDone => "done",
            _ => "revealing",
        };

    private static string Name(OutroPhase phase) => phase switch
    {
        OutroPhase.Waiting => "begun, waiting for its step",
        OutroPhase.Revealing => "revealing",
        OutroPhase.Celebrating => "celebrating",
        OutroPhase.Comic => "in its comic",
        _ => "done",
    };


    /// <summary>True for a live Unity object (not collected, not destroyed).</summary>
    private static bool Alive(UnityEngine.Object? value) => value is not null && !value.WasCollected && value != null;

    /// <summary>Logs the first few failures of each kind.</summary>
    private static void Failed(string what, Exception ex)
    {
        if (Errors.Allow(what))
        {
            ModLog.Error($"Golfer outro: {what} failed", ex);
        }
    }
}
#endif
