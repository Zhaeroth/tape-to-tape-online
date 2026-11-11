using System;
using T2TOnline.Core.Input;
using T2TOnline.Core.Protocol;

namespace T2TOnline.Core.Tests.Input;

public class MySettingsShortcutTests
{
    private const int UiCategory = 2;
    private const int RightTriggerAction = 40; // UI_UIRightTrigger's id, as Rewired would give it
    private const int OtherAction = 41;
    private const int Tab = 9; // Unity's key code
    private const int LeftCtrl = 306;

    private static readonly MySettingsFacts Showing = new(PauseMenuShown: true, Following: true);

    // ---- When it shows ----

    [Fact]
    public void It_shows_on_the_hosts_pause_menu_while_following()
    {
        Assert.Equal(MySettingsHidden.None, MySettingsShortcut.Availability(Showing));
    }

    [Fact]
    public void Each_reason_hides_it()
    {
        Assert.Equal(MySettingsHidden.NoPauseMenu, MySettingsShortcut.Availability(Showing with { PauseMenuShown = false }));
        Assert.Equal(MySettingsHidden.NotFollowing, MySettingsShortcut.Availability(Showing with { Following = false }));
        Assert.Equal(MySettingsHidden.OwnMenuOpen, MySettingsShortcut.Availability(Showing with { OwnMenuOpen = true }));
        Assert.Equal(MySettingsHidden.OwnMenusOff, MySettingsShortcut.Availability(Showing with { OwnMenusOff = true }));
        Assert.Equal(MySettingsHidden.UnlocksOpen, MySettingsShortcut.Availability(Showing with { UnlocksOpen = true }));
        Assert.Equal(MySettingsHidden.QuestionUp, MySettingsShortcut.Availability(Showing with { QuestionUp = true }));
        Assert.Equal(MySettingsHidden.PromptOpen, MySettingsShortcut.Availability(Showing with { PromptOpen = true }));
        Assert.Equal(MySettingsHidden.PadUnplugged, MySettingsShortcut.Availability(Showing with { PadUnplugged = true }));
        Assert.Equal(MySettingsHidden.Off, MySettingsShortcut.Availability(Showing with { Off = true }));
    }

    [Fact]
    public void The_first_reason_is_the_one_given()
    {
        // No pause menu at all says nothing about the rest; off after a failure says it before anything.
        Assert.Equal(MySettingsHidden.NoPauseMenu, MySettingsShortcut.Availability(new MySettingsFacts(false, false, OwnMenuOpen: true)));
        Assert.Equal(MySettingsHidden.Off, MySettingsShortcut.Availability(new MySettingsFacts(false, false, Off: true)));
        Assert.Equal(MySettingsHidden.NotFollowing, MySettingsShortcut.Availability(Showing with { Following = false, OwnMenuOpen = true }));
        Assert.Equal(MySettingsHidden.OwnMenuOpen, MySettingsShortcut.Availability(Showing with { OwnMenuOpen = true, PadUnplugged = true }));
    }

    [Fact]
    public void Every_reason_has_words()
    {
        foreach (MySettingsHidden why in Enum.GetValues<MySettingsHidden>())
        {
            Assert.NotEqual(why.ToString(), MySettingsShortcut.Describe(why));
        }
    }

    // ---- What presses it ----

    [Fact]
    public void The_games_defaults_are_rt_and_left_alt()
    {
        MySettingsBinding binding = MySettingsBinding.From(null, UiCategory, RightTriggerAction);
        Assert.Same(MySettingsBinding.Defaults, binding);
        Assert.Equal(new[] { PadAxis.RightTrigger }, binding.Axes);
        Assert.Equal(PadButtons.None, binding.Buttons);
        Assert.Equal(new[] { BindingElement.Key(MySettingsBinding.LeftAlt) }, binding.Keys);
        Assert.Equal(("RT", "Left Alt"), (binding.PadName, binding.KeyName));
    }

    [Fact]
    public void A_pad_on_the_games_defaults_sends_no_bindings_and_presses_with_rt()
    {
        var defaults = new InputBindingSet(1, InputKind.Gamepad, BindingSource.GameDefaults, 0, Array.Empty<BindingElement>(), Array.Empty<InputBinding>());
        MySettingsBinding binding = MySettingsBinding.From(defaults, UiCategory, RightTriggerAction);
        Assert.Equal(new[] { PadAxis.RightTrigger }, binding.Axes);
        Assert.Equal("RT", binding.PadName);
    }

    [Fact]
    public void A_pads_own_map_moves_it_where_the_friend_put_it()
    {
        var own = new InputBindingSet(1, InputKind.Gamepad, BindingSource.OwnMaps, 1, Array.Empty<BindingElement>(), new[]
        {
            new InputBinding(UiCategory, RightTriggerAction, BindingElement.PadButton(3)), // Y
            new InputBinding(0, RightTriggerAction, BindingElement.Axis(PadAxis.LeftTrigger)), // another category: not the menus'
            new InputBinding(UiCategory, OtherAction, BindingElement.Axis(PadAxis.RightTrigger)), // another action
        });

        MySettingsBinding binding = MySettingsBinding.From(own, UiCategory, RightTriggerAction);
        Assert.Equal(PadButtons.Y, binding.Buttons);
        Assert.Empty(binding.Axes);
        Assert.Equal("Y", binding.PadName);
        Assert.True(binding.PadDown(PadButtons.Y, 0, 0, 0, 0, 0, 0));
        Assert.False(binding.PadDown(PadButtons.None, 0, 0, 0, 0, 0, 1));
        Assert.Equal(new[] { BindingElement.Key(MySettingsBinding.LeftAlt) }, binding.Keys); // a pad friend's keyboard: the game's key
    }

    [Fact]
    public void A_trigger_bound_as_an_axis_range_still_reads_as_rt()
    {
        var own = new InputBindingSet(1, InputKind.Gamepad, BindingSource.OwnMaps, 0, Array.Empty<BindingElement>(), new[]
        {
            new InputBinding(UiCategory, RightTriggerAction, BindingElement.Axis(PadAxis.RightTrigger), Range: BindingRange.Positive),
        });

        MySettingsBinding binding = MySettingsBinding.From(own, UiCategory, RightTriggerAction);
        Assert.Equal("RT", binding.PadName);
        Assert.False(binding.PadDown(PadButtons.None, 0, 0, 0, 0, 0, 0.49f));
        Assert.True(binding.PadDown(PadButtons.None, 0, 0, 0, 0, 0, 0.5f));
    }

    [Fact]
    public void A_stick_bound_to_its_negative_side_presses_that_way_only()
    {
        var own = new InputBindingSet(1, InputKind.Gamepad, BindingSource.OwnMaps, 0, Array.Empty<BindingElement>(), new[]
        {
            new InputBinding(UiCategory, RightTriggerAction, BindingElement.Axis(PadAxis.RightStickY), Range: BindingRange.Negative),
        });

        MySettingsBinding binding = MySettingsBinding.From(own, UiCategory, RightTriggerAction);
        Assert.True(binding.PadDown(PadButtons.None, 0, 0, 0, -0.8f, 0, 0));
        Assert.False(binding.PadDown(PadButtons.None, 0, 0, 0, 0.8f, 0, 0));
    }

    [Fact]
    public void A_pads_map_with_no_binding_for_it_keeps_rt()
    {
        var own = new InputBindingSet(1, InputKind.Gamepad, BindingSource.OwnMaps, 1, Array.Empty<BindingElement>(), new[]
        {
            new InputBinding(UiCategory, OtherAction, BindingElement.PadButton(0)),
        });

        Assert.Equal(new[] { PadAxis.RightTrigger }, MySettingsBinding.From(own, UiCategory, RightTriggerAction).Axes);
    }

    [Fact]
    public void A_keyboards_own_key_is_the_one_and_rt_stays_for_the_pad_side()
    {
        BindingElement ctrl = BindingElement.Key(LeftCtrl);
        BindingElement q = BindingElement.Key(BindingElement.KeyQ);
        var own = new InputBindingSet(1, InputKind.KeyboardMouse, BindingSource.OwnMaps, 1, new[] { q, ctrl }, new[]
        {
            new InputBinding(UiCategory, RightTriggerAction, ctrl),
            new InputBinding(0, RightTriggerAction, q), // another category
        });

        MySettingsBinding binding = MySettingsBinding.From(own, UiCategory, RightTriggerAction);
        Assert.Equal(new[] { ctrl }, binding.Keys);
        Assert.Equal("Left Ctrl", binding.KeyName);
        Assert.Equal(new[] { PadAxis.RightTrigger }, binding.Axes);
        Assert.True(MySettingsBinding.IsModifierKey(ctrl));
        Assert.False(MySettingsBinding.IsModifierKey(q));
        Assert.Equal(1UL << 1, MySettingsBits.Mask(own, binding.Keys));
    }

    [Fact]
    public void A_keyboard_with_no_binding_for_it_keeps_left_alt_and_its_bit()
    {
        BindingElement alt = BindingElement.Key(MySettingsBinding.LeftAlt);
        BindingElement mouse = BindingElement.MouseButton(3);
        var own = new InputBindingSet(1, InputKind.KeyboardMouse, BindingSource.GameDefaults, 0, new[] { mouse, alt, BindingElement.Key(Tab) }, new[]
        {
            new InputBinding(0, OtherAction, mouse),
        });

        MySettingsBinding binding = MySettingsBinding.From(own, UiCategory, RightTriggerAction);
        Assert.Equal(new[] { alt }, binding.Keys);
        Assert.Equal(1UL << 1, MySettingsBits.Mask(own, binding.Keys));
        Assert.Equal(1UL << 0, MySettingsBits.MouseMask(own));
    }

    [Fact]
    public void A_pads_set_has_no_key_bits()
    {
        Assert.Equal(0UL, MySettingsBits.Mask(null, MySettingsBinding.Defaults.Keys));
        var pad = new InputBindingSet(1, InputKind.Gamepad, BindingSource.GameDefaults, 0, Array.Empty<BindingElement>(), Array.Empty<InputBinding>());
        Assert.Equal(0UL, MySettingsBits.Mask(pad, MySettingsBinding.Defaults.Keys));
        Assert.Equal(0UL, MySettingsBits.MouseMask(pad));
    }

    [Fact]
    public void Leaving_out_clears_only_its_elements()
    {
        var own = new InputBindingSet(1, InputKind.Gamepad, BindingSource.OwnMaps, 0, Array.Empty<BindingElement>(), new[]
        {
            new InputBinding(UiCategory, RightTriggerAction, BindingElement.PadButton(3)),
            new InputBinding(UiCategory, RightTriggerAction, BindingElement.Axis(PadAxis.LeftTrigger)),
        });

        MySettingsBinding binding = MySettingsBinding.From(own, UiCategory, RightTriggerAction);
        PadButtons held = PadButtons.Y | PadButtons.A;
        (float lx, float ly, float rx, float ry, float lt, float rt) = (0.3f, 0.4f, 0.5f, 0.6f, 1f, 1f);
        binding.LeaveOut(ref held, ref lx, ref ly, ref rx, ref ry, ref lt, ref rt);
        Assert.Equal(PadButtons.A, held);
        Assert.Equal((0.3f, 0.4f, 0.5f, 0.6f, 0f, 1f), (lx, ly, rx, ry, lt, rt));

        float rt2 = 1f, z = 0f;
        PadButtons none = PadButtons.None;
        MySettingsBinding.Defaults.LeaveOut(ref none, ref z, ref z, ref z, ref z, ref z, ref rt2);
        Assert.Equal(0f, rt2);
    }

    // ---- The presses ----

    [Fact]
    public void A_pad_presses_as_it_goes_down_while_it_shows()
    {
        var press = new MySettingsPress();
        Assert.False(press.Pad(down: false, live: true));
        Assert.True(press.Pad(down: true, live: true));
        Assert.False(press.Pad(down: true, live: true)); // held: once
        Assert.False(press.Pad(down: false, live: true));
        Assert.True(press.Pad(down: true, live: true));
    }

    [Fact]
    public void A_pad_held_from_before_it_showed_doesnt_press()
    {
        var press = new MySettingsPress();
        Assert.False(press.Pad(down: true, live: false));
        Assert.False(press.Pad(down: true, live: true));
        Assert.False(press.Pad(down: false, live: true));
        Assert.True(press.Pad(down: true, live: true));
    }

    [Fact]
    public void A_plain_key_presses_as_it_goes_down()
    {
        var press = new MySettingsPress();
        Assert.Equal(MySettingsKeyStep.Fire, press.Key(true, live: true, modifier: false, otherKeyWentDown: false, focused: true));
        Assert.Equal(MySettingsKeyStep.None, press.Key(true, live: true, modifier: false, otherKeyWentDown: false, focused: true));
    }

    [Fact]
    public void Left_alt_presses_when_let_go_alone()
    {
        var press = new MySettingsPress();
        Assert.Equal(MySettingsKeyStep.None, press.Key(true, true, modifier: true, otherKeyWentDown: true, focused: true)); // its own frame: its own down
        Assert.True(press.KeyWaits);
        Assert.Equal(MySettingsKeyStep.None, press.Key(true, true, true, false, true));
        Assert.Equal(MySettingsKeyStep.Fire, press.Key(false, true, true, false, true));
        Assert.False(press.KeyWaits);
    }

    [Fact]
    public void Alt_tab_never_presses()
    {
        var press = new MySettingsPress();
        press.Key(true, true, true, false, true);
        Assert.Equal(MySettingsKeyStep.Dropped, press.Key(true, true, true, otherKeyWentDown: true, focused: true));
        Assert.Equal(MySettingsKeyStep.None, press.Key(false, true, true, false, true)); // its release does nothing now

        press.Key(true, true, true, false, true);
        Assert.Equal(MySettingsKeyStep.Dropped, press.Key(true, true, true, false, focused: false));
    }

    [Fact]
    public void Left_alt_let_go_after_the_shortcut_went_does_nothing()
    {
        var press = new MySettingsPress();
        press.Key(true, true, true, false, true);
        Assert.Equal(MySettingsKeyStep.Dropped, press.Key(false, live: false, true, false, true));
    }

    [Fact]
    public void A_key_down_before_it_showed_doesnt_press()
    {
        var press = new MySettingsPress();
        Assert.Equal(MySettingsKeyStep.None, press.Key(true, live: false, modifier: true, false, true));
        Assert.Equal(MySettingsKeyStep.None, press.Key(false, live: true, modifier: true, false, true));
        Assert.False(press.KeyWaits);
    }

    [Fact]
    public void Reset_forgets_a_press()
    {
        var press = new MySettingsPress();
        press.Pad(true, true);
        press.Key(true, true, true, false, true);
        press.Reset();
        Assert.False(press.KeyWaits);
        Assert.True(press.Pad(true, true));
    }

    // ---- Kept from the host ----

    [Fact]
    public void A_press_is_kept_from_the_host_until_its_let_go_even_after_the_shortcut_went()
    {
        var keep = new MySettingsKeep();
        Assert.False(keep.Step(down: false, live: true));
        Assert.True(keep.Step(down: true, live: true));
        Assert.True(keep.Step(down: true, live: false)); // the menu went (the host resumed): still the friend's press
        Assert.False(keep.Step(down: false, live: false));
        Assert.False(keep.Step(down: true, live: false)); // a new press with nothing showing goes to the host
        Assert.Equal(1, keep.Count);
    }

    [Fact]
    public void A_hold_from_before_it_showed_goes_on_to_the_host()
    {
        var keep = new MySettingsKeep();
        Assert.False(keep.Step(down: true, live: false));
        Assert.False(keep.Step(down: true, live: true)); // a shot held through the host's pause: not ours
        Assert.False(keep.Step(down: false, live: true));
        Assert.True(keep.Step(down: true, live: true));
        Assert.Equal(1, keep.Count);
    }

    [Fact]
    public void Reset_and_clear()
    {
        var keep = new MySettingsKeep();
        keep.Step(false, true);
        keep.Step(true, true);
        keep.Reset();
        Assert.False(keep.Kept);
        Assert.Equal(1, keep.Count);
        keep.Clear();
        Assert.Equal(0, keep.Count);
    }

    // ---- The world map's Menu prompt (Default_Pause, any category) ----

    private const int PauseAction = 13; // Default_Pause's id, as Rewired would give it
    private const int DefaultCategory = 0;

    [Fact]
    public void The_maps_defaults_are_start_and_escape()
    {
        MySettingsBinding start = MySettingsBinding.StartDefaults;
        Assert.Equal(PadButtons.Start, start.Buttons);
        Assert.Empty(start.Axes);
        Assert.Equal(new[] { BindingElement.Key(MySettingsBinding.Escape) }, start.Keys);
        Assert.Equal(("Start", "Esc"), (start.PadName, start.KeyName));
        Assert.False(MySettingsBinding.IsModifierKey(start.Keys[0])); // acts as it goes down
        Assert.True(start.PadDown(PadButtons.Start, 0, 0, 0, 0, 0, 0));
        Assert.False(start.PadDown(PadButtons.None, 0, 0, 0, 0, 0, 1)); // RT isn't it
        Assert.Same(start, MySettingsBinding.From(null, -1, PauseAction, start));
    }

    [Fact]
    public void Any_category_finds_the_friends_own_pause_binding()
    {
        var own = new InputBindingSet(1, InputKind.Gamepad, BindingSource.OwnMaps, 1, Array.Empty<BindingElement>(), new[]
        {
            new InputBinding(DefaultCategory, PauseAction, BindingElement.PadButton(6)), // Back
            new InputBinding(UiCategory, RightTriggerAction, BindingElement.Axis(PadAxis.RightTrigger)),
        });

        MySettingsBinding map = MySettingsBinding.From(own, -1, PauseAction, MySettingsBinding.StartDefaults);
        Assert.Equal(PadButtons.Back, map.Buttons);
        Assert.Equal("Back", map.PadName);
        Assert.Equal(new[] { BindingElement.Key(MySettingsBinding.Escape) }, map.Keys); // a pad friend's keyboard: the game's key

        // The pause menu's stays the UI maps' right trigger.
        Assert.Equal("RT", MySettingsBinding.From(own, UiCategory, RightTriggerAction).PadName);
    }

    [Fact]
    public void A_pad_with_no_pause_binding_keeps_start()
    {
        var own = new InputBindingSet(1, InputKind.Gamepad, BindingSource.OwnMaps, 1, Array.Empty<BindingElement>(), new[]
        {
            new InputBinding(DefaultCategory, OtherAction, BindingElement.PadButton(7)),
        });

        MySettingsBinding map = MySettingsBinding.From(own, -1, PauseAction, MySettingsBinding.StartDefaults);
        Assert.Equal(PadButtons.Start, map.Buttons);
        Assert.Empty(map.Axes);
    }

    [Fact]
    public void A_keyboards_escape_is_its_pause_key_and_bit()
    {
        BindingElement esc = BindingElement.Key(MySettingsBinding.Escape);
        BindingElement alt = BindingElement.Key(MySettingsBinding.LeftAlt);
        var own = new InputBindingSet(1, InputKind.KeyboardMouse, BindingSource.GameDefaults, 0, new[] { alt, esc }, new[]
        {
            new InputBinding(DefaultCategory, PauseAction, esc),
            new InputBinding(UiCategory, RightTriggerAction, alt),
        });

        MySettingsBinding map = MySettingsBinding.From(own, -1, PauseAction, MySettingsBinding.StartDefaults);
        Assert.Equal(new[] { esc }, map.Keys);
        Assert.Equal("Esc", map.KeyName);
        Assert.Equal(1UL << 1, MySettingsBits.Mask(own, map.Keys));
        Assert.Equal(PadButtons.Start, map.Buttons); // the pad side keeps the game's
    }

    [Fact]
    public void The_maps_reasons_have_their_own_words()
    {
        Assert.NotEqual(MySettingsShortcut.Describe(MySettingsHidden.NoPauseMenu), MySettingsShortcut.Describe(MySettingsHidden.NoPauseMenu, MySettingsPlace.MapBar));
        Assert.Equal(MySettingsShortcut.Describe(MySettingsHidden.OwnMenuOpen), MySettingsShortcut.Describe(MySettingsHidden.OwnMenuOpen, MySettingsPlace.MapBar));
    }
}
