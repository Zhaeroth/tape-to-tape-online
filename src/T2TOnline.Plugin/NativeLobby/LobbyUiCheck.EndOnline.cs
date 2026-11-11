#if HAS_INTEROP
using System.Collections.Generic;
using System.Globalization;
using Il2CppBishopGames.UI;
using Il2CppTape2Tape.Customization.UI;
using Il2CppTape2Tape.Hockey.UI;
using T2TOnline.Core.Input;
using T2TOnline.Core.Lobby;
using T2TOnline.Core.OnlineSettings;
using T2TOnline.Plugin.Campaign;
using T2TOnline.Plugin.Campaign.Menus;
using T2TOnline.Plugin.NativeLobby.Widgets;
using T2TOnline.Plugin.NativeUi;
using T2TOnline.Plugin.Online;
using T2TOnline.Plugin.Streaming;

namespace T2TOnline.Plugin.NativeLobby;

// padwalk's endonline=on (Core's UiCheckPlan.PadWalkWithEndOnline): End Online Game pressed by the pad in the lobby, and at once the title's
// own New Run. The session ends from Settings > Online with Settings still up over the title; the co-op slot the lobby hosted stays the
// game's run file until the title is idle, so the step lets it go by the very call a restore from the Backups screen makes
// (RunSync.FreeSlotForRestore: the run file pointed back at the solo run, run saves held until the title reloads it); no restore runs, the
// tour's holds refuse every write. Then the title's New Run: the game's handler asks "replace your run?" only when the title thinks a run
// is ongoing (TitleScreen.<NewRun>g__StartNewRun reads m_HasOngoingCampaign before its yes/no, and only its Yes deletes the run;
// verified in the game's code), else it goes to the squad screen. The step answers only by B: on the yes/no the game's back input is
// Cancel (PromptTemplateMenu.OnBackInput: CloseWithResult(Cancel); the handler then ends the start with nothing deleted), on the squad
// screen it backs out. Anything else it doesn't recognise gets no press: the step fails away from the title's Main and the tour stops.
internal static partial class LobbyUiCheck
{
    // The title's Campaign group (TitleScreen.EButtonGroup: PressToStart, Main, Campaign, ...).
    private const int CampaignGroupValue = 2;

    // The run sync line a held press on the title's New Run writes (Campaign/RunSync.cs, TitleRunPress).
    private const string NewRunHeldStem = "Run sync: New Run on the title does nothing for a moment";

    // The line it writes when a press goes through late, the title still not reloaded.
    private const string NewRunLateStem = "Run sync: the title's campaign buttons still aren't reloaded for the run file as it is now";

    private static double _settingsClosedAt = -1;
    private static long _endOnlineLogFrom = -1;

    private static void AddEndOnlineSteps()
    {
        Steps[UiCheckPlan.EndOnlineStep] = StepEndOnline;
        Steps[UiCheckPlan.TitleNewRunStep] = StepTitleNewRun;
    }

    /// <summary>A lobby by the pad; RT, the Online tab, End Online Game and its End; Settings over the title; the slot let go; B; back to Main.</summary>
    private static IEnumerable<Wait> StepEndOnline()
    {
        OnlineSession session = Sd();
        _settingsClosedAt = -1;
        _endOnlineLogFrom = SessionLogLength();
        foreach (Wait wait in OnlineRowOnMain())
        {
            yield return wait;
        }

        foreach (Wait wait in OpenGroup("A on Online", null))
        {
            yield return wait;
        }

        if (_walkOk && Cursor() != "HostLobby")
        {
            foreach (Wait wait in Press("up", PadButtons.DPadUp, () => Cursor() == "HostLobby", "the cursor on Host Lobby"))
            {
                yield return wait;
            }
        }

        foreach (Wait wait in OpenLobby("A on Host Lobby"))
        {
            yield return wait;
        }

        if (!_walkOk)
        {
            yield break;
        }

        yield return Soft(() => session.Actions.Snapshot().StartAllowed, 8, "Start allowed");
        int hosted = RunSync.Instance?.RunFileSlot ?? -1;
        StepNote($"hosting: the game's run file is {SlotText(hosted)}");
        foreach (Wait wait in Trigger("RT", right: true, () => Settings() is { IsHiding: false } && SettingsOverLobby(), "the game's Settings over the lobby", within: 8))
        {
            yield return wait;
        }

        yield return Seconds(1.2);
        foreach (Wait wait in RbToOnline())
        {
            yield return wait;
        }

        WalkCheck("End Online Game in the lobby", () => Settings() is { } menu && SettingsTab.Shows(menu, SettingsRowId.EndOnlineGame), "shown for the host in a session");
        foreach (Wait wait in ToRow(SettingsRowId.EndOnlineGame))
        {
            yield return wait;
        }

        foreach (Wait wait in WalkPress("A on End Online Game", PadButtons.A, () => LobbyPrompts.IsOpen && !LobbyPrompts.IsAsking && Top()?.TryCast<CustomizableYesNoPrompt>() != null,
            "the game's yes/no: end the online game?", within: 4))
        {
            yield return wait;
        }

        if (!_walkOk)
        {
            yield break;
        }

        yield return Seconds(0.9);
        StepNote($"the question: cursor {Cursor()} (Stay is its first answer)");
        foreach (Wait wait in Snap("end_asks", 0))
        {
            yield return wait;
        }

        foreach (Wait wait in OtherAnswer())
        {
            yield return wait;
        }

        foreach (Wait wait in Press("A on End", PadButtons.A, () => session.Actions.Snapshot().Role == LobbyRole.None && !LobbyPrompts.IsOpen, "the session over", within: 8))
        {
            yield return wait;
        }

        if (!_walkOk)
        {
            yield break;
        }

        yield return Soft(() => !LobbyShell.IsOpen, ShellSeconds, "the lobby gone under Settings");
        Check("Settings after End", () => Settings() != null && !LobbyShell.IsOpen && session.Actions.Snapshot().Role == LobbyRole.None,
            "the session over, the lobby gone, Settings still up over the title");
        foreach (Wait wait in Snap("settings_after_end", 6))
        {
            yield return wait;
        }

        // The co-op slot the lobby hosted is the game's run file until the title is idle: let go here by the call a restore makes, with no restore.
        if (RunSync.Instance is not { } sync)
        {
            Problem("run sync isn't running: no run file to let go");
        }
        else
        {
            int active = sync.RunFileSlot;
            if (active <= 0)
            {
                StepNote($"the run file is {SlotText(active)} already: nothing to let go (the title went idle before the call)");
            }
            else
            {
                bool freed = sync.FreeSlotForRestore(active);
                StepNote($"the call a restore makes for co-op slot {active} (no restore runs): {(freed ? "let go" : "refused")}; the run file is now {SlotText(sync.RunFileSlot)}");
                Check("the hosted slot's run file let go", () => freed && sync.RunFileSlot == 0, "the solo run the game's run file again, Settings still up");
            }
        }

        if (Settings() != null)
        {
            foreach (Wait wait in WalkPress("B on Settings", PadButtons.B, () => Settings() == null && Top() == null && !LobbyShell.IsOpen, "Settings closed, nothing over the title", within: OpenSeconds))
            {
                yield return wait;
            }
        }

        _settingsClosedAt = FrameClock.Seconds;
        StepNote($"after Settings: {Where()}; the run file {SlotText(RunSync.Instance?.RunFileSlot ?? -1)}");
        if (!_walkOk)
        {
            yield break;
        }

        foreach (Wait wait in BackToMain())
        {
            yield return wait;
        }
    }

    /// <summary>At once after the end: Campaign, New Run by the pad; its question answered by B, or its squad screen backed out of by B; back to Main.</summary>
    private static IEnumerable<Wait> StepTitleNewRun()
    {
        long logFrom = _endOnlineLogFrom >= 0 ? _endOnlineLogFrom : SessionLogLength();
        string when = _settingsClosedAt >= 0
            ? string.Create(CultureInfo.InvariantCulture, $"{FrameClock.Seconds - _settingsClosedAt:0.0} s after Settings closed")
            : "no End Online Game before it";
        StepNote($"{when}: the title's run file {SlotText(RunSync.Instance?.RunFileSlot ?? -1)}, reloading {Title().m_IsRefreshingCampaignState}");
        foreach (Wait wait in MainTo("Campaign"))
        {
            yield return wait;
        }

        foreach (Wait wait in WalkPress("A on Campaign", PadButtons.A, CampaignGroup, "the Campaign group"))
        {
            yield return wait;
        }

        // The cursor onto New Run by up (the title's lists wrap): never A anywhere else in this group, which holds the player's Continue and Abandon Run.
        for (int i = 0; i < 8 && _walkOk && CampaignGroup() && !OnNewRun(); i++)
        {
            string at = Cursor();
            foreach (Wait wait in WalkTap(PadButtons.DPadUp))
            {
                yield return wait;
            }

            yield return Soft(() => Cursor() != at, 1, "the cursor to move");
            yield return Seconds(WalkSettleSeconds);
        }

        WalkCheck("the cursor on New Run", () => CampaignGroup() && OnNewRun(), "on the Campaign group's New Run");
        if (!_walkOk)
        {
            yield break;
        }

        // New Run pressed until it answers: a press held while the title reloads for the run file opens nothing (its line says so).
        bool ongoing = false;
        bool opened = false;
        for (int press = 1; press <= 3 && !opened; press++)
        {
            if (!MayPressOrStop("A on New Run", () => CampaignGroup() && OnNewRun() && Top() == null && !Title().m_IsStartingRun))
            {
                yield break;
            }

            ongoing = Title().m_HasOngoingCampaign; // what the game's handler reads for its question, a moment from now
            bool refreshing = Title().m_IsRefreshingCampaignState;
            foreach (Wait wait in Tap(PadButtons.A))
            {
                yield return wait;
            }

            yield return Soft(() => Top() != null || Title().m_IsStartingRun, 2, "New Run's question or squad screen");
            opened = Top() != null || Title().m_IsStartingRun;
            StepNote($"A on New Run ({press}): {(ongoing ? "the title shows a run ongoing in the solo run file (its question comes first)" : "the title shows no run ongoing in the solo run file (the squad screen comes)")}, reloading {refreshing}; {(opened ? "it answered" : "nothing opened")}; {Where()}");
            if (!opened)
            {
                yield return Soft(() => !Title().m_IsRefreshingCampaignState && CampaignGame.IsIdle(Title()), 4, "the title reloaded");
                yield return Seconds(0.5);
            }
        }

        int held = SessionLogCount(logFrom, NewRunHeldStem);
        int late = SessionLogCount(logFrom, NewRunLateStem);
        StepNote($"the title's hold line: {(held > 0 ? "came" : held == 0 ? "didn't come (the title had reloaded before the press)" : "the log couldn't be read")}{(late > 0 ? "; a press went through late, the title still not reloaded" : string.Empty)}");
        if (!opened)
        {
            Fail($"New Run answered no press in 3: {Where()}");
            _walkOk = false;
            yield break;
        }

        // What New Run opened, past its transition: its question when the title thought a run ongoing, else its squad screen.
        yield return Soft(() => Top() is { } top && !IsWoosh(top), 8, "what New Run opened");
        yield return Seconds(1);
        AbstractMenu? opening = Top();
        bool question = ongoing && opening?.TryCast<CustomizableYesNoPrompt>() != null && !LobbyPrompts.IsOpen;
        bool squad = !ongoing && opening?.TryCast<ChooseMetaMenu>() != null;
        StepNote($"New Run opened {(opening == null ? "nothing" : MenuGame.MenuId(opening))}: {(question ? "the game's question before a run is replaced" : squad ? "the game's squad screen" : "not a screen the step answers")}");
        if (!question && !squad)
        {
            Fail($"New Run opened {(opening == null ? "nothing on top" : MenuGame.MenuId(opening))} with a run ongoing {ongoing}: the step answers only the game's question (a run ongoing) or its squad screen (none): no press; the tour stops here");
            _walkOk = false;
            yield break;
        }

        foreach (Wait wait in Snap(question ? "newrun_asks" : "newrun_squad", 0))
        {
            yield return wait;
        }

        if (question)
        {
            // B is the game's Cancel whatever the cursor is on: the handler ends the start, nothing deleted. Only on that question.
            if (!MayPressOrStop("B on New Run's question", () => Top()?.TryCast<CustomizableYesNoPrompt>() != null && !LobbyPrompts.IsOpen))
            {
                yield break;
            }

            foreach (Wait wait in Press("B on New Run's question", PadButtons.B, () => Top() == null && !Title().m_IsStartingRun, "Cancel: the question gone, no run started, the title live", within: 4))
            {
                yield return wait;
            }
        }
        else
        {
            for (int i = 1; i <= 6 && Top() is { } top && (top.TryCast<ChooseMetaMenu>() != null || IsWoosh(top)); i++)
            {
                foreach (Wait wait in Tap(PadButtons.B))
                {
                    yield return wait;
                }

                yield return Soft(() => Top() == null && !Title().m_IsStartingRun, 3, "the title");
                StepNote($"B {i} on the squad screen: {Where()}");
            }

            Expect("the title after the squad screen", () => Top() == null && !Title().m_IsStartingRun && !Title().m_IsExiting, "the title live, no run started");
        }

        if (!_walkOk)
        {
            yield break;
        }

        yield return Soft(() => CampaignGroup() || OnMain(), 3, "the title's Campaign group");
        StepNote($"New Run answered after the end: {Where()}; the run file {SlotText(RunSync.Instance?.RunFileSlot ?? -1)}");

        // Back to Main by B on the Campaign group, once the title's own back handler takes it: a screen that has begun to close is no
        // longer on top but still counts over the title until it is destroyed, and a B then is taken and lost (Core's TitleBack). At most
        // three presses, each only on the Campaign group with the title taking B; Main not coming after them fails the step, away from
        // Main, and the tour stops.
        for (int press = 1; press <= 3 && !OnMain() && _walkOk; press++)
        {
            yield return Soft(() => CampaignGroup() && TitleTakesBack() == null, 6, "the Campaign group taking B");
            StepNote($"before B {press} on the Campaign group: {TitleTakesBack() ?? "the title takes B"}; {Where()}");
            if (!MayPressOrStop($"B {press} on the Campaign group", () => CampaignGroup() && TitleTakesBack() == null))
            {
                break;
            }

            foreach (Wait wait in Tap(PadButtons.B))
            {
                yield return wait;
            }

            yield return Soft(OnMain, 2, "Main");
            StepNote($"B {press} on the Campaign group: {Where()}");
        }

        Check("back on Main", () => OnMain() && !Title().m_IsStartingRun, "Main, no run started");
    }

    /// <summary>The title's Campaign group, nothing over it.</summary>
    private static bool CampaignGroup() => CampaignGame.Title() is { } title && (int)title.m_CurrentButtonGroup == CampaignGroupValue && !title._isTransitioningGroup && !LobbyShell.IsOpen && Top() == null;

    /// <summary>Null when the title's own back handler takes a B now and leaves its group for Main, else why not (Core's <see cref="TitleBack"/>).</summary>
    private static string? TitleTakesBack() => CampaignGame.Title() is not { } title ? "no title"
        : TitleBack.WhyNot((int)title.m_CurrentButtonGroup, title.m_MenusOverSelf?.Count ?? 0, title.m_IsClickingButton, title.m_IsExiting, title._isTransitioningGroup, title._groupInputReady);

    /// <summary>The cursor is on the title's own New Run button.</summary>
    private static bool OnNewRun() =>
        CampaignGame.Title() is { } title && title.m_NewRunButton is { } button && Navigation().CurrentNavigable?.Pointer == button.Pointer;

    private static string SlotText(int slot) => slot > 0 ? string.Create(CultureInfo.InvariantCulture, $"co-op slot {slot}") : slot == 0 ? "the solo run" : "not known";
}
#endif
