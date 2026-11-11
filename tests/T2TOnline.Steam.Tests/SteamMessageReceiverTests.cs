using System;
using System.Collections.Generic;
using T2TOnline.Steam.Native;

namespace T2TOnline.Steam.Tests;

public class SteamMessageReceiverTests
{
    private const ulong Alex = 76561199000000001UL;
    private const ulong Sam = 76561199000000002UL;

    private sealed class Collector
    {
        public List<(ulong Sender, int Channel, byte[] Payload)> Messages { get; } = new();

        public void Handle(ulong sender, int channel, ReadOnlySpan<byte> payload) => Messages.Add((sender, channel, payload.ToArray()));
    }

    [Fact]
    public void Delivers_sender_channel_and_payload_then_releases()
    {
        using var queue = new FakeMessageQueue();
        queue.Add(Alex, new byte[] { 1, 2, 3 });
        queue.Add(Sam, new byte[] { 4 });
        var receiver = new SteamMessageReceiver(queue, 8, 1024);
        var collector = new Collector();

        int delivered = receiver.Drain(1, collector.Handle);

        Assert.Equal(2, delivered);
        Assert.Equal((Alex, 1), (collector.Messages[0].Sender, collector.Messages[0].Channel));
        Assert.Equal(new byte[] { 1, 2, 3 }, collector.Messages[0].Payload);
        Assert.Equal(Sam, collector.Messages[1].Sender);
        Assert.Equal(new byte[] { 4 }, collector.Messages[1].Payload);
        Assert.True(queue.AllReleased());
        Assert.Equal(0, queue.DoubleReleases);
        Assert.Equal(2, receiver.Delivered);
    }

    [Fact]
    public void Each_message_s_steam_receive_time_is_readable_in_its_handler()
    {
        using var queue = new FakeMessageQueue();
        System.Runtime.InteropServices.Marshal.WriteInt64(queue.Add(Alex, new byte[] { 1 }), SteamNetworkingMessage.TimeReceivedOffset, 1_234_567);
        System.Runtime.InteropServices.Marshal.WriteInt64(queue.Add(Sam, new byte[] { 2 }), SteamNetworkingMessage.TimeReceivedOffset, 2_345_678);
        var receiver = new SteamMessageReceiver(queue, 8, 1024);
        var times = new List<long>();

        receiver.Drain(0, (_, _, _) => times.Add(receiver.TimeReceived));

        Assert.Equal(new long[] { 1_234_567, 2_345_678 }, times);
    }

    [Fact]
    public void Empty_payloads_are_delivered()
    {
        using var queue = new FakeMessageQueue();
        queue.Add(Alex, ReadOnlySpan<byte>.Empty);
        var receiver = new SteamMessageReceiver(queue, 8, 1024);
        var collector = new Collector();

        Assert.Equal(1, receiver.Drain(0, collector.Handle));
        Assert.Empty(collector.Messages[0].Payload);
        Assert.True(queue.AllReleased());
    }

    [Fact]
    public void Drains_in_batches_until_steam_runs_dry()
    {
        using var queue = new FakeMessageQueue();
        for (int i = 0; i < 10; i++)
        {
            queue.Add(Alex, new[] { (byte)i });
        }

        var receiver = new SteamMessageReceiver(queue, 4, 1024);
        var collector = new Collector();

        Assert.Equal(10, receiver.Drain(0, collector.Handle));
        Assert.Equal(3, queue.ReceiveCalls); // 4 + 4 + 2: a short batch means the queue is empty.
        for (int i = 0; i < 10; i++)
        {
            Assert.Equal((byte)i, collector.Messages[i].Payload[0]);
        }

        Assert.True(queue.AllReleased());
    }

    [Fact]
    public void Respects_the_message_limit_and_leaves_the_rest_queued()
    {
        using var queue = new FakeMessageQueue();
        for (int i = 0; i < 10; i++)
        {
            queue.Add(Alex, new[] { (byte)i });
        }

        var receiver = new SteamMessageReceiver(queue, 4, 1024);
        var collector = new Collector();

        Assert.Equal(6, receiver.Drain(0, collector.Handle, maxMessages: 6));
        Assert.Equal(4, queue.Waiting);
        Assert.Equal(6, queue.Released);
    }

    [Fact]
    public void Odd_messages_are_released_and_skipped_without_throwing()
    {
        using var queue = new FakeMessageQueue();
        queue.AddNull();
        queue.AddRaw(0, -5, Alex);                        // negative length
        queue.AddRaw(0, 16, Alex);                        // data missing
        queue.Add(Alex, new byte[2000]);                  // over the receiver's limit
        queue.Add(Alex, new byte[] { 9 }, SteamNetworkingIdentityType.IPAddress);
        queue.Add(0, new byte[] { 9 });                    // zero Steam ID
        queue.Add(Sam, new byte[] { 7 });                  // the only good one
        var receiver = new SteamMessageReceiver(queue, 16, 1024);
        var collector = new Collector();

        Assert.Equal(1, receiver.Drain(0, collector.Handle));
        Assert.Equal(Sam, collector.Messages[0].Sender);
        Assert.Equal(6, receiver.Skipped);
        Assert.True(queue.AllReleased());
        Assert.Equal(0, queue.DoubleReleases);
    }

    [Fact]
    public void Bad_counts_from_steam_never_read_past_what_was_asked()
    {
        using var queue = new FakeMessageQueue();
        queue.Add(Alex, new byte[] { 1 });
        queue.Add(Alex, new byte[] { 2 });
        var receiver = new SteamMessageReceiver(queue, 2, 1024);
        var collector = new Collector();

        queue.ReportedCount = -1;
        Assert.Equal(0, receiver.Drain(0, collector.Handle));
        Assert.Equal(2, queue.Waiting);

        // Steam claims more than it wrote: the extra slots are empty, skipped, and the loop still ends.
        queue.ReportedCount = 99;
        Assert.Equal(2, receiver.Drain(0, collector.Handle, maxMessages: 4));
        Assert.Equal(2, receiver.Skipped);
        Assert.True(queue.AllReleased());
        Assert.Equal(0, queue.DoubleReleases);
    }

    [Fact]
    public void A_throwing_handler_still_releases_the_whole_batch()
    {
        using var queue = new FakeMessageQueue();
        for (int i = 0; i < 6; i++)
        {
            queue.Add(Alex, new[] { (byte)i });
        }

        var receiver = new SteamMessageReceiver(queue, 4, 1024);
        int calls = 0;

        Assert.Throws<InvalidOperationException>(() => receiver.Drain(0, (_, _, payload) =>
        {
            calls++;
            if (payload[0] == 1)
            {
                throw new InvalidOperationException("handler bug");
            }
        }));

        Assert.Equal(2, calls);
        Assert.Equal(4, queue.Released);   // The batch Steam handed over...
        Assert.Equal(2, receiver.Dropped); // ...two of them undelivered...
        Assert.Equal(2, queue.Waiting);    // ...and the next batch still queued.
        Assert.Equal(0, queue.DoubleReleases);
    }

    [Fact]
    public void Big_messages_grow_the_buffer_up_to_the_limit()
    {
        using var queue = new FakeMessageQueue();
        byte[] big = new byte[100_000];
        new Random(7).NextBytes(big);
        queue.Add(Alex, big);
        var receiver = new SteamMessageReceiver(queue, 4, SteamMessages.MaxSendSize);
        var collector = new Collector();

        Assert.Equal(1, receiver.Drain(0, collector.Handle));
        Assert.Equal(big, collector.Messages[0].Payload);
    }

    [Fact]
    public void Steady_state_draining_does_not_allocate()
    {
        using var queue = new FakeMessageQueue();
        var receiver = new SteamMessageReceiver(queue, 32, 1024);
        long checksum = 0;
        SteamMessageHandler handler = (sender, _, payload) => checksum += (long)sender + payload[0];
        for (int i = 0; i < 400; i++)
        {
            queue.Add(Alex, new byte[] { (byte)i, 0, 0, 0, 0, 0, 0, 0 });
        }

        receiver.Drain(0, handler, maxMessages: 64); // Warm up (JIT, first calls).
        long before = GC.GetAllocatedBytesForCurrentThread();
        int delivered = receiver.Drain(0, handler);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(336, delivered);
        Assert.Equal(0, allocated);
        Assert.NotEqual(0, checksum);
    }

    [Fact]
    public void Steady_state_draining_with_state_does_not_allocate()
    {
        using var queue = new FakeMessageQueue();
        var receiver = new SteamMessageReceiver(queue, 32, 1024);
        var sums = new long[1];
        for (int i = 0; i < 400; i++)
        {
            queue.Add(Alex, new byte[] { (byte)i, 0, 0, 0, 0, 0, 0, 0 });
        }

        // A struct state and a static lambda (cached by the compiler): nothing captured, nothing boxed.
        SteamMessageHandler<(long[] Sums, int Tag)> handler = static (state, sender, channel, payload) =>
            state.Sums[0] += (long)sender + payload[0] + state.Tag + channel;

        receiver.Drain(0, (Sums: sums, Tag: 7), handler, maxMessages: 64); // Warm up (JIT, first calls).
        long before = GC.GetAllocatedBytesForCurrentThread();
        int delivered = receiver.Drain(0, (Sums: sums, Tag: 7), handler);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(336, delivered);
        Assert.Equal(0, allocated);
        Assert.NotEqual(0, sums[0]);
    }

    [Fact]
    public void Draining_with_state_hands_the_state_to_every_message()
    {
        using var queue = new FakeMessageQueue();
        queue.Add(Alex, new byte[] { 1 });
        queue.Add(Sam, new byte[] { 2, 3 });
        var receiver = new SteamMessageReceiver(queue, 8, 1024);
        var collector = new Collector();

        int delivered = receiver.Drain(5, (collector, 40), static (state, sender, channel, payload) =>
            state.collector.Handle(sender, channel + state.Item2, payload));

        Assert.Equal(2, delivered);
        Assert.Equal((Alex, 45), (collector.Messages[0].Sender, collector.Messages[0].Channel));
        Assert.Equal(new byte[] { 2, 3 }, collector.Messages[1].Payload);
        Assert.True(queue.AllReleased());
    }

    [Fact]
    public void A_handler_that_drains_its_own_receiver_is_refused_before_anything_is_taken()
    {
        using var queue = new FakeMessageQueue();
        for (int i = 0; i < 6; i++)
        {
            queue.Add(Alex, new[] { (byte)i });
        }

        var receiver = new SteamMessageReceiver(queue, 4, 1024);
        int calls = 0;

        Assert.Throws<InvalidOperationException>(() => receiver.Drain(0, (_, _, _) =>
        {
            calls++;
            receiver.Drain(0, (_, _, _) => Assert.Fail("a nested drain must not deliver"));
        }));

        Assert.Equal(1, calls);
        Assert.Equal(1, queue.ReceiveCalls);  // The nested drain never asked Steam...
        Assert.Equal(4, queue.Released);      // ...the outer batch was released once...
        Assert.Equal(0, queue.DoubleReleases);
        Assert.Equal(3, receiver.Dropped);    // ...three of them undelivered...
        Assert.Equal(2, queue.Waiting);       // ...and the rest is still queued.

        var collector = new Collector();
        Assert.Equal(2, receiver.Drain(0, collector.Handle)); // The receiver works again afterwards.
        Assert.True(queue.AllReleased());
    }

    [Fact]
    public void A_handler_that_catches_the_refused_re_entry_finishes_the_drain()
    {
        using var queue = new FakeMessageQueue();
        for (int i = 0; i < 6; i++)
        {
            queue.Add(Alex, new[] { (byte)i });
        }

        var receiver = new SteamMessageReceiver(queue, 4, 1024);
        int refused = 0;

        int delivered = receiver.Drain(0, (_, _, _) =>
        {
            try
            {
                receiver.Drain(1, 0, static (_, _, _, _) => { });
            }
            catch (InvalidOperationException)
            {
                refused++;
            }
        });

        Assert.Equal(6, delivered);
        Assert.Equal(6, refused);
        Assert.True(queue.AllReleased());
        Assert.Equal(0, queue.DoubleReleases);
        Assert.Equal(0, receiver.Dropped);
    }

    [Fact]
    public void Rejects_bad_constructor_arguments()
    {
        using var queue = new FakeMessageQueue();
        Assert.Throws<ArgumentOutOfRangeException>(() => new SteamMessageReceiver(queue, 0, 1024));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SteamMessageReceiver(queue, 4, 0));
        Assert.Throws<ArgumentNullException>(() => new SteamMessageReceiver(queue, 4, 1024).Drain(0, null!));
        Assert.Throws<ArgumentNullException>(() => new SteamMessageReceiver(queue, 4, 1024).Drain(0, 1, null!));
    }
}
