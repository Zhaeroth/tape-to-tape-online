using T2TOnline.Core.Lobby;

namespace T2TOnline.Core.Tests.Lobby;

public class LobbyMeterTests
{
    [Fact]
    public void A_route_by_the_rows_counts_presses_and_seconds_from_the_online_row()
    {
        var meter = new LobbyMeter();
        meter.Press(); // before the Online row: not counted
        meter.OnlineOpened(1_000_000);
        meter.Press();
        meter.Press();
        meter.Press();
        meter.Press();

        Assert.Equal("Lobby: entered by browser, 5 presses, 6.1 s.", meter.Entered(LobbyRoute.Browser, 7_100_000));

        // Counted once: the next one starts over.
        Assert.Equal("Lobby: arrived by browser.", meter.Entered(LobbyRoute.Browser, 8_000_000));
    }

    [Theory]
    [InlineData(LobbyRoute.Invite, "Lobby: arrived by invite.")]
    [InlineData(LobbyRoute.JoinGame, "Lobby: arrived by Join Game.")]
    [InlineData(LobbyRoute.ConnectLobby, "Lobby: arrived by a launch from an invite.")]
    [InlineData(LobbyRoute.Key, "Lobby: arrived by key.")]
    [InlineData(LobbyRoute.Panel, "Lobby: arrived by the F1 panel.")]
    public void A_route_with_no_presses_says_only_how(LobbyRoute route, string line)
    {
        var meter = new LobbyMeter();
        meter.OnlineOpened(0);

        Assert.Equal(line, meter.Entered(route, 1_000_000));
    }

    [Fact]
    public void Hosting_by_the_row_counts_the_same()
    {
        var meter = new LobbyMeter();
        meter.OnlineOpened(0);
        meter.Press();
        Assert.Equal("Lobby: entered by the Host Lobby row, 2 presses, 1.5 s.", meter.Entered(LobbyRoute.HostRow, 1_500_000));
    }

    [Fact]
    public void A_failed_join_says_why_and_the_rows_age()
    {
        Assert.Equal("Lobby: join failed: Full (row age 3 s).", LobbyMeter.JoinFailed(LobbyFailure.Full, 3));
        Assert.Equal("Lobby: join failed: Closed.", LobbyMeter.JoinFailed(LobbyFailure.Closed, -1));
    }

    [Fact]
    public void A_start_with_someone_not_here_says_who_by_tag_and_how_it_went()
    {
        Assert.Equal("Lobby: start waits: P#a1b2c3 isn't here yet; it goes by itself when they are.", LobbyMeter.StartWaits("P#a1b2c3"));
        Assert.Equal("Lobby: start: P#a1b2c3 not here, waited 4 s.", LobbyMeter.StartedWithout("P#a1b2c3", 4_200_000, wentOnAnyway: false));
        Assert.Equal("Lobby: start: P#a1b2c3 not here, started anyway.", LobbyMeter.StartedWithout("P#a1b2c3", 0, wentOnAnyway: true));
        Assert.Equal("Lobby: start: a?b not here, started anyway.", LobbyMeter.StartedWithout("a\nb", 0, wentOnAnyway: true)); // nothing passed can forge a log line
    }

    [Fact]
    public void The_summary_counts_what_a_play_test_watches()
    {
        var meter = new LobbyMeter();
        meter.ReadyToggled();
        meter.ReadyToggled();
        meter.CountdownCancelled();
        meter.LeavePromptAnswered(left: false);
        meter.LeavePromptAnswered(left: true);
        meter.LeavePromptAnswered(left: true);
        meter.FocusRepaired();

        Assert.Equal("Lobby: ready marks: 2; countdown cancels: 1; leave prompt: stayed 1, left 2; focus repaired: 1.", meter.Summary());
    }
}
