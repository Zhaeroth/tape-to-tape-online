using System.Collections.Generic;
using System.Linq;
using T2TOnline.Core.Lobby;
using T2TOnline.Core.Protocol;
using B = T2TOnline.Core.Lobby.LobbyButton;
using K = T2TOnline.Core.Lobby.LobbyFocusKind;
using S = T2TOnline.Core.Lobby.LobbyScreen;

namespace T2TOnline.Core.Tests.Lobby;

/// <summary>The lobby's UX tables, row by row: first focus, Back, prompts per role and focus, the seats, and moving around.</summary>
public class LobbyRulesTests
{
    private const ulong HostId = 100;
    private const ulong Sam = 200;
    private const ulong Casey = 300;

    private static LobbyState Host(bool alone = true, bool backFromGame = false, bool titleReady = true, LobbyPhase phase = LobbyPhase.Open, bool waiting = false,
        LobbyMode mode = LobbyMode.Campaign, LobbySaveRow? save = null, int friends = 0, int capacity = 4, SeatStage stage = SeatStage.Here)
    {
        var people = new List<LobbyPerson>();
        if (!alone || friends > 0)
        {
            people.Add(LobbyStateTests.Person(Sam, "Sam", stage));
        }

        if (friends > 1)
        {
            people.Add(LobbyStateTests.Person(Casey, "Casey"));
        }

        LobbyBoardMessage board = new LobbyBoardBuilder(HostId, capacity).Build(new LobbyBoardInputs
        {
            Phase = phase,
            Mode = mode,
            Save = save ?? LobbySaveRow.None,
            Host = LobbyStateTests.Person(HostId, "Alex"),
            People = people,
        });
        return LobbyState.From(board, new LobbyViewInputs { Role = LobbyRole.Host, Self = HostId, BackFromGame = backFromGame, TitleReady = titleReady, WaitingForEveryone = waiting });
    }

    private static LobbyState Friend(LobbyPhase phase = LobbyPhase.Open, bool reconnecting = false, bool ready = false)
    {
        LobbyBoardMessage board = new LobbyBoardBuilder(HostId).Build(new LobbyBoardInputs
        {
            Phase = phase,
            Host = LobbyStateTests.Person(HostId, "Alex"),
            People = new[] { LobbyStateTests.Person(Sam, "Sam", ready: ready) },
        });
        return LobbyState.From(board, new LobbyViewInputs { Role = LobbyRole.Friend, Self = Sam, Reconnecting = reconnecting });
    }

    private static LobbyScreenFacts Of(LobbyState state, bool overlay = true) => new() { State = state, OverlayOn = overlay };

    private static LobbyFocus Default(S screen, LobbyScreenFacts facts) => LobbyRules.DefaultFocus(screen, facts);

    private static (B, string, LobbyAction)[] Bar(S screen, LobbyFocus focus, LobbyScreenFacts facts) =>
        LobbyRules.Prompts(screen, focus, facts).Select(p => (p.Button, p.Term, p.Action)).ToArray();

    // Default focus, row by row.

    [Fact]
    public void Online_starts_on_join_if_anyone_hosts_else_on_host()
    {
        Assert.Equal(new LobbyFocus(K.JoinLobbyRow), Default(S.Online, new LobbyScreenFacts { AnyoneHosting = true }));
        Assert.Equal(new LobbyFocus(K.HostLobbyRow), Default(S.Online, new LobbyScreenFacts { AnyoneHosting = false }));
    }

    [Fact]
    public void The_join_list_starts_on_the_first_joinable_row_or_the_empty_states_host_lobby()
    {
        Assert.Equal(new LobbyFocus(K.JoinRow, 2), Default(S.JoinList, new LobbyScreenFacts { JoinRows = 3, FirstJoinRow = 2 }));
        Assert.Equal(new LobbyFocus(K.JoinRow, 0), Default(S.JoinList, new LobbyScreenFacts { JoinRows = 3, FirstJoinRow = -1 })); // only greyed rows: the first explains itself
        Assert.Equal(new LobbyFocus(K.EmptyStateHost), Default(S.JoinList, new LobbyScreenFacts { JoinRows = 0 }));
    }

    [Fact]
    public void Joining_prompts_and_failures_start_on_their_only_or_safe_control()
    {
        Assert.Equal(new LobbyFocus(K.JoinCancel), Default(S.Joining, default));
        Assert.Equal(new LobbyFocus(K.SafeAnswer), Default(S.Prompt, default));
        Assert.Equal(new LobbyFocus(K.FirstButton), Default(S.Failure, default));
    }

    [Fact]
    public void The_friend_list_starts_on_the_first_friend_or_its_back()
    {
        Assert.Equal(new LobbyFocus(K.FriendRow, 0), Default(S.InvitePanel, new LobbyScreenFacts { FriendRows = 2 }));
        Assert.Equal(new LobbyFocus(K.PanelBack), Default(S.InvitePanel, new LobbyScreenFacts { FriendRows = 0 }));
    }

    [Fact]
    public void A_host_alone_starts_on_the_first_empty_seat()
    {
        Assert.Equal(new LobbyFocus(K.Seat, 1), Default(S.Lobby, Of(Host())));
    }

    [Fact]
    public void A_host_back_from_a_game_starts_on_start()
    {
        Assert.Equal(new LobbyFocus(K.StartRow), Default(S.Lobby, Of(Host(backFromGame: true))));
    }

    [Fact]
    public void A_host_reopening_with_friends_starts_on_start()
    {
        Assert.Equal(new LobbyFocus(K.StartRow), Default(S.Lobby, Of(Host(alone: false))));
    }

    [Fact]
    public void A_friend_starts_on_their_own_card()
    {
        Assert.Equal(new LobbyFocus(K.Seat, 1), Default(S.Lobby, Of(Friend())));
    }

    [Fact]
    public void A_friend_with_no_seat_of_their_own_yet_starts_on_a_real_card()
    {
        LobbyState state = LobbyState.From(new LobbyBoardBuilder(HostId).Build(new LobbyBoardInputs { Host = LobbyStateTests.Person(HostId, "Alex") }), new LobbyViewInputs { Role = LobbyRole.Friend, Self = Sam });

        Assert.Equal(new LobbyFocus(K.Seat, 0), Default(S.Lobby, Of(state)));
    }

    [Fact]
    public void A_friend_arriving_never_moves_focus()
    {
        var focus = new LobbyFocus(K.Seat, 2);
        Assert.Equal(focus, LobbyRules.Repair(focus, Host()));
        Assert.Equal(focus, LobbyRules.Repair(focus, Host(alone: false)));
    }

    // Back, row by row.

    private static LobbyBackRule Back(S screen, LobbyState? state = null) => LobbyRules.Back(screen, new LobbyScreenFacts { State = state ?? LobbyState.None });

    [Fact]
    public void Back_from_the_plain_screens_asks_nothing_and_lands_where_the_table_says()
    {
        Assert.Equal((LobbyBackAction.CloseScreen, false, S.Online, K.OnlineRow), Row(Back(S.Online)));
        Assert.Equal((LobbyBackAction.CloseScreen, false, S.Online, K.JoinLobbyRow), Row(Back(S.JoinList)));
        Assert.Equal((LobbyBackAction.CloseScreen, false, S.Lobby, K.PreviousSeat), Row(Back(S.InvitePanel)));
        Assert.Equal((LobbyBackAction.CancelJoin, false, S.JoinList, K.JoinRow), Row(Back(S.Joining)));
        Assert.Equal(LobbyBackAction.SafeAnswer, Back(S.Prompt).Action);
        Assert.Equal(LobbyBackAction.LastButton, Back(S.Failure).Action);
    }

    [Fact]
    public void A_host_alone_closes_without_asking()
    {
        Assert.Equal((LobbyBackAction.LeaveLobby, false, S.Online, K.HostLobbyRow), Row(Back(S.Lobby, Host())));
    }

    [Fact]
    public void A_host_with_one_friend_is_asked_by_name_with_close_as_the_other_answer()
    {
        LobbyBackRule rule = Back(S.Lobby, Host(alone: false));

        Assert.Equal((LobbyBackAction.LeaveLobby, true, S.Online, K.HostLobbyRow), Row(rule));
        Assert.Equal("Close the lobby? Sam goes back to Online.", rule.Question!.Text(0));
        Assert.Equal(LobbyTerms.Close, rule.OtherAnswerTerm);
    }

    [Fact]
    public void A_host_with_several_is_asked_for_everyone()
    {
        Assert.Equal("Close the lobby? Everyone goes back to Online.", Back(S.Lobby, Host(friends: 2)).Question!.Text(0));
    }

    [Fact]
    public void A_friend_is_asked_and_lands_on_the_join_list()
    {
        LobbyBackRule rule = Back(S.Lobby, Friend());

        Assert.Equal((LobbyBackAction.LeaveLobby, true, S.JoinList, K.JoinRow), Row(rule));
        Assert.Equal("Leave Alex's lobby?", rule.Question!.Text(0));
        Assert.Equal(LobbyWords.Leave.Key, rule.OtherAnswerTerm);
    }

    [Fact]
    public void During_the_countdown_a_host_cancels_and_a_friend_can_do_nothing()
    {
        Assert.Equal((LobbyBackAction.CancelStart, false, S.Lobby, K.StartRow), Row(Back(S.Lobby, Host(phase: LobbyPhase.Counting))));
        Assert.Equal(LobbyBackAction.Nothing, Back(S.Lobby, Friend(LobbyPhase.Counting)).Action);
    }

    [Fact]
    public void During_a_wait_the_host_cancels_it()
    {
        Assert.Equal((LobbyBackAction.CancelStart, false, S.Lobby, K.StartRow), Row(Back(S.Lobby, Host(waiting: true))));
    }

    [Fact]
    public void Once_its_starting_b_does_nothing()
    {
        foreach (LobbyPhase phase in new[] { LobbyPhase.Starting, LobbyPhase.Playing, LobbyPhase.Returning })
        {
            Assert.Equal(LobbyBackAction.Nothing, Back(S.Lobby, Host(phase: phase)).Action);
            Assert.Equal(LobbyBackAction.Nothing, Back(S.Lobby, Friend(phase)).Action);
        }
    }

    [Fact]
    public void In_the_half_second_after_a_return_b_does_nothing()
    {
        Assert.Equal(LobbyBackAction.Nothing, Back(S.Lobby, Host(alone: false) with { InputLocked = true }).Action);
        Assert.Equal(LobbyBackAction.Nothing, Back(S.Lobby, Friend() with { InputLocked = true }).Action);
    }

    [Fact]
    public void A_friend_waiting_for_a_lost_host_has_a_back_and_it_asks_nothing()
    {
        Assert.Equal((LobbyBackAction.LeaveLobby, false, S.JoinList, K.JoinRow), Row(Back(S.Lobby, Friend(reconnecting: true))));
    }

    // Prompts, row by row: what's there is what can be done now, and nothing is greyed.

    [Fact]
    public void Online_shows_select_and_back()
    {
        Assert.Equal(new[] { (B.Confirm, LobbyTerms.Select, LobbyAction.Open), (B.Back, LobbyTerms.Back, LobbyAction.Back) }, Bar(S.Online, new LobbyFocus(K.JoinLobbyRow), default));
    }

    [Fact]
    public void The_join_list_joins_a_good_row_explains_a_greyed_one_and_always_refreshes()
    {
        (B, string, LobbyAction) refresh = (B.Special, LobbyWords.Refresh.Key, LobbyAction.Refresh);
        (B, string, LobbyAction) back = (B.Back, LobbyTerms.Back, LobbyAction.Back);
        Assert.Equal(new[] { (B.Confirm, LobbyWords.Join.Key, LobbyAction.Join), refresh, back }, Bar(S.JoinList, new LobbyFocus(K.JoinRow), new LobbyScreenFacts { FocusedRowJoinable = true }));
        Assert.Equal(new[] { (B.Confirm, LobbyWords.Why.Key, LobbyAction.Explain), refresh, back }, Bar(S.JoinList, new LobbyFocus(K.JoinRow), new LobbyScreenFacts { FocusedRowJoinable = false }));
        Assert.Equal(new[] { (B.Confirm, LobbyWords.HostLobby.Key, LobbyAction.HostLobby), refresh, back }, Bar(S.JoinList, new LobbyFocus(K.EmptyStateHost), default));
    }

    [Fact]
    public void Joining_only_cancels()
    {
        Assert.Equal(new[] { (B.Back, LobbyTerms.Cancel, LobbyAction.CancelJoin) }, Bar(S.Joining, new LobbyFocus(K.JoinCancel), default));
    }

    [Fact]
    public void The_friend_list_invites_who_can_be_and_offers_the_overlay_only_if_its_on()
    {
        (B, string, LobbyAction) back = (B.Back, LobbyTerms.Back, LobbyAction.Back);
        (B, string, LobbyAction) overlay = (B.Special, LobbyWords.SteamOverlay.Key, LobbyAction.InviteOverlay);
        (B, string, LobbyAction) invite = (B.Confirm, LobbyWords.Invite.Key, LobbyAction.Invite);
        Assert.Equal(new[] { invite, overlay, back }, Bar(S.InvitePanel, new LobbyFocus(K.FriendRow), new LobbyScreenFacts { FocusedFriendInvitable = true, OverlayOn = true }));
        Assert.Equal(new[] { invite, back }, Bar(S.InvitePanel, new LobbyFocus(K.FriendRow), new LobbyScreenFacts { FocusedFriendInvitable = true, OverlayOn = false }));
        Assert.Equal(new[] { overlay, back }, Bar(S.InvitePanel, new LobbyFocus(K.FriendRow), new LobbyScreenFacts { FocusedFriendInvitable = false, OverlayOn = true }));
        Assert.Equal(new[] { back }, Bar(S.InvitePanel, new LobbyFocus(K.PanelBack), default));
    }

    private static readonly (B, string, LobbyAction) ModeLeft = (B.TabLeft, LobbyTerms.GameMode, LobbyAction.ModeLeft);
    private static readonly (B, string, LobbyAction) ModeRight = (B.TabRight, LobbyTerms.GameMode, LobbyAction.ModeRight);
    private static readonly (B, string, LobbyAction) InviteAnywhere = (B.Special2, LobbyWords.Invite.Key, LobbyAction.OpenInvitePanel);
    private static readonly (B, string, LobbyAction) Settings = (B.RightTrigger, LobbyTerms.Settings, LobbyAction.OpenSettings);
    private static readonly (B, string, LobbyAction) CloseIt = (B.Back, LobbyWords.CloseLobby.Key, LobbyAction.Back);
    private static readonly (B, string, LobbyAction) LeaveIt = (B.Back, LobbyWords.LeaveLobby.Key, LobbyAction.Back);

    [Fact]
    public void A_host_on_an_empty_seat_invites()
    {
        LobbyState state = Host();
        (B, string, LobbyAction) start = (B.Start, LobbyTerms.NewRun, LobbyAction.Start);
        Assert.Equal(new[] { (B.Confirm, LobbyWords.Invite.Key, LobbyAction.OpenInvitePanel), InviteAnywhere, ModeLeft, ModeRight, start, Settings, CloseIt }, Bar(S.Lobby, new LobbyFocus(K.Seat, 1), Of(state)));
    }

    [Fact]
    public void A_host_on_a_friends_card_gets_profile_and_remove_and_a_does_nothing()
    {
        LobbyState state = Host(alone: false);
        (B, string, LobbyAction) start = (B.Start, LobbyTerms.NewRun, LobbyAction.Start);
        (B, string, LobbyAction) profile = (B.Special, LobbyWords.SteamProfile.Key, LobbyAction.OpenProfile);
        (B, string, LobbyAction) remove = (B.LeftTrigger, LobbyWords.Remove.Key, LobbyAction.Remove);
        Assert.Equal(new[] { profile, remove, InviteAnywhere, ModeLeft, ModeRight, start, Settings, CloseIt }, Bar(S.Lobby, new LobbyFocus(K.Seat, 1), Of(state)));

        // The overlay off: no profile prompt, and nothing moves to take its place.
        Assert.Equal(new[] { remove, InviteAnywhere, ModeLeft, ModeRight, start, Settings, CloseIt }, Bar(S.Lobby, new LobbyFocus(K.Seat, 1), Of(state, overlay: false)));
    }

    [Fact]
    public void A_host_on_their_own_card_gets_profile_and_no_remove()
    {
        (B, string, LobbyAction)[] bar = Bar(S.Lobby, new LobbyFocus(K.Seat, 0), Of(Host()));

        Assert.Contains((B.Special, LobbyWords.SteamProfile.Key, LobbyAction.OpenProfile), bar);
        Assert.DoesNotContain(bar, p => p.Item3 == LobbyAction.Remove);
    }

    [Fact]
    public void A_full_lobby_shows_no_invite_prompt()
    {
        LobbyState full = Host(friends: 2, capacity: 3);

        Assert.DoesNotContain(Bar(S.Lobby, new LobbyFocus(K.StartRow), Of(full)), p => p.Item3 == LobbyAction.OpenInvitePanel);
    }

    [Fact]
    public void Start_is_only_a_prompt_when_it_can_be_done_and_says_what_it_runs()
    {
        var used = new LobbySaveRow(2, true, "squad-a", 2, 5, 0);
        (B, string, LobbyAction)[] onContinue = Bar(S.Lobby, new LobbyFocus(K.StartRow), Of(Host(save: used)));
        Assert.Contains((B.Confirm, LobbyTerms.ContinueRun, LobbyAction.Start), onContinue);
        Assert.Contains((B.Start, LobbyTerms.ContinueRun, LobbyAction.Start), onContinue);

        (B, string, LobbyAction)[] onNew = Bar(S.Lobby, new LobbyFocus(K.StartRow), Of(Host()));
        Assert.Contains((B.Confirm, LobbyTerms.NewRun, LobbyAction.Start), onNew);

        (B, string, LobbyAction)[] quick = Bar(S.Lobby, new LobbyFocus(K.StartRow), Of(Host(mode: LobbyMode.QuickGame)));
        Assert.Contains((B.Start, LobbyTerms.Start, LobbyAction.Start), quick);

        Assert.DoesNotContain(Bar(S.Lobby, new LobbyFocus(K.StartRow), Of(Host(titleReady: false))), p => p.Item3 == LobbyAction.Start);
    }

    [Fact]
    public void New_run_is_offered_on_the_save_row_of_a_used_slot_only()
    {
        var used = new LobbySaveRow(2, true, "squad-a", 2, 5, 0);
        Assert.Contains((B.Special, LobbyTerms.NewRun, LobbyAction.NewRun), Bar(S.Lobby, new LobbyFocus(K.SaveRow), Of(Host(save: used))));
        Assert.DoesNotContain(Bar(S.Lobby, new LobbyFocus(K.SaveRow), Of(Host())), p => p.Item3 == LobbyAction.NewRun);
    }

    [Fact]
    public void The_countdown_shows_a_host_cancel_and_a_friend_nothing()
    {
        Assert.Equal(new[] { (B.Back, LobbyTerms.Cancel, LobbyAction.CancelStart) }, Bar(S.Lobby, new LobbyFocus(K.StartRow), Of(Host(phase: LobbyPhase.Counting))));
        Assert.Empty(Bar(S.Lobby, new LobbyFocus(K.Seat, 1), Of(Friend(LobbyPhase.Counting))));
    }

    [Fact]
    public void A_wait_shows_a_host_cancel()
    {
        Assert.Equal(new[] { (B.Back, LobbyTerms.Cancel, LobbyAction.CancelStart) }, Bar(S.Lobby, new LobbyFocus(K.StartRow), Of(Host(waiting: true))));
    }

    [Fact]
    public void Starting_shows_nothing_to_anyone()
    {
        Assert.Empty(Bar(S.Lobby, new LobbyFocus(K.StartRow), Of(Host(phase: LobbyPhase.Starting))));
        Assert.Empty(Bar(S.Lobby, new LobbyFocus(K.Seat, 1), Of(Friend(LobbyPhase.Starting))));
    }

    [Fact]
    public void The_input_lock_shows_no_prompts()
    {
        Assert.Empty(Bar(S.Lobby, new LobbyFocus(K.StartRow), Of(Host(backFromGame: true) with { InputLocked = true })));
    }

    [Fact]
    public void A_friend_on_their_own_card_marks_ready_or_not_and_has_no_host_prompts()
    {
        (B, string, LobbyAction) profile = (B.Special, LobbyWords.SteamProfile.Key, LobbyAction.OpenProfile);
        Assert.Equal(new[] { (B.Confirm, LobbyTerms.Ready, LobbyAction.ToggleReady), profile, Settings, LeaveIt }, Bar(S.Lobby, new LobbyFocus(K.Seat, 1), Of(Friend())));
        Assert.Equal(new[] { (B.Confirm, LobbyWords.NotReady.Key, LobbyAction.ToggleReady), profile, Settings, LeaveIt }, Bar(S.Lobby, new LobbyFocus(K.Seat, 1), Of(Friend(ready: true))));
    }

    [Fact]
    public void A_friend_on_someone_elses_card_can_only_see_their_profile()
    {
        (B, string, LobbyAction) profile = (B.Special, LobbyWords.SteamProfile.Key, LobbyAction.OpenProfile);
        Assert.Equal(new[] { profile, Settings, LeaveIt }, Bar(S.Lobby, new LobbyFocus(K.Seat, 0), Of(Friend())));
        Assert.Equal(new[] { Settings, LeaveIt }, Bar(S.Lobby, new LobbyFocus(K.Seat, 0), Of(Friend(), overlay: false)));
    }

    [Fact]
    public void A_friend_reconnecting_only_has_back()
    {
        Assert.Equal(new[] { (B.Back, LobbyTerms.Back, LobbyAction.Back) }, Bar(S.Lobby, new LobbyFocus(K.Seat, 1), Of(Friend(reconnecting: true))));
    }

    [Fact]
    public void A_prompt_or_a_failure_answers()
    {
        Assert.Equal(new[] { (B.Confirm, LobbyTerms.Select, LobbyAction.Answer) }, Bar(S.Prompt, new LobbyFocus(K.SafeAnswer), default));
        Assert.Equal(new[] { (B.Confirm, LobbyTerms.Select, LobbyAction.Answer) }, Bar(S.Failure, new LobbyFocus(K.FirstButton), default));
    }

    // Layout and movement.

    [Fact]
    public void Linear_lists_wrap_and_the_lobby_does_not()
    {
        Assert.True(LobbyRules.Wraps(S.Online));
        Assert.True(LobbyRules.Wraps(S.JoinList));
        Assert.True(LobbyRules.Wraps(S.InvitePanel));
        Assert.False(LobbyRules.Wraps(S.Lobby));
        Assert.False(LobbyRules.Wraps(S.Prompt));

        Assert.Equal(0, LobbyRules.MoveInList(S.JoinList, 2, 3, 1));
        Assert.Equal(2, LobbyRules.MoveInList(S.JoinList, 0, 3, -1));
        Assert.Equal(2, LobbyRules.MoveInList(S.Lobby, 2, 3, 1));
        Assert.Equal(0, LobbyRules.MoveInList(S.Lobby, 0, 3, -1));
        Assert.Equal(-1, LobbyRules.MoveInList(S.JoinList, 0, 0, 1));
    }

    [Fact]
    public void Four_seats_are_vertical_splits_and_more_are_five_by_two()
    {
        Assert.Equal(new SeatLayout(4, 1, 277, 700, 184, 32, 57, true), LobbyRules.LayoutFor(4));
        Assert.Equal(new SeatLayout(4, 1, 277, 700, 184, 32, 57, true), LobbyRules.LayoutFor(2));
        Assert.Equal(new SeatLayout(5, 2, 222, 391, 96, 24, 46, false), LobbyRules.LayoutFor(5));
        Assert.Equal(new SeatLayout(5, 2, 222, 391, 96, 24, 46, false), LobbyRules.LayoutFor(10));
    }

    [Fact]
    public void Focus_moves_across_four_seats_without_wrapping()
    {
        LobbyState state = Host();
        Assert.Equal(new LobbyFocus(K.Seat, 2), LobbyRules.Move(new LobbyFocus(K.Seat, 1), LobbyDirection.Right, state));
        Assert.Equal(new LobbyFocus(K.Seat, 3), LobbyRules.Move(new LobbyFocus(K.Seat, 3), LobbyDirection.Right, state));
        Assert.Equal(new LobbyFocus(K.Seat, 2), LobbyRules.Move(new LobbyFocus(K.Seat, 2), LobbyDirection.Up, state));
        Assert.Equal(new LobbyFocus(K.Seat, 2), LobbyRules.Move(new LobbyFocus(K.Seat, 2), LobbyDirection.Down, state));
    }

    [Fact]
    public void Off_the_seats_left_edge_a_host_reaches_the_rows_and_a_friend_stays()
    {
        Assert.Equal(new LobbyFocus(K.StartRow), LobbyRules.Move(new LobbyFocus(K.Seat, 0), LobbyDirection.Left, Host()));
        Assert.Equal(new LobbyFocus(K.Seat, 0), LobbyRules.Move(new LobbyFocus(K.Seat, 0), LobbyDirection.Left, Friend()));
    }

    [Fact]
    public void Focus_moves_in_a_grid_of_ten()
    {
        LobbyState state = Host(capacity: 10);
        Assert.Equal(new LobbyFocus(K.Seat, 5), LobbyRules.Move(new LobbyFocus(K.Seat, 0), LobbyDirection.Down, state));
        Assert.Equal(new LobbyFocus(K.Seat, 0), LobbyRules.Move(new LobbyFocus(K.Seat, 5), LobbyDirection.Up, state));
        Assert.Equal(new LobbyFocus(K.Seat, 9), LobbyRules.Move(new LobbyFocus(K.Seat, 9), LobbyDirection.Down, state));
        Assert.Equal(new LobbyFocus(K.Seat, 4), LobbyRules.Move(new LobbyFocus(K.Seat, 4), LobbyDirection.Right, state));
    }

    [Fact]
    public void A_friend_skips_seats_they_cant_do_anything_with()
    {
        LobbyBoardMessage board = new LobbyBoardBuilder(HostId).Build(new LobbyBoardInputs
        {
            Host = LobbyStateTests.Person(HostId, "Alex"),
            People = new[] { LobbyStateTests.Person(Sam, "Sam"), LobbyStateTests.Person(Casey, "Riley", SeatStage.Invited) },
        });
        LobbyState state = LobbyState.From(board, new LobbyViewInputs { Role = LobbyRole.Friend, Self = Sam });

        Assert.False(LobbyRules.CanFocusSeat(state, 2));
        Assert.False(LobbyRules.CanFocusSeat(state, 3));
        Assert.False(LobbyRules.CanFocusSeat(state, -1));
        Assert.False(LobbyRules.CanFocusSeat(state, 9));
        Assert.Equal(new LobbyFocus(K.Seat, 1), LobbyRules.Move(new LobbyFocus(K.Seat, 1), LobbyDirection.Right, state));
        Assert.Equal(new LobbyFocus(K.Seat, 0), LobbyRules.Move(new LobbyFocus(K.Seat, 1), LobbyDirection.Left, state));
    }

    [Fact]
    public void A_friend_has_no_rows_to_move_in()
    {
        Assert.Equal(new LobbyFocus(K.ModeRow), LobbyRules.Move(new LobbyFocus(K.ModeRow), LobbyDirection.Down, Friend()));
    }

    [Fact]
    public void The_hosts_column_runs_mode_save_invite_start_and_stops_at_its_ends()
    {
        LobbyState state = Host();
        LobbyFocus at = new(K.ModeRow);
        Assert.Equal(at, LobbyRules.Move(at, LobbyDirection.Up, state));
        at = LobbyRules.Move(at, LobbyDirection.Down, state);
        Assert.Equal(new LobbyFocus(K.SaveRow), at);
        at = LobbyRules.Move(at, LobbyDirection.Down, state);
        Assert.Equal(new LobbyFocus(K.InviteRow), at);
        at = LobbyRules.Move(at, LobbyDirection.Down, state);
        Assert.Equal(new LobbyFocus(K.StartRow), at);
        Assert.Equal(at, LobbyRules.Move(at, LobbyDirection.Down, state));
        Assert.Equal(at, LobbyRules.Move(at, LobbyDirection.Left, state));
        Assert.Equal(new LobbyFocus(K.InviteRow), LobbyRules.Move(at, LobbyDirection.Up, state));
    }

    [Fact]
    public void Quick_game_has_no_save_row_and_a_full_lobby_no_invite_row()
    {
        LobbyState quick = Host(mode: LobbyMode.QuickGame);
        Assert.Equal(new LobbyFocus(K.InviteRow), LobbyRules.Move(new LobbyFocus(K.ModeRow), LobbyDirection.Down, quick));

        LobbyState full = Host(friends: 2, capacity: 3);
        Assert.Equal(new LobbyFocus(K.StartRow), LobbyRules.Move(new LobbyFocus(K.SaveRow), LobbyDirection.Down, full));
    }

    [Fact]
    public void Right_from_the_rows_goes_to_an_empty_seat_first()
    {
        Assert.Equal(new LobbyFocus(K.Seat, 1), LobbyRules.Move(new LobbyFocus(K.StartRow), LobbyDirection.Right, Host()));
        Assert.Equal(new LobbyFocus(K.Seat, 2), LobbyRules.Move(new LobbyFocus(K.StartRow), LobbyDirection.Right, Host(alone: false)));
        Assert.Equal(new LobbyFocus(K.Seat, 0), LobbyRules.Move(new LobbyFocus(K.StartRow), LobbyDirection.Right, Host(friends: 2, capacity: 3)));
    }

    [Fact]
    public void Repair_puts_focus_back_on_something_real()
    {
        Assert.Equal(new LobbyFocus(K.ModeRow), LobbyRules.Repair(new LobbyFocus(K.SaveRow), Host(mode: LobbyMode.QuickGame)));
        Assert.Equal(new LobbyFocus(K.SaveRow), LobbyRules.Repair(new LobbyFocus(K.SaveRow), Host()));
        Assert.Equal(new LobbyFocus(K.StartRow), LobbyRules.Repair(new LobbyFocus(K.InviteRow), Host(friends: 2, capacity: 3)));
        Assert.Equal(new LobbyFocus(K.InviteRow), LobbyRules.Repair(new LobbyFocus(K.InviteRow), Host()));
        Assert.Equal(new LobbyFocus(K.Seat, 1), LobbyRules.Repair(new LobbyFocus(K.Seat, 99), Host()));

        // A friend's focus on a card that emptied: their own card.
        Assert.Equal(new LobbyFocus(K.Seat, 1), LobbyRules.Repair(new LobbyFocus(K.Seat, 3), Friend()));
        Assert.Equal(new LobbyFocus(K.Seat, 1), LobbyRules.Repair(new LobbyFocus(K.ModeRow), Friend()));
    }

    // Start, modes, words.

    [Fact]
    public void Start_runs_the_games_own_path_for_the_mode_and_save()
    {
        var used = new LobbySaveRow(2, true, "squad-a", 2, 5, 0);
        Assert.Equal(LobbyStartKind.QuickGame, LobbyRules.StartKindOf(LobbyMode.QuickGame, used));
        Assert.Equal(LobbyStartKind.ContinueRun, LobbyRules.StartKindOf(LobbyMode.Campaign, used));
        Assert.Equal(LobbyStartKind.NewRun, LobbyRules.StartKindOf(LobbyMode.Campaign, used, newRun: true));
        Assert.Equal(LobbyStartKind.NewRun, LobbyRules.StartKindOf(LobbyMode.Campaign, LobbySaveRow.None));
        Assert.Equal(LobbyTerms.Start, LobbyRules.StartTerm(LobbyStartKind.QuickGame));
        Assert.Equal(LobbyTerms.ContinueRun, LobbyRules.StartTerm(LobbyStartKind.ContinueRun));
        Assert.Equal(LobbyTerms.NewRun, LobbyRules.StartTerm(LobbyStartKind.NewRun));
    }

    [Fact]
    public void Only_two_modes_are_listed_in_the_games_own_words()
    {
        Assert.Equal(LobbyMode.QuickGame, LobbyRules.NextMode(LobbyMode.Campaign));
        Assert.Equal(LobbyMode.Campaign, LobbyRules.NextMode(LobbyMode.QuickGame));
        Assert.Equal("v2ui/title_screen/quick_match", LobbyRules.ModeTerm(LobbyMode.QuickGame));
        Assert.Equal("v2ui/title_screen/campaign", LobbyRules.ModeTerm(LobbyMode.Campaign));
    }

    [Theory]
    [InlineData(LobbyButton.TabRight, LobbyAction.ModeRight)]
    [InlineData(LobbyButton.TabLeft, LobbyAction.ModeLeft)]
    public void L_and_R_each_give_the_other_mode_from_the_one_at_the_press(LobbyButton button, LobbyAction action)
    {
        Assert.Equal(LobbyMode.QuickGame, LobbyRules.ModeAfter(button, action, LobbyMode.Campaign));
        Assert.Equal(LobbyMode.Campaign, LobbyRules.ModeAfter(button, action, LobbyMode.QuickGame));
    }

    [Fact]
    public void Two_presses_back_to_back_go_there_and_back_when_each_reads_the_mode_the_last_one_set()
    {
        // RB then LB (a click on each glyph) inside one redraw of the screen: each press reads the session's mode, not the screen's copy.
        LobbyMode mode = LobbyMode.Campaign;
        mode = LobbyRules.ModeAfter(LobbyButton.TabRight, LobbyAction.ModeRight, mode);
        Assert.Equal(LobbyMode.QuickGame, mode);
        mode = LobbyRules.ModeAfter(LobbyButton.TabLeft, LobbyAction.ModeLeft, mode);
        Assert.Equal(LobbyMode.Campaign, mode);
    }

    [Fact]
    public void The_selectors_own_choice_names_its_mode_whatever_the_mode_is()
    {
        foreach (LobbyMode current in new[] { LobbyMode.Campaign, LobbyMode.QuickGame })
        {
            Assert.Equal(LobbyMode.Campaign, LobbyRules.ModeAfter(LobbyButton.Confirm, LobbyAction.ModeLeft, current));
            Assert.Equal(LobbyMode.QuickGame, LobbyRules.ModeAfter(LobbyButton.Confirm, LobbyAction.ModeRight, current));
        }
    }

    [Fact]
    public void Starts_prompt_names_who_is_not_here()
    {
        Assert.Null(LobbyRules.NotHereLine(Host(alone: false)));
        Assert.Equal("Sam is still loading.", LobbyRules.NotHereLine(Host(alone: false, stage: SeatStage.Loading))!.Text(0));
        Assert.Equal("Sam is still joining.", LobbyRules.NotHereLine(Host(alone: false, stage: SeatStage.Joining))!.Text(0));
        Assert.Equal("Sam is reconnecting.", LobbyRules.NotHereLine(Host(alone: false, stage: SeatStage.Reconnecting))!.Text(0));

        LobbyBoardMessage two = new LobbyBoardBuilder(HostId).Build(new LobbyBoardInputs
        {
            Host = LobbyStateTests.Person(HostId, "Alex"),
            People = new[] { LobbyStateTests.Person(Sam, "Sam", SeatStage.Loading), LobbyStateTests.Person(Casey, "Casey", SeatStage.Joining) },
        });
        Assert.Equal("2 players aren't here yet.", LobbyRules.NotHereLine(LobbyState.From(two, new LobbyViewInputs { Role = LobbyRole.Host }))!.Text(0));
    }

    [Fact]
    public void The_countdown_number_is_three_two_one()
    {
        Assert.Equal(3, LobbyRules.CountdownNumber(3000));
        Assert.Equal(3, LobbyRules.CountdownNumber(2001));
        Assert.Equal(2, LobbyRules.CountdownNumber(2000));
        Assert.Equal(1, LobbyRules.CountdownNumber(1));
        Assert.Equal(0, LobbyRules.CountdownNumber(0));
        Assert.Equal(0, LobbyRules.CountdownNumber(-5));
    }

    [Fact]
    public void The_status_line_says_the_most_urgent_thing()
    {
        Assert.Equal("Reconnecting to Alex...", LobbyRules.StatusLine(Friend(reconnecting: true), "Alex")!.Text(0));
        Assert.Equal("Starting in 3...", LobbyRules.StatusLine(Friend(LobbyPhase.Counting) with { CountdownMs = 2500 }, "Alex")!.Text(0));
        Assert.Equal("Starting...", LobbyRules.StatusLine(Friend(LobbyPhase.Starting), "Alex")!.Text(0));
        Assert.Equal("Waiting for everyone...", LobbyRules.StatusLine(Host(waiting: true), "Alex")!.Text(0));
        Assert.Null(LobbyRules.StatusLine(Host(), "Alex"));
        Assert.Null(LobbyRules.StatusLine(Friend(), "Alex"));
        Assert.Equal("Alex is inviting", LobbyRules.StatusLine(Friend() with { Activity = HostActivity.Inviting }, "Alex")!.Text(0));
        Assert.Null(LobbyRules.StatusLine(Host() with { Activity = HostActivity.Inviting }, "Alex")); // the host knows
    }

    [Fact]
    public void The_band_tells_a_host_alone_the_lobby_is_waiting_for_players()
    {
        Assert.Equal("Waiting for players", LobbyRules.BandLine(Host(), panelOpen: false, out bool news)!.Text(0));
        Assert.False(news);

        // Someone came: the seats say it, and the band has nothing to add.
        Assert.Null(LobbyRules.BandLine(Host(alone: false), panelOpen: false, out _));

        // Only someone invited: nobody's here yet.
        Assert.Equal("Waiting for players", LobbyRules.BandLine(Host(alone: false, stage: SeatStage.Invited), panelOpen: false, out _)!.Text(0));

        // A one-seat lobby has no seat to wait on.
        Assert.Null(LobbyRules.BandLine(Host(capacity: 1), panelOpen: false, out _));

        // A friend's band has the host's status or nothing: their column already says they wait for the host.
        Assert.Null(LobbyRules.BandLine(Friend(), panelOpen: false, out _));
    }

    [Fact]
    public void The_band_puts_the_status_first_then_the_last_game_then_the_wait()
    {
        LobbyState back = Host() with { Result = new LobbyResult(LobbyResultKind.MatchWon, 3, 1) };
        Assert.Equal("Last game: won 3-1", LobbyRules.BandLine(back, panelOpen: false, out bool news)!.Text(0));
        Assert.True(news);

        Assert.Equal("Waiting for everyone...", LobbyRules.BandLine(Host(waiting: true) with { Result = back.Result }, panelOpen: false, out news)!.Text(0));
        Assert.False(news);

        Assert.Equal("Alex is inviting", LobbyRules.BandLine(Friend() with { Activity = HostActivity.Inviting }, panelOpen: false, out _)!.Text(0));
        Assert.Equal("Friends need T2T Online installed.", LobbyRules.BandLine(back, panelOpen: true, out news)!.Text(0));
        Assert.False(news);
    }

    [Fact]
    public void The_band_is_empty_under_the_countdown()
    {
        Assert.Null(LobbyRules.BandLine(Host(phase: LobbyPhase.Counting), panelOpen: false, out _));
        Assert.Null(LobbyRules.BandLine(Host(phase: LobbyPhase.Starting), panelOpen: false, out _));
        Assert.Null(LobbyRules.BandLine(Friend(LobbyPhase.Playing), panelOpen: false, out _));
    }

    private static LobbyState AllInvited()
    {
        LobbyBoardMessage board = new LobbyBoardBuilder(HostId, 3).Build(new LobbyBoardInputs
        {
            Host = LobbyStateTests.Person(HostId, "Alex"),
            People = new[] { LobbyStateTests.Person(Sam, "Sam", SeatStage.Invited), LobbyStateTests.Person(Casey, "Casey", SeatStage.Invited) },
        });
        return LobbyState.From(board, new LobbyViewInputs { Role = LobbyRole.Host, Self = HostId, TitleReady = true });
    }

    [Fact]
    public void An_invited_seat_doesnt_stop_the_host_asking_someone_else()
    {
        LobbyState state = AllInvited();
        Assert.True(LobbyRules.HasRoomToInvite(state));

        // Y, and A on a seat held only by an invite, open the list; the Invite row stays in the column.
        Assert.Contains(InviteAnywhere, Bar(S.Lobby, new LobbyFocus(K.StartRow), Of(state)));
        Assert.Contains((B.Confirm, LobbyWords.Invite.Key, LobbyAction.OpenInvitePanel), Bar(S.Lobby, new LobbyFocus(K.Seat, 1), Of(state)));
        Assert.Equal(new LobbyFocus(K.InviteRow), LobbyRules.Move(new LobbyFocus(K.StartRow), LobbyDirection.Up, state));
        Assert.Equal(new LobbyFocus(K.InviteRow), LobbyRules.Repair(new LobbyFocus(K.InviteRow), state));
    }

    [Fact]
    public void Seats_with_people_in_them_leave_no_room_to_invite()
    {
        Assert.False(LobbyRules.HasRoomToInvite(Host(friends: 2, capacity: 3)));
        Assert.False(LobbyRules.HasRoomToInvite(Host(friends: 2, capacity: 3, stage: SeatStage.Reconnecting)));
        Assert.True(LobbyRules.HasRoomToInvite(Host(friends: 2, capacity: 4)));
    }

    [Fact]
    public void A_row_still_checking_offers_no_a_at_all()
    {
        (B, string, LobbyAction)[] bar = Bar(S.JoinList, new LobbyFocus(K.JoinRow), new LobbyScreenFacts { FocusedRowChecking = true });

        // "Why?" would answer with a grunt: there's nothing to say yet.
        Assert.DoesNotContain(bar, p => p.Item1 == B.Confirm);
        Assert.Contains(bar, p => p.Item3 == LobbyAction.Refresh);
    }

    private static (LobbyBackAction, bool, S, K) Row(LobbyBackRule rule) => (rule.Action, rule.Asks, rule.Landing, rule.Focus.Kind);
}
