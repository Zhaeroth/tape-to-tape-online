#if HAS_INTEROP
using System;
using Il2CppBishopGames.UI;
using T2TOnline.Plugin.Diagnostics;
using UnityEngine;

namespace T2TOnline.Plugin.Campaign.Scenes;

// The lock, lifted in part for a friend's own menu while they follow (Online/OnlineSession.Remap.cs): their own game's Settings
// menu, or its remapper over it, gets their input; the host's mirrored screens under it stay deaf. Apart from the lock itself
// (FriendUi.cs).
internal static partial class FriendUi
{
    private static Func<Component?, bool>? _ownMenuOwns;
    private static Func<bool>? _ownMenuTakesBack;
    private static Func<Component, bool>? _ownMenuKeepsCursor;
    private static string _ownMenu = string.Empty;
    private static bool _navigationLifted;
    private static int _ownMenuClicks;
    private static int _cursorMovesHeld;

    /// <summary>True while the lock is lifted in part for the friend's own menu (<see cref="SuspendForOwnMenu"/>).</summary>
    public static bool Suspended { get; private set; }

    /// <summary>
    /// The friend opened their own game's menu (<paramref name="what"/>, for the log) while following: what the lock switched
    /// off for input is put back (the EventSystem's input module, the keyboard and mouse UI maps), and its sweep pauses, so
    /// the remapper (Unity UI, not the game's navigation) takes their device, mouse and keys. With
    /// <paramref name="navigation"/> (the whole Settings menu, which the friend navigates) the navigation-off request goes
    /// too. The backstops stay, but let through what <paramref name="owns"/> says is the menu's own (a click or shortcut on
    /// it) and a Back while <paramref name="takesBack"/>: the host's mirrored screens under it get nothing. With navigation
    /// on, the host's cursor mirrored under it doesn't take the friend's while <paramref name="keepsCursor"/> says so. Nothing
    /// happens unless locked. Never throws.
    /// </summary>
    internal static void SuspendForOwnMenu(string what, bool navigation, Func<Component?, bool> owns, Func<bool> takesBack, Func<Component, bool> keepsCursor)
    {
        ArgumentNullException.ThrowIfNull(owns);
        ArgumentNullException.ThrowIfNull(takesBack);
        ArgumentNullException.ThrowIfNull(keepsCursor);
        if (!Locked || Suspended)
        {
            return;
        }

        Suspended = true;
        _ownMenu = what;
        _ownMenuOwns = owns;
        _ownMenuTakesBack = takesBack;
        _ownMenuKeepsCursor = keepsCursor;
        _ownMenuClicks = 0;
        _cursorMovesHeld = 0;
        int maps = 0;
        int modules = 0;
        Guard("putting the keyboard and mouse maps back for your own menu", () =>
        {
            foreach (Il2CppRewired.ControllerMap map in DisabledMaps)
            {
                if (map != null && !map.WasCollected && !map.enabled)
                {
                    map.enabled = true;
                    maps++;
                }
            }
        });
        Guard("putting the pointer input back for your own menu", () =>
        {
            foreach (UnityEngine.EventSystems.BaseInputModule module in DisabledModules)
            {
                if (module != null && !module.WasCollected && !module.enabled)
                {
                    module.enabled = true;
                    modules++;
                }
            }
        });
        _navigationLifted = false;
        if (navigation)
        {
            Guard("putting the navigation back for your own menu", () =>
            {
                if (_navigation != null && _token != null && !_navigation.WasCollected)
                {
                    _navigation.RemoveNavigationActiveState(_token);
                    _navigationLifted = true;
                }

                _navigation = null; // the sweep asks again at the resume
            });
        }

        ModLog.Info($"Friend UI: lifted in part for {what}: {maps} keyboard/mouse map(s) and {modules} pointer input module(s) back on" +
            (navigation ? $", navigation {(_navigationLifted ? "on" : "as it was")}" : ", navigation still off (the remapper is Unity UI)") +
            "; only that menu's own buttons and back answer, the host's screens under it stay mirrored.");
    }

    /// <summary>The friend's own menu closed: the lock is whole again (the next sweep switches off what was put back). Never throws.</summary>
    internal static void ResumeAfterOwnMenu(string why)
    {
        if (!Suspended)
        {
            return;
        }

        string what = _ownMenu;
        int clicks = _ownMenuClicks;
        ForgetOwnMenu();
        if (Locked)
        {
            _nextSweep = 0;
            Sweep();
            ModLog.Info($"Friend UI: whole again after {what} ({why}); {clicks} of its own click(s) and back(s) let through" +
                (_cursorMovesHeld > 0 ? $", {_cursorMovesHeld} of the host's mirrored cursor moves held back while it was on top." : "."));
        }
    }

    /// <summary>Forgets the friend's own menu (the lock is whole again, or gone).</summary>
    private static void ForgetOwnMenu()
    {
        Suspended = false;
        _ownMenu = string.Empty;
        _ownMenuOwns = null;
        _ownMenuTakesBack = null;
        _ownMenuKeepsCursor = null;
        _navigationLifted = false;
    }

    /// <summary>
    /// The one cursor moves (<c>NavigationController.SetCurrentNavigable</c>): while the friend navigates their own menu, the
    /// mirror's copy of the host's cursor on a screen under it (FriendMenus, the map's, the title's) is held back, or a pad's
    /// next move would start from there. Does nothing else, and costs a bool, the rest of the time. Never throws.
    /// </summary>
    private static bool SelectionPrefix(AbstractNavigable navigable)
    {
        if (!_navigationLifted || _ownMenuKeepsCursor == null || navigable == null)
        {
            return true;
        }

        try
        {
            if (_ownMenuKeepsCursor(navigable))
            {
                _cursorMovesHeld++;
                return false;
            }
        }
        catch (Exception ex)
        {
            Failed("keeping the cursor on your own menu", ex);
        }

        return true;
    }

    /// <summary>True when the lock lets <paramref name="target"/>'s click or shortcut through: it's on the friend's own menu. Never throws.</summary>
    private static bool OwnMenuAllows(Component? target)
    {
        if (!Suspended || _ownMenuOwns == null)
        {
            return false;
        }

        try
        {
            bool owns = _ownMenuOwns(target);
            _ownMenuClicks += owns ? 1 : 0;
            return owns;
        }
        catch (Exception ex)
        {
            Failed("checking a click on your own menu", ex);
            return false;
        }
    }

    /// <summary>True when the lock lets a Back through: the friend's own menu takes it. Never throws.</summary>
    private static bool OwnMenuAllowsBack()
    {
        if (!Suspended || _ownMenuTakesBack == null)
        {
            return false;
        }

        try
        {
            bool takes = _ownMenuTakesBack();
            _ownMenuClicks += takes ? 1 : 0;
            return takes;
        }
        catch (Exception ex)
        {
            Failed("checking a back on your own menu", ex);
            return false;
        }
    }
}
#endif
