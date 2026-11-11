#if HAS_INTEROP
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Il2CppBishopGames.Input;
using Il2CppBishopGames.UI;
using Il2CppControllerConnection;
using Il2CppInterop.Runtime;
using Il2CppRewired;
using Il2CppTape2Tape.Customization.UI;
using Il2CppTape2Tape.Hockey.UI;
using T2TOnline.Core.Input;
using T2TOnline.Core.Lobby;
using T2TOnline.Core.OnlineSettings;
using T2TOnline.Plugin.Campaign;
using T2TOnline.Plugin.Campaign.Menus;
using T2TOnline.Plugin.Diagnostics;
using T2TOnline.Plugin.Input;
using T2TOnline.Plugin.Match;
using T2TOnline.Plugin.NativeLobby.Widgets;
using T2TOnline.Plugin.NativeUi;
using T2TOnline.Plugin.Online;
using T2TOnline.Plugin.Startup;
using T2TOnline.Plugin.Streaming;
using UnityEngine;

namespace T2TOnline.Plugin.NativeLobby;

// The walk by a pad alone (run.txt's first line "padwalk"): a virtual pad, a Rewired custom controller as a friend's is, joined by the game's own join code at the first press, the title's
// controlling player from then on as a real pad's is (PressedToStart's and CheckForStartOnAnyInput's SetSingleControllingPlayer_Internal; verified in the game's code).
// Rewired sees its buttons go down and up: the game's UI actions fire, RewiredUIInputReader tells the navigation, UIInputShortcutBase runs its own press and hold. Nothing here calls a screen,
// Trigger or OnConfirm. Each press's effect is asserted once, and a FAIL line says what was expected and what's on screen instead. The same pad presses for the one-PC tour.
internal static partial class LobbyUiCheck
{
    private const double WalkSettleSeconds = 0.3;
    private const double RepeatGapAtLeast = 0.3;

    private static WalkPad? _walk;
    private static bool _focusSettingSaved;
    private static bool _ignoredUnfocused;
    private static int _slotAtStart = -1;

    private static bool Walking => UiCheckPlan.IsWalkPlan(_tour);

    /// <summary>A tour on the real session, whose steps share a lobby (or the replay tour's, a match; the campaign tour's, a run): nothing is closed between them, and no fake is attached.</summary>
    private static bool OnRealSession => SelfDriving || Walking || Replaying || Campaigning;

    private static void AddPadWalkSteps()
    {
        Steps["pw_start"] = StepWalkStart;
        Steps["pw_group"] = StepWalkGroup;
        Steps["pw_join"] = StepWalkJoin;
        Steps["pw_host"] = StepWalkHost;
        Steps["pw_moves"] = StepWalkMoves;
        Steps["pw_tabs"] = StepWalkTabs;
        Steps["pw_selectors"] = StepWalkSelectors;
        Steps["pw_invite"] = StepWalkInvite;
        Steps["pw_hold"] = StepWalkHold;
        Steps["pw_count"] = StepWalkCount;
        Steps["pw_backout"] = StepWalkBackOut;
        Steps["pw_newrun"] = StepWalkNewRun;
        Steps["pw_lobby_settings"] = StepWalkSettings;
        Steps["pw_notice"] = StepWalkNotice;
        Steps["pw_close"] = StepWalkClose;
        Steps["pw_mash"] = StepWalkMash;
        Steps["pw_quick"] = StepWalkQuick;
        AddEndOnlineSteps(); // endonline=on's (LobbyUiCheck.EndOnline.cs)
    }

    /// <summary>Every frame while a tour has its pad: what the game's join poll does for a real pad, it does not for a custom controller: <see cref="GameJoin"/> relays it.</summary>
    private static void TickWalk()
    {
        _walk?.Tick();
        _keys?.Tick();
    }

    // ---- The steps ----

    private static IEnumerable<Wait> StepWalkStart()
    {
        OnlineSession session = Sd();
        TitleScreen title = Title();
        foreach (Wait wait in MakePad())
        {
            yield return wait;
        }

        if (_walk == null)
        {
            yield break;
        }

        StepNote($"at the start: group {(int)title.m_CurrentButtonGroup}, window focused {Application.isFocused}, the UI's controlling player: {Controlling()}, {Application.targetFrameRate} fps asked, {1 / Math.Max(Time.unscaledDeltaTime, 0.001f):0} now");
        foreach (Wait wait in Snap("press_to_start", SettleFrames))
        {
            yield return wait;
        }

        if ((int)title.m_CurrentButtonGroup == 0)
        {
            // A real pad's first press joins it (RewiredPlayerInputAssigner.Tick); the title takes a press from a pad that's a game player. Which comes first in a frame says whether a second press is needed.
            foreach (Wait wait in Press("A, the pad's first press", PadButtons.A, () => _walk!.GamePlayer >= 0, "the pad a game player, by the game's join"))
            {
                yield return wait;
            }

            yield return Seconds(0.5);
            if ((int)Title().m_CurrentButtonGroup == 0)
            {
                StepNote("the title stayed on press-to-start: the first press only joined the pad (the title's check ran before it was a game player); a person presses again");
                foreach (Wait wait in Press("A, a second press", PadButtons.A, () => (int)Title().m_CurrentButtonGroup != 0, "off press-to-start"))
                {
                    yield return wait;
                }
            }
        }

        yield return Soft(() => (int)Title().m_CurrentButtonGroup == 1 && !Title()._isTransitioningGroup, 8, "the main group");
        Check("press-to-start", () => (int)Title().m_CurrentButtonGroup == 1 && !Title()._isTransitioningGroup, "the main group");
        Check("the UI's controlling player", () => IsPadControlling(), $"the pad (Rewired player {_walk!.RewiredId})");
        yield return Soft(() => OnlineGroup.IsIn && Cursor() == "Campaign", 5, "the cursor on Campaign");
        Check("Main's first focus", () => OnlineGroup.IsIn && Cursor() == "Campaign", "the cursor on Campaign, the Online row in");
        UiAssets.Request(OnlineGroup.Sources);
        yield return Until(() => Array.TrueForAll(OnlineGroup.Sources, UiAssets.Ready) || UiAssets.Problem != null, UiAssets.TimeoutSeconds + 1, "the source prefabs");
        foreach (Wait wait in Snap("main", SettleFrames))
        {
            yield return wait;
        }
    }

    private static IEnumerable<Wait> StepWalkGroup()
    {
        foreach (Wait wait in Press("down", PadButtons.DPadDown, () => Cursor() == "PlayNow", "the cursor on Play Now"))
        {
            yield return wait;
        }

        foreach (Wait wait in Press("down", PadButtons.DPadDown, () => Cursor() == "Online", "the cursor on Online", snap: "online_row"))
        {
            yield return wait;
        }

        for (int round = 1; round <= 2; round++)
        {
            foreach (Wait wait in OpenGroup($"A on Online ({round})", round == 1 ? "group" : null))
            {
                yield return wait;
            }

            foreach (Wait wait in Press($"B on the group ({round})", PadButtons.B, () => OnMain() && Cursor() == "Online", "Main, the cursor on Online", snap: round == 1 ? "back_on_online" : null))
            {
                yield return wait;
            }
        }

        // The group's own Back row, by up from its first: the title's lists wrap.
        foreach (Wait wait in OpenGroup("A on Online", null))
        {
            yield return wait;
        }

        string first = Cursor();
        foreach (Wait wait in Press("up, past the group's first", PadButtons.DPadUp, () => Cursor() != first && Cursor() != "JoinLobby", "its last row", snap: "group_back_row"))
        {
            yield return wait;
        }

        foreach (Wait wait in Press("A on the group's Back", PadButtons.A, () => OnMain() && Cursor() == "Online", "Main, the cursor on Online"))
        {
            yield return wait;
        }
    }

    private static IEnumerable<Wait> StepWalkJoin()
    {
        foreach (Wait wait in OpenGroup("A on Online", null))
        {
            yield return wait;
        }

        if (Cursor() != "JoinLobby")
        {
            foreach (Wait wait in Press("down", PadButtons.DPadDown, () => Cursor() == "JoinLobby", "the cursor on Join Lobby"))
            {
                yield return wait;
            }
        }

        foreach (Wait wait in Press("A on Join Lobby", PadButtons.A, () => LobbyShell.IsIdle && LobbyShell.Kind == ShellKind.Browser && CursorInShell(), "the real Join list, its cursor in it", within: ShellSeconds))
        {
            yield return wait;
        }

        yield return Seconds(1);
        StepNote($"the real Join list: {LobbyScreens.Instance.Hosts.Rows.Count} row(s)");
        foreach (Wait wait in Snap("join_list", 0))
        {
            yield return wait;
        }

        int refreshes = Sd().TourRefreshes;
        if (MayPressOrStop("X held on the Join list", () => LobbyShell.IsShown && LobbyShell.Kind == ShellKind.Browser && Top()?.Pointer == LobbyShell.Menu?.Pointer))
        {
            _walk!.Hold(PadButtons.X);
            yield return Seconds(0.8);
            _walk.Let(PadButtons.X);
            yield return Seconds(WalkSettleSeconds);
            Check("X held for 0.8 s on the Join list", () => Sd().TourRefreshes == refreshes + 1, $"one refresh, {Sd().TourRefreshes - refreshes} made");
        }

        foreach (Wait wait in Press("B on the Join list", PadButtons.B, () => !LobbyShell.IsOpen && InGroup() && Cursor() == "JoinLobby", "the Online group, the cursor on Join Lobby", within: ShellSeconds, snap: "back_on_join"))
        {
            yield return wait;
        }
    }

    private static IEnumerable<Wait> StepWalkHost()
    {
        foreach (Wait wait in Press("up", PadButtons.DPadUp, () => Cursor() == "HostLobby", "the cursor on Host Lobby"))
        {
            yield return wait;
        }

        foreach (Wait wait in OpenLobby("A on Host Lobby"))
        {
            yield return wait;
        }

        LobbyState state = Sd().Actions.Snapshot();
        StepNote($"the lobby: role {state.Role}, mode {state.Mode}, save {state.Save.Slot}, seats {state.Seats.Count}, players {state.Players}; the status box's setting {ModSettings.ShowStatusBox}");
    }

    private static IEnumerable<Wait> StepWalkMoves()
    {
        (PadButtons, string, LobbyFocus)[] walk =
        {
            (PadButtons.DPadRight, "right", Seat(2)),
            (PadButtons.DPadRight, "right", Seat(3)),
            (PadButtons.DPadRight, "right, the seats' end", Seat(3)),
            (PadButtons.DPadUp, "up, on a seat", Seat(3)),
            (PadButtons.DPadDown, "down, on a seat", Seat(3)),
            (PadButtons.DPadLeft, "left", Seat(2)),
            (PadButtons.DPadLeft, "left", Seat(1)),
            (PadButtons.DPadLeft, "left", Seat(0)),
            (PadButtons.DPadLeft, "left, off the seats", Row(LobbyFocusKind.StartRow)),
            (PadButtons.DPadLeft, "left, the column's edge", Row(LobbyFocusKind.StartRow)),
            (PadButtons.DPadDown, "down, the column's end", Row(LobbyFocusKind.StartRow)),
            (PadButtons.DPadUp, "up", Row(LobbyFocusKind.InviteRow)),
            (PadButtons.DPadUp, "up", Row(LobbyFocusKind.SaveRow)),
            (PadButtons.DPadUp, "up", Row(LobbyFocusKind.ModeRow)),
            (PadButtons.DPadUp, "up, the column's top", Row(LobbyFocusKind.ModeRow)),
            (PadButtons.DPadDown, "down", Row(LobbyFocusKind.SaveRow)),
            (PadButtons.DPadDown, "down", Row(LobbyFocusKind.InviteRow)),
            (PadButtons.DPadLeft, "left on Invite, the column's edge", Row(LobbyFocusKind.InviteRow)),
            (PadButtons.DPadRight, "right, off Invite", Seat(1)),
            (PadButtons.DPadLeft, "left", Seat(0)),
            (PadButtons.DPadLeft, "left", Row(LobbyFocusKind.StartRow)),
            (PadButtons.DPadRight, "right, off Start", Seat(1)),
        };
        foreach ((PadButtons button, string what, LobbyFocus focus) in walk)
        {
            foreach (Wait wait in Press(what, button, () => FocusIs(focus), Named(focus), snap: Snappable(focus, what)))
            {
                yield return wait;
            }
        }
    }

    private static IEnumerable<Wait> StepWalkTabs()
    {
        OnlineSession session = Sd();
        ILobbyActions actions = session.Actions;
        LobbyView lobby = Lobby();
        Check("the mode to begin with", () => actions.Snapshot().Mode == LobbyMode.Campaign, "Campaign");
        int before = session.TourModeChanges;
        foreach (Wait wait in Press("RB", PadButtons.RightBumper, () => actions.Snapshot().Mode == LobbyMode.QuickGame && !lobby.SaveRowShown, "Quick Game, the save row gone", snap: "quick_game"))
        {
            yield return wait;
        }

        Check("RB's changes", () => session.TourModeChanges == before + 1, $"one change, {session.TourModeChanges - before} made");
        before = session.TourModeChanges;
        foreach (Wait wait in Press("LB", PadButtons.LeftBumper, () => actions.Snapshot().Mode == LobbyMode.Campaign && lobby.SaveRowShown, "Campaign, the save row back", snap: "campaign"))
        {
            yield return wait;
        }

        Check("LB's changes", () => session.TourModeChanges == before + 1, $"one change, {session.TourModeChanges - before} made");

        // A long press is still one.
        before = session.TourModeChanges;
        if (MayPressOrStop("RB held", LobbyOnTop))
        {
            _walk!.Hold(PadButtons.RightBumper);
            yield return Seconds(0.8);
            _walk.Let(PadButtons.RightBumper);
            yield return Seconds(WalkSettleSeconds);
            Check("RB held for 0.8 s", () => session.TourModeChanges == before + 1 && actions.Snapshot().Mode == LobbyMode.QuickGame, $"one change, {session.TourModeChanges - before} made");
        }
        before = session.TourModeChanges;
        foreach (Wait wait in Press("LB", PadButtons.LeftBumper, () => actions.Snapshot().Mode == LobbyMode.Campaign, "Campaign"))
        {
            yield return wait;
        }

        // The cursor on the row that goes: put right.
        foreach (Wait wait in MoveTo(Row(LobbyFocusKind.SaveRow)))
        {
            yield return wait;
        }

        foreach (Wait wait in Press("RB, the cursor on the save row", PadButtons.RightBumper, () => actions.Snapshot().Mode == LobbyMode.QuickGame && FocusIs(Row(LobbyFocusKind.ModeRow)) && CursorInShell(), "Quick Game, the cursor put right on the mode", snap: "save_row_went"))
        {
            yield return wait;
        }

        foreach (Wait wait in Press("LB", PadButtons.LeftBumper, () => actions.Snapshot().Mode == LobbyMode.Campaign && FocusIs(Row(LobbyFocusKind.ModeRow)), "Campaign, the cursor stays on the mode"))
        {
            yield return wait;
        }
    }

    private static IEnumerable<Wait> StepWalkSelectors()
    {
        OnlineSession session = Sd();
        ILobbyActions actions = session.Actions;
        Check("on the mode selector", () => FocusIs(Row(LobbyFocusKind.ModeRow)), "the cursor on the mode");
        foreach ((PadButtons button, string what, LobbyMode mode, int changes) in new[]
        {
            (PadButtons.DPadLeft, "left on the mode, its first", LobbyMode.Campaign, 0),
            (PadButtons.DPadRight, "right on the mode", LobbyMode.QuickGame, 1),
            (PadButtons.DPadRight, "right on the mode, its last", LobbyMode.QuickGame, 0),
            (PadButtons.DPadLeft, "left on the mode", LobbyMode.Campaign, 1),
        })
        {
            int before = session.TourModeChanges;
            foreach (Wait wait in Press(what, button, () => actions.Snapshot().Mode == mode && FocusIs(Row(LobbyFocusKind.ModeRow)), $"{mode}, the cursor stays"))
            {
                yield return wait;
            }

            Check($"{what}: its changes", () => session.TourModeChanges == before + changes, $"{changes} change(s), {session.TourModeChanges - before} made");
        }

        foreach (Wait wait in Press("down", PadButtons.DPadDown, () => FocusIs(Row(LobbyFocusKind.SaveRow)), "the cursor on the save row"))
        {
            yield return wait;
        }

        // There and back from the player's slot, in whichever direction there's a neighbour.
        int slots = actions.Slots().Count;
        int slot = actions.Snapshot().Save.Slot;
        bool down = slot > 1;
        int other = down ? slot - 1 : slot + 1;
        foreach (Wait wait in Press(down ? "left on the save" : "right on the save", down ? PadButtons.DPadLeft : PadButtons.DPadRight, () => actions.Snapshot().Save.Slot == other, $"slot {other}", snap: "slot_other"))
        {
            yield return wait;
        }

        yield return Soft(() => actions.Snapshot().StartAllowed, 8, "Start allowed again");
        Check("after the slot's change", () => actions.Snapshot().StartAllowed, "Start allowed");
        foreach (Wait wait in Press(down ? "right on the save" : "left on the save", down ? PadButtons.DPadRight : PadButtons.DPadLeft, () => actions.Snapshot().Save.Slot == slot, $"slot {slot} again"))
        {
            yield return wait;
        }

        yield return Soft(() => actions.Snapshot().StartAllowed, 8, "Start allowed again");
        Check("the save selector's end", () => CoopSlots.Selected == _slotAtStart && actions.Snapshot().StartAllowed, $"the player's slot {_slotAtStart}, and Start allowed");
        StepNote($"{slots} slots");
    }

    private static IEnumerable<Wait> StepWalkInvite()
    {
        LobbyView lobby = Lobby();
        foreach (Wait wait in MoveTo(Seat(1)))
        {
            yield return wait;
        }

        foreach (Wait wait in Press("A on the empty seat", PadButtons.A, () => lobby.PanelOpen && CursorInShell(), "the friends list, its cursor in it"))
        {
            yield return wait;
        }

        yield return Seconds(1.5);
        int rows = lobby.FriendRowCount;
        StepNote($"the friends list: {rows} row(s); invites refused while a tour runs");
        foreach (Wait wait in Snap("friends_list", 0))
        {
            yield return wait;
        }

        long first = CursorPointer();
        if (rows >= 2)
        {
            foreach (Wait wait in Press("down in the list", PadButtons.DPadDown, () => CursorInShell() && CursorPointer() != first, "the next row"))
            {
                yield return wait;
            }

            foreach (Wait wait in Press("up in the list", PadButtons.DPadUp, () => CursorPointer() == first, "the first row again"))
            {
                yield return wait;
            }
        }
        else
        {
            StepNote($"{rows} row(s): down and up have nowhere to go: they stay");
            foreach (Wait wait in Press("down in the list", PadButtons.DPadDown, () => CursorInShell(), "the cursor in the list"))
            {
                yield return wait;
            }

            foreach (Wait wait in Press("up in the list", PadButtons.DPadUp, () => CursorInShell(), "the cursor in the list"))
            {
                yield return wait;
            }
        }

        int modes = Sd().TourModeChanges;
        long on = CursorPointer();

        // Never X here: the list's bar puts Steam's invite window on it (UI_UISpecial) while the overlay is on.
        foreach (PadButtons other in new[] { PadButtons.RightBumper, PadButtons.Y, PadButtons.Start })
        {
            if (!MayPressOrStop($"{other} on the list", () => lobby.PanelOpen && CursorInShell()))
            {
                break;
            }

            foreach (Wait wait in Tap(other))
            {
                yield return wait;
            }

            yield return Seconds(WalkSettleSeconds);
        }

        Expect("RB, Y and Start on the list", () => lobby.PanelOpen && Sd().TourModeChanges == modes && CursorPointer() == on && Flow().Host == LobbyHostState.Open, "nothing: the list has the bar");
        foreach (Wait wait in Press("B on the list", PadButtons.B, () => !lobby.PanelOpen && FocusIs(Seat(1)) && CursorInShell(), "back on that seat", snap: "panel_back_on_seat"))
        {
            yield return wait;
        }

        foreach (Wait wait in Press("Y", PadButtons.Y, () => lobby.PanelOpen && CursorInShell(), "the friends list by its shortcut"))
        {
            yield return wait;
        }

        foreach (Wait wait in Press("B on the list", PadButtons.B, () => !lobby.PanelOpen && FocusIs(Seat(1)) && CursorInShell(), "back on that seat"))
        {
            yield return wait;
        }

        foreach (Wait wait in MoveTo(Row(LobbyFocusKind.InviteRow)))
        {
            yield return wait;
        }

        foreach (Wait wait in Press("A on Invite", PadButtons.A, () => lobby.PanelOpen && CursorInShell(), "the friends list"))
        {
            yield return wait;
        }

        // A press on a friend, straight away: the one that opened it may still be going. No invite either way, while a tour runs; the screen's own half second is what's asked here.
        foreach (Wait wait in Press("B on the list", PadButtons.B, () => !lobby.PanelOpen && FocusIs(Row(LobbyFocusKind.InviteRow)) && CursorInShell(), "back on Invite"))
        {
            yield return wait;
        }

        foreach (Wait wait in MoveTo(Seat(1)))
        {
            yield return wait;
        }
    }

    private static IEnumerable<Wait> StepWalkHold()
    {
        // The game's repeat: 0.5 s, then quicker. Each stop of the cursor with its time.
        var seen = new List<(double At, LobbyFocus Focus)>();
        foreach (Wait wait in HoldWatching("D-pad right", p => p.Hold(PadButtons.DPadRight), p => p.Let(PadButtons.DPadRight), 1.6, seen))
        {
            yield return wait;
        }

        CheckHold("right held", seen, Seat(2), Seat(3));
        foreach (Wait wait in HoldWatching("D-pad left", p => p.Hold(PadButtons.DPadLeft), p => p.Let(PadButtons.DPadLeft), 2.2, seen))
        {
            yield return wait;
        }

        CheckHold("left held", seen, Seat(2), Seat(1), Seat(0), Row(LobbyFocusKind.StartRow));
        foreach (Wait wait in HoldWatching("the stick up", p => p.Stick(0, 1), p => p.Stick(0, 0), 2.2, seen))
        {
            yield return wait;
        }

        CheckHold("the stick held up", seen, Row(LobbyFocusKind.InviteRow), Row(LobbyFocusKind.SaveRow), Row(LobbyFocusKind.ModeRow));
        foreach (Wait wait in HoldWatching("the stick down", p => p.Stick(0, -1), p => p.Stick(0, 0), 2.2, seen))
        {
            yield return wait;
        }

        CheckHold("the stick held down", seen, Row(LobbyFocusKind.SaveRow), Row(LobbyFocusKind.InviteRow), Row(LobbyFocusKind.StartRow));

        // What a hold does on a selector, whose left and right are a change of value: said, for the eye.
        foreach (Wait wait in MoveTo(Row(LobbyFocusKind.ModeRow)))
        {
            yield return wait;
        }

        OnlineSession session = Sd();
        int before = session.TourModeChanges;
        foreach (Wait wait in HoldWatching("D-pad right on the mode", p => p.Hold(PadButtons.DPadRight), p => p.Let(PadButtons.DPadRight), 1.6, seen))
        {
            yield return wait;
        }

        int changes = session.TourModeChanges - before;
        StepNote($"right held 1.6 s on the mode selector: {changes} change(s), now {session.Actions.Snapshot().Mode}");
        Check("a hold on the mode selector", () => changes == 1, "one change: only two modes are listed, and strobing between them says nothing");
        if (session.Actions.Snapshot().Mode != LobbyMode.Campaign)
        {
            foreach (Wait wait in Press("LB", PadButtons.LeftBumper, () => session.Actions.Snapshot().Mode == LobbyMode.Campaign, "Campaign"))
            {
                yield return wait;
            }
        }
    }

    private static IEnumerable<Wait> StepWalkCount()
    {
        OnlineSession session = Sd();
        ILobbyActions actions = session.Actions;
        LobbyView lobby = Lobby();
        foreach (Wait wait in EnsureQuick())
        {
            yield return wait;
        }

        yield return Soft(() => actions.Snapshot().StartAllowed, 8, "Start allowed");
        uint epoch = Flow().Epoch;
        foreach (Wait wait in Press("Start", PadButtons.Start, () => Flow().Host == LobbyHostState.Counting && Flow().Epoch == epoch + 1, "the countdown, with a new epoch", snap: "countdown"))
        {
            yield return wait;
        }

        int modes = session.TourModeChanges;
        foreach (Wait wait in Press("Y in the countdown", PadButtons.Y, () => Flow().Host == LobbyHostState.Counting && !lobby.PanelOpen && FocusIs(Row(LobbyFocusKind.StartRow)), "still counting, no list, the cursor still on Start"))
        {
            yield return wait;
        }

        foreach (Wait wait in Press("RB in the countdown", PadButtons.RightBumper, () => Flow().Host == LobbyHostState.Counting && session.TourModeChanges == modes, "still counting, the mode as it was"))
        {
            yield return wait;
        }

        foreach (Wait wait in Press("B in the countdown", PadButtons.B, () => Flow().Host == LobbyHostState.Open && FocusIs(Row(LobbyFocusKind.StartRow)) && CursorInShell(), "the lobby, the cursor on Start", snap: "cancelled"))
        {
            yield return wait;
        }

        yield return Seconds(LobbyTiming.CountdownMicros / 1_000_000.0 + 0.3);
        Expect("after the countdown's time", () => Flow().Host == LobbyHostState.Open, "the lobby still");
        foreach (Wait wait in Press("A on Start", PadButtons.A, () => Flow().Host == LobbyHostState.Counting, "the countdown"))
        {
            yield return wait;
        }

        foreach (Wait wait in Press("B in the countdown", PadButtons.B, () => Flow().Host == LobbyHostState.Open && FocusIs(Row(LobbyFocusKind.StartRow)), "the lobby, the cursor on Start"))
        {
            yield return wait;
        }
    }

    private static IEnumerable<Wait> StepWalkBackOut()
    {
        foreach (Wait wait in StartToController("A on Start", PadButtons.A))
        {
            yield return wait;
        }

        foreach (Wait wait in BackOutOfController())
        {
            yield return wait;
        }

        Expect("the lobby after the back-out", () => Flow().Host == LobbyHostState.Open && LobbyShell.IsShown && !LobbyShell.HandingOver && !Title().m_IsExiting && !Title().m_IsStartingRun, "the flow Open, the shell live, the title not leaving");
        Expect("the cursor after the back-out", () => FocusIs(Row(LobbyFocusKind.StartRow)) && CursorInShell(), "on Start");
        foreach (Wait wait in Press("up", PadButtons.DPadUp, () => FocusIs(Row(LobbyFocusKind.InviteRow)), "the cursor on Invite: the lobby answers", snap: "alive_after_backout"))
        {
            yield return wait;
        }

        foreach (Wait wait in Press("down", PadButtons.DPadDown, () => FocusIs(Row(LobbyFocusKind.StartRow)), "the cursor on Start"))
        {
            yield return wait;
        }
    }

    /// <summary>The other start: Campaign, New Run on the empty slot, the game's squad screen over the shell, and B as a person would. Opening that screen can save the profile: the guard refuses, and lists it.</summary>
    private static IEnumerable<Wait> StepWalkNewRun()
    {
        ILobbyActions actions = Sd().Actions;
        foreach (Wait wait in Press("LB", PadButtons.LeftBumper, () => actions.Snapshot().Mode == LobbyMode.Campaign, "Campaign"))
        {
            yield return wait;
        }

        yield return Soft(() => actions.Snapshot().StartAllowed, 8, "Start allowed");
        if (actions.Snapshot().Save.HasRun)
        {
            StepNote("the slot holds a run: Start would continue it, which leaves the title for good: not walked");
        }
        else
        {
            foreach (Wait wait in Press("Start, New Run", PadButtons.Start, () => Flow().Host is LobbyHostState.Counting or LobbyHostState.Starting or LobbyHostState.GameFlow, "the countdown"))
            {
                yield return wait;
            }

            yield return Soft(() => Flow().Host == LobbyHostState.GameFlow, 8, "the game's flow");
            yield return Soft(() => Top()?.TryCast<ChooseMetaMenu>() != null, 12, "the game's squad screen");
            yield return Seconds(1.5);
            Check("New Run by the pad", () => Top()?.TryCast<ChooseMetaMenu>() != null && LobbyShell.IsShown && LobbyShell.HandingOver, "the squad screen, the shell under it");
            foreach (Wait wait in Snap("squad_screen", 0))
            {
                yield return wait;
            }

            for (int i = 1; i <= 6 && (Flow().Host != LobbyHostState.Open || Top()?.Pointer != LobbyShell.Menu?.Pointer) && SquadOverLobby(); i++)
            {
                foreach (Wait wait in Tap(PadButtons.B))
                {
                    yield return wait;
                }

                yield return Soft(() => Flow().Host == LobbyHostState.Open && Top()?.Pointer == LobbyShell.Menu?.Pointer, 3, "the lobby");
                StepNote($"B {i} on the squad screen: {Where()}");
            }

            yield return Soft(() => LobbyShell.IsShown && !LobbyShell.HandingOver && CursorInShell(), 4, "the lobby's cursor back");
            yield return Seconds(WalkSettleSeconds);
            Expect("the lobby after the squad screen", () => Flow().Host == LobbyHostState.Open && !Title().m_IsStartingRun && !Title().m_IsExiting && FocusIs(Row(LobbyFocusKind.StartRow)) && CursorInShell(), "the flow Open, the title live, the cursor on Start");
        }

        foreach (Wait wait in EnsureQuick())
        {
            yield return wait;
        }
    }

    private static IEnumerable<Wait> StepWalkSettings()
    {
        // Safe under the guard (verified in the game's code, reached from every tab's OnEnable and OnDisable): closing it reaches only SystemSettings.Save, BaseGameSettings' and
        // GameplaySettings' SaveSettings, all ending in the writes the guard refuses and lists below; nothing here changes a value; the title's menus are re-prepared only if a language
        // changed.
        uint epoch = Flow().Epoch;
        yield return Soft(() => Sd().Actions.Snapshot().StartAllowed, 8, "Start allowed");
        foreach (Wait wait in Trigger("RT", right: true, () => Top() is { } top && MenuGame.MenuId(top).Contains("Settings", StringComparison.Ordinal), "the game's Settings over the lobby", within: 8))
        {
            yield return wait;
        }

        yield return Seconds(1.5);
        StepNote($"Settings: flow {Flow().Host}, cursor {Cursor()}, controlling: {Controlling()}");
        foreach (Wait wait in Snap("settings", 0))
        {
            yield return wait;
        }

        Check("the flow under Settings", () => Flow().Host == LobbyHostState.Open && Flow().Epoch == epoch, "no phase change");
        int modes = Sd().TourModeChanges;
        if (MayPressOrStop("RB on Settings", () => Settings() != null))
        {
            foreach (Wait wait in Tap(PadButtons.RightBumper))
            {
                yield return wait;
            }

            yield return Seconds(WalkSettleSeconds);
            if (!Check("RB on Settings", () => Sd().TourModeChanges == modes && Top() is { } over && MenuGame.MenuId(over).Contains("Settings", StringComparison.Ordinal), "Settings' own tab, the lobby's mode as it was"))
            {
                _walkOk = false;
            }
        }

        // The Online tab as the host sees it in a session (LobbyUiCheck.SettingsWalk.cs): End Online Game shown and reached, never pressed; Leave not shown;
        // Save Backups refused (a session is on, the lobby is open under Settings): the game's one-button prompt says to leave first.
        foreach (Wait wait in RbToOnline())
        {
            yield return wait;
        }

        WalkCheck("End Online Game in the lobby", () => Settings() is { } menu && SettingsTab.Shows(menu, SettingsRowId.EndOnlineGame) && !SettingsTab.Shows(menu, SettingsRowId.LeaveOnlineGame),
            "shown for the host in a session, Leave not", gate: false);
        foreach (Wait wait in ToRow(SettingsRowId.SaveBackups))
        {
            yield return wait;
        }

        foreach (Wait wait in LeaveFirstPrompt())
        {
            yield return wait;
        }

        if (Settings() is { } open && SettingsTab.Shows(open, SettingsRowId.EndOnlineGame))
        {
            foreach (Wait wait in ToRow(SettingsRowId.EndOnlineGame))
            {
                yield return wait;
            }

            StepNote("End Online Game: reached by the cursor, never pressed (it would end the session)");
        }

        if (_walkOk)
        {
            foreach (Wait wait in Snap("lobby_settings_online", 6))
            {
                yield return wait;
            }
        }

        // B only on Settings or a prompt over the lobby: the step's own way back to the lobby, whatever failed before.
        for (int i = 0; i < 4 && Top()?.Pointer != LobbyShell.Menu?.Pointer && SettingsOverLobby(); i++)
        {
            foreach (Wait wait in Tap(PadButtons.B))
            {
                yield return wait;
            }

            yield return Soft(() => Top()?.Pointer == LobbyShell.Menu?.Pointer, 3, "Settings gone");
        }

        yield return Seconds(WalkSettleSeconds);
        Check("B on Settings", () => Top()?.Pointer == LobbyShell.Menu?.Pointer && FocusIs(Row(LobbyFocusKind.StartRow)) && CursorInShell() && IsPadControlling(), "the lobby, the cursor on Start, the pad still controlling");
        yield return Soft(() => Sd().Actions.Snapshot().StartAllowed, 8, "Start allowed");
        Check("the flow after Settings", () => Flow().Host == LobbyHostState.Open && Flow().Epoch == epoch && Sd().Actions.Snapshot().StartAllowed, "no phase change, Start allowed");
        foreach (Wait wait in Snap("settings_closed", 0))
        {
            yield return wait;
        }
    }

    private static IEnumerable<Wait> StepWalkNotice()
    {
        OnlineSession session = Sd();
        LobbyFocus at = Lobby().Focus;
        foreach ((string name, PadButtons button, string letter) in new[] { ("a", PadButtons.A, "A"), ("b", PadButtons.B, "B") })
        {
            if (!_walkOk)
            {
                break; // the presses have stopped: no second notice is raised that nothing would close
            }

            session.Notify(NoticeId.BackupFailed);
            yield return Soft(() => Top() is { } top && top.TryCast<CustomizableDismissablePrompt>() != null, 4, "a notice's prompt");
            yield return Seconds(0.9);
            Expect("the notice", () => Top()?.TryCast<CustomizableDismissablePrompt>() != null && !CursorInShellOnly(), "the game's one-button prompt over the lobby, its button focused");
            foreach (Wait wait in Snap($"notice_{name}", 0))
            {
                yield return wait;
            }

            int modes = session.TourModeChanges;
            if (MayPressOrStop("RB, Y and Start under the notice", () => Top()?.TryCast<CustomizableDismissablePrompt>() != null))
            {
                foreach (PadButtons under in new[] { PadButtons.RightBumper, PadButtons.Y, PadButtons.Start })
                {
                    foreach (Wait wait in Tap(under))
                    {
                        yield return wait;
                    }
                }
            }

            yield return Seconds(WalkSettleSeconds);
            Expect("RB, Y and Start under the notice", () => session.TourModeChanges == modes && !Lobby().PanelOpen && Flow().Host == LobbyHostState.Open && Top()?.TryCast<CustomizableDismissablePrompt>() != null, "nothing: the notice has the input");
            foreach (Wait wait in Press($"{letter} on the notice", button, () => Top()?.Pointer == LobbyShell.Menu?.Pointer && !LobbyPrompts.IsOpen && FocusIs(at) && CursorInShell(), "the lobby, the cursor where it was", within: 4))
            {
                yield return wait;
            }

            Check($"after {letter} on the notice", () => Flow().Host == LobbyHostState.Open && LobbyShell.IsShown && LobbyShell.Kind == ShellKind.Lobby, "still the lobby");
        }

        foreach (Wait wait in ToastHere("in the lobby"))
        {
            yield return wait;
        }
    }

    private static IEnumerable<Wait> StepWalkClose()
    {
        OnlineSession session = Sd();

        // Host alone, the table's row: closes, with no question. The question needs a friend in: the one-PC tour's.
        foreach (Wait wait in Press("B on the lobby, host alone", PadButtons.B, () => session.Actions.Snapshot().Role == LobbyRole.None && !LobbyShell.IsOpen && InGroup() && Cursor() == "HostLobby", "no session, the Online group, the cursor on Host Lobby", within: ShellSeconds, snap: "closed_on_group"))
        {
            yield return wait;
        }

        Expect("the prompt", () => !LobbyPrompts.IsOpen, "no question: host alone");
        Check("after the close", () => session.TourStuckLine().Length == 0 && !Title().m_IsExiting, $"nothing held ({session.TourStuckLine()})");
        foreach (Wait wait in Press("B on the group", PadButtons.B, () => OnMain() && Cursor() == "Online", "Main, the cursor on Online", snap: "main_on_online"))
        {
            yield return wait;
        }

        foreach (Wait wait in ToastHere("at the title"))
        {
            yield return wait;
        }
    }

    private static IEnumerable<Wait> StepWalkMash()
    {
        OnlineSession session = Sd();

        // Four presses in half a second on the Online row, as a person would who wants the lobby. Nothing opens twice. The first
        // only with the cursor on that row (Main's cursor can rest on Campaign, the player's own), the rest only on what it opens.
        session.TourNotices = new List<NoticeId>();
        var seen = new List<string>();
        foreach (Wait wait in OnlineRowOnMain())
        {
            yield return wait;
        }

        foreach (Wait wait in Mash("A on Online", PadButtons.A, 4, seen, () => OnMain() && Cursor() == "Online", OnlineMashScreen))
        {
            yield return wait;
        }

        yield return Seconds(1);
        StepNote($"A mashed on Online went: {string.Join(" > ", seen)}; now {Where()}");
        Check("the mash on Online", () => !session.TourNotices!.Contains(NoticeId.AlreadyInSession) && seen.Count(s => s.StartsWith("shell Lobby", StringComparison.Ordinal)) <= 1 && LobbyScreens.Instance.Lobby is not { PanelOpen: true }, "one lobby at most, no list opened by the presses in flight, no \"already in a session\"");
        foreach (Wait wait in Snap("mash_online", 0))
        {
            yield return wait;
        }

        foreach (Wait wait in BackToMain())
        {
            yield return wait;
        }

        // On Join Lobby: its list opens once.
        foreach (Wait wait in OpenGroup("A on Online", null))
        {
            yield return wait;
        }

        if (Cursor() != "JoinLobby")
        {
            foreach (Wait wait in Press("down", PadButtons.DPadDown, () => Cursor() == "JoinLobby", "the cursor on Join Lobby"))
            {
                yield return wait;
            }
        }

        seen.Clear();
        foreach (Wait wait in Mash("A on Join Lobby", PadButtons.A, 3, seen, () => InGroup() && Top() == null && Cursor() == "JoinLobby", OnlineMashScreen))
        {
            yield return wait;
        }

        yield return Seconds(1);
        StepNote($"A mashed on Join Lobby went: {string.Join(" > ", seen)}; now {Where()}");
        Check("the mash on Join Lobby", () => !session.TourNotices!.Contains(NoticeId.AlreadyInSession) && seen.Count(s => s.StartsWith("shell Browser", StringComparison.Ordinal)) == 1 && LobbyShell.Kind == ShellKind.Browser && LobbyShell.IsShown && Flow().Role == LobbyRole.None, "one list, up, nothing hosted or joined by the presses that opened it");
        foreach (Wait wait in Snap("mash_join", 0))
        {
            yield return wait;
        }

        session.TourNotices = null;
        foreach (Wait wait in BackToMain())
        {
            yield return wait;
        }
    }

    private static IEnumerable<Wait> StepWalkQuick()
    {
        OnlineSession session = Sd();
        ILobbyActions actions = session.Actions;
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

        yield return Soft(() => actions.Snapshot().StartAllowed, 8, "Start allowed");
        foreach (Wait wait in StartToController("Start", PadButtons.Start))
        {
            yield return wait;
        }

        foreach (Wait wait in ForwardFromController())
        {
            yield return wait;
        }

        foreach (Wait wait in MatchAndMashedReturn(InMatchSettings))
        {
            yield return wait;
        }

        foreach (Wait wait in Press("B on the lobby, host alone", PadButtons.B, () => actions.Snapshot().Role == LobbyRole.None && !LobbyShell.IsOpen, "no session", within: ShellSeconds))
        {
            yield return wait;
        }

        yield return Soft(() => InGroup() && Cursor() == "HostLobby", 5, "the Online group, on Host Lobby");
        Check("after a game, closed", () => session.TourStuckLine().Length == 0, $"nothing held ({session.TourStuckLine()})");
        Expect("after a game, the title", () => InGroup() && Cursor() == "HostLobby", "the Online group, the cursor on Host Lobby");
        foreach (Wait wait in Snap("usable_title", SettleFrames))
        {
            yield return wait;
        }

        foreach (Wait wait in Press("B on the group", PadButtons.B, () => OnMain() && Cursor() == "Online", "Main, the cursor on Online"))
        {
            yield return wait;
        }
    }

    // ---- Stretches two tours share ----

    /// <summary>The mode to Quick Game by a bumper, from whichever it's on: the lobby remembers the last one chosen in this game.</summary>
    private static IEnumerable<Wait> EnsureQuick()
    {
        ILobbyActions actions = Sd().Actions;
        if (actions.Snapshot().Mode != LobbyMode.QuickGame)
        {
            foreach (Wait wait in Press("RB", PadButtons.RightBumper, () => actions.Snapshot().Mode == LobbyMode.QuickGame, "Quick Game"))
            {
                yield return wait;
            }
        }
    }

    private static IEnumerable<Wait> OpenGroup(string what, string? snap)
    {
        // A on Main only with the cursor on its Online row: its other rows are the player's own (Campaign, Play Now, Exit).
        if (_walkOk && !(OnMain() && Cursor() == "Online"))
        {
            Fail($"{what}: not pressed: expected Main, the cursor on Online; {Where()}");
            _walkOk = false;
            yield break;
        }

        foreach (Wait wait in Press(what, PadButtons.A, () => InGroup() && Cursor() == GroupFocus(), "the Online group, the cursor by the rule", snap: snap))
        {
            yield return wait;
        }
    }

    private static IEnumerable<Wait> OpenLobby(string what)
    {
        foreach (Wait wait in Press(what, PadButtons.A, () => Flow().Host == LobbyHostState.Open && LobbyShell.IsIdle && LobbyShell.Kind == ShellKind.Lobby && FocusIs(Seat(1)) && CursorInShell(), "a real session, the lobby, the cursor on the first empty seat", within: 20 + ShellSeconds))
        {
            yield return wait;
        }

        foreach (Wait wait in Snap("lobby", SettleFrames))
        {
            yield return wait;
        }
    }

    /// <summary>A press that starts: the countdown, its 3 s, and the game's controller screen over the shell.</summary>
    private static IEnumerable<Wait> StartToController(string what, PadButtons button)
    {
        foreach (Wait wait in Press(what, button, () => Flow().Host is LobbyHostState.Counting or LobbyHostState.Starting or LobbyHostState.GameFlow, "the countdown"))
        {
            yield return wait;
        }

        yield return Soft(() => Flow().Host == LobbyHostState.GameFlow, 8, "the game's flow");
        yield return Soft(() => Top()?.TryCast<ControllerAssignmentMenu>() != null, 8, "the game's controller screen");
        Check("after the countdown", () => Flow().Host == LobbyHostState.GameFlow && Top()?.TryCast<ControllerAssignmentMenu>() != null && LobbyShell.IsShown && LobbyShell.HandingOver && !Title().m_IsExiting, "the game's controller screen, the shell under it, the title not leaving");
        yield return Seconds(1.2);
        StepNote($"the controller screen: {ControllerScreenLine()}");
        foreach (Wait wait in Snap("controller_screen", 0))
        {
            yield return wait;
        }
    }

    private static IEnumerable<Wait> BackOutOfController()
    {
        // B only on the game's controller screen (or its transition) over the lobby's shell: the screen this is for.
        for (int i = 1; i <= 8 && (Flow().Host != LobbyHostState.Open || Top()?.Pointer != LobbyShell.Menu?.Pointer) && ControllerOverLobby(); i++)
        {
            foreach (Wait wait in Tap(PadButtons.B))
            {
                yield return wait;
            }

            yield return Soft(() => Flow().Host == LobbyHostState.Open && Top()?.Pointer == LobbyShell.Menu?.Pointer, 1.6, "the lobby");
            StepNote($"B {i} on the controller screen: {Where()}; {ControllerScreenLine()}");
            if (i == 1)
            {
                foreach (Wait wait in Snap("controller_b1", 0))
                {
                    yield return wait;
                }
            }
        }

        yield return Soft(() => LobbyShell.IsShown && !LobbyShell.HandingOver && CursorInShell(), 4, "the lobby's cursor back");
        yield return Seconds(WalkSettleSeconds);
    }

    /// <summary>The controller screen left forward as a pad would. If the game's screen doesn't go by the pad, its two steps, and it's said.</summary>
    private static IEnumerable<Wait> ForwardFromController()
    {
        // The pad's player onto a side, ready, then Start: a person's way through Play Now's screen.
        foreach ((PadButtons button, string what) in new[] { (PadButtons.DPadLeft, "left"), (PadButtons.A, "A"), (PadButtons.Start, "Start"), (PadButtons.A, "A"), (PadButtons.Start, "Start") })
        {
            if (Top()?.TryCast<ControllerAssignmentMenu>() == null || Title().m_IsExiting)
            {
                break;
            }

            foreach (Wait wait in Tap(button))
            {
                yield return wait;
            }

            yield return Seconds(0.6);
            StepNote($"{what} on the controller screen: {ControllerScreenLine()}; {Where()}");
        }

        foreach (Wait wait in Snap("controller_start", 0))
        {
            yield return wait;
        }

        if (Top()?.TryCast<ControllerAssignmentMenu>() is { } still && !Title().m_IsExiting)
        {
            StepNote("still on the controller screen: leaving it by its own two steps, SetResult(false) and FinishExitMenu (its prompts are for a player)");
            still.SetResult(false);
            still.FinishExitMenu();
        }
    }

    /// <summary>
    /// The match, a toast in it, <paramref name="inMatch"/> when given (the pad walk's pause-menu checks), the game's own quit, and A and B mashed from the quit
    /// through the return until the lobby's half second is nearly over. Nothing may act.
    /// </summary>
    private static IEnumerable<Wait> MatchAndMashedReturn(Func<IEnumerable<Wait>>? inMatch = null)
    {
        OnlineSession session = Sd();
        yield return Soft(() => Flow().Host == LobbyHostState.InGame, 30, "the scene left the title");
        yield return Soft(() => HudModel.GameManagerInstance() != null && !MatchSetup.Loading, 45, "the match loaded");
        yield return Seconds(MatchSeconds);
        StepNote($"in the match: flow {Flow().Host}, shell open {LobbyShell.IsOpen}, scene {Campaign.Scenes.WorldMap.SceneName}");
        Check("in the match", () => Flow().Host == LobbyHostState.InGame && !LobbyShell.IsOpen, "the flow InGame, the shell gone");
        if (PlaceNow(out string notMatch) != TourPlace.Match)
        {
            Fail($"no match to quit or mash through: the game at {TourStarts.Describe(PlaceNow(out _))}{(notMatch.Length > 0 ? $" ({notMatch})" : string.Empty)}; nothing pressed");
            _walkOk = false;
            yield break;
        }
        foreach (Wait wait in ToastHere("in a match"))
        {
            yield return wait;
        }

        foreach (Wait wait in Snap("match", 0))
        {
            yield return wait;
        }

        if (inMatch != null)
        {
            foreach (Wait wait in inMatch())
            {
                yield return wait;
            }
        }

        Il2Cpp.GameLoader? loader = UnityEngine.Object.FindObjectOfType<Il2Cpp.GameLoader>();
        Il2CppState.GameModeControllers.IGameModeController? controller = loader?._gameModeController;
        if (controller == null)
        {
            // Never a scene load in its place: a load from a match the tour can't see is not the tour's to make.
            Fail("the game's mode controller wasn't found: no quit, no mash");
            _walkOk = false;
            yield break;
        }

        // As a run's end does before its load (the session must clear it), and the match counted as ended for the ribbon.
        Il2CppTape2Tape.CampaignUtil.startNewRunImmediately = true;
        session.TourMarkMatchEnded();
        controller.Quit();

        uint epoch = Flow().Epoch;
        int mashes = 0;
        int inShell = 0;
        int held = 0;
        double lobbyAt = -1;
        double started = FrameClock.Seconds;
        bool a = true;
        while (FrameClock.Seconds - started < 45)
        {
            if (lobbyAt < 0 && LobbyShell.IsShown && LobbyShell.Kind == ShellKind.Lobby)
            {
                lobbyAt = FrameClock.Seconds;
                StepNote(string.Create(CultureInfo.InvariantCulture, $"the shell is up {lobbyAt - started:0.0} s after the quit; locked at arrival {Flow().InputLockWasSet}, flow {Flow().Host}"));
            }

            // Well inside the view's own half second from when the shell showed, so that the last press lands locked.
            if (lobbyAt >= 0 && FrameClock.Seconds - lobbyAt > 0.3)
            {
                break;
            }

            // Only while the game is in the match, its load, the title's press-to-start or the lobby it returns to: never on a screen
            // of the player's own. On the title's Main the presses wait for the lobby to cover it.
            if (!MashScreen(out string mashAt, out bool hold))
            {
                Fail($"A and B mashed through the return: stopped after {mashes}: the game at {mashAt}, not the match, its load or the lobby");
                _walkOk = false;
                break;
            }

            if (hold)
            {
                held++;
                yield return Frames(1);
                continue;
            }

            PadButtons mash = a ? PadButtons.A : PadButtons.B;
            a = !a;
            _walk!.Hold(mash);
            yield return Seconds(0.06);
            _walk.Let(mash);
            yield return Seconds(0.04);
            mashes++;
            inShell += lobbyAt >= 0 ? 1 : 0;
            if (!Expect("A and B mashed through the return", () => Flow().Host is LobbyHostState.InGame or LobbyHostState.GameFlow or LobbyHostState.Returning or LobbyHostState.Open && !LobbyPrompts.IsOpen && Flow().Epoch == epoch, "no countdown, no question"))
            {
                break; // a press acted: the next would act on what it opened
            }
        }

        StepNote($"mashed A and B {mashes} times from the quit, {inShell} of them on the lobby");
        if (held > 0)
        {
            StepNote($"the mash waited {held} frame(s) with the title's Main or another of its groups up, for the lobby to cover it");
        }

        if (!_walkOk)
        {
            yield break;
        }

        yield return Soft(() => Flow().Host == LobbyHostState.Open && LobbyShell.IsIdle, 12, "back at the lobby");
        yield return Seconds(LobbyTiming.InputLockMicros / 1_000_000.0);
        TitleScreen title = Title();
        LobbyState state = session.Actions.Snapshot();
        StepNote($"back at the lobby: {Where()}, the lock was set {Flow().InputLockWasSet}, back from a game {state.BackFromGame}, ribbon {state.Result}, epoch {epoch} to {Flow().Epoch}");
        Check("the input lock", () => Flow().InputLockWasSet, "set by the return");
        Expect("the mash", () => Flow().Host == LobbyHostState.Open && Flow().Epoch == epoch && LobbyShell.IsShown && !LobbyPrompts.IsOpen && session.Actions.Snapshot().Role == LobbyRole.Host, "the lobby up, no countdown, no question, the session on");
        Expect("the cursor back from a game", () => FocusIs(Row(LobbyFocusKind.StartRow)) && CursorInShell() && !title.m_IsStartingRun && Top()?.Pointer == LobbyShell.Menu?.Pointer, "on Start, no squad screen");
        foreach (Wait wait in Snap("return_at_rest", 0))
        {
            yield return wait;
        }

        foreach (Wait wait in Press("up, after the lock", PadButtons.DPadUp, () => FocusIs(Row(LobbyFocusKind.InviteRow)), "the lobby answers"))
        {
            yield return wait;
        }

        foreach (Wait wait in Press("down", PadButtons.DPadDown, () => FocusIs(Row(LobbyFocusKind.StartRow)), "the cursor on Start"))
        {
            yield return wait;
        }
    }

    private static IEnumerable<Wait> BackToMain()
    {
        // B only on what leads back to Main from the Online group's rows: the group, the Join list or the lobby it opened.
        for (int i = 0; i < 12 && !OnMain() && PlaceNow(out _) is TourPlace.Group or TourPlace.TourScreen or TourPlace.Lobby or TourPlace.Loading; i++)
        {
            if (PlaceNow(out _) == TourPlace.Loading)
            {
                yield return Seconds(0.5);
                continue;
            }

            foreach (Wait wait in Tap(PadButtons.B))
            {
                yield return wait;
            }

            yield return Soft(() => OnMain(), 1.5, "Main");
            StepNote($"B, back towards Main: {Where()}");
        }

        Check("back on Main", () => OnMain() && !LobbyShell.IsOpen && Sd().Actions.Snapshot().Role == LobbyRole.None, "Main, no session");
        foreach (Wait wait in OnlineRowOnMain())
        {
            yield return wait;
        }
    }

    /// <summary>The cursor onto Online from anywhere on Main, by up and down.</summary>
    private static IEnumerable<Wait> OnlineRowOnMain()
    {
        string[] rows = { "Campaign", "PlayNow", "Online", "Customize", "Compendium", "Glossary", "Settings", "Exit" };
        for (int i = 0; i < rows.Length && OnMain() && Cursor() != "Online"; i++)
        {
            string at = Cursor();
            bool up = Array.IndexOf(rows, at) > Array.IndexOf(rows, "Online");
            foreach (Wait wait in Tap(up ? PadButtons.DPadUp : PadButtons.DPadDown))
            {
                yield return wait;
            }

            yield return Soft(() => Cursor() != at, 1, "the cursor to move");
            yield return Seconds(WalkSettleSeconds);
        }

        Check("the cursor on Main", () => OnMain() && Cursor() == "Online", "on Online");
    }

    private static IEnumerable<Wait> MoveTo(LobbyFocus focus)
    {
        if (!MayPressOrStop($"to {Named(focus)}", LobbyOnTop))
        {
            yield break;
        }

        LobbyView lobby = Lobby();
        LobbyState state = Sd().Actions.Snapshot();
        for (int i = 0; i < 12 && lobby.Focus != focus && LobbyOnTop(); i++)
        {
            PadButtons button = StepTowards(lobby.Focus, focus, state);
            LobbyFocus from = lobby.Focus;
            foreach (Wait wait in Tap(button))
            {
                yield return wait;
            }

            yield return Soft(() => lobby.Focus != from, 1, "the cursor to move");
            yield return Seconds(WalkSettleSeconds);
        }

        if (!Check($"moved to {Named(focus)}", () => lobby.Focus == focus && LobbyOnTop(), Named(focus)))
        {
            _walkOk = false;
        }
    }

    private static PadButtons StepTowards(in LobbyFocus at, in LobbyFocus to, LobbyState state)
    {
        bool toColumn = to.Kind != LobbyFocusKind.Seat;
        if (at.Kind == LobbyFocusKind.Seat)
        {
            if (toColumn)
            {
                return PadButtons.DPadLeft;
            }

            return at.Index < to.Index ? PadButtons.DPadRight : PadButtons.DPadLeft;
        }

        if (!toColumn)
        {
            // A selector's right is a change of value: the way into the seats is from Invite or Start.
            return at.Kind is LobbyFocusKind.ModeRow or LobbyFocusKind.SaveRow ? PadButtons.DPadDown : PadButtons.DPadRight;
        }

        return ColumnOrder(at.Kind) < ColumnOrder(to.Kind) ? PadButtons.DPadDown : PadButtons.DPadUp;
    }

    private static int ColumnOrder(LobbyFocusKind kind) => kind switch
    {
        LobbyFocusKind.ModeRow => 0,
        LobbyFocusKind.SaveRow => 1,
        LobbyFocusKind.InviteRow => 2,
        _ => 3,
    };

    /// <summary>
    /// A press and its assertion, the state said either way; a screenshot at the stop. Not made once an expectation of the
    /// step has failed; its own failure ends the step's presses (a press on the wrong screen acts on whatever is there).
    /// </summary>
    private static IEnumerable<Wait> Press(string what, PadButtons button, Func<bool> done, string wanted, double within = 3, string? snap = null)
    {
        if (!MayPress(what))
        {
            yield break;
        }

        foreach (Wait wait in Tap(button))
        {
            yield return wait;
        }

        yield return Soft(done, within, wanted);
        if (!Check(what, done, wanted))
        {
            _walkOk = false;
        }

        if (snap != null)
        {
            foreach (Wait wait in Snap(snap, 0))
            {
                yield return wait;
            }
        }

        yield return Seconds(WalkSettleSeconds);
    }

    /// <summary>A trigger is an axis: all the way in, then out. On the keyboard walk, the key of its action. Gated as <see cref="Press"/>.</summary>
    private static IEnumerable<Wait> Trigger(string what, bool right, Func<bool> done, string wanted, double within = 3)
    {
        if (!MayPress(what))
        {
            yield break;
        }

        if (_keys != null)
        {
            foreach (Wait wait in KeyTap(KeyWalk.ForTrigger(right)))
            {
                yield return wait;
            }
        }
        else
        {
            _walk!.Trigger(right, 1);
            yield return TapTime();
            _walk!.Trigger(right, 0);
        }

        yield return Soft(done, within, wanted);
        if (!Check(what, done, wanted))
        {
            _walkOk = false;
        }

        yield return Seconds(WalkSettleSeconds);
    }

    /// <summary>
    /// Down for what a person's tap is, 4 to 8 frames at 60 fps; on a slow frame rate, at most a quarter of a second, under the game's repeat. Then up. On the
    /// keyboard walk, the key the player's own map binds to the action the button makes (LobbyUiCheck.KeyWalk.cs).
    /// </summary>
    private static IEnumerable<Wait> Tap(PadButtons button)
    {
        if (_keys != null)
        {
            foreach (Wait wait in KeyTap(KeyWalk.For(button)))
            {
                yield return wait;
            }

            yield break;
        }

        _walk!.Hold(button);
        yield return TapTime();
        _walk!.Let(button);
        yield return Frames(2);
    }

    private static Wait TapTime()
    {
        int frame = FrameClock.Frame;
        double start = FrameClock.Seconds;
        return Until(() => (FrameClock.Frame - frame >= 5 && FrameClock.Seconds - start >= 0.066) || FrameClock.Seconds - start >= 0.25, 1, "a tap");
    }

    /// <summary>
    /// Taps as quick as a mash, with what the title showed after each: the first only where <paramref name="first"/> holds (the
    /// row it is for under the cursor), each later one only where <paramref name="then"/> holds (what that row opens). A press
    /// refused for its screen fails the step and ends its presses.
    /// </summary>
    private static IEnumerable<Wait> Mash(string what, PadButtons button, int presses, List<string> seen, Func<bool> first, Func<bool> then)
    {
        string last = string.Empty;
        for (int i = 0; i < presses; i++)
        {
            bool going = _walkOk;
            if (!MayPress(what, i == 0 ? first : then))
            {
                if (going)
                {
                    Fail($"{what}: press {i + 1} of {presses} not made: the screen it is for isn't up; {Where()}");
                    _walkOk = false;
                }

                break;
            }

            _walk!.Hold(button);
            yield return Soft(() => { Note(seen, ref last); return false; }, 0.05, "a mash's press");
            _walk!.Let(button);
            yield return Soft(() => { Note(seen, ref last); return false; }, 0.05, "a mash's gap");
        }

        for (int i = 0; i < 40; i++)
        {
            Note(seen, ref last);
            yield return Frames(1);
        }

        static void Note(List<string> seen, ref string last)
        {
            string now = LobbyShell.IsOpen ? $"shell {LobbyShell.Kind}" : $"group {(int)Title().m_CurrentButtonGroup}";
            if (now != last)
            {
                seen.Add(now);
                last = now;
            }
        }
    }

    /// <summary>Holds for <paramref name="seconds"/>, with each stop of the cursor and when; the list is filled anew.</summary>
    private static IEnumerable<Wait> HoldWatching(string what, Action<WalkPad> down, Action<WalkPad> up, double seconds, List<(double At, LobbyFocus Focus)> seen)
    {
        seen.Clear();
        if (!MayPressOrStop($"{what} held", LobbyOnTop))
        {
            yield break;
        }

        LobbyView lobby = Lobby();
        LobbyFocus last = lobby.Focus;
        double started = FrameClock.Seconds;
        down(_walk!);
        yield return Soft(() =>
        {
            if (lobby.Focus != last)
            {
                last = lobby.Focus;
                seen.Add((FrameClock.Seconds - started, last));
            }

            return FrameClock.Seconds - started >= seconds;
        }, seconds + 1, what);
        up(_walk!);
        yield return Seconds(WalkSettleSeconds);
        string went = string.Join(", ", seen.Select(s => string.Create(CultureInfo.InvariantCulture, $"{Named(s.Focus)} at {s.At:0.00}")));
        StepNote(string.Create(CultureInfo.InvariantCulture, $"{what} held for {seconds:0.#} s: {went}"));
    }

    private static void CheckHold(string what, List<(double At, LobbyFocus Focus)> seen, params LobbyFocus[] wanted)
    {
        Check(what, () => seen.Select(s => s.Focus).SequenceEqual(wanted), string.Join(", ", wanted.Select(f => Named(f))) + " and no further");
        if (seen.Count >= 2 && seen[1].At - seen[0].At < RepeatGapAtLeast)
        {
            Fail(string.Create(CultureInfo.InvariantCulture, $"{what}: expected the second move after the game's repeat delay, at least {RepeatGapAtLeast} s after the first; it came {seen[1].At - seen[0].At:0.00} s after"));
        }

        Expect($"{what}: nothing opened", () => !Lobby().PanelOpen && !LobbyPrompts.IsOpen && Flow().Host == LobbyHostState.Open, "no list, no question, the lobby");
    }

    // ---- The pad ----

    private static IEnumerable<Wait> MakePad()
    {
        if (_walk != null)
        {
            yield break;
        }

        ReInput.ConfigHelper? config = ReInput.configuration;
        if (config != null)
        {
            // Monitors may be off, or the window behind others: Rewired would answer nothing. A session's FocusGuard does the same.
            _ignoredUnfocused = config.ignoreInputWhenAppNotInFocus;
            _focusSettingSaved = true;
            config.ignoreInputWhenAppNotInFocus = false;
        }

        _slotAtStart = CoopSlots.Selected;
        _walk = WalkPad.Make(out string problem);
        if (_walk == null)
        {
            Problem($"the virtual pad couldn't be made: {problem}");
            yield break;
        }

        Campaign.HostDecides.TourPadPlayer = _walk.RewiredId;
        StepNote($"the virtual pad: Rewired player {_walk.RewiredId}, waiting to join; a press of it doesn't trip the tour's abort, which reads the keyboard, the mouse and joysticks");
        yield return Frames(4);
    }

    /// <summary>The pad gone with its game player, as the game does for an unplugged one; the player's slot and Rewired's focus setting as they were. From any state.</summary>
    private static void EndWalk()
    {
        WalkPad? pad = _walk;
        _walk = null;
        Campaign.HostDecides.TourPadPlayer = -1;
        Campaign.HostDecides.TourPadSetAside = false; // the keyboard host step's, should a restore come before its own finally
        if (pad != null)
        {
            try
            {
                pad.Dispose();
            }
            catch (Exception ex)
            {
                Line($"NOT RESTORED: the virtual pad didn't go cleanly ({ModLog.SafeErrorText(ex)}).");
            }
        }

        if (_focusSettingSaved)
        {
            _focusSettingSaved = false;
            ReInput.ConfigHelper? config = ReInput.configuration;
            if (config != null)
            {
                config.ignoreInputWhenAppNotInFocus = _ignoredUnfocused;
            }
        }

        if (_slotAtStart > 0 && CoopSlots.Selected != _slotAtStart)
        {
            if (!CoopSlots.TrySelect(_slotAtStart, out string problem))
            {
                Line($"NOT RESTORED: the co-op slot is {CoopSlots.Selected}, not {_slotAtStart} ({problem}).");
            }
        }

        _slotAtStart = -1;
        if (pad != null)
        {
            TitleScreen? title = CampaignGame.Title();
            Line($"The pad has gone: {(title == null ? "no title" : $"the title idle {CampaignGame.IsIdle(title)}, not leaving {!title.m_IsExiting}")}, cursor {Cursor()}, the UI's controlling player: {Controlling()}.");
        }
    }

    // ---- What's asked ----

    /// <summary>An expectation, said either way; true when it holds. It doesn't stop the step's presses by itself (<see cref="Press"/> does).</summary>
    private static bool Check(string what, Func<bool> done, string wanted)
    {
        bool ok;
        try
        {
            ok = done();
        }
        catch (Exception ex)
        {
            ok = false;
            wanted += $" (the check threw {ex.GetType().Name})";
        }

        if (ok)
        {
            StepNote($"{what}: {Where()}");
        }
        else
        {
            Fail($"{what}: expected {wanted}; {Where()}");
        }

        return ok;
    }

    /// <summary>
    /// <see cref="Check"/> for an expectation the step's next presses depend on (the screen or state they are for): when it
    /// fails, the step's presses stop, as <see cref="Press"/>'s own do.
    /// </summary>
    private static bool Expect(string what, Func<bool> done, string wanted)
    {
        if (Check(what, done, wanted))
        {
            return true;
        }

        _walkOk = false;
        return false;
    }

    private static void Fail(string line)
    {
        Line($"  FAIL {line}");
        _stepProblem ??= line;
    }

    private static string Where()
    {
        TitleScreen? title = CampaignGame.Title();
        AbstractMenu? top = Top();
        LobbyView? lobby = LobbyScreens.Instance.Lobby;
        string text = $"cursor {Cursor()}, group {(title == null ? -1 : (int)title.m_CurrentButtonGroup)}, top {(top == null ? "none" : MenuGame.MenuId(top))}, flow {Flow().Host}";
        return lobby == null ? text : $"{text}, focus {lobby.Focus.Kind} {lobby.Focus.Index}{(lobby.PanelOpen ? ", list open" : string.Empty)}, mode {Sd().Actions.Snapshot().Mode}";
    }

    private static string Cursor()
    {
        return ObjectName(MenuGame.Module()?.NavigationController?.CurrentNavigable); // a row named after a Steam ID is "a friend's row"
    }

    private static long CursorPointer() => (MenuGame.Module()?.NavigationController?.CurrentNavigable?.Pointer ?? IntPtr.Zero).ToInt64();

    // Nothing over the title either: Settings opened from Main leaves the title's group at Main (seen in the pad walk's own lines: top Settings, group 1).
    private static bool OnMain() => CampaignGame.Title() is { } title && (int)title.m_CurrentButtonGroup == 1 && !title._isTransitioningGroup && !LobbyShell.IsOpen && Top() == null;

    private static bool InGroup() => CampaignGame.Title() is { } title && (int)title.m_CurrentButtonGroup == OnlineGroup.GroupValue && !title._isTransitioningGroup && !LobbyShell.IsOpen;

    private static string GroupFocus() => LobbyScreens.Instance.Hosts.HostingLine() != null ? "JoinLobby" : "HostLobby";

    private static LobbyView Lobby() => LobbyScreens.Instance.Lobby ?? throw new InvalidOperationException("the lobby isn't up");

    private static bool FocusIs(in LobbyFocus focus) => LobbyScreens.Instance.Lobby is { } lobby && lobby.Focus == focus;

    private static bool CursorInShellOnly() => Top()?.Pointer == LobbyShell.Menu?.Pointer;

    /// <summary>The game's squad screen, or its transition, over the lobby's shell (the lobby's own New Run).</summary>
    private static bool SquadOverLobby() =>
        LobbyShell.IsOpen && (Top() is not { } top || top.TryCast<ChooseMetaMenu>() != null || TourStarts.IsTransition(MenuGame.MenuId(top)) || top.Pointer == LobbyShell.Menu?.Pointer);

    /// <summary>The game's Settings, or a prompt, over the lobby's shell.</summary>
    private static bool SettingsOverLobby() =>
        LobbyShell.IsOpen && LobbyShell.Kind == ShellKind.Lobby && (Settings() != null || Top()?.TryCast<PromptTemplateMenu>() != null || Top()?.TryCast<CustomizableDismissablePrompt>() != null);

    /// <summary>
    /// Where a mash through a match's return may press: the match, its load, the lobby (or a game screen over it), and the title's
    /// press-to-start the return passes (a press there reaches Main at most: the title's back handler does nothing there). On the title's
    /// Main or another of its groups it holds its presses (<paramref name="hold"/>) until the lobby covers the title: a press there
    /// is on the player's own rows (Main's cursor rests on Campaign). Anywhere else it stops (false).
    /// </summary>
    private static bool MashScreen(out string at, out bool hold)
    {
        TourPlace place = PlaceNow(out string detail);
        at = $"{TourStarts.Describe(place)}{(detail.Length > 0 ? $" ({detail})" : string.Empty)}";
        hold = place is TourPlace.Main or TourPlace.Group;
        return hold || place is TourPlace.Match or TourPlace.Loading or TourPlace.Lobby or TourPlace.LobbyCovered or TourPlace.PressStart;
    }

    /// <summary>
    /// pw_mash's later presses: only on what A on Online or Join Lobby opens or passes through: the title changing group (it takes
    /// no press then), the Online group, the lobby's or the Join list's shell, or Main still on its Online row. Never another of
    /// the title's groups, a scene load or a game screen.
    /// </summary>
    private static bool OnlineMashScreen()
    {
        if (!CampaignGame.TitleLoaded() || CampaignGame.Title() is not { m_HasStarted: true, m_IsExiting: false } title)
        {
            return false;
        }

        AbstractMenu? top = Top();
        if (LobbyShell.IsOpen)
        {
            return top == null || top.Pointer == LobbyShell.Menu?.Pointer || TourStarts.IsTransition(MenuGame.MenuId(top));
        }

        if (top != null)
        {
            return TourStarts.IsTransition(MenuGame.MenuId(top));
        }

        int group = (int)title.m_CurrentButtonGroup;
        return title._isTransitioningGroup || group == OnlineGroup.GroupValue || (group == 1 && Cursor() == "Online");
    }

    /// <summary>The game's controller screen, or its transition, over the lobby's shell (the lobby's own start).</summary>
    private static bool ControllerOverLobby() =>
        LobbyShell.IsOpen && (Top() is not { } top || top.TryCast<ControllerAssignmentMenu>() != null || TourStarts.IsTransition(MenuGame.MenuId(top)) || top.Pointer == LobbyShell.Menu?.Pointer);

    private static LobbyFocus Seat(int index) => new(LobbyFocusKind.Seat, index);

    private static LobbyFocus Row(LobbyFocusKind kind) => new(kind);

    private static string Named(in LobbyFocus focus) => focus.Kind == LobbyFocusKind.Seat ? $"seat {focus.Index + 1}" : focus.Kind switch
    {
        LobbyFocusKind.ModeRow => "the mode",
        LobbyFocusKind.SaveRow => "the save",
        LobbyFocusKind.InviteRow => "Invite",
        LobbyFocusKind.StartRow => "Start",
        _ => focus.Kind.ToString(),
    };

    // A picture of each place the cursor can be, once: the walk has many more presses than places.
    private static readonly HashSet<string> Snapped = new(StringComparer.Ordinal);

    private static string? Snappable(in LobbyFocus focus, string what)
    {
        string name = $"at_{focus.Kind}_{focus.Index}".ToLowerInvariant();
        return Snapped.Add(name) ? name : null;
    }

    private static string Controlling()
    {
        UIInputSource? source = MenuGame.Module()?.InputSource;
        IUIInputPlayer? player = source?.SingleControllingPlayer;
        return player == null ? "nobody" : $"Rewired player {player.NativeId}, game player {GamePlayerOf(player.NativeId)}{(_walk != null && _walk.RewiredId == player.NativeId ? " (the pad)" : string.Empty)}";
    }

    private static int GamePlayerOf(int rewired) => RewiredPlayerInputAssigner.Instance != null ? RewiredPlayerInputAssigner.GetPlayerId(rewired) : -1;

    private static bool IsPadControlling() => _walk != null && MenuGame.Module()?.InputSource?.SingleControllingPlayer is { } player && player.NativeId == _walk.RewiredId;

    private static string ControllerScreenLine()
    {
        ControllerAssignmentMenu? menu = Top()?.TryCast<ControllerAssignmentMenu>();
        if (menu == null)
        {
            return "not up";
        }

        var players = new List<string>();
        Il2CppSystem.Collections.Generic.List<ControllerAssignmentPlayer>? list = menu.m_Players;
        for (int i = 0; list != null && i < list.Count; i++)
        {
            ControllerAssignmentPlayer p = list[i];
            if (p != null && p.IsPlayerControlled)
            {
                players.Add($"game player {p.GamePlayerId} on {p.CurrentSide}{(p.IsLockedIn ? " ready" : string.Empty)}{(p.IsKeyboardAssigned ? " (keyboard)" : string.Empty)}");
            }
        }

        return $"{players.Count} player(s): {string.Join("; ", players)}; both ready {menu.AreBothTeamsReady}";
    }

    /// <summary>A virtual pad a tour presses: <see cref="VirtualPad"/>, fed at Rewired's own moment for custom controllers, as a friend's and the F8 loopback's are.</summary>
    private sealed class WalkPad : IDisposable
    {
        private readonly VirtualPad _pad;
        private Il2CppSystem.Action? _hook;
        private PadButtons _held;
        private short _x;
        private short _y;
        private ushort _left;
        private ushort _right;
        private bool _faulted;
        private int _gamePlayer = -1;

        private WalkPad(VirtualPad pad) => _pad = pad;

        public int RewiredId => _pad.RewiredPlayerId;

        public int GamePlayer => _gamePlayer;

        public static WalkPad? Make(out string problem)
        {
            VirtualPad? pad = VirtualPad.Create("T2TOnline tour", EInputDeviceType.GamepadXbox, -1, null, out problem);
            if (pad == null)
            {
                return null;
            }

            var walk = new WalkPad(pad);
            walk._hook = DelegateSupport.ConvertDelegate<Il2CppSystem.Action>(new Action(walk.OnInputSourceUpdate));
            ReInput.add_InputSourceUpdateEvent(walk._hook);
            return walk;
        }

        public void Hold(PadButtons button) => _held |= button;

        public void Let(PadButtons button) => _held &= ~button;

        /// <summary>Every button, the stick and both triggers let go.</summary>
        public void LetAll()
        {
            _held = PadButtons.None;
            _x = _y = 0;
            _left = _right = 0;
        }

        public void Stick(float x, float y)
        {
            _x = PadState.StickToRaw(x);
            _y = PadState.StickToRaw(y);
        }

        public void Trigger(bool right, float value)
        {
            if (right)
            {
                _right = PadState.TriggerToRaw(value);
            }
            else
            {
                _left = PadState.TriggerToRaw(value);
            }
        }

        public void Tick()
        {
            if (_faulted)
            {
                return;
            }

            int now = GameJoin.TryRelay(_pad, "Pad walk");
            if (now != _gamePlayer)
            {
                _gamePlayer = now;
                ModLog.Info($"Lobby check: the pad is game player {now}.");
            }
        }

        private void OnInputSourceUpdate()
        {
            if (_faulted)
            {
                return;
            }

            try
            {
                _pad.Apply(new PadState(_held, 0, _x, _y, 0, 0, _left, _right), PadButtons.None);
            }
            catch (Exception ex)
            {
                _faulted = true;
                ModLog.Warn($"Lobby check: the pad's input failed ({ModLog.SafeErrorText(ex)}).");
            }
        }

        public void Dispose()
        {
            if (_hook != null)
            {
                ReInput.remove_InputSourceUpdateEvent(_hook);
                _hook = null;
            }

            _pad.ApplyNeutral();
            int rewired = _pad.RewiredPlayerId;
            int gamePlayer = RewiredPlayerInputAssigner.Instance != null ? RewiredPlayerInputAssigner.GetPlayerId(rewired) : -1;
            Il2CppTape2Tape.Player? player = gamePlayer >= 0 ? Il2Cpp.PlayersManager.Instance?.GetPlayerFromRewired(_pad.Player) : null;
            if (player != null)
            {
                RewiredPlayerInputAssigner.SetControlsToActivationMode(rewired);
                Il2Cpp.PlayersManager.Instance!.RemovePlayer(player);
            }

            _pad.Dispose();
        }
    }
}
#endif
