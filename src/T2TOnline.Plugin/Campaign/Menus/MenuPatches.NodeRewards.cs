#if HAS_INTEROP
using System;
using System.Collections.Generic;
using Il2CppRogue.NPC.GeneralManagerNPC;
using T2TOnline.Plugin.Campaign.Scenes;
using T2TOnline.Plugin.Diagnostics;

namespace T2TOnline.Plugin.Campaign.Menus;

/// <summary>
/// The General Manager's rerolled map node rewards (v1; Hooks/hooks.txt, "the speech mirror ...";
/// <see cref="NodeRewardsMirror"/>): a prefix and a postfix on <c>GeneralManagerEvent.RerollSkaterNodeRewards</c> (unfolded,
/// non-generic, no arguments) read the map's rewards before and after the host's game rerolls them, while the campaign is
/// shared. Reads only; on a friend the reroll never runs (its <c>NextChoices</c>, the only caller, is refused).
/// <para>
/// The reroll is a static method, so Harmony gives its patches no instance (<c>__instance</c> would be null): the General
/// Manager whose page rolls is the one whose <c>NextChoices</c> runs it (its only caller, verified in the game's code), noted
/// by that method's prefix (<see cref="EventNextChoicesPrefix"/>).
/// </para>
/// </summary>
internal static partial class MenuPatches
{
    // The General Manager event whose NextChoices runs now on the host (the reroll's only caller), or null.
    private static GeneralManagerEvent? _rolling;
    private static bool _rollingMissingSaid;

    /// <summary>True when the host's rerolled node rewards can go to friends (both patches went in).</summary>
    public static bool NodeRewardsReady { get; private set; }

    /// <summary>The node rewards' patches.</summary>
    private static void InstallNodeRewards(HarmonyLib.Harmony harmony, List<string> failed) =>
        NodeRewardsReady = Patch(harmony, failed, typeof(GeneralManagerEvent), "RerollSkaterNodeRewards", prefix: nameof(NodeRewardsPrefix), postfix: nameof(NodeRewardsPostfix));

    /// <summary>The host's game is about to reroll the map's node rewards: what they are now.</summary>
    private static void NodeRewardsPrefix()
    {
        try
        {
            if (Views is { Hosting: true } && RunSync.Instance?.HostOnCoopSlot == true)
            {
                NodeRewardsMirror.HostBefore();
            }
        }
        catch (Exception ex)
        {
            Failed("node rewards before", ex);
        }
    }

    /// <summary>The host's game rerolled the map's node rewards: the changed ones go to friends.</summary>
    private static void NodeRewardsPostfix()
    {
        try
        {
            if (Views is { Hosting: true } views && RunSync.Instance?.HostOnCoopSlot == true)
            {
                if (_rolling == null)
                {
                    // NextChoices' patch didn't go in: friends' maps take the rewards at the host's next move (the stale mark).
                    if (!_rollingMissingSaid)
                    {
                        _rollingMissingSaid = true;
                        ModLog.Warn("Menus: the host's General Manager rerolled the map's node rewards, but its event isn't known here; friends' maps load them again at the host's next move.");
                    }

                    return;
                }

                NodeRewardsMirror.HostAfter(_rolling, views);
            }
        }
        catch (Exception ex)
        {
            Failed("node rewards", ex);
        }
    }
}
#endif
