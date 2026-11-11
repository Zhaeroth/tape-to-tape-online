using T2TOnline.Core.Lobby;

namespace T2TOnline.Core.Tests.Lobby;

public class TitleBackTests
{
    [Theory]
    [InlineData(2)] // Campaign
    [InlineData(3)] // Play
    [InlineData(6)] // the Online group
    public void A_settled_group_takes_back_to_main(int group)
    {
        Assert.Null(TitleBack.WhyNot(group, menusOver: 0, clicking: false, exiting: false, transitioning: false, groupInputReady: false));
    }

    [Fact]
    public void A_group_change_whose_input_is_ready_takes_it_too()
    {
        // The game's handler completes the change first, then goes to Main.
        Assert.Null(TitleBack.WhyNot(2, 0, clicking: false, exiting: false, transitioning: true, groupInputReady: true));
    }

    [Fact]
    public void A_menu_still_over_the_title_loses_the_press()
    {
        // A screen that has begun to close is no longer on top, but counts over the title until it is destroyed.
        Assert.Equal("1 menu(s) still over the title (one closing is counted until it is destroyed)", TitleBack.WhyNot(2, 1, false, false, false, false));
    }

    [Theory]
    [InlineData(true, false, false, false, "a title button's click is being handled")]
    [InlineData(false, true, false, false, "the title is leaving")]
    [InlineData(false, false, true, false, "the title's group change runs and doesn't take input yet")]
    public void The_other_guards_say_why(bool clicking, bool exiting, bool transitioning, bool ready, string why)
    {
        Assert.Equal(why, TitleBack.WhyNot(2, 0, clicking, exiting, transitioning, ready));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Press_to_start_and_main_are_not_left_for_main(int group)
    {
        Assert.NotNull(TitleBack.WhyNot(group, 0, false, false, false, false));
    }
}
