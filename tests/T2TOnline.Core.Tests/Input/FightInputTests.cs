using T2TOnline.Core.Input;

namespace T2TOnline.Core.Tests.Input;

/// <summary>A player's fight verbs on their own device, and what they pressed in a brawl.</summary>
public class FightInputTests
{
    // This build's ids (the Nov 1 build's Rewired data): block high 75, block low 76, jab 82, uppercut 74, in Default (0).
    private static readonly FightActionIds Ids = new(BlockHigh: 75, BlockLow: 76, Jab: 82, Uppercut: 74, Category: 0);

    private static readonly InputBinding[] PadTemplate =
    {
        new(0, 74, BindingElement.PadButton(1)), // B
        new(0, 75, BindingElement.PadButton(5)), // RB
        new(0, 76, BindingElement.PadButton(4)), // LB
        new(0, 82, BindingElement.PadButton(2)), // X
        new(0, 2, BindingElement.PadButton(2)), // Shoot on X too: not a fight verb
        new(6, 64, BindingElement.PadButton(5)), // the Fighting maps' stale ids: not read
        new(4, 75, BindingElement.PadButton(3)), // another category: not the forward controls
    };

    private static PadState Pad(PadButtons buttons, float lt = 0, float rt = 0, float lx = 0) =>
        new(buttons, 0, PadState.StickToRaw(lx), 0, 0, 0, PadState.TriggerToRaw(lt), PadState.TriggerToRaw(rt));

    [Fact]
    public void The_game_template_maps_its_four_verbs_and_nothing_else()
    {
        FightInputMap map = FightInputMap.Build(PadTemplate, Ids, keyTable: null);
        Assert.Equal(FightActions.None, map.Unbound);
        Assert.Equal(FightActions.BlockHigh, map.Held(Pad(PadButtons.RightBumper), PadButtons.None));
        Assert.Equal(FightActions.BlockLow | FightActions.Jab, map.Held(Pad(PadButtons.LeftBumper | PadButtons.X), PadButtons.None));
        Assert.Equal(FightActions.None, map.Held(Pad(PadButtons.Y | PadButtons.A), PadButtons.None)); // Y is block high only in another category
        Assert.Equal(FightActions.Uppercut, map.Held(Pad(PadButtons.None), PadButtons.B)); // a tap holds for its frame
        Assert.Equal(FightActions.Uppercut, map.Tapped(PadButtons.B | PadButtons.A));
        Assert.Equal("RightBumper", map.Where(FightActions.BlockHigh, e => e.ToString()));
    }

    [Fact]
    public void A_verb_on_a_trigger_or_stick_counts_past_the_dead_zone()
    {
        InputBinding[] remapped =
        {
            new(0, 76, BindingElement.Axis(PadAxis.LeftTrigger), Range: BindingRange.Positive),
            new(0, 75, BindingElement.Axis(PadAxis.LeftStickX), Negative: true, Range: BindingRange.Negative),
        };
        FightInputMap map = FightInputMap.Build(remapped, Ids, null);
        Assert.Equal(FightActions.Jab | FightActions.Uppercut, map.Unbound);
        Assert.Equal(FightActions.None, map.Held(Pad(PadButtons.None, lt: 0.45f), PadButtons.None)); // more than 0.5, as the game wants
        Assert.Equal(FightActions.BlockLow, map.Held(Pad(PadButtons.None, lt: 0.6f), PadButtons.None));
        Assert.Equal(FightActions.None, map.Held(Pad(PadButtons.None, lx: 0.9f), PadButtons.None));
        Assert.Equal(FightActions.BlockHigh, map.Held(Pad(PadButtons.None, lx: -0.9f), PadButtons.None));
        Assert.Equal("nothing", map.Where(FightActions.Jab, e => e.ToString()));
    }

    [Fact]
    public void A_keyboard_maps_by_its_key_table()
    {
        var builder = new KeyboardBindingTableBuilder(KeyboardBindingTableBuilder.BrawlKeys);
        BindingElement shift = BindingElement.Key(304);
        BindingElement control = BindingElement.Key(306);
        builder.Add(new InputBinding(0, 75, shift));
        builder.Add(new InputBinding(0, 76, control));
        builder.Add(new InputBinding(0, 82, BindingElement.Key(32)));
        InputBindingSet table = builder.Build(1, BindingSource.GameDefaults, 0);
        FightInputMap map = FightInputMap.Build(table.Bindings, Ids, table);
        ulong shiftBit = 1UL << table.IndexOf(shift);
        ulong controlBit = 1UL << table.IndexOf(control);
        Assert.Equal(FightActions.Uppercut, map.Unbound);
        Assert.Equal(FightActions.BlockHigh, map.Held(shiftBit, 0));
        Assert.Equal(FightActions.BlockLow, map.Held(0, controlBit));
        Assert.Equal(FightActions.BlockLow, map.Tapped(controlBit));
        Assert.Equal(FightActions.None, map.Held(0b11, 0)); // Q and E open the table: not fight verbs

        // Keys without a table can't be read: left out.
        Assert.Equal(FightActions.All, FightInputMap.Build(table.Bindings, Ids, null).Unbound);
    }

    [Fact]
    public void Presses_count_fresh_presses_and_taps_and_add_up_held_time()
    {
        var presses = new FightPresses();
        Assert.Equal(FightActions.None, presses.Feed(FightActions.BlockHigh, FightActions.None, 0)); // not running yet
        presses.Begin(1_000_000);
        Assert.Equal(FightActions.BlockHigh, presses.Feed(FightActions.BlockHigh, FightActions.None, 1_000_000));
        Assert.Equal(FightActions.None, presses.Feed(FightActions.BlockHigh, FightActions.None, 1_500_000)); // still held: no new press
        Assert.Equal(FightActions.BlockHigh, presses.Feed(FightActions.BlockHigh, FightActions.BlockHigh, 1_600_000)); // let go and pressed again
        Assert.Equal(FightActions.Jab, presses.Feed(FightActions.Jab, FightActions.None, 2_000_000));
        Assert.Equal(FightActions.None, presses.Feed(FightActions.None, FightActions.None, 2_100_000));
        presses.End(3_000_000);

        Assert.Equal(2, presses.PressesOf(FightActions.BlockHigh));
        Assert.Equal(1, presses.PressesOf(FightActions.Jab));
        Assert.Equal(0, presses.PressesOf(FightActions.BlockLow));
        Assert.Equal(1_000_000, presses.HeldMicrosOf(FightActions.BlockHigh));
        Assert.Equal(100_000, presses.HeldMicrosOf(FightActions.Jab));
        Assert.Equal(1_600_000, presses.LastBlockPressMicros);
        Assert.Equal(2_000_000, presses.DurationMicros);
        Assert.False(presses.Running);
        Assert.Equal(
            "block high 2 time(s) (held 1.0 s) on RB, block low 0 time(s) on LB, jab 1 time(s) on X, uppercut 0 time(s) on B",
            presses.Describe(v => v switch { FightActions.BlockHigh => "RB", FightActions.BlockLow => "LB", FightActions.Jab => "X", _ => "B" }));

        presses.Begin(5_000_000);
        Assert.Equal(0, presses.PressesOf(FightActions.BlockHigh));
        Assert.Equal(long.MinValue, presses.LastBlockPressMicros);
    }

    [Fact]
    public void A_clock_that_goes_back_adds_no_time()
    {
        var presses = new FightPresses();
        presses.Begin(1_000_000);
        presses.Feed(FightActions.BlockLow, FightActions.None, 1_000_000);
        presses.Feed(FightActions.BlockLow, FightActions.None, 900_000);
        presses.Feed(FightActions.BlockLow, FightActions.None, 1_200_000);
        presses.End(1_200_000);
        Assert.Equal(200_000, presses.HeldMicrosOf(FightActions.BlockLow));
    }
}
