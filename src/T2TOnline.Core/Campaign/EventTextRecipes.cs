using System;
using System.Collections.Generic;
using System.Linq;

namespace T2TOnline.Core.Campaign;

/// <summary>A Nurse event's injury offer (its roster's <c>InjuryOfferType</c> and <c>InjuryOfferGames</c>), which its card descriptions name.</summary>
/// <param name="Injury">The injury type offered (the game's <c>InjuryType</c> number), or null for none.</param>
/// <param name="Games">The games it lasts.</param>
public sealed record NurseOffer(int? Injury, int Games);

/// <summary>What one event set's texts did, for the log: on the host, how its texts went; on a friend, how they were made.</summary>
public sealed class EventTextTally
{
    /// <summary>Host: texts that go with their recipe (each friend makes them in its own language).</summary>
    public int Recipes { get; private set; }

    /// <summary>Host: texts that are a name or a number (the same in every language: nothing to make).</summary>
    public int Neutral { get; private set; }

    /// <summary>Host: texts no recipe makes (friends see them in the host's language), as "button" or "text 2" with the text.</summary>
    public List<(string Slot, string Text)> Untranslated { get; } = new();

    /// <summary>Friend: texts made here from the host's recipes.</summary>
    public int Made { get; private set; }

    /// <summary>Friend: recipes that made nothing here (the host's text kept), each as its recipe in words.</summary>
    public List<string> Failed { get; } = new();

    /// <summary>For logs: "9 as recipes, 3 names or numbers, 0 kept" (host) or "9 made here, 0 kept" (friend).</summary>
    public override string ToString() => Made > 0 || Failed.Count > 0
        ? $"{Made} made here, {Failed.Count} kept"
        : $"{Recipes} as recipes, {Neutral} names or numbers, {Untranslated.Count} kept";

    internal void AddRecipe() => Recipes++;

    internal void AddNeutral() => Neutral++;

    internal void AddMade() => Made++;
}

/// <summary>
/// The producers of an event choice's translated texts in the game, as candidate <see cref="TextRecipe"/>s, and their use on both sides:
/// <list type="bullet">
/// <item>Host (<see cref="WithRecipes"/>): every candidate for the choice's event class and objects is made on the host's
/// PC; a text goes with the first candidate that makes it exactly (else alone: a name or a number needs nothing, anything
/// else is noted as kept in the host's language).</item>
/// <item>Friend (<see cref="Localize"/>): each recipe made on this PC, in its language, with its copies of the choice's
/// objects; a recipe that makes nothing keeps the host's text.</item>
/// </list>
/// Pure logic over <see cref="ITextFunctions"/>; never throws for what the game's functions do.
/// </summary>
public static class EventTextRecipes
{
    /// <summary>Fan Number 1's power-up and ability choices' name and description (<c>CreatePowerupChoice</c>, <c>CreateAbilityChoice</c>).</summary>
    public const string FanNameDescription = "v2ui/event_choice/fan_no_1/name_desc_format";

    /// <summary>Fan Number 1's upgrade choices' skater and power-up (<c>CreateTalentUpgradeChoice</c>, <c>CreateAbilityUpgradeChoice</c>).</summary>
    public const string FanUpgrade = "v2ui/event_choice/fan_no_1/upgrade_format";

    /// <summary>Fan Number 1's stat choices' button (<c>GenerateStats</c>).</summary>
    public const string FanTraining = "v2ui/event_choice/fan_no_1/training/name";

    /// <summary>Fan Number 1's size increase's name.</summary>
    public const string FanGrow = "v2ui/event_choice/fan_no_1/increase_size/name";

    /// <summary>Fan Number 1's size reduction's name.</summary>
    public const string FanShrink = "v2ui/event_choice/fan_no_1/reduce_size/name";

    /// <summary>The Blademaster's locked ability card's title (<c>InitializeLockedAbilityChoices</c>).</summary>
    public const string BlademasterAbility = "npc/blademaster/new_ability_card_title";

    /// <summary>The start of the Blademaster's upgrade cards' title, the game's own English in every language (<c>AddTalentChoice</c>, <c>AddAbilityChoice</c>, <c>AddRelicChoice</c>).</summary>
    public const string BlademasterUpgrade = "Upgrade: ";

    /// <summary>Between the skater and the power-up in a Blademaster's talent or ability upgrade's title, the game's own in every language.</summary>
    public const string BlademasterOwner = "'s ";

    /// <summary>The highlight colour the events give a power-up's description.</summary>
    public const string DescriptionColor = "1FFF75";

    /// <summary>The game's forward stats (<c>EForwardStat</c>: overall, speed, accuracy, shot power, checking).</summary>
    public const int ForwardStats = 5;

    /// <summary>The game's Nurse procedures (<c>NurseProcedureState.Procedure</c>: transplant to cloning).</summary>
    public const int NurseProcedures = 10;

    /// <summary>The Nurse's event class.</summary>
    public const string NurseClass = "NurseEvent";

    private const string FanClass = "FanNumber1Event";
    private const string BlademasterClass = "BlademasterEvent";
    private const string ChaosClass = "ChaosEvent3Choice";

    /// <summary>
    /// The recipes that may have made <paramref name="choice"/>'s translated button name in an event of
    /// <paramref name="eventClass"/>, in the producers' order (<paramref name="skaterName"/>: the choice's skater's "first last").
    /// </summary>
    public static IReadOnlyList<TextRecipe> ButtonCandidates(string eventClass, EventChoiceData choice, string? skaterName)
    {
        ArgumentNullException.ThrowIfNull(choice);
        var list = new List<TextRecipe>();
        switch (eventClass)
        {
            case FanClass:
                if (choice.Object1 != null)
                {
                    list.Add(TextRecipe.Format(TextRecipe.Term(FanNameDescription), TextRecipe.ObjectName(ChoiceObjectRef.Object1),
                        TextRecipe.ObjectDescription(ChoiceObjectRef.Object1, false, false, DescriptionColor)));
                }

                if (choice.Object2 != null && skaterName != null)
                {
                    list.Add(TextRecipe.Format(TextRecipe.Term(FanUpgrade), TextRecipe.Literal(skaterName), TextRecipe.ObjectName(ChoiceObjectRef.Object2)));
                }

                list.Add(TextRecipe.Term(FanTraining));
                list.Add(TextRecipe.Term(FanGrow));
                list.Add(TextRecipe.Term(FanShrink));
                break;
            case BlademasterClass:
                if (choice.Object1 != null && skaterName != null)
                {
                    list.Add(TextRecipe.Format(TextRecipe.Term(BlademasterAbility), TextRecipe.ObjectName(ChoiceObjectRef.Object1), TextRecipe.Literal(skaterName)));
                }

                // An upgrade's title, of the current one's name (verified in the game's code: AddTalentChoice, AddRelicChoice).
                if (choice.Object2 != null && skaterName != null)
                {
                    list.Add(TextRecipe.Concat(TextRecipe.Literal(BlademasterUpgrade), TextRecipe.Literal(skaterName), TextRecipe.Literal(BlademasterOwner), TextRecipe.ObjectName(ChoiceObjectRef.Object2)));
                }

                if (choice.Object2 != null)
                {
                    list.Add(TextRecipe.Concat(TextRecipe.Literal(BlademasterUpgrade), TextRecipe.ObjectName(ChoiceObjectRef.Object2)));
                }

                break;
            case ChaosClass:
                // The relic's name (verified in the game's code: Initialize).
                if (choice.Object1 != null)
                {
                    list.Add(TextRecipe.ObjectName(ChoiceObjectRef.Object1));
                }

                break;
            case NurseClass:
                for (int procedure = 0; procedure < NurseProcedures; procedure++)
                {
                    list.Add(TextRecipe.NurseProcedureName(procedure));
                }

                break;
        }

        return list;
    }

    /// <summary>
    /// The recipes that may have made one of <paramref name="choice"/>'s custom texts in an event of <paramref name="eventClass"/>,
    /// in the producers' order: a Nurse card's description (its procedure from <paramref name="buttonRecipe"/>, its offer
    /// <paramref name="nurse"/>), the choice's objects' names, its free agent's talents' names and descriptions, its stat
    /// bonuses' stat names (then every stat's), Fan Number 1's size names.
    /// </summary>
    public static IReadOnlyList<TextRecipe> TextCandidates(string eventClass, EventChoiceData choice, TextRecipe? buttonRecipe, NurseOffer? nurse)
    {
        ArgumentNullException.ThrowIfNull(choice);
        var list = new List<TextRecipe>();
        if (eventClass == NurseClass && (buttonRecipe != null || choice.TranslatedButtonName == null))
        {
            // Its card: the procedure its button names (none without a button), the talent it discloses (the choice's object).
            int? procedure = buttonRecipe is { Op: TextOp.NurseProcedureName } named ? named.Number : null;
            ChoiceObjectRef talent = choice.Object1 != null ? ChoiceObjectRef.Object1 : ChoiceObjectRef.None;
            if (nurse != null)
            {
                list.Add(TextRecipe.NurseCardDescription(procedure, talent, nurse.Injury, nurse.Games));
            }

            list.Add(TextRecipe.NurseCardDescription(procedure, talent, null, 0));
        }

        if (choice.Object1 != null)
        {
            list.Add(TextRecipe.ObjectName(ChoiceObjectRef.Object1));
        }

        if (choice.Object2 != null)
        {
            list.Add(TextRecipe.ObjectName(ChoiceObjectRef.Object2));
        }

        for (int i = 0; choice.FreeAgent != null && i < choice.FreeAgent.Talents.Count; i++)
        {
            list.Add(TextRecipe.ObjectName(ChoiceObjectRef.AgentTalent(i)));
            list.Add(TextRecipe.ObjectDescription(ChoiceObjectRef.AgentTalent(i), false, false, DescriptionColor));
        }

        IEnumerable<int> bonusStats = choice.StatBonuses?.Bonuses.Select(b => b.StatForward) ?? Enumerable.Empty<int>();
        foreach (int stat in bonusStats.Concat(Enumerable.Range(0, ForwardStats)).Where(s => s is >= 0 and <= TextRecipe.MaxStat).Distinct())
        {
            list.Add(TextRecipe.StatName(stat, false));
        }

        if (eventClass == FanClass)
        {
            list.Add(TextRecipe.Term(FanGrow));
            list.Add(TextRecipe.Term(FanShrink));
        }

        return list;
    }

    /// <summary>
    /// True for a text that's the same in every language: no letters at all ("+2", "12"), or the choice's skater's name
    /// (<paramref name="skaterName"/>; names aren't translated).
    /// </summary>
    public static bool IsNeutral(string text, string? skaterName)
    {
        ArgumentNullException.ThrowIfNull(text);
        return !text.Any(char.IsLetter) || (skaterName != null && string.Equals(text.Trim(), skaterName.Trim(), StringComparison.Ordinal));
    }

    /// <summary>
    /// Host: <paramref name="choice"/> with the recipe of each of its texts that one makes exactly on this PC
    /// (<paramref name="functions"/>: the game's own, over this choice's objects), counted in <paramref name="tally"/>. Each
    /// candidate is made at most once. Never throws for what a function does.
    /// </summary>
    public static EventChoiceData WithRecipes(string eventClass, EventChoiceData choice, ITextFunctions functions, string? skaterName, NurseOffer? nurse, EventTextTally tally)
    {
        ArgumentNullException.ThrowIfNull(choice);
        ArgumentNullException.ThrowIfNull(functions);
        ArgumentNullException.ThrowIfNull(tally);
        var made = new Dictionary<TextRecipe, string?>(ReferenceEqualityComparer.Instance);
        TextRecipe? button = Slot(choice.TranslatedButtonName, ButtonCandidates(eventClass, choice, skaterName), "button");
        TextRecipe?[]? texts = null;
        if (choice.Texts != null)
        {
            IReadOnlyList<TextRecipe> candidates = TextCandidates(eventClass, choice, button, nurse);
            texts = new TextRecipe?[choice.Texts.Count];
            for (int i = 0; i < texts.Length; i++)
            {
                texts[i] = Slot(choice.Texts[i], candidates, $"text {i + 1}");
            }
        }

        return choice.WithRecipes(button, texts != null && texts.Any(r => r != null) ? texts : null);

        TextRecipe? Slot(string? text, IReadOnlyList<TextRecipe> candidates, string slot)
        {
            if (string.IsNullOrEmpty(text))
            {
                return null;
            }

            foreach (TextRecipe candidate in candidates)
            {
                if (!made.TryGetValue(candidate, out string? result))
                {
                    made[candidate] = result = candidate.Evaluate(functions);
                }

                if (string.Equals(result, text, StringComparison.Ordinal) && candidate.Check() == null)
                {
                    tally.AddRecipe();
                    return candidate;
                }
            }

            if (IsNeutral(text, skaterName))
            {
                tally.AddNeutral();
            }
            else
            {
                tally.Untranslated.Add((slot, text));
            }

            return null;
        }
    }

    /// <summary>
    /// Friend: <paramref name="choice"/> with each text that has a recipe made by it on this PC (<paramref name="functions"/>:
    /// the game's own, over this PC's copies of the choice's objects), in this PC's language; a recipe that makes nothing keeps
    /// the host's text. Counted in <paramref name="tally"/>. Never throws for what a function does.
    /// </summary>
    public static EventChoiceData Localize(EventChoiceData choice, ITextFunctions functions, EventTextTally tally)
    {
        ArgumentNullException.ThrowIfNull(choice);
        ArgumentNullException.ThrowIfNull(functions);
        ArgumentNullException.ThrowIfNull(tally);
        if (choice.ButtonRecipe == null && choice.TextRecipes == null)
        {
            return choice;
        }

        string? button = Make(choice.TranslatedButtonName, choice.ButtonRecipe);
        List<string?>? texts = choice.Texts?.Select((text, i) => Make(text, choice.TextRecipes != null && i < choice.TextRecipes.Count ? choice.TextRecipes[i] : null)).ToList();
        return choice.WithTexts(button, texts);

        string? Make(string? host, TextRecipe? recipe)
        {
            if (recipe == null || host == null)
            {
                return host;
            }

            // Nothing, or an empty text (a term this PC's language leaves empty), keeps the host's.
            string? here = recipe.Evaluate(functions);
            if (string.IsNullOrEmpty(here))
            {
                tally.Failed.Add(recipe.ToString());
                return host;
            }

            tally.AddMade();
            return here;
        }
    }
}
