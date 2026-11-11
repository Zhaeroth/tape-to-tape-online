using System;
using System.Collections.Generic;
using System.Linq;
using T2TOnline.Core.Campaign;
using T2TOnline.Core.Protocol;
using T2TOnline.Core.Serialization;

namespace T2TOnline.Core.Tests.Campaign;

public class GameMenuModelTests
{
    private static GameMenuSide Side(int forwards, int[] line, params GameMenuSkater[] skaters) => new(
        new[] { 12, 30, 21, 9, 4, 3, 11 },
        83.4567f,
        Enumerable.Range(0, forwards).Select(i => GameMenuModel.IdPrint($"fwd-{i}")).ToArray(),
        line,
        skaters);

    private static GameMenuSkater Skater(int index, int goals) => new(index, new[] { goals, 1, 5, 12, 3, 2, 1 });

    private static GameMenuModel Full(int events = 3, int assists = 1)
    {
        var timeline = new List<GameMenuEvent>
        {
            GameMenuEvent.Goal(1, 59.97f, new GameMenuRef(true, 2), 1, new[] { new GameMenuRef(true, 0), new GameMenuRef(true, 4) }, new[] { 1, 2 }, 1, 0),
            GameMenuEvent.Injury(2, 301.5f, new GameMenuRef(false, 7), 3, 2),
            GameMenuEvent.Goal(3, 0.25f, new GameMenuRef(false, 1), 4, Array.Empty<GameMenuRef>(), Array.Empty<int>(), 1, 1),
        };
        while (timeline.Count < events)
        {
            bool home = timeline.Count % 2 == 0;
            GameMenuRef[] helpers = Enumerable.Range(0, assists).Select(i => new GameMenuRef(home, 5 + i)).ToArray();
            timeline.Add(GameMenuEvent.Goal(3, 900.125f + timeline.Count, new GameMenuRef(home, 3), 2, helpers, helpers.Select(_ => 3).ToArray(), 9, 9));
        }

        return new GameMenuModel(
            tabs: 1 | 2 | 8,
            tab: 2,
            buttons: 1 | 2 | 4 | 128,
            tycoon: false,
            home: Side(10, new[] { 0, 1, 2, 3, 4 }, Skater(0, 0), Skater(2, 3), Skater(9, 1), new GameMenuSkater(4, new int[7])),
            visitor: Side(12, new[] { 5, 6, 7, 8, 9 }, Skater(1, 2), Skater(11, 99_999)),
            events: timeline,
            injuries: "1a2b:3:1,ffff:0:2",
            check: 0xDEADBEEF);
    }

    /// <summary>
    /// A model whose timeline names goalies: the home goalie scores (a forward assisting), the visitors' goalie assists,
    /// the home goalie is hurt. The home goalie is a campaign run's (two prints), the visitors' an asset (one).
    /// </summary>
    private static GameMenuModel WithGoalies(bool visitorGoalie = true)
    {
        GameMenuSide Goalied(GameMenuSide side, IReadOnlyList<ushort> goalie) => new(side.Counts, side.TimeOnAttack, side.Ids, side.Line, side.Skaters, goalie);
        var timeline = new List<GameMenuEvent>
        {
            GameMenuEvent.Goal(1, 12.5f, GameMenuRef.GoalieOf(true), 1, new[] { new GameMenuRef(true, 3) }, new[] { 2 }, 1, 0),
            GameMenuEvent.Goal(2, 300f, new GameMenuRef(false, 0), 1, new[] { GameMenuRef.GoalieOf(false), new GameMenuRef(false, 2) }, new[] { 1, 4 }, 1, 1),
            GameMenuEvent.Injury(3, 400.75f, GameMenuRef.GoalieOf(true), 2, 3),
        };
        return new GameMenuModel(
            tabs: 2 | 8,
            tab: 8,
            buttons: 0,
            tycoon: true,
            home: Goalied(Side(10, new[] { 0, 1, 2, 3, 4 }, Skater(3, 0)), GameMenuModel.GoaliePrints("goalie-asset", "goalie-run-7")),
            visitor: Goalied(Side(12, new[] { 5, 6, 7, 8, 9 }, Skater(0, 1)), visitorGoalie ? GameMenuModel.GoaliePrints("goalie-other", "goalie-other") : Array.Empty<ushort>()),
            events: timeline,
            injuries: string.Empty,
            check: 7);
    }

    private static List<KeyValuePair<string, NetValue>> Written(GameMenuModel model)
    {
        var values = new List<KeyValuePair<string, NetValue>> { new("context", NetValue.FromString("EndOfGame")) };
        Assert.True(model.TryWrite(values, out string problem), problem);
        return values;
    }

    /// <summary>The values as a friend gets them: through a real menu message, written and read.</summary>
    private static MenuData OverTheWire(IReadOnlyList<KeyValuePair<string, NetValue>> values)
    {
        var state = new MenuState(5, "Game", "GameMenu", "standard_game", 0, new MenuData(values, Array.Empty<MenuItem>()));
        var writer = new BitWriter();
        Assert.True(MenuMessages.Write(writer, MenuEvent.Opened(state)));
        var reader = new BitReader(writer.ToArray());
        Assert.True(MenuMessages.TryRead(MessageType.MenuOpen, ref reader, out MenuEvent? read));
        return read!.State!.Data;
    }

    private static MenuData With(IReadOnlyList<KeyValuePair<string, NetValue>> values, string key, NetValue? value)
    {
        var changed = values.Where(v => v.Key != key).ToList();
        if (value is { } set)
        {
            changed.Add(new(key, set));
        }

        return new MenuData(changed, Array.Empty<MenuItem>());
    }

    private static bool Reads(MenuData data) => GameMenuModel.TryRead(data, out _, out _);

    [Fact]
    public void A_full_model_round_trips_through_a_menu_message()
    {
        GameMenuModel model = Full();
        MenuData data = OverTheWire(Written(model));

        Assert.True(GameMenuModel.TryRead(data, out GameMenuModel? read, out string problem), problem);
        Assert.Equal("EndOfGame", data.Text("context")); // the menu's own values stay as they were
        Assert.Equal((11, 2, 135, false), (read!.Tabs, read.Tab, read.Buttons, read.Tycoon));
        Assert.Equal(model.Home.Counts, read.Home.Counts);
        Assert.Equal(83.4567f, read.Home.TimeOnAttack); // exactly: the friend's menu rounds it as the host's does
        Assert.Equal(model.Visitor.Ids, read.Visitor.Ids);
        Assert.Equal(new[] { 5, 6, 7, 8, 9 }, read.Visitor.Line);

        // Forwards with nothing to show aren't sent (a friend writes 0 for them).
        Assert.Equal(new[] { 0, 2, 9 }, read.Home.Skaters.Select(s => s.Index));
        Assert.Equal(model.Home.Skaters[1].Counts, read.Home.Skaters[1].Counts);
        Assert.Equal(99_999, read.Visitor.Skaters.Single(s => s.Index == 11).Counts[0]);

        Assert.Equal(3, read.Events.Count);
        GameMenuEvent goal = read.Events[0];
        Assert.Equal((GameMenuEventKind.Goal, 1, 59.97f, new GameMenuRef(true, 2), 1), (goal.Kind, goal.Period, goal.Time, goal.Who, goal.ScorerGoals));
        Assert.Equal(new[] { new GameMenuRef(true, 0), new GameMenuRef(true, 4) }, goal.Assists);
        Assert.Equal(new[] { 1, 2 }, goal.AssistTotals);
        Assert.Equal((1, 0), (goal.HomeScore, goal.VisitorScore));
        GameMenuEvent injury = read.Events[1];
        Assert.Equal((GameMenuEventKind.Injury, 2, 301.5f, new GameMenuRef(false, 7), 3, 2), (injury.Kind, injury.Period, injury.Time, injury.Who, injury.InjuryType, injury.Duration));
        Assert.Empty(read.Events[2].Assists);
        Assert.Equal("1a2b:3:1,ffff:0:2", read.Injuries);
        Assert.Equal(0xDEADBEEFu, read.Check);
    }

    [Fact]
    public void Long_lists_go_on_in_numbered_values_and_every_value_fits()
    {
        GameMenuModel model = Full(events: 30);
        List<KeyValuePair<string, NetValue>> values = Written(model);

        Assert.Contains(values, v => v.Key == "gm.tl.1");
        Assert.All(values, v => Assert.True(v.Value.Kind != NetValueKind.Text || v.Value.Text!.Length <= NetValue.MaxStringBytes));
        Assert.True(values.Count <= MenuData.MaxValues);
        Assert.True(GameMenuModel.TryRead(OverTheWire(values), out GameMenuModel? read, out string problem), problem);
        Assert.Equal(30, read!.Events.Count);
        Assert.Equal(model.Events.Select(e => e.Time), read.Events.Select(e => e.Time));
    }

    [Fact]
    public void A_model_that_would_not_fit_is_not_written()
    {
        var values = new List<KeyValuePair<string, NetValue>>();
        Assert.False(Full(events: GameMenuModel.MaxEvents, assists: GameMenuModel.MaxAssists).TryWrite(values, out string problem));
        Assert.Contains("don't fit", problem, StringComparison.Ordinal);
        Assert.Empty(values); // nothing half-written

        // The most events, each short, still fit.
        Assert.True(Full(events: GameMenuModel.MaxEvents, assists: 0).TryWrite(values, out problem), problem);
        Assert.True(values.Count <= GameMenuModel.MaxValues);
    }

    [Fact]
    public void Numbers_out_of_range_are_not_written()
    {
        GameMenuModel Bad(GameMenuSide home, IReadOnlyList<GameMenuEvent>? events = null, int tab = 2, string injuries = "") =>
            new(1, tab, 1, false, home, Side(3, new[] { 0 }), events ?? Array.Empty<GameMenuEvent>(), injuries, 0);

        var cases = new[]
        {
            Bad(new GameMenuSide(new[] { -1, 0, 0, 0, 0, 0, 0 }, 0, new ushort[3], Array.Empty<int>(), Array.Empty<GameMenuSkater>())),
            Bad(new GameMenuSide(new int[7], float.NaN, new ushort[3], Array.Empty<int>(), Array.Empty<GameMenuSkater>())),
            Bad(new GameMenuSide(new int[7], 0, new ushort[3], new[] { 3 }, Array.Empty<GameMenuSkater>())), // a line index past the forwards
            Bad(new GameMenuSide(new int[7], 0, new ushort[3], new[] { 1, 1 }, Array.Empty<GameMenuSkater>())), // the same forward twice
            Bad(new GameMenuSide(new int[7], 0, new ushort[3], Array.Empty<int>(), new[] { Skater(5, 1) })),
            Bad(Side(3, new[] { 0 }), new[] { GameMenuEvent.Injury(1, 1, new GameMenuRef(true, 3), 0, 1) }),
            Bad(Side(3, new[] { 0 }), new[] { GameMenuEvent.Injury(1, 1, new GameMenuRef(true, 0), GameMenuModel.MaxInjuryType + 1, 1) }),
            Bad(Side(3, new[] { 0 }), tab: 3),
            Bad(Side(3, new[] { 0 }), injuries: "line\nbreak"),
        };

        foreach (GameMenuModel model in cases)
        {
            Assert.False(model.TryWrite(new List<KeyValuePair<string, NetValue>>(), out string problem));
            Assert.NotEqual(string.Empty, problem);
        }
    }

    [Fact]
    public void A_menu_without_numbers_or_of_another_version_is_not_read()
    {
        Assert.False(GameMenuModel.TryRead(MenuData.Empty, out GameMenuModel? none, out string problem));
        Assert.Null(none);
        Assert.Contains("without its numbers", problem, StringComparison.Ordinal);

        List<KeyValuePair<string, NetValue>> values = Written(Full());
        Assert.Contains(values, v => v.Key == GameMenuModel.VersionKey && v.Value.WholeValue == 3);
        Assert.False(GameMenuModel.TryRead(With(values, GameMenuModel.VersionKey, NetValue.FromInt(4)), out _, out problem));
        Assert.Contains("format 4", problem, StringComparison.Ordinal);
        Assert.False(Reads(With(values, GameMenuModel.VersionKey, NetValue.FromInt(0))));
        Assert.False(Reads(With(values, GameMenuModel.VersionKey, NetValue.FromString("2"))));
    }

    [Fact]
    public void An_older_hosts_format_1_is_still_read()
    {
        // Format 1 named forwards only: the same values without goalies.
        GameMenuModel model = Full();
        Assert.True(GameMenuModel.TryRead(OverTheWire(With(Written(model), GameMenuModel.VersionKey, NetValue.FromInt(1)).Values), out GameMenuModel? read, out string problem), problem);
        Assert.Equal(model.Events.Count, read!.Events.Count);
        Assert.Empty(read.Home.Goalie);
        Assert.Empty(read.Visitor.Goalie);
    }

    [Fact]
    public void Goalies_round_trip_as_their_sides_goalie()
    {
        GameMenuModel model = WithGoalies();
        List<KeyValuePair<string, NetValue>> values = Written(model);
        Assert.Equal(GameMenuModel.MaxGoaliePrints, values.Single(v => v.Key == "gm.gk.h").Value.Text!.Split(',').Length);
        Assert.Single(values.Single(v => v.Key == "gm.gk.v").Value.Text!.Split(','));
        Assert.StartsWith("G,1,12.5,hg,1,h3,2,", values.Single(v => v.Key == "gm.tl").Value.Text, StringComparison.Ordinal);

        Assert.True(GameMenuModel.TryRead(OverTheWire(values), out GameMenuModel? read, out string problem), problem);
        Assert.Equal(model.Home.Goalie, read!.Home.Goalie);
        Assert.Equal(model.Visitor.Goalie, read.Visitor.Goalie);
        GameMenuEvent goal = read.Events[0];
        Assert.Equal(GameMenuRef.GoalieOf(true), goal.Who);
        Assert.True(goal.Who.IsGoalie);
        Assert.Equal(new[] { new GameMenuRef(true, 3) }, goal.Assists);
        GameMenuEvent assisted = read.Events[1];
        Assert.Equal(new[] { GameMenuRef.GoalieOf(false), new GameMenuRef(false, 2) }, assisted.Assists);
        Assert.Equal(new[] { 1, 4 }, assisted.AssistTotals);
        GameMenuEvent hurt = read.Events[2];
        Assert.Equal((GameMenuEventKind.Injury, GameMenuRef.GoalieOf(true), 2, 3), (hurt.Kind, hurt.Who, hurt.InjuryType, hurt.Duration));
        Assert.Equal(("hg", "vg", "v12"), (GameMenuRef.GoalieOf(true).ToString(), GameMenuRef.GoalieOf(false).ToString(), new GameMenuRef(false, 12).ToString()));
    }

    [Fact]
    public void A_goalie_is_named_only_on_a_side_that_has_one()
    {
        // The visitors' goalie isn't known: the host can't write a timeline naming it...
        Assert.False(WithGoalies(visitorGoalie: false).TryWrite(new List<KeyValuePair<string, NetValue>>(), out string problem));
        Assert.Contains("timeline event", problem, StringComparison.Ordinal);

        // ...and a friend refuses one that names it anyway (or a goalie token in format 1, which had none).
        List<KeyValuePair<string, NetValue>> values = Written(WithGoalies());
        Assert.False(GameMenuModel.TryRead(With(values, "gm.gk.v", null), out _, out problem));
        Assert.Contains("don't add up", problem, StringComparison.Ordinal);
        Assert.False(Reads(With(With(values, "gm.gk.h", null).Values, "gm.gk.v", null)));
    }

    [Fact]
    public void A_friends_goalie_is_the_hosts_when_a_print_matches()
    {
        Assert.Single(GameMenuModel.GoaliePrints("goalie-asset", "goalie-asset"));
        Assert.Single(GameMenuModel.GoaliePrints("goalie-asset", null));
        Assert.Single(GameMenuModel.GoaliePrints("goalie-asset", string.Empty));
        Assert.Equal(new[] { GameMenuModel.IdPrint("goalie-asset"), GameMenuModel.IdPrint("run-7") }, GameMenuModel.GoaliePrints("goalie-asset", "run-7"));

        // A campaign run's goalie: the host's has its asset id and the run's id; the friend's, loaded from the run's file,
        // has only the run's id (OriginalId falls back to it).
        IReadOnlyList<ushort> host = GameMenuModel.GoaliePrints("goalie-asset", "run-7");
        Assert.True(GameMenuModel.SameGoalie(host, GameMenuModel.GoaliePrints("run-7", "run-7")));

        // An exhibition's or an opponent's goalie: the friend's is the asset itself, found by the host's asset id.
        Assert.True(GameMenuModel.SameGoalie(GameMenuModel.GoaliePrints("goalie-asset", "copy-3"), GameMenuModel.GoaliePrints("goalie-asset", "goalie-asset")));

        Assert.False(GameMenuModel.SameGoalie(host, GameMenuModel.GoaliePrints("goalie-other", "run-8")));
        Assert.False(GameMenuModel.SameGoalie(Array.Empty<ushort>(), GameMenuModel.GoaliePrints("goalie-asset", null)));
        Assert.False(GameMenuModel.SameGoalie(host, Array.Empty<ushort>()));
        Assert.Throws<ArgumentException>(() => new GameMenuSide(new int[7], 0, new ushort[3], Array.Empty<int>(), Array.Empty<GameMenuSkater>(), new ushort[3]));
    }

    [Fact]
    public void Malformed_goalie_values_are_refused()
    {
        List<KeyValuePair<string, NetValue>> values = Written(WithGoalies());
        Assert.True(Reads(new MenuData(values, Array.Empty<MenuItem>())));
        var bad = new (string Key, NetValue? Value)[]
        {
            ("gm.gk.h", NetValue.FromString("1,2,3")), // over the most prints
            ("gm.gk.h", NetValue.FromString("1,")),
            ("gm.gk.h", NetValue.FromString("zz")),
            ("gm.gk.h", NetValue.FromString("12345")),
            ("gm.gk.h", NetValue.FromString("-1")),
            ("gm.tl", NetValue.FromString("G,1,1,hgx,1,,,1,0")),
            ("gm.tl", NetValue.FromString("G,1,1,g,1,,,1,0")),
            ("gm.tl", NetValue.FromString("G,1,1,hg0,1,,,1,0")),
            ("gm.tl", NetValue.FromString("G,1,1,HG,1,,,1,0")),
            ("gm.tl", NetValue.FromString("G,1,1,h2,1,xg,1,1,0")),
            ("gm.tl", NetValue.FromString("I,1,1,gv,1,1")),
        };

        foreach ((string key, NetValue? value) in bad)
        {
            Assert.False(GameMenuModel.TryRead(With(values, key, value), out GameMenuModel? model, out string problem), $"{key} = {value}");
            Assert.Null(model);
            Assert.NotEqual(string.Empty, problem);
        }

        // A timeline naming goalies on both sides reads (both sides have one).
        Assert.True(Reads(With(values, "gm.tl", NetValue.FromString("G,1,1,vg,1,h0+hg,1+2,0,1"))));
    }

    [Fact]
    public void Malformed_or_out_of_range_values_are_refused()
    {
        List<KeyValuePair<string, NetValue>> values = Written(Full());
        Assert.True(Reads(new MenuData(values, Array.Empty<MenuItem>())));

        var bad = new (string Key, NetValue? Value)[]
        {
            (GameMenuModel.TabsKey, NetValue.FromInt(16)),
            (GameMenuModel.TabsKey, null),
            (GameMenuModel.TabKey, NetValue.FromInt(3)),
            (GameMenuModel.ButtonsKey, NetValue.FromInt(-1)),
            (GameMenuModel.ButtonsKey, NetValue.FromInt(256)),
            (GameMenuModel.TycoonKey, NetValue.FromInt(1)),
            (GameMenuModel.TycoonKey, null),
            ("gm.team.h", NetValue.FromString("1,2,3,4,5,6,7")), // no time on attack
            ("gm.team.h", NetValue.FromString("1,2,3,4,5,6,7,8,9")),
            ("gm.team.h", NetValue.FromString("1,2,3,4,5,6,-7,8")),
            ("gm.team.h", NetValue.FromString("1,2,3,4,5,6,07,8")),
            ("gm.team.h", NetValue.FromString("1,2,3,4,5,6,100000,8")),
            ("gm.team.h", NetValue.FromString("1,2,3,4,5,6,7,NaN")),
            ("gm.team.h", NetValue.FromString("1,2,3,4,5,6,7,Infinity")),
            ("gm.team.h", NetValue.FromString("1,2,3,4,5,6,7,1E9")),
            ("gm.team.h", NetValue.FromString("1,2,3,4,5,6,7,-1")),
            ("gm.team.h", NetValue.FromString("1, 2,3,4,5,6,7,8")),
            ("gm.team.v", null),
            ("gm.ids.h", NetValue.FromString("12345")),
            ("gm.ids.h", NetValue.FromString("zz")),
            ("gm.ids.h", NetValue.FromString("1,,2")),
            ("gm.ids.h", NetValue.FromString(string.Join(",", Enumerable.Repeat("1", GameMenuModel.MaxForwards + 1)))),
            ("gm.line.h", NetValue.FromString("0,1,1")),
            ("gm.line.h", NetValue.FromString("0,10")), // past the side's 10 forwards
            ("gm.line.h", NetValue.FromString("0,1,2,3,4,5,6,7,8")),
            ("gm.sk.h", NetValue.FromString("0:1,2,3,4,5,6")),
            ("gm.sk.h", NetValue.FromString("10:1,2,3,4,5,6,7")),
            ("gm.sk.h", NetValue.FromString("0:1,2,3,4,5,6,7;0:1,2,3,4,5,6,7")),
            ("gm.sk.h", NetValue.FromString("0:1,2,3,4,5,6,7;")),
            ("gm.sk.h", NetValue.FromString(":1,2,3,4,5,6,7")),
            ("gm.sk.h.1", NetValue.FromString("1:1,2,3,4,5,6,7")), // a list's later value without its first: a gap
            ("gm.tl", NetValue.FromString("G,1,1,h2,1,h0,,1,0")), // an assist without its total
            ("gm.tl", NetValue.FromString("G,1,1,h2,1,h0+h1+h2+h3+h4,1+1+1+1+1,1,0")),
            ("gm.tl", NetValue.FromString("G,1,1,h10,1,,,1,0")), // a scorer past the side's forwards
            ("gm.tl", NetValue.FromString("G,1,1,x2,1,,,1,0")),
            ("gm.tl", NetValue.FromString("G,100,1,h2,1,,,1,0")),
            ("gm.tl", NetValue.FromString("I,1,1,v2,64,1")),
            ("gm.tl", NetValue.FromString("I,1,1,v2,1,1000")),
            ("gm.tl", NetValue.FromString("I,1,1,v2,1")),
            ("gm.tl", NetValue.FromString("X,1,1,v2,1,1")),
            ("gm.tl", NetValue.FromInt(4)),
            ("gm.tl.3", NetValue.FromString("I,1,1,v2,1,1")),
            (GameMenuModel.CheckKey, NetValue.FromInt(-1)),
            (GameMenuModel.CheckKey, NetValue.FromInt(1L + uint.MaxValue)),
            (GameMenuModel.CheckKey, null),
        };

        foreach ((string key, NetValue? value) in bad)
        {
            List<KeyValuePair<string, NetValue>> start = key == "gm.sk.h.1" ? values.Where(v => v.Key != "gm.sk.h").ToList() : values;
            bool read = GameMenuModel.TryRead(With(start, key, value), out GameMenuModel? model, out string problem);
            Assert.False(read, $"{key} = {value}");
            Assert.Null(model);
            Assert.NotEqual(string.Empty, problem);
        }
    }

    [Fact]
    public void Random_values_never_throw()
    {
        List<KeyValuePair<string, NetValue>>[] models = { Written(Full(events: 12)), Written(WithGoalies()) };
        var random = new Random(4242);
        const string Alphabet = "0123456789,;:+-.EehvgGIxaf ";
        for (int round = 0; round < 4000; round++)
        {
            List<KeyValuePair<string, NetValue>> values = models[round % 2];
            KeyValuePair<string, NetValue> target = values[random.Next(values.Count)];
            string text = target.Value.Kind == NetValueKind.Text ? target.Value.Text! : string.Empty;
            char[] chars = text.ToCharArray();
            for (int i = 0; i < chars.Length && i < 4; i++)
            {
                chars[random.Next(chars.Length)] = Alphabet[random.Next(Alphabet.Length)];
            }

            NetValue mutated = random.Next(4) == 0 ? NetValue.FromInt(random.Next(-5, 300)) : NetValue.FromString(new string(chars));
            _ = GameMenuModel.TryRead(With(values, target.Key, mutated), out _, out _);
        }
    }

    /// <summary>The host's injury list: a new injury, a healed one, a shorter one, and a row whose icon the host couldn't name.</summary>
    public static GameMenuInjuryRow[] InjuryRows() => new[]
    {
        new GameMenuInjuryRow(GameMenuModel.IdPrint("fwd-2"), true, GameMenuInjuryIcon.Injury, 3, true, -1, 4),
        new GameMenuInjuryRow(GameMenuModel.IdPrint("fwd-7"), false, GameMenuInjuryIcon.Recovery, 0, true, 2, 0),
        new GameMenuInjuryRow(0xFFFF, true, GameMenuInjuryIcon.Injury, 1, false, 999, 3),
        new GameMenuInjuryRow(0, false, GameMenuInjuryIcon.None, 0, false, -99, -99),
    };

    /// <summary><see cref="Full"/> with the injury list's rows (format 3).</summary>
    public static GameMenuModel WithInjuries(IReadOnlyList<GameMenuInjuryRow>? rows = null, bool shown = true, int events = 3)
    {
        GameMenuModel m = Full(events);
        return new GameMenuModel(m.Tabs, m.Tab, m.Buttons, m.Tycoon, m.Home, m.Visitor, m.Events, m.Injuries, m.Check, rows ?? InjuryRows(), shown);
    }

    [Fact]
    public void Injury_rows_round_trip_with_the_list_shown_or_not()
    {
        List<KeyValuePair<string, NetValue>> values = Written(WithInjuries());
        string expected = string.Join(";", $"{GameMenuModel.IdPrint("fwd-2"):x},h,i,3,1,-1,4", $"{GameMenuModel.IdPrint("fwd-7"):x},v,r,0,1,2,0", "ffff,h,i,1,0,999,3", "0,v,-,0,0,-99,-99");
        Assert.Equal(expected, values.Single(v => v.Key == GameMenuModel.InjuryRowsKey).Value.Text);
        Assert.Equal((NetValueKind.Flag, 1L), (values.Single(v => v.Key == GameMenuModel.InjuryShownKey).Value.Kind, values.Single(v => v.Key == GameMenuModel.InjuryShownKey).Value.WholeValue));
        Assert.True(GameMenuModel.TryRead(OverTheWire(values), out GameMenuModel? read, out string problem), problem);
        Assert.Equal(InjuryRows(), read!.InjuryRows);
        Assert.True(read.InjuryListShown);

        // Hidden, and an empty list: both say so.
        Assert.True(GameMenuModel.TryRead(OverTheWire(Written(WithInjuries(shown: false))), out read, out problem), problem);
        Assert.False(read!.InjuryListShown);
        Assert.Equal(4, read.InjuryRows!.Count);
        List<KeyValuePair<string, NetValue>> empty = Written(WithInjuries(Array.Empty<GameMenuInjuryRow>()));
        Assert.DoesNotContain(empty, v => v.Key == GameMenuModel.InjuryRowsKey);
        Assert.True(GameMenuModel.TryRead(OverTheWire(empty), out read, out problem), problem);
        Assert.Empty(read!.InjuryRows!);
        Assert.True(read.InjuryListShown);

        // Not sent: none read (a friend compares digests then).
        Assert.True(GameMenuModel.TryRead(OverTheWire(Written(Full())), out read, out problem), problem);
        Assert.Null(read!.InjuryRows);
        Assert.False(read.InjuryListShown);
        Assert.Null(new GameMenuModel(1, 1, 1, false, Full().Home, Full().Visitor, Array.Empty<GameMenuEvent>(), string.Empty, 0, null, injuryListShown: true).InjuryRows);
    }

    [Fact]
    public void The_most_injury_rows_fit_in_numbered_values()
    {
        GameMenuInjuryRow[] most = Enumerable.Range(0, GameMenuModel.MaxInjuryRows)
            .Select(i => new GameMenuInjuryRow((ushort)(0xF000 + i), i % 2 == 0, GameMenuInjuryIcon.Recovery, GameMenuModel.MaxInjuryType, true, -99, GameMenuModel.MaxHealing))
            .ToArray();
        List<KeyValuePair<string, NetValue>> values = Written(WithInjuries(most));
        Assert.Contains(values, v => v.Key == GameMenuModel.InjuryRowsKey + ".1");
        Assert.True(values.Count <= MenuData.MaxValues);
        Assert.True(GameMenuModel.TryRead(OverTheWire(values), out GameMenuModel? read, out string problem), problem);
        Assert.Equal(most, read!.InjuryRows);
    }

    [Fact]
    public void Injury_rows_that_dont_fit_are_left_out_and_the_rest_goes()
    {
        // Sides full of busy forwards and a timeline that grows: some size leaves no room for the rows, and the menu goes
        // without them (and says so) rather than as text.
        GameMenuSide Busy(bool home) => new(new int[7], 1, Enumerable.Range(0, GameMenuModel.MaxForwards).Select(i => (ushort)(i + (home ? 0 : 100))).ToArray(), new[] { 0 },
            Enumerable.Range(0, GameMenuModel.MaxForwards).Select(i => new GameMenuSkater(i, Enumerable.Repeat(GameMenuModel.MaxCount, 7).ToArray())).ToArray(),
            new ushort[] { 1, 2 });
        bool found = false;
        for (int events = 0; events <= GameMenuModel.MaxEvents && !found; events++)
        {
            GameMenuEvent[] timeline = Enumerable.Range(0, events).Select(i => GameMenuEvent.Injury(1, i, new GameMenuRef(i % 2 == 0, i % 40), 1, 1)).ToArray();
            var model = new GameMenuModel(1, 1, 1, false, Busy(true), Busy(false), timeline, "0", 0, InjuryRows(), true);
            var values = new List<KeyValuePair<string, NetValue>>();
            if (!model.TryWrite(values, out _, out bool left) || !left)
            {
                continue;
            }

            found = true;
            Assert.True(values.Count <= GameMenuModel.MaxValues);
            Assert.DoesNotContain(values, v => v.Key.StartsWith(GameMenuModel.InjuryShownKey, StringComparison.Ordinal) || v.Key.StartsWith(GameMenuModel.InjuryRowsKey, StringComparison.Ordinal));
            Assert.True(GameMenuModel.TryRead(OverTheWire(values.Prepend(new("context", NetValue.FromString("EndOfGame"))).Take(MenuData.MaxValues).ToList()), out GameMenuModel? read, out string problem), problem);
            Assert.Null(read!.InjuryRows);
        }

        Assert.True(found, "some timeline should leave the rows out");

        // A menu with room keeps them; one without rows has none to leave out.
        Assert.True(WithInjuries().TryWrite(new List<KeyValuePair<string, NetValue>>(), out _, out bool kept));
        Assert.False(kept);
        Assert.True(Full().TryWrite(new List<KeyValuePair<string, NetValue>>(), out _, out bool none));
        Assert.False(none);
    }

    [Fact]
    public void Injury_rows_out_of_range_are_not_written()
    {
        GameMenuInjuryRow good = InjuryRows()[0];
        var bad = new[]
        {
            good with { Type = GameMenuModel.MaxInjuryType + 1 },
            good with { Type = -1 },
            good with { Previous = GameMenuModel.MinHealing - 1 },
            good with { New = GameMenuModel.MaxHealing + 1 },
            good with { Icon = (GameMenuInjuryIcon)7 },
            good with { Icon = GameMenuInjuryIcon.None }, // no icon names no type
            good with { Icon = GameMenuInjuryIcon.None, Type = 0, Hd = true },
        };
        foreach (GameMenuInjuryRow row in bad)
        {
            Assert.False(WithInjuries(new[] { row }).TryWrite(new List<KeyValuePair<string, NetValue>>(), out string problem), row.ToString());
            Assert.Contains("injury list row", problem, StringComparison.Ordinal);
        }

        GameMenuInjuryRow[] tooMany = Enumerable.Repeat(good, GameMenuModel.MaxInjuryRows + 1).ToArray();
        Assert.False(WithInjuries(tooMany).TryWrite(new List<KeyValuePair<string, NetValue>>(), out string many));
        Assert.Contains("over 16 rows", many, StringComparison.Ordinal);
    }

    [Fact]
    public void Malformed_injury_rows_are_refused()
    {
        List<KeyValuePair<string, NetValue>> values = Written(WithInjuries());
        Assert.True(Reads(new MenuData(values, Array.Empty<MenuItem>())));
        string row = "1a29,h,i,3,1,-1,4";
        var bad = new (string Key, NetValue? Value)[]
        {
            (GameMenuModel.InjuryShownKey, NetValue.FromInt(1)),
            (GameMenuModel.InjuryRowsKey, NetValue.FromInt(1)),
            (GameMenuModel.InjuryRowsKey, NetValue.FromString(row + ";")),
            (GameMenuModel.InjuryRowsKey, NetValue.FromString("1a29,h,i,3,1,-1")),
            (GameMenuModel.InjuryRowsKey, NetValue.FromString("1a29,h,i,3,1,-1,4,5")),
            (GameMenuModel.InjuryRowsKey, NetValue.FromString("12345,h,i,3,1,-1,4")),
            (GameMenuModel.InjuryRowsKey, NetValue.FromString(",h,i,3,1,-1,4")),
            (GameMenuModel.InjuryRowsKey, NetValue.FromString("1a29,x,i,3,1,-1,4")),
            (GameMenuModel.InjuryRowsKey, NetValue.FromString("1a29,h,q,3,1,-1,4")),
            (GameMenuModel.InjuryRowsKey, NetValue.FromString("1a29,h,i,64,1,-1,4")),
            (GameMenuModel.InjuryRowsKey, NetValue.FromString("1a29,h,i,3,2,-1,4")),
            (GameMenuModel.InjuryRowsKey, NetValue.FromString("1a29,h,i,3,1,-100,4")),
            (GameMenuModel.InjuryRowsKey, NetValue.FromString("1a29,h,i,3,1,-0,4")),
            (GameMenuModel.InjuryRowsKey, NetValue.FromString("1a29,h,i,3,1,--1,4")),
            (GameMenuModel.InjuryRowsKey, NetValue.FromString("1a29,h,i,3,1,-1,1000")),
            (GameMenuModel.InjuryRowsKey, NetValue.FromString("1a29,h,i,3,1,-1,04")),
            (GameMenuModel.InjuryRowsKey, NetValue.FromString("1a29,h,-,3,0,-1,4")), // no icon with a type
            (GameMenuModel.InjuryRowsKey, NetValue.FromString(string.Join(";", Enumerable.Repeat(row, GameMenuModel.MaxInjuryRows + 1)))),
        };

        foreach ((string key, NetValue? value) in bad)
        {
            bool read = GameMenuModel.TryRead(With(values, key, value), out GameMenuModel? model, out string problem);
            Assert.False(read, $"{key} = {value}");
            Assert.Null(model);
            Assert.NotEqual(string.Empty, problem);
        }

        // A later chunk without its first is a gap; rows from a format 2 host are never read.
        Assert.False(Reads(With(values.Where(v => v.Key != GameMenuModel.InjuryRowsKey).ToList(), GameMenuModel.InjuryRowsKey + ".1", NetValue.FromString(row))));
        Assert.True(GameMenuModel.TryRead(With(values, GameMenuModel.VersionKey, NetValue.FromInt(2)), out GameMenuModel? older, out string olderProblem), olderProblem);
        Assert.Null(older!.InjuryRows);
    }

    [Fact]
    public void Random_injury_rows_never_throw_and_what_reads_is_within_the_limits()
    {
        List<KeyValuePair<string, NetValue>> values = Written(WithInjuries());
        string rows = values.Single(v => v.Key == GameMenuModel.InjuryRowsKey).Value.Text!;
        var random = new Random(77);
        const string Alphabet = "0123456789abcdef,;-hvir ";
        int read = 0;
        for (int round = 0; round < 4000; round++)
        {
            char[] chars = rows.ToCharArray();
            for (int i = random.Next(1, 4); i > 0; i--)
            {
                chars[random.Next(chars.Length)] = Alphabet[random.Next(Alphabet.Length)];
            }

            if (GameMenuModel.TryRead(With(values, GameMenuModel.InjuryRowsKey, NetValue.FromString(new string(chars))), out GameMenuModel? model, out _))
            {
                read++;
                Assert.True(model!.InjuryRows!.Count <= GameMenuModel.MaxInjuryRows);
                Assert.True(model.TryWrite(new List<KeyValuePair<string, NetValue>>(), out string problem), problem);
            }
        }

        Assert.True(read > 0, "some changed rows should still read");
    }

    [Fact]
    public void Id_prints_and_text_checks_are_stable_and_tell_things_apart()
    {
        Assert.Equal(GameMenuModel.IdPrint("fwd-3"), GameMenuModel.IdPrint("fwd-3"));
        Assert.NotEqual(GameMenuModel.IdPrint("fwd-3"), GameMenuModel.IdPrint("fwd-4"));
        Assert.Equal(GameMenuModel.IdPrint(string.Empty), GameMenuModel.IdPrint(null));

        uint check = GameMenuModel.TextCheck(new[] { "12", "1:23", "Jo", null });
        Assert.Equal(check, GameMenuModel.TextCheck(new[] { "12", "1:23", "Jo", null }));
        Assert.NotEqual(check, GameMenuModel.TextCheck(new[] { "1:23", "12", "Jo", null }));
        Assert.NotEqual(check, GameMenuModel.TextCheck(new[] { "12", "1:23", "Jo", string.Empty }));
        Assert.NotEqual(GameMenuModel.TextCheck(new[] { "1", "2" }), GameMenuModel.TextCheck(new[] { "12" }));

        // Fixed values: the host and a friend may run different builds of the mod's Core within one version.
        Assert.Equal(2166136261u, GameMenuModel.TextCheck(Array.Empty<string?>()));
        Assert.Equal((ushort)0xCD20, GameMenuModel.IdPrint("a")); // FNV-1a("a") = 0xE40C292C, folded
    }
}
