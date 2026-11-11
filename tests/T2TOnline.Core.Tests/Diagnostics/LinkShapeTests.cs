using System;
using System.Linq;
using T2TOnline.Core.Diagnostics;

namespace T2TOnline.Core.Tests.Diagnostics;

/// <summary>A link's route and this PC's ping location as the log writes them: never a data centre (D127).</summary>
public class LinkShapeTests
{
    private const uint Ord = 0x6F7264; // "ord", as Steam packs a data centre's code
    private const uint Iad = 0x696164;

    [Fact]
    public void A_route_is_relayed_or_direct_and_says_when_the_two_sides_relays_differ()
    {
        Assert.Equal("no route yet", LinkShape.Route(connected: false, relayed: true, Ord, Iad));
        Assert.Equal("direct", LinkShape.Route(connected: true, relayed: false, 0, Iad));
        Assert.Equal("relayed", LinkShape.Route(connected: true, relayed: true, Ord, Ord));
        Assert.Equal("relayed", LinkShape.Route(connected: true, relayed: true, Ord, 0)); // the far side's not known yet
        Assert.Equal("relayed, two relays", LinkShape.Route(connected: true, relayed: true, Ord, Iad));
    }

    [Theory]
    [InlineData("ord=20+2,iad=30+3,atl=45+4/40+2,dfw=55+5/50+2", 1.4f, "ready (4 relays measured, nearest 20 ms, age 1 s)")]
    [InlineData("sea=22+2/9+1", 0f, "ready (1 relay measured, nearest 9 ms, age 0 s)")]
    [InlineData("lax=30+3,broken,=5+1,sea=x+1,sto=12", -1f, "ready (2 relays measured, nearest 12 ms)")]
    [InlineData("", 3f, "unknown")]
    [InlineData(null, 3f, "unknown")]
    [InlineData("nothing,here", 3f, "unreadable")]
    public void A_ping_location_is_a_count_and_the_nearest_ping(string? location, float age, string expected)
    {
        Assert.Equal(expected, LinkShape.PingLocation(location, age));
    }

    [Fact]
    public void A_ping_location_line_holds_no_data_centre()
    {
        string line = LinkShape.PingLocation("ord=20+2,iad=30+3,atl=45+4/40+2", 2f);

        foreach (string code in new[] { "ord", "iad", "atl" })
        {
            Assert.DoesNotContain(code, line, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void A_location_far_past_steams_limit_is_read_only_that_far()
    {
        string location = string.Concat(Enumerable.Repeat("abc=50+1,", 400)) + "xyz=1+1";

        Assert.Equal("ready (113 relays measured, nearest 50 ms, age 0 s)", LinkShape.PingLocation(location, 0f));
    }
}
