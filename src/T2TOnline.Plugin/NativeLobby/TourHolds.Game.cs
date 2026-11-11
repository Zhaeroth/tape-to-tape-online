#if HAS_INTEROP
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using T2TOnline.Core.Lobby;
using T2TOnline.Plugin.Campaign;
using T2TOnline.Plugin.Client;
using T2TOnline.Plugin.Diagnostics;
using T2TOnline.Plugin.NativeLobby.Widgets;
using T2TOnline.Plugin.Online;
using T2TOnline.Plugin.Startup;

namespace T2TOnline.Plugin.NativeLobby;

// Taking and letting go of the holds, with the game's parts they need: the save guard, the achievements patch, run sync.
internal static partial class TourHolds
{
    private static string _folder = string.Empty;

    /// <summary>
    /// True while a prompt of the mod's own is on its way or up (a lobby question or notice, a held Steam invite's question):
    /// a game routine that answers any prompt with yes (the campaign walker) waits meanwhile.
    /// </summary>
    public static bool ModPromptOpen => LobbyPrompts.IsOpen || LobbyQuestion.Current != null;

    /// <summary>The folder of the tour that holds the player's data now (where its own output goes: a problem report it makes too), or null.</summary>
    public static string? Folder => On && _folder.Length > 0 ? _folder : null;

    /// <summary>
    /// A tour writing to <paramref name="folder"/> begins: the save guard holds every game save, then the switch goes on.
    /// False, with why and nothing held, when the guard can't hold or the achievements patch isn't in (an unlock would reach
    /// Steam): then the tour doesn't run. Call before the tour's first step.
    /// </summary>
    public static bool Hold(string folder, out string problem)
    {
        if (!SaveGuard.HoldForCheck(true, out problem))
        {
            return false;
        }

        if (!AchievementMirror.Installed)
        {
            SaveGuard.HoldForCheck(false, out _);
            problem = "the achievements patch isn't in, so an unlock would reach Steam";
            return false;
        }

        _folder = folder ?? string.Empty;
        Begin();
        ModLog.Info("Lobby check: holds on: game saves refused but the tour's own run file, achievements held back, allowances refused, " +
            "settings held in memory, no backups or restores, the title's own prompts held.");
        return true;
    }

    /// <summary>
    /// After the tour's last restore, when the game stays open: the switch off, the run file gone, the settings held in memory
    /// back to their saved values (run sync told when the co-op slot was one), then the save guard let go. A run file the
    /// game still plays (a campaign tour stopped away from the title screen) stays the co-op run file until run sync puts the
    /// solo run back (<see cref="SoloRunBack"/>): the run in memory is the tour's, and its saves must never reach a co-op slot.
    /// Never throws.
    /// </summary>
    public static void Release()
    {
        try
        {
            bool stillPlayed = RunFile != null && RunSync.Instance is { RunFileSlot: > 0 };
            string? runFile = End(keepRunFile: stillPlayed);
            IReadOnlyList<string> settings = ModSettings.DropHeld();
            if (settings.Count > 0)
            {
                ModLog.Info($"Settings: the check tour is over: {string.Join(", ", settings)} go back to their saved values.");
                if (settings.Contains(nameof(ModSettings.CoopSlot)))
                {
                    RunSync.Instance?.CoopSlotChanged();
                }
            }

            if (stillPlayed)
            {
                ModLog.Info("Run sync: the game still plays the check tour's run, in the tour's own run file, until your solo run is back at the title screen; " +
                    "the co-op slots are the run files again from then.");
            }
            else if (runFile != null)
            {
                ModLog.Info("Run sync: the check tour's own run file is no longer the co-op run file; the co-op slots are again.");
            }
        }
        catch (Exception ex)
        {
            ModLog.Warn($"Lobby check: letting go of the holds failed in part ({ModLog.SafeErrorText(ex)}).");
        }
        finally
        {
            SaveGuard.HoldForCheck(false, out _);
        }
    }

    /// <summary>
    /// A campaign tour's own run file (<c>tour-run.es3</c> in the tour's folder) becomes the co-op run file in place of every
    /// co-op slot, so the run it plays reaches the disk (run sync's checkpoints, Continue) and the player's slots are never
    /// touched; the save guard lets it through, and nothing else. Only while a tour holds and the game's run file is the solo
    /// run (call it before hosting: a co-op slot the game already points at would stay its run file until the title screen). False, with why, when it can't; logged either way. Pruned with its tour.
    /// </summary>
    public static bool UseTourRunFile(out string problem)
    {
        problem = !_on ? "no check tour holds"
            : _folder.Length == 0 ? "the tour has no folder"
            : RunSync.Instance is not { } sync ? "run sync isn't running"
            : sync.RunFileSlot != 0 ? "the game's run file isn't the solo run now"
            : string.Empty;
        if (problem.Length > 0)
        {
            ModLog.Warn($"Run sync: the check tour's own run file refused: {problem}.");
            return false;
        }

        try
        {
            string path = Path.GetFullPath(TourWrites.RunFileIn(_folder));
            System.Threading.Volatile.Write(ref _runFile, path);
            ModLog.Info($"Run sync: the check tour's own run file is the co-op run file now ({DevOutput.Label(path)}); the co-op slots are left alone.");
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or System.Security.SecurityException)
        {
            problem = $"its path couldn't be made ({ex.GetType().Name})";
            ModLog.Warn($"Run sync: the check tour's own run file refused: {problem}.");
            return false;
        }
    }
}
#endif
