#if HAS_INTEROP
using System;
using System.Collections.Generic;
using Il2CppRogue.NPC.BlademasterNPC;
using Il2CppRogue.NPC.ChaosEvents;
using Il2CppRogue.NPC.CoachNPC;
using Il2CppRogue.NPC.ConditioningNPC;
using Il2CppRogue.NPC.FanNumber1NPC;
using Il2CppRogue.NPC.GeneralManagerNPC;
using Il2CppRogue.NPC.NurseNPC;
using Il2CppRogue.NPC.NurseNPC.Procedures;
using Il2CppSTS.Map;
using Il2CppTape2Tape.Hockey.UI;
using Il2CppTape2Tape.Speech;
using T2TOnline.Core.Campaign;
using T2TOnline.Plugin.Diagnostics;

namespace T2TOnline.Plugin.Campaign.Menus;

/// <summary>
/// The event menu's patches (Hooks/hooks.txt, "event menus as real views"): all on unfolded, non-generic methods.
/// <list type="bullet">
/// <item>Host: each new set of choices (a page, <c>GoToNextSetOfChoices</c>'s prefix; a reroll, <c>RerollChoices</c>' postfix) goes
/// out before the menu shows it (the event's step already rolled it, and its new cards come after its fade); an event NPC's line goes out as it starts
/// (<c>SpeechBubbleController.ResolveDefaults</c>, <see cref="EventSpeech"/>).</item>
/// <item>Friend, for the view it opens: the nine real events' <c>Initialize</c> overrides take the host's choices instead of
/// rolling (<see cref="EventViews.TryInstall"/>); <c>EventChoiceMenu.Show</c>'s postfix checks they're in place. A Nurse's
/// procedure state an earlier visit left on its event asset is cleared first (<see cref="NurseScreen.ClearStale"/>).</item>
/// <item>Friend, whenever it follows a host (backstops, D40): no event rolls (an <c>Initialize</c> not armed is skipped
/// too, and a General Manager's <c>NextChoices</c> rolls no page), no visit is marked (<c>TrainingEvent</c> and <c>CoachEvent.MarkVisited</c>: run counters), no NPC dialogue
/// (<c>GetCurrentDialogue</c> answers none: its lines are picked at random and mark this profile's seen dialogues, then
/// save it; the host's line plays instead, <see cref="EventSpeech"/>, which asks for the NPC past this), no seen-dialogue mark at all (<c>SeenDialogues.MarkSeen</c>), no Nurse offer marked (<c>NurseEvent.OnChoicesShown</c>
/// and <c>NurseProcedures.MarkOfferedAndSave</c>: this profile's offered procedures, then a save), and the menu's pick,
/// skip, reroll and exit are refused (the friend's own input is off already, <see cref="Scenes.FriendUi"/>).</item>
/// </list>
/// The real event menu is shown on a friend's PC only when every friend-side patch went in (<see cref="EventMenuReady"/>);
/// a Nurse's only when its own went in too (<see cref="NurseReady"/>).
/// </summary>
internal static partial class MenuPatches
{
    /// <summary>True when every patch the real event menu needs on a friend went in (otherwise events show as text).</summary>
    public static bool EventMenuReady { get; private set; }

    /// <summary>True when the Nurse's own patches went in too (otherwise a Nurse shows as text).</summary>
    public static bool NurseReady { get; private set; }

    /// <summary>True when the host's NPC lines can be captured (otherwise friends' event screens go without them).</summary>
    public static bool SpeechReady { get; private set; }

    /// <summary>The event menu's patches; <paramref name="shows"/> and <paramref name="closes"/>: the menus' own open and close patches went in.</summary>
    private static void InstallEvents(HarmonyLib.Harmony harmony, List<string> failed, bool shows, bool closes)
    {
        // Host: each new set of choices. (Required for the real view too: the same mod on the host's PC patches the same
        // methods, so one that fails here would fail there, and a friend's menu would miss the host's pages.)
        bool events = Patch(harmony, failed, typeof(EventChoiceMenu), "GoToNextSetOfChoices", prefix: nameof(EventPagePrefix));

        // Friend: the binding, the host's choices in place of the roll, and every backstop.
        events &= Patch(harmony, failed, typeof(EventChoiceMenu), "InitData", postfix: nameof(InitDataPostfix));
        foreach (Type node in new[]
        {
            typeof(CoachEvent), typeof(RegularPlayerTraining), typeof(StatsTraining), typeof(TeamTraining), typeof(ChaosEvent3Choice), typeof(BlademasterEvent),
            typeof(FanNumber1Event), typeof(GeneralManagerEvent),
        })
        {
            events &= Patch(harmony, failed, node, "Initialize", prefix: nameof(EventInitializePrefix));
        }

        // The Nurse (v1): real cards. Its roll reads this player's offered procedures;
        // its cards being shown marks the one offered on this profile and saves it: neither runs on a friend (the second
        // only through the first, a backstop to the cleared state, NurseScreen.ClearStale).
        bool nurse = Patch(harmony, failed, typeof(NurseEvent), "Initialize", prefix: nameof(EventInitializePrefix));
        nurse &= Patch(harmony, failed, typeof(NurseEvent), "OnChoicesShown", prefix: nameof(NurseChoicesShownPrefix));
        nurse &= Patch(harmony, failed, typeof(NurseProcedures), "MarkOfferedAndSave", prefix: nameof(NurseOfferedPrefix), postfix: nameof(NurseOfferedPostfix));

        // v1: its procedure screen as a real view (MenuPatches.Nurse.cs).
        InstallNurse(harmony, failed);

        // A General Manager's next page never rolls on a friend either (it would roll, and reroll this map's node rewards): a
        // backstop, as the menu's own pick and skip, its only callers here, are refused.
        Patch(harmony, failed, typeof(GeneralManagerEvent), "NextChoices", prefix: nameof(EventNextChoicesPrefix));

        events &= Patch(harmony, failed, typeof(TrainingEvent), "MarkVisited", prefix: nameof(EventMarkVisitedPrefix));
        events &= Patch(harmony, failed, typeof(CoachEvent), "MarkVisited", prefix: nameof(EventMarkVisitedPrefix));
        events &= Patch(harmony, failed, typeof(EventChoiceMenu), "Show", postfix: nameof(EventShowPostfix));
        events &= Patch(harmony, failed, typeof(EventChoiceMenu), "GetCurrentDialogue", prefix: nameof(EventDialoguePrefix));
        events &= Patch(harmony, failed, typeof(EventChoiceMenu), "OnItemClicked", prefix: nameof(EventPickPrefix));
        events &= Patch(harmony, failed, typeof(EventChoiceMenu), "ExitWithNoChoice", prefix: nameof(EventSkipPrefix));

        // Not on RerollChoicesInPlace, which the game's compiler inlined here (verified in the game's code): nothing calls its own copy.
        events &= Patch(harmony, failed, typeof(EventChoiceMenu), "RerollChoices", prefix: nameof(EventRerollRefusePrefix), postfix: nameof(EventRerolledPostfix));
        events &= Patch(harmony, failed, typeof(EventChoiceMenu), "Exit", prefix: nameof(EventExitPrefix));
        events &= Patch(harmony, failed, typeof(Il2Cpp.SeenDialogues), "MarkSeen", prefix: nameof(MarkSeenPrefix), postfix: nameof(MarkSeenPostfix));
        EventMenuReady = events && shows && closes;
        NurseReady = EventMenuReady && nurse;

        // Host: an event NPC's line as it starts. Every bubble line in the game is resolved here, once per line (and once
        // more for a stacking one); SpeechBubbleController.Show itself isn't patched: its CancellationToken and
        // Nullable<AnchorSide> are 8-byte structs passed by value, which the interop's detour reads as pointers.
        SpeechReady = Patch(harmony, failed, typeof(SpeechBubbleController), "ResolveDefaults", prefix: nameof(SpeechLinePrefix));

        // The speech mirror's presses and the cards that wait for the host's (MenuPatches.Speech.cs).
        InstallSpeech(harmony, failed);
    }

    /// <summary>
    /// Host: the game resolves a bubble line (each line of every bubble in this game): an event NPC's line goes to friends
    /// (<see cref="EventSpeech.LineResolved"/>). Reads only.
    /// </summary>
    private static void SpeechLinePrefix(SpeechBubbleController __instance, SpeechLineData src)
    {
        try
        {
            if (Views is { Hosting: true } views && src != null)
            {
                EventSpeech.LineResolved(views, __instance, src);
            }
        }
        catch (Exception ex)
        {
            Failed("NPC line", ex);
        }
    }

    /// <summary>The host's event moved to its next choices (a pick or skip with more to come): they go out now.</summary>
    private static void EventPagePrefix(EventChoiceMenu __instance)
    {
        try
        {
            if (Views is { Hosting: true } views)
            {
                views.Host.EventChoices(__instance, EventSetStep.Page, views.HasFriends);
            }
        }
        catch (Exception ex)
        {
            Failed("event page", ex);
        }
    }

    /// <summary>The host's event menu ran its reroll: choices it rerolled in place go out now (<see cref="HostMenus.EventRerolled"/>).</summary>
    private static void EventRerolledPostfix(EventChoiceMenu __instance)
    {
        try
        {
            if (Views is { Hosting: true } views)
            {
                views.Host.EventRerolled(__instance, views.HasFriends);
            }
        }
        catch (Exception ex)
        {
            Failed("event reroll", ex);
        }
    }

    /// <summary>
    /// An event rolls its choices. On a following friend, never: the event this PC opened for the host gets the host's
    /// choices, and any other's roll is skipped (only the host's game rolls). The host's own events roll as ever.
    /// </summary>
    private static bool EventInitializePrefix(EventNode __instance)
    {
        try
        {
            if (!FollowingHost)
            {
                return true;
            }

            // A Nurse's state from an earlier visit (its asset is shared by every visit in this game) goes before its cards.
            NurseScreen.ClearStale(__instance);
            if (!EventViews.TryInstall(__instance))
            {
                // Not one this PC opened for the host (only a late one could be): no roll, and no choices if it held none,
                // so its menu shows nothing until it's closed.
                __instance.eventChoices ??= new Il2CppSystem.Collections.Generic.List<EventChoice>();
                ReportOnce("event roll", "Menus: an event's own roll was skipped on this PC (only the host's game rolls its events).");
            }

            return false;
        }
        catch (Exception ex)
        {
            Failed("event roll", ex);
            return !FollowingHost;
        }
    }

    /// <summary>
    /// A General Manager's next page on a following friend: none (answers "no more choices"; it would roll free agents and
    /// reroll this map's node rewards). The host's next page comes as its choices instead. Anywhere else the page rolls, and
    /// its event is noted for the node rewards' reroll it may run (<see cref="NodeRewardsPostfix"/>: that method is static).
    /// </summary>
    private static bool EventNextChoicesPrefix(GeneralManagerEvent __instance, ref bool __result)
    {
        try
        {
            if (FollowingHost)
            {
                ReportOnce("event next choices", "Menus: a General Manager's next choices weren't rolled on this PC (only the host's game rolls them; its roll would also reroll this map's node rewards).");
                __result = false;
                return false;
            }

            _rolling = __instance;
        }
        catch (Exception ex)
        {
            Failed("event next choices block", ex);
        }

        return true;
    }

    /// <summary>
    /// A Nurse's cards are shown on a following friend: its own step (with a procedure state, it marks the procedure offered
    /// on this player's profile and saves it) is skipped. The host's Nurse made the offer; a friend's profile gets the mark
    /// with shared progress (<see cref="NurseOfferedPostfix"/>).
    /// </summary>
    private static bool NurseChoicesShownPrefix()
    {
        try
        {
            if (FollowingHost)
            {
                ReportOnce("nurse shown", "Menus: the Nurse's cards-shown step was skipped on this PC (it marks the procedure offered on this profile and saves it; the host's Nurse made the offer).");
                return false;
            }
        }
        catch (Exception ex)
        {
            Failed("Nurse cards shown block", ex);
            return !FollowingHost;
        }

        return true;
    }

    /// <summary>
    /// A Nurse procedure marked offered on a following friend's profile, and the profile saved: refused. Its only caller is
    /// the step above (skipped), so this means another way in: said once, as a warning.
    /// </summary>
    private static bool NurseOfferedPrefix()
    {
        try
        {
            if (FollowingHost)
            {
                if (Reported.Add("nurse offered"))
                {
                    ModLog.Warn("Menus: a Nurse procedure's offer mark was refused on this PC while it follows a host (it would mark this profile's offered procedures and save it).");
                }

                return false;
            }
        }
        catch (Exception ex)
        {
            Failed("Nurse offer mark block", ex);
            return !FollowingHost;
        }

        return true;
    }

    /// <summary>Host: the procedure its Nurse offered is shared progress (every friend's profile marks it, <see cref="Progress.ProgressPatches.NurseOffered"/>).</summary>
    private static void NurseOfferedPostfix(NurseProcedureState.Procedure procedure) => Progress.ProgressPatches.NurseOffered(procedure);

    /// <summary>An event marks its visit (run counters: the host's checkpoint brings them): never on a following friend.</summary>
    private static bool EventMarkVisitedPrefix()
    {
        try
        {
            if (FollowingHost)
            {
                ReportOnce("event visit", "Menus: an event's visit mark was skipped on this PC (the host's run counts it).");
                return false;
            }
        }
        catch (Exception ex)
        {
            Failed("event visit block", ex);
        }

        return true;
    }

    /// <summary>The event menu's <c>Show</c> ran its first part (the roll, before any card): a friend's view checks the host's choices are in.</summary>
    private static void EventShowPostfix(EventChoiceMenu __instance)
    {
        try
        {
            if (FollowingHost)
            {
                EventViews.AfterShow(__instance);
            }
        }
        catch (Exception ex)
        {
            Failed("event shown", ex);
        }
    }

    /// <summary>
    /// The NPC's own lines on a following friend: none (they're picked at random, marked seen on this player's profile and
    /// saved); the rest of the intro plays. Its only caller is the intro at the NPC's lines: that point is noted, and the
    /// host's line plays here then (<see cref="EventSpeech"/>, which asks for the NPC itself past this refusal).
    /// </summary>
    private static bool EventDialoguePrefix(EventChoiceMenu __instance, ref NpcIntroDialogue __result)
    {
        try
        {
            if (FollowingHost && !EventSpeech.Bypassing)
            {
                EventSpeech.ReachedDialogue(__instance);
                ReportOnce("event dialogue", "Menus: the event NPC's own lines are left out on this PC (they'd mark this profile's seen dialogues); the host's lines play here instead.");
                __result = null!;
                return false;
            }
        }
        catch (Exception ex)
        {
            Failed("event dialogue block", ex);
        }

        return true;
    }

    /// <summary>A pick in an event on a following friend: refused (the host's game picks; its pick is shown here).</summary>
    private static bool EventPickPrefix() => RefuseEvent("event pick", "Menus: a pick in the event menu was refused on this PC (the host's game picks).");

    /// <summary>A skip of an event on a following friend: refused.</summary>
    private static bool EventSkipPrefix() => RefuseEvent("event skip", "Menus: a skip of the event menu was refused on this PC (the host's game decides).");

    /// <summary>A reroll of an event on a following friend: refused (it would roll and count this PC's reroll relic).</summary>
    private static bool EventRerollRefusePrefix() => RefuseEvent("event reroll", "Menus: a reroll of the event menu was refused on this PC (the host's new choices are shown here).");

    /// <summary>
    /// An event's exit. On a host: noted, friends' NPCs say goodbye with its. On a following friend: refused (it would move this
    /// PC's map; the host's close closes it here).
    /// </summary>
    private static bool EventExitPrefix(EventChoiceMenu __instance)
    {
        NoteHostExit(__instance);
        return RefuseEvent("event exit", "Menus: the event menu's own exit was refused on this PC (it closes when the host's does).");
    }

    /// <summary>
    /// A dialogue marked seen on a following friend's profile: refused (a backstop to the lines left out), except by shared
    /// progress, which marks the event NPC lines heard from the host's (<see cref="Progress.ProgressFriend.ApplyingNow"/>).
    /// </summary>
    private static bool MarkSeenPrefix() => Progress.ProgressFriend.ApplyingNow is { Applying: true } ||
        RefuseEvent("seen dialogue", "Menus: a dialogue's seen mark was refused on this PC while it follows a host (this profile's own dialogues are kept for its own runs).");

    /// <summary>Host: a seen mark on its profile; an event NPC's line is shared progress (<see cref="Progress.ProgressPatches.DialogueSeen"/>).</summary>
    private static void MarkSeenPostfix(Il2Cpp.SeenDialogues __instance, string id) => Progress.ProgressPatches.DialogueSeen(__instance, id);

    private static bool RefuseEvent(string key, string message)
    {
        try
        {
            if (FollowingHost)
            {
                ReportOnce(key, message);
                return false;
            }
        }
        catch (Exception ex)
        {
            Failed($"{key} block", ex);
        }

        return true;
    }
}
#endif
