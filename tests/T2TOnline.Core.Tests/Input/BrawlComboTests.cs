using T2TOnline.Core.Input;
using T2TOnline.Core.Tests.Protocol;

namespace T2TOnline.Core.Tests.Input;

/// <summary>The couch's brawl-offer combo, as the host relays it for friends: both held, one just pressed.</summary>
public class BrawlComboTests
{
    private const ulong Lb = (ulong)PadButtons.LeftBumper;
    private const ulong Rb = (ulong)PadButtons.RightBumper;
    private const ulong Combo = (ulong)BrawlCombo.PadCombo;

    [Fact]
    public void Both_held_with_one_just_pressed_fires()
    {
        Assert.True(BrawlCombo.Fires(previousHeld: Lb, held: Lb | Rb, taps: 0, Combo));
        Assert.True(BrawlCombo.Fires(previousHeld: 0, held: Lb | Rb, taps: 0, Combo));
        Assert.True(BrawlCombo.Fires(previousHeld: Lb | (ulong)PadButtons.A, held: Lb | Rb | (ulong)PadButtons.A, taps: 0, Combo));
    }

    [Fact]
    public void Held_on_from_before_or_one_alone_does_not()
    {
        Assert.False(BrawlCombo.Fires(previousHeld: Lb | Rb, held: Lb | Rb, taps: 0, Combo)); // the game wants a fresh press
        Assert.False(BrawlCombo.Fires(previousHeld: 0, held: Lb, taps: 0, Combo));
        Assert.False(BrawlCombo.Fires(previousHeld: 0, held: (ulong)PadButtons.A, taps: 0, Combo));
        Assert.False(BrawlCombo.Fires(previousHeld: 0, held: ulong.MaxValue, taps: 0, combo: 0));
    }

    [Fact]
    public void A_tap_counts_as_down_and_just_pressed()
    {
        Assert.True(BrawlCombo.Fires(previousHeld: Lb, held: Lb, taps: Rb, Combo));
        Assert.True(BrawlCombo.Fires(previousHeld: Lb | Rb, held: Lb | Rb, taps: Rb, Combo)); // let go and pressed again in one update
        Assert.False(BrawlCombo.Fires(previousHeld: 0, held: 0, taps: Lb, Combo));
    }

    [Fact]
    public void The_watch_fires_on_a_fresh_combo_as_the_couch_does()
    {
        var watch = new BrawlComboWatch();
        Assert.False(watch.Next(Lb, 0, Combo));
        Assert.True(watch.Next(Lb | Rb, 0, Combo));
        Assert.False(watch.Next(Lb | Rb, 0, Combo)); // held on: no new press
        Assert.False(watch.Next(0, 0, Combo));
        Assert.True(watch.Next(Lb, Rb, Combo)); // a tap counts
    }

    [Fact]
    public void A_combo_held_through_a_hand_back_is_no_fresh_press()
    {
        // The AI played for them, or they were away, with LB+RB already down: their controller takes their input again.
        var watch = new BrawlComboWatch();
        watch.Hold();
        Assert.False(watch.Next(Lb | Rb, 0, Combo));
        Assert.False(watch.Next(Lb | Rb, 0, Combo));

        // Let go and pressed again: a combo, as their controller's own buttons count.
        Assert.False(watch.Next(Lb, 0, Combo));
        Assert.True(watch.Next(Lb | Rb, 0, Combo));

        // A tap on the very update of the hand-back was down before it too.
        watch.Hold();
        Assert.False(watch.Next(Lb, Rb, Combo));
        Assert.False(watch.Next(Lb, 0, Combo));
        Assert.True(watch.Next(Lb | Rb, 0, Combo));
    }

    [Fact]
    public void The_keyboard_combo_is_q_and_e_in_the_friends_table()
    {
        ulong combo = BrawlCombo.KeyboardCombo(InputMessagesTests.KeyboardSet());
        Assert.Equal(0b11UL, combo); // Q and E open every key table
        Assert.True(BrawlCombo.Fires(0b01, 0b11, 0, combo));
        Assert.Equal(0UL, BrawlCombo.KeyboardCombo(null));
        Assert.Equal(0UL, BrawlCombo.KeyboardCombo(InputMessagesTests.PadSet()));
    }
}
