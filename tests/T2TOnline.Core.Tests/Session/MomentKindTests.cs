using System;
using System.Collections.Generic;
using T2TOnline.Core.Protocol;
using T2TOnline.Core.Serialization;
using T2TOnline.Core.Session;
using T2TOnline.Core.Stream;

namespace T2TOnline.Core.Tests.Session;

/// <summary>
/// D135: a friend unlocks a shared moment only for a match of the kind the game's own condition needs, as its own game
/// loaded and saw it (<see cref="MatchView"/>): per moment, an honest case unlocks and forged ones don't; a host that let a
/// friend play one ordinary match and hold its run can't unlock any of the seven.
/// </summary>
public class MomentKindTests
{
    private const ulong Session = 0x77;
    private static readonly DateTime At = new(2026, 11, 5, 21, 0, 0, DateTimeKind.Utc);

    private const long Second = 1_000_000;

    // A match this PC played enough of (20 s or more), seen to end.
    private static MatchView Ended(MatchKind kind, int act, int home, int visitor, MatchSigns signs = MatchSigns.None, int skaters = 5) =>
        new(kind, act, skaters, true, home, visitor, signs | MatchSigns.PlayedEnough);

    // The match plays here from <paramref name="from"/> for <paramref name="seconds"/>, its HUD at home-visitor, half a second a frame.
    private static long PlayFor(MatchViewTracker view, long from, double seconds, int home = -1, int visitor = -1)
    {
        long end = from + (long)(seconds * Second);
        for (long now = from; now <= end; now += Second / 2)
        {
            view.TryNote(home, visitor, MatchSigns.None, false, true, now, out _);
        }

        return end;
    }

    private static MomentReason Judge(string name, MatchView view, bool run = false, bool held = true, int match = 4)
    {
        var gate = new SharedPlayGate(new SharedPlayRecord());
        gate.Welcomed(Session);
        gate.NoteMatch(4, view, At, out _);
        if (held)
        {
            gate.NoteRun(At, out _);
        }

        return MomentCheck.Judge(new AchievementMoment(name, match, run), gate);
    }

    [Fact]
    public void Mercy_needs_an_elite_seen_ended_by_the_mercy_rule_with_the_home_team_ahead()
    {
        Assert.Equal(MomentReason.None, Judge("MERCY", Ended(MatchKind.Elite, 1, 5, 0, MatchSigns.EliteMercy)));
        Assert.Equal(MomentReason.WrongKind, Judge("MERCY", Ended(MatchKind.Challenge, 1, 5, 0, MatchSigns.EliteMercy)));
        Assert.Equal(MomentReason.WrongKind, Judge("MERCY", Ended(MatchKind.Boss, 1, 5, 0, MatchSigns.EliteMercy)));
        Assert.Equal(MomentReason.NoMercy, Judge("MERCY", Ended(MatchKind.Elite, 1, 3, 2)));
        Assert.Equal(MomentReason.NotWon, Judge("MERCY", Ended(MatchKind.Elite, 1, 0, 5, MatchSigns.EliteMercy)));
        Assert.Equal(MomentReason.MatchNotOver, Judge("MERCY", MatchView.Unknown with { Kind = MatchKind.Elite, Signs = MatchSigns.EliteMercy }));
    }

    [Fact]
    public void Elite_win_and_act_2_need_that_node_s_match_seen_won()
    {
        Assert.Equal(MomentReason.None, Judge("ELITE_WIN", Ended(MatchKind.Elite, 3, 2, 1)));
        Assert.Equal(MomentReason.NotWon, Judge("ELITE_WIN", Ended(MatchKind.Elite, 3, 1, 2)));
        Assert.Equal(MomentReason.NotWon, Judge("ELITE_WIN", Ended(MatchKind.Elite, 3, 2, 2)));
        Assert.Equal(MomentReason.WrongKind, Judge("ELITE_WIN", Ended(MatchKind.Boss, 2, 2, 1)));
        Assert.Equal(MomentReason.WrongKind, Judge("ELITE_WIN", Ended(MatchKind.Exhibition, 0, 2, 1)));

        Assert.Equal(MomentReason.None, Judge("WIN_ACT2", Ended(MatchKind.Boss, 2, 4, 3)));
        Assert.Equal(MomentReason.WrongKind, Judge("WIN_ACT2", Ended(MatchKind.Boss, 1, 4, 3)));
        Assert.Equal(MomentReason.WrongKind, Judge("WIN_ACT2", Ended(MatchKind.Elite, 2, 4, 3)));
        Assert.Equal(MomentReason.NotWon, Judge("WIN_ACT2", Ended(MatchKind.Boss, 2, 3, 4)));
    }

    [Fact]
    public void Run_moments_need_the_run_held_and_a_boss_seen_beaten()
    {
        Assert.Equal(MomentReason.None, Judge("WIN_ACT3", Ended(MatchKind.Boss, 3, 2, 0), run: true));
        Assert.Equal(MomentReason.RunNotHeld, Judge("WIN_ACT3", Ended(MatchKind.Boss, 3, 2, 0), run: true, held: false));
        Assert.Equal(MomentReason.WrongKind, Judge("WIN_ACT3", Ended(MatchKind.Boss, 2, 2, 0), run: true));
        Assert.Equal(MomentReason.NotWon, Judge("WIN_ACT3", Ended(MatchKind.Boss, 3, 0, 2), run: true));
        Assert.Equal(MomentReason.NoMatch, Judge("WIN_ACT3", Ended(MatchKind.Boss, 3, 2, 0), run: true, match: 0));

        Assert.Equal(MomentReason.None, Judge("GAWAIN", Ended(MatchKind.Boss, 1, 5, 1), run: true));
        Assert.Equal(MomentReason.WrongKind, Judge("GAWAIN", Ended(MatchKind.Elite, 1, 5, 1), run: true));
        Assert.Equal(MomentReason.NotWon, Judge("GAWAIN", Ended(MatchKind.Boss, 1, 1, 5), run: true));
        Assert.Equal(MomentReason.MatchNotYet, Judge("GAWAIN", Ended(MatchKind.Boss, 1, 5, 1), run: true, match: 6));
    }

    [Fact]
    public void Scoring_squad_needs_goals_enough_for_more_than_one_scorer()
    {
        Assert.Equal(MomentReason.None, Judge("SCORING_SQUAD", Ended(MatchKind.Exhibition, 0, 3, 1)));
        Assert.Equal(MomentReason.None, Judge("SCORING_SQUAD", Ended(MatchKind.Challenge, 1, 2, 0)));
        Assert.Equal(MomentReason.TooFewGoals, Judge("SCORING_SQUAD", Ended(MatchKind.Elite, 1, 1, 0)));
        Assert.Equal(MomentReason.None, Judge("SCORING_SQUAD", Ended(MatchKind.Exhibition, 0, 1, 0, skaters: 1))); // a lone skater
        Assert.Equal(MomentReason.MatchNotOver, Judge("SCORING_SQUAD", MatchView.Unknown with { Kind = MatchKind.Elite }));
        Assert.Equal(MomentReason.WrongKind, Judge("SCORING_SQUAD", Ended(MatchKind.Unknown, 0, 9, 0)));
    }

    [Fact]
    public void Scoring_squad_in_an_exhibition_counts_either_side_s_goals_as_the_humans_may_play_either()
    {
        // Play Now puts players on either team (the game's check reads the scorer's team, the one with players); a campaign's
        // human team is always the home team.
        Assert.Equal(MomentReason.None, Judge("SCORING_SQUAD", Ended(MatchKind.Exhibition, 0, 0, 3)));
        Assert.Equal(MomentReason.None, Judge("SCORING_SQUAD", Ended(MatchKind.Exhibition, 0, 1, 2)));
        Assert.Equal(MomentReason.TooFewGoals, Judge("SCORING_SQUAD", Ended(MatchKind.Exhibition, 0, 1, 1)));
        Assert.Equal(MomentReason.TooFewGoals, Judge("SCORING_SQUAD", Ended(MatchKind.Challenge, 1, 0, 3)));
        Assert.Equal(MomentReason.TooFewGoals, Judge("SCORING_SQUAD", Ended(MatchKind.Boss, 2, 1, 4)));

        // The line size asked of an exhibition is its smaller side's: a lone visitor skater's one goal is every skater scoring.
        static KeyValuePair<string, NetValue> Value(string key, long value) => new(key, NetValue.FromInt(value));
        var lone = new List<KeyValuePair<string, NetValue>>
        {
            Value(CampaignMatchValues.GameTypeKey, 0), Value("mode.skaterHome", 3), Value("mode.skaterVisitor", 1),
        };
        MatchView setup = MatchView.FromSetup(lone);
        Assert.Equal(1, setup.Skaters);
        Assert.Equal(MomentReason.None, Judge("SCORING_SQUAD", setup with { Ended = true, HomeGoals = 0, VisitorGoals = 1, Signs = MatchSigns.PlayedEnough }));

        // A campaign match asks the home team's own line, whatever the visitors field.
        var campaign = new List<KeyValuePair<string, NetValue>>
        {
            Value(CampaignMatchValues.GameTypeKey, CampaignMatchValues.CampaignGameType), Value("campaign.nodeType", 0), Value("campaign.act", 1),
            Value("mode.skaterHome", 5), Value("mode.skaterVisitor", 1),
        };
        Assert.Equal(5, MatchView.FromSetup(campaign).Skaters);
    }

    [Fact]
    public void Headshot_redirect_needs_the_headshot_goal_in_this_pc_s_stream_of_that_match()
    {
        Assert.Equal(MomentReason.None, Judge("HEADSHOT_REDIRECT", MatchView.Unknown with { Kind = MatchKind.Elite, Signs = MatchSigns.HeadshotGoal | MatchSigns.PlayedEnough }));
        Assert.Equal(MomentReason.MatchNotOver, Judge("HEADSHOT_REDIRECT", MatchView.Unknown with { Kind = MatchKind.Elite, Signs = MatchSigns.HeadshotGoal }));
        Assert.Equal(MomentReason.MatchNotOver, Judge("HEADSHOT_REDIRECT", MatchView.Unknown with { Kind = MatchKind.Elite }));
        Assert.Equal(MomentReason.NoHeadshot, Judge("HEADSHOT_REDIRECT", Ended(MatchKind.Elite, 1, 4, 0)));
        Assert.Equal(MomentReason.WrongKind, Judge("HEADSHOT_REDIRECT", MatchView.Unknown)); // played before kinds were kept: not knowable
    }

    [Fact]
    public void A_moment_of_a_match_this_pc_didn_t_see_end_waits_then_is_refused_once_a_later_match_plays()
    {
        var gate = new SharedPlayGate(new SharedPlayRecord());
        var inbox = new MomentInbox();
        gate.Welcomed(Session);
        inbox.Welcomed(Session);
        gate.NoteMatch(3, MatchView.Unknown with { Kind = MatchKind.Elite }, At, out _);
        Assert.Equal(MomentArrival.Waiting, inbox.Arrived(new AchievementMoment("ELITE_WIN", 3, false), gate, out MomentReason reason));
        Assert.Equal(MomentReason.MatchNotOver, reason);

        // Enough of it plays here, the score comes, then the end: it unlocks.
        gate.NoteView(3, MatchView.Unknown with { HomeGoals = 2, VisitorGoals = 1, Signs = MatchSigns.PlayedEnough }, At, out _);
        Assert.False(inbox.TryTakeDecided(gate, out _, out _));
        gate.NoteView(3, MatchView.Unknown with { Ended = true }, At, out _);
        Assert.True(inbox.TryTakeDecided(gate, out AchievementMoment moment, out reason));
        Assert.Equal(("ELITE_WIN", MomentReason.None), (moment.Name, reason));

        // A drop before the end of match 5; this PC plays match 6 next: match 5's moment can't be seen to end here now.
        gate.NoteMatch(5, MatchView.Unknown with { Kind = MatchKind.Elite, HomeGoals = 3, VisitorGoals = 0 }, At, out _);
        Assert.Equal(MomentArrival.Waiting, inbox.Arrived(new AchievementMoment("ELITE_WIN", 5, false), gate, out _));
        gate.NoteMatch(6, MatchView.Unknown with { Kind = MatchKind.Challenge }, At, out _);
        Assert.True(inbox.TryTakeDecided(gate, out moment, out reason));
        Assert.Equal((5, MomentReason.EndNotSeen), (moment.Match, reason));
    }

    [Fact]
    public void The_mercy_rule_s_event_shown_after_the_end_still_counts_and_the_moment_waits_for_it()
    {
        // The game raises the end-of-game event, then the mercy rule's, in the same frame; this PC may show them apart (a late
        // packet, a frame's delivery cap). The host's MERCY comes over the session's channel, ahead of the stream.
        var gate = new SharedPlayGate(new SharedPlayRecord());
        var inbox = new MomentInbox();
        var view = new MatchViewTracker();
        gate.Welcomed(Session);
        inbox.Welcomed(Session);
        view.InSession(Session);
        view.Played(4);
        gate.NoteMatch(4, MatchView.Unknown with { Kind = MatchKind.Elite, Act = 1 }, At, out _);
        PlayFor(view, 0, 70);
        Assert.True(view.TryNote(5, 0, MatchSigns.None, false, false, 80 * Second, out MatchView seen));
        gate.NoteView(4, seen, At, out _);
        Assert.Equal(MomentArrival.Waiting, inbox.Arrived(new AchievementMoment("MERCY", 4, false), gate, out MomentReason reason));
        Assert.Equal(MomentReason.MatchNotOver, reason);

        // The end shows: it doesn't count yet, so the moment isn't judged on a view without the mercy rule's event.
        Assert.False(view.TryNote(5, 0, MatchSigns.None, true, false, 90 * Second, out _));
        Assert.False(view.TryNote(5, 0, MatchSigns.None, true, false, (90 * Second) + 500_000, out _));
        Assert.False(inbox.TryTakeDecided(gate, out _, out _));

        // The mercy rule's event shows a frame later: noted (not ended yet); once the end has shown a moment, the end counts.
        Assert.True(view.TryNote(5, 0, MatchSigns.EliteMercy, true, false, (90 * Second) + 516_000, out seen));
        Assert.Equal(new MatchView(MatchKind.Unknown, 0, 0, false, 5, 0, MatchSigns.EliteMercy | MatchSigns.PlayedEnough), seen);
        gate.NoteView(4, seen, At, out _);
        Assert.False(inbox.TryTakeDecided(gate, out _, out _));
        Assert.True(view.TryNote(5, 0, MatchSigns.EliteMercy, true, false, 91 * Second, out seen));
        Assert.Equal(new MatchView(MatchKind.Unknown, 0, 0, true, 5, 0, MatchSigns.EliteMercy | MatchSigns.PlayedEnough), seen);
        gate.NoteView(4, seen, At, out _);
        Assert.True(inbox.TryTakeDecided(gate, out AchievementMoment moment, out reason));
        Assert.Equal(("MERCY", MomentReason.None), (moment.Name, reason));

        // The score is final once the end counts: a later sample changes nothing; a sign later still is kept.
        Assert.False(view.TryNote(6, 0, MatchSigns.EliteMercy, true, false, 92 * Second, out _));
        Assert.True(view.TryNote(6, 0, MatchSigns.EliteMercy | MatchSigns.HeadshotGoal, true, false, 93 * Second, out seen));
        Assert.Equal(new MatchView(MatchKind.Unknown, 0, 0, true, 5, 0, MatchSigns.EliteMercy | MatchSigns.HeadshotGoal | MatchSigns.PlayedEnough), seen);
        Assert.False(view.TryNote(-1, -1, MatchSigns.None, false, false, 94 * Second, out _));
    }

    [Fact]
    public void A_match_of_the_same_number_in_another_hosting_session_starts_from_nothing()
    {
        // The host's game counts its own matches (and starts over when it restarts; another host has its own count).
        var view = new MatchViewTracker();
        view.InSession(Session);
        view.Played(1);
        Assert.True(view.TryNote(2, 1, MatchSigns.HeadshotGoal, true, false, 0, out MatchView seen));
        Assert.False(seen.Ended);
        Assert.True(view.TryNote(2, 1, MatchSigns.HeadshotGoal, true, false, 2 * Second, out seen));
        Assert.True(seen.Ended);

        view.InSession(Session + 1);
        Assert.Equal(0, view.Match);
        Assert.False(view.TryNote(2, 1, MatchSigns.HeadshotGoal, true, false, 3 * Second, out _)); // nothing playing here yet
        view.Played(1);
        Assert.True(view.TryNote(0, 0, MatchSigns.None, false, false, 4 * Second, out seen));
        Assert.Equal(new MatchView(MatchKind.Unknown, 0, 0, false, 0, 0, MatchSigns.None), seen);
        Assert.True(view.TryNote(3, 0, MatchSigns.None, false, false, 5 * Second, out _));
        Assert.False(view.TryNote(3, 0, MatchSigns.None, true, false, 6 * Second, out _));
        Assert.True(view.TryNote(3, 0, MatchSigns.None, true, false, 7 * Second, out seen));
        Assert.Equal(new MatchView(MatchKind.Unknown, 0, 0, true, 3, 0, MatchSigns.None), seen);
    }

    [Fact]
    public void A_rejoin_s_new_playback_of_the_same_match_keeps_what_was_seen_of_it()
    {
        var view = new MatchViewTracker();
        view.InSession(Session);
        view.Played(4);
        Assert.True(view.TryNote(2, 1, MatchSigns.HeadshotGoal, false, false, 0, out _));

        // Same hosting session, same match: its new playback has no score until the host's next full HUD.
        view.InSession(Session);
        view.Played(4);
        Assert.False(view.TryNote(-1, -1, MatchSigns.None, false, false, Second, out _));
        Assert.False(view.TryNote(-1, -1, MatchSigns.None, true, false, 2 * Second, out _));
        Assert.True(view.TryNote(-1, -1, MatchSigns.None, true, false, 3 * Second, out MatchView seen));
        Assert.Equal(new MatchView(MatchKind.Unknown, 0, 0, true, 2, 1, MatchSigns.HeadshotGoal), seen);

        // The next match starts from nothing; a match without its number notes nothing.
        view.Played(5);
        Assert.False(view.TryNote(-1, -1, MatchSigns.None, false, false, 4 * Second, out _));
        view.Played(0);
        Assert.False(view.TryNote(1, 0, MatchSigns.EliteMercy, true, false, 5 * Second, out _));
    }

    [Fact]
    public void A_moment_needs_20_seconds_of_its_match_played_here_before_its_end()
    {
        // A friend who joins in the match's last seconds: 15 s played here, then the end, won.
        var gate = new SharedPlayGate(new SharedPlayRecord());
        var view = new MatchViewTracker();
        gate.Welcomed(Session);
        gate.NoteRun(At, out _);
        view.InSession(Session);
        view.Played(4);
        gate.NoteMatch(4, MatchView.Unknown with { Kind = MatchKind.Boss, Act = 2 }, At, out _);
        long now = PlayFor(view, 0, 15, home: 3, visitor: 1);
        view.TryNote(3, 1, MatchSigns.None, true, true, now + Second, out _);
        Assert.True(view.TryNote(3, 1, MatchSigns.None, true, true, now + (3 * Second), out MatchView seen));
        Assert.Equal(new MatchView(MatchKind.Unknown, 0, 0, true, 3, 1, MatchSigns.None), seen); // the end's playing doesn't count
        gate.NoteView(4, seen, At, out _);
        Assert.Equal(MomentReason.TooLittlePlayed, MomentCheck.Judge(new AchievementMoment("WIN_ACT2", 4, false), gate));
        Assert.Equal(
            "this PC played less than 20 seconds of that match before its end (it joined in its last seconds, or was out of it for nearly all of it)",
            MomentCheck.Words(MomentReason.TooLittlePlayed));
        Assert.EndsWith(", seen to end 3-1 here, less than 20 seconds of it played here", (gate.ViewOf(4) with { Rule = WinRule.Ahead }).ToString(), StringComparison.Ordinal);

        // A run moment's named match needs them too, the run held or not.
        Assert.Equal(MomentReason.TooLittlePlayed, MomentCheck.Judge(new AchievementMoment("GAWAIN", 4, true), gate));

        // 20 s, the shortest a real match plays before its end is well above (32.8 s): it unlocks.
        var played = new MatchViewTracker();
        played.InSession(Session);
        played.Played(5);
        gate.NoteMatch(5, MatchView.Unknown with { Kind = MatchKind.Boss, Act = 2 }, At, out _);
        now = PlayFor(played, 0, 21, home: 1, visitor: 0);
        played.TryNote(1, 0, MatchSigns.None, true, false, now + Second, out _);
        Assert.True(played.TryNote(1, 0, MatchSigns.None, true, false, now + (3 * Second), out seen));
        gate.NoteView(5, seen, At, out _);
        Assert.Equal(MomentReason.None, MomentCheck.Judge(new AchievementMoment("WIN_ACT2", 5, false), gate));
        Assert.Equal(MomentReason.None, MomentCheck.Judge(new AchievementMoment("GAWAIN", 5, true), gate));

        // Two playbacks of one match (a rejoin between) add up: 12 s and 10 s make 20; the same stays one match.
        var rejoined = new MatchViewTracker();
        rejoined.InSession(Session);
        rejoined.Played(6);
        now = PlayFor(rejoined, 0, 12);
        Assert.False(rejoined.TryNote(-1, -1, MatchSigns.None, false, false, now + (20 * Second), out _)); // dropped: not playing
        rejoined.Played(6);
        now = PlayFor(rejoined, now + (40 * Second), 10);
        Assert.True(rejoined.TryNote(2, 0, MatchSigns.None, false, true, now + (Second / 2), out seen));
        Assert.True(seen.Signs.HasFlag(MatchSigns.PlayedEnough));

        // A stalled PC's long frame counts a second at most.
        var stalled = new MatchViewTracker();
        stalled.InSession(Session);
        stalled.Played(7);
        stalled.TryNote(0, 0, MatchSigns.None, false, true, 0, out _);
        stalled.TryNote(0, 0, MatchSigns.None, false, true, 600 * Second, out _);
        Assert.False(stalled.TryNote(0, 0, MatchSigns.None, true, false, 601 * Second, out _));
        Assert.True(stalled.TryNote(0, 0, MatchSigns.None, true, false, 603 * Second, out seen));
        Assert.False(seen.Signs.HasFlag(MatchSigns.PlayedEnough));
    }

    [Fact]
    public void Three_forged_short_matches_unlock_no_moment_with_or_without_the_run_held()
    {
        // A modified host streams three matches of a few seconds each, made to look like what each moment needs: an elite won
        // by the mercy rule, a boss of act 2 won, a boss of act 3 won 2-0 with a headshot goal. Nothing it names of them
        // unlocks: none played 20 s here.
        var gate = new SharedPlayGate(new SharedPlayRecord());
        var inbox = new MomentInbox();
        gate.Welcomed(Session);
        inbox.Welcomed(Session);
        (int Match, MatchKind Kind, int Act, MatchSigns Signs)[] forged =
        {
            (1, MatchKind.Elite, 1, MatchSigns.EliteMercy), (2, MatchKind.Boss, 2, MatchSigns.None), (3, MatchKind.Boss, 3, MatchSigns.HeadshotGoal),
        };
        foreach ((int match, MatchKind kind, int act, MatchSigns signs) in forged)
        {
            var view = new MatchViewTracker();
            view.InSession(Session);
            view.Played(match);
            gate.NoteMatch(match, MatchView.Unknown with { Kind = kind, Act = act }, At, out _);
            long now = PlayFor(view, 0, 5, home: 2, visitor: 0);
            view.TryNote(2, 0, signs, true, true, now, out _);
            Assert.True(view.TryNote(2, 0, signs, true, false, now + (2 * Second), out MatchView seen));
            gate.NoteView(match, seen, At, out _);
        }

        foreach ((string name, int match) in new[] { ("MERCY", 1), ("ELITE_WIN", 1), ("WIN_ACT2", 2), ("SCORING_SQUAD", 3), ("HEADSHOT_REDIRECT", 3), ("SCORING_SQUAD", 2) })
        {
            Assert.Equal(MomentArrival.Refused, inbox.Arrived(new AchievementMoment(name, match, false), gate, out MomentReason reason));
            Assert.Equal(MomentReason.TooLittlePlayed, reason);
        }

        // Run moments: with no run of the host's held here they wait; once one is held, the act 3 boss of a few seconds
        // they name still doesn't count.
        Assert.Equal(MomentArrival.Waiting, inbox.Arrived(new AchievementMoment("WIN_ACT3", 3, true), gate, out _));
        gate.NoteRun(At, out _);
        Assert.True(inbox.TryTakeDecided(gate, out AchievementMoment decided, out MomentReason runReason));
        Assert.Equal(("WIN_ACT3", MomentReason.TooLittlePlayed), (decided.Name, runReason));
        Assert.Equal(MomentArrival.Refused, inbox.Arrived(new AchievementMoment("GAWAIN", 3, true), gate, out runReason));
        Assert.Equal(MomentReason.TooLittlePlayed, runReason);
        Assert.Equal(MomentReason.TooLittlePlayed, MomentCheck.Judge(new AchievementMoment("GAWAIN", 2, true), gate));
    }

    [Fact]
    public void One_ordinary_match_and_a_held_run_unlock_none_of_the_seven()
    {
        // A modified host lets this PC play one Challenge match (won 1-0) and hold its run, then names that match in all seven.
        var gate = new SharedPlayGate(new SharedPlayRecord());
        var inbox = new MomentInbox();
        gate.Welcomed(Session);
        inbox.Welcomed(Session);
        gate.NoteMatch(1, Ended(MatchKind.Challenge, 1, 1, 0), At, out _);
        gate.NoteRun(At, out _);

        foreach (string name in SharedMoments.Names)
        {
            bool run = SharedMoments.SourceOf(name) == MomentSource.Run;
            MomentArrival arrival = inbox.Arrived(new AchievementMoment(name, 1, run), gate, out MomentReason reason);
            Assert.True(arrival == MomentArrival.Refused, $"{name}: {arrival} ({reason})");
        }
    }

    [Fact]
    public void A_view_is_kept_in_the_record_and_survives_a_restart()
    {
        var record = new SharedPlayRecord();
        Assert.True(record.NoteView(Session, 4, Ended(MatchKind.Elite, 2, 5, 0, MatchSigns.EliteMercy | MatchSigns.HeadshotGoal), At));
        Assert.False(record.NoteView(Session, 4, MatchView.Unknown with { HomeGoals = 5 }, At)); // nothing new: nothing to save
        Assert.True(record.NoteMatch(Session, 5, At));
        Assert.True(record.NoteView(Session, 5, MatchView.Unknown with { Kind = MatchKind.Boss, Act = 2 }, At));

        string text = record.Format();
        Assert.Contains("4:3.2.5.5.0.7", text, StringComparison.Ordinal);
        Assert.Contains("5:4.2.0.-1.-1.0", text, StringComparison.Ordinal);
        SharedPlayRecord read = SharedPlayRecord.Parse(text);
        Assert.Equal(text, read.Format());
        Assert.Equal(Ended(MatchKind.Elite, 2, 5, 0, MatchSigns.EliteMercy | MatchSigns.HeadshotGoal), read.ViewOf(Session, 4));
        Assert.False(read.ViewOf(Session, 5).Ended);

        // The first 0.11.0 builds' bare numbers still read (nothing known of the match), and a damaged view drops the line.
        SharedPlayRecord bare = SharedPlayRecord.Parse($"session {Session:x16} at 2026-11-05T21:00:00.0000000Z run 1 matches 3,7\n" +
            $"session {Session + 1:x16} at 2026-11-05T21:00:00.0000000Z run 0 matches 3:9.9.9.9.9.9\n");
        Assert.True(bare.PlayedMatch(Session, 7));
        Assert.Equal(MatchView.Unknown, bare.ViewOf(Session, 7));
        Assert.False(bare.Has(Session + 1));
    }

    [Fact]
    public void A_view_reads_the_kind_of_match_from_the_setup_this_pc_loaded()
    {
        static KeyValuePair<string, NetValue> Value(string key, long value) => new(key, NetValue.FromInt(value));

        var elite = new List<KeyValuePair<string, NetValue>>
        {
            Value(CampaignMatchValues.GameTypeKey, CampaignMatchValues.CampaignGameType), Value("campaign.nodeType", 1), Value("campaign.act", 2),
            Value("mode.skaterHome", 5),
        };
        Assert.Equal(MatchView.Unknown with { Kind = MatchKind.Elite, Act = 2, Skaters = 5 }, MatchView.FromSetup(elite));

        elite[1] = Value("campaign.nodeType", 2);
        Assert.Equal(MatchKind.Boss, MatchView.FromSetup(elite).Kind);
        elite[1] = Value("campaign.nodeType", 0);
        Assert.Equal(MatchKind.Challenge, MatchView.FromSetup(elite).Kind);
        elite[1] = Value("campaign.nodeType", 9);
        Assert.Equal(MatchKind.OtherNode, MatchView.FromSetup(elite).Kind);
        elite.RemoveAt(1);
        Assert.Equal(MatchKind.Unknown, MatchView.FromSetup(elite).Kind);

        var exhibition = new List<KeyValuePair<string, NetValue>> { Value(CampaignMatchValues.GameTypeKey, 0), Value("mode.skaterHome", 3) };
        Assert.Equal(MatchView.Unknown with { Kind = MatchKind.Exhibition, Skaters = 3 }, MatchView.FromSetup(exhibition));
        Assert.Equal(MatchView.Unknown, MatchView.FromSetup(null));

        // What a host can write that isn't a view never reads as one.
        Assert.False(MatchView.TryParse("3.1.5.1000.0.0", out _));
        Assert.False(MatchView.TryParse("6.1.5.1.0.0", out _));
        Assert.False(MatchView.TryParse("3.1.5.1.0.8", out _));
        Assert.False(MatchView.TryParse(null, out _));
        Assert.Equal("an Elite match of act 2, seen to end 5-0 here, the mercy rule on an elite seen",
            (Ended(MatchKind.Elite, 2, 5, 0, MatchSigns.EliteMercy) with { Rule = WinRule.Ahead }).ToString());
        Assert.Equal("an Elite match of act 2, seen to end 5-0 here, the mercy rule on an elite seen, the game's rule for its winner wasn't read here (a win is the home team ahead)",
            Ended(MatchKind.Elite, 2, 5, 0, MatchSigns.EliteMercy).ToString());
    }
}
