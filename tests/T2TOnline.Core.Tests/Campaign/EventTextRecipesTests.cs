using System;
using System.Collections.Generic;
using System.Linq;
using T2TOnline.Core.Campaign;

namespace T2TOnline.Core.Tests.Campaign;

/// <summary>
/// The producers' table: each host text made by the game's own construction (here in a fake "en") is matched to exactly that recipe, and a friend makes it in its own language.
/// </summary>
public class EventTextRecipesTests
{
    private const string Skater = "Ole Bergström";

    private static readonly EventObject Talent = new() { Kind = EventObjectKind.Talent, Id = "speedy", ClassName = "Speedy", Level = 1 };
    private static readonly EventObject Ability = new() { Kind = EventObjectKind.Ability, Id = "slapshot", ClassName = "Slapshot", Level = 1, Owner = EventObjectOwner.Skater, OwnerId = "f-1" };

    private static readonly ChoiceObjectRef O1 = ChoiceObjectRef.Object1;
    private static readonly ChoiceObjectRef O2 = ChoiceObjectRef.Object2;

    /// <summary>A choice whose texts are what <paramref name="button"/> and <paramref name="texts"/> make in "en" (a null recipe: that text as given in <paramref name="plain"/>).</summary>
    private static EventChoiceData Made(EventChoiceData shape, TextRecipe? button, IReadOnlyList<TextRecipe?>? texts = null, IReadOnlyList<string?>? plain = null)
    {
        var en = new FakeTexts("en");
        return new EventChoiceData
        {
            NodeType = shape.NodeType,
            ChoiceType = shape.ChoiceType,
            SkaterId = shape.SkaterId,
            FreeAgent = shape.FreeAgent,
            Object1 = shape.Object1,
            Object2 = shape.Object2,
            StatBonuses = shape.StatBonuses,
            TranslatedButtonName = button?.Evaluate(en) ?? shape.TranslatedButtonName,
            Texts = texts?.Select((r, i) => r?.Evaluate(en) ?? plain?[i]).ToList() ?? plain,
        };
    }

    private static void AssertSame(TextRecipe? expected, TextRecipe? found)
    {
        Assert.Equal(expected == null, found == null);
        if (expected != null)
        {
            Assert.Equal(TextRecipeTests.Encoded(expected), TextRecipeTests.Encoded(found!));
        }
    }

    /// <summary>Host in "en": the recipes found are exactly <paramref name="button"/> and <paramref name="texts"/>; then a friend in "de" makes the texts in German.</summary>
    private static EventChoiceData HostThenFriend(string eventClass, EventChoiceData choice, TextRecipe? button, IReadOnlyList<TextRecipe?>? texts, NurseOffer? nurse = null)
    {
        var tally = new EventTextTally();
        EventChoiceData sent = EventTextRecipes.WithRecipes(eventClass, choice, new FakeTexts("en"), Skater, nurse, tally);
        AssertSame(button, sent.ButtonRecipe);
        for (int i = 0; texts != null && i < texts.Count; i++)
        {
            AssertSame(texts[i], sent.TextRecipes?[i]);
        }

        Assert.Empty(tally.Untranslated);
        var de = new FakeTexts("de");
        var friend = new EventTextTally();
        EventChoiceData here = EventTextRecipes.Localize(sent, de, friend);
        Assert.Equal(button?.Evaluate(de) ?? choice.TranslatedButtonName, here.TranslatedButtonName);
        for (int i = 0; texts != null && i < texts.Count; i++)
        {
            Assert.Equal(texts[i]?.Evaluate(de) ?? choice.Texts![i], here.Texts![i]);
        }

        Assert.Empty(friend.Failed);
        Assert.Equal((button != null ? 1 : 0) + (texts?.Count(t => t != null) ?? 0), friend.Made);
        return sent;
    }

    [Fact]
    public void Fan_number_1s_power_up_and_ability_choices_are_its_name_and_description_format()
    {
        TextRecipe button = TextRecipe.Format(TextRecipe.Term(EventTextRecipes.FanNameDescription), TextRecipe.ObjectName(O1),
            TextRecipe.ObjectDescription(O1, false, false, EventTextRecipes.DescriptionColor));
        var texts = new[] { TextRecipe.ObjectName(O1) };
        HostThenFriend("FanNumber1Event", Made(new EventChoiceData { NodeType = 7, ChoiceType = 2, SkaterId = "f-1", Object1 = Talent }, button, texts), button, texts);
    }

    [Fact]
    public void Fan_number_1s_upgrades_are_the_skater_and_the_current_power_ups_name()
    {
        TextRecipe button = TextRecipe.Format(TextRecipe.Term(EventTextRecipes.FanUpgrade), TextRecipe.Literal(Skater), TextRecipe.ObjectName(O2));
        var texts = new[] { TextRecipe.ObjectName(O2) };
        HostThenFriend("FanNumber1Event", Made(new EventChoiceData { NodeType = 7, ChoiceType = 4, SkaterId = "f-1", Object1 = Ability, Object2 = Ability }, button, texts), button, texts);
    }

    [Fact]
    public void Stat_choices_are_the_stats_name_and_a_number()
    {
        // Fan Number 1's: a term on the button, the stat's name, "+2" (the same in every language: no recipe).
        var bonus = new StatBonusSet(false, new[] { new StatBonusData(1, 3, 0, 2, false, false) });
        TextRecipe button = TextRecipe.Term(EventTextRecipes.FanTraining);
        var texts = new TextRecipe?[] { TextRecipe.StatName(3, false), null };
        EventChoiceData fan = Made(new EventChoiceData { NodeType = 7, ChoiceType = 21, SkaterId = "f-1", StatBonuses = bonus }, button, texts, new string?[] { null, "+2" });
        HostThenFriend("FanNumber1Event", fan, button, texts);

        // A training's: no button; its stat's name found among every stat when its bonus is another's.
        var other = new StatBonusSet(false, new[] { new StatBonusData(1, 2, 0, 1, false, false) });
        var trainingTexts = new TextRecipe?[] { TextRecipe.StatName(4, false), null };
        EventChoiceData training = Made(new EventChoiceData { NodeType = 14, ChoiceType = 14, SkaterId = "f-1", StatBonuses = other }, null, trainingTexts, new string?[] { null, "+1" });
        var tally = new EventTextTally();
        EventChoiceData sent = EventTextRecipes.WithRecipes("StatsTraining", training, new FakeTexts("en"), Skater, null, tally);
        AssertSame(trainingTexts[0], sent.TextRecipes![0]);
        Assert.Null(sent.TextRecipes[1]);
        Assert.Equal((1, 1, 0), (tally.Recipes, tally.Neutral, tally.Untranslated.Count));
    }

    [Fact]
    public void A_general_managers_agent_texts_are_its_talents_names_and_descriptions()
    {
        // The game skips a bench quest talent (here talent 2 of 3): the texts are talents 1 and 3's.
        FreeAgentData agent = EventChoiceSetTests.Agent();
        var texts = new[]
        {
            TextRecipe.ObjectName(ChoiceObjectRef.AgentTalent(0)), TextRecipe.ObjectDescription(ChoiceObjectRef.AgentTalent(0), false, false, EventTextRecipes.DescriptionColor),
            TextRecipe.ObjectName(ChoiceObjectRef.AgentTalent(2)), TextRecipe.ObjectDescription(ChoiceObjectRef.AgentTalent(2), false, false, EventTextRecipes.DescriptionColor),
        };
        EventChoiceData choice = Made(new EventChoiceData { NodeType = 8, ChoiceType = 1, FreeAgent = agent, TranslatedButtonName = Skater }, null, texts);
        var tally = new EventTextTally();
        HostThenFriend("GeneralManagerEvent", choice, null, texts);
        _ = EventTextRecipes.WithRecipes("GeneralManagerEvent", choice, new FakeTexts("en"), Skater, null, tally);
        Assert.Equal((4, 1, 0), (tally.Recipes, tally.Neutral, tally.Untranslated.Count)); // the button is the agent's name
    }

    [Fact]
    public void A_nurses_cards_are_the_procedures_name_and_card_description()
    {
        var offer = new NurseOffer(1, 3);
        TextRecipe button = TextRecipe.NurseProcedureName(5);
        var texts = new[] { TextRecipe.NurseCardDescription(5, O1, 1, 3) };
        HostThenFriend(EventTextRecipes.NurseClass, Made(new EventChoiceData { NodeType = 5, ChoiceType = 27, Object1 = Talent }, button, texts), button, texts, offer);

        // No procedure: no button, the card without one.
        var none = new[] { TextRecipe.NurseCardDescription(null, ChoiceObjectRef.None, 1, 3) };
        HostThenFriend(EventTextRecipes.NurseClass, Made(new EventChoiceData { NodeType = 5, ChoiceType = 29 }, null, none), null, none, offer);

        // The offer couldn't be read on the host: the card as the game makes it without one still matches when it is that.
        var bare = new[] { TextRecipe.NurseCardDescription(2, ChoiceObjectRef.None, null, 0) };
        HostThenFriend(EventTextRecipes.NurseClass, Made(new EventChoiceData { NodeType = 5, ChoiceType = 24 }, TextRecipe.NurseProcedureName(2), bare), TextRecipe.NurseProcedureName(2), bare);
    }

    [Fact]
    public void The_blademasters_locked_ability_is_its_title_format_and_the_abilitys_name()
    {
        TextRecipe button = TextRecipe.Format(TextRecipe.Term(EventTextRecipes.BlademasterAbility), TextRecipe.ObjectName(O1), TextRecipe.Literal(Skater));
        var texts = new[] { TextRecipe.ObjectName(O1) };
        HostThenFriend("BlademasterEvent", Made(new EventChoiceData { NodeType = 6, ChoiceType = 2, SkaterId = "f-1", Object1 = Ability, Object2 = Ability }, button, texts), button, texts);
    }

    [Fact]
    public void The_blademasters_upgrades_are_the_games_english_around_the_current_ones_name()
    {
        // A talent or ability: "Upgrade: " + the skater + "'s " + the current one's name; the same name as its card's text.
        TextRecipe button = TextRecipe.Concat(TextRecipe.Literal(EventTextRecipes.BlademasterUpgrade), TextRecipe.Literal(Skater),
            TextRecipe.Literal(EventTextRecipes.BlademasterOwner), TextRecipe.ObjectName(O2));
        var texts = new[] { TextRecipe.ObjectName(O2) };
        EventChoiceData sent = HostThenFriend("BlademasterEvent", Made(new EventChoiceData { NodeType = 6, ChoiceType = 4, SkaterId = "f-1", Object1 = Talent, Object2 = Talent }, button, texts), button, texts);
        Assert.Equal("Upgrade: " + Skater + "'s en:name:object 2", sent.TranslatedButtonName);

        // A relic: no skater, "Upgrade: " + its name.
        var relic = new EventObject { Kind = EventObjectKind.Relic, Id = "tong", ClassName = "Tong", Level = 1, Owner = EventObjectOwner.Team };
        TextRecipe relicButton = TextRecipe.Concat(TextRecipe.Literal(EventTextRecipes.BlademasterUpgrade), TextRecipe.ObjectName(O2));
        EventChoiceData shape = Made(new EventChoiceData { NodeType = 6, ChoiceType = 7, Object1 = relic, Object2 = relic }, relicButton, texts);
        var tally = new EventTextTally();
        EventChoiceData relicSent = EventTextRecipes.WithRecipes("BlademasterEvent", shape, new FakeTexts("en"), null, null, tally);
        AssertSame(relicButton, relicSent.ButtonRecipe);
        AssertSame(texts[0], relicSent.TextRecipes![0]);
        Assert.Equal((2, 0), (tally.Recipes, tally.Untranslated.Count));
    }

    [Fact]
    public void A_chaos_cards_button_and_text_are_its_relics_name()
    {
        var relic = new EventObject { Kind = EventObjectKind.Relic, Id = "chaos-x", ClassName = "ChaosX", Level = 1, Chaos = true };
        TextRecipe name = TextRecipe.ObjectName(O1);
        HostThenFriend("ChaosEvent3Choice", Made(new EventChoiceData { NodeType = 9, ChoiceType = 7, Object1 = relic }, name, new[] { name }), name, new[] { name });
    }

    [Fact]
    public void An_empty_text_made_here_keeps_the_hosts()
    {
        // A term this PC's language leaves empty (the game's GetTranslation gives "" for one): the host's text, not nothing.
        var choice = new EventChoiceData { NodeType = 6, ChoiceType = 4, TranslatedButtonName = "Host", ButtonRecipe = TextRecipe.Literal(string.Empty) };
        var tally = new EventTextTally();
        Assert.Equal("Host", EventTextRecipes.Localize(choice, new FakeTexts("de"), tally).TranslatedButtonName);
        Assert.Equal((0, 1), (tally.Made, tally.Failed.Count));
    }

    [Fact]
    public void A_text_no_recipe_makes_goes_as_the_hosts_and_is_noted_and_names_and_numbers_need_none()
    {
        var choice = new EventChoiceData
        {
            NodeType = 9,
            ChoiceType = 3,
            Object1 = Talent,
            TranslatedButtonName = "Spezialangebot",
            Texts = new[] { Skater, "+3 / 12%", "Nur heute", null, string.Empty },
        };
        var tally = new EventTextTally();
        EventChoiceData sent = EventTextRecipes.WithRecipes("CoachEvent", choice, new FakeTexts("en"), Skater, null, tally);
        Assert.Null(sent.ButtonRecipe);
        Assert.Null(sent.TextRecipes);
        Assert.Equal((0, 2), (tally.Recipes, tally.Neutral));
        Assert.Equal(new[] { ("button", "Spezialangebot"), ("text 3", "Nur heute") }, tally.Untranslated);
        Assert.Contains("2 names or numbers, 2 kept", tally.ToString(), StringComparison.Ordinal);

        Assert.True(EventTextRecipes.IsNeutral("+2", null));
        Assert.True(EventTextRecipes.IsNeutral(" Ole Bergström ", Skater));
        Assert.True(EventTextRecipes.IsNeutral(string.Empty, null));
        Assert.False(EventTextRecipes.IsNeutral("Speed", Skater));
        Assert.False(EventTextRecipes.IsNeutral("Ole", Skater));
    }

    [Fact]
    public void Each_candidate_is_made_once_per_choice()
    {
        TextRecipe name = TextRecipe.ObjectName(O2);
        var shape = new EventChoiceData { NodeType = 7, ChoiceType = 4, Object1 = Talent, Object2 = Talent };
        var once = new FakeTexts("en");
        _ = EventTextRecipes.WithRecipes("CoachEvent", Made(shape, null, new[] { name }), once, null, null, new EventTextTally());
        var four = new FakeTexts("en");
        _ = EventTextRecipes.WithRecipes("CoachEvent", Made(shape, null, new[] { name, name, name, name }), four, null, null, new EventTextTally());
        Assert.Equal(once.Calls, four.Calls);
        Assert.Equal(2, once.Calls); // object 1's name (no match), then object 2's
    }

    [Fact]
    public void A_recipe_that_makes_nothing_here_is_noted_without_the_name_it_carries()
    {
        // A button's "... for <skater>" carries the skater's name as a literal, and a custom skater's is typed by its player:
        // what a friend's log says of a recipe that made nothing is what it is built from, never the name.
        var choice = new EventChoiceData
        {
            NodeType = 6,
            ChoiceType = 4,
            Object1 = Talent,
            TranslatedButtonName = $"New: Speedy for {Skater}",
            ButtonRecipe = TextRecipe.Format(TextRecipe.Term("v2ui/missing"), TextRecipe.ObjectName(O1), TextRecipe.Literal(Skater)),
        };
        var de = new FakeTexts("de");
        de.MissingTerms.Add("v2ui/missing");
        var tally = new EventTextTally();
        EventChoiceData here = EventTextRecipes.Localize(choice, de, tally);
        Assert.Equal($"New: Speedy for {Skater}", here.TranslatedButtonName);
        string noted = Assert.Single(tally.Failed);
        Assert.Equal("format(term \"v2ui/missing\", name of object 1, a text (13 characters))", noted);
        Assert.DoesNotContain("Ole", noted, StringComparison.Ordinal);
    }

    [Fact]
    public void A_recipe_that_makes_nothing_here_keeps_the_hosts_text()
    {
        var choice = new EventChoiceData
        {
            NodeType = 6,
            ChoiceType = 4,
            Object1 = Talent,
            Object2 = Talent,
            TranslatedButtonName = "Upgrade",
            Texts = new[] { "Speedy", "Speedy II" },
            ButtonRecipe = TextRecipe.Term("v2ui/missing"),
            TextRecipes = new[] { TextRecipe.ObjectName(O1), TextRecipe.ObjectName(O2) },
        };
        var de = new FakeTexts("de");
        de.Missing.Add(O2);
        de.MissingTerms.Add("v2ui/missing");
        var tally = new EventTextTally();
        EventChoiceData here = EventTextRecipes.Localize(choice, de, tally);
        Assert.Equal("Upgrade", here.TranslatedButtonName);
        Assert.Equal(new[] { "de:name:object 1", "Speedy II" }, here.Texts);
        Assert.Equal(1, tally.Made);
        Assert.Equal(new[] { "term \"v2ui/missing\"", "name of object 2" }, tally.Failed);
        Assert.Contains("1 made here, 2 kept", tally.ToString(), StringComparison.Ordinal);

        // Every other field is the host's, recipes included (they're the host's).
        Assert.Same(choice.Object1, here.Object1);
        Assert.Same(choice.TextRecipes, here.TextRecipes);

        // A choice without recipes is the host's as it is.
        var plain = new EventChoiceData { Texts = new[] { "x" } };
        Assert.Same(plain, EventTextRecipes.Localize(plain, de, tally));
    }

    [Fact]
    public void A_hosts_set_with_recipes_goes_whole_and_a_friend_makes_its_texts_in_its_own_language()
    {
        TextRecipe button = TextRecipe.Format(TextRecipe.Term(EventTextRecipes.FanNameDescription), TextRecipe.ObjectName(O1),
            TextRecipe.ObjectDescription(O1, false, false, EventTextRecipes.DescriptionColor));
        EventChoiceData choice = Made(new EventChoiceData { NodeType = 7, ChoiceType = 2, SkaterId = "f-1", Object1 = Talent }, button, new[] { TextRecipe.ObjectName(O1) });
        EventChoiceData sent = EventTextRecipes.WithRecipes("FanNumber1Event", choice, new FakeTexts("en"), Skater, null, new EventTextTally());
        var set = new EventChoiceSet { EventClass = "FanNumber1Event", EventName = "FanNumber1", NodeType = 7, Choices = new[] { sent } };

        Assert.True(MenuAttachment.EventChoices(4, set).TryGetEventChoices(out EventChoiceSet? read));
        EventChoiceData here = EventTextRecipes.Localize(read!.Choices[0], new FakeTexts("de"), new EventTextTally());
        Assert.StartsWith("<de:term:" + EventTextRecipes.FanNameDescription, here.TranslatedButtonName, StringComparison.Ordinal);
        Assert.Equal(new[] { "de:name:object 1" }, here.Texts);

        // Without its recipes it's every text as the host's.
        EventChoiceSet plain = set.WithoutRecipes();
        Assert.Null(plain.Choices[0].ButtonRecipe);
        Assert.Null(plain.Choices[0].TextRecipes);
        Assert.Equal(sent.Texts, plain.Choices[0].Texts);
        Assert.Equal((set.EventClass, set.EventName, set.NodeType), (plain.EventClass, plain.EventName, plain.NodeType));
    }

    [Fact]
    public void The_candidates_follow_the_producers_order_and_name_only_what_the_choice_has()
    {
        Assert.Empty(EventTextRecipes.ButtonCandidates("CoachEvent", new EventChoiceData(), Skater));
        Assert.Equal(EventTextRecipes.NurseProcedures, EventTextRecipes.ButtonCandidates(EventTextRecipes.NurseClass, new EventChoiceData(), null).Count);

        // No upgrade or ability title without a skater's name; no object's recipes without the object.
        Assert.Equal(3, EventTextRecipes.ButtonCandidates("FanNumber1Event", new EventChoiceData { Object2 = Talent }, null).Count);
        Assert.Empty(EventTextRecipes.ButtonCandidates("BlademasterEvent", new EventChoiceData { Object1 = Talent }, null));

        var agentChoice = new EventChoiceData { FreeAgent = EventChoiceSetTests.Agent(), StatBonuses = new StatBonusSet(false, new[] { new StatBonusData(1, 9, 0, 1, false, false) }) };
        IReadOnlyList<TextRecipe> texts = EventTextRecipes.TextCandidates("GeneralManagerEvent", agentChoice, null, null);
        Assert.All(texts.SelectMany(t => t.Targets()), target => Assert.True(target.IsOn(agentChoice)));
        Assert.Equal(new[] { 9, 0, 1, 2, 3, 4 }, texts.Where(t => t.Op == TextOp.StatName).Select(t => t.Number));
        Assert.Equal(6, texts.Count(t => t.Op is TextOp.ObjectName or TextOp.ObjectDescription)); // three talents, each named and described

        // A Nurse card only when its procedure is known (its button made by a recipe, or no button).
        Assert.DoesNotContain(EventTextRecipes.TextCandidates(EventTextRecipes.NurseClass, new EventChoiceData { TranslatedButtonName = "?" }, null, null), t => t.Op == TextOp.NurseCardDescription);
        Assert.Equal(2, EventTextRecipes.TextCandidates(EventTextRecipes.NurseClass, new EventChoiceData(), null, new NurseOffer(null, 0)).Count(t => t.Op == TextOp.NurseCardDescription));
    }
}
