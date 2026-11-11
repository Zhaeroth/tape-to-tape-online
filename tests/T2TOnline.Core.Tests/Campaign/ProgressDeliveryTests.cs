using System;
using System.Collections.Generic;
using System.Linq;
using T2TOnline.Core.Campaign;
using T2TOnline.Core.Serialization;

namespace T2TOnline.Core.Tests.Campaign;

public class ProgressDeliveryTests
{
    private const ulong Session = 0xfeed_beef_0000_0001;
    private const ulong Alex = 76561199000000001;
    private const ulong Sam = 76561199000000002;
    private static readonly StatEntry Goal = new(9, StatOp.IncrementCampaign, StatValue.None);

    private static ProgressBatch Batch(uint sequence, string run = "run-1", params ProgressEntry[] entries) =>
        new(sequence, 0, run, entries.Length == 0 ? new ProgressEntry[] { Goal } : entries);

    private static ProgressMessage Message(ProgressBatch batch, uint entitledFrom = 1, ulong session = Session) => new(session, entitledFrom, batch);

    // ------------------------------------------------------------------ batcher

    [Fact]
    public void The_window_is_sealed_at_a_safe_point_in_order()
    {
        var batcher = new ProgressBatcher();
        Assert.True(batcher.Add(Goal, "run-1", 0));
        Assert.True(batcher.Add(new UnlockPassEntry(4, 1, true, Array.Empty<string>()), "run-1", 10));
        Assert.False(batcher.TryTake(20, live: false, out _)); // not quiet for long enough
        batcher.Seal("stats saved");
        Assert.True(batcher.TryTake(20, live: false, out SealedProgress batch));
        Assert.Equal("stats saved", batch.Reason);
        Assert.Equal("run-1", batch.Run);
        Assert.IsType<StatEntry>(batch.Entries[0]);
        Assert.IsType<UnlockPassEntry>(batch.Entries[1]);
        Assert.Equal(0, batcher.Pending);
        batcher.Seal("nothing to seal");
        Assert.False(batcher.TryTake(30, live: false, out _));
    }

    [Fact]
    public void A_quiet_window_is_sealed_but_never_during_a_live_match()
    {
        var batcher = new ProgressBatcher();
        batcher.Add(Goal, "run-1", 1_000);
        Assert.False(batcher.TryTake(1_000 + ProgressBatcher.IdleMicros, live: true, out _));
        Assert.False(batcher.TryTake(1_000 + ProgressBatcher.IdleMicros - 1, live: false, out _));
        Assert.True(batcher.TryTake(1_000 + ProgressBatcher.IdleMicros, live: false, out SealedProgress batch));
        Assert.Equal("a quiet moment", batch.Reason);
    }

    [Fact]
    public void A_new_run_and_a_full_window_start_new_batches()
    {
        var batcher = new ProgressBatcher();
        batcher.Add(Goal, "run-1", 0);
        batcher.Add(Goal, "run-2", 0); // the run changed: run-1's credit goes on its own
        for (int i = 1; i < ProgressBatchMessage.MaxEntries; i++)
        {
            batcher.Add(Goal, "run-2", 0);
        }

        batcher.Add(Goal, "run-2", 0); // one past full
        batcher.Seal("end");
        var batches = new List<SealedProgress>();
        while (batcher.TryTake(0, live: true, out SealedProgress batch))
        {
            batches.Add(batch);
        }

        Assert.Equal(new[] { 1, ProgressBatchMessage.MaxEntries, 1 }, batches.Select(b => b.Entries.Count));
        Assert.Equal(new[] { "run-1", "run-2", "run-2" }, batches.Select(b => b.Run));
        Assert.Equal(new[] { "the run changed", "the batch was full", "end" }, batches.Select(b => b.Reason));
    }

    [Fact]
    public void An_unlock_check_repeating_the_last_with_nothing_between_is_dropped()
    {
        var batcher = new ProgressBatcher();
        var check = new UnlockPassEntry(4, 1, true, Array.Empty<string>());
        batcher.Add(check, "run-1", 0);
        batcher.Add(check, "run-1", 1); // nothing between: dropped
        batcher.Add(new UnlockPassEntry(0, 1, true, Array.Empty<string>()), "run-1", 2); // another timing: kept
        batcher.Add(Goal, "run-1", 3);
        batcher.Add(check, "run-1", 4); // after a stat change: kept
        batcher.Add(new UnlockPassEntry(4, 1, true, new[] { "BeanDiet" }), "run-1", 5); // earned something: kept
        batcher.Add(new UnlockPassEntry(4, 1, true, new[] { "BeanDiet" }), "run-1", 6); // and again: kept (it says what was earned)
        batcher.Seal("end");
        Assert.True(batcher.TryTake(10, live: false, out SealedProgress batch));
        Assert.Equal(6, batch.Entries.Count);
        Assert.Equal(1, batcher.Repeats);
    }

    [Fact]
    public void Credit_beyond_a_batchs_cap_for_one_stat_goes_in_the_next_batch()
    {
        var batcher = new ProgressBatcher();
        var big = new StatEntry(9, StatOp.IncrementCampaignBy, StatValue.Whole(ProgressLimits.MaxStatValue - 1));
        batcher.Add(big, "run-1", 0);
        batcher.Add(Goal, "run-1", 0); // exactly the cap: same batch
        batcher.Add(new StatEntry(9, StatOp.TrySetLargerCampaign, StatValue.Whole(ProgressLimits.MaxStatValue)), "run-1", 0); // a record adds nothing
        batcher.Add(new StatEntry(10, StatOp.IncrementCampaignBy, StatValue.Whole(5)), "run-1", 0); // another stat
        batcher.Add(Goal, "run-1", 0); // one past the cap: a new batch
        batcher.Seal("end");
        var batches = new List<SealedProgress>();
        while (batcher.TryTake(0, live: true, out SealedProgress batch))
        {
            batches.Add(batch);
        }

        Assert.Equal(new[] { 4, 1 }, batches.Select(b => b.Entries.Count));
        Assert.Equal(new[] { "a stat's credit filled the batch", "end" }, batches.Select(b => b.Reason));
        Assert.All(batches, b => Assert.True(ProgressLimits.IsWithinBatchLimits(b.Entries)));
        Assert.All(batches, b => _ = new ProgressBatch(1, 0, b.Run, b.Entries)); // each is a batch a friend reads
    }

    [Fact]
    public void A_run_history_entry_closes_its_batch_and_bad_entries_are_refused()
    {
        var batcher = new ProgressBatcher();
        batcher.Add(Goal, "run-1", 0);
        batcher.Add(new RunHistoryRecord(RunHistoryDataTests.Sample()), "run-1", 0);
        batcher.Add(Goal, "run-1", 0);
        Assert.True(batcher.TryTake(0, live: true, out SealedProgress first));
        Assert.Equal(2, first.Entries.Count);
        Assert.Equal(1, batcher.Pending);

        Assert.False(batcher.Add(new StatEntry(1, StatOp.Increment, StatValue.Whole(3)), "run-1", 0));
        Assert.False(batcher.Add(Goal, "bad run", 0));
        Assert.Equal(2, batcher.Refused);
        Assert.Equal(1, batcher.Pending);
    }

    // ------------------------------------------------------------------ outbox

    [Fact]
    public void A_friend_new_to_the_session_gets_only_what_comes_after_they_joined()
    {
        var outbox = new ProgressOutbox(Session);
        outbox.FriendJoined(Alex);
        outbox.Add(1, new byte[] { 1 });
        outbox.Add(2, new byte[] { 2 });
        outbox.FriendJoined(Sam);
        outbox.Add(3, new byte[] { 3 });

        Assert.Equal(new uint[] { 1, 2, 3 }, Drain(outbox, Alex).Select(s => s.Sequence));
        List<(uint Sequence, uint EntitledFrom)> sam = Drain(outbox, Sam);
        Assert.Equal(new uint[] { 3 }, sam.Select(s => s.Sequence));
        Assert.Equal(3u, sam[0].EntitledFrom);
        Assert.Empty(Drain(outbox, Alex)); // everything sent once
    }

    [Fact]
    public void A_friend_who_comes_back_gets_everything_since_they_first_joined_including_while_away()
    {
        var outbox = new ProgressOutbox(Session);
        outbox.Add(1, new byte[] { 1 }); // before anyone joined: nobody's
        outbox.FriendJoined(Alex);
        outbox.Add(2, new byte[] { 2 });
        Assert.Equal(new uint[] { 2 }, Drain(outbox, Alex).Select(s => s.Sequence));
        outbox.FriendLeft(Alex);
        outbox.Add(3, new byte[] { 3 }); // made while they were away
        Assert.Empty(Drain(outbox, Alex));
        Assert.True(outbox.AnyoneEntitled);

        outbox.FriendJoined(Alex);
        Assert.Equal(new uint[] { 2, 3 }, Drain(outbox, Alex).Select(s => s.Sequence));
        Assert.Equal(new[] { Alex }, outbox.Present);
    }

    [Fact]
    public void Batches_dropped_for_space_move_each_friends_start_on()
    {
        var outbox = new ProgressOutbox(Session);
        outbox.FriendJoined(Alex);
        byte[] big = new byte[(ProgressOutbox.MaxStoredBytes / 3) + 1]; // three don't fit
        for (uint i = 1; i <= 4; i++)
        {
            outbox.Add(i, big);
        }

        Assert.Equal(2, outbox.Dropped);
        List<(uint Sequence, uint EntitledFrom)> sent = Drain(outbox, Alex);
        Assert.Equal(new uint[] { 3, 4 }, sent.Select(s => s.Sequence));
        Assert.All(sent, s => Assert.Equal(3u, s.EntitledFrom));
        Assert.Throws<ArgumentException>(() => outbox.Add(9, big));
    }

    [Fact]
    public void The_outbox_says_whether_anything_waits_to_be_sent()
    {
        var outbox = new ProgressOutbox(Session);
        Assert.False(outbox.AnyToSend);
        outbox.FriendJoined(Alex);
        Assert.False(outbox.AnyToSend);
        outbox.Add(1, new byte[] { 1 });
        Assert.True(outbox.AnyToSend);
        Drain(outbox, Alex);
        Assert.False(outbox.AnyToSend);
        outbox.FriendLeft(Alex);
        outbox.Add(2, new byte[] { 2 });
        Assert.False(outbox.AnyToSend); // kept for them, but they aren't here
        outbox.FriendJoined(Alex);
        Assert.True(outbox.AnyToSend);
        Assert.Equal(new uint[] { 1, 2 }, Drain(outbox, Alex).Select(s => s.Sequence));
        Assert.False(outbox.AnyToSend);
    }

    [Fact]
    public void A_friend_has_only_so_many_ranges_waiting_to_be_sent_again()
    {
        var outbox = new ProgressOutbox(Session);
        outbox.FriendJoined(Alex);
        for (uint i = 1; i <= 40; i++)
        {
            outbox.Add(i, new byte[] { (byte)i });
        }

        Drain(outbox, Alex);

        // Ten requests, each for one batch with a gap to the next (no two merge), each taken (the host's pace allows it).
        for (uint i = 0; i < ProgressOutbox.MaxResendRanges + 2; i++)
        {
            Assert.True(outbox.Resend(Alex, 1 + (2 * i), 1 + (2 * i), i * ProgressOutbox.ResendGapMicros, out _, out _));
        }

        // The lowest eight are kept; the friend asks for the rest again.
        Assert.Equal(Enumerable.Range(0, ProgressOutbox.MaxResendRanges).Select(i => (uint)(1 + (2 * i))), Drain(outbox, Alex).Select(s => s.Sequence));
        Assert.Equal(8, ProgressOutbox.MaxResendRanges);
    }

    private static List<(uint Sequence, uint EntitledFrom)> Drain(ProgressOutbox outbox, ulong friend)
    {
        var sent = new List<(uint, uint)>();
        while (outbox.TryNext(friend, out uint sequence, out uint entitledFrom, out _))
        {
            outbox.MarkSent(friend, sequence);
            sent.Add((sequence, entitledFrom));
        }

        return sent;
    }

    // ------------------------------------------------------------------ inbox and ledger

    [Fact]
    public void Batches_apply_once_each_in_the_hosts_order()
    {
        var inbox = new ProgressInbox(new ProgressLedger());
        Assert.Equal(ProgressOffer.Accepted, inbox.OfferOwn(Message(Batch(2))));
        Assert.False(inbox.TryPeek(0, out _, out _)); // 1 is missing: wait for it
        Assert.Equal(ProgressOffer.Accepted, inbox.OfferOwn(Message(Batch(1))));
        Assert.Equal(new uint[] { 1, 2 }, Apply(inbox, 0));
        Assert.Equal(ProgressOffer.Duplicate, inbox.OfferOwn(Message(Batch(1))));
        Assert.Equal(ProgressOffer.Duplicate, inbox.OfferOwn(Message(Batch(2))));
        Assert.Equal(2, inbox.Duplicates);
        Assert.Equal(2u, inbox.AppliedThrough);
    }

    [Fact]
    public void Waiting_batches_can_be_asked_about()
    {
        var inbox = new ProgressInbox(new ProgressLedger());
        Assert.False(inbox.AnyWaiting(_ => true));
        inbox.OfferOwn(Message(Batch(1, "run-1", new SquadEntry(SquadOutcome.Won, "gauntlet", 2, 10f))));
        inbox.OfferOwn(Message(Batch(2, string.Empty)));
        Assert.True(inbox.AnyWaiting(b => b.Entries.Any(e => e is SquadEntry)));
        Apply(inbox, 0);
        Assert.False(inbox.AnyWaiting(_ => true));
    }

    [Fact]
    public void A_gap_is_waited_out_then_skipped()
    {
        var inbox = new ProgressInbox(new ProgressLedger());
        inbox.OfferOwn(Message(Batch(1)));
        Assert.Equal(new uint[] { 1 }, Apply(inbox, 0));
        inbox.OfferOwn(Message(Batch(4)));
        Assert.False(inbox.TryPeek(1_000, out _, out _));
        Assert.False(inbox.TryPeek(1_000 + ProgressInbox.GapGiveUpMicros - 1, out _, out _));
        Assert.True(inbox.TryPeek(1_000 + ProgressInbox.GapGiveUpMicros, out ProgressBatch? batch, out uint skipped));
        Assert.Equal(4u, batch!.Sequence);
        Assert.Equal(2u, skipped);
        inbox.Done(batch);
        Assert.Equal(4u, inbox.AppliedThrough);
        Assert.Equal(2, inbox.Skipped);

        // Given up on, not forgotten: if it comes after all (a rejoin sends everything again), it applies, once.
        Assert.Equal(ProgressOffer.Accepted, inbox.OfferOwn(Message(Batch(2))));
        Assert.Equal(new uint[] { 2 }, Apply(inbox, 0));
        Assert.Equal(ProgressOffer.Duplicate, inbox.OfferOwn(Message(Batch(2))));
        Assert.Equal(1, inbox.Late);
        Assert.Equal(4u, inbox.AppliedThrough);
    }

    [Fact]
    public void Batches_given_up_on_still_apply_after_a_rejoin_and_a_restart_and_only_once()
    {
        // The host makes five; batches 2 and 3 are lost on the wire (a broken Steam session); Alex's game gives up on
        // them, closes, restarts, and rejoins: the host sends everything since her first join again.
        var outbox = new ProgressOutbox(Session);
        outbox.FriendJoined(Alex);
        for (uint i = 1; i <= 5; i++)
        {
            outbox.Add(i, ProgressBatchMessage.EncodeBody(Batch(i))!);
        }

        var ledger = new ProgressLedger();
        var inbox = new ProgressInbox(ledger);
        List<byte[]> wire = Send(outbox, Alex);
        foreach (int i in new[] { 0, 3, 4 })
        {
            Receive(inbox, wire[i]);
        }

        var applied = new List<uint>(Apply(inbox, 0));
        Assert.True(inbox.Waiting(out uint from));
        Assert.Equal(2u, from);
        applied.AddRange(Apply(inbox, ProgressInbox.GapGiveUpMicros));
        Assert.Equal(new uint[] { 1, 4, 5 }, applied);
        Assert.False(inbox.Waiting(out _));
        Assert.True(ledger.IsMissing(Session, 2) && ledger.IsMissing(Session, 3));
        string saved = ledger.Format();

        outbox.FriendLeft(Alex);
        outbox.FriendJoined(Alex);
        var restarted = new ProgressInbox(ProgressLedger.Parse(saved));
        foreach (byte[] again in Send(outbox, Alex))
        {
            Receive(restarted, again);
        }

        Assert.Equal(new uint[] { 2, 3 }, Apply(restarted, 0));
        Assert.Equal(3, restarted.Duplicates);
        Assert.Equal(2, restarted.Late);
        Assert.Equal(5u, restarted.AppliedThrough);
    }

    [Fact]
    public void A_batch_refused_while_too_many_wait_still_applies_when_it_comes_again()
    {
        var ledger = new ProgressLedger();
        var inbox = new ProgressInbox(ledger);
        for (uint i = 1; i <= ProgressInbox.MaxHeld; i++)
        {
            Assert.Equal(ProgressOffer.Accepted, inbox.OfferOwn(Message(Batch(i))));
        }

        uint refused = ProgressInbox.MaxHeld + 1;
        Assert.Equal(ProgressOffer.Refused, inbox.OfferOwn(Message(Batch(refused))));
        Assert.Equal(ProgressInbox.MaxHeld, Apply(inbox, 0).Count);
        Assert.Equal(ProgressOffer.Accepted, inbox.OfferOwn(Message(Batch(refused + 1))));
        Assert.Empty(Apply(inbox, 0)); // the refused one is waited for first
        Assert.Equal(new[] { refused + 1 }, Apply(inbox, ProgressInbox.GapGiveUpMicros));
        Assert.Equal(1L, ledger.MissingCount(Session));

        Assert.Equal(ProgressOffer.Accepted, inbox.OfferOwn(Message(Batch(refused)))); // a rejoin brings it
        Assert.Equal(new[] { refused }, Apply(inbox, 0));
        Assert.Equal(0L, ledger.MissingCount(Session));
    }

    [Fact]
    public void The_ledger_keeps_missing_batches_as_ranges_and_round_trips_them()
    {
        var ledger = new ProgressLedger();
        ledger.SetApplied(Session, 20);
        ledger.AddMissing(Session, 5, 9);
        ledger.AddMissing(Session, 0, 3); // batch 0 doesn't exist: ignored
        ledger.AddMissing(Session, 8, 7); // backwards: ignored
        Assert.Equal(5L, ledger.MissingCount(Session));
        ledger.RemoveMissing(Session, 7);
        Assert.True(ledger.IsMissing(Session, 6) && ledger.IsMissing(Session, 8));
        Assert.False(ledger.IsMissing(Session, 7));
        ledger.RemoveMissing(Session, 5);
        ledger.RemoveMissing(Session, 9);
        Assert.Equal(2L, ledger.MissingCount(Session));
        Assert.False(ledger.IsMissing(Session + 1, 6));

        ProgressLedger read = ProgressLedger.Parse(ledger.Format().Replace("\n", "\r\n", StringComparison.Ordinal));
        Assert.True(read.IsMissing(Session, 6) && read.IsMissing(Session, 8));
        Assert.Equal(2L, read.MissingCount(Session));
        Assert.Equal(20u, read.AppliedThrough(Session));

        // A session forgotten for space takes its missing batches with it.
        for (ulong s = 1; s <= ProgressLedger.MaxSessions; s++)
        {
            read.SetApplied(Session + s, 1);
        }

        Assert.Equal(0L, read.MissingCount(Session));
    }

    [Fact]
    public void Late_batches_that_split_a_missing_range_dont_grow_the_ledger_past_its_cap()
    {
        // A host that numbers a batch far ahead leaves one long range missing; each late batch out of its middle splits it in two.
        var ledger = new ProgressLedger();
        ledger.SetApplied(Session, 10_000);
        ledger.AddMissing(Session, 1, 10_000);
        for (uint late = 2; late < 2_000; late += 2)
        {
            ledger.RemoveMissing(Session, late);
        }

        int ranges = ledger.Format().Split('\n').Count(line => line.StartsWith("missing ", StringComparison.Ordinal));
        Assert.Equal(ProgressLedger.MaxMissing, ranges);
        Assert.False(ledger.IsMissing(Session, 1_998)); // applied late
        Assert.True(ledger.IsMissing(Session, 1_999)); // the newest ranges are the ones kept
        Assert.True(ledger.IsMissing(Session, 9_999));
        Assert.False(ledger.IsMissing(Session, 1)); // the oldest went: it no longer applies if it comes
        Assert.Equal(ProgressLedger.MaxMissing, ProgressLedger.Parse(ledger.Format()).Format().Split('\n').Count(line => line.StartsWith("missing ", StringComparison.Ordinal)));
    }

    [Fact]
    public void A_gap_too_long_to_count_doesnt_wrap_the_count_of_batches_given_up_on()
    {
        var inbox = new ProgressInbox(new ProgressLedger());
        inbox.OfferOwn(Message(Batch(3_000_000_000)));
        Assert.False(inbox.TryPeek(0, out _, out _));
        Assert.True(inbox.TryPeek(ProgressInbox.GapGiveUpMicros, out ProgressBatch? batch, out uint skipped));
        Assert.Equal(2_999_999_999u, skipped);
        Assert.Equal(int.MaxValue, inbox.Skipped);
        inbox.Done(batch!);
        inbox.OfferOwn(Message(Batch(3_000_000_005)));
        Assert.False(inbox.TryPeek(ProgressInbox.GapGiveUpMicros, out _, out _));
        Assert.True(inbox.TryPeek(ProgressInbox.GapGiveUpMicros * 2, out _, out skipped));
        Assert.Equal(4u, skipped);
        Assert.Equal(int.MaxValue, inbox.Skipped);
    }

    [Fact]
    public void Batches_the_friend_wasnt_entitled_to_are_counted_not_waited_for()
    {
        var inbox = new ProgressInbox(new ProgressLedger());
        Assert.Equal(ProgressOffer.Accepted, inbox.OfferOwn(Message(Batch(9), entitledFrom: 9)));
        Assert.Equal(8L, inbox.NotEntitled);
        Assert.Equal(new uint[] { 9 }, Apply(inbox, 0));
    }

    // ------------------------------------------------------------------ the run's end when hosting stops

    [Fact]
    public void A_runs_end_recorded_after_the_last_stats_save_is_sealed_and_sent_when_hosting_stops()
    {
        // The match's last stats save seals its credit; then the run's end (squad win, quest steps, unlock checks) is
        // recorded while the match scene is still up, and the host stops hosting on its run-end screen.
        var batcher = new ProgressBatcher();
        var outbox = new ProgressOutbox(Session);
        outbox.FriendJoined(Alex);
        batcher.Add(Goal, "run-1", 0);
        batcher.Seal("stats saved");
        batcher.Add(new QuestEntry(QuestStep.RunWon, 0), "run-1", 10);
        batcher.Add(new SquadEntry(SquadOutcome.Won, "gauntlet", 3, 1234f), "run-1", 10);
        batcher.Add(new UnlockPassEntry(2, 1, true, Array.Empty<string>()), "run-1", 20);
        Publish(batcher, outbox, ProgressBatcher.IdleMicros * 10, live: true);
        Assert.Equal(1u, outbox.LastSequence); // a live match never seals by quiet

        // Hosting stops: what's left is sealed and published, and Alex (still here) gets it before the session closes.
        batcher.Seal("hosting stopped");
        Publish(batcher, outbox, long.MaxValue, live: false);
        var inbox = new ProgressInbox(new ProgressLedger());
        foreach (byte[] wire in Send(outbox, Alex))
        {
            Receive(inbox, wire);
        }

        Assert.True(inbox.TryPeek(0, out ProgressBatch? first, out _));
        inbox.Done(first!);
        Assert.True(inbox.TryPeek(0, out ProgressBatch? last, out _));
        Assert.Equal(2u, last!.Sequence);
        Assert.Contains(last.Entries, e => e is SquadEntry { Outcome: SquadOutcome.Won });
        Assert.Equal(3, last.Entries.Count);
    }

    [Fact]
    public void Once_the_match_is_over_a_quiet_moment_seals_the_runs_end()
    {
        // ProgressHost passes live = "a match is being played", false once its end-of-match steps ran.
        var batcher = new ProgressBatcher();
        batcher.Add(new SquadEntry(SquadOutcome.Won, "gauntlet", 3, 1234f), "run-1", 0);
        Assert.False(batcher.TryTake(ProgressBatcher.IdleMicros, live: true, out _));
        Assert.True(batcher.TryTake(ProgressBatcher.IdleMicros, live: false, out SealedProgress batch));
        Assert.Equal("a quiet moment", batch.Reason);
    }

    private static void Publish(ProgressBatcher batcher, ProgressOutbox outbox, long nowMicros, bool live)
    {
        while (batcher.TryTake(nowMicros, live, out SealedProgress sealedBatch))
        {
            var batch = new ProgressBatch(outbox.NextSequence, 0, sealedBatch.Run, sealedBatch.Entries);
            outbox.Add(batch.Sequence, ProgressBatchMessage.EncodeBody(batch)!);
        }
    }

    // ------------------------------------------------------------------ a session's credit to one stat

    [Fact]
    public void A_hosting_session_can_add_only_so_much_to_one_stat()
    {
        var credit = new ProgressCredit();
        var big = new StatEntry(9, StatOp.IncrementCampaignBy, StatValue.Whole(ProgressLimits.MaxStatValue));
        Assert.True(credit.TryGive(Session, big)); // exactly the cap
        Assert.False(credit.TryGive(Session, Goal)); // one more: refused
        Assert.True(credit.TryGive(Session, new StatEntry(10, StatOp.IncrementCampaign, StatValue.None))); // another stat
        Assert.True(credit.TryGive(Session, new StatEntry(9, StatOp.TrySetLargerCampaign, StatValue.Whole(ProgressLimits.MaxStatValue)))); // a record adds nothing
        Assert.Equal(1, credit.Refused);
        Assert.True(credit.TryGive(Session + 1, Goal)); // another hosting session starts from nothing
    }

    [Fact]
    public void A_hosting_session_can_take_only_so_much_off_one_stat()
    {
        var credit = new ProgressCredit();
        var back = new StatEntry(10, StatOp.IncrementBy, StatValue.Whole(-ProgressLimits.MaxStatDecrement));
        for (int i = 0; i < ProgressLimits.MaxStatDecrementPerSession / ProgressLimits.MaxStatDecrement; i++)
        {
            Assert.True(credit.TryGive(Session, back)); // up to the cap
        }

        Assert.False(credit.TryGive(Session, new StatEntry(10, StatOp.IncrementBy, StatValue.Whole(-1)))); // one more: refused
        Assert.True(credit.TryGive(Session, Goal)); // adding is counted apart
        Assert.True(credit.TryGive(Session, new StatEntry(11, StatOp.IncrementBy, StatValue.Whole(-1)))); // another stat
        Assert.Equal(1, credit.Refused);
        Assert.True(credit.TryGive(Session + 1, back)); // another hosting session starts from nothing
    }

    [Fact]
    public void Batch_after_batch_at_the_per_batch_cap_stops_at_the_session_cap()
    {
        var credit = new ProgressCredit();
        var full = new StatEntry(9, StatOp.IncrementBy, StatValue.Whole(ProgressLimits.MaxStatCreditPerBatch));
        int given = 0;
        for (int batch = 0; batch < 60; batch++)
        {
            given += credit.TryGive(Session, full) ? 1 : 0;
        }

        Assert.Equal(ProgressLimits.MaxStatCreditPerSession / ProgressLimits.MaxStatCreditPerBatch, given);
        Assert.Equal(60 - given, credit.Refused);
    }

    [Fact]
    public void However_much_play_a_ledger_holds_a_host_adds_at_most_the_days_ceiling_to_one_stat()
    {
        // A ledger file can hold any play (a damaged or edited one): the paces' bounds stop at the day's ceiling all the same.
        const string Host = "0123456789abcdef0123456789abcdef";
        var now = new DateTime(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc);
        long hour = now.Ticks / TimeSpan.TicksPerHour;
        ProgressLedger ledger = ProgressLedger.Parse($"play {Host} {hour} 9000000 100000 9000000 100000\n");
        PlayTally play = ledger.PlayWithin(Host, now);
        Assert.Equal(9_000_000, play.MatchSeconds);

        const int TimeOnIce = 59, Passes = 84;
        Assert.Equal(ProgressLimits.MaxStatCreditPerHostDay, StatBounds.CreditBound(TimeOnIce, play));
        Assert.Equal(ProgressLimits.MaxStatCreditPerHostDay, StatBounds.CreditBound(Passes, play));
        Assert.Equal(ProgressLimits.MaxStatCreditPerHostDay, ledger.RecordBound(Host, TimeOnIce, now));
        Assert.Equal(ProgressLimits.MaxStatCreditPerHostDay, StatBounds.EntityBound(EntityStatGroup.Skater, "shotsAttempted", play));
        Assert.Equal(ProgressLimits.MaxStatCreditPerHostDay, StatBounds.EntityGroupBound(EntityStatGroup.Skater, "shotsAttempted", play));
        Assert.Equal(200_000, ProgressLimits.MaxStatCreditPerHostDay);

        Assert.True(ledger.TrySpend(Host, TimeOnIce, ProgressLimits.MaxStatCreditPerHostDay, now)); // exactly the ceiling
        Assert.False(ledger.TrySpend(Host, TimeOnIce, 1, now)); // and not one more
        Assert.Equal(ProgressLimits.MaxStatCreditPerHostDay, ledger.BudgetUsed(Host, TimeOnIce, now).Added);
    }

    [Fact]
    public void A_friend_who_joined_mid_session_doesnt_wait_for_earlier_batches()
    {
        var inbox = new ProgressInbox(new ProgressLedger());
        Assert.Equal(ProgressOffer.Accepted, inbox.OfferOwn(Message(Batch(17), entitledFrom: 17)));
        Assert.Equal(new uint[] { 17 }, Apply(inbox, 0));
    }

    [Fact]
    public void A_new_hosting_session_counts_on_its_own()
    {
        var ledger = new ProgressLedger();
        var inbox = new ProgressInbox(ledger);
        inbox.OfferOwn(Message(Batch(1)));
        Apply(inbox, 0);
        Assert.Equal(ProgressOffer.Accepted, inbox.OfferOwn(Message(Batch(1), session: Session + 1)));
        Assert.Equal(new uint[] { 1 }, Apply(inbox, 0));
        Assert.Equal(1u, ledger.AppliedThrough(Session));
        Assert.Equal(1u, ledger.AppliedThrough(Session + 1));
    }

    [Fact]
    public void A_ledger_at_the_last_batch_number_never_wraps_back()
    {
        var ledger = new ProgressLedger();
        ledger.SetApplied(Session, uint.MaxValue);
        var inbox = new ProgressInbox(ledger);
        Assert.Equal(ProgressOffer.Duplicate, inbox.OfferOwn(Message(Batch(3), entitledFrom: 3)));
        Assert.Equal(uint.MaxValue, inbox.AppliedThrough);
        Assert.Empty(Apply(inbox, ProgressInbox.GapGiveUpMicros * 2));
        Assert.Equal(uint.MaxValue, ledger.AppliedThrough(Session));
    }

    [Fact]
    public void Too_many_waiting_out_of_order_are_refused()
    {
        var inbox = new ProgressInbox(new ProgressLedger());
        for (uint i = 0; i < ProgressInbox.MaxHeld; i++)
        {
            Assert.Equal(ProgressOffer.Accepted, inbox.OfferOwn(Message(Batch(i + 2))));
        }

        Assert.Equal(ProgressOffer.Refused, inbox.OfferOwn(Message(Batch(ProgressInbox.MaxHeld + 2))));
        Assert.Equal(ProgressInbox.MaxHeld, inbox.Held);
        inbox.Clear();
        Assert.Equal(0, inbox.Held);
    }

    [Fact]
    public void Nothing_applies_twice_across_a_rejoin_and_a_restart_of_the_friends_game()
    {
        // The host makes six batches; Alex drops after three (one of them on the wire when she dropped), her game
        // restarts, she rejoins, and the host sends every batch since her first join again.
        var outbox = new ProgressOutbox(Session);
        outbox.FriendJoined(Alex);
        for (uint i = 1; i <= 3; i++)
        {
            outbox.Add(i, ProgressBatchMessage.EncodeBody(Batch(i))!);
        }

        var applied = new List<uint>();
        var ledger = new ProgressLedger();
        var inbox = new ProgressInbox(ledger);
        foreach (byte[] wire in Send(outbox, Alex).Take(2))
        {
            Receive(inbox, wire);
        }

        applied.AddRange(Apply(inbox, 0));
        string saved = ledger.Format(); // the friend's game saved its ledger, then closed

        outbox.FriendLeft(Alex);
        for (uint i = 4; i <= 6; i++)
        {
            outbox.Add(i, ProgressBatchMessage.EncodeBody(Batch(i))!);
        }

        outbox.FriendJoined(Alex);
        var restarted = new ProgressInbox(ProgressLedger.Parse(saved));
        foreach (byte[] wire in Send(outbox, Alex))
        {
            Receive(restarted, wire);
        }

        applied.AddRange(Apply(restarted, 0));
        Assert.Equal(new uint[] { 1, 2, 3, 4, 5, 6 }, applied);
        Assert.Equal(2, restarted.Duplicates);
    }

    [Fact]
    public void The_ledger_round_trips_and_keeps_the_newest()
    {
        var ledger = new ProgressLedger();
        for (ulong s = 1; s <= ProgressLedger.MaxSessions + 2; s++)
        {
            ledger.SetApplied(s, (uint)s * 10);
        }

        ledger.SetRun("run-a", new[] { 1, 2, 3, 4, 5 });
        ledger.SetRun("run-a", new[] { 5, 4, 3, 2, -1 });
        for (int r = 0; r < ProgressLedger.MaxRuns; r++)
        {
            ledger.SetRun($"run-{r}", new[] { r, 0, 0, 0, 0 });
        }

        Assert.True(ledger.Changed);
        ProgressLedger read = ProgressLedger.Parse(ledger.Format());
        Assert.False(ledger.Changed);
        Assert.False(read.Changed);
        Assert.Equal(0u, read.AppliedThrough(1)); // the oldest two went
        Assert.Equal(0u, read.AppliedThrough(2));
        Assert.Equal(30u, read.AppliedThrough(3));
        Assert.Equal((ProgressLedger.MaxSessions + 2) * 10u, read.AppliedThrough(ProgressLedger.MaxSessions + 2));
        Assert.False(read.TryGetRun("run-a", out _)); // pushed out by newer runs
        Assert.True(read.TryGetRun("run-7", out int[] counters));
        Assert.Equal(new[] { 7, 0, 0, 0, 0 }, counters);
        Assert.Throws<ArgumentException>(() => ledger.SetRun("a run", new[] { 1, 2, 3, 4, 5 }));
        Assert.Throws<ArgumentException>(() => ledger.SetRun("run", new[] { 1 }));
    }

    [Fact]
    public void A_run_ends_once_on_a_profile_whatever_brings_it_again()
    {
        var ledger = new ProgressLedger();
        Assert.True(ledger.TryMarkOnce("squad", "run-a"));
        Assert.False(ledger.TryMarkOnce("squad", "run-a")); // the same run's result again: not twice
        Assert.True(ledger.TryMarkOnce("history", "run-a")); // another part of the same run
        Assert.True(ledger.TryMarkOnce("squad", "run-b")); // another run
        Assert.True(ledger.Changed);

        // A restart of the friend's game keeps what was applied.
        ProgressLedger read = ProgressLedger.Parse(ledger.Format());
        Assert.False(read.Changed);
        Assert.False(read.TryMarkOnce("squad", "run-a"));
        Assert.False(read.TryMarkOnce("history", "run-a"));
        Assert.False(read.TryMarkOnce("squad", "run-b"));

        // Bounded: the oldest go first.
        for (int r = 0; r < ProgressLedger.MaxOnce; r++)
        {
            Assert.True(read.TryMarkOnce("squad", $"run-{r}"));
        }

        Assert.True(read.TryMarkOnce("squad", "run-a"));
        Assert.Throws<ArgumentException>(() => ledger.TryMarkOnce("squad", "a run"));
        Assert.Throws<ArgumentException>(() => ledger.TryMarkOnce(string.Empty, "run-a"));
    }

    [Fact]
    public void A_runs_new_unlocks_are_kept_for_its_new_unlocks_screen_across_sessions_and_restarts()
    {
        // A co-op run's new unlocks on a friend's profile, from its first session (before a rejoin, or before the game restarted).
        var ledger = new ProgressLedger();
        Assert.True(ledger.NoteUnlock("run-a", "sweet-spot"));
        Assert.True(ledger.NoteUnlock("run-a", "royal-treatment"));
        Assert.False(ledger.NoteUnlock("run-a", "sweet-spot")); // once
        Assert.True(ledger.NoteUnlock("run-b", "bean-diet")); // another run's: its own screen
        Assert.False(ledger.NoteUnlock(null, "bean-diet"));
        Assert.False(ledger.NoteUnlock("run-a", "a space"));
        Assert.False(ledger.NoteUnlock("run-a", string.Empty));
        Assert.True(ledger.Changed);

        // The game restarted: the run's end in a later session still lists them, in the order they came.
        ProgressLedger read = ProgressLedger.Parse(ledger.Format());
        Assert.Equal(new[] { "sweet-spot", "royal-treatment" }, read.UnlocksOf("run-a"));
        Assert.Equal(new[] { "bean-diet" }, read.UnlocksOf("run-b"));
        Assert.Empty(read.UnlocksOf("run-c"));
        Assert.Empty(read.UnlocksOf(null));

        // The host's new-unlocks screen of run-a closed: they go, and that run's later progress isn't kept for it.
        Assert.Equal(2, read.UnlocksShown("run-a"));
        Assert.True(read.Changed);
        Assert.Empty(read.UnlocksOf("run-a"));
        Assert.False(read.NoteUnlock("run-a", "a-free-agent"));
        Assert.Equal(0, read.UnlocksShown("run-a"));
        Assert.Equal(0, read.UnlocksShown("a run"));

        ProgressLedger again = ProgressLedger.Parse(read.Format());
        Assert.False(again.NoteUnlock("run-a", "a-free-agent"));
        Assert.Equal(new[] { "bean-diet" }, again.UnlocksOf("run-b"));

        // An older version's ledger has none; a damaged unlock line is skipped, and the rest still reads.
        Assert.Empty(ProgressLedger.Parse("session 00000000000000aa 5\n").UnlocksOf("run-a"));
        ProgressLedger damaged = ProgressLedger.Parse("session 00000000000000aa 5\nunlock run-a x y\nunlock run-a\nunlock run-a ok-id\n");
        Assert.Equal(5u, damaged.AppliedThrough(0xaa));
        Assert.Equal(new[] { "ok-id" }, damaged.UnlocksOf("run-a"));
    }

    [Fact]
    public void The_kept_new_unlocks_are_bounded_the_oldest_going_first()
    {
        var ledger = new ProgressLedger();
        for (int i = 0; i < ProgressLedger.MaxUnlocks + 3; i++)
        {
            Assert.True(ledger.NoteUnlock(i < 3 ? "run-old" : "run-new", $"unlock-{i}"));
        }

        Assert.Empty(ledger.UnlocksOf("run-old"));
        Assert.Equal(ProgressLedger.MaxUnlocks, ProgressLedger.Parse(ledger.Format()).UnlocksOf("run-new").Count);
    }

    [Fact]
    public void A_damaged_ledger_keeps_what_still_reads()
    {
        ProgressLedger ledger = ProgressLedger.Parse(
            "session 00000000000000aa 5\n" +
            "session zz 5\n" +
            "session 00000000000000bb -3\n" +
            "run good-run 1 2 3 4 5\n" +
            "run bad run 1 2 3 4 5\n" +
            "run short-run 1 2\n" +
            "once squad good-run\n" +
            "once squad bad run\n" +
            "once history\n" +
            "garbage\n\0\0\n" +
            "session 00000000000000cc 7 # a comment\n");
        Assert.Equal(5u, ledger.AppliedThrough(0xaa));
        Assert.Equal(0u, ledger.AppliedThrough(0xbb));
        Assert.Equal(7u, ledger.AppliedThrough(0xcc));
        Assert.True(ledger.TryGetRun("good-run", out _));
        Assert.False(ledger.TryGetRun("short-run", out _));
        Assert.False(ledger.TryMarkOnce("squad", "good-run"));
        Assert.True(ledger.TryMarkOnce("history", "good-run"));
        Assert.Equal(0u, ProgressLedger.Parse(null).AppliedThrough(0xaa));
    }

    [Fact]
    public void A_ledger_saved_with_crlf_line_endings_or_tabs_still_remembers_everything()
    {
        // Regression case: "5\r" didn't read, so the whole ledger was forgotten and progress could apply twice.
        var ledger = new ProgressLedger();
        ledger.SetApplied(0xab, 5);
        ledger.SetRun("run-1", new[] { 1, 2, 3, 4, 5 });
        Assert.True(ledger.TryMarkOnce("squad", "run-1"));
        string text = ledger.Format();

        foreach (string saved in new[] { text.Replace("\n", "\r\n", StringComparison.Ordinal), text.Replace(' ', '\t') })
        {
            ProgressLedger read = ProgressLedger.Parse(saved);
            Assert.Equal(5u, read.AppliedThrough(0xab));
            Assert.True(read.TryGetRun("run-1", out int[] counters));
            Assert.Equal(new[] { 1, 2, 3, 4, 5 }, counters);
            Assert.False(read.TryMarkOnce("squad", "run-1"));
        }
    }

    [Fact]
    public void An_id_with_a_hash_in_it_is_remembered_as_it_is_and_never_as_another_id()
    {
        // An id is any printable ASCII without spaces, a '#' included (a host names its runs); a '#' starts a comment only where
        // it starts a word.
        var ledger = new ProgressLedger();
        Assert.True(ledger.TryMarkOnce("squad", "run-a#2"));
        ledger.SetRun("run-a#2", new[] { 1, 2, 3, 4, 5 });
        Assert.True(ledger.NoteUnlock("run-a#2", "unlock#1"));
        ledger.SetApplied(0xab, 5);

        ProgressLedger read = ProgressLedger.Parse(ledger.Format());
        Assert.False(read.TryMarkOnce("squad", "run-a#2")); // still marked after a restart
        Assert.True(read.TryGetRun("run-a#2", out int[] counters));
        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, counters);
        Assert.Equal(new[] { "unlock#1" }, read.UnlocksOf("run-a#2"));
        Assert.False(read.TryGetRun("run-a", out _)); // and nothing was read as the id before the '#'
        Assert.Empty(read.UnlocksOf("run-a"));
        Assert.True(read.TryMarkOnce("squad", "run-a"));
        Assert.Equal(5u, read.AppliedThrough(0xab));

        // A comment still starts at a '#' that starts a word: the header, and one after a line's words.
        ProgressLedger commented = ProgressLedger.Parse("# a header\n  # another\nsession 00000000000000cc 7 # a comment\nonce squad run-b #not-a-word-of-it\nrun #run-c 1 2 3 4 5\n");
        Assert.Equal(7u, commented.AppliedThrough(0xcc));
        Assert.False(commented.TryMarkOnce("squad", "run-b"));
        Assert.False(commented.TryGetRun("#run-c", out _)); // an id that starts with '#' isn't kept across a restart, and isn't read as another
    }

    [Fact]
    public void Random_ledger_text_never_throws()
    {
        var random = new Random(13);
        const string Alphabet = "session run once 0123456789abcdef-\n #\t";
        for (int i = 0; i < 2000; i++)
        {
            char[] text = new char[random.Next(0, 200)];
            for (int c = 0; c < text.Length; c++)
            {
                text[c] = Alphabet[random.Next(Alphabet.Length)];
            }

            _ = ProgressLedger.Parse(new string(text)).Format();
        }
    }

    private static List<byte[]> Send(ProgressOutbox outbox, ulong friend)
    {
        var wire = new List<byte[]>();
        while (outbox.TryNext(friend, out uint sequence, out uint entitledFrom, out byte[] body))
        {
            var writer = new BitWriter();
            ProgressBatchMessage.Write(writer, outbox.Session, entitledFrom, body);
            wire.Add(writer.ToArray());
            outbox.MarkSent(friend, sequence);
        }

        return wire;
    }

    private static void Receive(ProgressInbox inbox, byte[] wire)
    {
        var reader = new BitReader(wire);
        Assert.True(ProgressBatchMessage.TryRead(ref reader, out ProgressMessage? message));
        inbox.OfferOwn(message!);
    }

    private static List<uint> Apply(ProgressInbox inbox, long nowMicros)
    {
        var applied = new List<uint>();
        while (inbox.TryPeek(nowMicros, out ProgressBatch? batch, out _))
        {
            applied.Add(batch!.Sequence);
            inbox.Done(batch);
        }

        return applied;
    }
}
