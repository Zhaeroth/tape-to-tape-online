using System;
using System.Collections.Generic;
using System.Linq;
using T2TOnline.Core.Campaign;
using T2TOnline.Core.Serialization;

namespace T2TOnline.Core.Tests.Campaign;

public class SceneViewsTests
{
    private const string RunId = "6f1c2e7a-0b44-4c5e-9a3e-2d1f8b7c9e01";

    private static List<MapPoint> Path(params (int X, int Y)[] points) => points.Select(p => new MapPoint(p.X, p.Y)).ToList();

    /// <summary>A view as a friend's game gets it: written, sent and read back.</summary>
    private static HostView Sent(string screen, IReadOnlyList<KeyValuePair<string, NetValue>> values)
    {
        var writer = new BitWriter();
        HostViewMessage.Write(writer, new HostView(7, screen, 3, values));
        var reader = new BitReader(writer.AsSpan());
        Assert.True(HostViewMessage.TryRead(ref reader, out HostView? view));
        return view!;
    }

    private static List<KeyValuePair<string, NetValue>> Replace(List<KeyValuePair<string, NetValue>> values, string key, NetValue value)
    {
        values.RemoveAll(v => v.Key == key);
        values.Add(new(key, value));
        return values;
    }

    private static PreviewViewDetails Preview() => new(
        new MapViewDetails(RunId, 2, Path((3, 0), (2, 1)), null, null),
        new MapPoint(1, 2),
        NodeType: 0,
        new[] { "forward_a", "forward_b", string.Empty, "forward_d", "forward_e" },
        "goalie_a",
        "relic_x",
        string.Empty,
        "ability_y");

    [Theory]
    [InlineData("0,0", 0, 0)]
    [InlineData("3,14", 3, 14)]
    [InlineData("63,63", 63, 63)]
    [InlineData("-1,-1", -1, -1)]
    public void Points_read_what_they_write(string text, int x, int y)
    {
        Assert.True(MapPoint.TryParse(text, out MapPoint point));
        Assert.Equal(new MapPoint(x, y), point);
        Assert.Equal(text, point.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("3")]
    [InlineData("3,")]
    [InlineData(",3")]
    [InlineData("64,0")]
    [InlineData("0,-2")]
    [InlineData("-1,0")]
    [InlineData("1,2,3")]
    [InlineData("a,b")]
    [InlineData("123456,1")]
    public void Bad_points_are_refused(string? text) => Assert.False(MapPoint.TryParse(text, out _));

    [Fact]
    public void Paths_round_trip_and_refuse_bad_nodes()
    {
        List<MapPoint> path = Path((3, 0), (2, 1), (2, 2));
        Assert.Equal("3,0;2,1;2,2", MapPath.Format(path));
        Assert.True(MapPath.TryParse("3,0;2,1;2,2", out List<MapPoint> read));
        Assert.Equal(path, read);
        Assert.True(MapPath.TryParse(string.Empty, out List<MapPoint> empty));
        Assert.Empty(empty);
        Assert.False(MapPath.TryParse(null, out _));
        Assert.False(MapPath.TryParse("3,0;;2,1", out _));
        Assert.False(MapPath.TryParse("3,0;-1,-1", out _));
        Assert.False(MapPath.TryParse(string.Join(';', Enumerable.Repeat("1,1", MapPath.MaxPoints + 1)), out _));
        Assert.True(MapPath.TryParse(string.Join(';', Enumerable.Repeat("63,63", MapPath.MaxPoints)), out List<MapPoint> longest));
        Assert.True(MapPath.Format(longest).Length <= NetValue.MaxStringBytes);
    }

    [Fact]
    public void A_map_view_survives_the_network_with_its_cursor()
    {
        var details = new MapViewDetails(RunId, 2, Path((3, 0), (2, 1)), new MapPoint(1, 2), 12.5f);
        HostView view = Sent("map", details.ToValues());
        Assert.True(MapViewDetails.TryRead(view, out MapViewDetails? read, out string problem), problem);
        Assert.Equal(RunId, read!.RunId);
        Assert.Equal(2, read.Act);
        Assert.Equal(details.Path, read.Path);
        Assert.Equal(new MapPoint(2, 1), read.Node);
        Assert.Equal(new MapPoint(1, 2), read.Selected);
        Assert.Equal(12.5f, read.CameraX);
        Assert.True(read.SamePlace(details));
        Assert.Equal("2,1", view.Text(SceneViewKeys.Node));
    }

    [Fact]
    public void A_new_act_has_no_current_node_and_no_cursor_is_fine()
    {
        var details = new MapViewDetails(RunId, 1, new List<MapPoint>(), null, null);
        Assert.True(MapViewDetails.TryRead(Sent("map", details.ToValues()), out MapViewDetails? read, out _));
        Assert.True(read!.Node.IsNone);
        Assert.Null(read.Selected);
        Assert.Null(read.CameraX);
    }

    [Fact]
    public void A_new_acts_intro_travels_with_the_map_view_and_an_older_hosts_view_has_none()
    {
        var details = new MapViewDetails(RunId, 2, new List<MapPoint>(), null, null) { Intro = 2 };
        Assert.True(MapViewDetails.TryRead(Sent("map", details.ToValues()), out MapViewDetails? read, out string problem), problem);
        Assert.Equal(2, read!.Intro);
        Assert.True(read.SamePlace(details with { Intro = null })); // the intro isn't part of the place

        Assert.True(MapViewDetails.TryRead(Sent("map", (details with { Intro = null }).ToValues()), out MapViewDetails? none, out _));
        Assert.Null(none!.Intro);
        Assert.False((details with { Intro = null }).ToValues().Exists(v => v.Key == SceneViewKeys.Intro));

        foreach (NetValue bad in new[] { NetValue.FromInt(0), NetValue.FromInt(MapViewDetails.MaxAct + 1), NetValue.FromString("2"), NetValue.FromFloat(2f) })
        {
            List<KeyValuePair<string, NetValue>> values = (details with { Intro = null }).ToValues();
            values.Add(new(SceneViewKeys.Intro, bad));
            Assert.False(MapViewDetails.TryRead(Sent("map", values), out MapViewDetails? refused, out string why), bad.ToString());
            Assert.Null(refused);
            Assert.Equal("bad intro act", why);
        }
    }

    [Fact]
    public void The_hosts_act_intro_start_travels_with_the_map_view_and_an_older_hosts_view_says_nothing()
    {
        var held = new MapViewDetails(RunId, 2, new List<MapPoint>(), null, null) { Intro = 2, IntroStarted = false };
        Assert.True(MapViewDetails.TryRead(Sent("map", held.ToValues()), out MapViewDetails? read, out string problem), problem);
        Assert.False(read!.IntroStarted);
        Assert.True(read.SamePlace(held with { IntroStarted = true })); // the start isn't part of the place

        Assert.True(MapViewDetails.TryRead(Sent("map", (held with { IntroStarted = true }).ToValues()), out MapViewDetails? started, out _));
        Assert.True(started!.IntroStarted);

        Assert.True(MapViewDetails.TryRead(Sent("map", (held with { IntroStarted = null }).ToValues()), out MapViewDetails? older, out _));
        Assert.Null(older!.IntroStarted);
        Assert.False((held with { IntroStarted = null }).ToValues().Exists(v => v.Key == SceneViewKeys.IntroStarted));

        foreach (NetValue bad in new[] { NetValue.FromInt(1), NetValue.FromString("true"), NetValue.Null })
        {
            List<KeyValuePair<string, NetValue>> values = (held with { IntroStarted = null }).ToValues();
            values.Add(new(SceneViewKeys.IntroStarted, bad));
            Assert.False(MapViewDetails.TryRead(Sent("map", values), out MapViewDetails? refused, out string why), bad.ToString());
            Assert.Null(refused);
            Assert.Equal("bad intro start flag", why);
        }
    }

    [Fact]
    public void A_map_view_with_bad_details_is_refused_with_a_reason()
    {
        List<KeyValuePair<string, NetValue>> Good() => new MapViewDetails(RunId, 2, Path((3, 0), (2, 1)), new MapPoint(1, 2), 1f).ToValues();
        var cases = new Dictionary<string, List<KeyValuePair<string, NetValue>>>
        {
            ["no run"] = Replace(Good(), SceneViewKeys.Run, NetValue.FromString(string.Empty)),
            ["run with a control"] = Replace(Good(), SceneViewKeys.Run, NetValue.FromString("abc\u0001")),
            ["act 0"] = Replace(Good(), SceneViewKeys.Act, NetValue.FromInt(0)),
            ["act 99"] = Replace(Good(), SceneViewKeys.Act, NetValue.FromInt(99)),
            ["act as text"] = Replace(Good(), SceneViewKeys.Act, NetValue.FromString("2")),
            ["bad path"] = Replace(Good(), SceneViewKeys.Path, NetValue.FromString("3,0;x")),
            ["path as a number"] = Replace(Good(), SceneViewKeys.Path, NetValue.FromInt(3)),
            ["node not the path's last"] = Replace(Good(), SceneViewKeys.Node, NetValue.FromString("3,0")),
            ["bad cursor"] = Replace(Good(), SceneViewKeys.Selected, NetValue.FromString("-1,-1")),
            ["camera not a number"] = Replace(Good(), SceneViewKeys.CameraX, NetValue.FromString("1")),
            ["camera not finite"] = Replace(Good(), SceneViewKeys.CameraX, NetValue.FromFloat(float.NaN)),
            ["camera far away"] = Replace(Good(), SceneViewKeys.CameraX, NetValue.FromFloat(1e9f)),
        };

        foreach ((string name, List<KeyValuePair<string, NetValue>> values) in cases)
        {
            Assert.False(MapViewDetails.TryRead(Sent("map", values), out MapViewDetails? read, out string problem), name);
            Assert.Null(read);
            Assert.False(string.IsNullOrEmpty(problem), name);
        }

        var missing = Good();
        missing.RemoveAll(v => v.Key == SceneViewKeys.Path);
        Assert.False(MapViewDetails.TryRead(Sent("map", missing), out _, out _));
    }

    [Fact]
    public void A_preview_survives_the_network()
    {
        PreviewViewDetails preview = Preview();
        HostView view = Sent("preview", preview.ToValues());
        Assert.True(PreviewViewDetails.TryRead(view, out PreviewViewDetails? read, out string problem), problem);
        Assert.Equal(new MapPoint(1, 2), read!.Target);
        Assert.Equal(0, read.NodeType);
        Assert.Equal(preview.Opponent, read.Opponent);
        Assert.Equal("goalie_a", read.Goalie);
        Assert.Equal("relic_x", read.RewardRelic);
        Assert.Equal(string.Empty, read.RewardTalent);
        Assert.Equal("ability_y", read.RewardAbility);
        Assert.True(read.Map.SamePlace(preview.Map));
        Assert.Equal("1,2", view.Text(SceneViewKeys.Node));
        Assert.False(read.Zooming); // not started: the key isn't sent at all
        Assert.False(view.TryGet(SceneViewKeys.Zoom, out _));
    }

    [Fact]
    public void A_preview_whose_match_the_host_started_says_so()
    {
        PreviewViewDetails preview = Preview() with { Zooming = true };
        Assert.True(PreviewViewDetails.TryRead(Sent("preview", preview.ToValues()), out PreviewViewDetails? read, out string problem), problem);
        Assert.True(read!.Zooming);

        // Anything but a true flag is "not started": a whole number, text or false.
        foreach (NetValue odd in new[] { NetValue.FromInt(1), NetValue.FromString("true"), NetValue.FromBool(false) })
        {
            List<KeyValuePair<string, NetValue>> values = Preview().ToValues();
            values.Add(new(SceneViewKeys.Zoom, odd));
            Assert.True(PreviewViewDetails.TryRead(Sent("preview", values), out read, out problem), problem);
            Assert.False(read!.Zooming);
        }
    }

    [Fact]
    public void A_preview_with_bad_details_is_refused()
    {
        List<KeyValuePair<string, NetValue>> Good() => Preview().ToValues();
        var cases = new Dictionary<string, List<KeyValuePair<string, NetValue>>>
        {
            ["no node"] = Replace(Good(), SceneViewKeys.Node, NetValue.FromString("-1,-1")),
            ["event node"] = Replace(Good(), SceneViewKeys.NodeType, NetValue.FromInt(4)),
            ["slot as a number"] = Replace(Good(), SceneViewKeys.OpponentSlot + "3", NetValue.FromInt(3)),
            ["slot with a control"] = Replace(Good(), SceneViewKeys.OpponentSlot + "0", NetValue.FromString("a\u0007b")),
            ["slot too long"] = Replace(Good(), SceneViewKeys.OpponentSlot + "1", NetValue.FromString(new string('a', PreviewViewDetails.MaxAssetIdLength + 1))),
            ["goalie missing"] = Replace(Good(), SceneViewKeys.OpponentGoalie, NetValue.Null),
            ["reward as a flag"] = Replace(Good(), SceneViewKeys.RewardRelic, NetValue.FromBool(true)),
            ["bad act"] = Replace(Good(), SceneViewKeys.Act, NetValue.FromInt(-3)),
        };

        foreach ((string name, List<KeyValuePair<string, NetValue>> values) in cases)
        {
            Assert.False(PreviewViewDetails.TryRead(Sent("preview", values), out PreviewViewDetails? read, out string problem), name);
            Assert.Null(read);
            Assert.False(string.IsNullOrEmpty(problem), name);
        }
    }

    [Fact]
    public void A_node_entry_and_the_cursors_details_travel_with_the_map_view()
    {
        var details = new MapViewDetails(RunId, 2, Path((3, 0), (2, 1)), new MapPoint(2, 2), 4f)
        {
            Entering = new NodeEntry(new MapPoint(2, 2), 7),
            TooltipShown = true,
        };
        HostView view = Sent("map", details.ToValues());
        Assert.True(MapViewDetails.TryRead(view, out MapViewDetails? read, out string problem), problem);
        Assert.Equal(new NodeEntry(new MapPoint(2, 2), 7), read!.Entering);
        Assert.True(read.TooltipShown);
        Assert.Equal("2,2", view.Text(SceneViewKeys.Enter));
        Assert.Equal(7, view.Whole(SceneViewKeys.EnterNumber, -1));

        // Neither is part of the place (a friend's map doesn't move for them); a tooltip that's down says so.
        Assert.True(read.SamePlace(details with { Entering = null, TooltipShown = null }));
        Assert.True(MapViewDetails.TryRead(Sent("map", (details with { Entering = null, TooltipShown = false }).ToValues()), out MapViewDetails? down, out _));
        Assert.Null(down!.Entering);
        Assert.False(down.TooltipShown);
    }

    [Fact]
    public void A_map_view_without_an_entry_or_tooltip_says_nothing_about_them()
    {
        // As an older host sends it: no entry under way, the tooltip unknown (a friend then leaves its own alone).
        var details = new MapViewDetails(RunId, 2, Path((3, 0), (2, 1)), new MapPoint(2, 2), 4f);
        List<KeyValuePair<string, NetValue>> values = details.ToValues();
        Assert.DoesNotContain(values, v => v.Key is SceneViewKeys.Enter or SceneViewKeys.EnterNumber or SceneViewKeys.Tooltip);
        Assert.True(MapViewDetails.TryRead(Sent("map", values), out MapViewDetails? read, out _));
        Assert.Null(read!.Entering);
        Assert.Null(read.TooltipShown);
    }

    [Fact]
    public void A_bad_node_entry_or_tooltip_refuses_the_view_with_a_reason()
    {
        List<KeyValuePair<string, NetValue>> Good() =>
            (new MapViewDetails(RunId, 2, Path((3, 0), (2, 1)), new MapPoint(1, 2), 1f) { Entering = new NodeEntry(new MapPoint(1, 2), 3) }).ToValues();
        List<KeyValuePair<string, NetValue>> Without(string key)
        {
            List<KeyValuePair<string, NetValue>> values = Good();
            values.RemoveAll(v => v.Key == key);
            return values;
        }

        var cases = new Dictionary<string, List<KeyValuePair<string, NetValue>>>
        {
            ["a node without a number"] = Without(SceneViewKeys.EnterNumber),
            ["a number without a node"] = Without(SceneViewKeys.Enter),
            ["no node"] = Replace(Good(), SceneViewKeys.Enter, NetValue.FromString("-1,-1")),
            ["a node off the map"] = Replace(Good(), SceneViewKeys.Enter, NetValue.FromString("64,1")),
            ["a node as a number"] = Replace(Good(), SceneViewKeys.Enter, NetValue.FromInt(3)),
            ["entry 0"] = Replace(Good(), SceneViewKeys.EnterNumber, NetValue.FromInt(0)),
            ["a negative entry"] = Replace(Good(), SceneViewKeys.EnterNumber, NetValue.FromInt(-4)),
            ["an entry past the largest"] = Replace(Good(), SceneViewKeys.EnterNumber, NetValue.FromInt((long)NodeEntry.MaxNumber + 1)),
            ["an entry as text"] = Replace(Good(), SceneViewKeys.EnterNumber, NetValue.FromString("3")),
            ["a tooltip as a number"] = Replace(Good(), SceneViewKeys.Tooltip, NetValue.FromInt(1)),
        };

        foreach ((string name, List<KeyValuePair<string, NetValue>> values) in cases)
        {
            Assert.False(MapViewDetails.TryRead(Sent("map", values), out MapViewDetails? read, out string problem), name);
            Assert.Null(read);
            Assert.False(string.IsNullOrEmpty(problem), name);
        }
    }

    [Fact]
    public void The_relic_bars_cursor_travels_with_the_map_view()
    {
        var details = new MapViewDetails(RunId, 2, Path((3, 0), (2, 1)), new MapPoint(2, 1), 4f) { Relic = "BloodRelic", RelicAt = 3 };
        List<KeyValuePair<string, NetValue>> values = details.ToValues();
        Assert.True(MapViewDetails.TryRead(Sent("map", values), out MapViewDetails? read, out string problem), problem);
        Assert.Equal(("BloodRelic", (int?)3), (read!.Relic, read.RelicAt));

        // Not part of the place (the map doesn't move for it); out of the bar, it's not sent at all, as from an older host.
        Assert.True(read.SamePlace(details with { Relic = null, RelicAt = null }));
        List<KeyValuePair<string, NetValue>> outside = (details with { Relic = null, RelicAt = null }).ToValues();
        Assert.DoesNotContain(outside, v => v.Key is SceneViewKeys.Relic or SceneViewKeys.RelicAt);
        Assert.True(MapViewDetails.TryRead(Sent("map", outside), out MapViewDetails? none, out _));
        Assert.Equal(((string?)null, (int?)null), (none!.Relic, none.RelicAt));
        Assert.Contains(SceneViewKeys.Relic, SceneViewKeys.Transient);
        Assert.Contains(SceneViewKeys.RelicAt, SceneViewKeys.Transient);
    }

    [Fact]
    public void A_bad_relic_bar_cursor_refuses_the_view_with_a_reason()
    {
        List<KeyValuePair<string, NetValue>> Good() =>
            (new MapViewDetails(RunId, 2, Path((3, 0), (2, 1)), new MapPoint(1, 2), 1f) { Relic = "BeanDiet", RelicAt = 0 }).ToValues();
        List<KeyValuePair<string, NetValue>> Without(string key)
        {
            List<KeyValuePair<string, NetValue>> values = Good();
            values.RemoveAll(v => v.Key == key);
            return values;
        }

        var cases = new Dictionary<string, List<KeyValuePair<string, NetValue>>>
        {
            ["a relic without a place"] = Without(SceneViewKeys.RelicAt),
            ["a place without a relic"] = Without(SceneViewKeys.Relic),
            ["an empty relic"] = Replace(Good(), SceneViewKeys.Relic, NetValue.FromString(string.Empty)),
            ["a relic id too long"] = Replace(Good(), SceneViewKeys.Relic, NetValue.FromString(new string('r', MapViewDetails.MaxRelicIdLength + 1))),
            ["a relic as a number"] = Replace(Good(), SceneViewKeys.Relic, NetValue.FromInt(3)),
            ["a negative place"] = Replace(Good(), SceneViewKeys.RelicAt, NetValue.FromInt(-1)),
            ["a place past the last"] = Replace(Good(), SceneViewKeys.RelicAt, NetValue.FromInt(MapViewDetails.MaxRelicPlace + 1)),
            ["a place as text"] = Replace(Good(), SceneViewKeys.RelicAt, NetValue.FromString("0")),
        };

        foreach ((string name, List<KeyValuePair<string, NetValue>> values) in cases)
        {
            Assert.False(MapViewDetails.TryRead(Sent("map", values), out MapViewDetails? read, out string problem), name);
            Assert.Null(read);
            Assert.False(string.IsNullOrEmpty(problem), name);
        }
    }

    [Fact]
    public void A_cursors_tooltip_is_transient_but_a_node_entry_is_not()
    {
        // The tooltip changes with the cursor (not worth a log line or a fresh follow); an entry is a step the host's game takes.
        Assert.Contains(SceneViewKeys.Tooltip, SceneViewKeys.Transient);
        Assert.DoesNotContain(SceneViewKeys.Enter, SceneViewKeys.Transient);
        Assert.DoesNotContain(SceneViewKeys.EnterNumber, SceneViewKeys.Transient);
    }

    [Fact]
    public void Garbage_details_never_throw()
    {
        var random = new Random(4242);
        string[] keys = { SceneViewKeys.Run, SceneViewKeys.Act, SceneViewKeys.Path, SceneViewKeys.Node, SceneViewKeys.Selected, SceneViewKeys.CameraX,
            SceneViewKeys.NodeType, SceneViewKeys.OpponentSlot + "0", SceneViewKeys.OpponentGoalie, SceneViewKeys.RewardRelic,
            SceneViewKeys.Enter, SceneViewKeys.EnterNumber, SceneViewKeys.Tooltip, SceneViewKeys.Relic, SceneViewKeys.RelicAt };
        for (int round = 0; round < 2000; round++)
        {
            var values = new List<KeyValuePair<string, NetValue>>();
            foreach (string key in keys)
            {
                if (random.Next(4) == 0)
                {
                    continue;
                }

                values.Add(new(key, random.Next(4) switch
                {
                    0 => NetValue.FromInt(random.Next(-5, 70)),
                    1 => NetValue.FromFloat((float)(random.NextDouble() * 200 - 100)),
                    2 => NetValue.FromString(new string(Enumerable.Range(0, random.Next(0, 12)).Select(_ => "0123,;-?ab"[random.Next(10)]).ToArray())),
                    _ => NetValue.Null,
                }));
            }

            var view = new HostView(1, "map", 1, values);
            _ = MapViewDetails.TryRead(view, out _, out _);
            _ = PreviewViewDetails.TryRead(view, out _, out _);
        }
    }

    [Fact]
    public void A_friend_on_the_hosts_map_stays_advances_or_reloads()
    {
        var host = new MapViewDetails(RunId, 2, Path((3, 0), (2, 1), (2, 2)), null, null);
        Assert.Equal(MapMove.Stay, MapFollow.Decide(new MapPlace(RunId, 2, Path((3, 0), (2, 1), (2, 2))), host, out _));
        Assert.Equal(MapMove.Advance, MapFollow.Decide(new MapPlace(RunId, 2, Path((3, 0), (2, 1))), host, out string why));
        Assert.Contains("1 node", why, StringComparison.Ordinal);
        Assert.Equal(MapMove.Advance, MapFollow.Decide(new MapPlace(RunId, 2, new List<MapPoint>()), host, out _));
        Assert.Equal(MapMove.Reload, MapFollow.Decide(null, host, out _));
        Assert.Equal(MapMove.Reload, MapFollow.Decide(new MapPlace("another", 2, Path((3, 0), (2, 1))), host, out _));
        Assert.Equal(MapMove.Reload, MapFollow.Decide(new MapPlace(RunId, 1, Path((3, 0), (2, 1))), host, out _));
        Assert.Equal(MapMove.Reload, MapFollow.Decide(new MapPlace(RunId, 2, Path((3, 0), (1, 1))), host, out _));
        Assert.Equal(MapMove.Reload, MapFollow.Decide(new MapPlace(RunId, 2, Path((3, 0), (2, 1), (2, 2), (2, 3))), host, out _));
    }

    [Theory]
    [InlineData(true, "start_screen", true)]
    [InlineData(false, "world_map", true)]
    [InlineData(false, "start_screen", false)]
    [InlineData(false, "standard_game", false)]
    [InlineData(true, "standard_game", true)]
    public void Only_a_friend_in_the_campaign_leaves_for_the_title(bool holding, string scene, bool leaves) =>
        Assert.Equal(leaves, MapFollow.LeaveForTitle(holding, scene));

    [Fact]
    public void The_gate_lets_one_change_through_per_interval()
    {
        var gate = new RepublishGate(100_000);
        Assert.True(gate.TryPass(0));
        Assert.False(gate.TryPass(50_000));
        Assert.False(gate.TryPass(99_999));
        Assert.True(gate.TryPass(100_000));
        Assert.False(gate.TryPass(150_000));
        gate.Reset();
        Assert.True(gate.TryPass(150_001));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RepublishGate(-1));
    }
}
