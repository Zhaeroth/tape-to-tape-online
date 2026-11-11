using System.Collections.Generic;
using System.Linq;
using T2TOnline.Core.Campaign;
using static T2TOnline.Core.Tests.Campaign.ControllerScreenStateTests;

namespace T2TOnline.Core.Tests.Campaign;

public class ControllerScreenPlanTests
{
    private const ulong Self = 76561199000000002UL;

    /// <summary>An icon as this PC's game shows it before the mirror touched it (the game's own colour and glyphs).</summary>
    private static ControllerIconShown Shown(int slot, int side = 1, int position = 6, bool ready = false, bool off = false, bool keyboard = false,
        bool controlled = true, long colour = ControllerScreenPlan.Unpainted, int glyphs = -1) =>
        new(slot, side, position, ready, off, keyboard, controlled, colour, glyphs);

    /// <summary>The same icon after the host's state went in (no step left).</summary>
    private static ControllerIconShown As(ControllerIcon icon, ulong self = Self) =>
        new(icon.Slot, icon.Side, icon.Position, icon.Ready, icon.Off, icon.Keyboard, icon.Controlled, icon.Colour, icon.Steam == self && self != 0 ? -1 : icon.Device);

    [Fact]
    public void A_move_to_a_side_with_a_lock_and_ready_is_side_then_position_then_ready()
    {
        ControllerScreenState host = Read(Settings(), Icon(0, side: 2, position: 2, ready: true));
        ControllerPlan plan = ControllerScreenPlan.For(new[] { Shown(0) }, host, Self, first: false);

        Assert.Equal(new[] { ControllerStepKind.Side, ControllerStepKind.Position, ControllerStepKind.Ready, ControllerStepKind.Colour, ControllerStepKind.Glyphs },
            plan.Steps.Select(s => s.Kind));
        Assert.Equal(2, plan.Steps[0].Value);
        Assert.True(plan.Steps[0].Tween);
        Assert.Equal(2, plan.Steps[1].Value);
        Assert.Equal(1, plan.Steps[2].Value);
        Assert.Equal(0x112233FFL, plan.Steps[3].Value);
        Assert.Equal(1, plan.Steps[4].Value);
        Assert.True(plan.RefreshReady);
        Assert.False(plan.Waiting);
        Assert.Empty(plan.Missing);
        Assert.Equal("P1 playing, locked at C, ready", ControllerScreenPlan.Describe(plan, campaign: true));
    }

    [Fact]
    public void A_move_to_the_centre_needs_no_unlock_of_its_own()
    {
        // The game's SetSide to the centre clears the lock and the position itself: nothing more to do.
        ControllerScreenState host = Read(Settings(), Icon(4, side: 1, position: 6, ready: false, colour: -1, device: -1));
        ControllerPlan plan = ControllerScreenPlan.For(new[] { Shown(4, side: 0, position: 3, ready: true) }, host, Self, first: false);

        Assert.Equal(new[] { new ControllerStep(4, ControllerStepKind.Side, 1, Tween: true) }, plan.Steps);
        Assert.Equal("P5 not playing", ControllerScreenPlan.Describe(plan, campaign: true));
        Assert.Equal("P5 to the middle", ControllerScreenPlan.Describe(plan, campaign: false));
    }

    [Fact]
    public void Equal_screens_need_nothing_and_the_first_apply_never_slides()
    {
        ControllerScreenState host = Read(Settings(), Icon(0, side: 2, position: 0, ready: true, keyboard: true), Icon(1, side: 0), Icon(2, ai: true));
        var same = host.Icons.Select(i => As(i)).ToList();
        ControllerPlan none = ControllerScreenPlan.For(same, host, Self, first: false);
        Assert.Empty(none.Steps);
        Assert.False(none.RefreshReady);

        // The first apply always runs the menu's ready check, and any slide is a jump.
        ControllerPlan opened = ControllerScreenPlan.For(new[] { Shown(0) }.Concat(same.Skip(1)).ToList(), host, Self, first: true);
        Assert.True(opened.RefreshReady);
        Assert.False(opened.Steps.Single(s => s.Kind == ControllerStepKind.Side).Tween);
        Assert.True(ControllerScreenPlan.For(same, host, Self, first: true).RefreshReady);
    }

    [Fact]
    public void Control_flags_come_first_and_name_the_device()
    {
        ControllerScreenState host = Read(Settings(), Icon(0, keyboard: true), Icon(1, ai: true));
        ControllerPlan plan = ControllerScreenPlan.For(new[] { Shown(0, colour: 0x112233FF, glyphs: 1), Shown(1, colour: 0x112233FF, glyphs: 1) }, host, Self, first: false);

        Assert.Equal(new[]
        {
            new ControllerStep(0, ControllerStepKind.Controls, 1, Keyboard: true),
            new ControllerStep(1, ControllerStepKind.Controls, 0),
        }, plan.Steps);
        Assert.True(plan.RefreshReady);
        Assert.Equal("P1 keyboard; P2 the AI plays it", ControllerScreenPlan.Describe(plan, campaign: true));
    }

    [Fact]
    public void This_players_own_icon_keeps_the_games_own_glyphs()
    {
        // Their own icon: the game's sprites, which the prompt pin draws in their own device and bindings. The others: the
        // device the host draws them with (unknown: the game's own).
        ControllerScreenState host = Read(Settings(), Icon(0, device: 1), Icon(1, device: 3, steam: Self), Icon(2, device: -1, steam: 5));
        ControllerPlan plan = ControllerScreenPlan.For(new[] { Shown(0, colour: 0x112233FF), Shown(1, colour: 0x112233FF, glyphs: 3), Shown(2, colour: 0x112233FF, glyphs: 2) },
            host, Self, first: false);

        Assert.Equal(new[]
        {
            new ControllerStep(0, ControllerStepKind.Glyphs, 1),
            new ControllerStep(1, ControllerStepKind.Glyphs, -1),
            new ControllerStep(2, ControllerStepKind.Glyphs, -1),
        }, plan.Steps);
        Assert.False(plan.RefreshReady);
        Assert.Equal("P1 its button glyphs; P2 its button glyphs; P3 its button glyphs", ControllerScreenPlan.Describe(plan, campaign: true));

        // Not knowing who this player is, every icon is someone else's.
        ControllerPlan unknown = ControllerScreenPlan.For(new[] { Shown(0, colour: 0x112233FF), Shown(1, colour: 0x112233FF), Shown(2, colour: 0x112233FF) }, host, 0, first: false);
        Assert.Equal(new[] { new ControllerStep(0, ControllerStepKind.Glyphs, 1), new ControllerStep(1, ControllerStepKind.Glyphs, 3) }, unknown.Steps);
    }

    [Fact]
    public void An_unknown_colour_is_the_games_own_and_a_known_one_is_painted_once()
    {
        ControllerScreenState unknown = Read(Settings(), Icon(0, colour: -1, device: -1));
        Assert.Empty(ControllerScreenPlan.For(new[] { Shown(0) }, unknown, Self, first: false).Steps);

        ControllerScreenState known = Read(Settings(), Icon(0, colour: 0x00FF00FF, device: -1));
        Assert.Equal(new[] { new ControllerStep(0, ControllerStepKind.Colour, 0x00FF00FF) }, ControllerScreenPlan.For(new[] { Shown(0) }, known, Self, first: false).Steps);
        Assert.Empty(ControllerScreenPlan.For(new[] { Shown(0, colour: 0x00FF00FF) }, known, Self, first: false).Steps);

        // Back to unknown: the game's own colour again.
        Assert.Equal(new[] { new ControllerStep(0, ControllerStepKind.Colour, -1) }, ControllerScreenPlan.For(new[] { Shown(0, colour: 0x00FF00FF) }, unknown, Self, first: false).Steps);
    }

    [Fact]
    public void An_icon_the_host_lacks_is_disabled_and_one_this_PC_lacks_is_named()
    {
        ControllerScreenState host = Read(Settings(), Icon(0, colour: -1, device: -1), Icon(9, colour: -1, device: -1));
        ControllerPlan plan = ControllerScreenPlan.For(new[] { Shown(0), Shown(1), Shown(2, off: true) }, host, Self, first: false);

        Assert.Equal(new[] { new ControllerStep(1, ControllerStepKind.Off, 1) }, plan.Steps);
        Assert.True(plan.RefreshReady);
        Assert.Equal(new[] { 9 }, plan.Missing);
    }

    [Fact]
    public void A_host_screen_with_no_icons_yet_changes_nothing()
    {
        // Its first read: icons not made (none), or a retained menu's icons from its last showing (all disabled).
        foreach (ControllerScreenState host in new[] { Read(Settings()), Read(Settings(), Icon(0, off: true), Icon(1, off: true)) })
        {
            ControllerPlan plan = ControllerScreenPlan.For(new[] { Shown(0), Shown(1) }, host, Self, first: true);
            Assert.True(plan.Waiting);
            Assert.Empty(plan.Steps);
            Assert.False(plan.RefreshReady);
        }
    }

    [Fact]
    public void A_disabled_host_icon_is_disabled_here_and_a_second_icon_of_one_player_is_left_alone()
    {
        ControllerScreenState host = Read(Settings(), Icon(0, colour: -1, device: -1), Icon(1, off: true, colour: -1, device: -1));
        ControllerPlan plan = ControllerScreenPlan.For(new List<ControllerIconShown> { Shown(0), Shown(1), Shown(1) }, host, Self, first: false);

        Assert.Equal(new[] { new ControllerStep(1, ControllerStepKind.Off, 1) }, plan.Steps);
        Assert.Equal("P2 off", ControllerScreenPlan.Describe(plan, campaign: true));
    }

    [Theory]
    [InlineData(0, "RW")]
    [InlineData(2, "C")]
    [InlineData(5, "Goalie")]
    [InlineData(6, "none")]
    public void Positions_are_named_as_the_games_enum(int position, string name) => Assert.Equal(name, ControllerScreenPlan.PositionWord(position));
}
