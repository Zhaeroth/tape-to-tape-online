using T2TOnline.Core.Input;
using T2TOnline.Core.Lobby;

namespace T2TOnline.Core.Tests.Lobby;

public class TourShortcutsTests
{
    [Theory]
    [InlineData(KeyWalk.UiConfirm, PadButtons.A)]
    [InlineData(KeyWalk.UiBack, PadButtons.B)]
    [InlineData(KeyWalk.UiTabLeft, PadButtons.LeftBumper)]
    [InlineData(KeyWalk.UiTabRight, PadButtons.RightBumper)]
    [InlineData(KeyWalk.UiStart, PadButtons.Start)]
    [InlineData(KeyWalk.UiSpecial, PadButtons.X)]
    [InlineData(KeyWalk.UiSpecial2, PadButtons.Y)]
    public void A_shortcuts_action_is_made_by_the_button_the_menus_read_it_from(int action, PadButtons button)
    {
        Assert.Equal(button, TourShortcuts.ButtonFor(action));

        // The keyboard walk's table, read the other way: the same action.
        Assert.Equal(action, KeyWalk.For(button)!.ActionId);
    }

    [Fact]
    public void The_special_actions_are_on_the_pads_x_and_y_as_the_game_maps_them()
    {
        // The pad walk's own presses on the game's map: X held on the Join list refreshes it once (its Refresh is the bar's
        // UI_UISpecial shortcut), Y on the lobby opens the invite list (its Invite is UI_UISpecial2).
        Assert.Equal(PadButtons.X, TourShortcuts.ButtonFor(KeyWalk.UiSpecial));
        Assert.Equal(PadButtons.Y, TourShortcuts.ButtonFor(KeyWalk.UiSpecial2));
    }

    [Theory]
    [InlineData(KeyWalk.UiLeftTrigger)]
    [InlineData(KeyWalk.UiRightTrigger)]
    [InlineData(KeyWalk.UiMoveVertical)]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(9999)]
    public void An_action_the_tour_has_no_button_for_is_none(int action)
    {
        Assert.Null(TourShortcuts.ButtonFor(action));
    }

    [Theory]
    [InlineData(0, 0.1)]
    [InlineData(-1, 0.1)]
    [InlineData(double.NaN, 0.1)]
    [InlineData(0.5, 0.75)]
    [InlineData(10, TourShortcuts.MaxHoldSeconds)]
    public void A_hold_lasts_past_what_the_shortcut_asks_within_a_bound(double required, double held)
    {
        Assert.Equal(held, TourShortcuts.HoldSeconds(required), 6);
    }
}
