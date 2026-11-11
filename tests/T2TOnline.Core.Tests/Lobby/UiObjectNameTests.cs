using T2TOnline.Core.Lobby;

namespace T2TOnline.Core.Tests.Lobby;

/// <summary>Lobby UI objects named after a Steam ID or a lobby ID are written as the row they are.</summary>
public class UiObjectNameTests
{
    [Theory]
    [InlineData("Friend76561199000000002", "a friend's row")]
    [InlineData("Lobby109775240917663232", "a lobby's row")]
    [InlineData("Seat76561199000000002", "Seat<id>")]
    [InlineData("76561199000000002", "<id>")]
    [InlineData("Lobby3", "Lobby3")] // a check tour's made-up lobby: kept, the tour finds it by name
    [InlineData("Seat1", "Seat1")]
    [InlineData("Start", "Start")]
    [InlineData("Build12345678901234", "Build12345678901234")] // 14 digits: not an ID's length
    [InlineData("", "")]
    [InlineData(null, "")]
    public void An_id_in_a_name_is_never_written(string? name, string expected)
    {
        Assert.Equal(expected, UiObjectName.ForLog(name));
    }
}
