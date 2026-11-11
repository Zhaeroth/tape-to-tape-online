#if HAS_INTEROP
using System.Collections.Generic;
using T2TOnline.Plugin.Campaign.Menus;

namespace T2TOnline.Plugin.Campaign;

/// <summary>Registers the menu views (Campaign/Menus/) and the run's toasts (v1), and their patches.</summary>
internal static partial class CampaignModules
{
    static partial void AddMenus(List<ICampaignModule> modules, CampaignContext context)
    {
        modules.Add(new MenuViews(context));
        modules.Add(new Toasts());
    }

    static partial void InstallMenus(HarmonyLib.Harmony harmony, List<string> failed)
    {
        MenuPatches.Install(harmony, failed);
        Toasts.Install(harmony, failed);
    }
}
#endif
