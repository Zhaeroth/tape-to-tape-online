using System.Linq;
using T2TOnline.Core.Lobby;
using T2TOnline.Core.Protocol;
using E = T2TOnline.Core.Lobby.LobbyEffect;
using Ev = T2TOnline.Core.Lobby.LobbyEvent;
using H = T2TOnline.Core.Lobby.LobbyHostState;

namespace T2TOnline.Core.Tests.Lobby;

/// <summary>The host's flow in time: the countdown, the barrier, the back-out, implicit Start, the return, epochs.</summary>
public class LobbyFlowHostTests
{
    private const ulong Sam = 7;
    private const ulong Alex = 8;
    private const long Second = 1_000_000;

    private static LobbyFlow Open(params ulong[] friends)
    {
        var flow = new LobbyFlow();
        flow.Handle(Ev.HostPressed, 0);
        flow.Handle(Ev.LobbyCreated, 0);
        foreach (ulong friend in friends)
        {
            flow.Handle(Ev.FriendArrived, 0, peer: friend);
        }

        return flow;
    }

    /// <summary>A game played and back at the lobby, the last backup long past.</summary>
    private static LobbyFlow BackAtLobby(long now, params ulong[] friends)
    {
        LobbyFlow flow = Open(friends);
        flow.Handle(Ev.StartPressed, now);
        flow.Update(now + LobbyTiming.CountdownMicros);
        foreach (ulong friend in friends)
        {
            flow.Handle(Ev.FriendAcked, now + LobbyTiming.CountdownMicros, flow.Epoch, friend);
        }

        flow.Handle(Ev.SceneLeftTitle, now + (5 * Second));
        flow.Handle(Ev.TitleSceneLoaded, now + (10 * Second));
        flow.Handle(Ev.TitleReady, now + (11 * Second));
        Assert.Equal(H.Open, flow.Host);
        return flow;
    }

    [Fact]
    public void The_countdown_runs_three_seconds_on_update_then_the_barrier_starts()
    {
        LobbyFlow flow = Open(Sam);
        Assert.Equal(new[] { E.PublishBoard }, flow.Handle(Ev.StartPressed, 10 * Second));

        Assert.Equal(H.Counting, flow.Host);
        Assert.Equal(LobbyPhase.Counting, flow.BoardPhase);
        Assert.Equal(1u, flow.Epoch);
        Assert.Equal(3000, flow.CountdownMsLeft(10 * Second));
        Assert.Equal(1000, flow.CountdownMsLeft(12 * Second));
        Assert.Empty(flow.Update((13 * Second) - 1));
        Assert.Equal(1, flow.CountdownMsLeft((13 * Second) - 1));

        // No backup on the first Start: hosting just took one. Friends get ready, and the game isn't called yet.
        Assert.Equal(new[] { E.PublishBoard }, flow.Update(13 * Second));
        Assert.Equal(H.Starting, flow.Host);
        Assert.Equal(LobbyPhase.Starting, flow.BoardPhase);
        Assert.Equal(0, flow.CountdownMsLeft(13 * Second));
    }

    [Fact]
    public void Cancel_works_only_during_the_countdown_and_the_next_start_is_a_new_epoch()
    {
        LobbyFlow flow = Open(Sam);
        flow.Handle(Ev.StartPressed, 0);
        Assert.Equal(new[] { E.PublishBoard }, flow.Handle(Ev.CancelPressed, Second));
        Assert.Equal(H.Open, flow.Host);
        Assert.Equal(LobbyPhase.Open, flow.BoardPhase);
        Assert.Empty(flow.Update(10 * Second)); // the countdown it would have ended with is gone

        flow.Handle(Ev.StartPressed, 10 * Second);
        Assert.Equal(2u, flow.Epoch);
        flow.Update(13 * Second);
        Assert.Equal(H.Starting, flow.Host);

        // Too late: friends are getting ready.
        Assert.Empty(flow.Handle(Ev.CancelPressed, 13 * Second));
        Assert.Equal(H.Starting, flow.Host);
    }

    [Fact]
    public void With_no_friend_start_goes_straight_to_the_game_in_order()
    {
        LobbyFlow flow = Open();
        flow.Handle(Ev.StartPressed, 0);

        Assert.Equal(new[] { E.PublishBoard, E.AssignGamePlayers, E.CallGameStart, E.CloseLobbyForStart }, flow.Update(3 * Second));
        Assert.Equal(H.GameFlow, flow.Host);
        Assert.Equal(LobbyPhase.Playing, flow.BoardPhase);
    }

    [Fact]
    public void The_barrier_waits_for_every_friend_and_the_game_starts_after_the_last_ack()
    {
        LobbyFlow flow = Open(Sam, Alex);
        flow.Handle(Ev.StartPressed, 0);
        flow.Update(3 * Second);
        Assert.Equal(new[] { Sam, Alex }, flow.AwaitingAcks.OrderBy(id => id));

        Assert.Equal(new[] { E.FriendFollowing }, flow.Handle(Ev.FriendAcked, (3 * Second) + 412_000, flow.Epoch, Sam));
        Assert.Equal(412_000, flow.LastAckAfterMicros);
        Assert.Equal(H.Starting, flow.Host);
        Assert.Equal(new[] { Alex }, flow.AwaitingAcks);

        Assert.Equal(new[] { E.FriendFollowing, E.PublishBoard, E.AssignGamePlayers, E.CallGameStart, E.CloseLobbyForStart }, flow.Handle(Ev.FriendAcked, 4 * Second, flow.Epoch, Alex));
        Assert.Equal(H.GameFlow, flow.Host);
        Assert.Empty(flow.AwaitingAcks);
    }

    [Fact]
    public void The_barrier_gives_up_after_three_seconds_and_who_missed_it_catches_up_when_they_ack()
    {
        LobbyFlow flow = Open(Sam, Alex);
        flow.Handle(Ev.StartPressed, 0);
        flow.Update(3 * Second);
        flow.Handle(Ev.FriendAcked, 4 * Second, flow.Epoch, Sam);

        Assert.Empty(flow.Update((6 * Second) - 1));
        Assert.Equal(new[] { E.PublishBoard, E.AssignGamePlayers, E.CallGameStart, E.CloseLobbyForStart }, flow.Update(6 * Second));
        Assert.Equal(H.GameFlow, flow.Host);
        Assert.Equal(new[] { Alex }, flow.AwaitingAcks); // who missed it, for the log

        Assert.False(flow.IsFollowing(Alex));
        Assert.Equal(new[] { E.FriendFollowing }, flow.Handle(Ev.FriendAcked, 9 * Second, flow.Epoch, Alex));
        Assert.True(flow.IsFollowing(Alex));
        Assert.True(flow.IsFollowing(Sam));
        Assert.Empty(flow.Handle(Ev.FriendAcked, 10 * Second, flow.Epoch, Alex)); // once
    }

    [Fact]
    public void A_friend_who_rejoins_long_after_the_start_is_timed_from_their_own_arrival()
    {
        LobbyFlow flow = Open(Sam, Alex);
        flow.Handle(Ev.StartPressed, 0);
        flow.Update(3 * Second);
        flow.Handle(Ev.FriendAcked, (3 * Second) + 412_000, flow.Epoch, Sam);
        Assert.False(flow.LastAckFromArrival);

        // Alex drops an hour into the game and is back at once: their ack counts from their hello, not from the Start.
        long hour = 3_600 * Second;
        flow.Handle(Ev.FriendGone, hour, peer: Alex);
        flow.Handle(Ev.FriendArrived, hour + Second, peer: Alex);
        Assert.Equal(new[] { E.FriendFollowing }, flow.Handle(Ev.FriendAcked, hour + Second + 325_000, flow.Epoch, Alex));
        Assert.Equal(325_000, flow.LastAckAfterMicros);
        Assert.True(flow.LastAckFromArrival);

        // A friend there from the start is timed from the barrier's start, as before.
        LobbyFlow other = Open(Sam);
        other.Handle(Ev.StartPressed, 0);
        other.Update(3 * Second);
        other.Handle(Ev.FriendAcked, 4 * Second, other.Epoch, Sam);
        Assert.Equal(Second, other.LastAckAfterMicros);
        Assert.False(other.LastAckFromArrival);
    }

    [Fact]
    public void A_friend_leaving_during_the_barrier_stops_it_waiting_for_them()
    {
        LobbyFlow flow = Open(Sam, Alex);
        flow.Handle(Ev.StartPressed, 0);
        flow.Update(3 * Second);
        flow.Handle(Ev.FriendAcked, 3 * Second, flow.Epoch, Sam);

        Assert.Equal(new[] { E.PublishBoard, E.AssignGamePlayers, E.CallGameStart, E.CloseLobbyForStart }, flow.Handle(Ev.FriendGone, 4 * Second, peer: Alex));
        Assert.Equal(H.GameFlow, flow.Host);
    }

    [Fact]
    public void A_friend_arriving_during_the_barrier_does_not_hold_the_others()
    {
        LobbyFlow flow = Open(Sam);
        flow.Handle(Ev.StartPressed, 0);
        flow.Update(3 * Second);
        flow.Handle(Ev.FriendArrived, 3 * Second, peer: Alex);
        Assert.Equal(SessionPhase.Playing, flow.WelcomePhase); // Alex's welcome: follow at once

        flow.Handle(Ev.FriendAcked, 3 * Second, flow.Epoch, Sam);

        Assert.Equal(H.GameFlow, flow.Host);
        Assert.Equal(new[] { E.FriendFollowing }, flow.Handle(Ev.FriendAcked, 4 * Second, flow.Epoch, Alex));
    }

    [Fact]
    public void A_stale_ack_cannot_confuse_a_later_start()
    {
        LobbyFlow flow = Open(Sam);
        flow.Handle(Ev.StartPressed, 0);
        flow.Update(3 * Second);
        uint first = flow.Epoch;
        flow.Handle(Ev.FriendAcked, 3 * Second, first, Sam);
        Assert.Equal(H.GameFlow, flow.Host);

        // Backed out, and started again: Sam's ack of the first Start, or a repeat of it in a member message, counts for nothing.
        flow.Handle(Ev.TitleIdle, 4 * Second);
        flow.Update(5 * Second);
        Assert.Equal(H.Open, flow.Host);
        Assert.Empty(flow.Handle(Ev.FriendAcked, 6 * Second, first, Sam));
        flow.Handle(Ev.StartPressed, 7 * Second);
        Assert.Equal(first + 1, flow.Epoch);
        flow.Update(10 * Second);
        Assert.Equal(H.Starting, flow.Host);

        Assert.Empty(flow.Handle(Ev.FriendAcked, 10 * Second, first, Sam));
        Assert.Equal(H.Starting, flow.Host);
        Assert.Equal(new[] { Sam }, flow.AwaitingAcks);
        Assert.False(flow.IsFollowing(Sam));

        Assert.Equal(new[] { E.FriendFollowing, E.PublishBoard, E.AssignGamePlayers, E.CallGameStart, E.CloseLobbyForStart }, flow.Handle(Ev.FriendAcked, 11 * Second, first + 1, Sam));
    }

    [Fact]
    public void An_ack_from_no_one_of_ours_or_of_epoch_0_does_nothing()
    {
        LobbyFlow flow = Open(Sam);
        Assert.Empty(flow.Handle(Ev.FriendAcked, 0, 0, Sam)); // no Start yet, and a member message before one says 0
        flow.Handle(Ev.StartPressed, 0);
        flow.Update(3 * Second);

        Assert.Empty(flow.Handle(Ev.FriendAcked, 3 * Second, flow.Epoch, Alex));
        Assert.Empty(flow.Handle(Ev.FriendAcked, 3 * Second, flow.Epoch + 1, Sam));
        Assert.Empty(flow.Handle(Ev.FriendAcked, 3 * Second, 0, Sam));
        Assert.Equal(H.Starting, flow.Host);
    }

    [Fact]
    public void A_friend_who_said_hello_again_must_ack_again()
    {
        LobbyFlow flow = Open(Sam);
        flow.Handle(Ev.StartPressed, 0);
        flow.Update(3 * Second);
        flow.Handle(Ev.FriendAcked, 3 * Second, flow.Epoch, Sam);
        Assert.True(flow.IsFollowing(Sam));

        // Rejoined: their game may have restarted, and what's sent before they follow is dropped. Their member message after the welcome says.
        flow.Handle(Ev.FriendArrived, 9 * Second, peer: Sam);

        Assert.False(flow.IsFollowing(Sam));
        Assert.Equal(new[] { E.FriendFollowing }, flow.Handle(Ev.FriendAcked, 10 * Second, flow.Epoch, Sam));
    }

    [Fact]
    public void Back_out_needs_the_title_idle_for_a_whole_second()
    {
        LobbyFlow flow = Open();
        flow.Handle(Ev.StartPressed, 0);
        flow.Update(3 * Second);
        Assert.Equal(H.GameFlow, flow.Host);

        // Idle, then busy again before the second is out: a menu closing and the next one opening.
        Assert.Empty(flow.Handle(Ev.TitleIdle, 4 * Second));
        Assert.Empty(flow.Update((5 * Second) - 1));
        Assert.Empty(flow.Handle(Ev.TitleBusy, (5 * Second) - 1));
        Assert.Empty(flow.Update(6 * Second));
        Assert.Equal(H.GameFlow, flow.Host);

        // Held for the second: repeating TitleIdle each tick doesn't restart it.
        Assert.Empty(flow.Handle(Ev.TitleIdle, 7 * Second));
        Assert.Empty(flow.Handle(Ev.TitleIdle, (8 * Second) - 1));
        Assert.Equal(new[] { E.ShowLobby, E.PublishBoard }, flow.Update(8 * Second));
        Assert.Equal(H.Open, flow.Host);
        Assert.Equal(LobbyPhase.Open, flow.BoardPhase);

        while (flow.TryTakeChange(out LobbyFlowChange change))
        {
            if (change.To == "Open" && change.From == "GameFlow")
            {
                Assert.Contains("backed out", change.Why);
                return;
            }
        }

        Assert.Fail("The back-out wasn't told to the log.");
    }

    [Fact]
    public void A_start_the_game_refused_comes_back_as_a_back_out()
    {
        LobbyFlow flow = Open();
        flow.Handle(Ev.StartPressed, 0);
        flow.Update(3 * Second);

        // The title never went busy: the first idle report starts the second.
        flow.Handle(Ev.TitleIdle, 3 * Second);
        Assert.Equal(new[] { E.ShowLobby, E.PublishBoard }, flow.Handle(Ev.TitleIdle, 4 * Second));
        Assert.Equal(H.Open, flow.Host);
    }

    [Fact]
    public void Implicit_start_from_the_lobby_has_every_friend_follow_with_no_call_of_the_game()
    {
        LobbyFlow flow = Open(Sam);

        Assert.Equal(new[] { E.PublishBoard, E.AssignGamePlayers, E.CloseLobbyForStart }, flow.Handle(Ev.TitleBusy, Second));

        Assert.Equal(H.GameFlow, flow.Host);
        Assert.True(flow.IsImplicitStart);
        Assert.Equal(1u, flow.Epoch);
        Assert.Equal(SessionPhase.Playing, flow.WelcomePhase);
        Assert.Equal(new[] { E.FriendFollowing }, flow.Handle(Ev.FriendAcked, 2 * Second, flow.Epoch, Sam));
    }

    [Fact]
    public void Implicit_start_during_the_countdown_cancels_it_and_keeps_its_epoch()
    {
        LobbyFlow flow = Open(Sam);
        flow.Handle(Ev.StartPressed, 0);
        uint epoch = flow.Epoch;

        Assert.DoesNotContain(E.CallGameStart, flow.Handle(Ev.TitleBusy, Second));

        Assert.Equal(H.GameFlow, flow.Host);
        Assert.Equal(epoch, flow.Epoch);
        Assert.Equal(0, flow.CountdownMsLeft(Second));
        Assert.Empty(flow.Update(10 * Second));
    }

    [Fact]
    public void Implicit_start_during_the_barrier_does_not_wait_or_call_the_game()
    {
        LobbyFlow flow = Open(Sam);
        flow.Handle(Ev.StartPressed, 0);
        flow.Update(3 * Second);

        Assert.Equal(new[] { E.PublishBoard, E.AssignGamePlayers, E.CloseLobbyForStart }, flow.Handle(Ev.TitleBusy, 4 * Second));
        Assert.Equal(H.GameFlow, flow.Host);
    }

    [Fact]
    public void A_game_already_running_when_hosting_starts_is_an_implicit_start_too()
    {
        // F7 mid-match: the lobby exists, and the next tick says the scene isn't the title. A level, so repeating is safe.
        LobbyFlow flow = Open();

        Assert.Equal(new[] { E.PublishBoard, E.HideLobby }, flow.Handle(Ev.SceneLeftTitle, Second));
        Assert.Equal(H.InGame, flow.Host);
        Assert.Empty(flow.Handle(Ev.SceneLeftTitle, 2 * Second));
        Assert.Empty(flow.Handle(Ev.TitleBusy, 2 * Second));
    }

    [Fact]
    public void An_implicit_game_backed_out_of_returns_to_the_lobby_without_implicit_left_over()
    {
        LobbyFlow flow = Open();
        flow.Handle(Ev.TitleBusy, Second);
        flow.Handle(Ev.TitleIdle, 2 * Second);
        flow.Update(3 * Second);

        Assert.Equal(H.Open, flow.Host);
        Assert.False(flow.IsImplicitStart);
    }

    [Fact]
    public void The_return_clears_the_new_run_flag_first_opens_the_lobby_and_locks_input_half_a_second()
    {
        LobbyFlow flow = Open();
        flow.Handle(Ev.StartPressed, 0);
        flow.Update(3 * Second);
        flow.Handle(Ev.SceneLeftTitle, 5 * Second);
        Assert.True(flow.KeepsNewRunFlagClear); // each frame, all game long: EndRun sets it just before its scene load

        Assert.Equal(new[] { E.ClearNewRunFlag, E.ShowLobby, E.PublishBoard }, flow.Handle(Ev.TitleSceneLoaded, 100 * Second));
        Assert.Equal(H.Returning, flow.Host);
        Assert.Equal(LobbyPhase.Returning, flow.BoardPhase);
        Assert.Equal(SessionPhase.Lobby, flow.WelcomePhase);
        Assert.True(flow.BackFromGame);
        Assert.True(flow.IsInputLocked(100 * Second));

        Assert.Equal(new[] { E.PublishBoard }, flow.Handle(Ev.TitleReady, 101 * Second));
        Assert.Equal(H.Open, flow.Host);
        Assert.True(flow.IsInputLocked((101 * Second) + LobbyTiming.InputLockMicros - 1));
        Assert.False(flow.IsInputLocked((101 * Second) + LobbyTiming.InputLockMicros));
        Assert.True(flow.BackFromGame);
    }

    [Fact]
    public void A_later_start_backs_up_again_and_only_once_per_game()
    {
        LobbyFlow flow = BackAtLobby(0, Sam);
        flow.Handle(Ev.StartPressed, 20 * Second);
        Assert.False(flow.BackFromGame);

        // Each game has its own backup, and New Run could delete a slot. In the barrier: a cancel never churns backups.
        Assert.Equal(new[] { E.BackupAgain, E.PublishBoard }, flow.Update(23 * Second));

        // Backed out with nothing played: the backup still stands.
        flow.Handle(Ev.FriendAcked, 23 * Second, flow.Epoch, Sam);
        flow.Handle(Ev.TitleIdle, 24 * Second);
        flow.Update(25 * Second);
        flow.Handle(Ev.StartPressed, 30 * Second);
        Assert.Equal(new[] { E.PublishBoard }, flow.Update(33 * Second));
    }

    [Fact]
    public void A_cancelled_start_takes_no_backup()
    {
        LobbyFlow flow = BackAtLobby(0, Sam);
        flow.Handle(Ev.StartPressed, 20 * Second);
        Assert.DoesNotContain(E.BackupAgain, flow.Handle(Ev.CancelPressed, 21 * Second));
        flow.Handle(Ev.StartPressed, 30 * Second);
        Assert.Contains(E.BackupAgain, flow.Update(33 * Second));
    }

    [Fact]
    public void An_implicit_game_after_a_game_backs_up_too()
    {
        LobbyFlow flow = BackAtLobby(0, Sam);
        Assert.Equal(new[] { E.BackupAgain, E.PublishBoard, E.AssignGamePlayers, E.CloseLobbyForStart }, flow.Handle(Ev.TitleBusy, 20 * Second));
    }

    [Fact]
    public void Wait_starts_by_itself_when_everyone_is_here_and_says_how_long_it_held()
    {
        LobbyFlow flow = Open(Sam);
        Assert.Empty(flow.Handle(Ev.EveryoneHere, 0)); // not waiting: a level, ignored

        flow.Handle(Ev.WaitPressed, 2 * Second);
        Assert.True(flow.IsWaitingForEveryone);
        Assert.Equal(H.Open, flow.Host);

        Assert.Equal(new[] { E.PublishBoard }, flow.Handle(Ev.EveryoneHere, 6 * Second));
        Assert.Equal(H.Counting, flow.Host);
        Assert.False(flow.IsWaitingForEveryone);
        Assert.Equal(4 * Second, flow.LastWaitMicros);
    }

    [Fact]
    public void Wait_can_be_cancelled_and_nothing_starts()
    {
        LobbyFlow flow = Open(Sam);
        flow.Handle(Ev.WaitPressed, 0);
        Assert.Equal(new[] { E.PublishBoard }, flow.Handle(Ev.CancelPressed, Second));
        Assert.False(flow.IsWaitingForEveryone);
        Assert.Equal(H.Open, flow.Host);
        Assert.Empty(flow.Handle(Ev.EveryoneHere, 2 * Second));
        Assert.Equal(H.Open, flow.Host);
    }

    [Fact]
    public void Start_anyway_during_a_wait_counts_what_it_waited()
    {
        LobbyFlow flow = Open(Sam);
        flow.Handle(Ev.WaitPressed, 0);
        flow.Handle(Ev.StartPressed, 3 * Second);
        Assert.Equal(H.Counting, flow.Host);
        Assert.Equal(3 * Second, flow.LastWaitMicros);

        flow.Handle(Ev.CancelPressed, 4 * Second);
        flow.Handle(Ev.StartPressed, 5 * Second);
        Assert.Equal(0, flow.LastWaitMicros); // no wait this time
    }

    [Fact]
    public void Closing_ends_the_session_and_forgets_everything()
    {
        foreach (H state in new[] { H.Open, H.Counting, H.Starting, H.GameFlow, H.InGame, H.Returning })
        {
            LobbyFlow flow = LobbyFlowTableTests.HostAt(state);

            Assert.Contains(E.EndSession, flow.Handle(Ev.LeavePressed, 100 * Second));

            Assert.Equal(H.Idle, flow.Host);
            Assert.Equal(LobbyRole.None, flow.Role);
            Assert.Equal(0u, flow.Epoch);
            Assert.False(flow.KeepsNewRunFlagClear);
            Assert.False(flow.IsFollowing(7));
            Assert.Empty(flow.AwaitingAcks);

            // And hosting again starts clean: no stale ack can land, and the first Start needs no backup again.
            flow.Handle(Ev.HostPressed, 200 * Second);
            flow.Handle(Ev.LobbyCreated, 200 * Second);
            flow.Handle(Ev.StartPressed, 200 * Second);
            Assert.Equal(1u, flow.Epoch);
            Assert.DoesNotContain(E.BackupAgain, flow.Update(203 * Second));
        }
    }

    [Fact]
    public void The_welcome_phase_follows_the_host_state()
    {
        Assert.Equal(SessionPhase.Lobby, LobbyFlowTableTests.HostAt(H.Creating).WelcomePhase);
        Assert.Equal(SessionPhase.Lobby, LobbyFlowTableTests.HostAt(H.Open).WelcomePhase);
        Assert.Equal(SessionPhase.Lobby, LobbyFlowTableTests.HostAt(H.Counting).WelcomePhase);
        Assert.Equal(SessionPhase.Playing, LobbyFlowTableTests.HostAt(H.Starting).WelcomePhase);
        Assert.Equal(SessionPhase.Playing, LobbyFlowTableTests.HostAt(H.GameFlow).WelcomePhase);
        Assert.Equal(SessionPhase.Playing, LobbyFlowTableTests.HostAt(H.InGame).WelcomePhase);
        Assert.Equal(SessionPhase.Lobby, LobbyFlowTableTests.HostAt(H.Returning).WelcomePhase);
    }

    [Fact]
    public void Seats_are_kept_by_state_and_reason()
    {
        // In the lobby a deliberate leave frees the seat at once, a drop keeps it 30 s; in a game it's 2 minutes whatever they said (D22).
        Assert.Equal(0, LobbyTiming.KeepSeatMicros(H.Open, LeaveReason.Quit));
        Assert.Equal(0, LobbyTiming.KeepSeatMicros(H.Open, LeaveReason.Removed));
        Assert.Equal(LobbyTiming.KeepSeatInLobbyMicros, LobbyTiming.KeepSeatMicros(H.Open, LeaveReason.TimedOut));
        Assert.Equal(LobbyTiming.KeepSeatInLobbyMicros, LobbyTiming.KeepSeatMicros(H.Counting, LeaveReason.LeftLobby));
        Assert.Equal(LobbyTiming.RejoinInGameMicros, LobbyTiming.KeepSeatMicros(H.InGame, LeaveReason.Quit));
        Assert.Equal(LobbyTiming.RejoinInGameMicros, LobbyTiming.KeepSeatMicros(H.GameFlow, LeaveReason.TimedOut));
        Assert.Equal(0, LobbyTiming.KeepSeatMicros(H.InGame, LeaveReason.Removed));
        Assert.Equal(0, LobbyTiming.KeepSeatMicros(H.Idle, LeaveReason.TimedOut));
    }

    [Fact]
    public void A_friend_can_not_host_and_a_host_can_not_join()
    {
        LobbyFlow host = Open();
        Assert.Empty(host.Handle(Ev.JoinPressed, 0));
        Assert.Equal(LobbyFriendState.Idle, host.Friend);

        var friend = new LobbyFlow();
        friend.Handle(Ev.JoinPressed, 0);
        Assert.Empty(friend.Handle(Ev.HostPressed, 0));
        Assert.Equal(H.Idle, friend.Host);
        Assert.Equal(LobbyRole.Friend, friend.Role);
    }

    [Fact]
    public void State_changes_are_kept_for_the_log_in_order_with_their_epoch()
    {
        LobbyFlow flow = Open();
        while (flow.TryTakeChange(out _))
        {
        }

        flow.Handle(Ev.StartPressed, 0);
        Assert.True(flow.TryTakeChange(out LobbyFlowChange change));
        Assert.Equal(("Open", "Counting", 1u), (change.From, change.To, change.Epoch));
        Assert.Equal("Lobby: phase Open -> Counting (epoch 1): Start.", change.Line);
        Assert.False(flow.TryTakeChange(out _));

        // A log nobody reads stays bounded.
        for (int i = 0; i < 1000; i++)
        {
            flow.Handle(Ev.CancelPressed, i);
            flow.Handle(Ev.StartPressed, i);
        }

        int kept = 0;
        while (flow.TryTakeChange(out _))
        {
            kept++;
        }

        Assert.InRange(kept, 1, 32);
    }
}
