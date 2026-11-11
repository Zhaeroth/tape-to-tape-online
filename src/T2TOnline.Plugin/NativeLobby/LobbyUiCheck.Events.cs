#if HAS_INTEROP
using System;
using System.Collections.Generic;
using Il2CppBishopGames.UI;
using Il2CppRogue.NPC.NurseNPC.Procedures;
using Il2CppTape2Tape.Customization.UI;
using Il2CppTape2Tape.Hockey.UI;
using T2TOnline.Core.Input;
using T2TOnline.Core.Lobby;
using T2TOnline.Plugin.Campaign.Menus;
using T2TOnline.Plugin.Campaign.Scenes;
using T2TOnline.Plugin.Diagnostics;
using T2TOnline.Plugin.Streaming;

namespace T2TOnline.Plugin.NativeLobby;

// The campaign tours' events on demand (events=on; Core's CampaignTour.EventsStep, after cp_first, on the map). The developer's
// event key (Ctrl+F10) can't be pressed by a tour (a human's key press stops it), so its own call (EventDevKey.Open) is made:
// it opens the next kind in its turn (the Coach, three trainings, Chaos, the Blademaster, the General Manager, Fan Number 1,
// the Nurse) the game's own way, as a visit to its node would, with no map node: its pick applies to the run (the tour's own
// run file), and the run's next save carries it (Run sync's "team changed" line then names what changed).
// Each event is driven by the tour's pad as a player would: A skips the NPC's intro and picks the card under the cursor, the
// d-pad moves to the next card when a pick changes nothing, a game yes/no prompt is answered Yes (as the game's own walker
// answers every prompt), a reward menu is the armed reward coroutine's (Watch); on the Nurse's procedure screen, which takes
// the event screen over, A on the focused option and the d-pad to the next when an A moved nothing (Core's
// CampaignTour.TakeoverPress; the screen's own back does nothing at its first stage and at its result, so a takeover is left
// only by its end). The campaign walk's takeovers (LobbyUiCheck.Takeover.cs) are driven here too. When six presses in a row
// change nothing on the event screen itself, its skip shortcut, held (the screen's own way out with no pick: B does nothing
// there); when that changes nothing either, or a screen the step doesn't press on comes up (a match, the map's menu, a
// prompt of the mod's held too long), the tour stops after the step with no press on it. Only skater counts and the event's
// class are logged.
internal static partial class LobbyUiCheck
{
    private const double EventPressSettleSeconds = 0.7;
    private const int EventBackPresses = 4;

    private static CampaignEventEnd _cpEventEnd;
    private static string _cpEventOther = string.Empty;
    private static int _cpEventPresses;
    private static int _cpEventChoices;
    private static int _cpEventStages;

    private static IEnumerable<Wait> StepCampaignEvents()
    {
        if (!CampaignGoesOn())
        {
            yield break;
        }

        if (_cpRunEnded || _walk == null)
        {
            Problem(_walk == null ? $"{CampaignTour.EventsKey}=on: not tried: the tour's pad is gone" : "the run has ended: no map to open events on");
            yield break;
        }

        HashSet<string>? first = SkaterIds();
        int opened = 0, closed = 0, byBack = 0, signings = 0, losses = 0, called = 0;
        for (int number = 1; number <= CampaignTour.EventKinds.Count && !Stopping; number++)
        {
            // Where the key opens one (its own blocker reads the same): the map loaded, nothing over it, its cursor on the map.
            yield return Soft(() => { Watch(); return EventKeyReady(); }, 20, "the map free, its cursor on the map");
            if (!EventKeyReady())
            {
                if (MapFree())
                {
                    // Nothing over the map, only its cursor elsewhere (the key's own blocker): no more events, the map as the next step finds it.
                    Problem($"{CampaignTour.EventsKey}=on: the map's cursor isn't on one of its points for event {number}: the key opens nothing then; the step ends here");
                    break;
                }

                StopAfterThisStep($"{CampaignTour.EventsKey}=on: the map isn't free for event {number} ({MenuOverMap() ?? $"the scene {MenuGame.Name(WorldMap.SceneName, "a scene")}"}); nothing more is pressed");
                yield break;
            }

            HashSet<string>? before = SkaterIds();
            double started = FrameClock.Seconds;
            called++;
            try
            {
                EventDevKey.Open();
            }
            catch (Exception ex)
            {
                Problem($"{CampaignTour.EventsKey}=on: the key's call threw ({ModLog.SafeErrorText(ex)})");
                yield break;
            }

            EventChoiceMenu? menu = null;
            yield return Soft(() => { Watch(); return (menu = Top()?.TryCast<EventChoiceMenu>()) != null; }, 5, "the event screen");
            if (menu == null || menu.WasCollected)
            {
                StepNote(CampaignTour.EventLine(number, null, CampaignEventEnd.NotOpened, FrameClock.Seconds - started, 0, 0, before?.Count ?? -1, before?.Count ?? -1));
                continue; // a screen of another kind that came up instead is the next event's readiness check's to stop on
            }

            opened++;
            string eventClass = EventClass(menu);
            yield return Seconds(1.2); // its NPC's entrance
            foreach (Wait wait in Snap($"event_{number}", 0))
            {
                yield return wait;
            }

            foreach (Wait wait in DriveEvent(menu))
            {
                yield return wait;
            }

            HashSet<string>? after = SkaterIds();
            (int added, int removed) = Changes(before, after);
            signings += added;
            losses += removed;
            StepNote(CampaignTour.EventLine(number, eventClass, _cpEventEnd, FrameClock.Seconds - started, _cpEventPresses, _cpEventChoices, before?.Count ?? -1, after?.Count ?? -1));
            if (_cpEventEnd is CampaignEventEnd.Closed or CampaignEventEnd.LeftByBack)
            {
                closed++;
                byBack += _cpEventEnd == CampaignEventEnd.LeftByBack ? 1 : 0;
                continue;
            }

            StopAfterThisStep(_cpEventEnd == CampaignEventEnd.Stuck
                ? $"{CampaignTour.EventsKey}=on: the pad couldn't leave event {number} ({CampaignTour.EventClassText(eventClass)}); nothing more is pressed"
                : $"{CampaignTour.EventsKey}=on: during event {number} ({CampaignTour.EventClassText(eventClass)}) {_cpEventOther} came up, which the step doesn't press on");
            yield break;
        }

        HashSet<string>? last = SkaterIds();
        StepNote(CampaignTour.EventsLine(called, opened, closed, byBack, first?.Count ?? -1, last?.Count ?? -1, signings, losses));
        StepNote("campaign: events=on: the picks are in the tour's run; the run's next save carries them (Run sync's team changed line names the skaters)");
        if (CampaignTour.EventsProblem(opened, closed, signings, losses) is { } problem)
        {
            Problem(problem);
        }

        // A screen the key opened after its wait (the last kind's has no next readiness check to stop on) is never pressed on.
        if (!Stopping && !MapFree())
        {
            yield return Soft(() => { Watch(); return MapFree(); }, 3, "the map free after the events");
            if (!MapFree())
            {
                StopAfterThisStep($"{CampaignTour.EventsKey}=on: the map isn't free after the events ({MenuOverMap() ?? $"the scene {MenuGame.Name(WorldMap.SceneName, "a scene")}"}); nothing more is pressed");
                yield break;
            }
        }

        // The map's own input ready again, as cp_continue finds it after cp_first.
        yield return Soft(() => { Watch(); return MapStartHeldNow() == null; }, MapInputSeconds, "the map's own input ready again");
    }

    /// <summary>
    /// One event, from its screen to the map free again, by the pad. Sets <see cref="_cpEventEnd"/> (and <see cref="_cpEventOther"/>
    /// for a screen it doesn't press on), the presses made, the choices the screen counted and the takeover's stages moved on.
    /// <paramref name="leave"/>: the event screen is to be left with no pick (cp_end's way to the map's menu): B from the first
    /// press; a takeover, which its own back can't leave, is still driven to its end.
    /// </summary>
    private static IEnumerable<Wait> DriveEvent(EventChoiceMenu menu, bool leave = false)
    {
        _cpEventEnd = CampaignEventEnd.Stuck;
        _cpEventOther = string.Empty;
        _cpEventPresses = _cpEventChoices = _cpEventStages = 0;
        double started = FrameClock.Seconds;
        double modPromptSince = -1;
        double freeSince = -1;
        int noEffect = 0;
        int sinceProgress = 0;
        bool back = leave;
        bool leftNoPick = false; // a press of the way out with no pick was made (not a takeover driven to its end)
        string signature = EventSignature(menu);
        (string Key, int Stage) progress = TakeoverProgress(menu);
        while (!Stopping)
        {
            Watch();
            if (!menu.WasCollected)
            {
                _cpEventChoices = Math.Max(_cpEventChoices, menu.m_ChoicesMade);
            }

            if (FrameClock.Seconds - started > CampaignTour.EventSeconds)
            {
                _cpEventEnd = CampaignEventEnd.Stuck;
                yield break;
            }

            if (!WorldMap.MapLoaded)
            {
                _cpEventEnd = CampaignEventEnd.OtherScreen;
                _cpEventOther = $"a scene load ({MenuGame.Name(WorldMap.SceneName, "a scene")})";
                yield break;
            }

            if (TourHolds.ModPromptOpen)
            {
                modPromptSince = modPromptSince < 0 ? FrameClock.Seconds : modPromptSince;
                if (FrameClock.Seconds - modPromptSince > ModPromptSeconds)
                {
                    _cpEventEnd = CampaignEventEnd.OtherScreen;
                    _cpEventOther = "a prompt of the mod's";
                    yield break;
                }

                yield return Seconds(0.2);
                continue;
            }

            modPromptSince = -1;
            AbstractMenu? top = Top();
            if (top == null)
            {
                // Nothing shown over the map for a moment, its cursor on the map again: the event is over (between two of its
                // screens nothing is shown only for a frame or two).
                freeSince = freeSince < 0 ? FrameClock.Seconds : freeSince;
                double free = FrameClock.Seconds - freeSince;
                if ((free >= 0.6 && EventKeyReady()) || (free >= 2 && MapFree()))
                {
                    _cpEventEnd = leftNoPick ? CampaignEventEnd.LeftByBack : CampaignEventEnd.Closed;
                    yield break;
                }

                yield return Seconds(0.2);
                continue;
            }

            freeSince = -1;

            string id = MenuGame.MenuId(top);
            if (TourStarts.IsTransition(id) || top.TryCast<SkaterPowerupRewardMenu>() != null || top.TryCast<RelicRewardMenu>() != null)
            {
                yield return Seconds(0.3); // a transition goes by itself; a reward menu is the armed reward coroutine's (Watch)
                continue;
            }

            bool takeover = top.TryCast<EventChoiceMenu>() is { m_IsTakenOver: true };
            PadButtons? button = EventPress(top, back, noEffect, sinceProgress);
            if (button == null)
            {
                _cpEventEnd = CampaignEventEnd.OtherScreen;
                _cpEventOther = MenuGame.Name(id, "a menu");
                yield break;
            }

            if (button == PadButtons.None)
            {
                yield return Seconds(0.2); // the screen is busy (its pick's animation, its exit): no press now
                continue;
            }

            // A press on the NPC's intro skips it or waits it out: it never counts as a press that changed nothing.
            bool introPress = top.TryCast<EventChoiceMenu>() is { m_IsPlayingIntro: true };
            AbstractMenu pressed = top;
            if (top.TryCast<PromptTemplateMenu>() is { } prompt)
            {
                foreach (Wait wait in AnswerGamePromptYes(prompt))
                {
                    yield return wait;
                }
            }
            else if (MayPress($"{button} on {MenuGame.Name(id, "the event's screen")}", () => !pressed.WasCollected && Top()?.Pointer == pressed.Pointer))
            {
                // The way out with no pick is the event screen's skip shortcut (its back handler does nothing), held as long as it asks.
                double hold = 0;
                if (back && !takeover && !introPress)
                {
                    leftNoPick = true;
                    if (top.TryCast<EventChoiceMenu>() is { } skipped)
                    {
                        SkipPress(skipped, out hold);
                    }
                }

                if (hold > TapSeconds && _keys == null && _walk != null)
                {
                    _walk.Hold(button.Value);
                    yield return Seconds(hold);
                    _walk.Let(button.Value);
                    yield return Frames(2);
                }
                else
                {
                    foreach (Wait wait in Tap(button.Value))
                    {
                        yield return wait;
                    }
                }
            }

            _cpEventPresses++;
            yield return Seconds(introPress ? 1.5 : EventPressSettleSeconds);
            if (takeover)
            {
                // The takeover's own screen: its stage moving on (or its commit, its close) is progress; a focus move isn't.
                (string Key, int Stage) after = TakeoverProgress(menu);
                _cpEventStages += after.Stage != progress.Stage && after.Stage >= 0 ? 1 : 0;
                sinceProgress = after.Key != progress.Key ? 0 : sinceProgress + 1;
                progress = after;
                noEffect = 0;
                signature = menu.WasCollected ? "gone" : EventSignature(menu);
                continue;
            }

            string now = menu.WasCollected ? "gone" : EventSignature(menu);
            noEffect = now != signature ? 0 : introPress ? noEffect : noEffect + 1;
            signature = now;
            if (!back && noEffect >= CampaignTour.EventNoEffectPresses)
            {
                back = true;
                noEffect = 0;
                StepNote($"campaign: {CampaignTour.EventsKey}=on: {CampaignTour.EventNoEffectPresses} presses changed nothing: its skip, the screen's own way out with no pick");
            }
            else if (back && noEffect >= EventBackPresses)
            {
                _cpEventEnd = CampaignEventEnd.Stuck;
                yield break;
            }
        }
    }

    /// <summary>
    /// The pad's next press on <paramref name="top"/> during an event: on a takeover's screen (the Nurse's), the press
    /// <see cref="CampaignTour.TakeoverPress"/> gives <paramref name="sinceProgress"/> presses after its stage last moved on (none
    /// while it takes no input); the screen's skip once the picks gave up (<paramref name="back"/>; B on its intro); A on the
    /// intro or a card (right to the next card when A changed nothing); A on a game yes/no prompt (answered Yes by its own
    /// cursor); <see cref="PadButtons.None"/> while the screen is busy; null for a screen the step doesn't press on.
    /// </summary>
    private static PadButtons? EventPress(AbstractMenu top, bool back, int noEffect, int sinceProgress)
    {
        if (top.TryCast<EventChoiceMenu>() is { } choices)
        {
            if (choices.m_IsExiting || choices.m_IsSwappingChoices || choices.m_IsInstantiatingMenu)
            {
                return PadButtons.None;
            }

            if (choices.m_IsTakenOver)
            {
                return TakeoverTakesInput(choices) ? CampaignTour.TakeoverPress(sinceProgress) : PadButtons.None;
            }

            if (choices.m_IsPlayingIntro)
            {
                // The intro takes A or B (the screen's own intro inputs), and its skip shortcut not at all; B picks nothing after it.
                return back ? PadButtons.B : PadButtons.A;
            }

            if (back)
            {
                return SkipPress(choices, out _);
            }

            bool onCard = WorldMap.Navigation?.CurrentNavigable is { } at && EventCapture.ChoiceOf(choices, at) >= 0;
            return !onCard || noEffect % 2 == 1 ? PadButtons.DPadRight : PadButtons.A;
        }

        if (top.TryCast<PromptTemplateMenu>() != null)
        {
            return PadButtons.A; // answered Yes (AnswerGamePromptYes)
        }

        if (top.TryCast<OldSquadMenu>() != null || top.TryCast<BenchwarmerMenu>() != null)
        {
            return back ? PadButtons.B : noEffect % 2 == 1 ? PadButtons.Start : PadButtons.A;
        }

        return null;
    }

    /// <summary>A game yes/no prompt over an event: the cursor to its Yes by the d-pad, then A (as the concede's prompt is answered).</summary>
    private static IEnumerable<Wait> AnswerGamePromptYes(PromptTemplateMenu prompt)
    {
        yield return Seconds(0.6); // its intro
        AbstractNavigable? yes = prompt.WasCollected ? null : prompt.GetTargetNavigable(PromptTemplateMenu.PromptTemplateResult.Yes);
        bool PromptUp() => !prompt.WasCollected && Top()?.Pointer == prompt.Pointer;
        if (yes != null)
        {
            foreach (Wait wait in CursorTo(yes, "the prompt's Yes", PadButtons.DPadDown, PadButtons.DPadRight, PromptUp))
            {
                yield return wait;
            }

            if (CursorPointer() != yes.Pointer.ToInt64())
            {
                foreach (Wait wait in CursorTo(yes, "the prompt's Yes", PadButtons.DPadUp, PadButtons.DPadLeft, PromptUp))
                {
                    yield return wait;
                }
            }
        }

        if (MayPress("A on the game's prompt", PromptUp))
        {
            foreach (Wait wait in Tap(PadButtons.A))
            {
                yield return wait;
            }
        }
    }

    /// <summary>What a press can change on an event's screens, as one text: the top screen, the cursor, the choices made, the screen's own stages.</summary>
    private static string EventSignature(EventChoiceMenu menu)
    {
        try
        {
            return string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"{Top()?.Pointer.ToInt64() ?? 0}|{CursorPointer()}|{menu.m_ChoicesMade}|{menu.m_IsPlayingIntro}|{menu.m_IsTakenOver}|{menu.m_IsExiting}|{menu.IsVisible}");
        }
        catch (Exception)
        {
            return "unread";
        }
    }

    /// <summary>
    /// The pad's press for the event screen's way out with no pick: the button of its skip shortcut's own action (the game's
    /// <c>EventChoiceMenu.OnBackInput</c> only stops the back there), and how long to hold it; B with a tap when it can't be read.
    /// </summary>
    private static PadButtons SkipPress(EventChoiceMenu menu, out double holdSeconds)
    {
        holdSeconds = 0;
        try
        {
            UIInputShortcutBase? skip = menu.m_SkipShortcut;
            if (skip != null && !skip.WasCollected && TourShortcuts.ButtonFor(skip.m_RegisteredActionId) is { } button)
            {
                holdSeconds = TourShortcuts.HoldSeconds(skip.m_RequiredHoldTime);
                return button;
            }
        }
        catch (Exception)
        {
            // Unread: B, the button a player tries first.
        }

        return PadButtons.B;
    }

    /// <summary>
    /// True when the takeover's screen of <paramref name="menu"/> takes a press now: found, its input open (its entrance and its
    /// own animations over), no lineup over it, not closing, and the UI module's own gate letting it take input (nothing that
    /// blocks over it on the input stack): what its own confirm and direction handlers gate on, read in place.
    /// </summary>
    private static bool TakeoverTakesInput(EventChoiceMenu menu)
    {
        try
        {
            NurseProcedureScreen? screen = NurseScreen.Screen(menu, out _);
            if (screen == null || screen.WasCollected || !screen._inputOpen || screen._lineupOpen || screen._closing)
            {
                return false;
            }

            UIModule? module = screen._uiModule;
            return module == null || module.CanProcessInput(new IUIInputHandler(screen.Pointer));
        }
        catch (Exception)
        {
            return false; // unread: no press now (the event's own time limit ends a wait that never ends)
        }
    }

    /// <summary>
    /// What a press can move on a takeover's screen, as one key (the menu's takeover and exit, the screen, its stage, commit and
    /// close) with its stage (-1 when there is none to read). Focus moves are left out: they aren't progress.
    /// </summary>
    private static (string Key, int Stage) TakeoverProgress(EventChoiceMenu menu)
    {
        try
        {
            if (menu.WasCollected)
            {
                return ("gone", -1);
            }

            NurseProcedureScreen? screen = menu.m_IsTakenOver ? NurseScreen.Screen(menu, out _) : null;
            NurseProcedureState? state = screen == null || screen.WasCollected ? null : screen._state;
            int stage = state == null ? -1 : (int)state._CurrentStage_k__BackingField;
            return (string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"{menu.m_IsTakenOver}|{menu.m_IsExiting}|{screen?.Pointer.ToInt64() ?? 0}|{stage}|{screen?._committed ?? false}|{screen?._closing ?? false}"), stage);
        }
        catch (Exception)
        {
            return ("unread", -1);
        }
    }

    /// <summary>The map free for the event key: loaded, nothing over it, no prompt of the mod's, the cursor on one of the map's points.</summary>
    private static bool EventKeyReady()
    {
        try
        {
            return MapFree() && WorldMap.PointOf(WorldMap.Navigation?.CurrentNavigable) != null;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>The event's class (the game's own type name for the kinds the key opens), or "" when unread.</summary>
    private static string EventClass(EventChoiceMenu menu)
    {
        try
        {
            return menu.SourceNode is { } node ? MenuGame.TypeName(node) : string.Empty;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    /// <summary>The run's team's skater ids now (kept in memory only, never logged), or null when unread.</summary>
    private static HashSet<string>? SkaterIds()
    {
        try
        {
            Il2CppSystem.Collections.Generic.List<Il2CppData.SkaterData>? all = WorldMap.State?.runData?.teamData?.GetAllSkaters();
            if (all == null)
            {
                return null;
            }

            var ids = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < all.Count; i++)
            {
                if (all[i]?.id is { Length: > 0 } id)
                {
                    ids.Add(id);
                }
            }

            return ids;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Skaters on the team after and not before (a signing), and before and not after (a loss).</summary>
    private static (int Added, int Removed) Changes(HashSet<string>? before, HashSet<string>? after)
    {
        if (before == null || after == null)
        {
            return (0, 0);
        }

        int added = 0, removed = 0;
        foreach (string id in after)
        {
            added += before.Contains(id) ? 0 : 1;
        }

        foreach (string id in before)
        {
            removed += after.Contains(id) ? 0 : 1;
        }

        return (added, removed);
    }
}
#endif
