#if HAS_INTEROP
using System;
using System.Collections.Generic;
using Il2CppRewired;
using T2TOnline.Core.Input;
using T2TOnline.Plugin.Diagnostics;
using T2TOnline.Plugin.Input;

namespace T2TOnline.Plugin.Campaign.Scenes;

/// <summary>
/// The lock's one check a session (log only, nothing changes): a pad friend's keyboard stays on its game player, and the
/// lock switches off only its UI maps, so a keyboard or mouse map left on that binds one of the map camera's pan actions
/// (<see cref="MapPanCheck.PanActions"/>) would pan this PC's map until the host's camera moves. Which category binds them
/// is only in Rewired's data at run time: the line says.
/// </summary>
internal static partial class FriendUi
{
    // Checked once a lock, at its first sweep with Rewired up.
    private static bool _panChecked;

    /// <summary>After the sweep: the keyboard and mouse maps still on, and whether any binds a pan action (once a lock). Never throws.</summary>
    private static void CheckPanMaps()
    {
        if (_panChecked)
        {
            return;
        }

        try
        {
            if (!ReInput.isReady)
            {
                return; // the next sweep checks
            }

            _panChecked = true;
            var stillOn = new List<(string Category, bool BindsPan)>();
            Il2CppSystem.Collections.Generic.List<ControllerMap> maps = _maps ??= new Il2CppSystem.Collections.Generic.List<ControllerMap>();
            for (int id = 0; id < ReInput.players.playerCount; id++)
            {
                Player? player = ReInput.players.GetPlayer(id);
                if (player == null)
                {
                    continue;
                }

                foreach (ControllerType type in LockedDevices)
                {
                    maps.Clear();
                    player.controllers.maps.GetAllMaps(type, maps);
                    for (int i = 0; i < maps.Count; i++)
                    {
                        ControllerMap map = maps[i];
                        if (map == null || !map.enabled)
                        {
                            continue;
                        }

                        bool binds = false;
                        foreach (int action in MapPanCheck.PanActions)
                        {
                            binds |= map.ContainsAction(action);
                        }

                        stillOn.Add(($"{RewiredBindings.CategoryName(map.categoryId)} ({(type == ControllerType.Keyboard ? "keyboard" : "mouse")})", binds));
                    }
                }
            }

            maps.Clear();
            ModLog.Info(MapPanCheck.Describe(stillOn));
        }
        catch (Exception ex)
        {
            _panChecked = true;
            ModLog.Info(MapPanCheck.Failed(ModLog.SafeErrorText(ex)));
        }
    }
}
#endif
