#if HAS_INTEROP
using System;
using System.Reflection;
using HarmonyLib;
using Il2CppControllerConnection;
using Il2CppRewired;
using T2TOnline.Plugin.Diagnostics;

namespace T2TOnline.Plugin.Input;

/// <summary>
/// Keeps a pad the mod holds (<see cref="LocalPad"/>) away from the game. The game's
/// <c>ControllerConnectionHandler.OnControllerConnected</c> gives every newly connected joystick to the first empty
/// Rewired player in "press to join" mode. So when a held pad reconnects (dead battery, wireless dropout) the game takes
/// it back, and the next press joins an extra player that the same pad then drives alongside the mod's virtual pad.
/// While the held pad is gone, that handler is skipped for joysticks and the mod picks the connecting pad up at once
/// (<see cref="LocalPad.TryReacquire"/>), before a press can reach the game. While the held pad is still connected, a pad
/// that connects can't be it: the game handles that one as on the couch ("press to join").
/// </summary>
internal static class HeldPadGuard
{
    /// <summary>Patches the game. Returns false, logging why, if the target is missing.</summary>
    public static bool Install(HarmonyLib.Harmony harmony)
    {
        ArgumentNullException.ThrowIfNull(harmony);
        MethodInfo? target = AccessTools.Method(typeof(ControllerConnectionHandler), "OnControllerConnected");
        if (target == null)
        {
            ModLog.Warn("Held pad guard: ControllerConnectionHandler.OnControllerConnected not found; a reconnected pad may join an extra player.");
            return false;
        }

        harmony.Patch(target, prefix: new HarmonyMethod(typeof(HeldPadGuard).GetMethod(nameof(OnControllerConnectedPrefix), BindingFlags.NonPublic | BindingFlags.Static)));
        return true;
    }

    /// <summary>
    /// Returns false (skip the game's handler) for a joystick connecting while the mod holds a pad that's gone: it's taken
    /// as the held pad at once if it's the best match. Any other connection is the game's.
    /// </summary>
    private static bool OnControllerConnectedPrefix(ControllerStatusChangedEventArgs eventData)
    {
        try
        {
            if (LocalPad.Held is not { } held || eventData == null || eventData.controllerType != ControllerType.Joystick)
            {
                return true;
            }

            if (held.IsConnected)
            {
                ModLog.Info("Held pad guard: another gamepad connected while the mod holds yours; the game takes it as on the couch (press to join).");
                return true;
            }

            bool picked = held.TryReacquire();
            ModLog.Info(picked
                ? $"Held pad guard: a gamepad connected while yours was gone; the mod reads \"{held.Joystick.name}\" from now on, and the game's own assignment is skipped, so it can't join an extra player."
                : "Held pad guard: a gamepad connected while yours was gone, but it isn't one the mod can take (another player of this game has it); the game's own assignment is skipped.");
            return false;
        }
        catch (Exception ex)
        {
            ModLog.Error("Held pad guard failed; letting the game handle the connection", ex);
            return true;
        }
    }
}
#endif
