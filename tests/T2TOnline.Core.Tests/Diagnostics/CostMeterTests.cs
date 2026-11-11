using System.Threading;
using T2TOnline.Core.Diagnostics;

namespace T2TOnline.Core.Tests.Diagnostics;

public class CostMeterTests
{
    [Fact]
    public void Empty_meter_summarizes_to_zero_and_describes_as_nothing()
    {
        var meter = new CostMeter("capture");
        Assert.Equal(default, meter.Total);
        Assert.Equal(string.Empty, meter.DescribeTotal());
        Assert.Equal(string.Empty, meter.DescribeInterval());
    }

    [Fact]
    public void Mean_and_max_are_exact_and_p95_is_close_above()
    {
        var meter = new CostMeter("capture");
        for (int i = 1; i <= 100; i++)
        {
            meter.AddMicros(i);
        }

        CostSummary total = meter.Total;
        Assert.Equal(100, total.Count);
        Assert.Equal(50.5, total.MeanMicros, 6);
        Assert.Equal(100, total.MaxMicros);
        Assert.Equal(5.05, total.TotalMillis, 6);
        // The true 95th is 95: a bucket edge at or above it, at most 9% above.
        Assert.InRange(total.P95Micros, 95, 95 * 1.091);
    }

    [Fact]
    public void Percentile_never_exceeds_the_longest()
    {
        var meter = new CostMeter("load");
        meter.AddMicros(37);
        Assert.Equal(37, meter.Total.P95Micros);
        Assert.Equal(37, meter.Total.MaxMicros);
    }

    [Fact]
    public void A_rare_spike_shows_in_max_but_not_in_p95()
    {
        var meter = new CostMeter("load");
        for (int i = 0; i < 99; i++)
        {
            meter.AddMicros(20);
        }

        meter.AddMicros(16_000);
        CostSummary total = meter.Total;
        Assert.Equal(16_000, total.MaxMicros);
        Assert.InRange(total.P95Micros, 20, 22);
    }

    [Fact]
    public void Interval_restarts_while_the_total_keeps_counting()
    {
        var meter = new CostMeter("blend");
        meter.AddMicros(10);
        meter.AddMicros(30);
        CostSummary first = meter.TakeInterval();
        Assert.Equal(2, first.Count);
        Assert.Equal(20, first.MeanMicros, 6);

        meter.AddMicros(100);
        CostSummary second = meter.TakeInterval();
        Assert.Equal(1, second.Count);
        Assert.Equal(100, second.MaxMicros);
        Assert.Equal(0, meter.TakeInterval().Count);
        Assert.Equal(3, meter.Total.Count);
        Assert.Equal(100, meter.Total.MaxMicros);
    }

    [Fact]
    public void Bad_and_extreme_durations_are_held_to_range()
    {
        var meter = new CostMeter("odd");
        meter.AddMicros(-5);
        meter.AddMicros(double.NaN);
        meter.AddMicros(0.25);
        meter.AddMicros(1e12);
        CostSummary total = meter.Total;
        Assert.Equal(4, total.Count);
        Assert.Equal(1e12, total.MaxMicros);
        Assert.True(double.IsFinite(total.P95Micros));
    }

    [Fact]
    public void Clear_forgets_both_windows()
    {
        var meter = new CostMeter("x");
        meter.AddMicros(5);
        meter.Clear();
        Assert.Equal(0, meter.Count);
        Assert.Equal(0, meter.TakeInterval().Count);
    }

    [Fact]
    public void Describes_name_mean_p95_max_and_count()
    {
        var meter = new CostMeter("capture");
        meter.AddMicros(24);
        Assert.Equal("capture 24/24/24 µs ×1", meter.DescribeTotal());
        Assert.Equal("capture 24/24/24 µs ×1", meter.DescribeInterval());
        Assert.Equal(string.Empty, meter.DescribeInterval());
    }

    [Fact]
    public void Times_real_work_from_a_start_stamp()
    {
        var meter = new CostMeter("sleep");
        long start = CostMeter.Start();
        Thread.Sleep(2);
        meter.AddSince(start);
        Assert.InRange(meter.Total.MeanMicros, 1_000, 2_000_000);
    }
}
