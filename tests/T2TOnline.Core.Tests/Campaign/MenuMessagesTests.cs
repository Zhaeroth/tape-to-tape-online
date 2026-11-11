using System;
using System.Collections.Generic;
using System.Linq;
using T2TOnline.Core.Campaign;
using T2TOnline.Core.Protocol;
using T2TOnline.Core.Serialization;

namespace T2TOnline.Core.Tests.Campaign;

public class MenuMessagesTests
{
    private static KeyValuePair<string, NetValue> V(string key, NetValue value) => new(key, value);

    private static MenuData Offers() => new(
        new[] { V("rerollCount", NetValue.FromInt(2)), V("category", NetValue.FromInt(3)), V("isTeamHome", NetValue.FromBool(true)) },
        new[]
        {
            new MenuItem("relic", new[] { V("id", NetValue.FromString("goldenStick")), V("level", NetValue.FromInt(1)) }),
            new MenuItem("relic", new[] { V("id", NetValue.FromString("lucky_puck")), V("level", NetValue.FromInt(2)) }),
            new MenuItem("skater", new[] { V("id", NetValue.FromString("fwd-17")) }),
        });

    private static byte[] Written(MenuEvent menuEvent)
    {
        var writer = new BitWriter();
        Assert.True(MenuMessages.Write(writer, menuEvent));
        return writer.ToArray();
    }

    private static MenuEvent? Read(MessageType type, byte[] bytes)
    {
        var reader = new BitReader(bytes);
        return MenuMessages.TryRead(type, ref reader, out MenuEvent? read) ? read : null;
    }

    private static MenuEvent RoundTrip(MenuEvent menuEvent)
    {
        MenuEvent? read = Read(menuEvent.Type, Written(menuEvent));
        Assert.NotNull(read);
        return read!;
    }

    [Fact]
    public void An_opened_menu_round_trips_with_its_data()
    {
        var state = new MenuState(7, "RelicRewardMenu", "RelicRewardMenu", "standard_game", 0, Offers());
        MenuEvent read = RoundTrip(MenuEvent.Opened(state));

        Assert.Equal(MenuEventKind.Opened, read.Kind);
        Assert.Equal(MessageType.MenuOpen, read.Type);
        Assert.Equal(7u, read.Serial);
        Assert.Equal("RelicRewardMenu", read.State!.Id);
        Assert.Equal("standard_game", read.State.Scene);
        Assert.Equal(state.Data.ContentHash, read.State.Data.ContentHash);
        Assert.Equal(2, read.State.Data.Whole("rerollCount"));
        Assert.True(read.State.Data.Flag("isTeamHome"));
        Assert.Equal(new[] { "goldenStick", "lucky_puck" }, read.State.Data.Group("relic").Select(r => r.Text("id")));
        Assert.Equal(2, read.State.Data.Group("relic").Last().Whole("level"));
        Assert.Equal("fwd-17", read.State.Data.Group("skater").Single().Text("id"));
    }

    [Fact]
    public void Updates_closes_selections_values_and_clicks_round_trip()
    {
        var state = new MenuState(9, "ChooseMetaMenu", "ChooseMetaMenu", string.Empty, 4, MenuData.Empty);
        MenuEvent updated = RoundTrip(MenuEvent.Updated(state));
        Assert.Equal(MenuEventKind.Updated, updated.Kind);
        Assert.Equal(4u, updated.State!.Version);
        Assert.Equal(string.Empty, updated.State.Scene);

        Assert.Equal(MenuEventKind.Closed, RoundTrip(MenuEvent.Closed(9)).Kind);

        MenuTarget path = MenuTarget.AtPath(new[] { 0, 3, 12, 4095 }, "MetaTeamItem(Clone)");
        MenuEvent selected = RoundTrip(MenuEvent.Selected(9, path));
        Assert.Equal(MessageType.MenuNav, selected.Type);
        Assert.Equal(path, selected.Target);
        Assert.Equal(MenuTarget.None, RoundTrip(MenuEvent.Selected(9, MenuTarget.None)).Target);

        MenuEvent value = RoundTrip(MenuEvent.ValueSet(9, MenuTarget.AtItem("state", 1), 2));
        Assert.Equal(MessageType.MenuSelect, value.Type);
        Assert.Equal(MenuTarget.AtItem("state", 1), value.Target);
        Assert.Equal(2, value.Index);

        MenuEvent click = RoundTrip(MenuEvent.Clicked(9, path, 1, 3));
        Assert.Equal(MessageType.MenuClick, click.Type);
        Assert.Equal((byte)1, click.Result);
        Assert.Equal((byte)3, click.Effect);
    }

    [Fact]
    public void Text_from_the_host_is_made_safe_to_show()
    {
        var data = new MenuData(new[] { V("title", NetValue.FromString("Pick one\n[ERROR] forged log line\u202E")) }, Array.Empty<MenuItem>());
        MenuEvent read = RoundTrip(MenuEvent.Opened(new MenuState(1, "EventChoice", "EventChoiceMenu", "world_map", 0, data)));
        string title = read.State!.Data.Text("title");
        Assert.DoesNotContain('\n', title);
        Assert.DoesNotContain('\u202E', title);
        Assert.StartsWith("Pick one", title, StringComparison.Ordinal);

        MenuTarget leaf = RoundTrip(MenuEvent.Selected(1, MenuTarget.AtPath(new[] { 1 }, "Card\r\nx"))).Target;
        Assert.DoesNotContain('\n', leaf.Leaf);
    }

    [Fact]
    public void Bad_names_counts_and_indices_are_refused_when_built()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new MenuState(0, "Game", "GameMenu", string.Empty, 0, MenuData.Empty));
        Assert.Throws<ArgumentException>(() => new MenuState(1, string.Empty, "GameMenu", string.Empty, 0, MenuData.Empty));
        Assert.Throws<ArgumentException>(() => new MenuState(1, "Gäme", "GameMenu", string.Empty, 0, MenuData.Empty));
        Assert.Throws<ArgumentException>(() => new MenuState(1, new string('a', MenuState.MaxNameBytes + 1), "GameMenu", string.Empty, 0, MenuData.Empty));
        Assert.Throws<ArgumentException>(() => new MenuData(new[] { V("a", NetValue.Null), V("a", NetValue.Null) }, Array.Empty<MenuItem>()));
        Assert.Throws<ArgumentException>(() => new MenuData(Enumerable.Range(0, MenuData.MaxValues + 1).Select(i => V("k" + i, NetValue.Null)).ToList(), Array.Empty<MenuItem>()));
        Assert.Throws<ArgumentException>(() => new MenuData(Array.Empty<KeyValuePair<string, NetValue>>(), Enumerable.Range(0, MenuData.MaxItems + 1).Select(_ => new MenuItem("x", Array.Empty<KeyValuePair<string, NetValue>>())).ToList()));
        Assert.Throws<ArgumentException>(() => new MenuItem(" lead", Array.Empty<KeyValuePair<string, NetValue>>()));
        Assert.Throws<ArgumentException>(() => MenuTarget.AtPath(Array.Empty<int>(), "x"));
        Assert.Throws<ArgumentException>(() => MenuTarget.AtPath(new int[MenuTarget.MaxDepth + 1], "x"));
        Assert.Throws<ArgumentException>(() => MenuTarget.AtPath(new[] { MenuTarget.MaxSiblingIndex + 1 }, "x"));
        Assert.Throws<ArgumentOutOfRangeException>(() => MenuTarget.AtItem("relic", MenuTarget.MaxIndex + 1));
        Assert.Throws<ArgumentException>(() => MenuEvent.ValueSet(1, MenuTarget.None, 0));
        Assert.Throws<ArgumentException>(() => MenuEvent.Clicked(1, MenuTarget.None, 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => MenuEvent.Closed(0));
    }

    [Fact]
    public void Crafted_messages_out_of_range_are_refused_when_read()
    {
        // Serial 0.
        var writer = new BitWriter();
        writer.WriteByte((byte)MenuEventKind.Closed);
        writer.WriteUInt32(0);
        Assert.Null(Read(MessageType.MenuOpen, writer.ToArray()));

        // An unknown change.
        writer.Reset();
        writer.WriteByte(9);
        writer.WriteUInt32(1);
        Assert.Null(Read(MessageType.MenuOpen, writer.ToArray()));

        // A path deeper than allowed.
        writer.Reset();
        writer.WriteUInt32(1);
        writer.WriteByte((byte)MenuTargetKind.Path);
        writer.WriteVarUInt(MenuTarget.MaxDepth + 1);
        for (int i = 0; i <= MenuTarget.MaxDepth; i++)
        {
            writer.WriteVarUInt(0);
        }

        writer.WriteShortString("x");
        Assert.Null(Read(MessageType.MenuNav, writer.ToArray()));

        // A sibling index out of range.
        writer.Reset();
        writer.WriteUInt32(1);
        writer.WriteByte((byte)MenuTargetKind.Path);
        writer.WriteVarUInt(1);
        writer.WriteVarUInt(MenuTarget.MaxSiblingIndex + 1);
        writer.WriteShortString("x");
        Assert.Null(Read(MessageType.MenuNav, writer.ToArray()));

        // A value out of range.
        writer.Reset();
        writer.WriteUInt32(1);
        MenuTarget.AtItem("state", 0).Write(writer);
        writer.WriteVarUInt(MenuTarget.MaxIndex + 1);
        Assert.Null(Read(MessageType.MenuSelect, writer.ToArray()));

        // A value or click on nothing.
        writer.Reset();
        writer.WriteUInt32(1);
        MenuTarget.None.Write(writer);
        writer.WriteVarUInt(0);
        Assert.Null(Read(MessageType.MenuSelect, writer.ToArray()));

        // Repeated value names.
        writer.Reset();
        writer.WriteByte((byte)MenuEventKind.Opened);
        writer.WriteUInt32(1);
        writer.WriteShortString("Game");
        writer.WriteShortString("GameMenu");
        writer.WriteShortString(string.Empty);
        writer.WriteVarUInt(0);
        writer.WriteVarUInt(2);
        writer.WriteShortString("a");
        NetValue.FromInt(1).Write(writer);
        writer.WriteShortString("a");
        NetValue.FromInt(2).Write(writer);
        writer.WriteVarUInt(0);
        Assert.Null(Read(MessageType.MenuOpen, writer.ToArray()));

        // More rows than allowed.
        writer.Reset();
        writer.WriteByte((byte)MenuEventKind.Opened);
        writer.WriteUInt32(1);
        writer.WriteShortString("Game");
        writer.WriteShortString("GameMenu");
        writer.WriteShortString(string.Empty);
        writer.WriteVarUInt(0);
        writer.WriteVarUInt(0);
        writer.WriteVarUInt(MenuData.MaxItems + 1);
        Assert.Null(Read(MessageType.MenuOpen, writer.ToArray()));
    }

    [Fact]
    public void A_close_carries_its_reason_and_transition()
    {
        MenuEvent plain = RoundTrip(MenuEvent.Closed(9));
        Assert.Equal((MenuCloseReason.Other, MenuTransition.None), (plain.Reason, plain.Transition));

        foreach (MenuCloseReason reason in new[] { MenuCloseReason.Other, MenuCloseReason.Back })
        {
            foreach (MenuTransition transition in new[] { MenuTransition.None, MenuTransition.Progress, MenuTransition.Loading, MenuTransition.Regress })
            {
                MenuEvent read = RoundTrip(MenuEvent.Closed(9, reason, transition));
                Assert.Equal(MenuEventKind.Closed, read.Kind);
                Assert.Equal(9u, read.Serial);
                Assert.Equal((reason, transition), (read.Reason, read.Transition));
            }
        }

        Assert.Equal("Closed #9 (Back, Loading)", MenuEvent.Closed(9, MenuCloseReason.Back, MenuTransition.Loading).ToString());
        Assert.Equal("Closed #9", MenuEvent.Closed(9).ToString());
        Assert.Throws<ArgumentException>(() => MenuEvent.Closed(9, (MenuCloseReason)2));
        Assert.Throws<ArgumentException>(() => MenuEvent.Closed(9, MenuCloseReason.Other, (MenuTransition)4));
    }

    [Fact]
    public void A_close_with_an_unknown_reason_or_transition_or_without_them_is_refused()
    {
        static byte[] Close(params byte[] tail)
        {
            var writer = new BitWriter();
            writer.WriteByte((byte)MenuEventKind.Closed);
            writer.WriteUInt32(4);
            foreach (byte b in tail)
            {
                writer.WriteByte(b);
            }

            return writer.ToArray();
        }

        Assert.NotNull(Read(MessageType.MenuOpen, Close(1, 3)));
        Assert.Null(Read(MessageType.MenuOpen, Close()));
        Assert.Null(Read(MessageType.MenuOpen, Close(1)));
        Assert.Null(Read(MessageType.MenuOpen, Close(2, 0)));
        Assert.Null(Read(MessageType.MenuOpen, Close(0, 4)));
        Assert.Null(Read(MessageType.MenuOpen, Close(255, 255)));
        Assert.Null(Read(MessageType.MenuOpen, Close(0, 0, 0)));
    }

    [Fact]
    public void The_board_closes_a_menu_with_its_reason_and_transition()
    {
        var board = new MenuBoard();
        uint serial = board.Opened("ChooseMeta", "ChooseMetaMenu", "start_screen", MenuData.Empty)!.Serial;
        MenuEvent closed = board.Closed(serial, MenuCloseReason.Back, MenuTransition.Regress)!;
        Assert.Equal((MenuCloseReason.Back, MenuTransition.Regress), (closed.Reason, closed.Transition));
        Assert.Null(board.Closed(serial, MenuCloseReason.Back, MenuTransition.Regress));

        var mirror = new MenuMirror();
        uint again = board.Opened("RunEnd", "RunEndMenu", "standard_game", MenuData.Empty)!.Serial;
        Assert.Equal(MenuMirrorChange.Opened, mirror.Apply(RoundTrip(MenuEvent.Opened(board.Open.Single()))));
        Assert.Equal(MenuMirrorChange.Closed, mirror.Apply(RoundTrip(board.Closed(again, MenuCloseReason.Other, MenuTransition.Loading)!)));
        Assert.Empty(mirror.Open);
    }

    [Fact]
    public void A_message_with_bytes_left_over_or_of_another_type_is_refused()
    {
        byte[] closed = Written(MenuEvent.Closed(3));
        Assert.NotNull(Read(MessageType.MenuOpen, closed));
        Assert.Null(Read(MessageType.MenuOpen, closed.Concat(new byte[] { 0 }).ToArray()));
        Assert.Null(Read(MessageType.HostView, closed));
        Assert.Null(Read(MessageType.MenuOpen, Array.Empty<byte>()));
    }

    [Fact]
    public void Every_truncation_and_random_bytes_never_throw()
    {
        var events = new[]
        {
            MenuEvent.Opened(new MenuState(5, "SkaterPowerupReward", "SkaterPowerupRewardMenu", "standard_game", 3, Offers())),
            MenuEvent.Selected(5, MenuTarget.AtPath(new[] { 2, 0, 7 }, "PowerUp")),
            MenuEvent.ValueSet(5, MenuTarget.AtItem("state", 0), 1),
            MenuEvent.Clicked(5, MenuTarget.AtItem("relic", 1), 1, 0),
            MenuEvent.Synced(new uint[] { 3, 5, 9 }),
            MenuEvent.Closed(5, MenuCloseReason.Back, MenuTransition.Regress),
        };

        foreach (MenuEvent menuEvent in events)
        {
            byte[] bytes = Written(menuEvent);
            for (int length = 0; length < bytes.Length; length++)
            {
                Assert.Null(Read(menuEvent.Type, bytes.AsSpan(0, length).ToArray()));
            }
        }

        var random = new Random(11);
        var types = new[] { MessageType.MenuOpen, MessageType.MenuNav, MessageType.MenuSelect, MessageType.MenuClick };
        for (int i = 0; i < 6000; i++)
        {
            MenuEvent source = events[i % events.Length];
            byte[] junk = Written(source);
            if (i % 3 == 0)
            {
                junk = new byte[random.Next(0, 120)];
                random.NextBytes(junk);
            }
            else
            {
                // A real message with a few bytes flipped: the hardest inputs for a decoder.
                for (int flips = random.Next(1, 4); flips > 0 && junk.Length > 0; flips--)
                {
                    junk[random.Next(junk.Length)] ^= (byte)random.Next(1, 256);
                }
            }

            _ = Read(types[random.Next(types.Length)], junk);
        }
    }

    [Fact]
    public void A_message_over_the_size_limit_is_not_written()
    {
        string text = new('x', 250);
        var items = Enumerable.Range(0, MenuData.MaxItems)
            .Select(i => new MenuItem("line", Enumerable.Range(0, MenuData.MaxItemValues).Select(k => V("t" + k, NetValue.FromString(text))).ToList()))
            .ToList();
        var huge = new MenuState(1, "RunEnd", "RunEndMenu", string.Empty, 0, new MenuData(Array.Empty<KeyValuePair<string, NetValue>>(), items));
        Assert.False(MenuMessages.Write(new BitWriter(), MenuEvent.Opened(huge)));
    }

    [Fact]
    public void The_board_numbers_menus_and_reports_only_changes()
    {
        var board = new MenuBoard();
        MenuEvent first = board.Opened("RelicRewardMenu", "RelicRewardMenu", "standard_game", Offers())!;
        MenuEvent second = board.Opened("CustomizableYesNoPrompt", "CustomizableYesNoPrompt", "standard_game", MenuData.Empty)!;
        Assert.Equal(1u, first.Serial);
        Assert.Equal(2u, second.Serial);

        MenuTarget card = MenuTarget.AtPath(new[] { 4, 1 }, "Relic");
        Assert.NotNull(board.Selected(1, card));
        Assert.Null(board.Selected(1, MenuTarget.AtPath(new[] { 4, 1 }, "Relic"))); // where it was
        Assert.Null(board.Selected(99, card)); // unknown menu
        Assert.NotNull(board.ValueSet(1, MenuTarget.AtItem("state", 0), 1));
        Assert.Null(board.ValueSet(1, MenuTarget.AtItem("state", 0), 1));
        Assert.Null(board.Updated(1, Offers())); // same data
        MenuEvent updated = board.Updated(1, MenuData.Empty)!;
        Assert.Equal(1u, updated.State!.Version);
        Assert.NotNull(board.Clicked(1, card, 1, 0));
        Assert.Null(board.Clicked(42, card, 1, 0));

        Assert.NotNull(board.Closed(2));
        Assert.Null(board.Closed(2));
        Assert.Equal(new[] { 1u }, board.Open.Select(s => s.Serial));
        Assert.Equal(3u, board.Opened("Game", "GameMenu", "standard_game", MenuData.Empty)!.Serial); // serials never repeat
    }

    [Fact]
    public void The_board_tracks_at_most_its_limit()
    {
        var board = new MenuBoard();
        for (int i = 0; i < MenuBoard.MaxOpen; i++)
        {
            Assert.NotNull(board.Opened("Game", "GameMenu", string.Empty, MenuData.Empty));
        }

        Assert.Null(board.Opened("Game", "GameMenu", string.Empty, MenuData.Empty));
        board.Clear();
        Assert.Empty(board.Open);
        Assert.NotNull(board.Opened("Game", "GameMenu", string.Empty, MenuData.Empty));
    }

    [Fact]
    public void A_bad_value_throws_and_keeps_nothing_so_later_snapshots_still_work()
    {
        // Regression case: the value was kept before the check threw, and every later snapshot threw too.
        var board = new MenuBoard();
        MenuEvent opened = board.Opened("RelicRewardMenu", "RelicRewardMenu", "world_map", MenuData.Empty)!;
        MenuTarget relic = MenuTarget.AtItem("relic", 1);
        Assert.Throws<ArgumentException>(() => board.ValueSet(opened.Serial, relic, MenuTarget.MaxIndex + 1));
        Assert.Throws<ArgumentException>(() => board.ValueSet(opened.Serial, relic, -1));
        Assert.Throws<ArgumentException>(() => board.ValueSet(opened.Serial, MenuTarget.None, 0));

        Assert.Equal(new[] { MenuEventKind.Synced, MenuEventKind.Opened }, board.Snapshot().Select(e => e.Kind));
        Assert.NotNull(board.ValueSet(opened.Serial, relic, MenuTarget.MaxIndex));
        Assert.Equal(MenuTarget.MaxIndex, board.Snapshot().Single(e => e.Kind == MenuEventKind.ValueSet).Index);
    }

    [Fact]
    public void A_friend_who_joins_late_gets_the_same_menus_from_the_snapshot()
    {
        var board = new MenuBoard();
        var live = new MenuMirror();
        void Send(MenuEvent? menuEvent)
        {
            Assert.NotNull(menuEvent);
            live.Apply(RoundTrip(menuEvent!));
        }

        Send(board.Opened("ChooseMetaMenu", "ChooseMetaMenu", "start_screen", MenuData.Empty));
        Send(board.ValueSet(1, MenuTarget.AtPath(new[] { 5, 0 }, "Difficulty"), 3));
        Send(board.ValueSet(1, MenuTarget.AtItem("state", 0), 1));
        Send(board.Selected(1, MenuTarget.AtPath(new[] { 2, 6 }, "MetaTeamItem(Clone)")));
        Send(board.Opened("CustomizableYesNoPrompt", "CustomizableYesNoPrompt", "start_screen", MenuData.Empty));
        Send(board.Closed(2));

        var late = new MenuMirror();
        foreach (MenuEvent menuEvent in board.Snapshot())
        {
            Assert.NotEqual(MenuMirrorChange.Ignored, late.Apply(RoundTrip(menuEvent)));
        }

        Assert.Equal(live.Open.Count, late.Open.Count);
        MirroredMenu a = live.Open.Single();
        MirroredMenu b = late.Open.Single();
        Assert.Equal(a.State.Id, b.State.Id);
        Assert.Equal(a.Selection, b.Selection);
        Assert.Equal(a.Values.OrderBy(v => v.Key.ToString()), b.Values.OrderBy(v => v.Key.ToString()));
    }

    [Fact]
    public void The_open_list_round_trips_and_bad_ones_are_refused()
    {
        MenuEvent read = RoundTrip(MenuEvent.Synced(new uint[] { 4, 2, 11 }));
        Assert.Equal(MenuEventKind.Synced, read.Kind);
        Assert.Equal(MessageType.MenuOpen, read.Type);
        Assert.Equal(new uint[] { 4, 2, 11 }, read.OpenSerials);
        Assert.Empty(RoundTrip(MenuEvent.Synced(Array.Empty<uint>())).OpenSerials);

        Assert.Throws<ArgumentException>(() => MenuEvent.Synced(new uint[] { 1, 0 }));
        Assert.Throws<ArgumentException>(() => MenuEvent.Synced(new uint[] { 1, 1 }));
        Assert.Throws<ArgumentException>(() => MenuEvent.Synced(Enumerable.Range(1, MenuBoard.MaxOpen + 1).Select(i => (uint)i).ToList()));

        byte[] Crafted(ulong count, params uint[] serials)
        {
            var writer = new BitWriter();
            writer.WriteByte((byte)MenuEventKind.Synced);
            writer.WriteVarUInt(count);
            foreach (uint serial in serials)
            {
                writer.WriteUInt32(serial);
            }

            return writer.ToArray();
        }

        Assert.NotNull(Read(MessageType.MenuOpen, Crafted(2, 7, 8)));
        Assert.Null(Read(MessageType.MenuOpen, Crafted(2, 7, 0))); // serial 0
        Assert.Null(Read(MessageType.MenuOpen, Crafted(2, 7, 7))); // twice
        Assert.Null(Read(MessageType.MenuOpen, Crafted(3, 7, 8))); // fewer than it says
        Assert.Null(Read(MessageType.MenuOpen, Crafted(1, 7, 8))); // more than it says
        Assert.Null(Read(MessageType.MenuOpen, Crafted(MenuBoard.MaxOpen + 1, Enumerable.Range(1, MenuBoard.MaxOpen + 1).Select(i => (uint)i).ToArray())));
    }

    [Fact]
    public void A_friend_who_rejoins_drops_the_menus_that_closed_while_it_was_away()
    {
        var board = new MenuBoard();
        var friend = new MenuMirror();
        friend.Apply(RoundTrip(board.Opened("EventChoice", "EventChoiceMenu", "world_map", MenuData.Empty)!));
        friend.Apply(RoundTrip(board.Opened("CustomizableYesNoPrompt", "CustomizableYesNoPrompt", "world_map", MenuData.Empty)!));

        // Away: the prompt and the event close, a reward opens. None of it reaches the friend.
        Assert.NotNull(board.Closed(2));
        Assert.NotNull(board.Closed(1));
        Assert.NotNull(board.Opened("RelicRewardMenu", "RelicRewardMenu", "world_map", Offers()));

        IReadOnlyList<MenuEvent> snapshot = board.Snapshot();
        Assert.Equal(MenuEventKind.Synced, snapshot[0].Kind);
        Assert.Equal(MenuMirrorChange.Synced, friend.Apply(RoundTrip(snapshot[0])));
        Assert.Empty(friend.Open);
        foreach (MenuEvent menuEvent in snapshot.Skip(1))
        {
            friend.Apply(RoundTrip(menuEvent));
        }

        Assert.Equal(new[] { 3u }, friend.Open.Select(m => m.State.Serial));
    }

    [Fact]
    public void With_nothing_open_the_snapshot_still_closes_what_a_friend_holds()
    {
        var board = new MenuBoard();
        var friend = new MenuMirror();
        friend.Apply(RoundTrip(board.Opened("Game", "GameMenu", "standard_game", MenuData.Empty)!));
        Assert.NotNull(board.Closed(1));

        IReadOnlyList<MenuEvent> snapshot = board.Snapshot();
        Assert.Single(snapshot);
        Assert.Equal(MenuMirrorChange.Synced, friend.Apply(RoundTrip(snapshot[0])));
        Assert.Empty(friend.Open);
    }

    [Fact]
    public void Data_says_what_it_costs_to_send()
    {
        var writer = new BitWriter();
        Offers().Write(writer);
        Assert.Equal(writer.ByteLength, Offers().ByteLength);
        Assert.True(MenuData.Empty.ByteLength > 0);
        Assert.True(Offers().ByteLength > MenuData.Empty.ByteLength);
    }

    [Fact]
    public void The_pacer_lets_a_burst_through_then_holds_until_it_refills()
    {
        var pacer = new MenuPacer();
        const int Update = 12 * 1024;
        Assert.True(pacer.TrySend(Update, 0));
        Assert.True(pacer.TrySend(Update, 0.1));
        Assert.False(pacer.TrySend(Update, 0.2)); // 8 KB + 1.6 KB left: held
        Assert.Equal(1, pacer.Held);
        Assert.True(pacer.TrySend(Update, 0.6)); // refilled past 12 KB
        Assert.False(pacer.TrySend(1024, 0.6));
        Assert.Equal(2, pacer.Held);

        // A small menu changing several times a second is never held.
        var small = new MenuPacer();
        for (int i = 0; i < 200; i++)
        {
            Assert.True(small.TrySend(2 * 1024, i * 0.4));
        }

        Assert.Equal(0, small.Held);
    }

    [Fact]
    public void Updates_that_must_go_out_are_charged_and_delay_the_rest()
    {
        var pacer = new MenuPacer();
        pacer.Charge(MenuPacer.BurstBytes + (8 * 1024), 0); // 8 KB below empty
        Assert.False(pacer.TrySend(1024, 0.5)); // 4 KB still owed
        Assert.True(pacer.TrySend(1024, 1.2)); // 1.6 KB after it refilled

        // With a full budget an update bigger than the burst still goes; time going back refills nothing.
        var full = new MenuPacer();
        Assert.True(full.TrySend(MenuMessages.MaxMessageBytes, 10));
        Assert.False(full.TrySend(1024, 5));
        Assert.False(full.TrySend(1024, 10));
    }

    [Fact]
    public void The_mirror_ignores_unknown_menus_and_old_versions()
    {
        var mirror = new MenuMirror();
        var state = new MenuState(4, "RelicRewardMenu", "RelicRewardMenu", "standard_game", 2, Offers());
        Assert.Equal(MenuMirrorChange.Ignored, mirror.Apply(MenuEvent.Updated(state)));
        Assert.Equal(MenuMirrorChange.Ignored, mirror.Apply(MenuEvent.Selected(4, MenuTarget.AtItem("relic", 0))));
        Assert.Equal(MenuMirrorChange.Ignored, mirror.Apply(MenuEvent.Closed(4)));

        Assert.Equal(MenuMirrorChange.Opened, mirror.Apply(MenuEvent.Opened(state)));
        Assert.Equal(MenuMirrorChange.Ignored, mirror.Apply(MenuEvent.Updated(new MenuState(4, "RelicRewardMenu", "RelicRewardMenu", "standard_game", 1, MenuData.Empty))));
        Assert.Equal(MenuMirrorChange.Updated, mirror.Apply(MenuEvent.Updated(state.With(MenuData.Empty))));
        Assert.True(mirror.TryGet(4, out MirroredMenu held));
        Assert.Equal(3u, held.State.Version);
        Assert.Equal(MenuMirrorChange.Refreshed, mirror.Apply(MenuEvent.Opened(state)));
        Assert.Equal(MenuMirrorChange.Selected, mirror.Apply(MenuEvent.Selected(4, MenuTarget.AtItem("relic", 1))));
        Assert.Equal(MenuMirrorChange.Clicked, mirror.Apply(MenuEvent.Clicked(4, MenuTarget.AtItem("relic", 1), 1, 0)));
        Assert.Equal(MenuMirrorChange.Closed, mirror.Apply(MenuEvent.Closed(4)));
        Assert.Empty(mirror.Open);
        Assert.False(mirror.TryGet(4, out _));
    }

    [Fact]
    public void The_mirror_holds_at_most_the_boards_limit()
    {
        var mirror = new MenuMirror();
        for (uint serial = 1; serial <= MenuBoard.MaxOpen; serial++)
        {
            Assert.Equal(MenuMirrorChange.Opened, mirror.Apply(MenuEvent.Opened(new MenuState(serial, "Game", "GameMenu", string.Empty, 0, MenuData.Empty))));
        }

        Assert.Equal(MenuMirrorChange.Ignored, mirror.Apply(MenuEvent.Opened(new MenuState(100, "Game", "GameMenu", string.Empty, 0, MenuData.Empty))));
        for (int i = 0; i <= MenuBoard.MaxValuesPerMenu; i++)
        {
            MenuMirrorChange change = mirror.Apply(MenuEvent.ValueSet(1, MenuTarget.AtItem("v" + i, 0), 1));
            Assert.Equal(i < MenuBoard.MaxValuesPerMenu ? MenuMirrorChange.ValueSet : MenuMirrorChange.Ignored, change);
        }
    }

    [Fact]
    public void Targets_compare_by_value()
    {
        Assert.Equal(MenuTarget.AtPath(new[] { 1, 2 }, "a"), MenuTarget.AtPath(new List<int> { 1, 2 }, "a"));
        Assert.NotEqual(MenuTarget.AtPath(new[] { 1, 2 }, "a"), MenuTarget.AtPath(new[] { 1, 3 }, "a"));
        Assert.NotEqual(MenuTarget.AtPath(new[] { 1 }, "a"), MenuTarget.AtPath(new[] { 1 }, "b"));
        Assert.NotEqual(MenuTarget.AtItem("relic", 1), MenuTarget.AtItem("skater", 1));
        Assert.Equal(MenuTarget.AtItem("relic", 1).GetHashCode(), MenuTarget.AtItem("relic", 1).GetHashCode());
        Assert.Equal("1/2 (a)", MenuTarget.AtPath(new[] { 1, 2 }, "a").ToString());
        Assert.Equal("relic#1", MenuTarget.AtItem("relic", 1).ToString());
    }
}
