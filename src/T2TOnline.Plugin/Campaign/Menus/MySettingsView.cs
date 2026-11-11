#if HAS_INTEROP
using System;
using Il2CppBishopGames.Catalogs;
using Il2CppTape2Tape.Customization.UI;
using Il2CppTape2Tape.Hockey.UI;
using Il2CppTMPro;
using T2TOnline.Core.Lobby;
using T2TOnline.Plugin.Campaign.Scenes;
using T2TOnline.Plugin.Diagnostics;
using T2TOnline.Plugin.NativeLobby;
using UnityEngine;

namespace T2TOnline.Plugin.Campaign.Menus;

/// <summary>
/// A following friend's "[RT] My Settings" on the host's pause menu as their PC draws it (<see cref="GameMenuViews.PauseShown"/>): a copy of
/// that menu's own bottom shortcut, its Edit Lineup (<c>ScaledContainer/UIInputShortcut_EditLineup</c>, which the game shows only at the end
/// of a period, so its place is empty in the pause context; verified in the game's code), at the same place, with the game's
/// <c>UI_UIRightTrigger</c> for its glyph (the friend's own device and binding: the prompt pin) and the mod's word for its label. The copy
/// is a child of the menu: it goes with it, and with its canvas under a host overlay. Its own input never acts (the friend's device is on
/// none of this game's players while following, and the friend UI lock refuses shortcuts): the session reads the friend's press
/// (Online/OnlineSession.MySettings.cs) and plays the shortcut's own press here (<see cref="PlayPress"/>). One copy per menu instance;
/// a failure turns it off for the session, said once (the map's Menu prompt, which needs no copy, goes on). Nothing here throws.
/// </summary>
internal static class MySettingsView
{
    /// <summary>The copy's object name.</summary>
    public const string CloneName = "UIInputShortcut_MySettings";

    private const string PartsSource = "GameMenu";
    private const string SourcePath = "ScaledContainer/UIInputShortcut_EditLineup";
    private const string LabelPath = "Text (TMP)";
    private const string GlyphPath = "Text (TMP)/Container";
    private const string Action = "UI_UIRightTrigger";

    private static IntPtr _menuPointer;
    private static GameObject? _clone;
    private static UIInputShortcutBase? _shortcut;
    private static RectTransform? _label;
    private static RectTransform? _glyph;
    private static bool _woken;

    // The copy was last left showing here: while it wasn't, hiding it has nothing to do and asks the game nothing (every frame with no pause menu).
    private static bool _shown;

    /// <summary>Why the shortcut is off for this session (null while it isn't).</summary>
    public static string? OffReason { get; private set; }

    /// <summary>
    /// Every frame on a following friend: the copy on <paramref name="menu"/> (the host's pause menu drawn here; null: none), shown when
    /// <paramref name="show"/>. True when it's on screen and can be pressed. False, with <see cref="OffReason"/> set, once it has failed.
    /// </summary>
    public static bool Update(GameMenu? menu, bool show)
    {
        if (OffReason != null)
        {
            return false;
        }

        try
        {
            if (menu is null)
            {
                Hide(); // the session gives a menu that is alive, or none: no call into the game to tell
                return false;
            }

            if (menu.Pointer != _menuPointer || _clone == null || _clone.WasCollected)
            {
                if (!show)
                {
                    Hide();
                    return false; // made only once it's to show
                }

                Attach(menu);
            }

            GameObject clone = _clone!;
            if (clone.activeSelf != show)
            {
                clone.SetActive(show);
            }

            _shown = show;

            if (show && !_woken)
            {
                // Awake now: the action again through the game's own refresh (it registers its input and draws the glyph), as the
                // lobby's prompts do once their shell has woken.
                _woken = true;
                _shortcut!.RefreshInput(new InputActionsCatalogReference(Action));
                LobbyLocalization.Apply(_label!.GetComponent<TMP_Text>(), LobbyWords.MySettings.Key);
            }

            return show && clone.activeInHierarchy;
        }
        catch (Exception ex)
        {
            TurnOff($"showing it: {ModLog.SafeErrorText(ex)}");
            return false;
        }
    }

    /// <summary>True when <paramref name="point"/> (screen pixels) is on the shortcut's label or glyph. The menu's canvas is a screen-space overlay (no camera).</summary>
    public static bool Over(Vector2 point)
    {
        try
        {
            return OffReason == null && _clone != null && !_clone.WasCollected && _clone.activeInHierarchy &&
                ((_label != null && RectTransformUtility.RectangleContainsScreenPoint(_label, point)) ||
                 (_glyph != null && RectTransformUtility.RectangleContainsScreenPoint(_glyph, point)));
        }
        catch (Exception ex)
        {
            TurnOff($"reading a click: {ModLog.SafeErrorText(ex)}");
            return false;
        }
    }

    /// <summary>The shortcut's own press (its animation and press event), with the lock lifted for it. A failure is logged; the press still counts.</summary>
    public static void PlayPress()
    {
        try
        {
            UIInputShortcutBase? shortcut = _shortcut;
            if (shortcut != null && !shortcut.WasCollected && _clone != null && !_clone.WasCollected && _clone.activeInHierarchy)
            {
                FriendUi.Apply(shortcut.Trigger);
            }
        }
        catch (Exception ex)
        {
            ModLog.Warn($"Own menu: My Settings' press couldn't play on the shortcut ({ModLog.SafeErrorText(ex)}); your own Settings open all the same.");
        }
    }

    /// <summary>The session's friend side ended: the copy hidden if it's still up, and forgotten; a failure's switch-off goes too.</summary>
    public static void Forget()
    {
        Hide();
        _menuPointer = IntPtr.Zero;
        _clone = null;
        _shortcut = null;
        _label = null;
        _glyph = null;
        _woken = false;
        OffReason = null;
    }

    private static void Hide()
    {
        if (!_shown)
        {
            return;
        }

        _shown = false;
        if (_clone != null && !_clone.WasCollected && _clone.activeSelf)
        {
            _clone.SetActive(false);
        }
    }

    /// <summary>The copy made on <paramref name="menu"/> (or one a retained instance carries already), asleep until it's shown. Throws on failure (the caller turns it off).</summary>
    private static void Attach(GameMenu menu)
    {
        Hide();
        _clone = null;
        _shortcut = null;
        _woken = false;
        _menuPointer = menu.Pointer;

        UIInputShortcutBase source = menu.m_EditLineupButton ?? throw new InvalidOperationException("the pause menu has no Edit Lineup shortcut");
        Transform parent = source.transform.parent ?? throw new InvalidOperationException("the Edit Lineup shortcut has no parent");
        Transform? kept = parent.Find(CloneName);
        GameObject clone;
        if (kept != null)
        {
            clone = kept.gameObject; // a retained instance shown again: its copy from before
            _woken = clone.activeSelf;
            _shown = _woken;
        }
        else
        {
            string? missing = UiAssets.CheckParts(PartsSource, menu.gameObject);
            if (missing != null)
            {
                throw new InvalidOperationException(missing);
            }

            clone = UiClone.Make(source.gameObject, UiClone.Widgets, "the pause menu's Edit Lineup", out string problem)
                ?? throw new InvalidOperationException(problem);
            clone.name = CloneName;

            // Its action is set once it's awake, through the game's own RefreshInput (Update): the field holds a struct with a string, which
            // the game writes with the collector's barrier and the interop's field setter would copy without one. Asleep until then.
            clone.SetActive(false);
            UiClone.Place(clone, parent, source.transform.GetSiblingIndex() + 1);
            ModLog.Info($"Own menu: My Settings is made on this PC's copy of the host's pause menu (its Edit Lineup shortcut's place: {SourcePath}).");
        }

        _clone = clone;
        _shortcut = clone.GetComponent<UIInputShortcutBase>() ?? throw new InvalidOperationException("the copy has no shortcut");
        _label = clone.transform.Find(LabelPath)?.GetComponent<RectTransform>() ?? throw new InvalidOperationException("the copy has no label");
        _glyph = clone.transform.Find(GlyphPath)?.GetComponent<RectTransform>();
    }

    private static void TurnOff(string why)
    {
        OffReason = why;
        ModLog.Warn($"Own menu: My Settings couldn't be put on the host's pause menu ({why}); it's off there for this session (the map's Menu prompt still opens your own Settings).");
        try
        {
            Hide();
        }
        catch (Exception ex)
        {
            ModLog.Warn($"Own menu: hiding My Settings failed too ({ModLog.SafeErrorText(ex)}).");
        }
    }
}
#endif
