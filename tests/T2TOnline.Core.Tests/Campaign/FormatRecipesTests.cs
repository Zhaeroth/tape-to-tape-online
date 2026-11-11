using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using T2TOnline.Core.Campaign;
using T2TOnline.Core.Serialization;

namespace T2TOnline.Core.Tests.Campaign;

/// <summary>
/// A game's text functions in one language for the formatted texts' tests: real terms with holes (the game's English ones, and
/// a German of the same shape), the game's safe format (holes, and plural forms by the count among the arguments), and the
/// names the toast ops ask for.
/// </summary>
internal sealed class GameTexts : IToastTextFunctions
{
    private static readonly Dictionary<string, Dictionary<string, string>> Terms = new(StringComparer.Ordinal)
    {
        ["en"] = new(StringComparer.Ordinal)
        {
            ["worldmap/notifications/lineup_changed"] = "Line {0} on the ice",
            ["v2ui/mercy/notification/desc"] = "{0} wins by mercy",
            ["relics/relic_freezer/notification_description"] = "{0} was frozen!",
            ["relics/random_upgrade/notification_description"] = "{0} was upgraded to {1}!",
            ["relics/hot_cold/notification_hot"] = "{0} is <color=#ba0000>HOT</color>",
            ["relics/benchwarmer_strip/notification_title"] = "{0} has been stripped",
            ["v2ui/injury_toast/desc"] = "[i2p_One]{0} for this game and {1} other game[i2p_Plural]{0} for this game and {1} other games",
            ["v2/player-customization/cant_delete_skater/desc"] = "Skater {0} can't be deleted because they are in an active tournament.",
            ["v2/team-customization/cant_delete_team/desc"] = "Team {0} can't be deleted because it is in an active tournament.",
            ["twin/a"] = "{0} joins the {1} squad",
            ["twin/b"] = "{0} joins the {1} squad",
        },
        ["de"] = new(StringComparer.Ordinal)
        {
            ["worldmap/notifications/lineup_changed"] = "Linie {0} auf dem Eis",
            ["v2ui/mercy/notification/desc"] = "{0} gewinnt durch die Gnadenregel",
            ["relics/relic_freezer/notification_description"] = "{0} wurde eingefroren!",
            ["relics/random_upgrade/notification_description"] = "{0} wurde auf {1} verbessert!",
            ["relics/hot_cold/notification_hot"] = "{0} ist <color=#ba0000>HEISS</color>",
            ["relics/benchwarmer_strip/notification_title"] = "{0} wurde ausgezogen",
            ["v2ui/injury_toast/desc"] = "[i2p_One]{0} für dieses Spiel und {1} weiteres Spiel[i2p_Plural]{0} für dieses Spiel und {1} weitere Spiele",
            ["v2/player-customization/cant_delete_skater/desc"] = "Spieler {0} kann nicht gelöscht werden, da er an einem aktiven Turnier teilnimmt.",
            ["v2/team-customization/cant_delete_team/desc"] = "Team {0} kann nicht gelöscht werden, da es an einem aktiven Turnier teilnimmt.",
            ["twin/a"] = "{0} tritt der Mannschaft {1} bei",
            ["twin/b"] = "{0} verlässt die Mannschaft {1}",
        },
    };

    private static readonly Dictionary<string, Dictionary<string, string>> Names = new(StringComparer.Ordinal)
    {
        ["en"] = new(StringComparer.Ordinal)
        {
            ["team:Run"] = "Ice Bears",
            ["team:Home"] = "Lumberjacks",
            ["asset:Relic:hot_cake:1"] = "Hot Cake",
            ["asset:Relic:hot_cake:2"] = "Hot Cake II",
            ["asset:Talent:speedy:1"] = "Speedster",
            ["injury:2"] = "Injured Legs",
            ["keystat:1"] = "<keyword=\"speed\">Speed</keyword>",
            ["keystat:0"] = "<keyword=\"overall\">Overall</keyword>",
        },
        ["de"] = new(StringComparer.Ordinal)
        {
            ["team:Run"] = "Eisbären",
            ["team:Home"] = "Holzfäller",
            ["asset:Relic:hot_cake:1"] = "Heißer Kuchen",
            ["asset:Relic:hot_cake:2"] = "Heißer Kuchen II",
            ["asset:Talent:speedy:1"] = "Flitzer",
            ["injury:2"] = "Verletzte Beine",
            ["keystat:1"] = "<keyword=\"speed\">Tempo</keyword>",
            ["keystat:0"] = "<keyword=\"overall\">Gesamt</keyword>",
        },
    };

    private readonly string _language;

    public GameTexts(string language) => _language = language;

    /// <summary>The terms with holes of this language, as the game's term index holds them.</summary>
    public static TextTemplates TemplatesOf(string language)
    {
        var templates = new TextTemplates(language);
        foreach ((string term, string text) in Terms[language])
        {
            _ = templates.Add(term, text);
        }

        return templates;
    }

    public string? Term(string key) => Terms[_language].GetValueOrDefault(key);

    /// <summary>The game's safe format: holes by number, and a plural term's form by the count (the first argument that is a number).</summary>
    public string? Format(string format, IReadOnlyList<string> args)
    {
        if (format.Contains("[i2p_", StringComparison.Ordinal))
        {
            int count = args.Select(a => int.TryParse(a, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? n : (int?)null).FirstOrDefault(n => n != null) ?? 0;
            string[] forms = System.Text.RegularExpressions.Regex.Split(format, @"\[i2p_[A-Za-z]+\]");
            format = forms[count == 1 ? 1 : 2];
        }

        string made = format;
        for (int i = 0; i < args.Count; i++)
        {
            made = made.Replace("{" + i.ToString(CultureInfo.InvariantCulture) + "}", args[i], StringComparison.Ordinal);
        }

        return made;
    }

    public string? ObjectName(ChoiceObjectRef target) => null;

    public string? ObjectDescription(ChoiceObjectRef target, bool includeUpgrade, bool includeStatBonus, string colorHex) => null;

    public string? StatName(int stat, bool description) => null;

    public string? NurseProcedureName(int procedure) => null;

    public string? NurseCardDescription(int? procedure, ChoiceObjectRef talent, int? injury, int injuryGames) => null;

    public string? TeamName(ToastTeam team) => Names[_language].GetValueOrDefault("team:" + team);

    public string? AssetName(ToastObjectKind kind, string id, int level) =>
        Names[_language].GetValueOrDefault($"asset:{kind}:{id}:{level.ToString(CultureInfo.InvariantCulture)}");

    public string? InjuryName(int injury) => Names[_language].GetValueOrDefault("injury:" + injury.ToString(CultureInfo.InvariantCulture));

    public string? LocalizedStatName(int stat) => Names[_language].GetValueOrDefault("keystat:" + stat.ToString(CultureInfo.InvariantCulture));
}

/// <summary>What the host's game knows by name, in English, as the nodes that make each in another language.</summary>
internal sealed class HostNames : ITextNames
{
    private readonly Dictionary<string, TextRecipe> _nodes = new(StringComparer.Ordinal);
    private readonly HashSet<string> _names = new(StringComparer.Ordinal);

    public bool Throws { get; set; }

    public int Asked { get; private set; }

    public HostNames Know(string text, TextRecipe node)
    {
        _nodes[text] = node;
        return this;
    }

    public HostNames Name(string text)
    {
        _names.Add(text);
        return this;
    }

    public TextRecipe? Translated(string text)
    {
        Asked++;
        return Throws ? throw new InvalidOperationException("the game threw") : _nodes.GetValueOrDefault(text);
    }

    public bool IsName(string text) => Throws ? throw new InvalidOperationException("the game threw") : _names.Contains(text);

    /// <summary>The names an English host's game knows.</summary>
    public static HostNames English() => new HostNames()
        .Know("Ice Bears", TextRecipe.TeamName(ToastTeam.Run))
        .Know("Lumberjacks", TextRecipe.TeamName(ToastTeam.Home))
        .Know("Hot Cake", TextRecipe.AssetName(ToastObjectKind.Relic, "hot_cake", 1))
        .Know("Hot Cake II", TextRecipe.AssetName(ToastObjectKind.Relic, "hot_cake", 2))
        .Know("Injured Legs", TextRecipe.InjuryName(2))
        .Know("<keyword=\"speed\">Speed</keyword>", TextRecipe.LocalizedStatName(1))
        .Know("<keyword=\"overall\">Overall</keyword>", TextRecipe.LocalizedStatName(0))
        .Name("Ole Bergström")
        .Name("Sally Kirby");
}

public class FormatRecipesTests
{
    private static readonly TextTemplates English = GameTexts.TemplatesOf("en");

    private static FormatRecipe Build(string shown, ITextNames? names = null, TextTemplates? templates = null)
    {
        FormatRecipe? built = FormatRecipes.TryBuild(shown, templates ?? English, names ?? HostNames.English(), new GameTexts("en"), out string why);
        Assert.True(built != null, $"\"{shown}\": {why}");
        return built!;
    }

    /// <summary>A recipe as it travels: written, read, and made with a German game's functions.</summary>
    private static string? MadeInGerman(TextRecipe recipe)
    {
        var writer = new BitWriter();
        recipe.Write(writer);
        var reader = new BitReader(writer.ToArray());
        Assert.True(TextRecipe.TryRead(ref reader, out TextRecipe? read));
        return read!.Evaluate(new GameTexts("de"));
    }

    public static IEnumerable<object[]> Makers() => new[]
    {
        // Line change (NotificationQueue.QueueLineChangeNotification): a number in a term.
        new object[] { "line change", "Line 2 on the ice", "Linie 2 auf dem Eis", 0, 0 },

        // Mercy: the winning team's display name (a squad's translated name for the run's).
        new object[] { "mercy", "Ice Bears wins by mercy", "Eisbären gewinnt durch die Gnadenregel", 1, 0 },
        new object[] { "mercy, the visitor", "Lumberjacks wins by mercy", "Holzfäller gewinnt durch die Gnadenregel", 1, 0 },

        // The relic freezer: the frozen relic's name.
        new object[] { "relic freezer", "Hot Cake was frozen!", "Heißer Kuchen wurde eingefroren!", 1, 0 },

        // The Improverizer: both names inside the game's colour markup, the second one a level up.
        new object[] { "random upgrade", "<color=green>Hot Cake</color> was upgraded to <color=green>Hot Cake II</color>!", "<color=green>Heißer Kuchen</color> wurde auf <color=green>Heißer Kuchen II</color> verbessert!", 2, 0 },

        // Hot and Cold: a skater's name, which no language translates.
        new object[] { "hot", "Ole Bergström is <color=#ba0000>HOT</color>", "Ole Bergström ist <color=#ba0000>HEISS</color>", 0, 0 },

        // The benchwarmer strip.
        new object[] { "benchwarmer strip", "Sally Kirby has been stripped", "Sally Kirby wurde ausgezogen", 0, 0 },

        // The team bonus and Missing Toe lines: code formats, a number and a stat's keyword name.
        new object[] { "team bonus", "\t+2 <keyword=\"speed\">Speed</keyword>", "\t+2 <keyword=\"speed\">Tempo</keyword>", 1, 0 },
        new object[] { "missing toe", "\t-3 <keyword=\"overall\">Overall</keyword>", "\t-3 <keyword=\"overall\">Gesamt</keyword>", 1, 0 },

        // The injury toast: the injury's name and the other games, in the term's plural form.
        new object[] { "injury, other games", "Injured Legs for this game and 2 other games", "Verletzte Beine für dieses Spiel und 2 weitere Spiele", 1, 0 },
        new object[] { "injury, one other game", "Injured Legs for this game and 1 other game", "Verletzte Beine für dieses Spiel und 1 weiteres Spiel", 1, 0 },
        new object[] { "injury, no other games", "Injured Legs for this game and 0 other games", "Verletzte Beine für dieses Spiel und 0 weitere Spiele", 1, 0 },

        // The customization menus' prompts (item 2): a skater's or a team's name in a term.
        new object[] { "can't delete a skater", "Skater Ole Bergström can't be deleted because they are in an active tournament.", "Spieler Ole Bergström kann nicht gelöscht werden, da er an einem aktiven Turnier teilnimmt.", 0, 0 },
        new object[] { "can't delete a team", "Team Ice Bears can't be deleted because it is in an active tournament.", "Team Eisbären kann nicht gelöscht werden, da es an einem aktiven Turnier teilnimmt.", 1, 0 },
    };

    [Theory]
    [MemberData(nameof(Makers))]
    public void Each_makers_text_gets_a_recipe_a_friend_makes_in_its_own_language(string maker, string shown, string german, int translated, int kept)
    {
        FormatRecipe built = Build(shown);
        Assert.Equal((translated, kept), (built.Translated, built.Kept));
        Assert.Null(built.Recipe.Check());

        // The host's own functions make exactly the shown text; a friend's game, through the wire, the same words in German.
        Assert.Equal(shown, built.Recipe.Evaluate(new GameTexts("en")));
        Assert.Equal(german, MadeInGerman(built.Recipe));
        Assert.Contains("hole(s)", built.ToString(), StringComparison.Ordinal);
        Assert.False(string.IsNullOrEmpty(maker));
    }

    [Fact]
    public void A_recipe_is_the_templates_term_or_code_format_formatted_with_a_node_per_hole()
    {
        FormatRecipe line = Build("Line 2 on the ice");
        Assert.Equal("format(term \"worldmap/notifications/lineup_changed\", \"2\")", line.Recipe.ToString());
        Assert.Equal("term worldmap/notifications/lineup_changed", line.Template);
        Assert.Equal(1, line.Holes);

        FormatRecipe bonus = Build("\t+2 <keyword=\"speed\">Speed</keyword>");
        Assert.Equal(TextOp.Format, bonus.Recipe.Op);
        Assert.Equal("\t+{0} {1}", bonus.Recipe.Args[0].Text);
        Assert.Equal(TextOp.Literal, bonus.Recipe.Args[0].Op);
        Assert.Equal(TextOp.LocalizedStatName, bonus.Recipe.Args[2].Op);

        FormatRecipe upgrade = Build("<color=green>Hot Cake</color> was upgraded to <color=green>Hot Cake II</color>!");
        Assert.Equal(TextOp.Concat, upgrade.Recipe.Args[1].Op);
        Assert.Equal(new[] { TextOp.Literal, TextOp.AssetName, TextOp.Literal }, upgrade.Recipe.Args[1].Args.Select(a => a.Op));
        Assert.Equal(2, upgrade.Recipe.Args[2].Args[1].Number);
    }

    [Fact]
    public void Markup_around_a_word_the_game_doesnt_know_is_kept_whole()
    {
        FormatRecipe built = Build("<color=green>Mystery Relic</color> was frozen!");
        Assert.Equal(TextOp.Literal, built.Recipe.Args[1].Op);
        Assert.Equal("<color=green>Mystery Relic</color>", built.Recipe.Args[1].Text);
        Assert.Equal((0, 1), (built.Translated, built.Kept));
    }

    [Fact]
    public void A_hole_with_words_no_node_makes_is_kept_as_the_hosts_and_counted()
    {
        // The recipe still makes the shown text exactly; the unknown relic stays English on a German friend, and the log says so.
        FormatRecipe built = Build("Mystery Relic was frozen!");
        Assert.Equal((0, 1), (built.Translated, built.Kept));
        Assert.Equal("Mystery Relic wurde eingefroren!", MadeInGerman(built.Recipe));

        // A number or a skater's name is not "words kept".
        Assert.Equal(0, Build("Line 12 on the ice").Kept);
        Assert.Equal(0, Build("Ole Bergström has been stripped").Kept);
        Assert.Equal(1, Build("Riley Unknown has been stripped").Kept);
    }

    [Fact]
    public void Two_terms_that_fit_equally_make_no_recipe_a_term_in_another_language_could_mean_another_thing()
    {
        FormatRecipe? built = FormatRecipes.TryBuild("Ole joins the Ice Bears squad", English, HostNames.English(), new GameTexts("en"), out string why);
        Assert.Null(built);
        Assert.Contains("twin/a", why, StringComparison.Ordinal);
        Assert.Contains("twin/b", why, StringComparison.Ordinal);
        Assert.Contains("fit it equally", why, StringComparison.Ordinal);
    }

    [Fact]
    public void A_text_no_template_fits_makes_no_recipe_and_says_why()
    {
        foreach (string text in new[] { "Free Agent Left", "Line two on the ice!", "Mercy" })
        {
            Assert.Null(FormatRecipes.TryBuild(text, English, HostNames.English(), new GameTexts("en"), out string why));
            Assert.Equal("no term of the game has this shape", why);
        }

        Assert.Null(FormatRecipes.TryBuild(string.Empty, English, HostNames.English(), new GameTexts("en"), out string empty));
        Assert.Equal("it is empty", empty);
    }

    [Fact]
    public void A_recipe_the_hosts_own_functions_dont_reproduce_is_never_sent()
    {
        // The host's game makes another text from the same term: not the shown one.
        var other = new GameTexts("de");
        Assert.Null(FormatRecipes.TryBuild("Line 2 on the ice", English, HostNames.English(), other, out string why));
        Assert.Contains("makes another text here (19 characters), not the shown one", why, StringComparison.Ordinal);
        Assert.DoesNotContain("Linie", why, StringComparison.Ordinal); // the made text holds the shown one's arguments: never in a reason

        // A name the host's game knows but can't make (its relic isn't in the lists): nothing, not half a text.
        var names = HostNames.English().Know("Ghost Relic", TextRecipe.AssetName(ToastObjectKind.Relic, "ghost", 1));
        Assert.Null(FormatRecipes.TryBuild("Ghost Relic was frozen!", English, names, new GameTexts("en"), out why));
        Assert.Contains("makes nothing here", why, StringComparison.Ordinal);

        // A term the host's game lacks: the same.
        var terms = new TextTemplates("en");
        Assert.True(terms.Add("gone/term", "Gone {0} for good"));
        Assert.Null(FormatRecipes.TryBuild("Gone Ole for good", terms, HostNames.English(), new GameTexts("en"), out why));
        Assert.Contains("makes nothing here", why, StringComparison.Ordinal);
    }

    [Fact]
    public void A_hole_too_long_for_a_recipe_makes_no_recipe()
    {
        string name = new('x', TextRecipe.MaxTextBytes + 1);
        Assert.Null(FormatRecipes.TryBuild($"{name} was frozen!", English, HostNames.English(), new GameTexts("en"), out string why));
        Assert.Contains("out of its limits", why, StringComparison.Ordinal);

        // At the limit it goes.
        Assert.NotNull(FormatRecipes.TryBuild($"{new string('x', TextRecipe.MaxTextBytes)} was frozen!", English, HostNames.English(), new GameTexts("en"), out _));
    }

    [Fact]
    public void A_number_is_never_looked_up_even_when_a_term_has_that_text()
    {
        var names = HostNames.English().Know("2", TextRecipe.Term("some/term_that_says_two"));
        FormatRecipe built = Build("Line 2 on the ice", names);
        Assert.Equal(TextOp.Literal, built.Recipe.Args[1].Op);
        Assert.Equal("2", built.Recipe.Args[1].Text);
        Assert.Equal((0, 0), (built.Translated, built.Kept));
    }

    [Fact]
    public void A_names_lookup_that_throws_leaves_the_hole_the_hosts_text()
    {
        var names = HostNames.English();
        names.Throws = true;
        FormatRecipe built = Build("Hot Cake was frozen!", names);
        Assert.Equal(TextOp.Literal, built.Recipe.Args[1].Op);
        Assert.Equal((0, 1), (built.Translated, built.Kept));
        Assert.True(names.Asked > 0);
    }

    [Fact]
    public void The_recipe_of_a_longer_template_wins_over_a_shorter_one_that_also_fits()
    {
        var templates = new TextTemplates("en");
        Assert.True(templates.Add("a/short", "{0} frozen!"));
        Assert.True(templates.Add("relics/relic_freezer/notification_description", "{0} was frozen!"));
        FormatRecipe? built = FormatRecipes.TryBuild("Hot Cake was frozen!", templates, HostNames.English(), new GameTexts("en"), out string why);
        Assert.True(built != null, why);
        Assert.Equal("term relics/relic_freezer/notification_description", built!.Template);
    }

    [Fact]
    public void A_term_in_each_plural_form_is_tried_until_one_makes_the_text()
    {
        // "0 other games" and "1 other game" are different forms of one term; each text finds its own.
        Assert.Equal("Verletzte Beine für dieses Spiel und 1 weiteres Spiel", MadeInGerman(Build("Injured Legs for this game and 1 other game").Recipe));
        Assert.Equal("Verletzte Beine für dieses Spiel und 5 weitere Spiele", MadeInGerman(Build("Injured Legs for this game and 5 other games").Recipe));
    }

    [Fact]
    public void Arguments_must_not_be_null()
    {
        Assert.Throws<ArgumentNullException>(() => FormatRecipes.TryBuild(null!, English, HostNames.English(), new GameTexts("en"), out _));
        Assert.Throws<ArgumentNullException>(() => FormatRecipes.TryBuild("x", null!, HostNames.English(), new GameTexts("en"), out _));
        Assert.Throws<ArgumentNullException>(() => FormatRecipes.TryBuild("x", English, null!, new GameTexts("en"), out _));
        Assert.Throws<ArgumentNullException>(() => FormatRecipes.TryBuild("x", English, HostNames.English(), null!, out _));
    }
}
