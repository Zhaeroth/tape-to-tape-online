using System;
using System.Collections.Generic;
using T2TOnline.Steam.Native;

namespace T2TOnline.Steam.Tests;

/// <summary>
/// <see cref="SteamMessageReceiver.Discard"/>: a channel nobody should send on (a host's bulk channel) is emptied without
/// copying anything, every message released once, each one's sender and size counted.
/// </summary>
public class SteamMessageDiscardTests
{
    private const ulong Riley = 76561199000000003UL;
    private const ulong Casey = 76561199000000004UL;

    private static List<(ulong Sender, int Channel, int Size)> Seen(out SteamMessageDropped handler)
    {
        var seen = new List<(ulong Sender, int Channel, int Size)>();
        handler = (sender, channel, size) => seen.Add((sender, channel, size));
        return seen;
    }

    [Fact]
    public void Messages_are_dropped_unread_with_each_ones_sender_channel_and_size()
    {
        using var queue = new FakeMessageQueue();
        queue.Add(Riley, new byte[] { 1, 2, 3 });
        queue.Add(Casey, new byte[] { 4 });
        queue.Add(Riley, ReadOnlySpan<byte>.Empty);
        var receiver = new SteamMessageReceiver(queue, 8, 1024);
        List<(ulong Sender, int Channel, int Size)> seen = Seen(out SteamMessageDropped handler);

        int dropped = receiver.Discard(2, handler, 256);

        Assert.Equal(3, dropped);
        Assert.Equal(new[] { (Riley, 2, 3), (Casey, 2, 1), (Riley, 2, 0) }, seen);
        Assert.True(queue.AllReleased());
        Assert.Equal(0, queue.DoubleReleases);
        Assert.Equal(3, receiver.Discarded);
        Assert.Equal(0, receiver.Delivered);
        Assert.False(receiver.Draining);
    }

    [Fact]
    public void The_limit_per_call_holds_and_the_rest_waits_for_the_next()
    {
        using var queue = new FakeMessageQueue();
        for (int i = 0; i < 10; i++)
        {
            queue.Add(Riley, new[] { (byte)i });
        }

        var receiver = new SteamMessageReceiver(queue, 4, 1024);

        Assert.Equal(6, receiver.Discard(2, null, maxMessages: 6));
        Assert.Equal(4, queue.Waiting);
        Assert.Equal(6, queue.Released);
        Assert.Equal(4, receiver.Discard(2, null));
        Assert.True(queue.AllReleased());
    }

    [Fact]
    public void Odd_messages_are_released_and_counted_as_skipped_not_dropped()
    {
        using var queue = new FakeMessageQueue();
        queue.AddNull();
        queue.AddRaw(0, -5, Riley);                        // negative length
        queue.AddRaw(0, 16, Riley);                        // data missing
        queue.Add(Riley, new byte[2000]);                  // over the receiver's limit
        queue.Add(Riley, new byte[] { 9 }, SteamNetworkingIdentityType.IPAddress);
        queue.Add(0, new byte[] { 9 });                    // zero Steam ID
        queue.Add(Casey, new byte[] { 7 });                // the only good one
        var receiver = new SteamMessageReceiver(queue, 16, 1024);
        List<(ulong Sender, int Channel, int Size)> seen = Seen(out SteamMessageDropped handler);

        Assert.Equal(1, receiver.Discard(2, handler));
        Assert.Equal(new[] { (Casey, 2, 1) }, seen);
        Assert.Equal(6, receiver.Skipped);
        Assert.Equal(1, receiver.Discarded);
        Assert.True(queue.AllReleased());
        Assert.Equal(0, queue.DoubleReleases);
    }

    [Fact]
    public void Nothing_is_copied_however_big_the_message()
    {
        using var queue = new FakeMessageQueue();
        var receiver = new SteamMessageReceiver(queue, 8, SteamMessages.MaxSendSize);
        long sizes = 0;
        SteamMessageDropped handler = (_, _, size) => sizes += size;
        queue.Add(Riley, new byte[16]);
        receiver.Discard(2, handler); // Warm up (JIT, first calls).
        for (int i = 0; i < 4; i++)
        {
            queue.Add(Casey, new byte[200_000]); // a drain would grow its buffer for these
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        int dropped = receiver.Discard(2, handler);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(4, dropped);
        Assert.Equal(0, allocated);
        Assert.Equal(16 + (4 * 200_000), sizes);
        Assert.True(queue.AllReleased());
    }

    [Fact]
    public void A_throwing_handler_still_releases_the_whole_batch()
    {
        using var queue = new FakeMessageQueue();
        for (int i = 0; i < 6; i++)
        {
            queue.Add(Riley, new[] { (byte)i });
        }

        var receiver = new SteamMessageReceiver(queue, 4, 1024);
        int calls = 0;

        Assert.Throws<InvalidOperationException>(() => receiver.Discard(2, (_, _, _) =>
        {
            if (++calls == 2)
            {
                throw new InvalidOperationException("handler bug");
            }
        }));

        Assert.Equal(2, calls);
        Assert.Equal(4, queue.Released);   // The batch Steam handed over...
        Assert.Equal(2, receiver.Dropped); // ...two of them not reached...
        Assert.Equal(2, queue.Waiting);    // ...and the next batch still queued.
        Assert.Equal(0, queue.DoubleReleases);
        Assert.False(receiver.Draining);
    }

    [Fact]
    public void Draining_says_a_batch_is_being_handled_and_a_discard_from_inside_one_is_refused()
    {
        using var queue = new FakeMessageQueue();
        queue.Add(Riley, new byte[] { 1 });
        queue.Add(Casey, new byte[] { 2 });
        var receiver = new SteamMessageReceiver(queue, 8, 1024);
        var inside = new List<bool>();
        int refused = 0;

        Assert.False(receiver.Draining);
        int delivered = receiver.Drain(0, (_, _, _) =>
        {
            inside.Add(receiver.Draining);
            try
            {
                receiver.Discard(2, null);
            }
            catch (InvalidOperationException)
            {
                refused++;
            }
        });

        Assert.Equal(2, delivered);
        Assert.Equal(2, refused);
        Assert.Equal(1, queue.ReceiveCalls); // the refused discards never asked Steam
        Assert.False(receiver.Draining);

        queue.Add(Riley, new byte[] { 3 });
        Assert.Equal(1, receiver.Discard(2, (_, _, _) => inside.Add(receiver.Draining)));
        Assert.Equal(new[] { true, true, true }, inside);
        Assert.False(receiver.Draining);
        Assert.True(queue.AllReleased());
    }
}
