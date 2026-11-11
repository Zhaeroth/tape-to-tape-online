using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace T2TOnline.Core.Input;

/// <summary>
/// The game's four fight verbs a player presses in a brawl: "Fight Block High", "Fight Block Low", "Fight Jab" and "Fight
/// Uppercut". The game reads them in its forward controls (the Default map category, which stays on through a brawl): a
/// block counts while its button is held, a punch at its press.
/// </summary>
[Flags]
public enum FightActions : byte
{
    /// <summary>None.</summary>
    None = 0,

    /// <summary>Block high (the jab's block on the game's defaults: RB on a pad, LeftShift on a keyboard).</summary>
    BlockHigh = 1 << 0,

    /// <summary>Block low (the uppercut's block on the game's defaults: LB on a pad, LeftControl on a keyboard).</summary>
    BlockLow = 1 << 1,

    /// <summary>Jab (X on a pad, Space on a keyboard, by default).</summary>
    Jab = 1 << 2,

    /// <summary>Uppercut (B on a pad, DownArrow on a keyboard, by default).</summary>
    Uppercut = 1 << 3,

    /// <summary>Either block.</summary>
    Blocks = BlockHigh | BlockLow,

    /// <summary>All four.</summary>
    All = BlockHigh | BlockLow | Jab | Uppercut,
}

/// <summary>
/// Where the fight verbs are in this build's Rewired data: each action's id (-1 when the build has none) and the map
/// category the game plays them in (its forward controls'), read by name in the plugin.
/// </summary>
/// <param name="BlockHigh">"Fight Block High"'s action id.</param>
/// <param name="BlockLow">"Fight Block Low"'s action id.</param>
/// <param name="Jab">"Fight Jab"'s action id.</param>
/// <param name="Uppercut">"Fight Uppercut"'s action id.</param>
/// <param name="Category">The Default map category's id.</param>
public readonly record struct FightActionIds(int BlockHigh, int BlockLow, int Jab, int Uppercut, int Category)
{
    /// <summary>The fight verb an action id is, or <see cref="FightActions.None"/>.</summary>
    public FightActions Of(int actionId) =>
        actionId < 0 ? FightActions.None
        : actionId == BlockHigh ? FightActions.BlockHigh
        : actionId == BlockLow ? FightActions.BlockLow
        : actionId == Jab ? FightActions.Jab
        : actionId == Uppercut ? FightActions.Uppercut
        : FightActions.None;
}

/// <summary>
/// One player's fight verbs on their own device: which pad buttons, pad axes or key-table elements each verb is bound to
/// (their controls, or the game's gamepad template when they play on its defaults), so a played frame can say which verbs
/// it holds. Built once per brawl; reading a frame allocates nothing. An axis or trigger counts past the game's button
/// dead zone (0.5), as the game reads it.
/// </summary>
public sealed class FightInputMap
{
    /// <summary>How far an axis must go to hold a verb (the game's input behaviours' button dead zone).</summary>
    public const float AxisThreshold = 0.5f;

    private static readonly FightActions[] Verbs = { FightActions.BlockHigh, FightActions.BlockLow, FightActions.Jab, FightActions.Uppercut };

    private readonly PadButtons[] _buttons = new PadButtons[Verbs.Length];
    private readonly ulong[] _keys = new ulong[Verbs.Length];
    private readonly List<(PadAxis Axis, bool Negative)>[] _axes = { new(), new(), new(), new() };
    private readonly List<BindingElement>[] _elements = { new(), new(), new(), new() };

    private FightInputMap()
    {
    }

    /// <summary>
    /// The map of <paramref name="bindings"/> (in the category <paramref name="ids"/> names) onto the fight verbs. Pad
    /// buttons and axes map directly; keys and mouse buttons by their index in <paramref name="keyTable"/>'s elements (a
    /// keyboard friend's frames hold those bits), and are left out without one. Invalid elements are left out.
    /// </summary>
    public static FightInputMap Build(IEnumerable<InputBinding> bindings, FightActionIds ids, InputBindingSet? keyTable)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        var map = new FightInputMap();
        foreach (InputBinding binding in bindings)
        {
            int verb = IndexOf(ids.Of(binding.ActionId));
            if (verb < 0 || binding.Category != ids.Category || !binding.Element.IsValid)
            {
                continue;
            }

            BindingElement element = binding.Element;
            switch (element.Kind)
            {
                case BindingElementKind.PadButton:
                    map._buttons[verb] |= (PadButtons)(1 << element.Code);
                    break;
                case BindingElementKind.PadAxis:
                    map._axes[verb].Add(((PadAxis)element.Code, binding.Negative || binding.Range == BindingRange.Negative));
                    break;
                default:
                    int index = keyTable?.IndexOf(element) ?? -1;
                    if (index is < 0 or >= 64)
                    {
                        continue;
                    }

                    map._keys[verb] |= 1UL << index;
                    break;
            }

            if (!map._elements[verb].Contains(element))
            {
                map._elements[verb].Add(element);
            }
        }

        return map;
    }

    /// <summary>The verbs that have no element at all (unbound on this device).</summary>
    public FightActions Unbound
    {
        get
        {
            var unbound = FightActions.None;
            for (int i = 0; i < Verbs.Length; i++)
            {
                unbound |= _elements[i].Count == 0 ? Verbs[i] : FightActions.None;
            }

            return unbound;
        }
    }

    /// <summary>The verbs a pad frame holds (a tap, pressed and let go since the last frame, counts as held).</summary>
    public FightActions Held(in PadState state, PadButtons taps)
    {
        PadButtons down = state.Buttons | taps;
        var held = FightActions.None;
        for (int i = 0; i < Verbs.Length; i++)
        {
            if ((down & _buttons[i]) != 0 || AxisHolds(i, state))
            {
                held |= Verbs[i];
            }
        }

        return held;
    }

    /// <summary>The verbs a pad frame's taps press (let go and pressed again between two frames).</summary>
    public FightActions Tapped(PadButtons taps)
    {
        var tapped = FightActions.None;
        for (int i = 0; i < Verbs.Length; i++)
        {
            tapped |= (taps & _buttons[i]) != 0 ? Verbs[i] : FightActions.None;
        }

        return tapped;
    }

    /// <summary>The verbs a keyboard frame holds (bits of the key table held, or tapped since the last frame).</summary>
    public FightActions Held(ulong keysHeld, ulong keyTaps)
    {
        ulong down = keysHeld | keyTaps;
        var held = FightActions.None;
        for (int i = 0; i < Verbs.Length; i++)
        {
            held |= (down & _keys[i]) != 0 ? Verbs[i] : FightActions.None;
        }

        return held;
    }

    /// <summary>The verbs a keyboard frame's taps press.</summary>
    public FightActions Tapped(ulong keyTaps)
    {
        var tapped = FightActions.None;
        for (int i = 0; i < Verbs.Length; i++)
        {
            tapped |= (keyTaps & _keys[i]) != 0 ? Verbs[i] : FightActions.None;
        }

        return tapped;
    }

    /// <summary>
    /// Where <paramref name="verb"/> (one verb) is on this device, for the log: its elements by <paramref name="name"/>,
    /// joined with "or" ("RightBumper", "LeftShift or E"); "nothing" when it's unbound.
    /// </summary>
    public string Where(FightActions verb, Func<BindingElement, string> name)
    {
        ArgumentNullException.ThrowIfNull(name);
        int index = IndexOf(verb);
        return index < 0 || _elements[index].Count == 0 ? "nothing" : string.Join(" or ", _elements[index].Select(name));
    }

    /// <summary>The verb's index in the map, or -1 for anything but a single verb.</summary>
    internal static int IndexOf(FightActions verb) => verb switch
    {
        FightActions.BlockHigh => 0,
        FightActions.BlockLow => 1,
        FightActions.Jab => 2,
        FightActions.Uppercut => 3,
        _ => -1,
    };

    /// <summary>A verb's name for the log: "block high", "block low", "jab", "uppercut".</summary>
    public static string NameOf(FightActions verb) => verb switch
    {
        FightActions.BlockHigh => "block high",
        FightActions.BlockLow => "block low",
        FightActions.Jab => "jab",
        FightActions.Uppercut => "uppercut",
        _ => ((byte)verb).ToString(CultureInfo.InvariantCulture),
    };

    private bool AxisHolds(int verb, in PadState state)
    {
        List<(PadAxis Axis, bool Negative)> axes = _axes[verb];
        for (int i = 0; i < axes.Count; i++)
        {
            float value = state.GetAxis(axes[i].Axis);
            if (axes[i].Negative ? value < -AxisThreshold : value > AxisThreshold)
            {
                return true;
            }
        }

        return false;
    }
}
