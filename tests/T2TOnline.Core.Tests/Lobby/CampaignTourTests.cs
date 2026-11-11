using System;
using System.Collections.Generic;
using System.Linq;
using T2TOnline.Core.Lobby;

namespace T2TOnline.Core.Tests.Lobby;

/// <summary>The campaign tours: their plan, limits and keys, the walk's rules, the walker's lines read, the node lines and the summary.</summary>
public class CampaignTourTests
{
    [Fact]
    public void The_campaign_plan_has_no_problem_and_goes_in_its_order()
    {
        Assert.Null(UiCheckPlan.Problem(CampaignTour.Steps));
        Assert.Equal(new[] { "cp_host", "cp_newrun", "cp_first", "cp_continue", "cp_nodes", "cp_end", "cp_close", "finish" }, CampaignTour.Steps.Select(s => s.Id).ToArray());
    }

    [Fact]
    public void Every_campaign_step_runs_in_the_players_own_settings_and_none_changes_them()
    {
        Assert.All(CampaignTour.Steps.Take(CampaignTour.Steps.Count - 1), s =>
        {
            Assert.Equal(UiCheckStepKind.Plain, s.Kind);
            Assert.True(s.Needs.HasFlag(UiCheckNeeds.PlayersSettings), s.Id);
        });
        Assert.Equal(UiCheckStepKind.Finish, CampaignTour.Steps[^1].Kind);
    }

    [Theory]
    [InlineData("campaign act1", 15)]
    [InlineData("Campaign  Act1", 15)]
    [InlineData("campaign full", 40)]
    [InlineData(" campaign full ", 40)]
    public void Both_tours_are_picked_by_name_with_their_limits_and_share_the_steps(string line, int minutes)
    {
        Assert.True(UiCheckRequest.TryParse(line, UiCheckPlan.Tours, out UiCheckRequest? request, out string problem), problem);
        Assert.Same(CampaignTour.Steps, request!.Tour.Steps);
        Assert.Equal(TimeSpan.FromMinutes(minutes), request.Tour.Limit);
        Assert.NotNull(CampaignTour.KindOf(request.Tour.Name));
    }

    [Theory]
    [InlineData("campaign act1", CampaignTourKind.Act1)]
    [InlineData("campaign full", CampaignTourKind.Full)]
    public void The_kind_follows_the_tours_name(string name, CampaignTourKind kind)
    {
        Assert.Equal(kind, CampaignTour.KindOf(name));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("campaign")]
    [InlineData("onepc")]
    [InlineData("Campaign Act1")]
    public void Anything_else_is_no_campaign_tour(string? name)
    {
        Assert.Null(CampaignTour.KindOf(name));
    }

    [Theory]
    [InlineData("campaign act1", 1, false)]
    [InlineData("campaign act1 seed=7", 7, false)]
    [InlineData("campaign full seed=0 record=on", 0, true)]
    [InlineData("campaign full record=off seed=999999", 999999, false)]
    [InlineData("campaign full record=on", 1, true)]
    public void The_arguments_are_read_with_their_defaults(string line, int seed, bool record)
    {
        Assert.True(UiCheckRequest.TryParse(line, UiCheckPlan.Tours, out UiCheckRequest? request, out string problem), problem);
        Assert.Equal(new CampaignTourArgs(seed, record), CampaignTour.ArgsOf(request));
    }

    [Fact]
    public void No_request_gives_the_defaults()
    {
        Assert.Equal(new CampaignTourArgs(CampaignTour.DefaultSeed, false), CampaignTour.ArgsOf(null));
    }

    [Theory]
    [InlineData("campaign act1 seed=1000000", "\"seed\" takes 0 to 999999, not \"1000000\"")]
    [InlineData("campaign act1 seed=-1", "\"seed\" takes 0 to 999999, not \"-1\"")]
    [InlineData("campaign full record=yes", "\"record\" takes on or off, not \"yes\"")]
    [InlineData("campaign full route=3", "the tour \"campaign full\" takes no key \"route\" (it takes seed, record, kbhost, events, late, sides, brawl, brawlend)")]
    [InlineData("campaign half", "no tour is called \"campaign half\"")]
    [InlineData("campaign full seed=1 seed=2", "the key \"seed\" is given twice")]
    public void What_the_campaign_tours_dont_take_is_refused(string line, string why)
    {
        Assert.False(UiCheckRequest.TryParse(line, UiCheckPlan.Tours, out UiCheckRequest? request, out string problem));
        Assert.Null(request);
        Assert.Contains(why, problem, StringComparison.Ordinal);
    }

    [Fact]
    public void The_one_pc_tour_has_its_failed_load_after_the_drop_and_before_the_kick()
    {
        string[] ids = UiCheckPlan.OnePc.Select(s => s.Id).ToArray();
        Assert.Null(UiCheckPlan.Problem(UiCheckPlan.OnePc));
        Assert.Equal(new[] { "op_host", "op_arrive", "op_board", "op_close_prompt", "op_start", "op_match", "op_drop", "op_failload", "op_kick", "op_close", "finish" }, ids);
    }

    [Fact]
    public void Route_seeds_are_never_zero_repeat_for_a_seed_and_vary_by_walk()
    {
        var seeds = new HashSet<int>();
        for (int walk = 1; walk <= 50; walk++)
        {
            int route = CampaignTour.RouteSeed(CampaignTour.DefaultSeed, walk);
            Assert.NotEqual(0, route);
            Assert.True(route > 0);
            Assert.Equal(route, CampaignTour.RouteSeed(CampaignTour.DefaultSeed, walk));
            seeds.Add(route);
        }

        Assert.True(seeds.Count >= 45);
        Assert.NotEqual(0, CampaignTour.RouteSeed(0, 0));
        Assert.NotEqual(CampaignTour.RouteSeed(1, 3), CampaignTour.RouteSeed(2, 3));
    }

    [Theory]
    [InlineData("Challenge", CampaignNodeKind.Match)]
    [InlineData("Elite", CampaignNodeKind.Match)]
    [InlineData("Boss", CampaignNodeKind.Match)]
    [InlineData("Chaos", CampaignNodeKind.Event)]
    [InlineData("Nurse", CampaignNodeKind.Event)]
    [InlineData("FanNumber1", CampaignNodeKind.Event)]
    [InlineData("NULL", CampaignNodeKind.Unknown)]
    [InlineData("?", CampaignNodeKind.Unknown)]
    [InlineData("", CampaignNodeKind.Unknown)]
    [InlineData(null, CampaignNodeKind.Unknown)]
    public void Node_kinds_follow_the_games_node_types(string? type, CampaignNodeKind kind)
    {
        Assert.Equal(kind, CampaignTour.NodeKindOf(type));
    }

    [Theory]
    [InlineData(CampaignTourKind.Act1, 1, "Boss", true)]
    [InlineData(CampaignTourKind.Act1, 2, "Boss", false)]
    [InlineData(CampaignTourKind.Act1, 1, "Elite", false)]
    [InlineData(CampaignTourKind.Full, 3, "Boss", true)]
    [InlineData(CampaignTourKind.Full, 1, "Boss", false)]
    [InlineData(CampaignTourKind.Full, 3, null, false)]
    public void The_last_match_is_the_last_acts_boss(CampaignTourKind kind, int act, string? type, bool last)
    {
        Assert.Equal(last, CampaignTour.IsLastMatch(kind, act, type));
    }

    [Theory]
    [InlineData(CampaignTourKind.Act1, 1, false, 3, 60, CampaignWalkVerdict.WalkOn)]
    [InlineData(CampaignTourKind.Act1, 2, false, 3, 60, CampaignWalkVerdict.ActOver)]
    [InlineData(CampaignTourKind.Act1, 2, true, 3, 60, CampaignWalkVerdict.RunEnded)]
    [InlineData(CampaignTourKind.Full, 2, false, 3, 60, CampaignWalkVerdict.WalkOn)]
    [InlineData(CampaignTourKind.Full, 3, true, 30, 60, CampaignWalkVerdict.RunEnded)]
    [InlineData(CampaignTourKind.Act1, 1, false, CampaignTour.MaxNodesAct1, 60, CampaignWalkVerdict.NodeCap)]
    [InlineData(CampaignTourKind.Full, 2, false, CampaignTour.MaxNodesFull, 60, CampaignWalkVerdict.NodeCap)]
    [InlineData(CampaignTourKind.Full, 2, false, CampaignTour.MaxNodesAct1, 60, CampaignWalkVerdict.WalkOn)]
    [InlineData(CampaignTourKind.Act1, 1, false, 3, 750, CampaignWalkVerdict.TimeReserve)]
    [InlineData(CampaignTourKind.Act1, 1, false, 3, 749, CampaignWalkVerdict.WalkOn)]
    [InlineData(CampaignTourKind.Full, 2, false, 3, 2250, CampaignWalkVerdict.TimeReserve)]
    public void The_walk_ends_at_the_run_the_act_the_cap_or_the_time_kept_for_the_end(CampaignTourKind kind, int act, bool runEnded, int nodes, double elapsed, CampaignWalkVerdict verdict)
    {
        TimeSpan limit = kind == CampaignTourKind.Act1 ? CampaignTour.Act1Limit : CampaignTour.FullLimit;
        Assert.Equal(verdict, CampaignTour.Next(kind, act, runEnded, nodes, elapsed, limit));
        Assert.False(string.IsNullOrEmpty(CampaignTour.Said(verdict)));
    }

    [Theory]
    [InlineData(5, 0, 0, false)]
    [InlineData(20, 0, 0, true)]
    [InlineData(5, 1, 2, false)]
    [InlineData(5, 0, 2, true)]
    [InlineData(5, 3, 1, false)]
    public void A_dwell_lasts_its_seconds_or_ends_once_the_visitors_lead_by_two(double seconds, int home, int visitor, bool over)
    {
        Assert.Equal(over, CampaignTour.DwellOver(seconds, home, visitor));
    }

    [Theory]
    [InlineData(3u, 4u, true)]
    [InlineData(4u, 4u, false)]
    [InlineData(5u, 4u, false)]
    [InlineData(0u, 1u, true)]
    public void Checkpoints_must_rise_after_each_map_load(uint before, uint after, bool rose)
    {
        Assert.Equal(rose, CampaignTour.CheckpointRose(before, after));
    }

    [Fact]
    public void The_ribbon_at_the_end_reads_the_runs_end_with_its_act()
    {
        Assert.Null(CampaignTour.RibbonProblem(new LobbyResult(LobbyResultKind.RunOver, 2, 1), won: false));
        Assert.Null(CampaignTour.RibbonProblem(new LobbyResult(LobbyResultKind.RunOver, 2, 0), won: false));
        Assert.Null(CampaignTour.RibbonProblem(new LobbyResult(LobbyResultKind.RunWon, 3, 8), won: true));
        Assert.Equal("the ribbon's act and floor read 0-0", CampaignTour.RibbonProblem(new LobbyResult(LobbyResultKind.RunOver, 0, 0), won: false));
        Assert.Equal("the ribbon reads RunOver, not RunWon", CampaignTour.RibbonProblem(new LobbyResult(LobbyResultKind.RunOver, 3, 8), won: true));
        Assert.Equal("the ribbon reads None, not RunOver", CampaignTour.RibbonProblem(LobbyResult.None, won: false));
        Assert.Equal("the ribbon reads RunSaved, not RunOver", CampaignTour.RibbonProblem(new LobbyResult(LobbyResultKind.RunSaved, 2, 1), won: false));
    }

    [Fact]
    public void The_ribbon_after_the_maps_menu_is_run_saved_at_the_runs_place()
    {
        Assert.Null(CampaignTour.SavedRibbonProblem(new LobbyResult(LobbyResultKind.RunSaved, 1, 4), 1, 4));
        Assert.Equal("the ribbon reads 1-3, the run is at 1-4", CampaignTour.SavedRibbonProblem(new LobbyResult(LobbyResultKind.RunSaved, 1, 3), 1, 4));
        Assert.Equal("the ribbon reads RunOver, not RunSaved", CampaignTour.SavedRibbonProblem(new LobbyResult(LobbyResultKind.RunOver, 1, 4), 1, 4));
        Assert.Equal("RunSaved 1-4", CampaignTour.RibbonText(new LobbyResult(LobbyResultKind.RunSaved, 1, 4)));
        Assert.Equal("None", CampaignTour.RibbonText(LobbyResult.None));
    }

    [Fact]
    public void A_walker_line_is_read_field_by_field()
    {
        Assert.True(WalkerLine.TryRead("[SoakTest:Campaign] run=1 act=1 phase=SelectingNode node=Elite point=0,2 opponent=Spartans", out WalkerLine line));
        Assert.Equal("SelectingNode", line.Phase);
        Assert.Equal("Elite", line.Node);
        Assert.Equal("0,2", line.Point);
        Assert.Equal("Spartans", line.Opponent);
        Assert.True(line.SelectsNode);
        Assert.False(line.Timeout);

        Assert.True(WalkerLine.TryRead("[SoakTest:Campaign] run=1 act=1 phase=EventNode node=Chaos menu=EventChoiceMenu choice=2", out line));
        Assert.Equal("EventChoiceMenu", line.Menu);
        Assert.Equal("2", line.Choice);
        Assert.False(line.SelectsNode);

        Assert.True(WalkerLine.TryRead("[SoakTest:Campaign] TIMEOUT run=1 act=1 phase=WorldMap floor=2 scene=world_map menus=RelicRewardMenu globalMatch=3 reason=world map never finished loading", out line));
        Assert.True(line.Timeout);
        Assert.Equal("WorldMap", line.Phase);
    }

    [Fact]
    public void A_walker_lines_value_keeps_its_spaces_up_to_the_next_key()
    {
        // A team's name can be two words; the walker writes it last, as the game names it.
        Assert.True(WalkerLine.TryRead("[SoakTest:Campaign] run=1 act=1 phase=SelectingNode node=Elite point=1,2 opponent=Greasy Lettuce", out WalkerLine line));
        Assert.Equal("Greasy Lettuce", line.Opponent);
        Assert.Equal("1,2", line.Point);
        Assert.Equal("Elite", line.Node);

        // Anywhere in the line, a value ends where the next key begins.
        Assert.True(WalkerLine.TryRead("[SoakTest:Campaign] phase=SelectingNode opponent=Greasy Lettuce  node=Boss point=0,7", out line));
        Assert.Equal("Greasy Lettuce", line.Opponent);
        Assert.Equal("Boss", line.Node);
        Assert.Equal("0,7", line.Point);

        // An equals sign starts a key only after a word: "=B" and "2=" stay in the value.
        Assert.True(WalkerLine.TryRead("[SoakTest:Campaign] phase=SelectingNode opponent=A =B 2=C node=Boss", out line));
        Assert.Equal("A =B 2=C", line.Opponent);
        Assert.Equal("Boss", line.Node);
    }

    [Fact]
    public void A_two_word_opponent_is_named_whole_in_the_nodes_line()
    {
        var tally = new CampaignTourTally();
        Read(tally, "phase=SelectingNode node=Elite point=1,2 opponent=Greasy Lettuce", now: 0);
        tally.MatchStarted();
        tally.MatchWon(38, 8, 131);
        Assert.Equal(new[] { "campaign: node 1 (act 1, floor 1): Elite Greasy Lettuce; won (forced) in 38 s; checkpoints 8; menus sent 131." }, tally.TakeLines());
    }

    [Theory]
    [InlineData(0, "the walk ended after 0 match(es) won: match 2, with the friend's drop and the AI stand-in, was never played")]
    [InlineData(1, "the walk ended after 1 match(es) won: match 2, with the friend's drop and the AI stand-in, was never played")]
    [InlineData(2, null)]
    [InlineData(4, null)]
    public void A_walk_that_ended_before_the_drops_match_says_so(int matchesWon, string? problem)
    {
        Assert.Equal(problem, CampaignTour.DropNotPlayed(matchesWon));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("nothing of the walker's here")]
    [InlineData("anode=x")]
    public void Anything_else_isnt_a_walker_line(string? text)
    {
        Assert.False(WalkerLine.TryRead(text, out _));
    }

    [Fact]
    public void A_walker_line_is_bounded()
    {
        string huge = "[SoakTest:Campaign] phase=SelectingNode node=" + new string('x', 5000);
        Assert.True(WalkerLine.TryRead(huge, out WalkerLine line));
        Assert.Equal(48, line.Node.Length);
    }

    [Fact]
    public void Events_close_at_the_next_node_and_a_match_when_it_is_won()
    {
        var tally = new CampaignTourTally();
        Read(tally, "phase=SelectingNode node=Chaos point=0,1 opponent=?", now: 10);
        Read(tally, "phase=EventNode node=Chaos menu=EventChoiceMenu choice=1", now: 12);
        Assert.Empty(tally.TakeLines());
        Read(tally, "phase=SelectingNode node=Elite point=0,2 opponent=Spartans", now: 30, checkpoints: 4, menus: 9);
        Assert.Equal(new[] { "campaign: node 1 (act 1, floor 1): Chaos at 0,1; event 1 in 20 s; checkpoints 4; menus sent 9." }, tally.TakeLines());
        tally.MatchStarted();
        tally.RewardSeen();
        tally.MatchWon(91, 6, 15);
        Assert.Equal(new[] { "campaign: node 2 (act 1, floor 1): Elite Spartans; won (forced), rewards 1 in 61 s; checkpoints 6; menus sent 15." }, tally.TakeLines());
        Assert.Equal((2, 1, 1, 1), (tally.Nodes, tally.Matches, tally.Events, tally.Rewards));
        Assert.Null(tally.Current);
    }

    [Fact]
    public void An_event_that_leads_to_a_match_counts_as_a_match()
    {
        var tally = new CampaignTourTally();
        Read(tally, "phase=SelectingNode node=Boss point=0,7 opponent=?", now: 0);
        tally.MatchStarted();
        tally.CloseEvent(5, 1, 1);
        Assert.Empty(tally.TakeLines());
        tally.MatchWon(40, 2, 3);
        Assert.Equal(new[] { "campaign: node 1 (act 1, floor 1): Boss at 0,7; won (forced) in 40 s; checkpoints 2; menus sent 3." }, tally.TakeLines());
        Assert.Equal((1, 1, 0), (tally.Matches, tally.Nodes, tally.Events));
    }

    [Fact]
    public void An_event_with_no_choice_read_says_so_and_nothing_closes_with_no_node()
    {
        var tally = new CampaignTourTally();
        tally.MatchWon(1, 1, 1);
        tally.CloseEvent(1, 1, 1);
        tally.RewardSeen();
        Assert.Empty(tally.TakeLines());
        Assert.Equal(1, tally.Rewards);
        Read(tally, "phase=SelectingNode node=Nurse point=1,3", now: 0);
        tally.CloseEvent(8, 2, 2);
        Assert.Equal(new[] { "campaign: node 1 (act 1, floor 1): Nurse at 1,3; event ? in 8 s; checkpoints 2; menus sent 2." }, tally.TakeLines());
        tally.Reset();
        Assert.Equal((0, 0, 0, 0), (tally.Nodes, tally.Matches, tally.Events, tally.Rewards));
    }

    [Fact]
    public void The_summary_line_says_every_count_of_the_tour()
    {
        var tally = new CampaignTourTally();
        Read(tally, "phase=SelectingNode node=Challenge point=0,0 opponent=Bears", now: 0);
        tally.MatchStarted();
        tally.MatchWon(30, 2, 4);
        string line = CampaignTour.SummaryLine(tally, "over", 2, 1, new LobbyResult(LobbyResultKind.RunOver, 2, 1), 9, 3, 1, 0, 42, "0123456789abcdef (48.0 KB)");
        Assert.Equal("campaign: 1 nodes (1 matches, 0 events, 0 rewards), the run over at 2-1; ribbon RunOver 2-1; checkpoints 9; time-scale resets 3; " +
            "achievements held back 1; allowances refused 0; writes refused 42; tour run file 0123456789abcdef (48.0 KB).", line);
    }

    [Fact]
    public void The_run_files_text_is_its_hash_start_and_size()
    {
        Assert.Equal("0123456789abcdef (48.0 KB)", CampaignTour.FileText("0123456789ABCDEF0011223344", 49152));
        Assert.Equal("missing", CampaignTour.FileText(null, 0));
        Assert.Equal("abc (0.0 KB)", CampaignTour.FileText("ABC", -5));
    }

    [Fact]
    public void The_drops_line_says_what_was_seen()
    {
        Assert.Equal("campaign: the second match: a simulated drop; the AI stand-in played their skater 3.1 s after the cut; the friend back after 16.0 s (seat 2)",
            CampaignTour.DropLine(3.06, 16, "seat 2"));
        Assert.Equal("campaign: the second match: a simulated drop; the AI stand-in never played their skater; the friend back after 16.0 s (seat 2)",
            CampaignTour.DropLine(null, 16, "seat 2"));
    }

    [Fact]
    public void The_drop_waits_for_live_play_and_leaves_time_for_the_hand_back()
    {
        // The stand-in starts 3 s into the silence and hands back half a second after the friend's return.
        Assert.True(CampaignTour.DropWaitSeconds > 30);
        Assert.True(CampaignTour.AfterDropSeconds >= 1);
    }

    [Fact]
    public void The_maps_start_is_held_until_nothing_is_on_the_uis_input_stack()
    {
        // The map loaded, shown and not ignoring inputs, the navigation active, the pad registered: a loading transition still
        // hiding keeps the UI module from taking the bar's press.
        Assert.Equal("an input handler is still on the UI's stack",
            CampaignTour.MapStartHeld(entryShown: true, mapIgnoresInputs: false, navigationActive: true, moduleTakesInput: false, padRegistered: true));
        Assert.Null(CampaignTour.MapStartHeld(entryShown: true, mapIgnoresInputs: false, navigationActive: true, moduleTakesInput: true, padRegistered: true));
    }

    [Theory]
    [InlineData(false, false, true, true, true, "the map bar's Menu entry isn't on screen")]
    [InlineData(true, true, true, true, true, "the map ignores inputs")]
    [InlineData(true, false, false, true, true, "the game's navigation is off")]
    [InlineData(true, false, true, true, false, "the Menu entry doesn't answer the pad's player")]
    [InlineData(false, true, false, false, false, "the map bar's Menu entry isn't on screen")]
    public void The_maps_start_names_the_first_thing_holding_it(bool shown, bool ignores, bool navigation, bool takes, bool registered, string held)
    {
        Assert.Equal(held, CampaignTour.MapStartHeld(shown, ignores, navigation, takes, registered));
    }

    [Fact]
    public void The_controller_screen_from_the_map_shows_both_players_on_the_team_only_when_both_are_on_the_right()
    {
        const int right = CampaignTour.SideRight;
        const int center = CampaignTour.SideCenter;
        Assert.True(CampaignTour.ScreenShowsTeam(true, right, true, right));
        // A player in the middle isn't on the team: the next start would leave them off.
        Assert.False(CampaignTour.ScreenShowsTeam(true, center, true, right));
        Assert.False(CampaignTour.ScreenShowsTeam(true, right, true, center));
        // A player the screen doesn't show proves nothing.
        Assert.False(CampaignTour.ScreenShowsTeam(false, right, true, right));
        Assert.False(CampaignTour.ScreenShowsTeam(true, right, false, right));
    }

    [Fact]
    public void The_first_matchs_line_says_when_the_friends_skater_came_or_that_it_never_did()
    {
        Assert.Equal("campaign: the first match: the friend's skater in live play 4.2 s after the match came into play (game player 2: on a team, a skater)",
            CampaignTour.FirstMatchLine(4.24, "game player 2: on a team, a skater"));
        Assert.Equal("campaign: the first match: the friend's game player held no skater in live play within 45 s (game player 2: on no team, no skater), with no visit to the controller screen",
            CampaignTour.FirstMatchLine(null, "game player 2: on no team, no skater"));
    }

    [Fact]
    public void A_side_is_said_as_what_it_means_in_a_campaign()
    {
        Assert.Equal("the team (right)", CampaignTour.SideText(CampaignTour.SideRight));
        Assert.Equal("the middle (not playing)", CampaignTour.SideText(CampaignTour.SideCenter));
        Assert.Equal("the left", CampaignTour.SideText(CampaignTour.SideLeft));
        Assert.Equal("side 7", CampaignTour.SideText(7));
    }

    [Theory]
    [InlineData("campaign act1")]
    [InlineData("campaign full")]
    [InlineData("campaign act1 kbhost=off")]
    [InlineData("campaign full seed=3 record=on")]
    public void Without_kbhost_on_a_campaign_tour_runs_its_steps_as_before(string line)
    {
        Assert.True(UiCheckRequest.TryParse(line, UiCheckPlan.Tours, out UiCheckRequest? request, out string problem), problem);
        IReadOnlyList<UiCheckStep> plan = CampaignTour.StepsFor(request!);
        Assert.Same(CampaignTour.Steps, plan);
        Assert.Equal(new[] { "cp_host", "cp_newrun", "cp_first", "cp_continue", "cp_nodes", "cp_end", "cp_close", "finish" }, plan.Select(s => s.Id).ToArray());
        Assert.True(CampaignTour.IsCampaignPlan(plan));
        Assert.Equal("campaign: the plan has 8 steps, without cp_kbhost (kbhost=on adds it)", CampaignTour.PlanLine(plan));
    }

    [Theory]
    [InlineData("campaign act1 kbhost=on")]
    [InlineData("campaign full kbhost=on seed=7")]
    public void With_kbhost_on_the_keyboard_host_step_comes_after_the_first_match(string line)
    {
        Assert.True(UiCheckRequest.TryParse(line, UiCheckPlan.Tours, out UiCheckRequest? request, out string problem), problem);
        IReadOnlyList<UiCheckStep> plan = CampaignTour.StepsFor(request!);
        Assert.Same(CampaignTour.StepsWithKeyboardHost, plan);
        Assert.Null(UiCheckPlan.Problem(plan));
        Assert.Equal(new[] { "cp_host", "cp_newrun", "cp_first", "cp_kbhost", "cp_continue", "cp_nodes", "cp_end", "cp_close", "finish" }, plan.Select(s => s.Id).ToArray());
        Assert.True(CampaignTour.IsCampaignPlan(plan));
        Assert.Equal("campaign: the plan has 9 steps, cp_kbhost after cp_first (kbhost=on)", CampaignTour.PlanLine(plan));
    }

    [Fact]
    public void Kbhost_is_refused_by_a_tour_that_doesnt_take_it_and_never_changes_another_tours_plan()
    {
        Assert.False(UiCheckRequest.TryParse("padwalk kbhost=on", UiCheckPlan.Tours, out _, out _));
        Assert.False(UiCheckRequest.TryParse("campaign act1 kbhost=yes", UiCheckPlan.Tours, out _, out _));
        Assert.True(UiCheckRequest.TryParse("padwalk", UiCheckPlan.Tours, out UiCheckRequest? request, out string problem), problem);
        Assert.Same(request!.Tour.Steps, CampaignTour.StepsFor(request));
        Assert.False(CampaignTour.IsCampaignPlan(request.Tour.Steps));
    }

    [Fact]
    public void The_keyboard_host_step_runs_on_the_map_between_the_first_match_and_the_continue()
    {
        string[] ids = CampaignTour.StepsWithKeyboardHost.Select(s => s.Id).ToArray();
        Assert.Equal(Array.IndexOf(ids, "cp_first") + 1, Array.IndexOf(ids, CampaignTour.KeyboardHostStep));
        Assert.Equal(Array.IndexOf(ids, CampaignTour.KeyboardHostStep) + 1, Array.IndexOf(ids, "cp_continue"));
        Assert.Equal(TourPlace.Map, TourStarts.Of(CampaignTour.KeyboardHostStep));
        Assert.False(TourStarts.EndsAtMain(CampaignTour.KeyboardHostStep));
    }

    [Fact]
    public void A_keyboard_host_that_refused_the_friend_and_kept_its_cursor_passes()
    {
        Assert.Null(CampaignTour.KeyboardHostProblem(hostOnKeyboard: true, refused: 2, refusedIdle: 0, cursorStayed: true));
        Assert.Equal(
            "campaign: a keyboard host: the host's own player keyboard (Rewired player 0, game player 0), the tour's pad set aside; " +
            "the friend's d-pad on the map: refused 2 time(s) by the host-decides rule (0 in the same time before it, with nothing pressed); the cursor stayed on MapNode",
            CampaignTour.KeyboardHostLine("keyboard (Rewired player 0, game player 0)", 2, 0, true, "MapNode", "MapNode"));
    }

    [Fact]
    public void The_friends_press_of_the_keyboard_host_step_is_made_only_on_the_bare_map_with_the_pad_the_one_controlling_player()
    {
        Assert.Null(CampaignTour.KeyboardHostHeld(hosting: true, mapBare: true, anyPlayerControl: false, padControls: true));
    }

    [Fact]
    public void A_menu_any_player_drives_holds_the_friends_press_whoever_the_game_names_as_controlling()
    {
        // The map's own menu: every player's press is taken there without the ask, so the rule can't be shown and the press would move the menu's cursor.
        foreach (bool padControls in new[] { false, true })
        {
            string? held = CampaignTour.KeyboardHostHeld(hosting: true, mapBare: true, anyPlayerControl: true, padControls);
            Assert.NotNull(held);
            Assert.Contains("any-player control is on", held, StringComparison.Ordinal);
            Assert.Contains("the rule isn't asked", held, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Nobody_on_the_games_controlling_list_holds_the_friends_press()
    {
        // The game's own ask could then say yes to the friend, should the rule not refuse.
        string? held = CampaignTour.KeyboardHostHeld(hosting: true, mapBare: true, anyPlayerControl: false, padControls: false);
        Assert.NotNull(held);
        Assert.Contains("the tour's pad isn't the UI's controlling player", held, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, true, "this PC isn't hosting")]
    [InlineData(true, false, "a screen is over the map")]
    [InlineData(false, false, "this PC isn't hosting")]
    public void Off_the_bare_map_of_a_hosting_the_friends_press_is_held(bool hosting, bool mapBare, string why)
    {
        Assert.Equal(why, CampaignTour.KeyboardHostHeld(hosting, mapBare, anyPlayerControl: false, padControls: true));
        Assert.Equal(why, CampaignTour.KeyboardHostHeld(hosting, mapBare, anyPlayerControl: true, padControls: false));
    }

    [Fact]
    public void The_keyboard_host_steps_control_lines_say_who_has_the_menus_and_whether_the_game_asks()
    {
        Assert.Equal(
            "campaign: a keyboard host: the map's menu up: the game's any-player control on, the UI's controlling player nobody " +
            "(every player's press is taken here, as on a couch, with no ask: the rule has nothing to refuse)",
            CampaignTour.KeyboardHostControlLine("the map's menu up", anyPlayerControl: true, "nobody"));
        Assert.Equal(
            "campaign: a keyboard host: the map after its menu: the game's any-player control off, the UI's controlling player Rewired player 1, game player 1 (the pad) " +
            "(one player has the menus: the game asks before it takes another's press)",
            CampaignTour.KeyboardHostControlLine("the map after its menu", anyPlayerControl: false, "Rewired player 1, game player 1 (the pad)"));
    }

    [Fact]
    public void A_keyboard_host_step_with_no_keyboard_player_found_says_the_rule_had_nothing_to_refuse()
    {
        string? problem = CampaignTour.KeyboardHostProblem(hostOnKeyboard: false, refused: 0, refusedIdle: 0, cursorStayed: true);
        Assert.NotNull(problem);
        Assert.Contains("found no keyboard player of the host's own", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void A_keyboard_host_step_with_no_ask_refused_says_the_press_never_reached_the_ask()
    {
        Assert.Contains("no ask of the friend's was refused: their d-pad never reached the game's ask on the map", CampaignTour.KeyboardHostProblem(true, 0, 0, true), StringComparison.Ordinal);
        Assert.Contains("no ask of the friend's was refused", CampaignTour.KeyboardHostProblem(true, -3, 0, true), StringComparison.Ordinal);
    }

    [Fact]
    public void A_keyboard_host_step_with_refusals_while_nothing_was_pressed_fails_whatever_the_press_counted()
    {
        // Asks the game makes about the friend by itself fall under the press too: its refusals no longer show the press reached the ask.
        foreach (int refused in new[] { 1, 3, 40 })
        {
            string? problem = CampaignTour.KeyboardHostProblem(hostOnKeyboard: true, refused, refusedIdle: 3, cursorStayed: true);
            Assert.NotNull(problem);
            Assert.Contains("asked about the friend's player 3 time(s) with nothing pressed", problem, StringComparison.Ordinal);
            Assert.Contains("can't be told from those", problem, StringComparison.Ordinal);
        }

        // With none refused under the press, that is what's said.
        Assert.Contains("no ask of the friend's was refused", CampaignTour.KeyboardHostProblem(true, 0, 3, true), StringComparison.Ordinal);
    }

    [Fact]
    public void A_keyboard_host_step_whose_cursor_moved_fails_even_with_refusals_counted()
    {
        Assert.Contains("the cursor of the host's map moved under the friend's d-pad", CampaignTour.KeyboardHostProblem(true, 4, 0, false), StringComparison.Ordinal);
        Assert.EndsWith("refused 0 time(s) by the host-decides rule (3 in the same time before it, with nothing pressed); the cursor moved from MapNode to EliteNode",
            CampaignTour.KeyboardHostLine("none", -1, 3, false, "MapNode", "EliteNode"), StringComparison.Ordinal);
    }

    private static void Read(CampaignTourTally tally, string fields, double now, uint checkpoints = 1, long menus = 0)
    {
        Assert.True(WalkerLine.TryRead("[SoakTest:Campaign] run=1 act=1 " + fields, out WalkerLine line));
        tally.Read(line, 1, 1, now, checkpoints, menus);
    }
}
