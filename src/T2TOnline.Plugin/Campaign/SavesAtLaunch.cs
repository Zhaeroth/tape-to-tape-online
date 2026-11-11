#if HAS_INTEROP
using System;
using System.Collections.Generic;
using T2TOnline.Plugin.Diagnostics;

namespace T2TOnline.Plugin.Campaign;

/// <summary>
/// The saves' start-up steps, on every launch whatever online's state (D46): co-op slots from before 0.6.1 move out of the
/// game folder (<see cref="CoopSlots.MoveFromGameFolder"/>), a host's run left on this PC by a game that stopped during a session
/// goes (<see cref="RunSync.RemoveSessionLeftovers"/>), and a restore the game stopped in the middle of is put right
/// (<see cref="SaveBackups.StartUpAtLaunch"/>). Run once from the plugin's <c>OnInitializeMelon</c>, after the start-up
/// checks and before the rest of the mod starts: still before the game's first scene, so before the game reads a save.
/// <para>
/// On a game build the mod may patch, the guard on the game's loads from Steam Cloud goes in first
/// (<see cref="RunSync.InstallCloudLoadGuard"/>; the same patch online play uses): the game's load of a cloud-saved file
/// writes Steam's copy over the local one, so without it a finished restore would be undone at the game's first load of each
/// file. On a build it can't patch, the recovery does only what stays (<see cref="SaveBackups.StartUpAtLaunch"/>). Nothing
/// here needs a session, Steam networking or the lobby. Never throws.
/// </para>
/// </summary>
internal static class SavesAtLaunch
{
    private static bool _ran;

    /// <summary>
    /// Runs the saves' start-up steps once. <paramref name="gamePatchable"/>: the game build is a tested one and every hook
    /// resolved, so the Steam Cloud load guard and the save guard may go in.
    /// </summary>
    public static void Run(HarmonyLib.Harmony harmony, bool gamePatchable)
    {
        if (_ran)
        {
            return;
        }

        _ran = true;
        try
        {
            CoopSlots.MoveFromGameFolder();
            RunSync.RemoveSessionLeftovers();
            bool cloudLoadsHeld = false;
            if (gamePatchable)
            {
                var failed = new List<string>();
                cloudLoadsHeld = RunSync.InstallCloudLoadGuard(harmony, failed);
                if (!cloudLoadsHeld)
                {
                    ModLog.Warn($"Backups: the guard on the game's loads from Steam Cloud couldn't go in ({string.Join("; ", failed)}); a restore left unfinished is " +
                        "finished now only where Steam Cloud can't undo it.");
                }
            }

            SaveBackups.StartUpAtLaunch(saveGuardUsable: gamePatchable, cloudLoadsHeld: cloudLoadsHeld);
        }
        catch (Exception ex)
        {
            ModLog.Error("Backups: the saves' start-up steps failed; a restore left unfinished is looked for again when the backups are next read (Save Backups, or the next online game)", ex);
        }
    }
}
#endif
