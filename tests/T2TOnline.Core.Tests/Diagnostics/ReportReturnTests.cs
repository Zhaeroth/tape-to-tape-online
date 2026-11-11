using T2TOnline.Core.Diagnostics;

namespace T2TOnline.Core.Tests.Diagnostics;

/// <summary>The cursor's way back to the Online menu's Report a Problem row after its report's prompt.</summary>
public class ReportReturnTests
{
    // The Online menu still up as the prompt is answered: the title, no session, nothing over it, the group at rest, the row there.
    private static readonly ReportReturnLook OnTheMenu = new(AtTitle: true, InSession: false, ScreenOpen: false, PromptOpen: false, MenuOnTop: false, GroupShown: true, RowThere: true, ReportBusy: false);

    [Fact]
    public void Only_a_report_the_Online_menus_row_asked_for_hands_the_cursor_back()
    {
        Assert.True(ReportReturn.Applies(ReportSources.OnlineMenu));
        foreach (string source in ReportSources.All)
        {
            Assert.Equal(source == ReportSources.OnlineMenu, ReportReturn.Applies(source));
        }

        Assert.False(ReportReturn.Applies(null));
        Assert.False(ReportReturn.Applies(string.Empty));
    }

    [Fact]
    public void With_the_Online_menu_still_up_the_row_takes_the_cursor()
    {
        Assert.Null(ReportReturn.WhyNot(OnTheMenu));
    }

    [Fact]
    public void Anything_that_took_the_screen_leaves_the_cursor_to_the_game_and_says_why()
    {
        Assert.Equal("the title has gone", ReportReturn.WhyNot(OnTheMenu with { AtTitle = false }));
        Assert.Equal("an online game is on", ReportReturn.WhyNot(OnTheMenu with { InSession = true }));
        Assert.Equal("a screen is open over the title", ReportReturn.WhyNot(OnTheMenu with { ScreenOpen = true }));
        Assert.Equal("another prompt is up", ReportReturn.WhyNot(OnTheMenu with { PromptOpen = true }));
        Assert.Equal("a menu is on top", ReportReturn.WhyNot(OnTheMenu with { MenuOnTop = true }));
        Assert.Equal("the title shows another group", ReportReturn.WhyNot(OnTheMenu with { GroupShown = false }));
        Assert.Equal("the row has gone", ReportReturn.WhyNot(OnTheMenu with { RowThere = false }));
        Assert.Equal("another report is being made", ReportReturn.WhyNot(OnTheMenu with { ReportBusy = true }));
    }

    [Fact]
    public void A_match_or_a_session_comes_before_what_the_title_shows()
    {
        // In a match nothing of the title's is looked at; with a session on, whatever the title shows is the session's.
        Assert.Equal("the title has gone", ReportReturn.WhyNot(OnTheMenu with { AtTitle = false, InSession = true, GroupShown = false, RowThere = false }));
        Assert.Equal("an online game is on", ReportReturn.WhyNot(OnTheMenu with { InSession = true, ScreenOpen = true, MenuOnTop = true }));
    }
}
