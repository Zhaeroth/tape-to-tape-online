using T2TOnline.Core.Lobby;

namespace T2TOnline.Core.Tests.Lobby;

public class SignalBarsTests
{
    [Theory]
    [InlineData(-1, 0, 0)] // unknown
    [InlineData(-500, 50, 0)]
    [InlineData(0, 0, 3)]
    [InlineData(58, 0, 3)] // a relayed link's usual round trip: it plays without noticeable delay
    [InlineData(80, 0, 3)]
    [InlineData(81, 0, 2)]
    [InlineData(150, 0, 2)]
    [InlineData(151, 0, 1)]
    [InlineData(5000, 0, 1)]
    [InlineData(20, 2, 3)] // loss of 2 percent isn't over 2
    [InlineData(20, 2.01, 1)]
    [InlineData(100, 50, 1)]
    public void Bars_follow_the_round_trip_and_loss(int ping, double loss, int bars)
    {
        Assert.Equal(bars, SignalBars.Of(ping, loss));
    }
}
