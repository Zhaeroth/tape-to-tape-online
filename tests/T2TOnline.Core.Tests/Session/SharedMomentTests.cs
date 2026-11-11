using System;
using System.Linq;
using T2TOnline.Core.Campaign;
using T2TOnline.Core.Protocol;
using T2TOnline.Core.Session;

namespace T2TOnline.Core.Tests.Session;

/// <summary>
/// Shared achievement moments (D34, D48, D135): the host owes a moment only to the friends who shared the play it came from,
/// and keeps it with that play; a friend's game unlocks one only for play it saw itself: a match it played under the number
/// the moment names, or the host's co-op run held here.
/// </summary>
public class SharedMomentTests
{
    private const ulong Alex = 201;
    private const ulong Sam = 202;
    private const ulong Riley = 203;
    private const ulong Session = 0xabc;
    private static readonly ulong[] Everyone = { Alex, Sam, Riley };
    private static readonly DateTime At = new(2026, 11, 5, 21, 0, 0, DateTimeKind.Utc);

    private static AchievementMoment Match(string name, int match) => new(name, match, false);

    private static AchievementMoment Run(string name, int match) => new(name, match, true);

    // What this PC saw of a match (enough of each played here): an elite won on the mercy rule, a boss won, an
    // exhibition, still playing.
    private static readonly MatchView EliteMercyWin = new(MatchKind.Elite, 1, 5, true, 5, 0, MatchSigns.EliteMercy | MatchSigns.PlayedEnough);
    private static readonly MatchView ExhibitionWon = new(MatchKind.Exhibition, 0, 5, true, 3, 1, MatchSigns.PlayedEnough);
    private static readonly MatchView ExhibitionPlaying = MatchView.Unknown with { Kind = MatchKind.Exhibition, Skaters = 5, Signs = MatchSigns.PlayedEnough };

    private static MatchView BossWon(int act) => new(MatchKind.Boss, act, 5, true, 4, 2, MatchSigns.PlayedEnough);

    private static string[] Names(AchievementLedger ledger, ulong friend) => ledger.OwedTo(friend).Select(m => m.Name).ToArray();

    private static (MomentInbox Inbox, SharedPlayGate Gate, SharedPlayRecord Record) Friend()
    {
        var record = new SharedPlayRecord();
        var gate = new SharedPlayGate(record);
        var inbox = new MomentInbox();
        gate.Welcomed(Session);
        inbox.Welcomed(Session);
        return (inbox, gate, record);
    }

    // ---------------------------------------------------------------- the host's ledger

    [Fact]
    public void A_match_moment_is_owed_only_to_the_friends_who_watched_that_match()
    {
        var ledger = new AchievementLedger();
        ledger.Watching(Alex, 3);
        ledger.Watching(Sam, 2); // an earlier match: their load of match 3 failed

        MomentShares shares = ledger.Record("MERCY", MomentSource.Match, match: 3, runShared: true, Everyone);

        Assert.True(shares.FirstTime);
        Assert.Equal(new[] { Alex }, shares.NewlyOwed);
        Assert.Equal(new[] { Sam, Riley }, shares.NotShared);
        Assert.Equal(new[] { Match("MERCY", 3) }, ledger.OwedTo(Alex));
        Assert.Empty(ledger.OwedTo(Sam));
    }

    [Fact]
    public void A_friend_who_dropped_out_of_the_match_they_watched_is_still_owed_its_moment()
    {
        var ledger = new AchievementLedger();
        ledger.Watching(Riley, 5);
        // Riley drops (their seat is kept, the AI plays for them); the match goes on and is won by mercy.

        MomentShares shares = ledger.Record("MERCY", MomentSource.Match, match: 5, runShared: false, Everyone);

        Assert.Equal(new[] { Riley }, shares.NewlyOwed);
        Assert.Equal(new[] { Match("MERCY", 5) }, ledger.OwedTo(Riley));
    }

    [Fact]
    public void No_match_moment_without_a_match()
    {
        var ledger = new AchievementLedger();
        ledger.Watching(Alex, 0); // nothing loaded yet: not a match

        Assert.Empty(ledger.Record("SCORING_SQUAD", MomentSource.Match, match: 0, runShared: true, Everyone).NewlyOwed);
    }

    [Fact]
    public void A_run_moment_goes_to_everyone_in_the_session_only_on_the_shared_run()
    {
        var ledger = new AchievementLedger();
        ledger.Watching(Alex, 1); // an exhibition together earlier

        MomentShares solo = ledger.Record("WIN_ACT3", MomentSource.Run, match: 4, runShared: false, Everyone);
        Assert.Empty(solo.NewlyOwed);
        Assert.Equal(Everyone, solo.NotShared);

        MomentShares coop = ledger.Record("WIN_ACT3", MomentSource.Run, match: 9, runShared: true, Everyone);
        Assert.False(coop.FirstTime);
        Assert.Equal(Everyone, coop.NewlyOwed);
        Assert.Equal(new[] { Run("WIN_ACT3", 9) }, ledger.OwedTo(Sam));
    }

    [Fact]
    public void A_moment_earned_again_in_the_same_match_is_owed_once_and_a_friend_who_didn_t_watch_the_later_match_keeps_the_first()
    {
        var ledger = new AchievementLedger();
        ledger.Watching(Alex, 1);
        Assert.Equal(new[] { Alex }, ledger.Record("MERCY", MomentSource.Match, 1, false, Everyone).NewlyOwed);
        Assert.Empty(ledger.Record("MERCY", MomentSource.Match, 1, false, Everyone).NewlyOwed); // the game unlocks it again in the same match

        // Alex dropped after match 1 (her seat is kept); Sam watches match 2, where it's earned again.
        ledger.Watching(Sam, 2);
        MomentShares again = ledger.Record("MERCY", MomentSource.Match, 2, false, Everyone);

        Assert.False(again.FirstTime);
        Assert.Equal(new[] { Sam }, again.NewlyOwed);
        Assert.Equal(new[] { Match("MERCY", 1) }, ledger.OwedTo(Alex));
        Assert.Equal(new[] { Match("MERCY", 2) }, ledger.OwedTo(Sam));
        Assert.Equal(new[] { "MERCY" }, ledger.Earned);
    }

    [Fact]
    public void A_moment_refused_for_one_match_is_owed_again_from_a_later_match_the_friend_shared()
    {
        // Alex joins the host's elite match 3 in its last seconds. The host wins it: ELITE_WIN is owed to her for match 3,
        // and her game refuses it (too little of that match played here), for good.
        var ledger = new AchievementLedger();
        ledger.Watching(Alex, 3);
        Assert.Equal(new[] { Alex }, ledger.Record("ELITE_WIN", MomentSource.Match, 3, true, Everyone).NewlyOwed);
        var (inbox, gate, _) = Friend();
        gate.NoteMatch(3, new MatchView(MatchKind.Elite, 1, 5, true, 2, 1, MatchSigns.None), At, out _);
        Assert.Equal(MomentArrival.Refused, inbox.Arrived(Assert.Single(ledger.OwedTo(Alex)), gate, out MomentReason reason));
        Assert.Equal(MomentReason.TooLittlePlayed, reason);

        // Match 7, another elite, she plays from its start to its end. The host's game unlocks ELITE_WIN again (it does
        // every time): it's owed to her again, for the match she shared, and her game unlocks it.
        ledger.Watching(Alex, 7);
        gate.NoteMatch(7, new MatchView(MatchKind.Elite, 2, 5, true, 3, 0, MatchSigns.PlayedEnough), At, out _);
        MomentShares again = ledger.Record("ELITE_WIN", MomentSource.Match, 7, true, Everyone);
        Assert.False(again.FirstTime);
        Assert.Equal(new[] { Alex }, again.NewlyOwed);
        AchievementMoment owed = Assert.Single(ledger.OwedTo(Alex));
        Assert.Equal(Match("ELITE_WIN", 7), owed);
        Assert.Equal(MomentArrival.UnlockNow, inbox.Arrived(owed, gate, out _));

        // Earned once more in that match, and the catch-up after a rejoin: nothing new on either side.
        Assert.Empty(ledger.Record("ELITE_WIN", MomentSource.Match, 7, true, Everyone).NewlyOwed);
        Assert.Equal(MomentArrival.Repeat, inbox.Arrived(Assert.Single(ledger.OwedTo(Alex)), gate, out _));
    }

    [Fact]
    public void A_run_moment_earned_again_moves_to_the_later_match_only_for_a_friend_who_watched_it()
    {
        // GAWAIN comes from every boss win of the shared run. Alex watched both boss matches; Sam dropped before the second
        // (his seat is kept): what he's owed stays the match he may have seen, not one his game never played.
        var ledger = new AchievementLedger();
        ledger.Watching(Alex, 5);
        ledger.Watching(Sam, 5);
        Assert.Equal(Everyone, ledger.Record("GAWAIN", MomentSource.Run, 5, true, Everyone).NewlyOwed);

        ledger.Watching(Alex, 10);
        Assert.Equal(new[] { Alex }, ledger.Record("GAWAIN", MomentSource.Run, 10, true, Everyone).NewlyOwed);
        Assert.Equal(new[] { Run("GAWAIN", 10) }, ledger.OwedTo(Alex));
        Assert.Equal(new[] { Run("GAWAIN", 5) }, ledger.OwedTo(Sam));
        Assert.Equal(new[] { Run("GAWAIN", 5) }, ledger.OwedTo(Riley));
    }

    [Fact]
    public void What_a_friend_is_owed_comes_back_in_the_order_earned_with_its_play_and_hosting_s_end_forgets_it()
    {
        var ledger = new AchievementLedger();
        ledger.Watching(Alex, 1);
        ledger.Record("SCORING_SQUAD", MomentSource.Match, 1, true, Everyone);
        ledger.Record("GAWAIN", MomentSource.Run, 1, true, Everyone);
        ledger.Record("MERCY", MomentSource.Match, 1, true, Everyone);

        Assert.Equal(new[] { Match("SCORING_SQUAD", 1), Run("GAWAIN", 1), Match("MERCY", 1) }, ledger.OwedTo(Alex));
        Assert.Equal(new[] { "GAWAIN" }, Names(ledger, Sam));

        ledger.Clear();
        Assert.Empty(ledger.OwedTo(Alex));
        Assert.Empty(ledger.Earned);
        Assert.Empty(ledger.Record("MERCY", MomentSource.Match, 1, true, Everyone).NewlyOwed); // who watched is forgotten too
    }

    // ---------------------------------------------------------------- the friend's check

    [Fact]
    public void A_match_moment_unlocks_only_for_a_match_this_pc_played_under_that_number()
    {
        var (inbox, gate, _) = Friend();
        gate.NoteMatch(3, EliteMercyWin, At, out _);
        gate.NoteMatch(5, At, out _);

        Assert.Equal(MomentArrival.UnlockNow, inbox.Arrived(Match("MERCY", 3), gate, out MomentReason reason));
        Assert.Equal(MomentReason.None, reason);

        // Match 4 played on the host while this PC wasn't in it (it played 5 since): never.
        Assert.Equal(MomentArrival.Refused, inbox.Arrived(Match("ELITE_WIN", 4), gate, out reason));
        Assert.Equal(MomentReason.MatchNotPlayed, reason);

        // No match at all.
        Assert.Equal(MomentArrival.Refused, inbox.Arrived(Match("SCORING_SQUAD", 0), gate, out reason));
        Assert.Equal(MomentReason.NoMatch, reason);
        Assert.Equal(0, inbox.Waiting);
    }

    [Fact]
    public void A_match_moment_of_a_match_still_loading_here_waits_and_unlocks_once_it_plays()
    {
        var (inbox, gate, _) = Friend();
        gate.NoteMatch(6, At, out _);

        Assert.Equal(MomentArrival.Waiting, inbox.Arrived(Match("HEADSHOT_REDIRECT", 7), gate, out MomentReason reason));
        Assert.Equal(MomentReason.MatchNotYet, reason);
        Assert.Equal(MomentArrival.AlreadyWaiting, inbox.Arrived(Match("HEADSHOT_REDIRECT", 7), gate, out _));
        Assert.False(inbox.TryTakeDecided(gate, out _, out _));

        // It plays here, but the headshot goal's event hasn't come through this PC's stream yet: still waiting.
        gate.NoteMatch(7, ExhibitionPlaying, At, out _);
        Assert.False(inbox.TryTakeDecided(gate, out _, out _));
        gate.NoteView(7, MatchView.Unknown with { Signs = MatchSigns.HeadshotGoal }, At, out _);
        Assert.True(inbox.TryTakeDecided(gate, out AchievementMoment moment, out reason));
        Assert.Equal((Match("HEADSHOT_REDIRECT", 7), MomentReason.None), (moment, reason));
        Assert.Equal(0, inbox.Waiting);
    }

    [Fact]
    public void A_waiting_match_moment_is_refused_once_this_pc_plays_a_later_match_instead()
    {
        var (inbox, gate, _) = Friend();
        Assert.Equal(MomentArrival.Waiting, inbox.Arrived(Match("MERCY", 7), gate, out _));

        gate.NoteMatch(8, At, out _);
        Assert.True(inbox.TryTakeDecided(gate, out AchievementMoment moment, out MomentReason reason));
        Assert.Equal((Match("MERCY", 7), MomentReason.MatchNotPlayed), (moment, reason));
    }

    [Fact]
    public void A_run_moment_unlocks_only_once_this_pc_holds_the_host_s_co_op_run()
    {
        var (inbox, gate, _) = Friend();
        gate.NoteMatch(4, BossWon(2), At, out _);
        gate.NoteMatch(9, BossWon(3), At, out _); // a match played isn't the run held

        Assert.Equal(MomentArrival.Waiting, inbox.Arrived(Run("WIN_ACT3", 9), gate, out MomentReason reason));
        Assert.Equal(MomentReason.RunNotHeld, reason);

        Assert.True(gate.NoteRun(At, out _));
        Assert.False(gate.NoteRun(At, out _)); // once
        Assert.True(inbox.TryTakeDecided(gate, out AchievementMoment moment, out reason));
        Assert.Equal((Run("WIN_ACT3", 9), MomentReason.None), (moment, reason));

        // GAWAIN comes from a boss win mid-run: held now, it unlocks at once.
        Assert.Equal(MomentArrival.UnlockNow, inbox.Arrived(Run("GAWAIN", 4), gate, out _));
    }

    [Fact]
    public void A_moment_whose_source_contradicts_its_name_or_that_isn_t_shared_is_refused()
    {
        var (inbox, gate, _) = Friend();
        gate.NoteMatch(2, At, out _);
        gate.NoteRun(At, out _);

        Assert.Equal(MomentArrival.Refused, inbox.Arrived(Run("MERCY", 2), gate, out MomentReason reason));
        Assert.Equal(MomentReason.WrongSource, reason);
        Assert.Equal(MomentArrival.Refused, inbox.Arrived(Match("WIN_ACT3", 2), gate, out reason));
        Assert.Equal(MomentReason.WrongSource, reason);
        Assert.Equal(MomentArrival.Refused, inbox.Arrived(Match("ACH_HAT_TRICK", 2), gate, out reason));
        Assert.Equal(MomentReason.NotShared, reason);
        Assert.All(Enum.GetValues<MomentReason>(), r => Assert.False(string.IsNullOrEmpty(MomentCheck.Words(r))));
    }

    [Fact]
    public void Each_moment_of_each_match_is_decided_once_per_hosting_session()
    {
        var (inbox, gate, _) = Friend();
        gate.NoteMatch(3, EliteMercyWin, At, out _);
        gate.NoteMatch(4, EliteMercyWin, At, out _);

        Assert.Equal(MomentArrival.UnlockNow, inbox.Arrived(Match("MERCY", 3), gate, out _));
        Assert.Equal(MomentArrival.Repeat, inbox.Arrived(Match("MERCY", 3), gate, out _)); // the catch-up after a rejoin
        Assert.Equal(MomentArrival.UnlockNow, inbox.Arrived(Match("MERCY", 4), gate, out _)); // another match: its own
        Assert.Equal(MomentArrival.Refused, inbox.Arrived(Match("ELITE_WIN", 1), gate, out _));
        Assert.Equal(MomentArrival.Repeat, inbox.Arrived(Match("ELITE_WIN", 1), gate, out _)); // refused once, said once
        Assert.Equal(2, inbox.Repeats);

        // A new hosting session (the host hosted again): its moments are its own.
        inbox.Welcomed(Session + 1);
        gate.Welcomed(Session + 1);
        gate.NoteMatch(3, EliteMercyWin, At, out _);
        Assert.Equal(MomentArrival.UnlockNow, inbox.Arrived(Match("MERCY", 3), gate, out _));
    }

    [Fact]
    public void A_host_that_fills_the_decided_moments_can_t_have_one_unlocked_again_and_again()
    {
        // A host sends far more moments than the game has; past the inbox's memory, one it then sends over and over would be
        // judged (and unlocked, asked of Steam, logged) every time.
        var (inbox, gate, _) = Friend();
        gate.NoteMatch(3, EliteMercyWin, At, out _);
        for (int i = 0; i < MomentInbox.MaxDecided; i++)
        {
            Assert.Equal(MomentArrival.Refused, inbox.Arrived(Match($"NOT_A_MOMENT_{i}", 3), gate, out _));
        }

        Assert.Equal(MomentArrival.Refused, inbox.Arrived(Match("MERCY", 3), gate, out MomentReason reason));
        Assert.Equal(MomentReason.TooMany, reason);
        Assert.Equal(MomentArrival.Refused, inbox.Arrived(Match("MERCY", 3), gate, out _));
        Assert.Equal(MomentArrival.Repeat, inbox.Arrived(Match("NOT_A_MOMENT_0", 3), gate, out _));

        // A new hosting session starts over.
        inbox.Welcomed(Session + 1);
        gate.Welcomed(Session + 1);
        gate.NoteMatch(3, EliteMercyWin, At, out _);
        Assert.Equal(MomentArrival.UnlockNow, inbox.Arrived(Match("MERCY", 3), gate, out _));
    }

    [Fact]
    public void A_name_from_the_host_reaches_the_log_only_when_it_is_a_shared_moment()
    {
        Assert.Equal("MERCY", MomentCheck.NameForLog("MERCY"));
        Assert.DoesNotContain("76561199000000001", MomentCheck.NameForLog("76561199000000001"), StringComparison.Ordinal);
        Assert.DoesNotContain("Alex", MomentCheck.NameForLog("Alex says hi"), StringComparison.Ordinal);
        Assert.False(string.IsNullOrEmpty(MomentCheck.NameForLog(null)));
    }

    [Fact]
    public void A_moment_missed_during_a_drop_unlocks_with_the_catch_up_if_this_pc_played_that_match()
    {
        // The host side: Alex plays match 4, drops during it; the match is won by mercy while she's away.
        var ledger = new AchievementLedger();
        ledger.Watching(Alex, 4);
        Assert.Equal(new[] { Alex }, ledger.Record("MERCY", MomentSource.Match, 4, false, Everyone).NewlyOwed);

        // Alex's side: match 4 played before the drop; the rejoin (same hosting session) keeps what she saw.
        var (inbox, gate, _) = Friend();
        gate.NoteMatch(4, EliteMercyWin, At, out _);
        gate.Welcomed(Session);
        inbox.Welcomed(Session);

        // The catch-up sends what she's owed, with its match: it unlocks.
        AchievementMoment owed = Assert.Single(ledger.OwedTo(Alex));
        Assert.Equal(MomentArrival.UnlockNow, inbox.Arrived(owed, gate, out _));

        // Sam never played match 4: the same moment, sent by a host that says so anyway, isn't his.
        var (bobInbox, bobGate, _) = Friend();
        bobGate.NoteMatch(5, At, out _);
        Assert.Equal(MomentArrival.Refused, bobInbox.Arrived(owed, bobGate, out MomentReason reason));
        Assert.Equal(MomentReason.MatchNotPlayed, reason);
    }

    [Fact]
    public void After_a_restart_the_record_says_what_this_pc_played_and_the_catch_up_unlocks_for_that_only()
    {
        var (_, gate, record) = Friend();
        Assert.True(gate.NoteMatch(4, EliteMercyWin, At, out bool noted));
        Assert.True(noted);
        Assert.True(gate.NoteMatch(9, BossWon(3), At, out noted));
        Assert.True(gate.NoteRun(At, out noted));
        Assert.True(noted);

        // The game restarts (the record is the file read again) and rejoins the same hosting session.
        var restarted = new SharedPlayGate(SharedPlayRecord.Parse(record.Format()));
        var inbox = new MomentInbox();
        Assert.True(restarted.Welcomed(Session));
        inbox.Welcomed(Session);

        Assert.Equal(MomentArrival.UnlockNow, inbox.Arrived(Match("MERCY", 4), restarted, out _));
        Assert.Equal(MomentArrival.UnlockNow, inbox.Arrived(Run("WIN_ACT3", 9), restarted, out _));
        Assert.Equal(MomentArrival.Waiting, inbox.Arrived(Match("ELITE_WIN", 10), restarted, out _)); // not played (yet)

        // Another hosting session knows none of it.
        var elsewhere = new SharedPlayGate(SharedPlayRecord.Parse(record.Format()));
        Assert.False(elsewhere.Welcomed(Session + 1));
        Assert.False(elsewhere.Played(4));
        Assert.False(elsewhere.HeldRun);
    }

    [Fact]
    public void Another_host_naming_a_hosting_session_this_pc_shared_with_someone_else_inherits_none_of_its_play()
    {
        // This PC played match 4 and held the run in Alex's hosting session. Sam was a friend there too, so Sam knows its id
        // (every welcome carries it), and a modified game can name it in its own welcome.
        byte[] secret = Enumerable.Range(1, HostKeys.SecretBytes).Select(i => (byte)i).ToArray();
        const ulong alex = 76561199000000001, sam = 76561199000000002;
        var record = new SharedPlayRecord();
        var gate = new SharedPlayGate(record);
        gate.Welcomed(HostKeys.SessionTag(secret, alex, Session));
        gate.NoteMatch(4, At, out _);
        gate.NoteRun(At, out _);

        // A restart, then Sam's welcome naming that session: nothing of Alex's play counts for Sam's moments.
        var later = new SharedPlayGate(SharedPlayRecord.Parse(record.Format()));
        var inbox = new MomentInbox();
        Assert.False(later.Welcomed(HostKeys.SessionTag(secret, sam, Session)));
        inbox.Welcomed(Session);
        Assert.Equal(MomentArrival.Waiting, inbox.Arrived(Match("MERCY", 4), later, out MomentReason reason));
        Assert.Equal(MomentReason.MatchNotYet, reason);
        Assert.Equal(MomentArrival.Waiting, inbox.Arrived(Run("WIN_ACT3", 4), later, out reason));
        Assert.Equal(MomentReason.RunNotHeld, reason);

        // Alex's own welcome into it after a restart: what this PC shared there counts.
        var again = new SharedPlayGate(SharedPlayRecord.Parse(record.Format()));
        Assert.True(again.Welcomed(HostKeys.SessionTag(secret, alex, Session)));
        Assert.True(again.Played(4));
        Assert.True(again.HeldRun);
    }

    [Fact]
    public void A_friend_keeps_only_so_many_waiting_says_what_was_left_and_a_new_session_forgets_them()
    {
        var (inbox, gate, _) = Friend();
        for (int i = 0; i < MomentInbox.MaxWaiting; i++)
        {
            Assert.Equal(MomentArrival.Waiting, inbox.Arrived(Match("MERCY", 10 + i), gate, out _));
        }

        Assert.Equal(MomentArrival.Full, inbox.Arrived(Match("MERCY", 99), gate, out _));

        var left = inbox.TakeLeft(gate);
        Assert.Equal(MomentInbox.MaxWaiting, left.Count);
        Assert.All(left, l => Assert.Equal(MomentReason.MatchNotYet, l.Reason));
        Assert.Equal(0, inbox.Waiting);

        inbox.Arrived(Run("WIN_ACT3", 1), gate, out _);
        inbox.Clear();
        Assert.Equal(0, inbox.Waiting);
        gate.NoteRun(At, out _);
        Assert.False(inbox.TryTakeDecided(gate, out _, out _));
    }

    [Fact]
    public void The_one_pc_test_with_no_session_still_judges_by_what_this_game_saw()
    {
        var gate = new SharedPlayGate(new SharedPlayRecord());
        var inbox = new MomentInbox();
        Assert.True(gate.NoteMatch(2, ExhibitionWon, At, out bool noted));
        Assert.False(noted); // no session: nothing kept
        Assert.Equal(MomentArrival.UnlockNow, inbox.Arrived(Match("SCORING_SQUAD", 2), gate, out _));
    }
}
