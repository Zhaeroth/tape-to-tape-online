using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using T2TOnline.Core.Campaign;

namespace T2TOnline.Core.Tests.Campaign;

/// <summary>
/// D135: what a host may credit to a friend's profile in any 24 hours is bounded by the play the friend's PC saw with that
/// host (<see cref="StatBounds"/>): honest sessions pass with room, a host that plays one match and sends everything, or
/// wears a stat down, or cycles match loads, gets nothing past what that play explains.
/// </summary>
public class PlayBoundTests
{
    private const ulong Session = 0x5151;
    private static readonly DateTime Evening = new(2026, 11, 5, 18, 0, 0, DateTimeKind.Utc);
    private static readonly string Host = HostKeys.Tag(Enumerable.Range(1, HostKeys.SecretBytes).Select(i => (byte)i).ToArray(), 76561199000000001);

    // One five-minute match played here, in ten minutes of the host's run held here.
    private static readonly PlayTally OneMatch = new(300, 1, 600, 1);

    private static StatEntry Add(int stat, double value) =>
        new(stat, StatOp.IncrementCampaignBy, Math.Abs(value % 1) > 0 ? StatValue.Real((float)value) : StatValue.Whole((int)value));

    private static StatEntry Record(int stat, int value) => new(stat, StatOp.TrySetLargerCampaign, StatValue.Whole(value));

    /// <summary>
    /// Plays <paramref name="timeline"/> (<see cref="HonestNights"/>) into the ledger and the credit, its time running on with
    /// the play; returns the operations refused, and the largest share of a bound any stat used.
    /// </summary>
    private static (int Refused, double Share, string Tightest) Replay(IEnumerable<string> timeline, ProgressLedger ledger, ProgressCredit credit, ref DateTime now)
    {
        int refused = 0;
        double share = 0;
        string tightest = string.Empty;
        foreach (string line in timeline)
        {
            string[] parts = line.Split(' ');
            if (parts[0] == "p")
            {
                var play = new PlayTally(Number(parts[1]), (int)Number(parts[2]), Number(parts[3]), (int)Number(parts[4]));
                ledger.NotePlay(Host, now, play);
                now = now.AddSeconds(Math.Max(play.MatchSeconds, play.RunSeconds));
                continue;
            }

            foreach (string item in parts.Skip(1))
            {
                string[] pair = item.Split(':');
                int stat = (int)Number(pair[0]);
                StatEntry entry = pair[1] == "reset" ? new StatEntry(stat, StatOp.ResetCampaign, StatValue.None)
                    : stat == ProgressGameStats.LargestWinStreak ? new StatEntry(stat, StatOp.TrySetLargerCampaignFromStat, StatValue.Whole(ProgressGameStats.CurrentWinStreak))
                    : pair[1].StartsWith('=') ? Record(stat, (int)Math.Ceiling(Number(pair[1][1..])))
                    : Add(stat, Number(pair[1]));
                if (!credit.TryGive(Session, entry, ledger, Host, now))
                {
                    refused++;
                    continue;
                }

                double used = StatOps.IsRecord(entry.Op) ? entry.Value.WholeValue : ledger.BudgetUsed(Host, stat, now).Added;
                double bound = StatBounds.CreditBound(stat, ledger.PlayWithin(Host, now));
                if (!StatOps.IsFromStat(entry.Op) && used / bound > share)
                {
                    share = used / bound;
                    tightest = $"stat {stat}: {used} of {bound:0.#}";
                }
            }
        }

        return (refused, share, tightest);
    }

    private static double Number(string text) => double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);

    // ---------------------------------------------------------------- honest play passes

    [Fact]
    public void An_honest_long_session_passes_every_operation_with_room()
    {
        DateTime now = Evening;
        (int refused, double share, string tightest) = Replay(HonestNights.Long, new ProgressLedger(), new ProgressCredit(), ref now);
        Assert.Equal(0, refused);
        Assert.True(share < 0.6, tightest); // the tightest is games played: ten credited (one for a match missed in a drop) of 21
    }

    [Fact]
    public void An_honest_short_session_passes_every_operation_with_room()
    {
        DateTime now = Evening;
        (int refused, double share, string tightest) = Replay(HonestNights.Short, new ProgressLedger(), new ProgressCredit(), ref now);
        Assert.Equal(0, refused);
        Assert.True(share < 0.6, tightest);
    }

    [Fact]
    public void Six_hours_of_honest_play_with_one_host_pass()
    {
        var ledger = new ProgressLedger();
        var credit = new ProgressCredit();
        DateTime now = Evening;
        int refused = 0;
        double share = 0;
        string tightest = string.Empty;
        for (int run = 0; run < 4; run++)
        {
            (int r, double s, string t) = Replay(HonestNights.Long, ledger, credit, ref now);
            refused += r;
            (share, tightest) = s > share ? (s, t) : (share, tightest);
        }

        Assert.True(now - Evening > TimeSpan.FromHours(6), $"{now - Evening}");
        Assert.Equal(0, refused);
        Assert.True(share < 0.6, tightest);
    }

    // ---------------------------------------------------------------- attacks fail

    [Fact]
    public void A_host_that_plays_one_match_and_sends_everything_gets_only_what_that_match_explains()
    {
        var ledger = new ProgressLedger();
        var credit = new ProgressCredit();
        ledger.NotePlay(Host, Evening, OneMatch);

        // Every stat of the game pumped to 1,000,000, and every record set to it: all refused.
        for (int stat = 0; stat < StatBounds.KnownStats; stat++)
        {
            Assert.False(credit.TryGive(Session, Add(stat, 1_000_000), ledger, Host, Evening), $"stat {stat}");
            Assert.False(credit.TryGive(Session, Record(stat, 1_000_000), ledger, Host, Evening), $"record {stat}");
        }

        Assert.Equal(2 * StatBounds.KnownStats, credit.OverBudget);

        // Sent one at a time, each stat takes at most its bound for that match.
        for (int stat = 0; stat < StatBounds.KnownStats; stat++)
        {
            int taken = 0;
            while (taken < 1_000 && credit.TryGive(Session, Add(stat, 1), ledger, Host, Evening))
            {
                taken++;
            }

            Assert.True(taken <= StatBounds.CreditBound(stat, OneMatch), $"stat {stat}: {taken}");
        }

        // The ten hat tricks of the game's HAT_TRICK achievement can't come from one match; games won are a handful.
        Assert.True(ledger.BudgetUsed(Host, 12, Evening).Added < 10);
        Assert.True(ledger.BudgetUsed(Host, 1, Evening).Added <= 5);
    }

    [Fact]
    public void Loading_match_after_match_without_playing_them_buys_nothing_per_match()
    {
        // A match counts once 20 s of it played here: one of a few seconds, or loaded and left, doesn't.
        var counter = new MatchPlayCounter();
        int counted = 0;
        for (int match = 1; match <= 100; match++)
        {
            for (int second = 0; second < 15; second++)
            {
                counted += counter.Played(match, 1) ? 1 : 0;
            }
        }

        Assert.Equal(0, counted);

        // 20 s of one match count it once; a rejoin's playback of the same match adds up; another match starts over.
        Assert.False(counter.Played(200, 12));
        Assert.True(counter.Played(200, 8));
        Assert.False(counter.Played(200, 600));
        Assert.False(counter.Played(201, 19.9));
        Assert.True(counter.Played(201, 0.1));
        Assert.False(counter.Played(202, double.NaN));

        // A ledger that claims more matches than its seconds: one per 20 s played (and one more), no more.
        var ledger = new ProgressLedger();
        ledger.NotePlay(Host, Evening, new PlayTally(100, 100, 100, 50));
        PlayTally play = ledger.PlayWithin(Host, Evening);
        Assert.Equal(6, play.CountedMatches);
        Assert.Equal(1, play.CountedRuns);
        Assert.Equal(15, StatBounds.CreditBound(1, play)); // games won: 3 and 2 a match
    }

    [Fact]
    public void A_low_count_stat_can_t_be_worn_down_even_after_a_long_night()
    {
        var ledger = new ProgressLedger();
        var credit = new ProgressCredit();
        ledger.NotePlay(Host, Evening, new PlayTally(4 * 3600, 40, 6 * 3600, 3));

        // Goals the host never gave: at most the slack comes off.
        int taken = 0;
        for (int i = 0; i < 1_000; i++)
        {
            taken += credit.TryGive(Session + (ulong)i, Add(9, -1), ledger, Host, Evening) ? 1 : 0;
        }

        Assert.Equal((int)StatBounds.DecrementSlack, taken);

        // A goal given and cancelled (the game's own take-back) passes.
        Assert.True(credit.TryGive(Session, Add(10, 1), ledger, Host, Evening));
        Assert.True(credit.TryGive(Session, Add(10, -1), ledger, Host, Evening));
    }

    [Fact]
    public void With_no_match_played_here_a_host_takes_nothing_off_a_stat_it_never_gave()
    {
        // The game takes back only what it credited, in the match it credited it in: with no match of the host's played here
        // in the window there's nothing for the slack to cover, so day after day nothing comes off.
        var ledger = new ProgressLedger();
        var credit = new ProgressCredit();
        int taken = 0;
        for (int day = 0; day < 10; day++)
        {
            for (int i = 0; i < 100; i++)
            {
                taken += credit.TryGive(Session + (ulong)((day * 100) + i), Add(9, -1), ledger, Host, Evening.AddDays(day)) ? 1 : 0;
            }
        }

        Assert.Equal(0, taken);
        Assert.Equal("at most 0 taken off", StatBounds.LimitWords(Add(9, -1), PlayTally.None, 0));

        // Time following the host's run alone isn't a match either; one match played here brings the slack.
        ledger.NotePlay(Host, Evening, new PlayTally(0, 0, 3600, 1));
        Assert.False(credit.TryGive(Session, Add(9, -1), ledger, Host, Evening));
        ledger.NotePlay(Host, Evening, OneMatch);
        Assert.True(credit.TryGive(Session, Add(9, -1), ledger, Host, Evening));
    }

    [Fact]
    public void Records_resets_and_fastest_times_are_bounded_by_the_play_too()
    {
        var ledger = new ProgressLedger();
        var credit = new ProgressCredit();
        var reset = new StatEntry(ProgressGameStats.CurrentWinStreak, StatOp.ResetCampaign, StatValue.None);
        var fastest = new StatEntry(11, StatOp.TrySetSmallerCampaign, StatValue.Real(0.01f));

        // No match played here with this host: no reset, no fastest goal; a record no larger than the floor.
        Assert.False(credit.TryGive(Session, reset, ledger, Host, Evening));
        Assert.False(credit.TryGive(Session, fastest, ledger, Host, Evening));
        Assert.True(credit.TryGive(Session, Record(21, 5), ledger, Host, Evening)); // most goals in one game
        Assert.False(credit.TryGive(Session, Record(21, 1_000), ledger, Host, Evening));

        // One match played: the reset and the fastest time pass; the record's bound grew with it.
        ledger.NotePlay(Host, Evening, OneMatch);
        Assert.True(credit.TryGive(Session, reset, ledger, Host, Evening));
        Assert.True(credit.TryGive(Session, fastest, ledger, Host, Evening));
        Assert.True(credit.TryGive(Session, Record(21, 70), ledger, Host, Evening));
        Assert.False(credit.TryGive(Session, Record(21, 71), ledger, Host, Evening));

        // A record from another stat is this profile's own: it passes whatever the play.
        Assert.True(credit.TryGive(Session, new StatEntry(20, StatOp.TrySetLargerCampaignFromStat, StatValue.Whole(19)), new ProgressLedger(), Host, Evening));
        Assert.Equal(4, credit.OverBudget);
    }

    [Fact]
    public void Another_host_s_play_funds_nothing_of_this_one()
    {
        var ledger = new ProgressLedger();
        var credit = new ProgressCredit();
        string sam = HostKeys.Tag(Enumerable.Range(1, HostKeys.SecretBytes).Select(i => (byte)i).ToArray(), 76561199000000002);
        ledger.NotePlay(sam, Evening, new PlayTally(4 * 3600, 40, 6 * 3600, 3));
        Assert.True(credit.TryGive(Session, Add(9, 20), ledger, Host, Evening)); // the floor
        Assert.False(credit.TryGive(Session, Add(9, 1), ledger, Host, Evening));
        Assert.True(credit.TryGive(Session, Add(9, 2_000), ledger, sam, Evening));
    }

    // ---------------------------------------------------------------- entity stats

    private static EntityStatEntry Skater(string id, int goals, int games = 1)
    {
        var values = new List<EntityStatValue>();
        foreach (EntityField field in EntityStatSchema.Fields(EntityStatGroup.Skater))
        {
            values.Add(field.Kind switch
            {
                EntityFieldKind.Text => EntityStatValue.OfText(id),
                EntityFieldKind.WholeList => EntityStatValue.OfList(null),
                EntityFieldKind.Real => EntityStatValue.OfReal(0),
                _ => EntityStatValue.OfWhole(field.Name == "goals" ? goals : field.Name == "gamesPlayed" ? games : 0),
            });
        }

        return new EntityStatEntry(true, false, EntityStatGroup.Skater, id, values);
    }

    [Fact]
    public void An_entity_s_count_is_bounded_by_the_play_and_a_group_s_across_hosting_sessions()
    {
        var ledger = new ProgressLedger();
        var credit = new EntityCredit();
        ledger.NotePlay(Host, Evening, OneMatch);
        double perEntity = StatBounds.EntityBound(EntityStatGroup.Skater, "goals", OneMatch);
        Assert.Equal(70, perEntity); // scoring: 20 and 10 a minute

        // One skater: 1,000 goals from one match is refused; its bound passes, one more in the session doesn't.
        Assert.False(credit.TryGive(Session, Skater("skater-a", 1_000), ledger, Host, Evening));
        Assert.True(credit.TryGive(Session, Skater("skater-a", 70), ledger, Host, Evening));
        Assert.False(credit.TryGive(Session, Skater("skater-a", 1), ledger, Host, Evening));
        Assert.Equal(2, credit.OverBudget);

        // Hosting again starts the session's count over, but not the host's budget for the group's goals.
        int sessions = 1;
        while (credit.TryGive(Session + (ulong)sessions, Skater("skater-a", 70), ledger, Host, Evening))
        {
            sessions++;
        }

        Assert.Equal(StatBounds.EntitiesPer(EntityStatGroup.Skater), sessions);

        // Games played: three and two a match for one skater.
        Assert.False(new EntityCredit().TryGive(Session, Skater("skater-b", 0, games: 6), ledger, Host, Evening));

        // No ledger: the session's caps only, as before.
        Assert.True(new EntityCredit().TryGive(Session, Skater("skater-c", 1_000)));
    }

    // ---------------------------------------------------------------- the table

    [Fact]
    public void Every_stat_of_the_game_has_a_pace_and_the_entity_fields_theirs()
    {
        Assert.Equal(StatPace.Scoring, StatBounds.PaceOf(9));
        Assert.Equal(StatPace.Clock, StatBounds.PaceOf(59));
        Assert.Equal(StatPace.Match, StatBounds.PaceOf(0));
        Assert.Equal(StatPace.Periods, StatBounds.PaceOf(14));
        Assert.Equal(StatPace.Node, StatBounds.PaceOf(134));
        Assert.Equal(StatPace.Run, StatBounds.PaceOf(105));
        Assert.Equal(StatPace.HatTrick, StatBounds.PaceOf(12));
        Assert.Equal(StatPace.Rare, StatBounds.PaceOf(188));
        Assert.Equal(StatPace.Roster, StatBounds.PaceOf(156));
        Assert.Equal(StatPace.Busy, StatBounds.PaceOf(84));
        Assert.Equal(StatPace.Busy, StatBounds.PaceOf(4_000)); // a stat a later build adds
        Assert.Equal(StatPace.Busy, StatBounds.PaceOf(-1));
        Assert.All(Enum.GetValues<StatPace>(), pace => Assert.True(StatBounds.RuleOf(pace).Floor > 0));

        Assert.Equal(StatPace.Scoring, StatBounds.PaceOf(EntityStatGroup.Skater, "goals"));
        Assert.Equal(StatPace.Scoring, StatBounds.PaceOf(EntityStatGroup.Goalie, "goalsAgainstByDifficulty"));
        Assert.Equal(StatPace.Match, StatBounds.PaceOf(EntityStatGroup.Skater, "gamesPlayed"));
        Assert.Equal(StatPace.Match, StatBounds.PaceOf(EntityStatGroup.Team, "wins"));
        Assert.Equal(StatPace.Match, StatBounds.PaceOf(EntityStatGroup.Talent, "totalGamesWithTalent"));
        Assert.Equal(StatPace.Run, StatBounds.PaceOf(EntityStatGroup.Skater, "campaignWon"));
        Assert.Equal(StatPace.Run, StatBounds.PaceOf(EntityStatGroup.Relic, "runsWonWithRelic"));
        Assert.Equal(StatPace.Clock, StatBounds.PaceOf(EntityStatGroup.Team, "timeOnAttack"));
        Assert.Equal(StatPace.Node, StatBounds.PaceOf(EntityStatGroup.Talent, "timesChosen"));
        Assert.Equal(StatPace.Rare, StatBounds.PaceOf(EntityStatGroup.Skater, "injuriesReceived"));
        Assert.Equal(StatPace.Busy, StatBounds.PaceOf(EntityStatGroup.Ability, "timesActivated"));
    }

    [Fact]
    public void The_two_records_the_Nov_6_build_adds_have_their_paces_and_honest_values_pass()
    {
        // StatType kept 0 to 192 and added 193 MostInjuredSkatersOnIce and 194 HighestGoldenHelmetChecking.
        Assert.Equal(195, StatBounds.KnownStats);
        Assert.Equal(StatPace.Rare, StatBounds.PaceOf(193));
        Assert.Equal(StatPace.Busy, StatBounds.PaceOf(194));

        var ledger = new ProgressLedger();
        var credit = new ProgressCredit();

        // With no play noted yet: every skater a team has on the ice injured at once, and a checking of 100.
        Assert.True(credit.TryGive(Session, Record(193, 5), ledger, Host, Evening));
        Assert.True(credit.TryGive(Session, Record(194, 100), ledger, Host, Evening));

        // The game caps no forward stat at 100 but accuracy: a golden-helmet forward's checking with bonuses can pass 100,
        // and the match that set it (noted before its batch) explains it.
        ledger.NotePlay(Host, Evening, OneMatch);
        Assert.True(credit.TryGive(Session, Record(194, 250), ledger, Host, Evening));

        // One five-minute match explains neither of these.
        Assert.False(credit.TryGive(Session, Record(193, 100), ledger, Host, Evening));
        Assert.False(credit.TryGive(Session, Record(194, 1_000), ledger, Host, Evening));
    }

    [Fact]
    public void Play_that_isn_t_plausible_is_never_noted_and_the_log_words_name_nobody()
    {
        var ledger = new ProgressLedger();
        ledger.NotePlay(Host, Evening, new PlayTally(double.NaN, 1, 1, 1));
        ledger.NotePlay(Host, Evening, new PlayTally(-5, 1, 1, 1));
        ledger.NotePlay(Host, Evening, new PlayTally(1e9, 1, 1, 1));
        ledger.NotePlay(Host, Evening, PlayTally.None);
        Assert.Equal(PlayTally.None, ledger.PlayWithin(Host, Evening));
        Assert.False(ledger.Changed);
        Assert.Throws<ArgumentException>(() => ledger.NotePlay("not a key", Evening, OneMatch));

        Assert.Equal("5.0 min of the host's matches over 1 match(es), 10.0 min following its run, 1 run(s)", OneMatch.ToString());
        Assert.Equal("at most 70 added", StatBounds.LimitWords(Add(9, 100), OneMatch, 0));
        Assert.Equal("at most 8 taken off", StatBounds.LimitWords(Add(9, -10), OneMatch, 3));
        Assert.Equal("a record of at most 70", StatBounds.LimitWords(Record(21, 100), OneMatch, 0));
        Assert.Equal("a match of the host's played here first", StatBounds.LimitWords(new StatEntry(19, StatOp.ResetCampaign, StatValue.None), OneMatch, 0));
    }
}
