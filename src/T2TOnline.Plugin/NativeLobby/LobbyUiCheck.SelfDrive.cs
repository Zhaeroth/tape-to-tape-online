#if HAS_INTEROP
using System;
using System.Collections.Generic;
using System.Globalization;
using Il2CppBishopGames.UI;
using Il2CppTape2Tape.Customization.UI;
using Il2CppTape2Tape.Hockey.UI;
using T2TOnline.Core.Lobby;
using T2TOnline.Plugin.Campaign;
using T2TOnline.Plugin.Campaign.Menus;
using T2TOnline.Plugin.Campaign.Scenes;
using T2TOnline.Plugin.Client;
using T2TOnline.Plugin.Diagnostics;
using T2TOnline.Plugin.Match;
using T2TOnline.Plugin.NativeLobby.Widgets;
using T2TOnline.Plugin.Online;
using T2TOnline.Plugin.Streaming;
using UnityEngine;

namespace T2TOnline.Plugin.NativeLobby;

// The self-driving tour (run.txt's first line "selfdrive"): a solo run of the session's flow as host, with no friend, through the same actions the native screens call. The real Join list;
// a real private Steam lobby for a minute or two, and the real friends list; Quick Game started under the shell, its controller screen left forward, a few seconds of the match, the
// game's own quit, the return; a New Run backed out of; a countdown cancelled; the lobby closed; a join of a lobby that isn't there, and its failure. The save guard holds through all of it,
// and the files are compared at the end. Writes it can't avoid asking for are refused and listed: opening the squad screen saves the profile (verified in the game's code), leaving the
// controller screen forward saves teams and rules.
internal static partial class LobbyUiCheck
{
    private const double MatchSeconds = 8;

    private static TourScreens? _tourScreens;

    /// <summary>The session, for the self-driving tour. Set once by the runtime.</summary>
    public static OnlineSession? Session { get; set; }

    /// <summary>Whether the self-driving tour is the one running: its steps share a lobby, so nothing is closed between them.</summary>
    private static bool SelfDriving => UiCheckPlan.IsSelfDrivePlan(_tour);

    private static void AddSelfDriveSteps()
    {
        Steps["sd_period"] = StepPlayPeriod;
        Steps["sd_browser"] = StepBrowser;
        Steps["sd_host"] = StepHost;
        Steps["sd_start"] = StepStart;
        Steps["sd_match"] = StepMatch;
        Steps["sd_return"] = StepReturn;
        Steps["sd_backout"] = StepBackOut;
        Steps["sd_cancel"] = StepCancel;
        Steps["sd_close"] = StepClose;
        Steps["sd_fail"] = StepFail;
        Steps["sd_report"] = StepReport;
        AddCustomTeamSteps(); // team=custom's (LobbyUiCheck.CustomTeam.cs)
    }

    private static OnlineSession Sd() => Session ?? throw new InvalidOperationException("the runtime gave the tour no session");

    private static LobbyFlow Flow() => Sd().Flow;

    /// <summary>The tour's screens when no real ones are wired: only the shell, so the hand-over, the return and focus are the real things. Every frame.</summary>
    private static void TickTourScreens() => _tourScreens?.Tick();

    /// <summary>A friend's two steps with no host: everything taken, everything given back. This is the worst failure there is: a stuck lock or guard.</summary>
    private static IEnumerable<Wait> StepPlayPeriod()
    {
        OnlineSession session = Sd();
        TitleScreen title = Title();
        if ((int)title.m_CurrentButtonGroup == 0 && !title._isTransitioningGroup)
        {
            StepNote("pressed through press-to-start with the game's own PressedToStart");
            title.PressedToStart(null!);
        }

        yield return Until(() => (int)Title().m_CurrentButtonGroup == 1 && !Title()._isTransitioningGroup, 8, "the main group");
        UiAssets.Request(OnlineGroup.Sources);
        yield return Until(() => Array.TrueForAll(OnlineGroup.Sources, UiAssets.Ready) || UiAssets.Problem != null, UiAssets.TimeoutSeconds + 1, "the source prefabs");
        if (session.TourStuckLine().Length > 0)
        {
            Problem($"already held before it started: {session.TourStuckLine()}");
        }

        if (!session.TourBeginPlay(out string problem))
        {
            Problem(problem);
            yield break;
        }

        yield return Frames(30);
        StepNote($"BeginPlay: UI lock {FriendUi.Locked}, following {CampaignRelay.Current?.Following == true}, campaign guard {SaveGuard.CampaignHolds}, prompts pinned {Input.PromptPin.Pinned}, held: {session.TourStuckLine()}");
        if (!FriendUi.Locked || CampaignRelay.Current?.Following != true || !SaveGuard.CampaignHolds)
        {
            Problem("BeginPlay didn't take the lock, following and the guard");
        }

        foreach (Wait wait in Snap("play_period_on", 0))
        {
            yield return wait;
        }

        // What must be read, raised with this PC locked: nothing could dismiss it now, so it waits, and comes once the lock is off.
        LobbyScreens.Instance.ShowFailure(Notices.Prompt(new LobbyLine(LobbyWords.NoticeBackupFailed)));
        yield return Seconds(1);
        StepNote($"a prompt raised under the lock: {(Top()?.TryCast<Il2CppTape2Tape.Customization.UI.CustomizableDismissablePrompt>() != null ? "up" : "not up")}, asked for {LobbyPrompts.IsOpen}");
        if (Top()?.TryCast<Il2CppTape2Tape.Customization.UI.CustomizableDismissablePrompt>() != null || LobbyPrompts.IsOpen)
        {
            Problem("a prompt came up under the lock, where nothing can dismiss it");
        }

        session.TourEndPlay();
        yield return Until(() => Top()?.TryCast<Il2CppTape2Tape.Customization.UI.CustomizableDismissablePrompt>() != null, 4, "the prompt, once the lock was off");
        foreach (Wait wait in Snap("play_period_prompt_after_lock", 0))
        {
            yield return wait;
        }

        // Only the one-button prompt this step raised: never whatever else is on top.
        if (Top()?.TryCast<Il2CppTape2Tape.Customization.UI.CustomizableDismissablePrompt>() is { } raised && LobbyPrompts.IsOpen)
        {
            _ = raised.Hide();
        }

        yield return Until(() => !LobbyPrompts.IsOpen, 4, "the prompt gone");
        yield return Frames(30);
        string stuck = session.TourStuckLine();
        StepNote($"EndPlay: UI lock {FriendUi.Locked}, following {CampaignRelay.Current?.Following == true}, campaign guard {SaveGuard.CampaignHolds}, prompts pinned {Input.PromptPin.Pinned}; still held: {(stuck.Length == 0 ? "nothing" : stuck)}");
        if (stuck.Length > 0)
        {
            Problem($"EndPlay left something held: {stuck}");
        }

        yield return Until(() => CampaignGame.IsIdle(Title()), 5, "the title idle again");
        foreach (Wait wait in Snap("play_period_off", 0))
        {
            yield return wait;
        }
    }

    /// <summary>The Online group by the game's own group change, its Join row pressed as a pad would: the real Join list, with whoever really is hosting.</summary>
    private static IEnumerable<Wait> StepBrowser()
    {
        TitleScreen title = Title();
        if (!OnlineGroup.Enter(title))
        {
            Problem("the Online group isn't in");
            yield break;
        }

        yield return Until(() => (int)Title().m_CurrentButtonGroup == OnlineGroup.GroupValue && !Title()._isTransitioningGroup, 4, "the Online group");
        yield return Seconds(1);
        StepNote($"the Online group: cursor {ObjectName(Navigation().CurrentNavigable)}, live line {LiveLineForLog()}");
        foreach (Wait wait in Snap("online_group", 0))
        {
            yield return wait;
        }

        AbstractButton? join = OnlineGroup.Button(title, "joinLobby");
        if (join == null)
        {
            Problem("Join Lobby isn't there");
            yield break;
        }

        AbstractNavigableUtils.SelectOnNextFrame(join, AbstractNavigable.ESelectFx.Default);
        yield return Frames(3);
        Navigation().OnConfirm();
        yield return Until(() => LobbyShell.IsShown && LobbyShell.Kind == ShellKind.Browser, ShellSeconds, "the Join list");
        yield return Until(() => LobbyShell.IsIdle, 4, "its intro over");
        yield return Seconds(2);
        BrowserList hosts = LobbyScreens.Instance.Hosts;
        StepNote($"the real Join list: {hosts.Rows.Count} row(s), cursor {ObjectName(Navigation().CurrentNavigable)}, in the shell {CursorInShell()}");
        if (!CursorInShell())
        {
            Problem("the Join list has no cursor");
        }

        foreach (Wait wait in Snap("join_list_real", SettleFrames))
        {
            yield return wait;
        }
    }

    private static bool CursorInShell() => Navigation().CurrentNavigable is { } current && LobbyShell.Parts is { } parts && current.transform.IsChildOf(parts.Root);

    /// <summary>
    /// The Online group's live line for the log: its word and how long it is on screen, never its text, which names the
    /// friend who hosts ("Alex is hosting"). "none" with nobody hosting.
    /// </summary>
    private static string LiveLineForLog()
    {
        LobbyLine? line = LobbyScreens.Instance.Hosts.HostingLine();
        return line == null ? "none" : $"{line.Term} ({OnlineGroup.LiveLine?.text?.Length ?? 0} characters shown)";
    }

    /// <summary>A toast through the screens' own way, where the player is, and what the game's queue says of it: which timings it shows by itself here, and whether one is on screen.</summary>
    private static IEnumerable<Wait> ToastHere(string where)
    {
        NotificationQueue? queue = NotificationQueue.Instance;
        if (queue == null)
        {
            StepNote($"{where}: the game's toast queue isn't there");
            yield break;
        }

        StepNote($"{where}, before a toast: the queue shows by itself the timings {(int)queue.m_AutoNotifyTimings}, and is showing {(int)queue.m_CurrentTimings}");
        LobbyToast.Show(new LobbyLine(LobbyWords.ToastInviteSent, "the check tour"));
        yield return Seconds(0.5);
        StepNote($"{where}, with a toast of the title's timing (16) queued: the queue is showing {(int)queue.m_CurrentTimings}");
        foreach (Wait wait in Snap($"toast_{where.Replace(' ', '_')}", 0))
        {
            yield return wait;
        }

        // At rest: at half a second it's still wiping in.
        yield return Seconds(1);
        foreach (Wait wait in Snap($"toast_{where.Replace(' ', '_')}_rest", 0))
        {
            yield return wait;
        }
    }

    private static IEnumerable<Wait> StepHost()
    {
        OnlineSession session = Sd();
        if (session.Screens == null)
        {
            _tourScreens = new TourScreens();
            session.Screens = _tourScreens;
            StepNote("no native screens are wired: the tour's own stands in, the shell alone");
        }

        ILobbyActions actions = session.Actions;
        if (LobbyShell.IsShown && LobbyShell.Kind == ShellKind.Browser && LobbyScreens.Instance.Hosts.IsEmpty)
        {
            // The empty state's Host Lobby, pressed as a pad would: from here it's a swap of screens, and the title's fade is what mustn't show.
            StepNote($"Host Lobby on the empty Join list, the cursor on {ObjectName(Navigation().CurrentNavigable)}");
            float lowest = 1;
            float noted = -1;
            int menus = -1;
            var series = new System.Text.StringBuilder();
            double started = FrameClock.Seconds;
            Navigation().OnConfirm();
            yield return Until(() =>
            {
                TitleScreen at = Title();
                float alpha = at.m_FadeToBlack.alpha;
                int over = at.m_MenusOverSelf?.Count ?? -1;
                lowest = Math.Min(lowest, alpha);
                if (Math.Abs(alpha - noted) > 0.05f || over != menus)
                {
                    noted = alpha;
                    menus = over;
                    series.Append(System.FormattableString.Invariant($" {(FrameClock.Seconds - started) * 1000:0}ms:{alpha:0.00}/{over}/{(LobbyShell.IsOpen ? LobbyShell.Id : "none")}"));
                }

                return LobbyShell.IsShown && LobbyShell.Kind == ShellKind.Lobby;
            }, 20 + ShellSeconds, "the swap to the lobby");
            StepNote($"the swap's fade:{series}");
            StepNote(string.Create(CultureInfo.InvariantCulture, $"the swap from the Join list to the lobby: the title's fade at its lowest {lowest:0.00} (1 is black)"));
            if (lowest < 0.7f)
            {
                Problem("the bare title showed through the swap of screens");
            }
        }
        else
        {
            if (LobbyShell.IsShown && LobbyShell.Kind == ShellKind.Browser)
            {
                StepNote("friends are really hosting: the Join list closes, and Host Lobby is called as the Online group's row does");
                LobbyScreens.Instance.CloseBrowser();
                yield return Until(() => !LobbyShell.IsOpen, ShellSeconds, "the Join list closed");
                yield return Frames(10);
            }

            actions.HostLobby();
        }

        yield return Until(() => Flow().Host == LobbyHostState.Open, 20, "the flow Open");
        if (Flow().Host != LobbyHostState.Open)
        {
            Problem($"the flow is {Flow().Host}: no lobby");
            yield break;
        }

        yield return Until(() => LobbyShell.IsShown || !LobbyShell.Available, ShellSeconds, "the shell shown");
        yield return Until(() => LobbyShell.IsIdle, 4, "the shell's intro over");
        foreach (Wait wait in StepHostOnScreen(actions))
        {
            yield return wait;
        }

        actions.SetMode(LobbyMode.QuickGame);
        yield return Seconds(0.5);
        yield return Until(() => actions.Snapshot().StartAllowed, 15, "Start allowed");
        LobbyState state = actions.Snapshot();
        StepNote($"the board: role {state.Role}, phase {state.Phase}, epoch {state.Epoch}, mode {state.Mode}, save {state.Save}, seats {state.Seats.Count}, host seat {(state.HostSeat is { } hostSeat ? LogNames.TextShape(hostSeat.Name) : "none")}, players {state.Players}, start allowed {state.StartAllowed} ({state.WhyNot})");
        if (state.Role != LobbyRole.Host || state.Phase != LobbyPhase.Open || state.Mode != LobbyMode.QuickGame || state.HostSeat == null || !state.StartAllowed)
        {
            Problem("the board isn't what the lobby should say");
        }

        if (!Flow().KeepsNewRunFlagClear)
        {
            Problem("hosting doesn't keep the new-run flag clear");
        }

        foreach (Wait wait in Snap("lobby_open", SettleFrames))
        {
            yield return wait;
        }
    }

    /// <summary>What a human does on the host's lobby that a look at it can't prove: a bumper held for half a second, the real friends list and back, a toast.</summary>
    private static IEnumerable<Wait> StepHostOnScreen(ILobbyActions actions)
    {
        LobbyView? lobby = LobbyScreens.Instance.Lobby;
        if (lobby == null)
        {
            StepNote("no lobby screen: the tour's stand-in");
            yield break;
        }

        yield return Frames(30);
        LobbyMode mode = actions.Snapshot().Mode;
        int changes = 0;
        if (!lobby.PressButtonForCheck(LobbyButton.TabRight))
        {
            Problem("RB isn't on the bar");
        }

        double until = FrameClock.Seconds + 0.5;
        yield return Until(() =>
        {
            LobbyMode now = actions.Snapshot().Mode;
            changes += now != mode ? 1 : 0;
            mode = now;
            return FrameClock.Seconds >= until;
        }, 2, "the bumper held");
        lobby.ReleaseButtonForCheck(LobbyButton.TabRight);
        StepNote($"RB held for half a second, by the game's own press: the mode changed {changes} time(s), now {mode}");
        if (changes != 1)
        {
            Problem($"a held bumper changed the mode {changes} times");
        }

        yield return Frames(10);
        LobbyFocus from = lobby.Focus;
        lobby.PressForCheck(LobbyAction.OpenInvitePanel);
        yield return Seconds(2.5);
        StepNote($"the real friends list: open {lobby.PanelOpen}, cursor {ObjectName(Navigation().CurrentNavigable)}, in the shell {CursorInShell()}");
        if (!lobby.PanelOpen || !CursorInShell())
        {
            Problem("the friends list has no cursor");
        }

        foreach (Wait wait in Snap("friends_list_real", 0))
        {
            yield return wait;
        }

        if (!lobby.TriggerForCheck(LobbyButton.Back))
        {
            Problem("Back isn't on the friends list's bar");
        }

        yield return Frames(10);
        StepNote($"back from the friends list: open {lobby.PanelOpen}, focus {lobby.Focus.Kind} {lobby.Focus.Index} (it came from {from.Kind} {from.Index}), cursor {ObjectName(Navigation().CurrentNavigable)}");
        if (lobby.PanelOpen || lobby.Focus != from)
        {
            Problem("the cursor didn't come back to where the friends list was opened from");
        }

        foreach (Wait wait in ToastHere("in the lobby"))
        {
            yield return wait;
        }
    }

    private static IEnumerable<Wait> StepStart()
    {
        OnlineSession session = Sd();
        ILobbyActions actions = session.Actions;
        actions.Start(newRun: false, anyway: false);
        yield return Until(() => Flow().Host == LobbyHostState.Counting, 2, "the countdown");
        yield return Seconds(1);
        StepNote($"counting: {actions.Snapshot().CountdownMs} ms left, epoch {actions.Snapshot().Epoch}, phase {actions.Snapshot().Phase}");
        foreach (Wait wait in Snap("countdown", 0))
        {
            yield return wait;
        }

        yield return Until(() => Flow().Host == LobbyHostState.GameFlow, 5, "the barrier passed, with no friend");
        yield return Until(() => Top()?.TryCast<ControllerAssignmentMenu>() != null, 8, "Play Now's controller screen over the shell");
        ControllerAssignmentMenu? controllers = Top()?.TryCast<ControllerAssignmentMenu>();
        if (controllers == null)
        {
            Problem("Play Now's controller screen didn't come");
            yield break;
        }

        yield return Seconds(1.2);
        foreach (Wait wait in Snap("controller_screen", 0))
        {
            yield return wait;
        }

        TitleScreen title = Title();
        StepNote($"under the controller screen: flow {Flow().Host}, shell shown {LobbyShell.IsShown}, handing over {LobbyShell.HandingOver}, title exiting {title.m_IsExiting}, its menu list {string.Join(", ", MenusOver())}");
        if (Screens() is null && (!LobbyShell.IsShown || !LobbyShell.HandingOver))
        {
            Problem("the shell isn't under the game's flow");
        }

        // team=custom: the match on the custom team sd_custom_team armed (nothing without the key).
        PlayCustomTeam();

        // Forward: Start's two steps, without the screen's own prompts (its AI-only match asks).
        StepNote("leaving the controller screen forward: SetResult(false), FinishExitMenu");
        controllers.SetResult(false);
        controllers.FinishExitMenu();
    }

    private static ILobbyScreens? Screens() => ReferenceEquals(Session?.Screens, _tourScreens) ? null : Session?.Screens;

    private static IEnumerable<Wait> StepMatch()
    {
        OnlineSession session = Sd();
        yield return Until(() => Flow().Host == LobbyHostState.InGame, 30, "the scene left the title");
        yield return Until(() => HudModel.GameManagerInstance() != null && !MatchSetup.Loading, 45, "the match loaded");
        yield return Seconds(MatchSeconds);
        LobbyState state = session.Actions.Snapshot();
        StepNote($"in the match: flow {Flow().Host}, phase {state.Phase}, shell open {LobbyShell.IsOpen}, scene {WorldMap.SceneName}, ended {HudModel.GameManagerInstance()?.isGameEnded}");
        if (state.Phase != LobbyPhase.Playing)
        {
            Problem($"the board says {state.Phase} in a match");
        }

        if (LobbyShell.IsOpen)
        {
            Problem("the shell stayed open into the match");
        }

        foreach (Wait wait in Snap("match", 0))
        {
            yield return wait;
        }

        foreach (Wait wait in ToastHere("in a match"))
        {
            yield return wait;
        }

        Il2Cpp.GameLoader? loader = UnityEngine.Object.FindObjectOfType<Il2Cpp.GameLoader>();
        Il2CppState.GameModeControllers.IGameModeController? controller = loader?._gameModeController;
        if (controller == null || PlaceNow(out _) != TourPlace.Match)
        {
            // Never a scene load in its place: a load from where the tour can't see isn't the tour's to make.
            Problem($"no match to quit through the game's own quit (the game at {TourStarts.Describe(PlaceNow(out string at))}{(at.Length > 0 ? $" ({at})" : string.Empty)}{(controller == null ? ", no mode controller" : string.Empty)}); nothing was done");
            yield break;
        }

        // What RunEndHelper.EndRun does just before its scene load: if the session's clearing didn't win, the title would open the squad screen by itself. And the ribbon's way in,
        // from the session's reading of the game's numbers: the tour marks the match as ended, as GameManager.isGameEnded would (a real end would save stats and unlock things).
        Il2CppTape2Tape.CampaignUtil.startNewRunImmediately = true;
        session.TourMarkMatchEnded();
        StepNote("the game's own quit (IGameModeController.Quit); the new-run flag set first, as a run's end sets it");
        controller.Quit();

        // What must be read, raised as the game leaves its match (a friend whose host closed under it): the game hides every menu before a load, so it waits for the title.
        session.TourLeavingForTitle = true;
        LobbyScreens.Instance.ShowFailure(Notices.Prompt(new LobbyLine(LobbyWords.NoticeBackupFailed)));
    }

    private static IEnumerable<Wait> StepReturn()
    {
        OnlineSession session = Sd();
        yield return Until(() => Flow().Host is LobbyHostState.Returning or LobbyHostState.Open, 30, "the return");
        StepNote($"the title scene is in: flow {Flow().Host}, shell open {LobbyShell.IsOpen}, the flag {Il2CppTape2Tape.CampaignUtil.startNewRunImmediately}");
        foreach (Wait wait in Snap("return_scene_in", 0))
        {
            yield return wait;
        }

        session.TourLeavingForTitle = false;
        yield return Until(() => Top()?.TryCast<Il2CppTape2Tape.Customization.UI.CustomizableDismissablePrompt>() != null, 30, "the prompt raised as the match left, on the title");
        StepNote($"the prompt raised as the match left is up: title started {Title().m_HasStarted}, shell open {LobbyShell.IsOpen}, flow {Flow().Host}");
        if (!Title().m_HasStarted || LobbyShell.IsOpen)
        {
            Problem("the prompt came before the title had started, or the shell came before it");
        }

        foreach (Wait wait in Snap("return_prompt_held", 0))
        {
            yield return wait;
        }

        // Only the one-button prompt raised as the match left: never whatever else is on top.
        if (Top()?.TryCast<Il2CppTape2Tape.Customization.UI.CustomizableDismissablePrompt>() is { } held && LobbyPrompts.IsOpen)
        {
            _ = held.Hide();
        }

        yield return Until(() => !LobbyPrompts.IsOpen, 4, "the prompt gone");
        for (int i = 0; i < 4 && !LobbyShell.IsShown; i++)
        {
            yield return Seconds(0.25);
            foreach (Wait wait in Snap($"return_wipe_{i}", 0))
            {
                yield return wait;
            }
        }

        yield return Until(() => LobbyShell.IsShown || !LobbyShell.Available, 12, "the shell over the title");
        foreach (Wait wait in Snap("return_shell_up", 0))
        {
            yield return wait;
        }

        yield return Until(() => Flow().Host == LobbyHostState.Open, 12, "back at the lobby");
        bool locked = Flow().InputLockWasSet;
        yield return Until(() => LobbyShell.IsIdle, 4, "the shell's intro over");
        yield return Frames(10);
        TitleScreen title = Title();
        LobbyState state = session.Actions.Snapshot();
        string cursor = ObjectName(Navigation().CurrentNavigable);
        bool inShell = Navigation().CurrentNavigable is { } current && LobbyShell.Parts is { } parts && current.transform.IsChildOf(parts.Root);
        StepNote($"back at the lobby: flow {Flow().Host}, input locked at arrival {locked}, the flag {Il2CppTape2Tape.CampaignUtil.startNewRunImmediately}, starting a run {title.m_IsStartingRun}, title exiting {title.m_IsExiting}, its menu list {string.Join(", ", MenusOver())}, cursor {cursor} (in the shell {inShell}), back from a game {state.BackFromGame}, ribbon {state.Result}, top {MenuGame.MenuId(Top()!)}");
        if (Flow().Host != LobbyHostState.Open)
        {
            Problem("the flow isn't back at the lobby");
        }

        if (Il2CppTape2Tape.CampaignUtil.startNewRunImmediately || title.m_IsStartingRun || Top()?.TryCast<Il2CppTape2Tape.Hockey.UI.ChooseMetaMenu>() != null)
        {
            Problem("the new-run flag wasn't cleared in time: the title went on to the squad screen");
        }

        if (!locked)
        {
            Problem("the input wasn't locked as the lobby came back");
        }

        if (state.Result.Kind != LobbyResultKind.MatchTied && state.Result.Kind != LobbyResultKind.MatchWon && state.Result.Kind != LobbyResultKind.MatchLost)
        {
            Problem($"the ribbon is {state.Result.Kind}");
        }

        if (!inShell)
        {
            Problem($"the cursor came back outside the shell ({cursor})");
        }

        foreach (Wait wait in Snap("return_at_rest", SettleFrames))
        {
            yield return wait;
        }

        yield return Seconds(LobbyTiming.InputLockMicros / 1_000_000.0);
        if (Flow().IsInputLocked(session.TourNow()))
        {
            Problem("the input lock didn't end");
        }
    }

    /// <summary>Campaign, New Run: its flow under the shell, backed out by the game's own routes: no save is deleted (its prompt is answered No; the squad screen backed out of).</summary>
    private static IEnumerable<Wait> StepBackOut()
    {
        OnlineSession session = Sd();
        ILobbyActions actions = session.Actions;
        actions.SetMode(LobbyMode.Campaign);
        yield return Seconds(0.5);
        yield return Until(() => actions.Snapshot().StartAllowed, 15, "Start allowed");
        TitleScreen title = Title();
        StepNote($"campaign: save {actions.Snapshot().Save}, ongoing {title.m_HasOngoingCampaign}, tutorial skipped {title.m_CampaignTutorial?.ShouldSkipTutorial()}");
        actions.Start(newRun: true, anyway: true);
        yield return Until(() => Flow().Host == LobbyHostState.GameFlow, 6, "the game's flow");
        yield return Until(() => Top() is { } top && !LobbyShell.IsShellId(MenuGame.MenuId(top)) && !IsWoosh(top), 12, "the game's flow over the shell");
        yield return Seconds(1.5);
        foreach (Wait wait in Snap("backout_flow", 0))
        {
            yield return wait;
        }

        StepNote($"the game's flow: {MenuGame.MenuId(Top()!)} over the shell; flow {Flow().Host}, starting a run {Title().m_IsStartingRun}, its menu list {string.Join(", ", MenusOver())}");
        // Only while the lobby's shell is open under the flow: its own start's screens, never the title's own once it has gone.
        for (int i = 0; i < 8 && LobbyShell.IsOpen; i++)
        {
            AbstractMenu? top = Top();
            if (top == null || LobbyShell.IsShellId(MenuGame.MenuId(top)))
            {
                break;
            }

            string id = MenuGame.MenuId(top);
            if (IsWoosh(top))
            {
                yield return Seconds(0.5); // the game's own transition: it goes by itself
                continue;
            }

            PromptTemplateMenu? prompt = top.TryCast<PromptTemplateMenu>();
            Il2CppTape2Tape.Hockey.UI.ChooseMetaMenu? squad = top.TryCast<Il2CppTape2Tape.Hockey.UI.ChooseMetaMenu>();
            if (prompt != null)
            {
                StepNote($"answering {id} No");
                prompt.OnNoClick(null!);
            }
            else if (squad != null)
            {
                StepNote($"backing out of {id} (its own BackOut)");
                squad.BackOut();
            }
            else
            {
                StepNote($"closing {id}");
                _ = top.Hide();
            }

            yield return Seconds(1.2);
        }

        yield return Until(() => Flow().Host == LobbyHostState.Open, 12, "the lobby again");
        yield return Seconds(0.8);
        TitleScreen after = Title();
        StepNote($"backed out: flow {Flow().Host}, shell shown {LobbyShell.IsShown}, handing over {LobbyShell.HandingOver}, top {MenuGame.MenuId(Top()!)}, starting a run {after.m_IsStartingRun}, title exiting {after.m_IsExiting}, cursor {ObjectName(Navigation().CurrentNavigable)}, start allowed {actions.Snapshot().StartAllowed}");
        if (Flow().Host != LobbyHostState.Open || after.m_IsExiting || after.m_IsStartingRun)
        {
            Problem("the title isn't live after the back-out");
        }

        if (Screens() is null && !LobbyShell.IsShown)
        {
            Problem("the shell isn't up after the back-out");
        }

        foreach (Wait wait in Snap("backout_lobby", SettleFrames))
        {
            yield return wait;
        }
    }

    private static bool IsWoosh(AbstractMenu menu) => MenuGame.MenuId(menu).StartsWith("Woosh", StringComparison.Ordinal);

    private static IEnumerable<Wait> StepCancel()
    {
        OnlineSession session = Sd();
        ILobbyActions actions = session.Actions;
        yield return Until(() => actions.Snapshot().StartAllowed, 15, "Start allowed");
        uint before = Flow().Epoch;
        actions.Start(newRun: false, anyway: true);
        yield return Until(() => Flow().Host == LobbyHostState.Counting, 2, "the countdown");
        yield return Seconds(1);
        actions.CancelStart();
        yield return Frames(10);
        StepNote($"cancelled: flow {Flow().Host}, epoch {before} to {Flow().Epoch}, top {MenuGame.MenuId(Top()!)}, phase {actions.Snapshot().Phase}, countdown {actions.Snapshot().CountdownMs} ms");
        if (Flow().Host != LobbyHostState.Open || Flow().Epoch != before + 1)
        {
            Problem("the cancel didn't bring the lobby back with a new epoch");
        }

        yield return Seconds(LobbyTiming.CountdownMicros / 1_000_000.0 + 0.5);
        if (Flow().Host != LobbyHostState.Open)
        {
            Problem($"the cancelled countdown went on: {Flow().Host}");
        }

        foreach (Wait wait in Snap("cancelled", 0))
        {
            yield return wait;
        }
    }

    private static IEnumerable<Wait> StepClose()
    {
        OnlineSession session = Sd();
        session.Actions.LeaveOrClose();
        yield return Until(() => Flow().Role == LobbyRole.None, 5, "the session over");
        yield return Until(() => !LobbyShell.IsOpen, ShellSeconds, "the shell closed");
        yield return Until(TitleIsBack, 4, "the title back");
        yield return Frames(8);
        LobbyState state = session.Actions.Snapshot();
        TitleScreen title = Title();
        StepNote($"closed: role {state.Role}, in session {session.Actions.InSession}, onboarding held {OnlineSession.LobbyUp}, IsIdle {CampaignGame.IsIdle(title)}, exiting {title.m_IsExiting}, group {(int)title.m_CurrentButtonGroup}, cursor {ObjectName(Navigation().CurrentNavigable)}, held: {session.TourStuckLine()}");
        if (state.Role != LobbyRole.None || session.Actions.InSession || !CampaignGame.IsIdle(title) || title.m_IsExiting || session.TourStuckLine().Length > 0)
        {
            Problem("the title isn't free after the lobby closed");
        }

        foreach (Wait wait in Snap("closed", 0))
        {
            yield return wait;
        }
    }

    /// <summary>A real failure on the real path: a join of a lobby that isn't there, Steam's answer, the game's prompt, the Join list it lands on, and Back from there to the Online group.</summary>
    private static IEnumerable<Wait> StepFail()
    {
        OnlineSession session = Sd();
        TitleScreen title = Title();
        if ((int)title.m_CurrentButtonGroup == 0 && !title._isTransitioningGroup)
        {
            title.PressedToStart(null!);
            yield return Until(() => (int)Title().m_CurrentButtonGroup == 1 && !Title()._isTransitioningGroup, 8, "the main group");
        }

        session.TourJoinNowhere();
        yield return Until(() => Top() is { } top && (top.TryCast<PromptTemplateMenu>() != null || top.TryCast<CustomizableDismissablePrompt>() != null), 40, "the failure's prompt");
        yield return Seconds(1);
        AbstractMenu? prompt = Top();
        StepNote($"the failure: {(prompt == null ? "nothing" : MenuGame.MenuId(prompt))}, flow {Flow().Role}, cursor {ObjectName(Navigation().CurrentNavigable)}");
        foreach (Wait wait in Snap("failure_real", 0))
        {
            yield return wait;
        }

        if (prompt?.TryCast<PromptTemplateMenu>() is { } twoButtons)
        {
            twoButtons.OnNoClick(null!);
        }
        else if (prompt != null)
        {
            _ = prompt.Hide();
        }

        yield return Until(() => LobbyShell.IsShown && LobbyShell.Kind == ShellKind.Browser, 12, "the Join list it lands on");
        yield return Until(() => LobbyShell.IsIdle, 4, "its intro over");
        yield return Frames(20);
        StepNote($"landed: shell {LobbyShell.Id}, cursor {ObjectName(Navigation().CurrentNavigable)}, in the shell {CursorInShell()}");
        if (!CursorInShell())
        {
            Problem("the Join list has no cursor after the failure");
        }

        foreach (Wait wait in Snap("failure_landing", 0))
        {
            yield return wait;
        }

        LobbyScreens.Instance.Browser?.Back();
        yield return Until(() => !LobbyShell.IsOpen, ShellSeconds, "the Join list closed");
        yield return Until(() => (int)Title().m_CurrentButtonGroup == OnlineGroup.GroupValue && !Title()._isTransitioningGroup, 4, "the Online group");
        yield return Frames(20);
        StepNote($"back: group {(int)Title().m_CurrentButtonGroup}, cursor {ObjectName(Navigation().CurrentNavigable)}");
        foreach (Wait wait in Snap("failure_back_on_group", 0))
        {
            yield return wait;
        }
    }

    /// <summary>
    /// A bug report made the way the native rows make one (<see cref="NativeUi.ReportFlow"/>: no Explorer), while the game runs, so a privacy scan after the
    /// tour reads a real one: its zip's name and what it cleaned go in the summary; then the game's prompt it brings, closed by the game's own hide (Close).
    /// </summary>
    private static IEnumerable<Wait> StepReport()
    {
        int before = BugReports.Finished;
        if (!NativeUi.ReportFlow.Ask("the check tour"))
        {
            Problem($"the bug report didn't start ({(BugReports.Busy ? "one was already being made" : BugReports.Status)})");
            yield break;
        }

        yield return Until(() => BugReports.Finished > before, 120, "the bug report made");
        (string name, string summary) = BugReports.Last;
        if (BugReports.Finished <= before || BugReports.State != ReportState.Ready || name.Length == 0)
        {
            Problem($"no bug report ({(summary.Length > 0 ? summary : BugReports.Status)})");
            yield break;
        }

        StepNote($"report: {name} ({summary})");
        yield return Until(() => LobbyPrompts.IsOpen && !LobbyPrompts.IsAsking, 10, "the report's prompt");
        StepNote($"the report's prompt: Show File {NativeUi.ReportFlow.LastShowFile}");
        if (!LobbyPrompts.IsOpen)
        {
            Problem("the report's prompt didn't come");
            yield break;
        }

        foreach (Wait wait in HidePrompt())
        {
            yield return wait;
        }
    }

    /// <summary>The session gone before anything else is put back, from any step.</summary>
    private static IEnumerable<Wait> LeaveSession()
    {
        if (Session == null || !OnRealSession)
        {
            yield break;
        }

        Session.TourNotices = null;
        Session.TourLeave();
        yield return Until(() => Flow().Role == LobbyRole.None, 5, "the session over");
        yield return Until(() => !LobbyShell.IsOpen, ShellSeconds, "the shell closed");
        if (_tourScreens != null)
        {
            if (ReferenceEquals(Session.Screens, _tourScreens))
            {
                Session.Screens = null;
            }

            _tourScreens = null;
        }
    }

    /// <summary>The tour's stand-in for the native screens: the shell alone, opened when the title allows it, with a button to hold the selection.</summary>
    private sealed class TourScreens : ILobbyScreens
    {
        private bool _wanted;
        private bool _focusOn;

        public void Tick()
        {
            if (_wanted && !LobbyShell.IsOpen && LobbyShell.Available && LobbyShell.TitleAllowingOpen() != null)
            {
                UiAssets.Request(OnlineGroup.Sources);
                if (LobbyShell.RequestOpen(ShellKind.Lobby, new ShellOptions(), out string problem))
                {
                    _focusOn = false;
                }
                else
                {
                    ModLog.Warn($"Lobby check: the shell didn't open: {problem}");
                }
            }

            // The held prefabs went with the last title, and may be on their way back.
            if (!_focusOn && LobbyShell.IsShown && LobbyShell.Parts is { } parts && UiAssets.Ready("GameSettings"))
            {
                _focusOn = true;
                AddButton(parts);
            }
        }

        private static void AddButton(ShellParts parts)
        {
            GameObject? source = UiAssets.Part("GameSettings", "ScaledContainer/BottomButtons/Button_Back");
            GameObject? clone = source == null ? null : UiClone.Make(source, UiClone.Widgets, "a button", out _);
            if (clone == null)
            {
                return;
            }

            UiClone.Place(clone, parts.ScrollContent);
            AbstractNavigable? button = clone.GetComponent<AbstractNavigable>();
            if (button != null)
            {
                LobbyShell.SetDefaultNavigables(button);
                AbstractNavigableUtils.SelectOnNextFrame(button, AbstractNavigable.ESelectFx.Default);
            }
        }

        public void ShowLobby() => _wanted = true;

        public void HideLobby()
        {
            _wanted = false;
            LobbyShell.Close("the session's flow");
        }

        public void HandOverForStart(string what)
        {
            _wanted = false;
            LobbyShell.BeginHandOver(what);
        }

        public void ShowJoinList(ulong lobby) => ModLog.Info($"Lobby check: the flow says the Join list (lobby {ModLog.Scrub.Tags.Lobby(lobby)}).");

        public void CloseJoinList() => ModLog.Info("Lobby check: the flow says the Join list closes.");

        public void JoinLobbyAgain(ulong lobby, string hostName) => ModLog.Info($"Lobby check: the session says join lobby {ModLog.Scrub.Tags.Lobby(lobby)} again.");

        public void ShowOnlineGroup() => ModLog.Info("Lobby check: the flow says the Online group.");

        public void ShowFailure(FailureMessage message) => ModLog.Warn($"Lobby check: the flow says a failure: {message.Reason}.");

        public void ShowToast(LobbyLine line) => ModLog.Info($"Lobby check: the flow says a toast: {line.Term}.");

        public void ShowReconnecting(bool reconnecting) => ModLog.Info($"Lobby check: the flow says reconnecting {reconnecting}.");
    }
}
#endif
