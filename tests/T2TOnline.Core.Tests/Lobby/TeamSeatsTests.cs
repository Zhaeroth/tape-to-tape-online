using System.Collections.Generic;
using System.Linq;
using T2TOnline.Core.Lobby;

namespace T2TOnline.Core.Tests.Lobby;

/// <summary>Who a campaign's start puts on the team (D145), late joiners, the controller screen's choices, and the game's memory put back.</summary>
public class TeamSeatsTests
{
    private const int R = TeamSides.Right;
    private const int C = TeamSides.Center;

    private static readonly SeatCandidate Keyboard = new(0, Friend: false, Gamepad: false, "unused");
    private static readonly SeatCandidate HostPad = new(1, Friend: false, Gamepad: true, "unused");
    private static readonly SeatCandidate Alex = new(2, Friend: true, Gamepad: true, "P#a1b2c3");
    private static readonly SeatCandidate Sam = new(3, Friend: true, Gamepad: false, "P#d4e5f6");

    /// <summary>Applies writes as the plugin does, recording each in the book.</summary>
    private static void Apply(TeamSeatBook book, Dictionary<int, int> memory, IEnumerable<SideWrite> writes, bool record = true)
    {
        foreach (SideWrite write in writes)
        {
            if (write.Side is int side)
            {
                memory[write.GamePlayer] = side;
            }
            else
            {
                memory.Remove(write.GamePlayer);
            }

            if (record)
            {
                book.Written(write);
            }
        }
    }

    /// <summary>Who the game's own start seats from a memory: every present player remembered on the right (the solo squad aside).</summary>
    private static int[] GameSeats(IEnumerable<SeatCandidate> players, Dictionary<int, int> memory) =>
        players.Where(p => memory.TryGetValue(p.GamePlayer, out int side) && side == R).Select(p => p.GamePlayer).ToArray();

    [Fact]
    public void A_pad_host_and_a_friend_with_nothing_remembered_are_both_on_the_team()
    {
        var book = new TeamSeatBook();
        var memory = new Dictionary<int, int>();
        SeatCandidate[] players = { Keyboard, HostPad, Alex };
        SeatPlan plan = book.PlanStart(players, memory, soloSquad: false);
        Apply(book, memory, plan.Writes);

        Assert.Equal(new[] { 1, 2 }, GameSeats(players, memory));
        Assert.Equal(SeatOutcome.LeftAsIs, plan.Decisions[0].Outcome);
        Assert.Equal("Lobby: on the team for this run: the host (game player 1), P#a1b2c3 (game player 2); the game seats them at its start.", plan.Line);
        Assert.Empty(plan.NotSeatedLines);
    }

    [Fact]
    public void A_keyboard_host_with_a_pad_friend_plays_too()
    {
        var book = new TeamSeatBook();
        var memory = new Dictionary<int, int>();
        SeatCandidate[] players = { Keyboard, Alex };
        SeatPlan plan = book.PlanStart(players, memory, soloSquad: false);
        Apply(book, memory, plan.Writes);

        Assert.Equal(new[] { 0, 2 }, GameSeats(players, memory));
        Assert.Contains("the host (game player 0)", plan.Line);
    }

    [Fact]
    public void The_hosts_own_couch_memory_is_kept_and_a_friends_old_side_on_a_reused_id_is_not()
    {
        var book = new TeamSeatBook();
        // Before the session: the keyboard and the pad both on the team; a couch pad on id 2 left in the middle.
        var memory = new Dictionary<int, int> { [0] = R, [1] = R, [2] = C };
        SeatCandidate[] players = { Keyboard, HostPad, Alex };
        SeatPlan plan = book.PlanStart(players, memory, soloSquad: false);
        Apply(book, memory, plan.Writes);

        Assert.Equal(new[] { 0, 1, 2 }, GameSeats(players, memory));
        Assert.Equal(new[] { new SideWrite(2, R) }, plan.Writes);
        Assert.Contains("the host (game player 0), another player on the host's PC (game player 1)", plan.Line);
    }

    [Fact]
    public void The_solo_squad_writes_nothing_and_says_the_game_seats_one()
    {
        var book = new TeamSeatBook();
        var memory = new Dictionary<int, int>();
        SeatPlan plan = book.PlanStart(new[] { Keyboard, HostPad, Alex }, memory, soloSquad: true);

        Assert.True(plan.SoloSquad);
        Assert.Empty(plan.Writes);
        Assert.All(plan.Decisions, d => Assert.Equal(SeatOutcome.LeftAsIs, d.Outcome));
        Assert.StartsWith("Lobby: the solo squad: the game puts one player on the team by its own rule", plan.Line);
        Assert.Equal(LateSeat.SoloSquad, book.PlanLateJoin(Sam, memory, onTeam: 1, alreadyOnTeam: false).Outcome);
    }

    [Fact]
    public void Beyond_the_teams_size_the_first_in_order_play_and_the_rest_are_named()
    {
        var book = new TeamSeatBook();
        var memory = new Dictionary<int, int>();
        var players = new List<SeatCandidate> { HostPad };
        for (int id = 2; id <= 8; id++)
        {
            players.Add(new SeatCandidate(id, Friend: true, Gamepad: true, $"P#{id}"));
        }

        SeatPlan plan = book.PlanStart(players, memory, soloSquad: false);
        Apply(book, memory, plan.Writes);

        Assert.Equal(new[] { 1, 2, 3, 4, 5, 6 }, GameSeats(players, memory));
        Assert.Equal(new[] { "Lobby: not on the team for this run: P#7 (game player 7): no room: the game's team takes 6.", "Lobby: not on the team for this run: P#8 (game player 8): no room: the game's team takes 6." },
            plan.NotSeatedLines.ToArray());
        Assert.Equal(C, memory[7]);
    }

    [Fact]
    public void A_choice_on_the_controller_screen_is_kept_for_the_sessions_later_starts()
    {
        var book = new TeamSeatBook();
        var memory = new Dictionary<int, int>();
        SeatCandidate[] players = { Keyboard, HostPad, Alex };
        Apply(book, memory, book.PlanStart(players, memory, soloSquad: false).Writes);

        // During the run the host saves the controller screen with the friend in the middle (the screen writes every player it shows).
        memory[0] = C;
        memory[1] = R;
        memory[2] = C;
        (IReadOnlyList<SideWrite> putBack, IReadOnlyList<int> chosen) = book.EndRun(memory);
        Apply(book, memory, putBack, record: false);
        Assert.Empty(memory);
        Assert.Equal(new[] { 0, 1, 2 }, chosen.ToArray());
        Assert.Equal("Lobby: the game's controller screen changed who plays this run: game player 0 in the middle, game player 1 on the team, game player 2 in the middle; kept for this session's later starts.",
            book.ChoicesLine(chosen));

        // Continue: the friend still sits out, the host still plays, nothing overwritten.
        SeatPlan next = book.PlanStart(players, memory, soloSquad: false);
        Apply(book, memory, next.Writes);
        Assert.Equal(new[] { 1 }, GameSeats(players, memory));
        Assert.Equal(new[] { "Lobby: not on the team for this run: P#a1b2c3 (game player 2): they were put in the middle on the game's controller screen this session." },
            next.NotSeatedLines.ToArray());
    }

    [Fact]
    public void The_host_moved_to_the_middle_stays_out_and_isnt_put_back_by_the_default()
    {
        var book = new TeamSeatBook();
        var memory = new Dictionary<int, int>();
        SeatCandidate[] players = { HostPad, Alex };
        Apply(book, memory, book.PlanStart(players, memory, soloSquad: false).Writes);
        memory[1] = C;
        Apply(book, memory, book.EndRun(memory).PutBack, record: false);

        Apply(book, memory, book.PlanStart(players, memory, soloSquad: false).Writes);
        Assert.Equal(new[] { 2 }, GameSeats(players, memory));
    }

    [Fact]
    public void A_screen_save_that_changed_nothing_leaves_no_choice()
    {
        var book = new TeamSeatBook();
        var memory = new Dictionary<int, int>();
        Apply(book, memory, book.PlanStart(new[] { HostPad, Alex }, memory, soloSquad: false).Writes);
        memory[1] = R;
        memory[2] = R;
        (_, IReadOnlyList<int> chosen) = book.EndRun(memory);
        Assert.Empty(chosen);
        Assert.Null(book.ChoicesLine(chosen));
        Assert.Empty(book.Choices);
    }

    [Fact]
    public void The_solo_squads_screen_save_isnt_kept_as_a_choice_for_a_later_run()
    {
        var book = new TeamSeatBook();
        var memory = new Dictionary<int, int>();
        SeatCandidate[] players = { Keyboard, Alex };
        Assert.Empty(book.PlanStart(players, memory, soloSquad: true).Writes);

        // The solo squad's screen takes one player on the team: the host takes the run, the friend is saved in the middle by the squad's rule.
        memory[0] = R;
        memory[2] = C;
        (IReadOnlyList<SideWrite> putBack, IReadOnlyList<int> chosen) = book.EndRun(memory);
        Apply(book, memory, putBack, record: false);
        Assert.Empty(chosen);
        Assert.Empty(book.Choices);
        Assert.Empty(memory);

        // The next run, another squad: both play.
        SeatPlan next = book.PlanStart(players, memory, soloSquad: false);
        Apply(book, memory, next.Writes);
        Assert.Equal(new[] { 0, 2 }, GameSeats(players, memory));
        Assert.Empty(next.NotSeatedLines);
    }

    [Fact]
    public void A_start_with_the_last_run_still_open_still_puts_back_the_first_runs_memory()
    {
        var book = new TeamSeatBook();
        var before = new Dictionary<int, int> { [2] = C };
        var memory = new Dictionary<int, int>(before);
        SeatCandidate[] players = { HostPad, Alex };
        Apply(book, memory, book.PlanStart(players, memory, soloSquad: false).Writes);

        // No return to the lobby was seen between the two starts: the second plan keeps the first run's memory to put back.
        Apply(book, memory, book.PlanStart(players, memory, soloSquad: false).Writes);
        Assert.Equal(new[] { 1, 2 }, GameSeats(players, memory));
        Apply(book, memory, book.EndRun(memory).PutBack, record: false);
        Assert.Equal(before, memory);
    }

    [Fact]
    public void A_late_joiner_on_a_gone_friends_id_is_seated_and_the_id_put_back_after()
    {
        var book = new TeamSeatBook();
        // Before the session a couch pad on id 2 was remembered on the team.
        var before = new Dictionary<int, int> { [2] = R };
        var memory = new Dictionary<int, int>(before);
        Apply(book, memory, book.PlanStart(new[] { HostPad, Alex }, memory, soloSquad: false).Writes);

        // Alex goes for good mid-run; Sam joins and the game gives Sam the same id.
        Assert.Null(book.Forget(2, memory));
        var samOnTwo = new SeatCandidate(2, Friend: true, Gamepad: false, "P#d4e5f6");
        LateSeatPlan late = book.PlanLateJoin(samOnTwo, memory, onTeam: 1, alreadyOnTeam: false);
        Assert.Equal(LateSeat.Add, late.Outcome);
        Assert.Null(late.Write); // the id is already remembered on the team

        (IReadOnlyList<SideWrite> putBack, IReadOnlyList<int> chosen) = book.EndRun(memory);
        Apply(book, memory, putBack, record: false);
        Assert.Empty(chosen);
        Assert.Equal(before, memory);
    }

    [Fact]
    public void A_late_joiner_is_added_from_the_next_match_once()
    {
        var book = new TeamSeatBook();
        var memory = new Dictionary<int, int>();
        Assert.Equal(LateSeat.NoRun, book.PlanLateJoin(Sam, memory, onTeam: 0, alreadyOnTeam: false).Outcome);

        Apply(book, memory, book.PlanStart(new[] { HostPad, Alex }, memory, soloSquad: false).Writes);
        LateSeatPlan late = book.PlanLateJoin(Sam, memory, onTeam: 2, alreadyOnTeam: false);
        Assert.Equal(LateSeat.Add, late.Outcome);
        Assert.Equal(new SideWrite(3, R), late.Write);
        Assert.Equal("Lobby: P#d4e5f6 (game player 3) is on the run's team from the next match (the game's home list, as its controller screen fills it).", late.Line);
        Apply(book, memory, new[] { late.Write!.Value });

        Assert.Equal(LateSeat.AlreadyOnTeam, book.PlanLateJoin(Sam, memory, onTeam: 3, alreadyOnTeam: true).Outcome);
        Assert.Equal(LateSeat.NoRoom, book.PlanLateJoin(new SeatCandidate(4, true, true, "P#4"), memory, onTeam: TeamSides.TeamCap, alreadyOnTeam: false).Outcome);
    }

    [Fact]
    public void A_friend_gone_for_good_has_their_id_put_back_so_a_new_player_on_it_isnt_seated_by_it()
    {
        var book = new TeamSeatBook();
        var memory = new Dictionary<int, int> { [2] = C };
        Apply(book, memory, book.PlanStart(new[] { HostPad, Alex }, memory, soloSquad: false).Writes);
        Assert.Equal(R, memory[2]);

        SideWrite? back = book.Forget(2, memory);
        Assert.Equal(new SideWrite(2, C), back);
        Apply(book, memory, new[] { back!.Value }, record: false);

        // A couch pad of the host's now gets id 2: the game's memory says the middle, as before the session.
        Assert.Equal(new[] { 1 }, GameSeats(new[] { HostPad, new SeatCandidate(2, false, true, "unused") }, memory));
        (_, IReadOnlyList<int> chosen) = book.EndRun(memory);
        Assert.Empty(chosen);
    }

    [Fact]
    public void After_a_session_the_memory_is_exactly_as_before_its_start()
    {
        var book = new TeamSeatBook();
        var before = new Dictionary<int, int> { [0] = TeamSides.Left, [1] = C, [5] = R };
        var memory = new Dictionary<int, int>(before);
        SeatCandidate[] players = { Keyboard, HostPad, Alex };

        // Run 1: seated, the screen used, a late joiner, a friend gone for good, back at the lobby.
        Apply(book, memory, book.PlanStart(players, memory, soloSquad: false).Writes);
        memory[0] = C;
        Apply(book, memory, new[] { book.PlanLateJoin(Sam, memory, 2, false).Write!.Value });
        Apply(book, memory, new[] { book.Forget(2, memory)!.Value }, record: false);
        Apply(book, memory, book.EndRun(memory).PutBack, record: false);
        Assert.Equal(before, memory);

        // Run 2 (Continue), then hosting stops mid-run.
        Apply(book, memory, book.PlanStart(players, memory, soloSquad: false).Writes);
        memory[1] = C;
        IReadOnlyList<SideWrite> putBack = book.EndSession(memory);
        Apply(book, memory, putBack, record: false);
        Assert.Equal(before, memory);
        Assert.False(book.RunOpen);
        Assert.Empty(book.Choices);
        Assert.Equal("Lobby: the game's remembered sides are put back as before this run's start (2 change(s); hosting stopped).", TeamSeatBook.PutBackLine(putBack.Count, "hosting stopped"));
    }

    [Fact]
    public void Nothing_is_put_back_or_chosen_without_a_seated_run()
    {
        var book = new TeamSeatBook();
        var memory = new Dictionary<int, int> { [1] = R };
        Assert.Empty(book.EndRun(memory).PutBack);
        Assert.Empty(book.EndSession(memory));
        Assert.Null(book.Forget(1, memory));
        Assert.Equal(R, memory[1]);
    }

    [Fact]
    public void A_write_that_failed_isnt_taken_for_a_screen_choice()
    {
        var book = new TeamSeatBook();
        var memory = new Dictionary<int, int>();
        SeatPlan plan = book.PlanStart(new[] { HostPad, Alex }, memory, soloSquad: false);
        Apply(book, memory, plan.Writes.Take(1)); // the friend's write failed: neither made nor recorded
        (IReadOnlyList<SideWrite> putBack, IReadOnlyList<int> chosen) = book.EndRun(memory);
        Assert.Empty(chosen);
        Assert.Equal(new[] { new SideWrite(1, null) }, putBack);
    }

    [Fact]
    public void Hosting_begun_during_a_run_says_once_that_a_joiner_isnt_on_its_team()
    {
        var book = new TeamSeatBook();
        var memory = new Dictionary<int, int>();

        // At the title (a Start's friends becoming game players before its seating): nothing to say.
        Assert.Null(book.UnseatedRunLine(Alex, inCampaignRun: false));

        // Hosting began on the run's map: the first joiner is named by tag, once for the session.
        Assert.Equal("Lobby: hosting began during this run, so P#d4e5f6 (game player 3), and any friend who joins it, isn't on its team until the host's Controllers screen puts them there; the lobby seats everyone at the next New Run or Continue (said once while hosting).",
            book.UnseatedRunLine(Sam, inCampaignRun: true));
        Assert.Null(book.UnseatedRunLine(Alex, inCampaignRun: true));

        // A new hosting session may say it again; one that has seen a campaign start, planned or not, never does.
        book.EndSession(memory);
        book.StartSeen();
        Assert.Null(book.UnseatedRunLine(Sam, inCampaignRun: true));
        book.EndSession(memory);
        book.PlanStart(new[] { HostPad, Alex }, memory, soloSquad: false);
        Assert.Null(book.UnseatedRunLine(Sam, inCampaignRun: true)); // a seated run is open: the joiner is queued instead
        book.EndRun(memory);
        Assert.Null(book.UnseatedRunLine(Sam, inCampaignRun: true)); // back at the lobby after a seated run: the next start seats them
    }

    [Theory]
    [InlineData(TeamSides.Right, "on the team")]
    [InlineData(TeamSides.Center, "in the middle")]
    [InlineData(TeamSides.Left, "on the left")]
    [InlineData(7, "on side 7")]
    public void Sides_read_in_plain_words(int side, string text) => Assert.Equal(text, TeamSides.Text(side));
}
