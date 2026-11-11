#if HAS_INTEROP
using System;
using System.Collections.Generic;
using Il2CppBishopGames.Catalogs;
using Il2CppBishopGames.UI;
using Il2CppInterop.Runtime;
using Il2CppTape2Tape.Customization.UI;
using Il2CppTMPro;
using T2TOnline.Core.Lobby;
using T2TOnline.Plugin.Diagnostics;
using UnityEngine;
using UnityEngine.UI;

namespace T2TOnline.Plugin.NativeLobby.Widgets;

/// <summary>
/// The bottom bar, from <see cref="LobbyRules.Prompts"/>: the game's own <c>UIInputShortcut_V2</c> with its glyph (device and remaps follow, the prompt pin too) and its label, nothing from raw
/// buttons, nothing greyed: a prompt that isn't listed isn't there, and its button does nothing. Back is the bar's own <c>Btn-Back</c>; each other button a clone of it, with the select sound in
/// place of the back sound. Confirm's press comes through the navigation (the focused button's click), so its prompt has no sound of its own and its press does nothing here.
/// <para>
/// A mouse clicks every prompt (<see cref="PointerClicks"/>, in the game's mouse mode, while nothing covers the shell): a click fires the prompt's own shortcut, its sound
/// and its listeners as a press does; Back's closes a shell that closes by the back stack (<see cref="LobbyShell.BackClicked"/>); Confirm's, which has no listener, is told
/// as A: the focused button of the shell is clicked (its sound, its handlers), or with none the screen is told a press of that prompt.
/// </para>
/// </summary>
internal sealed class PromptBar
{
    private const float BarHeight = 48;

    private static readonly HashSet<string> ShortcutKeep = new(UiClone.Widgets, StringComparer.Ordinal);
    private static Il2CppSystem.Action<UIInputShortcutBase>? _triggered;
    private static bool _clickFailedSaid;

    // Which bar a shortcut belongs to, by the native object: the delegate is one, the shortcuts many.
    private static readonly Dictionary<IntPtr, (PromptBar Bar, LobbyButton Button)> Owners = new();

    private readonly RectTransform _bar;
    private readonly Dictionary<LobbyButton, Slot> _slots = new();
    private readonly List<LobbyPrompt> _shown = new();
    private readonly Action<LobbyPrompt> _pressed;
    private readonly RectTransform _back;
    private bool _awake;

    /// <summary>
    /// The bar over the shell's own, its Back the shell's; <paramref name="pressed"/> is told each press of a listed prompt but Back and Confirm, and each mouse click of
    /// Confirm's when nothing of the shell is focused (the screen does what A on its row does).
    /// </summary>
    public PromptBar(ShellParts parts, Action<LobbyPrompt> pressed)
    {
        _bar = parts.BottomBar;
        _pressed = pressed;
        _back = parts.BackShortcut.transform.parent.GetComponent<RectTransform>() ?? throw new InvalidOperationException("the bar's Back has no rect");
        TMP_Text? backLabel = _back.Find("Label")?.GetComponent<TMP_Text>();
        Il2CppI2.Loc.Localize? backTerm = backLabel == null ? null : backLabel.GetComponent<Il2CppI2.Loc.Localize>();
        if (backTerm != null)
        {
            UiClone.Destroy(backTerm);
        }

        _slots[LobbyButton.Back] = new Slot(_back, parts.BackShortcut, backLabel);
        Clickable(LobbyButton.Back, _slots[LobbyButton.Back]);
    }

    /// <summary>The prompts drawn now.</summary>
    public IReadOnlyList<LobbyPrompt> Shown => _shown;

    /// <summary>The shell has woken: every prompt's shortcut can take its action now (<c>RefreshInput</c> needs its input module).</summary>
    public void Wake()
    {
        _awake = true;
        foreach (KeyValuePair<LobbyButton, Slot> slot in _slots)
        {
            Bind(slot.Key, slot.Value);
        }
    }

    /// <summary>
    /// Draws <paramref name="prompts"/> right aligned, in order, with Back last as the game's bars have it. <paramref name="others"/>: buttons a widget on screen answers and shows a glyph for: not drawn
    /// here, and not live here. L and R for one action are drawn as one prompt with two glyphs. A screen calls it on a change of focus or state.
    /// </summary>
    public void Show(IReadOnlyList<LobbyPrompt> prompts, params LobbyButton[] others)
    {
        _shown.Clear();
        var live = new HashSet<LobbyButton>();
        int order = 0;
        foreach (LobbyPrompt prompt in prompts)
        {
            if (Array.IndexOf(others, prompt.Button) >= 0)
            {
                continue;
            }

            live.Add(prompt.Button);
            _shown.Add(prompt);
            Slot slot = SlotOf(prompt.Button);
            bool paired = prompt.Button == LobbyButton.TabLeft && Lists(prompts, others, LobbyButton.TabRight);
            LobbyUi.Show(slot.Label, !paired);
            if (!paired)
            {
                LobbyUi.Say(slot.Label, prompt.Term);
            }

            if (paired)
            {
                Pair(slot, SlotOf(LobbyButton.TabRight));
            }
            else
            {
                Unpair(slot);
                slot.Root.SetSiblingIndex(order++);
            }
        }

        foreach (KeyValuePair<LobbyButton, Slot> slot in _slots)
        {
            LobbyUi.Show(slot.Value.Root, live.Contains(slot.Key));
        }

        UiClone.Rebuild(_bar);
    }

    private static bool Lists(IReadOnlyList<LobbyPrompt> prompts, LobbyButton[] others, LobbyButton button)
    {
        if (Array.IndexOf(others, button) >= 0)
        {
            return false;
        }

        foreach (LobbyPrompt prompt in prompts)
        {
            if (prompt.Button == button)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// LB's glyph inside RB's prompt, before its glyph: "[LB][RB] Game Mode", one prompt as the game's own pairs read (its tab bars put LB and RB together round what they
    /// change), not a lone glyph a bar's gap away from the next. Each glyph keeps its own shortcut and click. The prompt's own layout spaces them as a glyph from its label.
    /// </summary>
    private static void Pair(Slot left, Slot right)
    {
        if (!IsParent(right.Root, left.Root))
        {
            left.Root.SetParent(right.Root, false);
        }

        if (left.Root.GetSiblingIndex() != 0)
        {
            left.Root.SetSiblingIndex(0);
        }
    }

    /// <summary>A glyph shown alone goes back to the bar.</summary>
    private void Unpair(Slot slot)
    {
        if (!IsParent(_bar, slot.Root))
        {
            slot.Root.SetParent(_bar, false);
        }
    }

    /// <summary>True when <paramref name="parent"/> holds <paramref name="child"/> directly, by their native objects.</summary>
    private static bool IsParent(Transform parent, Transform child)
    {
        Transform? holder = child.parent;
        return holder is not null && holder.Pointer == parent.Pointer;
    }

    /// <summary>The prompt the given button does now, or false.</summary>
    public bool TryFind(LobbyButton button, out LobbyPrompt prompt)
    {
        foreach (LobbyPrompt shown in _shown)
        {
            if (shown.Button == button)
            {
                prompt = shown;
                return true;
            }
        }

        prompt = default;
        return false;
    }

    /// <summary>The tour: a listed prompt's shortcut fires, by the game's own trigger. False when it isn't listed.</summary>
    public bool TriggerForCheck(LobbyButton button)
    {
        if (!TryFind(button, out _) || !_slots.TryGetValue(button, out Slot? slot))
        {
            return false;
        }

        slot.Shortcut.Trigger();
        return true;
    }

    /// <summary>The tour: the game's own press of a listed prompt, as its button's would be (<see cref="ReleaseForCheck"/> lets go). False when it isn't listed.</summary>
    public bool PressForCheck(LobbyButton button)
    {
        if (!TryFind(button, out _) || !_slots.TryGetValue(button, out Slot? slot))
        {
            return false;
        }

        slot.Shortcut.Press();
        return true;
    }

    /// <summary>The tour: lets go of a press made by <see cref="PressForCheck"/>.</summary>
    public void ReleaseForCheck(LobbyButton button)
    {
        if (_slots.TryGetValue(button, out Slot? slot))
        {
            slot.Shortcut.Release(false);
        }
    }

    /// <summary>The game's action name of a prompt's button.</summary>
    public static string ActionOf(LobbyButton button) => button switch
    {
        LobbyButton.Confirm => "UI_UIConfirm",
        LobbyButton.Back => "UI_UIBack",
        LobbyButton.Special => "UI_UISpecial",
        LobbyButton.Special2 => "UI_UISpecial2",
        LobbyButton.TabLeft => "UI_UITabLeft",
        LobbyButton.TabRight => "UI_UITabRight",
        LobbyButton.Start => "UI_UIStart",
        LobbyButton.LeftTrigger => "UI_UILeftTrigger",
        _ => "UI_UIRightTrigger",
    };

    private Slot SlotOf(LobbyButton button)
    {
        if (_slots.TryGetValue(button, out Slot? known))
        {
            return known;
        }

        // Its sounds come from the bar's other shortcut, the select sound's: Back's would be wrong on anything else.
        GameObject clone = LobbyUi.Part(LobbyUi.Glossary, "ScaledContainer/BottomBar/InputShortcutWithLabel", _bar, ShortcutKeep, $"Prompt{button}");
        Transform shortcutAt = clone.transform.Find("UIInputShortcut_V2") ?? throw new InvalidOperationException("a prompt has no shortcut");
        UIInputShortcutBase shortcut = shortcutAt.GetComponent<UIInputShortcutBase>() ?? throw new InvalidOperationException("a prompt's shortcut has no script");

        // The source is a hold (0.4 s, looping). With the loop left on, a hold time of 0 fires every frame the button is down: Press leaves it to OnUpdate (verified in the game's code).
        shortcut.m_RequiredHoldTime = 0;
        shortcut.m_AllowHoldLoop = false;
        if (button == LobbyButton.Confirm)
        {
            UiClone.Destroy(shortcutAt.GetComponent<InputShortcutSFXFMOD>());
        }

        TMP_Text? label = clone.transform.Find("Label")?.GetComponent<TMP_Text>();
        if (label != null)
        {
            Il2CppI2.Loc.Localize? localize = label.GetComponent<Il2CppI2.Loc.Localize>();
            if (localize != null)
            {
                UiClone.Destroy(localize);
            }
        }

        // The same size as Back's: the source comes bigger.
        LayoutElement? size = clone.GetComponent<LayoutElement>();
        if (size != null)
        {
            size.preferredHeight = BarHeight;
        }

        // Clickable while the clone still sleeps (the source's shortcut is off until it's used): the game's mouse button refreshes its colours as it wakes, and
        // reads its lists then, which must be there (UnselectableButtonBase.BehaviourEnable; verified in the game's code). Back's slot is made asleep too.
        var slot = new Slot(LobbyUi.RectOf(clone), shortcut, label);
        _slots[button] = slot;
        Clickable(button, slot);
        clone.SetActive(true);
        if (_awake)
        {
            Bind(button, slot);
        }

        return slot;
    }

    private void Bind(LobbyButton button, Slot slot)
    {
        try
        {
            if (button != LobbyButton.Back)
            {
                slot.Shortcut.RefreshInput(new InputActionsCatalogReference(ActionOf(button)));
            }

            _triggered ??= DelegateSupport.ConvertDelegate<Il2CppSystem.Action<UIInputShortcutBase>>(new Action<UIInputShortcutBase>(OnTriggered))
                ?? throw new InvalidOperationException("the prompt's delegate couldn't be made");
            if (slot.Bound)
            {
                return;
            }

            slot.Bound = true;
            Owners[slot.Shortcut.Pointer] = (this, button);
            if (button is not (LobbyButton.Back or LobbyButton.Confirm))
            {
                slot.Shortcut.OnInputShortcutTriggered.Register(_triggered, "T2TOnline");
            }
        }
        catch (Exception ex)
        {
            ModLog.Warn($"Lobby: the {button} prompt couldn't be bound ({ModLog.SafeErrorText(ex)}).");
        }
    }

    private static void OnTriggered(UIInputShortcutBase shortcut)
    {
        try
        {
            if (shortcut != null && Owners.TryGetValue(shortcut.Pointer, out (PromptBar Bar, LobbyButton Button) owner) && owner.Bar.TryFind(owner.Button, out LobbyPrompt prompt))
            {
                owner.Bar._pressed(prompt);
            }
        }
        catch (Exception ex)
        {
            ModLog.Warn($"Lobby: a prompt's press failed ({ModLog.SafeErrorText(ex)}).");
        }
    }

    /// <summary>A prompt clickable by mouse. A failure leaves it to its button, logged once a game.</summary>
    private void Clickable(LobbyButton button, Slot slot)
    {
        try
        {
            slot.Click = PointerClicks.Make(slot.Root.gameObject, slot.Label, () => Clicked(button)).Pointer;
        }
        catch (Exception ex)
        {
            if (!_clickFailedSaid)
            {
                _clickFailedSaid = true;
                ModLog.Warn($"Lobby: the bar's prompts can't be clicked ({ModLog.SafeErrorText(ex)}); their buttons work.");
            }
        }
    }

    /// <summary>A prompt clicked: what its button would do, if it's listed now and nothing covers the shell.</summary>
    private void Clicked(LobbyButton button)
    {
        if (!_awake || !LobbyShell.TakesInput || !TryFind(button, out LobbyPrompt prompt) || !_slots.TryGetValue(button, out Slot? slot))
        {
            return;
        }

        switch (button)
        {
            case LobbyButton.Back:
                slot.Shortcut.Trigger();
                LobbyShell.BackClicked();
                break;
            case LobbyButton.Confirm:
                // What A does: the focused button's own click (its sound, its handlers). With nothing of the shell focused, the screen acts on its row.
                AbstractNavigable? focused = Campaign.Menus.MenuGame.Module()?.NavigationController?.CurrentNavigable;
                Transform? shell = LobbyShell.Menu?.transform;
                if (focused != null && !focused.WasCollected && shell != null && focused.isActiveAndEnabled && focused.transform.IsChildOf(shell))
                {
                    focused.Click();
                }
                else
                {
                    _pressed(prompt);
                }

                break;
            default:
                slot.Shortcut.Trigger();
                break;
        }
    }

    /// <summary>Forgets this bar: its shell has gone.</summary>
    public void Forget()
    {
        foreach (Slot slot in _slots.Values)
        {
            Owners.Remove(slot.ShortcutPointer);
            PointerClicks.Forget(slot.Click);
        }

        _slots.Clear();
    }

    private sealed class Slot
    {
        public Slot(RectTransform root, UIInputShortcutBase shortcut, TMP_Text? label)
        {
            Root = root;
            Shortcut = shortcut;
            ShortcutPointer = shortcut.Pointer;
            Label = label;
            if (label != null)
            {
                label.enableAutoSizing = true;
                label.fontSizeMin = 26;
                label.fontSizeMax = 32;
                label.fontSize = 32;
            }
        }

        public RectTransform Root { get; }

        public UIInputShortcutBase Shortcut { get; }

        public IntPtr ShortcutPointer { get; }

        public TMP_Text? Label { get; }

        public bool Bound { get; set; }

        public IntPtr Click { get; set; }
    }
}
#endif
