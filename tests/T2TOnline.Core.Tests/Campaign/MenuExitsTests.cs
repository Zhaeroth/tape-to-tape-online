using T2TOnline.Core.Campaign;

namespace T2TOnline.Core.Tests.Campaign;

public class MenuExitsTests
{
    [Fact]
    public void Six_menus_show_a_transition_and_three_of_them_show_it_first()
    {
        foreach (string type in new[] { "ChooseMetaMenu", "BenchwarmerMenu", "OldSquadMenu", "PermanentUnlockMenu", "RunEndMenu", "ControllerAssignmentMenu" })
        {
            Assert.True(MenuExits.ShowsTransition(type), type);
        }

        Assert.False(MenuExits.ShowsTransition("RelicRewardMenu"));
        Assert.False(MenuExits.ShowsTransition("EventChoiceMenu"));
        Assert.False(MenuExits.ShowsTransition(string.Empty));
        Assert.True(MenuExits.TransitionFirst("PermanentUnlockMenu"));
        Assert.True(MenuExits.TransitionFirst("RunEndMenu"));
        Assert.True(MenuExits.TransitionFirst("ControllerAssignmentMenu"));
        Assert.False(MenuExits.TransitionFirst("ChooseMetaMenu"));
        Assert.False(MenuExits.TransitionFirst("BenchwarmerMenu"));
        Assert.False(MenuExits.TransitionFirst("OldSquadMenu"));
    }

    [Fact]
    public void Each_exit_picks_the_transition_its_hide_shows()
    {
        // The squad screen (ChooseMetaMenu.<Hide>d__119): backed out, on, or a load (tutorial / General Manager).
        Assert.Equal(MenuTransition.Regress, MenuExits.ForChooseMeta(confirmed: false, loadsRun: false));
        Assert.Equal(MenuTransition.Regress, MenuExits.ForChooseMeta(confirmed: false, loadsRun: true));
        Assert.Equal(MenuTransition.Progress, MenuExits.ForChooseMeta(confirmed: true, loadsRun: false));
        Assert.Equal(MenuTransition.Loading, MenuExits.ForChooseMeta(confirmed: true, loadsRun: true));

        // The lineup (BenchwarmerMenu.<Hide>d__115).
        Assert.Equal(MenuTransition.Loading, MenuExits.ForBenchwarmer(true));
        Assert.Equal(MenuTransition.Regress, MenuExits.ForBenchwarmer(false));

        // The old squad menus (OldSquadMenu.<Hide>d__110): mode 1 is the superstar pick.
        Assert.Equal(MenuTransition.Progress, MenuExits.ForOldSquad(MenuExits.SuperStarOnlyMode));
        Assert.Equal(MenuTransition.Loading, MenuExits.ForOldSquad(0));
        Assert.Equal(MenuTransition.Loading, MenuExits.ForOldSquad(7));

        // The new-unlocks menu and the run-end menu.
        Assert.Equal(MenuTransition.Loading, MenuExits.ForPermanentUnlock(skipTransition: false));
        Assert.Equal(MenuTransition.None, MenuExits.ForPermanentUnlock(skipTransition: true));
        Assert.Equal(MenuTransition.None, MenuExits.ForRunEnd(playsTransition: false, visibleUnlocks: true));
        Assert.Equal(MenuTransition.Progress, MenuExits.ForRunEnd(playsTransition: true, visibleUnlocks: true));
        Assert.Equal(MenuTransition.Loading, MenuExits.ForRunEnd(playsTransition: true, visibleUnlocks: false));
    }

    [Fact]
    public void Transitions_have_the_game_s_names()
    {
        Assert.Equal("WooshProgress", MenuExits.Name(MenuTransition.Progress));
        Assert.Equal("WooshLoading", MenuExits.Name(MenuTransition.Loading));
        Assert.Equal("WooshRegress", MenuExits.Name(MenuTransition.Regress));
        Assert.Equal("no transition", MenuExits.Name(MenuTransition.None));
    }

    [Fact]
    public void A_transition_is_a_stray_once_the_screen_has_settled_long_enough()
    {
        var stray = new StrayTransition(10);
        Assert.False(stray.Watch(11, loading: false, settled: false));
        Assert.False(stray.Watch(12, loading: false, settled: true));
        Assert.False(stray.Watch(15.9, loading: false, settled: true));
        Assert.True(stray.Watch(16, loading: false, settled: true));
        Assert.True(stray.Due);
        Assert.Equal(4, stray.SettledFor, 3);

        // Due stays due.
        Assert.True(stray.Watch(16.5, loading: true, settled: false));
    }

    [Fact]
    public void Settling_starts_again_after_the_screen_moves_and_loading_never_counts()
    {
        var stray = new StrayTransition(0);
        Assert.False(stray.Watch(1, loading: false, settled: true));
        Assert.False(stray.Watch(4, loading: false, settled: false)); // a menu opening: not settled any more
        Assert.False(stray.Watch(5, loading: false, settled: true));
        Assert.False(stray.Watch(8.9, loading: false, settled: true));
        Assert.True(stray.Watch(9, loading: false, settled: true));

        // A long load never makes it a stray (the next scene's own code takes a loading transition down).
        var load = new StrayTransition(0);
        for (double t = 0.25; t <= 60; t += 0.25)
        {
            Assert.False(load.Watch(t, loading: true, settled: false));
        }

        Assert.False(load.Watch(61, loading: false, settled: true));
        Assert.True(load.Watch(65, loading: false, settled: true));
    }

    [Fact]
    public void A_way_back_goes_as_soon_as_the_screen_is_settled_and_a_way_on_a_moment_later()
    {
        Assert.Equal(0, StrayTransition.SettleSeconds(MenuTransition.Regress));
        Assert.Equal(1, StrayTransition.SettleSeconds(MenuTransition.Progress));
        Assert.Equal(StrayTransition.StraySeconds, StrayTransition.SettleSeconds(MenuTransition.Loading));

        var back = new StrayTransition(0, StrayTransition.SettleSeconds(MenuTransition.Regress));
        Assert.False(back.Watch(0.25, loading: false, settled: false)); // its show isn't complete yet
        Assert.True(back.Watch(0.5, loading: false, settled: true));
        Assert.Equal(0, back.SettledFor);

        // A way on waits while the next menu is still opening here (not settled), then a moment for one to come.
        var on = new StrayTransition(0, StrayTransition.SettleSeconds(MenuTransition.Progress));
        Assert.False(on.Watch(3, loading: false, settled: false));
        Assert.False(on.Watch(3.25, loading: false, settled: true));
        Assert.False(on.Watch(4, loading: false, settled: true));
        Assert.True(on.Watch(4.25, loading: false, settled: true));
    }

    [Fact]
    public void A_load_that_never_ends_does_not_leave_the_screen_covered()
    {
        var stray = new StrayTransition(5);
        Assert.False(stray.Watch(5 + StrayTransition.MaxLoadingSeconds - 0.1, loading: true, settled: false));
        Assert.True(stray.Watch(5 + StrayTransition.MaxLoadingSeconds, loading: true, settled: false));
        Assert.True(stray.SettledFor < 0);
    }

    [Theory]
    [InlineData(true, false, false, true, true)] // the relic menu, post-game
    [InlineData(true, false, true, true, false)] // a match's forced pick: its hide goes straight on
    [InlineData(false, true, false, true, true)] // the power-up menu, post-game
    [InlineData(false, true, true, true, true)] // the power-up menu has no forced pick
    [InlineData(true, false, false, false, false)] // a reward on the map: the game's fade does nothing
    [InlineData(false, true, false, false, false)]
    [InlineData(false, false, false, true, false)] // any other menu
    public void A_reward_menu_fades_on_its_close_as_its_own_hide_does(bool relic, bool powerUp, bool forced, bool postGame, bool fades) =>
        Assert.Equal(fades, RewardFadeRules.FadesOnClose(relic, powerUp, forced, postGame));

    [Fact]
    public void The_reward_fade_never_stays_black()
    {
        Assert.False(RewardFadeRules.GoesBack(nextScreen: false, sceneChanged: false, loading: false, blackFor: 1));
        Assert.True(RewardFadeRules.GoesBack(nextScreen: true, sceneChanged: false, loading: false, blackFor: 0));
        Assert.True(RewardFadeRules.GoesBack(nextScreen: false, sceneChanged: true, loading: true, blackFor: 0));
        Assert.True(RewardFadeRules.GoesBack(nextScreen: false, sceneChanged: false, loading: false, blackFor: RewardFadeRules.MaxBlackSeconds));

        // A load takes the cover with its scene: it isn't pulled back mid-load.
        Assert.False(RewardFadeRules.GoesBack(nextScreen: false, sceneChanged: false, loading: true, blackFor: RewardFadeRules.MaxBlackSeconds + 30));
    }

    [Fact]
    public void The_cover_before_a_reward_menu_goes_on_every_path()
    {
        // Nothing yet: it stays.
        Assert.False(RewardFadeRules.BeforeMenuGoesBack(nextScreen: false, sceneChanged: false, blackFor: 0.5, hostMenuUpFor: -1));

        // The menu's up here, or this PC's scene changed.
        Assert.True(RewardFadeRules.BeforeMenuGoesBack(nextScreen: true, sceneChanged: false, blackFor: 0, hostMenuUpFor: -1));
        Assert.True(RewardFadeRules.BeforeMenuGoesBack(nextScreen: false, sceneChanged: true, blackFor: 0, hostMenuUpFor: -1));

        // The host's menu is up: a moment for this PC's copy, then the arena rather than black.
        Assert.False(RewardFadeRules.BeforeMenuGoesBack(false, false, blackFor: 1, hostMenuUpFor: RewardFadeRules.AfterHostMenuSeconds - 0.1));
        Assert.True(RewardFadeRules.BeforeMenuGoesBack(false, false, blackFor: 1, hostMenuUpFor: RewardFadeRules.AfterHostMenuSeconds));

        // A drop: nothing says anything, and its limit ends it (a load doesn't count here).
        Assert.False(RewardFadeRules.BeforeMenuGoesBack(false, false, RewardFadeRules.MaxBeforeMenuSeconds - 0.1, -1));
        Assert.True(RewardFadeRules.BeforeMenuGoesBack(false, false, RewardFadeRules.MaxBeforeMenuSeconds, -1));
    }

    [Fact]
    public void A_transition_up_too_long_while_nothing_loads_is_a_stray_even_unsettled()
    {
        var stray = new StrayTransition(0);
        double t = 0;
        while (t < StrayTransition.MaxSeconds - 0.5)
        {
            t += 0.25;
            Assert.False(stray.Watch(t, loading: false, settled: false));
        }

        Assert.True(stray.Watch(StrayTransition.MaxSeconds + 0.1, loading: false, settled: false));
        Assert.True(stray.SettledFor < 0, "it went by the limit, not by settling");
    }
}
