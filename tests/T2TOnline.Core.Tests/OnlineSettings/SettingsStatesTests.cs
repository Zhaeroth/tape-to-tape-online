using System;
using System.Linq;
using T2TOnline.Core.OnlineSettings;

namespace T2TOnline.Core.Tests.OnlineSettings;

/// <summary>The Online tab's rows in each state: online on, online off from start-up, after the runtime stopped on an error.</summary>
public class SettingsStatesTests
{
    private static readonly SettingsContext Title = new(AtTitle: true, SettingsRole.None, ScreenOpen: false, DirectConnectionsOn: false, RelayRefused: false);

    private static readonly SettingsRowId[] Everyday =
    {
        SettingsRowId.ShareAchievements, SettingsRowId.ShareProgress, SettingsRowId.PlayOnlineWith, SettingsRowId.SaveBackups, SettingsRowId.ProblemReport,
    };

    [Fact]
    public void A_context_made_as_before_means_online_on()
    {
        Assert.Equal(SettingsState.OnlineOn, Title.State);
        Assert.Equal(SettingsRole.Host, (Title with { Role = SettingsRole.Host }).SessionRole);
    }

    [Theory]
    [InlineData(SettingsRole.None, new SettingsRowId[0])]
    [InlineData(SettingsRole.Friend, new[] { SettingsRowId.LeaveOnlineGame })]
    [InlineData(SettingsRole.Host, new[] { SettingsRowId.EndOnlineGame })]
    public void Online_on_shows_every_everyday_row_and_the_roles_own(SettingsRole role, SettingsRowId[] extra)
    {
        var rows = SettingsRows.For(Title with { Role = role }, SettingsRows.Order).Select(r => r.Id);
        Assert.Equal(Everyday.Concat(extra).OrderBy(id => id), rows.OrderBy(id => id));
    }

    [Theory]
    [InlineData(SettingsState.OnlineOff, SettingsRole.None)]
    [InlineData(SettingsState.OnlineOff, SettingsRole.Host)]
    [InlineData(SettingsState.AfterError, SettingsRole.Friend)]
    [InlineData(SettingsState.AfterError, SettingsRole.Host)]
    public void Offline_and_after_an_error_no_leave_or_end_whatever_a_stale_flow_says(SettingsState state, SettingsRole role)
    {
        SettingsContext context = Title with { Role = role, State = state };
        Assert.Equal(SettingsRole.None, context.SessionRole);
        Assert.Equal(Everyday, SettingsRows.For(context, SettingsRows.Order).Select(r => r.Id));
        Assert.All(SettingsRows.For(context, SettingsRows.Order), r => Assert.True(r.Usable, r.Id.ToString()));
    }

    [Fact]
    public void The_look_that_asks_whether_the_rows_still_fit_reads_the_role_as_the_rows_were_shown_for_it()
    {
        // The rows remember the context's session role; the look each frame asks the same rule with the state and this PC's part. Were the look to
        // take the part alone, a flow an error stopped half-way (still "friend", online off) would differ from the rows' "none" on every frame.
        foreach (SettingsState state in Enum.GetValues<SettingsState>())
        {
            foreach (SettingsRole role in Enum.GetValues<SettingsRole>())
            {
                SettingsRole shownFor = (Title with { Role = role, State = state }).SessionRole;
                Assert.Equal(shownFor, SettingsStates.SessionRole(state, role));
            }
        }

        Assert.Equal(SettingsRole.None, SettingsStates.SessionRole(SettingsState.AfterError, SettingsRole.Friend));
        Assert.Equal(SettingsRole.None, SettingsStates.SessionRole(SettingsState.OnlineOff, SettingsRole.Host));
        Assert.Equal(SettingsRole.Friend, SettingsStates.SessionRole(SettingsState.OnlineOn, SettingsRole.Friend));
    }

    [Theory]
    [InlineData(SettingsState.OnlineOff)]
    [InlineData(SettingsState.AfterError)]
    public void Save_backups_is_usable_at_the_title_and_locked_away_from_it(SettingsState state)
    {
        SettingsContext context = Title with { Role = SettingsRole.Friend, State = state };
        Assert.Equal(SettingsRowRefusal.None, SettingsRows.Refusal(SettingsRowId.SaveBackups, context));
        Assert.Equal(SettingsRowRefusal.NotAtTitle, SettingsRows.Refusal(SettingsRowId.SaveBackups, context with { AtTitle = false }));
        Assert.Equal(SettingsRowRefusal.ScreenOpen, SettingsRows.Refusal(SettingsRowId.SaveBackups, context with { ScreenOpen = true }));
    }

    [Theory]
    [InlineData(SettingsState.OnlineOn)]
    [InlineData(SettingsState.OnlineOff)]
    [InlineData(SettingsState.AfterError)]
    public void Direct_connections_follows_its_own_rule_in_every_state(SettingsState state)
    {
        SettingsContext context = Title with { State = state };
        Assert.False(SettingsRows.Shown(SettingsRowId.DirectConnections, context));
        Assert.True(SettingsRows.Shown(SettingsRowId.DirectConnections, context with { RelayRefused = true }));
        Assert.True(SettingsRows.Shown(SettingsRowId.DirectConnections, context with { DirectConnectionsOn = true }));
    }

    [Fact]
    public void After_an_error_the_tab_lands_on_problem_report()
    {
        Assert.Equal(SettingsRowId.ProblemReport, SettingsStates.Landing(Title with { State = SettingsState.AfterError }, SettingsRows.Order));
    }

    [Theory]
    [InlineData(SettingsState.OnlineOn)]
    [InlineData(SettingsState.OnlineOff)]
    public void Otherwise_it_lands_on_the_first_row(SettingsState state)
    {
        Assert.Equal(SettingsRowId.ShareAchievements, SettingsStates.Landing(Title with { State = state }, SettingsRows.Order));
    }

    [Fact]
    public void Without_problem_report_built_an_error_lands_on_the_first_row_and_with_no_rows_nowhere()
    {
        SettingsContext context = Title with { State = SettingsState.AfterError };
        Assert.Equal(SettingsRowId.SaveBackups, SettingsStates.Landing(context, new[] { SettingsRowId.SaveBackups }));
        Assert.Null(SettingsStates.Landing(context, Array.Empty<SettingsRowId>()));
        Assert.Null(SettingsStates.Landing(context, new[] { SettingsRowId.LeaveOnlineGame }));
        Assert.Throws<ArgumentNullException>(() => SettingsStates.Landing(context, null!));
    }

    [Fact]
    public void The_log_names_each_state()
    {
        Assert.Equal("online on", SettingsStates.Describe(SettingsState.OnlineOn));
        Assert.Equal("online off", SettingsStates.Describe(SettingsState.OnlineOff));
        Assert.Equal("after an error", SettingsStates.Describe(SettingsState.AfterError));
    }
}
