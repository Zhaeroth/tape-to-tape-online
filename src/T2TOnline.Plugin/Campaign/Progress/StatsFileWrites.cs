#if HAS_INTEROP
using System;
using Il2CppScriptableStates;
using Il2CppStats.PersistentStats;
using T2TOnline.Plugin.Diagnostics;

namespace T2TOnline.Plugin.Campaign.Progress;

/// <summary>What became of an entity's key in the stats file after a friend filed an update (<see cref="StatsFileWrites.WriteEntityKey"/>).</summary>
internal enum EntityKeyWrite
{
    /// <summary>Written into Easy Save's cached copy of the stats file; the next save of the stats puts it on disk.</summary>
    Written,

    /// <summary>The game's stats handler isn't Easy Save's: its save writes every entity, as before the Nov 6 build.</summary>
    WholeSave,

    /// <summary>Not written (the reason goes with it); the update is in memory only.</summary>
    Failed,
}

/// <summary>A friend's entity stat update filed here: whether the entity was there before, and what became of its key.</summary>
/// <param name="Existed">The entity had stats here before (combined into them); else added.</param>
/// <param name="Key">What became of its key in the stats file.</param>
/// <param name="KeyProblem">Why the key wasn't written, or null.</param>
internal readonly record struct EntityApplied(bool Existed, EntityKeyWrite Key, string? KeyProblem);

/// <summary>
/// The mod's own writes of the game's stats file (<c>statsV2.es3</c>) on the Nov 6 build, whose
/// <c>StatRepository.Save</c> writes only what the game's own entry points marked changed: with an Easy Save handler it
/// goes to <c>SaveChangedStats</c>, which writes the stats list only when <c>_statsListDirty</c> is set (the game sets it in
/// <c>IncrementStat</c>, <c>ResetStats</c>, <c>ResetRun</c>, <c>ResetAct</c> and at load) and each entity only when
/// <c>AddOrCombineEntityStat</c> marked it, then stores the file only when it wrote something. The mod changes stats on the
/// game's own <c>IStat</c> and <c>IEntityStatGroup</c> objects (a friend's shared progress, a host's per-run counters),
/// which mark nothing, so:
/// <list type="bullet">
/// <item>before each of the mod's own saves of the stats, the list is marked changed (<see cref="MarkListChanged"/>): the
/// save writes the whole list, as every save did before;</item>
/// <item>after a friend files an entity update, its key is written with the game's own
/// <c>ES3StatSaveLoadHandler.SaveEntityStat</c>, which is what <c>SaveChangedStats</c> calls for a marked entity
/// (<see cref="WriteEntityKey"/>). It writes into Easy Save's cache only; the disk write comes with the save after the
/// batch, inside the save guard's allowance.</item>
/// </list>
/// Nothing here throws into the game.
/// </summary>
internal static class StatsFileWrites
{
    private const int MaxWarnings = 3;

    private static int _markFailures;
    private static bool _wholeSaveNoted;

    /// <summary>
    /// Marks <paramref name="stats"/>' list as changed, so the save that follows writes it whole (<paramref name="why"/>: whose
    /// save, for the log). False, logged (the first few), when it can't: the save then writes only what the game marked
    /// itself. Never throws.
    /// </summary>
    public static bool MarkListChanged(StatRepository? stats, string why)
    {
        try
        {
            if (stats == null)
            {
                return false;
            }

            stats._statsListDirty = true;
            return true;
        }
        catch (Exception ex)
        {
            if (_markFailures++ < MaxWarnings)
            {
                ModLog.Warn($"Progress: couldn't mark the game's stats as changed before {why} ({ModLog.SafeErrorText(ex)}); that save writes only what the game " +
                    "marked itself, and the log line after it says whether the stats file changed.");
            }

            return false;
        }
    }

    /// <summary>
    /// Writes the key of entity <paramref name="entityId"/> of <paramref name="group"/> (in the set of
    /// <paramref name="gameType"/>, the humans' or the computer's: <paramref name="cpu"/>) from its stats in
    /// <paramref name="stats"/>, with the game's own <c>ES3StatSaveLoadHandler.SaveEntityStat</c> and set index
    /// (<c>StatRepository.GetEntitySetIndex</c>). Never throws: a failure is returned with <paramref name="problem"/>.
    /// </summary>
    public static EntityKeyWrite WriteEntityKey(StatRepository repository, IEntityStatGroup stats, GameType gameType, bool cpu, StatGroupType group,
        string entityId, out string? problem)
    {
        problem = null;
        try
        {
            IStatSaveLoadHandler? handler = repository._saveLoadHandler;
            if (handler == null)
            {
                problem = "the game's stats have no save handler";
                return EntityKeyWrite.Failed;
            }

            ES3StatSaveLoadHandler? easySave = handler.TryCast<ES3StatSaveLoadHandler>();
            if (easySave == null)
            {
                if (!_wholeSaveNoted)
                {
                    _wholeSaveNoted = true;
                    ModLog.Info("Progress: this game's stats aren't saved by Easy Save's handler, so its save writes every entity stat (as before the Nov 6 build); " +
                        "no entity key is written on its own.");
                }

                return EntityKeyWrite.WholeSave;
            }

            IEntityStat? stat = stats.GetStats(entityId);
            if (stat == null)
            {
                problem = "the entity's stats weren't there after filing the update";
                return EntityKeyWrite.Failed;
            }

            easySave.SaveEntityStat(StatRepository.GetEntitySetIndex(gameType, cpu), group, entityId, stat);
            return EntityKeyWrite.Written;
        }
        catch (Exception ex)
        {
            problem = ModLog.SafeErrorText(ex);
            return EntityKeyWrite.Failed;
        }
    }
}
#endif
