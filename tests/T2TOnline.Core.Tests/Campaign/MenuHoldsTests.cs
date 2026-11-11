using T2TOnline.Core.Campaign;
using T2TOnline.Core.Serialization;

namespace T2TOnline.Core.Tests.Campaign;

public class MenuHoldsTests
{
    private static readonly MenuTarget Details = MenuTarget.AtPath(new[] { 1, 3, 1, 0 }, "UIInputShortcut_V2");
    private static readonly MenuTarget Compare = MenuTarget.AtPath(new[] { 1, 2, 0, 4, 2 }, "UIInputShortcut_V2");

    [Fact]
    public void Hold_values_sit_above_every_list_index_and_read_back()
    {
        Assert.True(MenuHold.Released > MenuHold.MaxListIndex);
        Assert.True(MenuHold.Held <= MenuTarget.MaxIndex);
        Assert.True(MenuHold.TryRead(MenuHold.Value(true), out bool held) && held);
        Assert.True(MenuHold.TryRead(MenuHold.Value(false), out held) && !held);
        foreach (int index in new[] { 0, 1, 2, 999, 1002, 1023 })
        {
            Assert.False(MenuHold.IsHold(index));
            Assert.False(MenuHold.TryRead(index, out _));
        }
    }

    [Fact]
    public void A_hold_travels_as_a_value_event_with_no_new_layout()
    {
        // The board keeps the newest state per shortcut, sends each change once, and gives a friend who joins the hold.
        var board = new MenuBoard();
        uint serial = board.Opened("SkaterPowerupReward", "SkaterPowerupRewardMenu", "standard_game", MenuData.Empty)!.Serial;
        MenuEvent down = board.ValueSet(serial, Details, MenuHold.Value(true))!;
        Assert.Null(board.ValueSet(serial, Details, MenuHold.Value(true)));
        Assert.Contains(board.Snapshot(), e => e.Kind == MenuEventKind.ValueSet && e.Index == MenuHold.Held);

        var writer = new BitWriter(256);
        Assert.True(MenuMessages.Write(writer, down));
        var reader = new BitReader(writer.ToArray());
        Assert.True(MenuMessages.TryRead(down.Type, ref reader, out MenuEvent? read));
        Assert.Equal(Details, read!.Target);
        Assert.True(MenuHold.TryRead(read.Index, out bool held) && held);

        var mirror = new MenuMirror();
        foreach (MenuEvent e in board.Snapshot())
        {
            mirror.Apply(e);
        }

        Assert.True(mirror.TryGet(serial, out MirroredMenu menu));
        Assert.Equal(MenuHold.Held, menu.Values[Details]);
        Assert.NotNull(board.ValueSet(serial, Details, MenuHold.Value(false)));
    }

    [Fact]
    public void A_copy_applies_each_change_once_in_order()
    {
        var holds = new HoldFollower();
        Assert.Empty(holds.Due());
        holds.Want(Details, true);
        holds.Want(Compare, false);

        // Nothing is held on a fresh copy: only the hold is due, not the release.
        Assert.Equal(new[] { new HoldStep(Details, true) }, holds.Due());
        holds.Applied(Details, true);
        Assert.Empty(holds.Due());
        Assert.Equal((1, 1, 1), (holds.Followed, holds.Holds, holds.Wanted));

        holds.Want(Compare, true);
        holds.Want(Details, false);
        Assert.Equal(new[] { new HoldStep(Details, false), new HoldStep(Compare, true) }, holds.Due());
        holds.Applied(Details, false);
        holds.Applied(Compare, true);
        holds.Applied(Compare, true); // no change: not counted twice
        Assert.Empty(holds.Due());
        Assert.Equal((3, 2), (holds.Followed, holds.Holds));
        Assert.Equal(new[] { Compare }, holds.HeldNow());
    }

    [Fact]
    public void A_copy_made_again_gets_the_host_holds_again_and_a_missing_shortcut_waits_for_it()
    {
        var holds = new HoldFollower();
        holds.Want(Details, true);
        holds.Want(Compare, true);
        holds.Applied(Details, true);
        holds.Skip(Compare); // this copy has no such shortcut
        Assert.Empty(holds.Due());
        Assert.Equal(new[] { Details }, holds.HeldNow());

        // The copy closed (its held shortcut released first) and opened again: both are due once more.
        holds.Reset();
        Assert.Empty(holds.HeldNow());
        Assert.Equal(new[] { new HoldStep(Details, true), new HoldStep(Compare, true) }, holds.Due());
    }

    [Fact]
    public void A_release_the_copy_never_saw_held_is_nothing_to_do()
    {
        var holds = new HoldFollower();
        holds.Want(Details, false);
        Assert.Empty(holds.Due());
        holds.Applied(Details, false);
        Assert.Equal(0, holds.Followed);
    }
}
