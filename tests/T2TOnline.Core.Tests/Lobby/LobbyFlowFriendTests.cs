using System.Collections.Generic;
using T2TOnline.Core.Lobby;
using T2TOnline.Core.Protocol;
using E = T2TOnline.Core.Lobby.LobbyEffect;
using Ev = T2TOnline.Core.Lobby.LobbyEvent;
using F = T2TOnline.Core.Lobby.LobbyFriendState;

namespace T2TOnline.Core.Tests.Lobby;

/// <summary>A friend's flow in time: their two steps, mid-game joins, drops in each state, a host closing in each state, the return.</summary>
public class LobbyFlowFriendTests
{
    private const long Second = 1_000_000;

    [Fact]
    public void Joining_changes_nothing_of_theirs_until_the_board_says_starting()
    {
        var flow = new LobbyFlow();
        Assert.Equal(new[] { E.JoinLobby }, flow.Handle(Ev.JoinPressed, 0));
        Assert.Equal(new[] { E.SayHello }, flow.Handle(Ev.LobbyEntered, 0));
        Assert.Equal(new[] { E.ShowLobby, E.SendMember }, flow.Handle(Ev.WelcomedLobby, 0));
        Assert.False(flow.PlayActive);
        Assert.Equal(LobbyMemberStage.InLobby, flow.MemberStage);

        // The countdown can be cancelled: nothing of theirs moves for it.
        Assert.Empty(flow.Handle(Ev.BoardCounting, Second, epoch: 1));
        Assert.Empty(flow.Handle(Ev.BoardOpen, Second, epoch: 1));
        Assert.False(flow.PlayActive);

        Assert.Equal(new[] { E.SendMember, E.BeginPlay }, flow.Handle(Ev.BoardStarting, Second, epoch: 1));
        Assert.True(flow.PlayActive);
        Assert.Equal(LobbyMemberStage.Preparing, flow.MemberStage);
        Assert.Equal(0u, flow.AckedEpoch); // not yet

        Assert.Equal(new[] { E.SendMember, E.CloseLobbyForStart }, flow.Handle(Ev.PlayReady, 2 * Second));
        Assert.Equal(1u, flow.AckedEpoch);
        Assert.Equal(LobbyMemberStage.Playing, flow.MemberStage);
    }

    [Fact]
    public void A_mid_game_joiner_follows_at_once_and_acks_the_epoch_when_the_board_comes()
    {
        var flow = new LobbyFlow();
        flow.Handle(Ev.JoinPressed, 0);
        flow.Handle(Ev.LobbyEntered, 0);

        Assert.Equal(new[] { E.SendMember, E.BeginPlay }, flow.Handle(Ev.WelcomedPlaying, 0));
        Assert.Equal(F.Preparing, flow.Friend);

        // Ready before the board's read: no epoch known yet. The board that follows names it, and that's the ack. No lobby
        // screen ever showed: the Join list they came from closes, rather than stay up under the locked UI.
        Assert.Equal(new[] { E.SendMember, E.CloseJoinList }, flow.Handle(Ev.PlayReady, 0));
        Assert.Equal(0u, flow.AckedEpoch);
        Assert.Equal(new[] { E.SendMember }, flow.Handle(Ev.BoardPlaying, 0, epoch: 4));
        Assert.Equal(4u, flow.AckedEpoch);
        Assert.Empty(flow.Handle(Ev.BoardPlaying, Second, epoch: 4)); // the same again
    }

    [Fact]
    public void Only_a_friend_with_no_lobby_screen_up_closes_the_join_list_when_play_begins()
    {
        // From the lobby: its screen is handed over to the game's flow, as at every Start; the Join list isn't touched.
        LobbyFlow fromLobby = LobbyFlowTableTests.FriendAt(F.Preparing);
        Assert.DoesNotContain(E.CloseJoinList, fromLobby.Handle(Ev.PlayReady, Second));

        // Welcomed back into a running game after a drop from the lobby: the lobby screen is still theirs, handed over too.
        LobbyFlow lostFromLobby = LobbyFlowTableTests.FriendAt(F.Lost);
        lostFromLobby.Handle(Ev.WelcomedPlaying, Second);
        Assert.Equal(new[] { E.SendMember, E.CloseLobbyForStart }, lostFromLobby.Handle(Ev.PlayReady, Second));

        // Straight from the Join list (or an invite) into a running game: the list goes once they follow, not before (a failed
        // start lands on it again).
        var midGame = new LobbyFlow();
        midGame.Handle(Ev.JoinPressed, 0);
        midGame.Handle(Ev.LobbyEntered, 0);
        Assert.DoesNotContain(E.CloseJoinList, midGame.Handle(Ev.WelcomedPlaying, 0));
        Assert.Equal(new[] { E.SendMember, E.CloseJoinList }, midGame.Handle(Ev.PlayReady, 0));

        var failed = new LobbyFlow();
        failed.Handle(Ev.JoinPressed, 0);
        failed.Handle(Ev.LobbyEntered, 0);
        failed.Handle(Ev.WelcomedPlaying, 0);
        IReadOnlyList<E> effects = failed.Handle(Ev.PlayFailed, 0);
        Assert.DoesNotContain(E.CloseJoinList, effects);
        Assert.Contains(E.ShowJoinList, effects);
    }

    [Fact]
    public void A_board_read_while_preparing_gives_the_ack_its_epoch()
    {
        var flow = new LobbyFlow();
        flow.Handle(Ev.JoinPressed, 0);
        flow.Handle(Ev.LobbyEntered, 0);
        flow.Handle(Ev.WelcomedPlaying, 0);
        Assert.Empty(flow.Handle(Ev.BoardPlaying, 0, epoch: 4));

        flow.Handle(Ev.PlayReady, 0);

        Assert.Equal(4u, flow.AckedEpoch);
    }

    [Fact]
    public void Welcome_phases_read_as_the_flow_wants_them()
    {
        Assert.Equal(Ev.WelcomedLobby, LobbyFlow.WelcomeEvent(SessionPhase.Lobby));
        Assert.Equal(Ev.WelcomedPlaying, LobbyFlow.WelcomeEvent(SessionPhase.Playing));

        // A host with no flow says Unknown: today's behaviour, follow at once.
        Assert.Equal(Ev.WelcomedPlaying, LobbyFlow.WelcomeEvent(SessionPhase.Unknown));
        Assert.Equal(Ev.WelcomedPlaying, LobbyFlow.WelcomeEvent(SessionPhase.Menus));
        Assert.Equal(Ev.WelcomedPlaying, LobbyFlow.WelcomeEvent(SessionPhase.Match));
        Assert.Equal(Ev.WelcomedPlaying, LobbyFlow.WelcomeEvent((SessionPhase)200));
    }

    [Fact]
    public void Board_phases_read_as_the_flow_wants_them()
    {
        Assert.Equal(Ev.BoardOpen, LobbyFlow.BoardEvent(LobbyPhase.Open));
        Assert.Equal(Ev.BoardCounting, LobbyFlow.BoardEvent(LobbyPhase.Counting));
        Assert.Equal(Ev.BoardStarting, LobbyFlow.BoardEvent(LobbyPhase.Starting));
        Assert.Equal(Ev.BoardPlaying, LobbyFlow.BoardEvent(LobbyPhase.Playing));
        Assert.Equal(Ev.BoardOpen, LobbyFlow.BoardEvent(LobbyPhase.Returning));
    }

    [Fact]
    public void The_host_backing_out_ends_the_play_period_when_their_game_is_home()
    {
        LobbyFlow flow = LobbyFlowTableTests.FriendAt(F.Playing);

        Assert.Equal(new[] { E.SendMember }, flow.Handle(Ev.BoardOpen, Second, epoch: 1));
        Assert.Equal(F.Returning, flow.Friend);
        Assert.True(flow.PlayActive);
        Assert.Equal(LobbyMemberStage.Returning, flow.MemberStage);

        // Level-triggered: the follower says so each tick it's so, and EndPlay comes once.
        Assert.Equal(new[] { E.EndPlay, E.ShowLobby, E.SendMember }, flow.Handle(Ev.OwnTitleBack, 2 * Second));
        Assert.Empty(flow.Handle(Ev.OwnTitleBack, 2 * Second));
        Assert.False(flow.PlayActive);
        Assert.Equal(F.InLobby, flow.Friend);
        Assert.Equal(LobbyMemberStage.InLobby, flow.MemberStage);
        Assert.True(flow.BackFromGame);
    }

    [Fact]
    public void Own_title_back_before_the_board_waits_for_the_board()
    {
        // The follower reaches the title on the host's view; the board may come a moment after. Nothing ends until it does.
        LobbyFlow flow = LobbyFlowTableTests.FriendAt(F.Playing);

        Assert.Empty(flow.Handle(Ev.OwnTitleBack, Second));
        Assert.Equal(F.Playing, flow.Friend);

        flow.Handle(Ev.BoardOpen, 2 * Second, epoch: 1);
        Assert.Contains(E.EndPlay, flow.Handle(Ev.OwnTitleBack, 2 * Second));
    }

    [Fact]
    public void The_input_lock_after_a_return_lasts_half_a_second_on_a_friend_too()
    {
        LobbyFlow flow = LobbyFlowTableTests.FriendAt(F.Returning);
        flow.Handle(Ev.OwnTitleBack, 10 * Second);

        Assert.True(flow.IsInputLocked((10 * Second) + LobbyTiming.InputLockMicros - 1));
        Assert.False(flow.IsInputLocked((10 * Second) + LobbyTiming.InputLockMicros));
    }

    [Fact]
    public void A_rematch_is_the_same_play_period_with_no_begin_or_end()
    {
        LobbyFlow flow = LobbyFlowTableTests.FriendAt(F.Playing);

        // Rematch: the host stays in the game, and only a new board's epoch moves (which it doesn't here): nothing changes.
        Assert.Empty(flow.Handle(Ev.BoardPlaying, Second, epoch: 1));
        Assert.Equal(F.Playing, flow.Friend);
        Assert.True(flow.PlayActive);
    }

    [Fact]
    public void The_host_starting_again_before_they_got_home_goes_on_with_no_backup_or_handover()
    {
        LobbyFlow flow = LobbyFlowTableTests.FriendAt(F.Returning);

        Assert.Equal(new[] { E.SendMember }, flow.Handle(Ev.BoardStarting, Second, epoch: 2));

        Assert.Equal(F.Playing, flow.Friend);
        Assert.Equal(2u, flow.AckedEpoch);
        Assert.True(flow.PlayActive);
    }

    [Fact]
    public void A_back_out_while_still_preparing_waits_for_ready_so_end_play_cannot_overtake_begin_play()
    {
        LobbyFlow flow = LobbyFlowTableTests.FriendAt(F.Preparing);

        Assert.Empty(flow.Handle(Ev.BoardOpen, Second, epoch: 1));
        Assert.Equal(F.Preparing, flow.Friend);
        Assert.Empty(flow.Handle(Ev.OwnTitleBack, Second));

        Assert.Equal(new[] { E.SendMember }, flow.Handle(Ev.PlayReady, 2 * Second));
        Assert.Equal(F.Returning, flow.Friend);

        // Its lobby never closed: the play ends, with nothing to reopen.
        Assert.Equal(new[] { E.EndPlay, E.SendMember }, flow.Handle(Ev.OwnTitleBack, 2 * Second));
    }

    [Fact]
    public void A_back_out_then_a_new_start_while_preparing_stays_a_start()
    {
        LobbyFlow flow = LobbyFlowTableTests.FriendAt(F.Preparing);
        flow.Handle(Ev.BoardOpen, Second, epoch: 1);
        flow.Handle(Ev.BoardStarting, Second, epoch: 2);

        Assert.Equal(new[] { E.SendMember, E.CloseLobbyForStart }, flow.Handle(Ev.PlayReady, 2 * Second));
        Assert.Equal(F.Playing, flow.Friend);
        Assert.Equal(2u, flow.AckedEpoch);
    }

    [Fact]
    public void Drops_get_a_window_by_where_they_were_and_it_runs_out_by_itself()
    {
        // 40 s from a lobby, 2 minutes from a game; Update raises what RejoinExpired would.
        LobbyFlow lobby = LobbyFlowTableTests.FriendAt(F.InLobby);
        lobby.Handle(Ev.LinkLost, 0);
        Assert.Equal(40, lobby.RejoinSecondsLeft(0));
        Assert.Empty(lobby.Handle(Ev.LinkLost, 30 * Second)); // a level: the window doesn't start over
        Assert.Empty(lobby.Update((40 * Second) - 1));
        Assert.Equal(new[] { E.HideLobby, E.EndSession, E.SayLost, E.ShowJoinList }, lobby.Update(40 * Second));
        Assert.Equal(F.Idle, lobby.Friend);
        Assert.Equal(0, lobby.RejoinSecondsLeft(40 * Second));

        LobbyFlow game = LobbyFlowTableTests.FriendAt(F.Playing);
        game.Handle(Ev.LinkLost, 0);
        Assert.Equal(120, game.RejoinSecondsLeft(0));
        Assert.Equal(new[] { E.EndPlay, E.EndSession, E.SayLost, E.ShowJoinList }, game.Update(120 * Second));
    }

    [Theory]
    [InlineData(F.InLobby)]
    [InlineData(F.Playing)]
    [InlineData(F.Returning)]
    public void Back_from_a_drop_they_are_where_they_were(F state)
    {
        LobbyFlow flow = LobbyFlowTableTests.FriendAt(state);
        bool playing = flow.PlayActive;
        flow.Handle(Ev.LinkLost, Second);

        Assert.Equal(new[] { E.SendMember }, flow.Handle(Ev.LinkBack, 2 * Second));

        Assert.Equal(state, flow.Friend);
        Assert.Equal(playing, flow.PlayActive);
        Assert.Equal(0, flow.RejoinSecondsLeft(2 * Second));
    }

    [Fact]
    public void A_drop_while_preparing_keeps_its_begin_play_and_its_ready_counts()
    {
        LobbyFlow flow = LobbyFlowTableTests.FriendAt(F.Preparing);
        flow.Handle(Ev.LinkLost, Second);
        Assert.True(flow.PlayActive);
        Assert.Equal(LobbyMemberStage.Preparing, flow.MemberStage);

        Assert.Empty(flow.Handle(Ev.PlayReady, 2 * Second));
        Assert.Equal(LobbyMemberStage.Playing, flow.MemberStage);
        Assert.Equal(1u, flow.AckedEpoch);

        Assert.Equal(new[] { E.SendMember }, flow.Handle(Ev.LinkBack, 3 * Second));
        Assert.Equal(F.Playing, flow.Friend);
    }

    [Fact]
    public void A_drop_while_preparing_with_a_failure_ends_the_play()
    {
        LobbyFlow flow = LobbyFlowTableTests.FriendAt(F.Preparing);
        flow.Handle(Ev.LinkLost, Second);

        Assert.Contains(E.EndPlay, flow.Handle(Ev.PlayFailed, 2 * Second));
        Assert.Equal(F.Idle, flow.Friend);
    }

    [Fact]
    public void A_board_that_arrives_while_lost_proves_the_link_and_goes_on_from_it()
    {
        // Lost in a game, and the host is back at its lobby by the time the link is: home, with no second BeginPlay.
        LobbyFlow flow = LobbyFlowTableTests.FriendAt(F.Playing);
        flow.Handle(Ev.LinkLost, Second);

        Assert.Equal(new[] { E.SendMember }, flow.Handle(Ev.BoardOpen, 2 * Second, epoch: 1));

        Assert.Equal(F.Returning, flow.Friend);
        Assert.True(flow.PlayActive);
    }

    [Fact]
    public void Lost_in_a_game_and_welcomed_to_the_lobby_comes_home()
    {
        LobbyFlow flow = LobbyFlowTableTests.FriendAt(F.Playing);
        flow.Handle(Ev.LinkLost, Second);

        Assert.Equal(new[] { E.SendMember }, flow.Handle(Ev.WelcomedLobby, 2 * Second));

        Assert.Equal(F.Returning, flow.Friend);
    }

    [Fact]
    public void Lost_in_a_game_and_welcomed_to_it_goes_on_with_no_second_begin_play()
    {
        LobbyFlow flow = LobbyFlowTableTests.FriendAt(F.Playing);
        flow.Handle(Ev.LinkLost, Second);

        Assert.Equal(new[] { E.SendMember }, flow.Handle(Ev.WelcomedPlaying, 2 * Second));

        Assert.Equal(F.Playing, flow.Friend);
    }

    [Fact]
    public void Lost_while_coming_home_and_welcomed_to_a_new_game_goes_on_following()
    {
        LobbyFlow flow = LobbyFlowTableTests.FriendAt(F.Returning);
        flow.Handle(Ev.LinkLost, Second);

        Assert.Equal(new[] { E.SendMember }, flow.Handle(Ev.WelcomedPlaying, 2 * Second));
        Assert.Equal(F.Playing, flow.Friend);
    }

    [Fact]
    public void Lost_before_the_first_welcome_is_a_first_join_that_can_still_land()
    {
        // A first join can time out while the host loads, and comes in through the rejoin path.
        LobbyFlow flow = LobbyFlowTableTests.FriendAt(F.Greeting);
        flow.Handle(Ev.LinkLost, Second);
        Assert.Equal(40, flow.RejoinSecondsLeft(Second));

        Assert.Equal(new[] { E.ShowLobby, E.SendMember }, flow.Handle(Ev.WelcomedLobby, 2 * Second));
        Assert.Equal(F.InLobby, flow.Friend);
    }

    [Fact]
    public void Link_back_before_any_welcome_sends_nothing_about_a_stage_it_has_not_got()
    {
        LobbyFlow flow = LobbyFlowTableTests.FriendAt(F.Greeting);
        flow.Handle(Ev.LinkLost, Second);

        Assert.Empty(flow.Handle(Ev.LinkBack, 2 * Second));
        Assert.Equal(F.Greeting, flow.Friend);
    }

    [Fact]
    public void A_host_closing_in_each_state_lands_on_the_join_list_with_end_play_first_where_it_ran()
    {
        foreach (F state in new[] { F.JoiningLobby, F.Greeting, F.InLobby, F.Preparing, F.Playing, F.Returning, F.Lost })
        {
            LobbyFlow flow = LobbyFlowTableTests.FriendAt(state);
            bool playing = flow.PlayActive;

            IReadOnlyList<E> effects = flow.Handle(Ev.SessionEnded, 9 * Second);

            Assert.Equal(F.Idle, flow.Friend);
            Assert.False(flow.PlayActive);
            Assert.Equal(E.ShowJoinList, effects[^1]);
            Assert.Contains(E.ShowFailure, effects);
            Assert.Contains(E.EndSession, effects);
            Assert.Equal(playing, effects[0] == E.EndPlay);
            Assert.Equal(0u, flow.AckedEpoch);
        }
    }

    [Fact]
    public void Leaving_ends_the_session_and_starts_clean_next_time()
    {
        LobbyFlow flow = LobbyFlowTableTests.FriendAt(F.Playing);
        flow.Handle(Ev.LeavePressed, Second);

        flow.Handle(Ev.JoinPressed, 2 * Second);
        flow.Handle(Ev.LobbyEntered, 2 * Second);
        flow.Handle(Ev.WelcomedLobby, 2 * Second);

        Assert.Equal(0u, flow.AckedEpoch);
        Assert.Equal(0u, flow.Epoch);
        Assert.False(flow.BackFromGame);
        Assert.False(flow.IsInputLocked(2 * Second));
    }

    [Fact]
    public void Back_from_game_lasts_until_the_next_countdown()
    {
        LobbyFlow flow = LobbyFlowTableTests.FriendAt(F.Returning);
        flow.Handle(Ev.OwnTitleBack, Second);
        Assert.True(flow.BackFromGame);

        flow.Handle(Ev.BoardOpen, 2 * Second, epoch: 1);
        Assert.True(flow.BackFromGame);
        flow.Handle(Ev.BoardCounting, 3 * Second, epoch: 2);
        Assert.False(flow.BackFromGame);
    }

    [Fact]
    public void Member_messages_say_where_they_were_while_lost()
    {
        LobbyFlow flow = LobbyFlowTableTests.FriendAt(F.Returning);
        flow.Handle(Ev.LinkLost, Second);
        Assert.Equal(new LobbyMemberMessage(true, LobbyMemberStage.Returning, 1), flow.Member(ready: true));
    }
}
