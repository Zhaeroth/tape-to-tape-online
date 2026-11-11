using System;
using System.Collections.Generic;
using System.Linq;
using T2TOnline.Core.Input;

namespace T2TOnline.Core.Tests.Input;

public class ControllerMapsPrintTests
{
    // The game's category ids in the Oct 29 build's order don't matter here; these are just ids.
    private const int Default = 0;
    private const int Ui = 2;
    private const int Fighting = 7;

    private static readonly MapElement Shoot = new(12, 1, 4, 0, 0, false);
    private static readonly MapElement Pass = new(13, 1, 5, 0, 0, false);
    private static readonly MapElement MoveX = new(1, 0, 0, 0, 0, false);
    private static readonly MapElement Back = new(40, 1, 5, 0, 0, false);

    private static string Name(int category) => category switch
    {
        Default => "Default",
        Ui => "UI",
        Fighting => "Fighting",
        _ => $"category {category}",
    };

    private static ControllerMapReading Map(int category, bool enabled, params MapElement[] elements) => new(category, 0, enabled, elements);

    [Fact]
    public void Element_order_and_whether_a_map_is_on_dont_change_the_print()
    {
        ControllerMapsPrint a = ControllerMapsPrint.Of(new[] { Map(Default, true, Shoot, Pass, MoveX), Map(Ui, false, Back) });
        ControllerMapsPrint b = ControllerMapsPrint.Of(new[] { Map(Ui, true, Back), Map(Default, false, MoveX, Shoot, Pass) });

        Assert.True(a.SameMapsAs(b));
        Assert.Equal(a.Hash, b.Hash);
        Assert.Equal(a.Tag, b.Tag);
        Assert.Equal(new[] { Default }, a.Enabled);
        Assert.Equal(new[] { Ui }, b.Enabled);
        Assert.Equal(4, a.Bindings);
        Assert.Equal(2, a.Maps);
    }

    [Fact]
    public void A_moved_binding_a_repeat_or_another_category_changes_it()
    {
        ControllerMapsPrint baseline = ControllerMapsPrint.Of(new[] { Map(Default, true, Shoot, Pass) });
        ControllerMapsPrint moved = ControllerMapsPrint.Of(new[] { Map(Default, true, Shoot with { ElementId = 7 }, Pass) });
        ControllerMapsPrint repeated = ControllerMapsPrint.Of(new[] { Map(Default, true, Shoot, Pass, Pass) });
        ControllerMapsPrint elsewhere = ControllerMapsPrint.Of(new[] { Map(Ui, true, Shoot, Pass) });
        ControllerMapsPrint inverted = ControllerMapsPrint.Of(new[] { Map(Default, true, Shoot, Pass with { Invert = true }) });
        ControllerMapsPrint otherLayout = ControllerMapsPrint.Of(new[] { new ControllerMapReading(Default, 1, true, new[] { Shoot, Pass }) });

        foreach (ControllerMapsPrint other in new[] { moved, repeated, elsewhere, inverted, otherLayout })
        {
            Assert.False(baseline.SameMapsAs(other));
            Assert.NotEqual(baseline.Hash, other.Hash);
        }
    }

    [Fact]
    public void Two_pads_with_the_same_maps_print_the_same_and_an_empty_map_is_still_a_map()
    {
        ControllerMapsPrint one = ControllerMapsPrint.Of(new[] { Map(Default, true, Shoot), Map(Fighting, false) });
        ControllerMapsPrint two = ControllerMapsPrint.Of(new[] { Map(Default, false, Shoot), Map(Fighting, false) });
        ControllerMapsPrint noFighting = ControllerMapsPrint.Of(new[] { Map(Default, true, Shoot) });

        Assert.True(one.SameMapsAs(two));
        Assert.False(one.SameMapsAs(noFighting));
        Assert.Equal(new CategoryPrint(1, 0, 0), one.In(Fighting));
        Assert.Equal(default, noFighting.In(Fighting));
        Assert.False(ControllerMapsPrint.Empty.SameMapsAs(null));
        Assert.True(ControllerMapsPrint.Empty.SameMapsAs(ControllerMapsPrint.Of(Array.Empty<ControllerMapReading>())));
    }

    [Fact]
    public void Counts_name_each_category_and_extra_maps()
    {
        ControllerMapsPrint print = ControllerMapsPrint.Of(new[] { Map(Default, true, Shoot, Pass), Map(Ui, true, Back), Map(Ui, false, Back) });

        Assert.Equal("Default 2, UI 2 in 2 maps", print.Counts(Name));
        Assert.Equal("none", ControllerMapsPrint.Empty.Counts(Name));
        Assert.Equal("[Default, UI]", ControllerMapsPrint.NameSet(new[] { Default, Ui }, Name));
        Assert.Equal("[]", ControllerMapsPrint.NameSet(Array.Empty<int>(), Name));
        Assert.Equal(8, print.Tag.Length);
    }

    [Fact]
    public void No_drift_between_equal_maps()
    {
        ControllerMapsPrint expected = ControllerMapsPrint.Of(new[] { Map(Default, true, Shoot, Pass), Map(Ui, false, Back) });
        ControllerMapsPrint now = ControllerMapsPrint.Of(new[] { Map(Ui, true, Back), Map(Default, false, Pass, Shoot) });

        MapsDrift drift = MapsDrift.Between(expected, now);
        Assert.True(drift.None);
        Assert.Equal("none", drift.Kind);
        Assert.Equal("nothing", drift.Describe(expected, now, Name));
    }

    [Fact]
    public void Drift_names_missing_extra_and_changed_categories()
    {
        ControllerMapsPrint expected = ControllerMapsPrint.Of(new[] { Map(Default, true, Shoot, Pass), Map(Ui, false, Back), Map(Fighting, false) });
        ControllerMapsPrint now = ControllerMapsPrint.Of(new[] { Map(Default, true), Map(Fighting, false), Map(Fighting, true, Shoot), Map(5, true, MoveX) });

        MapsDrift drift = MapsDrift.Between(expected, now);
        Assert.False(drift.None);
        Assert.Equal(new[] { Ui }, drift.Missing);
        Assert.Equal(new[] { 5, Fighting }, drift.Extra);
        Assert.Equal(new[] { Default }, drift.Changed);
        Assert.Equal("missing 2; extra 5,7; changed 0", drift.Kind);
        string line = drift.Describe(expected, now, Name);
        Assert.Contains("UI: its map is missing (1 bindings)", line, StringComparison.Ordinal);
        Assert.Contains("Fighting: 2 maps (1 bindings) where 1 should be", line, StringComparison.Ordinal);
        Assert.Contains("category 5: 1 maps (1 bindings) where 0 should be", line, StringComparison.Ordinal);
        Assert.Contains($"Default: 2 bindings ({expected.In(Default).Tag}), now 0 ({now.In(Default).Tag})", line, StringComparison.Ordinal);
    }

    [Fact]
    public void The_same_kind_of_drift_has_the_same_kind()
    {
        ControllerMapsPrint expected = ControllerMapsPrint.Of(new[] { Map(Default, true, Shoot, Pass) });
        MapsDrift once = MapsDrift.Between(expected, ControllerMapsPrint.Of(new[] { Map(Default, true) }));
        MapsDrift again = MapsDrift.Between(expected, ControllerMapsPrint.Of(new[] { Map(Default, true, Back) }));

        Assert.Equal(once.Kind, again.Kind);
    }

    [Fact]
    public void Fewer_maps_than_expected_but_not_none_is_missing()
    {
        ControllerMapsPrint expected = ControllerMapsPrint.Of(new[] { Map(Ui, true, Back), Map(Ui, true, Shoot) });
        ControllerMapsPrint now = ControllerMapsPrint.Of(new[] { Map(Ui, true, Back) });

        MapsDrift drift = MapsDrift.Between(expected, now);
        Assert.Equal(new[] { Ui }, drift.Missing);
        Assert.Contains("UI: 1 of its 2 maps there", drift.Describe(expected, now, Name), StringComparison.Ordinal);
    }

    [Fact]
    public void A_large_controller_prints_quickly_and_stably()
    {
        // A keyboard friend's maps: ~120 element maps in seven categories, read back in a different order.
        var rng = new Random(7);
        var maps = new List<ControllerMapReading>();
        for (int category = 0; category < 7; category++)
        {
            var elements = new List<MapElement>();
            for (int i = 0; i < 20; i++)
            {
                elements.Add(new MapElement(rng.Next(0, 90), 1, rng.Next(0, 64), rng.Next(0, 2), 0, false));
            }

            maps.Add(Map(category, category == 0, elements.ToArray()));
        }

        ControllerMapsPrint a = ControllerMapsPrint.Of(maps);
        ControllerMapsPrint b = ControllerMapsPrint.Of(maps.AsEnumerable().Reverse().Select(m => m with { Elements = m.Elements.Reverse().ToArray() }));
        Assert.True(a.SameMapsAs(b));
        Assert.Equal(140, a.Bindings);
    }
}

public class MapsGuardTests
{
    private static readonly MapElement Shoot = new(12, 1, 4, 0, 0, false);
    private static readonly MapElement Check = new(14, 1, 17, 0, 0, false);

    private static ControllerMapsPrint Print(params (int Category, bool Enabled, MapElement[] Elements)[] maps) =>
        ControllerMapsPrint.Of(maps.Select(m => new ControllerMapReading(m.Category, 0, m.Enabled, m.Elements)));

    private static ControllerMapsPrint Built() => Print((0, false, new[] { Shoot }), (2, true, new[] { Check }), (4, false, Array.Empty<MapElement>()));

    [Fact]
    public void Nothing_to_compare_before_the_maps_are_built()
    {
        var guard = new MapsGuard();
        Assert.Equal(MapsVerdict.NoExpectation, guard.Check(Built(), out MapsDrift? drift, out bool logIt));
        Assert.Null(drift);
        Assert.False(logIt);
        Assert.Equal(1, guard.Checks);
    }

    [Fact]
    public void Maps_as_built_are_fine_whatever_the_game_switched_on()
    {
        var guard = new MapsGuard();
        guard.NoteApplied(Built());
        ControllerMapsPrint switched = Print((0, true, new[] { Shoot }), (2, false, new[] { Check }), (4, false, Array.Empty<MapElement>()));

        Assert.Equal(MapsVerdict.Fine, guard.Check(switched, out MapsDrift? drift, out _));
        Assert.Null(drift);
        Assert.Equal(new[] { 0 }, guard.LastGoodEnabled);
        Assert.Equal(1, guard.Applied);
        Assert.Equal(0, guard.Drifts);
    }

    [Fact]
    public void Drift_is_fixed_logged_once_per_kind_then_counted()
    {
        var guard = new MapsGuard();
        guard.NoteApplied(Built());
        ControllerMapsPrint reset = Print((0, false, Array.Empty<MapElement>()), (2, true, new[] { Check }), (4, false, Array.Empty<MapElement>()));

        Assert.Equal(MapsVerdict.Fix, guard.Check(reset, out MapsDrift? first, out bool logFirst));
        Assert.True(logFirst);
        Assert.Equal(new[] { 0 }, first!.Changed);
        Assert.True(guard.NoteFix(Built(), out bool gaveUp));
        Assert.False(gaveUp);

        Assert.Equal(MapsVerdict.Fix, guard.Check(reset, out _, out bool logSecond));
        Assert.False(logSecond);
        Assert.True(guard.NoteFix(Built(), out _));

        Assert.Equal(2, guard.Drifts);
        Assert.Equal(2, guard.Corrected);
        Assert.Equal(0, guard.NotCorrected);
        Assert.Contains("drift corrected 2 time(s)", guard.Summary(), StringComparison.Ordinal);
    }

    [Fact]
    public void Fixes_that_keep_failing_stop_until_the_next_build()
    {
        var guard = new MapsGuard(maxFailedFixes: 2);
        guard.NoteApplied(Built());
        ControllerMapsPrint missing = Print((0, false, new[] { Shoot }), (4, false, Array.Empty<MapElement>()));

        Assert.Equal(MapsVerdict.Fix, guard.Check(missing, out _, out _));
        Assert.False(guard.NoteFix(missing, out bool gaveUp1));
        Assert.False(gaveUp1);
        Assert.Equal(MapsVerdict.Fix, guard.Check(missing, out _, out _));
        Assert.False(guard.NoteFix(missing, out bool gaveUp2));
        Assert.True(gaveUp2);
        Assert.True(guard.GaveUp);
        Assert.Equal(MapsVerdict.CountOnly, guard.Check(missing, out _, out _));
        Assert.Equal(3, guard.NotCorrected);
        Assert.Contains("(3 not corrected)", guard.Summary(), StringComparison.Ordinal);

        guard.NoteApplied(Built()); // the friend's controls applied again: a fresh start
        Assert.False(guard.GaveUp);
        Assert.Equal(MapsVerdict.Fine, guard.Check(Built(), out _, out _));
    }

    [Fact]
    public void A_fix_between_failures_resets_the_count()
    {
        var guard = new MapsGuard(maxFailedFixes: 2);
        guard.NoteApplied(Built());
        ControllerMapsPrint missing = Print((0, false, new[] { Shoot }));

        guard.Check(missing, out _, out _);
        guard.NoteFix(missing, out _);
        guard.Check(missing, out _, out _);
        guard.NoteFix(Built(), out _);
        guard.Check(missing, out _, out _);
        guard.NoteFix(missing, out bool gaveUp);

        Assert.False(gaveUp);
        Assert.False(guard.GaveUp);
    }

    [Fact]
    public void Watching_sees_the_game_switch_categories_and_names_the_first_ones()
    {
        var guard = new MapsGuard(categoryChangesNamed: 2);
        guard.NoteApplied(Built());

        Assert.False(guard.Watch(new[] { (0, false), (2, true), (4, false) }, out bool name0, out _)); // the first look
        Assert.False(name0);
        Assert.True(guard.Watch(new[] { (0, true), (2, false), (4, false) }, out bool name1, out IReadOnlyList<int> was1));
        Assert.True(name1);
        Assert.Equal(new[] { 2 }, was1);
        Assert.False(guard.Watch(new[] { (0, true), (2, false), (4, false) }, out _, out _));
        Assert.True(guard.Watch(new[] { (0, true), (2, false), (4, true) }, out bool name2, out _));
        Assert.True(name2);
        Assert.True(guard.Watch(new[] { (0, true), (2, false), (4, false) }, out bool name3, out _));
        Assert.False(name3);
        Assert.Equal(3, guard.CategoryChanges);
        Assert.Equal(new[] { 0 }, guard.LastGoodEnabled);
    }

    [Fact]
    public void A_map_vanishing_is_a_change_and_keeps_the_last_good_categories()
    {
        var guard = new MapsGuard();
        guard.NoteApplied(Built());
        guard.Watch(new[] { (0, true), (2, false), (4, false) }, out _, out _);

        // The Default map (0) is gone: the categories on can't be trusted for it, the last good ones stay.
        Assert.True(guard.Watch(new[] { (2, false), (4, false) }, out _, out _));
        Assert.Equal(new[] { 0 }, guard.LastGoodEnabled);
        Assert.Equal(1, guard.CategoryChanges);
    }

    [Fact]
    public void Bad_limits_are_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new MapsGuard(maxFailedFixes: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MapsGuard(categoryChangesNamed: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MapsCheckSchedule(TimeSpan.Zero));
    }
}

public class MapsCheckScheduleTests
{
    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    [Fact]
    public void Checks_at_once_then_every_interval()
    {
        var schedule = new MapsCheckSchedule(S(3));
        Assert.True(schedule.Due(S(0), out string why));
        Assert.Equal("every 3 s", why);
        schedule.Done(S(0));
        Assert.False(schedule.Due(S(2.9), out _));
        Assert.True(schedule.Due(S(3), out _));
        schedule.Done(S(3.1));
        Assert.False(schedule.Due(S(6), out _));
        Assert.True(schedule.Due(S(6.1), out _));
    }

    [Fact]
    public void A_request_comes_sooner_with_its_reason_and_the_earliest_wins()
    {
        var schedule = new MapsCheckSchedule(S(3));
        schedule.Done(S(0));
        schedule.Request(S(1.5), "scene Arena loaded");
        schedule.Request(S(2.5), "later");
        schedule.Request(S(1), "a match loaded");

        Assert.False(schedule.Due(S(0.9), out _));
        Assert.True(schedule.Due(S(1), out string why));
        Assert.Equal("a match loaded", why);
        schedule.Done(S(1));

        // Done cleared the request; the periodic one is an interval on from then.
        Assert.False(schedule.Due(S(3.9), out _));
        Assert.True(schedule.Due(S(4), out string periodic));
        Assert.Equal("every 3 s", periodic);
    }

    [Fact]
    public void A_request_still_ahead_survives_a_check()
    {
        var schedule = new MapsCheckSchedule(S(3));
        schedule.Request(S(10), "scene loaded");
        Assert.True(schedule.Due(S(0), out _));
        schedule.Done(S(0));
        Assert.True(schedule.Due(S(3), out string why));
        Assert.Equal("every 3 s", why);
        schedule.Done(S(3));
        schedule.Done(S(6));
        Assert.True(schedule.Due(S(10), out string requested));
        Assert.Equal("scene loaded", requested);
    }
}
