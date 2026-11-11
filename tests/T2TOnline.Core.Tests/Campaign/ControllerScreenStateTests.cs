using System;
using System.Collections.Generic;
using System.Linq;
using T2TOnline.Core.Campaign;
using T2TOnline.Core.Protocol;
using T2TOnline.Core.Serialization;

namespace T2TOnline.Core.Tests.Campaign;

public class ControllerScreenStateTests
{
    private static KeyValuePair<string, NetValue> V(string key, NetValue value) => new(key, value);

    internal static List<KeyValuePair<string, NetValue>> Settings(int mode = 2, bool ready = false) => new()
    {
        V(ControllerScreenState.ModeKey, NetValue.FromInt(mode)),
        V(ControllerScreenState.LockedKey, NetValue.FromBool(true)),
        V(ControllerScreenState.OnboardingKey, NetValue.FromBool(false)),
        V(ControllerScreenState.ReadyKey, NetValue.FromBool(ready)),
    };

    /// <summary>A row as the host writes it: game player <paramref name="slot"/> with the numbers given.</summary>
    internal static MenuItem Icon(int slot, int side = 1, int position = 6, bool ready = false, bool off = false, bool keyboard = false, bool ai = false,
        long colour = 0x112233FF, int device = 1, ulong steam = 0) => ControllerScreenRow.Player(
        $"P{slot + 1}", slot, steam == 0 ? string.Empty : "Sam", steam, "playing", string.Empty, string.Empty, "gamepad", ai, colour, ready, keyboard,
        new ControllerRowNumbers(side, position, off, device));

    internal static ControllerScreenState Read(IReadOnlyList<KeyValuePair<string, NetValue>> values, params MenuItem[] rows)
    {
        Assert.True(ControllerScreenState.TryRead(new MenuData(values, rows), out ControllerScreenState? state, out string problem), problem);
        return state!;
    }

    [Fact]
    public void The_host_screen_reads_back_icon_by_icon()
    {
        ControllerScreenState state = Read(Settings(ready: true),
            Icon(0, side: 2, position: 2, ready: true, keyboard: true, steam: 0),
            Icon(1, side: 0, position: 4, device: 3, steam: 76561199000000001UL),
            Icon(2, ai: true, colour: -1, device: -1),
            new MenuItem("line", new[] { V("text", NetValue.FromString("Everyone's ready: the match starts.")) }));

        Assert.Equal((2, true, false, true), (state.Mode, state.LineupLocked, state.Onboarding, state.BothReady));
        Assert.Equal(new[]
        {
            new ControllerIcon(0, 2, 2, true, false, true, true, 0x112233FF, 1, 0),
            new ControllerIcon(1, 0, 4, false, false, false, true, 0x112233FF, 3, 76561199000000001UL),
            new ControllerIcon(2, 1, 6, false, false, false, false, -1, -1, 0),
        }, state.Icons);
        Assert.True(state.TryGet(1, out ControllerIcon second));
        Assert.Equal(4, second.Position);
        Assert.False(state.TryGet(7, out _));
        Assert.True(state.HasIcons);
    }

    [Fact]
    public void A_screen_with_no_icons_or_only_disabled_ones_has_none_yet()
    {
        // The host's first read is at T2TMenu.Show's start, before its icons are made (a log line from such a read: "6
        // line(s)").
        Assert.False(Read(Settings()).HasIcons);
        Assert.False(Read(Settings(), Icon(0, off: true), Icon(1, off: true)).HasIcons);
        Assert.True(Read(Settings(), Icon(0, off: true), Icon(1)).HasIcons);
    }

    [Fact]
    public void An_older_host_or_another_menu_reads_as_not_one()
    {
        // No settings: another menu's data, or an older host's controller screen.
        Assert.False(ControllerScreenState.TryRead(new MenuData(Array.Empty<KeyValuePair<string, NetValue>>(), new[] { Icon(0) }), out _, out string problem));
        Assert.Contains("older host", problem, StringComparison.Ordinal);

        // A version 1 row (no numbers).
        MenuItem old = new(ControllerScreenRow.Group, new[] { V("slot", NetValue.FromInt(0)), V("side", NetValue.FromString("playing")), V("ready", NetValue.FromBool(true)) });
        Assert.False(ControllerScreenState.TryRead(new MenuData(Settings(), new[] { old }), out _, out problem));
        Assert.Contains("older version", problem, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(-1, 1, 6, 0L)] // slot
    [InlineData(16, 1, 6, 0L)]
    [InlineData(0, 3, 6, 0L)] // side
    [InlineData(0, -1, 6, 0L)]
    [InlineData(0, 1, 7, 0L)] // position
    [InlineData(0, 1, -1, 0L)]
    [InlineData(0, 1, 6, -2L)] // colour
    [InlineData(0, 1, 6, 0x1_0000_0000L)]
    public void Values_out_of_the_games_ranges_are_refused(int slot, int side, int position, long colour)
    {
        MenuItem row = new(ControllerScreenRow.Group, new[]
        {
            V(ControllerScreenRow.SlotKey, NetValue.FromInt(slot)),
            V(ControllerScreenRow.SideKey, NetValue.FromInt(side)),
            V(ControllerScreenRow.PositionKey, NetValue.FromInt(position)),
            V(ControllerScreenRow.ColourKey, NetValue.FromInt(colour)),
        });
        Assert.False(ControllerScreenState.TryRead(new MenuData(Settings(), new[] { row }), out ControllerScreenState? state, out string problem));
        Assert.Null(state);
        Assert.NotEmpty(problem);
    }

    [Fact]
    public void A_mode_out_of_range_a_slot_twice_or_too_many_icons_are_refused()
    {
        Assert.False(ControllerScreenState.TryRead(new MenuData(Settings(mode: 3), new[] { Icon(0) }), out _, out _));
        Assert.False(ControllerScreenState.TryRead(new MenuData(Settings(), new[] { Icon(0), Icon(0) }), out _, out string twice));
        Assert.Contains("twice", twice, StringComparison.Ordinal);
        MenuItem[] seventeen = Enumerable.Range(0, 17).Select(i => Icon(i % 16)).ToArray();
        Assert.False(ControllerScreenState.TryRead(new MenuData(Settings(), seventeen), out _, out _));
        Assert.True(ControllerScreenState.TryRead(new MenuData(Settings(), Enumerable.Range(0, 16).Select(i => Icon(i)).ToArray()), out _, out _));
    }

    [Fact]
    public void An_unknown_device_reads_as_unknown_and_a_missing_flag_as_off()
    {
        MenuItem row = new(ControllerScreenRow.Group, new[]
        {
            V(ControllerScreenRow.SlotKey, NetValue.FromInt(3)),
            V(ControllerScreenRow.SideKey, NetValue.FromInt(0)),
            V(ControllerScreenRow.PositionKey, NetValue.FromInt(6)),
            V(ControllerScreenRow.DeviceKey, NetValue.FromInt(4000)),
            V(ControllerScreenRow.OffKey, NetValue.FromString("yes")), // not a flag
        });
        ControllerScreenState state = Read(Settings(), row);
        Assert.Equal(new ControllerIcon(3, 0, 6, false, false, false, true, -1, -1, 0), state.Icons.Single());
    }

    [Theory]
    [InlineData(0, 0)] // keyboard and mouse
    [InlineData(4, 4)] // a generic pad
    [InlineData(5, 5)] // the game's "other": it draws no glyphs, as the host's screen then shows none
    [InlineData(6, -1)] // not one of the game's devices
    [InlineData(31, -1)]
    [InlineData(-1, -1)]
    public void Only_the_games_own_devices_are_kept(int sent, int read)
    {
        Assert.Equal(read, Read(Settings(), Icon(0, device: sent)).Icons.Single().Device);
    }

    [Fact]
    public void Mutated_controller_screens_never_throw_and_what_reads_holds_its_ranges()
    {
        var rows = Enumerable.Range(0, 10).Select(i => Icon(i, side: i % 3, position: i % 7, ready: i % 2 == 0, device: i - 1, steam: (ulong)i)).ToArray();
        var state = new MenuState(5, "ControllerAssignmentCampaign", "ControllerAssignmentMenu", "world_map", 1, new MenuData(Settings(), rows));
        var writer = new BitWriter();
        Assert.True(MenuMessages.Write(writer, MenuEvent.Opened(state)));
        byte[] seed = writer.ToArray();
        var random = new Random(69);
        int read = 0;
        for (int round = 0; round < 3000; round++)
        {
            byte[] bytes = (byte[])seed.Clone();
            for (int flips = random.Next(1, 6); flips > 0; flips--)
            {
                bytes[random.Next(bytes.Length)] ^= (byte)(1 << random.Next(8));
            }

            var reader = new BitReader(bytes);
            if (!MenuMessages.TryRead(MessageType.MenuOpen, ref reader, out MenuEvent? menuEvent) || menuEvent?.State == null)
            {
                continue;
            }

            if (!ControllerScreenState.TryRead(menuEvent.State.Data, out ControllerScreenState? screen, out _))
            {
                continue;
            }

            read++;
            Assert.InRange(screen!.Mode, 0, ControllerScreenState.MaxMode);
            Assert.InRange(screen.Icons.Count, 0, ControllerScreenState.MaxIcons);
            Assert.Equal(screen.Icons.Count, screen.Icons.Select(i => i.Slot).Distinct().Count());
            Assert.All(screen.Icons, icon =>
            {
                Assert.InRange(icon.Slot, 0, ControllerScreenState.MaxSlot);
                Assert.InRange(icon.Side, 0, ControllerScreenState.MaxSide);
                Assert.InRange(icon.Position, 0, ControllerScreenState.NoPosition);
                Assert.InRange(icon.Colour, -1, uint.MaxValue);
                Assert.InRange(icon.Device, -1, ControllerScreenState.MaxDevice);
            });

            // A plan from whatever reads stays a plan.
            ControllerPlan plan = ControllerScreenPlan.For(screen.Icons.Select(i => new ControllerIconShown(i.Slot, 1, 6, false, false, false, false, ControllerScreenPlan.Unpainted, -1)).ToList(), screen, 7, first: false);
            Assert.All(plan.Steps, step => Assert.InRange(step.Slot, 0, ControllerScreenState.MaxSlot));
        }

        Assert.True(read > 0);
    }
}
