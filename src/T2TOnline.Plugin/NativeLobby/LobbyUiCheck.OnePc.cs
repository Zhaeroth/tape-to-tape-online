#if HAS_INTEROP
using System;
using System.Collections.Generic;
using Il2CppBishopGames.UI;
using Il2CppTape2Tape.Customization.UI;
using Il2CppTape2Tape.Hockey.UI;
using T2TOnline.Core.Input;
using T2TOnline.Core.Lobby;
using T2TOnline.Core.OnlineSettings;
using T2TOnline.Plugin.NativeLobby.Widgets;
using T2TOnline.Plugin.NativeUi;
using T2TOnline.Plugin.Online;
using T2TOnline.Plugin.Startup;

namespace T2TOnline.Plugin.NativeLobby;

// The tour with someone really in a seat (run.txt's first line "onepc"): the one-PC test's friend, this game over Steam's loopback, on the same paths a friend's game takes to the host's: hello,
// AcceptFriend and a real virtual pad, the board and the member messages, the barrier, game players, a drop, a kick. The host's presses are the tour's pad, as in "padwalk"; the friend's are a stream
// of a pad over Steam. The friend's following can't run in this game (it would load the host's scenes over them): their flow says ready, and acks.
internal static partial class LobbyUiCheck
{
    private static void AddOnePcSteps()
    {
        Steps["op_host"] = StepOnePcHost;
        Steps["op_arrive"] = StepOnePcArrive;
        Steps["op_board"] = StepOnePcBoard;
        Steps["op_close_prompt"] = StepOnePcClosePrompt;
        Steps["op_start"] = StepOnePcStart;
        Steps["op_match"] = StepOnePcMatch;
        Steps["op_drop"] = StepOnePcDrop;
        Steps["op_kick"] = StepOnePcKick;
        Steps["op_close"] = StepOnePcClose;
        Steps[UiCheckPlan.DirectConnectionsStep] = StepOnePcDirect; // dc=on's
    }

    private static LobbySeat FriendSeat() => Sd().Actions.Snapshot().Seats is { Count: > 1 } seats ? seats[1] : new LobbySeat();

    private static string SeatLine(LobbySeat seat) =>
        $"seat {seat.PlayerNumber}: a name of {Diagnostics.LogNames.TextShape(seat.Name)}, stage {seat.Stage}, ready {seat.Ready}, {seat.DeviceKind} (family {seat.DeviceFamily}), ping {seat.PingMs}, bars {seat.Bars}, kept {seat.ReconnectSeconds} s, avatar {AvatarCache.StatusOf(OnlineSession.SoloSeatId == seat.SteamId ? Il2CppSteamId() : seat.SteamId, AvatarSize.Large)}";

    private static ulong Il2CppSteamId() => T2TOnline.Steam.SteamRuntime.LocalSteamId;

    private static IEnumerable<Wait> StepOnePcHost()
    {
        foreach (Wait wait in StepWalkStart())
        {
            yield return wait;
        }

        foreach (Wait wait in OnlineRowOnMain())
        {
            yield return wait;
        }

        foreach (Wait wait in OpenGroup("A on Online", null))
        {
            yield return wait;
        }

        if (Cursor() != "HostLobby")
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

        foreach (Wait wait in EnsureQuick())
        {
            yield return wait;
        }

        yield return Soft(() => Sd().Actions.Snapshot().StartAllowed, 8, "Start allowed");
    }

    private static IEnumerable<Wait> StepOnePcArrive()
    {
        OnlineSession session = Sd();
        ILobbyActions actions = session.Actions;
        session.TourNotices = new List<NoticeId>();
        Check("the one-PC test starts", () => session.TourSeatFriend(holdHello: true), "a friend session on this game, its hello held back");
        yield return Soft(() => FriendSeat().Stage == SeatStage.Joining, 6, "the seat saying Joining");
        StepNote(SeatLine(FriendSeat()));
        Expect("the seat before their hello", () => FriendSeat().Stage == SeatStage.Joining && actions.Snapshot().Players == 2, "Joining on the first free seat, two players on the count");
        foreach (Wait wait in Snap("seat_joining", SettleFrames))
        {
            yield return wait;
        }

        // Start with someone not here: its one question, by the pad.
        foreach (Wait wait in Press("Start, someone not here", PadButtons.Start, () => Top()?.TryCast<CustomizableYesNoPrompt>() != null, "the game's yes/no over the lobby", within: 4))
        {
            yield return wait;
        }

        yield return Seconds(0.9);
        StepNote($"the question: cursor {Cursor()} (the first answer is the safe one)");
        foreach (Wait wait in Snap("start_asks", 0))
        {
            yield return wait;
        }

        foreach (Wait wait in Press("A on Wait", PadButtons.A, () => Flow().IsWaitingForEveryone && Top()?.Pointer == LobbyShell.Menu?.Pointer && Flow().Host == LobbyHostState.Open, "the lobby, waiting for everyone", within: 4))
        {
            yield return wait;
        }

        foreach (Wait wait in Snap("waiting", SettleFrames))
        {
            yield return wait;
        }

        // Their hello goes: AcceptFriend, a virtual pad, the seat. The wait comes true by itself.
        session.TourLetHelloGo();
        yield return Soft(() => FriendSeat().Stage == SeatStage.Here, 10, "the friend Here");
        StepNote(SeatLine(FriendSeat()));
        yield return Soft(() => Flow().Host == LobbyHostState.Counting, 3, "the countdown by itself");
        Expect("the wait came true", () => Flow().Host == LobbyHostState.Counting && FriendSeat().Stage == SeatStage.Here, "the countdown by itself, the friend Here");
        foreach (Wait wait in Press("B in the countdown", PadButtons.B, () => Flow().Host == LobbyHostState.Open && FocusIs(Row(LobbyFocusKind.StartRow)), "the lobby, the cursor on Start"))
        {
            yield return wait;
        }

        foreach (Wait wait in MoveTo(Seat(1)))
        {
            yield return wait;
        }

        yield return Seconds(1.5);
        StepNote(SeatLine(FriendSeat()));
        Expect("the card", () => FriendSeat().Stage == SeatStage.Here && !FriendSeat().IsHost && !FriendSeat().IsSelf && FriendSeat().DeviceKind == Core.Protocol.InputKind.Gamepad && FriendSeat().Name.Length > 0 && FriendSeat().PingMs >= 0, "Here, not the host, a pad, a name and a measured ping");
        Check("the notices so far", () => session.TourNotices!.Contains(NoticeId.FriendJoined), "the arrival told");
        foreach (Wait wait in Snap("seat_here", 0))
        {
            yield return wait;
        }

        // A press of A on someone's card: nothing, and no list.
        foreach (Wait wait in Press("A on the friend's card", PadButtons.A, () => !Lobby().PanelOpen && FocusIs(Seat(1)) && Flow().Host == LobbyHostState.Open, "nothing"))
        {
            yield return wait;
        }

        session.TourNotices = null;
    }

    private static IEnumerable<Wait> StepOnePcBoard()
    {
        OnlineSession session = Sd();
        StepNote(session.TourFriendLine());
        Check("what crossed", () => session.TourFriendLine().Contains("holds a board", StringComparison.Ordinal) && session.TourFriendLine().Contains("holds their member message", StringComparison.Ordinal), "a real board on the friend's session, their member message on the host's");
        session.TourFriendReady(true);
        yield return Soft(() => FriendSeat().Ready, 3, "the ready mark on the card");
        Check("their ready mark", () => FriendSeat().Ready, "the card ready");
        StepNote(session.TourFriendLine());
        foreach (Wait wait in Snap("seat_ready", SettleFrames))
        {
            yield return wait;
        }

        session.TourFriendReady(false);
        yield return Soft(() => !FriendSeat().Ready, 3, "the ready mark gone");
        Check("their ready mark again", () => !FriendSeat().Ready, "the card not ready");
    }

    /// <summary>
    /// <c>dc=on</c>: Direct Connections pressed by the pad with the one-PC friend in. The row shows only where the rule wants it (the setting on, or
    /// Steam refused relay only at this start); a tour may change neither, so the rows get the check-only context: the real one read now with
    /// Steam's refusal stood in, nothing else changed. The press goes the player's way (the game's toggle, the rows' handler, Core's
    /// <c>DirectConnectionsRule.OnPress</c> with the session's real count of others): refused for the session, the row back, the game's notice to
    /// leave first and its line; the setting and <c>settings.cfg</c> as they were. The context goes before the step ends.
    /// </summary>
    private static IEnumerable<Wait> StepOnePcDirect()
    {
        OnlineSession session = Sd();
        long logFrom = SessionLogLength();
        StepNote($"others in this online game: {session.OthersHosted} (the one-PC friend's pad); {SeatLine(FriendSeat())}");
        if (!Expect("the friend in", () => FriendSeat().Stage == SeatStage.Here && session.OthersHosted > 0, "Here, counted as someone else in this online game"))
        {
            yield break;
        }

        if (ModSettings.AllowDirectConnections)
        {
            // A press would turn it off, which a tour never does: the check is for turning it on.
            StepNote("Direct Connections is on in this player's settings: a press would turn it off, which a tour never does: not pressed");
            yield break;
        }

        string fileBefore = SettingsHash();
        SettingsContext real = TabRows.Context();
        TabRows.ForCheck = real with { RelayRefused = true };
        StepNote($"the rows' check-only context: this lobby's own ({DescribeState(real)}) with Steam's relay refusal stood in, so the rule shows the row");
        try
        {
            foreach (Wait wait in Trigger("RT", right: true, () => Settings() is { IsHiding: false } && SettingsOverLobby(), "the game's Settings over the lobby", within: 8))
            {
                yield return wait;
            }

            yield return Seconds(1.2);
            foreach (Wait wait in RbToOnline())
            {
                yield return wait;
            }

            WalkCheck("Direct Connections in the lobby", () => Settings() is { } menu && SettingsTab.Shows(menu, SettingsRowId.DirectConnections), "shown (the context's relay refusal)");
            foreach (Wait wait in ToRow(SettingsRowId.DirectConnections))
            {
                yield return wait;
            }

            WalkCheck("the toggle before the press", () => DirectShows() == false, "off, as the setting");
            foreach (Wait wait in WalkPress("A on Direct Connections, the friend in", PadButtons.A, LeaveFirstUp, "refused for the session: the game's notice to leave first", within: 3))
            {
                yield return wait;
            }

            string leaveFirst = LobbyLocalization.Say(LobbyWords.LeaveFirst.Key);
            WalkCheck("the notice's words", () => PromptText().Contains(leaveFirst, StringComparison.Ordinal), $"\"{leaveFirst}\" (it says \"{PromptText()}\")", gate: false);
            if (_walkOk)
            {
                foreach (Wait wait in Snap("direct_leave_first", 6))
                {
                    yield return wait;
                }
            }

            foreach (Wait wait in WalkPress("B on the notice", PadButtons.B, () => !LobbyPrompts.IsOpen && Settings() != null && OnRow(SettingsRowId.DirectConnections),
                "Settings up again, the cursor back on Direct Connections", within: 4))
            {
                yield return wait;
            }

            WalkCheck("the toggle after the press", () => DirectShows() == false && !ModSettings.AllowDirectConnections, "off, the setting off", gate: false);
            int said = SessionLogCount(logFrom, DirectConnectionsInSessionStem);
            WalkCheck("the refusal's line", () => said > 0, $"\"{DirectConnectionsInSessionStem} ...\" in the session log ({said} line(s))", gate: false);

            // B only on Settings or a prompt over the lobby: the step's own way back, whatever failed before.
            for (int i = 0; i < 4 && Top()?.Pointer != LobbyShell.Menu?.Pointer && SettingsOverLobby(); i++)
            {
                foreach (Wait wait in Tap(PadButtons.B))
                {
                    yield return wait;
                }

                yield return Soft(() => Top()?.Pointer == LobbyShell.Menu?.Pointer, 3, "Settings gone");
            }

            yield return Seconds(WalkSettleSeconds);
            Check("B on Settings", () => Top()?.Pointer == LobbyShell.Menu?.Pointer && CursorInShell() && FriendSeat().Stage == SeatStage.Here, "the lobby, its cursor in it, the friend still Here");
            Check("the settings file", () => SettingsHash() == fileBefore, "untouched");
        }
        finally
        {
            TabRows.ForCheck = null;
        }
    }

    // The rows' line for a press refused with others in the online game (NativeUi/SettingsTabRows.cs, LeaveFirstForDirectConnections).
    private const string DirectConnectionsInSessionStem = "Settings: Direct Connections asked to turn on with others in this online game; it stays off";

    /// <summary>What the Direct Connections row's toggle shows, or null when it isn't there.</summary>
    private static bool? DirectShows()
    {
        AbstractNavigable? row = Settings() is { } menu ? SettingsTab.Row(menu, SettingsRowId.DirectConnections) : null;
        Il2CppBishopGames.UI.Toggle? toggle = row?.GetComponent<Il2CppBishopGames.UI.Toggle>();
        return toggle?.IsSelected;
    }

    private static IEnumerable<Wait> StepOnePcClosePrompt()
    {
        LobbyFocus at = Lobby().Focus;
        foreach (Wait wait in AskClose())
        {
            yield return wait;
        }

        foreach (Wait wait in Snap("close_asks", 0))
        {
            yield return wait;
        }

        foreach (Wait wait in Press("B on the question", PadButtons.B, () => Top()?.Pointer == LobbyShell.Menu?.Pointer && !LobbyPrompts.IsOpen && FocusIs(at) && CursorInShell() && Flow().Host == LobbyHostState.Open, "the lobby, the cursor where it was", within: 4))
        {
            yield return wait;
        }

        yield return Seconds(1);
        Expect("after B on the question", () => !LobbyPrompts.IsOpen && Top()?.Pointer == LobbyShell.Menu?.Pointer, "not asked again");
        foreach (Wait wait in AskClose())
        {
            yield return wait;
        }

        foreach (Wait wait in Press("A on Stay", PadButtons.A, () => Top()?.Pointer == LobbyShell.Menu?.Pointer && !LobbyPrompts.IsOpen && FocusIs(at) && Flow().Host == LobbyHostState.Open, "the lobby, the cursor where it was", within: 4))
        {
            yield return wait;
        }
    }

    private static IEnumerable<Wait> AskClose()
    {
        foreach (Wait wait in Press("B on the lobby, a friend in", PadButtons.B, () => Top()?.TryCast<CustomizableYesNoPrompt>() != null, "the close question", within: 4))
        {
            yield return wait;
        }

        yield return Seconds(0.9);
        StepNote($"the question: cursor {Cursor()}");
    }

    private static IEnumerable<Wait> StepOnePcStart()
    {
        OnlineSession session = Sd();
        foreach (Wait wait in MoveTo(Row(LobbyFocusKind.StartRow)))
        {
            yield return wait;
        }

        yield return Soft(() => session.Actions.Snapshot().StartAllowed, 8, "Start allowed");
        foreach (Wait wait in StartToController("A on Start", PadButtons.A))
        {
            yield return wait;
        }

        StepNote(session.TourFriendLine());
        StepNote($"the barrier: last ack {Flow().LastAckAfterMicros / 1000} ms after it began, awaiting {Flow().AwaitingAcks.Count}, the friend game player {session.TourFriendGamePlayer}, following {Flow().IsFollowing(Il2CppSteamId())}");
        Check("the barrier", () => Flow().LastAckAfterMicros >= 0 && Flow().AwaitingAcks.Count == 0 && Flow().IsFollowing(Il2CppSteamId()), "their ack taken, nobody awaited");
        Check("seat order at Start", () => session.TourFriendGamePlayer >= 0, "the friend a game player, by the game's own AssignNextPlayer");
        Check("the controller screen", () => ControllerPlayers() == 3, "three players: the keyboard, the host's pad, the friend");

        // Their press, over Steam, on the real screen: their own card moves. Only on that screen: theirs is a press too.
        string before = ControllerScreenLine();
        if (MayPressOrStop("the friend's left on the controller screen", () => Top()?.TryCast<ControllerAssignmentMenu>() != null))
        {
            session.TourFriendPress(PadButtons.DPadLeft);
            yield return Seconds(0.2);
            session.TourFriendPress(PadButtons.None);
            yield return Soft(() => ControllerScreenLine() != before, 1.5, "the friend's card to move");
            StepNote($"the friend pressed left: {ControllerScreenLine()}");
            Check("the friend's press", () => ControllerScreenLine() != before, "their card moved");
            foreach (Wait wait in Snap("controller_screen_friend", SettleFrames))
            {
                yield return wait;
            }
        }

        foreach (Wait wait in BackOutOfController())
        {
            yield return wait;
        }

        Check("the lobby after the back-out", () => Flow().Host == LobbyHostState.Open && LobbyShell.IsShown && !LobbyShell.HandingOver && FocusIs(Row(LobbyFocusKind.StartRow)) && CursorInShell(), "the flow Open, the shell live, the cursor on Start");
        if (FriendSeat().Stage == SeatStage.Loading)
        {
            StepNote($"coming home: {SeatLine(FriendSeat())}");
            foreach (Wait wait in Snap("seat_loading", 0))
            {
                yield return wait;
            }
        }

        yield return Soft(() => FriendSeat().Stage == SeatStage.Here, 6, "the friend home");
        StepNote($"{SeatLine(FriendSeat())}; {session.TourFriendLine()}");
        Check("the friend after the back-out", () => FriendSeat().Stage == SeatStage.Here, "Here again");
    }

    private static int ControllerPlayers()
    {
        ControllerAssignmentMenu? menu = Top()?.TryCast<ControllerAssignmentMenu>();
        int players = 0;
        Il2CppSystem.Collections.Generic.List<ControllerAssignmentPlayer>? list = menu?.m_Players;
        for (int i = 0; list != null && i < list.Count; i++)
        {
            players += list[i] != null && list[i].IsPlayerControlled ? 1 : 0;
        }

        return players;
    }

    private static IEnumerable<Wait> StepOnePcMatch()
    {
        OnlineSession session = Sd();
        foreach (Wait wait in StartToController("Start", PadButtons.Start))
        {
            yield return wait;
        }

        foreach (Wait wait in ForwardFromController())
        {
            yield return wait;
        }

        foreach (Wait wait in MatchAndMashedReturn())
        {
            yield return wait;
        }

        StepNote($"{SeatLine(FriendSeat())}; {session.TourFriendLine()}");
        yield return Soft(() => FriendSeat().Stage == SeatStage.Here, 8, "the friend home");
        Check("the friend after the return", () => FriendSeat().Stage == SeatStage.Here, "Here again");
    }

    private static IEnumerable<Wait> StepOnePcDrop()
    {
        OnlineSession session = Sd();
        foreach (Wait wait in MoveTo(Seat(1)))
        {
            yield return wait;
        }

        session.SimulateDrop();
        yield return Soft(() => FriendSeat().Stage == SeatStage.Reconnecting, 16, "Reconnecting on the card");
        StepNote($"{SeatLine(FriendSeat())}; {session.TourFriendLine()}");
        Expect("the drop", () => FriendSeat().Stage == SeatStage.Reconnecting && FriendSeat().ReconnectSeconds > 0 && FocusIs(Seat(1)), "Reconnecting with its seconds, the seat kept, the cursor still on it");
        foreach (Wait wait in Snap("seat_reconnecting", SettleFrames))
        {
            yield return wait;
        }

        foreach (Wait wait in Press("Start, a friend away", PadButtons.Start, () => Top()?.TryCast<CustomizableYesNoPrompt>() != null, "the question", within: 4))
        {
            yield return wait;
        }

        yield return Seconds(0.9);
        foreach (Wait wait in Snap("start_asks_reconnecting", 0))
        {
            yield return wait;
        }

        foreach (Wait wait in Press("B on the question", PadButtons.B, () => Flow().IsWaitingForEveryone && !LobbyPrompts.IsOpen && Top()?.Pointer == LobbyShell.Menu?.Pointer, "its safe answer: the wait", within: 4))
        {
            yield return wait;
        }

        foreach (Wait wait in Press("B on the wait", PadButtons.B, () => !Flow().IsWaitingForEveryone && Top()?.Pointer == LobbyShell.Menu?.Pointer && Flow().Host == LobbyHostState.Open, "the wait cancelled", within: 4))
        {
            yield return wait;
        }

        yield return Soft(() => FriendSeat().Stage == SeatStage.Here, 30, "back in the same seat");
        StepNote($"{SeatLine(FriendSeat())}; {session.TourFriendLine()}");
        Check("the return", () => FriendSeat().Stage == SeatStage.Here && session.Actions.Snapshot().Players == 2, "Here in the same seat");
        foreach (Wait wait in Snap("seat_back", SettleFrames))
        {
            yield return wait;
        }
    }

    private static IEnumerable<Wait> StepOnePcKick()
    {
        OnlineSession session = Sd();
        foreach (Wait wait in MoveTo(Seat(1)))
        {
            yield return wait;
        }

        foreach (Wait wait in Trigger("LT on the friend's card", right: false, () => Top()?.TryCast<CustomizableYesNoPrompt>() != null, "the question", within: 4))
        {
            yield return wait;
        }

        yield return Seconds(0.9);
        StepNote($"the question: cursor {Cursor()}");
        foreach (Wait wait in Snap("remove_asks", 0))
        {
            yield return wait;
        }

        foreach (Wait wait in Press("B on the question", PadButtons.B, () => !LobbyPrompts.IsOpen && Top()?.Pointer == LobbyShell.Menu?.Pointer && FriendSeat().Stage == SeatStage.Here && FocusIs(Seat(1)), "they stay, the cursor on their card", within: 4))
        {
            yield return wait;
        }

        foreach (Wait wait in Trigger("LT on the friend's card", right: false, () => Top()?.TryCast<CustomizableYesNoPrompt>() != null, "the question", within: 4))
        {
            yield return wait;
        }

        yield return Seconds(0.9);
        foreach (Wait wait in OtherAnswer())
        {
            yield return wait;
        }

        foreach (Wait wait in Press("A on Remove", PadButtons.A, () => FriendSeat().IsEmpty && session.TourFriendGone, "the seat free, their session over", within: 8))
        {
            yield return wait;
        }

        yield return Seconds(WalkSettleSeconds);
        Check("after the kick", () => Flow().Host == LobbyHostState.Open && CursorInShell() && Top()?.Pointer == LobbyShell.Menu?.Pointer, "the lobby, the cursor in it");
        foreach (Wait wait in Snap("seat_removed", SettleFrames))
        {
            yield return wait;
        }

        // They're refused coming back, and hold nothing meanwhile.
        session.TourSeatFriend(holdHello: false);
        yield return Soft(() => session.TourFriendGone, 8, "the refusal");
        Check("coming back", () => session.TourFriendGone && FriendSeat().IsEmpty, "refused, the seat still free");

        // The tour goes on with them: the refusal forgotten.
        session.TourForgetRemoved();
        session.TourSeatFriend(holdHello: false);
        yield return Soft(() => FriendSeat().Stage == SeatStage.Here, 10, "the friend Here");
        Check("a new seat", () => FriendSeat().Stage == SeatStage.Here, "Here");
    }

    private static IEnumerable<Wait> OtherAnswer()
    {
        string safe = Cursor();
        if (!MayPressOrStop("the question's other answer", () => LobbyPrompts.IsOpen && Top()?.TryCast<CustomizableYesNoPrompt>() != null))
        {
            yield break;
        }

        foreach (Wait wait in Tap(PadButtons.DPadDown))
        {
            yield return wait;
        }

        yield return Soft(() => Cursor() != safe, 0.6, "the other answer");
        if (Cursor() == safe && Top()?.TryCast<CustomizableYesNoPrompt>() != null)
        {
            foreach (Wait wait in Tap(PadButtons.DPadRight))
            {
                yield return wait;
            }

            yield return Soft(() => Cursor() != safe, 0.6, "the other answer");
        }

        Expect("the question's other answer", () => Cursor() != safe, "the cursor on it");
        yield return Seconds(WalkSettleSeconds);
    }

    private static IEnumerable<Wait> StepOnePcClose()
    {
        OnlineSession session = Sd();
        foreach (Wait wait in AskClose())
        {
            yield return wait;
        }

        foreach (Wait wait in OtherAnswer())
        {
            yield return wait;
        }

        foreach (Wait wait in Press("A on Close", PadButtons.A, () => session.Actions.Snapshot().Role == LobbyRole.None && !LobbyShell.IsOpen, "no session", within: ShellSeconds))
        {
            yield return wait;
        }

        yield return Soft(() => session.TourFriendGone && InGroup() && Cursor() == "HostLobby", 8, "the Online group, the friend told");
        Check("after the close", () => session.TourFriendGone && session.TourStuckLine().Length == 0, "the friend gone, nothing held");
        Expect("after the close, the title", () => InGroup() && Cursor() == "HostLobby", "the Online group on Host Lobby");
        foreach (Wait wait in Snap("closed", 0))
        {
            yield return wait;
        }

        foreach (Wait wait in Press("B on the group", PadButtons.B, () => OnMain() && Cursor() == "Online", "Main, the cursor on Online"))
        {
            yield return wait;
        }
    }
}
#endif
