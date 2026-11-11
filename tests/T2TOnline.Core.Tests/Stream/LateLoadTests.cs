using T2TOnline.Core.Stream;

namespace T2TOnline.Core.Tests.Stream;

public class LateLoadTests
{
    [Fact]
    public void A_load_that_only_waited_brings_nothing_in_so_nothing_is_left()
    {
        // Waiting for the host's run or custom content: no scene load began, and the next match is the player's own.
        Assert.False(LateLoad.LeaveWhenIn(backToMenu: true, sceneLoadStarted: false, matchCameIn: false));
    }

    [Fact]
    public void A_load_the_loading_screen_has_started_is_left_once_it_is_in()
    {
        Assert.True(LateLoad.LeaveWhenIn(backToMenu: true, sceneLoadStarted: true, matchCameIn: false));
    }

    [Fact]
    public void A_match_that_came_in_on_the_loads_last_frame_is_left_too()
    {
        // The game has finished the load (nothing is loading any more) but the session ended before its own steps took
        // the match up: without this the host's match stayed on screen with no session.
        Assert.True(LateLoad.LeaveWhenIn(backToMenu: true, sceneLoadStarted: false, matchCameIn: true));
    }

    [Fact]
    public void Nothing_is_left_as_the_game_closes()
    {
        Assert.False(LateLoad.LeaveWhenIn(backToMenu: false, sceneLoadStarted: true, matchCameIn: true));
    }
}
