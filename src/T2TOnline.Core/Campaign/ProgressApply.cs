using System;
using System.Collections.Generic;
using System.Linq;

namespace T2TOnline.Core.Campaign;

/// <summary>Why shared progress can't apply on a friend's PC right now (D47's safe points).</summary>
public enum ApplyBlock
{
    /// <summary>Nothing: it can.</summary>
    None,

    /// <summary>This PC plays the host's match (client mode).</summary>
    PlayingHostMatch,

    /// <summary>The host's match loads, plays or is held on its last frame here.</summary>
    HostMatchHere,

    /// <summary>The host's run is loading here.</summary>
    HostRunLoading,

    /// <summary>This game's profile and stats haven't loaded yet (a save now would write defaults over them).</summary>
    NotLoaded,

    /// <summary>This PC's saves were restored and wait for the game to restart (nothing would be saved, yet marked applied).</summary>
    RestartPending,
}

/// <summary>Where a batch's run-bound parts (unlock checks, the squad record, bench quest steps, comics) apply.</summary>
public enum RunContext
{
    /// <summary>Nowhere: this PC has no copy of the batch's run, so they're skipped (logged).</summary>
    None,

    /// <summary>On the run this PC's game holds now (it's the batch's run).</summary>
    Held,

    /// <summary>
    /// On this PC's copy of the batch's run, kept from when its game held it (the host moved on to a new run, or the
    /// session ended, before the batch could apply): put in place just for the batch, then the run held now goes back.
    /// </summary>
    Kept,
}

/// <summary>What the friend's game is like right now, for <see cref="ProgressApplyPolicy.Decide"/>.</summary>
/// <param name="Sharing">ShareProgress is on on this PC.</param>
/// <param name="Block">Why nothing may apply now (<see cref="ApplyBlock.None"/>: it may).</param>
/// <param name="Ending">The session is ending: what waits applies now if it can at all.</param>
/// <param name="HeldRun">The run this PC's game holds (empty: none, or its own).</param>
/// <param name="ViewRun">The run the host's newest view is on (empty: none).</param>
/// <param name="KeptRuns">The shared runs this PC keeps a copy of (<see cref="RunContext.Kept"/>).</param>
/// <param name="Copies">
/// Copies of the host's runs this PC can ask for (a run it held before its game restarted), or null when it
/// can't ask (then such a batch's run-bound parts are skipped, as before).
/// </param>
/// <param name="LinkUp">
/// The link to the host is up (a session joined and the host heard from lately). A wait for a copy of the host's run counts
/// only the time it is; true when it isn't known.
/// </param>
/// <param name="HostRunOver">
/// The host's run is over and its run-end screens are up while its last match's scene stays loaded here (client mode still
/// on): that match no longer plays, so it no longer holds batches back (<see cref="ProgressApplyPolicy.LiftedAtRunEnd"/>), and
/// this player's new-unlocks screen shows what the run's last match unlocked here.
/// </param>
public readonly record struct ApplyFacts(bool Sharing, ApplyBlock Block, bool Ending, string HeldRun, string ViewRun, IReadOnlyCollection<string> KeptRuns,
    IRunCopies? Copies = null, bool LinkUp = true, bool HostRunOver = false);

/// <summary>Whether a batch applies now or waits.</summary>
public enum BatchVerdict
{
    /// <summary>It waits (nothing applied, nothing marked).</summary>
    Wait,

    /// <summary>It applies now.</summary>
    Apply,
}

/// <summary>What <see cref="ProgressApplyPolicy.Decide"/> made of a batch.</summary>
/// <param name="Verdict">Apply or wait.</param>
/// <param name="Why">Why it waits (empty when it applies).</param>
/// <param name="Context">Where its run-bound parts apply.</param>
/// <param name="Skipped">When they're skipped, why (for the batch's line; empty otherwise).</param>
/// <param name="CopyFor">When it waits for a copy of the host's run, that run (it's asked for); empty otherwise.</param>
public readonly record struct BatchDecision(BatchVerdict Verdict, string Why, RunContext Context, string Skipped, string CopyFor = "")
{
    /// <summary>The batch waits for <paramref name="why"/>.</summary>
    public static BatchDecision Wait(string why) => new(BatchVerdict.Wait, why, RunContext.None, string.Empty);

    /// <summary>The batch waits for a copy of the host's run <paramref name="run"/> (<paramref name="why"/>).</summary>
    public static BatchDecision WaitForCopy(string why, string run) => new(BatchVerdict.Wait, why, RunContext.None, string.Empty, run);

    /// <summary>The batch applies, its run-bound parts in <paramref name="context"/>.</summary>
    public static BatchDecision Apply(RunContext context, string skipped = "") => new(BatchVerdict.Apply, string.Empty, context, skipped);
}

/// <summary>What this PC's game has to apply a batch with, for <see cref="ProgressApplyPolicy.For"/>.</summary>
/// <param name="Stats">Its stats are there.</param>
/// <param name="Unlocks">Its unlock system is there.</param>
/// <param name="Quests">Its bench quest system is there.</param>
/// <param name="Checks">Its game tracks stats right now, as the host's did (the unlock checks need it).</param>
public readonly record struct ApplyAbilities(bool Stats, bool Unlocks, bool Quests, bool Checks);

/// <summary>What happens to one entry of a batch.</summary>
public enum EntryVerdict
{
    /// <summary>It applies.</summary>
    Apply,

    /// <summary>Skipped: a run-bound part with no run to apply it on.</summary>
    SkipNoRun,

    /// <summary>Skipped: this game's stats aren't there.</summary>
    SkipNoStats,

    /// <summary>Skipped: an unlock check while this game still doesn't track stats (given up on after the wait).</summary>
    SkipChecksOff,

    /// <summary>Skipped: this game's unlock system isn't there.</summary>
    SkipNoUnlocks,

    /// <summary>Skipped: this game's bench quest system isn't there.</summary>
    SkipNoQuests,
}

/// <summary>What happens to one stat operation on this profile.</summary>
public enum StatVerdict
{
    /// <summary>It applies.</summary>
    Apply,

    /// <summary>Refused: the whole number would wrap past its largest (the game adds without a check).</summary>
    Overflow,

    /// <summary>Refused: what the host's game takes back would take this profile's value below zero.</summary>
    BelowZero,
}

/// <summary>
/// A friend's decisions about the host's batches (D42, D47), apart from the game: whether a batch
/// applies now or waits and why, where its run-bound parts apply (the run held now, this PC's kept copy of the batch's run,
/// or nowhere), which entries apply, whether a stat operation may, a record holder's name, a run history entry, and which
/// derived stats to work out again. The plugin gathers the facts and makes the game's calls.
/// <list type="bullet">
/// <item>Nothing applies with ShareProgress off, nor at an unsafe moment (<see cref="ApplyBlock"/>); as a session ends the
/// host's match and run loads no longer count (its batches would otherwise be lost with the session), and once the host's
/// run is over and its run-end screens are up the host's last match no longer does either
/// (<see cref="ApplyFacts.HostRunOver"/>).</item>
/// <item>A batch's run-bound parts apply on the run held now when it's the batch's, else on this PC's kept copy of that run
/// (so a run's end applies whatever came first: another view loading the next run's checkpoint, the session's end, a
/// rejoin), else, for the run the host's view is on and this PC is about to load, after waiting up to
/// <see cref="RunWaitMicros"/>, else, for a run this PC held before its game restarted, on a copy of it from the host after
/// waiting up to <see cref="CopyWaitMicros"/>; otherwise they're skipped (logged).</item>
/// <item>The unlock checks wait up to <see cref="GateWaitMicros"/> for this game to track stats (not at a session's end),
/// then are skipped.</item>
/// </list>
/// Game-independent. Not thread-safe (one game thread).
/// </summary>
public sealed class ProgressApplyPolicy
{
    /// <summary>Longest a batch for the host's current run waits for this PC to load that run.</summary>
    public const long RunWaitMicros = 20_000_000;

    /// <summary>Longest a batch's unlock checks wait for this game to track stats.</summary>
    public const long GateWaitMicros = 60_000_000;

    /// <summary>Longest a batch waits for a copy of a host's run this PC held before its game restarted.</summary>
    public const long CopyWaitMicros = 30_000_000;

    /// <summary>Most batches applied in one frame (they're saved together).</summary>
    public const int MaxPerFrame = 8;

    /// <summary>Most batches applied as a session ends.</summary>
    public const int MaxAtEnd = 32;

    /// <summary>The run-bound parts, as a batch's line names them when they're skipped.</summary>
    public const string RunBoundParts = "unlock checks, squad record, quest steps and bench snapshots";

    private long _runWaitSince = -1;
    private long _gateSince = -1;
    private long _copyWaitSince = -1;
    private long _copyLastMicros = -1;
    private int _copyRestarts;

    /// <summary>
    /// True for an entry that needs the batch's run (the unlock checks, the squad record, the bench quest steps, the comics, the
    /// run's end for the benchwarmers).
    /// </summary>
    public static bool IsRunBound(ProgressEntry entry) => entry is UnlockPassEntry or SquadEntry or QuestEntry or ComicsEntry or RunEndingEntry;

    /// <summary>True for a batch whose run-bound parts are worth holding a run for (the comics only say which strips wait, so not them).</summary>
    public static bool HoldsRun(ProgressBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        return batch.Run.Length > 0 && batch.Entries.Any(e => e is UnlockPassEntry or SquadEntry or QuestEntry or RunEndingEntry);
    }

    /// <summary>Why <paramref name="block"/> stops a batch, for the log and the status box.</summary>
    public static string Describe(ApplyBlock block) => block switch
    {
        ApplyBlock.PlayingHostMatch => "this PC is playing the host's match",
        ApplyBlock.HostMatchHere => "the host's match is loaded here",
        ApplyBlock.HostRunLoading => "the host's run is loading here",
        ApplyBlock.NotLoaded => "this game's profile and stats aren't loaded yet",
        ApplyBlock.RestartPending => "this PC's saves were restored and wait for the game to restart",
        _ => string.Empty,
    };

    /// <summary>
    /// True when <paramref name="block"/> still stops a batch as a session ends. The host's match and its run loads end with
    /// the session (the batches that waited for them would be lost); a save before this game's own saves have loaded, or
    /// while a restore waits for a restart, never may.
    /// </summary>
    public static bool HoldsAtEnd(ApplyBlock block) => block is ApplyBlock.NotLoaded or ApplyBlock.RestartPending;

    /// <summary>
    /// True when <paramref name="block"/> no longer stops a batch because the host's run is over and its run-end screens are up
    /// here (<paramref name="hostRunOver"/>): the run's last match, whose scene those screens keep loaded, plays no more. Its
    /// scene staying loaded is the only thing that held the run's last progress back until the host left for the title, after
    /// its new-unlocks screen.
    /// </summary>
    public static bool LiftedAtRunEnd(ApplyBlock block, bool hostRunOver) =>
        hostRunOver && block is ApplyBlock.PlayingHostMatch or ApplyBlock.HostMatchHere;

    /// <summary>
    /// True when <paramref name="block"/> holds every batch back now: there is one, the session isn't ending or it still holds
    /// then (<see cref="HoldsAtEnd"/>), and the host's run being over doesn't lift it (<see cref="LiftedAtRunEnd"/>). The one
    /// rule <see cref="Decide"/> and <see cref="ProgressFactsNeed"/> both go by.
    /// </summary>
    public static bool BlockHolds(ApplyBlock block, bool ending, bool hostRunOver) =>
        block != ApplyBlock.None && (!ending || HoldsAtEnd(block)) && !LiftedAtRunEnd(block, hostRunOver);

    /// <summary>
    /// Why nothing may apply now, from the friend's game as it is: client mode on (<paramref name="clientMode"/>), the host's
    /// match loading or held here (<paramref name="hostMatchHere"/>), and the rest asked only when they can matter (game
    /// calls). The host's match is said first while it holds batches back; when it doesn't (<paramref name="ending"/>, or
    /// <paramref name="hostRunOver"/>: <see cref="Decide"/> lets it pass then), the host's run loading and this game's own
    /// saves are asked first, so neither lets a batch through before this game's profile and stats have loaded, or while a
    /// restored backup waits for a restart.
    /// </summary>
    public static ApplyBlock BlockOf(bool clientMode, bool hostMatchHere, bool ending, bool hostRunOver, Func<bool> runLoading, Func<bool> notLoaded,
        Func<bool> restartPending)
    {
        ArgumentNullException.ThrowIfNull(runLoading);
        ArgumentNullException.ThrowIfNull(notLoaded);
        ArgumentNullException.ThrowIfNull(restartPending);
        ApplyBlock match = clientMode ? ApplyBlock.PlayingHostMatch : hostMatchHere ? ApplyBlock.HostMatchHere : ApplyBlock.None;
        return match != ApplyBlock.None && !ending && !hostRunOver ? match
            : runLoading() ? ApplyBlock.HostRunLoading
            : notLoaded() ? ApplyBlock.NotLoaded
            : restartPending() ? ApplyBlock.RestartPending
            : match;
    }

    /// <summary>Whether <paramref name="batch"/> applies now and where its run-bound parts go (see the class).</summary>
    public BatchDecision Decide(ProgressBatch batch, in ApplyFacts facts, long nowMicros)
    {
        ArgumentNullException.ThrowIfNull(batch);
        if (!facts.Sharing)
        {
            return BatchDecision.Wait("ShareProgress is off on this PC");
        }

        if (BlockHolds(facts.Block, facts.Ending, facts.HostRunOver))
        {
            return BatchDecision.Wait(Describe(facts.Block));
        }

        if (!batch.Entries.Any(IsRunBound))
        {
            _runWaitSince = -1;
            return BatchDecision.Apply(RunContext.None);
        }

        if (batch.Run.Length == 0)
        {
            _runWaitSince = -1;
            return BatchDecision.Apply(RunContext.None, $"{RunBoundParts} skipped (the host wasn't in a run)");
        }

        if (batch.Run == facts.HeldRun)
        {
            _runWaitSince = -1;
            return BatchDecision.Apply(RunContext.Held);
        }

        bool kept = facts.KeptRuns != null && facts.KeptRuns.Contains(batch.Run);
        if (!kept && !facts.Ending && facts.ViewRun == batch.Run && HoldsRun(batch))
        {
            // The host is on that run and its checkpoint comes on another channel: this PC is about to load it.
            if (_runWaitSince < 0)
            {
                _runWaitSince = nowMicros;
            }

            if (nowMicros - _runWaitSince < RunWaitMicros)
            {
                return BatchDecision.Wait("the host's run it's for isn't loaded here yet");
            }

            _runWaitSince = -1;
            return BatchDecision.Apply(RunContext.None,
                $"{RunBoundParts} skipped: the host's run they're for didn't load here within {RunWaitMicros / 1_000_000} s");
        }

        _runWaitSince = -1;
        if (!kept && HoldsRun(batch) && facts.Copies is { } copies && copies.HeldBefore(batch.Run))
        {
            return DecideCopy(batch, facts, copies, nowMicros);
        }

        _copyWaitSince = -1;
        _copyLastMicros = -1;
        return kept ? BatchDecision.Apply(RunContext.Kept)
            : BatchDecision.Apply(RunContext.None, $"{RunBoundParts} skipped: this PC doesn't hold the host's run (it holds " +
                $"{(facts.HeldRun.Length == 0 ? "none" : "another")}) and has no copy of it (its game never held it, or restarted since)");
    }

    /// <summary>
    /// A batch whose run this PC held before its game restarted, and keeps no copy of now: it waits up to
    /// <see cref="CopyWaitMicros"/> for a copy from the host (asked for: <see cref="BatchDecision.CopyFor"/>), and applies
    /// without its run-bound parts if the host doesn't keep it, it can't be loaded here, or none comes in time. As a session
    /// ends while one is still on its way, the batch is left unapplied: a rejoin of the same hosting session brings it again
    /// (rather than marking it applied without its squad record). The wait counts only the time the link to the host is up,
    /// and starts again when the link restarts: a drop right after a restart's rejoin, however long, doesn't use it up,
    /// and a copy that "never came" because nobody could hear the ask isn't what ends the wait.
    /// </summary>
    private BatchDecision DecideCopy(ProgressBatch batch, in ApplyFacts facts, IRunCopies copies, long nowMicros)
    {
        RunCopyState state = copies.StateOf(batch.Run);
        if (state is RunCopyState.Refused or RunCopyState.Failed)
        {
            _copyWaitSince = -1;
            _copyLastMicros = -1;
            return BatchDecision.Apply(RunContext.None, $"{RunBoundParts} skipped: this PC held the host's run before its game restarted, " +
                $"and no copy of it could be had here ({copies.Why(batch.Run)})");
        }

        if (facts.Ending)
        {
            _copyWaitSince = -1;
            _copyLastMicros = -1;
            return BatchDecision.WaitForCopy("a copy of the host's run it's for was still on its way (a rejoin of this hosting session brings the batch again)", batch.Run);
        }

        // The wait's clock: it starts with the first look; the time the link is down isn't waited (the clock moves on with it),
        // and a link that restarted since the last look gives the wait a fresh start (asked again with it).
        long step = _copyLastMicros < 0 ? 0 : Math.Max(0, nowMicros - _copyLastMicros);
        _copyLastMicros = nowMicros;
        if (_copyWaitSince < 0 || copies.Restarts != _copyRestarts)
        {
            _copyWaitSince = nowMicros;
            _copyRestarts = copies.Restarts;
        }
        else if (!facts.LinkUp)
        {
            _copyWaitSince += step;
        }

        if (!facts.LinkUp)
        {
            return BatchDecision.WaitForCopy("the link to the host is down; the wait for a copy of the host's run it's for is paused (this PC held that run before its game restarted)", batch.Run);
        }

        // One the game is loading now ends by itself (the loader's own limit): given up on now, the batch would lose its
        // squad record with the copy a moment away.
        if (state == RunCopyState.Loading || nowMicros - _copyWaitSince < CopyWaitMicros)
        {
            return BatchDecision.WaitForCopy("this PC is getting a copy of the host's run it's for (it held that run before its game restarted)", batch.Run);
        }

        _copyWaitSince = -1;
        _copyLastMicros = -1;
        return BatchDecision.Apply(RunContext.None, $"{RunBoundParts} skipped: no copy of the host's run came within {CopyWaitMicros / 1_000_000} s of the link being up");
    }

    /// <summary>
    /// The unlock checks need this game to track stats right now, as the host's did. False when the batch can go on:
    /// <paramref name="tracking"/>, or the wait (<see cref="GateWaitMicros"/>, none as a session ends) is over and the checks
    /// are skipped (<paramref name="skipChecks"/>). True while the batch waits for it.
    /// </summary>
    public bool WaitsForGate(bool tracking, bool ending, long nowMicros, out bool skipChecks)
    {
        skipChecks = false;
        if (tracking)
        {
            _gateSince = -1;
            return false;
        }

        if (_gateSince < 0)
        {
            _gateSince = nowMicros;
        }

        if (!ending && nowMicros - _gateSince < GateWaitMicros)
        {
            return true;
        }

        _gateSince = -1;
        skipChecks = true;
        return false;
    }

    /// <summary>What happens to <paramref name="entry"/> with its run-bound parts in <paramref name="context"/>.</summary>
    public static EntryVerdict For(ProgressEntry entry, RunContext context, in ApplyAbilities can) => entry switch
    {
        StatEntry or StatTextEntry or EntityStatEntry => can.Stats ? EntryVerdict.Apply : EntryVerdict.SkipNoStats,
        RunHistoryRecord => EntryVerdict.Apply, // its own checks: HistoryRefusal
        UnlockPassEntry => context == RunContext.None ? EntryVerdict.SkipNoRun
            : !can.Checks ? EntryVerdict.SkipChecksOff
            : can.Unlocks ? EntryVerdict.Apply : EntryVerdict.SkipNoUnlocks,
        QuestEntry => context == RunContext.None ? EntryVerdict.SkipNoRun : can.Quests ? EntryVerdict.Apply : EntryVerdict.SkipNoQuests,
        SquadEntry or ComicsEntry or RunEndingEntry => context == RunContext.None ? EntryVerdict.SkipNoRun : EntryVerdict.Apply,
        ProfileMarkEntry => EntryVerdict.Apply, // the profile only: its own checks (ProfileMarks)
        _ => EntryVerdict.SkipNoRun,
    };

    /// <summary>The note a batch's line carries for entries skipped with <paramref name="verdict"/> (empty for none, or one the decision already explained).</summary>
    public static string Note(EntryVerdict verdict) => verdict switch
    {
        EntryVerdict.SkipNoStats => "stat operations skipped: this game's stats aren't there",
        EntryVerdict.SkipChecksOff => "unlock checks skipped: this game still doesn't track stats",
        EntryVerdict.SkipNoUnlocks => "unlock checks skipped: this game's unlock system isn't there",
        EntryVerdict.SkipNoQuests => "bench quest steps skipped: this game's bench quest system isn't there",
        _ => string.Empty,
    };

    /// <summary>
    /// Whether stat operation <paramref name="entry"/> may apply to a value that is <paramref name="before"/> now (NaN: not
    /// a number this mod reads): never past a whole number's largest value, and what the host's game takes back never below
    /// zero. The session's cap is <see cref="ProgressCredit"/>'s.
    /// </summary>
    public static StatVerdict Check(StatEntry entry, double before, bool whole)
    {
        ArgumentNullException.ThrowIfNull(entry);
        double credit = ProgressLimits.CreditOf(entry);
        if (whole && credit > 0 && !double.IsNaN(before) && before + credit > int.MaxValue)
        {
            return StatVerdict.Overflow;
        }

        return credit < 0 && (double.IsNaN(before) || before + credit < 0) ? StatVerdict.BelowZero : StatVerdict.Apply;
    }

    /// <summary>The values of its stat that <paramref name="entry"/> changes (a reset clears the lifetime value too).</summary>
    public static IEnumerable<(int Stat, StatBucket Bucket)> Changes(StatEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        StatBucket bucket = StatOps.BucketOf(entry.Op);
        yield return (entry.Stat, bucket);
        if (StatOps.IsReset(entry.Op) && bucket != StatBucket.Lifetime)
        {
            yield return (entry.Stat, StatBucket.Lifetime);
        }
    }

    /// <summary>
    /// Why run history entry <paramref name="entry"/> of a batch for run <paramref name="batchRun"/> isn't saved, or null when
    /// it is: it must be the batch's run, and one this PC held (<paramref name="runSeen"/>: this session, a kept copy, or an
    /// earlier session of the same co-op run), so a host can't fill a friend's history with runs nobody here played.
    /// </summary>
    public static string? HistoryRefusal(RunHistoryData entry, string batchRun, bool runSeen)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return batchRun.Length == 0 || entry.RunId != batchRun ? "the run history entry isn't for the batch's run; not saved"
            : !runSeen ? "the run history entry not saved: this PC never held that run"
            : null;
    }
}

/// <summary>
/// A run's best scorer for its squad record: what the game's <c>ProfileData.UpdateMaxGoalsIfAppropriate</c> takes from
/// the run's scorers (<c>RunStatsState.runScorersStats</c>, in order: each with more goals than the best so far, starting
/// from none, becomes the best), so the first of them with the most goals, when that's more than none. The host sends it in
/// its <see cref="SquadEntry"/>; a friend's game, whose run holds other scorers (an older checkpoint's, or another run's),
/// records with it. Game-independent.
/// </summary>
public static class RunScorers
{
    /// <summary>
    /// The best of <paramref name="scorers"/> (name and goals) by the game's rule, or none (empty, 0). Goals beyond
    /// <see cref="ProgressLimits.MaxRunGoals"/> count as that many; a name the record can't carry
    /// (<see cref="ProgressLimits.IsPlausibleScorer"/>) makes it none, and <paramref name="problem"/> says why (the friend's
    /// record then keeps its own).
    /// </summary>
    public static (string Name, int Goals) Best(IEnumerable<(string? Name, int Goals)> scorers, out string problem)
    {
        ArgumentNullException.ThrowIfNull(scorers);
        problem = string.Empty;
        string? name = null;
        int goals = 0;
        foreach ((string? candidate, int scored) in scorers)
        {
            if (scored > goals)
            {
                goals = scored;
                name = candidate;
            }
        }

        if (goals == 0)
        {
            return (string.Empty, 0);
        }

        goals = Math.Min(goals, ProgressLimits.MaxRunGoals);
        if (!ProgressLimits.IsPlausibleScorer(name, goals) || string.IsNullOrEmpty(name))
        {
            problem = $"the run's best scorer ({goals} goals) has a name the squad record can't carry";
            return (string.Empty, 0);
        }

        return (name, goals);
    }
}

/// <summary>
/// Within one batch on a friend: whether each record's lifetime value rose with its last operation, so a record holder's
/// name (<see cref="StatTextEntry"/>) follows this profile's own record, as the game names the holder only when its lifetime
/// record rises. Game-independent.
/// </summary>
public sealed class ProgressRecordWatch
{
    private readonly Dictionary<int, bool> _rose = new();

    /// <summary>Lifetime record operation <paramref name="entry"/> applied here and <paramref name="rose"/> (or didn't).</summary>
    public void Applied(StatEntry entry, bool rose)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.Op is StatOp.TrySetLarger or StatOp.TrySetLargerFromStat)
        {
            _rose[entry.Stat] = rose;
        }
    }

    /// <summary>True when <paramref name="text"/>'s record rose here with its last lifetime operation of the batch.</summary>
    public bool Holds(StatTextEntry text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return _rose.TryGetValue(text.RecordStat, out bool rose) && rose;
    }

    /// <summary>A new batch.</summary>
    public void Clear() => _rose.Clear();
}
