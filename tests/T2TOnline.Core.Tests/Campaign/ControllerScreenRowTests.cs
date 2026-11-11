using System;
using System.Collections.Generic;
using System.Linq;
using T2TOnline.Core.Campaign;
using T2TOnline.Core.Protocol;
using T2TOnline.Core.Serialization;

namespace T2TOnline.Core.Tests.Campaign;

public class ControllerScreenRowTests
{
    internal static MenuItem Row(int slot, ulong steam, ControllerRowNumbers? numbers = null) => ControllerScreenRow.Player(
        $"P{slot + 1} · {new string('n', 32)} · playing, LD ({new string('s', 40)}), keyboard, AI, ready", slot, new string('n', 32), steam,
        "not playing", "Right defense", new string('s', 40), "online pad", ai: true, color: 0xFF7F00FFL, ready: true, keyboard: true,
        numbers ?? new ControllerRowNumbers(2, 3, false, 4));

    [Fact]
    public void A_player_row_fits_the_protocols_rows()
    {
        // A row once had 12 values against a limit of 8, and every controller screen with a
        // player on it reached friends without its text. Version 2 (the real screen on friends) fills the 16.
        Assert.InRange(ControllerScreenRow.ValueCount, 1, MenuData.MaxItemValues);
        MenuItem row = Row(0, 76561199000000001UL);
        Assert.Equal(ControllerScreenRow.ValueCount, row.Values.Count);
        Assert.Equal(row.Values.Count, row.Values.Select(v => v.Key).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void A_full_controller_screen_round_trips_as_a_friend_reads_it()
    {
        // Ten players (the game's most) with the longest names and skaters the host sends, its settings and its status line.
        var items = Enumerable.Range(0, 10).Select(i => Row(i, i == 0 ? 0UL : 76561199000000000UL + (ulong)i, new ControllerRowNumbers(i % 3, i % 7, i == 9, i == 0 ? -1 : 4))).ToList();
        items.Add(new MenuItem("line", new[] { new KeyValuePair<string, NetValue>("text", NetValue.FromString("Waiting for 9 player(s) to be ready.")) }));
        var values = new[]
        {
            new KeyValuePair<string, NetValue>(ControllerScreenState.ModeKey, NetValue.FromInt(2)),
            new KeyValuePair<string, NetValue>(ControllerScreenState.LockedKey, NetValue.FromBool(false)),
            new KeyValuePair<string, NetValue>(ControllerScreenState.OnboardingKey, NetValue.FromBool(false)),
            new KeyValuePair<string, NetValue>(ControllerScreenState.ReadyKey, NetValue.FromBool(true)),
        };
        var state = new MenuState(3, "ControllerAssignmentCampaign", "ControllerAssignmentMenu", "standard_game", 0, new MenuData(values, items));

        var writer = new BitWriter();
        Assert.True(MenuMessages.Write(writer, MenuEvent.Opened(state)));
        var reader = new BitReader(writer.ToArray());
        Assert.True(MenuMessages.TryRead(MessageType.MenuOpen, ref reader, out MenuEvent? read));

        List<MenuItem> players = read!.State!.Data.Group(ControllerScreenRow.Group).ToList();
        Assert.Equal(10, players.Count);
        MenuItem first = players[0];
        Assert.Equal(0, first.Whole("slot", -1));
        Assert.Equal(0L, first.Whole("steam"));
        Assert.Equal(new string('n', 32), first.Text("name"));
        Assert.Equal("not playing", first.Text("side"));
        Assert.Equal("Right defense", first.Text("position"));
        Assert.Equal(new string('s', 40), first.Text("skater"));
        Assert.Equal("online pad", first.Text("device"));
        Assert.True(first.Flag("ai"));
        Assert.True(first.Flag("ready"));
        Assert.True(first.Flag("keyboard"));
        Assert.Equal(0xFF7F00FFL, first.Whole("color", -1));
        Assert.Equal(76561199000000009UL, unchecked((ulong)players[9].Whole("steam")));

        // The real screen's reading of the same bytes.
        Assert.True(ControllerScreenState.TryRead(read.State.Data, out ControllerScreenState? screen, out string problem), problem);
        Assert.Equal((2, false, false, true), (screen!.Mode, screen.LineupLocked, screen.Onboarding, screen.BothReady));
        Assert.Equal(10, screen.Icons.Count);
        Assert.Equal(new ControllerIcon(0, 0, 0, true, false, true, false, 0xFF7F00FFL, -1, 0), screen.Icons[0]);
        Assert.Equal(new ControllerIcon(9, 0, 2, true, true, true, false, 0xFF7F00FFL, 4, 76561199000000009UL), screen.Icons[9]);
    }

    [Fact]
    public void A_row_over_the_limit_is_still_refused()
    {
        var values = Enumerable.Range(0, MenuData.MaxItemValues + 1)
            .Select(i => new KeyValuePair<string, NetValue>("v" + i, NetValue.FromInt(i))).ToList();

        Assert.Throws<ArgumentException>(() => new MenuItem(ControllerScreenRow.Group, values));
    }
}
