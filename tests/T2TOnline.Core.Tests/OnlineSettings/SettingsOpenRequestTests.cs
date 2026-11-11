using System;
using T2TOnline.Core.OnlineSettings;

namespace T2TOnline.Core.Tests.OnlineSettings;

public class SettingsOpenRequestTests
{
    private static readonly TimeSpan At = TimeSpan.FromSeconds(100);

    [Fact]
    public void Nothing_asked_takes_nothing()
    {
        var request = new SettingsOpenRequest();
        SettingsOpenTake take = request.Take(At);
        Assert.False(take.Taken);
        Assert.False(take.Lapsed);
        Assert.False(request.Pending);
    }

    [Fact]
    public void A_request_is_taken_once()
    {
        var request = new SettingsOpenRequest();
        request.Ask(At, "the Online group's row");
        Assert.True(request.Pending);
        SettingsOpenTake take = request.Take(At + TimeSpan.FromMilliseconds(400));
        Assert.True(take.Taken);
        Assert.Equal("the Online group's row", take.From);
        Assert.Equal(TimeSpan.FromMilliseconds(400), take.Age);
        Assert.False(request.Take(At + TimeSpan.FromMilliseconds(500)).Taken);
        Assert.False(request.Pending);
    }

    [Fact]
    public void A_request_is_in_time_at_exactly_its_lifetime()
    {
        var request = new SettingsOpenRequest();
        request.Ask(At, "row");
        Assert.True(request.Take(At + SettingsOpenRequest.Lifetime).Taken);
    }

    [Fact]
    public void A_request_older_than_two_seconds_lapses_and_is_gone()
    {
        Assert.Equal(TimeSpan.FromSeconds(2), SettingsOpenRequest.Lifetime);
        var request = new SettingsOpenRequest();
        request.Ask(At, "row");
        SettingsOpenTake take = request.Take(At + TimeSpan.FromSeconds(2.1));
        Assert.False(take.Taken);
        Assert.True(take.Lapsed);
        Assert.False(request.Take(At + TimeSpan.FromSeconds(2.2)).Lapsed);
    }

    [Fact]
    public void A_new_ask_replaces_the_waiting_one()
    {
        var request = new SettingsOpenRequest();
        request.Ask(At, "first");
        request.Ask(At + TimeSpan.FromSeconds(3), "second");
        SettingsOpenTake take = request.Take(At + TimeSpan.FromSeconds(4));
        Assert.True(take.Taken);
        Assert.Equal("second", take.From);
    }

    [Fact]
    public void A_clock_that_went_back_counts_as_no_time()
    {
        var request = new SettingsOpenRequest();
        request.Ask(At, "row");
        SettingsOpenTake take = request.Take(At - TimeSpan.FromSeconds(1));
        Assert.True(take.Taken);
        Assert.Equal(TimeSpan.Zero, take.Age);
    }

    [Fact]
    public void Clear_forgets_the_request()
    {
        var request = new SettingsOpenRequest();
        request.Ask(At, "row");
        request.Clear();
        Assert.False(request.Take(At).Taken);
    }

    [Theory]
    [InlineData(0, 7, true, 7)]
    [InlineData(4, 7, true, 7)]
    [InlineData(0, 7, false, 0)]
    [InlineData(4, 7, false, 4)]
    [InlineData(0, -1, true, 0)]
    public void The_opening_tab_is_online_only_for_a_taken_request_with_the_tab_in(int game, int online, bool taken, int wanted)
    {
        Assert.Equal(wanted, SettingsOpenRequest.OpeningIndex(game, online, taken));
    }
}
