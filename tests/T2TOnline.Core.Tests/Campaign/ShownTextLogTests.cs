using System;
using T2TOnline.Core.Campaign;
using T2TOnline.Core.Diagnostics;

namespace T2TOnline.Core.Tests.Campaign;

public class ShownTextLogTests
{
    // A custom team's and a custom skater's names, as their players typed them.
    private const string TeamName = "Riley Casey Squad";
    private const string SkaterName = "Sam Alexson";

    private static readonly PlayerTextShape Texts = new(new byte[PlayerTextShape.KeyBytes]);

    private static string Shape(string? text) => Texts.Describe(text);

    /// <summary>The mercy toast as the game makes it for a custom team: "{0} wins by mercy" with the team's name.</summary>
    private static NotificationCopy Mercy() => new()
    {
        Type = 100,
        Timing = 32,
        ObjectKind = ToastObjectKind.Relic,
        ObjectId = "727e0295-7626-4057-8e7d-728a17f20cfd",
        Title = new ToastText(ToastTextKind.Term, "Mercy", "v2ui/mercy/notification/title"),
        Description = new ToastText(ToastTextKind.Recipe, $"{TeamName} wins by mercy",
            Recipe: TextRecipe.Format(TextRecipe.Term("v2ui/mercy/notification/desc"), TextRecipe.TeamName(ToastTeam.Run))),
    };

    /// <summary>The benchwarmer strip relic's toast: "{0} has been stripped", a skater's name in its title, as a recipe and as the host's text.</summary>
    private static NotificationCopy Stripped(bool asRecipe) => new()
    {
        Type = 100,
        Timing = 2,
        SkaterId = "0f2b7633-d0f6-4e06-92c9-5ef01aeb4039",
        Title = asRecipe
            ? new ToastText(ToastTextKind.Recipe, $"{SkaterName} has been stripped",
                Recipe: TextRecipe.Format(TextRecipe.Term("relics/benchwarmer_strip/notification_title"), TextRecipe.Literal(SkaterName)))
            : new ToastText(ToastTextKind.Literal, $"{SkaterName} has been stripped"),
        Description = new ToastText(ToastTextKind.Literal, SkaterName),
        CustomTexts = new[] { new ToastText(ToastTextKind.Literal, SkaterName) },
    };

    [Fact]
    public void The_copys_own_words_never_carry_a_typed_name()
    {
        // NotificationCopy.ToString, ToastText's and TextRecipe's write a text by its length: no caller can log a name.
        Assert.DoesNotContain(TeamName, Mercy().ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(SkaterName, Stripped(asRecipe: true).ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(SkaterName, Stripped(asRecipe: false).ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(SkaterName, Stripped(asRecipe: true).Title!.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(SkaterName, Stripped(asRecipe: true).Title!.Recipe!.ToString(), StringComparison.Ordinal);
        Assert.Equal(ShownTextLog.Toast(Mercy(), HostText.TextShape), Mercy().ToString());
    }

    [Fact]
    public void A_hosts_term_or_object_text_is_never_written_as_it_is()
    {
        // A modified host can send any text as a term's or an object's: the term goes by its key, the object's text by its shape.
        Assert.Equal("(a term) a key of 11 characters", ShownTextLog.Toast(new ToastText(ToastTextKind.Term, "Mercy", "Riley Casey"), Shape));
        Assert.Equal($"(a term: {Shape(SkaterName)})", ShownTextLog.Toast(new ToastText(ToastTextKind.Term, SkaterName), Shape));
        Assert.Equal($"(its object's description: {Shape(SkaterName)})", ShownTextLog.Toast(new ToastText(ToastTextKind.ObjectDescription, SkaterName), Shape));
        var copy = new NotificationCopy { Type = 100, Timing = 2, ObjectKind = ToastObjectKind.Relic, ObjectId = "Riley Casey", SkaterId = SkaterName };
        string line = ShownTextLog.Toast(copy, Shape);
        Assert.Equal("type 100, timing 2, relic a key of 11 characters, skater a key of 11 characters", line);
    }

    [Fact]
    public void A_toast_copy_never_writes_a_typed_name()
    {
        foreach (NotificationCopy copy in new[] { Mercy(), Stripped(asRecipe: true), Stripped(asRecipe: false) })
        {
            string line = ShownTextLog.Toast(copy, Shape);
            Assert.DoesNotContain(TeamName, line, StringComparison.Ordinal);
            Assert.DoesNotContain(SkaterName, line, StringComparison.Ordinal);
            Assert.DoesNotContain("Riley", line, StringComparison.Ordinal);
            Assert.DoesNotContain("Sam", line, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void A_toast_copy_keeps_the_games_own_words_and_says_what_a_recipe_is_made_of()
    {
        Assert.Equal(
            "type 100, timing 32, relic 727e0295-7626-4057-8e7d-728a17f20cfd, title (a term) v2ui/mercy/notification/title, " +
            "description (a recipe) format(term \"v2ui/mercy/notification/desc\", name of the run team)",
            ShownTextLog.Toast(Mercy(), Shape));

        string stripped = ShownTextLog.Toast(Stripped(asRecipe: true), Shape);
        Assert.Contains($"title (a recipe) format(term \"relics/benchwarmer_strip/notification_title\", a text ({Shape(SkaterName)}))", stripped, StringComparison.Ordinal);
        Assert.Contains($"description (the host's text: {Shape(SkaterName)})", stripped, StringComparison.Ordinal);
        Assert.Contains("skater 0f2b7633-d0f6-4e06-92c9-5ef01aeb4039", stripped, StringComparison.Ordinal);
        Assert.EndsWith("1 text(s)", stripped, StringComparison.Ordinal);

        // An object's name and description: the object's id is in the line; the host's text goes by its shape.
        Assert.Equal($"(its object's name: {Shape("Bean Diet")})", ShownTextLog.Toast(new ToastText(ToastTextKind.ObjectName, "Bean Diet"), Shape));
        Assert.Equal("none", ShownTextLog.Toast((ToastText?)null, Shape));
    }

    [Fact]
    public void A_format_a_recipe_starts_from_with_a_letter_in_it_is_a_hosts_text()
    {
        TextRecipe forged = TextRecipe.Format(TextRecipe.Literal($"{SkaterName} {{0}}"), TextRecipe.Term("t"));
        Assert.Equal($"format(a text ({Shape($"{SkaterName} {{0}}")}), term \"t\")", ShownTextLog.Recipe(forged, Shape));
        Assert.Equal($"format(a text ({HostText.TextShape($"{SkaterName} {{0}}")}), term \"t\")", forged.ToString());
    }

    [Fact]
    public void A_recipe_writes_its_literal_texts_as_their_shape_but_a_format_it_starts_from_as_it_is()
    {
        TextRecipe statBonus = TextRecipe.Format(TextRecipe.Literal("\t+{0} {1}"), TextRecipe.Literal("2"), TextRecipe.LocalizedStatName(1));
        // (The tab is a control character: a log line writes it as '?'.)
        Assert.Equal($"format(\"?+{{0}} {{1}}\", a text ({Shape("2")}), stat 1's keyword name)", ShownTextLog.Recipe(statBonus, Shape));

        TextRecipe nested = TextRecipe.Concat(TextRecipe.Literal("<color=green>"), TextRecipe.Format(TextRecipe.Term("t"), TextRecipe.Literal(SkaterName)));
        string line = ShownTextLog.Recipe(nested, Shape);
        Assert.DoesNotContain(SkaterName, line, StringComparison.Ordinal);
        Assert.Equal($"concat(a text ({Shape("<color=green>")}), format(term \"t\", a text ({Shape(SkaterName)})))", line);
        Assert.Equal("none", ShownTextLog.Recipe(null, Shape));
    }

    [Fact]
    public void A_prompt_writes_its_terms_and_only_the_shape_of_anything_else()
    {
        var concede = new PromptData
        {
            Kind = PromptKind.YesNo,
            Title = PromptText.Term("v2/ui/prompt/concede/title"),
            Description = PromptText.Term("v2/ui/prompt/concede/desc"),
            Yes = PromptText.Term("v2/ui/prompt/yes"),
            No = PromptText.Term("v2/ui/prompt/no"),
        };
        Assert.Equal(concede.ToString(), ShownTextLog.Prompt(concede, Shape));

        var cantDelete = new PromptData
        {
            Kind = PromptKind.Dismissable,
            Title = PromptText.Literal($"{SkaterName}?"),
            Description = PromptText.FromRecipe(
                TextRecipe.Format(TextRecipe.Term("v2/player-customization/cant_delete_skater/desc"), TextRecipe.Literal(SkaterName)),
                $"Skater {SkaterName} can't be deleted because they are in an active tournament."),
            Dismiss = PromptText.Term("v2/ui/ok"),
        };
        Assert.DoesNotContain(SkaterName, cantDelete.ToString(), StringComparison.Ordinal);
        string line = ShownTextLog.Prompt(cantDelete, Shape);
        Assert.DoesNotContain(SkaterName, line, StringComparison.Ordinal);
        Assert.Equal($"notice (1 term(s), 1 literal, 1 recipe(s)) title literal ({Shape($"{SkaterName}?")})", line);

        string description = ShownTextLog.Prompt(cantDelete.Description, Shape);
        Assert.DoesNotContain(SkaterName, description, StringComparison.Ordinal);
        Assert.StartsWith("recipe format(term \"v2/player-customization/cant_delete_skater/desc\", a text (", description, StringComparison.Ordinal);
        Assert.Equal("empty", ShownTextLog.Prompt((PromptText?)null, Shape));
    }

    [Fact]
    public void A_shown_text_is_quoted_only_when_it_is_one_of_the_games_terms()
    {
        Assert.Equal("\"Concede?\"", ShownTextLog.Shown(PromptText.Term("v2/ui/prompt/concede/title"), "Concede?", Shape));
        Assert.Equal("\"Concede?\"", ShownTextLog.Shown(PromptText.Localized("v2/ui/prompt/concede/title"), "Concede?", Shape));
        Assert.Equal($"a text of {Shape(SkaterName)}", ShownTextLog.Shown(PromptText.Literal(SkaterName), SkaterName, Shape));
        Assert.Equal($"a text of {Shape(SkaterName)}", ShownTextLog.Shown(null, SkaterName, Shape));
    }

    [Fact]
    public void A_reason_writes_each_quoted_text_as_its_shape()
    {
        // FormatRecipes' "makes ... here" quotes the text the recipe made, which can hold a name.
        string why = $"term v2ui/mercy/notification/desc makes \"{TeamName} wins by mercy\" here, not the shown text";
        string line = ShownTextLog.Reason(why, Shape);
        Assert.DoesNotContain(TeamName, line, StringComparison.Ordinal);
        Assert.Equal($"term v2ui/mercy/notification/desc makes ({Shape($"{TeamName} wins by mercy")}) here, not the shown text", line);
        Assert.Equal("no term of the game has this shape", ShownTextLog.Reason("no term of the game has this shape", Shape));
        Assert.Equal($"an odd ({Shape("quote")})", ShownTextLog.Reason("an odd \"quote", Shape));
        Assert.Equal(string.Empty, ShownTextLog.Reason(null, Shape));
    }

    [Fact]
    public void A_quote_that_is_never_closed_writes_the_rest_as_its_shape()
    {
        // A quoted text can itself hold a quote, which leaves the reason's last quote open: what follows it is still the
        // quoted text, so it is written as its shape too.
        string line = ShownTextLog.Reason($"makes \"{TeamName} wins", Shape);
        Assert.DoesNotContain(TeamName, line, StringComparison.Ordinal);
        Assert.Equal($"makes ({Shape($"{TeamName} wins")})", line);

        line = ShownTextLog.Reason($"the format \"{{0}} wins\" and \"{SkaterName}'s text", Shape);
        Assert.DoesNotContain(SkaterName, line, StringComparison.Ordinal);
        Assert.Equal($"the format ({Shape("{0} wins")}) and ({Shape($"{SkaterName}'s text")})", line);
        Assert.Equal($"({Shape(string.Empty)})", ShownTextLog.Reason("\"", Shape));
    }

    [Fact]
    public void A_failing_shape_writes_a_plain_word_and_never_throws()
    {
        static string Throws(string? text) => throw new InvalidOperationException("no shape");
        Assert.Equal("(the host's text: a text)", ShownTextLog.Toast(new ToastText(ToastTextKind.Literal, SkaterName), Throws));
        Assert.Equal("a text of a text", ShownTextLog.Shown(PromptText.Literal(SkaterName), SkaterName, Throws));
    }
}
