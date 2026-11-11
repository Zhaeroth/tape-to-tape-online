using System;
using T2TOnline.Core.Input;

namespace T2TOnline.Core.Tests.Input;

public class PromptPinStateTests
{
    private static readonly int[] ElevenKeyboard = Array.ConvertAll(new int[11], _ => PromptPinState.MouseKeyboard);

    [Fact]
    public void Unpinned_draws_nothing()
    {
        var state = new PromptPinState();
        Assert.False(state.Pinned);
        Assert.Equal(PromptPinState.Other, state.Family);
        for (int device = -1; device <= 6; device++)
        {
            Assert.False(state.Draws(device));
        }
    }

    [Fact]
    public void Pinned_draws_only_its_family()
    {
        var state = new PromptPinState();
        state.Pin(PromptPinState.GamepadXbox);
        Assert.True(state.Pinned);
        Assert.True(state.Draws(PromptPinState.GamepadXbox));
        Assert.False(state.Draws(PromptPinState.MouseKeyboard));
        Assert.False(state.Draws(PromptPinState.GamepadDualShock));
        Assert.False(state.Draws(PromptPinState.Other));

        state.Unpin();
        Assert.False(state.Draws(PromptPinState.GamepadXbox));
    }

    [Fact]
    public void A_keyboard_friend_pins_the_keyboard()
    {
        var state = new PromptPinState();
        state.Pin(PromptPinState.MouseKeyboard);
        Assert.True(state.Draws(PromptPinState.MouseKeyboard));
        Assert.False(state.Draws(PromptPinState.GamepadGeneric));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(PromptPinState.Other)]
    [InlineData(20)]
    public void Only_drawn_families_can_be_pinned(int family)
    {
        var state = new PromptPinState();
        Assert.Throws<ArgumentOutOfRangeException>(() => state.Pin(family));
        Assert.False(state.Pinned);
        state.Pin(PromptPinState.GamepadXbox);
        Assert.Throws<ArgumentOutOfRangeException>(() => state.Refamily(family));
        Assert.Equal(PromptPinState.GamepadXbox, state.Family);
    }

    [Fact]
    public void Another_pad_family_moves_the_pin()
    {
        var state = new PromptPinState();
        state.Refamily(PromptPinState.GamepadNintendo);
        Assert.False(state.Pinned); // nothing to move

        state.Pin(PromptPinState.GamepadXbox);
        state.Refamily(PromptPinState.GamepadDualShock);
        Assert.True(state.Draws(PromptPinState.GamepadDualShock));
        Assert.False(state.Draws(PromptPinState.GamepadXbox));
    }

    [Theory]
    [InlineData(0, -1)]
    [InlineData(-3, -1)]
    [InlineData(1, PromptPinState.MouseKeyboard)]
    [InlineData(2, PromptPinState.GamepadXbox)]
    [InlineData(3, PromptPinState.GamepadDualShock)]
    [InlineData(5, PromptPinState.GamepadGeneric)]
    public void The_icons_setting_forces_a_device_or_is_automatic(int setting, int expected) =>
        Assert.Equal(expected, PromptPinState.ForcedDevice(setting));

    [Fact]
    public void The_shadow_is_unknown_until_tracked()
    {
        var state = new PromptPinState();
        state.Pin(PromptPinState.GamepadXbox);
        Assert.False(state.ShadowKnown);
        state.GameChose(PromptPinState.GamepadDualShock, 1);
        state.GameForced(PromptPinState.GamepadNintendo);
        Assert.Equal(PromptPinState.Other, state.ShadowDefault);
        Assert.Equal(0, state.ShadowChanges);
        Assert.Equal("not known", state.DescribeShadow());
    }

    [Fact]
    public void A_game_player_choice_sets_that_player_and_the_default()
    {
        var state = Tracked(PromptPinState.GamepadXbox);
        state.GameChose(PromptPinState.GamepadDualShock, 2);
        Assert.Equal(PromptPinState.GamepadDualShock, state.Shadow(2));
        Assert.Equal(PromptPinState.GamepadDualShock, state.ShadowDefault);
        Assert.Equal(PromptPinState.MouseKeyboard, state.Shadow(0));
        Assert.Equal(PromptPinState.MouseKeyboard, state.Shadow(3));
        Assert.Equal(1, state.ShadowChanges);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(11)]
    [InlineData(int.MaxValue)]
    public void A_choice_with_no_game_player_sets_only_the_default(int gamePlayer)
    {
        var state = Tracked(PromptPinState.GamepadXbox);
        state.GameChose(PromptPinState.GamepadXbox, gamePlayer);
        Assert.Equal(PromptPinState.GamepadXbox, state.ShadowDefault);
        for (int i = 0; i < 11; i++)
        {
            Assert.Equal(PromptPinState.MouseKeyboard, state.Shadow(i));
        }

        Assert.Equal(PromptPinState.GamepadXbox, state.Shadow(gamePlayer)); // out of range reads the default
    }

    [Fact]
    public void Only_real_changes_count()
    {
        var state = Tracked(PromptPinState.GamepadXbox);
        state.GameChose(PromptPinState.MouseKeyboard, 0);
        state.GameChose(PromptPinState.MouseKeyboard, -1);
        Assert.Equal(0, state.ShadowChanges);
        state.GameChose(PromptPinState.GamepadXbox, -1);
        state.GameChose(PromptPinState.GamepadXbox, -1);
        state.GameChose(PromptPinState.MouseKeyboard, -1);
        Assert.Equal(2, state.ShadowChanges);
    }

    [Fact]
    public void A_forced_icon_set_covers_every_player()
    {
        var state = Tracked(PromptPinState.GamepadXbox);
        state.GameChose(PromptPinState.GamepadXbox, 1);
        state.GameForced(PromptPinState.GamepadNintendo);
        Assert.Equal(PromptPinState.GamepadNintendo, state.ShadowDefault);
        for (int i = 0; i < 11; i++)
        {
            Assert.Equal(PromptPinState.GamepadNintendo, state.Shadow(i));
        }

        Assert.Equal(2, state.ShadowChanges);
        state.GameForced(PromptPinState.GamepadNintendo);
        Assert.Equal(2, state.ShadowChanges);
    }

    [Fact]
    public void Unpinning_keeps_the_shadow_to_put_back()
    {
        var state = Tracked(PromptPinState.GamepadXbox);
        state.GameChose(PromptPinState.GamepadDualShock, 1);
        state.Unpin();
        Assert.True(state.ShadowKnown);
        Assert.Equal(PromptPinState.GamepadDualShock, state.Shadow(1));
        Assert.Equal(PromptPinState.GamepadDualShock, state.ShadowDefault);
    }

    [Fact]
    public void Pinning_again_starts_over()
    {
        var state = Tracked(PromptPinState.GamepadXbox);
        state.GameChose(PromptPinState.GamepadDualShock, 1);
        state.Drew(found: true);
        Assert.True(state.FirstOnScreen("world_map", 2));
        state.Unpin();

        state.Pin(PromptPinState.GamepadNintendo);
        Assert.False(state.ShadowKnown);
        Assert.Equal(0, state.Players);
        Assert.Equal(0, state.ShadowChanges);
        Assert.Equal(0, state.Drawn);
        Assert.Equal(0, state.ScreensLogged);
        Assert.True(state.FirstOnScreen("world_map", 2));
    }

    [Fact]
    public void Tracking_keeps_at_most_the_cap_of_players()
    {
        var state = new PromptPinState();
        state.Pin(PromptPinState.GamepadXbox);
        state.TrackGame(new int[PromptPinState.MaxPlayers + 10], PromptPinState.MouseKeyboard);
        Assert.Equal(PromptPinState.MaxPlayers, state.Players);
        Assert.Throws<ArgumentNullException>(() => state.TrackGame(null!, 0));
    }

    [Fact]
    public void Prompts_drawn_and_not_found_are_counted()
    {
        var state = new PromptPinState();
        state.Pin(PromptPinState.GamepadXbox);
        state.Drew(found: true);
        state.Drew(found: false);
        state.Drew(found: true);
        Assert.Equal(3, state.Drawn);
        Assert.Equal(1, state.NotFound);
    }

    [Fact]
    public void Each_screen_is_logged_once_up_to_the_cap()
    {
        var state = new PromptPinState(maxScreens: 2);
        Assert.False(state.FirstOnScreen("start_screen", 2)); // not pinned
        state.Pin(PromptPinState.GamepadXbox);
        Assert.True(state.FirstOnScreen("start_screen", 2));
        Assert.False(state.FirstOnScreen("start_screen", 2));
        Assert.True(state.FirstOnScreen("start_screen", 0));
        Assert.False(state.FirstOnScreen("world_map", 2));
        Assert.False(state.FirstOnScreen("standard_game", 0));
        Assert.False(state.FirstOnScreen("start_screen", 0));
        Assert.Equal(2, state.ScreensLogged);
        Assert.Equal(2, state.ScreensNotLogged);
        Assert.Throws<ArgumentNullException>(() => state.FirstOnScreen(null!, 0));
    }

    [Fact]
    public void No_screens_are_logged_with_a_cap_of_zero()
    {
        var state = new PromptPinState(maxScreens: 0);
        state.Pin(PromptPinState.GamepadXbox);
        Assert.False(state.FirstOnScreen("world_map", 2));
        Assert.Equal(1, state.ScreensNotLogged);
        Assert.Throws<ArgumentOutOfRangeException>(() => new PromptPinState(maxScreens: -1));
    }

    [Fact]
    public void The_shadow_reads_as_groups_of_players()
    {
        var state = Tracked(PromptPinState.GamepadXbox);
        Assert.Equal("default MouseKeyboard; game players 0-10 MouseKeyboard", state.DescribeShadow());

        state.GameChose(PromptPinState.GamepadXbox, 1);
        state.GameChose(PromptPinState.GamepadGeneric, 10);
        Assert.Equal("default GamepadGeneric; game players 0 MouseKeyboard, 1 GamepadXbox, 2-9 MouseKeyboard, 10 GamepadGeneric", state.DescribeShadow());
    }

    [Fact]
    public void A_small_shadow_reads_too()
    {
        var state = new PromptPinState();
        state.Pin(PromptPinState.GamepadXbox);
        state.TrackGame(Array.Empty<int>(), PromptPinState.Other);
        Assert.Equal("default Other", state.DescribeShadow());
        state.TrackGame(new[] { PromptPinState.GamepadNintendo }, PromptPinState.GamepadNintendo);
        Assert.Equal("default GamepadNintendo; game player 0 GamepadNintendo", state.DescribeShadow());
    }

    [Theory]
    [InlineData(PromptPinState.MouseKeyboard, "MouseKeyboard", true)]
    [InlineData(PromptPinState.GamepadXbox, "GamepadXbox", true)]
    [InlineData(PromptPinState.GamepadDualShock, "GamepadDualShock", true)]
    [InlineData(PromptPinState.GamepadNintendo, "GamepadNintendo", true)]
    [InlineData(PromptPinState.GamepadGeneric, "GamepadGeneric", true)]
    [InlineData(PromptPinState.Other, "Other", false)]
    [InlineData(9, "device 9", false)]
    [InlineData(-1, "device -1", false)]
    public void Device_types_have_the_game_names(int device, string name, bool known)
    {
        Assert.Equal(name, PromptPinState.DeviceName(device));
        Assert.Equal(known, PromptPinState.IsKnown(device));
    }

    private static PromptPinState Tracked(int family)
    {
        var state = new PromptPinState();
        state.Pin(family);
        state.TrackGame(ElevenKeyboard, PromptPinState.MouseKeyboard);
        return state;
    }
}
