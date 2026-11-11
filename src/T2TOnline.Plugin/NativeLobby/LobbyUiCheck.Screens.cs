#if HAS_INTEROP
using System;
using System.Collections.Generic;
using System.Linq;
using Il2CppBishopGames.UI;
using Il2CppTape2Tape.Hockey.UI;
using T2TOnline.Core.Lobby;
using T2TOnline.Plugin.NativeLobby.Widgets;
using UnityEngine;

namespace T2TOnline.Plugin.NativeLobby;

/// <summary>
/// The tour's look round the screens: the real screens on <see cref="FakeLobbyActions"/>, scene by scene, each with a picture and a dump. The fake is on for the whole tour and the session's put back
/// after it. Nothing here hosts, joins or touches a save: its presses change only the fake's own state.
/// </summary>
internal static partial class LobbyUiCheck
{
    private static FakeLobbyActions? _fake;
    private static ILobbyActions? _realActions;

    private static FakeLobbyActions Fake => _fake ?? throw new InvalidOperationException("the fake session isn't on");

    /// <summary>The tour's start: the screens on the fake.</summary>
    private static void AttachFake()
    {
        _realActions = LobbyScreens.Instance.Actions;
        _fake = new FakeLobbyActions { Screens = LobbyScreens.Instance };
        _fake.OwnId = Math.Max(T2TOnline.Steam.SteamRuntime.LocalSteamId, 1);
        _fake.SetState(_fake.HostAlone(_fake.OwnId));
        LobbyScreens.OverlayOverride = () => true;
        LobbyScreens.IdentityOverride = () => _fake.MyIdentity;
        LobbyScreens.Instance.Attach(_fake);
    }

    /// <summary>The scenes run at 16:9, a shape every player's game looks right in; the window comes back at the tour's end.</summary>
    private static IEnumerable<Wait> MainShape()
    {
        (int width, int height) = UiCheckPlan.SizesFor(Display.main.systemWidth, Display.main.systemHeight)[0];
        if (Screen.width != width || Screen.height != height)
        {
            _windowChanged = true;
            Screen.SetResolution(width, height, _windowMode);
            yield return Until(() => Screen.width == width && Screen.height == height, RestoreWindowSeconds, $"the window at {width}x{height}");
            yield return Frames(10);
        }
    }

    /// <summary>The tour's end: the session back behind the screens.</summary>
    private static void DetachFake()
    {
        LobbyScreens.OverlayOverride = null;
        LobbyScreens.IdentityOverride = null;
        if (_realActions != null)
        {
            LobbyScreens.Instance.Attach(_realActions);
        }

        _fake = null;
    }

    /// <summary>The Online group, nobody hosting, then with one and two friends hosting: its live line, and focus on Join.</summary>
    private static IEnumerable<Wait> StepScrGroup()
    {
        TitleScreen title = Title();
        foreach (Wait wait in MainShape())
        {
            yield return wait;
        }

        foreach (Wait wait in ToMain())
        {
            yield return wait;
        }

        // Out of a session for the step: the group's live line and smart focus are never shown in one (LobbyScreens.TickGroup), and the fake starts as
        // a host alone. Its state comes back at the step's end, for the steps after.
        LobbyState session = Fake.Snapshot();
        Fake.SetState(LobbyState.None);
        Fake.SetListings();
        OnlineGroup.Enter(title);
        yield return Until(() => (int)Title().m_CurrentButtonGroup == OnlineGroup.GroupValue && !Title()._isTransitioningGroup, 3, "the Online group");
        foreach (Wait wait in Snap("group_nobody", SettleFrames))
        {
            yield return wait;
        }

        Fake.SetListings(Fake.FiveRows().Take(1).ToArray());
        yield return Seconds(0.8);
        foreach (Wait wait in Snap("group_one_hosting", SettleFrames))
        {
            yield return wait;
        }

        Fake.SetListings(Fake.FiveRows());
        yield return Seconds(0.8);
        foreach (Wait wait in Snap("group_many_hosting", SettleFrames))
        {
            yield return wait;
        }

        foreach (Wait wait in ToMain())
        {
            yield return wait;
        }

        // In again with friends hosting: focus starts on Join.
        OnlineGroup.Enter(title);
        yield return Seconds(0.7);
        StepNote($"entered with friends hosting: the cursor is on {ObjectName(Navigation().CurrentNavigable)}");
        if (ObjectName(Navigation().CurrentNavigable) != "JoinLobby")
        {
            Problem("smart focus didn't land on Join Lobby");
        }

        foreach (Wait wait in Snap("group_focus_join", SettleFrames))
        {
            yield return wait;
        }

        foreach (Wait wait in ToMain())
        {
            yield return wait;
        }

        Fake.SetListings();
        yield return Seconds(0.7);
        Fake.SetState(session);
    }

    /// <summary>The Join list in all its states, then each kind of failure.</summary>
    private static IEnumerable<Wait> StepScrBrowser()
    {
        foreach (Wait wait in OpenBrowser())
        {
            yield return wait;
        }

        foreach (Wait wait in Snap("browser_empty", SettleFrames))
        {
            yield return wait;
        }

        Fake.SetListings(Fake.FiveRows());
        yield return Seconds(0.8);
        foreach (Wait wait in Snap("browser_rows", SettleFrames))
        {
            yield return wait;
        }

        // Down to each row in turn, by the game's own move: the details follow, the wrap is a pad's.
        NavigationController navigation = Navigation();
        for (int i = 1; i <= 4; i++)
        {
            navigation.m_CurrentDirection = AbstractNavigable.EDirection.Down;
            navigation.MoveInDirection();
            yield return Frames(10);
            foreach (Wait wait in Snap($"browser_row{i}", 2))
            {
                yield return wait;
            }
        }

        StepNote($"after four moves the cursor is on {ObjectName(navigation.CurrentNavigable)}");
        navigation.m_CurrentDirection = AbstractNavigable.EDirection.Down;
        navigation.MoveInDirection();
        yield return Frames(10);
        StepNote($"a fifth move wraps to {ObjectName(navigation.CurrentNavigable)}");

        // A refresh that drops a row: focus stays on its lobby, and a row with the cursor hands it on.
        // Rows are named by the order they were made (Lobby1, Lobby2...), not by lobby: the row object itself says whether the cursor's row went.
        AbstractNavigable? beforeRow = navigation.CurrentNavigable;
        string before = ObjectName(beforeRow);
        Fake.SetListings(Fake.FiveRows().Where(row => row.LobbyId != 3).ToArray());
        yield return Seconds(1.2);
        bool rowWent = beforeRow == null || beforeRow.WasCollected || !beforeRow.gameObject.activeInHierarchy;
        StepNote($"the row of lobby 3 went: the cursor was on {before}{(rowWent ? " (that row went)" : string.Empty)}, is on {ObjectName(navigation.CurrentNavigable)}");
        if (!rowWent && beforeRow!.Pointer != navigation.CurrentNavigable?.Pointer)
        {
            Problem("an auto-refresh moved the cursor");
        }

        Fake.SetListings(Fake.FiveRows());
        yield return Seconds(0.8);

        // Joining: a row pressed; the fake never answers until it's told.
        LobbyScreens.Instance.Browser?.Joining(1, "Alex");
        yield return Frames(6);
        foreach (Wait wait in Snap("browser_joining", 4))
        {
            yield return wait;
        }

        LobbyScreens.Instance.Browser?.JoinEnded();
        yield return Frames(6);
        foreach (Wait wait in Failures())
        {
            yield return wait;
        }

        Fake.SetListings();
        LobbyScreens.Instance.CloseBrowser();
        yield return Until(() => !LobbyShell.IsOpen, ShellSeconds, "the browser closed");
        yield return Until(TitleIsBack, 3, "the title back");
    }

    /// <summary>Every failure a player can meet, each with its button: the game's own prompts, over the browser.</summary>
    private static IEnumerable<Wait> Failures()
    {
        (LobbyFailure Reason, string Name)[] kinds =
        {
            (LobbyFailure.Full, "Alex"), (LobbyFailure.Closed, "Alex"), (LobbyFailure.Unreachable, "Alex"), (LobbyFailure.NotAllowed, "Alex"), (LobbyFailure.Blocked, "Alex"),
            (LobbyFailure.Limited, "Alex"), (LobbyFailure.RateLimited, "Alex"), (LobbyFailure.GameBuild, "Alex"), (LobbyFailure.NotAFriend, "Alex"), (LobbyFailure.HostOff, "Alex"),
            (LobbyFailure.NoController, "Alex"), (LobbyFailure.UnsupportedInput, "Alex"), (LobbyFailure.Unknown, "Alex"), (LobbyFailure.Removed, "Alex"), (LobbyFailure.HostClosed, "Alex"),
            (LobbyFailure.HostLost, "Alex"), (LobbyFailure.OnlineOff, string.Empty), (LobbyFailure.OnlineBroken, string.Empty), (LobbyFailure.SteamOff, string.Empty),
            (LobbyFailure.SteamWaiting, string.Empty), (LobbyFailure.RestoredBackup, string.Empty), (LobbyFailure.CreateFailed, string.Empty),
            (LobbyFailure.FriendsUnchecked, "Alex"), (LobbyFailure.HostFriendsUnchecked, "Alex"), (LobbyFailure.RelayOnlyRefused, string.Empty), (LobbyFailure.DirectLink, "Alex"),
        };
        foreach ((LobbyFailure reason, string name) in kinds)
        {
            foreach (Wait wait in ShowOneFailure(JoinFailures.Message(reason, name), reason.ToString()))
            {
                yield return wait;
            }
        }

        foreach (Wait wait in ShowOneFailure(JoinFailures.Message(LobbyFailure.ModVersion, FakeLobbyActions.NameWide, "0.9.0-m4", "0.8.0-m4"), "ModVersion"))
        {
            yield return wait;
        }

        LobbyScreens.Instance.ShowFailure(JoinFailures.Message(LobbyFailure.ScreenFailed));
        yield return Seconds(2);
        foreach (Wait wait in Snap("failure_toast", 0))
        {
            yield return wait;
        }

        yield return Seconds(3);
    }

    private static IEnumerable<Wait> ShowOneFailure(FailureMessage message, string name)
    {
        // Stays: the tour's landing is its next scene; the landings have their own test (LandOn, the Join list's focus).
        AbstractMenu? shell = LobbyShell.Menu;
        LobbyScreens.Instance.ShowFailure(message with { Landing = FailureLanding.Stay });
        yield return Until(() => Top() is { } top && top.Pointer != shell?.Pointer, 8, $"the {name} prompt");
        yield return Seconds(0.55);
        foreach (Wait wait in Snap($"failure_{name}", 0))
        {
            yield return wait;
        }

        foreach (Wait wait in CloseTopPrompt(shell))
        {
            yield return wait;
        }
    }

    private static IEnumerable<Wait> CloseTopPrompt(AbstractMenu? shell)
    {
        AbstractMenu? over = Top();
        if (over != null && over.Pointer != shell?.Pointer)
        {
            _ = over.Hide();
            yield return Until(() => Top()?.Pointer == shell?.Pointer && !LobbyPrompts.IsOpen, 5, "the prompt gone");
        }

        // In time, not in frames: a window at full speed gets through ten frames before the prompt's Out is over.
        double started = Streaming.FrameClock.Seconds;
        yield return Until(() => shell == null || (Navigation().CurrentNavigable is { } at && at.transform.IsChildOf(shell.transform)), 1, "the cursor back on the shell");
        AbstractNavigable? cursor = Navigation().CurrentNavigable;
        if (shell != null && (cursor == null || !cursor.transform.IsChildOf(shell.transform)))
        {
            Problem($"the prompt gave the cursor to {ObjectName(cursor)}, not the shell");
        }
        else
        {
            StepNote($"the cursor was back on the shell {(Streaming.FrameClock.Seconds - started) * 1000:0} ms after the prompt was gone");
        }
    }

    /// <summary>The lobby, scene by scene.</summary>
    private static IEnumerable<Wait> StepScrLobby()
    {
        ulong self = Fake.OwnId;
        foreach (Wait wait in OpenLobby(Fake.HostAlone(self)))
        {
            yield return wait;
        }

        foreach (Wait wait in Snap("lobby_host_alone", SettleFrames))
        {
            yield return wait;
        }

        // A friend arrives: the slam, half way through, and the cursor stays.
        AbstractNavigable? before = Navigation().CurrentNavigable;
        Fake.SetState(Fake.HostFull(self));
        yield return Seconds(0.1);
        ScreenCapture.CaptureScreenshot(System.IO.Path.Combine(_folder, "lobby_arrivals_slam_mid.png")); // no dump: its time would pass the slam

        yield return Seconds(0.4);
        StepNote($"arrivals: the cursor was on {ObjectName(before)}, is on {ObjectName(Navigation().CurrentNavigable)}");
        if (before?.Pointer != Navigation().CurrentNavigable?.Pointer)
        {
            Problem("an arrival moved the cursor");
        }

        foreach (Wait wait in Snap("lobby_host_four", SettleFrames))
        {
            yield return wait;
        }

        // Each thing the cursor can be on: the prompts follow.
        foreach ((LobbyFocus focus, string name) in new[]
        {
            (new LobbyFocus(LobbyFocusKind.Seat, 1), "seat1"), (new LobbyFocus(LobbyFocusKind.Seat, 2), "seat2"), (new LobbyFocus(LobbyFocusKind.ModeRow), "mode"),
            (new LobbyFocus(LobbyFocusKind.SaveRow), "save"), (new LobbyFocus(LobbyFocusKind.StartRow), "start"),
        })
        {
            LobbyScreens.Instance.Lobby?.FocusForCheck(focus);
            yield return Frames(14);
            foreach (Wait wait in Snap($"lobby_focus_{name}", 0))
            {
                yield return wait;
            }
        }

        Fake.SetState(Fake.HostStages(self));
        yield return Seconds(0.4);
        foreach (Wait wait in Snap("lobby_stages", SettleFrames))
        {
            yield return wait;
        }

        // Quick Game: no save row.
        Fake.SetState(Fake.HostFull(self, false) with { Mode = LobbyMode.QuickGame, Save = LobbySaveRow.None });
        LobbyScreens.Instance.Lobby?.FocusForCheck(new LobbyFocus(LobbyFocusKind.ModeRow));
        yield return Seconds(0.3);
        foreach (Wait wait in Snap("lobby_quick", SettleFrames))
        {
            yield return wait;
        }

        // The prompts: leaving with friends in; someone not here at Start; removing one.
        Fake.SetState(Fake.HostFull(self));
        yield return Seconds(0.2);
        AbstractMenu? shell = LobbyShell.Menu;
        LobbyScreens.Instance.Lobby?.Back();
        yield return Until(() => Top() is { } top && top.Pointer != shell?.Pointer, 4, "the leave prompt");
        yield return Seconds(0.55);
        foreach (Wait wait in Snap("prompt_leave", 0))
        {
            yield return wait;
        }

        foreach (Wait wait in CloseTopPrompt(shell))
        {
            yield return wait;
        }

        LobbyScreens.Instance.Lobby?.PressForCheck(LobbyAction.Start);
        yield return Until(() => Top() is { } top && top.Pointer != shell?.Pointer, 4, "the not-here prompt");
        yield return Seconds(0.55);
        foreach (Wait wait in Snap("prompt_not_here", 0))
        {
            yield return wait;
        }

        foreach (Wait wait in CloseTopPrompt(shell))
        {
            yield return wait;
        }

        // The invite panel: a state of this shell.
        Fake.SetFriends(FakeLobbyActions.SomeFriends());
        Fake.SetState(Fake.HostAlone(self));
        yield return Seconds(0.2);
        LobbyScreens.Instance.Lobby?.PressForCheck(LobbyAction.OpenInvitePanel);
        yield return Seconds(0.5);
        foreach (Wait wait in Snap("invite_panel", SettleFrames))
        {
            yield return wait;
        }

        LobbyScreens.Instance.Lobby?.PressForCheck(LobbyAction.Invite);
        yield return Seconds(1.2);
        foreach (Wait wait in Snap("invite_sent", 0))
        {
            yield return wait;
        }

        yield return Seconds(3);
        Fake.SetFriends();
        yield return Seconds(2.2);
        foreach (Wait wait in Snap("invite_nobody", SettleFrames))
        {
            yield return wait;
        }

        LobbyScreens.Instance.Lobby?.Back();
        yield return Seconds(0.4);
        foreach (Wait wait in Snap("invite_back_on_seat", SettleFrames))
        {
            yield return wait;
        }

        // The countdown: on the host, then on a friend; 3, then 1. The ribbon after a game; a friend who lost the host.
        Fake.SetState(FakeLobbyActions.Counting(Fake.HostFull(self, false), 2600));
        yield return Seconds(0.5);
        foreach (Wait wait in Snap("countdown_host", 0))
        {
            yield return wait;
        }

        Fake.SetState(Fake.HostFull(self, false));
        yield return Seconds(0.3);
        Fake.SetState(FakeLobbyActions.Back(Fake.HostFull(self, false), new LobbyResult(LobbyResultKind.MatchWon, 4, 2)));
        LobbyScreens.Instance.Lobby?.FocusForCheck(new LobbyFocus(LobbyFocusKind.StartRow));
        yield return Seconds(0.3);
        foreach (Wait wait in Snap("ribbon_won", SettleFrames))
        {
            yield return wait;
        }

        Fake.SetState(FakeLobbyActions.Back(Fake.HostFull(self, false), new LobbyResult(LobbyResultKind.RunOver, 2, 5)));
        yield return Seconds(0.3);
        foreach (Wait wait in Snap("ribbon_run_over", SettleFrames))
        {
            yield return wait;
        }

        Fake.SetState(Fake.HostFull(self, false) with { WaitingForEveryone = true });
        yield return Seconds(0.3);
        foreach (Wait wait in Snap("lobby_waiting", SettleFrames))
        {
            yield return wait;
        }

        foreach (Wait wait in CloseLobby())
        {
            yield return wait;
        }

        foreach (Wait wait in Presses(self))
        {
            yield return wait;
        }

        // A friend's lobby is a shell of its own: any player drives it.
        foreach (Wait wait in OpenLobby(Fake.FriendView(self)))
        {
            yield return wait;
        }

        foreach (Wait wait in Snap("friend", SettleFrames))
        {
            yield return wait;
        }

        Fake.ToggleReady();
        yield return Seconds(0.45);
        foreach (Wait wait in Snap("friend_ready", SettleFrames))
        {
            yield return wait;
        }

        Fake.SetState(FakeLobbyActions.Counting(Fake.FriendView(self), 1400));
        yield return Seconds(0.5);
        foreach (Wait wait in Snap("friend_countdown", 0))
        {
            yield return wait;
        }

        Fake.SetState(Fake.FriendView(self) with { Reconnecting = true });
        yield return Seconds(0.3);
        foreach (Wait wait in Snap("friend_reconnecting", SettleFrames))
        {
            yield return wait;
        }

        foreach (Wait wait in CloseLobby())
        {
            yield return wait;
        }

        foreach (Wait wait in OpenLobby(Fake.HostTen(self)))
        {
            yield return wait;
        }

        foreach (Wait wait in Snap("lobby_ten", SettleFrames))
        {
            yield return wait;
        }

        foreach (Wait wait in CloseLobby())
        {
            yield return wait;
        }

        Fake.SetState(Fake.HostAlone(self));
    }

    /// <summary>The real inputs' paths: the game's confirm on what has the cursor, the game's shortcuts on the bar. What they change is the fake's state, and the screen draws that.</summary>
    private static IEnumerable<Wait> Presses(ulong self)
    {
        Fake.SetFriends(FakeLobbyActions.SomeFriends());
        foreach (Wait wait in OpenLobby(Fake.HostAlone(self)))
        {
            yield return wait;
        }

        LobbyView lobby = LobbyScreens.Instance.Lobby ?? throw new InvalidOperationException("no lobby");
        NavigationController navigation = Navigation();
        lobby.FocusForCheck(new LobbyFocus(LobbyFocusKind.Seat, 1));
        yield return Frames(10);
        navigation.OnConfirm();
        yield return Seconds(0.4);
        Note("A on an empty seat opens the invite panel", lobby.PanelOpen);
        lobby.TriggerForCheck(LobbyButton.Back);
        yield return Seconds(0.4);
        Note("B closes the panel", !lobby.PanelOpen);
        yield return Frames(10);
        Note($"B lands on the seat it came from (on {ObjectName(navigation.CurrentNavigable)})", ObjectName(navigation.CurrentNavigable) == "Seat1");
        lobby.TriggerForCheck(LobbyButton.TabRight);
        yield return Seconds(0.2);
        Note("RB changes the mode", Fake.Snapshot().Mode == LobbyMode.QuickGame);
        lobby.TriggerForCheck(LobbyButton.TabLeft);
        yield return Seconds(0.2);
        Note("LB changes it back", Fake.Snapshot().Mode == LobbyMode.Campaign);
        lobby.FocusForCheck(new LobbyFocus(LobbyFocusKind.StartRow));
        yield return Frames(10);
        navigation.OnConfirm();
        yield return Seconds(0.2);
        Note("A on Start counts down", Fake.Snapshot().Phase == LobbyPhase.Counting);
        Note("B is on the bar during the countdown", lobby.TriggerForCheck(LobbyButton.Back));
        yield return Seconds(0.2);
        Note("B cancels the countdown", Fake.Snapshot().Phase == LobbyPhase.Open);
        yield return Frames(10);
        Note($"focus back on Start (on {ObjectName(navigation.CurrentNavigable)})", ObjectName(navigation.CurrentNavigable) == "Start");
        Fake.SetFriends();
        foreach (Wait wait in CloseLobby())
        {
            yield return wait;
        }
    }

    private static void Note(string what, bool held)
    {
        StepNote($"{(held ? "yes" : "NO")}: {what}");
        if (!held)
        {
            Problem(what);
        }
    }

    /// <summary>The worst screens in every language: the browser, four seats, a friend's, the longest failure, the group. Back in the player's language before anything closes.</summary>
    private static IEnumerable<Wait> StepScrLangs()
    {
        foreach (Wait wait in ToMain())
        {
            yield return wait;
        }

        Fake.SetListings(Fake.FiveRows());
        ulong self = Fake.OwnId;
        foreach (Wait wait in OpenLobby(Fake.HostFull(self)))
        {
            yield return wait;
        }

        foreach (string wanted in LobbyWords.Languages)
        {
            (string Name, string Code)? language = FindLanguage(wanted);
            if (language == null)
            {
                Problem($"the game has no language {wanted}");
                continue;
            }

            foreach (Wait wait in SetLanguage(language.Value.Name, language.Value.Code))
            {
                yield return wait;
            }

            yield return Seconds(0.6);
            foreach (Wait wait in Snap($"lobby_{language.Value.Code}", 4))
            {
                yield return wait;
            }

            foreach (Wait wait in ShowOneFailure(JoinFailures.Message(LobbyFailure.ModVersion, FakeLobbyActions.NameFriend, "0.9.0-m4", "0.8.0-m4"), $"longest_{language.Value.Code}"))
            {
                yield return wait;
            }
        }

        foreach (Wait wait in SetLanguage(_language, _languageCode))
        {
            yield return wait;
        }

        _languageChanged = false;
        yield return Seconds(0.3);
        foreach (Wait wait in CloseLobby())
        {
            yield return wait;
        }

        foreach (Wait wait in OpenBrowser())
        {
            yield return wait;
        }

        yield return Seconds(0.8);
        foreach (string wanted in LobbyWords.Languages)
        {
            (string Name, string Code)? language = FindLanguage(wanted);
            if (language != null)
            {
                foreach (Wait wait in SetLanguage(language.Value.Name, language.Value.Code))
                {
                    yield return wait;
                }

                yield return Seconds(0.8);
                foreach (Wait wait in Snap($"browser_{language.Value.Code}", 4))
                {
                    yield return wait;
                }
            }
        }

        foreach (Wait wait in SetLanguage(_language, _languageCode))
        {
            yield return wait;
        }

        _languageChanged = false;
        Fake.SetListings();
        yield return Seconds(0.3);
        LobbyScreens.Instance.CloseBrowser();
        yield return Until(() => !LobbyShell.IsOpen, ShellSeconds, "the browser closed");
        yield return Until(TitleIsBack, 3, "the title back");

        // The new native screens in every language (LobbyUiCheck.ScreensNative.cs).
        foreach (Wait wait in NativeScreensInEveryLanguage())
        {
            yield return wait;
        }
    }

    /// <summary>Every shape in English and German: the group, the browser, four seats, ten seats, the prompts.</summary>
    private static IEnumerable<Wait> StepScrSizes()
    {
        TitleScreen title = Title();
        int displayWidth = Display.main.systemWidth;
        int displayHeight = Display.main.systemHeight;
        StepNote($"the display is {displayWidth}x{displayHeight}");
        ulong self = Fake.OwnId;
        foreach ((string Language, string Code) language in new[] { (_language, _languageCode), FindLanguage("German") ?? (_language, _languageCode) })
        {
            foreach (Wait wait in SetLanguage(language.Language, language.Code))
            {
                yield return wait;
            }

            foreach ((int width, int height) in UiCheckPlan.SizesFor(displayWidth, displayHeight))
            {
                _windowChanged = true;
                Screen.SetResolution(width, height, _windowMode);
                yield return Until(() => Screen.width == width && Screen.height == height, RestoreWindowSeconds, $"the window at {width}x{height}");
                yield return Frames(10);
                StepNote($"asked for {width}x{height}, got {Screen.width}x{Screen.height} ({_windowMode})");
                Fake.SetListings(Fake.FiveRows());
                OnlineGroup.Enter(title);
                yield return Until(() => (int)Title().m_CurrentButtonGroup == OnlineGroup.GroupValue && !Title()._isTransitioningGroup, 3, "the Online group");
                yield return Seconds(0.7);
                foreach (Wait wait in Snap("group", SettleFrames))
                {
                    yield return wait;
                }

                foreach (Wait wait in ToMain())
                {
                    yield return wait;
                }

                yield return Seconds(0.3);
                foreach (Wait wait in OpenLobby(Fake.HostFull(self)))
                {
                    yield return wait;
                }

                foreach (Wait wait in Snap("lobby", SettleFrames))
                {
                    yield return wait;
                }

                Fake.SetState(Fake.FriendView(self));
                yield return Seconds(0.3);
                foreach (Wait wait in Snap("friend_on_host_shell", SettleFrames))
                {
                    yield return wait;
                }

                foreach (Wait wait in SetLanguageThen(_language, _languageCode, CloseLobby()))
                {
                    yield return wait;
                }

                foreach (Wait wait in SetLanguage(language.Language, language.Code))
                {
                    yield return wait;
                }

                foreach (Wait wait in OpenLobby(Fake.HostTen(self)))
                {
                    yield return wait;
                }

                foreach (Wait wait in Snap("lobby_ten", SettleFrames))
                {
                    yield return wait;
                }

                foreach (Wait wait in SetLanguageThen(_language, _languageCode, CloseLobby()))
                {
                    yield return wait;
                }

                foreach (Wait wait in SetLanguage(language.Language, language.Code))
                {
                    yield return wait;
                }

                foreach (Wait wait in OpenBrowser())
                {
                    yield return wait;
                }

                yield return Seconds(0.8);
                foreach (Wait wait in Snap("browser", SettleFrames))
                {
                    yield return wait;
                }

                foreach (Wait wait in SetLanguageThen(_language, _languageCode, CloseBrowserNow()))
                {
                    yield return wait;
                }

                // The new native screens at this shape (LobbyUiCheck.ScreensNative.cs); it ends in this loop's language.
                foreach (Wait wait in NativeScreensAtThisSize(language.Language, language.Code))
                {
                    yield return wait;
                }
            }
        }

        foreach (Wait wait in SetLanguage(_language, _languageCode))
        {
            yield return wait;
        }

        _languageChanged = false;
        Fake.SetListings();
        foreach (Wait wait in RestoreWindow())
        {
            yield return wait;
        }
    }

    private static IEnumerable<Wait> SetLanguageThen(string name, string code, IEnumerable<Wait> then)
    {
        foreach (Wait wait in SetLanguage(name, code))
        {
            yield return wait;
        }

        foreach (Wait wait in then)
        {
            yield return wait;
        }
    }

    private static IEnumerable<Wait> CloseBrowserNow()
    {
        LobbyScreens.Instance.CloseBrowser();
        yield return Until(() => !LobbyShell.IsOpen, ShellSeconds, "the browser closed");
        yield return Until(TitleIsBack, 3, "the title back");
    }

    private static IEnumerable<Wait> OpenLobby(LobbyState state)
    {
        Fake.SetState(state);
        LobbyScreens.Instance.ShowLobby();
        yield return Until(() => LobbyShell.IsIdle && LobbyShell.Kind == ShellKind.Lobby, 10, "the lobby's intro over");
        yield return Frames(4);
    }

    private static IEnumerable<Wait> CloseLobby()
    {
        LobbyScreens.Instance.HideLobby();
        yield return Until(() => !LobbyShell.IsOpen, ShellSeconds, "the lobby closed");
        yield return Until(TitleIsBack, 3, "the title back");
    }

    private static IEnumerable<Wait> OpenBrowser()
    {
        LobbyScreens.Instance.ShowJoinList(0);
        yield return Until(() => LobbyShell.IsIdle && LobbyShell.Kind == ShellKind.Browser, 10, "the browser's intro over");
        yield return Frames(4);
    }
}
#endif
