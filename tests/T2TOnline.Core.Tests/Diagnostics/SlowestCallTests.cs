using T2TOnline.Core.Diagnostics;

namespace T2TOnline.Core.Tests.Diagnostics;

/// <summary>The slowest of a repeated call, named in the cost line beside the meter that says how slow.</summary>
public class SlowestCallTests
{
    [Fact]
    public void It_keeps_the_slowest_per_interval_and_per_run()
    {
        var slowest = new SlowestCall();
        Assert.True(slowest.IsSlowest(1));
        slowest.Note(new SlowCall(3_500, "HUD values", null, 0, -1));
        slowest.Note(new SlowCall(150_000, "GameplayEvents.Knockout", "SoundManager", 149_900, 2));
        slowest.Note(new SlowCall(12_000, "a sound", null, 0, -1));
        Assert.False(slowest.IsSlowest(100_000));

        SlowCall interval = slowest.TakeInterval();
        Assert.Equal("GameplayEvents.Knockout", interval.What);
        Assert.Equal(0, slowest.Interval.Micros);
        Assert.True(slowest.IsSlowest(5_000)); // a new interval: anything is its slowest
        slowest.Note(new SlowCall(5_000, "HUD values", null, 0, -1));
        Assert.Equal("HUD values", slowest.Interval.What);
        Assert.Equal("GameplayEvents.Knockout", slowest.Total.What);

        slowest.Clear();
        Assert.Equal(default, slowest.Total);
    }

    [Fact]
    public void It_reads_as_what_was_slow_and_why()
    {
        Assert.Equal("150.0 ms: GameplayEvents.Knockout, 149.9 ms of it in SoundManager; a .NET garbage collection (generation 2) ran during it",
            new SlowCall(150_000, "GameplayEvents.Knockout", "SoundManager", 149_900, 2).ToString());
        Assert.Equal("3.5 ms: HUD values", new SlowCall(3_500, "HUD values", null, 0, -1).ToString());
        Assert.Equal(string.Empty, default(SlowCall).ToString());
    }

    [Fact]
    public void A_cost_meters_clock_never_reads_back()
    {
        long start = CostMeter.Start();
        Assert.True(CostMeter.MicrosSince(start) >= 0);
    }
}
