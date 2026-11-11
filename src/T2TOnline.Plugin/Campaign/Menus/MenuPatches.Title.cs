#if HAS_INTEROP
using System;
using System.Collections.Generic;
using Il2CppTape2Tape.Hockey.UI;
using Il2CppUnlocks;
using Il2CppList = Il2CppSystem.Collections.Generic;

namespace T2TOnline.Plugin.Campaign.Menus;

/// <summary>
/// The patches of the title's menus shown for real on a friend (Hooks/hooks.txt, "the
/// title's screens"): all on unfolded, non-generic methods without small by-value structs.
/// <list type="bullet">
/// <item>The unlockables (<see cref="UnlockablesView"/>): each row as the menu converts it (<c>ConvertUnlockData</c>: the host
/// reads it, a friend's takes the host's states), the quest rows and tabs as it makes them (<c>InitTabs</c>: a friend's
/// quest rows before, the host's attachment after), the host's tab and highlighted row; a friend's binding
/// (<c>InitData</c>).</item>
/// <item>The run history (<see cref="RunHistoryPages"/>): each page the host's history mode shows.</item>
/// </list>
/// The unlockables show for real only when every friend-side patch went in.
/// </summary>
internal static partial class MenuPatches
{
    /// <summary>True when every patch the real unlockables need on a friend went in (otherwise a note says so).</summary>
    public static bool UnlockablesReady { get; private set; }

    /// <summary>The title's menus' patches; <paramref name="shows"/> and <paramref name="closes"/>: the menus' own open and close patches went in.</summary>
    private static void InstallTitleScreens(HarmonyLib.Harmony harmony, List<string> failed, bool shows, bool closes)
    {
        bool unlockables = Patch(harmony, failed, typeof(UnlockableMenu), "InitData", postfix: nameof(InitDataPostfix));
        unlockables &= Patch(harmony, failed, typeof(UnlockableMenu), "ConvertUnlockData", postfix: nameof(UnlockConvertPostfix));
        unlockables &= Patch(harmony, failed, typeof(UnlockableMenu), "InitTabs", prefix: nameof(UnlockTabsPrefix), postfix: nameof(UnlockTabsPostfix));
        UnlockablesReady = unlockables && shows && closes;
        Patch(harmony, failed, typeof(UnlockableMenu), "RefreshTabContent", postfix: nameof(UnlockTabPostfix));
        Patch(harmony, failed, typeof(UnlockableMenu), "OnRowHighlighted", postfix: nameof(UnlockRowPostfix));
        Patch(harmony, failed, typeof(RunEndMenu), "ShowHistoryEntry", postfix: nameof(HistoryPagePostfix));
    }

    /// <summary>
    /// An unlockables row was just converted from its asset and the profile: the host reads its state; a friend's menu that
    /// shows the host's takes the host's state in place of this profile's.
    /// </summary>
    private static void UnlockConvertPostfix(UnlockableMenu __instance, IUnlockData unlock, UnlockData __result)
    {
        try
        {
            if (__result == null || unlock == null)
            {
                return;
            }

            if (Views is { Hosting: true })
            {
                UnlockablesView.HostConverted(__instance, unlock, __result);
            }
            else if (UnlockablesView.Armed)
            {
                UnlockablesView.FriendConverted(__instance, unlock, __result);
            }
        }
        catch (Exception ex)
        {
            Failed("unlockables row", ex);
        }
    }

    /// <summary>A friend's unlockables menu is about to make its tabs: its quest rows become the host's first (the tabs count them).</summary>
    private static void UnlockTabsPrefix(UnlockableMenu __instance, Il2CppList.List<TabData> tabs)
    {
        try
        {
            if (UnlockablesView.Armed)
            {
                UnlockablesView.FriendTabs(__instance, tabs);
            }
        }
        catch (Exception ex)
        {
            Failed("unlockables quests", ex);
        }
    }

    /// <summary>The host's unlockables menu made its tabs: its states go to friends.</summary>
    private static void UnlockTabsPostfix(UnlockableMenu __instance, Il2CppList.List<TabData> tabs)
    {
        try
        {
            if (Views is { Hosting: true } views)
            {
                UnlockablesView.HostTabs(__instance, tabs, views.Host, views.HasFriends);
            }
        }
        catch (Exception ex)
        {
            Failed("unlockables states", ex);
        }
    }

    /// <summary>The host's unlockables menu opened a tab: it goes to friends.</summary>
    private static void UnlockTabPostfix(UnlockableMenu __instance)
    {
        try
        {
            if (Views is { Hosting: true } views && views.HasFriends)
            {
                UnlockablesView.HostTab(__instance, views.Host);
            }
        }
        catch (Exception ex)
        {
            Failed("unlockables tab", ex);
        }
    }

    /// <summary>The host's unlockables menu highlighted a row: it goes to friends.</summary>
    private static void UnlockRowPostfix(UnlockableMenu __instance)
    {
        try
        {
            if (Views is { Hosting: true } views && views.HasFriends)
            {
                UnlockablesView.HostRow(__instance, views.Host);
            }
        }
        catch (Exception ex)
        {
            Failed("unlockables row highlight", ex);
        }
    }

    /// <summary>The host's run-end menu showed a run history page: it goes to friends.</summary>
    private static void HistoryPagePostfix(RunEndMenu __instance)
    {
        try
        {
            if (Views is { Hosting: true } views && __instance.m_IsHistoryMode)
            {
                RunHistoryPages.HostShown(views.Host, __instance, views.HasFriends);
            }
        }
        catch (Exception ex)
        {
            Failed("run history page", ex);
        }
    }
}
#endif
