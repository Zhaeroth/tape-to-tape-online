using System;
using T2TOnline.Core.Campaign;

namespace T2TOnline.Core.Tests.Campaign;

public class DetailedStatsModeTests
{
    // A panel whose plain options are (1, 2) and whose detailed ones are (5, 6), now showing the given pair.
    private static StatDisplayOptions Panel(int basic, int overall) => new(basic, overall, 1, 2, 5, 6);

    [Fact]
    public void The_host_says_detailed_when_one_panel_shows_its_detailed_stats()
    {
        Assert.Equal(1, DetailedStatsMode.HostValue(new[] { Panel(1, 2), Panel(5, 6) }));
        Assert.Equal(0, DetailedStatsMode.HostValue(new[] { Panel(1, 2), Panel(1, 2) }));
    }

    [Fact]
    public void A_menu_without_a_panel_whose_mode_shows_says_nothing()
    {
        Assert.Null(DetailedStatsMode.HostValue(Array.Empty<StatDisplayOptions>()));
        Assert.Null(DetailedStatsMode.HostValue(new[] { new StatDisplayOptions(3, 3, 3, 3, 3, 3) }));
    }

    [Fact]
    public void A_friends_panel_switches_only_to_what_it_doesnt_show_yet()
    {
        Assert.True(DetailedStatsMode.Switch(Panel(1, 2), 1));
        Assert.Null(DetailedStatsMode.Switch(Panel(5, 6), 1));
        Assert.False(DetailedStatsMode.Switch(Panel(5, 6), 0));
        Assert.Null(DetailedStatsMode.Switch(Panel(1, 2), 0));

        // Neither pair (a panel caught between its own refreshes): put on the host's.
        Assert.False(DetailedStatsMode.Switch(Panel(5, 2), 0));
        Assert.True(DetailedStatsMode.Switch(Panel(5, 2), 1));
    }

    [Fact]
    public void An_older_host_or_a_value_from_nowhere_switches_nothing()
    {
        foreach (long value in new[] { -1L, 2L, long.MaxValue, long.MinValue })
        {
            Assert.Null(DetailedStatsMode.Switch(Panel(1, 2), value));
        }

        // A panel whose mode doesn't show isn't touched either way.
        Assert.Null(DetailedStatsMode.Switch(new StatDisplayOptions(3, 3, 3, 3, 3, 3), 1));
    }
}
