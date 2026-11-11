using System;
using System.Collections.Generic;
using System.Linq;
using T2TOnline.Core.Diagnostics;

namespace T2TOnline.Core.Campaign;

/// <summary>One batch's line on a friend's PC: what applied, what was skipped and why (the plugin builds and writes it).</summary>
public interface IProgressLog
{
    /// <summary>The batch's number.</summary>
    uint Sequence { get; }

    /// <summary>Something the line says about the batch as a whole, or why a part of it didn't apply.</summary>
    void Note(string text);

    /// <summary>Something the batch did here ("squad win gauntlet ...").</summary>
    void Part(string text);

    /// <summary>Entry <paramref name="entry"/> threw <paramref name="exception"/> (the rest still apply).</summary>
    void Failed(ProgressEntry entry, Exception exception);
}

/// <summary>Whether this game tracks stats right now, as the unlock checks need (its difficulty set to the run's for them).</summary>
/// <param name="Tracking">It does.</param>
/// <param name="WhyNot">When it doesn't, why ("the difficulty here is training").</param>
/// <param name="AlignedTo">The run's difficulty this game's was set to for the checks, or -1 when it already was.</param>
public readonly record struct StatsGate(bool Tracking, string WhyNot, int AlignedTo);

/// <summary>How loud a line of the applier's is.</summary>
public enum ProgressLogLevel
{
    /// <summary>The session file only.</summary>
    Trace,

    /// <summary>An ordinary line.</summary>
    Info,

    /// <summary>Something went wrong, or something the player should know.</summary>
    Warn,

    /// <summary>A failure, with its exception.</summary>
    Error,
}

/// <summary>
/// What a friend's game does for <see cref="ProgressApplier"/>: the game's state and calls (the plugin's
/// <c>ProgressFriend</c>; a fake in the tests). Each call runs on the game thread, inside the applier's guards: one that
/// throws is caught there (an entry's own call fails that entry only; a put-back's is logged and the others still run).
/// </summary>
public interface IProgressTarget
{
    /// <summary>The shared run whose bench quest counts are live in this game (empty: none).</summary>
    string LiveSharedRun { get; }

    /// <summary>True while the campaign state holds a run (the run-bound parts need one).</summary>
    bool HoldsRun { get; }

    /// <summary>True while a run history entry is still being saved (nothing more applies until it's done).</summary>
    bool HistorySaving { get; }

    /// <summary>What the game is like right now, for <see cref="ProgressApplyPolicy.Decide"/> (<paramref name="ending"/>: the session is ending).</summary>
    ApplyFacts Facts(bool ending);

    /// <summary>Before the frame's batches: the history save in flight polled, the run held followed.</summary>
    void BeforeBatches(long nowMicros);

    /// <summary>Right before a batch applies: the run held looked at again.</summary>
    void BeforeBatch();

    /// <summary>
    /// <paramref name="batch"/> waits for a copy of the host's run it's for (this PC held it before its game restarted): it's
    /// asked for (once per run; called every frame the batch waits).
    /// </summary>
    void WantRunCopy(ProgressBatch batch);

    /// <summary>A new line for <paramref name="batch"/>.</summary>
    IProgressLog StartLog(ProgressBatch batch);

    /// <summary>Puts this PC's kept copy of shared run <paramref name="run"/> in the campaign state; false when it's gone.</summary>
    bool PutKeptRun(string run);

    /// <summary>The run held before <see cref="PutKeptRun"/> comes back.</summary>
    void PutHeldRunBack();

    /// <summary>True when this PC keeps a copy of shared run <paramref name="run"/>.</summary>
    bool Keeps(string run);

    /// <summary>The bench quests' per-run counts live now (<see cref="ProgressLedger.RunCounters"/> of them), or null when this game has none.</summary>
    int[]? ReadQuestCounts();

    /// <summary>Sets the bench quests' per-run counts (<see cref="ProgressLedger.RunCounters"/> of them).</summary>
    void WriteQuestCounts(IReadOnlyList<int> counts);

    /// <summary>What this game has to apply with (its <see cref="ApplyAbilities.Checks"/> is the applier's).</summary>
    ApplyAbilities Abilities();

    /// <summary>For the unlock checks: this game's difficulty set to the run's, and whether it tracks stats now.</summary>
    StatsGate OpenStatsGate();

    /// <summary>This game's difficulty back as it was before <see cref="OpenStatsGate"/>.</summary>
    void CloseStatsGate();

    /// <summary>The entries start applying (the game's own credit is let through; <paramref name="inRun"/>: told it's in the campaign).</summary>
    void StartApplying(bool inRun);

    /// <summary>They're done: what <see cref="StartApplying"/> changed goes back.</summary>
    void StopApplying();

    /// <summary>
    /// Applies a stat operation, a record holder, an entity stat, an unlock check, the comics or a bench quest step through
    /// the game's own calls (the applier handles the squad record, the run's end, the run history entry and the marks).
    /// </summary>
    void Apply(ProgressEntry entry, IProgressLog log);

    /// <summary>Why the run held here isn't <paramref name="squad"/>'s ("this PC's run is ..."), or null when it is.</summary>
    string? SquadMismatch(SquadEntry squad);

    /// <summary>Records <paramref name="squad"/> on this profile's squad record (its once-per-run mark is made).</summary>
    void RecordSquad(SquadEntry squad, IProgressLog log);

    /// <summary>The game's own end of the run held here for its benchwarmers (its once-per-run mark is made).</summary>
    void EndRun(IProgressLog log);

    /// <summary>Why this game can't save a run history entry now, or null when it can.</summary>
    string? HistoryUnavailable();

    /// <summary>Starts saving <paramref name="history"/> (its once-per-run mark is made); <see cref="HistorySaving"/> until it's done.</summary>
    void SaveHistory(RunHistoryRecord history, long nowMicros, IProgressLog log);

    /// <summary>Marks <paramref name="mark"/> on this profile (checked: <see cref="ProfileMarks"/>).</summary>
    void Mark(ProfileMarkEntry mark, IProgressLog log);

    /// <summary>After a batch's entries: the derived stats its operations changed, worked out again from this profile's own counts.</summary>
    void WorkOutDerived(IProgressLog log);

    /// <summary>The ledger changed: it's saved.</summary>
    void LedgerChanged();

    /// <summary>After the frame's batches: this profile and stats saved once; says what the save did, for each batch's line.</summary>
    string SaveAfter(IReadOnlyList<IProgressLog> applied);

    /// <summary>Writes <paramref name="log"/>'s line, ending with <paramref name="saved"/>.</summary>
    void Report(IProgressLog log, string saved);

    /// <summary>Writes a line of the applier's own.</summary>
    void Log(ProgressLogLevel level, string line, Exception? exception = null);
}

/// <summary>
/// A friend's applier of the host's batches (D42, D47: its order of work, tested without the game). Each frame the
/// batches that can apply do, in order (<see cref="ApplyWaiting"/>), against an <see cref="IProgressTarget"/>:
/// <list type="number">
/// <item>whether it applies now and where its run-bound parts go (<see cref="ProgressApplyPolicy.Decide"/>); the first that
/// waits stops the frame's batches (one that waits for a copy of the host's run has it asked for);</item>
/// <item>on a kept copy of its run, the copy is put in place just for the batch, with that run's bench quest
/// counts from the ledger (unless the live counts are already that run's);</item>
/// <item>the unlock checks wait for this game to track stats (<see cref="ProgressApplyPolicy.WaitsForGate"/>): while they
/// do, nothing of the batch applies, and what was put in place goes back;</item>
/// <item>each entry by its verdict (<see cref="ProgressApplyPolicy.For"/>), each on its own (one that throws doesn't stop
/// the rest); the squad record, the run's end for the benchwarmers and the run history entry count once per run (the
/// ledger), and a profile mark only when it's one this mod knows (<see cref="ProfileMarks"/>); then the derived stats;</item>
/// <item>whatever happens, what was put in place goes back, each part on its own: the game's flags, the difficulty, the kept
/// run's quest counts (into the ledger when the batch applied) and the run held before;</item>
/// <item>the batch is marked applied in the ledger before anything is saved (a game that stops in between misses it rather
/// than counting it twice); a batch that failed outside its entries still counts as applied, logged, so it can't hold up the
/// rest;</item>
/// <item>after the frame's batches, this profile and stats are saved once and each batch's line is written.</item>
/// </list>
/// Game-independent. Not thread-safe (one game thread).
/// </summary>
public sealed class ProgressApplier
{
    /// <summary>The ledger's once-per-run part for the squad record.</summary>
    public const string OnceSquad = "squad";

    /// <summary>The ledger's once-per-run part for the run history entry.</summary>
    public const string OnceHistory = "history";

    /// <summary>The ledger's once-per-run part for the run's end for the benchwarmers (bench snapshots).</summary>
    public const string OnceEnding = "ending";

    private readonly IProgressTarget _target;
    private readonly ProgressInbox _inbox;
    private readonly ProgressLedger _ledger;
    private readonly string _host;
    private readonly HashSet<string> _held = new(StringComparer.Ordinal);
    private readonly HashSet<string> _noted = new(StringComparer.Ordinal);

    /// <summary>An applier of <paramref name="inbox"/>'s batches from <paramref name="host"/> (the host's log tag, never a name: it is written into log lines), remembered in <paramref name="ledger"/>.</summary>
    public ProgressApplier(IProgressTarget target, ProgressInbox inbox, ProgressLedger ledger, string host)
    {
        _target = target ?? throw new ArgumentNullException(nameof(target));
        _inbox = inbox ?? throw new ArgumentNullException(nameof(inbox));
        _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
        _host = host ?? throw new ArgumentNullException(nameof(host));
    }

    /// <summary>The decisions (apply or wait, where the run-bound parts go).</summary>
    public ProgressApplyPolicy Policy { get; } = new();

    /// <summary>True once the session is ending (<see cref="Flush"/>): nothing it waits for can come now.</summary>
    public bool Ending { get; private set; }

    /// <summary>Batches applied.</summary>
    public int Applied { get; private set; }

    /// <summary>Batches applied on this PC's kept copy of a run it no longer held.</summary>
    public int KeptApplied { get; private set; }

    /// <summary>Batches that failed outside their entries (they count as applied).</summary>
    public int FailedBatches { get; private set; }

    /// <summary>Since when the next batch waits (-1: it doesn't).</summary>
    public long WaitSince { get; private set; } = -1;

    /// <summary>Why the next batch waits (empty: it doesn't, or hasn't yet).</summary>
    public string WaitWhy { get; private set; } = string.Empty;

    /// <summary>This game held shared run <paramref name="run"/> during this session (its run history entry may be saved here).</summary>
    public void Held(string run)
    {
        if (!string.IsNullOrEmpty(run))
        {
            _held.Add(run);
        }
    }

    /// <summary>True when this game held shared run <paramref name="run"/> during this session.</summary>
    public bool HeldThisSession(string run) => _held.Contains(run);

    /// <summary>
    /// Applies the batches that can apply now, in order, at most <paramref name="most"/> (see the class); returns how many
    /// did. Never throws: an error outside any one batch is logged once per kind, and tried again next frame.
    /// </summary>
    public int ApplyWaiting(long nowMicros, int most)
    {
        // Most frames apply nothing: no list for them (this runs every frame of a friend's session).
        List<IProgressLog>? applied = null;
        try
        {
            _target.BeforeBatches(nowMicros);
            for (int i = 0; i < most && (Ending || !_target.HistorySaving) && _inbox.TryPeek(nowMicros, out ProgressBatch? batch, out uint skipped, giveUpOnGap: Ending); i++)
            {
                if (skipped > 0)
                {
                    _target.Log(ProgressLogLevel.Warn, $"Progress: {skipped} of {_host}'s batches before batch {batch!.Sequence} " +
                        (Ending ? "hadn't come as the session ended" : $"didn't come within {ProgressInbox.GapGiveUpMicros / 1_000_000} s") +
                        " and were skipped; they still apply, once, if the host sends them again (they're asked for) or a rejoin of this hosting session brings them.");
                }

                // As the session ends nothing is left to wait for a run history entry still saving (the game's save goes on over
                // frames, and a run's end comes in the batch right after it), but one save runs at a time.
                if (_target.HistorySaving && batch!.Entries.Any(e => e is RunHistoryRecord))
                {
                    Wait(nowMicros, "a run history entry is still saving");
                    break;
                }

                ApplyFacts facts = _target.Facts(Ending);
                if (!facts.Sharing)
                {
                    // Kept, never marked applied: it applies if sharing is turned back on this session, or comes again on a rejoin.
                    Once("off", $"Progress: {_host}'s progress waits here while ShareProgress is off; it applies if you turn it back on during this session.");
                }

                BatchDecision decision = Policy.Decide(batch!, facts, nowMicros);
                if (decision.Verdict == BatchVerdict.Wait)
                {
                    if (decision.CopyFor.Length > 0 && !Ending)
                    {
                        _target.WantRunCopy(batch!); // not as the session ends: nothing would be asked any more
                    }

                    Wait(nowMicros, decision.Why);
                    break;
                }

                _target.BeforeBatch();
                IProgressLog? log = ApplyOne(batch!, decision, nowMicros);
                if (log == null)
                {
                    break; // it waits (the stats gate): nothing was applied
                }

                (applied ??= new List<IProgressLog>()).Add(log);
                WaitSince = -1;
            }
        }
        catch (Exception ex)
        {
            // Outside any one batch (the inbox, the history save's poll): logged once per kind, tried again next frame.
            Once($"update {ex.GetType().Name}", $"Progress: applying {_host}'s progress failed ({SafeError.Text(ex)}); it's tried again.");
        }
        finally
        {
            if (applied != null)
            {
                Finish(applied);
            }
        }

        return applied?.Count ?? 0;
    }

    /// <summary>
    /// The session is ending: from now on nothing a batch waits for can come (the host's match and run loads no longer hold
    /// it back, a batch for a run this PC was about to load applies without its run-bound parts, a gap isn't waited out, and
    /// only a second run history entry waits for one still saving). Applies what can, at most
    /// <see cref="ProgressApplyPolicy.MaxAtEnd"/>, saved once; returns how many did.
    /// </summary>
    public int Flush(long nowMicros)
    {
        Ending = true;
        return ApplyWaiting(nowMicros, ProgressApplyPolicy.MaxAtEnd);
    }

    /// <summary>Applies one batch and marks it done; null when it must wait (nothing applied). See the class.</summary>
    private IProgressLog? ApplyOne(ProgressBatch batch, BatchDecision decision, long nowMicros)
    {
        IProgressLog log = _target.StartLog(batch);
        bool swapped = false, questSwapped = false, gateOpen = false, applying = false, waits = false;
        int[]? liveQuest = null;
        RunContext context = decision.Context;
        try
        {
            if (decision.Skipped.Length > 0)
            {
                log.Note(decision.Skipped);
            }

            if (context == RunContext.Kept)
            {
                // This PC's copy of the batch's run goes in just for the batch, with that run's own bench quest
                // counts; the run held now and its counts go back in the finally.
                swapped = _target.PutKeptRun(batch.Run);
                if (!swapped)
                {
                    context = RunContext.None;
                    log.Note($"{ProgressApplyPolicy.RunBoundParts} skipped: this PC's copy of the host's run is gone");
                }
                else
                {
                    log.Note("applied on this PC's kept copy of the host's run (it no longer holds it)");

                    // The live counts are still that run's when it's the shared run followed last (newer than the ledger's).
                    if (batch.Run != _target.LiveSharedRun && _target.ReadQuestCounts() is int[] live)
                    {
                        liveQuest = live;
                        _target.WriteQuestCounts(_ledger.TryGetRun(batch.Run, out int[] counts) ? counts : new int[ProgressLedger.RunCounters]);
                        questSwapped = true;
                    }
                }
            }

            if (context != RunContext.None && !_target.HoldsRun)
            {
                // The facts named a run, and the campaign state holds none now (reset in between): said, as every other skip is.
                context = RunContext.None;
                log.Note($"{ProgressApplyPolicy.RunBoundParts} skipped: this game's campaign holds no run right now");
            }

            if (context != RunContext.None)
            {
                _held.Add(batch.Run);
            }

            // The unlock checks need this game to track stats right now, as the host's did.
            ApplyAbilities can = _target.Abilities();
            bool checksOn = true;
            if (context != RunContext.None && can.Stats && batch.Entries.Any(e => e is UnlockPassEntry))
            {
                gateOpen = true;
                StatsGate gate = _target.OpenStatsGate();
                if (Policy.WaitsForGate(gate.Tracking, Ending, nowMicros, out bool skipChecks))
                {
                    Wait(nowMicros, $"this game doesn't track stats right now ({gate.WhyNot})");
                    waits = true;
                    return null; // what was put in place goes back in the finally (nothing of the batch applied)
                }

                checksOn = !skipChecks;
                if (skipChecks)
                {
                    log.Note($"unlock checks skipped: this game still doesn't track stats ({gate.WhyNot})");
                }
                else if (gate.AlignedTo >= 0)
                {
                    log.Note($"difficulty set to the run's ({gate.AlignedTo}) for the unlock checks");
                }
            }

            can = can with { Checks = checksOn };
            applying = true;
            _target.StartApplying(context != RunContext.None);
            var skippedVerdicts = new HashSet<EntryVerdict>();
            foreach (ProgressEntry entry in batch.Entries)
            {
                EntryVerdict verdict = ProgressApplyPolicy.For(entry, context, can);
                if (verdict != EntryVerdict.Apply)
                {
                    skippedVerdicts.Add(verdict);
                    continue;
                }

                try
                {
                    ApplyEntry(entry, batch, nowMicros, log);
                }
                catch (Exception ex)
                {
                    // One entry that fails doesn't stop the rest, and the batch still counts as applied (never twice).
                    log.Failed(entry, ex);
                }
            }

            foreach (EntryVerdict verdict in skippedVerdicts)
            {
                string note = ProgressApplyPolicy.Note(verdict);
                if (note.Length > 0)
                {
                    log.Note(note);
                }
            }

            // The records and percentages the game works out from counters: again here, from this profile's own.
            if (can.Stats)
            {
                _target.WorkOutDerived(log);
            }
        }
        catch (Exception ex)
        {
            // Outside any one entry: the batch counts as applied all the same (never twice, never every frame), and says so.
            FailedBatches++;
            log.Note($"the batch failed as a whole ({SafeError.Text(ex)})");
            _target.Log(ProgressLogLevel.Error, $"Progress: {_host}'s batch {batch.Sequence} failed outside its entries; it counts as applied so the rest can follow", ex);
        }
        finally
        {
            PutBack(applying, gateOpen, swapped, questSwapped, liveQuest, batch.Run, applied: !waits);
        }

        // Applied, in the ledger, before anything is saved: this batch never counts twice when the host sends it again (a
        // game that stops before its profile is saved misses it instead).
        _inbox.Done(batch);
        _target.LedgerChanged();
        Applied++;
        KeptApplied += swapped ? 1 : 0;
        return log;
    }

    /// <summary>One entry the policy lets apply.</summary>
    private void ApplyEntry(ProgressEntry entry, ProgressBatch batch, long nowMicros, IProgressLog log)
    {
        switch (entry)
        {
            case SquadEntry squad:
                ApplySquad(squad, batch.Run, log);
                break;
            case RunEndingEntry:
                // A run ends once: its bench snapshots are taken once on this profile, whatever brings it again.
                if (!_ledger.TryMarkOnce(OnceEnding, batch.Run))
                {
                    log.Note("the run's end for the benchwarmers not applied again: this run's bench snapshots are already on this profile");
                    return;
                }

                _target.EndRun(log);
                break;
            case RunHistoryRecord history:
                ApplyHistory(history, batch, nowMicros, log);
                break;
            case ProfileMarkEntry mark:
                string? refused = ProfileMarks.Refusal(mark);
                if (refused != null)
                {
                    log.Note(refused);
                    return;
                }

                _target.Mark(mark, log);
                break;
            default:
                _target.Apply(entry, log);
                break;
        }
    }

    private void ApplySquad(SquadEntry squad, string run, IProgressLog log)
    {
        string what = squad.Outcome == SquadOutcome.Won ? "win" : "loss";
        string? mismatch = _target.SquadMismatch(squad);
        if (mismatch != null)
        {
            log.Note($"squad {what} {Diagnostics.HostText.KeyForLog(squad.Squad)} not recorded: {mismatch}");
            return;
        }

        // A run ends once: its result counts once on this profile, whatever brings it again.
        if (!_ledger.TryMarkOnce(OnceSquad, run))
        {
            log.Note($"squad {what} {Diagnostics.HostText.KeyForLog(squad.Squad)} not recorded again: this run's result is already on this profile");
            return;
        }

        _target.RecordSquad(squad, log);
    }

    private void ApplyHistory(RunHistoryRecord history, ProgressBatch batch, long nowMicros, IProgressLog log)
    {
        // Only the batch's run, and one this PC held (this session, a kept copy, or an earlier session of the same co-op
        // run): each entry takes one of the history's slots, and a host mustn't fill them with runs nobody here played.
        string? refused = ProgressApplyPolicy.HistoryRefusal(history.Entry, batch.Run,
            _held.Contains(batch.Run) || _target.Keeps(batch.Run) || _ledger.TryGetRun(batch.Run, out _));
        if (refused != null)
        {
            log.Note(refused);
            return;
        }

        if (_target.HistorySaving)
        {
            log.Note("a second run history entry in one batch; not saved");
            return;
        }

        string? unavailable = _target.HistoryUnavailable();
        if (unavailable != null)
        {
            log.Note(unavailable);
            return;
        }

        // Once per run: each entry takes a slot, and the oldest of this PC's own runs make way for it.
        if (!_ledger.TryMarkOnce(OnceHistory, batch.Run))
        {
            log.Note("the shared run is already in this PC's run history; not saved again");
            return;
        }

        _target.SaveHistory(history, nowMicros, log);
    }

    /// <summary>
    /// After a batch (applied or waiting): what was put in place goes back, each part on its own so one failing can't keep
    /// another: the game's flags, the difficulty, then on a kept run its quest counts (into the ledger when the batch
    /// applied; a batch that waits changed none of them, and the ledger isn't written every frame of its wait) and the run
    /// held before.
    /// </summary>
    private void PutBack(bool applying, bool gateOpen, bool swapped, bool questSwapped, int[]? liveQuest, string run, bool applied)
    {
        if (applying)
        {
            Guard("campaign flag", "put the game's campaign flag back", _target.StopApplying);
        }

        if (gateOpen)
        {
            Guard("difficulty", "put this PC's difficulty back", _target.CloseStatsGate);
        }

        if (!swapped)
        {
            return;
        }

        if (questSwapped && liveQuest != null)
        {
            Guard("kept counts", "put the bench quest counts back after applying on a kept run", () =>
            {
                if (applied && _target.ReadQuestCounts() is int[] counts && counts.Length == ProgressLedger.RunCounters)
                {
                    _ledger.SetRun(run, counts);
                }

                _target.WriteQuestCounts(liveQuest);
                if (applied)
                {
                    _target.LedgerChanged();
                }
            });
        }

        Guard("kept run", "put the run held here back after applying on a kept run", _target.PutHeldRunBack);
    }

    /// <summary>After the frame's batches: saved once, then each batch's line written.</summary>
    private void Finish(List<IProgressLog> applied)
    {
        string saved;
        try
        {
            saved = _target.SaveAfter(applied);
        }
        catch (Exception ex)
        {
            saved = $"NOT saved ({SafeError.Text(ex)})";
        }

        uint first = applied[0].Sequence, last = applied[^1].Sequence;
        foreach (IProgressLog log in applied)
        {
            try
            {
                _target.Report(log, applied.Count == 1 ? saved : $"{saved} (with batches {first} to {last})");
            }
            catch (Exception ex)
            {
                _target.Log(ProgressLogLevel.Warn, $"Progress: batch {log.Sequence} from {_host} applied ({saved}), but its line couldn't be written ({ex.GetType().Name}).");
            }
        }
    }

    private void Wait(long nowMicros, string why)
    {
        if (WaitSince < 0 || why != WaitWhy)
        {
            WaitSince = nowMicros;
            WaitWhy = why;
            _target.Log(ProgressLogLevel.Trace, $"Progress: {_inbox.Held} batch(es) from {_host} wait: {why}.");
        }
    }

    private void Guard(string key, string what, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Once(key, $"Progress: couldn't {what} ({SafeError.Text(ex)}).");
        }
    }

    private void Once(string key, string line)
    {
        if (_noted.Add(key))
        {
            _target.Log(ProgressLogLevel.Warn, line);
        }
    }
}

/// <summary>
/// Which profile marks a friend's game makes (<see cref="ProfileMarkEntry"/>): a Nurse procedure any time (the game's own
/// <c>MarkOffered</c> checks it's one of its procedures), and a seen dialogue only when it's an event NPC's line, as the
/// game names them (<c>NpcIntroDialogue</c>: its pool's first-visit id, "chaosNode", or one of its visit lines,
/// "chaosNode/visit_3"; a random line marks "chaosNode/visit_0"), never a run's mark ("chaosNode/run_&lt;run&gt;", which
/// only means something in that run), a campaign tip or a training milestone (a friend never sees those). Game-independent.
/// </summary>
public static class ProfileMarks
{
    /// <summary>What separates a pool's id from a visit line's number in its seen id (the game's <c>GetVisitSeenId</c>).</summary>
    public const string VisitSeparator = "/visit_";

    /// <summary>Most digits a visit line's number has.</summary>
    private const int MaxVisitDigits = 4;

    /// <summary>
    /// True when <paramref name="id"/> is an event NPC line's seen id: a dialogue pool's id (letters, digits, '_', '-', '.')
    /// on its own (the first visit), or followed by <see cref="VisitSeparator"/> and a number (a visit line).
    /// <paramref name="pool"/> is the pool's id.
    /// </summary>
    public static bool TryReadNpcLine(string? id, out string pool)
    {
        pool = string.Empty;
        if (!ProgressLimits.IsPlausibleId(id))
        {
            return false;
        }

        int slash = id!.IndexOf('/', StringComparison.Ordinal);
        string head = slash < 0 ? id : id[..slash];
        if (!IsPoolId(head))
        {
            return false;
        }

        if (slash >= 0)
        {
            string tail = id[slash..];
            if (!tail.StartsWith(VisitSeparator, StringComparison.Ordinal))
            {
                return false;
            }

            string digits = tail[VisitSeparator.Length..];
            if (digits.Length is 0 or > MaxVisitDigits || !digits.All(c => c is >= '0' and <= '9'))
            {
                return false;
            }
        }

        pool = head;
        return true;
    }

    /// <summary>True when <paramref name="id"/> is a line of the pool whose id is <paramref name="poolId"/> (see <see cref="TryReadNpcLine"/>).</summary>
    public static bool IsLineOf(string? id, string? poolId) =>
        TryReadNpcLine(id, out string pool) && string.Equals(pool, poolId, StringComparison.Ordinal);

    /// <summary>Why <paramref name="mark"/> isn't made on this profile, or null when it is.</summary>
    public static string? Refusal(ProfileMarkEntry mark)
    {
        ArgumentNullException.ThrowIfNull(mark);
        return mark.Mark == ProfileMark.DialogueSeen && !TryReadNpcLine(mark.Dialogue, out _)
            ? $"dialogue {Diagnostics.HostText.KeyForLog(mark.Dialogue)} not marked seen: it isn't an event NPC's line"
            : null;
    }

    private static bool IsPoolId(string id) => id.Length > 0 && id.All(c => c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_' or '-' or '.');
}
