#if HAS_INTEROP
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Il2CppRewired;
using T2TOnline.Core.Diagnostics;
using T2TOnline.Core.Input;
using T2TOnline.Plugin.Diagnostics;

namespace T2TOnline.Plugin.Input;

/// <summary>
/// Logs the game's Rewired setup once, so a log alone explains what the virtual pad could and couldn't bind, and where
/// Rewired puts a pad that connects (its auto-assignment settings, which only the game's serialized data sets) and how
/// often it updates input (the pads are fed once per input update).
/// </summary>
internal static class RewiredReport
{
    private const int MaxIdProbed = 256;

    /// <summary>Writes the report to the session log.</summary>
    public static void Log()
    {
        var actions = new List<string>();
        for (int id = 0; id < MaxIdProbed; id++)
        {
            InputAction? action = ReInput.mapping.GetAction(id);
            if (action != null)
            {
                actions.Add($"{action.id}:{action.name}");
            }
        }

        var categories = new List<string>();
        var templateMaps = new List<string>();
        var keyboardMaps = new List<string>();
        var mouseMaps = new List<string>();
        for (int id = 0; id < MaxIdProbed; id++)
        {
            InputMapCategory? category = ReInput.mapping.GetMapCategory(id);
            if (category == null)
            {
                continue;
            }

            categories.Add($"{category.id}:{category.name}");
            ControllerTemplateMap? map = ReInput.mapping.GetControllerTemplateMapInstance(GamepadTemplate.typeGuid, category.id, 0);
            int bindings = 0;
            foreach (ControllerTemplateActionElementMap unused in Native.Il2CppLists.Items(map?.ElementMaps))
            {
                bindings++;
            }

            templateMaps.Add(map == null ? $"{category.name}: none" : $"{category.name}: {bindings}");

            // The game's default keyboard and mouse maps: what a keyboard friend's virtual keyboard gets with no remaps.
            int keys = Native.Il2CppLists.Items(ReInput.mapping.GetKeyboardMapInstance(category.id, 0)?.AllMaps).Count();
            int mouse = Native.Il2CppLists.Items(ReInput.mapping.GetMouseMapInstance(category.id, 0)?.AllMaps).Count(m => m.actionId >= 0);
            keyboardMaps.Add($"{category.name} {keys}");
            if (mouse > 0)
            {
                mouseMaps.Add($"{category.name} {mouse}");
            }
        }

        var players = new StringBuilder();
        for (int id = 0; id < ReInput.players.playerCount; id++)
        {
            Player player = ReInput.players.GetPlayer(id);
            Player.ControllerHelper c = player.controllers;
            players.Append(CultureInfo.InvariantCulture, $" {player.id}:{player.name}[joy {c.joystickCount}{(c.hasKeyboard ? ", kb" : string.Empty)}{(c.hasMouse ? ", mouse" : string.Empty)}{(c.customControllerCount > 0 ? $", custom {c.customControllerCount}" : string.Empty)}" +
                $"{(c.excludeFromControllerAutoAssignment ? ", no auto-assign" : string.Empty)}{(player.isPlaying ? string.Empty : ", not playing")}]");
        }

        var joysticks = new StringBuilder();
        foreach (Joystick joystick in ReInput.controllers.GetJoysticks())
        {
            joysticks.Append(CultureInfo.InvariantCulture, $" {joystick.id}:\"{joystick.name}\" ({joystick.hardwareName}, gamepad template: {(joystick.ImplementsTemplate(GamepadTemplate.typeGuid) ? "yes" : "no")}, connected: {joystick.isConnected})");
        }

        ModLog.Info($"Rewired: {actions.Count} actions: {string.Join(", ", actions)}");
        ModLog.Info($"Rewired: {categories.Count} map categories: {string.Join(", ", categories)}. Gamepad-template bindings per category: {string.Join(", ", templateMaps)}.");
        ModLog.Info($"Rewired: keyboard bindings per category: {string.Join(", ", keyboardMaps)}; mouse buttons: {(mouseMaps.Count == 0 ? "none" : string.Join(", ", mouseMaps))} " +
            "(the game's defaults, as a keyboard friend's virtual keyboard has them without remaps).");
        ModLog.Info($"Rewired: players:{players}");
        ModLog.Info($"Rewired: joysticks:{(joysticks.Length == 0 ? " none" : joysticks.ToString())}");
        LogConfiguration();
        LogInputSources();
        VirtualPad.NameLackingAtStartUp();
    }

    /// <summary>
    /// Rewired's version and where it reads pads from on Windows, and each joystick's profile: a pad set up by Steam Input
    /// is read as Rewired's "Steam Controller" profile only with Windows Gaming Input on, and its triggers come only if Steam
    /// Input's layout for the game sends them.
    /// </summary>
    private static void LogInputSources()
    {
        ReInput.ConfigHelper config = ReInput.configuration;
        var profiles = new StringBuilder();
        foreach (Joystick joystick in ReInput.controllers.GetJoysticks())
        {
            profiles.Append(CultureInfo.InvariantCulture, $" \"{SafeText.Clean(joystick.name, 48)}\" {PadProfile.Describe(joystick)};");
        }

        ModLog.Info($"Rewired {SafeText.Clean(ReInput.programVersion, 32)}: Windows input from {config.windowsStandalonePrimaryInputSource}, XInput {OnOff(config.useXInput)}, " +
            $"Windows Gaming Input {OnOff(config.useWindowsGamingInput)} (a pad set up by Steam Input reads as Rewired's \"Steam Controller\" profile " +
            $"{TriggerCheck.SteamInputProfile.ToString("N", CultureInfo.InvariantCulture)[..8]}); joysticks' profiles:{(profiles.Length == 0 ? " none" : profiles.ToString().TrimEnd(';'))}.");
    }

    /// <summary>
    /// Where Rewired itself puts a joystick that connects (before the game's own handler sees it), and its update loop. A
    /// player marked "no auto-assign" above is skipped; a virtual pad's player is marked while the pad is there.
    /// </summary>
    private static void LogConfiguration()
    {
        ReInput.ConfigHelper config = ReInput.configuration;
        ModLog.Info($"Rewired config: auto-assign joysticks {OnOff(config.autoAssignJoysticks)}, at most {config.maxJoysticksPerPlayer} per player, " +
            $"spread evenly {OnOff(config.distributeJoysticksEvenly)}, only to playing players {OnOff(config.assignJoysticksToPlayingPlayersOnly)}, " +
            $"back to the previous owner on reconnect {OnOff(config.reassignJoystickToPreviousOwnerOnReconnect)}; input updates in {config.updateLoop} " +
            "(the virtual pads are fed once per input update).");
    }

    private static string OnOff(bool value) => value ? "on" : "off";
}
#endif
