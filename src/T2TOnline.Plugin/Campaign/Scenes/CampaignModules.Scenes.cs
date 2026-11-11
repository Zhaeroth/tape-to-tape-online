#if HAS_INTEROP
using System.Collections.Generic;
using Il2CppState;
using T2TOnline.Plugin.Campaign.Scenes;

namespace T2TOnline.Plugin.Campaign;

/// <summary>Registers the scenes module and its patches.</summary>
internal static partial class CampaignModules
{
    static partial void AddScenes(List<ICampaignModule> modules, CampaignContext context) => modules.Add(new SceneFollowing(context));

    static partial void InstallScenes(HarmonyLib.Harmony harmony, List<string> failed)
    {
        FriendUi.Install(harmony, failed);
        MapGuards.Install(harmony, failed);
        PreviewMirror.Install(harmony, failed);
        GameTips.Install(harmony, failed);
        ActIntro.Install(harmony, failed);
        MapReveal.Install(harmony, failed);
        NodeEntryMirror.Install(harmony, failed);
        TitleCursor.Install(harmony, failed);
        ScenePatches.Postfix(harmony, failed, typeof(CampaignState), nameof(CampaignState.SaveRunData), typeof(SceneFollowing), nameof(SceneFollowing.SaveRunDataPostfix));
    }
}
#endif
