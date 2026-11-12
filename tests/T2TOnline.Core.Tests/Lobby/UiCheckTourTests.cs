using System;
using System.Collections.Generic;
using System.Linq;
using T2TOnline.Core.Lobby;

namespace T2TOnline.Core.Tests.Lobby;

public class UiCheckTourTests
{
    private static readonly UiCheckStep[] Steps = { new("a", "a"), new("z", "z", UiCheckStepKind.Finish) };

    // Tours as the plugin may declare them: a name of two words, and keys of both kinds.
    private static readonly UiCheckTour[] Tours =
    {
        new("all", "everything", Steps, TimeSpan.FromMinutes(8)),
        new("campaign full", "a run", Steps, TimeSpan.FromMinutes(40), new[]
        {
            UiCheckKey.Number("seed", "the route's seed", 0, 999),
            UiCheckKey.Choice("record", "record its matches", "on", "off"),
        }),
        new("campaign act1", "act 1", Steps, TimeSpan.FromMinutes(15), new[] { UiCheckKey.Number("seed", "the route's seed", 0, 999) }),
        new("backups", "the backups", Steps, TimeSpan.FromMinutes(8), new[] { UiCheckKey.Choice("restore", "a same-bytes restore", "same") }),
    };

    [Theory]
    [InlineData("all", 12)]
    [InlineData("screens", 12)]
    [InlineData("screens en", 8)]
    [InlineData("screens quick", 8)]
    [InlineData("selfdrive", 8)]
    [InlineData("padwalk", 10)]
    [InlineData("kbwalk", 6)]
    [InlineData("onepc", 8)]
    [InlineData("campaign act1", 15)]
    [InlineData("campaign full", 40)]
    public void Each_tour_has_its_own_limit(string name, int minutes)
    {
        Assert.Equal(TimeSpan.FromMinutes(minutes), UiCheckRequest.Find(UiCheckPlan.Tours, name)!.Limit);
    }

    [Fact]
    public void Every_declared_tours_order_rules_hold_and_its_steps_are_the_plans()
    {
        Assert.All(UiCheckPlan.Tours, t => Assert.Null(UiCheckPlan.Problem(t.Steps)));
        Assert.Same(UiCheckPlan.All, UiCheckRequest.Find(UiCheckPlan.Tours, "all")!.Steps);
        Assert.Same(UiCheckPlan.Screens, UiCheckRequest.Find(UiCheckPlan.Tours, "screens")!.Steps);
        Assert.Same(UiCheckPlan.ScreensEnglish, UiCheckRequest.Find(UiCheckPlan.Tours, "screens en")!.Steps);
        Assert.Same(UiCheckPlan.ScreensQuick, UiCheckRequest.Find(UiCheckPlan.Tours, "screens quick")!.Steps);
        Assert.Same(UiCheckPlan.SelfDrive, UiCheckRequest.Find(UiCheckPlan.Tours, "selfdrive")!.Steps);
        Assert.Same(UiCheckPlan.PadWalk, UiCheckRequest.Find(UiCheckPlan.Tours, "padwalk")!.Steps);
        Assert.Same(UiCheckPlan.OnePc, UiCheckRequest.Find(UiCheckPlan.Tours, "onepc")!.Steps);
        Assert.Same(UiCheckPlan.KbWalk, UiCheckRequest.Find(UiCheckPlan.Tours, "kbwalk")!.Steps);
        Assert.Null(UiCheckRequest.Find(UiCheckPlan.Tours, "nosuchtour"));
    }

    [Fact]
    public void Tour_names_are_unique_lower_case_words_and_no_limit_is_zero()
    {
        Assert.Equal(UiCheckPlan.Tours.Count, UiCheckPlan.Tours.Select(t => t.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(UiCheckPlan.Tours, t =>
        {
            Assert.Matches("^[a-z0-9]+( [a-z0-9]+)*$", t.Name);
            Assert.True(t.Limit > TimeSpan.Zero && t.Limit <= TimeSpan.FromHours(1), t.Name);
        });
    }

    [Fact]
    public void Only_the_offline_start_the_campaign_tours_and_the_keyed_walks_take_arguments()
    {
        string[] keyed = { "padwalk", "selfdrive", "onepc" };
        Assert.All(UiCheckPlan.Tours.Where(t => t.Name != OfflineStart.TourName && CampaignTour.KindOf(t.Name) == null && !keyed.Contains(t.Name)), t => Assert.Empty(t.TakesKeys));
        Assert.Equal(new[] { OfflineStart.ReasonKey }, UiCheckRequest.Find(UiCheckPlan.Tours, OfflineStart.TourName)!.TakesKeys.Select(k => k.Name));
        Assert.All(UiCheckPlan.Tours.Where(t => CampaignTour.KindOf(t.Name) != null), t => Assert.Equal(new[] { "seed", "record", "kbhost", "events", "late", "sides", "brawl", "brawlend" }, t.TakesKeys.Select(k => k.Name)));
        Assert.Equal(new[] { "endonline" }, UiCheckRequest.Find(UiCheckPlan.Tours, "padwalk")!.TakesKeys.Select(k => k.Name));
        Assert.Equal(new[] { "team" }, UiCheckRequest.Find(UiCheckPlan.Tours, "selfdrive")!.TakesKeys.Select(k => k.Name));
        Assert.Equal(new[] { "dc" }, UiCheckRequest.Find(UiCheckPlan.Tours, "onepc")!.TakesKeys.Select(k => k.Name));
        Assert.False(UiCheckRequest.TryParse("kbwalk x=1", UiCheckPlan.Tours, out _, out string problem));
        Assert.Equal("the tour \"kbwalk\" takes no arguments (x)", problem);
        Assert.False(UiCheckRequest.TryParse("selfdrive x=1", UiCheckPlan.Tours, out _, out problem));
        Assert.Equal("the tour \"selfdrive\" takes no key \"x\" (it takes team)", problem);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void No_name_asks_for_the_whole_tour(string? line)
    {
        Assert.True(UiCheckRequest.TryParse(line, UiCheckPlan.Tours, out UiCheckRequest? request, out _));
        Assert.Equal("all", request!.Tour.Name);
        Assert.Empty(request.Arguments);
        Assert.Equal("none", request.ArgumentsText());
    }

    [Theory]
    [InlineData("campaign full")]
    [InlineData("  campaign   full  ")]
    [InlineData("Campaign Full")]
    [InlineData("campaign\tfull")]
    public void A_name_of_two_words_is_read_whatever_its_spaces_and_case(string line)
    {
        Assert.True(UiCheckRequest.TryParse(line, Tours, out UiCheckRequest? request, out string problem), problem);
        Assert.Equal("campaign full", request!.Tour.Name);
        Assert.Equal(TimeSpan.FromMinutes(40), request.Tour.Limit);
    }

    [Fact]
    public void Arguments_are_read_in_their_order()
    {
        Assert.True(UiCheckRequest.TryParse("campaign full record=on seed=3", Tours, out UiCheckRequest? request, out string problem), problem);
        Assert.Equal(new[] { new KeyValuePair<string, string>("record", "on"), new KeyValuePair<string, string>("seed", "3") }, request!.Arguments);
        Assert.Equal("3", request.Value("seed"));
        Assert.Equal("on", request.Value("record"));
        Assert.Null(request.Value("other"));
        Assert.Equal("record=on seed=3", request.ArgumentsText());
    }

    [Theory]
    [InlineData("campaign full seed=1000", "\"seed\" takes 0 to 999, not \"1000\"")]
    [InlineData("campaign full seed=-1", "\"seed\" takes 0 to 999, not \"-1\"")]
    [InlineData("campaign full seed=x", "\"seed\" takes 0 to 999, not \"x\"")]
    [InlineData("campaign full seed=+3", "isn't a key=value")]
    [InlineData("campaign full record=yes", "\"record\" takes on or off, not \"yes\"")]
    [InlineData("campaign full record=ON", "\"record\" takes on or off, not \"ON\"")]
    [InlineData("campaign full sed=3", "the tour \"campaign full\" takes no key \"sed\" (it takes seed, record)")]
    [InlineData("campaign act1 record=on", "the tour \"campaign act1\" takes no key \"record\" (it takes seed)")]
    [InlineData("campaign full seed=3 seed=4", "the key \"seed\" is given twice")]
    [InlineData("campaign full seed=3 now", "\"now\" comes after the arguments began")]
    [InlineData("campaign full seed=", "isn't a key=value")]
    [InlineData("campaign full =3", "isn't a key=value")]
    [InlineData("campaign full Seed=3", "isn't a key=value")]
    [InlineData("campaign full seed=3=4", "isn't a key=value")]
    [InlineData("campaign full seed=a/b", "isn't a key=value")]
    [InlineData("campaign half", "no tour is called \"campaign half\" (the tours: all, campaign full, campaign act1, backups)")]
    [InlineData("backups restore=other", "\"restore\" takes same, not \"other\"")]
    public void What_a_tour_doesnt_declare_is_refused_with_why(string line, string why)
    {
        Assert.False(UiCheckRequest.TryParse(line, Tours, out UiCheckRequest? request, out string problem));
        Assert.Null(request);
        Assert.Contains(why, problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Lines_keys_values_and_counts_are_bounded()
    {
        Assert.False(UiCheckRequest.TryParse("all " + new string('x', UiCheckRequest.MaxLineLength), Tours, out _, out string tooLong));
        Assert.Contains("over 160 characters", tooLong, StringComparison.Ordinal);

        var many = new UiCheckTour("many", "many", Steps, TimeSpan.FromMinutes(1), Enumerable.Range(0, 10).Select(i => UiCheckKey.Number($"k{i}", "k", 0, 9)).ToArray());
        string nine = "many " + string.Join(' ', Enumerable.Range(0, 9).Select(i => $"k{i}=1"));
        Assert.False(UiCheckRequest.TryParse(nine, new[] { many }, out _, out string tooMany));
        Assert.Equal("more than 8 arguments", tooMany);
        Assert.True(UiCheckRequest.TryParse("many " + string.Join(' ', Enumerable.Range(0, 8).Select(i => $"k{i}=1")), new[] { many }, out _, out _));

        var wide = new UiCheckTour("wide", "wide", Steps, TimeSpan.FromMinutes(1), new[] { UiCheckKey.Choice("name", "a name", new string('v', 32), new string('v', 33)) });
        Assert.True(UiCheckRequest.TryParse("wide name=" + new string('v', 32), new[] { wide }, out _, out _));
        Assert.False(UiCheckRequest.TryParse("wide name=" + new string('v', 33), new[] { wide }, out _, out _));
    }

    [Fact]
    public void A_refusal_never_echoes_more_than_a_short_clean_piece_of_the_line()
    {
        Assert.False(UiCheckRequest.TryParse("C:\\Users\\Alex\\secret path that goes on", Tours, out _, out string problem));
        Assert.DoesNotContain("\\", problem, StringComparison.Ordinal);
        Assert.DoesNotContain("goes on", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Keys_say_what_they_allow()
    {
        UiCheckKey number = UiCheckKey.Number("seed", "s", 2, 5);
        Assert.True(number.Allows("2"));
        Assert.True(number.Allows("5"));
        Assert.False(number.Allows("6"));
        Assert.False(number.Allows("1"));
        Assert.False(number.Allows(" 3"));
        Assert.Equal("2 to 5", number.Allowed);
        Assert.Throws<ArgumentOutOfRangeException>(() => UiCheckKey.Number("bad", "b", 5, 2));
        Assert.Throws<ArgumentException>(() => UiCheckKey.Choice("none", "n"));
    }
}
