using System;
using T2TOnline.Core.OnlineSettings;

namespace T2TOnline.Core.Tests.OnlineSettings;

/// <summary>The Online panel's place among the Settings menu's default selections.</summary>
public class SettingsDefaultsTests
{
    [Fact]
    public void In_the_games_own_list_it_goes_after_the_last_panel_before_the_bar_and_the_content()
    {
        // The Settings menu's list on the Nov 6 build: six panels' rows (Video, Gameplay, Audio, Controls, System, Stats), the bottom bar, the content.
        bool[] games = { true, true, true, true, true, true, false, false };
        Assert.Equal(6, SettingsDefaults.InsertIndex(games));
    }

    [Fact]
    public void With_no_panel_in_the_list_it_goes_last()
    {
        Assert.Equal(0, SettingsDefaults.InsertIndex(Array.Empty<bool>()));
        Assert.Equal(2, SettingsDefaults.InsertIndex(new[] { false, false }));
    }

    [Fact]
    public void It_follows_the_last_panel_wherever_that_is()
    {
        Assert.Equal(3, SettingsDefaults.InsertIndex(new[] { true, true, true }));
        Assert.Equal(3, SettingsDefaults.InsertIndex(new[] { true, false, true, false }));
        Assert.Equal(1, SettingsDefaults.InsertIndex(new[] { true, false, false }));
    }

    [Fact]
    public void A_missing_list_throws()
    {
        Assert.Throws<ArgumentNullException>(() => SettingsDefaults.InsertIndex(null!));
    }
}
