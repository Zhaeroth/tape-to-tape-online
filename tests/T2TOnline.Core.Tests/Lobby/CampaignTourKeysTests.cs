using System;
using System.Collections.Generic;
using System.Linq;
using T2TOnline.Core.Lobby;

namespace T2TOnline.Core.Tests.Lobby;

/// <summary>The campaign tours' opt-in keys (events, late, sides, brawl): each one's plan, the combinations allowed and refused, and their lines.</summary>
public class CampaignTourKeysTests
{
    private static readonly string[] OwnSteps = { "cp_host", "cp_newrun", "cp_first", "cp_continue", "cp_nodes", "cp_end", "cp_close", "finish" };

    private static IReadOnlyList<UiCheckStep> PlanOf(string line)
    {
        Assert.True(UiCheckRequest.TryParse(line, UiCheckPlan.Tours, out UiCheckRequest? request, out string problem), problem);
        return CampaignTour.StepsFor(request!);
    }

    [Theory]
    [InlineData("campaign act1 events=off late=off sides=off brawl=off")]
    [InlineData("campaign full events=off")]
    [InlineData("campaign act1 seed=4 record=on brawl=off")]
    public void Every_new_key_off_leaves_the_tours_own_steps(string line)
    {
        IReadOnlyList<UiCheckStep> plan = PlanOf(line);
        Assert.Same(CampaignTour.Steps, plan);
        Assert.Equal(OwnSteps, plan.Select(s => s.Id).ToArray());
        Assert.Equal("campaign: the plan has 8 steps, without cp_kbhost (kbhost=on adds it)", CampaignTour.PlanLine(plan));
    }

    [Fact]
    public void Events_on_adds_the_events_step_after_the_first_match()
    {
        IReadOnlyList<UiCheckStep> plan = PlanOf("campaign act1 events=on");
        Assert.Null(UiCheckPlan.Problem(plan));
        Assert.Equal(new[] { "cp_host", "cp_newrun", "cp_first", "cp_events", "cp_continue", "cp_nodes", "cp_end", "cp_close", "finish" }, plan.Select(s => s.Id).ToArray());
        Assert.True(CampaignTour.IsCampaignPlan(plan));
        Assert.Equal(CampaignTourExtras.Events, CampaignTour.ExtrasOfPlan(plan));
        Assert.Equal("campaign: the plan has 9 steps, cp_events after cp_first (events=on)", CampaignTour.PlanLine(plan));
    }

    [Fact]
    public void Late_on_adds_the_late_joiners_step_after_the_continue()
    {
        IReadOnlyList<UiCheckStep> plan = PlanOf("campaign act1 late=on");
        Assert.Null(UiCheckPlan.Problem(plan));
        Assert.Equal(new[] { "cp_host", "cp_newrun", "cp_first", "cp_continue", "cp_late", "cp_nodes", "cp_end", "cp_close", "finish" }, plan.Select(s => s.Id).ToArray());
        Assert.Equal("campaign: the plan has 9 steps, cp_late after cp_continue (late=on)", CampaignTour.PlanLine(plan));
    }

    [Fact]
    public void Sides_on_adds_the_sides_step_before_the_drops_nodes()
    {
        IReadOnlyList<UiCheckStep> plan = PlanOf("campaign full sides=on");
        Assert.Null(UiCheckPlan.Problem(plan));
        Assert.Equal(new[] { "cp_host", "cp_newrun", "cp_first", "cp_continue", "cp_sides", "cp_nodes", "cp_end", "cp_close", "finish" }, plan.Select(s => s.Id).ToArray());
        Assert.Equal("campaign: the plan has 9 steps, cp_sides after cp_continue (sides=on)", CampaignTour.PlanLine(plan));
    }

    [Fact]
    public void Brawl_on_adds_the_brawl_step_after_the_nodes()
    {
        IReadOnlyList<UiCheckStep> plan = PlanOf("campaign act1 brawl=on");
        Assert.Null(UiCheckPlan.Problem(plan));
        Assert.Equal(new[] { "cp_host", "cp_newrun", "cp_first", "cp_continue", "cp_nodes", "cp_brawl", "cp_end", "cp_close", "finish" }, plan.Select(s => s.Id).ToArray());
        Assert.Equal("campaign: the plan has 9 steps, cp_brawl after cp_nodes (brawl=on)", CampaignTour.PlanLine(plan));
    }

    [Fact]
    public void The_allowed_keys_together_put_each_step_in_its_place()
    {
        IReadOnlyList<UiCheckStep> plan = PlanOf("campaign act1 brawl=on sides=on late=on events=on");
        Assert.Null(UiCheckPlan.Problem(plan));
        Assert.Equal(new[] { "cp_host", "cp_newrun", "cp_first", "cp_events", "cp_continue", "cp_late", "cp_sides", "cp_nodes", "cp_brawl", "cp_end", "cp_close", "finish" }, plan.Select(s => s.Id).ToArray());
        Assert.Equal(
            "campaign: the plan has 12 steps, cp_events after cp_first (events=on), cp_late after cp_continue (late=on), cp_sides after cp_late (sides=on), cp_brawl after cp_nodes (brawl=on)",
            CampaignTour.PlanLine(plan));
        Assert.Same(plan, CampaignTour.KeyedSteps);
    }

    [Fact]
    public void Kbhost_goes_with_events_sides_and_brawl()
    {
        IReadOnlyList<UiCheckStep> plan = PlanOf("campaign full kbhost=on events=on sides=on brawl=on");
        Assert.Equal(new[] { "cp_host", "cp_newrun", "cp_first", "cp_kbhost", "cp_events", "cp_continue", "cp_sides", "cp_nodes", "cp_brawl", "cp_end", "cp_close", "finish" }, plan.Select(s => s.Id).ToArray());
        Assert.StartsWith("campaign: the plan has 12 steps, cp_kbhost after cp_first (kbhost=on), cp_events after cp_kbhost (events=on)", CampaignTour.PlanLine(plan), StringComparison.Ordinal);
    }

    [Fact]
    public void Kbhost_alone_keeps_its_plan_and_line()
    {
        IReadOnlyList<UiCheckStep> plan = PlanOf("campaign act1 kbhost=on events=off late=off");
        Assert.Same(CampaignTour.StepsWithKeyboardHost, plan);
        Assert.Equal("campaign: the plan has 9 steps, cp_kbhost after cp_first (kbhost=on)", CampaignTour.PlanLine(plan));
    }

    [Theory]
    [InlineData("campaign act1 kbhost=on late=on")]
    [InlineData("campaign full late=on events=on kbhost=on")]
    public void Kbhost_with_late_is_refused_before_anything_runs(string line)
    {
        Assert.False(UiCheckRequest.TryParse(line, UiCheckPlan.Tours, out UiCheckRequest? request, out string problem));
        Assert.Null(request);
        Assert.Equal(
            "kbhost=on and late=on can't both hold: the keyboard host step needs the friend seated on the map right after the first match, and late=on seats them only after the Continue",
            problem);
    }

    [Theory]
    [InlineData("campaign act1 events=yes", "\"events\" takes on or off, not \"yes\"")]
    [InlineData("campaign act1 late=1", "\"late\" takes on or off, not \"1\"")]
    [InlineData("padwalk brawl=on", "the tour \"padwalk\" takes no key \"brawl\" (it takes endonline)")]
    [InlineData("onepc sides=on", "the tour \"onepc\" takes no key \"sides\" (it takes dc)")]
    [InlineData("kbwalk late=on", "the tour \"kbwalk\" takes no arguments (late)")]
    public void A_new_key_with_another_value_or_on_another_tour_is_refused(string line, string why)
    {
        Assert.False(UiCheckRequest.TryParse(line, UiCheckPlan.Tours, out _, out string problem));
        Assert.Equal(why, problem);
    }

    [Fact]
    public void Every_allowed_combination_has_one_plan_with_no_problem_and_every_refused_one_none()
    {
        int plans = 0;
        foreach (CampaignTourExtras extras in Enumerable.Range(0, 32).Select(i => (CampaignTourExtras)i))
        {
            if (CampaignTour.ExtrasProblem(extras) != null)
            {
                Assert.Throws<ArgumentException>(() => CampaignTour.StepsWith(extras));
                continue;
            }

            IReadOnlyList<UiCheckStep> plan = CampaignTour.StepsWith(extras);
            plans++;
            Assert.Same(plan, CampaignTour.StepsWith(extras));
            Assert.Null(UiCheckPlan.Problem(plan));
            Assert.Equal(extras, CampaignTour.ExtrasOfPlan(plan));
            Assert.Equal(OwnSteps, plan.Where(s => CampaignTour.KeyOfStep(s.Id) == null).Select(s => s.Id).ToArray());
            Assert.All(plan, s => Assert.NotEqual(TourPlace.None, TourStarts.Of(s.Id)));
            Assert.All(plan.Take(plan.Count - 1), s => Assert.True(s.Needs.HasFlag(UiCheckNeeds.PlayersSettings), s.Id));
        }

        Assert.Equal(24, plans);
        Assert.Null(CampaignTour.ExtrasOfPlan(UiCheckPlan.PadWalk));
        Assert.Null(CampaignTour.ExtrasOfPlan(null));
    }

    [Fact]
    public void The_steps_keys_add_start_where_they_run()
    {
        Assert.Equal(TourPlace.Map, TourStarts.Of(CampaignTour.EventsStep));
        Assert.Equal(TourPlace.Map, TourStarts.Of(CampaignTour.LateJoinerStep));
        Assert.Equal(TourPlace.Map, TourStarts.Of(CampaignTour.SidesStep));
        Assert.Equal(TourPlace.Map | TourPlace.Lobby, TourStarts.Of(CampaignTour.BrawlStep));
        Assert.All(new[] { CampaignTour.EventsStep, CampaignTour.LateJoinerStep, CampaignTour.SidesStep, CampaignTour.BrawlStep }, id => Assert.False(TourStarts.EndsAtMain(id)));
    }

    [Fact]
    public void The_extras_are_read_from_the_keys_set_on()
    {
        var arguments = new List<KeyValuePair<string, string>>
        {
            new("seed", "3"), new("events", "on"), new("late", "off"), new("sides", "on"), new("brawl", "on"), new("kbhost", "off"),
        };
        Assert.Equal(CampaignTourExtras.Events | CampaignTourExtras.Sides | CampaignTourExtras.Brawl, CampaignTour.ExtrasOf(arguments));
        Assert.Null(CampaignTour.KeysCheck(arguments));
        Assert.Equal(CampaignTourExtras.None, CampaignTour.ExtrasOf(new List<KeyValuePair<string, string>>()));
    }

    [Fact]
    public void A_step_asked_to_stop_the_tour_stops_it_after_itself_with_its_reason()
    {
        var gate = new TourGate();
        Assert.Equal(TourGateKind.Run, gate.Before(CampaignTour.EventsStep, TourPlace.Map, null).Kind);
        gate.StopAfter(CampaignTour.EventsStep, "an event screen the pad couldn't leave");
        Assert.True(gate.Stopped);
        Assert.Equal(("after", CampaignTour.EventsStep), gate.StoppedAt);
        Assert.Equal("an event screen the pad couldn't leave", gate.After(CampaignTour.EventsStep, TourPlace.Map, null));
        gate.StopAfter("cp_continue", "a second reason");
        Assert.Equal("an event screen the pad couldn't leave", gate.StopReason);
        Assert.Equal(TourGateKind.Stop, gate.Before("cp_continue", TourPlace.Map, null).Kind);
    }

    // ---- The late joiner ----

    [Fact]
    public void A_late_joiner_seated_and_on_the_teams_list_passes()
    {
        Assert.Null(CampaignTour.LateJoinerProblem(here: true, gamePlayer: 2, onTeamList: true, overMap: null));
        Assert.Equal(
            "campaign: late=on: the friend seated on the map: Here after 3.1 s; game player 2 by their own A after 0.4 s; on the run's team's list; the game remembers them on the team (right); nothing over the map",
            CampaignTour.LateJoinerLine(3.1, 2, 0.4, true, "on the team (right)", "nothing over the map"));
    }

    [Theory]
    [InlineData(true, 2, true, "ControllerAssignment", "opened ControllerAssignment, which the tour didn't ask for")]
    [InlineData(false, -1, false, null, "never came to Here")]
    [InlineData(true, -1, false, null, "made no game player")]
    [InlineData(true, 2, false, null, "isn't on the run's team's list")]
    public void A_late_joiner_that_isnt_seated_says_why(bool here, int gamePlayer, bool onList, string? overMap, string why)
    {
        Assert.Contains(why, CampaignTour.LateJoinerProblem(here, gamePlayer, onList, overMap), StringComparison.Ordinal);
    }

    [Fact]
    public void The_late_joiners_first_match_line_says_when_or_that_it_never_came()
    {
        Assert.Equal("campaign: late=on: the late joiner's skater in live play 4.2 s after match 2 came into play (game player 2: on a team, a skater)",
            CampaignTour.LateMatchLine(2, 4.2, "game player 2: on a team, a skater"));
        Assert.Equal("campaign: late=on: the late joiner held no skater in live play in match 2 within 45 s (game player 2: on no team, no skater)",
            CampaignTour.LateMatchLine(2, null, "game player 2: on no team, no skater"));
    }

    // ---- Events ----

    [Fact]
    public void The_event_kinds_are_the_developer_keys_nine_in_its_order()
    {
        Assert.Equal(new[] { "CoachEvent", "RegularPlayerTraining", "StatsTraining", "TeamTraining", "ChaosEvent3Choice", "BlademasterEvent", "GeneralManagerEvent", "FanNumber1Event", "NurseEvent" },
            CampaignTour.EventKinds);
    }

    [Fact]
    public void An_events_line_names_a_known_kind_and_only_the_shape_of_anything_else()
    {
        Assert.Equal("campaign: events=on: event 7 (GeneralManagerEvent): closed by the pad in 12.4 s; 9 press(es), 1 choice(s) made; the team's skaters 8 -> 9",
            CampaignTour.EventLine(7, "GeneralManagerEvent", CampaignEventEnd.Closed, 12.4, 9, 1, 8, 9));
        Assert.Equal("campaign: events=on: event 2 (an event of 11 characters): not opened (the key's own line says why) in 0.0 s; 0 press(es), 0 choice(s) made; the team's skaters ? -> ?",
            CampaignTour.EventLine(2, "Alex's team", CampaignEventEnd.NotOpened, -1, -2, 0, -1, -1));

        // The way out with no pick is the screen's skip shortcut, not B (the event screen's back does nothing).
        Assert.Equal("campaign: events=on: event 4 (ChaosEvent3Choice): left with no pick by the pad's skip (its picks changed nothing) in 9.1 s; 8 press(es), 0 choice(s) made; the team's skaters 8 -> 8",
            CampaignTour.EventLine(4, "ChaosEvent3Choice", CampaignEventEnd.LeftByBack, 9.1, 8, 0, 8, 8));
    }

    [Fact]
    public void The_events_step_passes_with_every_event_left_and_a_change_to_the_team()
    {
        Assert.Null(CampaignTour.EventsProblem(opened: 8, closed: 8, signings: 1, losses: 0));
        Assert.Null(CampaignTour.EventsProblem(opened: 9, closed: 9, signings: 0, losses: 1));
        Assert.Equal("campaign: events=on: 9 called, 8 opened, 8 left by the pad (1 with no pick); the team's skaters 8 -> 8: 1 signing(s), 1 loss(es) on the way",
            CampaignTour.EventsLine(9, 8, 8, 1, 8, 8, 1, 1));
    }

    [Theory]
    [InlineData(0, 0, 0, 0, "opened no event screen")]
    [InlineData(9, 8, 1, 1, "1 of 9 event screen(s) weren't left by the pad")]
    [InlineData(9, 9, 0, 0, "no event's pick changed the team")]
    public void The_events_step_fails_with_why(int opened, int closed, int signings, int losses, string why)
    {
        Assert.Contains(why, CampaignTour.EventsProblem(opened, closed, signings, losses), StringComparison.Ordinal);
    }

    // ---- Sides ----

    [Fact]
    public void A_choice_saved_and_read_back_passes_both_ways()
    {
        Assert.Null(CampaignTour.SidesProblem(CampaignTour.SideCenter, moved: true, saved: true, rememberedSide: CampaignTour.SideCenter, friendOnList: false));
        Assert.Null(CampaignTour.SidesProblem(CampaignTour.SideRight, moved: true, saved: true, rememberedSide: CampaignTour.SideRight, friendOnList: true));
        Assert.Equal(
            "campaign: sides=on: the friend (game player 2) to the middle (not playing) by their own press; saved by the host's A: the screen closed; the game remembers them on the middle (not playing); the team's list 1 player(s), the friend not on it",
            CampaignTour.SidesLine(2, CampaignTour.SideCenter, true, true, "on the middle (not playing)", 1, false));
    }

    [Theory]
    [InlineData(1, false, true, 2, true, "didn't move them to the middle (not playing)")]
    [InlineData(1, true, false, 2, true, "didn't save")]
    [InlineData(1, true, true, 2, true, "remembers the friend on the team (right), not the middle (not playing)")]
    [InlineData(1, true, true, 1, true, "holds the friend after saving them on the middle (not playing)")]
    [InlineData(2, true, true, 2, false, "lacks the friend after saving them on the team (right)")]
    public void A_choice_not_kept_says_why(int wanted, bool moved, bool saved, int remembered, bool onList, string why)
    {
        Assert.Contains(why, CampaignTour.SidesProblem(wanted, moved, saved, remembered, onList), StringComparison.Ordinal);
    }

    // ---- Brawl ----

    [Fact]
    public void Brawl_offers_taken_fresh_and_refused_while_held_pass()
    {
        var tally = new CampaignBrawlTally();
        tally.Reset();
        tally.Fresh = CampaignBrawlOutcome.Taken;
        tally.FreshMatch = 1;
        tally.Held = CampaignBrawlOutcome.NotTaken;
        tally.OfferOpenAtHandBack = true;
        tally.AfterLetGo = CampaignBrawlOutcome.Taken;
        tally.Calls = 5;
        tally.Declined = 1;
        tally.Brawls = 2;
        Assert.Null(CampaignTour.BrawlProblem(tally));
        Assert.Equal(
            "campaign: brawl=on: in match 1 the friend's fresh LB+RB: taken; in the drop match, LB+RB held through the hand-back with an offer open: not taken while held, once let go and pressed again taken; the key's call made 5 time(s), declined 1; brawls begun 2",
            CampaignTour.BrawlLine(tally));
    }

    [Fact]
    public void A_declined_or_untried_offer_is_a_note_not_a_failure()
    {
        var tally = new CampaignBrawlTally { Fresh = CampaignBrawlOutcome.Declined, FreshMatch = 1, Held = CampaignBrawlOutcome.Declined, Calls = 8, Declined = 8 };
        Assert.Null(CampaignTour.BrawlProblem(tally));
        Assert.Contains("in match 1 the friend's fresh LB+RB: no open offer to press on (the game declined each call, or its offer ran out first)", CampaignTour.BrawlLine(tally), StringComparison.Ordinal);
        Assert.Null(CampaignTour.BrawlProblem(new CampaignBrawlTally()));
        Assert.Contains("not tried", CampaignTour.BrawlLine(new CampaignBrawlTally()), StringComparison.Ordinal);

        // No offer was open while it was held, then one opened for the fresh press: both said.
        tally.AfterLetGo = CampaignBrawlOutcome.Taken;
        Assert.Contains("in the drop match: no offer open while LB+RB was held (the game declined each call), once let go and pressed again taken;", CampaignTour.BrawlLine(tally), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(CampaignBrawlOutcome.NotTaken, CampaignBrawlOutcome.NotTaken, CampaignBrawlOutcome.Taken, "wasn't taken by the friend's fresh LB+RB")]
    [InlineData(CampaignBrawlOutcome.Taken, CampaignBrawlOutcome.TakenWhileHeld, CampaignBrawlOutcome.NotTried, "taken while the friend held LB+RB")]
    [InlineData(CampaignBrawlOutcome.Taken, CampaignBrawlOutcome.FiredWhileHeld, CampaignBrawlOutcome.NotTried, "held through the hand-back counted as a fresh combo")]
    [InlineData(CampaignBrawlOutcome.Taken, CampaignBrawlOutcome.NotTaken, CampaignBrawlOutcome.NotTaken, "let go and pressed again didn't take")]
    public void A_brawl_offer_mishandled_fails_with_why(CampaignBrawlOutcome fresh, CampaignBrawlOutcome held, CampaignBrawlOutcome afterLetGo, string why)
    {
        var tally = new CampaignBrawlTally { Fresh = fresh, FreshMatch = 1, Held = held, AfterLetGo = afterLetGo };
        Assert.Contains(why, CampaignTour.BrawlProblem(tally), StringComparison.Ordinal);
    }

    [Fact]
    public void A_press_is_taken_only_when_the_relay_accepted_an_offer_for_it()
    {
        var before = new BrawlRelayCounts(Combos: 4, Accepts: 1, NoOffer: 2);

        // The relay accepted: taken, whatever the game's own flag says by the time it's read (the brawl's start clears it).
        Assert.Equal(CampaignBrawlOutcome.Taken, CampaignTour.BrawlPressOutcome(before, new BrawlRelayCounts(5, 2, 2)));

        // The press never reached the gate as a combo: not taken.
        Assert.Equal(CampaignBrawlOutcome.NotTaken, CampaignTour.BrawlPressOutcome(before, before));

        // The combo came and the gate refused it with an offer open (a fight on, a goal just scored): not taken.
        Assert.Equal(CampaignBrawlOutcome.NotTaken, CampaignTour.BrawlPressOutcome(before, new BrawlRelayCounts(5, 1, 2)));

        // The offer had run out by the time the combo reached the host: the game's own gate, a note.
        Assert.Equal(CampaignBrawlOutcome.Declined, CampaignTour.BrawlPressOutcome(before, new BrawlRelayCounts(5, 1, 3)));

        // Two combos, one of them with no offer open and one refused otherwise: not taken.
        Assert.Equal(CampaignBrawlOutcome.NotTaken, CampaignTour.BrawlPressOutcome(before, new BrawlRelayCounts(6, 1, 3)));
    }

    [Fact]
    public void A_held_combo_breaks_the_rule_whenever_it_counts()
    {
        var before = new BrawlRelayCounts(Combos: 1, Accepts: 1, NoOffer: 0);
        Assert.Equal(CampaignBrawlOutcome.NotTaken, CampaignTour.BrawlHeldOutcome(before, before, offerOpen: true));
        Assert.Equal(CampaignBrawlOutcome.Declined, CampaignTour.BrawlHeldOutcome(before, before, offerOpen: false));
        Assert.Equal(CampaignBrawlOutcome.TakenWhileHeld, CampaignTour.BrawlHeldOutcome(before, new BrawlRelayCounts(2, 2, 0), offerOpen: true));
        Assert.Equal(CampaignBrawlOutcome.FiredWhileHeld, CampaignTour.BrawlHeldOutcome(before, new BrawlRelayCounts(2, 1, 1), offerOpen: false));
        Assert.Equal(CampaignBrawlOutcome.FiredWhileHeld, CampaignTour.BrawlHeldOutcome(before, new BrawlRelayCounts(2, 1, 0), offerOpen: true));

        var tally = new CampaignBrawlTally { Fresh = CampaignBrawlOutcome.Taken, FreshMatch = 1, Held = CampaignBrawlOutcome.FiredWhileHeld, OfferOpenAtHandBack = true };
        Assert.Contains("held through the hand-back with an offer open: counted as a fresh combo while held, no fresh press after it", CampaignTour.BrawlLine(tally), StringComparison.Ordinal);
    }

    [Fact]
    public void A_fight_left_on_by_an_earlier_match_is_not_this_match_s()
    {
        // The game's matchmaker is still on from a match that ended during a fight: nothing is under way in this one.
        Assert.False(CampaignTour.BrawlUnderWay(fightState: false, pendingStart: false, begunThisMatch: false, matchmakerOn: true));
        Assert.Equal(CampaignFightEnd.Nothing, CampaignTour.BrawlBeforeForcedEnd(underWay: false, overran: false));

        // This match's own fight: its state, an accepted offer waiting, or its end sequence still running after the state left.
        Assert.True(CampaignTour.BrawlUnderWay(fightState: true, pendingStart: false, begunThisMatch: false, matchmakerOn: false));
        Assert.True(CampaignTour.BrawlUnderWay(fightState: false, pendingStart: true, begunThisMatch: false, matchmakerOn: false));
        Assert.True(CampaignTour.BrawlUnderWay(fightState: false, pendingStart: false, begunThisMatch: true, matchmakerOn: true));
        Assert.False(CampaignTour.BrawlUnderWay(fightState: false, pendingStart: false, begunThisMatch: true, matchmakerOn: false));
    }

    [Fact]
    public void A_fight_is_waited_out_once_and_past_its_limit_its_shared_state_is_ended()
    {
        Assert.Equal(CampaignFightEnd.WaitOut, CampaignTour.BrawlBeforeForcedEnd(underWay: true, overran: false));
        Assert.Equal(CampaignFightEnd.EndSharedState, CampaignTour.BrawlBeforeForcedEnd(underWay: true, overran: true));
        Assert.Equal(CampaignFightEnd.Nothing, CampaignTour.BrawlBeforeForcedEnd(underWay: false, overran: true));
    }

    [Fact]
    public void The_forced_end_s_line_says_what_stayed_on_and_what_it_means_for_the_matches_after()
    {
        Assert.Equal(
            "campaign: brawl=on: before the forced end, the fight's shared state ended by the game's own calls " +
            "(6 fighter(s) out of fight mode, its matchmaker off, its brawl HUD down): the matches after play as usual",
            CampaignTour.BrawlSharedStateLine(6, matchmakerOn: false, hudUp: false, problem: null));

        // The matchmaker is what refuses every player a skater in the matches after: left on, the line says so.
        string stillOn = CampaignTour.BrawlSharedStateLine(0, matchmakerOn: true, hudUp: true, problem: "the matchmaker's end threw (NullReferenceException)");
        Assert.Contains("the game's own calls didn't end all of the fight's shared state (0 fighter(s) out of fight mode, its matchmaker still on, its brawl HUD still up; " +
            "the matchmaker's end threw (NullReferenceException)): the matches after may start with no player on a skater", stillOn, StringComparison.Ordinal);
        Assert.DoesNotContain("play as usual", stillOn, StringComparison.Ordinal);

        // One fighter couldn't be taken out of fight mode, the matchmaker was ended all the same: said, and not as a clean end.
        string oneFailed = CampaignTour.BrawlSharedStateLine(5, matchmakerOn: false, hudUp: false, problem: "a fighter couldn't be taken out of fight mode (NullReferenceException)");
        Assert.Contains("didn't end all of the fight's shared state (5 fighter(s) out of fight mode, its matchmaker off, its brawl HUD down; a fighter couldn't be taken out of fight mode (NullReferenceException)): " +
            "the matchmaker is off, so the matches after have their players on skaters", oneFailed, StringComparison.Ordinal);
    }
}
