using T2TOnline.Core.Campaign;

namespace T2TOnline.Core.Tests.Campaign;

/// <summary>A friend whose load of the host's match failed: the match's own menus stay off its screen, the run's end doesn't.</summary>
public class MatchFailedMenusTests
{
    [Theory]
    [InlineData("GameMenu")]
    [InlineData("GlossaryMenu")]
    [InlineData("BrawlFightMenu")]
    [InlineData("ControllerAssignmentMenu")]
    [InlineData("CanadaCurseMenu")]
    [InlineData("LineupMenu")]
    public void The_matchs_own_menus_are_kept_off(string type) => Assert.True(MatchFailedMenus.KeptOff(type));

    [Theory]
    [InlineData("RunEndMenu")]
    [InlineData("PermanentUnlockMenu")]
    [InlineData("RelicRewardMenu")]
    [InlineData("SkaterPowerupRewardMenu")]
    [InlineData("EventChoiceMenu")]
    [InlineData("ChooseMetaMenu")]
    [InlineData("")]
    public void The_runs_own_menus_and_the_runs_end_are_never_kept_off(string type) => Assert.False(MatchFailedMenus.KeptOff(type));

    [Fact]
    public void The_runs_end_shows_for_real_where_the_game_itself_shows_it_and_as_text_elsewhere()
    {
        // The run-end menu at its run's end: on the map (the game's own run's end on the map) and at the title (its history mode).
        Assert.True(MatchFailedMenus.ShowsHere("RunEndMenu", runsEnd: true, onMap: true, atTitle: false));
        Assert.True(MatchFailedMenus.ShowsHere("RunEndMenu", runsEnd: true, onMap: false, atTitle: true));
        Assert.False(MatchFailedMenus.ShowsHere("RunEndMenu", runsEnd: true, onMap: false, atTitle: false)); // between scenes

        // Not the map menu's "Run stats" or the title's history: those are where the host's are, or text.
        Assert.False(MatchFailedMenus.ShowsHere("RunEndMenu", runsEnd: false, onMap: true, atTitle: false));

        // The new-unlocks menu: the game shows it on the map at a run's end there, never at the title.
        Assert.True(MatchFailedMenus.ShowsHere("PermanentUnlockMenu", runsEnd: false, onMap: true, atTitle: false));
        Assert.False(MatchFailedMenus.ShowsHere("PermanentUnlockMenu", runsEnd: false, onMap: false, atTitle: true));

        // Nothing else moves scenes.
        Assert.False(MatchFailedMenus.ShowsHere("GameMenu", runsEnd: true, onMap: true, atTitle: true));
        Assert.False(MatchFailedMenus.ShowsHere("RelicRewardMenu", runsEnd: true, onMap: true, atTitle: true));
    }
}
