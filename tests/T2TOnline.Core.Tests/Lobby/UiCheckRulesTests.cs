using System.Collections.Generic;
using System.Linq;
using T2TOnline.Core.Lobby;

namespace T2TOnline.Core.Tests.Lobby;

public class UiCheckRulesTests
{
    private static readonly UiRect Canvas = new(0, 0, 1920, 1080);

    private static UiElement Text(string path, UiRect rect, int parent = 0, string shown = "Online", float preferred = 10, float available = 100) => new()
    {
        Path = path,
        Parent = parent,
        Rect = rect,
        Text = new UiTextFacts { Shown = shown, PreferredWidth = preferred, AvailableWidth = available },
    };

    private static UiElement Root() => new() { Path = "root", Rect = Canvas };

    private static List<UiCheckHit> Hits(params UiElement[] elements) => UiCheckRules.Compute(elements, Canvas);

    private static IEnumerable<UiCheckIssue> Kinds(params UiElement[] elements) => Hits(elements).Select(h => h.Flag);

    [Fact]
    public void A_screen_that_fits_has_no_flags()
    {
        Assert.Empty(Hits(Root(), Text("root/a", new UiRect(10, 10, 100, 40))));
    }

    [Fact]
    public void A_text_past_the_canvas_by_more_than_the_slack_is_offscreen()
    {
        Assert.Contains(UiCheckIssue.Offscreen, Kinds(Root(), Text("root/a", new UiRect(1830, 10, 100, 40))));
        Assert.DoesNotContain(UiCheckIssue.Offscreen, Kinds(Root(), Text("root/a", new UiRect(1821, 10, 100, 40))));
    }

    [Fact]
    public void A_background_past_the_canvas_doesnt_count()
    {
        Assert.Empty(Hits(Root(), new UiElement { Path = "root/bg", Parent = 0, Rect = new UiRect(-50, -50, 2020, 1180) }));
    }

    [Fact]
    public void A_text_wider_than_its_rect_with_wrap_off_is_over_measured_not_by_the_flag()
    {
        UiElement over = Text("root/a", new UiRect(0, 0, 100, 40), preferred: 180, available: 100);
        UiCheckHit hit = Assert.Single(Hits(Root(), over));
        Assert.Equal(UiCheckIssue.TextOver, hit.Flag);
        Assert.Contains("180", hit.Detail, System.StringComparison.Ordinal);
    }

    [Fact]
    public void A_wrapping_text_isnt_over_unless_truncated()
    {
        var wrapping = new UiElement { Path = "root/a", Parent = 0, Rect = new UiRect(0, 0, 100, 40), Text = new UiTextFacts { Shown = "x", Wraps = true, PreferredWidth = 500, AvailableWidth = 100 } };
        Assert.Empty(Hits(Root(), wrapping));
        var cut = wrapping with { Text = wrapping.Text! with { Truncated = true } };
        Assert.Equal(UiCheckIssue.TextOver, Assert.Single(Hits(Root(), cut)).Flag);
    }

    [Fact]
    public void An_auto_sizing_text_at_its_minimum_is_flagged()
    {
        var at = new UiTextFacts { Shown = "x", AutoSizes = true, Size = 18, SizeMin = 18, PreferredWidth = 1, AvailableWidth = 10 };
        Assert.Equal(UiCheckIssue.AutoSizeMin, Assert.Single(Hits(Root(), new UiElement { Path = "a", Parent = 0, Rect = new UiRect(0, 0, 10, 10), Text = at })).Flag);
        Assert.Empty(Hits(Root(), new UiElement { Path = "a", Parent = 0, Rect = new UiRect(0, 0, 10, 10), Text = at with { Size = 24 } }));
    }

    [Fact]
    public void Sibling_texts_that_intersect_overlap_cousins_dont()
    {
        UiElement first = Text("root/a", new UiRect(0, 0, 100, 40));
        UiElement sibling = Text("root/b", new UiRect(50, 0, 100, 40));
        UiCheckHit hit = Assert.Single(Hits(Root(), first, sibling));
        Assert.Equal(UiCheckIssue.Overlap, hit.Flag);
        UiElement group = new() { Path = "root/g", Parent = 0, Rect = Canvas };
        UiElement cousin = Text("root/g/b", new UiRect(50, 0, 100, 40), parent: 2);
        Assert.DoesNotContain(UiCheckIssue.Overlap, Kinds(Root(), first, group, cousin));
    }

    [Fact]
    public void Touching_edges_arent_an_overlap()
    {
        Assert.Empty(Hits(Root(), Text("root/a", new UiRect(0, 0, 100, 40)), Text("root/b", new UiRect(100, 0, 100, 40))));
    }

    [Fact]
    public void A_text_past_its_nearest_mask_is_clipped_only_by_that_mask()
    {
        var mask = new UiElement { Path = "root/mask", Parent = 0, Rect = new UiRect(0, 0, 200, 200), Masks = true };
        var plain = new UiElement { Path = "root/mask/plain", Parent = 1, Rect = new UiRect(0, 0, 900, 900) };
        UiElement inside = Text("root/mask/plain/a", new UiRect(10, 10, 100, 40), parent: 2);
        UiElement outside = Text("root/mask/plain/b", new UiRect(190, 0, 100, 40), parent: 2);
        UiCheckHit hit = Hits(Root(), mask, plain, inside, outside).Single(h => h.Flag == UiCheckIssue.Clipped);
        Assert.Equal("root/mask/plain/b", hit.Path);
        Assert.Contains("root/mask", hit.Detail, System.StringComparison.Ordinal);
    }

    [Fact]
    public void A_bad_parent_chain_ends_the_walk()
    {
        var loop = new UiElement { Path = "a", Parent = 0, Rect = new UiRect(0, 0, 1, 1), Text = new UiTextFacts { Shown = "x" } };
        Assert.NotNull(UiCheckRules.Compute(new[] { loop }, Canvas));
        var far = new UiElement { Path = "a", Parent = 9, Rect = new UiRect(0, 0, 1, 1), Text = new UiTextFacts { Shown = "x" } };
        Assert.NotNull(UiCheckRules.Compute(new[] { far }, Canvas));
    }

    [Fact]
    public void Missing_glyphs_and_a_term_without_text_are_named()
    {
        var facts = new UiTextFacts { Shown = "邀", MissingGlyphs = "邀", Term = "t2t/invite", TermHasText = false };
        List<UiCheckHit> hits = Hits(Root(), new UiElement { Path = "a", Parent = 0, Rect = new UiRect(0, 0, 10, 10), Text = facts });
        Assert.Contains(hits, h => h.Flag == UiCheckIssue.NoGlyph && h.Detail.Contains('邀'));
        Assert.Contains(hits, h => h.Flag == UiCheckIssue.NoTerm && h.Detail == "t2t/invite");
    }

    [Fact]
    public void A_navigable_with_no_neighbour_is_a_hole_with_one_it_isnt()
    {
        var alone = new UiElement { Path = "b", Parent = 0, Rect = new UiRect(0, 0, 10, 10), Navigable = true };
        Assert.Equal(UiCheckIssue.NavHole, Assert.Single(Hits(Root(), alone)).Flag);
        Assert.Empty(Hits(Root(), alone with { Neighbors = 1 }));
    }

    [Fact]
    public void A_default_selection_off_screen_or_empty_is_a_hole()
    {
        var off = new UiElement { Path = "b", Parent = 0, Rect = new UiRect(3000, 0, 10, 10), IsDefault = true };
        Assert.Contains(UiCheckIssue.NavHole, Kinds(Root(), off));
        var empty = new UiElement { Path = "b", Parent = 0, Rect = new UiRect(0, 0, 0, 10), IsDefault = true };
        Assert.Contains(UiCheckIssue.NavHole, Kinds(Root(), empty));
    }

    [Fact]
    public void A_fallback_sprite_is_missing_art()
    {
        UiCheckHit hit = Assert.Single(Hits(Root(), new UiElement { Path = "b", Parent = 0, Rect = new UiRect(0, 0, 1, 1), ArtFallback = "crown: difficulty_crowns_0" }));
        Assert.Equal(UiCheckIssue.MissingArt, hit.Flag);
    }

    [Fact]
    public void Each_flag_has_the_name_the_dumps_use()
    {
        Assert.Equal("OFFSCREEN", UiCheckRules.Name(UiCheckIssue.Offscreen));
        Assert.Equal("TEXT_OVER", UiCheckRules.Name(UiCheckIssue.TextOver));
        Assert.Equal("AUTOSIZE_MIN", UiCheckRules.Name(UiCheckIssue.AutoSizeMin));
        Assert.Equal("OVERLAP", UiCheckRules.Name(UiCheckIssue.Overlap));
        Assert.Equal("CLIPPED", UiCheckRules.Name(UiCheckIssue.Clipped));
        Assert.Equal("NO_GLYPH", UiCheckRules.Name(UiCheckIssue.NoGlyph));
        Assert.Equal("NAV_HOLE", UiCheckRules.Name(UiCheckIssue.NavHole));
        Assert.Equal("MISSING_ART", UiCheckRules.Name(UiCheckIssue.MissingArt));
        Assert.Equal("NO_TERM", UiCheckRules.Name(UiCheckIssue.NoTerm));
    }

    [Fact]
    public void Counts_are_per_flag()
    {
        Assert.Equal("none", UiCheckRules.Counts(new List<UiCheckHit>()));
        var hits = new List<UiCheckHit> { new(UiCheckIssue.TextOver, "a", ""), new(UiCheckIssue.Offscreen, "b", ""), new(UiCheckIssue.TextOver, "c", "") };
        Assert.Equal("OFFSCREEN 1, TEXT_OVER 2", UiCheckRules.Counts(hits));
    }

    [Fact]
    public void The_flags_table_has_a_row_per_screen_and_flag_and_adds_up_a_screen_seen_twice()
    {
        var screens = new List<KeyValuePair<string, IReadOnlyList<UiCheckHit>>>
        {
            new("sd_browser_list_en_1920x1080", new List<UiCheckHit> { new(UiCheckIssue.TextOver, "a", ""), new(UiCheckIssue.Offscreen, "b", ""), new(UiCheckIssue.TextOver, "c", "") }),
            new("title_title_en_1920x1080", new List<UiCheckHit>()),
            new("sd_browser_list_en_1920x1080", new List<UiCheckHit> { new(UiCheckIssue.Offscreen, "b", "") }),
            new("a\tb", new List<UiCheckHit> { new(UiCheckIssue.NavHole, "x", "") }),
        };

        Assert.Equal(
            "screen\tflag\tcount\n" +
            "sd_browser_list_en_1920x1080\tOFFSCREEN\t2\n" +
            "sd_browser_list_en_1920x1080\tTEXT_OVER\t2\n" +
            "title_title_en_1920x1080\tnone\t0\n" +
            "a b\tNAV_HOLE\t1\n",
            UiCheckRules.FlagsTable(screens));
        Assert.Equal("screen\tflag\tcount\n", UiCheckRules.FlagsTable(new List<KeyValuePair<string, IReadOnlyList<UiCheckHit>>>()));
    }
}
