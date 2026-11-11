#if HAS_INTEROP
using System;
using System.Collections.Generic;
using Il2CppBishopGames.UI;
using Il2CppRogue.NPC.NurseNPC;
using Il2CppRogue.NPC.NurseNPC.Procedures;
using Il2CppTape2Tape.Hockey.UI;
using T2TOnline.Core.Campaign;
using T2TOnline.Core.Diagnostics;
using T2TOnline.Plugin.Diagnostics;
using T2TOnline.Plugin.Startup;
using T2TOnline.Plugin.Streaming;
using Il2CppList = Il2CppSystem.Collections.Generic;

namespace T2TOnline.Plugin.Campaign.Menus;

/// <summary>
/// The host's side of the Nurse's procedure screen as a real view on friends (v1).
/// Nothing here changes the host's game: it reads, and its patches (<see cref="MenuPatches"/>) only record.
/// <list type="bullet">
/// <item>The roster (<see cref="Attach"/>): when a Nurse's event menu goes on the board, its roster and procedure state
/// (made by the event's <c>Initialize</c> before the menu showed) are read into a <see cref="NurseRosterData"/>
/// (<see cref="NurseRosters.Read"/>) and go with the menu as its <see cref="MenuAttachmentKind.NurseRoster"/> attachment,
/// right after its choices. One that can't be read goes with why, so friends show the procedure screen as text at
/// once.</item>
/// <item>The steps (<see cref="Pressed"/>, <see cref="Tick"/>): once the takeover's screen is found, each press its own
/// input handlers take (past their own gates: input open, no lineup over it, the UI's input stack) is noted; a frame later
/// it becomes a numbered step with the screen's stage and focus after it, and a change with no press (a held direction
/// repeating, the screen's own automatic steps) becomes a step of its own (<see cref="NurseStepWindow"/>). The newest
/// steps go as the menu's <see cref="MenuAttachmentKind.NurseSteps"/> attachment, at the takeover's start (with none) and
/// again with each new one (focus-only steps at most every <see cref="FocusSendSeconds"/>).</item>
/// <item>The outcome (<see cref="Committed"/>): once the screen's commit is done, what it did (the sacrifice's measured
/// deltas, the talents and bench room it settled on) goes with the roster again (format 2).</item>
/// </list>
/// One Nurse event menu at a time (the game has one event screen up at once); everything is forgotten when its takeover
/// ends, its menu closes or hosting stops.
/// </summary>
internal static class NurseHost
{
    /// <summary>Steps that only move the focus (a held direction) go to friends at most this often (the newest wins).</summary>
    public const double FocusSendSeconds = 0.25;

    /// <summary>The Butcher's speaker slot in the Nurse's event menu (<see cref="SpeechMirror"/>; the event's NPC is slot 0).</summary>
    public const int ButcherSlot = 1;

    // Step lines logged per takeover (the rest are counted in its summary).
    private const int MaxStepLogs = 30;

    private static IntPtr _menu;
    private static IntPtr _read;
    private static NurseRosterData? _roster;
    private static IntPtr _screen;
    private static NurseStepWindow? _window;
    private static bool _committed;
    private static bool _unsent;
    private static double _sentAt = double.NegativeInfinity;
    private static int _stepLogs;
    private static int _autoSteps;
    private static int _focusSteps;
    private static (bool Details, bool Lineup, bool Reveal) _before;

    /// <summary>True when the host's patches the friends' real screen needs went in (else the roster says so and friends show text).</summary>
    public static bool CaptureReady { get; set; }

    /// <summary>
    /// Host, as the event menu <paramref name="menu"/> goes on the board (<c>HostMenus.Track</c>, after its choices): a
    /// Nurse's roster is read (once per opening) and attached for friends, sent now when <paramref name="send"/>.
    /// <paramref name="real"/>: the event goes to friends for real (else its roster isn't needed: they show the event as
    /// text). Never throws: a roster that can't be read goes with why.
    /// </summary>
    public static void Attach(HostMenus host, EventChoiceMenu? menu, bool real, bool send)
    {
        if (menu == null || !real || menu.SourceNode?.TryCast<NurseEvent>() is not { } nurse)
        {
            return;
        }

        if (_menu != menu.Pointer || _roster == null)
        {
            Reset();
            _menu = menu.Pointer;
            ReadRoster(nurse);
        }

        AttachRoster(host, menu, send);
    }

    /// <summary>
    /// Host: <paramref name="menu"/>'s next choices went out (<c>HostMenus.EventChoices</c>). A Nurse's reroll runs its
    /// <c>Initialize</c> again (the menu's <c>RerollChoices</c> calls it before the new cards are made), so a new roster and
    /// state: read and sent again as at its open (a friend rebuilds it with the new choices).
    /// </summary>
    public static void Rolled(HostMenus host, EventChoiceMenu menu, bool send)
    {
        if (_menu != menu.Pointer || _roster == null || menu.SourceNode?.TryCast<NurseEvent>() is not { } nurse || nurse._roster?.Pointer == _read)
        {
            return;
        }

        ModLog.Info("Menus: the host's Nurse rolled its procedures again (a new roster); it goes to friends again.");
        Reset();
        ReadRoster(nurse);
        AttachRoster(host, menu, send);
    }

    /// <summary>
    /// Host, each frame while <paramref name="menu"/> is taken over (with friends in): finds the takeover's screen (its steps
    /// start then), settles the presses noted since the last frame into steps, and sends the newest steps when there are new
    /// ones (<paramref name="send"/>). A quiet frame checks the screen is alive and reads fields: its host menu and state (the
    /// interop wraps each object it returns) and two backing fields in place.
    /// </summary>
    public static void Tick(HostMenus host, EventChoiceMenu menu, bool send)
    {
        if (_roster is not { Problem: null } || _menu != menu.Pointer)
        {
            return;
        }

        NurseProcedureScreen? screen = NurseScreen.Screen(menu, out _);
        NurseProcedureState? state = screen?._state;
        if (screen == null || state == null)
        {
            return;
        }

        if (_window == null || _screen != screen.Pointer)
        {
            if (!NurseStepWindow.CanStart(menu.m_IsTakenOver, NurseScreen.CurrentProcedure(state)))
            {
                return; // its TakeOver hasn't run yet: the steps start from the procedure's first stage, a frame later
            }

            Start(screen, state);
            Send(host, menu, send, "its start");
            return;
        }

        IReadOnlyList<NurseStep> added = _window.Settle((int)state._CurrentStage_k__BackingField, state._FocusedOptionIndex_k__BackingField);
        bool focusOnly = true;
        foreach (NurseStep step in added)
        {
            Logged(step);
            focusOnly &= step.Kind == NurseStepKind.Focus;
        }

        _unsent |= added.Count > 0;
        if (_unsent && (!focusOnly || FrameClock.Seconds - _sentAt >= FocusSendSeconds))
        {
            Send(host, menu, send, added.Count > 0 ? added[^1].ToString() : "held steps");
        }
    }

    /// <summary>
    /// Host: one of the takeover screen's input handlers is about to take a press (its prefix). Noted (as a step a frame
    /// later) when it's the takeover's screen and its handler's own gate lets the press through: input open, no lineup over
    /// it, and the UI's input stack lets it act (the same <c>UIModule.CanProcessInput</c> call the handler makes).
    /// <paramref name="direction"/>: a direction press (its gate also needs the screen not closing and no replacement popup).
    /// </summary>
    public static void Pressed(NurseProcedureScreen screen, NurseStepKind kind, int arg = 0, bool direction = false)
    {
        if (!Following(screen) || !Gate(screen, direction))
        {
            return;
        }

        _window!.Pressed(kind, arg, reveal: screen.IsSacrificeRevealRunning());
    }

    /// <summary>Host: a named input action is about to reach the takeover's screen (its prefix): what it shows now, to compare after.</summary>
    public static void BeforeAction(NurseProcedureScreen screen)
    {
        _before = Following(screen) ? (screen._detailsHeld, screen._lineupOpen, Gate(screen, direction: false) && screen.IsSacrificeRevealRunning()) : default;
    }

    /// <summary>
    /// Host: a named input action reached the takeover's screen (its postfix): a step when it changed what the screen shows
    /// (the details hold, the lineup, the sacrifice's reveal ended), else nothing (every action's press and release reaches
    /// the handler; most do nothing there).
    /// </summary>
    public static void AfterAction(NurseProcedureScreen screen, string? action, bool pressed)
    {
        if (!Following(screen))
        {
            return;
        }

        (bool details, bool lineup, bool reveal) = _before;
        _before = default;
        if (pressed && !lineup && screen._lineupOpen)
        {
            _window!.Pressed(NurseStepKind.Lineup);
        }
        else if (pressed && reveal && !screen.IsSacrificeRevealRunning())
        {
            _window!.Pressed(NurseStepKind.SkipReveal, reveal: true);
        }
        else if (details != screen._detailsHeld && !string.IsNullOrEmpty(action))
        {
            _window!.Pressed(pressed ? NurseStepKind.Press : NurseStepKind.Release, action: SafeText.Clean(action, NurseStep.MaxActionBytes));
        }
    }

    /// <summary>
    /// Host: the takeover screen's commit check ran (its postfix). The first time the procedure is committed, what the commit
    /// did goes with the roster again (format 2), sent now when <paramref name="send"/>.
    /// </summary>
    public static void Committed(HostMenus host, NurseProcedureScreen screen, bool send)
    {
        if (_committed || _roster is not { Problem: null } roster || !screen._committed || screen._hostMenu is not { } menu || menu.Pointer != _menu)
        {
            return;
        }

        _committed = true;
        NurseOutcome outcome;
        try
        {
            outcome = Outcome(screen._state);
        }
        catch (Exception ex)
        {
            ModLog.Warn($"Menus: couldn't read the host's Nurse procedure's outcome ({ModLog.SafeErrorText(ex)}); friends show their screen's own result.");
            return;
        }

        NurseRosterData committed = roster.WithOutcome(outcome);
        if (committed.Check() is { } invalid)
        {
            ModLog.Warn($"Menus: the host's Nurse procedure's outcome can't go to friends ({invalid}); they show their screen's own result.");
            return;
        }

        _roster = committed;
        if (host.Attach(menu, MenuAttachmentKind.NurseRoster, serial => MenuAttachment.NurseRoster(serial, committed), $"outcome: {outcome}", send, out string problem))
        {
            ModLog.Info($"Menus: the host's Nurse procedure was committed here ({outcome}); its outcome goes to friends (their screens never commit: the run changes with the host's next checkpoint).");
        }
        else
        {
            ModLog.Warn($"Menus: the host's Nurse procedure's outcome can't be sent ({problem}); friends show their screen's own result.");
        }
    }

    /// <summary>Host: <paramref name="menu"/>'s takeover ended or it closed (null: hosting stopped): what went with it is forgotten.</summary>
    public static void Forget(EventChoiceMenu? menu)
    {
        if (menu != null && menu.Pointer != _menu)
        {
            return;
        }

        if (_window != null)
        {
            ModLog.Info($"Menus: the host's Nurse procedure screen sent {_window.Count} step(s) to friends ({_autoSteps} the screen's own, {_focusSteps} focus moves)" +
                $"{(_committed ? " and its outcome" : string.Empty)}.");
        }

        Reset();
        _menu = IntPtr.Zero;
        _read = IntPtr.Zero;
        _roster = null;
    }

    /// <summary>The roster of <paramref name="nurse"/> read for friends (logged).</summary>
    private static void ReadRoster(NurseEvent nurse)
    {
        _read = nurse._roster?.Pointer ?? IntPtr.Zero;
        if (!ModSettings.NurseRealScreen)
        {
            _roster = NurseRosterData.Unavailable(NurseRosterData.SwitchedOff);
            ModLog.Info("Menus: NurseRealScreen is off on this PC: friends see this Nurse's procedure screen as text over their event screen, not for real.");
            return;
        }

        _roster = Read(nurse);
        if (_roster.Problem == null)
        {
            ModLog.Info($"Menus: the host's Nurse roster goes to friends ({_roster}); their game rebuilds it with the game's own factory for the real procedure screen.");
        }
        else
        {
            ModLog.Warn($"Menus: the host's Nurse roster can't go to friends ({_roster.Problem}); they see its procedure screen as text.");
        }
    }

    /// <summary>
    /// The roster as the menu's attachment, sent now when <paramref name="send"/>. One that can't be made goes as why instead,
    /// so a friend never waits for it.
    /// </summary>
    private static void AttachRoster(HostMenus host, EventChoiceMenu menu, bool send)
    {
        NurseRosterData roster = _roster!;
        if (host.Attach(menu, MenuAttachmentKind.NurseRoster, serial => MenuAttachment.NurseRoster(serial, roster), roster.ToString(), send, out string problem))
        {
            return;
        }

        ModLog.Warn($"Menus: the host's Nurse roster can't be sent ({problem}); friends see its procedure screen as text.");
        NurseRosterData unavailable = _roster = NurseRosterData.Unavailable(NurseRosterData.NotSentLeadIn, problem);
        host.Attach(menu, MenuAttachmentKind.NurseRoster, serial => MenuAttachment.NurseRoster(serial, unavailable), unavailable.ToString(), send, out _);
    }

    /// <summary>The roster of <paramref name="nurse"/> as data, or why it can't go.</summary>
    private static NurseRosterData Read(NurseEvent nurse)
    {
        if (!CaptureReady)
        {
            return NurseRosterData.Unavailable(NurseRosterData.NotPatched);
        }

        NurseProcedureRoster? roster = nurse._roster;
        NurseProcedureState? state = nurse._state;
        if (roster == null || state == null)
        {
            return NurseRosterData.Unavailable(NurseRosterData.NoRoster);
        }

        try
        {
            NurseRosterData read = NurseRosters.Read(roster, state, NurseProcedureRoster.GetRequiredForwardId());
            return read.Check() is { } problem ? NurseRosterData.Unavailable(NurseRosterData.OutOfLimitsLeadIn, problem) : read;
        }
        catch (Exception ex)
        {
            return NurseRosterData.Unavailable(NurseRosterData.NotReadLeadIn, ModLog.SafeErrorText(ex));
        }
    }

    /// <summary>The takeover's screen was found: its steps start from its procedure, stage and focus now.</summary>
    private static void Start(NurseProcedureScreen screen, NurseProcedureState state)
    {
        // An instant procedure (heal all) commits inside its open, before its screen is found here: that stays known.
        bool committed = _committed;
        Reset();
        _committed = committed;
        _screen = screen.Pointer;
        int procedure = NurseScreen.CurrentProcedure(state);
        _window = new NurseStepWindow(procedure, (int)state._CurrentStage_k__BackingField, state._FocusedOptionIndex_k__BackingField);
        ModLog.Info($"Menus: the host's Nurse procedure screen is up ({NurseScreen.Title(procedure)}, procedure {procedure}, stage {_window.StartStage}, option {_window.StartFocus}); " +
            "friends open their own and follow its steps.");

        // The Butcher's lines on it: a press that shows one in full goes to friends on the speech mirror's menu value (his lines
        // wait for no press; they start with the screen's stage, so with the steps).
        if (screen._hostMenu is { } menu && screen._speech is { } speech && screen._butcherAnchor is { } anchor)
        {
            SpeechMirror.HostBegin(menu, ButcherSlot, speech, anchor, "Butcher");
        }
    }

    /// <summary>The newest steps as the menu's attachment, sent now when <paramref name="send"/>.</summary>
    private static void Send(HostMenus host, EventChoiceMenu menu, bool send, string about)
    {
        NurseStepsData data = _window!.Data();
        _unsent = false;
        _sentAt = FrameClock.Seconds;
        if (!host.Attach(menu, MenuAttachmentKind.NurseSteps, serial => MenuAttachment.NurseSteps(serial, data), about, send, out string problem))
        {
            ModLog.Warn($"Menus: the host's Nurse procedure steps can't be sent ({problem}); friends' screens fall back to text.");
        }
    }

    /// <summary>A step for the log (the first few of a takeover), and counted.</summary>
    private static void Logged(NurseStep step)
    {
        _autoSteps += step.Kind == NurseStepKind.Auto ? 1 : 0;
        _focusSteps += step.Kind == NurseStepKind.Focus ? 1 : 0;
        if (step.Kind == NurseStepKind.Focus || _stepLogs >= MaxStepLogs)
        {
            return;
        }

        _stepLogs++;
        ModLog.Info($"Menus: Nurse procedure {step}{(_stepLogs == MaxStepLogs ? " (further steps are counted)" : string.Empty)}.");
    }

    /// <summary>True when <paramref name="screen"/> is the takeover's screen whose steps go to friends (its window started).</summary>
    private static bool Following(NurseProcedureScreen screen)
    {
        if (_roster is not { Problem: null } || screen._hostMenu is not { } menu || menu.Pointer != _menu)
        {
            return false;
        }

        if (_window == null && screen._state is { } state)
        {
            // The screen the cards warmed (NurseEvent.OnChoiceItemCreated's Prewarm) is bound to the same event screen with
            // its input registered, so every press before the pick reaches its handlers too, before any procedure is set
            // (stage 0, no procedure; verified in the game's code). Those aren't the procedure's steps: the window starts
            // only once the takeover has run (the menu taken over, the procedure set by TakeOver).
            if (!NurseStepWindow.CanStart(menu.m_IsTakenOver, NurseScreen.CurrentProcedure(state)))
            {
                return false;
            }

            // A press in the takeover's frame before Tick found the screen: its start goes out with the next Tick.
            Start(screen, state);
            _unsent = true;
        }

        return _screen == screen.Pointer;
    }

    /// <summary>The native handlers' own gate (<c>OnNativeConfirm</c>, <c>OnNativeDirection</c>), read only.</summary>
    private static bool Gate(NurseProcedureScreen screen, bool direction)
    {
        if ((direction && (screen._closing || screen.IsReplacementPopup)) || !screen._inputOpen || screen._lineupOpen)
        {
            return false;
        }

        UIModule? module = screen._uiModule;
        return module == null || module.CanProcessInput(new IUIInputHandler(screen.Pointer));
    }

    /// <summary>What the committed procedure did, from its state.</summary>
    private static NurseOutcome Outcome(NurseProcedureState state)
    {
        var chips = new List<NurseSacrificeChip>();
        Il2CppList.List<NurseProcedureState.SacrificeResultChip>? read = state._sacrificeResultChips;
        for (int i = 0; read != null && i < read.Count && chips.Count < NurseRosterData.MaxChips; i++)
        {
            NurseProcedureState.SacrificeResultChip? chip = read[i];
            Il2CppSystem.Collections.ObjectModel.ReadOnlyCollection<int>? deltas = chip?.AttributeDeltas?.TryCast<Il2CppSystem.Collections.ObjectModel.ReadOnlyCollection<int>>();
            if (chip == null || deltas == null)
            {
                throw new InvalidOperationException($"sacrifice chip {i + 1} has no stat deltas the mod can read");
            }

            var values = new int[Math.Min(deltas.Count, NurseRosterData.MaxChips)];
            for (int d = 0; d < values.Length; d++)
            {
                values[d] = Math.Clamp(deltas[d], -NurseRosterData.MaxDelta, NurseRosterData.MaxDelta);
            }

            chips.Add(new NurseSacrificeChip { Player = chip.PlayerIndex, Deltas = values });
        }

        return new NurseOutcome
        {
            Procedure = Math.Max(0, NurseScreen.CurrentProcedure(state)),
            Chips = chips,
            Replacement = NurseRosters.Identity(state._replacementTalent),
            InjuryReward = NurseRosters.Identity(state._InjuryTalentReward_k__BackingField),
            ImmunityReward = NurseRosters.Identity(state._ImmunityTalentReward_k__BackingField),
            CloneBenchRoom = state._CloneBenchRoom_k__BackingField,
        };
    }

    private static void Reset()
    {
        _screen = IntPtr.Zero;
        _window = null;
        _committed = false;
        _unsent = false;
        _sentAt = double.NegativeInfinity;
        _stepLogs = 0;
        _autoSteps = 0;
        _focusSteps = 0;
        _before = default;
    }
}
#endif
