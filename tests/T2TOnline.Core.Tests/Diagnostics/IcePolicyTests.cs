using System;
using T2TOnline.Core.Diagnostics;

namespace T2TOnline.Core.Tests.Diagnostics;

/// <summary>Relay only unless the player allows direct connections, and the start-up line saying what Steam took (D133).</summary>
public class IcePolicyTests
{
    private const string Off = ", so online is off this session: Steam could connect you straight to another player, which would share your IP address with them. To play online anyway, set AllowDirectConnections = true in UserData/T2TOnline/settings.cfg and restart the game (your address is then shared with the players you connect to).";

    [Fact]
    public void Relay_only_is_ice_off_and_direct_is_every_candidate_type()
    {
        Assert.Equal(0, IcePolicy.Wanted(allowDirect: false));
        Assert.Equal(0x7fffffff, IcePolicy.Wanted(allowDirect: true));
        Assert.Equal(IcePolicy.Disabled, IcePolicy.Wanted(false));
        Assert.Equal(IcePolicy.AllCandidates, IcePolicy.Wanted(true));
    }

    [Fact]
    public void Relay_only_read_back_says_no_address_is_shared()
    {
        IceOutcome outcome = IcePolicy.Check(allowDirect: false, setOk: true, readOk: true, readBack: 0);

        Assert.True(outcome.InEffect);
        Assert.True(outcome.RelayOnly);
        Assert.False(outcome.TurnsOnlineOff);
        Assert.Equal("ICE: relay only (IP addresses are never shared).", outcome.Line);
    }

    [Fact]
    public void Direct_allowed_read_back_says_the_address_is_shared()
    {
        IceOutcome outcome = IcePolicy.Check(allowDirect: true, setOk: true, readOk: true, readBack: 0x7fffffff);

        Assert.True(outcome.InEffect);
        Assert.False(outcome.RelayOnly);
        Assert.False(outcome.TurnsOnlineOff);
        Assert.Equal("ICE: direct connections allowed (your address is shared with the players you connect to).", outcome.Line);
    }

    [Fact]
    public void The_read_back_decides_even_when_steam_said_the_set_failed()
    {
        IceOutcome outcome = IcePolicy.Check(allowDirect: false, setOk: false, readOk: true, readBack: 0);

        Assert.True(outcome.InEffect);
        Assert.True(outcome.RelayOnly);
        Assert.Equal("ICE: relay only (IP addresses are never shared) (Steam said the setting failed, but reads it back as set).", outcome.Line);
    }

    [Theory]
    [InlineData(false, true, -1, "ICE: relay only wanted (0), but Steam refused the setting (it reads back -1)" + Off)]
    [InlineData(true, true, -1, "ICE: relay only wanted (0), but Steam reads back -1 although it accepted the setting" + Off)]
    [InlineData(false, false, 0, "ICE: relay only wanted (0), but Steam refused the setting and its value can't be read back" + Off)]
    [InlineData(true, false, 0, "ICE: relay only wanted (0), but Steam's value can't be read back although it accepted the setting" + Off)]
    [InlineData(true, true, 2, "ICE: relay only wanted (0), but Steam reads back 2 although it accepted the setting" + Off)] // private (LAN) candidates only: still an address shared
    public void Relay_only_not_in_effect_turns_online_off_and_says_why_and_how_to_play_anyway(bool setOk, bool readOk, int readBack, string line)
    {
        IceOutcome outcome = IcePolicy.Check(allowDirect: false, setOk, readOk, readBack);

        Assert.False(outcome.InEffect);
        Assert.False(outcome.RelayOnly);
        Assert.True(outcome.TurnsOnlineOff);
        Assert.Equal(line, outcome.Line);
    }

    [Fact]
    public void Direct_not_in_effect_says_connections_may_all_be_relayed()
    {
        IceOutcome outcome = IcePolicy.Check(allowDirect: true, setOk: true, readOk: true, readBack: 0);

        Assert.False(outcome.InEffect);
        Assert.False(outcome.RelayOnly);
        Assert.False(outcome.TurnsOnlineOff); // relayed is the private side of what was wanted: online stays on
        Assert.Equal("ICE: direct connections wanted (2147483647), but Steam reads back 0 although it accepted the setting; connections may all go through Steam's relays.", outcome.Line);
    }

    [Fact]
    public void A_wrong_read_back_is_never_relay_only_and_always_turns_online_off()
    {
        foreach (int value in new[] { -1, 1, 2, 4, 6, 0x7fffffff, int.MinValue })
        {
            IceOutcome outcome = IcePolicy.Check(allowDirect: false, setOk: true, readOk: true, value);
            Assert.False(outcome.RelayOnly);
            Assert.True(outcome.TurnsOnlineOff);
        }
    }

    [Theory]
    [InlineData(true, true, false, true, DirectLinkAction.Leave)] // a friend's link to its host: left
    [InlineData(true, true, false, false, DirectLinkAction.Warn)] // a host's link to a friend: warned
    [InlineData(true, true, true, true, DirectLinkAction.None)]
    [InlineData(true, false, false, true, DirectLinkAction.None)]
    [InlineData(false, true, false, true, DirectLinkAction.None)] // direct connections allowed
    public void A_direct_link_under_relay_only_is_left_by_the_friend_and_warned_about_by_the_host(bool relayOnly, bool connected, bool relayed, bool linkToHost, DirectLinkAction action)
    {
        Assert.Equal(action, IcePolicy.OnRoute(relayOnly, connected, relayed, linkToHost));
    }

    [Theory]
    [InlineData(true, true, false, true)] // relay only in effect, connected, not relayed: Steam didn't keep it
    [InlineData(true, true, true, false)] // relayed, as it should be
    [InlineData(true, false, false, false)] // no route yet isn't direct
    [InlineData(false, true, false, false)] // direct allowed (or relay only not in effect: the start-up line said so)
    public void A_direct_link_is_flagged_only_while_relay_only_is_in_effect(bool relayOnly, bool connected, bool relayed, bool flagged)
    {
        Assert.Equal(flagged, IcePolicy.DirectDespiteRelayOnly(relayOnly, connected, relayed));
    }

    [Fact]
    public void A_single_direct_reading_is_a_lost_route_and_only_a_link_that_stays_direct_is_acted_on()
    {
        // Steam can drop a link's transport while it stays connected (a lost route, until a relay is found again or the link
        // times out), and that reads as not relayed: the friend leaves only after every report for 20 s read it direct.
        var watch = new DirectLinkWatch();
        const ulong Host = 76561199000000001;
        Assert.Equal(DirectLinkAction.None, watch.Observe(Host, true, true, false, true, TimeSpan.FromSeconds(100), out _));
        Assert.Equal(DirectLinkAction.None, watch.Observe(Host, true, true, false, true, TimeSpan.FromSeconds(105), out TimeSpan five));
        Assert.Equal(TimeSpan.FromSeconds(5), five);
        Assert.Equal(DirectLinkAction.None, watch.Observe(Host, true, true, true, true, TimeSpan.FromSeconds(110), out _)); // relayed again
        for (int s = 115; s < 135; s += 5)
        {
            Assert.Equal(DirectLinkAction.None, watch.Observe(Host, true, true, false, true, TimeSpan.FromSeconds(s), out _));
        }

        Assert.Equal(DirectLinkAction.Leave, watch.Observe(Host, true, true, false, true, TimeSpan.FromSeconds(135), out TimeSpan twenty));
        Assert.Equal(TimeSpan.FromSeconds(20), twenty);
    }

    [Fact]
    public void A_link_that_drops_or_goes_unread_starts_over_and_a_host_only_warns()
    {
        var watch = new DirectLinkWatch();
        const ulong Friend = 76561199000000002;
        watch.Observe(Friend, true, true, false, false, TimeSpan.FromSeconds(0), out _);
        watch.Observe(Friend, true, false, false, false, TimeSpan.FromSeconds(5), out _); // timed out: no longer connected
        watch.Observe(Friend, true, true, false, false, TimeSpan.FromSeconds(10), out _);
        Assert.Equal(DirectLinkAction.None, watch.Observe(Friend, true, true, false, false, TimeSpan.FromSeconds(25), out _)); // a gap past 12 s
        for (int s = 30; s < 45; s += 5)
        {
            Assert.Equal(DirectLinkAction.None, watch.Observe(Friend, true, true, false, false, TimeSpan.FromSeconds(s), out _));
        }

        Assert.Equal(DirectLinkAction.Warn, watch.Observe(Friend, true, true, false, false, TimeSpan.FromSeconds(45), out _));
        Assert.Equal(DirectLinkAction.None, new DirectLinkWatch().Observe(Friend, false, true, false, true, TimeSpan.FromSeconds(45), out _)); // direct allowed
    }

    [Fact]
    public void No_line_names_a_player_an_address_or_a_relay()
    {
        foreach (bool allow in new[] { false, true })
        {
            foreach (bool set in new[] { false, true })
            {
                foreach (bool read in new[] { false, true })
                {
                    string line = IcePolicy.Check(allow, set, read, -1).Line;
                    Assert.StartsWith("ICE: ", line, StringComparison.Ordinal);
                    Assert.DoesNotMatch(@"\d+\.\d+\.\d+\.\d+", line);
                    Assert.DoesNotContain("relayed via", line, StringComparison.Ordinal);
                }
            }
        }
    }
}
