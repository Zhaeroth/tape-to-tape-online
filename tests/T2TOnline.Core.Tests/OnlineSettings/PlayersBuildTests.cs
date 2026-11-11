using System;
using T2TOnline.Core.Lobby;
using T2TOnline.Core.OnlineSettings;

namespace T2TOnline.Core.Tests.OnlineSettings;

/// <summary>The players' build's rules (D142): the mode line, the native lobby a player can't turn off, and the native homes' names.</summary>
public class PlayersBuildTests
{
    [Fact]
    public void The_mode_line_says_which_build_runs()
    {
        Assert.Equal("Developer tools: off (the players' build).", PlayersBuild.ModeLine(devTools: false));
        Assert.Equal("Developer tools: on (F1, F7, Shift+F7, Ctrl+F5, Ctrl+F6 and the check tours).", PlayersBuild.ModeLine(devTools: true));
    }

    [Theory]
    [InlineData(false, false, true, true)]
    [InlineData(false, true, true, false)]
    [InlineData(true, false, false, false)]
    [InlineData(true, true, true, false)]
    public void The_native_lobby_reads_as_on_without_the_developer_tools(bool devTools, bool inFile, bool on, bool said)
    {
        Assert.Equal(on, PlayersBuild.NativeLobbyOn(devTools, inFile));
        Assert.Equal(said, PlayersBuild.NativeLobbyOverridden(devTools, inFile));
    }

    [Fact]
    public void The_native_homes_are_named_by_the_screens_own_words()
    {
        Assert.Equal("Settings > Online", PlayersBuild.SettingsOnline);
        Assert.Equal("Settings > Online > Save Backups", PlayersBuild.SaveBackups);
        Assert.Equal("Settings > Online > Problem Report", PlayersBuild.ProblemReport);
        Assert.Equal("Settings > Online > Leave Online Game", PlayersBuild.LeaveOnlineGame);
        Assert.Equal("Settings > Online > End Online Game", PlayersBuild.EndOnlineGame);
        Assert.Equal("Settings > Online > Play Online With", PlayersBuild.PlayOnlineWith);
        Assert.Equal("Online > Report a Problem", PlayersBuild.ReportAProblem);
        Assert.Equal("Online > Join Lobby", PlayersBuild.JoinLobby);
        Assert.EndsWith(LobbyWords.SaveBackups.Texts[0], PlayersBuild.SaveBackups, StringComparison.Ordinal);
        Assert.EndsWith(LobbyWords.LeaveOnlineGame.Texts[0], PlayersBuild.LeaveOnlineGame, StringComparison.Ordinal);

        // The same path the words that send a player there use (the notices' words).
        Assert.Contains(PlayersBuild.SaveBackups, LobbyWords.NoticeSaveCheck.Texts[0], StringComparison.Ordinal);
    }

    [Fact]
    public void A_report_is_made_where_the_native_screens_are_and_by_a_held_insert_elsewhere()
    {
        Assert.Equal("make a problem report in Settings > Online", PlayersBuild.ReportAdvice(nativeScreens: true));
        Assert.Equal("hold Insert for 2 seconds to make a problem report", PlayersBuild.ReportAdvice(nativeScreens: false));
    }

    [Fact]
    public void The_way_out_is_F7_for_a_developer_and_the_row_by_role_for_a_player()
    {
        Assert.Equal("F7", PlayersBuild.Leave(devTools: true, host: true));
        Assert.Equal("F7", PlayersBuild.Leave(devTools: true, host: false));
        Assert.Equal(PlayersBuild.EndOnlineGame, PlayersBuild.Leave(devTools: false, host: true));
        Assert.Equal(PlayersBuild.LeaveOnlineGame, PlayersBuild.Leave(devTools: false, host: false));
    }

    [Fact]
    public void Without_the_lobby_a_player_is_told_what_still_works()
    {
        Assert.Contains("F1, F7", PlayersBuild.WithoutLobby(devTools: true), StringComparison.Ordinal);
        Assert.Contains("Join Game", PlayersBuild.WithoutLobby(devTools: false), StringComparison.Ordinal);
        Assert.Contains("Online row", PlayersBuild.WithoutLobby(devTools: false), StringComparison.Ordinal);
    }

    [Fact]
    public void The_offline_notices_last_line_names_what_works_in_that_state()
    {
        Assert.Equal("Settings > Online has Problem Report and Save Backups.", PlayersBuild.OfflineNoticeLine(nativeScreens: true, devTools: false));
        Assert.Equal(LobbyWords.HoldInsert.Texts[0], PlayersBuild.OfflineNoticeLine(nativeScreens: false, devTools: false));
        Assert.EndsWith(" F1 opens the mod's panel.", PlayersBuild.OfflineNoticeLine(nativeScreens: true, devTools: true), StringComparison.Ordinal);
        Assert.StartsWith(LobbyWords.HoldInsert.Texts[0], PlayersBuild.OfflineNoticeLine(nativeScreens: false, devTools: true), StringComparison.Ordinal);
    }
}
