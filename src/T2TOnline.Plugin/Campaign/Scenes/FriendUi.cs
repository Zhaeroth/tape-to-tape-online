#if HAS_INTEROP
using System;
using System.Collections.Generic;
using Il2CppBishopGames.UI;
using Il2CppRewired;
using Il2CppSTS.Map;
using Il2CppTape2Tape.Customization.UI;
using T2TOnline.Plugin.Diagnostics;
using UnityEngine.EventSystems;

namespace T2TOnline.Plugin.Campaign.Scenes;

/// <summary>
/// A friend's own UI while its game follows a host (D40): the friend's pad
/// already drives a virtual pad on the host, so nothing typed, clicked or pressed on the friend's PC may act on its own
/// game. While <see cref="Locked"/>:
/// <list type="bullet">
/// <item>the game's UI navigation is switched off (<c>NavigationController.AddNavigationActiveState</c>, the game's own
/// switch, at a priority above the game's own requests), which stops cursor moves and confirms;</item>
/// <item>the keyboard and mouse maps of Rewired's UI category are off, and the EventSystem's input module (pointer
/// clicks) is off, both put back as they were at the end;</item>
/// <item>and, as a backstop, <c>AbstractNavigable.ForceClick</c> (every click's actions), <c>UIInputShortcutBase.FireEvent</c>
/// (every shortcut), <c>NavigationController.OnBackInput</c> (every back handler, the menus' included) and the campaign
/// map's mouse drag (<c>ScrollNonUI.OnMouseDown</c>: the camera follows the host's) are refused.</item>
/// </list>
/// The mirror (the scenes' map cursor, the menu views' menus) changes the friend's screens itself: it selects with
/// <c>NavigationController.SetCurrentNavigable(nav, true, true)</c>, which works while navigation is off, and runs
/// anything that would otherwise be refused inside <see cref="Apply"/>.
/// A friend's own Settings menu or remapper lifts it in part while it's up (FriendUi.OwnMenu.cs).
/// </summary>
internal static partial class FriendUi
{
    /// <summary>The navigation-off request's priority: above the game's own (a preview asks at about 100).</summary>
    private const int NavigationPriority = 100_000;

    /// <summary>Refusals named in the log; later ones are only counted.</summary>
    private const int NamedRefusals = 12;

    /// <summary>How often the lock is checked again (a scene brings its own EventSystem, the game re-enables maps).</summary>
    private const double SweepSeconds = 0.5;

    private static readonly List<ControllerMap> DisabledMaps = new();
    private static readonly List<BaseInputModule> DisabledModules = new();

    // The two devices whose UI maps the lock switches off.
    private static readonly ControllerType[] LockedDevices = { ControllerType.Keyboard, ControllerType.Mouse };

    // What's in the two lists, by native object: the game turning a map back on (a menu opening) doesn't add it twice.
    private static readonly HashSet<IntPtr> Disabled = new();
    private static Il2CppSystem.Object? _token;
    private static Il2CppSystem.Collections.Generic.List<ControllerMap>? _maps; // filled by Rewired at each sweep (kept: twice a second for a session)
    private static NavigationController? _navigation;
    private static int _lifted;
    private static double _nextSweep;
    private static int _refusals;
    private static int _errors;
    private static int _uiCategory = -1;

    /// <summary>True while this PC follows a host: its own UI input is off and the guards refuse.</summary>
    public static bool Locked { get; private set; }

    /// <summary>True inside <see cref="Apply"/>: the guards let the mirror's own calls through.</summary>
    public static bool Lifted => _lifted > 0;

    /// <summary>True when a guard refuses: locked, and not inside <see cref="Apply"/>.</summary>
    internal static bool Refusing => Locked && _lifted == 0;

    /// <summary>
    /// Runs <paramref name="action"/> with the guards lifted (nested calls add up): for the mirror's own selection
    /// changes, clicks shown for feedback, and menu calls that the guards would refuse. Exceptions reach the caller.
    /// </summary>
    public static void Apply(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        _lifted++;
        try
        {
            action();
        }
        finally
        {
            _lifted--;
        }
    }

    /// <summary>
    /// Selects <paramref name="target"/> (<c>SetCurrentNavigable(target, true, true)</c>) with the guards lifted, as
    /// <see cref="Apply"/> would, without a delegate: for the mirror's cursors, held several times a second. Exceptions
    /// reach the caller.
    /// </summary>
    public static void Select(NavigationController navigation, AbstractNavigable target)
    {
        ArgumentNullException.ThrowIfNull(navigation);
        ArgumentNullException.ThrowIfNull(target);
        _lifted++;
        try
        {
            navigation.SetCurrentNavigable(target, true, true);
        }
        finally
        {
            _lifted--;
        }
    }

    /// <summary>Patches the guards (they refuse only while <see cref="Locked"/>). Adds a line to <paramref name="failed"/> per patch that couldn't go in.</summary>
    internal static void Install(HarmonyLib.Harmony harmony, List<string> failed)
    {
        ScenePatches.Prefix(harmony, failed, typeof(AbstractNavigable), nameof(AbstractNavigable.ForceClick), typeof(FriendUi), nameof(ForceClickPrefix));
        ScenePatches.Prefix(harmony, failed, typeof(UIInputShortcutBase), nameof(UIInputShortcutBase.FireEvent), typeof(FriendUi), nameof(FireEventPrefix));
        ScenePatches.Prefix(harmony, failed, typeof(NavigationController), nameof(NavigationController.OnBackInput), typeof(FriendUi), nameof(BackInputPrefix));
        ScenePatches.Prefix(harmony, failed, typeof(ScrollNonUI), nameof(ScrollNonUI.OnMouseDown), typeof(FriendUi), nameof(MapDragPrefix));
        ScenePatches.Prefix(harmony, failed, typeof(NavigationController), nameof(NavigationController.SetCurrentNavigable), typeof(FriendUi), nameof(SelectionPrefix));
        ScenePatches.Prefix(harmony, failed, typeof(Il2CppUI.InGameMenu), nameof(Il2CppUI.InGameMenu.OpenControllerAssignment), typeof(FriendUi), nameof(OwnControllerScreenPrefix));
        ScenePatches.Prefix(harmony, failed, typeof(Il2CppUI.InGameMenu), nameof(Il2CppUI.InGameMenu.PausePressed), typeof(FriendUi), nameof(OwnPausePrefix));
    }

    /// <summary>This PC started following a host: its own UI input goes off.</summary>
    internal static void Lock(string why)
    {
        if (Locked)
        {
            return;
        }

        Locked = true;
        _refusals = 0;
        _nextSweep = 0;
        _panChecked = false;
        ModLog.Info($"Friend UI: locked ({why}). This game's own keyboard, mouse and menu input does nothing until the host's game is over here; the host's screens are mirrored.");
        Sweep();
    }

    /// <summary>This PC stopped following: everything the lock switched off is put back.</summary>
    internal static void Unlock(string why)
    {
        if (!Locked)
        {
            return;
        }

        Locked = false;
        ForgetOwnMenu();
        int maps = 0;
        int modules = 0;
        Guard("putting the navigation back", () =>
        {
            if (_navigation != null && _token != null)
            {
                _navigation.RemoveNavigationActiveState(_token);
            }
        });
        Guard("putting the keyboard and mouse maps back", () =>
        {
            foreach (ControllerMap map in DisabledMaps)
            {
                if (map != null && !map.WasCollected && !map.enabled)
                {
                    map.enabled = true;
                    maps++;
                }
            }
        });
        Guard("putting the pointer input back", () =>
        {
            foreach (BaseInputModule module in DisabledModules)
            {
                if (module != null && !module.WasCollected && !module.enabled)
                {
                    module.enabled = true;
                    modules++;
                }
            }
        });
        DisabledMaps.Clear();
        DisabledModules.Clear();
        Disabled.Clear();
        Guard("forgetting the maps it read", () => _maps?.Clear());
        _navigation = null;
        ModLog.Info($"Friend UI: unlocked ({why}): navigation on, {maps} keyboard/mouse map(s) and {modules} pointer input module(s) back on; {_refusals} local input(s) were refused.");
    }

    /// <summary>Every frame while following: the lock is checked again twice a second.</summary>
    internal static void Update()
    {
        if (!Locked || Suspended || Streaming.FrameClock.Seconds < _nextSweep)
        {
            return;
        }

        _nextSweep = Streaming.FrameClock.Seconds + SweepSeconds;
        Sweep();
    }

    private static void Sweep()
    {
        Guard("switching the navigation off", () =>
        {
            NavigationController? navigation = WorldMap.Navigation;
            if (navigation == null || (_navigation != null && _navigation.Pointer == navigation.Pointer))
            {
                return;
            }

            _token ??= new Il2CppSystem.Object();
            navigation.AddNavigationActiveState(_token, false, NavigationPriority);
            _navigation = navigation;
        });
        Guard("switching the keyboard and mouse UI maps off", DisableMaps);
        CheckPanMaps();
        Guard("switching the pointer input off", () =>
        {
            BaseInputModule? module = EventSystem.current?.currentInputModule;
            if (module != null && module.enabled)
            {
                module.enabled = false;
                if (Disabled.Add(module.Pointer))
                {
                    DisabledModules.Add(module);
                }
            }
        });
    }

    private static void DisableMaps()
    {
        if (!ReInput.isReady)
        {
            return;
        }

        if (_uiCategory < 0)
        {
            _uiCategory = ReInput.mapping.GetMapCategoryId("UI");
            if (_uiCategory < 0)
            {
                _uiCategory = int.MaxValue; // no such category in this build: nothing to switch off (logged once)
                ModLog.Warn("Friend UI: Rewired has no \"UI\" map category here; the keyboard and mouse UI maps stay on (the other guards still hold).");
            }
        }

        if (_uiCategory == int.MaxValue)
        {
            return;
        }

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
                    if (map != null && map.enabled && map.categoryId == _uiCategory)
                    {
                        map.enabled = false;
                        if (Disabled.Add(map.Pointer))
                        {
                            DisabledMaps.Add(map);
                        }
                    }
                }
            }
        }
    }

    /// <summary>A click's actions (<c>OnClick</c>) on a following friend's PC.</summary>
    private static bool ForceClickPrefix(AbstractNavigable __instance)
    {
        if (!Refusing || OwnMenuAllows(__instance))
        {
            return true;
        }

        Refused(() => $"a click on {__instance?.name ?? "a navigable"}");
        return false;
    }

    /// <summary>A shortcut's event (start, skip, reroll, lineup...) on a following friend's PC.</summary>
    private static bool FireEventPrefix(UIInputShortcutBase __instance)
    {
        if (!Refusing || OwnMenuAllows(__instance))
        {
            return true;
        }

        Refused(() => $"the shortcut {__instance?.name ?? "?"}");
        return false;
    }

    /// <summary>Back input to every back handler (the menus' <c>IBackHandler.HandleBackInput</c>) on a following friend's PC.</summary>
    private static bool BackInputPrefix()
    {
        if (!Refusing || OwnMenuAllowsBack())
        {
            return true;
        }

        Refused(() => "a back input");
        return false;
    }

    /// <summary>A mouse drag of the campaign map (Unity's <c>OnMouseDown</c>) on a following friend's PC: its camera follows the host's.</summary>
    private static bool MapDragPrefix()
    {
        if (!Refusing)
        {
            return true;
        }

        Refused(() => "a drag of the map");
        return false;
    }

    /// <summary>
    /// The game's own controller screen in a match (<c>InGameMenu.OpenControllerAssignment</c>, what its folded
    /// <c>OnPlayerRemoved</c> and <c>OnPlayerAdded</c> run) on a following friend's PC: one of this PC's own game players went or
    /// came (a pad lent to their own menu, or one the session doesn't hold, unplugged; its removal is the game's). The menu it
    /// opens could never be left: its icons' changes, clicks and back are all refused while following, so it would stay over
    /// the host's match. It's never opened instead; the friend's player on the host is the session's business (D22).
    /// </summary>
    private static bool OwnControllerScreenPrefix()
    {
        if (!Refusing)
        {
            return true;
        }

        Refused(() => "this game's own controller screen (one of its own players went or came)");
        return false;
    }

    /// <summary>This game's own pause (<c>InGameMenu.PausePressed</c>, any Rewired player's Pause) on a following friend's PC: a menu that could never be left, as above.</summary>
    private static bool OwnPausePrefix()
    {
        if (!Refusing)
        {
            return true;
        }

        Refused(() => "this game's own pause menu");
        return false;
    }

    private static void Refused(Func<string> what)
    {
        try
        {
            int count = ++_refusals;
            if (count <= NamedRefusals)
            {
                ModLog.Info($"Friend UI: refused {what()} (this game follows {LogNames.HostTag()}; its own input does nothing){(count == NamedRefusals ? "; further refusals are only counted" : string.Empty)}.");
            }
        }
        catch (Exception ex)
        {
            Failed("naming a refused input", ex);
        }
    }

    private static void Guard(string what, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Failed(what, ex);
        }
    }

    private static void Failed(string what, Exception ex)
    {
        if (_errors++ < 5)
        {
            ModLog.Error($"Friend UI: failed while {what}", ex);
        }
    }
}
#endif
