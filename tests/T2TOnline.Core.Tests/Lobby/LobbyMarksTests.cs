using System;
using T2TOnline.Core.Lobby;

namespace T2TOnline.Core.Tests.Lobby;

public class LobbyMarksTests
{
    [Fact]
    public void Every_seat_stage_has_an_icon_or_a_word_and_a_word_always()
    {
        foreach (SeatStage stage in Enum.GetValues<SeatStage>())
        {
            LobbyMark mark = LobbyMarks.Of(stage, 27);
            Assert.NotEqual(LobbyIcon.None, mark.Icon); // XAG 103: an icon and a word for every state
            Assert.False(string.IsNullOrEmpty(mark.Line.Term));
        }
    }

    [Fact]
    public void Seat_marks_say_what_the_table_says()
    {
        Assert.Equal((LobbyIcon.Plus, LobbyWords.OpenSeat.Key), Pair(LobbyMarks.Of(SeatStage.Open)));
        Assert.Equal((LobbyIcon.Invited, LobbyWords.Invited.Key), Pair(LobbyMarks.Of(SeatStage.Invited)));
        Assert.Equal((LobbyIcon.Dots, LobbyWords.Joining.Key), Pair(LobbyMarks.Of(SeatStage.Joining)));
        Assert.Equal((LobbyIcon.Device, LobbyWords.Here.Key), Pair(LobbyMarks.Of(SeatStage.Here)));
        Assert.Equal((LobbyIcon.Dots, LobbyTerms.Loading), Pair(LobbyMarks.Of(SeatStage.Loading)));
        LobbyMark reconnecting = LobbyMarks.Of(SeatStage.Reconnecting, 27);
        Assert.Equal((LobbyIcon.Clock, LobbyWords.Reconnecting.Key), Pair(reconnecting));
        Assert.Equal("Reconnecting 0:27", reconnecting.Line.Text(0));
    }

    [Fact]
    public void A_seat_gives_its_mark()
    {
        var seat = new LobbySeat { Stage = SeatStage.Reconnecting, ReconnectSeconds = 90 };
        Assert.Equal("Reconnecting 1:30", seat.Mark.Line.Text(0));
    }

    [Fact]
    public void The_clock_reads_minutes_and_seconds_and_never_goes_negative()
    {
        Assert.Equal("0:00", LobbyMarks.Clock(-5));
        Assert.Equal("0:09", LobbyMarks.Clock(9));
        Assert.Equal("2:00", LobbyMarks.Clock(120));
    }

    [Fact]
    public void The_host_and_ready_marks_carry_a_word()
    {
        Assert.Equal((LobbyIcon.Crown, LobbyWords.Host.Key), Pair(LobbyMarks.Host));
        Assert.Equal((LobbyIcon.Ready, LobbyTerms.Ready), Pair(LobbyMarks.Ready(true)));
        Assert.Equal((LobbyIcon.None, LobbyWords.NotReady.Key), Pair(LobbyMarks.Ready(false)));
    }

    [Fact]
    public void The_ribbon_shows_measured_numbers_or_nothing()
    {
        Assert.Null(LobbyMarks.Ribbon(LobbyResult.None));
        Assert.Null(LobbyMarks.Ribbon(new LobbyResult((LobbyResultKind)200, 1, 2)));
        Assert.Equal("Last game: won 4-2", LobbyMarks.Ribbon(new LobbyResult(LobbyResultKind.MatchWon, 4, 2))!.Text(0));
        Assert.Equal("Last game: lost 1-3", LobbyMarks.Ribbon(new LobbyResult(LobbyResultKind.MatchLost, 1, 3))!.Text(0));
        Assert.Equal("Last game: tied 2-2", LobbyMarks.Ribbon(new LobbyResult(LobbyResultKind.MatchTied, 2, 2))!.Text(0));
        Assert.Equal("Last game: 3-1", LobbyMarks.Ribbon(new LobbyResult(LobbyResultKind.MatchScore, 3, 1))!.Text(0));
        Assert.Equal("Run won: Act 3, Floor 9", LobbyMarks.Ribbon(new LobbyResult(LobbyResultKind.RunWon, 3, 9))!.Text(0));
        Assert.Equal("Run over: Act 2, Floor 5", LobbyMarks.Ribbon(new LobbyResult(LobbyResultKind.RunOver, 2, 5))!.Text(0));
        Assert.Equal("Run saved: Act 2, Floor 5", LobbyMarks.Ribbon(new LobbyResult(LobbyResultKind.RunSaved, 2, 5))!.Text(0));
    }

    [Fact]
    public void The_status_line_says_what_the_host_is_busy_with()
    {
        Assert.Null(LobbyMarks.HostStatus(HostActivity.None, "Alex"));
        Assert.Equal("Alex is picking a save", LobbyMarks.HostStatus(HostActivity.PickingSave, "Alex")!.Text(0));
        Assert.Equal("Alex is picking a game mode", LobbyMarks.HostStatus(HostActivity.PickingMode, "Alex")!.Text(0));
        Assert.Equal("Alex is inviting", LobbyMarks.HostStatus(HostActivity.Inviting, "Alex")!.Text(0));
        Assert.Equal("Alex is waiting for everyone", LobbyMarks.HostStatus(HostActivity.WaitingForEveryone, "Alex")!.Text(0));
    }

    private static (LobbyIcon, string) Pair(in LobbyMark mark) => (mark.Icon, mark.Line.Term);
}
