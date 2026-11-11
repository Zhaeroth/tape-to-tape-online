#if HAS_INTEROP
using System;
using System.Collections.Generic;
using Il2CppBishopGames.UI;
using Il2CppData;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppRogue;
using Il2CppRogue.FreeAgents;
using Il2CppRogue.NPC.ConditioningNPC;
using Il2CppRogue.NPC.FanNumber1NPC;
using Il2CppState;
using Il2CppSTS.Map;
using Il2CppTape2Tape.Hockey.UI;
using T2TOnline.Core.Campaign;
using T2TOnline.Core.Diagnostics;
using T2TOnline.Plugin.Diagnostics;
using UnityEngine;
using Il2CppList = Il2CppSystem.Collections.Generic;

namespace T2TOnline.Plugin.Campaign.Menus;

/// <summary>
/// The host's side of an event menu as a real view (D43): the choices the host's event
/// rolled, read from its <c>EventChoiceMenu.SourceNode</c> once its cards are built (the menu's <c>T2TMenu.Show</c>, and
/// each page or reroll's step), as an <see cref="EventChoiceSet"/> for friends. Every event rolls with unseeded randomness
/// (and the training event itself is picked at random), so a friend's game can't roll the same choices: it builds the
/// game's own choices from these instead (<see cref="EventViews"/>).
/// <para>
/// Which events go as the real menu (<see cref="RealClasses"/>): the Coach, the three trainings, Chaos and the
/// Blademaster, whose choices hold only run skaters, the game's own relics, talents and abilities, sprites, texts, stat
/// bonuses and injuries; Fan Number 1, whose size changes are preview talents made at run time (their texts, icon and flag
/// go, <see cref="EventObjectKind.Preview"/>); the General Manager, whose free agents are skaters its roll makes, not in the
/// run (each goes as the run file's own model of it, <see cref="FreeAgentData"/>, with its leaving conditions); the Nurse,
/// whose cards are real and whose picked procedure takes the screen over (its choices carry a takeover; a friend opens the
/// procedure screen for real, <see cref="NurseHost"/>, and its text goes in the menu's data for one that can't,
/// <see cref="NurseScreen"/>). A choice this can't carry (a
/// skater not in the run that isn't a forward, an object that isn't a relic, talent, ability or preview, a takeover
/// outside a Nurse's event, a leaving condition of another class) sends the whole event as text, with why.
/// </para>
/// <para>
/// Each choice's translated texts go with the recipes that make them (<see cref="EventTexts"/>: a friend makes them in its
/// own language), its images with their sources (<see cref="EventImages"/>), and a free agent with its look
/// (<see cref="SkaterModels.Agent"/>).
/// </para>
/// </summary>
internal static class EventCapture
{
    /// <summary>Value: the event's class ("CoachEvent").</summary>
    public const string ClassKey = "event.class";

    /// <summary>Value: the host's choices go with the menu as an attachment (flag).</summary>
    public const string SetKey = "event.set";

    /// <summary>Value: why the event shows as text on friends (text).</summary>
    public const string TextKey = "event.text";

    /// <summary>
    /// Value group: the event menu's state on the host's screen (items <see cref="Intro"/>, <see cref="Peek"/>,
    /// <see cref="Details"/>, <see cref="Takeover"/>, <see cref="Cards"/>, <see cref="Exit"/>).
    /// </summary>
    public const string StateGroup = "event";

    /// <summary>State item: the host's intro (<see cref="IntroNotYet"/>, <see cref="IntroPlaying"/>, <see cref="IntroOver"/>).</summary>
    public const int Intro = 0;

    /// <summary>The host's intro hasn't started.</summary>
    public const int IntroNotYet = 0;

    /// <summary>The host's intro is playing.</summary>
    public const int IntroPlaying = 1;

    /// <summary>The host's intro has played and ended (skipped or done): a friend's still playing is ended too.</summary>
    public const int IntroOver = 2;

    /// <summary>State item: the host's event screen is shown (1) or put aside to look at the map (0).</summary>
    public const int Peek = 1;

    /// <summary>State item: the host holds the details button (1) or not (0).</summary>
    public const int Details = 2;

    /// <summary>
    /// State item: the host's event screen is taken over (1: the Nurse's procedure screen is up over it,
    /// <c>EventChoiceMenu.m_IsTakenOver</c>) or not (0).
    /// </summary>
    public const int Takeover = 3;

    /// <summary>
    /// State item: the host's cards are in (1: its last <c>EventChoiceMenu.SetChoicesShown</c> showed them) or not (0): a
    /// friend's cards wait for the host's at the end of its intro (v1, the NPC's lines first).
    /// </summary>
    public const int Cards = 4;

    /// <summary>
    /// State item: the host's event has started its exit (1: <c>EventChoiceMenu.Exit</c> ran, so its cards go and its NPC says
    /// goodbye before its menu closes) or not (0): a friend's NPC says goodbye with it.
    /// </summary>
    public const int Exit = 5;

    /// <summary>How many state items an event menu has.</summary>
    public const int StateItems = 6;

    /// <summary>The event classes shown as the real menu on friends, with each one's name in words.</summary>
    public static readonly IReadOnlyDictionary<string, string> RealClasses = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["CoachEvent"] = "Coach",
        ["RegularPlayerTraining"] = "skater training",
        ["StatsTraining"] = "stat training",
        ["TeamTraining"] = "team training",
        ["ChaosEvent3Choice"] = "Chaos",
        ["BlademasterEvent"] = "Blademaster",
        ["FanNumber1Event"] = "Fan Number 1",
        ["GeneralManagerEvent"] = "General Manager",
        ["NurseEvent"] = "Nurse",
    };

    /// <summary>
    /// What can show as text, by event class, with why: a class here that isn't in <see cref="RealClasses"/> goes as text
    /// whole; one in both is real, and this part of it shows as text only when a friend can't follow it (the Nurse's
    /// procedure screen, over its real cards: <see cref="NurseFollow"/>).
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> TextClasses = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["NurseEvent"] = "the Nurse's procedure screen shows as text here (it takes the event screen over with its own state and input)",
    };

    /// <summary>The event's name in words for logs ("Coach", "stat training"), or its class.</summary>
    public static string Words(string eventClass) => RealClasses.TryGetValue(eventClass, out string? words) ? words : eventClass;

    /// <summary>Which of <paramref name="menu"/>'s cards <paramref name="navigable"/> is on (-1: none of them), for the log's pick.</summary>
    public static int ChoiceOf(EventChoiceMenu menu, AbstractNavigable navigable) => ChoiceOf(menu, navigable.GetComponentInParent<EventChoiceItem>());

    /// <summary>Where <paramref name="item"/> is among <paramref name="menu"/>'s cards (-1: not there): they're made in its choices' order.</summary>
    public static int ChoiceOf(EventChoiceMenu menu, EventChoiceItem? item)
    {
        Il2CppList.List<EventChoiceItem>? items = menu.m_Items;
        for (int i = 0; item != null && items != null && i < items.Count; i++)
        {
            if (items[i]?.Pointer == item.Pointer)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// The choices <paramref name="menu"/>'s event shows now, as <paramref name="step"/> number <paramref name="version"/>, or
    /// null with why it goes as text. Reads only; never throws for a choice it can't carry (that's the problem).
    /// </summary>
    public static EventChoiceSet? Read(EventChoiceMenu menu, EventSetStep step, int version, out string problem)
    {
        EventNode? node = menu.SourceNode;
        if (node == null)
        {
            problem = "the menu has no event";
            return null;
        }

        string eventClass = MenuGame.TypeName(node);
        if (!RealClasses.ContainsKey(eventClass))
        {
            problem = TextClasses.TryGetValue(eventClass, out string? why) ? why : $"a {SafeText.Clean(eventClass, 48)} event is one this version doesn't show for real";
            return null;
        }

        TeamData? team = (menu.m_CampaignState ?? MenuGame.Campaign())?.runData?.teamData;
        if (team == null)
        {
            problem = "the host's game has no run team";
            return null;
        }

        Il2CppList.List<EventChoice>? choices = node.eventChoices;
        int count = choices?.Count ?? 0;
        if (count == 0)
        {
            problem = "the event has no choices";
            return null;
        }

        if (count > EventChoiceSet.MaxChoices)
        {
            problem = $"the event has {count} choices (at most {EventChoiceSet.MaxChoices} go)";
            return null;
        }

        var skaters = new Dictionary<IntPtr, SkaterData>();
        Il2CppList.List<SkaterData>? all = team.GetAllSkaters();
        for (int i = 0; all != null && i < all.Count; i++)
        {
            if (all[i] != null)
            {
                skaters[all[i].Pointer] = all[i];
            }
        }

        // Only a Nurse's choices may take the screen over (its procedure screen, NurseHost). Each choice's texts go with
        // their recipes and its images with their sources (EventTexts, EventImages).
        var reading = new SetReading(node, eventClass, eventClass == EventChoiceSet.TakeoverClass);
        var read = new List<EventChoiceData>(count);
        for (int i = 0; i < count; i++)
        {
            EventChoiceData? choice = Choice(choices![i], team, skaters, reading, out problem);
            if (choice == null)
            {
                problem = $"choice {i + 1}: {problem}";
                return null;
            }

            read.Add(choice);
        }

        TrainingFields? training = null;
        if (node.TryCast<TrainingEvent>() is { } trainingEvent)
        {
            // Only a skater training trains its skater (the node sets it at each launch); another training's is whatever an
            // earlier launch left, read by nothing: it goes only when it's in the run.
            ForwardData? forward = trainingEvent.ForwardTraining;
            bool inRun = forward != null && skaters.ContainsKey(forward.Pointer);
            if (!inRun && trainingEvent.TryCast<RegularPlayerTraining>() != null)
            {
                problem = forward == null ? "the skater training has no skater" : "the training's skater isn't in the run";
                return null;
            }

            training = new TrainingFields
            {
                Stat = (int)trainingEvent.StatsImproving,
                Rarity = (int)trainingEvent.rarity,
                RewardType = (int)trainingEvent.RewardType,
                ForwardId = inRun ? forward!.id : null,
            };
        }

        Point? at = menu.mapNode?.Node?.point;
        var set = new EventChoiceSet
        {
            Version = version,
            Step = step,
            EventClass = eventClass,
            EventName = node.name ?? string.Empty,
            EventId = string.IsNullOrEmpty(node.id) ? null : node.id,
            NodeType = (int)menu.SourceNodeType,
            Point = at == null ? null : (at.x, at.y),
            RerollCount = Math.Clamp(menu.m_RerollCount, 0, EventChoiceSet.MaxCount),
            ChoicesMade = Math.Clamp(menu.m_ChoicesMade, 0, EventChoiceSet.MaxCount),
            Training = training,
            DescTerms = Terms(node.descTermParams),
            Choices = read,
        };

        problem = set.Problem() ?? string.Empty;
        if (problem.Length > 0 && reading.Texts.Recipes > 0 && set.WithoutRecipes() is { } plain && plain.Problem() == null)
        {
            // The recipes took it over the attachment's cap: it goes with every text as the host's.
            ModLog.Warn($"Menus: the host's {Words(eventClass)} choices don't fit with their texts' recipes ({problem}); they go without them (friends see the host's language).");
            set = plain;
            problem = string.Empty;
        }

        if (problem.Length == 0)
        {
            EventTexts.Said(set, reading.Texts, reading.Sprites);
        }

        return problem.Length == 0 ? set : null;
    }

    /// <summary>One choice as plain data, or null with why it can't go. <paramref name="reading"/>: the set's event, whether its choices may take the screen over (a Nurse's), its texts' and images' tallies.</summary>
    private static EventChoiceData? Choice(EventChoice choice, TeamData team, Dictionary<IntPtr, SkaterData> skaters, SetReading reading, out string problem)
    {
        problem = string.Empty;
        if (choice == null)
        {
            problem = "it can't be read";
            return null;
        }

        bool takeover = choice.onTakeover != null;
        if (takeover && !reading.Takeovers)
        {
            problem = "it opens a screen of its own (a takeover), which only a Nurse's choices go with";
            return null;
        }

        // A skater the event made that isn't in the run (a General Manager's free agent) goes as the run file's own model.
        SkaterData? skater = choice.Skater;
        ForwardData? agent = null;
        FreeAgentData? agentData = null;
        if (skater != null && !skaters.ContainsKey(skater.Pointer))
        {
            agent = skater.TryCast<ForwardData>();
            agentData = agent == null ? null : SkaterModels.Agent(agent, out problem);
            if (agentData == null)
            {
                problem = agent == null ? "its skater isn't in the run and isn't a forward" : $"its free agent can't be read ({problem})";
                return null;
            }
        }

        List<FreeAgentCondition>? conditions = null;
        if (choice.FreeAgentLeavingConditions is { } leaving && (conditions = Conditions(leaving, out problem)) == null)
        {
            return null;
        }

        EventObject? object1 = null, object2 = null;
        if ((choice.PowerUpOrRelic is { } first && (object1 = Object(first, team, skaters, agent, reading.Sprites, out problem)) == null) ||
            (choice.PowerUpOrRelic2 is { } second && (object2 = Object(second, team, skaters, agent, reading.Sprites, out problem)) == null))
        {
            return null;
        }

        List<string?>? images = null;
        List<ImageSource?>? sources = null;
        if (choice.CustomImages is { } sprites)
        {
            images = new List<string?>(sprites.Length);
            sources = new List<ImageSource?>(sprites.Length);
            foreach (Sprite? sprite in sprites)
            {
                string? name = sprite == null ? null : sprite.name;
                if (sprite != null && string.IsNullOrEmpty(name))
                {
                    problem = "one of its images has no name";
                    return null;
                }

                images.Add(name);
                sources.Add(sprite == null ? null : reading.Sprites.Source(sprite, choice.PowerUpOrRelic, choice.PowerUpOrRelic2, agent));
            }
        }

        List<string?>? texts = null;
        if (choice.CustomLocalizedTexts is { } strings)
        {
            texts = new List<string?>(strings.Length);
            foreach (string? text in strings)
            {
                texts.Add(text);
            }
        }

        StatBonusSet? bonuses = null;
        if (choice.CustomStatBonuses is { } custom)
        {
            var list = new List<StatBonusData>();
            Il2CppList.List<StatBonus>? stats = custom.m_StatBonuses;
            for (int i = 0; stats != null && i < stats.Count; i++)
            {
                StatBonus bonus = stats[i];
                list.Add(new StatBonusData((int)bonus.SkaterType, (int)bonus.StatForward, (int)bonus.StatGoalie, bonus.Bonus, bonus.IsConditional, bonus.IsBlockableStatGain));
            }

            bonuses = new StatBonusSet(custom.AreAlreadyCounted, list);
        }

        InjuryData? injury = null;
        if (choice.CustomInjury is { } hurt)
        {
            injury = new InjuryData((int)hurt.InjuryType, hurt.HealingTime, hurt.OriginalTime, hurt.LastDisplayTime);
        }

        var data = new EventChoiceData
        {
            ButtonName = choice.buttonName,
            TranslatedButtonName = choice.translatedButtonName,
            HasOnSelection = choice.onSelection != null,
            HasOnTakeover = takeover,
            Interactable = choice.interactable,
            NodeType = (int)choice.nodeType,
            Terms = Terms(choice.choicesTermParams),
            ChoiceType = (int)choice.ChoiceType,
            SkaterId = agentData == null ? skater?.id : null,
            FreeAgent = agentData,
            Conditions = conditions,
            Object1 = object1,
            Object2 = object2,
            Images = images,
            Texts = texts,
            StatBonuses = bonuses,
            Injury = injury,
            SuppressRewardTracking = choice.SuppressRewardTracking,
            ImageSources = sources,
        };
        return EventTexts.Host(reading.EventClass, reading.Node, choice, agent, data, reading.Texts);
    }

    /// <summary>
    /// A choice's power-up or relic: a relic, talent or ability, the run's own (the team's relic, a skater's talent or
    /// ability, found by object), the choice's free agent's (<paramref name="agent"/>, when it has one) or a fresh copy; a
    /// preview talent; or null with why.
    /// </summary>
    private static EventObject? Object(ScriptableObject value, TeamData team, Dictionary<IntPtr, SkaterData> skaters, ForwardData? agent, EventImages.HostSprites sprites, out string problem)
    {
        problem = string.Empty;
        string className = MenuGame.TypeName(value);
        if (value.TryCast<Relic>() is { } relic)
        {
            bool owned = false;
            Il2CppList.List<Relic>? relics = team.relics;
            for (int i = 0; relics != null && i < relics.Count && !owned; i++)
            {
                owned = relics[i] != null && relics[i].Pointer == relic.Pointer;
            }

            ChaosRelic? chaos = relic.TryCast<ChaosRelic>();
            return Checked(new EventObject
            {
                Kind = EventObjectKind.Relic,
                Id = relic.id ?? string.Empty,
                ClassName = className,
                Level = relic.level,
                Owner = owned ? EventObjectOwner.Team : EventObjectOwner.None,
                IntValue = relic.intValue,
                Chaos = chaos != null,
                TotalGames = chaos?.totalGames ?? 0,
                RemainingGames = chaos?._remainingGames ?? 0,
            }, out problem);
        }

        if (value.TryCast<FanNumber1PreviewTalent>() is { } preview)
        {
            // Made at run time by the event (a size change): its texts, icon and flag go; a friend's game makes its own.
            Sprite? icon = preview.icon;
            string? iconName = icon == null ? null : icon.name;
            if (icon != null && string.IsNullOrEmpty(iconName))
            {
                problem = "its preview's icon has no name";
                return null;
            }

            return Checked(new EventObject
            {
                Kind = EventObjectKind.Preview,
                Id = preview.id ?? string.Empty,
                ClassName = className,
                Level = preview.level,
                PreviewName = preview.m_LocalizedName,
                PreviewDescription = preview.m_LocalizedDescription,
                PreviewIcon = iconName,
                PreviewTraining = preview.IsTraining,
                PreviewIconSource = icon == null ? null : sprites.PreviewSource(icon),
            }, out problem);
        }

        PowerUp? power = value.TryCast<PowerUp>();
        EventObjectKind kind = power == null ? EventObjectKind.None
            : power.TryCast<Talent>() != null ? EventObjectKind.Talent
            : power.TryCast<Ability>() != null ? EventObjectKind.Ability
            : EventObjectKind.None;
        if (power == null || kind == EventObjectKind.None)
        {
            problem = $"it shows a {SafeText.Clean(className, 48)}, which isn't a relic, talent or ability";
            return null;
        }

        string? owner = null;
        foreach (SkaterData skater in skaters.Values)
        {
            if (kind == EventObjectKind.Ability && skater.ability != null && skater.ability.Pointer == power.Pointer)
            {
                owner = skater.id;
                break;
            }

            Il2CppList.List<Talent>? talents = skater.powerups;
            for (int i = 0; kind == EventObjectKind.Talent && talents != null && i < talents.Count && owner == null; i++)
            {
                if (talents[i] != null && talents[i].Pointer == power.Pointer)
                {
                    owner = skater.id;
                }
            }

            if (owner != null)
            {
                break;
            }
        }

        // Not the run's: the choice's own free agent's talent (its number) or ability (-1)?
        int agentIndex = int.MinValue;
        if (owner == null && agent != null)
        {
            if (kind == EventObjectKind.Ability && agent.ability != null && agent.ability.Pointer == power.Pointer)
            {
                agentIndex = -1;
            }

            Il2CppList.List<Talent>? talents = agent.powerups;
            for (int i = 0; kind == EventObjectKind.Talent && talents != null && i < talents.Count && agentIndex == int.MinValue; i++)
            {
                agentIndex = talents[i] != null && talents[i].Pointer == power.Pointer ? i : int.MinValue;
            }
        }

        return Checked(new EventObject
        {
            Kind = kind,
            Id = power.id ?? string.Empty,
            ClassName = className,
            Level = power.level,
            Owner = owner != null ? EventObjectOwner.Skater : agentIndex != int.MinValue ? EventObjectOwner.FreeAgent : EventObjectOwner.None,
            OwnerId = owner,
            OwnerIndex = agentIndex != int.MinValue ? agentIndex : 0,
        }, out problem);
    }

    /// <summary>
    /// A free agent (a skater the event made that isn't in the run) as the run file's own model of it: the game's
    /// <c>SerializerV2.SerializeToModel(ForwardData)</c>, the same the run's save makes of a skater, read field by field, plus
    /// what the model leaves out and the card shows (each talent's level and a temporary contract's games, the ability's
    /// level, the flavour-text keys). Null, with why, when the model can't be made or doesn't match the skater.
    /// </summary>
    internal static FreeAgentData? FreeAgent(ForwardData forward, out string problem)
    {
        ForwardDataModelV2? model;
        try
        {
            model = new SerializerV2().SerializeToModel(forward)?.TryCast<ForwardDataModelV2>();
        }
        catch (Exception ex)
        {
            problem = $"the game's run file model of it failed ({ModLog.SafeErrorText(ex)})";
            return null;
        }

        if (model == null)
        {
            problem = "the game made no forward model of it";
            return null;
        }

        Il2CppList.List<PowerUpModelV2>? models = model.powerUps;
        Il2CppList.List<Talent>? owned = forward.powerups;
        if (models == null || owned == null || models.Count != owned.Count)
        {
            problem = "its talents don't match the game's model of them";
            return null;
        }

        if (owned.Count > FreeAgentData.MaxTalents)
        {
            problem = $"it has {owned.Count} talents (at most {FreeAgentData.MaxTalents} go)";
            return null;
        }

        var talents = new List<FreeAgentTalent>(owned.Count);
        for (int i = 0; i < owned.Count; i++)
        {
            Talent? talent = owned[i];
            string? id = models[i]?.Id;
            if (talent == null || string.IsNullOrEmpty(id) || id != talent.id)
            {
                problem = $"its talent {i + 1} is missing or has no id";
                return null;
            }

            talents.Add(new FreeAgentTalent(id, talent.level, talent.TryCast<TemporaryContract>()?.numOfGames ?? -1));
        }

        Ability? ability = forward.ability;
        string? abilityId = model.ability?.Id;
        if ((ability == null) != (abilityId == null))
        {
            problem = "its ability doesn't match the game's model of it";
            return null;
        }

        TeamColorsData? colors = model.colorSchemes;
        problem = string.Empty;
        return new FreeAgentData
        {
            Id = model.id ?? string.Empty,
            OriginalId = model.originalId ?? string.Empty,
            Name = model.name ?? string.Empty,
            FirstName = model.firstName,
            LastName = model.lastName,
            Number = model.number,
            Checking = model.checking,
            Speed = model.speed,
            ShotAccuracy = model.shotAccuracy,
            ShotPower = model.shotPower,
            InjuryCounter = model.injuryCounter,
            IsLefty = model.isLefty,
            IsBlack = model.isBlack,
            HasOversizedHelmet = model.hasOversizedHelmet,
            ForwardRarity = (int)model.forwardRarity,
            SkaterSize = (int)model.skaterSize,
            DefaultSkaterType = (int)model.defaultSkaterType,
            SkaterCategory = (int)model.skaterCategory,
            Skins = new[]
            {
                model.bodySkin, model.headSkin, model.helmetSkin, model.logoSkin, model.numberSkin, model.stickSkin, model.bodyAwaySkin, model.bicepSkin,
                model.gloveSkin, model.skateSkin, model.pantsSkin, model.bicepAwaySkin, model.gloveAwaySkin, model.skateAwaySkin, model.pantsAwaySkin,
            },
            Colors = colors == null ? null : new[]
            {
                Scheme(colors.jerseyScheme), Scheme(colors.numberScheme), Scheme(colors.pantsScheme), Scheme(colors.glovesScheme),
                Scheme(colors.stickScheme), Scheme(colors.helmetScheme), Scheme(colors.socksScheme), Scheme(colors.skatesScheme),
            },
            AbilityId = abilityId,
            AbilityLevel = ability?.level ?? 0,
            Talents = talents,
            FlavorKey = forward.flavorTextLoc,
            PerkFlavorKey = forward.perkFlavorTextLoc,
            PowerUpTextKey = forward.powerupTextLoc,
        };
    }

    private static ColorSchemeData? Scheme(ColorScheme? scheme) =>
        scheme == null ? null : new ColorSchemeData(Colour(scheme.primaryColor), Colour(scheme.secondaryColor), Colour(scheme.tertiaryColor));

    private static ColorValue Colour(Color color) => new(color.r, color.g, color.b, color.a);

    /// <summary>
    /// A free agent's leaving conditions (a choice's <c>FreeAgentLeavingConditions</c>) by class, in order; or null with why
    /// (not a list, one of a class this doesn't carry, too many). A friend reads its own back with it too.
    /// </summary>
    public static List<FreeAgentCondition>? Conditions(Il2CppObjectBase leaving, out string problem)
    {
        // The game builds them as a List (CreatePlayerChoice); the field is typed as the read-only interface.
        Il2CppList.List<LeavingCondition>? list = leaving.TryCast<Il2CppList.List<LeavingCondition>>();
        if (list == null)
        {
            problem = "its free agent's leaving conditions aren't a list";
            return null;
        }

        if (list.Count > EventChoiceSet.MaxConditions)
        {
            problem = $"its free agent has {list.Count} leaving conditions (at most {EventChoiceSet.MaxConditions} go)";
            return null;
        }

        var conditions = new List<FreeAgentCondition>(list.Count);
        for (int i = 0; i < list.Count; i++)
        {
            LeavingCondition? condition = list[i];
            FreeAgentCondition? read = condition == null ? null
                : condition.TryCast<MaxGamesCondition>() is { } games ? FreeAgentCondition.MaxGames(games.maxMatches, games.matches)
                : condition.TryCast<MaxGoalsCondition>() is { } goals ? FreeAgentCondition.MaxGoals(goals.maxGoals, goals.goals)
                : condition.TryCast<InjuryCondition>() is { } injury ? FreeAgentCondition.Injury(injury.isInjured)
                : condition.TryCast<RandomCondition>() is { } random ? FreeAgentCondition.Random(random.chance)
                : null;
            if (read == null)
            {
                problem = condition == null ? "a leaving condition is missing" : $"its free agent has a {SafeText.Clean(MenuGame.TypeName(condition), 48)} leaving condition, which this version doesn't carry";
                return null;
            }

            conditions.Add(read);
        }

        problem = string.Empty;
        return conditions;
    }

    private static EventObject? Checked(EventObject value, out string problem)
    {
        // The set's own check runs once it's whole; an object's id is checked here to say which one it was.
        problem = value.Id.Length == 0 ? $"its {value.Kind.ToString().ToLowerInvariant()} ({SafeText.Clean(value.ClassName, 48)}) has no id" : string.Empty;
        return problem.Length == 0 ? value : null;
    }

    /// <summary>One set being read: its event, whether its choices may take the screen over, its texts' and images' tallies.</summary>
    private sealed class SetReading
    {
        public SetReading(EventNode node, string eventClass, bool takeovers)
        {
            Node = node;
            EventClass = eventClass;
            Takeovers = takeovers;
            Sprites = new EventImages.HostSprites(node);
        }

        public EventNode Node { get; }

        public string EventClass { get; }

        public bool Takeovers { get; }

        public EventImages.HostSprites Sprites { get; }

        public EventTextTally Texts { get; } = new();
    }

    /// <summary>A term list as plain data (null for none).</summary>
    private static List<EventTerm>? Terms(Il2CppList.List<EventTermParam>? terms)
    {
        if (terms == null)
        {
            return null;
        }

        var list = new List<EventTerm>(terms.Count);
        for (int i = 0; i < terms.Count; i++)
        {
            EventTermParam? term = terms[i];
            list.Add(new EventTerm(term?.name, term?.value));
        }

        return list;
    }
}
#endif
