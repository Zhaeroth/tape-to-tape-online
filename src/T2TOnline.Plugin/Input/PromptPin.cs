#if HAS_INTEROP
using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Il2CppBishopGames.Input;
using Il2CppControllerConnection;
using Il2CppRewired;
using Il2CppTape2Tape;
using Il2CppTape2Tape.Customization.UI;
using MelonLoader;
using T2TOnline.Core.Diagnostics;
using T2TOnline.Core.Input;
using T2TOnline.Plugin.Campaign.Menus;
using T2TOnline.Plugin.Diagnostics;
using T2TOnline.Plugin.Native;
using T2TOnline.Plugin.Streaming;
using UnityEngine;
using UnityEngine.SceneManagement;
using RewiredPlayer = Il2CppRewired.Player;

namespace T2TOnline.Plugin.Input;

/// <summary>
/// The device a friend plays with, whose button prompts their screen shows while they're online (<see cref="PromptPin"/>):
/// its family (the game's device type) and where its bindings are. A pad's are the saved maps of the Rewired player that
/// had it, for that joystick; the keyboard and mouse's are the saved maps of the Rewired player that has them.
/// </summary>
internal readonly struct PromptDevice
{
    /// <summary>The Rewired player that has the keyboard and mouse on every PC (the game assigns them to player 0 at start-up).</summary>
    public const int KeyboardPlayerId = 0;

    private PromptDevice(EInputDeviceType family, Joystick? pad, int mapsPlayerId)
    {
        Family = family;
        Pad = pad;
        MapsPlayerId = mapsPlayerId;
    }

    /// <summary>The game's device type: a pad family, or keyboard and mouse.</summary>
    public EInputDeviceType Family { get; }

    /// <summary>The pad's Rewired joystick; null for the keyboard and mouse.</summary>
    public Joystick? Pad { get; }

    /// <summary>The Rewired player whose saved maps are the friend's own; -1 for a pad no player had (the game would give it to the first free one).</summary>
    public int MapsPlayerId { get; }

    /// <summary>The pad <paramref name="pad"/> reads: its family now, its joystick now, the player that had it when the session started.</summary>
    public static PromptDevice ForPad(LocalPad pad)
    {
        ArgumentNullException.ThrowIfNull(pad);
        return new PromptDevice(pad.DeviceType, pad.Joystick, pad.OwnerPlayerId);
    }

    /// <summary>The keyboard and mouse, with the maps of Rewired player <paramref name="mapsPlayerId"/>.</summary>
    public static PromptDevice ForKeyboard(int mapsPlayerId) => new(EInputDeviceType.MouseKeyboard, null, mapsPlayerId);

    /// <summary>A pad of this PC's own game that Rewired player <paramref name="mapsPlayerId"/> holds, of family <paramref name="family"/> (the host's own players, for the controller screen's button names).</summary>
    public static PromptDevice ForHeldPad(EInputDeviceType family, Joystick pad, int mapsPlayerId)
    {
        ArgumentNullException.ThrowIfNull(pad);
        return new PromptDevice(family, pad, mapsPlayerId);
    }

    /// <summary>True when <paramref name="other"/> is the same device with the same maps (compared without asking the game).</summary>
    public bool SameAs(PromptDevice other) =>
        Family == other.Family && MapsPlayerId == other.MapsPlayerId && (Pad?.Pointer ?? IntPtr.Zero) == (other.Pad?.Pointer ?? IntPtr.Zero);
}

/// <summary>
/// The prompt pin: while a friend's device is with the host's game (a play period,
/// from BeginPlay to EndPlay), the button prompts on their screen show their own device family and their own bindings, for
/// every player, as their own couch would.
/// </summary>
/// <remarks>
/// The game draws prompts on each PC by itself: every game player has a device type in the
/// prompt mapper (<c>RewiredInputSpriteMapper</c>), set by its automatic detection from the player's last-used Rewired
/// controller (<c>RefreshIconsMode</c>), and a prompt's key or button name comes from that player's maps for that device
/// (<c>GetRawInputNameFromKeyboard</c>, <c>GetRawInputNameFromJoystick</c>). A friend's pad is on none of their game's
/// players while online (<see cref="LocalPad.Detach"/>), so the detection follows whatever else was used last (the
/// keyboard, after F7) and names come from other maps. While pinned, every player and the default are set to the
/// friend's family; the automatic detection is skipped (its choice is only followed, for the log and to put back at the
/// end); and a prompt asked for on the friend's family is named from the friend's own maps, loaded once from Rewired's
/// saved maps (the game's defaults where there are none). The game's "Controller icons" setting, when it forces an icon
/// set, is kept, as on the couch. Each kind of screen (a scene and an action category) logs once what the pin drew and
/// what the game would have drawn without it. A failure turns the pin off for the rest of the session, never into the
/// game.
/// </remarks>
internal static class PromptPin
{
    private const int MaxFailureLogs = 3;

    /// <summary>Changes of the game's own choice logged per pinned session; the rest are counted.</summary>
    private const int ChangeLines = 5;

    /// <summary>The game's own refresh cooldown (<c>REFRESH_COOLDOWN</c>, 0.5 s of unscaled time; read in RefreshIconsMode).</summary>
    private const float RefreshCooldown = 0.5f;

    /// <summary>How often the friend's device is looked at again (a pad picked up again, another family).</summary>
    private const double FollowSeconds = 0.5;

    private static readonly PromptPinState State = new();
    private static bool _installed;
    private static bool _ticking;
    private static Func<PromptDevice>? _source;
    private static PromptDevice _device;
    private static string _deviceLine = string.Empty;
    private static Il2CppSystem.Collections.Generic.List<ControllerMap>? _maps;
    private static Il2CppSystem.Collections.Generic.IList<ControllerMap>? _mapsView;
    private static Il2CppSystem.Collections.Generic.List<string>? _scratch;
    private static Il2CppSystem.Collections.Generic.IList<string>? _scratchView;
    private static RewiredInputSpriteMapper? _mapper;
    private static bool _applied;
    private static int _bypass;
    private static bool _faulted;
    private static int _failures;
    private static bool _observeOff; // following what the game would draw failed once: the pin carries on without that log
    private static int _changeLines;
    private static double _nextFollow;
    private static int _sceneHandle = int.MinValue;
    private static string _sceneName = string.Empty;
    private static bool _reload; // the friend's controls changed: their maps are read again at the next follow
    private static int _refreshesHeld; // the game's own refreshes the pin stood in for while pinned
    private static int _refreshesFollowed; // of those, followed in the shadow (the game's choice, for the log)

    /// <summary>True while a friend's prompts are pinned to their own device.</summary>
    public static bool Pinned => State.Pinned;

    /// <summary>
    /// The friend's controls changed mid-session (a remap, <c>OnlineSession.NoteControlsChanged</c>): their maps are read
    /// again within half a second, so this PC's prompts show the new bindings. Never throws.
    /// </summary>
    public static void ControlsChanged() => _reload = true;

    /// <summary>
    /// Patches the game's prompt mapper: three prefixes (Hooks/hooks.txt, "Prompt pin"), which do nothing until
    /// <see cref="Pin"/>. If one can't go in, the pin stays off for the game's life (a friend's prompts are then the game's
    /// own) and online play is unaffected; said in the log. Never throws.
    /// </summary>
    public static void Install(HarmonyLib.Harmony harmony)
    {
        ArgumentNullException.ThrowIfNull(harmony);
        var failed = new List<string>();
        try
        {
            Patch(harmony, failed, "RefreshIconsMode", new[] { typeof(RewiredPlayer), typeof(Controller) }, nameof(RefreshIconsPrefix));
            Patch(harmony, failed, "GetRawInputNameFromKeyboard", null, nameof(KeyboardNamePrefix));
            Patch(harmony, failed, "GetRawInputNameFromJoystick", null, nameof(JoystickNamePrefix));
            if (failed.Count == 0 && !_ticking)
            {
                MelonEvents.OnUpdate.Subscribe(Tick);
                _ticking = true;
            }
        }
        catch (Exception ex)
        {
            failed.Add($"{ModLog.SafeErrorText(ex)}");
        }

        _installed = failed.Count == 0;
        if (_installed)
        {
            ModLog.Info("Prompt pin: ready (while you're in a friend's game, your button prompts show your own pad and bindings).");
        }
        else
        {
            ModLog.Warn($"Prompt pin: off ({string.Join(", ", failed)} couldn't be installed): friends see this game's own button prompts while online; online play is unaffected.");
        }
    }

    /// <summary>
    /// A friend's play period started (not the one-PC test, whose game is the host's): from now on every player's prompts on
    /// this PC show the device <paramref name="source"/> gives, with its own bindings. It's asked again every half
    /// second, so a pad picked up again (another joystick, maybe another family) is followed. Never throws.
    /// </summary>
    public static void Pin(Func<PromptDevice> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!_installed)
        {
            ModLog.Info("Prompts: the prompt pin is off this game (see the start-up lines), so your prompts are this game's own while online.");
            return;
        }

        if (State.Pinned)
        {
            Unpin("a new session", restore: true);
        }

        _faulted = false;
        _observeOff = false;
        _failures = 0;
        _changeLines = 0;
        _refreshesHeld = 0;
        _refreshesFollowed = 0;
        _sceneHandle = int.MinValue;
        try
        {
            PromptDevice device = source();
            if (!PromptPinState.IsKnown((int)device.Family))
            {
                ModLog.Warn($"Prompts: your device reads as {PromptPinState.DeviceName((int)device.Family)}, which the game draws no prompts for; not pinned (this game's own prompts show).");
                return;
            }

            _scratch ??= new Il2CppSystem.Collections.Generic.List<string>();
            _scratchView ??= _scratch.Cast<Il2CppSystem.Collections.Generic.IList<string>>();
            Load(device);
            _reload = false; // just read; one left from outside a pinned session (the one-PC test's remap) has nothing to say
            _source = source;
            State.Pin((int)device.Family);
            _nextFollow = FrameClock.Seconds + FollowSeconds;
            RewiredInputSpriteMapper? mapper = CurrentMapper();
            if (mapper == null)
            {
                ModLog.Info($"Prompts: pinning to {_deviceLine}; this game's prompt mapper isn't up yet, so every player's prompts follow once it is.");
                return;
            }

            Apply(mapper);
            ModLog.Info($"Prompts: {PinnedSentence(mapper)} Without the pin this game would show: {State.DescribeShadow()}.");
        }
        catch (Exception ex)
        {
            Fail("pinning", ex);
        }
    }

    /// <summary>
    /// The friend's play period or side of the session ended (<paramref name="why"/>): prompts follow this game's own choice again. With
    /// <paramref name="restore"/> (not while the game is closing) every player's device goes back to what the game's own
    /// detection chose meanwhile, and the game refreshes it at once. Never throws.
    /// </summary>
    public static void Unpin(string why, bool restore)
    {
        if (!State.Pinned)
        {
            Forget();
            return;
        }

        try
        {
            State.Unpin();
            RewiredInputSpriteMapper? mapper = _mapper;
            string back = string.Empty;
            if (restore && mapper != null && !mapper.WasCollected && State.ShadowKnown)
            {
                int players = Math.Min(mapper.MaxPlayers, State.Players);
                for (int i = 0; i < players; i++)
                {
                    mapper.ChangeControllingDevice((EInputDeviceType)State.Shadow(i), i, false);
                }

                mapper.ChangeControllingDevice((EInputDeviceType)State.ShadowDefault, -1, true);
                mapper.RefreshIconsModeFromController(null, null);
                back = $" (now: default {PromptPinState.DeviceName((int)mapper.DefaultPlayerCurrentDevice)})";
            }

            string screens = State.ScreensNotLogged > 0 ? $", {State.ScreensNotLogged} more not" : string.Empty;
            ModLog.Info($"Prompts: unpinned ({why}): every player's prompts follow this game's own choice again{back}. While pinned: {State.Drawn} prompt(s) drawn " +
                $"from your own bindings ({State.NotFound} with none found, drawn blank as on your couch); this game's own choice changed {State.ShadowChanges} time(s); " +
                $"{State.ScreensLogged} screen(s) logged{screens}; {_refreshesHeld} of the game's prompt refreshes held while pinned ({_refreshesFollowed} followed in the " +
                $"shadow{(_observeOff ? ", the shadow off after its failure" : string.Empty)}).");
        }
        catch (Exception ex)
        {
            ModLog.Error("Prompts: unpinning failed; this game's prompts may show your device until its next refresh", ex);
        }
        finally
        {
            Forget();
        }
    }

    private static void Patch(HarmonyLib.Harmony harmony, List<string> failed, string method, Type[]? parameters, string prefix)
    {
        try
        {
            MethodInfo? target = parameters == null
                ? AccessTools.DeclaredMethod(typeof(RewiredInputSpriteMapper), method)
                : AccessTools.DeclaredMethod(typeof(RewiredInputSpriteMapper), method, parameters);
            if (target == null)
            {
                failed.Add($"RewiredInputSpriteMapper.{method} (not found)");
                return;
            }

            harmony.Patch(target, prefix: new HarmonyMethod(typeof(PromptPin).GetMethod(prefix, BindingFlags.NonPublic | BindingFlags.Static)));
        }
        catch (Exception ex)
        {
            failed.Add($"RewiredInputSpriteMapper.{method} ({ModLog.SafeErrorText(ex)})");
        }
    }

    /// <summary>Every frame: twice a second while pinned, the friend's device again and the mapper it applies to; after a failure, the pin goes.</summary>
    private static void Tick()
    {
        if (!State.Pinned)
        {
            return;
        }

        double now = FrameClock.Seconds;
        if (now < _nextFollow)
        {
            return;
        }

        _nextFollow = now + FollowSeconds;
        if (_faulted)
        {
            Unpin("after an error", restore: true);
            return;
        }

        try
        {
            Follow();
        }
        catch (Exception ex)
        {
            Fail("following your device", ex);
        }
    }

    /// <summary>Another device (a pad picked up again, or another family) is loaded and applied; a mapper not pinned yet is.</summary>
    private static void Follow()
    {
        PromptDevice device = _source!();
        if (!device.SameAs(_device) || _reload)
        {
            if (!PromptPinState.IsKnown((int)device.Family))
            {
                return; // nothing the game draws; the last device stays
            }

            bool family = device.Family != _device.Family;
            bool reload = _reload && device.SameAs(_device);
            _reload = false;
            Load(device);
            State.Refamily((int)device.Family);
            _applied &= !family;
            ModLog.Info(reload ? $"Prompts: your controls changed; following {_deviceLine} now." : $"Prompts: following {_deviceLine} now.");
        }

        RewiredInputSpriteMapper? mapper = CurrentMapper();
        if (mapper == null || (_applied && _mapper != null && _mapper.Pointer == mapper.Pointer))
        {
            return;
        }

        bool first = _mapper == null;
        Apply(mapper);
        if (first)
        {
            ModLog.Info($"Prompts: this game's prompt mapper is up; {PinnedSentence(mapper)} Without the pin this game would show: {State.DescribeShadow()}.");
        }
    }

    /// <summary>
    /// Loads the friend's own maps for <paramref name="device"/>, one per map category, as the game's own lookups take
    /// them when no player of this game holds the device: Rewired's saved map for that player (the game's default where
    /// there's none). The keyboard's are the mouse's map then the keyboard's, per category, as the game reads them.
    /// </summary>
    private static void Load(PromptDevice device)
    {
        Il2CppSystem.Collections.Generic.List<ControllerMap> maps = LoadMaps(device, out int player, out int bindings);
        _maps = maps;
        _mapsView = maps.Cast<Il2CppSystem.Collections.Generic.IList<ControllerMap>>();
        _device = device;
        string family = PromptPinState.DeviceName((int)device.Family);
        string what = device.Pad != null ? $"your pad \"{SafeText.Clean(device.Pad.name, 48)}\"" : "your keyboard and mouse";
        string whose = device.MapsPlayerId >= 0 ? $"Rewired player {player}'s" : $"Rewired player {player}'s (no player had the pad; the first free one)";
        _deviceLine = $"your {family} ({what}: {maps.Count} map(s), {bindings} binding(s), {whose} saved maps or the game's defaults)";
    }

    /// <summary>
    /// <paramref name="device"/>'s own maps, one per map category, as the game's own lookups take them when no player of this
    /// game holds the device (<see cref="Load"/>; also what a friend's prompt names for the host are made from,
    /// <see cref="PromptNames.Make"/>): Rewired's saved maps of <paramref name="player"/> (the device's own player, else the
    /// first free one), the game's defaults where there are none. <paramref name="bindings"/> counts their element maps.
    /// </summary>
    internal static Il2CppSystem.Collections.Generic.List<ControllerMap> LoadMaps(PromptDevice device, out int player, out int bindings)
    {
        player = device.MapsPlayerId >= 0 ? device.MapsPlayerId : FirstFreePlayer();
        var maps = new Il2CppSystem.Collections.Generic.List<ControllerMap>();
        bindings = 0;
        foreach (InputMapCategory category in Il2CppLists.Items(ReInput.mapping.MapCategories))
        {
            int id = category.id;
            if (device.Pad != null)
            {
                bindings += Add(maps, ReInput.mapping.GetJoystickMapInstanceSavedOrDefault(player, device.Pad, id, 0));
            }
            else
            {
                bindings += Add(maps, ReInput.mapping.GetMouseMapInstanceSavedOrDefault(player, id, 0));
                bindings += Add(maps, ReInput.mapping.GetKeyboardMapInstanceSavedOrDefault(player, id, 0));
            }
        }

        return maps;
    }

    private static int Add(Il2CppSystem.Collections.Generic.List<ControllerMap> maps, ControllerMap? map)
    {
        if (map == null)
        {
            return 0;
        }

        maps.Add(map);
        return map.elementMapCount;
    }

    /// <summary>The first Rewired player with no controller at all, where the game puts a newly connected pad; else 0 (whose maps the game reads for a pad no player has).</summary>
    private static int FirstFreePlayer()
    {
        for (int id = 0; id < ReInput.players.playerCount; id++)
        {
            RewiredPlayer.ControllerHelper? controllers = ReInput.players.GetPlayer(id)?.controllers;
            if (controllers != null && controllers.joystickCount == 0 && controllers.customControllerCount == 0 && !controllers.hasKeyboard && !controllers.hasMouse)
            {
                return id;
            }
        }

        return 0;
    }

    /// <summary>
    /// The pin goes on <paramref name="mapper"/>: the first time, the game's own choice is taken as it stands (what to
    /// follow, and to put back); then every player and the default get the friend's family, unless the game's
    /// "Controller icons" setting forces an icon set (kept, as on the couch).
    /// </summary>
    private static void Apply(RewiredInputSpriteMapper mapper)
    {
        int players = Math.Min(mapper.MaxPlayers, PromptPinState.MaxPlayers);
        if (_mapper == null || _mapper.Pointer != mapper.Pointer)
        {
            int[] devices = new int[players];
            for (int i = 0; i < players; i++)
            {
                devices[i] = (int)mapper.GetCurrentDeviceType(i);
            }

            State.TrackGame(devices, (int)mapper.DefaultPlayerCurrentDevice);
            _mapper = mapper;
        }

        if (ForcedDevice(mapper) < 0)
        {
            // As the game's own forced refresh does it: each player, and the default with them (an event only on a change).
            var family = (EInputDeviceType)State.Family;
            for (int i = 0; i < players; i++)
            {
                mapper.ChangeControllingDevice(family, i, true);
            }
        }

        _applied = true;
    }

    /// <summary>The pinned line: what every player's prompts show now.</summary>
    private static string PinnedSentence(RewiredInputSpriteMapper mapper)
    {
        int forced = ForcedDevice(mapper);
        return forced < 0
            ? $"pinned to {_deviceLine} for every player while online."
            : $"your Settings force the {PromptPinState.DeviceName(forced)} icon set on every player: kept, as on your couch; prompts of {_deviceLine} use your own bindings.";
    }

    /// <summary>The device the game's "Controller icons" setting forces, or -1 when it's automatic.</summary>
    internal static int ForcedDevice(RewiredInputSpriteMapper mapper) => PromptPinState.ForcedDevice(mapper.m_SystemSettings?._controllerIcon ?? 0);

    /// <summary>The game's prompt mapper (the UI module's), or null before the UI is up.</summary>
    internal static RewiredInputSpriteMapper? CurrentMapper() => MenuGame.Module()?.InputSpriteMapper?.TryCast<RewiredInputSpriteMapper>();

    /// <summary>
    /// The game's automatic detection (every trigger: a player's or any controller's last use, focus, the UI's player).
    /// While pinned it's skipped, past the game's own cooldown, and its choice only followed; with an icon set forced by
    /// the settings the game's own refresh runs.
    /// </summary>
    private static bool RefreshIconsPrefix(RewiredInputSpriteMapper __instance, RewiredPlayer triggeringPlayer, Controller triggeringController)
    {
        if (!State.Pinned || _faulted)
        {
            return true;
        }

        try
        {
            float now = UnityEngine.Time.unscaledTime;
            bool due = now - __instance.m_LastRefreshTime >= RefreshCooldown;
            int forced = ForcedDevice(__instance);
            if (forced >= 0)
            {
                if (due)
                {
                    State.GameForced(forced);
                }

                _applied = false; // the pin goes on again if the setting goes back to automatic
                return true;
            }

            if (!due)
            {
                return false; // the game skips this one too
            }

            __instance.m_LastRefreshTime = now;
            _refreshesHeld++;
            if (!_observeOff)
            {
                // Only for the log (what the game would have drawn): its failure mustn't take the pin down with it.
                try
                {
                    Observe(__instance, triggeringPlayer, triggeringController);
                    _refreshesFollowed++;
                }
                catch (Exception ex)
                {
                    _observeOff = true;
                    ModLog.Warn($"Prompts: following what this game would draw failed ({ModLog.SafeErrorText(ex)}); " +
                        "the pin carries on, only that log line is off for the session.");
                }
            }

            if (!_applied || _mapper == null || _mapper.Pointer != __instance.Pointer)
            {
                Apply(__instance);
            }

            return false;
        }
        catch (Exception ex)
        {
            Fail("following the game's icon refresh", ex);
            return true;
        }
    }

    /// <summary>What the game's automatic detection would choose for this trigger (RefreshIconsMode), followed in the shadow.</summary>
    private static void Observe(RewiredInputSpriteMapper mapper, RewiredPlayer? player, Controller? controller)
    {
        if (controller == null)
        {
            if (player != null)
            {
                controller = LastActive(player);
            }
            else
            {
                mapper.GetLastActiveController(true, out player, out controller);
            }
        }

        if (controller == null)
        {
            return;
        }

        int gamePlayer = player != null && RewiredPlayerInputAssigner.Instance != null ? RewiredPlayerInputAssigner.GetPlayerId(player.id) : -1;
        int device = (int)RewiredInputUtils.GetDeviceTypeFromController(controller);
        int changes = State.ShadowChanges;
        State.GameChose(device, gamePlayer);
        if (State.ShadowChanges != changes && _changeLines < ChangeLines)
        {
            // What this logs settles which of this PC's devices steer the game's own prompts while the pad is detached.
            _changeLines++;
            string players = gamePlayer >= 0 ? $"game player {gamePlayer} and prompts tied to no player" : "prompts tied to no player";
            ModLog.Info($"Prompts: without the pin this game would now show {PromptPinState.DeviceName(device)} for {players} (\"{SafeText.Clean(controller.name, 48)}\" was used); " +
                $"the pin keeps your {PromptPinState.DeviceName(State.Family)}{(_changeLines == ChangeLines ? " (further changes are only counted)" : string.Empty)}.");
        }
    }

    /// <summary>A keyboard-and-mouse prompt's name.</summary>
    private static bool KeyboardNamePrefix(
        RewiredInputSpriteMapper __instance, RewiredPlayer player, InputAction action, AxisRange axisRange, InputCategory category,
        Il2CppSystem.Collections.Generic.IList<string> modifiersRawInputNames, ref string? __result) =>
        Draw(__instance, player, action, axisRange, category, EInputDeviceType.MouseKeyboard, modifiersRawInputNames, ref __result);

    /// <summary>A pad prompt's name.</summary>
    private static bool JoystickNamePrefix(
        RewiredInputSpriteMapper __instance, RewiredPlayer player, InputAction action, AxisRange axisRange, InputCategory category, EInputDeviceType deviceType,
        Il2CppSystem.Collections.Generic.IList<string> modifiersRawInputNames, ref string? __result) =>
        Draw(__instance, player, action, axisRange, category, deviceType, modifiersRawInputNames, ref __result);

    /// <summary>
    /// A prompt on the pinned family is named from the friend's own maps, by the game's own matcher
    /// (<c>GetRawInputNameFromMaps</c>, which also adds any modifier keys); any other prompt (a device forced by the
    /// screen, or the settings) is the game's. Not pinned (a host), a prompt for a friend's Rewired player on their family is
    /// named from the names their PC sent (<see cref="PromptNames.TryAnswer"/>).
    /// </summary>
    private static bool Draw(
        RewiredInputSpriteMapper mapper, RewiredPlayer? player, InputAction action, AxisRange axisRange, InputCategory? category, EInputDeviceType device,
        Il2CppSystem.Collections.Generic.IList<string> modifiers, ref string? result)
    {
        if (ControllerGlyphViews.Scoped)
        {
            // A controller screen icon's button, drawn from its own player's names (Campaign/Menus/ControllerGlyphViews.cs): the
            // names the host sent answer it; with none, the game's own lookup, never the pin's (those are this player's bindings).
            return !ControllerGlyphViews.TryAnswer(action, axisRange, device, modifiers, ref result);
        }

        if (!State.Pinned)
        {
            return _bypass > 0 || !PromptNames.TryAnswer(mapper, player, action, axisRange, device, modifiers, ref result);
        }

        if (_bypass > 0 || _faulted || _mapsView == null || !State.Draws((int)device))
        {
            return true;
        }

        try
        {
            string? name = mapper.GetRawInputNameFromMaps(action, _mapsView, axisRange, device, modifiers);
            State.Drew(!string.IsNullOrEmpty(name));
            if (State.FirstOnScreen(SceneName(), category?.id ?? -1))
            {
                LogScreen(mapper, action, axisRange, category, device, name);
            }

            result = name;
            return false;
        }
        catch (Exception ex)
        {
            Fail("drawing a prompt", ex);
            return true;
        }
    }

    /// <summary>
    /// A kind of screen's one line: the prompt the pin drew, and what the game would have drawn without it (its own lookup
    /// for the device its detection chose, for prompts tied to no player). A failure here costs only the line.
    /// </summary>
    private static void LogScreen(RewiredInputSpriteMapper mapper, InputAction action, AxisRange axisRange, InputCategory? category, EInputDeviceType device, string? drawn)
    {
        try
        {
            string pinned = Glyph(mapper, drawn, (int)device);
            int shadow = State.ShadowDefault;
            string game = !State.ShadowKnown ? "unknown (its choice wasn't followed yet)"
                : !PromptPinState.IsKnown(shadow) ? $"nothing ({PromptPinState.DeviceName(shadow)}: no device it draws prompts for)"
                : GameGlyph(mapper, action, axisRange, category, shadow, drawn, (int)device);
            ModLog.Info($"Prompts ({_sceneName}, {SafeText.Clean(category?.name, 32)}): the pin drew \"{SafeText.Clean(action.name, 48)}\" as {pinned}, " +
                $"from your own bindings; without the pin this game would have drawn {game}.");
        }
        catch (Exception ex)
        {
            ModLog.Warn($"Prompts: the line for this screen failed ({ModLog.SafeErrorText(ex)}); the prompt itself is drawn.");
        }
    }

    /// <summary>The game's own name for the prompt on <paramref name="device"/>, the pin stepped aside (as the game asks for default prompts).</summary>
    private static string GameGlyph(RewiredInputSpriteMapper mapper, InputAction action, AxisRange axisRange, InputCategory? category, int device, string? drawn, int drawnDevice)
    {
        _bypass++;
        try
        {
            InputLayout layout = ReInput.mapping.GetLayout(device == PromptPinState.MouseKeyboard ? ControllerType.Keyboard : ControllerType.Joystick, 0);
            _scratch!.Clear();
            string? name = mapper.GetRawInputName_Internal(PlayerFor(device), action, axisRange, layout, category, (EInputDeviceType)device, _scratchView);
            bool same = device == drawnDevice && string.Equals(name, drawn, StringComparison.Ordinal);
            return same ? $"the same ({Glyph(mapper, name, device)})" : Glyph(mapper, name, device);
        }
        finally
        {
            _bypass--;
        }
    }

    /// <summary>A prompt as drawn: its name, device and sprite.</summary>
    private static string Glyph(RewiredInputSpriteMapper mapper, string? name, int device)
    {
        if (string.IsNullOrEmpty(name))
        {
            return $"nothing (no {PromptPinState.DeviceName(device)} binding found)";
        }

        Sprite? sprite = mapper.GetSpriteForRawInput(name, (EInputDeviceType)device);
        string spriteName = sprite == null ? "none" : $"\"{SafeText.Clean(sprite.name, 48)}\"";
        return $"\"{SafeText.Clean(name, 48)}\" ({PromptPinState.DeviceName(device)}, sprite {spriteName})";
    }

    /// <summary>
    /// The Rewired player whose maps the game's own lookup reads for a default prompt on <paramref name="device"/>
    /// (GetRawInputName_Internal): game player 0's, if its last-used controller is that device; else the
    /// first player's whose is; else none.
    /// </summary>
    private static RewiredPlayer? PlayerFor(int device)
    {
        RewiredPlayer? first = RewiredPlayerInputAssigner.Instance != null ? RewiredPlayerInputAssigner.GetRewiredPlayer(0) : null;
        if (first != null && DeviceOf(first) == device)
        {
            return first;
        }

        for (int id = 0; id < ReInput.players.playerCount; id++)
        {
            RewiredPlayer? player = ReInput.players.GetPlayer(id);
            if (player != null && (first == null || player.Pointer != first.Pointer) && DeviceOf(player) == device)
            {
                return player;
            }
        }

        return null;
    }

    private static int DeviceOf(RewiredPlayer player) => LastActive(player) is { } controller ? (int)RewiredInputUtils.GetDeviceTypeFromController(controller) : -1;

    /// <summary>
    /// A player's last-used controller, as Rewired's <c>Player.ControllerHelper.GetLastActiveController</c> answers: of
    /// its joysticks, keyboard and mouse, the one active last by Rewired's own clock. That call can't be made through the
    /// interop in this build ("Invalid call to method": calling it turned the pin off at its first
    /// refresh), so it's read from calls the mod already makes. Null when the player has none.
    /// </summary>
    private static Controller? LastActive(RewiredPlayer player)
    {
        Controller? last = null;
        double lastTime = double.MinValue;
        void Consider(Controller? controller)
        {
            if (controller == null)
            {
                return;
            }

            double time = controller.GetLastTimeActive();
            if (last == null || time > lastTime)
            {
                last = controller;
                lastTime = time;
            }
        }

        foreach (Joystick joystick in ReInput.controllers.GetJoysticks())
        {
            if (joystick != null && ReInput.controllers.IsJoystickAssignedToPlayer(joystick.id, player.id))
            {
                Consider(joystick);
            }
        }

        if (player.controllers.hasKeyboard)
        {
            Consider(ReInput.controllers.Keyboard);
        }

        if (player.controllers.hasMouse)
        {
            Consider(ReInput.controllers.Mouse);
        }

        return last;
    }

    /// <summary>The active scene's name, read again only when the scene changes.</summary>
    private static string SceneName()
    {
        Scene scene = SceneManager.GetActiveScene();
        int handle = scene.handle;
        if (handle != _sceneHandle)
        {
            _sceneHandle = handle;
            _sceneName = SafeText.Clean(scene.name, 48);
        }

        return _sceneName;
    }

    /// <summary>A failure: logged (the first few with their stack), and the pin is off for the rest of this session (the next frame check unpins).</summary>
    private static void Fail(string what, Exception ex)
    {
        _faulted = true;
        if (++_failures <= MaxFailureLogs)
        {
            ModLog.Error($"Prompts: {what} failed; the prompt pin is off for the rest of this session, and this game's own prompts show", ex);
        }
    }

    private static void Forget()
    {
        _source = null;
        _maps = null;
        _mapsView = null;
        _mapper = null;
        _applied = false;
        _device = default;
        _deviceLine = string.Empty;
    }
}
#endif
