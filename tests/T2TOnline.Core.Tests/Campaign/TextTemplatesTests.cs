using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using T2TOnline.Core.Campaign;

namespace T2TOnline.Core.Tests.Campaign;

/// <summary>The game's text templates (terms with holes, and two formats its code carries) and matching a shown text back to one.</summary>
public class TextTemplatesTests
{
    private static TextTemplates English()
    {
        var templates = new TextTemplates("English");
        Assert.True(templates.Add("worldmap/notifications/lineup_changed", "Line {0} on the ice"));
        Assert.True(templates.Add("v2ui/mercy/notification/desc", "{0} wins by mercy"));
        Assert.True(templates.Add("relics/random_upgrade/notification_description", "{0} was upgraded to {1}!"));
        Assert.True(templates.Add("v2ui/injury_toast/desc", "[i2p_One]{0} for this game and {1} other game[i2p_Plural]{0} for this game and {1} other games"));
        return templates;
    }

    [Fact]
    public void A_term_with_holes_is_a_template_and_its_text_is_matched_back_to_it()
    {
        TextTemplates templates = English();
        TextTemplateMatch match = Assert.Single(templates.Match("Line 3 on the ice"));
        Assert.Equal("worldmap/notifications/lineup_changed", match.Template.Term);
        Assert.Equal(new[] { "3" }, match.Holes);
        Assert.Equal(1, match.Template.Holes);
        Assert.Equal("term worldmap/notifications/lineup_changed", match.Template.ToString());

        // Holes take names with spaces and punctuation; a text that isn't the template's, or only starts like it, fits nothing.
        Assert.Equal(new[] { "Ice Bears (Home)" }, Assert.Single(templates.Match("Ice Bears (Home) wins by mercy")).Holes);
        Assert.Empty(templates.Match("Line 3 on the ice!"));
        Assert.Empty(templates.Match("Line on the ice"));
        Assert.Empty(templates.Match("Free Agent Left"));
        Assert.Empty(templates.Match(string.Empty));
        Assert.Empty(templates.Match(null));

        // Two holes, in order.
        Assert.Equal(new[] { "Hot Cake", "Hot Cake II" }, Assert.Single(templates.Match("Hot Cake was upgraded to Hot Cake II!")).Holes);
        Assert.Equal(5, templates.Count - TextTemplates.CodeFormats.Count); // four terms, the injury toast's in two plural forms
    }

    [Fact]
    public void A_text_with_plural_forms_is_one_template_a_form_with_the_terms_key()
    {
        TextTemplates templates = English();
        TextTemplateMatch plural = Assert.Single(templates.Match("Injured Legs for this game and 2 other games"));
        Assert.Equal(new[] { "Injured Legs", "2" }, plural.Holes);
        Assert.Equal("v2ui/injury_toast/desc", plural.Template.Term);
        Assert.Contains("[i2p_One]", plural.Template.Text, StringComparison.Ordinal);

        TextTemplateMatch one = Assert.Single(templates.Match("Injured Legs for this game and 1 other game"));
        Assert.Equal(new[] { "Injured Legs", "1" }, one.Holes);
        Assert.Equal(plural.Template.Key, one.Template.Key);
        Assert.NotSame(plural.Template, one.Template);
    }

    [Fact]
    public void The_games_two_code_formats_are_always_there_and_fit_a_number_and_a_stat_name()
    {
        var templates = new TextTemplates("English");
        Assert.Equal(TextTemplates.CodeFormats.Count, templates.Count);
        // The tab-and-plus format fits it first (more of the text is its own); the plain tab format fits it too, with "+2" as a hole.
        IReadOnlyList<TextTemplateMatch> both = templates.Match("\t+2 <keyword=\"speed\">Speed</keyword>");
        Assert.Equal(2, both.Count);
        TextTemplateMatch plus = both[0];
        Assert.Null(plus.Template.Term);
        Assert.Equal(new[] { "2", "<keyword=\"speed\">Speed</keyword>" }, plus.Holes);
        Assert.Equal("the format \"?+{0} {1}\"", plus.Template.ToString()); // the log shows a tab as ?
        Assert.Equal("code:\t+{0} {1}", plus.Template.Key);
        Assert.Equal(new[] { "-3", "<keyword=\"overall\">Overall</keyword>" }, Assert.Single(templates.Match("\t-3 <keyword=\"overall\">Overall</keyword>")).Holes);
        Assert.Empty(templates.Match("+2 Speed"));
    }

    [Fact]
    public void The_most_specific_template_comes_first_and_equal_ones_all_come()
    {
        var templates = new TextTemplates("English");
        Assert.True(templates.Add("a/short", "{0} was upgraded"));
        Assert.True(templates.Add("a/long", "{0} was upgraded to {1}!"));
        Assert.True(templates.Add("a/long_twin", "{0} was upgraded to {1}!"));
        IReadOnlyList<TextTemplateMatch> matches = templates.Match("Hot Cake was upgraded to Hot Cake II!");
        Assert.Equal(new[] { "a/long", "a/long_twin" }, matches.Take(2).Select(m => m.Template.Term));
        Assert.Equal("a/long", matches[0].Template.Term); // the game's order among equals
        Assert.True(matches.Count <= TextTemplates.MaxMatches);
    }

    [Fact]
    public void A_hole_used_twice_matches_the_same_text_both_times()
    {
        var templates = new TextTemplates("English");
        Assert.True(templates.Add("a/echo", "{0} meets {0} and {1}"));
        Assert.Equal(new[] { "Ole", "Sally" }, Assert.Single(templates.Match("Ole meets Ole and Sally")).Holes);
        Assert.Empty(templates.Match("Ole meets Riley and Sally"));
    }

    [Fact]
    public void Special_characters_in_a_template_are_literal_and_a_line_break_in_a_hole_is_fine()
    {
        var templates = new TextTemplates("English");
        Assert.True(templates.Add("a/regex", "Score (a+b)? [{0}] $ {1}. \\done"));
        Assert.Equal(new[] { "3", "4" }, Assert.Single(templates.Match("Score (a+b)? [3] $ 4. \\done")).Holes);
        Assert.Empty(templates.Match("Score (a+b)? [3] $ 4X \\done"));
        Assert.True(templates.Add("a/lines", "First {0}\nthen {1}"));
        Assert.Equal(new[] { "a\nb", "c" }, Assert.Single(templates.Match("First a\nb\nthen c")).Holes);
    }

    public static IEnumerable<object[]> NotTemplates()
    {
        yield return new object[] { "no hole", "Free Agent Left" };
        yield return new object[] { "a named parameter", "New {injury}" };
        yield return new object[] { "an I2 parameter", "{[FULLNAME0]}: {[STATS0]}" };
        yield return new object[] { "a format specifier", "Total {0:N0} games" };
        yield return new object[] { "an escaped brace", "{{0}} games of {0}" };
        yield return new object[] { "a lone brace", "games of {0 and more" };
        yield return new object[] { "a closing brace", "games } of {0}" };
        yield return new object[] { "two holes side by side", "Games {0}{1} played" };
        yield return new object[] { "holes from 1", "Games {1} played" };
        yield return new object[] { "a gap in the holes", "Games {0} and {2} played" };
        yield return new object[] { "too few letters", "{0}/{1}" };
        yield return new object[] { "only two letters", "{0} to {1}" };
        yield return new object[] { "a dash between holes", "{0} - {1}" };
        yield return new object[] { "too many holes", "Games {0} {1} {2} {3} {4} {5} {6} {7} {8} played" };
        yield return new object[] { "a hole number past 7", "Games {8} played" };
        yield return new object[] { "a hole number of three digits", "Games {123} played" };
        yield return new object[] { "over 512 characters", "Games {0} " + new string('x', TextTemplate.MaxChars) };
    }

    [Theory]
    [MemberData(nameof(NotTemplates))]
    public void A_text_that_isnt_a_template_is_not_kept(string what, string text)
    {
        var templates = new TextTemplates("English");
        Assert.False(templates.Add("a/term", text), what);
        Assert.Equal(TextTemplates.CodeFormats.Count, templates.Count);
        Assert.Null(TextTemplate.Parse("a/term", text));
    }

    [Fact]
    public void Nothing_is_kept_without_a_term_or_a_text_or_past_the_cap()
    {
        var templates = new TextTemplates("English");
        Assert.False(templates.Add(null, "Games {0} played"));
        Assert.False(templates.Add(string.Empty, "Games {0} played"));
        Assert.False(templates.Add("a/term", null));
        Assert.False(templates.Add("a/term", string.Empty));
        Assert.Equal("English", templates.Language);
        Assert.Equal("English", new TextTemplates("English").Language);
        Assert.Equal(string.Empty, new TextTemplates(null!).Language);

        for (int i = 0; i < TextTemplates.MaxTemplates; i++)
        {
            if (!templates.Add("a/" + i, "Games " + i + " {0} played"))
            {
                break;
            }
        }

        Assert.Equal(TextTemplates.MaxTemplates, templates.Count);
        Assert.False(templates.Add("a/more", "Games {0} played more"));
    }

    [Fact]
    public void A_code_format_may_have_no_letters_and_a_template_of_a_term_may_not()
    {
        Assert.NotNull(TextTemplate.Parse(null, "\t{0} {1}"));
        Assert.NotNull(TextTemplate.Parse(null, "{0}/{1}"));
        Assert.Null(TextTemplate.Parse("a/term", "{0}/{1}"));
        Assert.Null(TextTemplate.Parse(null, "no holes at all"));
    }

    [Fact]
    public void A_long_text_against_many_holes_never_takes_long()
    {
        var templates = new TextTemplates("English");
        Assert.True(templates.Add("a/many", "a {0} b {1} c {2} d {3} e {4} f {5} g {6} h {7} i"));
        string hostile = string.Concat(Enumerable.Repeat("a b c d e f g h ", 40)); // every literal is there, none in the right order at the end
        var clock = Stopwatch.StartNew();
        _ = templates.Match(hostile);
        Assert.True(clock.ElapsedMilliseconds < 2000, $"{clock.ElapsedMilliseconds} ms");
    }
}
