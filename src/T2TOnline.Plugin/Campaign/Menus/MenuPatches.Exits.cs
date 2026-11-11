#if HAS_INTEROP
using System;
using System.Collections.Generic;
using Il2CppBishopGames.UI;

namespace T2TOnline.Plugin.Campaign.Menus;

/// <summary>
/// The menu exits' patches: on the host, a prefix and a postfix on
/// <c>NavigationController.OnBackInput</c> (already a patch target: the friend's lock refuses it there, FriendUi) bracket
/// the host's back input, so a menu that closes inside it closes "backed out of" (<see cref="HostTransitions"/>) and a
/// friend's copy plays the game's back sound; a back the host's game played its sound for without a close (a step back,
/// a prompt opening) goes to friends as a value on the newest menu. The rest of the exits' work rides on the existing show
/// and close patches.
/// </summary>
internal static partial class MenuPatches
{
    /// <summary>The exits' patches.</summary>
    private static void InstallExits(HarmonyLib.Harmony harmony, List<string> failed) =>
        Patch(harmony, failed, typeof(NavigationController), nameof(NavigationController.OnBackInput), prefix: nameof(HostBackPrefix), postfix: nameof(HostBackPostfix));

    /// <summary>The host's back input is about to go to the menus' back handlers.</summary>
    private static void HostBackPrefix(NavigationController __instance)
    {
        try
        {
            if (Views is { Hosting: true })
            {
                HostTransitions.BackStarted(__instance);
            }
        }
        catch (Exception ex)
        {
            Failed("back input", ex);
        }
    }

    /// <summary>The host's back input went to the menus' back handlers: its back sound, when no shared menu closed with it.</summary>
    private static void HostBackPostfix()
    {
        try
        {
            if (HostTransitions.BackEnded() && Views is { Hosting: true } views && views.HasFriends)
            {
                views.Host.BackSound();
            }
        }
        catch (Exception ex)
        {
            Failed("back input", ex);
        }
    }
}
#endif
