#if HAS_INTEROP
using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Il2CppBishopGames.UI;
using Il2CppData;
using Il2CppRogue.NPC.NurseNPC.Procedures;
using Il2CppSkaters.Injuries;
using T2TOnline.Core.Campaign;
using T2TOnline.Plugin.Diagnostics;

namespace T2TOnline.Plugin.Campaign.Menus;

/// <summary>
/// The Nurse procedure screen's patches (v1; Hooks/hooks.txt, "the
/// Nurse's procedure screen as a real view"): all on unfolded, non-generic methods with no small by-value struct
/// (hookcheck). Host: the screen's five native input handlers (prefixes, and postfixes for the named actions) note each
/// press its own gate lets through, and its commit check's postfix sends what the commit did (<see cref="NurseHost"/>);
/// they only read. Friend, while following a host: the injury draws the game's roster factory makes answer the host's while
/// this PC rebuilds the host's roster (<see cref="NurseRosters.Armed"/>: only inside that call, so never for the game's own
/// draws); a procedure screen never takes this PC's own input (its <c>RegisterInput</c> is skipped: the host's presses are
/// replayed through its handlers instead), never opens a lineup of its own (the host's comes as its own view), and never
/// commits (its <c>CommitIfConfirmed</c> is skipped, after the host's outcome went into its state: no run change, no
/// injury stat, no reward event on this PC).
/// </summary>
internal static partial class MenuPatches
{
    // The friend's patches for the real procedure screen went in.
    private static bool _nurseFriend;

    /// <summary>True when the Nurse's procedure screen can be shown for real on a friend (otherwise it shows as text).</summary>
    public static bool NurseScreenReady => NurseReady && _nurseFriend;

    /// <summary>The Nurse procedure screen's patches (called from <see cref="InstallEvents"/>).</summary>
    private static void InstallNurse(HarmonyLib.Harmony harmony, List<string> failed)
    {
        // Friend: the roster factory's own injury draws, answered with the host's while it rebuilds the host's roster.
        bool friend = PatchOverload(harmony, failed, typeof(InjurySystem), "GetRandomInjury", new[] { typeof(ForwardData), typeof(TeamData) }, nameof(NurseInjuryDrawPrefix));
        friend &= Patch(harmony, failed, typeof(InjurySystem), "GetRandomizedHealingTimeInEvent", prefix: nameof(NurseGamesDrawPrefix));

        // Friend: the procedure screen opened here takes no input of its own, opens no lineup and never commits.
        friend &= Patch(harmony, failed, typeof(NurseProcedureScreen), "RegisterInput", prefix: nameof(NurseRegisterInputPrefix));
        friend &= Patch(harmony, failed, typeof(NurseProcedureScreen), "OpenLineup", prefix: nameof(NurseOpenLineupPrefix));
        bool commit = Patch(harmony, failed, typeof(NurseProcedureScreen), "CommitIfConfirmed", prefix: nameof(NurseCommitPrefix), postfix: nameof(NurseCommitPostfix));
        _nurseFriend = friend && commit;

        // Friend: what the game's popups gate on the commit skipped here. Without these its result shows without them.
        Patch(harmony, failed, typeof(NurseProcedureScreen), "BuildScreen", postfix: nameof(NurseBuiltPostfix));
        Patch(harmony, failed, typeof(NurseProcedureScreen), "PlacePendingSuture", prefix: nameof(NurseSuturePrefix));

        // Host: each press the screen takes, and the commit's outcome.
        bool host = Patch(harmony, failed, typeof(NurseProcedureScreen), "OnNativeConfirm", prefix: nameof(NurseConfirmPrefix));
        host &= Patch(harmony, failed, typeof(NurseProcedureScreen), "OnNativeBack", prefix: nameof(NurseBackPrefix));
        host &= Patch(harmony, failed, typeof(NurseProcedureScreen), "OnNativeDirection", prefix: nameof(NurseDirectionPrefix));
        host &= Patch(harmony, failed, typeof(NurseProcedureScreen), "OnNativeInputPressed", prefix: nameof(NurseActionPrefix), postfix: nameof(NurseActionPressedPostfix));
        host &= Patch(harmony, failed, typeof(NurseProcedureScreen), "OnNativeInputReleased", prefix: nameof(NurseActionPrefix), postfix: nameof(NurseActionReleasedPostfix));
        NurseHost.CaptureReady = host && commit;
    }

    /// <summary>Patches one overload of a method (its parameter types given) with a prefix; false (with a line in <paramref name="failed"/>) when it couldn't go in.</summary>
    private static bool PatchOverload(HarmonyLib.Harmony harmony, List<string> failed, Type type, string method, Type[] parameters, string prefix)
    {
        try
        {
            MethodInfo? target = AccessTools.DeclaredMethod(type, method, parameters);
            if (target == null)
            {
                failed.Add($"menus: {type.Name}.{method}({string.Join(", ", Array.ConvertAll(parameters, p => p.Name))}) not found");
                return false;
            }

            harmony.Patch(target, prefix: new HarmonyMethod(typeof(MenuPatches).GetMethod(prefix, BindingFlags.NonPublic | BindingFlags.Static)));
            return true;
        }
        catch (Exception ex)
        {
            failed.Add($"menus: {type.Name}.{method}: {ModLog.SafeErrorText(ex)}");
            return false;
        }
    }

    /// <summary>
    /// Friend, only while this PC rebuilds the host's Nurse roster with the game's factory: a skater's injury offer is the
    /// host's (one of the host's type for a skater the host's roster offered one, else none). Otherwise the game's own draw.
    /// </summary>
    private static bool NurseInjuryDrawPrefix(ForwardData forward, ref Injury __result)
    {
        try
        {
            if (NurseRosters.Armed)
            {
                __result = NurseRosters.ArmedOffer(forward)!;
                return false;
            }
        }
        catch (Exception ex)
        {
            // The game's own draw then: the rebuilt roster differs and its check shows the screen as text.
            Failed("Nurse injury draw", ex);
        }

        return true;
    }

    /// <summary>Friend, only while this PC rebuilds the host's Nurse roster: the offers' games are the host's.</summary>
    private static bool NurseGamesDrawPrefix(ref int __result)
    {
        if (!NurseRosters.Armed)
        {
            return true;
        }

        __result = NurseRosters.ArmedGames;
        return false;
    }

    /// <summary>
    /// Friend, while following: a procedure screen's native input isn't registered (its only caller is its <c>Awake</c>), so
    /// this PC's own pad and keys never drive it; the host's presses are replayed through the same handlers instead.
    /// </summary>
    private static bool NurseRegisterInputPrefix()
    {
        try
        {
            if (FollowingHost)
            {
                ReportOnce("nurse input", "Menus: a Nurse procedure screen here takes no input of its own while following (the host's presses are replayed through its handlers).");
                return false;
            }
        }
        catch (Exception ex)
        {
            Failed("Nurse input block", ex);
            return !FollowingHost;
        }

        return true;
    }

    /// <summary>Friend, while following: a procedure screen never opens a lineup of its own (the host's lineup comes as its own view).</summary>
    private static bool NurseOpenLineupPrefix()
    {
        try
        {
            if (FollowingHost)
            {
                ReportOnce("nurse lineup", "Menus: a Nurse procedure screen's own lineup was skipped on this PC (the host's lineup shows as its own view).");
                return false;
            }
        }
        catch (Exception ex)
        {
            Failed("Nurse lineup block", ex);
            return !FollowingHost;
        }

        return true;
    }

    /// <summary>
    /// Friend, while following: a procedure screen's commit never runs here (it would change this PC's copy of the run,
    /// count an injury on this player's stats and raise reward events). The host's outcome goes into its state first, with
    /// the state's own setters, so its result shows what the host's commit did (<see cref="FriendMenus.NurseCommitting"/>).
    /// </summary>
    private static bool NurseCommitPrefix(NurseProcedureScreen __instance)
    {
        try
        {
            if (!FollowingHost)
            {
                return true;
            }

            // Every confirm comes through here; only at its result would the game commit (verified in the game's code).
            if (NurseFollow.AtResult(__instance))
            {
                MenuViews.Instance?.Friend.NurseCommitting(__instance);
                ReportOnce("nurse commit", "Menus: a Nurse procedure's commit was skipped on this PC (the host's outcome is shown; the run changes with the host's next checkpoint).");
            }

            return false;
        }
        catch (Exception ex)
        {
            Failed("Nurse commit block", ex);
            return !FollowingHost;
        }
    }

    /// <summary>
    /// Friend, while following: a build of a procedure screen at its result. What the game's popups gate on the commit skipped here
    /// runs now, the host's having gone through (<see cref="FriendMenus.NurseBuilt"/>).
    /// </summary>
    private static void NurseBuiltPostfix(NurseProcedureScreen __instance)
    {
        try
        {
            if (FollowingHost && NurseFollow.AtResult(__instance))
            {
                MenuViews.Instance?.Friend.NurseBuilt(__instance);
            }
        }
        catch (Exception ex)
        {
            Failed("Nurse result", ex);
        }
    }

    /// <summary>
    /// Friend, while following: a procedure screen's panel places its pending talent's suture, which the game does only uncommitted. At
    /// a result the host's commit made, the game's committed branch runs instead: the added talent's flash
    /// (<see cref="FriendMenus.NurseFlashesAddedTalent"/>).
    /// </summary>
    private static bool NurseSuturePrefix(NurseProcedureScreen __instance, NursePanelView view)
    {
        try
        {
            if (FollowingHost && NurseFollow.AtResult(__instance) && MenuViews.Instance?.Friend.NurseFlashesAddedTalent(__instance, view) == true)
            {
                return false;
            }
        }
        catch (Exception ex)
        {
            Failed("Nurse suture", ex);
        }

        return true;
    }

    /// <summary>Host: the procedure screen's confirm press (noted as a step when its gate lets it through).</summary>
    private static void NurseConfirmPrefix(NurseProcedureScreen __instance) => NursePressed(__instance, NurseStepKind.Confirm, 0, direction: false);

    /// <summary>Host: the procedure screen's back press.</summary>
    private static void NurseBackPrefix(NurseProcedureScreen __instance) => NursePressed(__instance, NurseStepKind.Back, 0, direction: false);

    /// <summary>Host: a direction press on the procedure screen.</summary>
    private static void NurseDirectionPrefix(NurseProcedureScreen __instance, AbstractNavigable.EDirection direction) =>
        NursePressed(__instance, NurseStepKind.Direction, (int)direction, direction: true);

    private static void NursePressed(NurseProcedureScreen screen, NurseStepKind kind, int arg, bool direction)
    {
        try
        {
            if (Views is { Hosting: true })
            {
                NurseHost.Pressed(screen, kind, arg, direction);
            }
        }
        catch (Exception ex)
        {
            Failed("Nurse press", ex);
        }
    }

    /// <summary>Host: a named action is about to reach the procedure screen (what it shows now).</summary>
    private static void NurseActionPrefix(NurseProcedureScreen __instance)
    {
        try
        {
            if (Views is { Hosting: true })
            {
                NurseHost.BeforeAction(__instance);
            }
        }
        catch (Exception ex)
        {
            Failed("Nurse action", ex);
        }
    }

    /// <summary>Host: a named action's press reached the procedure screen (a step when it changed what it shows).</summary>
    private static void NurseActionPressedPostfix(NurseProcedureScreen __instance, string action) => NurseAction(__instance, action, pressed: true);

    /// <summary>Host: a named action's release reached the procedure screen.</summary>
    private static void NurseActionReleasedPostfix(NurseProcedureScreen __instance, string action) => NurseAction(__instance, action, pressed: false);

    private static void NurseAction(NurseProcedureScreen screen, string action, bool pressed)
    {
        try
        {
            if (Views is { Hosting: true })
            {
                NurseHost.AfterAction(screen, action, pressed);
            }
        }
        catch (Exception ex)
        {
            Failed("Nurse action", ex);
        }
    }

    /// <summary>Host: the procedure screen's commit check ran; once committed, its outcome goes to friends.</summary>
    private static void NurseCommitPostfix(NurseProcedureScreen __instance)
    {
        try
        {
            if (Views is { Hosting: true } views)
            {
                NurseHost.Committed(views.Host, __instance, views.HasFriends);
            }
        }
        catch (Exception ex)
        {
            Failed("Nurse commit", ex);
        }
    }
}
#endif
