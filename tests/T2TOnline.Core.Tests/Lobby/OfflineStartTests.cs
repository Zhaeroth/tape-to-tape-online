using System;
using System.Linq;
using T2TOnline.Core.Lobby;

namespace T2TOnline.Core.Tests.Lobby;

/// <summary>The developer's offline start: its tour, the run.txt lines it takes, and its words.</summary>
public class OfflineStartTests
{
    [Fact]
    public void The_tour_is_declared_with_its_reason_key_and_its_steps_order_rules_hold()
    {
        UiCheckTour tour = UiCheckRequest.Find(UiCheckPlan.Tours, "offline")!;
        Assert.Same(OfflineStart.Tour, tour);
        Assert.Null(UiCheckPlan.Problem(tour.Steps));
        Assert.Equal(new[] { "off_state", "off_settings", "off_report", "off_backups", "off_offer", "off_notice", "finish" }, tour.Steps.Select(s => s.Id).ToArray());
        Assert.Equal(TimeSpan.FromMinutes(5), tour.Limit);
        UiCheckKey key = tour.Key("reason")!;
        Assert.Equal(new[] { "steam", "relay", "error" }, key.Choices);
    }

    [Fact]
    public void Its_first_step_runs_in_the_players_own_settings()
    {
        Assert.True(OfflineStart.Steps[0].Needs.HasFlag(UiCheckNeeds.PlayersSettings));
    }

    [Theory]
    [InlineData("offline reason=steam", OfflineStartReason.Steam)]
    [InlineData("  offline   reason=relay ", OfflineStartReason.Relay)]
    [InlineData("offline reason=error", OfflineStartReason.Error)]
    [InlineData("Offline reason=steam", OfflineStartReason.Steam)]
    public void A_run_line_naming_the_offline_tour_and_a_reason_starts_it(string line, OfflineStartReason reason)
    {
        Assert.Equal(reason, OfflineStart.FromRunLine(line, UiCheckPlan.Tours));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("offline")]
    [InlineData("offline reason=lan")]
    [InlineData("offline reason=Steam")]
    [InlineData("offline seed=3")]
    [InlineData("offline reason=steam reason=error")]
    [InlineData("spike")]
    [InlineData("selfdrive reason=steam")]
    [InlineData("all")]
    public void Any_other_line_starts_nothing(string? line)
    {
        Assert.Equal(OfflineStartReason.None, OfflineStart.FromRunLine(line, UiCheckPlan.Tours));
    }

    [Fact]
    public void A_line_far_too_long_starts_nothing()
    {
        Assert.Equal(OfflineStartReason.None, OfflineStart.FromRunLine("offline reason=steam " + new string('x', 400), UiCheckPlan.Tours));
    }

    [Theory]
    [InlineData(OfflineStartReason.None, false, false)]
    [InlineData(OfflineStartReason.Steam, true, false)]
    [InlineData(OfflineStartReason.Relay, true, true)]
    [InlineData(OfflineStartReason.Error, false, false)]
    public void Steam_and_relay_start_the_shell_and_only_relay_tells_the_rows(OfflineStartReason reason, bool shell, bool relay)
    {
        Assert.Equal(shell, OfflineStart.StartsShell(reason));
        Assert.Equal(relay, OfflineStart.RelayRefused(reason));
    }

    [Fact]
    public void The_names_go_both_ways()
    {
        foreach (OfflineStartReason reason in new[] { OfflineStartReason.Steam, OfflineStartReason.Relay, OfflineStartReason.Error })
        {
            Assert.Equal(reason, OfflineStart.Parse(OfflineStart.Name(reason)));
            Assert.True(OfflineStart.Key.Allows(OfflineStart.Name(reason)));
        }

        Assert.Equal(string.Empty, OfflineStart.Name(OfflineStartReason.None));
    }

    [Fact]
    public void The_lines_say_it_was_the_developers_start()
    {
        Assert.Equal("Online: off for this start: the developer's offline start (steam).", OfflineStart.StartLine(OfflineStartReason.Steam));
        Assert.Equal("the developer's offline start (relay)", OfflineStart.OffReason(OfflineStartReason.Relay));
        Assert.Equal("Online: the developer's offline start (error) changes nothing: online is off already (old T2T Online files are in the game folder).",
            OfflineStart.IgnoredLine(OfflineStartReason.Error, "old T2T Online files are in the game folder"));
        Assert.StartsWith("Online: the developer's offline start (error): the runtime stops now", OfflineStart.StopLine, StringComparison.Ordinal);
        Assert.Contains("staged", OfflineStart.StopLine, StringComparison.Ordinal);
    }
}
