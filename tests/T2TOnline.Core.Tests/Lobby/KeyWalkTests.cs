using System;
using T2TOnline.Core.Input;
using T2TOnline.Core.Lobby;

namespace T2TOnline.Core.Tests.Lobby;

/// <summary>The keyboard walk's rules: which key of a keyboard's own maps makes a pad button's action, and the line each row gets.</summary>
public class KeyWalkTests
{
    private const byte Ui = 2;
    private const byte Default = 0;
    private const int KeyW = 'w';
    private const int KeyS = 's';
    private const int KeyQ = 'q';
    private const int KeyE = 'e';

    [Fact]
    public void The_arrows_enter_and_esc_are_taken_before_other_keys_bound_to_the_same_action()
    {
        InputBindingSet keys = Keys(
            new InputBinding(Ui, KeyWalk.UiMoveVertical, BindingElement.Key(KeyW)),
            new InputBinding(Ui, KeyWalk.UiMoveVertical, BindingElement.Key(KeyWalk.KeyUp)),
            new InputBinding(Ui, KeyWalk.UiMoveVertical, BindingElement.Key(KeyS), Negative: true),
            new InputBinding(Ui, KeyWalk.UiMoveVertical, BindingElement.Key(KeyWalk.KeyDown), Negative: true),
            new InputBinding(Ui, KeyWalk.UiConfirm, BindingElement.Key(KeyWalk.KeySpace)),
            new InputBinding(Ui, KeyWalk.UiConfirm, BindingElement.Key(KeyWalk.KeyReturn)),
            new InputBinding(Ui, KeyWalk.UiBack, BindingElement.Key(KeyWalk.KeyEscape)));

        Assert.Equal(KeyWalk.KeyUp, CodeOf(keys, Ui, PadButtons.DPadUp));
        Assert.Equal(KeyWalk.KeyDown, CodeOf(keys, Ui, PadButtons.DPadDown));
        Assert.Equal(KeyWalk.KeyReturn, CodeOf(keys, Ui, PadButtons.A));
        Assert.Equal(KeyWalk.KeyEscape, CodeOf(keys, Ui, PadButtons.B));
    }

    [Fact]
    public void Without_a_preferred_key_the_lowest_plain_key_of_the_action_is_taken()
    {
        InputBindingSet keys = Keys(
            new InputBinding(Ui, KeyWalk.UiTabRight, BindingElement.Key(KeyE)),
            new InputBinding(Ui, KeyWalk.UiTabLeft, BindingElement.Key(KeyQ)),
            new InputBinding(Ui, KeyWalk.UiTabLeft, BindingElement.Key(KeyW)));

        Assert.Equal(KeyE, CodeOf(keys, Ui, PadButtons.RightBumper));
        Assert.Equal(KeyQ, CodeOf(keys, Ui, PadButtons.LeftBumper));
    }

    [Fact]
    public void A_key_with_a_modifier_another_category_or_the_other_pole_never_makes_the_action()
    {
        InputBindingSet keys = Keys(
            new InputBinding(Ui, KeyWalk.UiBack, BindingElement.Key(KeyWalk.KeyEscape, KeyModifiers.Shift)),
            new InputBinding(Default, KeyWalk.UiBack, BindingElement.Key(KeyQ)),
            new InputBinding(Ui, KeyWalk.UiMoveHorizontal, BindingElement.Key(KeyWalk.KeyRight)));

        Assert.Equal(-1, KeyWalk.Pick(keys, Ui, KeyWalk.For(PadButtons.B)!));
        Assert.Equal(-1, KeyWalk.Pick(keys, Ui, KeyWalk.For(PadButtons.DPadLeft)!));
        Assert.Equal(KeyWalk.KeyRight, CodeOf(keys, Ui, PadButtons.DPadRight));
        Assert.Equal(KeyQ, CodeOf(keys, -1, PadButtons.B));
    }

    [Fact]
    public void The_join_takes_enter_first_in_any_category()
    {
        InputBindingSet keys = Keys(
            new InputBinding(Default, KeyWalk.Activate, BindingElement.Key(KeyWalk.KeySpace)),
            new InputBinding(Default, KeyWalk.Activate, BindingElement.Key(KeyWalk.KeyReturn)));

        int element = KeyWalk.Pick(keys, -1, KeyWalk.Join);
        Assert.Equal(KeyWalk.KeyReturn, keys.Elements[element].Code);
    }

    [Fact]
    public void Each_pad_button_the_walks_press_has_the_actions_a_pad_makes()
    {
        Assert.Equal(KeyWalk.UiConfirm, KeyWalk.For(PadButtons.A)!.ActionId);
        Assert.Equal(KeyWalk.UiBack, KeyWalk.For(PadButtons.B)!.ActionId);
        Assert.False(KeyWalk.For(PadButtons.DPadUp)!.Negative);
        Assert.True(KeyWalk.For(PadButtons.DPadDown)!.Negative);
        Assert.False(KeyWalk.For(PadButtons.DPadRight)!.Negative);
        Assert.True(KeyWalk.For(PadButtons.DPadLeft)!.Negative);
        Assert.Equal(KeyWalk.UiTabLeft, KeyWalk.For(PadButtons.LeftBumper)!.ActionId);
        Assert.Equal(KeyWalk.UiTabRight, KeyWalk.For(PadButtons.RightBumper)!.ActionId);
        Assert.Equal(KeyWalk.UiRightTrigger, KeyWalk.ForTrigger(true).ActionId);
        Assert.Null(KeyWalk.For(PadButtons.LeftStick));
    }

    [Fact]
    public void No_key_is_found_when_nothing_binds_the_action()
    {
        InputBindingSet keys = Keys(new InputBinding(Ui, KeyWalk.UiBack, BindingElement.Key(KeyWalk.KeyEscape)));
        Assert.Equal(-1, KeyWalk.Pick(keys, Ui, KeyWalk.For(PadButtons.A)!));
        Assert.Equal(-1, KeyWalk.Pick(keys, Ui, KeyWalk.ForTrigger(true)));
    }

    [Theory]
    [InlineData(KeyWalk.KeyReturn, "Enter")]
    [InlineData(KeyWalk.KeyEscape, "Esc")]
    [InlineData(KeyWalk.KeyUp, "Up")]
    [InlineData('q', "Q")]
    [InlineData('7', "7")]
    [InlineData(301, "key 301")]
    public void Keys_are_named_for_the_log(int code, string name)
    {
        Assert.Equal(name, KeyWalk.KeyName(code));
    }

    [Fact]
    public void A_rows_line_has_its_fixed_shape()
    {
        string line = KeyWalk.Line("Settings > Online", "ShareAchievements", "ok", "ok", KeyWalk.NotPressed, "Keyboard_Enter (MouseKeyboard)", "ShareAchievements", "ok");
        Assert.Equal("kbwalk: Settings > Online ShareAchievements: arrows ok, Enter ok, Esc -, glyph Keyboard_Enter (MouseKeyboard); mouse hit ShareAchievements, click ok", line);
        Assert.Contains("click -", KeyWalk.Line("a", "b", "ok", string.Empty, "ok", "g", "b", string.Empty), StringComparison.Ordinal);
        Assert.Contains("...", KeyWalk.Line("a", new string('x', 80), "ok", "ok", "ok", "g", "h", "c"), StringComparison.Ordinal);
    }

    private static InputBindingSet Keys(params InputBinding[] bindings)
    {
        var builder = new KeyboardBindingTableBuilder(Array.Empty<BindingElement>());
        foreach (InputBinding binding in bindings)
        {
            Assert.True(builder.Add(binding));
        }

        return builder.Build(1, BindingSource.GameDefaults, 0);
    }

    private static int CodeOf(InputBindingSet keys, int category, PadButtons button)
    {
        int element = KeyWalk.Pick(keys, category, KeyWalk.For(button)!);
        Assert.True(element >= 0, $"no key for {button}");
        return keys.Elements[element].Code;
    }
}
