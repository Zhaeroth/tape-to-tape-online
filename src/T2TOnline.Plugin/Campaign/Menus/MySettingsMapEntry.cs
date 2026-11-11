#if HAS_INTEROP
using System;
using Il2CppTape2Tape.Customization.UI;
using Il2CppTape2Tape.Hockey.UI;
using T2TOnline.Plugin.Diagnostics;
using UnityEngine;

namespace T2TOnline.Plugin.Campaign.Menus;

/// <summary>
/// A following friend's way to their own Settings on the world map: the map's own prompt bar's "[Start] Menu" entry
/// (<c>WorldMapMenusHandler.m_WorldMapMenuInput</c>, in the scene <c>Canvas/Buttons/BG/Menu/UIInputShortcut_V2</c>, action <c>Default_Pause</c>;
/// verified in the game's scene data). On a couch it opens the map's menu, whose Settings row is the next press; on a following friend it
/// does nothing today (their device is on none of their game's players, and <c>MapGuards</c> refuses the open), so there it opens their own
/// Settings, through the session (Online/OnlineSession.MySettings.cs). Nothing is added to the bar: it's full in most languages. The entry is
/// read, never changed; its own press (which would open this game's map menu) is never called: a press plays only its animation and sound.
/// The bar hides itself while a map sub-menu is open (<c>WorldMapInputBar.CheckState</c>). Nothing here throws.
/// </summary>
internal static class MySettingsMapEntry
{
    /// <summary>The entry's action, as the scene sets it: a different one means the game changed it, and the entry isn't used.</summary>
    private const string Action = "Default_Pause";

    private static UIInputShortcutBase? _entry;
    private static RectTransform? _rect;
    private static bool _refusedSaid;
    private static int _errors;

    /// <summary>
    /// The map bar's Menu entry while it's on screen (the map's handler up, its bar shown), or null. Read each frame on a following friend:
    /// the handler's static, then the entry's active state.
    /// </summary>
    public static UIInputShortcutBase? Shown()
    {
        try
        {
            WorldMapMenusHandler? handler = WorldMapMenusHandler.s_Instance;
            if (handler == null || handler.WasCollected)
            {
                return null;
            }

            UIInputShortcutBase? entry = handler.m_WorldMapMenuInput;
            if (entry == null || entry.WasCollected || !entry.gameObject.activeInHierarchy)
            {
                return null;
            }

            if (_entry == null || _entry.WasCollected || _entry.Pointer != entry.Pointer)
            {
                // A new map scene: its entry checked once.
                string? action = entry.Input?.Name;
                if (!string.Equals(action, Action, StringComparison.Ordinal))
                {
                    if (!_refusedSaid)
                    {
                        _refusedSaid = true;
                        ModLog.Warn($"Own menu: the map's Menu prompt isn't the game's {Action} here (it's {action ?? "none"}); it doesn't open your own Settings.");
                    }

                    return null;
                }

                _entry = entry;
                _rect = entry.transform.parent?.GetComponent<RectTransform>(); // the entry: its glyph and its label
            }

            return entry;
        }
        catch (Exception ex)
        {
            if (_errors++ < 3)
            {
                ModLog.Warn($"Own menu: the map's Menu prompt couldn't be read ({ModLog.SafeErrorText(ex)}).");
            }

            return null;
        }
    }

    /// <summary>True when <paramref name="point"/> (screen pixels) is on the entry (its glyph or label). The map's canvas is a screen-space overlay (no camera).</summary>
    public static bool Over(Vector2 point)
    {
        try
        {
            RectTransform? rect = _rect;
            return rect != null && !rect.WasCollected && _entry != null && !_entry.WasCollected && _entry.gameObject.activeInHierarchy &&
                RectTransformUtility.RectangleContainsScreenPoint(rect, point);
        }
        catch (Exception ex)
        {
            if (_errors++ < 3)
            {
                ModLog.Warn($"Own menu: a click on the map's Menu prompt couldn't be read ({ModLog.SafeErrorText(ex)}).");
            }

            return false;
        }
    }

    /// <summary>
    /// The entry's own press, seen and heard: its animation (<c>UIInputShortcut.InputShortcutTriggered</c>: the animator's trigger and its
    /// serialized event, which has no calls in the scene) and its own trigger sound, in the game's sound queue. Not its press event, whose
    /// handler opens this game's map menu. A failure is logged; the press still counts.
    /// </summary>
    public static void PlayPress()
    {
        try
        {
            UIInputShortcutBase? entry = _entry;
            if (entry == null || entry.WasCollected)
            {
                return;
            }

            entry.TryCast<UIInputShortcut>()?.InputShortcutTriggered();
            InputShortcutSFXFMOD? sound = entry.GetComponent<InputShortcutSFXFMOD>();
            if (sound != null)
            {
                NavigableSFXFMOD.PlayInGlobalQueue(sound.m_TriggerEventFMOD, NavigableSFXFMOD.EEventType.Click, true, null!, Vector3.zero);
            }
        }
        catch (Exception ex)
        {
            ModLog.Warn($"Own menu: the map's Menu prompt's press couldn't play ({ModLog.SafeErrorText(ex)}); your own Settings open all the same.");
        }
    }

    /// <summary>The session's friend side ended: forgotten (the entry belongs to the scene).</summary>
    public static void Forget()
    {
        _entry = null;
        _rect = null;
        _refusedSaid = false;
    }
}
#endif
