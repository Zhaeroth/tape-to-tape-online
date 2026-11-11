using T2TOnline.Core.Campaign;

namespace T2TOnline.Core.Tests.Campaign;

public class MenuSoundsTests
{
    [Theory]
    [InlineData(true, false, 0f, false, true)] // a press the shortcut can't take: the usual case
    [InlineData(true, false, 1f, false, true)] // the button isn't down: no hold to end, whatever the shortcut's hold time
    [InlineData(true, false, 0f, true, true)]
    [InlineData(true, true, 0f, false, true)] // down, on a shortcut with no hold: still the event (verified in the game's code)
    [InlineData(true, true, 0.5f, false, false)] // down with a hold time: the hold's release instead
    [InlineData(true, true, 0f, true, false)] // down with a hold loop: the hold's release instead
    [InlineData(false, false, 0f, false, false)] // an ordinary release
    [InlineData(false, true, 0f, false, false)]
    public void A_release_is_a_press_on_a_disabled_shortcut_only_from_a_cant_trigger_state_and_with_no_hold_to_end(
        bool fromCantTrigger, bool buttonDown, float holdTime, bool holdLoop, bool expected)
    {
        Assert.Equal(expected, MenuPress.RaisesCantTrigger(fromCantTrigger, buttonDown, holdTime, holdLoop));
    }

    [Fact]
    public void Presses_alternate_and_never_look_like_a_list_index_or_a_hold()
    {
        Assert.NotEqual(MenuPress.Value(true, 0), MenuPress.Value(true, 1));
        Assert.Equal(MenuPress.Value(true, 0), MenuPress.Value(true, 2));
        Assert.NotEqual(MenuPress.Value(false, 0), MenuPress.Value(false, 1));
        foreach (bool success in new[] { true, false })
        {
            for (int count = 0; count < 4; count++)
            {
                int value = MenuPress.Value(success, count);
                Assert.True(MenuPress.TryRead(value, out bool read));
                Assert.Equal(success, read);
                Assert.False(MenuHold.IsHold(value));
                Assert.False(MenuHold.IsListIndex(value));
                Assert.True(value > MenuHold.MaxListIndex);
                Assert.True(value <= MenuTarget.MaxIndex);
            }
        }

        Assert.True(MenuHold.IsListIndex(0));
        Assert.True(MenuHold.IsListIndex(MenuHold.MaxListIndex));
        Assert.False(MenuHold.IsListIndex(MenuHold.Released));
        Assert.False(MenuHold.IsListIndex(MenuHold.Held));
        Assert.False(MenuPress.TryRead(MenuHold.Held, out _));
        Assert.False(MenuPress.TryRead(MenuHold.Released, out _));
        Assert.False(MenuPress.TryRead(0, out _));
        Assert.False(MenuPress.TryRead(MenuHold.MaxListIndex, out _));
        Assert.False(MenuPress.TryRead(MenuPress.Failure + 2, out _));
    }

    [Fact]
    public void Back_sounds_alternate_on_their_own_group()
    {
        MenuTarget back = MenuTarget.AtItem(MenuSounds.BackGroup, 0);
        Assert.NotEqual(MenuSounds.BackValue(0), MenuSounds.BackValue(1));
        Assert.True(MenuSounds.IsBack(back, MenuSounds.BackValue(0)));
        Assert.True(MenuSounds.IsBack(back, MenuSounds.BackValue(7)));
        Assert.False(MenuSounds.IsBack(back, 0));
        Assert.False(MenuSounds.IsBack(back, 3));
        Assert.False(MenuSounds.IsBack(MenuTarget.AtItem(MenuSounds.BackGroup, 1), 1));
        Assert.False(MenuSounds.IsBack(MenuTarget.AtItem("state", 0), 1));
        Assert.False(MenuSounds.IsBack(MenuTarget.AtPath(new[] { 0 }, "back"), 1));
    }

    [Fact]
    public void Navigation_modes_read_only_on_their_group_and_in_range()
    {
        MenuTarget nav = MenuTarget.AtItem(MenuSounds.NavModeGroup, 0);
        for (int mode = 0; mode <= MenuSounds.MaxNavMode; mode++)
        {
            Assert.True(MenuSounds.TryReadNavMode(nav, mode, out int read));
            Assert.Equal(mode, read);
        }

        Assert.False(MenuSounds.TryReadNavMode(nav, MenuSounds.MaxNavMode + 1, out _));
        Assert.False(MenuSounds.TryReadNavMode(nav, -1, out _));
        Assert.False(MenuSounds.TryReadNavMode(MenuTarget.AtItem(MenuSounds.NavModeGroup, 1), 1, out _));
        Assert.False(MenuSounds.TryReadNavMode(MenuTarget.AtItem("tab", 0), 1, out _));
        Assert.Equal("Mouse", MenuSounds.NavModeName(MenuSounds.MouseNavMode));
        Assert.Equal("Controller", MenuSounds.NavModeName(3));
        Assert.Equal("Unknown", MenuSounds.NavModeName(9));
    }

    [Fact]
    public void A_press_whose_handler_closes_its_menu_reaches_a_friend_before_the_close()
    {
        // HostMenus sends a closing shortcut's press just before the close, so a friend's copy still has its sound.
        var board = new MenuBoard();
        var mirror = new MenuMirror();
        MenuEvent opened = board.Opened("YesNo", "CustomizableYesNoPrompt", "world_map", MenuData.Empty)!;
        Assert.Equal(MenuMirrorChange.Opened, mirror.Apply(opened));
        MenuTarget shortcut = MenuTarget.AtPath(new[] { 0, 1 }, "ConfirmShortcut");
        Assert.Equal(MenuMirrorChange.ValueSet, mirror.Apply(board.ValueSet(opened.Serial, shortcut, MenuPress.Value(true, 0))!));
        Assert.True(mirror.TryGet(opened.Serial, out MirroredMenu menu));
        Assert.Equal(MenuPress.Success, menu.Values[shortcut]);
        Assert.Equal(MenuMirrorChange.Closed, mirror.Apply(board.Closed(opened.Serial)!));

        // After the close the menu takes no value: the press could only have gone first.
        Assert.Null(board.ValueSet(opened.Serial, shortcut, MenuPress.Value(true, 1)));
    }

    [Fact]
    public void The_values_travel_as_menu_value_events()
    {
        foreach ((MenuTarget target, int value) in new[]
        {
            (MenuTarget.AtItem(MenuSounds.BackGroup, 0), MenuSounds.BackValue(1)),
            (MenuTarget.AtItem(MenuSounds.NavModeGroup, 0), 3),
            (MenuTarget.AtPath(new[] { 0, 2 }, "RerollShortcut"), MenuPress.Value(false, 1)),
        })
        {
            var board = new MenuBoard();
            uint serial = board.Opened("RelicReward", "RelicRewardMenu", "standard_game", MenuData.Empty)!.Serial;
            MenuEvent sent = board.ValueSet(serial, target, value)!;
            Assert.Equal(value, sent.Index);
            Assert.Equal(target, sent.Target);
        }
    }
}
