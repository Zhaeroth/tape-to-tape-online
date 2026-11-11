#if HAS_INTEROP
using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Il2CppRewired;
using T2TOnline.Plugin.Diagnostics;
using T2TOnline.Plugin.Hooks;
using UnityEngine;

namespace T2TOnline.Plugin.Online.Panel;

/// <summary>
/// Keeps the game from reacting to this PC's keyboard and mouse while the mod's panel (F1) has them, and gives them back
/// after. The game reads them three ways, and each is held:
/// <list type="bullet">
/// <item>Rewired (play, and the game's menus through its own navigation): the keyboard and mouse controllers are switched
/// off (<c>Controller.enabled</c>). Pads stay on, so friends' virtual pads and this PC's own pad still play.</item>
/// <item>Unity's UI event system (clicks and hovers on the game's buttons and map nodes): <c>EventSystem.Update</c> is
/// skipped, so no input module runs (Rewired's own event system, <c>RewiredEventSystem.Update</c>, runs it as its base).</item>
/// <item>Unity's mouse messages to scene objects (<c>OnMouseDown</c>, which drags the campaign map):
/// <c>SendMouseEvents.DoSendMouseEvents</c> is skipped.</item>
/// </list>
/// The cursor is shown and unlocked while held, and put back after. The game hides and locks it whenever a button on one
/// of this PC's pads is pressed (<c>HideCursor.OnGamePadPerformed</c>; a locked cursor jumps to the window's centre), so
/// that is skipped while held too: a host with a pad in hand would otherwise lose the mouse at every press. <see cref="Release"/>
/// undoes exactly what <see cref="Hold"/> did and never throws; the panel calls it when it closes, after any error of its
/// own, and when the mod shuts down.
/// </summary>
internal static class PanelInput
{
    private static readonly (string Type, string Method, string Why)[] Skipped =
    {
        ("UnityEngine.EventSystems.EventSystem", "Update", "the game's UI event system (clicks and hovers on its menus)"),
        ("UnityEngine.SendMouseEvents", "DoSendMouseEvents", "mouse messages to scene objects (dragging the campaign map)"),
        ("HideCursor", "OnGamePadPerformed", "the game hiding and locking the cursor at a pad press"),
    };

    private static bool _keyboardOff;
    private static bool _mouseOff;
    private static bool _cursorSaved;
    private static bool _savedCursorVisible;
    private static CursorLockMode _savedLockState;
    private static bool _keysUnreadable;
    private static IReadOnlyList<string>? _installed;

    /// <summary>True while the panel holds the keyboard and mouse.</summary>
    public static bool Held { get; private set; }

    /// <summary>
    /// Patches the game's UI event system and mouse messages so they can be held. Returns what couldn't be patched
    /// (logged): the panel still works without them, but the game's menus under it may then react to its clicks. Once
    /// only: a second panel (after the runtime stopped) gets the first one's result, and nothing is patched twice.
    /// </summary>
    public static IReadOnlyList<string> Install(HarmonyLib.Harmony harmony)
    {
        ArgumentNullException.ThrowIfNull(harmony);
        if (_installed != null)
        {
            return _installed;
        }

        var skip = new HarmonyMethod(typeof(PanelInput).GetMethod(nameof(SkipWhileHeld), BindingFlags.NonPublic | BindingFlags.Static));
        var failed = new List<string>();
        _installed = failed;
        foreach ((string typeName, string methodName, string why) in Skipped)
        {
            try
            {
                MethodInfo? target = Il2CppTypeLocator.Find(typeName) is { } type ? AccessTools.Method(type, methodName) : null;
                if (target == null)
                {
                    failed.Add(why);
                    ModLog.Warn($"Panel: {typeName}.{methodName} not found, so {why} can't be held while the panel is open.");
                    continue;
                }

                harmony.Patch(target, prefix: skip);
            }
            catch (Exception ex)
            {
                failed.Add(why);
                ModLog.Error($"Panel: {typeName}.{methodName} couldn't be patched, so {why} can't be held while the panel is open", ex);
            }
        }

        return failed;
    }

    /// <summary>
    /// Takes the keyboard and mouse from the game and frees the cursor. False (logged, and anything already taken given
    /// back) when it couldn't.
    /// </summary>
    public static bool Hold()
    {
        if (Held)
        {
            return true;
        }

        Held = true;
        try
        {
            if (ReInput.isReady)
            {
                Keyboard? keyboard = ReInput.controllers.Keyboard;
                if (keyboard != null && keyboard.enabled)
                {
                    keyboard.enabled = false;
                    _keyboardOff = true;
                }

                Il2CppRewired.Mouse? mouse = ReInput.controllers.Mouse;
                if (mouse != null && mouse.enabled)
                {
                    mouse.enabled = false;
                    _mouseOff = true;
                }
            }

            _savedCursorVisible = Cursor.visible;
            _savedLockState = Cursor.lockState;
            _cursorSaved = true;
            KeepCursorFree();
            return true;
        }
        catch (Exception ex)
        {
            ModLog.Error("Panel: couldn't take the keyboard and mouse from the game; giving back what was taken", ex);
            Release();
            return false;
        }
    }

    /// <summary>Call while held, from OnGUI (after the game's own updates): the game may hide or lock the cursor each frame.</summary>
    public static void KeepCursorFree()
    {
        if (!Cursor.visible)
        {
            Cursor.visible = true;
        }

        if (Cursor.lockState != CursorLockMode.None)
        {
            Cursor.lockState = CursorLockMode.None;
        }
    }

    /// <summary>
    /// True while Esc, F1 or a mouse button is still down: given the keyboard and mouse back then, the game would see that
    /// key or button pressed (Rewired counts a key already down on a controller switched back on as a new press).
    /// False when the keys can't be read, so nothing waits on them.
    /// </summary>
    public static bool ClosingKeysDown()
    {
        if (_keysUnreadable)
        {
            return false;
        }

        try
        {
            return UnityEngine.Input.GetKey(KeyCode.Escape) || UnityEngine.Input.GetKey(KeyCode.F1)
                || UnityEngine.Input.GetMouseButton(0) || UnityEngine.Input.GetMouseButton(1) || UnityEngine.Input.GetMouseButton(2);
        }
        catch (Exception ex)
        {
            _keysUnreadable = true;
            ModLog.Warn($"Panel: Unity's key state can't be read ({ModLog.SafeErrorText(ex)}); the keyboard goes back to the game as soon as the panel closes.");
            return false;
        }
    }

    /// <summary>Gives the keyboard and mouse back to the game and puts the cursor back. Safe to call any time; never throws.</summary>
    public static void Release()
    {
        // First: the game's UI event system and mouse messages run again whatever happens below.
        Held = false;
        if (_keyboardOff)
        {
            _keyboardOff = false;
            Step("keyboard", () =>
            {
                if (ReInput.isReady && ReInput.controllers.Keyboard is { } keyboard)
                {
                    keyboard.enabled = true;
                }
            });
        }

        if (_mouseOff)
        {
            _mouseOff = false;
            Step("mouse", () =>
            {
                if (ReInput.isReady && ReInput.controllers.Mouse is { } mouse)
                {
                    mouse.enabled = true;
                }
            });
        }

        if (_cursorSaved)
        {
            _cursorSaved = false;
            Step("cursor", () =>
            {
                Cursor.lockState = _savedLockState;
                Cursor.visible = _savedCursorVisible;
            });
        }
    }

    /// <summary>One part of <see cref="Release"/>: a failure is logged and the other parts still run.</summary>
    private static void Step(string what, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            ModLog.Error($"Panel: giving the {what} back to the game failed", ex);
        }
    }

    /// <summary>Skips the patched method while the panel holds the keyboard and mouse.</summary>
    private static bool SkipWhileHeld() => !Held;
}
#endif
