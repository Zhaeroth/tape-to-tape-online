using T2TOnline.Core.Campaign;

namespace T2TOnline.Core.Tests.Campaign;

/// <summary>
/// The title's New Run and Abandon Run while the title's campaign buttons aren't reloaded yet for the run file as it now
/// is: held for the moment, never while following a host, never for long.
/// </summary>
public class TitleRunButtonsHoldTests
{
    private const double Cap = TitleRunButtonsHold.CapSeconds;

    private static TitleReload Owed(double since = 10) => new(Following: false, ReloadOwed: true, Since: since, ReloadStartedAt: -1, TitleRefreshing: false);

    private static TitleReload Settled(double since = 10, double reloadAt = 10.2) => new(false, false, since, reloadAt, false);

    [Fact]
    public void A_press_before_the_reload_is_held_and_the_first_says_so()
    {
        // The run file went back to the solo run under an open menu; the menu closed and New Run was pressed before run
        // sync's reload of the title (a quarter of a second at most).
        var hold = new TitleRunButtonsHold();

        Assert.Equal(TitlePress.HeldFirst, hold.Press(Owed(), 10.05));
        Assert.Equal(TitlePress.Held, hold.Press(Owed(), 10.10));
        Assert.Equal(TitlePress.Held, hold.Press(Owed(), 10.20));
    }

    [Fact]
    public void A_press_after_the_reload_passes()
    {
        var hold = new TitleRunButtonsHold();
        Assert.Equal(TitlePress.HeldFirst, hold.Press(Owed(), 10.05));

        Assert.Equal(TitlePress.Passed, hold.Press(Settled(), 10.30));
        Assert.Equal(TitlePress.Passed, hold.Press(Settled(), 500));
    }

    [Fact]
    public void A_reload_that_started_holds_until_the_title_has_finished_loading()
    {
        // Run sync's reload clears "owed" as it starts; the title's buttons change only when its load ends.
        var hold = new TitleRunButtonsHold();
        var running = new TitleReload(false, false, 10, ReloadStartedAt: 10.2, TitleRefreshing: true);

        Assert.Equal(TitlePress.HeldFirst, hold.Press(running, 10.22));
        Assert.Equal(TitlePress.Passed, hold.Press(running with { TitleRefreshing = false }, 10.25));
    }

    [Fact]
    public void A_title_loading_on_its_own_long_after_the_reload_holds_nothing()
    {
        // The title loads its campaign state by itself too (as it comes up): only a load right after run sync's reload
        // started is the one waited for, and a title that never had a reload from run sync waits for none.
        var hold = new TitleRunButtonsHold();

        Assert.Equal(TitlePress.Passed, hold.Press(new TitleReload(false, false, 10, ReloadStartedAt: 10.2, TitleRefreshing: true), 10.2 + Cap));
        Assert.Equal(TitlePress.Passed, hold.Press(new TitleReload(false, false, 0, ReloadStartedAt: -1, TitleRefreshing: true), 3));
        Assert.Equal(TitlePress.Passed, hold.Press(default, 0)); // a game that never switched its run file
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Nothing_is_held_while_following_a_host(bool owed, bool refreshing)
    {
        // A friend's game follows the host's: its runs are the host's, and a press, if one reaches the title, is the game's.
        // (A host's own presses are held like anyone's: its title reloads for the co-op slot as hosting starts.)
        var hold = new TitleRunButtonsHold();

        Assert.Equal(TitlePress.Passed, hold.Press(new TitleReload(Following: true, owed, 10, 10.2, refreshing), 10.21));
        Assert.Equal(TitlePress.Passed, hold.Press(new TitleReload(Following: true, owed, 10, -1, refreshing), 10.21));
    }

    [Fact]
    public void A_reload_that_never_comes_keeps_nobody_from_new_run()
    {
        var hold = new TitleRunButtonsHold();
        Assert.Equal(TitlePress.HeldFirst, hold.Press(Owed(), 20));
        Assert.Equal(TitlePress.Held, hold.Press(Owed(), 20 + Cap - 0.01));

        // From the cap on, counted from the first press held (not from the switch, which may be minutes old under a menu).
        Assert.Equal(TitlePress.PassedLateFirst, hold.Press(Owed(), 20 + Cap));
        Assert.Equal(TitlePress.PassedLate, hold.Press(Owed(), 20 + Cap + 1));
        Assert.Equal(TitlePress.PassedLate, hold.Press(Owed(), 9000));
    }

    [Fact]
    public void A_switch_made_long_ago_under_a_menu_still_holds_the_first_press()
    {
        // Settings stayed open for a minute after the switch: the press right after it closed is the one to hold.
        var hold = new TitleRunButtonsHold();

        Assert.Equal(TitlePress.HeldFirst, hold.Press(Owed(since: 10), 70));
        Assert.Equal(TitlePress.Held, hold.Press(Owed(since: 10), 70.1));
    }

    [Fact]
    public void Each_switch_has_its_own_wait_and_its_own_cap()
    {
        var hold = new TitleRunButtonsHold();
        Assert.Equal(TitlePress.HeldFirst, hold.Press(Owed(since: 10), 10.1));
        Assert.Equal(TitlePress.PassedLateFirst, hold.Press(Owed(since: 10), 10.1 + Cap));

        // The reload came unseen (no press between); the run file switched again later.
        Assert.Equal(TitlePress.HeldFirst, hold.Press(Owed(since: 300), 300.1));
        Assert.Equal(TitlePress.Held, hold.Press(Owed(since: 300), 300.2));
        Assert.Equal(TitlePress.PassedLateFirst, hold.Press(Owed(since: 300), 300.1 + Cap));

        // And a wait that ended with a press seen starts the next one afresh too.
        Assert.Equal(TitlePress.Passed, hold.Press(Settled(since: 300, reloadAt: 304), 305));
        Assert.Equal(TitlePress.HeldFirst, hold.Press(Owed(since: 300), 306));
    }

    [Fact]
    public void Waited_says_when_the_buttons_may_be_stale()
    {
        Assert.True(Owed().Waited(11));
        Assert.False(Settled().Waited(11));
        Assert.True(new TitleReload(false, false, 10, 10.2, true).Waited(10.2 + Cap - 0.01));
        Assert.False(new TitleReload(false, false, 10, 10.2, true).Waited(10.2 + Cap));
        Assert.False(new TitleReload(true, true, 10, 10.2, true).Waited(10.3));
    }
}
