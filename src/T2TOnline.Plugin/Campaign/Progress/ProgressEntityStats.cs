#if HAS_INTEROP
using System;
using System.Collections.Generic;
using System.Reflection;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppScriptableStates;
using Il2CppStats.PersistentStats;
using T2TOnline.Core.Campaign;
using T2TOnline.Plugin.Diagnostics;

namespace T2TOnline.Plugin.Campaign.Progress;

/// <summary>
/// The game's per-entity stats in shared progress: a definition per skater, goalie, team,
/// talent, ability or relic, in four sets, updated only by the game's generic <c>StatManager.AddOrCombineEntityStat</c>
/// (never patched). Read by the stats screens, the Relic Freezer's unlock, the squad unlock "beat this team"
/// (<c>TeamDefeatedCondition</c>), the map's beaten teams and the rewards' "new" badges.
/// <list type="bullet">
/// <item>Host: the game asks for a set's groups (<c>StatRepository.GetEntityStatGroups</c>, postfix), then either combines
/// the update into the entity's definition (each class's <c>Combine</c>, prefix: the update's numbers) or adds it as the
/// definition, then raises <c>StatRepository.OnEntityStatUpdated</c> (nothing in the game listens: this mod does while
/// hosting). That event records the update: the numbers <c>Combine</c> got, or the definition just added. A <c>Combine</c>
/// with no event after it (the stats screens add definitions up to show them) records nothing.</item>
/// <item>Friend: the update is combined into this profile's own definition through the group's own
/// <c>AddOrCombine</c> (the game's <c>Combine</c>: every count added, a streak of games worked out from its own), or added
/// when it has none. Since the Nov 6 build the game's stats file keeps one key per entity and its save writes only the
/// entities its own <c>StatRepository.AddOrCombineEntityStat</c> marked changed (<c>StatRepository.SaveChangedStats</c>):
/// the entity's key is then written with the game's own <c>ES3StatSaveLoadHandler.SaveEntityStat</c>, as that save does,
/// into Easy Save's cached copy of the file; the save after the batch puts it on disk (<see cref="StatsFileWrites"/>).</item>
/// </list>
/// Fields are read and written by name (the interop's properties have the game's field names, <see cref="EntityStatSchema"/>).
/// Nothing here throws into the game.
/// </summary>
internal static class ProgressEntityStats
{
    private static readonly Dictionary<EntityStatGroup, (Type Type, PropertyInfo?[] Fields)> Classes = new();
    private static Il2CppSystem.Action<StatGroupType, string>? _listener;
    private static StatRepository? _heard;
    private static int _setFrame = -1;
    private static bool _setCampaign;
    private static bool _setCpu;
    private static (EntityStatGroup Group, IReadOnlyList<EntityStatValue> Values)? _combined;
    private static int _errors;

    /// <summary>Host: hears the game's entity stat updates of <paramref name="repository"/> (again if it's another one). Never throws.</summary>
    public static void Listen(StatRepository? repository)
    {
        try
        {
            if (repository == null || (_heard != null && _heard.Pointer == repository.Pointer))
            {
                return;
            }

            Unlisten();
            _listener ??= DelegateSupport.ConvertDelegate<Il2CppSystem.Action<StatGroupType, string>>(new Action<StatGroupType, string>(OnUpdated));
            repository.add_OnEntityStatUpdated(_listener);
            _heard = repository;
        }
        catch (Exception ex)
        {
            Failed("listening to entity stat updates", ex);
        }
    }

    /// <summary>Host: stops hearing them (hosting stopped). Never throws.</summary>
    public static void Unlisten()
    {
        StatRepository? heard = _heard;
        _heard = null;
        _combined = null;
        try
        {
            if (heard != null && _listener != null)
            {
                heard.remove_OnEntityStatUpdated(_listener);
            }
        }
        catch (Exception ex)
        {
            Failed("no longer listening to entity stat updates", ex);
        }
    }

    /// <summary>Host (the groups postfix): the next update goes to the campaign's or Play Now's set, the humans' or the computer's.</summary>
    public static void SetAsked(GameType gameType, bool cpu)
    {
        _setFrame = UnityEngine.Time.frameCount;
        _setCampaign = gameType == GameType.Rogue;
        _setCpu = cpu;
        _combined = null;
    }

    /// <summary>Host (a definition's <c>Combine</c> prefix): the numbers the update adds to an existing definition.</summary>
    public static void Combining(EntityStatGroup group, Il2CppObjectBase? other)
    {
        if (_setFrame != UnityEngine.Time.frameCount || other == null)
        {
            return; // not an update the game is filing (the stats screens add definitions up to show them)
        }

        _combined = (group, Read(group, other));
    }

    /// <summary>
    /// The fields of <paramref name="group"/>'s definition <paramref name="definition"/>, read by name, in the schema's order.
    /// Throws when a field can't be read (the caller logs it).
    /// </summary>
    public static IReadOnlyList<EntityStatValue> Read(EntityStatGroup group, Il2CppObjectBase definition)
    {
        (Type type, PropertyInfo?[] fields) = ClassOf(group);
        object typed = Activator.CreateInstance(type, definition.Pointer)!;
        IReadOnlyList<EntityField> schema = EntityStatSchema.Fields(group);
        var values = new EntityStatValue[schema.Count];
        for (int i = 0; i < schema.Count; i++)
        {
            PropertyInfo property = fields[i] ?? throw new MissingMemberException(type.Name, schema[i].Name);
            object? value = property.GetValue(typed);
            values[i] = schema[i].Kind switch
            {
                EntityFieldKind.Text => EntityStatValue.OfText(value as string),
                EntityFieldKind.Whole => EntityStatValue.OfWhole(value is int whole ? whole : 0),
                EntityFieldKind.Real => EntityStatValue.OfReal(value is float real ? real : 0f),
                _ => EntityStatValue.OfList(value is Il2CppStructArray<int> list ? ToArray(list) : null),
            };
        }

        return values;
    }

    /// <summary>
    /// Friend: <paramref name="entry"/> combined into this profile's own definition (or added as one) in
    /// <paramref name="repository"/>'s set for it, then the entity's key written into the stats file's cached copy
    /// (<see cref="StatsFileWrites.WriteEntityKey"/>). Returns whether the entity was there before and what became of its key.
    /// Throws when the game's group or a field isn't there (the caller logs it); a key not written is only returned.
    /// </summary>
    public static EntityApplied Apply(StatRepository repository, EntityStatEntry entry)
    {
        GameType gameType = entry.Campaign ? GameType.Rogue : GameType.Quick_Game;
        Il2CppSystem.Collections.Generic.Dictionary<StatGroupType, IEntityStatGroup> groups =
            repository.GetEntityStatGroups(gameType, entry.Cpu)
            ?? throw new InvalidOperationException("this game has no such set of entity stats");
        var key = (StatGroupType)(int)entry.Group;
        if (!groups.ContainsKey(key))
        {
            throw new InvalidOperationException($"this game has no {entry.Group} entity stats");
        }

        IEntityStatGroup group = groups[key];
        bool existed = group.HasStats(entry.EntityId);
        (Type type, PropertyInfo?[] fields) = ClassOf(entry.Group);

        // The class's own constructor first (it makes the lists the game's Combine adds into), then the update's fields.
        object definition = Activator.CreateInstance(type)!;
        IReadOnlyList<EntityField> schema = EntityStatSchema.Fields(entry.Group);
        for (int i = 0; i < schema.Count; i++)
        {
            PropertyInfo property = fields[i] ?? throw new MissingMemberException(type.Name, schema[i].Name);
            EntityStatValue value = entry.Values[i];
            switch (schema[i].Kind)
            {
                case EntityFieldKind.Text:
                    property.SetValue(definition, value.Text);
                    break;
                case EntityFieldKind.Whole:
                    property.SetValue(definition, value.Whole);
                    break;
                case EntityFieldKind.Real:
                    property.SetValue(definition, value.Real);
                    break;
                default:
                    if (value.List != null)
                    {
                        property.SetValue(definition, new Il2CppStructArray<int>(ToArray(value.List)));
                    }

                    break;
            }
        }

        group.AddOrCombine(entry.EntityId, ((Il2CppObjectBase)definition).Cast<IEntityStat>());

        // Filed in memory: from here nothing throws (the update stays applied whatever becomes of its key).
        EntityKeyWrite written = StatsFileWrites.WriteEntityKey(repository, group, gameType, entry.Cpu, key, entry.EntityId, out string? problem);
        return new EntityApplied(existed, written, problem);
    }

    /// <summary>The game raised an entity stat update (host, hosting): it's recorded for friends. Never throws into the game.</summary>
    private static void OnUpdated(StatGroupType groupType, string id)
    {
        try
        {
            var group = (EntityStatGroup)(int)groupType;
            (EntityStatGroup Group, IReadOnlyList<EntityStatValue> Values)? combined = _combined;
            bool fresh = _setFrame == UnityEngine.Time.frameCount;
            _combined = null;
            _setFrame = -1;
            if (!ProgressModule.Recording || ProgressModule.Host is not ProgressHost host || !fresh || !EntityStatSchema.IsKnown(group))
            {
                return;
            }

            IReadOnlyList<EntityStatValue> values;
            if (combined is { } update && update.Group == group)
            {
                values = update.Values;
            }
            else
            {
                // Added: the definition the game filed is the update itself.
                Il2CppSystem.Collections.Generic.Dictionary<StatGroupType, IEntityStatGroup>? groups =
                    _heard?.GetEntityStatGroups(_setCampaign ? GameType.Rogue : GameType.Quick_Game, _setCpu);
                IEntityStat? stored = groups != null && groups.ContainsKey(groupType) ? groups[groupType].GetStats(id) : null;
                if (stored == null)
                {
                    return;
                }

                values = Read(group, stored);
            }

            host.Record(new EntityStatEntry(_setCampaign, _setCpu, group, id ?? string.Empty, values));
        }
        catch (Exception ex)
        {
            Failed("recording an entity stat update", ex);
        }
    }

    private static (Type Type, PropertyInfo?[] Fields) ClassOf(EntityStatGroup group)
    {
        if (Classes.TryGetValue(group, out var known))
        {
            return known;
        }

        Type type = group switch
        {
            EntityStatGroup.Skater => typeof(SkaterStatDefinition),
            EntityStatGroup.Goalie => typeof(GoalieStatDefinition),
            EntityStatGroup.Team => typeof(TeamStatDefinition),
            EntityStatGroup.Talent => typeof(TalentStatDefinition),
            EntityStatGroup.Ability => typeof(AbilityStatDefinition),
            EntityStatGroup.Relic => typeof(RelicStatDefinition),
            _ => throw new ArgumentOutOfRangeException(nameof(group), group, "not an entity stat group"),
        };

        IReadOnlyList<EntityField> schema = EntityStatSchema.Fields(group);
        var fields = new PropertyInfo?[schema.Count];
        for (int i = 0; i < schema.Count; i++)
        {
            fields[i] = type.GetProperty(schema[i].Name, BindingFlags.Public | BindingFlags.Instance);
        }

        Classes[group] = (type, fields);
        return (type, fields);
    }

    private static int[] ToArray(Il2CppStructArray<int> list)
    {
        int[] values = new int[Math.Min(list.Length, EntityStatSchema.MaxListLength + 1)];
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = list[i];
        }

        return values;
    }

    private static int[] ToArray(IReadOnlyList<int> list)
    {
        int[] values = new int[list.Count];
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = list[i];
        }

        return values;
    }

    private static void Failed(string what, Exception ex)
    {
        if (_errors++ < 3)
        {
            ModLog.Warn($"Progress: failed {what} ({ModLog.SafeErrorText(ex)}){(_errors == 3 ? "; further failures aren't logged" : string.Empty)}.");
        }
    }
}
#endif
