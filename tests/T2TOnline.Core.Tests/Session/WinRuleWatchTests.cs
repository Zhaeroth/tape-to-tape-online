using System;
using System.Collections.Generic;
using T2TOnline.Core.Protocol;
using T2TOnline.Core.Session;

namespace T2TOnline.Core.Tests.Session;

/// <summary>
/// D135: when a friend's game reads the game's rule for the winner of the host's campaign match, and which read counts
/// (<see cref="WinRuleWatch"/>): only while the match is loaded there, the newest read, noted once as its end shows.
/// </summary>
public class WinRuleWatchTests
{
    private const ulong Session = 0x77;
    private static readonly DateTime At = new(2026, 11, 10, 21, 0, 0, DateTimeKind.Utc);

    private static readonly MatchView Elite = MatchView.Unknown with { Kind = MatchKind.Elite, Act = 1 };

    private static MatchView Rule(WinRule rule, int target = 0) => MatchView.Unknown with { Rule = rule, GoalTarget = target };

    private static WinRuleWatch Watching(int match = 4)
    {
        var watch = new WinRuleWatch();
        Assert.True(watch.IsAnother(Session, match));
        watch.Begin(Session, match, Elite);
        Assert.False(watch.IsAnother(Session, match));
        return watch;
    }

    [Fact]
    public void The_rule_is_read_as_soon_as_the_match_is_loaded_then_again_at_most_every_few_seconds()
    {
        WinRuleWatch watch = Watching();
        Assert.False(watch.ReadDue(loaded: false, ended: false, now: 10)); // its setup is here, its scene isn't yet
        Assert.True(watch.ReadDue(loaded: true, ended: false, now: 10.5));
        Assert.False(watch.ReadDue(true, false, 10.6));
        Assert.False(watch.ReadDue(true, false, 10.5 + WinRuleWatch.RereadSeconds - 0.01));
        Assert.True(watch.ReadDue(true, false, 10.5 + WinRuleWatch.RereadSeconds));
    }

    [Fact]
    public void A_read_that_gave_nothing_is_tried_again_and_the_match_closes_unread_with_why()
    {
        WinRuleWatch watch = Watching();
        Assert.True(watch.ReadDue(true, false, 1));
        watch.Missed("the home team's relics aren't there");
        Assert.Equal(WinRuleClose.Nothing, watch.Close(ended: false, out _, out _));
        Assert.True(watch.ReadDue(true, false, 1 + WinRuleWatch.RereadSeconds));
        watch.Missed("the visitors' relics aren't there");

        Assert.Equal(WinRuleClose.Unread, watch.Close(ended: true, out MatchView ruled, out string problem));
        Assert.Equal(WinRule.Unknown, ruled.Rule);
        Assert.Equal("the visitors' relics aren't there", problem);
        Assert.Equal(WinRuleClose.Nothing, watch.Close(true, out _, out _)); // said once
    }

    [Fact]
    public void A_later_read_that_differs_replaces_the_one_held_and_the_newest_is_the_one_noted()
    {
        // The game decides a match's winner as it ends. Its goal target is final once the match is loaded (the match's own
        // set-up works it out while the scene loads), so reads of one match shouldn't differ; should they all the same, the
        // newest is what the game decides on. Here a speedrun Elite read with a target of 3, then of 4.
        WinRuleWatch watch = Watching();
        Assert.True(watch.ReadDue(true, false, 0));
        Assert.True(watch.Took(Rule(WinRule.GoalTarget, 3))); // news: the first read
        Assert.Equal(WinRuleClose.Nothing, watch.Close(false, out _, out _)); // nothing is noted before the end

        Assert.True(watch.ReadDue(true, false, WinRuleWatch.RereadSeconds));
        Assert.False(watch.Took(Rule(WinRule.GoalTarget, 3))); // the same again: no news
        Assert.True(watch.ReadDue(true, false, 2 * WinRuleWatch.RereadSeconds));
        Assert.True(watch.Took(Rule(WinRule.GoalTarget, 4))); // news: the target changed

        Assert.Equal(WinRuleClose.Read, watch.Close(ended: true, out MatchView ruled, out _));
        Assert.Equal((WinRule.GoalTarget, 4), (ruled.Rule, ruled.GoalTarget));

        // A 3-4 loss is no win by the rule noted; by the first read it would have been.
        Assert.False(MatchWinRules.HomeWins(ruled.Rule, 3, 4, ruled.GoalTarget));
        Assert.True(MatchWinRules.HomeWins(WinRule.GoalTarget, 3, 4, 3));
    }

    [Fact]
    public void The_frame_the_end_shows_reads_once_more_whatever_the_wait_and_that_read_is_the_one_noted()
    {
        WinRuleWatch watch = Watching();
        Assert.True(watch.ReadDue(true, false, 5));
        watch.Took(Rule(WinRule.Ahead));
        Assert.False(watch.ReadDue(true, false, 5.1));
        Assert.True(watch.ReadDue(loaded: true, ended: true, now: 5.2)); // held on its last frame: still loaded
        Assert.True(watch.Took(Rule(WinRule.NotTwoBehind)));
        Assert.Equal(WinRuleClose.Read, watch.Close(true, out MatchView ruled, out _));
        Assert.Equal(WinRule.NotTwoBehind, ruled.Rule);
    }

    [Fact]
    public void An_end_that_shows_with_the_match_gone_keeps_what_was_read_while_it_was_loaded_and_reads_nothing_more()
    {
        // The host left the match before its end showed here (the one-PC test's friend sees only that): no object of it is left.
        WinRuleWatch watch = Watching();
        Assert.True(watch.ReadDue(true, false, 1));
        watch.Took(Rule(WinRule.TwoAhead));
        Assert.False(watch.ReadDue(loaded: false, ended: true, now: 30));
        Assert.Equal(WinRuleClose.Read, watch.Close(true, out MatchView ruled, out _));
        Assert.Equal(WinRule.TwoAhead, ruled.Rule);

        // Settled: nothing is read for this match again, loaded or not.
        Assert.False(watch.ReadDue(true, true, 40));
        Assert.False(watch.Took(Rule(WinRule.Ahead)));
        Assert.Equal(WinRuleClose.Nothing, watch.Close(true, out _, out _));
    }

    [Fact]
    public void A_match_never_loaded_here_is_never_read_and_closes_without_a_word()
    {
        // Its end was the first thing this PC saw of it: its moments are refused for too little play anyway.
        WinRuleWatch watch = Watching();
        Assert.False(watch.ReadDue(loaded: false, ended: false, now: 1));
        Assert.False(watch.ReadDue(loaded: false, ended: true, now: 2));
        Assert.Equal(WinRuleClose.Nothing, watch.Close(true, out MatchView ruled, out string problem));
        Assert.Equal((WinRule.Unknown, string.Empty), (ruled.Rule, problem));
    }

    [Fact]
    public void A_failed_read_after_a_good_one_leaves_the_good_one_held()
    {
        WinRuleWatch watch = Watching();
        Assert.True(watch.ReadDue(true, false, 0));
        watch.Took(Rule(WinRule.GoalTarget, 5));
        Assert.True(watch.ReadDue(true, true, 9)); // the end's frame: the scene is going
        watch.Missed("the match's game manager isn't there");
        Assert.Equal(WinRuleClose.Read, watch.Close(true, out MatchView ruled, out _));
        Assert.Equal((WinRule.GoalTarget, 5), (ruled.Rule, ruled.GoalTarget));
    }

    [Fact]
    public void A_read_that_names_no_rule_isn_t_one()
    {
        WinRuleWatch watch = Watching();
        Assert.True(watch.ReadDue(true, false, 0));
        Assert.False(watch.Took(MatchView.Unknown));
        Assert.Equal(WinRuleClose.Unread, watch.Close(true, out _, out _));
    }

    [Theory]
    [InlineData(0, MatchKind.Elite, WinRule.Unknown)] // no match noted
    [InlineData(4, MatchKind.Exhibition, WinRule.Unknown)] // only a campaign match's winner has rules beyond the lead
    [InlineData(4, MatchKind.Unknown, WinRule.Unknown)]
    [InlineData(4, MatchKind.Boss, WinRule.Ahead)] // its rule is in the record already (a rejoin after this game restarted)
    [InlineData(4, MatchKind.Elite, WinRule.GoalTarget)]
    public void Only_a_campaign_match_with_no_rule_kept_yet_is_read(int match, MatchKind kind, WinRule kept)
    {
        var watch = new WinRuleWatch();
        watch.Begin(Session, match, MatchView.Unknown with { Kind = kind, Rule = kept });
        Assert.False(watch.ReadDue(true, false, 1));
        Assert.False(watch.ReadDue(true, true, 2));
        Assert.Equal(WinRuleClose.Nothing, watch.Close(true, out _, out _));
    }

    [Fact]
    public void Another_match_or_hosting_session_starts_from_nothing_and_drops_what_was_held_of_the_last()
    {
        WinRuleWatch watch = Watching(4);
        Assert.True(watch.ReadDue(true, false, 1));
        watch.Took(Rule(WinRule.NotTwoBehind)); // match 4's end never shows here (dropped before it)

        Assert.True(watch.IsAnother(Session, 5));
        watch.Begin(Session, 5, Elite);
        Assert.Equal(5, watch.Match);
        Assert.True(watch.ReadDue(true, false, 1.1)); // no wait carried over
        watch.Missed("the home team's relics aren't there");
        Assert.Equal(WinRuleClose.Unread, watch.Close(true, out MatchView ruled, out _)); // match 4's read isn't match 5's
        Assert.Equal(WinRule.Unknown, ruled.Rule);

        Assert.True(watch.IsAnother(Session + 1, 5)); // the same number in another hosting session is another match
    }

    [Theory]
    [InlineData(1800)] // the host's unlock comes while the match still plays here: it waits for the end
    [InlineData(2400)] // on the very frame the end shows here
    [InlineData(2430)] // while the end settles
    [InlineData(2700)] // after the end counts: judged as it arrives
    public void A_win_moment_is_never_judged_before_its_match_s_rule_is_noted(int arrivesAtFrame)
    {
        // The session's order each frame: what the stream showed of the match (its end counts once it has shown for
        // MatchViewTracker.EndSettleMicros), then the rule (noted on the first frame the end shows), then the moments.
        // A 2-1 lead against visitors holding Tied Homie: a win by the lead, none by the game's rule. Judged with no rule
        // noted, it would unlock.
        const long Frame = 16_667;
        const int EndsAtFrame = 2400; // 40 s in
        var gate = new SharedPlayGate(new SharedPlayRecord());
        var view = new MatchViewTracker();
        var watch = new WinRuleWatch();
        var inbox = new MomentInbox();
        var moment = new AchievementMoment("ELITE_WIN", 4, false);
        gate.Welcomed(Session);
        inbox.Welcomed(Session);
        view.InSession(Session);
        view.Played(4);
        gate.NoteMatch(4, Elite, At, out _);
        watch.Begin(Session, 4, gate.ViewOf(4));

        var reasons = new List<MomentReason>();
        for (int frame = 0; frame <= EndsAtFrame + 400; frame++)
        {
            long now = frame * Frame;
            bool ended = frame >= EndsAtFrame;
            if (view.TryNote(2, 1, MatchSigns.None, ended, playing: !ended, now, out MatchView seen))
            {
                gate.NoteView(4, seen, At, out _);
            }

            if (watch.ReadDue(loaded: true, ended, now / 1_000_000.0))
            {
                watch.Took(Rule(WinRule.TwoAhead));
            }

            if (watch.Close(ended, out MatchView ruled, out _) == WinRuleClose.Read)
            {
                Assert.Equal(EndsAtFrame, frame);
                Assert.False(gate.ViewOf(4).Ended); // noted before the end counts
                gate.NoteView(4, ruled, At, out _);
            }

            if (frame == arrivesAtFrame && inbox.Arrived(moment, gate, out MomentReason arrived) != MomentArrival.Waiting)
            {
                reasons.Add(arrived);
            }

            while (inbox.Waiting > 0 && inbox.TryTakeDecided(gate, out _, out MomentReason decided))
            {
                Assert.True(now >= (EndsAtFrame * Frame) + MatchViewTracker.EndSettleMicros);
                reasons.Add(decided);
            }
        }

        Assert.Equal(MomentReason.NotWonByRule, Assert.Single(reasons)); // decided once, by the rule
        MatchView kept = gate.ViewOf(4);
        Assert.Equal((true, WinRule.TwoAhead, 2, 1), (kept.Ended, kept.Rule, kept.HomeGoals, kept.VisitorGoals));
    }

    [Fact]
    public void A_view_names_an_unread_rule_only_once_its_end_is_seen()
    {
        // The rule is noted as the end shows: before that a campaign match's view doesn't call it missing.
        MatchView playing = new(MatchKind.Elite, 1, 5, false, 1, 0, MatchSigns.PlayedEnough);
        Assert.Equal("an Elite match of act 1, its end not seen here", playing.ToString());
        Assert.Equal("an Elite match of act 1, seen to end 1-0 here, the game's rule for its winner wasn't read here (a win is the home team ahead)",
            (playing with { Ended = true }).ToString());
        Assert.Equal("an Elite match of act 1, its end not seen here, the home team wins only two goals ahead (the visitors hold Tied Homie)",
            (playing with { Rule = WinRule.TwoAhead }).ToString());
    }
}
