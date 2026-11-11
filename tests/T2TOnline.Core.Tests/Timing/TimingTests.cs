using T2TOnline.Core.Timing;

namespace T2TOnline.Core.Tests.Timing;

public class ClockSyncTests
{
    [Fact]
    public void Symmetric_delay_gives_the_exact_offset()
    {
        var sync = new ClockSync();
        const long Offset = 5_000_000;
        for (long t = 0; t < 16; t++)
        {
            long send = t * 500_000;
            Assert.True(sync.AddSample(send, send + 20_000 + Offset, send + 40_000));
        }

        Assert.Equal(Offset, sync.OffsetMicros);
        Assert.Equal(40_000, sync.RoundTripMicros);
        Assert.Equal(20_000, sync.OneWayDelayMicros);
        Assert.Equal(Offset + 7, sync.ToHost(7));
        Assert.Equal(7, sync.ToLocal(Offset + 7));
    }

    [Fact]
    public void Round_trip_spikes_do_not_move_the_offset()
    {
        var sync = new ClockSync();
        const long Offset = -1_234_567;
        for (int i = 0; i < 16; i++)
        {
            long send = i * 500_000L;
            bool spike = i % 5 == 0;
            long forward = spike ? 180_000 : 20_000;
            long back = spike ? 20_000 : 20_000;
            sync.AddSample(send, send + forward + Offset, send + forward + back);
        }

        Assert.InRange(sync.OffsetMicros, Offset - 1, Offset + 1);
        Assert.Equal(40_000, sync.RoundTripMicros);
    }

    [Fact]
    public void Only_the_last_16_samples_count()
    {
        var sync = new ClockSync();
        for (int i = 0; i < 16; i++)
        {
            sync.AddSample(i * 1000L, (i * 1000L) + 500 + 1_000_000, (i * 1000L) + 1000);
        }

        for (int i = 16; i < 32; i++)
        {
            sync.AddSample(i * 1000L, (i * 1000L) + 500 + 2_000_000, (i * 1000L) + 1000);
        }

        Assert.Equal(2_000_000, sync.OffsetMicros);
    }

    [Fact]
    public void Negative_round_trips_are_rejected()
    {
        var sync = new ClockSync();
        Assert.False(sync.AddSample(100, 50, 99));
        Assert.False(sync.HasEstimate);
    }

    [Fact]
    public void Reset_forgets_everything()
    {
        var sync = new ClockSync();
        sync.AddSample(0, 10, 20);
        sync.Reset();
        Assert.False(sync.HasEstimate);
        Assert.Equal(0, sync.OffsetMicros);
    }
}

public class SequenceMathTests
{
    [Fact]
    public void Distances_survive_wraparound()
    {
        Assert.Equal(2, SequenceMath.Distance(uint.MaxValue, 1u));
        Assert.Equal(-2, SequenceMath.Distance(1u, uint.MaxValue));
        Assert.Equal(2, SequenceMath.Distance(ushort.MaxValue, (ushort)1));
        Assert.True(SequenceMath.IsNewer(0u, uint.MaxValue));
        Assert.False(SequenceMath.IsNewer(uint.MaxValue, 0u));
        Assert.True(SequenceMath.IsNewer((ushort)3, (ushort)65_000));
        Assert.False(SequenceMath.IsNewer(5u, 5u));
    }
}
