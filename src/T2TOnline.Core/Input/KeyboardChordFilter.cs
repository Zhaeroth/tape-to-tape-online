using System;
using System.Collections.Generic;
using System.Numerics;

namespace T2TOnline.Core.Input;

/// <summary>
/// Leaves out a keyboard friend's key presses made while Alt or the Windows key is held:
/// Alt+Tab would otherwise send Tab ("Change Line" in a match) to the host just before the window loses focus,
/// where the couch game pauses instead. A key that goes down while either is held counts neither as a press nor as held
/// until it's let go; a key held before stays held, and a binding that asks for Alt or the Windows key itself is never
/// left out (its chord is the modifier). Over the friend's key table: element <c>i</c> at bit <c>i</c>.
/// </summary>
public sealed class KeyboardChordFilter
{
    private ulong _leftOut;

    /// <summary>Presses left out so far.</summary>
    public long Filtered { get; private set; }

    /// <summary>
    /// True from <see cref="Reset"/> until Alt and the Windows key have been seen up: the release of the Alt that took the
    /// window away (Alt+Tab) went to the other window, so whether the game's key state ever heard it isn't known (the
    /// code alone can't tell), and a stuck Alt would leave out every press from then on. Ask for the
    /// modifiers while this is true, even with nothing going down.
    /// </summary>
    public bool ModifiersUnproven { get; private set; }

    /// <summary>
    /// The key table's elements as they go to the host: <paramref name="held"/> and <paramref name="pressed"/> (gone down
    /// since the last call) with the keys pressed while <paramref name="altOrWindowsHeld"/> left out until they're let go.
    /// Only the elements in <paramref name="filterable"/> are ever left out (the keys whose binding needs neither Alt nor the
    /// Windows key; never mouse buttons).
    /// </summary>
    public (ulong Held, ulong Pressed) Apply(ulong held, ulong pressed, bool altOrWindowsHeld, ulong filterable)
    {
        if (ModifiersUnproven)
        {
            ModifiersUnproven = altOrWindowsHeld;
            altOrWindowsHeld = false;
        }

        if (altOrWindowsHeld)
        {
            ulong caught = pressed & filterable & ~_leftOut;
            Filtered += BitOperations.PopCount(caught);
            _leftOut |= pressed & filterable;
        }

        (ulong outHeld, ulong outPressed) = (held & ~_leftOut, pressed & ~_leftOut);

        // A key let go is free again (one pressed and let go within this call is left out once, then forgotten).
        _leftOut &= held;
        return (outHeld, outPressed);
    }

    /// <summary>Forgets the keys being left out (the keyboard went off, so every key counts as let go); Alt and the Windows key count as up until they've been seen up (<see cref="ModifiersUnproven"/>).</summary>
    public void Reset()
    {
        _leftOut = 0;
        ModifiersUnproven = true;
    }

    /// <summary>
    /// True when <paramref name="bindings"/> play with one of <paramref name="keyCodes"/> outside the menus (any map category but
    /// <paramref name="uiCategory"/>). A friend who remapped Shoot to Left Alt holds it while pressing other keys: those are no
    /// Alt+Tab, so that modifier mustn't leave them out. The game's own maps bind Left Alt in the UI category only, and a
    /// menu's trigger isn't held while playing.
    /// </summary>
    public static bool PlaysWith(IEnumerable<InputBinding> bindings, int uiCategory, params int[] keyCodes)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        ArgumentNullException.ThrowIfNull(keyCodes);
        foreach (InputBinding binding in bindings)
        {
            if (binding.Category != uiCategory && binding.Element.Kind == BindingElementKind.Key && Array.IndexOf(keyCodes, binding.Element.Code) >= 0)
            {
                return true;
            }
        }

        return false;
    }
}
