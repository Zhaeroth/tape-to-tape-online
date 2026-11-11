using T2TOnline.Core.Campaign;

namespace T2TOnline.Core.Tests.Campaign;

/// <summary>
/// A friend's own new-unlocks screen at the end of a host's run the host's game showed none for: when it shows, that it
/// never shows twice, and that nothing holds the way home for good.
/// </summary>
public class OwnUnlocksScreenTests
{
    private static OwnUnlocksFacts Ended(int own = 3, bool hostScreen = false, int waiting = 0, long waited = 0, bool ready = true) =>
        new(RunEnded: true, HostScreenCame: hostScreen, OwnUnlocks: own, BatchesWaiting: waiting, WaitedMicros: waited, MenuReady: ready);

    [Fact]
    public void A_run_that_ended_with_no_menu_of_the_hosts_shows_this_players_own_unlocks_before_the_way_home()
    {
        Assert.Equal(OwnUnlocksChoice.Show, OwnUnlocksScreen.Decide(Ended(), out string why));
        Assert.Empty(why);
    }

    [Fact]
    public void Never_a_second_screen_when_the_hosts_showed_this_players_own_and_none_without_a_runs_end_here()
    {
        Assert.Equal(OwnUnlocksChoice.GoHome, OwnUnlocksScreen.Decide(Ended(hostScreen: true), out string shown));
        Assert.Empty(shown); // the host's copy here showed them: nothing to say

        // The host went to its title without a run's end here (a quit from the map, a Quick Game, a late join after it).
        Assert.Equal(OwnUnlocksChoice.GoHome, OwnUnlocksScreen.Decide(Ended() with { RunEnded = false }, out string quiet));
        Assert.Empty(quiet);
    }

    [Fact]
    public void Nothing_of_this_players_own_goes_home_as_the_game_does_and_says_why()
    {
        Assert.Equal(OwnUnlocksChoice.GoHome, OwnUnlocksScreen.Decide(Ended(own: 0), out string none));
        Assert.Contains("nothing new", none);

        Assert.Equal(OwnUnlocksChoice.GoHome, OwnUnlocksScreen.Decide(Ended(ready: false), out string unsafeMenu));
        Assert.Contains("can't be shown safely", unsafeMenu);
    }

    [Fact]
    public void The_hosts_run_end_exit_says_whether_its_new_unlocks_menu_follows_and_this_players_own_takes_the_games_transition()
    {
        // The game's run-end menu exits with the loading transition when no new-unlocks menu follows, the progress one when it does.
        Assert.True(OwnUnlocksScreen.HostShowsNone(MenuTransition.Loading));
        Assert.False(OwnUnlocksScreen.HostShowsNone(MenuTransition.Progress));
        Assert.False(OwnUnlocksScreen.HostShowsNone(MenuTransition.None));

        Assert.Equal(MenuTransition.Progress, OwnUnlocksScreen.ExitBefore(MenuTransition.Loading, OwnUnlocksChoice.Show));
        Assert.Equal(MenuTransition.Loading, OwnUnlocksScreen.ExitBefore(MenuTransition.Loading, OwnUnlocksChoice.GoHome));
        Assert.Equal(MenuTransition.Loading, OwnUnlocksScreen.ExitBefore(MenuTransition.Loading, OwnUnlocksChoice.Wait));
        Assert.Equal(MenuTransition.Progress, OwnUnlocksScreen.ExitBefore(MenuTransition.Progress, OwnUnlocksChoice.GoHome));
        Assert.Equal(MenuTransition.None, OwnUnlocksScreen.ExitBefore(MenuTransition.None, OwnUnlocksChoice.Show));
    }

    [Fact]
    public void Shared_progress_already_here_applies_first_for_a_few_seconds_at_most()
    {
        Assert.Equal(OwnUnlocksChoice.Wait, OwnUnlocksScreen.Decide(Ended(own: 0, waiting: 2, waited: 0), out _));
        Assert.Equal(OwnUnlocksChoice.Wait, OwnUnlocksScreen.Decide(Ended(waiting: 1, waited: OwnUnlocksScreen.BatchWaitMicros - 1), out _));
        Assert.Equal(OwnUnlocksChoice.Show, OwnUnlocksScreen.Decide(Ended(waiting: 1, waited: OwnUnlocksScreen.BatchWaitMicros), out _));

        // Not when the host's menu showed them, or nothing ended here: the way home doesn't wait for nothing.
        Assert.Equal(OwnUnlocksChoice.GoHome, OwnUnlocksScreen.Decide(Ended(hostScreen: true, waiting: 4), out _));
        Assert.Equal(OwnUnlocksChoice.GoHome, OwnUnlocksScreen.Decide(Ended(waiting: 4) with { RunEnded = false }, out _));
    }

    [Fact]
    public void The_way_home_waits_while_it_is_asked_for_and_up_and_goes_on_once_it_closes()
    {
        var screen = new OwnUnlocksScreen();
        Assert.False(screen.Holding);
        Assert.False(screen.Up(0)); // not asked for
        Assert.True(screen.TryStart(1_000));
        Assert.True(screen.Holding);
        Assert.False(screen.TryStart(2_000)); // one at a time
        Assert.True(screen.Up(500_000));
        Assert.Equal(1, screen.Shown);
        Assert.True(screen.Holding);
        Assert.Equal(1_500_000, screen.UpMicros(2_000_000));
        Assert.Equal(1_999_000, screen.HeldMicros(2_000_000));

        Assert.True(screen.Finish());
        Assert.False(screen.Holding);
        Assert.Equal(OwnUnlocksState.Done, screen.State);
        Assert.Equal(0, screen.HeldMicros(3_000_000));

        // Never twice for one run's end.
        Assert.False(screen.TryStart(4_000_000));
        Assert.False(screen.Finish());
    }

    [Fact]
    public void A_screen_that_never_shows_or_stays_up_too_long_is_said_once_so_nothing_holds_the_way_home_for_good()
    {
        var screen = new OwnUnlocksScreen();
        Assert.True(screen.TryStart(0));
        Assert.Equal(OwnUnlocksTick.None, screen.Update(OwnUnlocksScreen.OpenTimeoutMicros));
        Assert.Equal(OwnUnlocksTick.OpenTimedOut, screen.Update(OwnUnlocksScreen.OpenTimeoutMicros + 1));
        Assert.Equal(OwnUnlocksTick.None, screen.Update(OwnUnlocksScreen.OpenTimeoutMicros + 2));

        var slow = new OwnUnlocksScreen();
        Assert.True(slow.TryStart(0));
        Assert.True(slow.Up(9_000_000)); // just in time
        Assert.Equal(OwnUnlocksTick.None, slow.Update(30_000_000));
        Assert.Equal(OwnUnlocksTick.None, slow.Update(9_000_000 + OwnUnlocksScreen.MaxOpenMicros));
        Assert.Equal(OwnUnlocksTick.OpenTooLong, slow.Update(9_000_001 + OwnUnlocksScreen.MaxOpenMicros));
        Assert.Equal(OwnUnlocksTick.None, slow.Update(10_000_000 + OwnUnlocksScreen.MaxOpenMicros));
        Assert.True(slow.Finish());
        Assert.Equal(OwnUnlocksTick.None, slow.Update(20_000_000 + OwnUnlocksScreen.MaxOpenMicros));
    }

    [Fact]
    public void A_later_run_end_has_its_own_turn_but_never_takes_one_that_still_holds_the_way_home()
    {
        var screen = new OwnUnlocksScreen();
        Assert.True(screen.Finish()); // decided against, or shown
        Assert.True(screen.NextRunEnd());
        Assert.Equal(OwnUnlocksState.Idle, screen.State);
        Assert.True(screen.TryStart(0));
        Assert.False(screen.NextRunEnd()); // the one up stays up
        Assert.True(screen.Holding);
    }
}
