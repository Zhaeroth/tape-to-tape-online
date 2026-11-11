using System;
using T2TOnline.Core.OnlineSettings;

namespace T2TOnline.Core.Tests.OnlineSettings;

public class DirectConnectionsRuleTests
{
    [Fact]
    public void Turning_it_on_asks_first()
    {
        Assert.Equal(DirectConnectionsPress.Ask, DirectConnectionsRule.OnPress(savedOn: false, toggledTo: true, tourRunning: false, othersInSession: false));
    }

    [Fact]
    public void Turning_it_off_is_at_once()
    {
        Assert.Equal(DirectConnectionsPress.TurnOff, DirectConnectionsRule.OnPress(savedOn: true, toggledTo: false, tourRunning: false, othersInSession: false));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void No_change_is_nothing(bool on)
    {
        Assert.Equal(DirectConnectionsPress.Nothing, DirectConnectionsRule.OnPress(on, on, tourRunning: false, othersInSession: false));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public void A_check_tour_never_changes_it(bool saved, bool toggled)
    {
        Assert.Equal(DirectConnectionsPress.Refuse, DirectConnectionsRule.OnPress(saved, toggled, tourRunning: true, othersInSession: false));
        DirectConnectionsPress withOthers = DirectConnectionsRule.OnPress(saved, toggled, tourRunning: true, othersInSession: true);
        Assert.True(withOthers is DirectConnectionsPress.Refuse or DirectConnectionsPress.RefuseInSession, withOthers.ToString());
    }

    [Fact]
    public void A_check_tour_with_others_in_the_online_game_gets_the_sessions_refusal_for_turning_it_on()
    {
        // Both refusals leave it off: the session's answers for the session, so a tour with its one-PC friend in shows the
        // game's notice and its line, as a player with a friend in sees them.
        Assert.Equal(DirectConnectionsPress.RefuseInSession, DirectConnectionsRule.OnPress(savedOn: false, toggledTo: true, tourRunning: true, othersInSession: true));
    }

    [Theory]
    [InlineData(true, false)] // turning it off: a tour never changes it, whoever is in the game
    [InlineData(false, false)] // no change
    [InlineData(true, true)] // no change
    public void A_check_tour_with_others_in_keeps_its_own_refusal_for_every_other_press(bool saved, bool toggled)
    {
        Assert.Equal(DirectConnectionsPress.Refuse, DirectConnectionsRule.OnPress(saved, toggled, tourRunning: true, othersInSession: true));
    }

    [Fact]
    public void Every_press_that_isnt_refused_is_answered_as_before_the_tour_rule()
    {
        // Out of a tour the rule is the session's and the player's alone: all eight cases, as they always were.
        foreach (bool saved in new[] { false, true })
        {
            foreach (bool toggled in new[] { false, true })
            {
                foreach (bool others in new[] { false, true })
                {
                    DirectConnectionsPress expected = toggled == saved ? DirectConnectionsPress.Nothing
                        : !toggled ? DirectConnectionsPress.TurnOff
                        : others ? DirectConnectionsPress.RefuseInSession
                        : DirectConnectionsPress.Ask;
                    Assert.Equal(expected, DirectConnectionsRule.OnPress(saved, toggled, tourRunning: false, others));
                }
            }
        }
    }

    [Fact]
    public void Turning_it_on_is_not_asked_while_someone_else_is_in_the_online_game()
    {
        // The question is the player's own consent to show their address; a friend's press can answer a menu on the host's screen.
        Assert.Equal(DirectConnectionsPress.RefuseInSession, DirectConnectionsRule.OnPress(savedOn: false, toggledTo: true, tourRunning: false, othersInSession: true));
    }

    [Fact]
    public void Turning_it_off_is_allowed_with_others_in_the_online_game()
    {
        Assert.Equal(DirectConnectionsPress.TurnOff, DirectConnectionsRule.OnPress(savedOn: true, toggledTo: false, tourRunning: false, othersInSession: true));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void No_change_is_nothing_with_others_in_the_online_game_too(bool on)
    {
        Assert.Equal(DirectConnectionsPress.Nothing, DirectConnectionsRule.OnPress(on, on, tourRunning: false, othersInSession: true));
    }

    [Theory]
    [InlineData(SettingsRole.None, 0, false)]
    [InlineData(SettingsRole.None, 3, false)] // no session: a count left over means nothing
    [InlineData(SettingsRole.Host, 0, false)] // a host alone in their lobby
    [InlineData(SettingsRole.Host, 1, true)]
    [InlineData(SettingsRole.Host, 3, true)]
    [InlineData(SettingsRole.Friend, 0, true)] // in a host's session: the host is the other
    [InlineData(SettingsRole.Friend, 2, true)]
    public void Someone_else_is_in_the_online_game_for_a_friend_and_for_a_host_with_anyone_in_it(SettingsRole role, int othersHosted, bool others)
    {
        Assert.Equal(others, DirectConnectionsRule.OthersInSession(role, othersHosted));
    }

    [Fact]
    public void After_an_error_or_with_online_off_no_one_else_is_in_it()
    {
        // The rows' session role is none then, whatever a flow stopped half-way says: the question can be put.
        var stale = new SettingsContext(AtTitle: true, SettingsRole.Friend, ScreenOpen: false, DirectConnectionsOn: false, RelayRefused: true, SettingsState.AfterError);
        Assert.False(DirectConnectionsRule.OthersInSession(stale.SessionRole, othersHosted: 2));
    }

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)] // someone joined while the question was up
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    public void The_answer_is_saved_only_if_still_no_one_else_is_in_it(bool tourRunning, bool othersInSession, bool may)
    {
        Assert.Equal(may, DirectConnectionsRule.MayTurnOn(tourRunning, othersInSession));
    }

    [Fact]
    public void The_marker_holds_the_time_in_utc_and_nothing_else()
    {
        string text = DirectConnectionsRule.MarkerText(new DateTime(2026, 11, 6, 18, 30, 5, DateTimeKind.Utc));
        Assert.Equal("Steam refused relay-only connections at 2026-11-06T18:30:05Z (UTC).\n", text);
        Assert.True(DirectConnectionsRule.IsMarker(text));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("something else")]
    public void Anything_else_isnt_a_marker(string? text)
    {
        Assert.False(DirectConnectionsRule.IsMarker(text));
    }

    [Fact]
    public void The_rows_show_direct_connections_by_the_same_rule()
    {
        var off = new SettingsContext(AtTitle: true, SettingsRole.None, ScreenOpen: false, DirectConnectionsOn: false, RelayRefused: false);
        Assert.False(SettingsRows.Shown(SettingsRowId.DirectConnections, off));
        Assert.True(SettingsRows.Shown(SettingsRowId.DirectConnections, off with { RelayRefused = true }));
        Assert.True(SettingsRows.Shown(SettingsRowId.DirectConnections, off with { DirectConnectionsOn = true }));
    }
}
