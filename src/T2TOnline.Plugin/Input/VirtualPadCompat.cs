#if HAS_INTEROP
using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Il2Cpp;
using Il2CppBishopGames.Input;
using Il2CppRewired;
using Il2CppTape2Tape;
using Il2CppTape2Tape.Hockey.UI;
using T2TOnline.Plugin.Campaign.Menus;
using T2TOnline.Plugin.Diagnostics;
using RewiredPlayer = Il2CppRewired.Player;

namespace T2TOnline.Plugin.Input;

/// <summary>
/// Makes the game treat a virtual pad as the gamepad it stands in for, and a virtual keyboard as a keyboard player.
/// </summary>
/// <remarks>
/// A virtual pad or keyboard is a Rewired custom controller. In a few places the game decides "is this player on a
/// gamepad (or the keyboard)?" by counting joysticks, by the player's keyboard and mouse, or by the controller's type, so
/// it saw a virtual controller as no controller at all: the controller assignment screen ignored its input ("Input
/// received from non-player-controlled controller"), button prompts had no icon set ("Input device Custom not
/// implemented"), and the in-match input reader reported a keyboard player. Each patch changes the answer only for
/// players and controllers registered here, and only when the game's own answer would have been "not a gamepad" (or
/// "nobody controls it"). A virtual keyboard reads as <c>MouseKeyboard</c> for prompts and as a keyboard player on the
/// controller screen (its icon, and the couch rule that a pad player moving onto a side bumps a keyboard player there),
/// never as a gamepad; it is deliberately not a keyboard to the menus' input source (<c>T2TUIInputPlayer.HasKeyboard</c>),
/// which lets a keyboard player always drive the cursor: the host decides there (D45). The join poll
/// (RewiredPlayerInputAssigner.ActivateControllers) is relayed by the controller's owner instead, so it needs no patch.
/// </remarks>
internal static class VirtualPadCompat
{
    /// <summary>Failures of one patch logged with their stack; after that they're counted (the patches run every frame).</summary>
    private const int MaxFailureLogs = 3;

    private static readonly Dictionary<int, (int Controller, EInputDeviceType Type)> ByPlayer = new();
    private static readonly Dictionary<int, EInputDeviceType> ByController = new();
    private static readonly HashSet<string> Reported = new();
    private static readonly Dictionary<string, int> Failures = new(StringComparer.Ordinal);

    /// <summary>Patches the game. Returns false, logging why, if any target is missing.</summary>
    public static bool Install(HarmonyLib.Harmony harmony)
    {
        ArgumentNullException.ThrowIfNull(harmony);
        return Patch(harmony, typeof(RewiredInputUtils), nameof(RewiredInputUtils.GetDeviceTypeFromController), nameof(DeviceTypePostfix))
            & Patch(harmony, typeof(RewiredInputReader), "get_" + nameof(RewiredInputReader.IsUsingGamepad), nameof(UsingGamepadPostfix))
            & Patch(harmony, typeof(ControllerAssignmentPlayer), nameof(ControllerAssignmentPlayer.RefreshControllerTypeState), nameof(AssignmentPlayerPostfix))
            & Patch(harmony, typeof(ControllerAssignmentPlayer), nameof(ControllerAssignmentPlayer.RegisterInputs), nameof(AssignmentPlayerPostfix));
    }

    /// <summary>
    /// Records that Rewired player <paramref name="rewiredPlayerId"/> is driven by virtual controller
    /// <paramref name="controllerId"/> of family <paramref name="deviceType"/> (<c>MouseKeyboard</c> for a virtual keyboard).
    /// </summary>
    public static void Register(int rewiredPlayerId, int controllerId, EInputDeviceType deviceType)
    {
        ByPlayer[rewiredPlayerId] = (controllerId, deviceType);
        ByController[controllerId] = deviceType;
    }

    /// <summary>Forgets a virtual controller (its player stays registered if another one has taken over there).</summary>
    public static void Unregister(int rewiredPlayerId, int controllerId)
    {
        if (ByPlayer.TryGetValue(rewiredPlayerId, out (int Controller, EInputDeviceType Type) entry) && entry.Controller == controllerId)
        {
            ByPlayer.Remove(rewiredPlayerId);
        }

        ByController.Remove(controllerId);
    }

    /// <summary>True while a virtual pad or keyboard drives Rewired player <paramref name="rewiredPlayerId"/> (a friend's, or the one-PC test's).</summary>
    public static bool IsVirtualPlayer(int rewiredPlayerId) => ByPlayer.ContainsKey(rewiredPlayerId);

    /// <summary>True for a gamepad family (not the keyboard, not "other").</summary>
    public static bool IsGamepad(EInputDeviceType type) => type is >= EInputDeviceType.GamepadXbox and <= EInputDeviceType.GamepadGeneric;

    private static bool Patch(HarmonyLib.Harmony harmony, Type type, string method, string postfix)
    {
        MethodInfo? target = AccessTools.Method(type, method);
        if (target == null)
        {
            ModLog.Warn($"Virtual pad compat: {type.Name}.{method} not found; virtual pads may not count as gamepads there.");
            return false;
        }

        harmony.Patch(target, postfix: new HarmonyMethod(typeof(VirtualPadCompat).GetMethod(postfix, BindingFlags.NonPublic | BindingFlags.Static)));
        return true;
    }

    /// <summary>
    /// Button-prompt icons and device checks: a virtual pad reports the real pad's family (Xbox, PlayStation...), a virtual
    /// keyboard the keyboard's.
    /// </summary>
    private static void DeviceTypePostfix(Controller controller, ref EInputDeviceType __result)
    {
        try
        {
            if (__result == EInputDeviceType.Other && controller != null && controller.type == ControllerType.Custom &&
                ByController.TryGetValue(controller.id, out EInputDeviceType type))
            {
                __result = type;
                ReportOnce($"device type {type}", type == EInputDeviceType.MouseKeyboard
                    ? "Virtual pad compat: button prompts for virtual keyboards are the keyboard's."
                    : $"Virtual pad compat: button prompts for virtual pads use the {type} icon set.");
            }
        }
        catch (Exception ex)
        {
            Failed("device type", ex);
        }
    }

    /// <summary>
    /// In-match input reader: a player on a virtual pad is a gamepad player (a virtual keyboard's isn't: the game's gamepad
    /// counts and its choice of who leads the menus after a player goes ask this).
    /// </summary>
    private static void UsingGamepadPostfix(RewiredInputReader __instance, ref bool __result)
    {
        try
        {
            // Asked during play for keyboard players too: with no virtual controller (couch play) nothing more is read.
            RewiredPlayer? player = __result || ByPlayer.Count == 0 ? null : __instance._rewiredPlayer;
            if (player != null && ByPlayer.TryGetValue(player.id, out (int Controller, EInputDeviceType Type) entry) && IsGamepad(entry.Type))
            {
                __result = true;
                ReportOnce("reader", $"Virtual pad compat: the in-match input reader treats Rewired player {player.id} as a gamepad player.");
            }
        }
        catch (Exception ex)
        {
            Failed("input reader", ex);
        }
    }

    /// <summary>
    /// Controller assignment screen: a player on a virtual controller is controlled, by a gamepad (a virtual pad) or by a
    /// keyboard (a virtual keyboard: the keyboard icon, and the couch's rule that a pad player moving onto a side bumps a
    /// keyboard player there), and gets a colour. On a friend's PC, an icon of the host's screen shown there takes the
    /// host's flags instead, whatever this PC's own players have (a device change here can't flip them back).
    /// </summary>
    private static void AssignmentPlayerPostfix(ControllerAssignmentPlayer __instance)
    {
        try
        {
            if (ControllerViews.HostFlags(__instance, out bool hostKeyboard, out bool hostControlled))
            {
                __instance.IsKeyboardAssigned = hostKeyboard;
                __instance.IsPlayerControlled = hostControlled;
                return;
            }

            RewiredPlayer? player = __instance.RewiredPlayer;
            if (player == null || __instance.IsPlayerControlled || !ByPlayer.TryGetValue(player.id, out (int Controller, EInputDeviceType Type) entry))
            {
                return;
            }

            bool keyboard = entry.Type == EInputDeviceType.MouseKeyboard;
            __instance.IsKeyboardAssigned = keyboard;
            __instance.IsPlayerControlled = true;
            ControllerAssignmentPlayer.SetPlayerActive(__instance.GamePlayerId, true);
            ReportOnce($"assignment {player.id} {keyboard}", $"Virtual pad compat: the controller assignment screen treats Rewired player {player.id} (game player {__instance.GamePlayerId}) " +
                $"as a {(keyboard ? "keyboard" : "gamepad")} player.");
        }
        catch (Exception ex)
        {
            Failed("controller assignment", ex);
        }
    }

    private static void ReportOnce(string key, string message)
    {
        if (Reported.Add(key))
        {
            ModLog.Info(message);
        }
    }

    /// <summary>A patch failed: the first few with their stack, then a count at 10, 100, 1000... failures.</summary>
    private static void Failed(string patch, Exception ex)
    {
        int count = Failures.GetValueOrDefault(patch) + 1;
        Failures[patch] = count;
        if (count <= MaxFailureLogs)
        {
            ModLog.Error($"Virtual pad compat: {patch} patch failed{(count == MaxFailureLogs ? " (further failures are only counted)" : string.Empty)}", ex);
            return;
        }

        int power = 10;
        while (power < count && power <= int.MaxValue / 10)
        {
            power *= 10;
        }

        if (power == count)
        {
            ModLog.Warn($"Virtual pad compat: {patch} patch has failed {count} times so far ({ModLog.SafeErrorText(ex)}).");
        }
    }
}
#endif
