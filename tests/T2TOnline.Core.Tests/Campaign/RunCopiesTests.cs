using System;
using System.Collections.Generic;
using System.Linq;
using T2TOnline.Core.Campaign;
using T2TOnline.Core.Serialization;

namespace T2TOnline.Core.Tests.Campaign;

/// <summary>
/// A friend whose game restarted after holding a shared run gets a copy of it from the host, so that run's
/// end (squad record, unlock checks, quest steps, bench snapshots) still applies on its profile.
/// </summary>
public class RunCopiesTests
{
    private const ulong Session = 0xabc;
    private const string RunA = "run-a";
    private const string RunB = "run-b";
    private static readonly SquadEntry Win = new(SquadOutcome.Won, "gauntlet", 2, 1800f);

    private static RunCheckpoint Checkpoint(uint sequence, string run = RunA) => new(sequence, new Dictionary<RunCheckpointPart, byte[]>
    {
        [RunCheckpointPart.Run] = System.Text.Encoding.UTF8.GetBytes($"{{\"runData\":{{\"value\":{{\"runID\":\"{run}\",\"squadId\":\"gauntlet\"}}}},\"n\":{sequence}}}"),
        [RunCheckpointPart.RunStats] = new byte[] { 1, 2, 3 },
    });

    private static ApplyFacts Facts(IRunCopies? copies, string held = "", string[]? kept = null, bool ending = false, bool linkUp = true) =>
        new(true, ApplyBlock.None, ending, held, string.Empty, kept ?? Array.Empty<string>(), copies, linkUp);

    private const long Second = 1_000_000;

    [Fact]
    public void The_host_keeps_the_newest_checkpoint_of_its_newest_three_runs()
    {
        var store = new RunCopyStore();
        Assert.True(store.Keep(RunA, Checkpoint(1)));
        Assert.True(store.Keep(RunA, Checkpoint(2)));
        Assert.False(store.Keep(RunA, Checkpoint(2))); // the same one again
        Assert.True(store.TryGet(RunA, out RunCheckpoint? a));
        Assert.Equal(2u, a!.Sequence);

        store.Keep(RunB, Checkpoint(3, RunB));
        store.Keep("run-c", Checkpoint(4, "run-c"));
        store.Keep(RunA, Checkpoint(5)); // run-a played again (a later session of the same co-op run): newest again
        store.Keep("run-d", Checkpoint(6, "run-d"));
        Assert.Equal(new[] { "run-c", RunA, "run-d" }, store.Runs);
        Assert.False(store.TryGet(RunB, out _));
        Assert.False(store.Keep("not a run", Checkpoint(7)));

        store.Clear();
        Assert.Empty(store.Runs);
        Assert.False(store.TryGet(RunA, out _));
    }

    [Fact]
    public void The_host_answers_a_friend_about_a_run_at_most_every_ten_seconds()
    {
        var store = new RunCopyStore();
        store.Keep(RunA, Checkpoint(1));
        store.Keep(RunB, Checkpoint(2));
        Assert.True(store.TryAnswer(1, RunA, 0));
        Assert.False(store.TryAnswer(1, RunA, RunCopyStore.AnswerGapMicros - 1));
        Assert.True(store.TryAnswer(2, RunA, 1)); // another friend
        Assert.True(store.TryAnswer(1, RunB, 1)); // another run
        Assert.True(store.TryAnswer(1, RunA, RunCopyStore.AnswerGapMicros));
        for (ulong friend = 10; friend < 200; friend++)
        {
            Assert.True(store.TryAnswer(friend, RunA, 5)); // each friend's first: answered, and the memory of answers stays small
        }

        Assert.False(store.TryAnswer(199, RunA, 6)); // the newest answers still pace their friend
        Assert.True(store.TryAnswer(199, RunA, 5 + RunCopyStore.AnswerGapMicros));
    }

    [Fact]
    public void Run_ids_nobody_keeps_cant_wash_a_copys_pacing_out_or_fill_the_log()
    {
        // A crafted friend asking about a stream of made-up runs: a copy is up to 500 KB, deflated on the host's game thread.
        var store = new RunCopyStore();
        store.Keep(RunA, Checkpoint(1));
        Assert.True(store.TryAnswer(1, RunA, 0));
        Assert.True(store.TryAnswer(1, "made-up-0", 1)); // the first "not kept" goes
        for (int i = 1; i < 500; i++)
        {
            Assert.False(store.TryAnswer(1, $"made-up-{i}", 2 + i), "one \"not kept\" per friend every 10 s");
            Assert.False(store.TryAnswer(1, RunA, 2 + i), "and no second copy");
        }

        Assert.True(store.TryAnswer(2, "made-up-1", 3)); // another friend's own
        Assert.True(store.TryAnswer(1, "made-up-9", RunCopyStore.AnswerGapMicros + 1));
        Assert.True(store.TryAnswer(1, RunA, RunCopyStore.AnswerGapMicros + 1));
    }

    [Fact]
    public void A_copy_being_loaded_isnt_given_up_on_and_another_hosting_session_starts_again()
    {
        var batch = new ProgressBatch(5, 3, RunA, new ProgressEntry[] { Win });
        var requests = new RunCopyRequests(_ => true);
        var policy = new ProgressApplyPolicy();
        Assert.Equal(BatchVerdict.Wait, policy.Decide(batch, Facts(requests), 0).Verdict);
        requests.Want(RunA);
        Assert.True(requests.TryAsk(0, out _, out _, out _));
        requests.Answered(RunA);

        // The host's answer came late and the loader has its own 20 s limit: the batch still gets its squad record.
        BatchDecision waiting = policy.Decide(batch, Facts(requests), ProgressApplyPolicy.CopyWaitMicros + 5_000_000);
        Assert.Equal((BatchVerdict.Wait, RunA), (waiting.Verdict, waiting.CopyFor));

        // Its load failed: that's what ends the wait.
        requests.Failed(RunA, "the game's loader made no run of it");
        Assert.Equal(BatchVerdict.Apply, policy.Decide(batch, Facts(requests), ProgressApplyPolicy.CopyWaitMicros + 6_000_000).Verdict);

        // A rejoin into another hosting session: what the last host said (or didn't) says nothing of this one's.
        requests.Forget();
        Assert.Equal(RunCopyState.None, requests.StateOf(RunA));
        Assert.True(requests.Want(RunA));
    }

    [Fact]
    public void Nothing_is_asked_for_as_the_session_ends()
    {
        var requests = new RunCopyRequests(_ => true);
        var game = new CopyGame(requests);
        var ledger = new ProgressLedger();
        var inbox = new ProgressInbox(ledger);
        var batch = new ProgressBatch(1, 3, RunA, new ProgressEntry[] { Win });
        Assert.Equal(ProgressOffer.Accepted, inbox.OfferOwn(new ProgressMessage(Session, 1, batch)));
        var applier = new ProgressApplier(game, inbox, ledger, "the host");
        Assert.Equal(0, applier.Flush(0));
        Assert.Empty(game.Wanted);
        Assert.Equal(1, inbox.Held);
    }

    [Fact]
    public void A_friend_asks_at_once_again_after_ten_seconds_and_gives_up_after_three()
    {
        var requests = new RunCopyRequests(run => run == RunA);
        Assert.True(requests.HeldBefore(RunA));
        Assert.False(requests.HeldBefore(RunB));
        Assert.False(requests.HeldBefore("not a run"));
        Assert.Equal(RunCopyState.None, requests.StateOf(RunA));
        Assert.True(requests.Want(RunA));
        Assert.False(requests.Want(RunA)); // once
        Assert.Equal(RunCopyState.Wanted, requests.StateOf(RunA));

        Assert.True(requests.TryAsk(0, out string run, out int attempt, out string gaveUp));
        Assert.Equal((RunA, 1, string.Empty), (run, attempt, gaveUp));
        Assert.True(requests.Expecting(RunA));
        Assert.False(requests.TryAsk(RunCopyRequests.AskAgainMicros - 1, out _, out _, out _));
        Assert.True(requests.TryAsk(RunCopyRequests.AskAgainMicros, out _, out attempt, out _));
        Assert.Equal(2, attempt);
        Assert.True(requests.TryAsk(2 * RunCopyRequests.AskAgainMicros, out _, out attempt, out _));
        Assert.Equal(3, attempt);
        Assert.False(requests.TryAsk(3 * RunCopyRequests.AskAgainMicros, out _, out _, out gaveUp));
        Assert.Equal(RunA, gaveUp);
        Assert.Equal(RunCopyState.Failed, requests.StateOf(RunA));
        Assert.Contains("didn't answer 3 requests", requests.Why(RunA), StringComparison.Ordinal);
        Assert.False(requests.TryAsk(10 * RunCopyRequests.AskAgainMicros, out _, out _, out gaveUp));
        Assert.Empty(gaveUp);
        Assert.Equal(3, requests.Asked);
    }

    [Fact]
    public void A_friend_takes_only_a_copy_it_waits_for_and_follows_its_load()
    {
        var requests = new RunCopyRequests(_ => true);
        Assert.False(requests.Expecting(RunA)); // never asked
        requests.Want(RunA);
        Assert.False(requests.Expecting(RunA)); // wanted, not asked yet
        requests.TryAsk(0, out _, out _, out _);
        Assert.True(requests.Expecting(RunA));
        requests.Answered(RunA);
        Assert.Equal(RunCopyState.Loading, requests.StateOf(RunA));
        Assert.False(requests.Expecting(RunA)); // a second answer is ignored
        Assert.False(requests.TryAsk(RunCopyRequests.AskAgainMicros * 5, out _, out _, out _)); // and not asked again while loading
        requests.Loaded(RunA);
        Assert.Equal(RunCopyState.Loaded, requests.StateOf(RunA));

        requests.Want(RunB);
        requests.Refused(RunB, "not kept");
        Assert.Equal((RunCopyState.Refused, "not kept"), (requests.StateOf(RunB), requests.Why(RunB)));
        requests.Failed("unknown-run", "x"); // nothing to say about a run never wanted
        Assert.Equal(RunCopyState.None, requests.StateOf("unknown-run"));
    }

    [Fact]
    public void A_host_cant_make_a_friend_follow_many_runs()
    {
        var requests = new RunCopyRequests(_ => true);
        for (int i = 0; i < RunCopyRequests.MaxRuns; i++)
        {
            Assert.True(requests.Want($"run-{i}"));
        }

        Assert.False(requests.Want("run-more"));
        Assert.False(requests.Want("not a run"));
        int asked = 0;
        while (requests.TryAsk(0, out _, out _, out _))
        {
            asked++;
        }

        Assert.Equal(RunCopyRequests.MaxRuns, asked);
    }

    [Fact]
    public void A_runs_end_waits_for_a_copy_only_for_a_run_this_pc_held_before()
    {
        var policy = new ProgressApplyPolicy();
        var batch = new ProgressBatch(5, 3, RunA, new ProgressEntry[] { Win });
        var requests = new RunCopyRequests(run => run == RunA);

        BatchDecision decision = policy.Decide(batch, Facts(requests), 0);
        Assert.Equal((BatchVerdict.Wait, RunA), (decision.Verdict, decision.CopyFor));
        Assert.Contains("getting a copy of the host's run", decision.Why, StringComparison.Ordinal);

        // Never held here (a late joiner, or no ledger entry): no copy, applied without its run-bound parts, as before.
        var other = new ProgressBatch(6, 3, RunB, new ProgressEntry[] { Win });
        BatchDecision late = new ProgressApplyPolicy().Decide(other, Facts(requests), 0);
        Assert.Equal((BatchVerdict.Apply, RunContext.None, string.Empty), (late.Verdict, late.Context, late.CopyFor));
        Assert.Contains("no copy of it", late.Skipped, StringComparison.Ordinal);

        // No copies to ask (null): as before.
        Assert.Equal(BatchVerdict.Apply, new ProgressApplyPolicy().Decide(batch, Facts(null), 0).Verdict);

        // A batch with only stats or marks, or only comics, never waits for a run.
        var stats = new ProgressBatch(7, 3, RunA, new ProgressEntry[] { new StatEntry(9, StatOp.Increment, StatValue.None), new ComicsEntry() });
        Assert.Equal(BatchVerdict.Apply, new ProgressApplyPolicy().Decide(stats, Facts(requests), 0).Verdict);
    }

    [Fact]
    public void Once_the_copy_is_kept_the_runs_end_applies_on_it()
    {
        var policy = new ProgressApplyPolicy();
        var batch = new ProgressBatch(5, 3, RunA, new ProgressEntry[] { Win, new RunEndingEntry() });
        var requests = new RunCopyRequests(_ => true);
        Assert.Equal(BatchVerdict.Wait, policy.Decide(batch, Facts(requests), 0).Verdict);
        requests.Want(RunA);
        requests.TryAsk(0, out _, out _, out _);
        requests.Answered(RunA);
        Assert.Equal(BatchVerdict.Wait, policy.Decide(batch, Facts(requests), 1_000_000).Verdict);
        requests.Loaded(RunA);
        Assert.Equal(BatchDecision.Apply(RunContext.Kept), policy.Decide(batch, Facts(requests, kept: new[] { RunA }), 2_000_000));
    }

    [Fact]
    public void A_copy_refused_failed_or_too_late_lets_the_batch_apply_without_its_run_bound_parts()
    {
        var batch = new ProgressBatch(5, 3, RunA, new ProgressEntry[] { Win });

        var refused = new RunCopyRequests(_ => true);
        refused.Want(RunA);
        refused.Refused(RunA, "the host doesn't keep it any more");
        BatchDecision a = new ProgressApplyPolicy().Decide(batch, Facts(refused), 0);
        Assert.Equal((BatchVerdict.Apply, RunContext.None), (a.Verdict, a.Context));
        Assert.Contains("the host doesn't keep it any more", a.Skipped, StringComparison.Ordinal);

        var failed = new RunCopyRequests(_ => true);
        failed.Want(RunA);
        failed.Failed(RunA, "the game's loader made no run of it");
        Assert.Contains("the game's loader made no run of it", new ProgressApplyPolicy().Decide(batch, Facts(failed), 0).Skipped, StringComparison.Ordinal);

        var policy = new ProgressApplyPolicy();
        var slow = new RunCopyRequests(_ => true);
        Assert.Equal(BatchVerdict.Wait, policy.Decide(batch, Facts(slow), 0).Verdict);
        Assert.Equal(BatchVerdict.Wait, policy.Decide(batch, Facts(slow), ProgressApplyPolicy.CopyWaitMicros - 1).Verdict);
        BatchDecision late = policy.Decide(batch, Facts(slow), ProgressApplyPolicy.CopyWaitMicros);
        Assert.Equal((BatchVerdict.Apply, RunContext.None), (late.Verdict, late.Context));
        Assert.Contains("no copy of the host's run came within 30 s", late.Skipped, StringComparison.Ordinal);

        // The wait starts again for the next run that needs a copy.
        var next = new ProgressBatch(6, 3, RunB, new ProgressEntry[] { Win });
        Assert.Equal(BatchVerdict.Wait, policy.Decide(next, Facts(slow), ProgressApplyPolicy.CopyWaitMicros + 1).Verdict);
    }

    [Fact]
    public void As_the_session_ends_a_batch_waiting_for_a_copy_is_left_for_a_rejoin()
    {
        var batch = new ProgressBatch(5, 3, RunA, new ProgressEntry[] { Win });
        var requests = new RunCopyRequests(_ => true);
        BatchDecision decision = new ProgressApplyPolicy().Decide(batch, Facts(requests, ending: true), 0);
        Assert.Equal((BatchVerdict.Wait, RunA), (decision.Verdict, decision.CopyFor));
        Assert.Contains("a rejoin of this hosting session brings the batch again", decision.Why, StringComparison.Ordinal);

        // One refused already applies without them, session end or not.
        requests.Want(RunA);
        requests.Refused(RunA, "not kept");
        Assert.Equal(BatchVerdict.Apply, new ProgressApplyPolicy().Decide(batch, Facts(requests, ending: true), 0).Verdict);
    }

    [Fact]
    public void The_messages_round_trip()
    {
        var request = new ProgressRunRequest(Session, RunA);
        var writer = new BitWriter();
        ProgressRunRequestMessage.Write(writer, request);
        var reader = new BitReader(writer.ToArray());
        Assert.True(ProgressRunRequestMessage.TryRead(ref reader, out ProgressRunRequest read));
        Assert.Equal(request, read);

        foreach (ProgressRunCopy copy in new[]
        {
            new ProgressRunCopy(Session, RunA, RunCopyStatus.Copy, Checkpoint(9)),
            new ProgressRunCopy(Session, RunB, RunCopyStatus.NotKept, null),
        })
        {
            writer = new BitWriter();
            Assert.True(ProgressRunCopyMessage.Write(writer, copy));
            reader = new BitReader(writer.ToArray());
            Assert.True(ProgressRunCopyMessage.TryRead(ref reader, out ProgressRunCopy? got));
            Assert.Equal((copy.Session, copy.Run, copy.Status), (got!.Session, got.Run, got.Status));
            Assert.Equal(copy.Checkpoint?.Hash, got.Checkpoint?.Hash);
            Assert.Equal(copy.Checkpoint?.Sequence, got.Checkpoint?.Sequence);
            Assert.Equal(copy.Checkpoint?.Get(RunCheckpointPart.Run), got.Checkpoint?.Get(RunCheckpointPart.Run));
        }

        // A copy without its checkpoint isn't written.
        Assert.False(ProgressRunCopyMessage.Write(new BitWriter(), new ProgressRunCopy(Session, RunA, RunCopyStatus.Copy, null)));
    }

    [Fact]
    public void Bad_messages_are_refused()
    {
        static bool ReadRequest(Action<BitWriter> write)
        {
            var w = new BitWriter();
            write(w);
            var r = new BitReader(w.ToArray());
            return ProgressRunRequestMessage.TryRead(ref r, out _);
        }

        static bool ReadCopy(Action<BitWriter> write)
        {
            var w = new BitWriter();
            write(w);
            var r = new BitReader(w.ToArray());
            return ProgressRunCopyMessage.TryRead(ref r, out _);
        }

        Assert.True(ReadRequest(w => { w.WriteByte(1); w.WriteUInt64(Session); w.WriteShortString(RunA); }));
        Assert.False(ReadRequest(w => { w.WriteByte(2); w.WriteUInt64(Session); w.WriteShortString(RunA); })); // another format
        Assert.False(ReadRequest(w => { w.WriteByte(1); w.WriteUInt64(Session); w.WriteShortString("a run"); })); // not an id
        Assert.False(ReadRequest(w => { w.WriteByte(1); w.WriteUInt64(Session); w.WriteShortString(string.Empty); }));
        Assert.False(ReadRequest(w => { w.WriteByte(1); w.WriteUInt64(Session); })); // truncated

        Assert.True(ReadCopy(w => { w.WriteByte(1); w.WriteUInt64(Session); w.WriteShortString(RunA); w.WriteByte((byte)RunCopyStatus.NotKept); }));
        Assert.False(ReadCopy(w => { w.WriteByte(1); w.WriteUInt64(Session); w.WriteShortString(RunA); w.WriteByte(0); })); // no status
        Assert.False(ReadCopy(w => { w.WriteByte(1); w.WriteUInt64(Session); w.WriteShortString(RunA); w.WriteByte(3); })); // unknown status
        Assert.False(ReadCopy(w => { w.WriteByte(1); w.WriteUInt64(Session); w.WriteShortString(RunA); w.WriteByte((byte)RunCopyStatus.Copy); })); // no checkpoint
        Assert.False(ReadCopy(w =>
        {
            // A checkpoint with run stats only: no run to load.
            w.WriteByte(1);
            w.WriteUInt64(Session);
            w.WriteShortString(RunA);
            w.WriteByte((byte)RunCopyStatus.Copy);
            RunCheckpointMessage.Write(w, new RunCheckpoint(3, new Dictionary<RunCheckpointPart, byte[]> { [RunCheckpointPart.RunStats] = new byte[] { 1 } }));
        }));

        // A changed byte anywhere: false, or something within the limits.
        var good = new BitWriter();
        ProgressRunCopyMessage.Write(good, new ProgressRunCopy(Session, RunA, RunCopyStatus.Copy, Checkpoint(9)));
        byte[] bytes = good.ToArray();
        for (int i = 0; i < bytes.Length; i++)
        {
            byte[] copy = (byte[])bytes.Clone();
            copy[i] ^= 0x5A;
            var r = new BitReader(copy);
            if (ProgressRunCopyMessage.TryRead(ref r, out ProgressRunCopy? got))
            {
                Assert.True(ProgressLimits.IsPlausibleId(got!.Run));
                Assert.True(got.Status == RunCopyStatus.NotKept || got.Checkpoint?.Get(RunCheckpointPart.Run) != null);
            }
        }

        for (int length = 0; length < bytes.Length - 1; length++)
        {
            var r = new BitReader(bytes.AsSpan(0, length).ToArray());
            Assert.False(ProgressRunCopyMessage.TryRead(ref r, out _), $"read {length} of {bytes.Length} bytes");
        }

        var random = new Random(116);
        for (int i = 0; i < 3000; i++)
        {
            byte[] junk = new byte[random.Next(0, 200)];
            random.NextBytes(junk);
            if (i % 2 == 0)
            {
                Array.Copy(bytes, junk, Math.Min(random.Next(1, 30), Math.Min(bytes.Length, junk.Length)));
            }

            var r1 = new BitReader(junk);
            ProgressRunCopyMessage.TryRead(ref r1, out _);
            var r2 = new BitReader(junk);
            ProgressRunRequestMessage.TryRead(ref r2, out _);
        }
    }

    [Fact]
    public void The_applier_asks_for_the_copy_a_batch_waits_for()
    {
        var ledger = new ProgressLedger();
        ledger.SetRun(RunA, new[] { 1, 2, 3, 4, 5 }); // held before the restart
        var inbox = new ProgressInbox(ledger);
        inbox.OfferOwn(new ProgressMessage(Session, 1, new ProgressBatch(1, 3, RunA, new ProgressEntry[] { Win })));
        var requests = new RunCopyRequests(run => ledger.TryGetRun(run, out _));
        var game = new CopyGame(requests);
        var applier = new ProgressApplier(game, inbox, ledger, "\"Host\"");

        Assert.Equal(0, applier.ApplyWaiting(0, 8));
        Assert.Equal(new[] { 1u }, game.Wanted);
        Assert.Equal(RunCopyState.Wanted, requests.StateOf(RunA));
        applier.ApplyWaiting(1, 8);
        Assert.Equal(new[] { 1u, 1u }, game.Wanted); // every frame it waits (asked once: Want is once per run)

        // The copy comes and is kept: the run's end applies on it, with that run's quest counts from the ledger.
        requests.TryAsk(1, out _, out _, out _);
        requests.Answered(RunA);
        requests.Loaded(RunA);
        game.Kept.Add(RunA);
        Assert.Equal(1, applier.ApplyWaiting(2, 8));
        Assert.Equal(new[] { "put kept run-a", "write quest 1,2,3,4,5", "squad gauntlet" }, game.Calls.Take(3));
    }

    [Fact]
    public void While_the_link_is_down_nothing_is_asked_and_no_request_ages()
    {
        // A drop right after a restart's rejoin spent the three requests and the 30 s on a host nobody could hear.
        var requests = new RunCopyRequests(_ => true);
        requests.Want(RunA);
        requests.Tick(0, linkUp: true);
        Assert.True(requests.TryAsk(0, out _, out int attempt, out _));
        Assert.Equal(1, attempt);

        requests.Tick(5 * Second, linkUp: false);
        Assert.False(requests.LinkUp);
        Assert.False(requests.TryAsk(5 * Second, out _, out _, out _)); // not asked, so not counted
        requests.Tick(100 * Second, linkUp: false);
        Assert.False(requests.TryAsk(100 * Second, out _, out _, out string gaveUp));
        Assert.Empty(gaveUp);
        Assert.Equal(RunCopyState.Asked, requests.StateOf(RunA));

        // Back: the ask's 10 s counted only the 5 s before the drop, and 5 s after it.
        requests.Tick(105 * Second, linkUp: true);
        Assert.False(requests.TryAsk(105 * Second, out _, out _, out _));
        requests.Tick(110 * Second, linkUp: true);
        Assert.True(requests.TryAsk(110 * Second, out _, out attempt, out _));
        Assert.Equal(2, attempt);
        Assert.Equal(2, requests.Asked);
    }

    [Fact]
    public void A_wait_for_a_copy_counts_only_the_time_the_link_is_up()
    {
        var policy = new ProgressApplyPolicy();
        var batch = new ProgressBatch(5, 3, RunA, new ProgressEntry[] { Win });
        var requests = new RunCopyRequests(_ => true);
        Assert.Equal(BatchVerdict.Wait, policy.Decide(batch, Facts(requests), 0).Verdict);
        Assert.Equal(BatchVerdict.Wait, policy.Decide(batch, Facts(requests), 20 * Second).Verdict);

        // A drop of 200 s, frame by frame: the wait is paused, not over.
        for (long t = 21; t <= 220; t++)
        {
            BatchDecision paused = policy.Decide(batch, Facts(requests, linkUp: false), t * Second);
            Assert.Equal((BatchVerdict.Wait, RunA), (paused.Verdict, paused.CopyFor));
            Assert.Contains("link to the host is down", paused.Why, StringComparison.Ordinal);
        }

        // Up again: 20 s were waited, 10 s are left.
        Assert.Equal(BatchVerdict.Wait, policy.Decide(batch, Facts(requests), 221 * Second).Verdict);
        Assert.Equal(BatchVerdict.Wait, policy.Decide(batch, Facts(requests), 229 * Second).Verdict);
        BatchDecision over = policy.Decide(batch, Facts(requests), 231 * Second);
        Assert.Equal((BatchVerdict.Apply, RunContext.None), (over.Verdict, over.Context));
        Assert.Contains("within 30 s of the link being up", over.Skipped, StringComparison.Ordinal);
    }

    [Fact]
    public void A_link_that_restarts_asks_again_and_gives_the_wait_a_fresh_start()
    {
        var policy = new ProgressApplyPolicy();
        var batch = new ProgressBatch(5, 3, RunA, new ProgressEntry[] { Win });
        var requests = new RunCopyRequests(_ => true);
        requests.Want(RunA);
        requests.Tick(0, true);
        requests.TryAsk(0, out _, out _, out _);
        requests.Tick(10 * Second, true);
        requests.TryAsk(10 * Second, out _, out _, out _);
        Assert.Equal(RunCopyState.Asked, requests.StateOf(RunA));
        Assert.Equal(BatchVerdict.Wait, policy.Decide(batch, Facts(requests), 0).Verdict);
        Assert.Equal(BatchVerdict.Wait, policy.Decide(batch, Facts(requests), 25 * Second).Verdict);

        // The host dropped this friend meanwhile (its asks went to nobody) and let it back in at 26 s.
        requests.Tick(26 * Second, true);
        Assert.Equal(1, requests.LinkRestarted());
        Assert.Equal(1, requests.Restarts);
        Assert.Equal(RunCopyState.Wanted, requests.StateOf(RunA));
        Assert.True(requests.TryAsk(26 * Second, out _, out int attempt, out _));
        Assert.Equal(1, attempt); // counted afresh: three more, not one
        Assert.Equal(BatchVerdict.Wait, policy.Decide(batch, Facts(requests), 26 * Second).Verdict);
        Assert.Equal(BatchVerdict.Wait, policy.Decide(batch, Facts(requests), 55 * Second).Verdict); // 29 s of the new wait
        Assert.Equal(BatchVerdict.Apply, policy.Decide(batch, Facts(requests), 56 * Second).Verdict);

        // A copy being loaded, kept or refused stands through a restart.
        var loading = new RunCopyRequests(_ => true);
        loading.Want(RunA);
        loading.TryAsk(0, out _, out _, out _);
        loading.Answered(RunA);
        Assert.Equal(0, loading.LinkRestarted());
        Assert.Equal(RunCopyState.Loading, loading.StateOf(RunA));
    }

    [Fact]
    public void A_copy_that_really_cant_come_still_ends_the_wait_on_a_healthy_link()
    {
        var requests = new RunCopyRequests(_ => true);
        requests.Want(RunA);
        string gaveUp = string.Empty;
        for (long t = 0; t <= 31 && gaveUp.Length == 0; t++)
        {
            requests.Tick(t * Second, true);
            requests.TryAsk(t * Second, out _, out _, out gaveUp);
        }

        Assert.Equal(RunA, gaveUp);
        Assert.Equal(3, requests.Asked);
        var batch = new ProgressBatch(5, 3, RunA, new ProgressEntry[] { Win });
        Assert.Equal(BatchVerdict.Apply, new ProgressApplyPolicy().Decide(batch, Facts(requests), 31 * Second).Verdict);
    }

    [Fact]
    public void A_drop_through_the_wait_keeps_the_squad_record_for_the_copy_that_comes_after()
    {
        var ledger = new ProgressLedger();
        ledger.SetRun(RunA, new[] { 1, 2, 3, 4, 5 }); // held before the restart
        var inbox = new ProgressInbox(ledger);
        inbox.OfferOwn(new ProgressMessage(Session, 1, new ProgressBatch(1, 3, RunA, new ProgressEntry[] { Win, new RunEndingEntry() })));
        var requests = new RunCopyRequests(run => ledger.TryGetRun(run, out _));
        var game = new CopyGame(requests) { LinkUp = false };
        var applier = new ProgressApplier(game, inbox, ledger, "\"Host\"");

        // The rejoin's first minute: the link is down the whole 90 s.
        for (long t = 0; t < 90; t++)
        {
            requests.Tick(t * Second, game.LinkUp);
            Assert.Equal(0, applier.ApplyWaiting(t * Second, 8));
            Assert.False(requests.TryAsk(t * Second, out _, out _, out _));
        }

        Assert.Equal(1, inbox.Held);
        Assert.Equal(0, requests.Asked);
        Assert.Equal(RunCopyState.Wanted, requests.StateOf(RunA));

        // Back at 90 s: asked, answered, loaded, kept: the run's end applies on it, the squad record once.
        game.LinkUp = true;
        requests.Tick(90 * Second, true);
        Assert.Equal(0, applier.ApplyWaiting(90 * Second, 8));
        Assert.True(requests.TryAsk(90 * Second, out string run, out _, out _));
        Assert.Equal(RunA, run);
        requests.Answered(RunA);
        requests.Loaded(RunA);
        game.Kept.Add(RunA);
        Assert.Equal(1, applier.ApplyWaiting(91 * Second, 8));
        Assert.Contains("squad gauntlet", game.Calls);
        Assert.Contains("end run", game.Calls);
        Assert.Equal(0, inbox.Held);
        Assert.False(ledger.TryMarkOnce(ProgressApplier.OnceSquad, RunA)); // recorded: never again
        Assert.False(ledger.TryMarkOnce(ProgressApplier.OnceEnding, RunA));
    }

    [Fact]
    public void A_session_that_ends_in_a_drop_leaves_the_batch_whole_for_the_next_rejoin()
    {
        var ledger = new ProgressLedger();
        ledger.SetRun(RunA, new[] { 1, 2, 3, 4, 5 });
        var batch = new ProgressBatch(1, 3, RunA, new ProgressEntry[] { Win, new RunEndingEntry() });

        // First session: the link drops and the session ends before any copy came.
        var first = new ProgressInbox(ledger);
        first.OfferOwn(new ProgressMessage(Session, 1, batch));
        var requests = new RunCopyRequests(run => ledger.TryGetRun(run, out _));
        var game = new CopyGame(requests) { LinkUp = false };
        var applier = new ProgressApplier(game, first, ledger, "\"Host\"");
        Assert.Equal(0, applier.ApplyWaiting(0, 8));
        Assert.Equal(0, applier.Flush(1 * Second)); // the session's end: nothing it waits for can come
        Assert.Equal(1, first.Held);
        Assert.DoesNotContain("squad gauntlet", game.Calls);

        // The rejoin: the host sends it again; this time a copy comes and the whole batch applies, once.
        var second = new ProgressInbox(ledger);
        Assert.Equal(ProgressOffer.Accepted, second.OfferOwn(new ProgressMessage(Session, 1, batch)));
        var again = new RunCopyRequests(run => ledger.TryGetRun(run, out _));
        var rejoined = new CopyGame(again);
        var next = new ProgressApplier(rejoined, second, ledger, "\"Host\"");
        Assert.Equal(0, next.ApplyWaiting(2 * Second, 8));
        again.Tick(2 * Second, true);
        Assert.True(again.TryAsk(2 * Second, out _, out _, out _));
        again.Answered(RunA);
        again.Loaded(RunA);
        rejoined.Kept.Add(RunA);
        Assert.Equal(1, next.ApplyWaiting(3 * Second, 8));
        Assert.Single(rejoined.Calls, c => c == "squad gauntlet");

        // And a third offer of the same batch (a resend) applies nothing: no double count.
        var third = new ProgressInbox(ledger);
        Assert.Equal(ProgressOffer.Duplicate, third.OfferOwn(new ProgressMessage(Session, 1, batch)));
    }

    /// <summary>Just enough of a friend's game for a batch that waits for, then gets, a copy of its run.</summary>
    private sealed class CopyGame : IProgressTarget
    {
        private readonly RunCopyRequests _requests;
        private bool _swapped;

        public CopyGame(RunCopyRequests requests) => _requests = requests;

        public List<uint> Wanted { get; } = new();

        public List<string> Calls { get; } = new();

        public HashSet<string> Kept { get; } = new(StringComparer.Ordinal);

        public string LiveSharedRun => string.Empty;

        public bool HoldsRun => _swapped;

        public bool HistorySaving => false;

        /// <summary>The link to the host (<see cref="ApplyFacts.LinkUp"/>).</summary>
        public bool LinkUp { get; set; } = true;

        public ApplyFacts Facts(bool ending) => new(true, ApplyBlock.None, ending, string.Empty, string.Empty, Kept.ToList(), _requests, LinkUp);

        public void BeforeBatches(long nowMicros)
        {
        }

        public void BeforeBatch()
        {
        }

        public void WantRunCopy(ProgressBatch batch)
        {
            Wanted.Add(batch.Sequence);
            _requests.Want(batch.Run);
        }

        public IProgressLog StartLog(ProgressBatch batch) => new QuietLog(batch.Sequence);

        public bool PutKeptRun(string run)
        {
            Calls.Add($"put kept {run}");
            _swapped = Kept.Contains(run);
            return _swapped;
        }

        public void PutHeldRunBack() => _swapped = false;

        public bool Keeps(string run) => Kept.Contains(run);

        public int[]? ReadQuestCounts() => new int[ProgressLedger.RunCounters];

        public void WriteQuestCounts(IReadOnlyList<int> counts) => Calls.Add($"write quest {string.Join(',', counts)}");

        public ApplyAbilities Abilities() => new(true, true, true, true);

        public StatsGate OpenStatsGate() => new(true, string.Empty, -1);

        public void CloseStatsGate()
        {
        }

        public void StartApplying(bool inRun)
        {
        }

        public void StopApplying()
        {
        }

        public void Apply(ProgressEntry entry, IProgressLog log) => Calls.Add($"apply {entry.GetType().Name}");

        public string? SquadMismatch(SquadEntry squad) => null;

        public void RecordSquad(SquadEntry squad, IProgressLog log) => Calls.Add($"squad {squad.Squad}");

        public void EndRun(IProgressLog log) => Calls.Add("end run");

        public string? HistoryUnavailable() => null;

        public void SaveHistory(RunHistoryRecord history, long nowMicros, IProgressLog log)
        {
        }

        public void Mark(ProfileMarkEntry mark, IProgressLog log)
        {
        }

        public void WorkOutDerived(IProgressLog log)
        {
        }

        public void LedgerChanged()
        {
        }

        public string SaveAfter(IReadOnlyList<IProgressLog> applied) => "saved";

        public void Report(IProgressLog log, string saved)
        {
        }

        public void Log(ProgressLogLevel level, string line, Exception? exception = null)
        {
        }

        private sealed class QuietLog : IProgressLog
        {
            public QuietLog(uint sequence) => Sequence = sequence;

            public uint Sequence { get; }

            public void Note(string text)
            {
            }

            public void Part(string text)
            {
            }

            public void Failed(ProgressEntry entry, Exception exception)
            {
            }
        }
    }
}
