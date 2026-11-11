using System;
using System.Collections.Generic;
using T2TOnline.Core.Protocol;
using T2TOnline.Core.Serialization;
using T2TOnline.Core.Session;
using T2TOnline.Core.Stream;

namespace T2TOnline.Core.Tests.Session;

/// <summary>
/// D135: a friend judges a shared moment's win by the game's own rule for the match's winner (<see cref="MatchWinRules"/>),
/// from its own run, its own copy of the visitors and the score its own stream showed: a win the game gives without a lead
/// counts there, and a win a host claims that those don't support doesn't. Nothing a host sends says who won.
/// </summary>
public class MatchWinRuleTests
{
    private const ulong Session = 0x77;
    private static readonly DateTime At = new(2026, 11, 10, 21, 0, 0, DateTimeKind.Utc);

    private static readonly WinFacts Plain = new(false, false, false, false, 3);

    // A match this PC played enough of (20 s or more), seen to end, with the winner's rule read here.
    private static MatchView Ended(MatchKind kind, int act, int home, int visitor, WinRule rule, int target = 0, MatchSigns signs = MatchSigns.None) =>
        new MatchView(kind, act, 5, true, home, visitor, signs | MatchSigns.PlayedEnough) { Rule = rule, GoalTarget = target };

    private static SharedPlayGate Gate(int match, MatchView view)
    {
        var gate = new SharedPlayGate(new SharedPlayRecord());
        gate.Welcomed(Session);
        gate.NoteMatch(match, view, At, out _);
        gate.NoteRun(At, out _);
        return gate;
    }

    private static MomentReason Judge(string name, MatchView view)
    {
        bool run = SharedMoments.SourceOf(name) == MomentSource.Run;
        return MomentCheck.Judge(new AchievementMoment(name, 4, run), Gate(4, view));
    }

    private static List<KeyValuePair<string, NetValue>> Setup(string? endConditions)
    {
        var setup = new List<KeyValuePair<string, NetValue>>
        {
            new(MatchSetupValues.HostSerialKey, NetValue.FromInt(4)),
            new(CampaignMatchValues.GameTypeKey, NetValue.FromInt(CampaignMatchValues.CampaignGameType)),
        };
        if (endConditions != null)
        {
            setup.Add(new(MatchWinRules.EndConditionsKey, NetValue.FromString(endConditions)));
        }

        return setup;
    }

    [Fact]
    public void The_game_s_five_rules_apply_in_its_order()
    {
        // Nothing changes the last rule: ahead.
        Assert.Equal(WinRule.Ahead, MatchWinRules.Of(false, Plain));

        // A match ending at the next goal is the lead's, whatever the relics and squad.
        Assert.Equal(WinRule.Ahead, MatchWinRules.Of(true, new WinFacts(true, true, true, true, 3)));

        // The speedrun squad, or its curse on the home team: the goal target, over Tied Homie on either side.
        Assert.Equal(WinRule.GoalTarget, MatchWinRules.Of(false, Plain with { SpeedrunSquad = true }));
        Assert.Equal(WinRule.GoalTarget, MatchWinRules.Of(false, Plain with { HomeSpeedrunCurse = true, VisitorTiedHomie = true, HomeTiedHomie = true }));

        // Tied Homie on the visitors is asked before the home team's.
        Assert.Equal(WinRule.TwoAhead, MatchWinRules.Of(false, Plain with { VisitorTiedHomie = true }));
        Assert.Equal(WinRule.TwoAhead, MatchWinRules.Of(false, Plain with { VisitorTiedHomie = true, HomeTiedHomie = true }));
        Assert.Equal(WinRule.NotTwoBehind, MatchWinRules.Of(false, Plain with { HomeTiedHomie = true }));

        // A goal target the game's matches never set (0: every match a win) gives no rule: judged as the lead, as before.
        Assert.Equal(WinRule.Unknown, MatchWinRules.Of(false, new WinFacts(true, false, false, false, 0)));
        Assert.Equal(WinRule.Unknown, MatchWinRules.Of(false, new WinFacts(false, true, false, false, MatchView.MaxNumber + 1)));
    }

    [Fact]
    public void Each_rule_gives_the_win_as_the_game_does_at_its_edges()
    {
        // GameManager.get_WinningTeam: home > visitor; home >= target; home - 1 > visitor; visitor - 1 <= home.
        (WinRule Rule, int Home, int Visitor, bool Won)[] cases =
        {
            (WinRule.Ahead, 2, 1, true), (WinRule.Ahead, 2, 2, false), (WinRule.Ahead, 0, 1, false),
            (WinRule.Unknown, 1, 0, true), (WinRule.Unknown, 2, 2, false),
            (WinRule.GoalTarget, 3, 5, true), (WinRule.GoalTarget, 3, 0, true), (WinRule.GoalTarget, 2, 0, false), (WinRule.GoalTarget, 4, 9, true),
            (WinRule.TwoAhead, 3, 1, true), (WinRule.TwoAhead, 2, 1, false), (WinRule.TwoAhead, 1, 1, false),
            (WinRule.NotTwoBehind, 2, 2, true), (WinRule.NotTwoBehind, 1, 2, true), (WinRule.NotTwoBehind, 1, 3, false), (WinRule.NotTwoBehind, 0, 0, true),
        };
        foreach ((WinRule rule, int home, int visitor, bool won) in cases)
        {
            Assert.True(MatchWinRules.HomeWins(rule, home, visitor, 3) == won, $"{rule} {home}-{visitor}");
            Assert.True(Ended(MatchKind.Elite, 1, home, visitor, rule, 3).HomeWon == won, $"view {rule} {home}-{visitor}");
        }
    }

    [Fact]
    public void The_next_goal_is_read_from_the_end_conditions_of_the_setup_this_pc_loaded()
    {
        Assert.True(MatchWinRules.NextGoalEnds(Setup("0,1")));
        Assert.True(MatchWinRules.NextGoalEnds(Setup("1")));
        Assert.False(MatchWinRules.NextGoalEnds(Setup("0,2,4")));
        Assert.False(MatchWinRules.NextGoalEnds(Setup(string.Empty)));
        Assert.False(MatchWinRules.NextGoalEnds(Setup(null)));
        Assert.False(MatchWinRules.NextGoalEnds(Setup("10,11")));
        Assert.True(MatchWinRules.NextGoalEnds(Setup("x,,1"))); // what doesn't read is skipped, as the friend's load skips it
        Assert.False(MatchWinRules.NextGoalEnds(null));
        var whole = new List<KeyValuePair<string, NetValue>> { new(MatchWinRules.EndConditionsKey, NetValue.FromInt(1)) };
        Assert.False(MatchWinRules.NextGoalEnds(whole));
    }

    [Fact]
    public void Tied_homie_on_the_run_makes_a_tie_or_one_goal_behind_a_win_for_the_moments_of_a_won_match()
    {
        Assert.Equal(MomentReason.None, Judge("ELITE_WIN", Ended(MatchKind.Elite, 1, 2, 2, WinRule.NotTwoBehind)));
        Assert.Equal(MomentReason.None, Judge("ELITE_WIN", Ended(MatchKind.Elite, 1, 1, 2, WinRule.NotTwoBehind)));
        Assert.Equal(MomentReason.NotWonByRule, Judge("ELITE_WIN", Ended(MatchKind.Elite, 1, 1, 3, WinRule.NotTwoBehind)));
        Assert.Equal(MomentReason.None, Judge("WIN_ACT2", Ended(MatchKind.Boss, 2, 0, 0, WinRule.NotTwoBehind)));
        Assert.Equal(MomentReason.None, Judge("WIN_ACT3", Ended(MatchKind.Boss, 3, 3, 4, WinRule.NotTwoBehind)));
        Assert.Equal(MomentReason.None, Judge("GAWAIN", Ended(MatchKind.Boss, 1, 2, 2, WinRule.NotTwoBehind)));

        // The same scores with no relic changing the rule (read here, or not read): refused, as before.
        Assert.Equal(MomentReason.NotWon, Judge("ELITE_WIN", Ended(MatchKind.Elite, 1, 2, 2, WinRule.Ahead)));
        Assert.Equal(MomentReason.NotWon, Judge("ELITE_WIN", Ended(MatchKind.Elite, 1, 2, 2, WinRule.Unknown)));
    }

    [Fact]
    public void Tied_homie_on_the_visitors_needs_a_two_goal_lead()
    {
        Assert.Equal(MomentReason.None, Judge("ELITE_WIN", Ended(MatchKind.Elite, 2, 3, 1, WinRule.TwoAhead)));
        Assert.Equal(MomentReason.NotWonByRule, Judge("ELITE_WIN", Ended(MatchKind.Elite, 2, 2, 1, WinRule.TwoAhead)));
        Assert.Equal(MomentReason.NotWonByRule, Judge("WIN_ACT2", Ended(MatchKind.Boss, 2, 4, 3, WinRule.TwoAhead)));
        Assert.Equal(MomentReason.NotWonByRule, Judge("GAWAIN", Ended(MatchKind.Boss, 1, 1, 0, WinRule.TwoAhead)));
    }

    [Fact]
    public void The_speedrun_rule_is_the_goal_target_whatever_the_visitors_score()
    {
        Assert.Equal(MomentReason.None, Judge("ELITE_WIN", Ended(MatchKind.Elite, 1, 3, 4, WinRule.GoalTarget, target: 3)));
        Assert.Equal(MomentReason.None, Judge("WIN_ACT2", Ended(MatchKind.Boss, 2, 4, 0, WinRule.GoalTarget, target: 4)));
        Assert.Equal(MomentReason.NotWonByRule, Judge("ELITE_WIN", Ended(MatchKind.Elite, 1, 2, 0, WinRule.GoalTarget, target: 3)));
        Assert.Equal(MomentReason.NotWonByRule, Judge("WIN_ACT3", Ended(MatchKind.Boss, 3, 3, 1, WinRule.GoalTarget, target: 4)));
    }

    [Fact]
    public void Mercy_needs_the_lead_whatever_the_winner_s_rule()
    {
        // The mercy rule's win is raised only with the home team ahead (Mercy.OnAfterReplay), and MERCY asks no winner.
        Assert.Equal(MomentReason.None, Judge("MERCY", Ended(MatchKind.Elite, 1, 5, 0, WinRule.NotTwoBehind, signs: MatchSigns.EliteMercy)));
        Assert.Equal(MomentReason.NotWon, Judge("MERCY", Ended(MatchKind.Elite, 1, 2, 2, WinRule.NotTwoBehind, signs: MatchSigns.EliteMercy)));
        Assert.Equal(MomentReason.None, Judge("MERCY", Ended(MatchKind.Elite, 1, 3, 1, WinRule.GoalTarget, target: 5, signs: MatchSigns.EliteMercy)));

        // The moments of no winner don't change: SCORING_SQUAD counts goals, HEADSHOT_REDIRECT the event.
        Assert.Equal(MomentReason.None, Judge("SCORING_SQUAD", Ended(MatchKind.Elite, 1, 2, 2, WinRule.TwoAhead)));
        Assert.Equal(MomentReason.NoHeadshot, Judge("HEADSHOT_REDIRECT", Ended(MatchKind.Elite, 1, 2, 2, WinRule.NotTwoBehind)));
    }

    [Fact]
    public void A_win_a_host_claims_that_this_pc_s_own_run_and_score_don_t_support_stays_refused()
    {
        var gate = new SharedPlayGate(new SharedPlayRecord());
        var inbox = new MomentInbox();
        gate.Welcomed(Session);
        inbox.Welcomed(Session);
        gate.NoteRun(At, out _);

        // A tie claimed as ELITE_WIN: this PC's run holds no Tied Homie, so its rule is the lead.
        gate.NoteMatch(1, Ended(MatchKind.Elite, 1, 2, 2, MatchWinRules.Of(false, Plain)), At, out _);
        Assert.Equal(MomentArrival.Refused, inbox.Arrived(new AchievementMoment("ELITE_WIN", 1, false), gate, out MomentReason reason));
        Assert.Equal(MomentReason.NotWon, reason);

        // A one-goal win claimed as WIN_ACT2: this PC's copy of the visitors holds Tied Homie, so the game called it theirs.
        gate.NoteMatch(2, Ended(MatchKind.Boss, 2, 2, 1, MatchWinRules.Of(false, Plain with { VisitorTiedHomie = true })), At, out _);
        Assert.Equal(MomentArrival.Refused, inbox.Arrived(new AchievementMoment("WIN_ACT2", 2, false), gate, out reason));
        Assert.Equal(MomentReason.NotWonByRule, reason);
        Assert.Equal("this PC saw that match end without the home team winning by the game's rule for it", MomentCheck.Words(reason));

        // A goal-target win while behind: this PC's run is no speedrun squad's and holds no curse.
        gate.NoteMatch(3, Ended(MatchKind.Elite, 1, 3, 4, MatchWinRules.Of(false, Plain with { GoalTarget = 3 })), At, out _);
        Assert.Equal(MomentArrival.Refused, inbox.Arrived(new AchievementMoment("ELITE_WIN", 3, false), gate, out reason));
        Assert.Equal(MomentReason.NotWon, reason);

        // A tie with the rule not read here: refused as before, and the line says why.
        gate.NoteMatch(5, Ended(MatchKind.Elite, 1, 2, 2, WinRule.Unknown), At, out _);
        Assert.Equal(MomentArrival.Refused, inbox.Arrived(new AchievementMoment("ELITE_WIN", 5, false), gate, out reason));
        Assert.Equal(MomentReason.NotWon, reason);
        Assert.EndsWith(", the game's rule for its winner wasn't read here (a win is the home team ahead)", gate.ViewOf(5).ToString(), StringComparison.Ordinal);

        // What this PC read first stays: a later view can't bring a rule that would let the tie through.
        gate.NoteView(1, MatchView.Unknown with { Rule = WinRule.NotTwoBehind }, At, out _);
        Assert.Equal(WinRule.Ahead, gate.ViewOf(1).Rule);
        Assert.Equal(MomentReason.NotWon, MomentCheck.Judge(new AchievementMoment("ELITE_WIN", 1, false), gate));

        // The honest case of the same night: this PC's run holds Tied Homie and saw the tie; it unlocks.
        gate.NoteMatch(6, Ended(MatchKind.Elite, 2, 2, 2, MatchWinRules.Of(false, Plain with { HomeTiedHomie = true })), At, out _);
        Assert.Equal(MomentArrival.UnlockNow, inbox.Arrived(new AchievementMoment("ELITE_WIN", 6, false), gate, out reason));
        Assert.Equal(MomentReason.None, reason);
    }

    [Fact]
    public void A_rule_read_while_the_match_plays_is_kept_as_its_score_and_end_come()
    {
        // The glue notes the kind at the match's first frame here, the rule once read, then the stream's score and end.
        var gate = new SharedPlayGate(new SharedPlayRecord());
        var view = new MatchViewTracker();
        gate.Welcomed(Session);
        view.InSession(Session);
        view.Played(4);
        gate.NoteMatch(4, MatchView.Unknown with { Kind = MatchKind.Elite, Act = 1 }, At, out _);
        Assert.True(gate.NoteView(4, MatchView.Unknown with { Rule = WinRule.NotTwoBehind }, At, out bool noted));
        Assert.True(noted);

        long now = 0;
        for (; now <= 30_000_000; now += 500_000)
        {
            view.TryNote(1, 1, MatchSigns.None, false, true, now, out _);
        }

        Assert.True(view.TryNote(1, 2, MatchSigns.None, false, true, now, out MatchView seen));
        gate.NoteView(4, seen, At, out _);
        view.TryNote(1, 2, MatchSigns.None, true, false, now + 1_000, out _);
        Assert.True(view.TryNote(1, 2, MatchSigns.None, true, false, now + 2_000_000, out seen));
        Assert.Equal(WinRule.Unknown, seen.Rule); // the stream's views carry no rule
        gate.NoteView(4, seen, At, out _);

        MatchView kept = gate.ViewOf(4);
        Assert.Equal((WinRule.NotTwoBehind, true, 1, 2), (kept.Rule, kept.Ended, kept.HomeGoals, kept.VisitorGoals));
        Assert.Equal(MomentReason.None, MomentCheck.Judge(new AchievementMoment("ELITE_WIN", 4, false), gate));
        Assert.Equal("an Elite match of act 1, seen to end 1-2 here, the home team also wins at a tie or one goal behind (its run holds Tied Homie)", kept.ToString());
    }

    [Fact]
    public void The_rule_is_kept_in_the_record_and_a_record_without_it_still_reads()
    {
        var record = new SharedPlayRecord();
        Assert.True(record.NoteView(Session, 4, Ended(MatchKind.Elite, 2, 2, 2, WinRule.NotTwoBehind), At));
        Assert.True(record.NoteView(Session, 5, Ended(MatchKind.Boss, 2, 3, 1, WinRule.GoalTarget, target: 3), At));
        Assert.True(record.NoteView(Session, 6, Ended(MatchKind.Challenge, 1, 1, 0, WinRule.Unknown), At));

        string text = record.Format();
        Assert.Contains("4:3.2.5.2.2.4.4.0", text, StringComparison.Ordinal);
        Assert.Contains("5:4.2.5.3.1.4.2.3", text, StringComparison.Ordinal);
        Assert.Contains("6:2.1.5.1.0.4\n", text, StringComparison.Ordinal); // no rule: the six parts earlier builds read
        SharedPlayRecord read = SharedPlayRecord.Parse(text);
        Assert.Equal(text, read.Format());
        Assert.Equal(Ended(MatchKind.Elite, 2, 2, 2, WinRule.NotTwoBehind), read.ViewOf(Session, 4));
        Assert.Equal(Ended(MatchKind.Boss, 2, 3, 1, WinRule.GoalTarget, target: 3), read.ViewOf(Session, 5));
        Assert.True(read.ViewOf(Session, 4).HomeWon);

        // A rule that isn't one, or a target out of range, never reads: the line is dropped, as for any damaged view.
        Assert.True(MatchView.TryParse("3.2.5.2.2.4.4.0", out MatchView view));
        Assert.Equal(WinRule.NotTwoBehind, view.Rule);
        Assert.False(MatchView.TryParse("3.2.5.2.2.4.0.0", out _));
        Assert.False(MatchView.TryParse("3.2.5.2.2.4.5.0", out _));
        Assert.False(MatchView.TryParse("3.2.5.2.2.4.2.1000", out _));
        Assert.False(MatchView.TryParse("3.2.5.2.2.4.4", out _));
        Assert.True(MatchView.TryParse("3.2.5.2.2.4", out view));
        Assert.Equal(WinRule.Unknown, view.Rule);
    }

    [Fact]
    public void The_unlock_line_names_the_rule_that_let_a_win_through()
    {
        Assert.Equal(" (won by the game's own rule for that match: the home team also wins at a tie or one goal behind (its run holds Tied Homie); seen to end 2-2 here)",
            MomentCheck.WinRuleNote("ELITE_WIN", Ended(MatchKind.Elite, 1, 2, 2, WinRule.NotTwoBehind)));
        Assert.Equal(" (won by the game's own rule for that match: the home team wins on reaching the goal target of 3, whatever the visitors score " +
            "(the speedrun squad, or the speedrun curse on its run); seen to end 3-4 here)",
            MomentCheck.WinRuleNote("GAWAIN", Ended(MatchKind.Boss, 1, 3, 4, WinRule.GoalTarget, target: 3)));
        Assert.Equal(string.Empty, MomentCheck.WinRuleNote("ELITE_WIN", Ended(MatchKind.Elite, 1, 2, 1, WinRule.Ahead)));
        Assert.Equal(string.Empty, MomentCheck.WinRuleNote("ELITE_WIN", Ended(MatchKind.Elite, 1, 2, 1, WinRule.Unknown)));
        Assert.Equal(string.Empty, MomentCheck.WinRuleNote("MERCY", Ended(MatchKind.Elite, 1, 5, 0, WinRule.NotTwoBehind)));
        Assert.Equal(string.Empty, MomentCheck.WinRuleNote("HEADSHOT_REDIRECT", Ended(MatchKind.Elite, 1, 2, 2, WinRule.NotTwoBehind)));

        // An exhibition's view never names a rule (none is read for one); a campaign match's names one only when it isn't the lead.
        Assert.Equal("an exhibition match, seen to end 1-0 here", Ended(MatchKind.Exhibition, 0, 1, 0, WinRule.Unknown).ToString());
        Assert.Equal("a Challenge match of act 1, seen to end 1-0 here", Ended(MatchKind.Challenge, 1, 1, 0, WinRule.Ahead).ToString());
        Assert.Equal("an Elite match of act 2, seen to end 3-1 here, the home team wins only two goals ahead (the visitors hold Tied Homie)",
            Ended(MatchKind.Elite, 2, 3, 1, WinRule.TwoAhead).ToString());
    }

    [Fact]
    public void The_facts_read_go_in_the_log_as_yes_or_no_and_the_goal_target()
    {
        Assert.Equal("ends at the next goal: no; the speedrun squad: yes; on the home team: Tied Homie no, the speedrun curse no; Tied Homie on the visitors: yes; goal target 3",
            MatchWinRules.FactsText(false, Plain with { SpeedrunSquad = true, VisitorTiedHomie = true }));
        Assert.Equal("the home team wins ahead", MatchWinRules.Words(WinRule.Ahead, 0));
    }
}
