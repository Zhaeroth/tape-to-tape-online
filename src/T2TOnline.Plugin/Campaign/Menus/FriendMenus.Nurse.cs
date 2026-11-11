#if HAS_INTEROP
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using Il2CppBishopGames.UI;
using Il2CppRogue.NPC.NurseNPC.Procedures;
using Il2CppTape2Tape.Hockey.UI;
using T2TOnline.Core.Campaign;
using T2TOnline.Plugin.Campaign.Scenes;
using T2TOnline.Plugin.Diagnostics;
using T2TOnline.Plugin.Streaming;
using UnityEngine;

namespace T2TOnline.Plugin.Campaign.Menus;

/// <summary>
/// A friend's side of the Nurse's procedure screen as a real view (v1).
/// <list type="number">
/// <item>The roster (<see cref="NurseRebuild"/>): when the Nurse's event view is prepared (it waits for the host's roster,
/// sent right after the choices), the host's roster is rebuilt here with the game's own factory and checked
/// (<see cref="NurseRosters"/>), then goes on this PC's Nurse event with its choices, so its cards warm the procedure screen as
/// the host's do.</item>
/// <item>The takeover (<see cref="NurseTakeover"/>, <see cref="NurseTick"/>): when the host's event screen is taken over,
/// this PC opens the game's own procedure screen over its real event screen (<see cref="NurseFollow.Open"/>, the call the
/// host's pick makes) with the host's procedure, once the host's steps are here and its own intro is over. Its own input
/// is never registered (<c>RegisterInput</c> skipped): the host's steps are replayed through its handlers, in order, each
/// checked (<see cref="NurseFollower"/>). The Butcher's lines start with its stages, so with those steps; a press of the host's
/// that shows one in full is followed too (<see cref="NurseLineFollow"/>). The host's lineup from it comes as its own view, with
/// the screen's top canvases put under it as the game's own lineup puts them.</item>
/// <item>The commit (<see cref="NurseCommitting"/>): never runs here; the host's outcome goes into its state first.</item>
/// <item>The end: its own <c>Close</c> with the host's (it puts this PC's event screen back; nothing here sets the flag that
/// would make it exit the event). Anything that can't be followed falls back to the text panel over the event screen, with
/// why (logged); the cards go away again once the screen's close has put them back.</item>
/// </list>
/// </summary>
internal sealed partial class FriendMenus
{
    /// <summary>How long a takeover waits for the host's steps (and this PC's intro) before its screen shows as text.</summary>
    private const double NurseOpenWaitSeconds = 3;

    /// <summary>
    /// Friend, from the procedure screen's commit check at its result (its prefix, which then skips the commit): the host's
    /// outcome goes into the state of this PC's screen <paramref name="screen"/> with the state's own setters, so its result
    /// shows what the host's commit did. An outcome not here yet goes in when it comes. Never throws.
    /// </summary>
    public void NurseCommitting(NurseProcedureScreen screen)
    {
        foreach (View view in _views.Values)
        {
            if (view.NurseReal is not { Screen: { } open } nurse || open.Pointer != screen.Pointer)
            {
                continue;
            }

            nurse.CommitSeen = true;
            nurse.Outcome ??= Outcome(view.Serial);
            if (nurse.Outcome == null)
            {
                Log($"Menus: {view.LogId}: the Nurse's commit was skipped here; {LogNames.HostTag()}'s outcome isn't here yet (it goes in when it comes).");
                return;
            }

            ApplyNurseOutcome(view, nurse, screen, rebuild: false);
            return;
        }
    }

    /// <summary>
    /// A real Nurse event waits for the host's roster before it opens (it's sent right after the choices, so the cards here
    /// warm the procedure screen with it, as the host's do), when this game can show the procedure screen for real.
    /// </summary>
    private bool NurseRosterPending(MenuState state) =>
        state.Data.Text(EventCapture.ClassKey) == EventChoiceSet.TakeoverClass && MenuPatches.NurseScreenReady &&
        !_attachments.Has(state.Serial, MenuAttachmentKind.NurseRoster);

    /// <summary>
    /// A Nurse event's plan (its open, or a reroll): the host's newest roster rebuilt here and checked, to go on the event with
    /// its choices (<see cref="EventPlan.Nurse"/>); or why not, kept for the takeover (its procedure screen is text then).
    /// </summary>
    private void NurseRebuild(View view, EventPlan plan)
    {
        if (plan.Set.EventClass != EventChoiceSet.TakeoverClass)
        {
            return;
        }

        // Each plan (an open, a reroll) starts afresh: a takeover's state from an earlier one doesn't carry over.
        NurseView nurse = view.NurseReal = new NurseView();
        if (!MenuPatches.NurseScreenReady)
        {
            nurse.Why = "this game couldn't be patched to show it safely";
            return;
        }

        if (_attachments.Get(view.Serial, MenuAttachmentKind.NurseRoster) is not { } attachment || !attachment.TryGetNurseRoster(out NurseRosterData? hostRoster) || hostRoster == null)
        {
            nurse.Why = $"{LogNames.HostTag()}'s Nurse roster didn't come";
            ModLog.Warn($"Menus: {view.LogId}: {nurse.Why}; the procedure screen will show as text here.");
            return;
        }

        if (hostRoster.Problem != null)
        {
            nurse.Why = hostRoster.Problem == NurseRosterData.SwitchedOff ? $"{LogNames.HostTag()} has the real screen switched off" : $"{LogNames.HostTag()}'s game sent no Nurse roster ({NurseRosterData.ProblemForLog(hostRoster.Problem)})";
            Log($"Menus: {view.LogId}: {nurse.Why}; the procedure screen will show as text here.");
            return;
        }

        long started = Stopwatch.GetTimestamp();
        NurseBuilt? built = NurseRosters.Rebuild(hostRoster, out string problem);
        double ms = (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;
        if (built == null)
        {
            nurse.Why = problem;
            ModLog.Warn($"Menus: {view.LogId}: the Nurse's roster couldn't be rebuilt here ({problem}); {LogNames.HostTag()}'s procedure screen will show as text.");
            return;
        }

        plan.Nurse = built;
        nurse.Built = built;
        ModLog.Info(string.Create(CultureInfo.InvariantCulture, $"Menus: {view.LogId}: the Nurse's roster is rebuilt here with the game's own code from {LogNames.HostTag()}'s draws: identical ({hostRoster}; {ms:0.0} ms)."));
    }

    /// <summary>
    /// The host's takeover value changed (<see cref="ApplyEventState"/>). True when the real procedure screen here handles it
    /// (the text over the event screen and the cards' own put-away are skipped): its open is wanted (it opens in
    /// <see cref="NurseTick"/>), or it's following or falling back. False for the text path: no roster here, or the takeover's
    /// end (its screen is closed here first).
    /// </summary>
    private bool NurseTakeover(View view, bool takeover)
    {
        if (view.NurseReal is not { } nurse)
        {
            return false;
        }

        if (!takeover)
        {
            if (nurse.Wanted || nurse.Screen != null)
            {
                nurse.Wanted = false;
                CloseNurseScreen(view, nurse, $"{LogNames.HostTag()}'s procedure screen closed");
            }

            return false;
        }

        if (nurse.Wanted)
        {
            return true;
        }

        if (nurse.Why.Length == 0 && view.Event?.Nurse is not { Installed: true })
        {
            nurse.Why = view.Event?.Nurse?.Failure is { Length: > 0 } failure ? failure : "the rebuilt roster isn't on this PC's Nurse event";
        }

        if (nurse.Why.Length > 0 || view.Mode != Mode.Real)
        {
            return false;
        }

        nurse.Wanted = true;
        nurse.WantedSince = FrameClock.Seconds;
        view.Overlay = true;
        _panelDirty = true;
        Log($"Menus: {LogNames.HostTag()}'s Nurse took {view.LogId} over; its procedure screen opens here for real.");
        return true;
    }

    /// <summary>
    /// Each frame of an event view (<see cref="EventTick"/>): the real procedure screen opened once the host's steps and this
    /// PC's intro allow, then the host's steps followed; after a fallback, the cards put away again once the screen's close has
    /// put them back.
    /// </summary>
    private void NurseTick(View view, MirroredMenu menu, EventChoiceMenu open)
    {
        if (view.NurseReal is not { } nurse)
        {
            return;
        }

        NurseCardsAfterClose(view, nurse, open);
        if (!nurse.Wanted)
        {
            return;
        }

        double now = FrameClock.Seconds;
        if (nurse.Fallen)
        {
            if (view.IntroOver && !view.Swapping && !view.CardsHidden && NurseFollow.Released(nurse.Screen) &&
                Try(view, "putting the cards away under the procedure screen's text", () => open.SetChoicesShown(false, false)))
            {
                view.CardsHidden = true;
            }

            return;
        }

        // Not opened yet. "is null", not "== null": Unity's equality also calls a screen its own close destroyed null, and that one
        // mustn't open again before the host's takeover's end gets here (FollowNurse takes it).
        if (nurse.Screen is null)
        {
            OpenNurseScreen(view, nurse, open, now);
            return;
        }

        FollowNurse(view, nurse, now, SpeechMirror.HostProgress(menu, NurseHost.ButcherSlot));
    }

    /// <summary>
    /// The real procedure screen's close has put this PC's event screen back. On the host's its next step is the event's exit, whose
    /// first call takes the cards away (verified in the game's code: <c>CloseHostAsync</c>, <c>ExitAsync</c>);
    /// here nothing would until the host's close comes, after its NPC's goodbye, and the picked card (every card, for a friend who
    /// joined after the pick) would sit there. A takeover's close always exits the event on the host's (<c>m_IsTakenOver</c>, verified
    /// in the game's code), so the same call runs here once, whether this PC's close (its own timer, the host's last press)
    /// came before the host's end or after it, and whether the NPC's goodbye (the host's exit, <see cref="NpcGoodbye"/>) took them
    /// first: each call plays the cards' exit sound again (verified in the game's code: <c>AnimateChoiceCardsOut</c>), and
    /// the host's game makes one. A plain look until then.
    /// </summary>
    private void NurseCardsAfterClose(View view, NurseView nurse, EventChoiceMenu open)
    {
        if (nurse.CardsAway || nurse.Fallen || nurse.Closing is null || !NurseFollow.Released(nurse.Closing))
        {
            return;
        }

        nurse.CardsAway = true;
        if (!view.CardsHidden && Try(view, "putting the cards away after the procedure screen's close", () => open.SetChoicesShown(false, false)))
        {
            view.CardsHidden = true;
            Log($"Menus: {view.LogId}: the procedure screen's close put this PC's event screen back; its cards go, as {LogNames.HostTag()}'s event exits.");
        }
    }

    /// <summary>The host's steps or outcome for an event view (<see cref="Attached"/>).</summary>
    private static void NurseAttached(View view, MenuAttachment attachment)
    {
        if (view.NurseReal is not { } nurse)
        {
            return;
        }

        if (attachment.Kind == MenuAttachmentKind.NurseSteps && attachment.TryGetNurseSteps(out NurseStepsData? steps) && steps != null)
        {
            nurse.Steps = steps;
        }
        else if (attachment.Kind == MenuAttachmentKind.NurseRoster && attachment.TryGetNurseRoster(out NurseRosterData? roster) && roster?.Outcome is { } outcome)
        {
            nurse.Outcome = outcome;
            ModLog.Info($"Menus: {view.LogId}: got {LogNames.HostTag()}'s Nurse procedure outcome ({outcome}).");
            if (nurse.CommitSeen && !nurse.OutcomeApplied && nurse.Screen is { } screen && screen != null && !nurse.Fallen)
            {
                // It came after this PC's commit point: in now, and the screen built again with it.
                ApplyNurseOutcome(view, nurse, screen, rebuild: true);
            }
        }
    }

    /// <summary>
    /// The event menu here is closing (<see cref="CloseReal"/>): its procedure screen closes first (its own close), and a
    /// roster rebuilt for it that no procedure screen took is cleaned up (its talents destroyed, its state taken off the
    /// event). Never throws.
    /// </summary>
    private static void NurseClosed(View view)
    {
        if (view.NurseReal is { } nurse)
        {
            nurse.Wanted = false;
            CloseNurseScreen(view, nurse, "its event screen closed here");
        }

        if (view.Event?.Nurse is { } built)
        {
            NurseRosters.Release(built, view.Event.Node);
        }

        // One rebuilt for a reroll whose choices never went in (its plan isn't the view's).
        NurseRosters.Release(view.NurseReal?.Built, null);
    }

    /// <summary>True while the real procedure screen is up here (or opening): the panel shows nothing over it.</summary>
    private static bool NurseRealUp(View view) => view.NurseReal is { Wanted: true, Fallen: false } && view.Mode == Mode.Real;

    /// <summary>Why the host's procedure screen shows as text here (the note over it).</summary>
    private static string NurseTextWhy(View view) =>
        view.NurseReal is { Why.Length: > 0 } nurse ? $"its procedure screen can't be shown for real here ({nurse.Why})"
        : EventCapture.TextClasses.TryGetValue(EventChoiceSet.TakeoverClass, out string? text) ? text : string.Empty;

    /// <summary>Opens the game's procedure screen here once the host's steps and this PC's intro allow (or falls back after a wait).</summary>
    private void OpenNurseScreen(View view, NurseView nurse, EventChoiceMenu open, double now)
    {
        if (nurse.Steps == null && _attachments.Get(view.Serial, MenuAttachmentKind.NurseSteps) is { } attachment && attachment.TryGetNurseSteps(out NurseStepsData? steps))
        {
            nurse.Steps = steps;
        }

        if (nurse.Steps == null || !view.IntroOver || view.Swapping)
        {
            if (now - nurse.WantedSince > NurseOpenWaitSeconds)
            {
                NurseFallback(view, nurse, nurse.Steps == null ? $"{LogNames.HostTag()}'s procedure screen steps didn't come within {NurseOpenWaitSeconds:0} s"
                    : $"this PC's event screen wasn't ready for it within {NurseOpenWaitSeconds:0} s");
            }

            return;
        }

        if (view.Event?.Nurse is not { Installed: true } built)
        {
            NurseFallback(view, nurse, "the rebuilt roster isn't on this PC's Nurse event");
            return;
        }

        // The host's newest roster must still be the one rebuilt here: a reroll's new one can come just after its choices went in.
        if (_attachments.Get(view.Serial, MenuAttachmentKind.NurseRoster) is { } newest && newest.TryGetNurseRoster(out NurseRosterData? roster) && roster != null &&
            NurseRosterData.FirstDifference(roster, built.Host) is { } changed)
        {
            NurseBuilt? again = NurseRosters.Rebuild(roster, out string problem);
            if (again != null)
            {
                NurseRosters.Install(view.Event.Node, again);
            }

            if (again is not { Installed: true })
            {
                NurseFallback(view, nurse, $"{LogNames.HostTag()}'s roster changed after this PC rebuilt it ({changed}), and the new one couldn't go on here ({(again == null ? problem : again.Failure)})");
                return;
            }

            ModLog.Info($"Menus: {view.LogId}: {LogNames.HostTag()}'s newest Nurse roster came after its choices went in; rebuilt here with the game's own code: identical ({roster}).");
            view.Event.Nurse = again;
            nurse.Built = again;
            built = again;
        }

        // A friend who comes in late, with the host's screen too far ahead, shows it as text (never opened, then closed).
        if (nurse.Steps.Newest > NurseFollower.MaxBehind)
        {
            NurseFallback(view, nurse, $"{LogNames.HostTag()}'s screen was {nurse.Steps.Newest} steps ahead when it could open here");
            return;
        }

        // A host alone until now starts its steps from where it had got to: this PC's screen can't start there (text, never opened).
        if (nurse.Steps.StartStage != NurseFollow.FirstStage(nurse.Steps.Procedure))
        {
            NurseFallback(view, nurse, $"{LogNames.HostTag()}'s screen was already at stage {nurse.Steps.StartStage} when its steps started");
            return;
        }

        int procedure = nurse.Steps.Procedure;
        NurseProcedureScreen? screen = null;
        try
        {
            FriendUi.Apply(() => screen = NurseFollow.Open(open, built, procedure));
        }
        catch (Exception ex)
        {
            // It may have got as far as hiding this PC's event screen: what it left closes with the fallback, which puts that back.
            nurse.Screen = NurseFollow.LeftOpen(open);
            NurseFallback(view, nurse, $"the game couldn't open it here ({ModLog.SafeErrorText(ex)})");
            return;
        }

        built.Opened = true;
        _tally.ScreenReal(NurseScreenKind);
        nurse.Screen = screen;
        nurse.Follower = new NurseFollower();
        nurse.OpenedAt = now;
        int stage = NurseFollow.Stage(screen!);
        ModLog.Info($"Menus: {view.LogId}: {LogNames.HostTag()}'s Nurse procedure screen opens here for real ({NurseScreen.Title(procedure)}, in this PC's language; procedure {procedure}, " +
            $"stage {stage}, option {NurseFollow.Focus(screen!)}; {LogNames.HostTag()}'s at step {nurse.Steps.Newest}, {(now - nurse.WantedSince) * 1000:0} ms after the takeover came).");
        NurseFollowStep first = nurse.Follower.Opened(nurse.Steps, stage);
        if (first.Action == NurseFollowAction.Fallback)
        {
            NurseFallback(view, nurse, first.Why);
        }
    }

    /// <summary>The host's steps on the screen here: the follower's next decision, carried out.</summary>
    private void FollowNurse(View view, NurseView nurse, double now, SpeechProgress butcher)
    {
        NurseProcedureScreen screen = nurse.Screen!;
        if (screen == null)
        {
            // Gone with its own close (the host's last press, replayed) before the host's takeover's end got here: nothing's wrong.
            if (!nurse.ClosingSeen)
            {
                NurseFallback(view, nurse, "the procedure screen here closed by itself");
            }

            return;
        }

        NurseLineup(view, nurse, screen);
        if (screen._closing || nurse.Steps == null)
        {
            if (screen._closing)
            {
                nurse.ClosingSeen = true;
                nurse.Closing = screen;
            }

            return; // closing (its own timer, or the host's last press, replayed): nothing more to follow
        }

        FollowButcher(view, nurse, screen, butcher);
        NurseFollowStep next = nurse.Follower!.Next(nurse.Steps, NurseFollow.Ready(screen), NurseFollow.Stage(screen), NurseFollow.Focus(screen), now);
        try
        {
            switch (next.Action)
            {
                case NurseFollowAction.Apply:
                    NurseStep step = next.Step!;
                    string how = string.Empty;
                    FriendUi.Apply(() => how = NurseFollow.Replay(screen, step));
                    nurse.Replayed++;
                    nurse.Native += how.Contains("native", StringComparison.Ordinal) ? 1 : 0;
                    nurse.Developer += how.Contains("developer", StringComparison.Ordinal) ? 1 : 0;
                    Log($"Menus: {view.LogId}: {LogNames.HostTag()}'s Nurse {step} replayed here ({how}).");
                    break;
                case NurseFollowAction.Refocus:
                    int focus = next.Focus;
                    FriendUi.Apply(() => screen.DebugFocusOption(focus));
                    nurse.Refocused++;
                    break;
                case NurseFollowAction.SkipReveal:
                    FriendUi.Apply(screen.DebugSkipReveal);
                    Log($"Menus: {view.LogId}: the Nurse's sacrifice reveal here was ended so it takes {LogNames.HostTag()}'s next step.");
                    break;
                case NurseFollowAction.Fallback:
                    NurseFallback(view, nurse, next.Why);
                    break;
            }
        }
        catch (Exception ex)
        {
            NurseFallback(view, nurse, $"following {LogNames.HostTag()}'s step failed here ({ModLog.SafeErrorText(ex)})");
        }
    }

    /// <summary>
    /// The host's press showed the Butcher's typing line in full: this PC's too, with the game's own skip, once it's in step with
    /// the host's (<see cref="NurseLineFollow"/>; never on a line already shown, where a press plays the "next line" sound).
    /// </summary>
    private static void FollowButcher(View view, NurseView nurse, NurseProcedureScreen screen, SpeechProgress host)
    {
        if (nurse.LineFailed || !nurse.Line.Wanted(host, nurse.Follower!.Applied >= nurse.Steps!.Newest))
        {
            return;
        }

        try
        {
            if (nurse.Line.Press(host, NurseFollow.ButcherPhase(screen)))
            {
                screen._speech.Skip(screen._butcherAnchor);
                nurse.LinesShown++;
            }
        }
        catch (Exception ex)
        {
            nurse.LineFailed = true;
            ModLog.Warn($"Menus: {view.LogId}: following {LogNames.HostTag()}'s Butcher line failed here ({ModLog.SafeErrorText(ex)}); his lines type out on their own here.");
        }
    }

    /// <summary>
    /// The host's lineup from the procedure screen comes here as its own view (this PC's screen opens none, <c>OpenLineup</c> is
    /// skipped): while it's up, the screen's top canvases go under it, as the game's own <c>OpenLineup</c> puts them; they'd
    /// draw over it otherwise. Back when it goes. A plain look at the views: nothing allocated on a quiet frame.
    /// </summary>
    private void NurseLineup(View view, NurseView nurse, NurseProcedureScreen screen)
    {
        AbstractMenu? lineup = null;
        foreach (View other in _views.Values)
        {
            if (other.Type == LineupViews.Type && other.Mode == Mode.Real && other.Menu is { } menu && menu != null)
            {
                lineup = menu;
                break;
            }
        }

        if ((lineup != null) == (nurse.Lowered != null))
        {
            return;
        }

        try
        {
            if (lineup != null)
            {
                nurse.Lowered = new List<(Canvas Canvas, int Order)>(0);
                nurse.Lowered = NurseFollow.LowerUnder(screen, lineup);
                Log($"Menus: {view.LogId}: {LogNames.HostTag()}'s lineup is up over the procedure screen; {nurse.Lowered.Count} of its canvases go under it here, as on {LogNames.HostTag()}'s screen.");
            }
            else
            {
                List<(Canvas Canvas, int Order)> lowered = nurse.Lowered!;
                nurse.Lowered = null;
                NurseFollow.Restore(lowered);
                Log($"Menus: {view.LogId}: {LogNames.HostTag()}'s lineup closed; the procedure screen is on top here again.");
            }
        }
        catch (Exception ex)
        {
            ModLog.Warn($"Menus: {view.LogId}: moving the procedure screen's canvases for {LogNames.HostTag()}'s lineup failed here ({ModLog.SafeErrorText(ex)}).");
        }
    }

    /// <summary>The host's outcome into this PC's screen's state (logged); <paramref name="rebuild"/>: the screen is built again with it.</summary>
    private static void ApplyNurseOutcome(View view, NurseView nurse, NurseProcedureScreen screen, bool rebuild)
    {
        nurse.OutcomeApplied = true;
        try
        {
            string went = NurseFollow.ApplyOutcome(screen._state, nurse.Outcome!);
            screen._addedTalentFlashPending = true; // the game's commit sets it, for the flash at the result's next build (verified in the game's code)
            if (rebuild)
            {
                int focus = NurseFollow.Focus(screen);
                FriendUi.Apply(() => screen.DebugFocusOption(focus));
            }

            ModLog.Info($"Menus: {view.LogId}: the Nurse's commit was skipped here ({LogNames.HostTag()}'s outcome shown: {went}); the run changes with {LogNames.HostTag()}'s next checkpoint.");
        }
        catch (Exception ex)
        {
            ModLog.Warn($"Menus: {view.LogId}: couldn't put {LogNames.HostTag()}'s Nurse outcome into the screen here ({ModLog.SafeErrorText(ex)}); its result shows this PC's projection.");
        }
    }

    /// <summary>
    /// The real procedure screen here can't follow the host's: it closes (its own close puts this PC's event screen back;
    /// nothing here exits the event), and the host's screen shows as text over the event screen for the rest of the takeover.
    /// </summary>
    private void NurseFallback(View view, NurseView nurse, string why)
    {
        if (nurse.Fallen)
        {
            return;
        }

        nurse.Fallen = true;
        nurse.Why = why;
        nurse.Follower?.Fall(why);
        _tally.ScreenText(NurseScreenKind, why, wasReal: nurse.OpenedAt > 0);
        ModLog.Warn($"Menus: {view.LogId}: {LogNames.HostTag()}'s Nurse procedure screen falls back to text over the event screen here: {why}.");
        if (nurse.Screen is { } screen && screen != null)
        {
            try
            {
                FriendUi.Apply(() => NurseFollow.Close(screen));
            }
            catch (Exception ex)
            {
                ModLog.Warn($"Menus: {view.LogId}: the Nurse's procedure screen here couldn't be closed ({ModLog.SafeErrorText(ex)}).");
            }
        }

        view.Overlay = true;
        view.CardsHidden = false; // put away again once the screen's close has put them back (NurseTick)
        _panelDirty = true;
    }

    /// <summary>Closes the real procedure screen here if it's still up (its own close), and logs how its takeover went. Never throws.</summary>
    private static void CloseNurseScreen(View view, NurseView nurse, string why)
    {
        NurseProcedureScreen? screen = nurse.Screen;
        nurse.Screen = null;

        // "is null": a screen its own close already destroyed still gets the line below ("was closing").
        if (screen is null || nurse.Follower == null)
        {
            return;
        }

        nurse.Closing = screen;
        bool closed = false;
        try
        {
            if (screen != null)
            {
                FriendUi.Apply(() => closed = NurseFollow.Close(screen));
            }
        }
        catch (Exception ex)
        {
            ModLog.Warn($"Menus: {view.LogId}: the Nurse's procedure screen here couldn't be closed ({ModLog.SafeErrorText(ex)}).");
        }

        string outcome = nurse.OutcomeApplied ? $"; {LogNames.HostTag()}'s outcome shown" : nurse.CommitSeen ? $"; its commit skipped, {LogNames.HostTag()}'s outcome not shown" : string.Empty;
        ModLog.Info($"Menus: {view.LogId}: the Nurse's procedure screen here {(closed ? "closed" : "was closing")} ({why}) after {nurse.Replayed} of {LogNames.HostTag()}'s step(s) " +
            $"({nurse.Native} through its own handlers, {nurse.Developer} through its developer entry points, {nurse.Refocused} refocus(es), {nurse.LinesShown} Butcher line(s) shown in full){outcome}" +
            $"{(nurse.Fallen ? $"; it fell back to text: {nurse.Why}" : string.Empty)}.");
    }

    /// <summary>The host's commit's outcome for its open menu <paramref name="serial"/>, when it came (with its roster), else null.</summary>
    private NurseOutcome? Outcome(uint serial) =>
        _attachments.Get(serial, MenuAttachmentKind.NurseRoster) is { } attachment && attachment.TryGetNurseRoster(out NurseRosterData? roster) ? roster?.Outcome : null;

    /// <summary>The host's name for lines ("Alex"), or "the host".</summary>
    private string HostName => _host.Length > 0 ? _host : "the host";
}

/// <summary>A friend's state of the Nurse's procedure screen for one event menu view.</summary>
internal sealed class NurseView
{
    /// <summary>Why the procedure screen shows as text here ("" while it may be real).</summary>
    public string Why { get; set; } = string.Empty;

    /// <summary>The host's takeover is on and the real screen is wanted here.</summary>
    public bool Wanted { get; set; }

    /// <summary>When the takeover came.</summary>
    public double WantedSince { get; set; }

    /// <summary>The roster rebuilt for this view's newest plan, or null.</summary>
    public NurseBuilt? Built { get; set; }

    /// <summary>The procedure screen opened here, or null.</summary>
    public NurseProcedureScreen? Screen { get; set; }

    /// <summary>When it opened.</summary>
    public double OpenedAt { get; set; }

    /// <summary>What follows the host's steps on it.</summary>
    public NurseFollower? Follower { get; set; }

    /// <summary>The host's newest steps.</summary>
    public NurseStepsData? Steps { get; set; }

    /// <summary>The host's commit's outcome, once it came.</summary>
    public NurseOutcome? Outcome { get; set; }

    /// <summary>The screen here reached its commit point (the commit was skipped).</summary>
    public bool CommitSeen { get; set; }

    /// <summary>The host's outcome went into the screen here.</summary>
    public bool OutcomeApplied { get; set; }

    /// <summary>It fell back to text for the rest of the takeover.</summary>
    public bool Fallen { get; set; }

    /// <summary>The host's steps replayed here.</summary>
    public int Replayed { get; set; }

    /// <summary>Of those, through the screen's own native handlers.</summary>
    public int Native { get; set; }

    /// <summary>Of those, through its developer entry points (its input gate refused here).</summary>
    public int Developer { get; set; }

    /// <summary>Times the host's focus was put on the screen here.</summary>
    public int Refocused { get; set; }

    /// <summary>The result's effects that play once have played here (<see cref="FriendMenus.NurseBuilt"/>).</summary>
    public bool ResultPlayed { get; set; }

    /// <summary>The result's effects failed here: none is tried again (it shows without them).</summary>
    public bool ResultFailed { get; set; }

    /// <summary>True once the screen here was seen closing (its own timer, or the host's last press, replayed): it may go before the host's takeover ends.</summary>
    public bool ClosingSeen { get; set; }

    /// <summary>The screen that's closing here, or null: its close puts this PC's event screen back (<see cref="NurseFollow.Released"/>).</summary>
    public NurseProcedureScreen? Closing { get; set; }

    /// <summary>The cards went once that close was done: they don't come back (the host's event is exiting).</summary>
    public bool CardsAway { get; set; }

    /// <summary>What follows the Butcher's lines on the screen here.</summary>
    public NurseLineFollow Line { get; } = new();

    /// <summary>Butcher lines the host's presses showed in full here.</summary>
    public int LinesShown { get; set; }

    /// <summary>True once following his lines failed (they type out on their own).</summary>
    public bool LineFailed { get; set; }

    /// <summary>The canvases under the host's lineup now, with their orders, or null while it isn't up here.</summary>
    public List<(Canvas Canvas, int Order)>? Lowered { get; set; }
}
#endif
