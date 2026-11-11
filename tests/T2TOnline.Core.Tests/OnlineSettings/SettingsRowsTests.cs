using System;
using System.Linq;
using T2TOnline.Core.OnlineSettings;

namespace T2TOnline.Core.Tests.OnlineSettings;

public class SettingsRowsTests
{
    private static readonly SettingsContext Title = new(AtTitle: true, SettingsRole.None, ScreenOpen: false, DirectConnectionsOn: false, RelayRefused: false);

    [Fact]
    public void The_rows_go_in_the_tabs_order_and_each_has_a_kind()
    {
        Assert.Equal(Enum.GetValues<SettingsRowId>(), SettingsRows.Order);
        Assert.Equal(SettingsRowKind.Toggle, SettingsRows.KindOf(SettingsRowId.ShareAchievements));
        Assert.Equal(SettingsRowKind.Toggle, SettingsRows.KindOf(SettingsRowId.ShareProgress));
        Assert.Equal(SettingsRowKind.Choice, SettingsRows.KindOf(SettingsRowId.PlayOnlineWith));
        Assert.Equal(SettingsRowKind.Toggle, SettingsRows.KindOf(SettingsRowId.DirectConnections));
        Assert.Equal(SettingsRowKind.Button, SettingsRows.KindOf(SettingsRowId.SaveBackups));
        Assert.Equal(SettingsRowKind.Button, SettingsRows.KindOf(SettingsRowId.ProblemReport));
        Assert.Equal(SettingsRowKind.Button, SettingsRows.KindOf(SettingsRowId.LeaveOnlineGame));
        Assert.Equal(SettingsRowKind.Button, SettingsRows.KindOf(SettingsRowId.EndOnlineGame));
    }

    [Fact]
    public void This_build_makes_every_row_and_the_title_out_of_a_session_shows_the_six_for_everyone()
    {
        Assert.Equal(SettingsRows.Order, SettingsRows.Built);
        var rows = SettingsRows.For(Title, SettingsRows.Built);
        Assert.Equal(
            new[] { SettingsRowId.ShareAchievements, SettingsRowId.ShareProgress, SettingsRowId.PlayOnlineWith, SettingsRowId.SaveBackups, SettingsRowId.ProblemReport },
            rows.Select(r => r.Id));
        Assert.All(rows, r => Assert.True(r.Usable));
    }

    [Fact]
    public void The_host_in_a_match_sees_end_and_a_locked_save_backups()
    {
        var rows = SettingsRows.For(Title with { AtTitle = false, Role = SettingsRole.Host }, SettingsRows.Built);
        Assert.Contains(rows, r => r.Id == SettingsRowId.EndOnlineGame && r.Usable);
        Assert.DoesNotContain(rows, r => r.Id == SettingsRowId.LeaveOnlineGame);
        Assert.Equal(SettingsRowRefusal.NotAtTitle, rows.Single(r => r.Id == SettingsRowId.SaveBackups).Refusal);
    }

    [Fact]
    public void A_following_friend_at_their_title_sees_leave_and_save_backups_asks_them_to_leave_first()
    {
        var rows = SettingsRows.For(Title with { Role = SettingsRole.Friend }, SettingsRows.Built);
        Assert.Contains(rows, r => r.Id == SettingsRowId.LeaveOnlineGame && r.Usable);
        Assert.Equal(SettingsRowRefusal.InSession, rows.Single(r => r.Id == SettingsRowId.SaveBackups).Refusal);
        Assert.True(rows.Single(r => r.Id == SettingsRowId.ShareProgress).Usable);
        Assert.True(rows.Single(r => r.Id == SettingsRowId.PlayOnlineWith).Usable);
    }

    [Fact]
    public void Save_backups_is_locked_out_of_the_title_first()
    {
        var match = Title with { AtTitle = false, Role = SettingsRole.Host };
        Assert.Equal(SettingsRowRefusal.NotAtTitle, SettingsRows.Refusal(SettingsRowId.SaveBackups, match));
    }

    [Theory]
    [InlineData(SettingsRole.Host)]
    [InlineData(SettingsRole.Friend)]
    public void Save_backups_is_refused_at_the_title_in_a_session(SettingsRole role)
    {
        Assert.Equal(SettingsRowRefusal.InSession, SettingsRows.Refusal(SettingsRowId.SaveBackups, Title with { Role = role }));
    }

    [Fact]
    public void Save_backups_is_refused_with_a_screen_of_the_mods_under_settings()
    {
        Assert.Equal(SettingsRowRefusal.ScreenOpen, SettingsRows.Refusal(SettingsRowId.SaveBackups, Title with { ScreenOpen = true }));
    }

    [Fact]
    public void The_toggles_are_usable_everywhere()
    {
        var match = Title with { AtTitle = false, Role = SettingsRole.Friend, ScreenOpen = true };
        Assert.Equal(SettingsRowRefusal.None, SettingsRows.Refusal(SettingsRowId.ShareAchievements, match));
        Assert.Equal(SettingsRowRefusal.None, SettingsRows.Refusal(SettingsRowId.ProblemReport, match));
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    public void Direct_connections_shows_only_when_on_or_after_a_relay_refusal(bool on, bool refused, bool shown)
    {
        Assert.Equal(shown, SettingsRows.Shown(SettingsRowId.DirectConnections, Title with { DirectConnectionsOn = on, RelayRefused = refused }));
    }

    [Theory]
    [InlineData(SettingsRole.None, false, false)]
    [InlineData(SettingsRole.Friend, true, false)]
    [InlineData(SettingsRole.Host, false, true)]
    public void Leave_is_a_friends_and_end_the_hosts(SettingsRole role, bool leave, bool end)
    {
        SettingsContext context = Title with { Role = role };
        Assert.Equal(leave, SettingsRows.Shown(SettingsRowId.LeaveOnlineGame, context));
        Assert.Equal(end, SettingsRows.Shown(SettingsRowId.EndOnlineGame, context));
    }

    [Fact]
    public void Every_row_built_shows_in_order_with_its_refusal()
    {
        var friend = Title with { AtTitle = false, Role = SettingsRole.Friend, RelayRefused = true };
        var rows = SettingsRows.For(friend, SettingsRows.Order.Reverse().ToArray());
        Assert.Equal(
            new[] { SettingsRowId.ShareAchievements, SettingsRowId.ShareProgress, SettingsRowId.PlayOnlineWith, SettingsRowId.DirectConnections, SettingsRowId.SaveBackups, SettingsRowId.ProblemReport, SettingsRowId.LeaveOnlineGame },
            rows.Select(r => r.Id));
        Assert.False(rows.Single(r => r.Id == SettingsRowId.SaveBackups).Usable);
    }

    [Fact]
    public void Every_refusal_reads_in_the_log()
    {
        foreach (SettingsRowRefusal refusal in Enum.GetValues<SettingsRowRefusal>())
        {
            Assert.False(string.IsNullOrWhiteSpace(SettingsRows.Describe(refusal)));
        }
    }
}
