using System;
using T2TOnline.Core.Diagnostics;

namespace T2TOnline.Core.Tests.Diagnostics;

/// <summary>The rate rule for a friend's own 5 s link line (written every time, it is about a seventh of a long session's log lines and a third of its bytes).</summary>
public class LinkReportQuietTests
{
    private const string Joined = "Joined, session Connected, relayed";

    [Fact]
    public void The_first_is_written_then_a_steady_link_waits_up_to_thirty_seconds()
    {
        var rule = new LinkReportQuiet();
        Assert.True(rule.ShouldWrite(Joined, 60, 0, 0, final: false, out int leftOut));
        Assert.Equal(0, leftOut);
        for (int t = 5; t < 30; t += 5)
        {
            Assert.False(rule.ShouldWrite(Joined, 60, 0, t, final: false, out _));
        }

        Assert.True(rule.ShouldWrite(Joined, 60, 0, 30, final: false, out leftOut));
        Assert.Equal(5, leftOut);
    }

    [Fact]
    public void A_new_state_or_route_is_written_at_once_with_what_was_left_out()
    {
        var rule = new LinkReportQuiet();
        rule.ShouldWrite("Connecting, session Connecting, no route yet", 0, 0, 0, final: false, out _);
        Assert.True(rule.ShouldWrite(Joined, 60, 0, 5, final: false, out int leftOut));
        Assert.Equal(0, leftOut);
        Assert.False(rule.ShouldWrite(Joined, 60, 0, 10, final: false, out _));
        Assert.False(rule.ShouldWrite(Joined, 60, 0, 15, final: false, out _));

        // A link that reads direct under relay only is D133's to judge: its line is never left out.
        Assert.True(rule.ShouldWrite("Joined, session Connected, direct", 20, 0, 20, final: false, out leftOut));
        Assert.Equal(2, leftOut);
    }

    [Fact]
    public void A_rising_round_trip_or_a_refused_send_is_written()
    {
        var rule = new LinkReportQuiet();
        rule.ShouldWrite(Joined, 60, 0, 0, final: false, out _);
        Assert.False(rule.ShouldWrite(Joined, 60 + QuietReports.RoundTripRiseMs, 0, 5, final: false, out _));
        Assert.True(rule.ShouldWrite(Joined, 61 + QuietReports.RoundTripRiseMs, 0, 10, final: false, out int leftOut));
        Assert.Equal(1, leftOut);
        Assert.False(rule.ShouldWrite(Joined, 61 + QuietReports.RoundTripRiseMs, 0, 15, final: false, out _));
        Assert.True(rule.ShouldWrite(Joined, 61 + QuietReports.RoundTripRiseMs, 1, 20, final: false, out leftOut));
        Assert.Equal(1, leftOut);
    }

    [Fact]
    public void The_last_report_is_always_written_and_the_next_session_starts_afresh()
    {
        var rule = new LinkReportQuiet();
        rule.ShouldWrite(Joined, 60, 0, 0, final: false, out _);
        Assert.False(rule.ShouldWrite(Joined, 60, 0, 5, final: false, out _));
        Assert.False(rule.ShouldWrite(Joined, 60, 0, 10, final: false, out _));
        Assert.True(rule.ShouldWrite(Joined, 60, 0, 12, final: true, out int leftOut));
        Assert.Equal(2, leftOut);

        // The next session's first line, in the same state as the last one's: written.
        Assert.True(rule.ShouldWrite(Joined, 60, 0, 100, final: false, out leftOut));
        Assert.Equal(0, leftOut);
    }

    [Fact]
    public void A_quiet_line_still_needs_its_route_read_at_every_report()
    {
        // Why the session reads a link's route at every 5 s report, written or not: D133's guard confirms a direct link only
        // from readings closer together than its gap, and a quiet line comes at most every 30 s.
        Assert.True(QuietReports.QuietEverySeconds > DirectLinkWatch.MaxGap.TotalSeconds);
        var watch = new DirectLinkWatch();
        DirectLinkAction last = DirectLinkAction.None;
        for (int t = 0; t <= 120; t += (int)QuietReports.QuietEverySeconds)
        {
            last = watch.Observe(7, relayOnly: true, connected: true, relayed: false, linkToHost: false, TimeSpan.FromSeconds(t), out _);
            Assert.Equal(DirectLinkAction.None, last);
        }

        var every5 = new DirectLinkWatch();
        for (int t = 0; t <= 25; t += 5)
        {
            last = every5.Observe(7, relayOnly: true, connected: true, relayed: false, linkToHost: false, TimeSpan.FromSeconds(t), out _);
        }

        Assert.Equal(DirectLinkAction.Warn, last);
    }
}
