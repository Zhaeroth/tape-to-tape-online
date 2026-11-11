#if HAS_INTEROP
using System;
using Il2CppAI.Brawl;
using Il2CppFighting;
using Il2CppState;
using MelonLoader;
using T2TOnline.Core.Hosting;
using T2TOnline.Plugin.Campaign.Scenes;
using T2TOnline.Plugin.Diagnostics;
using T2TOnline.Plugin.Streaming;

namespace T2TOnline.Plugin.Match;

/// <summary>
/// Ends the game's fight state a match left on, when the match's scene unloads (Core's <see cref="BrawlLeftover"/>). The game
/// clears its fight matchmaker and its brawl HUD's flag only in a fight's own end sequence, so a match conceded or quit during
/// a fight (the pause menu opens in a fight) leaves both on, and while the matchmaker is on no player takes a skater in the
/// matches after; on a friend's PC the brawl HUD the stream started stays up when their match goes first. At every unload of
/// the match scene, on every PC and in every kind of match, while online is on: the end sequence's own calls for what is on,
/// in its order (the matchmaker, then the HUD), each tried on its own, then both flags read back and one line. Nothing per
/// frame, no patch: one MelonLoader scene event. Both calls touch no object of the gone scene that they don't test first.
/// </summary>
internal static class BrawlLeftoverGuard
{
    private static bool _installed;

    /// <summary>Times the guard ended something (for the session's brawl lines and the check tour's <c>brawlend=game</c>).</summary>
    internal static int Acts { get; private set; }

    /// <summary>True when the last time it acted, both flags were off afterwards and no call failed.</summary>
    internal static bool LastClean { get; private set; }

    /// <summary>Subscribes to the match scene's unload (once). Never throws.</summary>
    public static void Install()
    {
        if (_installed)
        {
            return;
        }

        try
        {
            MelonEvents.OnSceneWasUnloaded.Subscribe(OnSceneUnloaded);
            _installed = true;
            ModLog.Info("Brawl guard: ready (when a match's scene unloads, the game's fight state a match left on is ended by the game's own calls).");
        }
        catch (Exception ex)
        {
            ModLog.Warn($"Brawl guard: couldn't be installed ({ModLog.SafeErrorText(ex)}); a match left during a fight can leave the next match's players without skaters, as in the game without the mod.");
        }
    }

    private static void OnSceneUnloaded(int buildIndex, string name)
    {
        // Online off (a game build the mod doesn't support, the mod stopped after an error): the game is left as it is.
        if (name != WorldMap.MatchScene || !T2TOnlinePlugin.OnlineAvailable)
        {
            return;
        }

        try
        {
            EndLeftover();
        }
        catch (Exception ex)
        {
            ModLog.Warn($"Brawl guard: the match's unload couldn't be handled ({ModLog.SafeErrorText(ex)}).");
        }
    }

    private static void EndLeftover()
    {
        bool matchmakerOn = BrawlMatchmakerSystem.IsActive;
        bool hudUp = FightHpBarSystem.IsBrawlActive;
        if (!matchmakerOn && !hudUp)
        {
            return;
        }

        // A live match fighting (none once its scene is gone: HudModel tests its GameManager by Unity's null test), or an
        // accepted brawl about to begin (the match's teardown cancels it): that fight's own state.
        bool fighting = HudModel.CurrentGameState() == (int)GameState.Fight;
        bool pending = BrawlChallengeSystem.HasPendingAcceptedStart;
        if (BrawlLeftover.LeftToLiveFight(matchmakerOn, hudUp, fighting, pending))
        {
            ModLog.Info(BrawlLeftover.LeftAloneLine(matchmakerOn, hudUp));
            return;
        }

        BrawlLeftoverPlan plan = BrawlLeftover.Plan(matchmakerOn, hudUp, fighting, pending);
        string? problem = null;
        if (plan.EndMatchmaker)
        {
            try
            {
                BrawlMatchmakerSystem.EndBrawl();
            }
            catch (Exception ex)
            {
                problem ??= $"the matchmaker's end threw ({ModLog.SafeErrorText(ex)})";
            }
        }

        if (plan.EndHud)
        {
            try
            {
                FightHpBarSystem.EndBrawl();
            }
            catch (Exception ex)
            {
                problem ??= $"the brawl HUD's end threw ({ModLog.SafeErrorText(ex)})";
            }
        }

        BrawlLeftoverReport report = BrawlLeftover.Report(plan, Read(() => BrawlMatchmakerSystem.IsActive, "the matchmaker", ref problem),
            Read(() => FightHpBarSystem.IsBrawlActive, "the brawl HUD's flag", ref problem), problem);
        Acts++;
        LastClean = report.Clean;
        if (report.Clean)
        {
            ModLog.Info(report.Line);
        }
        else
        {
            ModLog.Warn(report.Line);
        }
    }

    /// <summary>A flag read back after the calls; one that can't be read counts as still on, and says so.</summary>
    private static bool Read(Func<bool> flag, string what, ref string? problem)
    {
        try
        {
            return flag();
        }
        catch (Exception ex)
        {
            problem ??= $"{what} couldn't be read back ({ModLog.SafeErrorText(ex)})";
            return true;
        }
    }
}
#endif
