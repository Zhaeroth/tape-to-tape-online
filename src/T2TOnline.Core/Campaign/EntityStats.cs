using System;
using System.Collections.Generic;
using T2TOnline.Core.Serialization;

namespace T2TOnline.Core.Campaign;

/// <summary>
/// A group of the game's per-entity stats (its <c>StatGroupType</c>): a definition per skater, goalie, team, talent, ability
/// or relic, kept in four sets (campaign or Play Now, the human team's or the computer's). Values are the game's and part of
/// the protocol: only add.
/// </summary>
public enum EntityStatGroup : byte
{
    /// <summary>A skater (<c>SkaterStatDefinition</c>).</summary>
    Skater = 0,

    /// <summary>A goalie (<c>GoalieStatDefinition</c>).</summary>
    Goalie = 1,

    /// <summary>A team (<c>TeamStatDefinition</c>).</summary>
    Team = 2,

    /// <summary>A talent (<c>TalentStatDefinition</c>).</summary>
    Talent = 3,

    /// <summary>An ability (<c>AbilityStatDefinition</c>).</summary>
    Ability = 4,

    /// <summary>A relic (<c>RelicStatDefinition</c>).</summary>
    Relic = 5,
}

/// <summary>What one field of an entity stat definition holds (not on the wire: the schema says it).</summary>
public enum EntityFieldKind
{
    /// <summary>Text (an id, a name, a face).</summary>
    Text,

    /// <summary>A whole number.</summary>
    Whole,

    /// <summary>A real number (seconds).</summary>
    Real,

    /// <summary>Whole numbers by difficulty.</summary>
    WholeList,
}

/// <summary>One field of an entity stat definition.</summary>
/// <param name="Name">The game's field name (the interop's property has the same name).</param>
/// <param name="Kind">What it holds.</param>
/// <param name="Credit">
/// The game's <c>Combine</c> adds it up (a count): it's credit, capped per session. The others are kept as the definition
/// first had them (names, numbers) or worked out from the friend's own value by <c>Combine</c> (a streak of games without
/// an injury).
/// </param>
public readonly record struct EntityField(string Name, EntityFieldKind Kind, bool Credit);

/// <summary>
/// The fields of each group's definition, in wire order (Oct 29 build: from the classes'
/// fields and their <c>Combine</c> methods, which add every count and every by-difficulty list).
/// </summary>
public static class EntityStatSchema
{
    private static readonly EntityField[] Skater = Build(
        T("skaterId"), T("skaterName"), K("number"), T("faceSkin"), C("gamesPlayed"), L("gamesPlayedByDifficulty"), C("gamesWon"),
        L("gamesWonByDifficulty"), C("goals"), L("goalsByDifficulty"), C("assists"), L("assistsByDifficulty"), C("shotsAttempted"),
        C("shotsOnNet"), C("redirects"), C("bats"), C("wristShots"), C("slapShots"), C("backHands"), C("oneTimers"), C("shotsOnPost"),
        C("knockoutsAttempted"), C("knockoutsPerformed"), L("knockoutsPerformedByDifficulty"), C("knockoutsReceived"), C("takeaways"),
        C("giveaways"), C("injuriesReceived"), C("injuriesInflicted"), C("passesAttempted"), C("passesReceived"), C("interceptions"),
        C("blockedShots"), C("faceOffsWon"), C("faceOffsLost"), C("criticalShots"), C("injuredGoals"), C("enragedGoals"),
        C("froggedGoals"), C("unassistedGoals"), K("consecutiveGamesWithoutReceivingInjury"), C("campaignPlayed"), C("campaignWon"));

    private static readonly EntityField[] Goalie = Build(
        T("goalieId"), T("goalieName"), K("number"), T("faceSkin"), C("gamesPlayed"), L("gamesPlayedByDifficulty"), C("gamesWon"),
        L("gamesWonByDifficulty"), C("goalsAgainst"), L("goalsAgainstByDifficulty"), C("shotsAgainst"), L("shotsAgainstByDifficulty"),
        C("shutouts"), C("campaignPlayed"), C("campaignWon"));

    private static readonly EntityField[] Team = Build(
        T("teamId"), T("teamName"), T("teamLogoId"), C("gamesPlayed"), C("wins"), C("losses"), C("goals"), C("goalsAgainst"),
        C("shotsOnNet"), C("knockoutsPerformed"), new EntityField("timeOnAttack", EntityFieldKind.Real, true), C("passesAttempted"),
        C("passesCompleted"), C("blockedShots"), C("interceptions"), C("faceOffWins"), C("criticalShots"), C("injuredGoals"),
        C("enragedGoals"), C("froggedGoals"), C("unassistedGoals"), C("shotsAttempted"), C("redirects"), C("bats"), C("wristShots"),
        C("slapShots"), C("backHands"), C("oneTimers"), C("knockoutsAttempted"), C("knockoutsReceived"), C("takeaways"), C("giveaways"),
        C("injuriesReceived"), C("injuriesInflicted"), C("faceOffLosses"));

    private static readonly EntityField[] Talent = Build(
        T("talentId"), T("talentName"), C("timesChosen"), C("timesUpgraded"), C("totalGamesWithTalent"), C("runsWonWithTalent"));

    private static readonly EntityField[] Ability = Build(
        T("abilityId"), T("abilityName"), C("timesChosen"), C("timesActivated"), C("timesUpgraded"), C("totalGamesWithAbility"),
        C("runsWonWithAbility"));

    private static readonly EntityField[] Relic = Build(
        T("relicId"), T("relicName"), C("timesChosen"), C("totalGamesWithRelic"), C("runsWonWithRelic"));

    /// <summary>Most values in a by-difficulty list (the game has a handful of difficulties).</summary>
    public const int MaxListLength = 16;

    /// <summary>True for a group this build knows.</summary>
    public static bool IsKnown(EntityStatGroup group) => group is >= EntityStatGroup.Skater and <= EntityStatGroup.Relic;

    /// <summary>The fields of <paramref name="group"/>'s definition, in wire order (empty for an unknown group).</summary>
    public static IReadOnlyList<EntityField> Fields(EntityStatGroup group) => group switch
    {
        EntityStatGroup.Skater => Skater,
        EntityStatGroup.Goalie => Goalie,
        EntityStatGroup.Team => Team,
        EntityStatGroup.Talent => Talent,
        EntityStatGroup.Ability => Ability,
        EntityStatGroup.Relic => Relic,
        _ => Array.Empty<EntityField>(),
    };

    private static EntityField[] Build(params EntityField[] fields) => fields;

    private static EntityField T(string name) => new(name, EntityFieldKind.Text, false);

    private static EntityField K(string name) => new(name, EntityFieldKind.Whole, false);

    private static EntityField C(string name) => new(name, EntityFieldKind.Whole, true);

    private static EntityField L(string name) => new(name, EntityFieldKind.WholeList, true);
}

/// <summary>One field's value in an entity stat definition: by its schema kind, text, a whole or real number, or a list.</summary>
public readonly struct EntityStatValue : IEquatable<EntityStatValue>
{
    private EntityStatValue(EntityFieldKind kind, int whole, float real, string? text, int[]? list)
    {
        Kind = kind;
        Whole = whole;
        Real = real;
        Text = text;
        List = list;
    }

    /// <summary>Which of the values it holds.</summary>
    public EntityFieldKind Kind { get; }

    /// <summary>A whole-number field's value.</summary>
    public int Whole { get; }

    /// <summary>A real-number field's value.</summary>
    public float Real { get; }

    /// <summary>A text field's value (null: the game left it unset).</summary>
    public string? Text { get; }

    /// <summary>A list field's values (null: the game left it unset).</summary>
    public IReadOnlyList<int>? List { get; }

    /// <summary>Text (null: unset).</summary>
    public static EntityStatValue OfText(string? text) => new(EntityFieldKind.Text, 0, 0, text, null);

    /// <summary>A whole number.</summary>
    public static EntityStatValue OfWhole(int value) => new(EntityFieldKind.Whole, value, 0, null, null);

    /// <summary>A real number.</summary>
    public static EntityStatValue OfReal(float value) => new(EntityFieldKind.Real, 0, value, null, null);

    /// <summary>Whole numbers by difficulty (null: unset).</summary>
    public static EntityStatValue OfList(IReadOnlyList<int>? values) =>
        new(EntityFieldKind.WholeList, 0, 0, null, values == null ? null : new List<int>(values).ToArray());

    /// <inheritdoc />
    public bool Equals(EntityStatValue other)
    {
        if (Kind != other.Kind || Whole != other.Whole || !Real.Equals(other.Real) || !string.Equals(Text, other.Text, StringComparison.Ordinal))
        {
            return false;
        }

        if (List == null || other.List == null)
        {
            return List == null && other.List == null;
        }

        if (List.Count != other.List.Count)
        {
            return false;
        }

        for (int i = 0; i < List.Count; i++)
        {
            if (List[i] != other.List[i])
            {
                return false;
            }
        }

        return true;
    }

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is EntityStatValue other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Kind, Whole, Real, Text, List?.Count ?? -1);

    /// <summary>Value equality.</summary>
    public static bool operator ==(EntityStatValue left, EntityStatValue right) => left.Equals(right);

    /// <summary>Value inequality.</summary>
    public static bool operator !=(EntityStatValue left, EntityStatValue right) => !left.Equals(right);

    /// <summary>What it adds up to as credit (a list's values together; text adds nothing).</summary>
    public double Sum()
    {
        switch (Kind)
        {
            case EntityFieldKind.Whole:
                return Whole;
            case EntityFieldKind.Real:
                return Real;
            case EntityFieldKind.WholeList:
                double total = 0;
                foreach (int value in List ?? Array.Empty<int>())
                {
                    total += value;
                }

                return total;
            default:
                return 0;
        }
    }
}

/// <summary>
/// One update of the game's per-entity stats (D42): what one call of the game's
/// <c>AddOrCombineEntityStat</c> added to entity <paramref name="EntityId"/> of <paramref name="Group"/> in one of the four
/// sets, as the definition the game built for it (a match's numbers for one skater, a talent chosen once...). A friend's
/// game does the same with its own groups: the definition is combined into its own one with the game's own
/// <c>Combine</c> (every count added, a streak worked out from its own), or added when it has none. So each profile ends up
/// with its own numbers plus what the shared run added, never the host's totals.
/// <code>
/// u8 set (bit 0 campaign, bit 1 the computer's team) · u8 group · str entity id · varuint field count (the schema's) ·
/// each field by its kind: text str · whole varint · real f32 · list varuint count + varint each
/// </code>
/// </summary>
/// <param name="Campaign">The campaign's set (else Play Now's): the game's <c>GameType.Rogue</c>.</param>
/// <param name="Cpu">The computer's team's set (else the humans').</param>
/// <param name="Group">The kind of entity.</param>
/// <param name="EntityId">The entity's id (the key the game files it under).</param>
/// <param name="Values">The definition's fields, in <see cref="EntityStatSchema.Fields"/> order.</param>
public sealed record EntityStatEntry(bool Campaign, bool Cpu, EntityStatGroup Group, string EntityId, IReadOnlyList<EntityStatValue> Values) : ProgressEntry
{
    /// <summary>True when the entry fits its group's schema and every limit (so it can be sent, and was read right).</summary>
    public bool IsWithinLimits()
    {
        if (!EntityStatSchema.IsKnown(Group) || !IsPlausibleEntityId(EntityId) || Values == null)
        {
            return false;
        }

        IReadOnlyList<EntityField> fields = EntityStatSchema.Fields(Group);
        if (Values.Count != fields.Count)
        {
            return false;
        }

        for (int i = 0; i < fields.Count; i++)
        {
            if (!IsValid(fields[i], Values[i]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>An entity id: 1 to <see cref="ProgressLimits.MaxIdBytes"/> bytes of UTF-8 without control characters.</summary>
    public static bool IsPlausibleEntityId(string? id) =>
        !string.IsNullOrEmpty(id) && id.Length <= ProgressLimits.MaxIdBytes && System.Text.Encoding.UTF8.GetByteCount(id) <= ProgressLimits.MaxIdBytes &&
        ProgressLimits.IsPlausibleText(id);

    /// <summary>The value of field <paramref name="name"/>, or default when the schema has no such field.</summary>
    public EntityStatValue Field(string name)
    {
        IReadOnlyList<EntityField> fields = EntityStatSchema.Fields(Group);
        for (int i = 0; i < fields.Count && i < Values.Count; i++)
        {
            if (fields[i].Name == name)
            {
                return Values[i];
            }
        }

        return default;
    }

    /// <summary>Writes the entry (after its kind byte).</summary>
    public void Write(BitWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteByte((byte)((Campaign ? 1 : 0) | (Cpu ? 2 : 0)));
        writer.WriteByte((byte)Group);
        writer.WriteShortString(EntityId, ProgressLimits.MaxIdBytes);
        IReadOnlyList<EntityField> fields = EntityStatSchema.Fields(Group);
        writer.WriteVarUInt((ulong)fields.Count);
        for (int i = 0; i < fields.Count; i++)
        {
            EntityStatValue value = Values[i];
            switch (fields[i].Kind)
            {
                case EntityFieldKind.Text:
                    writer.WriteShortString(value.Text ?? string.Empty, ProgressLimits.MaxTextBytes);
                    break;
                case EntityFieldKind.Whole:
                    writer.WriteVarInt(value.Whole);
                    break;
                case EntityFieldKind.Real:
                    writer.WriteSingle(value.Real);
                    break;
                default:
                    IReadOnlyList<int> list = value.List ?? Array.Empty<int>();
                    writer.WriteVarUInt((ulong)list.Count);
                    foreach (int item in list)
                    {
                        writer.WriteVarInt(item);
                    }

                    break;
            }
        }
    }

    /// <summary>Reads an entry (after its kind byte). False for anything malformed or out of limits. Never throws.</summary>
    public static bool TryRead(ref BitReader reader, out EntityStatEntry? entry)
    {
        entry = null;
        byte set = reader.ReadByte();
        var group = (EntityStatGroup)reader.ReadByte();
        string id = reader.ReadShortString(ProgressLimits.MaxIdBytes);
        ulong count = reader.ReadVarUInt();
        IReadOnlyList<EntityField> fields = EntityStatSchema.Fields(group);
        if (reader.IsOverrun || set > 3 || !EntityStatSchema.IsKnown(group) || count != (ulong)fields.Count)
        {
            return false;
        }

        var values = new EntityStatValue[fields.Count];
        for (int i = 0; i < fields.Count; i++)
        {
            switch (fields[i].Kind)
            {
                case EntityFieldKind.Text:
                    string text = reader.ReadShortString(ProgressLimits.MaxTextBytes);
                    values[i] = EntityStatValue.OfText(text.Length == 0 ? null : text);
                    break;
                case EntityFieldKind.Whole:
                    long whole = reader.ReadVarInt();
                    if (whole is < int.MinValue or > int.MaxValue)
                    {
                        return false;
                    }

                    values[i] = EntityStatValue.OfWhole((int)whole);
                    break;
                case EntityFieldKind.Real:
                    values[i] = EntityStatValue.OfReal(reader.ReadSingle());
                    break;
                default:
                    ulong length = reader.ReadVarUInt();
                    if (reader.IsOverrun || length > EntityStatSchema.MaxListLength)
                    {
                        return false;
                    }

                    int[] list = new int[(int)length];
                    for (int j = 0; j < list.Length; j++)
                    {
                        long item = reader.ReadVarInt();
                        if (item is < int.MinValue or > int.MaxValue)
                        {
                            return false;
                        }

                        list[j] = (int)item;
                    }

                    values[i] = EntityStatValue.OfList(list);
                    break;
            }

            if (reader.IsOverrun)
            {
                return false;
            }
        }

        entry = new EntityStatEntry((set & 1) != 0, (set & 2) != 0, group, id, values);
        return entry.IsWithinLimits();
    }

    private static bool IsValid(EntityField field, EntityStatValue value)
    {
        if (value.Kind != field.Kind)
        {
            return false;
        }

        // Counts, numbers and streaks are never negative and never more than one stat operation may carry.
        switch (field.Kind)
        {
            case EntityFieldKind.Text:
                return value.Text == null || ProgressLimits.IsPlausibleText(value.Text);
            case EntityFieldKind.Whole:
                return value.Whole is >= 0 and <= ProgressLimits.MaxStatValue;
            case EntityFieldKind.Real:
                return float.IsFinite(value.Real) && value.Real >= 0 && value.Real <= ProgressLimits.MaxStatValue;
            default:
                if (value.List == null)
                {
                    return true;
                }

                if (value.List.Count > EntityStatSchema.MaxListLength)
                {
                    return false;
                }

                foreach (int item in value.List)
                {
                    if (item is < 0 or > ProgressLimits.MaxStatValue)
                    {
                        return false;
                    }
                }

                return true;
        }
    }
}

/// <summary>
/// What one hosting session has added to each entity stat on a friend's profile (the entity stats' <see cref="ProgressCredit"/>):
/// each count of each entity at most <see cref="ProgressLimits.MaxStatCreditPerSession"/> together, and at most
/// <see cref="MaxEntitiesPerSession"/> entities touched, so a host can't pump an entity's numbers batch after batch nor fill
/// a friend's stats screens with made-up entities. With a ledger and a host key, what this PC's play with that host in the
/// last 24 hours allows bounds it too (D135): one entity's count in the hosting session at most its pace's bound
/// (<see cref="StatBounds.EntityBound"/>), and a group's count over all its entities within the host's rolling budget
/// (<see cref="ProgressLedger.EntityFits"/>), which hosting again doesn't start over. Game-independent. Not thread-safe
/// (one game thread).
/// </summary>
public sealed class EntityCredit
{
    /// <summary>Most entities one hosting session may touch on a friend (a run touches a few dozen).</summary>
    public const int MaxEntitiesPerSession = 1024;

    private readonly Dictionary<(int Set, EntityStatGroup Group, string Id, int Field), double> _given = new();
    private readonly HashSet<(int Set, EntityStatGroup Group, string Id)> _entities = new();
    private readonly List<((int, EntityStatGroup, string, int) Key, double Total, int Field, double Added)> _totals = new();
    private ulong _session;

    /// <summary>Entries refused for going over a cap.</summary>
    public int Refused { get; private set; }

    /// <summary>Entries refused for going past what this PC's play with the host allows (counted in <see cref="Refused"/> too).</summary>
    public int OverBudget { get; private set; }

    /// <summary>
    /// True (and counted) when <paramref name="entry"/> fits in what hosting session <paramref name="session"/> may still
    /// add; false (counted as refused, nothing counted) when any of its counts would go over. Another session starts from
    /// nothing.
    /// </summary>
    public bool TryGive(ulong session, EntityStatEntry entry) => TryGive(session, entry, null, string.Empty, default);

    /// <summary>
    /// As <see cref="TryGive(ulong, EntityStatEntry)"/>, and also within what this PC's play with host <paramref name="host"/>
    /// allows in <paramref name="ledger"/> at <paramref name="utc"/> (see the class; counted there only when every count
    /// fits; false, counted in <see cref="OverBudget"/>, when one doesn't). No ledger: the session's caps only.
    /// </summary>
    public bool TryGive(ulong session, EntityStatEntry entry, ProgressLedger? ledger, string host, DateTime utc)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(host);
        if (session != _session)
        {
            _session = session;
            _given.Clear();
            _entities.Clear();
        }

        int set = (entry.Campaign ? 1 : 0) | (entry.Cpu ? 2 : 0);
        var entity = (set, entry.Group, entry.EntityId);
        if (!_entities.Contains(entity) && _entities.Count >= MaxEntitiesPerSession)
        {
            Refused++;
            return false;
        }

        IReadOnlyList<EntityField> fields = EntityStatSchema.Fields(entry.Group);
        _totals.Clear();
        for (int i = 0; i < fields.Count && i < entry.Values.Count; i++)
        {
            if (!fields[i].Credit)
            {
                continue;
            }

            var key = (set, entry.Group, entry.EntityId, i);
            double added = entry.Values[i].Sum();
            double total = _given.GetValueOrDefault(key) + added;
            if (total > ProgressLimits.MaxStatCreditPerSession)
            {
                Refused++;
                return false;
            }

            if (ledger != null && added > 0 && (!ledger.EntityFits(host, entry.Group, i, fields[i].Name, added, utc, out double bound) || total > bound))
            {
                Refused++;
                OverBudget++;
                return false;
            }

            _totals.Add((key, total, i, added));
        }

        _entities.Add(entity);
        foreach ((var key, double total, int field, double added) in _totals)
        {
            _given[key] = total;
            ledger?.SpendEntity(host, entry.Group, field, added, utc);
        }

        return true;
    }
}
