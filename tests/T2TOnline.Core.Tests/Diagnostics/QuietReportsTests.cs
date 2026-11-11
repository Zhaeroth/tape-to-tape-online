using T2TOnline.Core.Diagnostics;

namespace T2TOnline.Core.Tests.Diagnostics;

/// <summary>The rate rule for a friend's 5 s input line on the host (written every time, it is close to a third of a host's log lines with one friend).</summary>
public class QuietReportsTests
{
    private static readonly InputReportCounters Clean = new(0, 50, 4, 0, 0, 0, 7, 0, 0);

    [Fact]
    public void The_first_is_written_then_quiet_ones_wait_up_to_thirty_seconds()
    {
        var reports = new QuietReports();
        Assert.True(reports.ShouldWrite(Clean, 0, out int leftOut));
        Assert.Equal(0, leftOut);
        for (int t = 5; t < 30; t += 5)
        {
            Assert.False(reports.ShouldWrite(Clean, t, out _));
        }

        Assert.Equal(5, reports.LeftOut);
        Assert.True(reports.ShouldWrite(Clean, 30, out leftOut));
        Assert.Equal(5, leftOut);
        Assert.Equal(0, reports.LeftOut);
    }

    [Fact]
    public void Small_growth_within_the_slack_is_quiet_more_is_written()
    {
        var reports = new QuietReports();
        reports.ShouldWrite(Clean, 0, out _);
        Assert.False(reports.ShouldWrite(Clean with { Underruns = Clean.Underruns + QuietReports.Slack }, 5, out _));
        Assert.True(reports.ShouldWrite(Clean with { Underruns = Clean.Underruns + QuietReports.Slack + 1 }, 10, out int leftOut));
        Assert.Equal(1, leftOut);
    }

    [Theory]
    [InlineData("loss")]
    [InlineData("rtt")]
    [InlineData("missing")]
    [InlineData("stale")]
    [InlineData("resyncs")]
    [InlineData("dropped")]
    [InlineData("holds")]
    [InlineData("refused")]
    public void Each_thing_that_moved_is_written(string what)
    {
        InputReportCounters moved = what switch
        {
            "loss" => Clean with { LossPercent = 0.5 },
            "rtt" => Clean with { RoundTripP95Ms = Clean.RoundTripP95Ms + QuietReports.RoundTripRiseMs + 1 },
            "missing" => Clean with { Missing = Clean.Missing + 8 },
            "stale" => Clean with { Stale = 3 },
            "resyncs" => Clean with { Resyncs = 3 },
            "dropped" => Clean with { DroppedTicks = 52 },
            "holds" => Clean with { HoldsOffByMoreThanAFrame = 1 },
            _ => Clean with { Refused = 1 },
        };
        Assert.False(QuietReports.Quiet(Clean, moved));
        var reports = new QuietReports();
        reports.ShouldWrite(Clean, 0, out _);
        Assert.True(reports.ShouldWrite(moved, 5, out _));
    }

    [Fact]
    public void Totals_that_went_down_are_new_counters_and_written()
    {
        // A rejoining friend's receiver starts over.
        Assert.False(QuietReports.Quiet(Clean with { Underruns = 40 }, Clean));
    }

    [Fact]
    public void Growth_is_measured_from_the_last_line_written_not_the_last_report()
    {
        var reports = new QuietReports();
        reports.ShouldWrite(Clean, 0, out _);
        Assert.False(reports.ShouldWrite(Clean with { Underruns = Clean.Underruns + 1 }, 5, out _));
        Assert.False(reports.ShouldWrite(Clean with { Underruns = Clean.Underruns + 2 }, 10, out _));
        Assert.True(reports.ShouldWrite(Clean with { Underruns = Clean.Underruns + 3 }, 15, out _));
    }

    [Fact]
    public void A_falling_round_trip_is_quiet_and_clear_writes_the_next()
    {
        var reports = new QuietReports();
        reports.ShouldWrite(Clean, 0, out _);
        Assert.False(reports.ShouldWrite(Clean with { RoundTripP95Ms = 10 }, 5, out _));
        reports.Clear();
        Assert.True(reports.ShouldWrite(Clean, 6, out int leftOut));
        Assert.Equal(0, leftOut);
    }
}
