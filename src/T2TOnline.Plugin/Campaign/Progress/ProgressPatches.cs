#if HAS_INTEROP
using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Il2Cpp;
using Il2CppInterop.Runtime;
using Il2CppRogue.BenchQuests;
using Il2CppRogue.BenchSnapshots;
using Il2CppState.EndOfGame;
using Il2CppState.EndOfGame.Steps;
using Il2CppState.RunHistory;
using Il2CppStats.PersistentStats;
using Il2CppTape2Tape.Hockey.UI;
using Il2CppUnlocks;
using Il2CppUnlocks.Relics;
using T2TOnline.Core.Campaign;
using T2TOnline.Core.Diagnostics;
using T2TOnline.Plugin.Diagnostics;

namespace T2TOnline.Plugin.Campaign.Progress;

/// <summary>
/// The game methods shared progress watches (Hooks/hooks.txt, "profile progress for every player"; all unique, none
/// generic). Each patch does one of three jobs, decided by the PC's role in the session:
/// <list type="bullet">
/// <item>Host (recording, <see cref="ProgressModule.Recording"/>): after the game credited something, record it for
/// friends: every stat operation (the 24 concrete <c>IntStat</c>/<c>FloatStat</c> methods, which the game calls
/// through its stat interface, so none is inlined into a caller), every unlock check it ran, the squad record, comics,
/// the run history entry and the bench quest steps outside matches. Safe points (a stats save, the end of a match's
/// campaign update and of its unlock checks) seal what was recorded into a batch. The title screen's abandon of the
/// co-op run counts as the campaign's until its run reset.</item>
/// <item>Friend (<see cref="ProgressModule.HoldingBack"/>): the friend's own game never credits anything by itself
/// during a session (its producers would count what the host's game already counts, D42): its own
/// stat operations, unlock checks, unlock marks, stored-unlock commits, squad losses and run or act resets are held back
/// (whatever it changed in memory would go to disk with the next shared progress save). Only the shared progress applier
/// gets through (<see cref="ProgressFriend.Applying"/>), and the squad menu's grants of what this profile's own squad
/// record earned (the game raises their achievements with them).</item>
/// <item>Friend, applying: the three unlocks that read the live match (Bean Diet, Tribal Mask, Turbo Crutch) can't be
/// checked after the match, so while the applier runs the host's unlock check they answer what the host's check
/// earned.</item>
/// </list>
/// Patches go in at start-up and stay (D38); outside a session every one lets the game run at once. Nothing here throws
/// into the game: a patch that fails lets the game's own code run and is logged (the first few).
/// </summary>
internal static class ProgressPatches
{
    private static readonly (string Method, StatOp Op)[] StatMethods =
    {
        ("Increment", StatOp.Increment),
        ("IncrementCampaign", StatOp.IncrementCampaign),
        ("IncrementPlayNow", StatOp.IncrementPlayNow),
        ("IncrementBy", StatOp.IncrementBy),
        ("IncrementCampaignBy", StatOp.IncrementCampaignBy),
        ("IncrementPlayNowBy", StatOp.IncrementPlayNowBy),
        ("TrySetLargerValue", StatOp.TrySetLarger),
        ("TrySetLargerCampaignValue", StatOp.TrySetLargerCampaign),
        ("TrySetLargerPlayNowValue", StatOp.TrySetLargerPlayNow),
        ("TrySetSmallerValue", StatOp.TrySetSmaller),
        ("TrySetSmallerCampaignValue", StatOp.TrySetSmallerCampaign),
        ("TrySetSmallerPlayNowValue", StatOp.TrySetSmallerPlayNow),
    };

    private static readonly Dictionary<MethodBase, StatOp> Ops = new();
    private static readonly Dictionary<IntPtr, List<IUnlockData>> LiveMatchByUnlockSystem = new();
    private static bool _squadGrants;
    private static int _squadGrantsFrame = -1;
    private static bool _squadGrantLogged;

    /// <summary>Patches that failed (the first five are logged; the stop lines say the total).</summary>
    public static int Failures { get; private set; }

    /// <summary>Installs every patch; adds a line to <paramref name="failed"/> for each that couldn't go in.</summary>
    public static void Install(HarmonyLib.Harmony harmony, List<string> failed)
    {
        ProgressLedgerFile.Install();
        ProgressRunScopes.Install();
        foreach (Type type in new[] { typeof(IntStat), typeof(FloatStat) })
        {
            foreach ((string name, StatOp op) in StatMethods)
            {
                MethodInfo? method = AccessTools.DeclaredMethod(type, name);
                if (method == null)
                {
                    failed.Add($"progress: {type.Name}.{name} not found");
                    continue;
                }

                Ops[method] = op;
                Patch(harmony, failed, method,
                    method.ReturnType == typeof(bool) ? nameof(HoldBackRecordPrefix) : nameof(HoldBackPrefix),
                    type == typeof(IntStat) ? nameof(WholeOpPostfix) : nameof(RealOpPostfix));
            }
        }

        Patch(harmony, failed, AccessTools.Method(typeof(UnlockSystem), nameof(UnlockSystem.RunUnlockPass)), nameof(PassPrefix), nameof(PassPostfix));
        Patch(harmony, failed, AccessTools.Method(typeof(UnlockSystem), nameof(UnlockSystem.MarkUnlocked)), nameof(MarkUnlockedPrefix), null);
        Patch(harmony, failed, AccessTools.Method(typeof(UnlockSystem), nameof(UnlockSystem.CommitStoredUnlocks)), nameof(CommitStoredPrefix), null);
        Patch(harmony, failed, AccessTools.Method(typeof(BeanDietUnlock), nameof(BeanDietUnlock.TryUnlocking)), nameof(BeanDietPrefix), null);
        Patch(harmony, failed, AccessTools.Method(typeof(TribalMaskUnlock), nameof(TribalMaskUnlock.TryUnlocking)), nameof(TribalMaskPrefix), null);
        Patch(harmony, failed, AccessTools.Method(typeof(TurboCrutchUnlock), nameof(TurboCrutchUnlock.GetUnlockProgress)), nameof(TurboCrutchPrefix), null);
        Patch(harmony, failed, AccessTools.Method(typeof(StatRepository), nameof(StatRepository.Save)), null, nameof(StatsSavedPostfix));
        Patch(harmony, failed, AccessTools.Method(typeof(StatRepository), nameof(StatRepository.ResetAct)), nameof(HoldBackResetPrefix), null);
        Patch(harmony, failed, AccessTools.Method(typeof(StatRepository), nameof(StatRepository.ResetRun)), nameof(HoldBackResetPrefix), nameof(RunResetPostfix));
        Patch(harmony, failed, AccessTools.Method(typeof(CampaignUpdateStep), nameof(CampaignUpdateStep.Execute)), null, nameof(CampaignUpdatePostfix));
        Patch(harmony, failed, AccessTools.Method(typeof(StoreUnlocksStep), nameof(StoreUnlocksStep.Execute)), null, nameof(StoreUnlocksPostfix));
        Patch(harmony, failed, AccessTools.Method(typeof(TitleScreen), nameof(TitleScreen.DeleteCampaignSavedDataAsync)), nameof(TitleEndPrefix), null);
        Patch(harmony, failed, AccessTools.Method(typeof(UnlockSystem), nameof(UnlockSystem.GrantUnlocksEarnedBySquads)), nameof(SquadGrantsPrefix), nameof(SquadGrantsPostfix));
        Patch(harmony, failed, AccessTools.Method(typeof(ProfileData), nameof(ProfileData.RecordSquadLoss)), nameof(SquadLossPrefix), nameof(SquadLossPostfix));
        Patch(harmony, failed, AccessTools.Method(typeof(RunHistoryRepository), nameof(RunHistoryRepository.SaveEntry)), nameof(RunHistoryPrefix), null);
        Patch(harmony, failed, AccessTools.Method(typeof(BenchQuestSystem), nameof(BenchQuestSystem.OnRewardAssigned)), null, nameof(RewardAssignedPostfix));
        Patch(harmony, failed, AccessTools.Method(typeof(BenchQuestSystem), nameof(BenchQuestSystem.OnNewActCreated)), null, nameof(NewActPostfix));
        Patch(harmony, failed, AccessTools.Method(typeof(BenchQuestSystem), nameof(BenchQuestSystem.OnRunFailed)), null, nameof(RunFailedPostfix));

        // What the game sets outright: the win streak, the record holders, entity stats.
        Patch(harmony, failed, AccessTools.Method(typeof(EndOfGameStatsHandler), nameof(EndOfGameStatsHandler.AddWinStreakStats)), nameof(WinStreakPrefix), nameof(WinStreakPostfix));
        Patch(harmony, failed, AccessTools.Method(typeof(EndOfGameStatsHandler), nameof(EndOfGameStatsHandler.AddTeamStats)), nameof(TeamStatsPrefix), nameof(TeamStatsPostfix));
        Patch(harmony, failed, AccessTools.Method(typeof(StatRepository), nameof(StatRepository.GetEntityStatGroups)), null, nameof(EntityGroupsPostfix));
        Patch(harmony, failed, AccessTools.Method(typeof(SkaterStatDefinition), nameof(SkaterStatDefinition.Combine)), nameof(SkaterCombinePrefix), null);
        Patch(harmony, failed, AccessTools.Method(typeof(GoalieStatDefinition), nameof(GoalieStatDefinition.Combine)), nameof(GoalieCombinePrefix), null);
        Patch(harmony, failed, AccessTools.Method(typeof(TeamStatDefinition), nameof(TeamStatDefinition.Combine)), nameof(TeamCombinePrefix), null);
        Patch(harmony, failed, AccessTools.Method(typeof(TalentStatDefinition), nameof(TalentStatDefinition.Combine)), nameof(TalentCombinePrefix), null);
        Patch(harmony, failed, AccessTools.Method(typeof(AbilityStatDefinition), nameof(AbilityStatDefinition.Combine)), nameof(AbilityCombinePrefix), null);
        Patch(harmony, failed, AccessTools.Method(typeof(RelicStatDefinition), nameof(RelicStatDefinition.Combine)), nameof(RelicCombinePrefix), null);

        // The run's end for the benchwarmers (v1): the host's recorded, a friend's own only from the applier. The
        // Nurse's offers and the NPC lines seen are recorded from MenuPatches' patches on their methods
        // (NurseOffered, DialogueSeen), which hold a friend's own back.
        Patch(harmony, failed, AccessTools.Method(typeof(BenchSnapshotService), nameof(BenchSnapshotService.HandleRunEnding)), nameof(RunEndingPrefix), nameof(RunEndingPostfix));
    }

    private static void Patch(HarmonyLib.Harmony harmony, List<string> failed, MethodInfo? method, string? prefix, string? postfix)
    {
        if (method == null)
        {
            failed.Add($"progress: a patch target wasn't found ({prefix ?? postfix})");
            return;
        }

        try
        {
            harmony.Patch(method,
                prefix: prefix == null ? null : new HarmonyMethod(typeof(ProgressPatches), prefix),
                postfix: postfix == null ? null : new HarmonyMethod(typeof(ProgressPatches), postfix));
        }
        catch (Exception ex)
        {
            failed.Add($"progress: {method.DeclaringType?.Name}.{method.Name} ({ModLog.SafeErrorText(ex)})");
        }
    }

    // ------------------------------------------------------------------ stat operations

    private static bool HoldBackPrefix(MethodBase __originalMethod) => !HeldBack(__originalMethod);

    private static bool HoldBackRecordPrefix(MethodBase __originalMethod, ref bool __result)
    {
        if (!HeldBack(__originalMethod))
        {
            return true;
        }

        __result = false;
        return false;
    }

    private static bool HeldBack(MethodBase method)
    {
        try
        {
            if (!ProgressModule.HoldingBack)
            {
                return false;
            }

            ProgressModule.NoteHeldBack($"stat {Ops.GetValueOrDefault(method)}");
            return true;
        }
        catch (Exception ex)
        {
            Failed("holding back a stat operation", ex);
            return false;
        }
    }

    private static void WholeOpPostfix(MethodBase __originalMethod, IntStat __instance, object[] __args)
    {
        try
        {
            if (ProgressModule.Recording)
            {
                RecordOp(__originalMethod, (int)__instance.StatType, __args);
            }
        }
        catch (Exception ex)
        {
            Failed("recording a stat operation", ex);
        }
    }

    private static void RealOpPostfix(MethodBase __originalMethod, FloatStat __instance, object[] __args)
    {
        try
        {
            if (ProgressModule.Recording)
            {
                RecordOp(__originalMethod, (int)__instance.StatType, __args);
            }
        }
        catch (Exception ex)
        {
            Failed("recording a stat operation", ex);
        }
    }

    private static void RecordOp(MethodBase method, int stat, object[] args)
    {
        if (!Ops.TryGetValue(method, out StatOp op))
        {
            return;
        }

        StatValue value = StatOps.HasValue(op) ? ValueOf(args) : StatValue.None;
        if (StatOps.HasValue(op) && value.Kind == StatValueKind.None)
        {
            ProgressModule.Host?.NoteUnreadable(op);
            return;
        }

        int frame = UnityEngine.Time.frameCount;
        if (_winStreakFrame == frame && stat == ProgressGameStats.LargestWinStreak && op is StatOp.TrySetLarger or StatOp.TrySetLargerCampaign or StatOp.TrySetLargerPlayNow)
        {
            // The game raises the largest streak to the host's own current streak: each profile's comes from its own.
            op = op == StatOp.TrySetLarger ? StatOp.TrySetLargerFromStat : op == StatOp.TrySetLargerCampaign ? StatOp.TrySetLargerCampaignFromStat : StatOp.TrySetLargerPlayNowFromStat;
            value = StatValue.Whole(ProgressGameStats.CurrentWinStreak);
        }

        ProgressModule.Host?.Record(new StatEntry(stat, op, value));
        if (_teamName != null && _teamStatsFrame == frame && op == StatOp.TrySetLarger && ProgressGameStats.HolderOf(stat) is int holder and >= 0)
        {
            // Right after the lifetime record the game names the team holding it, if it rose (on each profile, its own).
            ProgressModule.Host?.Record(new StatTextEntry(holder, stat, _teamName, _teamInCampaign));
        }
    }

    // ------------------------------------------------------------------ what the game sets outright

    private static int _winStreakFrame = -1;
    private static StatOp _streakReset;
    private static string? _teamName;
    private static int _teamStatsFrame = -1;
    private static bool _teamInCampaign;

    /// <summary>
    /// Host: the game's win streak at the end of a match (<c>AddWinStreakStats</c>): on a win the streak's +1 is an operation
    /// already recorded and the largest streak is raised to it (recorded as "from the current streak"); on a loss the game
    /// sets the streak back to zero outright, which this records. The game's own conditions: a team with players, a game
    /// that tracks stats.
    /// </summary>
    private static void WinStreakPrefix(EndOfGameStatsHandler __instance, Il2CppTape2Tape.Team team, bool isWinner)
    {
        try
        {
            _streakReset = StatOp.None;
            if (!ProgressModule.Recording)
            {
                return;
            }

            _winStreakFrame = UnityEngine.Time.frameCount;
            if (!isWinner && team?._players is { Count: > 0 } && __instance._statManager?.StatRepository?.ShouldSkipStatTracking() == false)
            {
                _streakReset = Il2CppTape2Tape.T2TUtils.IsInRogueMode ? StatOp.ResetCampaign : StatOp.ResetPlayNow;
            }
        }
        catch (Exception ex)
        {
            Failed("watching the win streak", ex);
        }
    }

    private static void WinStreakPostfix()
    {
        try
        {
            _winStreakFrame = -1;
            StatOp reset = _streakReset;
            _streakReset = StatOp.None;
            if (reset != StatOp.None && ProgressModule.Recording)
            {
                ProgressModule.Host?.Record(new StatEntry(ProgressGameStats.CurrentWinStreak, reset, StatValue.None));
            }
        }
        catch (Exception ex)
        {
            Failed("recording the win streak's reset", ex);
        }
    }

    /// <summary>Host: the human team's end-of-match stats (<c>AddTeamStats</c>): its name goes with the records it may hold.</summary>
    private static void TeamStatsPrefix(Il2CppTape2Tape.Team team)
    {
        try
        {
            _teamName = null;
            if (!ProgressModule.Recording || team?._players is not { Count: > 0 })
            {
                return;
            }

            string? name = team._teamData?.teamName;
            if (!string.IsNullOrEmpty(name) && ProgressLimits.IsPlausibleText(name))
            {
                _teamName = name;
                _teamStatsFrame = UnityEngine.Time.frameCount;
                _teamInCampaign = Il2CppTape2Tape.T2TUtils.IsInRogueMode;
            }
        }
        catch (Exception ex)
        {
            Failed("watching the team's end-of-match stats", ex);
        }
    }

    private static void TeamStatsPostfix() => _teamName = null;

    private static void EntityGroupsPostfix(Il2CppScriptableStates.GameType gameType, bool isCPU)
    {
        try
        {
            if (ProgressModule.Recording)
            {
                ProgressEntityStats.SetAsked(gameType, isCPU);
            }
        }
        catch (Exception ex)
        {
            Failed("watching an entity stat update", ex);
        }
    }

    private static void SkaterCombinePrefix(IEntityStat other) => Combining(EntityStatGroup.Skater, other);

    private static void GoalieCombinePrefix(IEntityStat other) => Combining(EntityStatGroup.Goalie, other);

    private static void TeamCombinePrefix(IEntityStat other) => Combining(EntityStatGroup.Team, other);

    private static void TalentCombinePrefix(IEntityStat other) => Combining(EntityStatGroup.Talent, other);

    private static void AbilityCombinePrefix(IEntityStat other) => Combining(EntityStatGroup.Ability, other);

    private static void RelicCombinePrefix(IEntityStat other) => Combining(EntityStatGroup.Relic, other);

    private static void Combining(EntityStatGroup group, IEntityStat? other)
    {
        try
        {
            if (ProgressModule.Recording)
            {
                ProgressEntityStats.Combining(group, other);
            }
        }
        catch (Exception ex)
        {
            Failed("reading an entity stat update", ex);
        }
    }

    /// <summary>The operation's value: a plain number (the record methods) or the game's boxed one (the "By" methods).</summary>
    private static StatValue ValueOf(object[] args)
    {
        object? arg = args is { Length: > 0 } ? args[0] : null;
        switch (arg)
        {
            case int whole:
                return StatValue.Whole(whole);
            case float real:
                return StatValue.Real(real);
            case Il2CppSystem.Object boxed:
                IntPtr klass = boxed.ObjectClass;
                if (klass == Il2CppClassPointerStore<int>.NativeClassPtr)
                {
                    return StatValue.Whole(boxed.Unbox<int>());
                }

                return klass == Il2CppClassPointerStore<float>.NativeClassPtr ? StatValue.Real(boxed.Unbox<float>()) : StatValue.None;
            default:
                return StatValue.None;
        }
    }

    // ------------------------------------------------------------------ unlock checks

    /// <summary>A recorded unlock check: how many unlocks its list held before it ran.</summary>
    private struct PassState
    {
        public bool Record;
        public int Before;
    }

    private static bool PassPrefix(UnlockSystem __instance, Il2CppSystem.Collections.Generic.List<IUnlockData> newUnlocks, out PassState __state)
    {
        __state = default;
        try
        {
            if (ProgressModule.HoldingBack)
            {
                ProgressModule.NoteHeldBack("unlock check");
                return false;
            }

            // A check the game skips (training difficulty, a practice match) earns nothing on the couch either.
            if (ProgressModule.Recording && __instance._statManager?.ShouldSkipStatTracking() == false)
            {
                __state = new PassState { Record = true, Before = newUnlocks?.Count ?? 0 };
            }
        }
        catch (Exception ex)
        {
            Failed("watching an unlock check", ex);
        }

        return true;
    }

    private static void PassPostfix(UnlockSystem __instance, UnlockTiming timing, Il2CppSystem.Collections.Generic.List<IUnlockData> newUnlocks,
        bool queueNotifications, ENotificationTiming notificationTiming, PassState __state)
    {
        if (!__state.Record)
        {
            return;
        }

        try
        {
            var earned = new List<string>();
            var unlocked = new List<string>();
            for (int i = __state.Before; newUnlocks != null && i < newUnlocks.Count; i++)
            {
                if (newUnlocks[i] is IUnlockData data)
                {
                    unlocked.Add(Describe(data));
                    if (IsLiveMatch(data))
                    {
                        earned.Add(data.RewardID);
                    }
                }
            }

            if (unlocked.Count > 0)
            {
                ModLog.Info($"Progress: this profile unlocked {string.Join(", ", unlocked)} ({timing} check, at its own thresholds; friends check their own).");
            }

            // The game's check skips an unlock the host already owns (or stored this run), so it can't say whether this
            // match earned it: ask the unlock itself, as the check would have.
            foreach (IUnlockData live in LiveMatchUnlocks(__instance))
            {
                string id = live.RewardID;
                if (live.Timing == timing && !earned.Contains(id) && TryLiveUnlock(live))
                {
                    earned.Add(id);
                }
            }

            ProgressModule.Host?.Record(new UnlockPassEntry((int)timing, (int)notificationTiming, queueNotifications, earned));
        }
        catch (Exception ex)
        {
            Failed("recording an unlock check", ex);
        }
    }

    /// <summary>
    /// The shared run's Mark Bench flag (<c>RunData.benchQuestData.markBenchQuestValid</c>) goes to friends in the host's
    /// checkpoint, and each friend's own quest reads it when the run is won. The game's check after a match
    /// (<c>CheckMarkBenchLineup</c>) skips it when the host has finished that quest itself, which would leave the run saying
    /// "still valid" for every friend: in that case it runs once more here with the host's mark lifted for the call, so the
    /// run tells the truth for every player. Never throws.
    /// </summary>
    private static void CheckMarkBenchForEveryone()
    {
        try
        {
            BenchQuestProfileData? data = ProfileData.Instance?.benchQuestData;
            BenchQuestSystem? quests = ProgressFriend.FromProject<BenchQuestSystem>();
            if (data == null || quests == null || !data.isMarkBenchCompleted || quests.IsDisabled || BenchQuestSystem.IsDifficultyTooLow)
            {
                return;
            }

            data.isMarkBenchCompleted = false;
            try
            {
                quests.CheckMarkBenchLineup();
            }
            finally
            {
                data.isMarkBenchCompleted = true;
            }
        }
        catch (Exception ex)
        {
            Failed("checking the Mark Bench lineup for friends", ex);
        }
    }

    /// <summary>An unlock for a log line: its name, category and id.</summary>
    internal static string Describe(IUnlockData unlock)
    {
        string id = unlock.RewardID ?? "?";
        try
        {
            return $"{SafeText.Clean(unlock.RewardName, 60)} ({unlock.Category}, {SafeText.Clean(id, 64)})";
        }
        catch (Exception)
        {
            return SafeText.Clean(id, 64);
        }
    }

    private static bool TryLiveUnlock(IUnlockData live)
    {
        try
        {
            return live.TryUnlocking();
        }
        catch (Exception)
        {
            return false; // it reads the live match, which may be gone (a check outside a match)
        }
    }

    /// <summary>True for the three unlocks that read the live match.</summary>
    internal static bool IsLiveMatch(IUnlockData data) =>
        data.TryCast<BeanDietUnlock>() != null || data.TryCast<TribalMaskUnlock>() != null || data.TryCast<TurboCrutchUnlock>() != null;

    /// <summary>The live-match unlocks <paramref name="unlocks"/> knows (kept per unlock system).</summary>
    private static List<IUnlockData> LiveMatchUnlocks(UnlockSystem unlocks)
    {
        if (LiveMatchByUnlockSystem.TryGetValue(unlocks.Pointer, out List<IUnlockData>? found))
        {
            return found;
        }

        found = new List<IUnlockData>();
        Il2CppSystem.Collections.Generic.Dictionary<string, IUnlockData>? all = unlocks._unlockData;
        if (all != null)
        {
            Il2CppSystem.Collections.Generic.Dictionary<string, IUnlockData>.Enumerator items = all.GetEnumerator();
            while (items.MoveNext())
            {
                IUnlockData? data = items.Current.Value;
                if (data != null && IsLiveMatch(data))
                {
                    found.Add(data);
                }
            }
        }

        // An empty list may mean the unlock data isn't loaded yet: only a full one is kept.
        if (found.Count > 0)
        {
            LiveMatchByUnlockSystem[unlocks.Pointer] = found;
        }

        return found;
    }

    private static bool BeanDietPrefix(BeanDietUnlock __instance, ref bool __result) => AnswerLive(__instance.RewardID, ref __result);

    private static bool TribalMaskPrefix(TribalMaskUnlock __instance, ref bool __result) => AnswerLive(__instance.RewardID, ref __result);

    private static bool TurboCrutchPrefix(TurboCrutchUnlock __instance, ref int __result)
    {
        bool earned = false;
        if (AnswerLive(__instance.RewardID, ref earned))
        {
            return true;
        }

        // Its check is "progress at least the requirement": any progress the requirement can't exceed.
        __result = earned ? int.MaxValue / 2 : 0;
        return false;
    }

    /// <summary>
    /// While a friend's applier runs the host's check: the host's answer. False when it answered (skip the original). Asked
    /// of the applier itself, so the session-end flush (after the module let go of it) answers the same way.
    /// </summary>
    private static bool AnswerLive(string id, ref bool earned)
    {
        try
        {
            if (ProgressFriend.ApplyingNow is not { Applying: true } friend)
            {
                return true;
            }

            earned = friend.LiveEarned(id);
            return false;
        }
        catch (Exception ex)
        {
            Failed("answering a live-match unlock", ex);
            earned = false;
            return false; // while applying, never let the friend's own match state decide
        }
    }

    private static bool MarkUnlockedPrefix(string id)
    {
        try
        {
            if (Menus.UnlockViews.Converting)
            {
                // The new-unlocks screen's own converter, run over this player's unlocks (already unlocked): nothing to mark.
                return false;
            }

            if (!ProgressModule.HoldingBack)
            {
                return true;
            }

            if (_squadGrants && _squadGrantsFrame == UnityEngine.Time.frameCount)
            {
                // The squad menu grants what this profile's own squad record earned (its own thresholds), and raises the
                // unlock's achievement right after: held back, the achievement would come without its unlock.
                if (!_squadGrantLogged)
                {
                    _squadGrantLogged = true;
                    ModLog.Info($"Progress: the squad menu here granted {SafeText.Clean(id)}, earned by this profile's own squad record; squad-earned unlocks " +
                        "are recorded while following (the game raises their achievements with them; logged once).");
                }

                Menus.UnlockViews.NotedForNextRun(id);
                return true;
            }

            ProgressModule.NoteHeldBackUnlock(id);
            return false;
        }
        catch (Exception ex)
        {
            Failed("holding back an unlock", ex);
            return true;
        }
    }

    private static void SquadGrantsPrefix()
    {
        _squadGrants = true;
        _squadGrantsFrame = UnityEngine.Time.frameCount; // it runs at once: a grant that threw can't leave this on
    }

    private static void SquadGrantsPostfix() => _squadGrants = false;

    private static bool CommitStoredPrefix()
    {
        try
        {
            if (!ProgressModule.HoldingBack)
            {
                return true;
            }

            // The run's stored unlocks here are the host's (its checkpoint): committing them would hand this profile the
            // host's unlocks at the host's thresholds.
            ProgressModule.NoteHeldBackUnlock("(the run's stored unlocks)");
            return false;
        }
        catch (Exception ex)
        {
            Failed("holding back stored unlocks", ex);
            return true;
        }
    }

    // ------------------------------------------------------------------ safe points and the run's end

    private static void StatsSavedPostfix()
    {
        try
        {
            if (ProgressModule.Recording)
            {
                ProgressModule.Host?.Seal("stats saved");
            }
        }
        catch (Exception ex)
        {
            Failed("sealing a batch", ex);
        }
    }

    private static void CampaignUpdatePostfix(EndOfGameContext ctx)
    {
        try
        {
            if (ctx != null)
            {
                Online.OnlineSession.RunEndNoted?.Invoke(ctx.IsRunWin, ctx.IsRunFailed || ctx.IsRunConceded); // the lobby's ribbon says what the game says
            }

            if (!ProgressModule.Recording || ProgressModule.Host is not ProgressHost host || ctx == null)
            {
                return;
            }

            try
            {
                // In the game's order: the Mark Bench check after every match, then on a win the quest and the squad record.
                CheckMarkBenchForEveryone();
                host.Record(new QuestEntry(QuestStep.MatchEnded, 0));
                if (ctx.IsRunWin)
                {
                    host.Record(new QuestEntry(QuestStep.RunWon, 0));
                    Il2CppState.CampaignState? campaign = ctx.CampaignState;
                    Il2CppData.RunData? run = campaign?.runData;
                    if (campaign != null && run != null)
                    {
                        (string scorer, int goals) = BestScorer(campaign);
                        host.Record(new SquadEntry(SquadOutcome.Won, run.squadId ?? string.Empty, run.difficultyLevel, campaign.GetTotalRunTime(), scorer, goals));
                    }
                }
            }
            finally
            {
                // The match is over (the step runs at once, before the run-end menus that can hold the scene): this goes out
                // now, and what follows (the comics, the unlock checks) at the next quiet moment.
                host.MatchOver("the match's campaign update");
            }
        }
        catch (Exception ex)
        {
            Failed("recording the end of a match", ex);
        }
    }

    private static void StoreUnlocksPostfix()
    {
        try
        {
            if (ProgressModule.Recording)
            {
                ProgressModule.Host?.MatchOver("the end-of-match unlock checks");
            }
        }
        catch (Exception ex)
        {
            Failed("sealing the end-of-match unlock checks", ex);
        }
    }

    private static void TitleEndPrefix()
    {
        try
        {
            if (ProgressModule.Recording)
            {
                ProgressModule.Host?.TitleEndStarted();
            }
        }
        catch (Exception ex)
        {
            Failed("watching the run's end at the title screen", ex);
        }
    }

    /// <summary>Friend: the run's or act's reset of this PC's own "this run"/"this act" values is held back while following.</summary>
    private static bool HoldBackResetPrefix(MethodBase __originalMethod)
    {
        try
        {
            if (!ProgressModule.HoldingBack)
            {
                return true;
            }

            // A local new-act event (a map the guard refused to make still raises it) would zero this PC's own run's act
            // values, and the next shared save would write them.
            ProgressModule.NoteHeldBack($"stats {__originalMethod.Name}");
            return false;
        }
        catch (Exception ex)
        {
            Failed("holding back a stats reset", ex);
            return true;
        }
    }

    private static void RunResetPostfix()
    {
        try
        {
            ProgressModule.Host?.TitleEndDone();
        }
        catch (Exception ex)
        {
            Failed("closing the run's end at the title screen", ex);
        }
    }

    private static bool SquadLossPrefix()
    {
        try
        {
            if (!ProgressModule.HoldingBack)
            {
                return true;
            }

            // The run here is the host's: its loss comes with the host's progress, once (ProfileData.OnRunLost and
            // OnRunConceded, which call this, are folded: this is where it can be held back).
            ProgressModule.NoteHeldBack("squad loss");
            return false;
        }
        catch (Exception ex)
        {
            Failed("holding back a squad loss", ex);
            return true;
        }
    }

    private static void SquadLossPostfix(ProfileData __instance)
    {
        try
        {
            if (!ProgressModule.Recording || ProgressModule.Host is not ProgressHost host)
            {
                return;
            }

            Il2CppState.CampaignState? campaign = __instance.campaignState;
            Il2CppData.RunData? run = campaign?.runData;
            if (campaign != null && run != null)
            {
                (string scorer, int goals) = BestScorer(campaign);
                host.Record(new SquadEntry(SquadOutcome.Lost, run.squadId ?? string.Empty, run.difficultyLevel, campaign.GetTotalRunTime(), scorer, goals));
            }
        }
        catch (Exception ex)
        {
            Failed("recording a squad loss", ex);
        }
    }

    /// <summary>
    /// Host: the run's best scorer, as the game's squad record takes it from the run's scorers right after this
    /// (<see cref="RunScorers.Best"/>): friends' games record with it, since their run holds other scorers. None when the run
    /// stats aren't there (logged once).
    /// </summary>
    private static (string Name, int Goals) BestScorer(Il2CppState.CampaignState campaign)
    {
        Il2CppSystem.Collections.Generic.List<ScorersStats>? scorers = campaign.runStatsState?.runScorersStats;
        if (scorers == null)
        {
            ProgressModule.Host?.NoteOnce("no scorers", "Progress: the run's scorers weren't there at its end; friends' squad records keep their own most goals in a run.");
            return (string.Empty, 0);
        }

        var read = new List<(string? Name, int Goals)>(scorers.Count);
        for (int i = 0; i < scorers.Count; i++)
        {
            if (scorers[i] is ScorersStats scorer)
            {
                read.Add((scorer.displayName, scorer.goals));
            }
        }

        (string name, int goals) = RunScorers.Best(read, out string problem);
        if (problem.Length > 0)
        {
            ProgressModule.Host?.NoteOnce("scorer", $"Progress: {problem}; friends' squad records keep their own most goals in a run.");
        }

        return (name, goals);
    }

    /// <summary>
    /// The game ends a run for its benchwarmers (<c>BenchSnapshotService.HandleRunEnding</c>, from the run's end and the title
    /// screen's abandon): a friend's own is held back while it follows (its producers aren't the host's run's end; the
    /// applier's goes through, <see cref="ProgressModule.HoldingBack"/>).
    /// </summary>
    private static bool RunEndingPrefix()
    {
        try
        {
            if (!ProgressModule.HoldingBack)
            {
                return true;
            }

            ProgressModule.NoteHeldBack("run's end for the benchwarmers");
            return false;
        }
        catch (Exception ex)
        {
            Failed("holding back a run's end for the benchwarmers", ex);
            return true;
        }
    }

    /// <summary>Host: the shared run ended for its benchwarmers: each friend's game takes its own snapshots on its copy of
    /// the run.</summary>
    private static void RunEndingPostfix()
    {
        try
        {
            if (ProgressModule.Recording)
            {
                ProgressModule.Host?.Record(new RunEndingEntry());
            }
        }
        catch (Exception ex)
        {
            Failed("recording the run's end for the benchwarmers", ex);
        }
    }

    /// <summary>
    /// Host (from MenuPatches' postfix on <c>NurseProcedures.MarkOfferedAndSave</c>): the host's Nurse offered
    /// <paramref name="procedure"/>, marked on every friend's profile too.
    /// </summary>
    internal static void NurseOffered(Il2CppRogue.NPC.NurseNPC.Procedures.NurseProcedureState.Procedure procedure)
    {
        try
        {
            if (ProgressModule.Recording)
            {
                ProgressModule.Host?.Record(ProfileMarkEntry.NurseOffered((int)procedure));
            }
        }
        catch (Exception ex)
        {
            Failed("recording a Nurse offer", ex);
        }
    }

    /// <summary>
    /// Host (from MenuPatches' postfix on <c>SeenDialogues.MarkSeen</c>): the host's profile marked dialogue
    /// <paramref name="id"/> seen. Only an event NPC's line goes to friends (<see cref="ProfileMarks"/>: a first visit's or a
    /// visit line's of one of the game's dialogue pools, never a run's mark, a tip or a training milestone), which every
    /// friend heard from the host's (D62).
    /// </summary>
    internal static void DialogueSeen(Il2Cpp.SeenDialogues seen, string id)
    {
        try
        {
            if (!ProgressModule.Recording || seen == null || ProfileData.Instance?.seenDialogues?.Pointer != seen.Pointer ||
                !ProfileMarks.TryReadNpcLine(id, out string pool) || !NpcPools.Has(pool))
            {
                return;
            }

            ProgressModule.Host?.Record(ProfileMarkEntry.DialogueSeen(id));
        }
        catch (Exception ex)
        {
            Failed("recording a seen NPC line", ex);
        }
    }

    /// <summary>
    /// The ids of the game's event NPC dialogue pools (<c>NpcDialoguePool.FirstVisitSeenId</c>), read from the pools loaded
    /// (the one whose line was just marked is: its NPC is on screen). Read again, at most once a second, when an id isn't
    /// known: a seen mark is rare (an event's visit, a tip).
    /// </summary>
    private static class NpcPools
    {
        private static readonly HashSet<string> Ids = new(StringComparer.Ordinal);
        private static double _readAt = double.MinValue;

        public static bool Has(string pool)
        {
            if (Ids.Contains(pool))
            {
                return true;
            }

            double now = Streaming.FrameClock.Seconds;
            if (now - _readAt < 1)
            {
                return false;
            }

            _readAt = now;
            foreach (UnityEngine.Object found in UnityEngine.Resources.FindObjectsOfTypeAll(Il2CppType.Of<Il2CppTape2Tape.Dialogue.NpcDialoguePool>()))
            {
                if (found?.TryCast<Il2CppTape2Tape.Dialogue.NpcDialoguePool>()?.FirstVisitSeenId is { Length: > 0 } id)
                {
                    Ids.Add(id);
                }
            }

            return Ids.Contains(pool);
        }
    }

    private static void RunHistoryPrefix(RunHistoryEntryModel entry)
    {
        try
        {
            if (ProgressModule.Recording && entry != null)
            {
                ProgressModule.Host?.Record(new RunHistoryRecord(RunHistoryMapping.ToData(entry)));
            }
        }
        catch (Exception ex)
        {
            Failed("recording the run history entry", ex);
        }
    }

    private static void RewardAssignedPostfix() => RecordQuest(QuestStep.RewardAssigned, 0);

    private static void NewActPostfix(int act) => RecordQuest(QuestStep.NewAct, Math.Clamp(act, 0, ProgressLimits.MaxAct));

    private static void RunFailedPostfix() => RecordQuest(QuestStep.RunFailed, 0);

    private static void RecordQuest(QuestStep step, int act)
    {
        try
        {
            if (ProgressModule.Recording)
            {
                ProgressModule.Host?.Record(new QuestEntry(step, act));
            }
        }
        catch (Exception ex)
        {
            Failed("recording a bench quest step", ex);
        }
    }

    private static void Failed(string what, Exception ex)
    {
        if (Failures++ < 5)
        {
            ModLog.Warn($"Progress: failed {what} ({ModLog.SafeErrorText(ex)}){(Failures == 5 ? "; further failures are counted (the session's stop line says how many), not logged" : string.Empty)}.");
        }
    }
}
#endif
