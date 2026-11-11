#if HAS_INTEROP
using System;
using System.Collections.Generic;
using System.Globalization;
using Il2CppBishopGames.UI;
using T2TOnline.Core.Lobby;
using T2TOnline.Plugin.Campaign;
using T2TOnline.Plugin.Campaign.Menus;
using T2TOnline.Plugin.Campaign.Scenes;
using T2TOnline.Plugin.Diagnostics;
using T2TOnline.Plugin.Streaming;

namespace T2TOnline.Plugin.NativeLobby;

// A tour never presses, clicks or calls into a screen it did not mean to be on (Core's TourGate and TourStarts). Every step
// declares where it starts; before a step the tour reads where the game is (TourPlace) and, elsewhere, the step makes no
// press and fails as "can't start". Two can't-starts in a row, a step that must end at the title's Main and doesn't, or the
// game on a screen the tours never open stop the tour: no further step runs, one STOPPED line says why, the tour puts back
// only what needs no press, and the game quits with every hold on (Core's TourEnd). Inside a step the walks gate their own
// presses (_walkOk, and each loop on the screen it is for): a failed expectation the next press depends on (Expect) or a
// press whose screen isn't up (MayPressOrStop) ends the step's presses.
internal static partial class LobbyUiCheck
{
    // How long a step waits for the place it starts from: a transition or a group change settles in this.
    private const double StartWaitSeconds = 8;

    // How long a step that must end at the title's Main waits for it once its body is done (a last group change).
    private const double EndWaitSeconds = 3;

    private static readonly TourGate Gate = new();
    private static bool _stopPending;
    private static string? _stopWhy;

    /// <summary>A new tour: the gate's count and any stop cleared.</summary>
    private static void BeginSafety()
    {
        Gate.Reset();
        _stopPending = false;
        _stopWhy = null;
    }

    /// <summary>
    /// The step's start gate, then the step, then (for a step that must end at the title's Main) its end. A step that can't
    /// start yields nothing of its own: no press, no click, no call.
    /// </summary>
    private static IEnumerable<Wait> Gated(UiCheckStep step, Func<IEnumerable<Wait>> body)
    {
        TourPlace wanted = TourStarts.Of(step.Id);
        TourPlace now = PlaceNow(out string detail);
        if (!Allows(wanted, now))
        {
            double since = FrameClock.Seconds;
            yield return Soft(() => Allows(wanted, PlaceNow(out _)), StartWaitSeconds, $"{TourStarts.Describe(wanted)}, where {step.Id} starts");
            now = PlaceNow(out detail);
            if (Allows(wanted, now))
            {
                StepNote(string.Create(CultureInfo.InvariantCulture, $"{step.Id} starts at {TourStarts.Describe(now)} after {FrameClock.Seconds - since:0.0} s"));
            }
        }

        TourGateVerdict verdict = Gate.Before(step.Id, now, detail);
        if (verdict.Kind != TourGateKind.Run)
        {
            Problem(verdict.Kind == TourGateKind.CantStart ? verdict.Why : $"can't start: {verdict.Why}; nothing was pressed");
            _stopPending = verdict.Kind == TourGateKind.Stop;
            yield break;
        }

        foreach (Wait wait in body())
        {
            yield return wait;
        }

        if (step.Kind == UiCheckStepKind.Finish || _phase != Phase.Running)
        {
            yield break;
        }

        if (TourStarts.EndsAtMain(step.Id) && PlaceNow(out _) != TourPlace.Main)
        {
            yield return Soft(() => PlaceNow(out _) == TourPlace.Main, EndWaitSeconds, "the title's Main, where the step ends");
        }

        now = PlaceNow(out detail);
        if (Gate.After(step.Id, now, detail) != null)
        {
            _stopPending = true;
        }
    }

    /// <summary>After a step's line: a stop the gate asked for turns the tour to its stop's restore.</summary>
    private static bool TakeStop()
    {
        if (!_stopPending || !Gate.Stopped || _phase != Phase.Running)
        {
            return false;
        }

        _stopPending = false;
        (string when, string stepId) = Gate.StoppedAt;
        int at = -1;
        for (int i = 0; i < _tour.Count; i++)
        {
            if (string.Equals(_tour[i].Id, stepId, StringComparison.Ordinal))
            {
                at = i;
                break;
            }
        }

        var notRun = new List<string>();
        for (int i = at < 0 ? _tour.Count : (when == "before" ? at : at + 1); i < _tour.Count; i++)
        {
            notRun.Add(_tour[i].Id);
        }

        _stopWhy = Gate.StopReason;
        string line = TourGate.StoppedLine(when, stepId, _stopWhy ?? "?", notRun);
        Line(line);
        ModLog.Warn($"Lobby check: {line}");
        _failed++;
        Flush();
        _body?.Dispose();
        _body = null;
        _wait = null;
        _step = null;
        _plan = null;
        _phase = Phase.Restoring;
        _body = StopRestore().GetEnumerator();
        return true;
    }

    /// <summary>
    /// A stopped tour's restore: only what needs no press, click or call into a screen. The pad's and the walker's presses let
    /// go, the walker disarmed, the virtual pad and keyboard gone, the language and the window back. The session, the run, the
    /// match and whatever is on screen are left as they are: the game quits with every hold on (Conclude), so none of it
    /// reaches the disk or Steam.
    /// </summary>
    private static IEnumerable<Wait> StopRestore()
    {
        _walk?.LetAll();
        ReleaseTap();
        TourCampaignWalker.Disarm();
        TourCampaignWalker.CurrentPump = null;
        _cpRewardPump = null;
        yield return Frames(2);
        EndWalk();
        EndNativeChecks();
        if (_languageChanged || Il2CppI2.Loc.LocalizationManager.CurrentLanguage != _language)
        {
            foreach (Wait wait in SetLanguage(_language, _languageCode))
            {
                yield return wait;
            }

            _languageChanged = false;
        }

        foreach (Wait wait in RestoreWindow())
        {
            yield return wait;
        }

        if (Il2CppI2.Loc.LocalizationManager.CurrentLanguage != _language)
        {
            Line($"NOT RESTORED: the language is {Il2CppI2.Loc.LocalizationManager.CurrentLanguage}, not {_language}.");
        }
        else
        {
            Line($"Restored after the stop: language {_language}; window {UnityEngine.Screen.width}x{UnityEngine.Screen.height} {UnityEngine.Screen.fullScreenMode}; nothing on screen was touched.");
        }
    }

    /// <summary>True when <paramref name="now"/> is one place, one the start <paramref name="wanted"/> allows.</summary>
    private static bool Allows(TourPlace wanted, TourPlace now) =>
        now is not (TourPlace.None or TourPlace.OtherScreen or TourPlace.Unknown) && (now & (now - 1)) == 0 && (wanted & now) == now;

    /// <summary>
    /// Where the game is now, for the gate: the title's groups with nothing over them, a screen over the title (the tours' own
    /// or not), the lobby (on top, or under a game screen), a match, the world map, or between them. <paramref name="detail"/>
    /// names the menu or scene, for the summary (the game's names, nobody's). Never throws.
    /// </summary>
    private static TourPlace PlaceNow(out string detail)
    {
        detail = string.Empty;
        try
        {
            if (WorldMap.SceneName == WorldMap.TitleScene && CampaignGame.Title() is { } title)
            {
                AbstractMenu? top = Top();
                if (top != null)
                {
                    string id = MenuGame.MenuId(top);
                    detail = MenuGame.Name(id, "a menu");
                    if (LobbyShell.Menu is { } shell && top.Pointer == shell.Pointer)
                    {
                        return LobbyShell.Kind == ShellKind.Lobby ? TourPlace.Lobby : TourPlace.TourScreen;
                    }

                    if (TourStarts.IsTransition(id))
                    {
                        return TourPlace.Loading;
                    }

                    if (LobbyShell.IsOpen && LobbyShell.Kind == ShellKind.Lobby)
                    {
                        return TourPlace.LobbyCovered;
                    }

                    return TourStarts.IsTourScreen(id) || LobbyShell.IsShellId(id) ? TourPlace.TourScreen : TourPlace.OtherScreen;
                }

                if (LobbyShell.IsOpen || !title.m_HasStarted || title.m_IsExiting || title._isTransitioningGroup)
                {
                    return TourPlace.Loading;
                }

                int group = (int)title.m_CurrentButtonGroup;
                return group == 0 ? TourPlace.PressStart : group == 1 ? TourPlace.Main : TourPlace.Group;
            }

            if (WorldMap.MapLoaded)
            {
                return TourPlace.Map;
            }

            if (HudModel.GameManagerInstance() != null && WorldMap.SceneName != WorldMap.TitleScene)
            {
                return TourPlace.Match;
            }

            detail = MenuGame.Name(WorldMap.SceneName, "a scene");
            return TourPlace.Loading;
        }
        catch (Exception ex)
        {
            detail = ex.GetType().Name;
            return TourPlace.Unknown;
        }
    }

    /// <summary>
    /// True when a press may be made: no expectation of the step has failed, and <paramref name="screen"/> (the screen the press
    /// is for) is up. Else nothing is pressed and the step's summary says so, with <paramref name="what"/>.
    /// </summary>
    private static bool MayPress(string what, Func<bool>? screen = null)
    {
        if (!_walkOk)
        {
            StepNote($"{what}: not pressed (an earlier expectation failed)");
            return false;
        }

        bool there;
        try
        {
            there = screen?.Invoke() ?? true;
        }
        catch (Exception)
        {
            there = false;
        }

        if (!there)
        {
            StepNote($"{what}: not pressed (the screen it is for isn't up: the game at {TourStarts.Describe(PlaceNow(out string detail))}{(detail.Length > 0 ? $" ({detail})" : string.Empty)})");
        }

        return there;
    }

    /// <summary>
    /// <see cref="MayPress"/> for a press the rest of the step's walk depends on: when <paramref name="screen"/> isn't up,
    /// the step fails and its presses stop, since the walk has lost its place and a later press would act on whatever is up.
    /// </summary>
    private static bool MayPressOrStop(string what, Func<bool> screen)
    {
        bool going = _walkOk;
        if (MayPress(what, screen))
        {
            return true;
        }

        if (going)
        {
            Fail($"{what}: not pressed: the screen it is for isn't up; {Where()}");
            _walkOk = false;
        }

        return false;
    }

    /// <summary>The lobby on top (the mod's shell, its lobby kind, nothing over it).</summary>
    private static bool LobbyOnTop() => LobbyShell.IsShown && LobbyShell.Kind == ShellKind.Lobby && Top()?.Pointer == LobbyShell.Menu?.Pointer;
}
#endif
