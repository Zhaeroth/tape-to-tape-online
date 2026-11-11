#if HAS_INTEROP
using System;
using System.Collections.Generic;
using Il2CppTape2Tape.Hockey.UI;
using T2TOnline.Plugin.Diagnostics;

namespace T2TOnline.Plugin.Campaign.Menus;

/// <summary>
/// The brawl menu's patch (v1, <see cref="BrawlViews"/>; Hooks/hooks.txt, "the brawl offer"): the
/// host's fight controls showing (<c>BrawlFightMenu.ShowControls</c>: unique, non-generic, no arguments; called by
/// the menu's <c>Show</c> and its game-state listener) sends the menu's data again at once, so friends' copies switch to the
/// fight controls with the host's (the menu's data is otherwise read again only every 0.4 to 1.6 s).
/// </summary>
internal static partial class MenuPatches
{
    private static bool _brawlLogged;

    /// <summary>The brawl menu's patch (host).</summary>
    private static void InstallBrawl(HarmonyLib.Harmony harmony, List<string> failed) =>
        Patch(harmony, failed, typeof(BrawlFightMenu), "ShowControls", postfix: nameof(BrawlControlsPostfix));

    /// <summary>The host's brawl menu shows its fight controls: its data goes out again now.</summary>
    private static void BrawlControlsPostfix(BrawlFightMenu __instance)
    {
        try
        {
            if (Views is { Hosting: true } views && views.HasFriends)
            {
                if (!_brawlLogged)
                {
                    _brawlLogged = true;
                    ModLog.Info("Menus: the host's brawl started; friends' copies of the brawl menu show the fight controls (logged once).");
                }

                views.Host.Changed(__instance, friends: true);
            }
        }
        catch (Exception ex)
        {
            Failed("brawl controls", ex);
        }
    }
}
#endif
