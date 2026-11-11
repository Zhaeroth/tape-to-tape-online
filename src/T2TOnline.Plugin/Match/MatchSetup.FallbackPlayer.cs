#if HAS_INTEROP
using System;
using System.Reflection;
using HarmonyLib;
using Il2CppScriptableStates;
using Il2CppState.GameModeControllers;
using T2TOnline.Plugin.Campaign;
using T2TOnline.Plugin.Diagnostics;

namespace T2TOnline.Plugin.Match;

/// <summary>
/// The game's fallback player for a campaign match (Nov 6 build): <c>GameControllerBase.InitializePlayers</c> calls
/// <c>AssignFallbackCampaignPlayer</c>, which, in a campaign match (game type 1, not the tutorial) whose home team has no
/// players, takes this PC's first gamepad player (else its keyboard player), unlocks its position and adds it to the home
/// team. A host's match on a friend's PC has no players on purpose (<see cref="ApplyRules"/>: the host's players play
/// through their pads on the host), so there the fallback is skipped (prefix): while a host's match loads or plays here,
/// or while this PC holds the host's run. Anywhere else (the host, a player's own match) the game's own runs. The prefix's
/// target runs: <c>InitializePlayers</c> is its only caller (<c>GameControllerBase.Initialize</c> and
/// <c>NormalMatchGameModeController.OnAnthemOver</c> call that). Logged once, then each skip at trace. Nothing here throws
/// into the game.
/// </summary>
internal static partial class MatchSetup
{
    private static int _fallbackSkips;

    /// <summary>Patches the fallback (<see cref="FallbackPlayerPrefix"/>). A failure is logged; match setup stays on without it.</summary>
    private static void InstallFallbackPlayer(HarmonyLib.Harmony harmony)
    {
        try
        {
            MethodInfo fallback = AccessTools.Method(typeof(GameControllerBase), nameof(GameControllerBase.AssignFallbackCampaignPlayer))
                ?? throw new MissingMethodException(nameof(GameControllerBase), nameof(GameControllerBase.AssignFallbackCampaignPlayer));
            harmony.Patch(fallback, prefix: new HarmonyMethod(typeof(MatchSetup), nameof(FallbackPlayerPrefix)));
        }
        catch (Exception ex)
        {
            ModLog.Warn($"Match setup: couldn't patch the game's fallback player for a campaign match ({ModLog.SafeErrorText(ex)}); in a host's campaign match " +
                "on this PC the game may put this PC's first controller's player on the home team.");
        }
    }

    /// <summary>
    /// Skips the game's fallback player while this PC loads or plays a host's match, its log line failing or not. Never
    /// throws into the game.
    /// </summary>
    private static bool FallbackPlayerPrefix(GameControllerBase __instance)
    {
        bool hostsMatch = false;
        try
        {
            hostsMatch = _forced != null || HostMatchHere || RunSync.Instance is { HoldingHostRun: true };
            if (!hostsMatch)
            {
                return true;
            }

            // What the game would have done here, for the log: its own test (a campaign match, not the tutorial, no home players).
            TeamSelection? teams = __instance.teamSelectionState;
            int homePlayers = teams?.homeTeamPlayers?.Count ?? -1;
            bool wouldAdd = teams != null && (int)teams.gameType == GameTypeCampaign && !teams.onboard && homePlayers == 0;
            if (_fallbackSkips++ == 0)
            {
                ModLog.Info($"Match setup: the game's fallback player for a campaign match with no players was skipped: nobody plays on this PC in a host's match " +
                    $"(home team players here: {(homePlayers < 0 ? "unknown" : homePlayers.ToString(System.Globalization.CultureInfo.InvariantCulture))}; " +
                    $"{(wouldAdd ? "the game would have put this PC's first controller's player on the home team" : "the game wouldn't have added anyone to this match")}). " +
                    "Further ones are logged at trace.");
            }
            else
            {
                ModLog.Trace($"Match setup: the game's fallback player skipped again ({_fallbackSkips} so far; home team players here: {homePlayers}; would have added one: {wouldAdd}).");
            }

            return false;
        }
        catch (Exception ex)
        {
            // Once it's known to be a host's match, only the log line can have failed: the fallback stays skipped.
            ModLog.Error($"Match setup: deciding on the game's fallback player failed; {(hostsMatch ? "skipped all the same (a host's match)" : "the game's own runs")}", ex);
            return !hostsMatch;
        }
    }
}
#endif
