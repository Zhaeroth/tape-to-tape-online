using System;
using System.Collections.Generic;
using System.Linq;
using T2TOnline.Core.Campaign;

namespace T2TOnline.Core.Tests.Campaign;

/// <summary>
/// The friend's applier runs each batch's work in a fixed order against the game's side, and puts
/// back what it changed whatever happens. A fake game records every call in order.
/// </summary>
public class ProgressApplierTests
{
    private const ulong Session = 0xabc;
    private const string RunA = "run-a";
    private const string RunB = "run-b";
    private static readonly StatEntry Goal = new(9, StatOp.IncrementCampaign, StatValue.None);
    private static readonly UnlockPassEntry Check = new(2, 8, true, Array.Empty<string>());
    private static readonly SquadEntry Win = new(SquadOutcome.Won, "gauntlet", 2, 1800f, "Kovalski", 6);

    private static ProgressBatch Batch(uint sequence, string run, params ProgressEntry[] entries) => new(sequence, 1, run, entries);

    private static (ProgressApplier Applier, FakeGame Game, ProgressInbox Inbox, ProgressLedger Ledger) Make(params ProgressBatch[] batches)
    {
        var ledger = new ProgressLedger();
        var inbox = new ProgressInbox(ledger);
        var game = new FakeGame();
        foreach (ProgressBatch batch in batches)
        {
            inbox.OfferOwn(new ProgressMessage(Session, 1, batch));
        }

        return (new ProgressApplier(game, inbox, ledger, "\"Host\""), game, inbox, ledger);
    }

    [Fact]
    public void A_runs_end_on_the_run_held_applies_in_the_games_order_and_is_in_the_ledger_before_the_save()
    {
        var (applier, game, inbox, ledger) = Make(Batch(1, RunA, Goal, Check, Win, new RunEndingEntry(), ProfileMarkEntry.NurseOffered(3)));
        game.Held = RunA;

        Assert.Equal(1, applier.ApplyWaiting(0, ProgressApplyPolicy.MaxPerFrame));
        Assert.Equal(new[]
        {
            "start in run", "apply StatEntry", "apply UnlockPassEntry", "squad gauntlet", "end run", "mark Nurse procedure 3 offered", "derived",
            "stop", "close gate", "ledger", "save 1", "report 1",
        }, game.Calls.SkipWhile(c => c != "start in run"));
        Assert.Contains("open gate", game.Calls);
        Assert.Equal(1u, inbox.AppliedThrough);
        Assert.Equal(1u, ledger.AppliedThrough(Session));
        Assert.False(ledger.TryMarkOnce(ProgressApplier.OnceSquad, RunA));
        Assert.False(ledger.TryMarkOnce(ProgressApplier.OnceEnding, RunA));
        Assert.True(applier.HeldThisSession(RunA));
        Assert.Equal((1, 0, 0), (applier.Applied, applier.KeptApplied, applier.FailedBatches));
    }

    [Fact]
    public void A_batch_for_another_hosting_session_than_the_welcome_s_never_reaches_the_game()
    {
        // D135: a host naming a new session in each batch would start every session cap over; such a batch is never held,
        // so no stat operation of it is ever applied (or counted against a cap).
        var ledger = new ProgressLedger();
        var inbox = new ProgressInbox(ledger);
        var game = new FakeGame();
        Assert.Equal(ProgressOffer.WrongSession, inbox.Offer(new ProgressMessage(Session + 1, 1, Batch(1, string.Empty, Goal)), expected: Session));
        Assert.Equal(ProgressOffer.WrongSession, inbox.Offer(new ProgressMessage(Session, 1, Batch(1, string.Empty, Goal)), expected: 0));

        var applier = new ProgressApplier(game, inbox, ledger, "\"Host\"");
        Assert.Equal(0, applier.ApplyWaiting(0, 8));
        Assert.DoesNotContain("apply StatEntry", game.Calls);
        Assert.Equal((0UL, 0, 2L), (inbox.Session, inbox.Held, inbox.WrongSession));
        Assert.Equal(0u, ledger.AppliedThrough(Session + 1));
    }

    [Fact]
    public void Every_batch_that_can_apply_in_a_frame_does_and_they_are_saved_once()
    {
        var (applier, game, _, _) = Make(Batch(1, string.Empty, Goal), Batch(2, string.Empty, Goal), Batch(3, string.Empty, Goal));
        Assert.Equal(2, applier.ApplyWaiting(0, most: 2));
        Assert.Equal(1, game.Calls.Count(c => c.StartsWith("save", StringComparison.Ordinal)));
        Assert.Equal(new[] { "1: saved (with batches 1 to 2)", "2: saved (with batches 1 to 2)" }, game.Reported);
        Assert.Equal(1, applier.ApplyWaiting(0, most: 8));
        Assert.Equal("3: saved", game.Reported.Last());
    }

    [Fact]
    public void A_batch_on_a_kept_run_gets_that_runs_quest_counts_and_everything_goes_back()
    {
        var (applier, game, _, ledger) = Make(Batch(1, RunA, new QuestEntry(QuestStep.RunWon, 0), Win));
        ledger.SetRun(RunA, new[] { 1, 2, 3, 4, 5 });
        game.Held = RunB; // the host went on to the next run first
        game.Kept.Add(RunA);
        game.Quest = new[] { 9, 9, 9, 9, 9 }; // the next run's counts, live
        game.OnApply = e => game.Quest = game.Quest!.Select(c => c + 10).ToArray(); // the quest step counts on what's live

        Assert.Equal(1, applier.ApplyWaiting(0, 8));
        Assert.Equal(new[]
        {
            "put kept run-a", "write quest 1,2,3,4,5", "start in run", "apply QuestEntry", "squad gauntlet", "derived", "stop",
            "write quest 9,9,9,9,9", "ledger", "held run back", "ledger", "save 1", "report 1",
        }, game.Calls.SkipWhile(c => !c.StartsWith("put kept", StringComparison.Ordinal)));
        Assert.True(ledger.TryGetRun(RunA, out int[] kept));
        Assert.Equal(new[] { 11, 12, 13, 14, 15 }, kept); // that run's counts, as the batch left them
        Assert.Equal(new[] { 9, 9, 9, 9, 9 }, game.Quest); // the live run's back
        Assert.Equal(RunB, game.Current);
        Assert.Equal(1, applier.KeptApplied);
        Assert.Contains("applied on this PC's kept copy of the host's run (it no longer holds it)", game.Logs.Single().Notes);
    }

    [Fact]
    public void A_kept_run_whose_counts_are_live_already_keeps_them()
    {
        var (applier, game, _, _) = Make(Batch(1, RunA, new QuestEntry(QuestStep.RunWon, 0)));
        game.Held = string.Empty; // the session ended and nothing is held
        game.Kept.Add(RunA);
        game.LiveSharedRun = RunA;
        applier.ApplyWaiting(0, 8);
        Assert.DoesNotContain(game.Calls, c => c.StartsWith("write quest", StringComparison.Ordinal));
        Assert.Contains("held run back", game.Calls);
    }

    [Fact]
    public void When_the_kept_copy_is_gone_the_run_bound_parts_are_skipped_and_the_rest_applies()
    {
        var (applier, game, _, ledger) = Make(Batch(1, RunA, Goal, Win, new RunEndingEntry()));
        game.Held = RunB;
        game.Kept.Add(RunA);
        game.KeptGone = true;
        applier.ApplyWaiting(0, 8);
        Assert.Contains("apply StatEntry", game.Calls);
        Assert.DoesNotContain(game.Calls, c => c.StartsWith("squad", StringComparison.Ordinal) || c == "end run" || c == "held run back");
        Assert.Contains($"{ProgressApplyPolicy.RunBoundParts} skipped: this PC's copy of the host's run is gone", game.Logs.Single().Notes);
        Assert.True(ledger.TryMarkOnce(ProgressApplier.OnceSquad, RunA)); // never marked: a later copy could still record it
    }

    [Fact]
    public void When_the_campaign_holds_no_run_after_all_the_run_bound_parts_are_skipped_and_the_line_says_so()
    {
        // The facts said the batch's run is held here, yet the campaign state holds none as the batch applies (it was reset in
        // between): the rest applies, the squad record isn't marked (a later copy can still record it), and the line says why.
        var (applier, game, inbox, ledger) = Make(Batch(1, RunA, Goal, Win, new RunEndingEntry()));
        game.Held = RunA;
        game.NoRunInState = true;
        Assert.Equal(1, applier.ApplyWaiting(0, 8));
        Assert.Contains("apply StatEntry", game.Calls);
        Assert.DoesNotContain(game.Calls, c => c.StartsWith("squad", StringComparison.Ordinal) || c == "end run" || c == "open gate");
        Assert.Contains("start", game.Calls); // not told it's in the campaign
        Assert.Contains($"{ProgressApplyPolicy.RunBoundParts} skipped: this game's campaign holds no run right now", game.Logs.Single().Notes);
        Assert.Equal(1u, inbox.AppliedThrough);
        Assert.True(ledger.TryMarkOnce(ProgressApplier.OnceSquad, RunA));
        Assert.False(applier.HeldThisSession(RunA));
    }

    [Fact]
    public void Unlock_checks_wait_for_stats_tracking_with_nothing_applied_then_are_skipped()
    {
        var (applier, game, inbox, ledger) = Make(Batch(1, RunA, Goal, Check));
        ledger.SetRun(RunA, new[] { 1, 1, 1, 1, 1 });
        game.Held = RunB;
        game.Kept.Add(RunA);
        game.Quest = new[] { 7, 7, 7, 7, 7 };
        game.Tracking = false;

        Assert.Equal(0, applier.ApplyWaiting(0, 8));
        Assert.Equal(new[] { "put kept run-a", "write quest 1,1,1,1,1", "open gate", "close gate", "write quest 7,7,7,7,7", "held run back" },
            game.Calls.SkipWhile(c => !c.StartsWith("put kept", StringComparison.Ordinal)));
        Assert.Equal(0u, inbox.AppliedThrough);
        Assert.StartsWith("this game doesn't track stats right now", applier.WaitWhy, StringComparison.Ordinal);
        Assert.True(ledger.TryGetRun(RunA, out int[] counts));
        Assert.Equal(new[] { 1, 1, 1, 1, 1 }, counts); // a batch that waits changed none of them

        game.Calls.Clear();
        Assert.Equal(1, applier.ApplyWaiting(ProgressApplyPolicy.GateWaitMicros, 8));
        Assert.DoesNotContain("apply UnlockPassEntry", game.Calls);
        Assert.Contains("apply StatEntry", game.Calls);
        Assert.Contains("unlock checks skipped: this game still doesn't track stats (training)", game.Logs.Last().Notes);
    }

    [Fact]
    public void The_difficulty_set_for_the_checks_is_said_and_put_back()
    {
        var (applier, game, _, _) = Make(Batch(1, RunA, Check));
        game.Held = RunA;
        game.AlignedTo = 3;
        applier.ApplyWaiting(0, 8);
        Assert.Contains("difficulty set to the run's (3) for the unlock checks", game.Logs.Single().Notes);
        Assert.True(game.Calls.IndexOf("close gate") > game.Calls.IndexOf("apply UnlockPassEntry"));
    }

    [Fact]
    public void One_entry_that_fails_doesnt_stop_the_rest()
    {
        var (applier, game, inbox, _) = Make(Batch(1, RunA, Goal, Check, Goal));
        game.Held = RunA;
        game.OnApply = e =>
        {
            if (e is UnlockPassEntry)
            {
                throw new InvalidOperationException("boom");
            }
        };
        applier.ApplyWaiting(0, 8);
        Assert.Equal(2, game.Calls.Count(c => c == "apply StatEntry"));
        Assert.Contains("UnlockPassEntry failed: boom", game.Logs.Single().Notes);
        Assert.Equal(1u, inbox.AppliedThrough);
        Assert.Equal(0, applier.FailedBatches);
    }

    [Fact]
    public void A_batch_that_fails_outside_its_entries_counts_as_applied_and_puts_back()
    {
        var quest = new QuestEntry(QuestStep.RunWon, 0);
        var (applier, game, inbox, _) = Make(Batch(1, RunA, Goal, quest), Batch(2, RunA, Goal, quest));
        game.Held = RunB;
        game.Kept.Add(RunA);
        game.FailDerived = true;
        Assert.Equal(2, applier.ApplyWaiting(0, 8));
        Assert.Equal(2u, inbox.AppliedThrough);
        Assert.Equal(2, applier.FailedBatches);
        Assert.Equal(2, game.Calls.Count(c => c == "held run back"));
        Assert.Contains(game.Lines, l => l.Level == ProgressLogLevel.Error && l.Text.Contains("failed outside its entries", StringComparison.Ordinal));
        Assert.Contains("the batch failed as a whole (InvalidOperationException: derived)", game.Logs[0].Notes);
    }

    [Fact]
    public void A_put_back_that_fails_is_logged_once_and_the_others_still_run()
    {
        var (applier, game, _, _) = Make(Batch(1, RunA, Check), Batch(2, RunA, Check));
        game.Held = RunB;
        game.Kept.Add(RunA);
        game.FailStop = true;
        applier.ApplyWaiting(0, 8);
        Assert.Equal(2, game.Calls.Count(c => c == "close gate"));
        Assert.Equal(2, game.Calls.Count(c => c == "held run back"));
        Assert.Single(game.Lines, l => l.Text.StartsWith("Progress: couldn't put the game's campaign flag back", StringComparison.Ordinal));
    }

    [Fact]
    public void A_runs_squad_record_and_bench_snapshots_count_once_whatever_brings_them_again()
    {
        var (applier, game, _, ledger) = Make(Batch(1, RunA, Win, new RunEndingEntry()), Batch(2, RunA, Win, new RunEndingEntry()));
        game.Held = RunA;
        applier.ApplyWaiting(0, 8);
        Assert.Equal(1, game.Calls.Count(c => c == "squad gauntlet"));
        Assert.Equal(1, game.Calls.Count(c => c == "end run"));
        Assert.Contains("squad win gauntlet not recorded again: this run's result is already on this profile", game.Logs[1].Notes);
        Assert.Contains("the run's end for the benchwarmers not applied again: this run's bench snapshots are already on this profile", game.Logs[1].Notes);

        // A rejoin (a new applier, the same ledger) brings it again: still once.
        var inbox = new ProgressInbox(new ProgressLedger());
        inbox.OfferOwn(new ProgressMessage(Session + 1, 1, Batch(1, RunA, Win, new RunEndingEntry())));
        var again = new ProgressApplier(game, inbox, ledger, "\"Host\"");
        game.Calls.Clear();
        again.ApplyWaiting(0, 8);
        Assert.DoesNotContain(game.Calls, c => c.StartsWith("squad", StringComparison.Ordinal) || c == "end run");
    }

    [Fact]
    public void A_squad_record_for_another_squad_isnt_recorded_or_marked()
    {
        var (applier, game, _, ledger) = Make(Batch(1, RunA, Win));
        game.Held = RunA;
        game.Mismatch = "this PC's run is lines on difficulty 1";
        applier.ApplyWaiting(0, 8);
        Assert.DoesNotContain("squad gauntlet", game.Calls);
        Assert.Contains("squad win gauntlet not recorded: this PC's run is lines on difficulty 1", game.Logs.Single().Notes);
        Assert.True(ledger.TryMarkOnce(ProgressApplier.OnceSquad, RunA));
    }

    [Fact]
    public void A_run_history_entry_is_saved_once_for_a_run_held_here_and_holds_back_the_next_batches()
    {
        var history = new RunHistoryRecord(new RunHistoryData { RunId = RunA, SquadId = "gauntlet", IsWin = true });
        var (applier, game, inbox, ledger) = Make(Batch(1, RunA, history), Batch(2, RunA, Goal));
        game.Held = RunA;
        applier.Held(RunA); // as the game side does when it sees the host's run held here
        Assert.Equal(1, applier.ApplyWaiting(0, 8));
        Assert.Contains("save history run-a", game.Calls);
        Assert.Equal(1u, inbox.AppliedThrough); // batch 2 waits for the save in flight
        Assert.False(ledger.TryMarkOnce(ProgressApplier.OnceHistory, RunA));

        game.HistorySaving = false;
        Assert.Equal(1, applier.ApplyWaiting(0, 8));
    }

    [Fact]
    public void A_run_history_entry_for_a_run_this_pc_never_held_isnt_saved()
    {
        var history = new RunHistoryRecord(new RunHistoryData { RunId = RunA, SquadId = "gauntlet" });
        var (applier, game, _, _) = Make(Batch(1, RunA, history));
        game.Held = RunB;
        applier.ApplyWaiting(0, 8);
        Assert.DoesNotContain(game.Calls, c => c.StartsWith("save history", StringComparison.Ordinal));
        Assert.Contains("the run history entry not saved: this PC never held that run", game.Logs.Single().Notes);
    }

    [Fact]
    public void Marks_apply_on_the_profile_without_a_run_and_only_known_kinds_of_line()
    {
        var (applier, game, _, _) = Make(Batch(1, string.Empty, ProfileMarkEntry.NurseOffered(2), ProfileMarkEntry.DialogueSeen("chaosNode/visit_2"),
            ProfileMarkEntry.DialogueSeen("tip:first")));
        applier.ApplyWaiting(0, 8);
        Assert.Equal(new[] { "mark Nurse procedure 2 offered", "mark dialogue chaosNode/visit_2 seen" }, game.Calls.Where(c => c.StartsWith("mark", StringComparison.Ordinal)));
        // An honest host sends only event NPC lines (letters, digits and '_', '-', '.', '/'): any other id reads as its length.
        Assert.Contains("dialogue a key of 9 characters not marked seen: it isn't an event NPC's line", game.Logs.Single().Notes);
        Assert.Contains("start", game.Calls); // not told it's in the campaign: no run
    }

    [Fact]
    public void The_first_batch_that_waits_stops_the_frame()
    {
        var (applier, game, inbox, _) = Make(Batch(1, string.Empty, Goal), Batch(2, RunB, Win), Batch(3, string.Empty, Goal));
        game.View = RunB; // the host is on run-b, which this PC is about to load
        Assert.Equal(1, applier.ApplyWaiting(0, 8));
        Assert.Equal(1u, inbox.AppliedThrough);
        Assert.Equal("the host's run it's for isn't loaded here yet", applier.WaitWhy);
        Assert.Single(game.Lines, l => l.Level == ProgressLogLevel.Trace);
        applier.ApplyWaiting(1, 8);
        Assert.Single(game.Lines, l => l.Level == ProgressLogLevel.Trace); // the same wait isn't logged again

        game.Held = RunB;
        Assert.Equal(2, applier.ApplyWaiting(2, 8));
        Assert.Equal(-1, applier.WaitSince);
    }

    [Fact]
    public void With_sharing_off_batches_wait_and_it_is_said_once()
    {
        var (applier, game, inbox, _) = Make(Batch(1, string.Empty, Goal));
        game.Sharing = false;
        applier.ApplyWaiting(0, 8);
        applier.ApplyWaiting(1, 8);
        Assert.Equal(0u, inbox.AppliedThrough);
        Assert.Single(game.Lines, l => l.Level == ProgressLogLevel.Warn && l.Text.Contains("ShareProgress is off", StringComparison.Ordinal));
        Assert.Equal("ShareProgress is off on this PC", applier.WaitWhy);
    }

    [Fact]
    public void As_the_session_ends_the_hosts_match_no_longer_holds_a_runs_end_back()
    {
        var (applier, game, inbox, _) = Make(Batch(1, RunA, Win, new RunEndingEntry()));
        game.Held = RunA;
        game.Block = ApplyBlock.HostMatchHere;
        Assert.Equal(0, applier.ApplyWaiting(0, 8));
        Assert.Equal(1, applier.Flush(0));
        Assert.True(applier.Ending);
        Assert.Equal(1u, inbox.AppliedThrough);
        Assert.Contains("end run", game.Calls);
    }

    [Fact]
    public void At_the_runs_end_its_last_match_still_loaded_here_no_longer_holds_its_progress_back()
    {
        // The host's run-end screens keep the run's last match loaded (client mode on here): its unlock checks, and on a won
        // run the squad record sealed before them, apply before this player's new-unlocks screen.
        var (applier, game, inbox, _) = Make(Batch(1, RunA, Goal, Check), Batch(2, RunA, Win, new QuestEntry(QuestStep.RunWon, 0)), Batch(3, RunA, Check));
        game.Held = RunA;
        game.Block = ApplyBlock.PlayingHostMatch;
        Assert.Equal(0, applier.ApplyWaiting(0, 8));
        Assert.Equal("this PC is playing the host's match", applier.WaitWhy);

        game.HostRunOver = true;
        Assert.Equal(3, applier.ApplyWaiting(1, 8));
        Assert.Equal(3u, inbox.AppliedThrough);
        Assert.False(applier.Ending);
        Assert.Contains("squad gauntlet", game.Calls);

        // Not this game's own saves not having loaded, though.
        var (later, other, otherInbox, _) = Make(Batch(1, RunA, Goal));
        other.Held = RunA;
        other.Block = ApplyBlock.NotLoaded;
        other.HostRunOver = true;
        Assert.Equal(0, later.ApplyWaiting(0, 8));
        Assert.Equal(0u, otherInbox.AppliedThrough);
    }

    [Fact]
    public void As_the_session_ends_a_runs_end_right_after_its_history_entry_still_applies()
    {
        // The host seals the entry at once, and its HandleRunEnding comes after it: the next batch. The game's save of the entry
        // goes on over frames, and there are no more frames.
        var history = new RunHistoryRecord(new RunHistoryData { RunId = RunA, SquadId = "gauntlet", IsWin = true });
        var (applier, game, inbox, ledger) = Make(Batch(1, RunA, history), Batch(2, RunA, new RunEndingEntry()), Batch(3, string.Empty, Goal));
        game.Held = RunA;
        applier.Held(RunA);

        Assert.Equal(3, applier.Flush(0));
        Assert.Equal(3u, inbox.AppliedThrough);
        Assert.Contains("save history run-a", game.Calls);
        Assert.Contains("end run", game.Calls);
        Assert.False(ledger.TryMarkOnce(ProgressApplier.OnceEnding, RunA));
    }

    [Fact]
    public void As_the_session_ends_a_second_history_entry_waits_for_the_first_and_isnt_marked()
    {
        var first = new RunHistoryRecord(new RunHistoryData { RunId = RunA, SquadId = "gauntlet" });
        var second = new RunHistoryRecord(new RunHistoryData { RunId = RunB, SquadId = "gauntlet" });
        var (applier, game, inbox, ledger) = Make(Batch(1, RunA, first), Batch(2, RunB, second), Batch(3, string.Empty, Goal));
        applier.Held(RunA);
        applier.Held(RunB);

        Assert.Equal(1, applier.Flush(0));
        Assert.Equal(1u, inbox.AppliedThrough);
        Assert.Equal("a run history entry is still saving", applier.WaitWhy);
        Assert.True(ledger.TryMarkOnce(ProgressApplier.OnceHistory, RunB)); // a rejoin brings it again, and it's saved then
    }

    [Fact]
    public void As_the_session_ends_a_gap_isnt_waited_out_and_what_came_after_it_applies()
    {
        var (applier, game, inbox, ledger) = Make(Batch(1, string.Empty, Goal), Batch(4, string.Empty, Goal));
        Assert.Equal(1, applier.ApplyWaiting(0, 8)); // 4 waits for 2 and 3, which may still come
        Assert.Equal(1u, inbox.AppliedThrough);

        Assert.Equal(1, applier.Flush(1));
        Assert.Equal(4u, inbox.AppliedThrough);
        Assert.True(ledger.IsMissing(Session, 2) && ledger.IsMissing(Session, 3)); // they still apply if a rejoin brings them
        Assert.Contains(game.Lines, l => l.Level == ProgressLogLevel.Warn && l.Text.Contains("2 of \"Host\"'s batches before batch 4 hadn't come as the session ended", StringComparison.Ordinal));
    }

    [Fact]
    public void A_frame_with_nothing_to_apply_allocates_nothing()
    {
        // It runs every frame of a friend's session, with a hosting session known and nothing waiting.
        var (applier, _, inbox, _) = Make(Batch(1, string.Empty, Goal));
        Assert.Equal(1, applier.ApplyWaiting(0, 8));
        applier.ApplyWaiting(1, 8);
        inbox.TryRequest(1, out _, out _);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int frame = 0; frame < 1000; frame++)
        {
            applier.ApplyWaiting(2 + frame, 8);
            inbox.TryRequest(2 + frame, out _, out _);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void An_error_outside_any_batch_is_logged_once_and_tried_again()
    {
        var (applier, game, inbox, _) = Make(Batch(1, string.Empty, Goal));
        game.FailBefore = true;
        applier.ApplyWaiting(0, 8);
        applier.ApplyWaiting(1, 8);
        Assert.Single(game.Lines, l => l.Text.StartsWith("Progress: applying \"Host\"'s progress failed (InvalidOperationException", StringComparison.Ordinal));
        game.FailBefore = false;
        Assert.Equal(1, applier.ApplyWaiting(2, 8));
        Assert.Equal(1u, inbox.AppliedThrough);
    }

    [Fact]
    public void A_line_that_cant_be_written_is_said_and_the_rest_are_written()
    {
        var (applier, game, _, _) = Make(Batch(1, string.Empty, Goal), Batch(2, string.Empty, Goal));
        game.FailReportOf = 1;
        Assert.Equal(2, applier.ApplyWaiting(0, 8));
        Assert.Equal(new[] { "2: saved (with batches 1 to 2)" }, game.Reported);
        Assert.Contains((ProgressLogLevel.Warn, "Progress: batch 1 from \"Host\" applied (saved), but its line couldn't be written (InvalidOperationException)."), game.Lines);
    }

    /// <summary>A friend's game as the applier sees it, every call recorded in order.</summary>
    private sealed class FakeGame : IProgressTarget
    {
        public List<string> Calls { get; } = new();

        public List<FakeLog> Logs { get; } = new();

        public List<string> Reported { get; } = new();

        public List<(ProgressLogLevel Level, string Text)> Lines { get; } = new();

        public HashSet<string> Kept { get; } = new(StringComparer.Ordinal);

        public string Held { get; set; } = string.Empty;

        public string Current => Swapped ? SwappedRun : Held;

        public string View { get; set; } = string.Empty;

        public bool Sharing { get; set; } = true;

        public ApplyBlock Block { get; set; }

        public bool HostRunOver { get; set; }

        public bool KeptGone { get; set; }

        public int[]? Quest { get; set; } = new int[ProgressLedger.RunCounters];

        public bool Tracking { get; set; } = true;

        public int AlignedTo { get; set; } = -1;

        public string? Mismatch { get; set; }

        public Action<ProgressEntry>? OnApply { get; set; }

        public bool FailDerived { get; set; }

        public bool FailStop { get; set; }

        public bool FailBefore { get; set; }

        public uint FailReportOf { get; set; }

        public string LiveSharedRun { get; set; } = string.Empty;

        public bool HistorySaving { get; set; }

        public bool NoRunInState { get; set; }

        public bool HoldsRun => !NoRunInState && Current.Length > 0;

        private bool Swapped { get; set; }

        private string SwappedRun { get; set; } = string.Empty;

        public ApplyFacts Facts(bool ending) => new(Sharing, Block, ending, Held, View, Kept.ToList(), HostRunOver: HostRunOver);

        public void BeforeBatches(long nowMicros)
        {
            if (FailBefore)
            {
                throw new InvalidOperationException("before");
            }
        }

        public void BeforeBatch()
        {
        }

        public void WantRunCopy(ProgressBatch batch) => Calls.Add($"want copy {batch.Run}");

        public IProgressLog StartLog(ProgressBatch batch)
        {
            var log = new FakeLog(batch.Sequence);
            Logs.Add(log);
            return log;
        }

        public bool PutKeptRun(string run)
        {
            Calls.Add($"put kept {run}");
            if (KeptGone || !Kept.Contains(run))
            {
                return false;
            }

            Swapped = true;
            SwappedRun = run;
            return true;
        }

        public void PutHeldRunBack()
        {
            Calls.Add("held run back");
            Swapped = false;
            SwappedRun = string.Empty;
        }

        public bool Keeps(string run) => Kept.Contains(run);

        public int[]? ReadQuestCounts() => Quest?.ToArray();

        public void WriteQuestCounts(IReadOnlyList<int> counts)
        {
            Calls.Add($"write quest {string.Join(',', counts)}");
            Quest = counts.ToArray();
        }

        public ApplyAbilities Abilities() => new(true, true, true, true);

        public StatsGate OpenStatsGate()
        {
            Calls.Add("open gate");
            return new StatsGate(Tracking, "training", AlignedTo);
        }

        public void CloseStatsGate() => Calls.Add("close gate");

        public void StartApplying(bool inRun) => Calls.Add(inRun ? "start in run" : "start");

        public void StopApplying()
        {
            Calls.Add("stop");
            if (FailStop)
            {
                throw new InvalidOperationException("flag");
            }
        }

        public void Apply(ProgressEntry entry, IProgressLog log)
        {
            Calls.Add($"apply {entry.GetType().Name}");
            OnApply?.Invoke(entry);
        }

        public string? SquadMismatch(SquadEntry squad) => Mismatch;

        public void RecordSquad(SquadEntry squad, IProgressLog log) => Calls.Add($"squad {squad.Squad}");

        public void EndRun(IProgressLog log) => Calls.Add("end run");

        public string? HistoryUnavailable() => null;

        public void SaveHistory(RunHistoryRecord history, long nowMicros, IProgressLog log)
        {
            Calls.Add($"save history {history.Entry.RunId}");
            HistorySaving = true;
        }

        public void Mark(ProfileMarkEntry mark, IProgressLog log) => Calls.Add($"mark {mark.Describe()}");

        public void WorkOutDerived(IProgressLog log)
        {
            Calls.Add("derived");
            if (FailDerived)
            {
                throw new InvalidOperationException("derived");
            }
        }

        public void LedgerChanged() => Calls.Add("ledger");

        public string SaveAfter(IReadOnlyList<IProgressLog> applied)
        {
            Calls.Add($"save {applied.Count}");
            return "saved";
        }

        public void Report(IProgressLog log, string saved)
        {
            Calls.Add($"report {log.Sequence}");
            if (log.Sequence == FailReportOf)
            {
                throw new InvalidOperationException("report");
            }

            Reported.Add($"{log.Sequence}: {saved}");
        }

        public void Log(ProgressLogLevel level, string line, Exception? exception = null) => Lines.Add((level, line));
    }

    /// <summary>A batch's line, kept as its notes and parts.</summary>
    private sealed class FakeLog : IProgressLog
    {
        public FakeLog(uint sequence) => Sequence = sequence;

        public uint Sequence { get; }

        public List<string> Notes { get; } = new();

        public List<string> Parts { get; } = new();

        public void Note(string text) => Notes.Add(text);

        public void Part(string text) => Parts.Add(text);

        public void Failed(ProgressEntry entry, Exception exception) => Notes.Add($"{entry.GetType().Name} failed: {exception.Message}");
    }
}
