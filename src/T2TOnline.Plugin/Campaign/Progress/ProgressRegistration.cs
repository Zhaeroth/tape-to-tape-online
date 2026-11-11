#if HAS_INTEROP
using System.Collections.Generic;
using T2TOnline.Plugin.Campaign.Progress;

namespace T2TOnline.Plugin.Campaign;

/// <summary>Registers the progress module and installs its patches.</summary>
internal static partial class CampaignModules
{
    static partial void AddProgress(List<ICampaignModule> modules, CampaignContext context) => modules.Add(new ProgressModule(context));

    static partial void InstallProgress(HarmonyLib.Harmony harmony, List<string> failed)
    {
        ProgressPatches.Install(harmony, failed);
        HistoryLogos.Install(harmony, failed);
    }
}
#endif
