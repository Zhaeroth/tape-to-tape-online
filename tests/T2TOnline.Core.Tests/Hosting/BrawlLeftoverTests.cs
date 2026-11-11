using System;
using T2TOnline.Core.Hosting;

namespace T2TOnline.Core.Tests.Hosting;

/// <summary>The game's fight state a match leaves on: which piece the mod ends at the match scene's unload, and its one line.</summary>
public class BrawlLeftoverTests
{
    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Each_piece_that_is_on_is_ended_when_no_match_fights(bool matchmakerOn, bool hudUp)
    {
        BrawlLeftoverPlan plan = BrawlLeftover.Plan(matchmakerOn, hudUp, matchFighting: false, startPending: false);
        Assert.Equal(matchmakerOn, plan.EndMatchmaker);
        Assert.Equal(hudUp, plan.EndHud);
        Assert.True(plan.Acts);
        Assert.False(BrawlLeftover.LeftToLiveFight(matchmakerOn, hudUp, matchFighting: false, startPending: false));
    }

    [Fact]
    public void Nothing_on_is_nothing_to_do()
    {
        BrawlLeftoverPlan plan = BrawlLeftover.Plan(matchmakerOn: false, hudUp: false, matchFighting: false, startPending: false);
        Assert.False(plan.Acts);
        Assert.Equal(default, plan);
        Assert.False(BrawlLeftover.LeftToLiveFight(false, false, matchFighting: true, startPending: true));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void A_live_fight_or_an_accepted_start_keeps_the_state_as_it_is(bool matchFighting, bool startPending)
    {
        // The state is that fight's own: the guard never ends a fight that could be on.
        BrawlLeftoverPlan plan = BrawlLeftover.Plan(matchmakerOn: true, hudUp: true, matchFighting, startPending);
        Assert.False(plan.Acts);
        Assert.True(BrawlLeftover.LeftToLiveFight(true, true, matchFighting, startPending));
        Assert.True(BrawlLeftover.LeftToLiveFight(false, true, matchFighting, startPending));
        Assert.Equal(
            "Brawl: a match's scene unloaded with the game's fight state on (matchmaker was on, brawl HUD was up) while a live match is fighting or about to: left to that fight.",
            BrawlLeftover.LeftAloneLine(true, true));
    }

    [Fact]
    public void Both_ended_reads_as_the_next_match_playing_as_usual()
    {
        BrawlLeftoverReport report = BrawlLeftover.Report(new BrawlLeftoverPlan(true, true), matchmakerOnAfter: false, hudUpAfter: false, problem: null);
        Assert.True(report.Clean);
        Assert.Equal(
            "Brawl: a match ended during a fight; the game's fight state ended by its own calls (matchmaker was on, brawl HUD was up): " +
            "the next match's players take skaters and the skaters' icons show as usual.",
            report.Line);
    }

    [Fact]
    public void One_piece_ended_names_only_that_one()
    {
        // A friend's PC: the matchmaker is never set there, only the brawl HUD its mirror started.
        BrawlLeftoverReport hud = BrawlLeftover.Report(new BrawlLeftoverPlan(false, true), false, false, null);
        Assert.True(hud.Clean);
        Assert.Equal("Brawl: a match ended during a fight; the game's fight state ended by its own calls (brawl HUD was up): the skaters' icons show as usual.", hud.Line);

        BrawlLeftoverReport matchmaker = BrawlLeftover.Report(new BrawlLeftoverPlan(true, false), false, false, null);
        Assert.True(matchmaker.Clean);
        Assert.Equal("Brawl: a match ended during a fight; the game's fight state ended by its own calls (matchmaker was on): the next match's players take skaters as usual.", matchmaker.Line);
    }

    [Fact]
    public void A_failed_call_says_what_failed_and_what_stayed_on()
    {
        BrawlLeftoverReport report = BrawlLeftover.Report(new BrawlLeftoverPlan(true, true), matchmakerOnAfter: true, hudUpAfter: false,
            problem: "the matchmaker's end threw (NullReferenceException)");
        Assert.False(report.Clean);
        Assert.Equal(
            "Brawl: a match ended during a fight (matchmaker was on, brawl HUD was up); the game's own calls didn't end all of its fight state " +
            "(the matchmaker's end threw (NullReferenceException); matchmaker still on, brawl HUD down): the next match may start with no player on a skater.",
            report.Line);
    }

    [Fact]
    public void The_hud_left_up_alone_says_the_icons_may_stay_hidden()
    {
        BrawlLeftoverReport report = BrawlLeftover.Report(new BrawlLeftoverPlan(false, true), matchmakerOnAfter: false, hudUpAfter: true,
            problem: "the brawl HUD's end threw (InvalidCastException)");
        Assert.False(report.Clean);
        Assert.Contains("(the brawl HUD's end threw (InvalidCastException); matchmaker off, brawl HUD still up): the next match's players take skaters, but the skaters' icons may stay hidden.",
            report.Line, StringComparison.Ordinal);
    }

    [Fact]
    public void A_failed_call_with_both_off_afterwards_is_still_not_clean()
    {
        // The HUD's end clears its flag before it raises its event: a throw from a listener leaves the flag off.
        BrawlLeftoverReport report = BrawlLeftover.Report(new BrawlLeftoverPlan(true, true), false, false, "the brawl HUD's end threw (NullReferenceException)");
        Assert.False(report.Clean);
        Assert.EndsWith("(the brawl HUD's end threw (NullReferenceException); matchmaker off, brawl HUD down): both are off now, so the next match plays as usual.", report.Line, StringComparison.Ordinal);
    }

    [Fact]
    public void A_flag_still_on_with_no_failure_is_not_clean()
    {
        // A call that returned but left its piece on (a game update that changed it): said, with no failure named.
        BrawlLeftoverReport report = BrawlLeftover.Report(new BrawlLeftoverPlan(true, false), matchmakerOnAfter: true, hudUpAfter: false, problem: null);
        Assert.False(report.Clean);
        Assert.Contains("didn't end all of its fight state (matchmaker still on, brawl HUD down): the next match may start with no player on a skater.", report.Line, StringComparison.Ordinal);

        BrawlLeftoverReport hud = BrawlLeftover.Report(new BrawlLeftoverPlan(false, true), matchmakerOnAfter: false, hudUpAfter: true, problem: null);
        Assert.False(hud.Clean);
        Assert.EndsWith("(matchmaker off, brawl HUD still up): the next match's players take skaters, but the skaters' icons may stay hidden.", hud.Line, StringComparison.Ordinal);
    }
}
