using System;
using System.Collections.Generic;
using T2TOnline.Core.Serialization;

namespace T2TOnline.Core.Campaign;

/// <summary>
/// One of the game's stat operations: the twelve methods every stat object (<c>IntStat</c>, <c>FloatStat</c>) has, which
/// are where all of the game's stat credit lands (D42). Values are part of the protocol: only add.
/// </summary>
public enum StatOp : byte
{
    /// <summary>Not a valid operation.</summary>
    None = 0,

    /// <summary>Lifetime value + 1.</summary>
    Increment = 1,

    /// <summary>Campaign value + 1 (and this run's and this act's).</summary>
    IncrementCampaign = 2,

    /// <summary>Play Now value + 1.</summary>
    IncrementPlayNow = 3,

    /// <summary>Lifetime value + an amount.</summary>
    IncrementBy = 4,

    /// <summary>Campaign value + an amount (and this run's and this act's).</summary>
    IncrementCampaignBy = 5,

    /// <summary>Play Now value + an amount.</summary>
    IncrementPlayNowBy = 6,

    /// <summary>Lifetime record: raised to a value when it's larger.</summary>
    TrySetLarger = 7,

    /// <summary>Campaign record (and this run's and this act's): raised to a value when it's larger.</summary>
    TrySetLargerCampaign = 8,

    /// <summary>Play Now record: raised to a value when it's larger.</summary>
    TrySetLargerPlayNow = 9,

    /// <summary>Lifetime record: lowered to a value when it's smaller (a fastest time).</summary>
    TrySetSmaller = 10,

    /// <summary>Campaign record: lowered to a value when it's smaller.</summary>
    TrySetSmallerCampaign = 11,

    /// <summary>Play Now record: lowered to a value when it's smaller.</summary>
    TrySetSmallerPlayNow = 12,

    /// <summary>
    /// The lifetime and campaign values (and this run's and this act's) set back to zero: the current win streak broken by a
    /// campaign match lost (the game sets it outright, <c>StatRepository.SetStat(stat, 0)</c>, which none of the counting
    /// operations goes through). Only for <see cref="ProgressGameStats.CurrentWinStreak"/>.
    /// </summary>
    ResetCampaign = 13,

    /// <summary>The lifetime and Play Now values set back to zero (a streak broken outside the campaign). Only the current win streak.</summary>
    ResetPlayNow = 14,

    /// <summary>
    /// Lifetime record raised to another stat's own lifetime value, the stat named by the operation's value: the game's
    /// largest win streak, from the current streak right after a win. Each profile's from its own streak, never the host's.
    /// Only <see cref="ProgressGameStats.LargestWinStreak"/> from <see cref="ProgressGameStats.CurrentWinStreak"/>.
    /// </summary>
    TrySetLargerFromStat = 15,

    /// <summary>Campaign record (and this run's and this act's) raised to another stat's own lifetime value (see <see cref="TrySetLargerFromStat"/>).</summary>
    TrySetLargerCampaignFromStat = 16,

    /// <summary>Play Now record raised to another stat's own lifetime value (see <see cref="TrySetLargerFromStat"/>).</summary>
    TrySetLargerPlayNowFromStat = 17,
}

/// <summary>Which of a stat's values an operation is about (not on the wire: for the appliers and logs).</summary>
public enum StatBucket
{
    /// <summary>The lifetime value (every mode).</summary>
    Lifetime,

    /// <summary>The campaign value, which every unlock threshold reads.</summary>
    Campaign,

    /// <summary>The Play Now value.</summary>
    PlayNow,
}

/// <summary>What the <see cref="StatOp"/> values mean.</summary>
public static class StatOps
{
    /// <summary>True for an operation this build knows.</summary>
    public static bool IsKnown(StatOp op) => op is >= StatOp.Increment and <= StatOp.TrySetLargerPlayNowFromStat;

    /// <summary>
    /// True when the operation takes a value: every one but the three plain increments and the two resets (a record from
    /// another stat takes that stat's number).
    /// </summary>
    public static bool HasValue(StatOp op) => op is >= StatOp.IncrementBy and <= StatOp.TrySetSmallerPlayNow || IsFromStat(op);

    /// <summary>True for a record (set larger or smaller), which keeps the best value instead of adding.</summary>
    public static bool IsRecord(StatOp op) => op is >= StatOp.TrySetLarger and <= StatOp.TrySetSmallerPlayNow || IsFromStat(op);

    /// <summary>True for a reset (the value set back to zero, not added to).</summary>
    public static bool IsReset(StatOp op) => op is StatOp.ResetCampaign or StatOp.ResetPlayNow;

    /// <summary>True for a record raised to another stat's own value on each profile (its value names that stat).</summary>
    public static bool IsFromStat(StatOp op) => op is >= StatOp.TrySetLargerFromStat and <= StatOp.TrySetLargerPlayNowFromStat;

    /// <summary>True when the operation changes the campaign value, which every unlock threshold reads.</summary>
    public static bool IsCampaign(StatOp op) =>
        op is StatOp.IncrementCampaign or StatOp.IncrementCampaignBy or StatOp.TrySetLargerCampaign or StatOp.TrySetSmallerCampaign or
            StatOp.ResetCampaign or StatOp.TrySetLargerCampaignFromStat;

    /// <summary>True when the operation changes the Play Now value.</summary>
    public static bool IsPlayNow(StatOp op) =>
        op is StatOp.IncrementPlayNow or StatOp.IncrementPlayNowBy or StatOp.TrySetLargerPlayNow or StatOp.TrySetSmallerPlayNow or
            StatOp.ResetPlayNow or StatOp.TrySetLargerPlayNowFromStat;

    /// <summary>The value an operation is about (a reset sets the lifetime value to zero too).</summary>
    public static StatBucket BucketOf(StatOp op) => IsCampaign(op) ? StatBucket.Campaign : IsPlayNow(op) ? StatBucket.PlayNow : StatBucket.Lifetime;
}

/// <summary>
/// Facts about the game's stats that shared progress relies on (Oct 29 build): which stats
/// the game sets outright rather than counting, and which name the holder of a record. Values are the game's
/// <c>StatType</c> numbers, part of the protocol.
/// </summary>
public static class ProgressGameStats
{
    /// <summary><c>StatType.CurrentWinStreak</c>: +1 on a win, set back to zero on a loss (<c>AddWinStreakStats</c>).</summary>
    public const int CurrentWinStreak = 19;

    /// <summary><c>StatType.LargestWinStreak</c>: raised to the current streak after a win.</summary>
    public const int LargestWinStreak = 20;

    /// <summary><c>StatType.MostGoalsInOneGame</c>, whose holder is <see cref="TeamWithMostGoalsInOneGame"/>.</summary>
    public const int MostGoalsInOneGame = 21;

    /// <summary><c>StatType.TeamWithMostGoalsInOneGame</c>: the team's name, set when the lifetime record rises.</summary>
    public const int TeamWithMostGoalsInOneGame = 22;

    /// <summary><c>StatType.MostHitsInOneGame</c>, whose holder is <see cref="TeamWithMostHitsInOneGame"/>.</summary>
    public const int MostHitsInOneGame = 28;

    /// <summary><c>StatType.TeamWithMostHitsInOneGame</c>: the team's name, set when the lifetime record rises.</summary>
    public const int TeamWithMostHitsInOneGame = 29;

    /// <summary>True for the only stat the game sets back to zero outside a run's or act's reset.</summary>
    public static bool IsResettable(int stat) => stat == CurrentWinStreak;

    /// <summary>True when the game raises record <paramref name="record"/> to the value of stat <paramref name="source"/>.</summary>
    public static bool IsRecordFrom(int record, int source) => record == LargestWinStreak && source == CurrentWinStreak;

    /// <summary>The stat naming the holder of record <paramref name="record"/>, or -1 when it has none.</summary>
    public static int HolderOf(int record) => record switch
    {
        MostGoalsInOneGame => TeamWithMostGoalsInOneGame,
        MostHitsInOneGame => TeamWithMostHitsInOneGame,
        _ => -1,
    };
}

/// <summary>A stat operation's value: whole for the game's whole-number stats, real for its time stats, or none.</summary>
/// <param name="Kind">Which of the two it holds, if either.</param>
/// <param name="WholeValue">The value of a whole-number stat.</param>
/// <param name="RealValue">The value of a real-number stat.</param>
public readonly record struct StatValue(StatValueKind Kind, int WholeValue, float RealValue)
{
    /// <summary>No value (a plain increment).</summary>
    public static StatValue None => default;

    /// <summary>A whole-number value.</summary>
    public static StatValue Whole(int value) => new(StatValueKind.Whole, value, 0f);

    /// <summary>A real-number value.</summary>
    public static StatValue Real(float value) => new(StatValueKind.Real, 0, value);

    /// <inheritdoc />
    public override string ToString() => Kind switch
    {
        StatValueKind.Whole => WholeValue.ToString(System.Globalization.CultureInfo.InvariantCulture),
        StatValueKind.Real => RealValue.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
        _ => "-",
    };
}

/// <summary>Which number a <see cref="StatValue"/> holds. Values are part of the protocol: only add.</summary>
public enum StatValueKind : byte
{
    /// <summary>None.</summary>
    None = 0,

    /// <summary>A whole number (<c>IntStat</c>).</summary>
    Whole = 1,

    /// <summary>A real number (<c>FloatStat</c>).</summary>
    Real = 2,
}

/// <summary>How a shared run ended for a squad's record. Values are part of the protocol: only add.</summary>
public enum SquadOutcome : byte
{
    /// <summary>Not a valid outcome.</summary>
    None = 0,

    /// <summary>The run was won (the game's <c>ProfileData.RecordSquadWin</c>).</summary>
    Won = 1,

    /// <summary>The run was lost or conceded (the game's <c>ProfileData.RecordSquadLoss</c>).</summary>
    Lost = 2,
}

/// <summary>
/// A step of the game's bench quest system that happens outside a match, so the match stream doesn't carry it (the
/// in-match ones reach a friend's own quest system as mirrored events). Values are part of the protocol: only add.
/// </summary>
public enum QuestStep : byte
{
    /// <summary>Not a valid step.</summary>
    None = 0,

    /// <summary>A match of the run ended: the Mark Bench lineup check (<c>CheckMarkBenchLineup</c>).</summary>
    MatchEnded = 1,

    /// <summary>The run was won (<c>BenchQuestSystem.OnRunWon</c>).</summary>
    RunWon = 2,

    /// <summary>A reward was assigned (<c>OnRewardAssigned</c>: the Boring Dude quest counts talents).</summary>
    RewardAssigned = 3,

    /// <summary>A new act started (<c>OnNewActCreated</c>, with the act).</summary>
    NewAct = 4,

    /// <summary>The run was lost or conceded (<c>OnRunFailed</c>: the per-run counts start again).</summary>
    RunFailed = 5,
}

/// <summary>
/// One piece of profile progress the host's game credited, in the order it happened (D42). A closed set: the kinds
/// below are all there is.
/// </summary>
public abstract record ProgressEntry
{
    private protected ProgressEntry()
    {
    }
}

/// <summary>A stat operation on the stat <paramref name="Stat"/> (the game's <c>StatType</c> value).</summary>
/// <param name="Stat">The stat, by the game's <c>StatType</c> number.</param>
/// <param name="Op">What was done to it.</param>
/// <param name="Value">With what (none for a plain increment).</param>
public sealed record StatEntry(int Stat, StatOp Op, StatValue Value) : ProgressEntry;

/// <summary>
/// An unlock check the host's game ran (<c>UnlockSystem.RunUnlockPass</c>): a friend's game runs the same check on its
/// own profile. <paramref name="Earned"/> lists the live-match unlocks (which read the match as it happens, so they can't
/// be checked afterwards) this check earned on the host.
/// </summary>
/// <param name="Timing">The game's <c>UnlockTiming</c> number.</param>
/// <param name="NotificationTiming">The game's <c>ENotificationTiming</c> the host's check used (for the log).</param>
/// <param name="Queue">Whether the host's check queued unlock toasts.</param>
/// <param name="Earned">The live-match unlocks (reward ids) this check earned on the host.</param>
public sealed record UnlockPassEntry(int Timing, int NotificationTiming, bool Queue, IReadOnlyList<string> Earned) : ProgressEntry;

/// <summary>
/// A shared run's end for its squad's record (wins, losses, best difficulty, fastest win, and the most goals one player
/// scored in a run, with that player's name). Since batch format 2 it carries the host's best scorer of the run: the game
/// takes the squad's "most goals in a run" from the run's own scorers (<c>ProfileData.UpdateMaxGoalsIfAppropriate</c>, on
/// a win and on a loss), which a friend's game doesn't hold as the host's game did at the run's end.
/// </summary>
/// <param name="Outcome">Won or lost.</param>
/// <param name="Squad">The squad's id.</param>
/// <param name="Difficulty">The run's difficulty.</param>
/// <param name="RunSeconds">The run's time on the host (a friend's fastest win is the shared run's time).</param>
/// <param name="BestScorer">
/// The run's best scorer by the game's own rule (the first of its scorers with the most goals, when that's more than none),
/// as the game names them (<c>ScorersStats.displayName</c>); empty when nobody scored.
/// </param>
/// <param name="BestGoals">That scorer's goals in the run (0 when <paramref name="BestScorer"/> is empty).</param>
public sealed record SquadEntry(SquadOutcome Outcome, string Squad, int Difficulty, float RunSeconds, string BestScorer = "", int BestGoals = 0) : ProgressEntry;

/// <summary>
/// The host's game ended the shared run for its benchwarmers (<c>BenchSnapshotService.HandleRunEnding</c>: a random share of
/// each benchwarmer's powerups kept, hot and cold rolled, their toasts queued). A friend's game runs the same call on its own
/// copy of the run, so the snapshots, and their roll, are its own profile's. Run-bound; at most one per batch.
/// </summary>
public sealed record RunEndingEntry : ProgressEntry;

/// <summary>What a <see cref="ProfileMarkEntry"/> marks on a friend's profile. Values are part of the protocol: only add.</summary>
public enum ProfileMark : byte
{
    /// <summary>Not a valid mark.</summary>
    None = 0,

    /// <summary>
    /// The host's Nurse offered a procedure (<c>NurseProcedures.MarkOfferedAndSave</c>): this profile's own Nurse doesn't
    /// offer it again (<c>NurseProcedures.MarkOffered</c> on the profile's <c>offeredNurseProcedures</c>).
    /// </summary>
    NurseOffered = 1,

    /// <summary>
    /// An event NPC's line the host's game marked seen (<c>SeenDialogues.MarkSeen</c>: a first visit's or a visit line's),
    /// which the friends heard from the host's (D62): this profile's own NPC doesn't say it again.
    /// </summary>
    DialogueSeen = 2,
}

/// <summary>
/// Something the host's game marked on its profile while the shared run played, which a friend's profile marks too (the
/// profile only, never the run): a Nurse procedure offered, an event NPC's line seen. Made by <see cref="NurseOffered"/>
/// and <see cref="DialogueSeen"/>.
/// </summary>
/// <param name="Mark">What it marks.</param>
/// <param name="Procedure">The Nurse procedure (the game's <c>NurseProcedureState.Procedure</c> number) for <see cref="ProfileMark.NurseOffered"/>, else 0.</param>
/// <param name="Dialogue">The seen dialogue's id for <see cref="ProfileMark.DialogueSeen"/>, else empty.</param>
public sealed record ProfileMarkEntry(ProfileMark Mark, int Procedure, string Dialogue) : ProgressEntry
{
    /// <summary>The host's Nurse offered procedure <paramref name="procedure"/>.</summary>
    public static ProfileMarkEntry NurseOffered(int procedure) => new(ProfileMark.NurseOffered, procedure, string.Empty);

    /// <summary>The host's game marked event NPC line <paramref name="id"/> seen.</summary>
    public static ProfileMarkEntry DialogueSeen(string id) => new(ProfileMark.DialogueSeen, 0, id);

    /// <summary>For logs: "Nurse procedure 3 offered" or "dialogue chaosNode/visit_1 seen".</summary>
    public string Describe() => Mark == ProfileMark.NurseOffered
        ? $"Nurse procedure {Procedure.ToString(System.Globalization.CultureInfo.InvariantCulture)} offered"
        : $"dialogue {Diagnostics.HostText.KeyForLog(Dialogue)} seen";
}

/// <summary>The host's game showed the golfer's comic strips: a friend's game marks the strips its own profile unlocked as seen.</summary>
public sealed record ComicsEntry : ProgressEntry;

/// <summary>The shared run's history entry, field by field (see <see cref="RunHistoryData"/>), for every player's run history.</summary>
/// <param name="Entry">The entry.</param>
public sealed record RunHistoryRecord(RunHistoryData Entry) : ProgressEntry;

/// <summary>A bench quest step outside a match (see <see cref="QuestStep"/>).</summary>
/// <param name="Step">The step.</param>
/// <param name="Act">The act, for <see cref="QuestStep.NewAct"/> (else 0).</param>
public sealed record QuestEntry(QuestStep Step, int Act) : ProgressEntry;

/// <summary>
/// The holder of a record, by name (the game sets the text stat outright when the lifetime record rises, which no
/// counting operation goes through): "the team that played this match is <paramref name="Text"/>; if the record operation
/// just before raised your lifetime record of <paramref name="RecordStat"/>, <paramref name="Stat"/> names it". So each
/// profile names the holder of its own record.
/// </summary>
/// <param name="Stat">The text stat (<see cref="ProgressGameStats.HolderOf"/> of <paramref name="RecordStat"/>).</param>
/// <param name="RecordStat">The record it goes with.</param>
/// <param name="Text">The team's name.</param>
/// <param name="Campaign">The host's game set it in the campaign (the campaign value too) rather than Play Now.</param>
public sealed record StatTextEntry(int Stat, int RecordStat, string Text, bool Campaign) : ProgressEntry;

/// <summary>Limits on progress entries, and the check every entry passes before it's sent or used.</summary>
public static class ProgressLimits
{
    /// <summary>Largest stat number (the game has about 190).</summary>
    public const int MaxStat = 4095;

    /// <summary>Largest unlock timing number (the game has 7).</summary>
    public const int MaxTiming = 31;

    /// <summary>Most live-match unlocks one check can earn (the game has 3).</summary>
    public const int MaxEarned = 8;

    /// <summary>Longest id (unlock, squad, run), in UTF-8 bytes.</summary>
    public const int MaxIdBytes = 64;

    /// <summary>Longest text a stat or an entity stat holds (a team's or a skater's name, a face), in UTF-8 bytes.</summary>
    public const int MaxTextBytes = 128;

    /// <summary>Largest difficulty number.</summary>
    public const int MaxDifficulty = 63;

    /// <summary>Largest act number.</summary>
    public const int MaxAct = 15;

    /// <summary>Most goals one player scores in a run (a whole run's best is a few dozen).</summary>
    public const int MaxRunGoals = 1000;

    /// <summary>Longest best scorer's name a squad record carries, in UTF-8 bytes (the game's are short skater names).</summary>
    public const int MaxScorerBytes = 64;

    /// <summary>Largest Nurse procedure number (the game has 10).</summary>
    public const int MaxProcedure = 63;

    /// <summary>Most profile marks in one batch (an event visit makes one or two).</summary>
    public const int MaxMarksPerBatch = 64;

    /// <summary>
    /// Largest encoded run history entry, uncompressed (a 15-match run comes to a few KB). Also the most a whole message
    /// may inflate to: a batch holds at most one entry (the host seals right after it), so a message can't make a friend
    /// inflate more than this however many entries it claims.
    /// </summary>
    public const int MaxRunHistoryBytes = 256 * 1024;

    /// <summary>Largest notification timing number (a flags value; -1 is the game's "all").</summary>
    public const int MaxNotificationTiming = 0xFFFF;

    /// <summary>
    /// Largest value one stat operation carries: a count, seconds or a record from one match or run (the game's are far
    /// smaller). A record is never negative; an adding operation may be, down to <see cref="MaxStatDecrement"/>.
    /// </summary>
    public const int MaxStatValue = 1_000_000;

    /// <summary>
    /// Most one adding operation may take off a stat. The game takes back a little of what it credited now and then (for example
    /// <c>TotalGoalsAgainst</c> by -1 when a goal against it is cancelled), never much at once.
    /// </summary>
    public const int MaxStatDecrement = 100;

    /// <summary>
    /// Most one hosting session takes off one stat on a friend's profile, its batches together (<see cref="ProgressCredit"/>),
    /// so a host can't wear a friend's stat down batch after batch (the friend's applier also never takes one below zero).
    /// </summary>
    public const int MaxStatDecrementPerSession = 1_000;

    /// <summary>Most one batch adds to one stat, its operations together (a plain increment adds 1).</summary>
    public const int MaxStatCreditPerBatch = 1_000_000;

    /// <summary>
    /// Most one hosting session adds to one stat on a friend's profile, its batches together (<see cref="ProgressCredit"/>):
    /// far more than any session gives (the largest are seconds on the ice: a day of play is under 90,000), so a host can't
    /// pump a stat batch after batch.
    /// </summary>
    public const int MaxStatCreditPerSession = 1_000_000;

    /// <summary>
    /// Most one host adds to one stat on a friend's profile in any 24 hours, over all its hosting sessions
    /// (<see cref="ProgressLedger.TrySpend"/>), however much play this PC saw with it: the ceiling above the bound that play
    /// sets (<see cref="StatBounds"/>). A two-hour co-op run credits under 3,000 to any one stat (the largest are seconds on
    /// the ice), and a whole day holds 86,400 seconds.
    /// </summary>
    public const int MaxStatCreditPerHostDay = 200_000;

    /// <summary>
    /// Most one host takes off one stat on a friend's profile in any 24 hours, over all its hosting sessions (the game
    /// takes back a goal or a shot now and then: a few a night), and never more than it added to that stat in that time
    /// and, once a match of its played here, a little (<see cref="StatBounds.DecrementBound"/>).
    /// </summary>
    public const int MaxStatDecrementPerHostDay = 1_000;

    /// <summary>
    /// Non-empty printable ASCII with no spaces at all, at most <see cref="MaxIdBytes"/> long. The game's unlock, squad
    /// and run ids are identifiers and GUIDs; anything else is corrupt or hostile.
    /// </summary>
    public static bool IsPlausibleId(string? id)
    {
        if (string.IsNullOrEmpty(id) || id.Length > MaxIdBytes)
        {
            return false;
        }

        foreach (char c in id)
        {
            if (c is <= ' ' or > '~')
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Text a stat may hold: at most <see cref="MaxTextBytes"/> of UTF-8 and no control characters (it goes in logs and on
    /// the stats screens). Empty is allowed (the game leaves some names unset).
    /// </summary>
    public static bool IsPlausibleText(string? text)
    {
        if (text == null || text.Length > MaxTextBytes || System.Text.Encoding.UTF8.GetByteCount(text) > MaxTextBytes)
        {
            return false;
        }

        foreach (char c in text)
        {
            if (char.IsControl(c))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>True when <paramref name="entry"/> is within every limit (so it can be sent, and was read right).</summary>
    public static bool IsValid(ProgressEntry? entry) => entry switch
    {
        StatEntry stat => stat.Stat is >= 0 and <= MaxStat && StatOps.IsKnown(stat.Op) && IsValidValue(stat.Op, stat.Value) && IsAllowed(stat),
        StatTextEntry text => text.RecordStat is >= 0 and <= MaxStat && ProgressGameStats.HolderOf(text.RecordStat) == text.Stat &&
            text.Text.Length > 0 && IsPlausibleText(text.Text),
        EntityStatEntry entity => entity.IsWithinLimits(),
        UnlockPassEntry pass => pass.Timing is >= 0 and <= MaxTiming &&
            pass.NotificationTiming is >= -1 and <= MaxNotificationTiming &&
            pass.Earned != null && pass.Earned.Count <= MaxEarned && AllPlausible(pass.Earned),
        SquadEntry squad => squad.Outcome is SquadOutcome.Won or SquadOutcome.Lost && IsPlausibleId(squad.Squad) &&
            squad.Difficulty is >= 0 and <= MaxDifficulty && float.IsFinite(squad.RunSeconds) && squad.RunSeconds >= 0 &&
            IsPlausibleScorer(squad.BestScorer, squad.BestGoals),
        ComicsEntry => true,
        RunEndingEntry => true,
        ProfileMarkEntry mark => mark.Dialogue != null && mark.Mark switch
        {
            ProfileMark.NurseOffered => mark.Procedure is >= 0 and <= MaxProcedure && mark.Dialogue.Length == 0,
            ProfileMark.DialogueSeen => mark.Procedure == 0 && IsPlausibleId(mark.Dialogue),
            _ => false,
        },
        RunHistoryRecord history => history.Entry != null && history.Entry.IsWithinLimits(),
        QuestEntry quest => quest.Step is >= QuestStep.MatchEnded and <= QuestStep.RunFailed && quest.Act is >= 0 and <= MaxAct,
        _ => false,
    };

    /// <summary>
    /// A squad record's best scorer: none (empty, 0 goals), or a name of at most <see cref="MaxScorerBytes"/> UTF-8 bytes with
    /// no control characters and 1 to <see cref="MaxRunGoals"/> goals.
    /// </summary>
    public static bool IsPlausibleScorer(string? name, int goals) =>
        name != null && (name.Length == 0 ? goals == 0
            : goals is >= 1 and <= MaxRunGoals && name.Length <= MaxScorerBytes && System.Text.Encoding.UTF8.GetByteCount(name) <= MaxScorerBytes &&
              IsPlausibleText(name));

    /// <summary>
    /// What <paramref name="entry"/> adds to its stat: 1 for a plain increment, its value for the others that add (negative
    /// when the game takes some back), 0 for a record and a reset (they don't add).
    /// </summary>
    public static double CreditOf(StatEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (StatOps.IsRecord(entry.Op) || StatOps.IsReset(entry.Op))
        {
            return 0;
        }

        return !StatOps.HasValue(entry.Op) ? 1 : entry.Value.Kind == StatValueKind.Real ? entry.Value.RealValue : entry.Value.WholeValue;
    }

    /// <summary>
    /// True when <paramref name="entries"/> together add at most <see cref="MaxStatCreditPerBatch"/> to any one stat, hold at
    /// most one <see cref="RunEndingEntry"/> (a run ends once) and at most <see cref="MaxMarksPerBatch"/> profile marks (each
    /// entry is checked on its own by <see cref="IsValid"/>).
    /// </summary>
    public static bool IsWithinBatchLimits(IReadOnlyList<ProgressEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        Dictionary<int, double>? credit = null;
        int endings = 0, marks = 0;
        foreach (ProgressEntry entry in entries)
        {
            endings += entry is RunEndingEntry ? 1 : 0;
            marks += entry is ProfileMarkEntry ? 1 : 0;
            if (endings > 1 || marks > MaxMarksPerBatch)
            {
                return false;
            }

            if (entry is not StatEntry stat)
            {
                continue;
            }

            credit ??= new Dictionary<int, double>();
            double total = credit.GetValueOrDefault(stat.Stat) + CreditOf(stat);
            if (total > MaxStatCreditPerBatch)
            {
                return false;
            }

            credit[stat.Stat] = total;
        }

        return true;
    }

    /// <summary>
    /// The operations the game only ever does to one stat: a reset only to the current win streak, a record from another
    /// stat only to the largest win streak from the current one. Anything else would let a host wipe or set a friend's stat.
    /// </summary>
    private static bool IsAllowed(StatEntry stat) =>
        StatOps.IsReset(stat.Op) ? ProgressGameStats.IsResettable(stat.Stat)
        : !StatOps.IsFromStat(stat.Op) || ProgressGameStats.IsRecordFrom(stat.Stat, stat.Value.WholeValue);

    private static bool IsValidValue(StatOp op, StatValue value)
    {
        if (!StatOps.HasValue(op))
        {
            return value.Kind == StatValueKind.None;
        }

        if (StatOps.IsFromStat(op))
        {
            return value.Kind == StatValueKind.Whole && value.WholeValue is >= 0 and <= MaxStat;
        }

        // A record is a time, a count or a size, never below zero; an adding operation may take a little back.
        int least = StatOps.IsRecord(op) ? 0 : -MaxStatDecrement;
        return value.Kind switch
        {
            StatValueKind.Whole => value.WholeValue >= least && value.WholeValue <= MaxStatValue,
            StatValueKind.Real => float.IsFinite(value.RealValue) && value.RealValue >= least && value.RealValue <= MaxStatValue,
            _ => false,
        };
    }

    private static bool AllPlausible(IReadOnlyList<string> ids)
    {
        foreach (string id in ids)
        {
            if (!IsPlausibleId(id))
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>
/// A numbered batch of the host's progress: everything its game credited between two safe points (a stats save, the
/// end of a match, the end of a run), in order. <see cref="Checkpoint"/> and <see cref="Run"/> say which run it belongs
/// to, so a friend's game applies the run-bound parts (unlock checks, the squad record) only while it holds that run.
/// </summary>
public sealed class ProgressBatch
{
    /// <summary>
    /// A batch. Throws for an entry outside <see cref="ProgressLimits"/>, entries beyond a batch's limits
    /// (<see cref="ProgressLimits.IsWithinBatchLimits"/>), or an implausible run id.
    /// </summary>
    public ProgressBatch(uint sequence, uint checkpoint, string run, IReadOnlyList<ProgressEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(entries);
        if (sequence == 0)
        {
            throw new ArgumentException("Batches are numbered from 1.", nameof(sequence));
        }

        if (run.Length > 0 && !ProgressLimits.IsPlausibleId(run))
        {
            throw new ArgumentException("Not a run id.", nameof(run));
        }

        if (entries.Count > ProgressBatchMessage.MaxEntries)
        {
            throw new ArgumentException($"At most {ProgressBatchMessage.MaxEntries} entries.", nameof(entries));
        }

        foreach (ProgressEntry entry in entries)
        {
            if (!ProgressLimits.IsValid(entry))
            {
                throw new ArgumentException($"Entry out of limits: {entry}.", nameof(entries));
            }
        }

        if (!ProgressLimits.IsWithinBatchLimits(entries))
        {
            throw new ArgumentException($"More than {ProgressLimits.MaxStatCreditPerBatch} credit to one stat, more than one run ending, or more than " +
                $"{ProgressLimits.MaxMarksPerBatch} profile marks.", nameof(entries));
        }

        Sequence = sequence;
        Checkpoint = checkpoint;
        Run = run;
        Entries = entries;
    }

    /// <summary>The host's number for this batch in its hosting session, from 1.</summary>
    public uint Sequence { get; }

    /// <summary>The host's newest run checkpoint when the batch was made (0: none).</summary>
    public uint Checkpoint { get; }

    /// <summary>The host's run id when its entries were credited (empty: none).</summary>
    public string Run { get; }

    /// <summary>The entries, in the order the host's game credited them.</summary>
    public IReadOnlyList<ProgressEntry> Entries { get; }

    /// <summary>How many entries of each kind, for logs ("23 stat operations, 2 unlock checks, squad win").</summary>
    public string Describe()
    {
        int stats = 0, passes = 0, earned = 0, comics = 0, histories = 0, quests = 0, texts = 0, entities = 0, endings = 0, marks = 0;
        var parts = new List<string>();
        var squads = new List<string>();
        foreach (ProgressEntry entry in Entries)
        {
            switch (entry)
            {
                case StatEntry:
                    stats++;
                    break;
                case StatTextEntry:
                    texts++;
                    break;
                case EntityStatEntry:
                    entities++;
                    break;
                case UnlockPassEntry pass:
                    passes++;
                    earned += pass.Earned.Count;
                    break;
                case SquadEntry squad:
                    squads.Add($"squad {(squad.Outcome == SquadOutcome.Won ? "win" : "loss")} {Diagnostics.HostText.KeyForLog(squad.Squad)} on difficulty {squad.Difficulty}");
                    break;
                case ComicsEntry:
                    comics++;
                    break;
                case RunHistoryRecord:
                    histories++;
                    break;
                case QuestEntry:
                    quests++;
                    break;
                case RunEndingEntry:
                    endings++;
                    break;
                case ProfileMarkEntry:
                    marks++;
                    break;
            }
        }

        parts.Add($"{stats} stat operation{(stats == 1 ? string.Empty : "s")}");
        parts.Add($"{passes} unlock check{(passes == 1 ? string.Empty : "s")}{(earned > 0 ? $" ({earned} live-match unlock{(earned == 1 ? string.Empty : "s")} earned)" : string.Empty)}");
        if (texts > 0)
        {
            parts.Add($"{texts} record holder{(texts == 1 ? string.Empty : "s")}");
        }

        if (entities > 0)
        {
            parts.Add($"{entities} entity stat{(entities == 1 ? string.Empty : "s")}");
        }

        parts.AddRange(squads);
        if (comics > 0)
        {
            parts.Add("comics seen");
        }

        if (histories > 0)
        {
            parts.Add("the run history entry");
        }

        if (quests > 0)
        {
            parts.Add($"{quests} bench quest step{(quests == 1 ? string.Empty : "s")}");
        }

        if (endings > 0)
        {
            parts.Add("the run's end for the benchwarmers");
        }

        if (marks > 0)
        {
            parts.Add($"{marks} profile mark{(marks == 1 ? string.Empty : "s")}");
        }

        return string.Join(", ", parts);
    }
}

/// <summary>A progress message as read: the host's session, the first batch meant for this friend, and the batch.</summary>
/// <param name="Session">The host's hosting session (random per session), so batches from another session aren't confused.</param>
/// <param name="EntitledFrom">
/// The first batch of the session the host keeps for this friend: earlier ones were made before they joined (or dropped
/// for space), so a friend never waits for them.
/// </param>
/// <param name="Batch">The batch.</param>
public sealed record ProgressMessage(ulong Session, uint EntitledFrom, ProgressBatch Batch);

/// <summary>
/// Host to friend (channel 1, <c>MessageType.ProgressEvent</c>): one <see cref="ProgressBatch"/>. The batch body is
/// encoded once (<see cref="EncodeBody"/>) and sent to each friend behind a short per-friend prefix. Layout:
/// <code>
/// u8 format (2) · u64 session · u32 entitled-from · u32 sequence · u32 checkpoint · str run · varuint count · entries
/// entry: u8 kind, then
///   stat (1):        varuint stat · u8 op · [op with a value: u8 value kind · varint whole | f32 real]
///   unlock check (2): u8 timing · varint notification timing · bool queue · u8 count · str id × count
///   squad (3):       u8 outcome · str squad · varuint difficulty · f32 run seconds · str best scorer · varuint its goals
///   comics (4):      nothing
///   run history (5): varuint raw length · varuint deflated length · the entry's fields (RunHistoryData), deflated
///   quest (6):       u8 step · varuint act
///   record holder (7): varuint stat · varuint record stat · bool campaign · str text
///   entity stat (8): see <see cref="EntityStatEntry"/>
///   run ending (9):  nothing
///   profile mark (10): u8 mark · [Nurse offered: u8 procedure | dialogue seen: str id]
/// </code>
/// Format 2 (protocol 6) added the squad record's best scorer and kinds 9 and 10; another format reads as false. Strings are <see cref="BitWriter.WriteShortString"/> (length-prefixed UTF-8). Everything is bounded and checked on the
/// way in; a bad message reads as false, never throws.
/// </summary>
public static class ProgressBatchMessage
{
    /// <summary>The layout's version (the first byte): 2 since the squad record carries the run's best scorer.</summary>
    public const byte Format = 2;

    /// <summary>Most entries in one batch (the host starts a new batch beyond it).</summary>
    public const int MaxEntries = 4096;

    /// <summary>Largest message, in bytes: within Steam's 512 KB reliable messages, with room for the header.</summary>
    public const int MaxMessageBytes = 500 * 1024;

    private const byte StatKind = 1;
    private const byte PassKind = 2;
    private const byte SquadKind = 3;
    private const byte ComicsKind = 4;
    private const byte HistoryKind = 5;
    private const byte QuestKind = 6;
    private const byte TextKind = 7;
    private const byte EntityKind = 8;
    private const byte RunEndingKind = 9;
    private const byte MarkKind = 10;

    /// <summary>
    /// The batch's body (sequence onwards), encoded once for every friend. Null when it wouldn't fit in a message (the
    /// host logs it and splits its batches so this doesn't happen).
    /// </summary>
    public static byte[]? EncodeBody(ProgressBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        var writer = new BitWriter(256 + (batch.Entries.Count * 6));
        writer.WriteUInt32(batch.Sequence);
        writer.WriteUInt32(batch.Checkpoint);
        writer.WriteShortString(batch.Run, ProgressLimits.MaxIdBytes);
        writer.WriteVarUInt((ulong)batch.Entries.Count);
        foreach (ProgressEntry entry in batch.Entries)
        {
            WriteEntry(writer, entry);
            if (writer.ByteLength > MaxMessageBytes - 32)
            {
                return null;
            }
        }

        return writer.ToArray();
    }

    /// <summary>Writes the payload (after the header) for one friend: the prefix, then <paramref name="body"/> from <see cref="EncodeBody"/>.</summary>
    public static void Write(BitWriter writer, ulong session, uint entitledFrom, ReadOnlySpan<byte> body)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteByte(Format);
        writer.WriteUInt64(session);
        writer.WriteUInt32(entitledFrom);
        writer.WriteBytes(body);
    }

    /// <summary>Reads the payload. False for anything malformed, out of limits or of another format. Never throws.</summary>
    public static bool TryRead(ref BitReader reader, out ProgressMessage? message)
    {
        message = null;
        byte format = reader.ReadByte();
        ulong session = reader.ReadUInt64();
        uint entitledFrom = reader.ReadUInt32();
        if (reader.IsOverrun || format != Format || !TryReadBody(ref reader, out ProgressBatch? batch))
        {
            return false;
        }

        message = new ProgressMessage(session, entitledFrom, batch!);
        return true;
    }

    /// <summary>Reads a body written by <see cref="EncodeBody"/>. False for anything malformed or out of limits. Never throws.</summary>
    public static bool TryReadBody(ref BitReader reader, out ProgressBatch? batch)
    {
        batch = null;
        uint sequence = reader.ReadUInt32();
        uint checkpoint = reader.ReadUInt32();
        string run = reader.ReadShortString(ProgressLimits.MaxIdBytes);
        ulong count = reader.ReadVarUInt();
        if (reader.IsOverrun || sequence == 0 || (run.Length > 0 && !ProgressLimits.IsPlausibleId(run)) || count > MaxEntries ||
            count * 8 > (ulong)reader.RemainingBits)
        {
            return false;
        }

        // A whole message inflates to at most one run history entry's worth: deflate turns a run of zeros a thousand times
        // smaller, so without this a few hundred KB could make this PC inflate hundreds of MB.
        int inflateBudget = ProgressLimits.MaxRunHistoryBytes;
        var entries = new List<ProgressEntry>((int)count);
        for (ulong i = 0; i < count; i++)
        {
            if (!TryReadEntry(ref reader, ref inflateBudget, out ProgressEntry? entry) || !ProgressLimits.IsValid(entry))
            {
                return false;
            }

            entries.Add(entry!);
        }

        if (reader.IsOverrun || !ProgressLimits.IsWithinBatchLimits(entries))
        {
            return false;
        }

        batch = new ProgressBatch(sequence, checkpoint, run, entries);
        return true;
    }

    private static void WriteEntry(BitWriter writer, ProgressEntry entry)
    {
        switch (entry)
        {
            case StatEntry stat:
                writer.WriteByte(StatKind);
                writer.WriteVarUInt((ulong)stat.Stat);
                writer.WriteByte((byte)stat.Op);
                if (StatOps.HasValue(stat.Op))
                {
                    writer.WriteByte((byte)stat.Value.Kind);
                    if (stat.Value.Kind == StatValueKind.Real)
                    {
                        writer.WriteSingle(stat.Value.RealValue);
                    }
                    else
                    {
                        writer.WriteVarInt(stat.Value.WholeValue);
                    }
                }

                break;
            case UnlockPassEntry pass:
                writer.WriteByte(PassKind);
                writer.WriteByte((byte)pass.Timing);
                writer.WriteVarInt(pass.NotificationTiming);
                writer.WriteBool(pass.Queue);
                writer.WriteByte((byte)pass.Earned.Count);
                foreach (string id in pass.Earned)
                {
                    writer.WriteShortString(id, ProgressLimits.MaxIdBytes);
                }

                break;
            case SquadEntry squad:
                writer.WriteByte(SquadKind);
                writer.WriteByte((byte)squad.Outcome);
                writer.WriteShortString(squad.Squad, ProgressLimits.MaxIdBytes);
                writer.WriteVarUInt((ulong)squad.Difficulty);
                writer.WriteSingle(squad.RunSeconds);
                writer.WriteShortString(squad.BestScorer, ProgressLimits.MaxScorerBytes);
                writer.WriteVarUInt((ulong)squad.BestGoals);
                break;
            case ComicsEntry:
                writer.WriteByte(ComicsKind);
                break;
            case RunHistoryRecord history:
                var fields = new BitWriter(1024);
                history.Entry.Write(fields);
                byte[] deflated = Compression.Deflate(fields.AsSpan());
                writer.WriteByte(HistoryKind);
                writer.WriteVarUInt((ulong)fields.ByteLength);
                writer.WriteVarUInt((ulong)deflated.Length);
                writer.WriteBytes(deflated);
                break;
            case QuestEntry quest:
                writer.WriteByte(QuestKind);
                writer.WriteByte((byte)quest.Step);
                writer.WriteVarUInt((ulong)quest.Act);
                break;
            case StatTextEntry text:
                writer.WriteByte(TextKind);
                writer.WriteVarUInt((ulong)text.Stat);
                writer.WriteVarUInt((ulong)text.RecordStat);
                writer.WriteBool(text.Campaign);
                writer.WriteShortString(text.Text, ProgressLimits.MaxTextBytes);
                break;
            case EntityStatEntry entity:
                writer.WriteByte(EntityKind);
                entity.Write(writer);
                break;
            case RunEndingEntry:
                writer.WriteByte(RunEndingKind);
                break;
            case ProfileMarkEntry mark:
                writer.WriteByte(MarkKind);
                writer.WriteByte((byte)mark.Mark);
                if (mark.Mark == ProfileMark.NurseOffered)
                {
                    writer.WriteByte((byte)mark.Procedure);
                }
                else
                {
                    writer.WriteShortString(mark.Dialogue, ProgressLimits.MaxIdBytes);
                }

                break;
            default:
                throw new ArgumentException($"Unknown entry {entry.GetType().Name}.", nameof(entry));
        }
    }

    /// <summary>Reads one entry. <paramref name="inflateBudget"/>: the bytes this message may still inflate (a run history entry spends it).</summary>
    private static bool TryReadEntry(ref BitReader reader, ref int inflateBudget, out ProgressEntry? entry)
    {
        entry = null;
        byte kind = reader.ReadByte();
        switch (kind)
        {
            case StatKind:
                ulong stat = reader.ReadVarUInt();
                var op = (StatOp)reader.ReadByte();
                if (reader.IsOverrun || stat > ProgressLimits.MaxStat || !StatOps.IsKnown(op))
                {
                    return false;
                }

                StatValue value = StatValue.None;
                if (StatOps.HasValue(op))
                {
                    var valueKind = (StatValueKind)reader.ReadByte();
                    if (valueKind == StatValueKind.Whole)
                    {
                        long whole = reader.ReadVarInt();
                        if (whole is < int.MinValue or > int.MaxValue)
                        {
                            return false;
                        }

                        value = StatValue.Whole((int)whole);
                    }
                    else if (valueKind == StatValueKind.Real)
                    {
                        value = StatValue.Real(reader.ReadSingle());
                    }
                    else
                    {
                        return false;
                    }
                }

                entry = new StatEntry((int)stat, op, value);
                return !reader.IsOverrun;
            case PassKind:
                int timing = reader.ReadByte();
                long notification = reader.ReadVarInt();
                bool queue = reader.ReadBool();
                int earnedCount = reader.ReadByte();
                if (reader.IsOverrun || earnedCount > ProgressLimits.MaxEarned || notification is < -1 or > ProgressLimits.MaxNotificationTiming)
                {
                    return false;
                }

                var earned = new List<string>(earnedCount);
                for (int i = 0; i < earnedCount; i++)
                {
                    earned.Add(reader.ReadShortString(ProgressLimits.MaxIdBytes));
                }

                entry = new UnlockPassEntry(timing, (int)notification, queue, earned);
                return !reader.IsOverrun;
            case SquadKind:
                var outcome = (SquadOutcome)reader.ReadByte();
                string squad = reader.ReadShortString(ProgressLimits.MaxIdBytes);
                ulong difficulty = reader.ReadVarUInt();
                float seconds = reader.ReadSingle();
                string scorer = reader.ReadShortString(ProgressLimits.MaxScorerBytes);
                ulong goals = reader.ReadVarUInt();
                if (reader.IsOverrun || difficulty > ProgressLimits.MaxDifficulty || goals > ProgressLimits.MaxRunGoals)
                {
                    return false;
                }

                entry = new SquadEntry(outcome, squad, (int)difficulty, seconds, scorer, (int)goals);
                return true;
            case ComicsKind:
                entry = new ComicsEntry();
                return !reader.IsOverrun;
            case HistoryKind:
                ulong rawLength = reader.ReadVarUInt();
                ulong deflatedLength = reader.ReadVarUInt();
                if (reader.IsOverrun || rawLength is 0 or > ProgressLimits.MaxRunHistoryBytes || rawLength > (ulong)inflateBudget ||
                    deflatedLength > MaxMessageBytes || (long)deflatedLength * 8 > reader.RemainingBits)
                {
                    return false;
                }

                inflateBudget -= (int)rawLength;
                byte[] deflated = new byte[(int)deflatedLength];
                reader.ReadBytes(deflated);
                if (reader.IsOverrun || !Compression.TryInflate(deflated, (int)rawLength, out byte[] raw) || raw.Length != (int)rawLength)
                {
                    return false;
                }

                // The entry must be all there is: bytes it doesn't read (zeros padding a small entry out) are refused.
                var fieldReader = new BitReader(raw);
                if (!RunHistoryData.TryRead(ref fieldReader, out RunHistoryData? history) || fieldReader.RemainingBits >= 8)
                {
                    return false;
                }

                entry = new RunHistoryRecord(history!);
                return true;
            case QuestKind:
                var step = (QuestStep)reader.ReadByte();
                ulong act = reader.ReadVarUInt();
                if (reader.IsOverrun || act > ProgressLimits.MaxAct)
                {
                    return false;
                }

                entry = new QuestEntry(step, (int)act);
                return true;
            case TextKind:
                ulong textStat = reader.ReadVarUInt();
                ulong recordStat = reader.ReadVarUInt();
                bool campaign = reader.ReadBool();
                string text = reader.ReadShortString(ProgressLimits.MaxTextBytes);
                if (reader.IsOverrun || textStat > ProgressLimits.MaxStat || recordStat > ProgressLimits.MaxStat)
                {
                    return false;
                }

                entry = new StatTextEntry((int)textStat, (int)recordStat, text, campaign);
                return true;
            case EntityKind:
                if (!EntityStatEntry.TryRead(ref reader, out EntityStatEntry? entity))
                {
                    return false;
                }

                entry = entity;
                return true;
            case RunEndingKind:
                entry = new RunEndingEntry();
                return !reader.IsOverrun;
            case MarkKind:
                var mark = (ProfileMark)reader.ReadByte();
                if (mark == ProfileMark.NurseOffered)
                {
                    entry = ProfileMarkEntry.NurseOffered(reader.ReadByte());
                }
                else if (mark == ProfileMark.DialogueSeen)
                {
                    entry = ProfileMarkEntry.DialogueSeen(reader.ReadShortString(ProgressLimits.MaxIdBytes));
                }
                else
                {
                    return false;
                }

                return !reader.IsOverrun;
            default:
                return false;
        }
    }
}
