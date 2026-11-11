using System;
using System.Collections.Generic;
using System.Linq;
using T2TOnline.Core.Campaign;
using T2TOnline.Core.Serialization;

namespace T2TOnline.Core.Tests.Campaign;

public class EventChoiceSetTests
{
    /// <summary>A Blademaster-like set that uses every field of the format at least once.</summary>
    public static EventChoiceSet Sample(int version = 0, EventSetStep step = EventSetStep.Open) => new()
    {
        Version = version,
        Step = step,
        EventClass = "BlademasterEvent",
        EventName = "Blademaster_Default",
        EventId = "3f2a9c1e-1d2b-4b7e-9a51-6c0f2d8e7a10",
        NodeType = 6,
        Point = (3, 7),
        RerollCount = 1,
        ChoicesMade = 0,
        DescTerms = new[] { new EventTerm("SKATER", "Ölof Bergström"), new EventTerm("COUNT", "2"), new EventTerm(null, null) },
        Choices = new[]
        {
            new EventChoiceData
            {
                HasOnSelection = true,
                Interactable = true,
                NodeType = 6,
                ChoiceType = 4,
                SkaterId = "e1b9c7d2-0000-4000-8000-000000000001",
                Object1 = new EventObject { Kind = EventObjectKind.Ability, Id = "slapshot", ClassName = "Slapshot", Level = 2 },
                Object2 = new EventObject { Kind = EventObjectKind.Ability, Id = "slapshot", ClassName = "Slapshot", Level = 1, Owner = EventObjectOwner.Skater, OwnerId = "e1b9c7d2-0000-4000-8000-000000000001" },
                Terms = new[] { new EventTerm("LEVEL", "2") },
            },
            new EventChoiceData
            {
                ButtonName = "v2ui/event_choice/upgrade",
                TranslatedButtonName = "Upgrade\nnow",
                HasOnSelection = true,
                Interactable = false,
                NodeType = 6,
                ChoiceType = 7,
                Object1 = new EventObject { Kind = EventObjectKind.Relic, Id = "chaos_cursed_stick", ClassName = "CursedStick", Level = 2, IntValue = -3, Chaos = true, TotalGames = 5, RemainingGames = 4 },
                Object2 = new EventObject { Kind = EventObjectKind.Relic, Id = "chaos_cursed_stick", ClassName = "CursedStick", Level = 1, Owner = EventObjectOwner.Team, Chaos = true, TotalGames = 5, RemainingGames = 2 },
                Images = new[] { "icon_stat_speed", null },
                Texts = new[] { "Speed", "+2", null, string.Empty },
                StatBonuses = new StatBonusSet(true, new[] { new StatBonusData(1, 1, 0, 2, false, true), new StatBonusData(2, 0, 12, -1, true, false) }),
                Injury = new InjuryData(2, 3, 4, -1),
                SuppressRewardTracking = true,
                ButtonRecipe = TextRecipe.Format(TextRecipe.Term(EventTextRecipes.BlademasterAbility), TextRecipe.ObjectName(ChoiceObjectRef.Object1), TextRecipe.Literal("Ölof Bergström")),
                TextRecipes = new[] { TextRecipe.StatName(1, false), null, null, null },
                ImageSources = new[] { ImageSource.Event("stat_icons", 64, 0, 64, 64.5f), null },
            },
            new EventChoiceData { NodeType = -1, ChoiceType = 0, Texts = Array.Empty<string?>(), Images = Array.Empty<string?>(), Terms = Array.Empty<EventTerm>() },
        },
    };

    /// <summary>A training set: the training's own fields.</summary>
    public static EventChoiceSet Training() => new()
    {
        EventClass = "StatsTraining",
        EventName = "StatsTraining_Speed",
        NodeType = 14,
        Training = new TrainingFields { Stat = 3, Rarity = 16, RewardType = 1, ForwardId = "f-1" },
        Choices = new[]
        {
            new EventChoiceData
            {
                HasOnSelection = true,
                Interactable = true,
                NodeType = 14,
                ChoiceType = 14,
                SkaterId = "f-1",
                Images = new[] { "stat_speed" },
                Texts = new[] { "Speed", "+1" },
                TextRecipes = new[] { TextRecipe.StatName(1, false), null },
                ImageSources = new[] { ImageSource.Named(null, 0, 0, 32, 32) },
            },
        },
    };

    /// <summary>A General Manager's free agent: every field of the run file's model, colours, contract, levels and flavour keys.</summary>
    public static FreeAgentData Agent(string id = "fa-3b1c-0001") => new()
    {
        Id = id,
        OriginalId = "GM_Template_Ole",
        Name = "GM_Template_Ole(Clone)",
        FirstName = "Ole",
        LastName = "Bergström",
        Number = 17,
        Checking = 4,
        Speed = -2,
        ShotAccuracy = 5,
        ShotPower = 9999,
        InjuryCounter = 0.25f,
        IsLefty = true,
        IsBlack = false,
        HasOversizedHelmet = true,
        ForwardRarity = 2,
        SkaterSize = 1,
        DefaultSkaterType = 0,
        SkaterCategory = 3,
        Skins = FreeAgentData.SkinNames.Select((name, i) => i % 4 == 3 ? null : "skin_" + name).ToList(),
        Colors = Enumerable.Range(0, FreeAgentData.SchemeCount)
            .Select(i => i == 5 ? null : new ColorSchemeData(new ColorValue(1, 0.5f, 0, 1), new ColorValue(0.125f * i, 0.25f, -0.5f, 1), new ColorValue(2.5f, 0, 0, 0.5f)))
            .ToList(),
        AbilityId = "slapshot",
        AbilityLevel = 1,
        Talents = new[] { new FreeAgentTalent("speedy", 1), new FreeAgentTalent("temporary_contract", 1, 3), new FreeAgentTalent("bench_quest_mark", 2) },
        FlavorKey = "skaters/ole/flavor",
        PerkFlavorKey = "skaters/ole/perk",
    };

    /// <summary>A General Manager page: two free agents, every condition kind, objects the agents own.</summary>
    public static EventChoiceSet GeneralManager(int version = 0, EventSetStep step = EventSetStep.Open) => new()
    {
        Version = version,
        Step = step,
        EventClass = "GeneralManagerEvent",
        EventName = "GeneralManager",
        NodeType = 8,
        Point = (2, 4),
        ChoicesMade = version,
        Choices = new[]
        {
            new EventChoiceData
            {
                TranslatedButtonName = "Ole Bergström",
                HasOnSelection = true,
                Interactable = true,
                NodeType = 8,
                ChoiceType = 1,
                Terms = Array.Empty<EventTerm>(),
                FreeAgent = Agent().WithLook(RunPreviewSetTests.Look(), new DateDmy(29, 2, 1996), 1),
                Conditions = new[] { FreeAgentCondition.MaxGames(3, 0) },
                Object1 = new EventObject { Kind = EventObjectKind.Talent, Id = "temporary_contract", ClassName = "TemporaryContract", Level = 1, Owner = EventObjectOwner.FreeAgent, OwnerIndex = 1 },
                Object2 = new EventObject { Kind = EventObjectKind.Ability, Id = "slapshot", ClassName = "Slapshot", Level = 1, Owner = EventObjectOwner.FreeAgent, OwnerIndex = -1 },
                Images = new[] { "icon_bench_quest_mark" },
                Texts = new[] { "Mark the bench", "<color=#1FFF75>Win with him on the bench</color>" },
                TextRecipes = new[]
                {
                    TextRecipe.ObjectName(ChoiceObjectRef.AgentTalent(2)),
                    TextRecipe.ObjectDescription(ChoiceObjectRef.AgentTalent(2), false, false, EventTextRecipes.DescriptionColor),
                },
                ImageSources = new[] { ImageSource.Icon(ChoiceObjectRef.AgentTalent(2), "talent_atlas", 128, 256, 64, 64) },
            },
            new EventChoiceData
            {
                TranslatedButtonName = "Pia Lind",
                HasOnSelection = true,
                Interactable = false,
                NodeType = 8,
                ChoiceType = 1,
                FreeAgent = new FreeAgentData { Id = "fa-2", OriginalId = "fa-2", Name = "Pia", Talents = Array.Empty<FreeAgentTalent>() },
                Conditions = new[] { FreeAgentCondition.MaxGoals(5, 2), FreeAgentCondition.Injury(true), FreeAgentCondition.Random(0.125f), FreeAgentCondition.MaxGames(0, 0) },
            },
        },
    };

    /// <summary>A Fan Number 1 set with two size previews (one with a long tagged description, one bare).</summary>
    public static EventChoiceSet FanNumber1() => new()
    {
        EventClass = "FanNumber1Event",
        EventName = "FanNumber1",
        NodeType = 7,
        Point = (5, 1),
        Choices = new[]
        {
            new EventChoiceData
            {
                TranslatedButtonName = "Grow",
                HasOnSelection = true,
                Interactable = true,
                NodeType = 7,
                ChoiceType = 2,
                SkaterId = "f-1",
                Terms = Array.Empty<EventTerm>(),
                Object1 = new EventObject
                {
                    Kind = EventObjectKind.Preview,
                    Id = "fan_number_1_increase_size",
                    ClassName = "FanNumber1PreviewTalent",
                    Level = 1,
                    PreviewName = "Grow",
                    PreviewDescription = string.Concat(Enumerable.Repeat("<sprite name=\"size_up\"> Größer: +1 ", 40)),
                    PreviewIcon = "icon_size_up",
                    PreviewIconSource = ImageSource.Event("fan_icons", 0, 0, 48, 48),
                },
                Texts = new[] { "Grow" },
                SuppressRewardTracking = true,
                ButtonRecipe = TextRecipe.Term(EventTextRecipes.FanGrow),
                TextRecipes = new[] { TextRecipe.ObjectName(ChoiceObjectRef.Object1) },
            },
            new EventChoiceData
            {
                HasOnSelection = true,
                Interactable = true,
                NodeType = 7,
                ChoiceType = 2,
                SkaterId = "f-1",
                Object1 = new EventObject { Kind = EventObjectKind.Preview, Id = "fan_number_1_reduce_size", ClassName = "FanNumber1PreviewTalent", Level = 1, PreviewTraining = true },
            },
        },
    };

    /// <summary>A Nurse set: its choices open the procedure screen (a takeover), with long card texts.</summary>
    public static EventChoiceSet Nurse() => new()
    {
        EventClass = EventChoiceSet.TakeoverClass,
        EventName = "Nurse",
        NodeType = 5,
        Choices = new[]
        {
            new EventChoiceData
            {
                TranslatedButtonName = "Transplant",
                HasOnTakeover = true,
                Interactable = true,
                NodeType = 5,
                ChoiceType = 22,
                Texts = new[] { new string('t', EventChoiceSet.MaxTextBytes) },
                ButtonRecipe = TextRecipe.NurseProcedureName(0),
                TextRecipes = new[] { TextRecipe.NurseCardDescription(0, ChoiceObjectRef.None, 2, 3) },
            },
            new EventChoiceData { TranslatedButtonName = "No procedure", HasOnSelection = true, Interactable = true, NodeType = 5, ChoiceType = 29 },
        },
    };

    public static byte[] Encoded(EventChoiceSet set)
    {
        var writer = new BitWriter();
        set.Write(writer);
        return writer.ToArray();
    }

    private static EventChoiceSet? Read(byte[] bytes)
    {
        var reader = new BitReader(bytes);
        bool ok = EventChoiceSet.TryRead(ref reader, out EventChoiceSet? set);
        Assert.Equal(ok, set != null);
        return ok && reader.RemainingBits < 8 ? set : null;
    }

    [Fact]
    public void A_set_round_trips_field_by_field()
    {
        EventChoiceSet sent = Sample(version: 3, step: EventSetStep.Reroll);
        EventChoiceSet read = Read(Encoded(sent))!;

        Assert.NotNull(read);
        Assert.Equal(Encoded(sent), Encoded(read));
        Assert.Equal((3, EventSetStep.Reroll, "BlademasterEvent", "Blademaster_Default", 6), (read.Version, read.Step, read.EventClass, read.EventName, read.NodeType));
        Assert.Equal(sent.EventId, read.EventId);
        Assert.Equal((3, 7), read.Point);
        Assert.Equal((1, 0), (read.RerollCount, read.ChoicesMade));
        Assert.Null(read.Training);
        Assert.Equal(sent.DescTerms, read.DescTerms);
        Assert.Equal(3, read.Choices.Count);

        EventChoiceData first = read.Choices[0];
        Assert.True(first.HasOnSelection && first.Interactable && !first.HasOnTakeover && !first.SuppressRewardTracking);
        Assert.Equal((6, 4, "e1b9c7d2-0000-4000-8000-000000000001"), (first.NodeType, first.ChoiceType, first.SkaterId));
        Assert.Null(first.ButtonName);
        Assert.Equal((EventObjectKind.Ability, "slapshot", "Slapshot", 2, EventObjectOwner.None), (first.Object1!.Kind, first.Object1.Id, first.Object1.ClassName, first.Object1.Level, first.Object1.Owner));
        Assert.Equal((EventObjectOwner.Skater, first.SkaterId), (first.Object2!.Owner, first.Object2.OwnerId));
        Assert.Null(first.Images);
        Assert.Null(first.Texts);
        Assert.Null(first.StatBonuses);
        Assert.Null(first.Injury);

        EventChoiceData second = read.Choices[1];
        Assert.Equal(("v2ui/event_choice/upgrade", "Upgrade\nnow"), (second.ButtonName, second.TranslatedButtonName));
        Assert.False(second.Interactable);
        Assert.True(second.SuppressRewardTracking);
        Assert.Equal((true, 5, 4, -3), (second.Object1!.Chaos, second.Object1.TotalGames, second.Object1.RemainingGames, second.Object1.IntValue));
        Assert.Equal(EventObjectOwner.Team, second.Object2!.Owner);
        Assert.Equal(new[] { "icon_stat_speed", null }, second.Images);
        Assert.Equal(new[] { "Speed", "+2", null, string.Empty }, second.Texts);
        Assert.True(second.StatBonuses!.AreAlreadyCounted);
        Assert.Equal(sent.Choices[1].StatBonuses!.Bonuses, second.StatBonuses.Bonuses);
        Assert.Equal(new InjuryData(2, 3, 4, -1), second.Injury);

        EventChoiceData third = read.Choices[2];
        Assert.Equal(-1, third.NodeType);
        Assert.Empty(third.Texts!);
        Assert.Empty(third.Images!);
        Assert.Empty(third.Terms!);
        Assert.Null(third.Object1);
    }

    [Fact]
    public void A_training_set_round_trips_with_its_fields()
    {
        EventChoiceSet read = Read(Encoded(Training()))!;
        Assert.NotNull(read);
        Assert.Equal((3, 16, 1, "f-1"), (read.Training!.Stat, read.Training.Rarity, read.Training.RewardType, read.Training.ForwardId));
        Assert.Null(read.Point);
        Assert.Null(read.EventId);
        Assert.Null(read.DescTerms);
        Assert.True(Training().IsWithinLimits());
        Assert.Contains("StatsTraining", Training().ToString(), StringComparison.Ordinal);
        Assert.Contains("1 choice(s)", Training().ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_general_managers_free_agents_round_trip_field_by_field()
    {
        EventChoiceSet sent = GeneralManager(version: 1, step: EventSetStep.Page);
        EventChoiceSet read = Read(Encoded(sent))!;

        Assert.NotNull(read);
        Assert.Equal(Encoded(sent), Encoded(read));
        EventChoiceData first = read.Choices[0];
        Assert.Null(first.SkaterId);
        FreeAgentData agent = first.FreeAgent!;
        FreeAgentData expected = Agent();
        Assert.Equal((expected.Id, expected.OriginalId, expected.Name, expected.FirstName, expected.LastName), (agent.Id, agent.OriginalId, agent.Name, agent.FirstName, agent.LastName));
        Assert.Equal((17, 4, -2, 5, 9999, 0.25f), (agent.Number, agent.Checking, agent.Speed, agent.ShotAccuracy, agent.ShotPower, agent.InjuryCounter));
        Assert.Equal((true, false, true, 2, 1, 0, 3), (agent.IsLefty, agent.IsBlack, agent.HasOversizedHelmet, agent.ForwardRarity, agent.SkaterSize, agent.DefaultSkaterType, agent.SkaterCategory));
        Assert.Equal(expected.Skins, agent.Skins);
        Assert.Equal(expected.Colors, agent.Colors);
        Assert.Null(agent.Colors![5]);
        Assert.Equal(("slapshot", 1), (agent.AbilityId, agent.AbilityLevel));
        Assert.Equal(expected.Talents, agent.Talents);
        Assert.Equal(3, agent.Talents[1].ContractGames);
        Assert.Equal(-1, agent.Talents[0].ContractGames);
        Assert.Equal(("skaters/ole/flavor", "skaters/ole/perk", (string?)null), (agent.FlavorKey, agent.PerkFlavorKey, agent.PowerUpTextKey));
        Assert.Equal(new[] { FreeAgentCondition.MaxGames(3, 0) }, first.Conditions);
        Assert.Equal((EventObjectOwner.FreeAgent, 1), (first.Object1!.Owner, first.Object1.OwnerIndex));
        Assert.Equal((EventObjectOwner.FreeAgent, -1), (first.Object2!.Owner, first.Object2.OwnerIndex));
        Assert.Contains("the free agent's talent 2", first.Object1.ToString(), StringComparison.Ordinal);
        Assert.Contains("the free agent's ability", first.Object2.ToString(), StringComparison.Ordinal);

        EventChoiceData second = read.Choices[1];
        Assert.Null(second.FreeAgent!.Colors);
        Assert.Empty(second.FreeAgent.Talents);
        Assert.All(second.FreeAgent.Skins, Assert.Null);
        Assert.Equal(sent.Choices[1].Conditions, second.Conditions);
        Assert.Contains("max goals 5 (2 scored)", second.Conditions![0].ToString(), StringComparison.Ordinal);
        Assert.Contains("#17", agent.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_free_agents_empty_strings_stay_empty_and_its_nulls_null()
    {
        // The game's assets leave unset strings empty: a name, skin or flavour key that's "" comes back "", not null.
        var agent = new FreeAgentData
        {
            Id = "fa",
            OriginalId = "fa",
            Name = string.Empty,
            FirstName = string.Empty,
            LastName = null,
            Skins = FreeAgentData.SkinNames.Select((_, i) => i % 2 == 0 ? string.Empty : null).ToList(),
            FlavorKey = string.Empty,
            PerkFlavorKey = null,
            PowerUpTextKey = string.Empty,
        };
        var set = new EventChoiceSet { EventClass = "GeneralManagerEvent", EventName = "GM", NodeType = 8, Choices = new[] { new EventChoiceData { NodeType = 8, ChoiceType = 1, FreeAgent = agent } } };
        FreeAgentData read = Read(Encoded(set))!.Choices[0].FreeAgent!;
        Assert.Equal((string.Empty, string.Empty, (string?)null), (read.Name, read.FirstName, read.LastName));
        Assert.Equal(agent.Skins, read.Skins);
        Assert.Equal((string.Empty, (string?)null, string.Empty), (read.FlavorKey, read.PerkFlavorKey, read.PowerUpTextKey));
    }

    [Fact]
    public void A_fan_number_1_preview_round_trips_with_its_texts_and_flag()
    {
        EventChoiceSet read = Read(Encoded(FanNumber1()))!;
        Assert.NotNull(read);
        EventObject grow = read.Choices[0].Object1!;
        Assert.Equal((EventObjectKind.Preview, "fan_number_1_increase_size", "FanNumber1PreviewTalent", EventObjectOwner.None), (grow.Kind, grow.Id, grow.ClassName, grow.Owner));
        Assert.Equal(FanNumber1().Choices[0].Object1!.PreviewDescription, grow.PreviewDescription);
        Assert.Equal(("Grow", "icon_size_up", false), (grow.PreviewName, grow.PreviewIcon, grow.PreviewTraining));
        EventObject shrink = read.Choices[1].Object1!;
        Assert.Equal(((string?)null, (string?)null, (string?)null, true), (shrink.PreviewName, shrink.PreviewDescription, shrink.PreviewIcon, shrink.PreviewTraining));
        Assert.Contains("preview fan_number_1_increase_size", grow.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Only_a_nurses_choices_may_open_a_takeover()
    {
        EventChoiceSet read = Read(Encoded(Nurse()))!;
        Assert.NotNull(read);
        Assert.True(read.Choices[0].HasOnTakeover);
        Assert.Equal(EventChoiceSet.MaxTextBytes, read.Choices[0].Texts![0]!.Length);
        Assert.NotNull(MenuAttachment.EventChoices(5, Nurse()));

        // The same choices in another event's set are refused, when written and when read.
        var other = new EventChoiceSet { EventClass = "CoachEvent", EventName = "Coach", NodeType = 9, Choices = Nurse().Choices };
        Assert.Contains("takeover", other.Problem(), StringComparison.Ordinal);
        Assert.ThrowsAny<ArgumentException>(() => MenuAttachment.EventChoices(5, other));
        byte[] nurse = Encoded(Nurse());
        byte[] renamed = Encoded(new EventChoiceSet { EventClass = "NurseEvenX", EventName = "Nurse", NodeType = 5, Choices = new[] { Nurse().Choices[1] } });
        Assert.NotNull(Read(renamed));
        nurse[3 + "NurseEvent".Length] = (byte)'X'; // the class's last letter: "NurseEvenX"
        Assert.Null(Read(nurse));
    }

    [Theory]
    [InlineData(2, 2, false)] // no reroll left, or a page's: this set is the newest
    [InlineData(2, 1, true)] // a reroll ran since this set was read
    [InlineData(0, 0, false)]
    [InlineData(0, -1, false)] // read as the set reads it: never below none
    [InlineData(EventChoiceSet.MaxCount, EventChoiceSet.MaxCount + 5, false)] // nor above its cap
    public void A_reroll_since_a_set_was_read_shows_in_the_menus_count(int inSet, int now, bool expected)
    {
        Assert.Equal(expected, new EventChoiceSet { RerollCount = inSet }.RerolledSince(now));
    }

    [Fact]
    public void A_set_over_the_attachment_cap_is_refused_whole_and_one_under_it_goes()
    {
        string text = new('w', EventChoiceSet.MaxTextBytes);
        var choice = new EventChoiceData { Texts = Enumerable.Repeat<string?>(text, EventChoiceSet.MaxCustom).ToList(), Terms = Enumerable.Repeat(new EventTerm("n", text), EventChoiceSet.MaxTerms).ToList() };
        var over = new EventChoiceSet { EventClass = "CoachEvent", EventName = "Coach", Choices = Enumerable.Repeat(choice, EventChoiceSet.MaxChoices).ToList() };

        // Every field is within its own limit; the whole isn't.
        Assert.Contains($"at most {EventChoiceSet.MaxBytes}", over.Problem(), StringComparison.Ordinal);
        Assert.False(over.IsWithinLimits());
        Assert.ThrowsAny<ArgumentException>(() => MenuAttachment.EventChoices(1, over));

        var under = new EventChoiceSet { EventClass = "CoachEvent", EventName = "Coach", Choices = Enumerate(choice, 5) };
        Assert.Null(under.Problem());
        Assert.InRange(MenuAttachment.EventChoices(1, under).BodyLength, 200_000, EventChoiceSet.MaxBytes);

        static List<EventChoiceData> Enumerate(EventChoiceData c, int n) => Enumerable.Repeat(c, n).ToList();
    }

    [Fact]
    public void An_event_choices_body_of_format_1_or_2_is_refused()
    {
        byte[] body = Encoded(GeneralManager());
        Assert.Equal(3, MenuAttachment.EventChoicesFormat);
        Assert.NotNull(ReadAttachment(body, MenuAttachmentKind.EventChoices, 3));
        Assert.Null(ReadAttachment(body, MenuAttachmentKind.EventChoices, 2));
        Assert.Null(ReadAttachment(body, MenuAttachmentKind.EventChoices, 1));
        Assert.Null(ReadAttachment(Encoded(Sample()), MenuAttachmentKind.EventChoices, 2));
    }

    public static IEnumerable<object[]> OutOfLimits()
    {
        EventChoiceData good = Sample().Choices[0];
        EventChoiceSet With(Func<EventChoiceSet, EventChoiceSet> change) => change(Sample());
        EventChoiceSet Choice(EventChoiceData choice) => With(s => new EventChoiceSet { EventClass = s.EventClass, EventName = s.EventName, NodeType = s.NodeType, Choices = new[] { choice } });
        EventObject Object(EventObject o) => o;

        yield return new object[] { "no choices", With(s => new EventChoiceSet { EventClass = s.EventClass, EventName = s.EventName, Choices = Array.Empty<EventChoiceData>() }) };
        yield return new object[] { "nine choices", With(s => new EventChoiceSet { EventClass = s.EventClass, EventName = s.EventName, Choices = Enumerable.Repeat(good, EventChoiceSet.MaxChoices + 1).ToList() }) };
        yield return new object[] { "a null choice", Choice(null!) };
        yield return new object[] { "a version over 65535", new EventChoiceSet { Version = 65536, EventClass = "CoachEvent", EventName = "Coach", Choices = new[] { good } } };
        yield return new object[] { "a negative version", new EventChoiceSet { Version = -1, EventClass = "CoachEvent", EventName = "Coach", Choices = new[] { good } } };
        yield return new object[] { "no step", new EventChoiceSet { Step = EventSetStep.None, EventClass = "CoachEvent", EventName = "Coach", Choices = new[] { good } } };
        yield return new object[] { "an unknown step", new EventChoiceSet { Step = (EventSetStep)9, EventClass = "CoachEvent", EventName = "Coach", Choices = new[] { good } } };
        yield return new object[] { "a class that isn't a name", new EventChoiceSet { EventClass = "Coach Event", EventName = "Coach", Choices = new[] { good } } };
        yield return new object[] { "an empty class", new EventChoiceSet { EventClass = string.Empty, EventName = "Coach", Choices = new[] { good } } };
        yield return new object[] { "a class starting with a digit", new EventChoiceSet { EventClass = "9Coach", EventName = "Coach", Choices = new[] { good } } };
        yield return new object[] { "an empty name", new EventChoiceSet { EventClass = "CoachEvent", EventName = string.Empty, Choices = new[] { good } } };
        yield return new object[] { "a long name", new EventChoiceSet { EventClass = "CoachEvent", EventName = new string('n', EventChoiceSet.MaxNameBytes + 1), Choices = new[] { good } } };
        yield return new object[] { "a name with a line break", new EventChoiceSet { EventClass = "CoachEvent", EventName = "Coa\nch", Choices = new[] { good } } };
        yield return new object[] { "an empty id", new EventChoiceSet { EventClass = "CoachEvent", EventName = "Coach", EventId = string.Empty, Choices = new[] { good } } };
        yield return new object[] { "a node type out of range", new EventChoiceSet { EventClass = "CoachEvent", EventName = "Coach", NodeType = 64, Choices = new[] { good } } };
        yield return new object[] { "a node type below -1", new EventChoiceSet { EventClass = "CoachEvent", EventName = "Coach", NodeType = -2, Choices = new[] { good } } };
        yield return new object[] { "a point off the map", new EventChoiceSet { EventClass = "CoachEvent", EventName = "Coach", Point = (2, 1001), Choices = new[] { good } } };
        yield return new object[] { "too many rerolls", new EventChoiceSet { EventClass = "CoachEvent", EventName = "Coach", RerollCount = 100, Choices = new[] { good } } };
        yield return new object[] { "negative choices made", new EventChoiceSet { EventClass = "CoachEvent", EventName = "Coach", ChoicesMade = -1, Choices = new[] { good } } };
        yield return new object[] { "a training stat out of range", new EventChoiceSet { EventClass = "StatsTraining", EventName = "T", Training = new TrainingFields { Stat = 64 }, Choices = new[] { good } } };
        yield return new object[] { "a training skater with a tab", new EventChoiceSet { EventClass = "StatsTraining", EventName = "T", Training = new TrainingFields { ForwardId = "a\tb" }, Choices = new[] { good } } };
        yield return new object[] { "17 description terms", new EventChoiceSet { EventClass = "CoachEvent", EventName = "Coach", DescTerms = Enumerable.Repeat(new EventTerm("a", "b"), 17).ToList(), Choices = new[] { good } } };
        yield return new object[] { "a null description term", new EventChoiceSet { EventClass = "CoachEvent", EventName = "Coach", DescTerms = new EventTerm[] { null! }, Choices = new[] { good } } };
        yield return new object[] { "a term value over 2048 bytes", new EventChoiceSet { EventClass = "CoachEvent", EventName = "Coach", DescTerms = new[] { new EventTerm("a", new string('é', 1025)) }, Choices = new[] { good } } };
        yield return new object[] { "a lone surrogate", new EventChoiceSet { EventClass = "CoachEvent", EventName = "Coach", DescTerms = new[] { new EventTerm("a", "\uD800") }, Choices = new[] { good } } };
        yield return new object[] { "a takeover", Choice(new EventChoiceData { HasOnTakeover = true }) };
        yield return new object[] { "a choice type over 255", Choice(new EventChoiceData { ChoiceType = 256 }) };
        yield return new object[] { "a negative choice type", Choice(new EventChoiceData { ChoiceType = -1 }) };
        yield return new object[] { "a choice's node type out of range", Choice(new EventChoiceData { NodeType = 100 }) };
        yield return new object[] { "a button name with a NUL", Choice(new EventChoiceData { ButtonName = "a\0b" }) };
        yield return new object[] { "an empty skater id", Choice(new EventChoiceData { SkaterId = string.Empty }) };
        yield return new object[] { "nine images", Choice(new EventChoiceData { Images = Enumerable.Repeat<string?>("x", 9).ToList() }) };
        yield return new object[] { "nine texts", Choice(new EventChoiceData { Texts = Enumerable.Repeat<string?>("x", 9).ToList() }) };
        yield return new object[] { "an empty image name", Choice(new EventChoiceData { Images = new[] { string.Empty } }) };
        yield return new object[] { "17 stat bonuses", Choice(new EventChoiceData { StatBonuses = new StatBonusSet(false, Enumerable.Repeat(new StatBonusData(1, 1, 0, 1, false, false), 17).ToList()) }) };
        yield return new object[] { "a stat bonus out of range", Choice(new EventChoiceData { StatBonuses = new StatBonusSet(false, new[] { new StatBonusData(1, 1, 0, 1000, false, false) }) }) };
        yield return new object[] { "a stat bonus's skater type out of range", Choice(new EventChoiceData { StatBonuses = new StatBonusSet(false, new[] { new StatBonusData(16, 1, 0, 1, false, false) }) }) };
        yield return new object[] { "an injury out of range", Choice(new EventChoiceData { Injury = new InjuryData(1, 10000, 1, 1) }) };
        yield return new object[] { "an injury type out of range", Choice(new EventChoiceData { Injury = new InjuryData(64, 1, 1, 1) }) };
        yield return new object[] { "an object of no kind", Choice(new EventChoiceData { Object1 = Object(new EventObject { Id = "x", ClassName = "X" }) }) };
        yield return new object[] { "an object without an id", Choice(new EventChoiceData { Object1 = Object(new EventObject { Kind = EventObjectKind.Relic, Id = string.Empty, ClassName = "X" }) }) };
        yield return new object[] { "an object's class that isn't a name", Choice(new EventChoiceData { Object2 = Object(new EventObject { Kind = EventObjectKind.Relic, Id = "x", ClassName = "X.Y" }) }) };
        yield return new object[] { "an object's level out of range", Choice(new EventChoiceData { Object1 = Object(new EventObject { Kind = EventObjectKind.Talent, Id = "x", ClassName = "X", Level = 100 }) }) };
        yield return new object[] { "a skater's object without its skater", Choice(new EventChoiceData { Object1 = Object(new EventObject { Kind = EventObjectKind.Talent, Id = "x", ClassName = "X", Owner = EventObjectOwner.Skater }) }) };
        yield return new object[] { "a team's object with a skater", Choice(new EventChoiceData { Object1 = Object(new EventObject { Kind = EventObjectKind.Relic, Id = "x", ClassName = "X", Owner = EventObjectOwner.Team, OwnerId = "s" }) }) };
        yield return new object[] { "a team's talent", Choice(new EventChoiceData { Object1 = Object(new EventObject { Kind = EventObjectKind.Talent, Id = "x", ClassName = "X", Owner = EventObjectOwner.Team }) }) };
        yield return new object[] { "a skater's relic", Choice(new EventChoiceData { Object1 = Object(new EventObject { Kind = EventObjectKind.Relic, Id = "x", ClassName = "X", Owner = EventObjectOwner.Skater, OwnerId = "s" }) }) };
        yield return new object[] { "an unknown owner", Choice(new EventChoiceData { Object1 = Object(new EventObject { Kind = EventObjectKind.Relic, Id = "x", ClassName = "X", Owner = (EventObjectOwner)9 }) }) };
        yield return new object[] { "a chaos talent", Choice(new EventChoiceData { Object1 = Object(new EventObject { Kind = EventObjectKind.Talent, Id = "x", ClassName = "X", Chaos = true }) }) };
        yield return new object[] { "a chaos relic's games out of range", Choice(new EventChoiceData { Object1 = Object(new EventObject { Kind = EventObjectKind.Relic, Id = "x", ClassName = "X", Chaos = true, TotalGames = 1000 }) }) };
        yield return new object[] { "games on a plain relic", Choice(new EventChoiceData { Object1 = Object(new EventObject { Kind = EventObjectKind.Relic, Id = "x", ClassName = "X", TotalGames = 1 }) }) };
        yield return new object[] { "an object of an unknown kind", Choice(new EventChoiceData { Object1 = Object(new EventObject { Kind = (EventObjectKind)5, Id = "x", ClassName = "X" }) }) };
        yield return new object[] { "a takeover outside a Nurse's set", Choice(new EventChoiceData { HasOnTakeover = true, ChoiceType = 22 }) };
        yield return new object[] { "a text over 2048 bytes", Choice(new EventChoiceData { Texts = new[] { new string('x', EventChoiceSet.MaxTextBytes + 1) } }) };

        // Previews.
        EventObject Preview(Func<EventObject, EventObject> change) =>
            change(new EventObject { Kind = EventObjectKind.Preview, Id = "fan_number_1_increase_size", ClassName = "FanNumber1PreviewTalent", Level = 1, PreviewName = "Grow" });
        yield return new object[] { "a preview's name on a talent", Choice(new EventChoiceData { Object1 = Object(new EventObject { Kind = EventObjectKind.Talent, Id = "x", ClassName = "X", PreviewName = "Grow" }) }) };
        yield return new object[] { "a preview's flag on a relic", Choice(new EventChoiceData { Object1 = Object(new EventObject { Kind = EventObjectKind.Relic, Id = "x", ClassName = "X", PreviewTraining = true }) }) };
        yield return new object[] { "a preview's icon on an ability", Choice(new EventChoiceData { Object1 = Object(new EventObject { Kind = EventObjectKind.Ability, Id = "x", ClassName = "X", PreviewIcon = "i" }) }) };
        yield return new object[] { "a skater's preview", Choice(new EventChoiceData { Object1 = Preview(p => new EventObject { Kind = p.Kind, Id = p.Id, ClassName = p.ClassName, Owner = EventObjectOwner.Skater, OwnerId = "s" }) }) };
        yield return new object[] { "a chaos preview", Choice(new EventChoiceData { Object1 = Preview(p => new EventObject { Kind = p.Kind, Id = p.Id, ClassName = p.ClassName, Chaos = true, TotalGames = 1 }) }) };
        yield return new object[] { "a numbered preview", Choice(new EventChoiceData { Object1 = Preview(p => new EventObject { Kind = p.Kind, Id = p.Id, ClassName = p.ClassName, IntValue = 2 }) }) };
        yield return new object[] { "a preview's name over 2048 bytes", Choice(new EventChoiceData { Object1 = Preview(p => new EventObject { Kind = p.Kind, Id = p.Id, ClassName = p.ClassName, PreviewName = new string('n', 2049) }) }) };
        yield return new object[] { "a preview's description with a NUL", Choice(new EventChoiceData { Object1 = Preview(p => new EventObject { Kind = p.Kind, Id = p.Id, ClassName = p.ClassName, PreviewDescription = "a\0b" }) }) };
        yield return new object[] { "a preview's icon with a line break", Choice(new EventChoiceData { Object1 = Preview(p => new EventObject { Kind = p.Kind, Id = p.Id, ClassName = p.ClassName, PreviewIcon = "a\nb" }) }) };

        // Free agents and the objects they own.
        FreeAgentData agent = Agent();
        EventChoiceData AgentChoice(FreeAgentData a, IReadOnlyList<FreeAgentCondition>? conditions = null, EventObject? object1 = null) =>
            new() { NodeType = 8, ChoiceType = 1, FreeAgent = a, Conditions = conditions, Object1 = object1 };
        EventObject Owned(EventObjectKind kind, string id, int index) => new() { Kind = kind, Id = id, ClassName = "X", Level = 1, Owner = EventObjectOwner.FreeAgent, OwnerIndex = index };
        yield return new object[] { "a free agent's object without a free agent", Choice(new EventChoiceData { Object1 = Owned(EventObjectKind.Talent, "speedy", 0) }) };
        yield return new object[] { "a free agent's talent past its talents", Choice(AgentChoice(agent, object1: Owned(EventObjectKind.Talent, "speedy", 3))) };
        yield return new object[] { "a free agent's talent at 16", Choice(AgentChoice(agent, object1: Owned(EventObjectKind.Talent, "speedy", FreeAgentData.MaxTalents))) };
        yield return new object[] { "a free agent's talent with another id", Choice(AgentChoice(agent, object1: Owned(EventObjectKind.Talent, "slow", 0))) };
        yield return new object[] { "a free agent's ability at a talent's index", Choice(AgentChoice(agent, object1: Owned(EventObjectKind.Ability, "slapshot", 0))) };
        yield return new object[] { "a free agent's talent at the ability's index", Choice(AgentChoice(agent, object1: Owned(EventObjectKind.Talent, "slapshot", -1))) };
        yield return new object[] { "a free agent's relic", Choice(AgentChoice(agent, object1: Owned(EventObjectKind.Relic, "speedy", 0))) };
        yield return new object[] { "a free agent's object with a skater", Choice(AgentChoice(agent, object1: new EventObject { Kind = EventObjectKind.Talent, Id = "speedy", ClassName = "X", Owner = EventObjectOwner.FreeAgent, OwnerId = "s" })) };
        yield return new object[] { "an index on a team's object", Choice(new EventChoiceData { Object1 = new EventObject { Kind = EventObjectKind.Relic, Id = "x", ClassName = "X", Owner = EventObjectOwner.Team, OwnerIndex = 1 } }) };
        yield return new object[] { "a run skater and a free agent", Choice(new EventChoiceData { SkaterId = "f-1", FreeAgent = agent }) };
        yield return new object[] { "a free agent without an id", Choice(AgentChoice(new FreeAgentData { OriginalId = agent.OriginalId, Name = agent.Name })) };
        yield return new object[] { "a free agent without a template", Choice(AgentChoice(new FreeAgentData { Id = "a", Name = "n" })) };
        yield return new object[] { "a free agent's name with a tab", Choice(AgentChoice(new FreeAgentData { Id = "a", OriginalId = "o", Name = "n", FirstName = "a\tb" })) };
        yield return new object[] { "17 talents", Choice(AgentChoice(new FreeAgentData { Id = "a", OriginalId = "o", Name = "n", Talents = Enumerable.Repeat(new FreeAgentTalent("t", 1), 17).ToList() })) };
        yield return new object[] { "a null talent", Choice(AgentChoice(new FreeAgentData { Id = "a", OriginalId = "o", Name = "n", Talents = new FreeAgentTalent[] { null! } })) };
        yield return new object[] { "a talent at level 100", Choice(AgentChoice(new FreeAgentData { Id = "a", OriginalId = "o", Name = "n", Talents = new[] { new FreeAgentTalent("t", 100) } })) };
        yield return new object[] { "a contract of 1000 games", Choice(AgentChoice(new FreeAgentData { Id = "a", OriginalId = "o", Name = "n", Talents = new[] { new FreeAgentTalent("t", 1, 1000) } })) };
        yield return new object[] { "a contract of -2 games", Choice(AgentChoice(new FreeAgentData { Id = "a", OriginalId = "o", Name = "n", Talents = new[] { new FreeAgentTalent("t", 1, -2) } })) };
        yield return new object[] { "14 skins", Choice(AgentChoice(new FreeAgentData { Id = "a", OriginalId = "o", Name = "n", Skins = new string?[14] })) };
        yield return new object[] { "a skin with a line break", Choice(AgentChoice(new FreeAgentData { Id = "a", OriginalId = "o", Name = "n", Skins = FreeAgentData.SkinNames.Select(_ => (string?)"a\nb").ToList() })) };
        yield return new object[] { "an empty template", Choice(AgentChoice(new FreeAgentData { Id = "a", OriginalId = string.Empty, Name = "n" })) };
        yield return new object[] { "an empty ability", Choice(AgentChoice(new FreeAgentData { Id = "a", OriginalId = "o", Name = "n", AbilityId = string.Empty })) };
        yield return new object[] { "an empty talent id", Choice(AgentChoice(new FreeAgentData { Id = "a", OriginalId = "o", Name = "n", Talents = new[] { new FreeAgentTalent(string.Empty, 1) } })) };
        yield return new object[] { "7 colour schemes", Choice(AgentChoice(new FreeAgentData { Id = "a", OriginalId = "o", Name = "n", Colors = new ColorSchemeData?[7] })) };
        ColorValue nan = new(float.NaN, 0, 0, 1);
        yield return new object[] { "a colour that isn't a number", Choice(AgentChoice(new FreeAgentData { Id = "a", OriginalId = "o", Name = "n", Colors = Enumerable.Repeat<ColorSchemeData?>(new ColorSchemeData(nan, nan, nan), 8).ToList() })) };
        yield return new object[] { "an endless colour", Choice(AgentChoice(new FreeAgentData { Id = "a", OriginalId = "o", Name = "n", Colors = new ColorSchemeData?[] { null, null, null, null, null, null, null, new(default, default, new ColorValue(0, float.PositiveInfinity, 0, 0)) } })) };
        yield return new object[] { "an injury counter that isn't a number", Choice(AgentChoice(new FreeAgentData { Id = "a", OriginalId = "o", Name = "n", InjuryCounter = float.NaN })) };
        yield return new object[] { "a stat over 9999", Choice(AgentChoice(new FreeAgentData { Id = "a", OriginalId = "o", Name = "n", Speed = 10000 })) };
        yield return new object[] { "a negative number", Choice(AgentChoice(new FreeAgentData { Id = "a", OriginalId = "o", Name = "n", Number = -1 })) };
        yield return new object[] { "a size over 255", Choice(AgentChoice(new FreeAgentData { Id = "a", OriginalId = "o", Name = "n", SkaterSize = 256 })) };
        yield return new object[] { "an ability level without an ability", Choice(AgentChoice(new FreeAgentData { Id = "a", OriginalId = "o", Name = "n", AbilityLevel = 1 })) };
        yield return new object[] { "a flavour key with a line break", Choice(AgentChoice(new FreeAgentData { Id = "a", OriginalId = "o", Name = "n", PerkFlavorKey = "a\nb" })) };

        // Leaving conditions.
        yield return new object[] { "9 conditions", Choice(AgentChoice(agent, Enumerable.Repeat(FreeAgentCondition.MaxGames(3, 0), 9).ToList())) };
        yield return new object[] { "a null condition", Choice(AgentChoice(agent, new FreeAgentCondition[] { null! })) };
        yield return new object[] { "a condition of no kind", Choice(AgentChoice(agent, new[] { new FreeAgentCondition(FreeAgentConditionKind.None, 0, 0, false, 0) })) };
        yield return new object[] { "a condition of an unknown kind", Choice(AgentChoice(agent, new[] { new FreeAgentCondition((FreeAgentConditionKind)5, 0, 0, false, 0) })) };
        yield return new object[] { "a chance over 1", Choice(AgentChoice(agent, new[] { FreeAgentCondition.Random(1.5f) })) };
        yield return new object[] { "a chance that isn't a number", Choice(AgentChoice(agent, new[] { FreeAgentCondition.Random(float.NaN) })) };
        yield return new object[] { "max games over 9999", Choice(AgentChoice(agent, new[] { FreeAgentCondition.MaxGames(10000, 0) })) };
        yield return new object[] { "max goals with a flag", Choice(AgentChoice(agent, new[] { new FreeAgentCondition(FreeAgentConditionKind.MaxGoals, 3, 0, true, 0) })) };
        yield return new object[] { "an injury with numbers", Choice(AgentChoice(agent, new[] { new FreeAgentCondition(FreeAgentConditionKind.Injury, 1, 0, true, 0) })) };
        yield return new object[] { "a random condition with a count", Choice(AgentChoice(agent, new[] { new FreeAgentCondition(FreeAgentConditionKind.Random, 0, 1, false, 0.5f) })) };
    }

    [Theory]
    [MemberData(nameof(OutOfLimits))]
    public void Sets_out_of_their_limits_are_refused_when_written_and_never_sent(string what, EventChoiceSet set)
    {
        var writer = new BitWriter();
        ArgumentException thrown = Assert.ThrowsAny<ArgumentException>(() => set.Write(writer));
        Assert.False(string.IsNullOrEmpty(thrown.Message), what);
        Assert.False(set.IsWithinLimits(), what);
        Assert.NotNull(set.Problem());
        Assert.ThrowsAny<ArgumentException>(() => MenuAttachment.EventChoices(4, set));
    }

    [Fact]
    public void A_set_with_every_count_and_name_at_its_limit_is_sent_and_reads()
    {
        // Texts a quarter of their cap: a set with every text at its cap too is over the attachment's (tested above).
        string text = new('w', EventChoiceSet.MaxTextBytes / 4);
        string name = new('n', EventChoiceSet.MaxNameBytes);
        List<EventTerm> terms = Enumerable.Repeat(new EventTerm(name, text), EventChoiceSet.MaxTerms).ToList();
        var obj = new EventObject { Kind = EventObjectKind.Relic, Id = name, ClassName = new string('C', 64), Level = 99, IntValue = int.MinValue, Chaos = true, TotalGames = 999, RemainingGames = 999 };
        var choice = new EventChoiceData
        {
            ButtonName = text,
            TranslatedButtonName = text,
            HasOnSelection = true,
            Interactable = true,
            NodeType = 63,
            ChoiceType = 255,
            Terms = terms,
            SkaterId = name,
            Object1 = obj,
            Object2 = obj,
            Images = Enumerable.Repeat<string?>(name, EventChoiceSet.MaxCustom).ToList(),
            Texts = Enumerable.Repeat<string?>(text, EventChoiceSet.MaxCustom).ToList(),
            StatBonuses = new StatBonusSet(true, Enumerable.Repeat(new StatBonusData(15, 63, 63, -999, true, true), EventChoiceSet.MaxStatBonuses).ToList()),
            Injury = new InjuryData(63, 9999, 9999, -1),
            SuppressRewardTracking = true,
        };
        var largest = new EventChoiceSet
        {
            Version = EventChoiceSet.MaxVersion,
            Step = EventSetStep.Page,
            EventClass = new string('E', 64),
            EventName = name,
            EventId = name,
            NodeType = -1,
            Point = (EventChoiceSet.MaxCoordinate, -1),
            RerollCount = EventChoiceSet.MaxCount,
            ChoicesMade = EventChoiceSet.MaxCount,
            Training = new TrainingFields { Stat = 63, Rarity = 1023, RewardType = 63, ForwardId = name },
            DescTerms = terms,
            Choices = Enumerable.Repeat(choice, EventChoiceSet.MaxChoices).ToList(),
        };

        Assert.Null(largest.Problem());
        MenuAttachment attachment = MenuAttachment.EventChoices(uint.MaxValue, largest);
        Assert.InRange(attachment.BodyLength, 100_000, MenuAttachment.MaxBodyBytes);
        var writer = new BitWriter();
        Assert.True(MenuAttachmentMessage.Write(writer, attachment));
        Assert.InRange(writer.ByteLength, 1, MenuAttachmentMessage.MaxMessageBytes);
        var reader = new BitReader(writer.AsSpan());
        Assert.True(MenuAttachmentMessage.TryRead(ref reader, out MenuAttachment? read));
        Assert.True(read!.TryGetEventChoices(out EventChoiceSet? set));
        Assert.Equal(Encoded(largest), Encoded(set!));
    }

    [Fact]
    public void Free_agents_at_every_limit_are_sent_and_read()
    {
        string name = new('n', EventChoiceSet.MaxNameBytes);
        var agent = new FreeAgentData
        {
            Id = name,
            OriginalId = name,
            Name = name,
            FirstName = name,
            LastName = name,
            Number = 9999,
            Checking = -999,
            Speed = 9999,
            ShotAccuracy = -999,
            ShotPower = 9999,
            InjuryCounter = 1e6f,
            ForwardRarity = 255,
            SkaterSize = 255,
            DefaultSkaterType = 255,
            SkaterCategory = 255,
            Skins = FreeAgentData.SkinNames.Select(_ => (string?)name).ToList(),
            Colors = Enumerable.Repeat<ColorSchemeData?>(new ColorSchemeData(new(1000, -1000, 0, 1), new(0, 0, 0, 0), new(1, 1, 1, 1)), FreeAgentData.SchemeCount).ToList(),
            AbilityId = name,
            AbilityLevel = 99,
            Talents = Enumerable.Range(0, FreeAgentData.MaxTalents).Select(i => new FreeAgentTalent(name, 99, i % 2 == 0 ? 999 : -1)).ToList(),
            FlavorKey = name,
            PerkFlavorKey = name,
            PowerUpTextKey = name,
        };
        var conditions = Enumerable.Repeat(FreeAgentCondition.MaxGoals(9999, -1), EventChoiceSet.MaxConditions).ToList();
        var choice = new EventChoiceData
        {
            NodeType = 8,
            ChoiceType = 1,
            FreeAgent = agent,
            Conditions = conditions,
            Object1 = new EventObject { Kind = EventObjectKind.Talent, Id = name, ClassName = "C", Level = 99, Owner = EventObjectOwner.FreeAgent, OwnerIndex = FreeAgentData.MaxTalents - 1 },
            Object2 = new EventObject { Kind = EventObjectKind.Ability, Id = name, ClassName = "C", Level = 99, Owner = EventObjectOwner.FreeAgent, OwnerIndex = -1 },
        };
        var set = new EventChoiceSet { EventClass = "GeneralManagerEvent", EventName = "GM", NodeType = 8, Choices = Enumerable.Repeat(choice, EventChoiceSet.MaxChoices).ToList() };

        Assert.Null(set.Problem());
        MenuAttachment attachment = MenuAttachment.EventChoices(7, set);
        Assert.True(attachment.TryGetEventChoices(out EventChoiceSet? read));
        Assert.Equal(Encoded(set), Encoded(read!));
        Assert.Equal(FreeAgentData.MaxTalents, read!.Choices[7].FreeAgent!.Talents.Count);
    }

    [Fact]
    public void A_set_cut_short_is_refused_at_every_length()
    {
        foreach (EventChoiceSet set in new[] { Sample(), Training(), GeneralManager(), FanNumber1(), Nurse() })
        {
            byte[] bytes = Encoded(set);
            for (int length = 0; length < bytes.Length; length++)
            {
                Assert.Null(Read(bytes[..length]));
            }

            Assert.NotNull(Read(bytes));
        }
    }

    [Fact]
    public void Crafted_sets_that_break_a_rule_are_refused()
    {
        // A choice count over the cap, written by hand after a valid head.
        var writer = new BitWriter();
        WriteHead(writer);
        writer.WriteVarUInt(EventChoiceSet.MaxChoices + 1);
        Assert.Null(Read(writer.ToArray()));

        // A text whose length is more than what follows.
        writer = new BitWriter();
        writer.WriteUInt16(0);
        writer.WriteByte((byte)EventSetStep.Open);
        writer.WriteVarUInt(10);
        writer.WriteBytes("Coach"u8);
        Assert.Null(Read(writer.ToArray()));

        // A name over the cap.
        writer = new BitWriter();
        writer.WriteUInt16(0);
        writer.WriteByte((byte)EventSetStep.Open);
        writer.WriteVarUInt(EventChoiceSet.MaxNameBytes + 1);
        writer.WriteBytes(new byte[EventChoiceSet.MaxNameBytes + 1]);
        Assert.Null(Read(writer.ToArray()));

        // Bytes that aren't UTF-8 in the event's class.
        writer = new BitWriter();
        writer.WriteUInt16(0);
        writer.WriteByte((byte)EventSetStep.Open);
        writer.WriteVarUInt(2);
        writer.WriteBytes(new byte[] { 0xC3, 0x28 });
        Assert.Null(Read(writer.ToArray()));

        // A term count over the cap in the description.
        writer = new BitWriter();
        WriteHead(writer, terms: false);
        writer.WriteBool(true);
        writer.WriteVarUInt(EventChoiceSet.MaxTerms + 1);
        Assert.Null(Read(writer.ToArray()));

        // A number that doesn't fit an int.
        writer = new BitWriter();
        writer.WriteUInt16(0);
        writer.WriteByte((byte)EventSetStep.Open);
        EventWireText(writer, "CoachEvent");
        EventWireText(writer, "Coach");
        writer.WriteBool(false);
        writer.WriteVarInt(long.MaxValue);
        Assert.Null(Read(writer.ToArray()));
    }

    [Fact]
    public void An_attachment_of_event_choices_round_trips_and_is_only_event_choices()
    {
        MenuAttachment sent = MenuAttachment.EventChoices(13, Sample());
        Assert.Equal(MenuAttachmentKind.EventChoices, sent.Kind);
        Assert.Equal(MenuAttachment.EventChoicesFormat, sent.Format);
        Assert.Contains("event choices for #13", sent.ToString(), StringComparison.Ordinal);
        Assert.False(sent.TryGetRunSummary(out _));

        var writer = new BitWriter();
        Assert.True(MenuAttachmentMessage.Write(writer, sent));
        var reader = new BitReader(writer.AsSpan());
        Assert.True(MenuAttachmentMessage.TryRead(ref reader, out MenuAttachment? read));
        Assert.Equal(sent.ContentHash, read!.ContentHash);
        Assert.True(read.TryGetEventChoices(out EventChoiceSet? set));
        Assert.Equal(Encoded(Sample()), Encoded(set!));

        // A run summary isn't event choices, and serial 0 is no menu.
        Assert.False(MenuAttachment.RunSummary(13, RunHistoryDataTests.Sample()).TryGetEventChoices(out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => MenuAttachment.EventChoices(0, Sample()));
        Assert.Throws<ArgumentNullException>(() => MenuAttachment.EventChoices(1, null!));
    }

    [Fact]
    public void Event_choice_attachments_with_a_wrong_format_or_body_are_refused()
    {
        byte[] body = Encoded(Sample());
        Assert.NotNull(ReadAttachment(body, MenuAttachmentKind.EventChoices, MenuAttachment.EventChoicesFormat));
        Assert.Null(ReadAttachment(body, MenuAttachmentKind.EventChoices, 0));
        Assert.Null(ReadAttachment(body, MenuAttachmentKind.EventChoices, MenuAttachment.EventChoicesFormat + 1));
        Assert.Null(ReadAttachment(body.Concat(new byte[] { 0 }).ToArray(), MenuAttachmentKind.EventChoices, MenuAttachment.EventChoicesFormat));
        Assert.Null(ReadAttachment(body[..^1], MenuAttachmentKind.EventChoices, MenuAttachment.EventChoicesFormat));

        // Each kind's body under the other kind.
        Assert.Null(ReadAttachment(body, MenuAttachmentKind.RunSummary, MenuAttachment.RunSummaryFormat));
        Assert.Null(ReadAttachment(RunHistoryDataTests.Encoded(RunHistoryDataTests.Sample()), MenuAttachmentKind.EventChoices, MenuAttachment.EventChoicesFormat));
    }

    [Fact]
    public void Changed_and_random_bytes_never_throw_and_what_reads_is_within_the_limits()
    {
        byte[][] seeds = { Encoded(Sample()), Encoded(Training()), Encoded(GeneralManager()), Encoded(FanNumber1()), Encoded(Nurse()) };
        var random = new Random(2026);
        int read = 0;
        for (int round = 0; round < 10000; round++)
        {
            byte[] bytes = (byte[])seeds[round % seeds.Length].Clone();
            switch (random.Next(4))
            {
                case 0:
                    for (int flips = random.Next(1, 6); flips > 0; flips--)
                    {
                        bytes[random.Next(bytes.Length)] ^= (byte)(1 << random.Next(8));
                    }

                    break;
                case 1:
                    bytes[random.Next(bytes.Length)] = new byte[] { 0x00, 0x01, 0x7F, 0x80, 0xFE, 0xFF }[random.Next(6)];
                    break;
                case 2:
                    bytes = bytes[..random.Next(bytes.Length)];
                    break;
                default:
                    bytes = new byte[random.Next(0, 200)];
                    random.NextBytes(bytes);
                    break;
            }

            var reader = new BitReader(bytes);
            if (!EventChoiceSet.TryRead(ref reader, out EventChoiceSet? set))
            {
                Assert.Null(set);
                continue;
            }

            // Whatever reads is a set that could have been sent, and writes back to what was read.
            read++;
            Assert.Null(set!.Problem());
            Assert.InRange(set.Choices.Count, 1, EventChoiceSet.MaxChoices);
            byte[] again = Encoded(set);
            var reread = new BitReader(again);
            Assert.True(EventChoiceSet.TryRead(ref reread, out EventChoiceSet? second));
            Assert.Equal(again, Encoded(second!));
            _ = set.ToString();
        }

        Assert.True(read > 0, "some changed sets should still read");
    }

    private static MenuAttachment? ReadAttachment(byte[] body, MenuAttachmentKind kind, byte format)
    {
        var writer = new BitWriter();
        writer.WriteUInt32(13);
        writer.WriteByte((byte)kind);
        writer.WriteByte(format);
        writer.WriteVarUInt((ulong)body.Length);
        byte[] deflated = Compression.Deflate(body);
        writer.WriteVarUInt((ulong)deflated.Length);
        writer.WriteBytes(deflated);
        var reader = new BitReader(writer.AsSpan());
        return MenuAttachmentMessage.TryRead(ref reader, out MenuAttachment? read) ? read : null;
    }

    /// <summary>A valid set's fields up to its description's terms (and those too, as none, unless <paramref name="terms"/> is false).</summary>
    private static void WriteHead(BitWriter writer, bool terms = true)
    {
        writer.WriteUInt16(0);
        writer.WriteByte((byte)EventSetStep.Open);
        EventWireText(writer, "CoachEvent");
        EventWireText(writer, "Coach");
        writer.WriteBool(false); // no id
        writer.WriteVarInt(9);
        writer.WriteBool(false); // no point
        writer.WriteVarUInt(0);
        writer.WriteVarUInt(0);
        writer.WriteBool(false); // no training
        if (terms)
        {
            writer.WriteBool(false); // no description terms
        }
    }

    private static void EventWireText(BitWriter writer, string text)
    {
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(text);
        writer.WriteVarUInt((ulong)bytes.Length);
        writer.WriteBytes(bytes);
    }
}
