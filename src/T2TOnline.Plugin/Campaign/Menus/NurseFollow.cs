#if HAS_INTEROP
using System;
using System.Collections.Generic;
using System.Linq;
using Il2CppBishopGames.UI;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppRogue.NPC.NurseNPC.Procedures;
using Il2CppTape2Tape.Hockey.UI;
using Il2CppTape2Tape.Speech;
using T2TOnline.Core.Campaign;
using T2TOnline.Core.Diagnostics;
using T2TOnline.Plugin.Diagnostics;
using UnityEngine;
using UnityEngine.UI;
using Il2CppList = Il2CppSystem.Collections.Generic;

namespace T2TOnline.Plugin.Campaign.Menus;

/// <summary>
/// The game calls a friend's real Nurse procedure screen is driven with (v1),
/// for <see cref="FriendMenus"/>: the game's own <c>NurseProcedureScreen.Open</c> over this PC's event screen (the same call
/// the host's pick makes), the host's steps replayed through the screen's own native input handlers (its developer entry
/// points when the handler's own gate refuses here), the host's outcome put into its state with the state's own setters,
/// and its own <c>Close</c>, which puts this PC's event screen back and never exits the event here (it exits only when its
/// host menu's <c>m_IsTakenOver</c> is set, verified in the game's code at <c>CloseHostAsync</c>, which nothing sets on a friend). Each call
/// may throw: the caller catches it and falls back to text.
/// </summary>
internal static class NurseFollow
{
    /// <summary>The game's procedure screen opened over <paramref name="menu"/> with this PC's rebuilt roster and state.</summary>
    public static NurseProcedureScreen Open(EventChoiceMenu menu, NurseBuilt built, int procedure)
    {
        if (!Enum.IsDefined(typeof(NurseProcedureState.Procedure), procedure))
        {
            throw new InvalidOperationException($"the host's procedure {procedure} is one this game doesn't know");
        }

        NurseProcedureScreen? screen = NurseProcedureScreen.Open(menu, built.Roster, built.State, (NurseProcedureState.Procedure)procedure);
        return screen ?? throw new InvalidOperationException("the game opened no procedure screen");
    }

    /// <summary>
    /// The procedure screen an <see cref="Open"/> that threw left on <paramref name="menu"/>, or null: past the point it took its
    /// instance, its take-over may have hidden this PC's event screen, and only its own close puts that back. Never throws.
    /// </summary>
    public static NurseProcedureScreen? LeftOpen(EventChoiceMenu menu)
    {
        try
        {
            foreach (UnityEngine.Object any in Resources.FindObjectsOfTypeAll(Il2CppType.Of<NurseProcedureScreen>()))
            {
                NurseProcedureScreen? screen = any?.TryCast<NurseProcedureScreen>();
                if (screen != null && screen._opened && screen._hostMenu is { } host && host.Pointer == menu.Pointer)
                {
                    return screen;
                }
            }
        }
        catch (Exception ex)
        {
            ModLog.Warn($"Menus: looking for a Nurse procedure screen left open here failed ({ModLog.SafeErrorText(ex)}).");
        }

        return null;
    }

    /// <summary>The stage the game's <c>TakeOver</c> starts <paramref name="procedure"/> at (verified in the game's code:
    /// heal all at its result, the rest at the skater).</summary>
    public static int FirstStage(int procedure) =>
        (int)(procedure == (int)NurseProcedureState.Procedure.HealAll ? NurseProcedureState.Stage.Result : NurseProcedureState.Stage.Player);

    /// <summary>True when <paramref name="screen"/> is at its result: the one stage its commit check would commit at (verified in the game's
    /// code).</summary>
    public static bool AtResult(NurseProcedureScreen screen) => screen._state is { } state && state._CurrentStage_k__BackingField == NurseProcedureState.Stage.Result;

    /// <summary>The Butcher's bubble on <paramref name="screen"/> as this PC's game has it, read as the speech mirror reads a speaker's.</summary>
    public static SpeechPhase ButcherPhase(NurseProcedureScreen screen)
    {
        SpeechBubbleController? speech = screen._speech;
        SpeechBubbleAnchor? anchor = screen._butcherAnchor;
        Il2CppList.Dictionary<SpeechBubbleAnchor, Il2CppList.List<SpeechBubbleController.ActiveBubble>>? active = speech == null ? null : speech.m_Active;
        if (active == null || anchor == null || !active.ContainsKey(anchor))
        {
            return SpeechPhase.None;
        }

        Il2CppList.List<SpeechBubbleController.ActiveBubble>? bubbles = active[anchor];
        int count = bubbles?.Count ?? 0;
        SpeechBubbleView? view = count > 0 ? bubbles![count - 1]?.View : null;
        if (view == null)
        {
            return SpeechPhase.None;
        }

        return view.m_Phase switch
        {
            SpeechBubbleView.Phase.Typing => SpeechPhase.Typing,
            SpeechBubbleView.Phase.Waiting => SpeechPhase.Waiting,
            _ => SpeechPhase.Idle,
        };
    }

    /// <summary>
    /// The host's lineup is up here as its own view over <paramref name="screen"/>: the four canvases the game's own
    /// <c>OpenLineup</c> lowers (the screen's root, the speech canvas and the two attribute cursors'; verified in the game's code)
    /// go right under <paramref name="lineup"/>, as the game puts them under the lineup it opens itself
    /// (verified in the game's code: one below the next menu's order). Returns each with the order it had, for
    /// <see cref="Restore"/>.
    /// </summary>
    public static List<(Canvas Canvas, int Order)> LowerUnder(NurseProcedureScreen screen, AbstractMenu lineup)
    {
        var lowered = new List<(Canvas Canvas, int Order)>(4);
        Canvas? own = lineup.m_Canvas;
        if (own == null)
        {
            return lowered;
        }

        int order = own.sortingOrder - 1;
        void Lower(Canvas? canvas)
        {
            if (canvas != null)
            {
                lowered.Add((canvas, canvas.sortingOrder));
                canvas.sortingOrder = order;
            }
        }

        NursePromptBarView? bar = screen._bar;
        Lower(bar == null ? null : bar.GetComponentInParent<Canvas>()?.rootCanvas);
        Lower(screen._speechCanvas);
        Image? cursor = screen._attributeCursor;
        Lower(cursor == null ? null : cursor.GetComponent<Canvas>());
        cursor = screen._secondAttributeCursor;
        Lower(cursor == null ? null : cursor.GetComponent<Canvas>());
        return lowered;
    }

    /// <summary>The host's lineup went: the canvases <see cref="LowerUnder"/> lowered go back to their orders.</summary>
    public static void Restore(List<(Canvas Canvas, int Order)> lowered)
    {
        foreach ((Canvas canvas, int order) in lowered)
        {
            if (canvas != null)
            {
                canvas.sortingOrder = order;
            }
        }
    }

    /// <summary>True while <paramref name="screen"/> takes input (its input open, not closing, no lineup of its own over it).</summary>
    public static bool Ready(NurseProcedureScreen screen) => screen._inputOpen && !screen._closing && !screen._lineupOpen;

    /// <summary>The screen's stage as the state's number, or -1.</summary>
    public static int Stage(NurseProcedureScreen screen) => screen._state is { } state ? (int)state._CurrentStage_k__BackingField : -1;

    /// <summary>The screen's focused option as the state has it, or -1.</summary>
    public static int Focus(NurseProcedureScreen screen) => screen._state is { } state ? state._FocusedOptionIndex_k__BackingField : -1;

    /// <summary>True once the screen's close has put this PC's event screen back (or the screen is gone).</summary>
    public static bool Released(NurseProcedureScreen? screen) => screen == null || screen._hostReleased;

    /// <summary>
    /// Replays one of the host's steps on <paramref name="screen"/>: through its own native handler when the handler's own gate
    /// lets it act here (the same <c>UIModule.CanProcessInput</c> call the handler makes), else its developer entry point.
    /// The sacrifice's reveal is matched first: a press that only ended the host's reveal ends this PC's (or does nothing once
    /// it's over here), and one pressed after the host's reveal ended ends this PC's first. Returns how it went, for the log.
    /// </summary>
    public static string Replay(NurseProcedureScreen screen, NurseStep step)
    {
        bool reveal = screen.IsSacrificeRevealRunning();
        if (step.Kind == NurseStepKind.SkipReveal || (step.Reveal && step.Kind is NurseStepKind.Confirm or NurseStepKind.Direction))
        {
            // The host's press only ended its reveal.
            if (reveal)
            {
                screen.DebugSkipReveal();
                return "the reveal ended";
            }

            return "nothing to end (the reveal here was over)";
        }

        string prefix = string.Empty;
        if (reveal && step.Kind is NurseStepKind.Confirm or NurseStepKind.Direction or NurseStepKind.Back)
        {
            // The host's reveal had ended by itself: this PC's ends first.
            screen.DebugSkipReveal();
            prefix = "the reveal ended, then ";
        }

        bool native = Gate(screen);
        switch (step.Kind)
        {
            case NurseStepKind.Confirm:
                if (native)
                {
                    screen.OnNativeConfirm();
                    return prefix + "native confirm";
                }

                screen.DebugSelectOption(Focus(screen));
                return prefix + "its developer select (its input gate refused here)";
            case NurseStepKind.Back:
                if (native)
                {
                    screen.OnNativeBack();
                    return prefix + "native back";
                }

                screen.DebugBack();
                return prefix + "its developer back (its input gate refused here)";
            case NurseStepKind.Direction:
                if (native && step.Arg < (int)AbstractNavigable.EDirection.LENGTH)
                {
                    // One move: the press starts the screen's hold-repeat, the release stops it at once (the host's repeats come as
                    // focus steps).
                    var direction = (AbstractNavigable.EDirection)step.Arg;
                    screen.OnNativeDirection(direction);
                    screen.OnNativeDirectionReleased(direction);
                    return prefix + $"native {direction.ToString().ToLowerInvariant()}";
                }

                screen.DebugFocusOption(step.Focus);
                return prefix + "its developer focus (its input gate refused here)";
            case NurseStepKind.Press:
                if (native)
                {
                    screen.OnNativeInputPressed(step.Action!, null!);
                    return $"native {HostText.KeyForLog(step.Action)} down";
                }

                return "left out (its input gate refused here: the details hold only shows a tooltip)";
            case NurseStepKind.Release:
                screen.OnNativeInputReleased(step.Action!, null!);
                return $"native {HostText.KeyForLog(step.Action)} up";
            default:
                return "nothing to replay";
        }
    }

    /// <summary>
    /// Puts the host's commit's <paramref name="outcome"/> into <paramref name="state"/> with the state's own setters: the
    /// sacrifice's measured deltas per skater (<c>SetSacrificeOutcome</c>: the result chips <c>PrepareResult</c> made) and the bench room
    /// (<c>SetCloneBenchRoom</c>). The talents it settled on are the ones this PC's state has made from the same roster: any
    /// that differs is said. Returns what went in, for the log.
    /// </summary>
    public static string ApplyOutcome(NurseProcedureState state, NurseOutcome outcome)
    {
        var parts = new List<string>(4);
        int procedure = NurseScreen.CurrentProcedure(state);
        if (procedure != outcome.Procedure)
        {
            throw new InvalidOperationException($"the host committed procedure {outcome.Procedure}, this PC's screen does procedure {procedure}");
        }

        if (outcome.Chips.Count > 0)
        {
            // An int[][] by skater, as the game's own commit hands them over (verified in the game's code); a skater with
            // none stays null. Each fresh row is stored through the write barrier (GameArrays).
            int players = state.Players?.Length ?? 0;
            var deltas = new Il2CppReferenceArray<Il2CppStructArray<int>>(players);
            foreach (NurseSacrificeChip chip in outcome.Chips.Where(c => c.Player < players))
            {
                var row = new Il2CppStructArray<int>(chip.Deltas.ToArray());
                GameArrays.Store(deltas, chip.Player, row.Pointer);
            }

            state.SetSacrificeOutcome(new Il2CppList.IReadOnlyList<Il2CppStructArray<int>>(deltas.Pointer));
            parts.Add($"the sacrifice's deltas for {outcome.Chips.Count} skater(s)");
        }

        if (state._CloneBenchRoom_k__BackingField != outcome.CloneBenchRoom)
        {
            state.SetCloneBenchRoom(outcome.CloneBenchRoom);
            parts.Add(outcome.CloneBenchRoom ? "room on the bench" : "no room on the bench");
        }

        string? Differs(string what, string? host, string? here) => host == here ? null : $"{what} {SafeText.Clean(here ?? "none", 40)} here, {(host == null ? "none" : HostText.KeyForLog(host))} on the host's";
        string? replacement = outcome.Procedure == (int)NurseProcedureState.Procedure.ReplaceTalent
            ? Differs("replacement", outcome.Replacement, NurseRosters.Identity(state._replacementTalent))
            : null;
        string? differences = replacement ?? Differs("injury reward", outcome.InjuryReward, NurseRosters.Identity(state._InjuryTalentReward_k__BackingField)) ??
            Differs("immunity reward", outcome.ImmunityReward, NurseRosters.Identity(state._ImmunityTalentReward_k__BackingField));
        string went = parts.Count > 0 ? string.Join(" and ", parts) : "nothing to put in (the result is the same as the projection here)";
        return differences == null ? went : $"{went}; differs: {differences}";
    }

    /// <summary>The screen's own close (it puts this PC's event screen back; it never exits the event here). False when it was already closing.</summary>
    public static bool Close(NurseProcedureScreen screen)
    {
        if (screen._closing)
        {
            return false;
        }

        screen.Close();
        return true;
    }

    /// <summary>The handlers' own gate, read only (<c>OnNativeConfirm</c>).</summary>
    private static bool Gate(NurseProcedureScreen screen)
    {
        UIModule? module = screen._uiModule;
        return Ready(screen) && (module == null || module.CanProcessInput(new IUIInputHandler(screen.Pointer)));
    }
}
#endif
