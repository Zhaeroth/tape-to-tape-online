using System;
using System.Collections.Generic;
using System.Globalization;
using T2TOnline.Core.Input;

namespace T2TOnline.Core.Lobby;

/// <summary>An action a key makes on the keyboard walk: the game's Rewired action, the pole of an axis action, and the keys a person reaches for first.</summary>
/// <param name="ActionId">The game's Rewired action id.</param>
/// <param name="Negative">True for the negative pole of an axis action (down, left).</param>
/// <param name="What">The action in words, for the log ("UI Confirm").</param>
/// <param name="Preferred">Unity key codes tried first, in order (the arrows, Enter, Esc); any other plain key bound to the action after them.</param>
public sealed record KeyWalkAction(int ActionId, bool Negative, string What, IReadOnlyList<int> Preferred);

/// <summary>
/// The keyboard walk's rules (the check tour's <c>kbwalk</c>): which key of a keyboard's own maps makes the action a pad's button makes, so a tour drives the
/// game's screens with the keys a keyboard player has, and the line each walked row gets. The action ids and key codes are the game's (Rewired's action list,
/// checked against the running game; Unity's <c>KeyCode</c>).
/// </summary>
public static class KeyWalk
{
    /// <summary>Rewired's "UI Confirm".</summary>
    public const int UiConfirm = 24;

    /// <summary>Rewired's "UI Move Vertical" (up is its positive pole).</summary>
    public const int UiMoveVertical = 25;

    /// <summary>Rewired's "UI Move Horizontal" (right is its positive pole).</summary>
    public const int UiMoveHorizontal = 26;

    /// <summary>Rewired's "UI Back".</summary>
    public const int UiBack = 27;

    /// <summary>Rewired's "Activate": the join at press-to-start (<c>GameJoin</c>'s action).</summary>
    public const int Activate = 31;

    /// <summary>Rewired's "UI Tab Left".</summary>
    public const int UiTabLeft = 37;

    /// <summary>Rewired's "UI Tab Right".</summary>
    public const int UiTabRight = 38;

    /// <summary>Rewired's "UI Special".</summary>
    public const int UiSpecial = 39;

    /// <summary>Rewired's "UI Special 2".</summary>
    public const int UiSpecial2 = 49;

    /// <summary>Rewired's "UI Left Trigger".</summary>
    public const int UiLeftTrigger = 51;

    /// <summary>Rewired's "UI Right Trigger".</summary>
    public const int UiRightTrigger = 52;

    /// <summary>Rewired's "UI Start".</summary>
    public const int UiStart = 73;

    /// <summary>Unity's <c>KeyCode.Return</c>.</summary>
    public const int KeyReturn = 13;

    /// <summary>Unity's <c>KeyCode.Escape</c>.</summary>
    public const int KeyEscape = 27;

    /// <summary>Unity's <c>KeyCode.Space</c>.</summary>
    public const int KeySpace = 32;

    /// <summary>Unity's <c>KeyCode.KeypadEnter</c>.</summary>
    public const int KeyKeypadEnter = 271;

    /// <summary>Unity's <c>KeyCode.UpArrow</c>.</summary>
    public const int KeyUp = 273;

    /// <summary>Unity's <c>KeyCode.DownArrow</c>.</summary>
    public const int KeyDown = 274;

    /// <summary>Unity's <c>KeyCode.RightArrow</c>.</summary>
    public const int KeyRight = 275;

    /// <summary>Unity's <c>KeyCode.LeftArrow</c>.</summary>
    public const int KeyLeft = 276;

    /// <summary>What a field of the walk's line says when the walk doesn't press that there.</summary>
    public const string NotPressed = "-";

    private static readonly int[] None = Array.Empty<int>();

    /// <summary>The join at press-to-start: Enter first, as a keyboard player presses it.</summary>
    public static KeyWalkAction Join { get; } = new(Activate, false, "Activate", new[] { KeyReturn, KeySpace, KeyKeypadEnter });

    /// <summary>The action the pad's <paramref name="button"/> makes in the game's menus, or null for a button the walk has no key for.</summary>
    public static KeyWalkAction? For(PadButtons button) => button switch
    {
        PadButtons.A => new KeyWalkAction(UiConfirm, false, "UI Confirm", new[] { KeyReturn, KeyKeypadEnter, KeySpace }),
        PadButtons.B => new KeyWalkAction(UiBack, false, "UI Back", new[] { KeyEscape }),
        PadButtons.DPadUp => new KeyWalkAction(UiMoveVertical, false, "UI Move Vertical, up", new[] { KeyUp }),
        PadButtons.DPadDown => new KeyWalkAction(UiMoveVertical, true, "UI Move Vertical, down", new[] { KeyDown }),
        PadButtons.DPadRight => new KeyWalkAction(UiMoveHorizontal, false, "UI Move Horizontal, right", new[] { KeyRight }),
        PadButtons.DPadLeft => new KeyWalkAction(UiMoveHorizontal, true, "UI Move Horizontal, left", new[] { KeyLeft }),
        PadButtons.LeftBumper => new KeyWalkAction(UiTabLeft, false, "UI Tab Left", None),
        PadButtons.RightBumper => new KeyWalkAction(UiTabRight, false, "UI Tab Right", None),
        PadButtons.X => new KeyWalkAction(UiSpecial, false, "UI Special", None), // the game's pad map: X makes "UI Special", Y "UI Special 2" (TourShortcuts)
        PadButtons.Y => new KeyWalkAction(UiSpecial2, false, "UI Special 2", None),
        PadButtons.Start => new KeyWalkAction(UiStart, false, "UI Start", None),
        _ => null,
    };

    /// <summary>The action a trigger makes in the game's menus (the right one: "UI Right Trigger").</summary>
    public static KeyWalkAction ForTrigger(bool right) => right
        ? new KeyWalkAction(UiRightTrigger, false, "UI Right Trigger", None)
        : new KeyWalkAction(UiLeftTrigger, false, "UI Left Trigger", None);

    /// <summary>
    /// The key-table element of <paramref name="controls"/> that makes <paramref name="action"/> in map category <paramref name="category"/> (any category when
    /// negative): a plain key (no modifier, no mouse button) bound to the action's pole, the preferred keys first in their order, then the lowest key code. -1
    /// when no plain key makes it. Never throws on what the set holds.
    /// </summary>
    public static int Pick(InputBindingSet controls, int category, KeyWalkAction action)
    {
        ArgumentNullException.ThrowIfNull(controls);
        ArgumentNullException.ThrowIfNull(action);
        int best = -1;
        int bestRank = int.MaxValue;
        foreach (InputBinding binding in controls.Bindings)
        {
            if (binding.ActionId != action.ActionId || binding.Negative != action.Negative || (category >= 0 && binding.Category != category)
                || binding.Element.Kind != BindingElementKind.Key || binding.Element.Modifiers != KeyModifiers.None)
            {
                continue;
            }

            int element = controls.IndexOf(binding.Element);
            if (element < 0 || element >= KeyboardMouseState.MaxElements)
            {
                continue;
            }

            int preferred = IndexOf(action.Preferred, binding.Element.Code);
            int rank = preferred >= 0 ? preferred : action.Preferred.Count + binding.Element.Code;
            if (rank < bestRank)
            {
                bestRank = rank;
                best = element;
            }
        }

        return best;
    }

    /// <summary>A key's name for the log: the walk's own keys by name ("Enter", "Up"), a letter or digit as itself, any other by its Unity code.</summary>
    public static string KeyName(int keyCode) => keyCode switch
    {
        KeyReturn => "Enter",
        KeyEscape => "Esc",
        KeySpace => "Space",
        KeyKeypadEnter => "Keypad Enter",
        KeyUp => "Up",
        KeyDown => "Down",
        KeyRight => "Right",
        KeyLeft => "Left",
        >= 'a' and <= 'z' => ((char)(keyCode - 'a' + 'A')).ToString(),
        >= '0' and <= '9' => ((char)keyCode).ToString(),
        _ => string.Create(CultureInfo.InvariantCulture, $"key {keyCode}"),
    };

    /// <summary>
    /// The walk's line for one row: <c>&lt;screen&gt; &lt;row&gt;: arrows &lt;r&gt;, Enter &lt;r&gt;, Esc &lt;r&gt;, glyph &lt;name&gt;; mouse hit &lt;r&gt;, click &lt;r&gt;</c>,
    /// each result "ok", <see cref="NotPressed"/> or what went wrong. Names and results are cut to 60 characters each.
    /// </summary>
    public static string Line(string screen, string row, string arrows, string enter, string escape, string glyph, string hit, string click) =>
        $"kbwalk: {Cut(screen)} {Cut(row)}: arrows {Cut(arrows)}, Enter {Cut(enter)}, Esc {Cut(escape)}, glyph {Cut(glyph)}; mouse hit {Cut(hit)}, click {Cut(click)}";

    private static string Cut(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return NotPressed;
        }

        return text.Length <= 60 ? text : text[..57] + "...";
    }

    private static int IndexOf(IReadOnlyList<int> list, int value)
    {
        for (int i = 0; i < list.Count; i++)
        {
            if (list[i] == value)
            {
                return i;
            }
        }

        return -1;
    }
}
