using T2TOnline.Core.Input;

namespace T2TOnline.Core.Tests.Input;

public class KeyboardChordFilterTests
{
    private const int LeftAlt = 308; // Unity's key code
    private const int RightAlt = 307;
    private const int UiCategory = 2;

    [Fact]
    public void The_games_own_menu_binding_on_alt_doesnt_count_as_playing_with_it()
    {
        var bindings = new[]
        {
            new InputBinding(UiCategory, 10, BindingElement.Key(LeftAlt)),
            new InputBinding(0, 11, BindingElement.Key(BindingElement.KeyQ)),
        };

        Assert.False(KeyboardChordFilter.PlaysWith(bindings, UiCategory, LeftAlt, RightAlt));
    }

    [Fact]
    public void A_gameplay_binding_on_alt_counts_as_playing_with_it()
    {
        var bindings = new[] { new InputBinding(0, 11, BindingElement.Key(RightAlt)) };

        Assert.True(KeyboardChordFilter.PlaysWith(bindings, UiCategory, LeftAlt, RightAlt));
    }

    [Fact]
    public void A_mouse_button_with_alts_code_isnt_alt()
    {
        var bindings = new[] { new InputBinding(0, 11, BindingElement.MouseButton(LeftAlt)) };

        Assert.False(KeyboardChordFilter.PlaysWith(bindings, UiCategory, LeftAlt, RightAlt));
    }

    private const ulong Tab = 1UL << 0;
    private const ulong W = 1UL << 1;
    private const ulong AltChord = 1UL << 2; // a binding that asks for Alt itself
    private const ulong Mouse = 1UL << 3;
    private const ulong Filterable = Tab | W; // keys whose binding needs neither Alt nor the Windows key

    [Fact]
    public void Without_alt_every_key_goes_through()
    {
        var filter = new KeyboardChordFilter();
        Assert.Equal((Tab | W, Tab), filter.Apply(Tab | W, Tab, altOrWindowsHeld: false, Filterable));
        Assert.Equal(0, filter.Filtered);
    }

    [Fact]
    public void Alt_tab_never_reaches_the_host_as_a_press_or_a_hold()
    {
        var filter = new KeyboardChordFilter();
        Assert.Equal((0UL, 0UL), filter.Apply(Tab, Tab, altOrWindowsHeld: true, Filterable));
        Assert.Equal(1, filter.Filtered);

        // Alt let go first, Tab still down: it stays left out until it's let go.
        Assert.Equal((0UL, 0UL), filter.Apply(Tab, 0, altOrWindowsHeld: false, Filterable));
        Assert.Equal((0UL, 0UL), filter.Apply(0, 0, altOrWindowsHeld: false, Filterable));

        // Pressed again without Alt: a press as usual.
        Assert.Equal((Tab, Tab), filter.Apply(Tab, Tab, altOrWindowsHeld: false, Filterable));
        Assert.Equal(1, filter.Filtered);
    }

    [Fact]
    public void A_key_held_before_alt_stays_held()
    {
        var filter = new KeyboardChordFilter();
        Assert.Equal((W, W), filter.Apply(W, W, altOrWindowsHeld: false, Filterable));
        Assert.Equal((W, 0UL), filter.Apply(W, 0, altOrWindowsHeld: true, Filterable));
        Assert.Equal((W, 0UL), filter.Apply(W | Tab, Tab, altOrWindowsHeld: true, Filterable)); // Tab left out, W kept
        Assert.Equal(1, filter.Filtered);
    }

    [Fact]
    public void A_binding_that_asks_for_alt_and_mouse_buttons_are_never_left_out()
    {
        var filter = new KeyboardChordFilter();
        Assert.Equal((AltChord | Mouse, AltChord | Mouse), filter.Apply(AltChord | Mouse, AltChord | Mouse, altOrWindowsHeld: true, Filterable));
        Assert.Equal(0, filter.Filtered);
    }

    [Fact]
    public void A_press_and_release_within_one_read_under_alt_is_left_out_once_then_forgotten()
    {
        var filter = new KeyboardChordFilter();
        Assert.Equal((0UL, 0UL), filter.Apply(0, Tab, altOrWindowsHeld: true, Filterable));
        Assert.Equal(1, filter.Filtered);
        Assert.Equal((Tab, Tab), filter.Apply(Tab, Tab, altOrWindowsHeld: false, Filterable));
    }

    [Fact]
    public void A_key_left_out_and_still_held_isnt_counted_again()
    {
        var filter = new KeyboardChordFilter();
        filter.Apply(Tab, Tab, altOrWindowsHeld: true, Filterable);
        filter.Apply(Tab, 0, altOrWindowsHeld: true, Filterable);
        filter.Apply(Tab, 0, altOrWindowsHeld: true, Filterable);
        Assert.Equal(1, filter.Filtered);
    }

    [Fact]
    public void Reset_forgets_the_keys_left_out()
    {
        var filter = new KeyboardChordFilter();
        filter.Apply(Tab, Tab, altOrWindowsHeld: true, Filterable);
        filter.Reset();
        Assert.Equal((Tab, 0UL), filter.Apply(Tab, 0, altOrWindowsHeld: false, Filterable));
    }

    [Fact]
    public void A_filter_that_never_went_off_asks_nothing_extra()
    {
        var filter = new KeyboardChordFilter();
        Assert.False(filter.ModifiersUnproven);
    }

    [Fact]
    public void Alt_that_reads_held_as_the_keyboard_comes_back_leaves_nothing_out_until_its_seen_up()
    {
        var filter = new KeyboardChordFilter();
        filter.Reset(); // Alt+Tab away: the keyboard went off, and Alt's release went to the other window
        Assert.True(filter.ModifiersUnproven);

        // Back, the game's key state still saying Alt: every press counts, or a stuck Alt would make the friend's skater deaf.
        Assert.Equal((W, W), filter.Apply(W, W, altOrWindowsHeld: true, Filterable));
        Assert.Equal((W | Tab, Tab), filter.Apply(W | Tab, Tab, altOrWindowsHeld: true, Filterable));
        Assert.Equal(0, filter.Filtered);
        Assert.True(filter.ModifiersUnproven);

        // Seen up: from now on Alt means Alt again.
        Assert.Equal((0UL, 0UL), filter.Apply(0, 0, altOrWindowsHeld: false, Filterable));
        Assert.False(filter.ModifiersUnproven);
        Assert.Equal((0UL, 0UL), filter.Apply(Tab, Tab, altOrWindowsHeld: true, Filterable));
        Assert.Equal(1, filter.Filtered);
    }

    [Fact]
    public void A_clean_return_is_proven_at_the_first_look()
    {
        var filter = new KeyboardChordFilter();
        filter.Reset();
        Assert.Equal((0UL, 0UL), filter.Apply(0, 0, altOrWindowsHeld: false, Filterable));
        Assert.False(filter.ModifiersUnproven);
        Assert.Equal((0UL, 0UL), filter.Apply(Tab, Tab, altOrWindowsHeld: true, Filterable)); // the next Alt+Tab is one
    }

    [Fact]
    public void All_64_elements_are_handled()
    {
        var filter = new KeyboardChordFilter();
        Assert.Equal((0UL, 0UL), filter.Apply(ulong.MaxValue, ulong.MaxValue, altOrWindowsHeld: true, ulong.MaxValue));
        Assert.Equal(64, filter.Filtered);
        Assert.Equal((0UL, 0UL), filter.Apply(ulong.MaxValue, 0, altOrWindowsHeld: false, ulong.MaxValue));
        Assert.Equal((0UL, 0UL), filter.Apply(0, 0, altOrWindowsHeld: false, ulong.MaxValue));
        Assert.Equal((1UL << 63, 1UL << 63), filter.Apply(1UL << 63, 1UL << 63, altOrWindowsHeld: false, ulong.MaxValue));
    }
}
