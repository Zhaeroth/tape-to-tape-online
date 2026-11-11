using System;
using T2TOnline.Core.Session;

namespace T2TOnline.Core.Tests.Session;

/// <summary>The host's per-peer count of what it read and dropped, for the 10 s inbound line.</summary>
public class InboundTallyTests
{
    private const ulong Alex = 76561199000000001;
    private const ulong Sam = 76561199000000002;

    [Fact]
    public void Reads_and_drops_are_counted_per_peer_for_the_window()
    {
        var tally = new InboundTally();
        tally.Read(Alex, 24);
        tally.Read(Alex, 30);
        tally.Read(Sam, 10);
        tally.Dropped(Sam, 500);

        Assert.True(tally.TryGet(Alex, out InboundWindow alex));
        Assert.Equal(new InboundWindow(54, 2, 0, 0), alex);
        Assert.True(tally.TryGet(Sam, out InboundWindow sam));
        Assert.Equal(new InboundWindow(10, 1, 500, 1), sam);
        Assert.False(tally.TryGet(76561199000000003, out InboundWindow none));
        Assert.True(none.IsEmpty);
    }

    [Fact]
    public void The_first_drop_is_said_once_with_who_sent_it_and_the_totals_last_until_clear()
    {
        var tally = new InboundTally();
        Assert.True(tally.Dropped(Sam, 100));
        Assert.False(tally.Dropped(Alex, 200));
        Assert.False(tally.Dropped(Sam, 300));
        tally.NextWindow();

        Assert.Equal(Sam, tally.FirstDropFrom);
        Assert.Equal(3, tally.DroppedMessages);
        Assert.Equal(600, tally.DroppedBytes);

        tally.Clear();
        Assert.Equal(0, tally.DroppedMessages);
        Assert.Equal(0UL, tally.FirstDropFrom);
        Assert.True(tally.Dropped(Alex, 1));
    }

    [Fact]
    public void A_new_window_starts_at_zero_and_a_quiet_peer_gives_its_place_up()
    {
        var tally = new InboundTally();
        tally.Read(Alex, 24);
        tally.Read(Sam, 24);
        tally.NextWindow();
        tally.Read(Alex, 10);
        tally.NextWindow(); // Sam sent nothing in this window: their place is free

        Assert.Equal(1, Count(tally.Peers, Alex));
        Assert.Equal(0, Count(tally.Peers, Sam));
        Assert.False(tally.TryGet(Alex, out _));
    }

    [Fact]
    public void Past_the_table_senders_are_counted_together()
    {
        var tally = new InboundTally();
        for (ulong i = 0; i < InboundTally.MaxPeers; i++)
        {
            tally.Read(Alex + 10 + i, 1);
        }

        tally.Read(Alex, 5);
        tally.Dropped(Sam, 5);
        tally.Read(0, 5);

        Assert.Equal(3, tally.OtherMessages);
        Assert.False(tally.TryGet(Alex, out _));
        Assert.Equal(1, tally.DroppedMessages);
    }

    [Fact]
    public void Counting_allocates_nothing()
    {
        var tally = new InboundTally();
        tally.Read(Alex, 1);
        tally.Dropped(Sam, 1);

        // Counting allocates on every call or on none, so one clean attempt proves none. The runtime can allocate a little
        // on this thread once, while its tiered compiler swaps a hot method in: the least of a few attempts is the measure.
        long least = long.MaxValue;
        for (int attempt = 0; attempt < 5 && least != 0; attempt++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 10_000; i++)
            {
                tally.Read(Alex, 24);
                tally.Dropped(Sam, 512);
                tally.TryGet(Alex, out _);
            }

            least = Math.Min(least, GC.GetAllocatedBytesForCurrentThread() - before);
        }

        Assert.Equal(0, least);
    }

    private static int Count(ReadOnlySpan<ulong> peers, ulong peer)
    {
        int count = 0;
        foreach (ulong id in peers)
        {
            count += id == peer ? 1 : 0;
        }

        return count;
    }
}
