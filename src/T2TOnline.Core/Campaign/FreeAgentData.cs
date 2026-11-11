using System;
using System.Collections.Generic;
using System.Globalization;
using T2TOnline.Core.Serialization;

namespace T2TOnline.Core.Campaign;

/// <summary>What a free agent's leaving condition is (the game's concrete <c>LeavingCondition</c> classes). Values are part of the protocol: only add.</summary>
public enum FreeAgentConditionKind : byte
{
    /// <summary>Nothing (never sent).</summary>
    None = 0,

    /// <summary>The game's <c>MaxGamesCondition</c>: the agent leaves after <see cref="FreeAgentCondition.Max"/> matches.</summary>
    MaxGames = 1,

    /// <summary>The game's <c>MaxGoalsCondition</c>: the agent leaves after <see cref="FreeAgentCondition.Max"/> goals.</summary>
    MaxGoals = 2,

    /// <summary>The game's <c>InjuryCondition</c>: the agent leaves when injured.</summary>
    Injury = 3,

    /// <summary>The game's <c>RandomCondition</c>: the agent leaves at random, with <see cref="FreeAgentCondition.Chance"/>.</summary>
    Random = 4,
}

/// <summary>
/// One of a free agent's leaving conditions (an event choice's <c>FreeAgentLeavingConditions</c>), every field of its class:
/// <c>MaxGamesCondition {maxMatches, matches}</c> and <c>MaxGoalsCondition {maxGoals, goals}</c> as <see cref="Max"/> and
/// <see cref="Current"/>, <c>InjuryCondition.isInjured</c> as <see cref="IsInjured"/>, <c>RandomCondition.chance</c> as
/// <see cref="Chance"/>; the fields another kind doesn't have are 0 (false).
/// </summary>
/// <param name="Kind">Its class.</param>
/// <param name="Max">The most matches or goals before the agent leaves (<see cref="FreeAgentConditionKind.MaxGames"/>, <see cref="FreeAgentConditionKind.MaxGoals"/>).</param>
/// <param name="Current">The matches played or goals scored so far (the same two).</param>
/// <param name="IsInjured">Whether the agent is injured (<see cref="FreeAgentConditionKind.Injury"/>).</param>
/// <param name="Chance">The chance the agent leaves, 0 to 1 (<see cref="FreeAgentConditionKind.Random"/>).</param>
public sealed record FreeAgentCondition(FreeAgentConditionKind Kind, int Max, int Current, bool IsInjured, float Chance)
{
    /// <summary>A <c>MaxGamesCondition</c>.</summary>
    public static FreeAgentCondition MaxGames(int max, int played) => new(FreeAgentConditionKind.MaxGames, max, played, false, 0);

    /// <summary>A <c>MaxGoalsCondition</c>.</summary>
    public static FreeAgentCondition MaxGoals(int max, int scored) => new(FreeAgentConditionKind.MaxGoals, max, scored, false, 0);

    /// <summary>An <c>InjuryCondition</c>.</summary>
    public static FreeAgentCondition Injury(bool injured) => new(FreeAgentConditionKind.Injury, 0, 0, injured, 0);

    /// <summary>A <c>RandomCondition</c>.</summary>
    public static FreeAgentCondition Random(float chance) => new(FreeAgentConditionKind.Random, 0, 0, false, chance);

    /// <summary>For logs: "max games 3 (0 played)".</summary>
    public override string ToString() => Kind switch
    {
        FreeAgentConditionKind.MaxGames => $"max games {Max} ({Current} played)",
        FreeAgentConditionKind.MaxGoals => $"max goals {Max} ({Current} scored)",
        FreeAgentConditionKind.Injury => IsInjured ? "leaves when injured (injured)" : "leaves when injured",
        FreeAgentConditionKind.Random => string.Create(CultureInfo.InvariantCulture, $"leaves at random ({Chance:0.###})"),
        _ => Kind.ToString(),
    };

    internal void Write(BitWriter writer)
    {
        writer.WriteByte((byte)Kind);
        switch (Kind)
        {
            case FreeAgentConditionKind.MaxGames or FreeAgentConditionKind.MaxGoals:
                writer.WriteVarInt(Max);
                writer.WriteVarInt(Current);
                break;
            case FreeAgentConditionKind.Injury:
                writer.WriteBool(IsInjured);
                break;
            case FreeAgentConditionKind.Random:
                writer.WriteSingle(Chance);
                break;
        }
    }

    internal static bool TryRead(ref BitReader reader, out FreeAgentCondition? condition)
    {
        condition = null;
        var kind = (FreeAgentConditionKind)reader.ReadByte();
        bool ok = true;
        FreeAgentCondition read = kind switch
        {
            FreeAgentConditionKind.MaxGames or FreeAgentConditionKind.MaxGoals => new(kind, EventWire.Whole(ref reader, ref ok), EventWire.Whole(ref reader, ref ok), false, 0),
            FreeAgentConditionKind.Injury => new(kind, 0, 0, reader.ReadBool(), 0),
            FreeAgentConditionKind.Random => new(kind, 0, 0, false, reader.ReadSingle()),
            _ => new(kind, 0, 0, false, 0),
        };
        if (!ok || reader.IsOverrun || read.Check() != null)
        {
            return false;
        }

        condition = read;
        return true;
    }

    internal string? Check()
    {
        switch (Kind)
        {
            case FreeAgentConditionKind.MaxGames or FreeAgentConditionKind.MaxGoals:
                return EventWire.OutOfRange(Max) || EventWire.OutOfRange(Current) || IsInjured || Chance != 0 ? $"a {this} condition is out of range" : null;
            case FreeAgentConditionKind.Injury:
                return Max != 0 || Current != 0 || Chance != 0 ? "an injury condition with numbers" : null;
            case FreeAgentConditionKind.Random:
                return Max != 0 || Current != 0 || IsInjured || !float.IsFinite(Chance) || Chance is < 0 or > 1 ? "a random condition is out of range" : null;
            default:
                return $"a leaving condition of kind {Kind}";
        }
    }
}

/// <summary>A colour (the game's <c>Color</c>): red, green, blue and alpha.</summary>
/// <param name="R">Red.</param>
/// <param name="G">Green.</param>
/// <param name="B">Blue.</param>
/// <param name="A">Alpha.</param>
public readonly record struct ColorValue(float R, float G, float B, float A)
{
    /// <summary>True when every channel is a finite number no bigger than the game could use (HDR colours go past 1).</summary>
    public bool IsValid => Ok(R) && Ok(G) && Ok(B) && Ok(A);

    private static bool Ok(float value) => float.IsFinite(value) && value is >= -1000 and <= 1000;
}

/// <summary>A colour scheme (the game's <c>ColorScheme</c>): its three colours.</summary>
/// <param name="Primary">The game's <c>primaryColor</c>.</param>
/// <param name="Secondary">The game's <c>secondaryColor</c>.</param>
/// <param name="Tertiary">The game's <c>tertiaryColor</c>.</param>
public sealed record ColorSchemeData(ColorValue Primary, ColorValue Secondary, ColorValue Tertiary);

/// <summary>A date (the game's <c>DateDMY</c>: a skater's birth date); all 0 when it's unset.</summary>
/// <param name="Day">The day, 0 to 31.</param>
/// <param name="Month">The month, 0 to 12.</param>
/// <param name="Year">The year, 0 to 9999.</param>
public readonly record struct DateDmy(int Day, int Month, int Year)
{
    /// <summary>True when every part is in its range (0 for unset).</summary>
    public bool IsValid => Day is >= 0 and <= 31 && Month is >= 0 and <= 12 && Year is >= 0 and <= 9999;

    /// <summary>For logs: "31.12.1999".</summary>
    public override string ToString() => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{Day}.{Month}.{Year}");
}

/// <summary>One of a free agent's talents: its asset id, its level and, for a temporary contract, its games.</summary>
/// <param name="Id">The talent's asset id (the run file's <c>PowerUpModelV2.Id</c>).</param>
/// <param name="Level">Its level (the run file doesn't keep it; the card shows it).</param>
/// <param name="ContractGames">A <c>TemporaryContract</c>'s <c>numOfGames</c>; -1 for any other talent.</param>
public sealed record FreeAgentTalent(string Id, int Level, int ContractGames = -1);

/// <summary>
/// A skater an event made that isn't in the run (a General Manager's free agent), as plain data: the run file's own model of
/// a forward (the game's <c>ForwardDataModelV2</c>, which its <c>SerializerV2</c> makes and its <c>DeserializerV2</c> turns back
/// into a <c>ForwardData</c>), plus what the model leaves out and the event's card shows: each talent's level and a
/// temporary contract's games, the ability's level, and the three flavour-text keys. A friend's game builds the skater with
/// the game's own deserializer from it, exactly as its checkpoint load will once the host's pick is in the run.
/// Immutable; checked with the set it's in.
/// <para>
/// Two layouts: <see cref="Version1"/> (the run-start carrier's, whose skaters carry their look apart, as a
/// <see cref="SkaterLook"/>) and <see cref="Version2"/> (event choices from format 3): version 1's fields, then the look the
/// model drops (<see cref="Look"/>, <see cref="BirthDate"/>, <see cref="NumberFormat"/>), so a General Manager's card and
/// preview on a friend's screen are the host's. Version 1 leaves those out.
/// </para>
/// </summary>
public sealed class FreeAgentData
{
    /// <summary>The layout without the look (the run-start carrier's).</summary>
    public const int Version1 = 1;

    /// <summary>The layout with the look (event choices, format 3): version 1's fields, then a flag and the look, birth date and number format.</summary>
    public const int Version2 = 2;

    /// <summary>Most talents.</summary>
    public const int MaxTalents = 16;

    /// <summary>Highest number format (the game's <c>NumberFormat</c> and room for more).</summary>
    public const int MaxNumberFormat = 15;

    /// <summary>The colour schemes a <c>TeamColorsData</c> has.</summary>
    public const int SchemeCount = 8;

    /// <summary>The model's skin fields, in the order of <see cref="Skins"/>.</summary>
    public static readonly IReadOnlyList<string> SkinNames = new[]
    {
        "bodySkin", "headSkin", "helmetSkin", "logoSkin", "numberSkin", "stickSkin", "bodyAwaySkin", "bicepSkin", "gloveSkin",
        "skateSkin", "pantsSkin", "bicepAwaySkin", "gloveAwaySkin", "skateAwaySkin", "pantsAwaySkin",
    };

    /// <summary>The <c>TeamColorsData</c> schemes, in the order of <see cref="Colors"/>.</summary>
    public static readonly IReadOnlyList<string> SchemeNames = new[]
    {
        "jerseyScheme", "numberScheme", "pantsScheme", "glovesScheme", "stickScheme", "helmetScheme", "socksScheme", "skatesScheme",
    };

    /// <summary>The skater's run id (the model's <c>id</c>).</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>The template it was made from (the model's <c>originalId</c>).</summary>
    public string OriginalId { get; init; } = string.Empty;

    /// <summary>The skater asset's name (the model's <c>name</c>).</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>First name, or null.</summary>
    public string? FirstName { get; init; }

    /// <summary>Last name, or null.</summary>
    public string? LastName { get; init; }

    /// <summary>Jersey number.</summary>
    public int Number { get; init; }

    /// <summary>The checking stat.</summary>
    public int Checking { get; init; }

    /// <summary>The speed stat.</summary>
    public int Speed { get; init; }

    /// <summary>The shot accuracy stat.</summary>
    public int ShotAccuracy { get; init; }

    /// <summary>The shot power stat.</summary>
    public int ShotPower { get; init; }

    /// <summary>The model's injury counter.</summary>
    public float InjuryCounter { get; init; }

    /// <summary>Left-handed.</summary>
    public bool IsLefty { get; init; }

    /// <summary>The model's <c>isBlack</c>.</summary>
    public bool IsBlack { get; init; }

    /// <summary>An oversized helmet.</summary>
    public bool HasOversizedHelmet { get; init; }

    /// <summary>The game's <c>ForwardRarity</c> number.</summary>
    public int ForwardRarity { get; init; }

    /// <summary>The game's <c>SkaterSize</c> number.</summary>
    public int SkaterSize { get; init; }

    /// <summary>The game's <c>SkaterType</c> number.</summary>
    public int DefaultSkaterType { get; init; }

    /// <summary>The game's <c>SkaterCategory</c> number.</summary>
    public int SkaterCategory { get; init; }

    /// <summary>The 15 skins by name, in <see cref="SkinNames"/>' order (a null entry is none).</summary>
    public IReadOnlyList<string?> Skins { get; init; } = new string?[SkinNames.Count];

    /// <summary>The colour schemes (null: none), in <see cref="SchemeNames"/>' order (a null entry is a scheme the game left null).</summary>
    public IReadOnlyList<ColorSchemeData?>? Colors { get; init; }

    /// <summary>The ability's asset id, or null for none.</summary>
    public string? AbilityId { get; init; }

    /// <summary>The ability's level (0 with no ability).</summary>
    public int AbilityLevel { get; init; }

    /// <summary>The talents, in order.</summary>
    public IReadOnlyList<FreeAgentTalent> Talents { get; init; } = Array.Empty<FreeAgentTalent>();

    /// <summary>The flavour text's key (the game's <c>flavorTextLoc</c>), or null.</summary>
    public string? FlavorKey { get; init; }

    /// <summary>The perk flavour text's key (<c>perkFlavorTextLoc</c>: the event's card shows it), or null.</summary>
    public string? PerkFlavorKey { get; init; }

    /// <summary>The power-up flavour text's key (<c>powerupTextLoc</c>), or null.</summary>
    public string? PowerUpTextKey { get; init; }

    /// <summary>
    /// What its card shows that the model drops (the hidden stat offsets, female, golden helmet, glasses, away helmet, size
    /// offset, number colour, stick and skate colours, height, weight), or null (version 1, or not read).
    /// </summary>
    public SkaterLook? Look { get; init; }

    /// <summary>Its birth date (the game's <c>birthDate</c>; only with a <see cref="Look"/>).</summary>
    public DateDmy BirthDate { get; init; }

    /// <summary>Its number format (the game's <c>NumberFormat</c> number; only with a <see cref="Look"/>).</summary>
    public int NumberFormat { get; init; }

    /// <summary>
    /// For logs: "skater fa-3b1c... #17 (from GM_Template_Ole), 3 talent(s)": by its ids (<see cref="Diagnostics.HostText.KeyForLog(string?)"/>),
    /// never its name (a host sends it, and a skater's name can be typed by a player).
    /// </summary>
    public override string ToString() =>
        $"skater {Diagnostics.HostText.KeyForLog(Id)} #{Number} (from {Diagnostics.HostText.KeyForLog(OriginalId)}), {Talents.Count} talent(s)";

    /// <summary>This agent with <paramref name="look"/>, <paramref name="birthDate"/> and <paramref name="numberFormat"/> (what <see cref="Version2"/> carries).</summary>
    public FreeAgentData WithLook(SkaterLook look, DateDmy birthDate, int numberFormat) => new()
    {
        Id = Id,
        OriginalId = OriginalId,
        Name = Name,
        FirstName = FirstName,
        LastName = LastName,
        Number = Number,
        Checking = Checking,
        Speed = Speed,
        ShotAccuracy = ShotAccuracy,
        ShotPower = ShotPower,
        InjuryCounter = InjuryCounter,
        IsLefty = IsLefty,
        IsBlack = IsBlack,
        HasOversizedHelmet = HasOversizedHelmet,
        ForwardRarity = ForwardRarity,
        SkaterSize = SkaterSize,
        DefaultSkaterType = DefaultSkaterType,
        SkaterCategory = SkaterCategory,
        Skins = Skins,
        Colors = Colors,
        AbilityId = AbilityId,
        AbilityLevel = AbilityLevel,
        Talents = Talents,
        FlavorKey = FlavorKey,
        PerkFlavorKey = PerkFlavorKey,
        PowerUpTextKey = PowerUpTextKey,
        Look = look ?? throw new ArgumentNullException(nameof(look)),
        BirthDate = birthDate,
        NumberFormat = numberFormat,
    };

    /// <summary>Writes the agent in <see cref="Version1"/>'s layout (without its look).</summary>
    internal void Write(BitWriter writer) => Write(writer, Version1);

    /// <summary>Writes the agent in layout <paramref name="version"/> (<see cref="Version1"/> leaves the look out).</summary>
    internal void Write(BitWriter writer, int version)
    {
        EventWire.Name(writer, Id);
        EventWire.Name(writer, OriginalId);
        EventWire.Name(writer, Name);
        EventWire.OptionalName(writer, FirstName);
        EventWire.OptionalName(writer, LastName);
        writer.WriteVarInt(Number);
        writer.WriteVarInt(Checking);
        writer.WriteVarInt(Speed);
        writer.WriteVarInt(ShotAccuracy);
        writer.WriteVarInt(ShotPower);
        writer.WriteSingle(InjuryCounter);
        writer.WriteBool(IsLefty);
        writer.WriteBool(IsBlack);
        writer.WriteBool(HasOversizedHelmet);
        writer.WriteVarInt(ForwardRarity);
        writer.WriteVarInt(SkaterSize);
        writer.WriteVarInt(DefaultSkaterType);
        writer.WriteVarInt(SkaterCategory);
        foreach (string? skin in Skins)
        {
            EventWire.OptionalName(writer, skin);
        }

        writer.WriteBool(Colors != null);
        if (Colors != null)
        {
            foreach (ColorSchemeData? scheme in Colors)
            {
                writer.WriteBool(scheme != null);
                if (scheme != null)
                {
                    WriteColor(writer, scheme.Primary);
                    WriteColor(writer, scheme.Secondary);
                    WriteColor(writer, scheme.Tertiary);
                }
            }
        }

        EventWire.OptionalName(writer, AbilityId);
        writer.WriteVarInt(AbilityLevel);
        writer.WriteVarUInt((ulong)Talents.Count);
        foreach (FreeAgentTalent talent in Talents)
        {
            EventWire.Name(writer, talent.Id);
            writer.WriteVarInt(talent.Level);
            writer.WriteVarInt(talent.ContractGames);
        }

        EventWire.OptionalName(writer, FlavorKey);
        EventWire.OptionalName(writer, PerkFlavorKey);
        EventWire.OptionalName(writer, PowerUpTextKey);
        if (version < Version2)
        {
            return;
        }

        writer.WriteBool(Look != null);
        if (Look != null)
        {
            Look.Write(writer);
            writer.WriteVarInt(BirthDate.Day);
            writer.WriteVarInt(BirthDate.Month);
            writer.WriteVarInt(BirthDate.Year);
            writer.WriteVarInt(NumberFormat);
        }
    }

    /// <summary>Reads an agent in <see cref="Version1"/>'s layout.</summary>
    internal static bool TryRead(ref BitReader reader, out FreeAgentData? agent) => TryRead(ref reader, Version1, out agent);

    /// <summary>Reads an agent in layout <paramref name="version"/>. False for anything malformed or out of range. Never throws.</summary>
    internal static bool TryRead(ref BitReader reader, int version, out FreeAgentData? agent)
    {
        agent = null;
        string? id = EventWire.ReadName(ref reader), originalId = EventWire.ReadName(ref reader), name = EventWire.ReadName(ref reader);
        if (id == null || originalId == null || name == null || !EventWire.TryReadOptionalName(ref reader, out string? first) || !EventWire.TryReadOptionalName(ref reader, out string? last))
        {
            return false;
        }

        bool ok = true;
        int number = EventWire.Whole(ref reader, ref ok), checking = EventWire.Whole(ref reader, ref ok), speed = EventWire.Whole(ref reader, ref ok);
        int accuracy = EventWire.Whole(ref reader, ref ok), power = EventWire.Whole(ref reader, ref ok);
        float injury = reader.ReadSingle();
        bool lefty = reader.ReadBool(), black = reader.ReadBool(), helmet = reader.ReadBool();
        int rarity = EventWire.Whole(ref reader, ref ok), size = EventWire.Whole(ref reader, ref ok), type = EventWire.Whole(ref reader, ref ok), category = EventWire.Whole(ref reader, ref ok);
        var skins = new string?[SkinNames.Count];
        for (int i = 0; ok && i < skins.Length; i++)
        {
            ok = EventWire.TryReadOptionalName(ref reader, out skins[i]);
        }

        ColorSchemeData?[]? colors = null;
        if (ok && reader.ReadBool())
        {
            colors = new ColorSchemeData?[SchemeCount];
            for (int i = 0; i < colors.Length && !reader.IsOverrun; i++)
            {
                colors[i] = reader.ReadBool() ? new ColorSchemeData(ReadColor(ref reader), ReadColor(ref reader), ReadColor(ref reader)) : null;
            }
        }

        if (!ok || reader.IsOverrun || !EventWire.TryReadOptionalName(ref reader, out string? abilityId))
        {
            return false;
        }

        int abilityLevel = EventWire.Whole(ref reader, ref ok);
        int count = EventWire.Count(ref reader, MaxTalents, ref ok);
        var talents = new List<FreeAgentTalent>(ok ? count : 0);
        for (int i = 0; ok && i < count; i++)
        {
            string? talentId = EventWire.ReadName(ref reader);
            int level = EventWire.Whole(ref reader, ref ok), games = EventWire.Whole(ref reader, ref ok);
            ok &= talentId != null;
            talents.Add(new FreeAgentTalent(talentId ?? string.Empty, level, games));
        }

        if (!ok || reader.IsOverrun || !EventWire.TryReadOptionalName(ref reader, out string? flavor) || !EventWire.TryReadOptionalName(ref reader, out string? perk) ||
            !EventWire.TryReadOptionalName(ref reader, out string? powerUp))
        {
            return false;
        }

        SkaterLook? look = null;
        DateDmy birth = default;
        int numberFormat = 0;
        if (version >= Version2 && reader.ReadBool())
        {
            if (!SkaterLook.TryRead(ref reader, out look))
            {
                return false;
            }

            birth = new DateDmy(EventWire.Whole(ref reader, ref ok), EventWire.Whole(ref reader, ref ok), EventWire.Whole(ref reader, ref ok));
            numberFormat = EventWire.Whole(ref reader, ref ok);
        }

        if (!ok || reader.IsOverrun)
        {
            return false;
        }

        var read = new FreeAgentData
        {
            Id = id,
            OriginalId = originalId,
            Name = name,
            FirstName = first,
            LastName = last,
            Number = number,
            Checking = checking,
            Speed = speed,
            ShotAccuracy = accuracy,
            ShotPower = power,
            InjuryCounter = injury,
            IsLefty = lefty,
            IsBlack = black,
            HasOversizedHelmet = helmet,
            ForwardRarity = rarity,
            SkaterSize = size,
            DefaultSkaterType = type,
            SkaterCategory = category,
            Skins = skins,
            Colors = colors,
            AbilityId = abilityId,
            AbilityLevel = abilityLevel,
            Talents = talents,
            FlavorKey = flavor,
            PerkFlavorKey = perk,
            PowerUpTextKey = powerUp,
            Look = look,
            BirthDate = birth,
            NumberFormat = numberFormat,
        };
        if (read.Check() != null)
        {
            return false;
        }

        agent = read;
        return true;
    }

    /// <summary>
    /// Why the agent is out of range ("a free agent's talent 2's level is out of range"), or null when it's fine. Its ids
    /// can't be empty; its names, skins and flavour keys can (the game's assets leave unset strings empty).
    /// </summary>
    internal string? Check()
    {
        string? problem = EventWire.CheckName(Id, "a free agent's id", required: true) ?? EventWire.CheckName(OriginalId, "a free agent's template", required: true) ??
            EventWire.CheckName(Name, "a free agent's asset name", required: true, allowEmpty: true) ??
            EventWire.CheckName(FirstName, "a free agent's first name", required: false, allowEmpty: true) ??
            EventWire.CheckName(LastName, "a free agent's last name", required: false, allowEmpty: true) ?? EventWire.CheckName(AbilityId, "a free agent's ability", required: false) ??
            EventWire.CheckName(FlavorKey, "a free agent's flavour text", required: false, allowEmpty: true) ??
            EventWire.CheckName(PerkFlavorKey, "a free agent's perk text", required: false, allowEmpty: true) ??
            EventWire.CheckName(PowerUpTextKey, "a free agent's power-up text", required: false, allowEmpty: true);
        if (problem != null)
        {
            return problem;
        }

        if (Number is < 0 or > 9999 || Stat(Checking) || Stat(Speed) || Stat(ShotAccuracy) || Stat(ShotPower) || !float.IsFinite(InjuryCounter) || InjuryCounter is < -1e6f or > 1e6f)
        {
            return "a free agent's number or stats are out of range";
        }

        if (ForwardRarity is < 0 or > 255 || SkaterSize is < 0 or > 255 || DefaultSkaterType is < 0 or > 255 || SkaterCategory is < 0 or > 255)
        {
            return "a free agent's rarity, size, type or category is out of range";
        }

        if (Skins == null || Skins.Count != SkinNames.Count)
        {
            return $"a free agent with {Skins?.Count ?? 0} skins ({SkinNames.Count} go)";
        }

        for (int i = 0; i < Skins.Count; i++)
        {
            problem ??= EventWire.CheckName(Skins[i], $"a free agent's {SkinNames[i]}", required: false, allowEmpty: true);
        }

        if (problem != null)
        {
            return problem;
        }

        if (Colors != null)
        {
            if (Colors.Count != SchemeCount)
            {
                return $"a free agent with {Colors.Count} colour schemes ({SchemeCount} go)";
            }

            foreach (ColorSchemeData? scheme in Colors)
            {
                if (scheme != null && !(scheme.Primary.IsValid && scheme.Secondary.IsValid && scheme.Tertiary.IsValid))
                {
                    return "a free agent's colour is out of range";
                }
            }
        }

        if (AbilityLevel is < 0 or > 99 || (AbilityId == null && AbilityLevel != 0))
        {
            return "a free agent's ability level is out of range";
        }

        if (Talents == null || Talents.Count > MaxTalents)
        {
            return $"a free agent with {Talents?.Count ?? 0} talents (at most {MaxTalents})";
        }

        for (int i = 0; i < Talents.Count; i++)
        {
            FreeAgentTalent? talent = Talents[i];
            problem = talent == null ? $"a free agent's talent {i + 1} is missing" : EventWire.CheckName(talent.Id, $"a free agent's talent {i + 1}", required: true);
            if (problem != null)
            {
                return problem;
            }

            if (talent!.Level is < 0 or > 99 || talent.ContractGames is < -1 or > 999)
            {
                return $"a free agent's talent {i + 1}'s level or games are out of range";
            }
        }

        if (Look == null)
        {
            return BirthDate != default || NumberFormat != 0 ? "a free agent's birth date or number format without its look" : null;
        }

        return Look.Check() ?? (!BirthDate.IsValid ? $"a free agent's birth date {BirthDate} is out of range"
            : NumberFormat is < 0 or > MaxNumberFormat ? $"a free agent's number format {NumberFormat} is out of range"
            : null);
    }

    private static bool Stat(int value) => value is < -999 or > 9999;

    private static void WriteColor(BitWriter writer, ColorValue color)
    {
        writer.WriteSingle(color.R);
        writer.WriteSingle(color.G);
        writer.WriteSingle(color.B);
        writer.WriteSingle(color.A);
    }

    private static ColorValue ReadColor(ref BitReader reader) => new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
}
