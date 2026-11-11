#if HAS_INTEROP
using System;
using System.Collections.Generic;
using Il2CppBishopGames.UI;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppTMPro;
using T2TOnline.Plugin.Diagnostics;
using UnityEngine;

namespace T2TOnline.Plugin.NativeLobby.Widgets;

/// <summary>
/// Mouse clicks on what a pad doesn't select (a bar's prompt, a tab's glyph), with the game's own mouse-only button, <c>UnselectableColorButton</c>, as its own tabs
/// and arrows use its kin: the game's pointer handlers, a click counted only in the game's mouse mode (<c>UnselectableButtonBase.OnPointerClick</c> calls <c>Click</c>
/// only in <c>NavigationMode.Mouse</c>; verified in the game's code), a label that turns the game's pink under the pointer. One delegate for every such button, told
/// apart by the native object: the game holds the delegate, and these keep it alive.
/// </summary>
internal static class PointerClicks
{
    private static readonly Dictionary<IntPtr, Action> Handlers = new();
    private static readonly Color Down = LobbyUi.Hex(0xA6A6A6);
    private static Il2CppSystem.Action<UnselectableButtonBase>? _clicked;

    /// <summary>
    /// Makes <paramref name="root"/> clickable by mouse: <paramref name="action"/> on a click; <paramref name="label"/>, when given, shows the pointer over it. Its images and
    /// texts below are what the pointer hits. The button is added while <paramref name="root"/> is asleep: the game's button draws its colours as it is enabled
    /// (<c>UnselectableButtonBase.BehaviourEnable</c> ends in <c>UnselectableColorButton.Refresh</c>, which reads both lists; verified in the game's code), and on an awake
    /// object that is inside <c>AddComponent</c> itself, before the lists can be given: the game's refresh then throws on the missing lists. So an awake root sleeps while
    /// the button is added and its lists are set, and wakes again with them (a bar's prompts sleep and wake the same way each time the bar hides and shows them). Throws
    /// with what's wrong.
    /// </summary>
    public static UnselectableButtonBase Make(GameObject root, TMP_Text? label, Action action)
    {
        ArgumentNullException.ThrowIfNull(root);
        UnselectableColorButton? button = root.GetComponent<UnselectableColorButton>();
        bool wake = button == null && root.activeInHierarchy;
        if (wake)
        {
            root.SetActive(false);
        }

        try
        {
            button ??= root.AddComponent(Il2CppType.Of<UnselectableColorButton>())?.TryCast<UnselectableColorButton>()
                ?? throw new InvalidOperationException($"{root.name} can't take a mouse button");
            button.m_ImagesData = new Il2CppReferenceArray<UnselectableColorButton.ImageData>(0);
            TextMeshProUGUI? text = label == null ? null : label.TryCast<TextMeshProUGUI>();
            var texts = new Il2CppReferenceArray<UnselectableColorButton.TextData>(text == null ? 0 : 1);
            if (text != null)
            {
                text.raycastTarget = true;
                var data = new UnselectableColorButton.TextData
                {
                    TextComponent = text,
                    NormalColor = LobbyUi.White,
                    HighlightColor = LobbyUi.Pink,
                    DownColor = Down,
                    DisabledColor = LobbyUi.Disabled,
                };

                // A fresh object held only by this fresh array, which was made first (a collection may have marked it since):
                // stored through the write barrier (GameArrays), so the incremental collector can't miss it.
                Campaign.Menus.GameArrays.Store(texts, 0, data.Pointer);
                GC.KeepAlive(data);
            }

            button.m_TextsData = texts;
            Bind(button, action);
            return button;
        }
        finally
        {
            if (wake)
            {
                root.SetActive(true);
            }
        }
    }

    /// <summary>What a click on <paramref name="button"/> does (a game's own unselectable button too: a tab). Once per button: the game's event keeps the registration.</summary>
    public static void Bind(UnselectableButtonBase button, Action action)
    {
        ArgumentNullException.ThrowIfNull(button);
        _clicked ??= DelegateSupport.ConvertDelegate<Il2CppSystem.Action<UnselectableButtonBase>>(new Action<UnselectableButtonBase>(OnClicked))
            ?? throw new InvalidOperationException("the mouse click delegate couldn't be made");
        if (Handlers.TryAdd(button.Pointer, action))
        {
            (button.OnClick ?? throw new InvalidOperationException("the button has no click event")).Register(_clicked, "T2TOnline");
        }
        else
        {
            Handlers[button.Pointer] = action;
        }
    }

    /// <summary>Forgets a button that went (by its native object, which may be gone already).</summary>
    public static void Forget(IntPtr button) => Handlers.Remove(button);

    private static void OnClicked(UnselectableButtonBase button)
    {
        try
        {
            if (button != null && Handlers.TryGetValue(button.Pointer, out Action? action))
            {
                action();
            }
        }
        catch (Exception ex)
        {
            ModLog.Warn($"Lobby: a mouse click failed ({ModLog.SafeErrorText(ex)}).");
        }
    }
}
#endif
