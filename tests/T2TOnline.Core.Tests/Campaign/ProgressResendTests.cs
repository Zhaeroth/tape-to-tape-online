using System;
using System.Collections.Generic;
using T2TOnline.Core.Campaign;
using T2TOnline.Core.Serialization;

namespace T2TOnline.Core.Tests.Campaign;

/// <summary>A friend asks for missing batches again, and learns what the host shares.</summary>
public class ProgressResendTests
{
    private const ulong Session = 0xfeed_beef_0000_0001;
    private const ulong Alex = 76561199000000001;
    private static readonly StatEntry Goal = new(9, StatOp.IncrementCampaign, StatValue.None);

    private static ProgressBatch Batch(uint sequence) => new(sequence, 0, "run-1", new ProgressEntry[] { Goal });

    private static ProgressMessage Message(uint sequence, uint entitledFrom = 1) => new(Session, entitledFrom, Batch(sequence));

    private static List<uint> Apply(ProgressInbox inbox, long now)
    {
        var applied = new List<uint>();
        while (inbox.TryPeek(now, out ProgressBatch? batch, out _))
        {
            applied.Add(batch!.Sequence);
            inbox.Done(batch);
        }

        return applied;
    }

    private static List<uint> Drain(ProgressOutbox outbox, ulong friend)
    {
        var sent = new List<uint>();
        while (outbox.TryNext(friend, out uint sequence, out _, out _))
        {
            sent.Add(sequence);
            outbox.MarkSent(friend, sequence);
        }

        return sent;
    }

    // ------------------------------------------------------------------ the messages

    [Fact]
    public void Status_and_resend_messages_round_trip()
    {
        var status = new ProgressStatus(Session, false, 4, 70);
        var writer = new BitWriter();
        ProgressStatusMessage.Write(writer, status);
        var reader = new BitReader(writer.AsSpan());
        Assert.True(ProgressStatusMessage.TryRead(ref reader, out ProgressStatus read));
        Assert.Equal(status, read);

        var request = new ProgressResend(Session, 5, ProgressResendMessage.ToNewest);
        writer = new BitWriter();
        ProgressResendMessage.Write(writer, request);
        reader = new BitReader(writer.AsSpan());
        Assert.True(ProgressResendMessage.TryRead(ref reader, out ProgressResend readRequest));
        Assert.Equal(request, readRequest);
    }

    [Fact]
    public void Bad_status_and_resend_messages_are_refused_and_never_throw()
    {
        foreach (Action<BitWriter> bad in new Action<BitWriter>[]
        {
            w => ProgressResendMessage.Write(w, new ProgressResend(Session, 0, 3)), // no batch 0
            w => ProgressResendMessage.Write(w, new ProgressResend(Session, 9, 3)), // backwards
            w => { w.WriteByte(2); w.WriteUInt64(Session); w.WriteUInt32(1); w.WriteUInt32(1); }, // another format
        })
        {
            var writer = new BitWriter();
            bad(writer);
            var reader = new BitReader(writer.AsSpan());
            Assert.False(ProgressResendMessage.TryRead(ref reader, out _));
        }

        foreach (Action<BitWriter> bad in new Action<BitWriter>[]
        {
            w => ProgressStatusMessage.Write(w, new ProgressStatus(Session, true, 0, 3)), // batches are numbered from 1
            w => { w.WriteByte(1); w.WriteUInt64(Session); w.WriteByte(2); w.WriteUInt32(1); w.WriteUInt32(1); }, // unknown flags
        })
        {
            var writer = new BitWriter();
            bad(writer);
            var reader = new BitReader(writer.AsSpan());
            Assert.False(ProgressStatusMessage.TryRead(ref reader, out _));
        }

        var random = new Random(114);
        for (int i = 0; i < 5000; i++)
        {
            byte[] bytes = new byte[random.Next(24)];
            random.NextBytes(bytes);
            var a = new BitReader(bytes);
            _ = ProgressResendMessage.TryRead(ref a, out _);
            var b = new BitReader(bytes);
            _ = ProgressStatusMessage.TryRead(ref b, out _);
        }

        var truncated = new BitWriter();
        ProgressStatusMessage.Write(truncated, new ProgressStatus(Session, true, 1, 1));
        var cut = new BitReader(truncated.AsSpan()[..^1]);
        Assert.False(ProgressStatusMessage.TryRead(ref cut, out _));
    }

    // ------------------------------------------------------------------ the host's outbox

    [Fact]
    public void A_friend_asking_again_gets_those_batches_first_then_what_comes_next()
    {
        var outbox = new ProgressOutbox(Session);
        outbox.FriendJoined(Alex);
        for (uint i = 1; i <= 6; i++)
        {
            outbox.Add(i, new byte[] { (byte)i });
        }

        Assert.Equal(new uint[] { 1, 2, 3, 4, 5, 6 }, Drain(outbox, Alex));
        Assert.True(outbox.Resend(Alex, 2, 3, 0, out uint first, out uint last));
        Assert.Equal((2u, 3u), (first, last));
        Assert.True(outbox.AnyToSend);
        Assert.Equal(new uint[] { 2, 3 }, Drain(outbox, Alex)); // everything up to 6 went before
        Assert.False(outbox.AnyToSend);
        Assert.Equal(1, outbox.Resends);
    }

    [Fact]
    public void A_resend_sends_only_what_was_already_sent_and_is_still_kept_and_only_every_two_seconds()
    {
        var outbox = new ProgressOutbox(Session);
        outbox.Add(1, new byte[] { 1 }); // before Alex joined: not hers
        outbox.FriendJoined(Alex);
        outbox.Add(2, new byte[] { 2 });
        outbox.Add(3, new byte[] { 3 });
        Assert.Equal(2u, outbox.EntitledFrom(Alex));
        Assert.True(outbox.TryNext(Alex, out uint next, out _, out _));
        outbox.MarkSent(Alex, next); // 2 went; 3 hasn't yet

        Assert.True(outbox.Resend(Alex, 1, ProgressResendMessage.ToNewest, 0, out uint first, out uint last));
        Assert.Equal((2u, 2u), (first, last)); // not 1 (before her join), not 3 (it comes anyway)
        Assert.False(outbox.Resend(Alex, 2, 2, ProgressOutbox.ResendGapMicros - 1, out _, out _)); // too soon
        Assert.Equal(new uint[] { 2, 3 }, Drain(outbox, Alex));
        Assert.False(outbox.Resend(Alex, 9, 12, ProgressOutbox.ResendGapMicros, out _, out _)); // nothing that far
        Assert.False(outbox.Resend(Alex + 1, 2, 2, 0, out _, out _)); // a stranger
        Assert.Equal(3, outbox.ResendsRefused);
    }

    [Fact]
    public void Batches_dropped_for_space_since_the_request_are_skipped_and_a_rejoin_replaces_it()
    {
        var outbox = new ProgressOutbox(Session);
        outbox.FriendJoined(Alex);
        outbox.Add(1, new byte[ProgressOutbox.MaxStoredBytes / 2]);
        outbox.Add(2, new byte[ProgressOutbox.MaxStoredBytes / 2]);
        Drain(outbox, Alex);
        Assert.True(outbox.Resend(Alex, 1, 2, 0, out _, out _));
        outbox.Add(3, new byte[10]); // 1 goes for space
        Assert.Equal(new uint[] { 2, 3 }, Drain(outbox, Alex));

        Assert.True(outbox.Resend(Alex, 2, 2, ProgressOutbox.ResendGapMicros, out _, out _));
        outbox.FriendLeft(Alex);
        outbox.FriendJoined(Alex); // everything since her first join comes again: the request is moot
        Assert.Equal(new uint[] { 2, 3 }, Drain(outbox, Alex));
    }

    [Fact]
    public void A_friend_gets_the_status_on_joining_on_a_sharing_change_and_a_while_after_new_batches()
    {
        var outbox = new ProgressOutbox(Session);
        outbox.FriendJoined(Alex);
        Assert.True(outbox.TryStatus(Alex, true, 0, out ProgressStatus status));
        Assert.Equal(new ProgressStatus(Session, true, 1, 0), status);
        outbox.MarkStatusSent(Alex, status);
        Assert.False(outbox.TryStatus(Alex, true, 1, out _));

        Assert.True(outbox.TryStatus(Alex, false, 2, out status)); // sharing turned off: at once
        Assert.False(status.Sharing);
        outbox.MarkStatusSent(Alex, status);

        outbox.Add(1, new byte[] { 1 });
        Assert.False(outbox.TryStatus(Alex, false, 3, out _)); // the batch goes first
        Assert.True(outbox.TryStatus(Alex, false, 3, out _, immediately: true)); // unless hosting is closing
        Assert.False(outbox.TryStatus(Alex, false, 3 + ProgressOutbox.StatusEveryMicros - 1, out _));
        Assert.True(outbox.TryStatus(Alex, false, 3 + ProgressOutbox.StatusEveryMicros, out status));
        Assert.Equal(1u, status.Newest);
        outbox.MarkStatusSent(Alex, status);

        Drain(outbox, Alex);
        Assert.True(outbox.Resend(Alex, 1, 1, 0, out _, out _));
        Assert.True(outbox.TryStatus(Alex, false, 4, out _)); // after a resend, at once
        Assert.False(outbox.TryStatus(Alex + 1, true, 0, out _)); // not for a stranger
    }

    // ------------------------------------------------------------------ the friend's inbox

    [Fact]
    public void A_gap_is_asked_for_after_a_moment_then_again_a_few_times()
    {
        var inbox = new ProgressInbox(new ProgressLedger());
        inbox.OfferOwn(Message(1));
        inbox.OfferOwn(Message(4));
        Apply(inbox, 0);
        Assert.False(inbox.TryRequest(0, out _, out _)); // just seen
        Assert.True(inbox.TryRequest(ProgressInbox.AskAfterMicros, out uint from, out uint to));
        Assert.Equal((2u, 3u), (from, to));
        long at = ProgressInbox.AskAfterMicros;
        Assert.False(inbox.TryRequest(at + ProgressInbox.AskAgainMicros - 1, out _, out _));
        for (int i = 1; i < ProgressInbox.MaxAsks; i++)
        {
            at += ProgressInbox.AskAgainMicros;
            Assert.True(inbox.TryRequest(at, out _, out _));
        }

        Assert.False(inbox.TryRequest(at + (10 * ProgressInbox.AskAgainMicros), out _, out _)); // asked enough
        Assert.Equal(ProgressInbox.MaxAsks, inbox.Requests);

        inbox.OfferOwn(Message(2));
        inbox.OfferOwn(Message(3)); // the resend came
        Assert.Equal(new uint[] { 2, 3, 4 }, Apply(inbox, at));
        Assert.False(inbox.TryRequest(at + ProgressInbox.AskAfterMicros, out _, out _));
    }

    [Fact]
    public void The_hosts_newest_batch_shows_the_last_ones_missing()
    {
        var inbox = new ProgressInbox(new ProgressLedger());
        inbox.OfferOwn(Message(1));
        inbox.OfferOwn(Message(2));
        Apply(inbox, 0);
        Assert.False(inbox.TryRequest(10_000_000, out _, out _)); // nothing says more exist
        inbox.StatusOwn(new ProgressStatus(Session, true, 1, 4));
        Assert.False(inbox.TryRequest(10_000_000, out _, out _));
        Assert.True(inbox.TryRequest(10_000_000 + ProgressInbox.AskAfterMicros, out uint from, out uint to));
        Assert.Equal((3u, 4u), (from, to));
    }

    [Fact]
    public void Batches_refused_while_too_many_waited_are_asked_for_and_the_one_waited_for_is_always_taken()
    {
        var inbox = new ProgressInbox(new ProgressLedger());
        for (uint i = 2; i < ProgressInbox.MaxHeld + 2; i++)
        {
            Assert.Equal(ProgressOffer.Accepted, inbox.OfferOwn(Message(i)));
        }

        Assert.Equal(ProgressOffer.Refused, inbox.OfferOwn(Message(ProgressInbox.MaxHeld + 2)));
        Assert.Equal(ProgressOffer.Refused, inbox.OfferOwn(Message(ProgressInbox.MaxHeld + 3)));
        Assert.Equal(ProgressOffer.Accepted, inbox.OfferOwn(Message(1))); // older than every one waiting: it's the one they wait for
        Assert.Equal(ProgressInbox.MaxHeld + 1, Apply(inbox, 0).Count);

        Assert.False(inbox.TryRequest(0, out _, out _));
        Assert.True(inbox.TryRequest(ProgressInbox.AskAfterMicros, out uint from, out uint to));
        Assert.Equal((ProgressInbox.MaxHeld + 2u, ProgressInbox.MaxHeld + 3u), (from, to));
    }

    [Fact]
    public void Batches_given_up_on_are_asked_for_when_nothing_else_is_missing_and_apply_once()
    {
        var ledger = new ProgressLedger();
        var inbox = new ProgressInbox(ledger);
        inbox.OfferOwn(Message(1));
        inbox.OfferOwn(Message(3));
        Assert.Equal(new uint[] { 1 }, Apply(inbox, 0));
        Assert.Equal(new uint[] { 3 }, Apply(inbox, ProgressInbox.GapGiveUpMicros)); // 2 given up on
        Assert.Equal(1L, ledger.MissingCount(Session));

        long now = ProgressInbox.GapGiveUpMicros + 1;
        Assert.False(inbox.TryRequest(now, out _, out _));
        Assert.True(inbox.TryRequest(now + ProgressInbox.AskAfterMicros, out uint from, out uint to));
        Assert.Equal((2u, 2u), (from, to));

        inbox.OfferOwn(Message(2));
        Assert.Equal(new uint[] { 2 }, Apply(inbox, now));
        Assert.Equal(0L, ledger.MissingCount(Session));
        Assert.Equal(ProgressOffer.Duplicate, inbox.OfferOwn(Message(2)));

    }

    [Fact]
    public void Batches_given_up_on_that_the_host_no_longer_keeps_arent_asked_for()
    {
        var ledger = new ProgressLedger();
        var inbox = new ProgressInbox(ledger);
        inbox.OfferOwn(Message(1));
        inbox.OfferOwn(Message(3));
        Apply(inbox, 0);
        Apply(inbox, ProgressInbox.GapGiveUpMicros); // 2 given up on
        inbox.StatusOwn(new ProgressStatus(Session, true, 3, 3)); // the host keeps batches from 3 on for this PC
        Assert.False(inbox.TryRequest(ProgressInbox.GapGiveUpMicros, out _, out _));
        Assert.False(inbox.TryRequest(2 * ProgressInbox.GapGiveUpMicros, out _, out _));
        Assert.Equal(1L, ledger.MissingCount(Session)); // still remembered: a rejoin may bring it
    }

    [Fact]
    public void The_hosts_status_says_whether_it_shares_and_what_isnt_coming()
    {
        var inbox = new ProgressInbox(new ProgressLedger());
        Assert.Null(inbox.HostStatus);
        inbox.StatusOwn(new ProgressStatus(Session, false, 5, 0));
        Assert.Equal(Session, inbox.Session);
        Assert.False(inbox.HostStatus!.Value.Sharing);
        Assert.Equal(4u, inbox.AppliedThrough); // 1-4 were before this PC joined
        Assert.Equal(4L, inbox.NotEntitled);
        Assert.False(inbox.TryRequest(10_000_000, out _, out _));

        inbox.StatusOwn(new ProgressStatus(Session + 1, true, 1, 0)); // a new hosting session
        Assert.True(inbox.HostStatus!.Value.Sharing);
        Assert.Equal(0u, inbox.AppliedThrough);
    }
}
