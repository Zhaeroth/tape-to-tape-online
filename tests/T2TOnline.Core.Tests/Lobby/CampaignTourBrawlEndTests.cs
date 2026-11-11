using System;
using System.Collections.Generic;
using System.Linq;
using T2TOnline.Core.Lobby;

namespace T2TOnline.Core.Tests.Lobby;

/// <summary>The brawl key's <c>brawlend=game</c>: its rule with <c>brawl=on</c>, its plan, its arguments, its line and its verdict.</summary>
public class CampaignTourBrawlEndTests
{
    private static UiCheckRequest Parse(string line)
    {
        Assert.True(UiCheckRequest.TryParse(line, UiCheckPlan.Tours, out UiCheckRequest? request, out string problem), problem);
        return request!;
    }

    [Theory]
    [InlineData("campaign act1 brawl=on brawlend=game")]
    [InlineData("campaign full brawlend=game brawl=on")]
    [InlineData("campaign act1 brawl=on brawlend=tour")]
    public void Brawlend_adds_no_step_to_the_brawl_plan(string line)
    {
        IReadOnlyList<UiCheckStep> plan = CampaignTour.StepsFor(Parse(line));
        Assert.Same(CampaignTour.StepsWith(CampaignTourExtras.Brawl), plan);
        Assert.Equal(new[] { "cp_host", "cp_newrun", "cp_first", "cp_continue", "cp_nodes", "cp_brawl", "cp_end", "cp_close", "finish" }, plan.Select(s => s.Id).ToArray());
        Assert.Equal("campaign: the plan has 9 steps, cp_brawl after cp_nodes (brawl=on)", CampaignTour.PlanLine(plan));
    }

    [Fact]
    public void Kbhost_and_the_other_keys_go_with_it()
    {
        IReadOnlyList<UiCheckStep> plan = CampaignTour.StepsFor(Parse("campaign act1 kbhost=on events=on sides=on brawl=on brawlend=game"));
        Assert.Same(CampaignTour.StepsWith(CampaignTourExtras.KeyboardHost | CampaignTourExtras.Events | CampaignTourExtras.Sides | CampaignTourExtras.Brawl), plan);
        Assert.True(UiCheckRequest.TryParse("campaign act1 late=on brawl=on brawlend=game seed=2", UiCheckPlan.Tours, out _, out string problem), problem);
    }

    [Theory]
    [InlineData("campaign act1 brawlend=game", "brawlend=game needs brawl=on: the fight it ends is the one the brawl key's fresh offer begins")]
    [InlineData("campaign full brawl=off brawlend=game", "brawlend=game needs brawl=on: the fight it ends is the one the brawl key's fresh offer begins")]
    [InlineData("campaign act1 brawlend=tour kbhost=on", "brawlend=tour needs brawl=on: the fight it ends is the one the brawl key's fresh offer begins")]
    [InlineData("campaign act1 brawl=on brawlend=on", "\"brawlend\" takes game or tour, not \"on\"")]
    [InlineData("padwalk brawlend=game", "the tour \"padwalk\" takes no key \"brawlend\" (it takes endonline)")]
    public void Brawlend_without_brawl_on_or_with_another_value_is_refused(string line, string why)
    {
        Assert.False(UiCheckRequest.TryParse(line, UiCheckPlan.Tours, out UiCheckRequest? request, out string problem));
        Assert.Null(request);
        Assert.Equal(why, problem);
    }

    [Fact]
    public void Kbhost_with_late_is_still_said_first()
    {
        Assert.False(UiCheckRequest.TryParse("campaign act1 kbhost=on late=on brawlend=game", UiCheckPlan.Tours, out _, out string problem));
        Assert.StartsWith("kbhost=on and late=on can't both hold", problem, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("campaign act1 brawl=on brawlend=game", true)]
    [InlineData("campaign act1 brawl=on brawlend=tour", false)]
    [InlineData("campaign act1 brawl=on", false)]
    [InlineData("campaign act1", false)]
    public void The_argument_is_read_off_by_default(string line, bool byGame)
    {
        CampaignTourArgs args = CampaignTour.ArgsOf(Parse(line));
        Assert.Equal(byGame, args.BrawlEndByGame);
        Assert.Equal(CampaignTour.DefaultSeed, args.Seed);
        Assert.False(CampaignTour.ArgsOf(null).BrawlEndByGame);
    }

    private static CampaignBrawlEndTally Shown()
    {
        var tally = new CampaignBrawlEndTally();
        tally.Reset();
        tally.Match = 1;
        tally.MatchmakerOnAtEnd = tally.HudUpAtEnd = true;
        tally.GuardActs = 1;
        tally.GuardClean = true;
        tally.AfterRead = true;
        tally.TeamAtForcedEnd = 2;
        tally.ReadNextMatch(2, new[] { new CampaignGamePlayer(1, true), new CampaignGamePlayer(2, true) }, new[] { 1, 2 });
        return tally;
    }

    [Fact]
    public void The_guard_ending_it_and_every_player_on_a_skater_after_it_passes()
    {
        CampaignBrawlEndTally tally = Shown();
        Assert.Null(CampaignTour.BrawlEndProblem(tally));
        Assert.Equal(
            "campaign: brawlend=game: match 1 ended by the forced win with its fight on (matchmaker on, brawl HUD up), without the tour's own end; at its unload the mod's guard acted 1 time(s), cleanly; " +
            "on the map after it: matchmaker off, brawl HUD down; in match 2, 2 of 2 of the team's player(s) on a skater (the team's list held 2 in match 1); no brawl line after it said a match ended during a brawl it didn't have",
            CampaignTour.BrawlEndLine(tally));
    }

    [Fact]
    public void No_fight_left_on_fails_as_not_shown()
    {
        // The key asks for the guard's proof: a declined offer or a fight that never began must not read as a pass.
        var tally = new CampaignBrawlEndTally();
        Assert.Equal("brawlend=game: not shown (no fight began in the fresh offer's match): the mod's guard wasn't tested; run the tour again (another seed)",
            CampaignTour.BrawlEndProblem(tally));
        Assert.Equal("campaign: brawlend=game: not shown (no fight began in the fresh offer's match): the mod's guard wasn't tested", CampaignTour.BrawlEndLine(tally));

        tally = Shown();
        tally.GuardActs = 0;
        tally.NotShown = "the fight had ended before the forced end";
        Assert.Equal("brawlend=game: not shown (the fight had ended before the forced end): the mod's guard wasn't tested; run the tour again (another seed)",
            CampaignTour.BrawlEndProblem(tally));
        Assert.Equal("campaign: brawlend=game: not shown (the fight had ended before the forced end): the mod's guard wasn't tested", CampaignTour.BrawlEndLine(tally));
    }

    [Fact]
    public void A_guard_that_did_not_act_fails()
    {
        CampaignBrawlEndTally tally = Shown();
        tally.GuardActs = 0;
        Assert.Equal("brawlend=game: the mod's guard didn't act after match 1 ended with its fight on", CampaignTour.BrawlEndProblem(tally));
        Assert.Contains("at its unload the mod's guard didn't act;", CampaignTour.BrawlEndLine(tally), StringComparison.Ordinal);
    }

    [Fact]
    public void A_guard_that_did_not_end_it_all_fails()
    {
        CampaignBrawlEndTally tally = Shown();
        tally.GuardClean = false;
        Assert.Equal("brawlend=game: the mod's guard didn't end all of the fight state match 1 left on", CampaignTour.BrawlEndProblem(tally));
        Assert.Contains("acted 1 time(s), not cleanly (its WARN line says what stayed on)", CampaignTour.BrawlEndLine(tally), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void A_flag_still_on_on_the_map_fails(bool matchmaker, bool hud)
    {
        CampaignBrawlEndTally tally = Shown();
        tally.MatchmakerOnAfter = matchmaker;
        tally.HudUpAfter = hud;
        Assert.Equal("brawlend=game: the game's fight state was still on on the map after match 1", CampaignTour.BrawlEndProblem(tally));
        Assert.Contains($"matchmaker {(matchmaker ? "still on" : "off")}, brawl HUD {(hud ? "still up" : "down")}", CampaignTour.BrawlEndLine(tally), StringComparison.Ordinal);
    }

    [Fact]
    public void A_game_player_not_on_the_team_is_named_and_not_counted()
    {
        // The PC's idle keyboard is game player 0 in every tour and holds no skater in any match: it isn't on the run's team.
        CampaignBrawlEndTally tally = Shown();
        CampaignGamePlayer[] players = { new(0, false), new(1, true), new(2, true) };
        tally.ReadNextMatch(2, players, new[] { 1, 2 });
        Assert.Null(CampaignTour.BrawlEndProblem(tally));
        Assert.Equal((2, 0), (tally.TeamPlayers, tally.TeamWithoutSkater));
        Assert.Equal(new[] { 0 }, tally.NotOnTeam);
        Assert.Contains("; in match 2, 2 of 2 of the team's player(s) on a skater (the team's list held 2 in match 1; game player 0 isn't on the team); ",
            CampaignTour.BrawlEndLine(tally), StringComparison.Ordinal);
        Assert.Equal("campaign: brawlend=game: match 2 in play: game player 0 with no skater (not on the team), game player 1 on a skater, game player 2 on a skater; the run's team: game player(s) 1, 2",
            CampaignTour.BrawlEndPlayersLine(2, players, new[] { 1, 2 }));
        Assert.True(CampaignTour.TeamOnSkaters(players, new[] { 1, 2 }));

        // A friend who sat out is named the same way; two of them together.
        tally.TeamAtForcedEnd = -1;
        tally.ReadNextMatch(2, new CampaignGamePlayer[] { new(0, false), new(1, true), new(2, false) }, new[] { 1 });
        Assert.Null(CampaignTour.BrawlEndProblem(tally));
        Assert.Contains("; in match 2, 1 of 1 of the team's player(s) on a skater (game players 0, 2 aren't on the team); ", CampaignTour.BrawlEndLine(tally), StringComparison.Ordinal);
    }

    [Fact]
    public void A_team_player_with_no_skater_in_the_next_match_fails()
    {
        CampaignBrawlEndTally tally = Shown();
        CampaignGamePlayer[] players = { new(0, false), new(1, true), new(2, false) };
        tally.ReadNextMatch(2, players, new[] { 1, 2 });
        Assert.Equal("brawlend=game: in match 2, 1 of 2 of the team's player(s) had no skater", CampaignTour.BrawlEndProblem(tally));
        Assert.Contains("in match 2, 1 of 2 of the team's player(s) on a skater (the team's list held 2 in match 1; game player 0 isn't on the team)", CampaignTour.BrawlEndLine(tally), StringComparison.Ordinal);
        Assert.False(CampaignTour.TeamOnSkaters(players, new[] { 1, 2 }));

        tally.ReadNextMatch(2, new CampaignGamePlayer[] { new(1, false), new(2, false) }, new[] { 1, 2 });
        Assert.Equal("brawlend=game: in match 2, 2 of 2 of the team's player(s) had no skater", CampaignTour.BrawlEndProblem(tally));
        Assert.Contains("in match 2, 0 of 2 of the team's player(s) on a skater", CampaignTour.BrawlEndLine(tally), StringComparison.Ordinal);
    }

    [Fact]
    public void A_team_player_missing_from_the_match_counts_as_with_no_skater()
    {
        CampaignBrawlEndTally tally = Shown();
        CampaignGamePlayer[] players = { new(0, false), new(1, true) };
        tally.ReadNextMatch(2, players, new[] { 1, 2, 1 });
        Assert.Equal((2, 1), (tally.TeamPlayers, tally.TeamWithoutSkater)); // listed twice is one player
        Assert.Equal("brawlend=game: in match 2, 1 of 2 of the team's player(s) had no skater", CampaignTour.BrawlEndProblem(tally));
        Assert.EndsWith("; the run's team: game player(s) 1, 2 (not in the match), 1", CampaignTour.BrawlEndPlayersLine(2, players, new[] { 1, 2, 1 }), StringComparison.Ordinal);
        Assert.False(CampaignTour.TeamOnSkaters(players, new[] { 1, 2 }));
    }

    [Fact]
    public void No_team_player_read_fails()
    {
        // The list unread: three game players with skaters must not read as a pass.
        CampaignBrawlEndTally tally = Shown();
        CampaignGamePlayer[] players = { new(0, false), new(1, true), new(2, true) };
        tally.ReadNextMatch(2, players, null);
        Assert.False(tally.TeamRead);
        Assert.Empty(tally.NotOnTeam);
        Assert.Equal("brawlend=game: in match 2, none of the run's team's players read (the team's list couldn't be read)", CampaignTour.BrawlEndProblem(tally));
        Assert.Contains("; in match 2, none of the run's team's players read (the team's list couldn't be read); ", CampaignTour.BrawlEndLine(tally), StringComparison.Ordinal);
        Assert.EndsWith("game player 2 on a skater; the run's team's list unread", CampaignTour.BrawlEndPlayersLine(2, players, null), StringComparison.Ordinal);
        Assert.False(CampaignTour.TeamOnSkaters(players, null));

        // The list read and empty.
        tally.ReadNextMatch(2, players, Array.Empty<int>());
        Assert.Equal("brawlend=game: in match 2, none of the run's team's players read (the team's list was empty)", CampaignTour.BrawlEndProblem(tally));
        Assert.Equal(new[] { 0, 1, 2 }, tally.NotOnTeam);
        Assert.EndsWith("; the run's team: no player", CampaignTour.BrawlEndPlayersLine(2, players, Array.Empty<int>()), StringComparison.Ordinal);
        Assert.False(CampaignTour.TeamOnSkaters(players, Array.Empty<int>()));

        // The game players unread: the team's players count with no skater.
        tally.ReadNextMatch(2, null, new[] { 1, 2 });
        Assert.Equal("brawlend=game: in match 2, 2 of 2 of the team's player(s) had no skater", CampaignTour.BrawlEndProblem(tally));
        Assert.Equal("campaign: brawlend=game: match 2 in play: the game players unread; the run's team: game player(s) 1 (not in the match), 2 (not in the match)",
            CampaignTour.BrawlEndPlayersLine(2, null, new[] { 1, 2 }));
        Assert.False(CampaignTour.TeamOnSkaters(null, new[] { 1, 2 }));
    }

    [Fact]
    public void A_next_match_not_reached_fails_as_unproven()
    {
        CampaignBrawlEndTally tally = Shown();
        tally.NextMatch = 0;
        tally.TeamPlayers = 0;
        Assert.Equal("brawlend=game: the match after match 1 wasn't reached, so no player's skater was read after it", CampaignTour.BrawlEndProblem(tally));
        Assert.Contains("; the next match wasn't reached;", CampaignTour.BrawlEndLine(tally), StringComparison.Ordinal);
    }

    [Fact]
    public void The_flags_not_read_on_the_map_fail_as_unproven()
    {
        // The run broke before the map: the guard's act is read late, the flags never.
        CampaignBrawlEndTally tally = Shown();
        tally.AfterRead = false;
        tally.NextMatch = tally.TeamPlayers = 0;
        Assert.Equal("brawlend=game: the game's fight flags weren't read on the map after match 1 (the run didn't reach it)", CampaignTour.BrawlEndProblem(tally));
        Assert.Contains("; the flags weren't read on the map after it;", CampaignTour.BrawlEndLine(tally), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void A_later_brawl_line_made_by_a_left_over_flag_fails(int lines)
    {
        CampaignBrawlEndTally tally = Shown();
        tally.CutShortAfter = lines;
        Assert.Equal($"brawlend=game: {lines} brawl line(s) after match 1 said a match ended during a brawl it didn't have (a flag left on)", CampaignTour.BrawlEndProblem(tally));
        Assert.EndsWith($"; {lines} brawl line(s) after it said a match ended during a brawl it didn't have", CampaignTour.BrawlEndLine(tally), StringComparison.Ordinal);
    }

    [Fact]
    public void A_reset_forgets_everything()
    {
        CampaignBrawlEndTally tally = Shown();
        tally.NotShown = "x";
        tally.CutShortAfter = 3;
        tally.MatchmakerOnAfter = tally.HudUpAfter = true;
        tally.ReadNextMatch(2, new CampaignGamePlayer[] { new(0, false), new(1, false) }, new[] { 1 });
        tally.Reset();
        Assert.Equal(0, tally.Match + tally.NextMatch + tally.TeamPlayers + tally.TeamWithoutSkater + tally.GuardActs + tally.CutShortAfter);
        Assert.Equal(-1, tally.TeamAtForcedEnd);
        Assert.Empty(tally.NotOnTeam);
        Assert.Null(tally.NotShown);
        Assert.False(tally.MatchmakerOnAtEnd || tally.HudUpAtEnd || tally.GuardClean || tally.AfterRead || tally.MatchmakerOnAfter || tally.HudUpAfter || tally.TeamRead);
    }
}
