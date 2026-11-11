#if HAS_INTEROP
using System;
using System.Collections.Generic;
using Il2CppRogue.NPC.NurseNPC.Procedures;
using Il2CppTape2Tape.Hockey.UI;
using T2TOnline.Core.Lobby;
using T2TOnline.Plugin.Campaign.Menus;
using T2TOnline.Plugin.Diagnostics;
using T2TOnline.Plugin.Streaming;

namespace T2TOnline.Plugin.NativeLobby;

// The campaign tours' takeovers (Core's CampaignTour.TakeoverPress). An event's pick can hand its screen to a screen of its own:
// the game's EventChoiceMenu.Choose sets the menu's m_IsTakenOver and calls the choice's onTakeover, and only the Nurse's
// choices have one (its procedure screen). The game's campaign walker drives only an event's choice menu: under a takeover
// it finds no enabled card and gives up three seconds later ("zero enabled choices"). So while the walker's map walk runs,
// a takeover pauses it (its coroutine isn't stepped), the pad drives the takeover's screen to its end as a player would (the
// events step's own driver, DriveEvent), and once the map is free again (nothing over it, the map's tracker unlocked) the
// walk is stepped on: the walker's next look finds the event over and walks to the next node. cp_end leaves an event screen
// still up the same way before it looks for the map's menu.
internal static partial class LobbyUiCheck
{
    private static int _cpEventScreens;
    private static bool _cpTakeoverGoesOn;

    /// <summary>The event screen on top while a screen of its own has taken it over, or null.</summary>
    private static EventChoiceMenu? TakenOverEvent()
    {
        try
        {
            return Top()?.TryCast<EventChoiceMenu>() is { m_IsTakenOver: true } menu && !menu.WasCollected ? menu : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// A takeover met by the walker's map walk (paused meanwhile): its screen driven to its end by the pad, then the map free
    /// again. Sets <see cref="_cpTakeoverGoesOn"/>: true when the walk may be stepped on.
    /// </summary>
    private static IEnumerable<Wait> DriveTakeover(EventChoiceMenu menu)
    {
        _cpTakeoverGoesOn = false;
        _cpEventScreens++;
        string screen = TakeoverText(menu);
        StepNote(CampaignTour.TakeoverLine(Tally.Nodes, TourCampaignWalker.LastNodeType(), screen));
        double started = FrameClock.Seconds;
        foreach (Wait wait in DriveEvent(menu))
        {
            yield return wait;
        }

        bool left = _cpEventEnd is CampaignEventEnd.Closed or CampaignEventEnd.LeftByBack;
        if (left)
        {
            // The walker's own end of an event: no event, old squad or prompt screen up and the map's tracker unlocked.
            yield return Soft(() => { Watch(); return MapFree() && !TrackerLocked(); }, CampaignTour.TakeoverMapSeconds, "the map free after the takeover");
        }

        bool free = MapFree() && !TrackerLocked();
        StepNote(CampaignTour.TakeoverEndLine(screen, _cpEventEnd, FrameClock.Seconds - started, _cpEventPresses, _cpEventStages, free));
        _cpTakeoverGoesOn = CampaignTour.WalkGoesOnAfter(_cpEventEnd, free);
        if (!_cpTakeoverGoesOn && _cpEventEnd == CampaignEventEnd.OtherScreen)
        {
            StepNote($"campaign: during the takeover {_cpEventOther} came up, which the tour doesn't press on");
        }
    }

    /// <summary>
    /// cp_end, before the map's menu: an event screen still up (a walk that failed inside an event) is left by the pad as a player
    /// leaves it: its skip shortcut held (a game exit prompt, if one comes, answered Yes); a takeover, which its back can't
    /// leave, driven to its end. Where the pad can't, the map's menu isn't reached and the step stops as it would have.
    /// </summary>
    private static IEnumerable<Wait> LeaveEventScreen(EventChoiceMenu menu)
    {
        bool takenOver = !menu.WasCollected && menu.m_IsTakenOver;
        _cpEventScreens += takenOver ? 1 : 0;
        double started = FrameClock.Seconds;
        foreach (Wait wait in DriveEvent(menu, leave: true))
        {
            yield return wait;
        }

        StepNote(CampaignTour.LeaveEventLine(takenOver, _cpEventEnd, FrameClock.Seconds - started, _cpEventPresses));
        if (_cpEventEnd is not (CampaignEventEnd.Closed or CampaignEventEnd.LeftByBack))
        {
            Problem(_cpEventEnd == CampaignEventEnd.OtherScreen
                ? $"campaign: leaving the event screen, {_cpEventOther} came up, which the tour doesn't press on"
                : "campaign: the pad couldn't leave the event screen still up");
            yield break;
        }

        // The map's menu needs the map's own input back (MapMenu waits for it too).
        yield return Soft(() => { Watch(); return MapFree() && !TrackerLocked(); }, CampaignTour.TakeoverMapSeconds, "the map free after the event screen");
    }

    /// <summary>The map's tracker locked (an event or a node's opening holds the map), read; true when unread.</summary>
    private static bool TrackerLocked()
    {
        try
        {
            Il2CppSTS.Map.MapPlayerTracker? tracker = Il2CppSTS.Map.MapPlayerTracker.Instance;
            return tracker != null && !tracker.WasCollected && tracker.isLocked;
        }
        catch (Exception)
        {
            return true;
        }
    }

    /// <summary>The takeover's screen for a line: <c>Transplant, the Nurse's procedure screen</c> (the procedure by its name in the game's code), or a bare name.</summary>
    private static string TakeoverText(EventChoiceMenu menu)
    {
        try
        {
            NurseProcedureScreen? screen = NurseScreen.Screen(menu, out _);
            int procedure = NurseScreen.CurrentProcedure(screen?._state);
            string? name = procedure >= 0 && Enum.IsDefined(typeof(NurseProcedureState.Procedure), procedure)
                ? Enum.GetName(typeof(NurseProcedureState.Procedure), procedure)
                : null;
            return screen == null ? "its own screen, not found yet"
                : name == null ? "the Nurse's procedure screen" : $"{name}, the Nurse's procedure screen";
        }
        catch (Exception ex)
        {
            return $"unread ({ModLog.SafeErrorText(ex)})";
        }
    }
}
#endif
