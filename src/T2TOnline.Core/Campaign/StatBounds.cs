using System;
using System.Globalization;

namespace T2TOnline.Core.Campaign;

/// <summary>
/// The play a friend's PC saw with one host (D135): seconds of the host's matches played here, the host's matches played
/// here, seconds this PC followed with the host's co-op run held, and the host's runs held here. What a host may credit to a
/// friend's profile is bounded by it (<see cref="StatBounds"/>), so a host can't give more than this PC's own play could
/// have earned.
/// </summary>
/// <param name="MatchSeconds">Seconds of the host's matches played here (the stream playing).</param>
/// <param name="Matches">
/// The host's matches of which at least <see cref="StatBounds.SecondsPerMatch"/> played here (<see cref="MatchPlayCounter"/>):
/// one loaded and left sooner, or joined in its last seconds, isn't a match played here.
/// </param>
/// <param name="RunSeconds">Seconds following the host with its co-op run held here (its matches included).</param>
/// <param name="Runs">The host's co-op runs held here.</param>
public readonly record struct PlayTally(double MatchSeconds, int Matches, double RunSeconds, int Runs)
{
    /// <summary>The largest value one field may hold (a day holds 86,400 seconds; far more is damage).</summary>
    public const double MaxValue = 10_000_000;

    /// <summary>No play.</summary>
    public static PlayTally None => default;

    /// <summary>True when no play at all is counted.</summary>
    public bool IsEmpty => MatchSeconds <= 0 && Matches <= 0 && RunSeconds <= 0 && Runs <= 0;

    /// <summary>
    /// The matches the bounds count: <see cref="Matches"/>, each a match with <see cref="StatBounds.SecondsPerMatch"/> of it
    /// played here, and never more than one per that many seconds of match played in the window (and one more: a match whose
    /// first seconds fell out of it), so neither a host that loads match after match nor a damaged file buys anything per
    /// match.
    /// </summary>
    public int CountedMatches => (int)Math.Min(Math.Max(Matches, 0), 1 + Math.Floor(Math.Max(MatchSeconds, 0) / StatBounds.SecondsPerMatch));

    /// <summary>The runs the bounds count: at most one per <see cref="StatBounds.SecondsPerRun"/> of run held here (and one to start).</summary>
    public int CountedRuns => (int)Math.Min(Math.Max(Runs, 0), 1 + Math.Floor(Math.Max(RunSeconds, 0) / StatBounds.SecondsPerRun));

    /// <summary>Both together.</summary>
    public static PlayTally operator +(PlayTally left, PlayTally right) =>
        new(left.MatchSeconds + right.MatchSeconds, left.Matches + right.Matches, left.RunSeconds + right.RunSeconds, left.Runs + right.Runs);

    /// <summary>True when every field is a number from 0 to <see cref="MaxValue"/> (a value read from a file).</summary>
    public bool IsPlausible() =>
        IsPlausible(MatchSeconds) && Matches is >= 0 and <= (int)MaxValue && IsPlausible(RunSeconds) && Runs is >= 0 and <= (int)MaxValue;

    /// <summary>For logs: "12.3 min of the host's matches over 3 match(es), 20.0 min following its run, 1 run(s)".</summary>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture,
        $"{MatchSeconds / 60:0.0} min of the host's matches over {Matches} match(es), {RunSeconds / 60:0.0} min following its run, {Runs} run(s)");

    private static bool IsPlausible(double value) => double.IsFinite(value) && value is >= 0 and <= MaxValue;
}

/// <summary>How fast honest play can move a stat (<see cref="StatBounds"/>): the kind of stat it is.</summary>
public enum StatPace : byte
{
    /// <summary>A frequent action in a match (passes, shots, hits, knockouts, faceoffs, farts, an ability used).</summary>
    Busy,

    /// <summary>Seconds of a match (time on the ice, on attack, on defence; the longest game; the fastest goal).</summary>
    Clock,

    /// <summary>A goal or an assist of any kind (and records of them).</summary>
    Scoring,

    /// <summary>Something a match brings now and then (an injury, a broken stick, the glass, a brawl won, a referee hit).</summary>
    Rare,

    /// <summary>A hat trick (three goals by one skater in one match).</summary>
    HatTrick,

    /// <summary>One per match at most (a game played, won or lost, a shutout, a win against a team, a boss beaten).</summary>
    Match,

    /// <summary>A match's periods (a few per match).</summary>
    Periods,

    /// <summary>A step of the run outside its matches (a node visited, a reward chosen or skipped, an injury healed).</summary>
    Node,

    /// <summary>One per run at most (a run started, won, failed or conceded).</summary>
    Run,

    /// <summary>A count of the squad's skaters (the largest forwards, the special helmets, benched players on a win).</summary>
    Roster,
}

/// <summary>
/// One <see cref="StatPace"/>'s bound: what a host may add in 24 hours, from this PC's play with it in that time.
/// </summary>
/// <param name="Floor">
/// Allowed with no play at all (a batch that comes before its play is noted, a match missed during a drop, a match ended
/// before <see cref="StatBounds.SecondsPerMatch"/> of it played here).
/// </param>
/// <param name="PerMatchMinute">Per minute of the host's matches played here.</param>
/// <param name="PerMatch">Per match played here (<see cref="PlayTally.CountedMatches"/>).</param>
/// <param name="PerRunMinute">Per minute following with the host's run held here.</param>
/// <param name="PerRun">Per run held here (<see cref="PlayTally.CountedRuns"/>).</param>
public readonly record struct PaceRule(double Floor, double PerMatchMinute, double PerMatch, double PerRunMinute, double PerRun)
{
    /// <summary>The bound for <paramref name="play"/>.</summary>
    public double Bound(in PlayTally play) =>
        Floor + (PerMatchMinute * Math.Max(play.MatchSeconds, 0) / 60) + (PerMatch * play.CountedMatches) +
        (PerRunMinute * Math.Max(play.RunSeconds, 0) / 60) + (PerRun * play.CountedRuns);
}

/// <summary>
/// What a host may credit to each stat on a friend's profile, bounded by the play this PC saw with that host (D135): a
/// pace per stat (<see cref="PaceOf(int)"/>) and a rule per pace (<see cref="RuleOf"/>), each well above what honest play
/// gives. The rates are about five times the largest honest rate per minute (or per match, per run) measured on the
/// game's Nov 1 build; the floors cover a match missed during a drop or ended before <see cref="SecondsPerMatch"/> of it
/// played here. Never more than <see cref="ProgressLimits.MaxStatCreditPerHostDay"/>. Game-independent.
/// </summary>
public static class StatBounds
{
    /// <summary>
    /// A match counts for the bounds once this many seconds of it played here (<see cref="MatchPlayCounter"/>), and its match
    /// moments, run moments included, need as much of it played here before its end (<c>MatchSigns.PlayedEnough</c>). Below
    /// every real match measured (the shortest, a Challenge its first goal ended, played 32.8 s before its end on the
    /// Nov 1 build), so a match left sooner, or joined in its last seconds, isn't counted; a host streaming matches of a few
    /// seconds buys nothing with them, and one match per this many seconds played here at the most.
    /// </summary>
    public const double SecondsPerMatch = 20;

    /// <summary>
    /// A run counts for the bounds once per this many seconds of run held here: short, as players reroll squads quickly (a
    /// run started and conceded at once).
    /// </summary>
    public const double SecondsPerRun = 120;

    /// <summary>
    /// What a host may take off a stat beyond what it added to it in the window, once a match of its played here in that time
    /// (the game takes back only what it gave, in the match it gave it in; this covers a credit that came in an earlier window
    /// or before a lost save).
    /// </summary>
    public const double DecrementSlack = 5;

    /// <summary>The game's stats this table knows (<c>StatType</c> 0 to 194, Nov 6 build); later ones are <see cref="StatPace.Busy"/>.</summary>
    public const int KnownStats = 195;

    /// <summary>
    /// How many entities of a group one match can credit at most, for a group's field over all its entities (both teams'
    /// skaters and goalies, every talent, ability and relic the squad holds).
    /// </summary>
    public static int EntitiesPer(EntityStatGroup group) => group switch
    {
        EntityStatGroup.Skater => 16,
        EntityStatGroup.Goalie => 4,
        EntityStatGroup.Team => 2,
        EntityStatGroup.Talent => 40,
        EntityStatGroup.Ability => 20,
        EntityStatGroup.Relic => 30,
        _ => 1,
    };

    private static readonly StatPace[] Paces = Build();

    /// <summary>The rule of <paramref name="pace"/>.</summary>
    public static PaceRule RuleOf(StatPace pace) => pace switch
    {
        // Seconds: on the ice at most 1.2 s per second played here (a boss match's clock), attack and defence less.
        StatPace.Clock => new PaceRule(600, 120, 0, 0, 0),

        // The busiest: 12.4 passes a minute at most; 86 knockouts in one match.
        StatPace.Busy => new PaceRule(100, 60, 0, 0, 0),

        // 2.25 assists and 1.1 goals a minute at most; 15 assists in one match.
        StatPace.Scoring => new PaceRule(20, 10, 0, 0, 0),

        // 0.55 injuries a minute at most; 5 in one match.
        StatPace.Rare => new PaceRule(10, 6, 0, 0, 0),

        // One a match at most; 0.25 a minute.
        StatPace.HatTrick => new PaceRule(2, 1, 0, 0, 0),

        // One a match by the game's own rule.
        StatPace.Match => new PaceRule(3, 0, 2, 0, 0),

        // Five in one match at most (three and overtime).
        StatPace.Periods => new PaceRule(8, 0, 6, 0, 0),

        // 30 rewards over two hours of a run; 6 at once after a boss; 5 injuries healed at once.
        StatPace.Node => new PaceRule(10, 0, 3, 1, 0),

        // One a run by the game's own rule (started, then won, failed or conceded).
        StatPace.Run => new PaceRule(2, 0, 0, 0, 2),

        // A squad's skaters: a few dozen at most.
        StatPace.Roster => new PaceRule(30, 0, 0, 0, 0),
        _ => new PaceRule(100, 60, 0, 0, 0),
    };

    /// <summary>The pace of the game's stat <paramref name="stat"/> (its <c>StatType</c> number).</summary>
    public static StatPace PaceOf(int stat) => stat >= 0 && stat < Paces.Length ? Paces[stat] : StatPace.Busy;

    /// <summary>
    /// The pace of field <paramref name="field"/> of an entity stat (by its name in <see cref="EntityStatSchema"/>): games and
    /// wins per match, campaign and run wins per run, goals and assists as scoring, time on attack as a clock, injuries as
    /// rare, a reward chosen or upgraded as a node, anything else as busy.
    /// </summary>
    public static StatPace PaceOf(EntityStatGroup group, string field)
    {
        ArgumentNullException.ThrowIfNull(field);
        if (field.StartsWith("campaign", StringComparison.Ordinal) || field.StartsWith("runsWon", StringComparison.Ordinal))
        {
            return StatPace.Run;
        }

        if (field.Contains("ames", StringComparison.Ordinal) || field is "wins" or "losses" or "shutouts")
        {
            return StatPace.Match;
        }

        if (field.Contains("oals", StringComparison.Ordinal) || field.Contains("ssists", StringComparison.Ordinal))
        {
            return StatPace.Scoring;
        }

        if (field.StartsWith("timeOn", StringComparison.Ordinal))
        {
            return StatPace.Clock;
        }

        if (field.StartsWith("injuries", StringComparison.Ordinal))
        {
            return StatPace.Rare;
        }

        return field is "timesChosen" or "timesUpgraded" ? StatPace.Node : StatPace.Busy;
    }

    /// <summary>What a host may add to <paramref name="stat"/> in 24 hours, from <paramref name="play"/> with it.</summary>
    public static double CreditBound(int stat, in PlayTally play) => Math.Min(ProgressLimits.MaxStatCreditPerHostDay, RuleOf(PaceOf(stat)).Bound(play));

    /// <summary>
    /// What a host may take off a stat in 24 hours, having added <paramref name="added"/> to it in that time: what it gave,
    /// and a little (<see cref="DecrementSlack"/>) once a match of its played here in that time (<paramref name="play"/>),
    /// never more than <see cref="ProgressLimits.MaxStatDecrementPerHostDay"/>.
    /// </summary>
    public static double DecrementBound(double added, in PlayTally play) =>
        Math.Min(ProgressLimits.MaxStatDecrementPerHostDay, Math.Max(added, 0) + (play.Matches > 0 ? DecrementSlack : 0));

    /// <summary>
    /// For a log line, the bound <paramref name="entry"/> went past with <paramref name="play"/> and what the host had added
    /// to its stat already (<paramref name="added"/>): "at most 70 added", "at most 7 taken off", "a record of at most 400",
    /// "a match of the host's played here first".
    /// </summary>
    public static string LimitWords(StatEntry entry, in PlayTally play, double added)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (StatOps.IsReset(entry.Op) || entry.Op is StatOp.TrySetSmaller or StatOp.TrySetSmallerCampaign or StatOp.TrySetSmallerPlayNow)
        {
            return "a match of the host's played here first";
        }

        if (StatOps.IsRecord(entry.Op))
        {
            return string.Create(CultureInfo.InvariantCulture, $"a record of at most {CreditBound(entry.Stat, play):0.#}");
        }

        return ProgressLimits.CreditOf(entry) < 0
            ? string.Create(CultureInfo.InvariantCulture, $"at most {DecrementBound(added, play):0.#} taken off")
            : string.Create(CultureInfo.InvariantCulture, $"at most {CreditBound(entry.Stat, play):0.#} added");
    }

    /// <summary>What a host may add to field <paramref name="field"/> of one entity of <paramref name="group"/>, from <paramref name="play"/>.</summary>
    public static double EntityBound(EntityStatGroup group, string field, in PlayTally play) =>
        Math.Min(ProgressLimits.MaxStatCreditPerHostDay, RuleOf(PaceOf(group, field)).Bound(play));

    /// <summary>What a host may add to field <paramref name="field"/> of <paramref name="group"/> over all its entities, from <paramref name="play"/>.</summary>
    public static double EntityGroupBound(EntityStatGroup group, string field, in PlayTally play) =>
        Math.Min(ProgressLimits.MaxStatCreditPerHostDay, RuleOf(PaceOf(group, field)).Bound(play) * EntitiesPer(group));

    private static StatPace[] Build()
    {
        var paces = new StatPace[KnownStats];

        // GamesPlayed, GamesWon, GamesLost, GamesWonLost, OTGamesPlayed, OTGamesWon, OTGamesLost, OTGamesWonLost,
        // GamesWonInFirstPeriod, Shutouts, ShutoutsAgainst, CurrentWinStreak, LargestWinStreak, MercyWins, WinsAgainst*
        // (six), MostConsecutiveScoringGamesByOneSkaterInRun, GoldenHelmetPlayerBenchedGames, Act1BossWins, Act2BossWins.
        Set(paces, StatPace.Match, 0, 1, 2, 3, 4, 5, 6, 7, 8, 16, 17, 19, 20, 115, 144, 145, 146, 147, 148, 149, 164, 172, 178, 179);

        // PeriodsPlayed, OTPeriodsPlayed, MostOTPeriods.
        Set(paces, StatPace.Periods, 14, 15, 61);

        // FastestGoal, FastestHatTrick, TotalPlayTimeOnIce, LongestGame, TotalTimeOnAttack, TotalTimeOnDefense.
        Set(paces, StatPace.Clock, 11, 13, 59, 60, 62, 63);

        // TotalGoals, TotalGoalsAgainst, BiggestShutout, MostGoalsInOneGame, MostAssistsInOneGame, the goals by kind (33 to
        // 44), Assists, GoalieAssists, InjuredGoals, EnragedGoals, FroggedGoals, UnassistedGoals, BreakawayGoals,
        // ZeroShotPowerGoals, MostInjuredGoalsInRun, MostEnragedGoalsInRun, MostGoalieAssistsInAct,
        // MostAssistsWithoutGoalInRun, MostUniqueAssistersInRun, MostAssistsByOneSkaterInRun, EnragedCritGoals, AngusGoals,
        // MostAngusGoalsInRun, MultiValueGoals, KellyJellyGoals.
        Set(paces, StatPace.Scoring, 9, 10, 18, 21, 24, 33, 34, 35, 36, 37, 38, 39, 40, 41, 42, 43, 44, 82, 83, 121, 122, 123, 124, 125,
            137, 152, 153, 154, 160, 161, 162, 171, 182, 183, 184, 189);

        // HatTricks.
        Set(paces, StatPace.HatTrick, 12);

        // GlassBreaks, RefereeHits, RefereeHitsAgainst, InjuriesInflicted, MostInjuriesInflicted, InjuriesReceived,
        // MostInjuriesReceived, SticksBroken, MostInjuriesReceivedByOneSkaterInRun, GoldenHelmetInjuries,
        // RefereesFroggified, EnragedInjuries, BrawlsWon, HelmetsKnockedOff, MostInjuredSkatersOnIce (Nov 6 build: a record
        // of the most of the squad's skaters injured on the ice at once, at most the skaters a team has on the ice).
        Set(paces, StatPace.Rare, 55, 68, 69, 74, 75, 76, 77, 126, 163, 166, 169, 170, 188, 190, 193);

        // HighestGoldenHelmetChecking (Nov 6 build): a record of the best checking among the squad's golden-helmet forwards,
        // with bonuses. The game caps no forward stat at 100 but accuracy (a forward's checking goes up to 500, bonuses on
        // top), so it can pass 100: busy's floor holds 100 with no play, and 60 more for each minute of the host's matches
        // played here (the match that set it, noted before its batch applies).
        Set(paces, StatPace.Busy, 194);

        // InjuriesHealed, the visits (127 to 133), RewardsSelected, RewardsSkipped.
        Set(paces, StatPace.Node, 78, 127, 128, 129, 130, 131, 132, 133, 134, 135);

        // TotalRuns, RunsWon, Easy/Standard/HardRunsWon, RunsFailed, Easy/Standard/HardRunsFailed, RunsConceded,
        // NightmareRunsWon, NightmareRunsFailed.
        Set(paces, StatPace.Run, 105, 106, 107, 108, 109, 110, 111, 112, 113, 114, 150, 151);

        // MostLargeForwards, MostBenchedPlayersOnRunWin, MostSpecialHelmets.
        Set(paces, StatPace.Roster, 155, 156, 157);
        return paces;
    }

    private static void Set(StatPace[] paces, StatPace pace, params int[] stats)
    {
        foreach (int stat in stats)
        {
            paces[stat] = pace;
        }
    }
}

/// <summary>
/// Counts the host's matches for <see cref="PlayTally.Matches"/>: a match counts once <see cref="StatBounds.SecondsPerMatch"/>
/// of it played here, its seconds added up over a rejoin's new playback of the same match. A match is known by its number on
/// the host (any other number is another match). Game-independent. Not thread-safe (one game thread).
/// </summary>
public sealed class MatchPlayCounter
{
    private int _match;
    private double _seconds;
    private bool _counted;

    /// <summary>
    /// The host's match <paramref name="match"/> (its number on the host) played <paramref name="seconds"/> more here. True
    /// once, when that brings it to <see cref="StatBounds.SecondsPerMatch"/>: it counts as a match played here.
    /// </summary>
    public bool Played(int match, double seconds)
    {
        if (match != _match)
        {
            _match = match;
            _seconds = 0;
            _counted = false;
        }

        if (double.IsFinite(seconds) && seconds > 0)
        {
            _seconds += seconds;
        }

        if (_counted || _seconds < StatBounds.SecondsPerMatch)
        {
            return false;
        }

        _counted = true;
        return true;
    }
}
