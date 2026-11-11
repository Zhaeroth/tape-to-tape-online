using System;
using T2TOnline.Core.Protocol;
using T2TOnline.Core.Session;
using T2TOnline.Core.Stream;

namespace T2TOnline.Core.Tests.Session;

/// <summary>
/// Custom content before the match needs it: the host's barrier holds a friend while their
/// content still moves (45 s base, then while it moved within 10 s, then 30 s after it all went, 120 s at most), and a
/// friend's load waits while pieces keep arriving (60 s base, a 10 s stall ends it after that, 150 s at most).
/// </summary>
public class ContentHoldTests
{
    private const ulong Friend = 200;
    private const ulong Other = 300;
    private const long Mb = 1024 * 1024;

    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    [Fact]
    public void A_friend_without_content_moving_holds_the_barriers_own_45_seconds()
    {
        var hold = new ContentHold();
        hold.Note(Friend, 0, 0, S(0));
        Assert.False(hold.Holds(Friend, S(1))); // no stream yet: no barrier
        hold.StreamStarted(S(5));
        Assert.True(hold.Holds(Friend, S(49.9)));
        Assert.False(hold.Holds(Friend, S(50)));
        Assert.True(hold.Holds(Other, S(10))); // never noted: the same
        Assert.Equal("still loading after 45 s", hold.Ended(Friend, S(50)));
        Assert.Equal(S(50), hold.Deadline(Friend, S(20)));
    }

    [Fact]
    public void Content_still_moving_holds_past_45_seconds_then_30_seconds_for_the_load()
    {
        // Two 4 MB logos at about 128 KB/s: about 64 s of content, sent from the setup (the stream starts 8 s later).
        var hold = new ContentHold();
        long pending = 8 * Mb;
        double t = 0;
        hold.Note(Friend, pending, 8 * Mb, S(t));
        hold.StreamStarted(S(8));
        for (t = 1; pending > 0; t++)
        {
            pending = Math.Max(0, pending - (128 * 1024));
            hold.Note(Friend, pending, 8 * Mb, S(t));
            Assert.True(hold.Holds(Friend, S(t)), $"held while moving, at {t} s");
            if (t > 53 && pending > 0)
            {
                Assert.True(hold.Extended(Friend, S(t)));
                Assert.Null(hold.Deadline(Friend, S(t)));
            }
        }

        double done = t - 1;
        Assert.Equal(64, done);
        Assert.True(hold.Holds(Friend, S(done + 29.9)));
        Assert.False(hold.Holds(Friend, S(done + 30)));
        Assert.Equal(S(done + 30), hold.Deadline(Friend, S(done + 1)));
        Assert.StartsWith("still loading 30 s after their custom content had all gone (their custom content: 8.0 of 8.0 MB, 128 KB/s)", hold.Ended(Friend, S(done + 30)), StringComparison.Ordinal);
    }

    [Fact]
    public void Content_that_stops_moving_holds_nothing_more_after_45_seconds()
    {
        var hold = new ContentHold();
        hold.Note(Friend, 4 * Mb, 4 * Mb, S(0));
        hold.StreamStarted(S(0));
        hold.Note(Friend, 3 * Mb, 4 * Mb, S(20)); // the last piece that moved
        Assert.True(hold.Holds(Friend, S(44)));
        Assert.True(hold.Holds(Friend, S(29.9)));
        Assert.False(hold.Holds(Friend, S(45))); // after 45 s, and nothing for 10 s
        Assert.Equal("their custom content stopped moving 25 s ago at 1.0 of 4.0 MB, 23 KB/s", hold.Ended(Friend, S(45))); // the rate up to now
        Assert.Equal(S(45), hold.Deadline(Friend, S(40)));
    }

    [Fact]
    public void Nothing_holds_past_120_seconds_after_the_stream_started()
    {
        var hold = new ContentHold();
        hold.StreamStarted(S(0));
        for (int t = 0; t < 120; t++)
        {
            hold.Note(Friend, (120 - t) * 1024L, 120 * 1024, S(t)); // a trickle that just makes it, always moving
            if (t == 100)
            {
                Assert.True(hold.Holds(Friend, S(t)));
            }
        }

        // Its last KB is 1 s away at its rate, and the cap is 1 s away: the hold ends with the cap whatever it would do.
        Assert.True(hold.Holds(Friend, S(119.9)));
        Assert.False(hold.Holds(Friend, S(120)));
        Assert.StartsWith("still loading 120 s after the stream started, the longest it waits", hold.Ended(Friend, S(120)), StringComparison.Ordinal);

        // Content all gone at 110 s: its 30 s for the load still stop at 120.
        var late = new ContentHold();
        late.StreamStarted(S(0));
        late.Note(Friend, Mb, Mb, S(0));
        for (int t = 10; t <= 110; t += 5)
        {
            late.Note(Friend, t == 110 ? 0 : Mb - (t * 1024), Mb, S(t));
        }

        Assert.True(late.Holds(Friend, S(119)));
        Assert.False(late.Holds(Friend, S(120)));
        Assert.Equal(S(120), late.Deadline(Friend, S(111)));
    }

    [Fact]
    public void Content_too_slow_to_be_gone_by_the_cap_holds_nobody_past_45_seconds()
    {
        // Two 4 MB logos at 16 KB/s: over 8 minutes. The host and every other friend would stand still for the whole 120 s
        // and this friend would still join in progress: they go at 45 s, as for content that stopped.
        var hold = new ContentHold();
        hold.StreamStarted(S(0));
        long pending = 8 * Mb;
        for (int t = 0; t <= 60; t++)
        {
            hold.Note(Friend, pending, 8 * Mb, S(t));
            pending -= 16 * 1024;
        }

        Assert.True(hold.Holds(Friend, S(44.9)));
        Assert.False(hold.Holds(Friend, S(45)));
        Assert.False(hold.Extended(Friend, S(46)));
        Assert.StartsWith("their custom content is too slow to be all gone 120 s after the stream started (0.9 of 8.0 MB", hold.Ended(Friend, S(60)), StringComparison.Ordinal);
        Assert.Equal(S(45), hold.Deadline(Friend, S(30)));

        // Content that started moving a moment ago has no rate to judge yet: it holds while it moves.
        var late = new ContentHold();
        late.StreamStarted(S(0));
        late.Note(Friend, 8 * Mb, 8 * Mb, S(44));
        late.Note(Friend, (8 * Mb) - 1024, 8 * Mb, S(48));
        Assert.True(late.Holds(Friend, S(50)));
        Assert.Null(late.Deadline(Friend, S(50)));

        // A rate that just makes it holds until the content is gone.
        var fast = new ContentHold();
        fast.StreamStarted(S(0));
        long left = 8 * Mb;
        for (int t = 0; t < 100; t++)
        {
            fast.Note(Friend, left, 8 * Mb, S(t));
            left -= 8 * Mb / 110;
        }

        Assert.True(fast.Holds(Friend, S(99)));
        Assert.True(fast.Extended(Friend, S(99)));
    }

    [Fact]
    public void Content_that_went_before_the_stream_started_leaves_the_barriers_own_time()
    {
        var hold = new ContentHold();
        hold.Note(Friend, Mb, Mb, S(0));
        hold.Note(Friend, 0, Mb, S(4)); // all gone 4 s after the setup
        hold.StreamStarted(S(10));
        Assert.True(hold.Holds(Friend, S(54)));
        Assert.False(hold.Holds(Friend, S(55)));
        Assert.False(hold.Extended(Friend, S(50)));
    }

    [Fact]
    public void Three_friends_each_hold_for_their_own_content()
    {
        var hold = new ContentHold();
        ulong[] friends = { 1, 2, 3 };
        foreach (ulong friend in friends)
        {
            hold.Note(friend, friend == 1 ? 0 : 4 * Mb, friend == 1 ? 0 : 4 * Mb, S(0));
        }

        hold.StreamStarted(S(0));
        for (int t = 1; t <= 60; t++)
        {
            hold.Note(2, Math.Max(0, (4 * Mb) - (t * 100 * 1024L)), 4 * Mb, S(t)); // moving all along
            hold.Note(3, 3 * Mb, 4 * Mb, S(t)); // stuck
        }

        Assert.False(hold.Holds(1, S(60)));
        Assert.True(hold.Holds(2, S(60)));
        Assert.False(hold.Holds(3, S(60)));
        Assert.True(hold.Extended(2, S(60)));
        Assert.False(hold.Extended(3, S(60)));

        hold.Clear(); // a new match
        Assert.Null(hold.Of(2));
        Assert.Null(hold.StreamStartedAt);
    }

    [Fact]
    public void Describing_content_reads_for_the_log()
    {
        var hold = new ContentHold();
        hold.Note(Friend, 4 * Mb, 4 * Mb, S(0));
        hold.Note(Friend, (int)(1.9 * Mb), 4 * Mb, S(10));
        Assert.Equal("2.1 of 4.0 MB, 215 KB/s", hold.Of(Friend)!.Describe(S(10)));
        Assert.Equal("0.0 of 0.0 MB", new ContentMovement().Describe(S(5)));
    }

    [Fact]
    public void A_friends_wait_goes_on_while_pieces_arrive_and_ends_at_a_stall_or_150_seconds()
    {
        var wait = new ContentWait(100);
        wait.Note(0, 0, 100);
        Assert.False(wait.Expired(159)); // 59 s, nothing yet: the old limit still stands
        Assert.True(wait.Expired(160.5));

        var moving = new ContentWait(0);
        for (int t = 1; t <= 140; t++)
        {
            moving.Note(t * 30L * 1024, 4 * Mb, t);
            Assert.False(moving.Expired(t), $"arriving at {t} s");
            if (t == 61)
            {
                Assert.True(moving.ExtendsNow(t));
            }
        }

        Assert.False(moving.ExtendsNow(100)); // said once
        Assert.False(moving.Expired(149.5));
        Assert.True(moving.Expired(150.5));
        Assert.StartsWith("didn't all arrive within 150 s (4.1 of 4.1 MB", moving.Why(151), StringComparison.Ordinal); // more than first known

        var stalled = new ContentWait(0);
        stalled.Note(Mb, 4 * Mb, 30);
        Assert.False(stalled.Expired(59));
        Assert.True(stalled.Expired(61)); // past 60 s, nothing for 31 s
        Assert.Equal("stopped arriving: nothing for 31 s, 61 s after the match's setup (1.0 of 4.0 MB, 17 KB/s)", stalled.Why(61));
    }

    [Fact]
    public void The_sender_says_how_far_each_friends_batch_has_gone()
    {
        var steam = new SendBuffer(512 * 1024);
        var transport = new RetryingTransport(steam);
        var sender = new CustomContentSender(transport, transport.QueuedBytes);
        Assert.Equal(default, sender.Progress(Friend));

        CustomItem first = Logo("custom_home.png", 1), second = Logo("custom_away.png", 2);
        sender.Send(Friend, new[] { first, second });
        ContentProgress start = sender.Progress(Friend);
        Assert.Equal(first.Bytes.Length + second.Bytes.Length, start.BatchBytes);
        Assert.True(start.PendingBytes > 0 && start.PendingBytes < start.BatchBytes);
        Assert.True(sender.HeldBytes(Friend) <= CustomContentMessage.MaxChunkBytes + 512);
        Assert.Equal(start.BatchBytes - start.PendingBytes, start.SentBytes);

        long last = start.PendingBytes;
        for (int frame = 0; frame < 400 && sender.IsSending(Friend); frame++)
        {
            steam.Drain(64 * 1024);
            transport.Flush();
            sender.Update();
            ContentProgress now = sender.Progress(Friend);
            Assert.True(now.PendingBytes <= last);
            last = now.PendingBytes;
        }

        ContentProgress done = sender.Progress(Friend);
        Assert.Equal(0, done.PendingBytes);
        Assert.Equal(start.BatchBytes, done.BatchBytes); // the last batch's, until a new one starts
        Assert.Equal(0, sender.Progress(Other).BatchBytes);

        // A new batch starts from its own size.
        CustomItem third = Logo("custom_third.png", 3);
        sender.Send(Friend, new[] { third });
        Assert.Equal(third.Bytes.Length, sender.Progress(Friend).BatchBytes);
    }

    [Fact]
    public void The_receiver_says_how_much_of_an_item_is_here()
    {
        CustomItem item = Logo("custom_home.png", 1);
        var receiver = new CustomContentReceiver();
        Assert.False(receiver.ProgressOf(item.Hash, out long received, out long total));
        Assert.Equal((0L, 0L), (received, total));

        Assert.True(receiver.Receive(new CustomChunk(item.Hash, item.Kind, item.Key, item.Bytes.Length, 0, item.Bytes[..1000])));
        Assert.False(receiver.ProgressOf(item.Hash, out received, out total));
        Assert.Equal((1000L, (long)item.Bytes.Length), (received, total));

        Assert.True(receiver.Receive(new CustomChunk(item.Hash, item.Kind, item.Key, item.Bytes.Length, 1000, item.Bytes[1000..])));
        Assert.True(receiver.ProgressOf(item.Hash, out received, out total));
        Assert.Equal(((long)item.Bytes.Length, (long)item.Bytes.Length), (received, total));
    }

    private static CustomItem Logo(string key, byte seed)
    {
        byte[] bytes = new byte[CustomItem.MaxLogoBytes - 64];
        new Random(seed).NextBytes(bytes);
        byte[] header = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R', 0, 0, 1, 0, 0, 0, 1, 0 };
        header.CopyTo(bytes, 0);
        return new CustomItem(CustomContentKind.Logo, key, bytes);
    }

    /// <summary>Steam's send buffer: a send that would take what's pending over the capacity is refused.</summary>
    private sealed class SendBuffer : ISessionTransport
    {
        private readonly int _capacity;
        private long _pending;

        public SendBuffer(int capacity) => _capacity = capacity;

        public bool Send(ulong peer, Channel channel, ReadOnlySpan<byte> message)
        {
            if (_pending + message.Length > _capacity)
            {
                return false;
            }

            _pending += message.Length;
            return true;
        }

        /// <summary>The wire took <paramref name="bytes"/>.</summary>
        public void Drain(int bytes) => _pending = Math.Max(0, _pending - bytes);
    }
}
