using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace T2TOnline.Core.Input;

/// <summary>
/// The game actions each element of a standard pad gives under a set of bindings (a player's own maps, or the game's
/// gamepad maps), for the controller check's grid: one short text per element of <see cref="PadCheck.Elements"/>, and a
/// longer one naming the element for the line under the grid.
/// </summary>
public static class PadCheckActions
{
    /// <summary>
    /// For each element of <see cref="PadCheck.Elements"/> (same index), the actions <paramref name="bindings"/> give on it,
    /// each once in the bindings' order: "Shoot, UI Submit". A binding on half a stick says which way ("UI Horizontal
    /// (right)"), a button driving an action's negative side says so ("Move Vertical (-)"). "" for an element nothing is
    /// bound to.
    /// </summary>
    /// <param name="bindings">The bindings, in gamepad-template terms (keys and mouse buttons are ignored).</param>
    /// <param name="actionName">An action's name by its id.</param>
    /// <param name="skip">Actions left out (as a host's virtual controller leaves them out), or null.</param>
    public static string[] ByElement(IEnumerable<InputBinding> bindings, Func<int, string> actionName, Func<int, bool>? skip = null)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        ArgumentNullException.ThrowIfNull(actionName);
        IReadOnlyList<PadCheckElement> elements = PadCheck.Elements;
        var names = new List<string>[elements.Count];
        for (int i = 0; i < names.Length; i++)
        {
            names[i] = new List<string>();
        }

        foreach (InputBinding binding in bindings)
        {
            if (binding.Element.IsKeyboard || (skip != null && skip(binding.ActionId)))
            {
                continue;
            }

            for (int i = 0; i < elements.Count; i++)
            {
                PadCheckElement element = elements[i];
                if (element.Binding != binding.Element)
                {
                    continue;
                }

                string name = Describe(element, binding, actionName(binding.ActionId));
                if (!names[i].Contains(name))
                {
                    names[i].Add(name);
                }
            }
        }

        var texts = new string[names.Length];
        for (int i = 0; i < names.Length; i++)
        {
            texts[i] = string.Join(", ", names[i]);
        }

        return texts;
    }

    /// <summary>The line under the grid for element <paramref name="index"/>: "RT: Backwards Right, UI Right Trigger", or that it gives no action.</summary>
    /// <param name="index">The element's index in <see cref="PadCheck.Elements"/>.</param>
    /// <param name="actions">Its text from <see cref="ByElement"/>.</param>
    /// <param name="missing">True when the pad's template lacks it.</param>
    public static string Detail(int index, string actions, bool missing)
    {
        ArgumentNullException.ThrowIfNull(actions);
        string name = (uint)index < (uint)PadCheck.Elements.Count ? PadCheck.Elements[index].Name : $"element {index.ToString(CultureInfo.InvariantCulture)}";
        if (missing)
        {
            return $"{name}: this pad's gamepad template lacks it, so it's never pressed online" + (actions.Length > 0 ? $" (it would give {actions})." : ".");
        }

        return actions.Length > 0 ? $"{name}: {actions}" : $"{name}: no game action is bound to it in these maps.";
    }

    private static string Describe(PadCheckElement element, InputBinding binding, string action)
    {
        if (element.TwoWay && binding.Range != BindingRange.Full)
        {
            return $"{action} ({(binding.Range == BindingRange.Negative ? element.NegativeName : element.PositiveName)})";
        }

        return !element.IsAxis && binding.Negative ? $"{action} (-)" : action;
    }
}

/// <summary>A friend's controller on the host: the game actions it gave the game, and those its maps bind that haven't come yet.</summary>
/// <param name="Given">The actions it gave the game, in the game's order.</param>
/// <param name="NotYet">The actions its maps bind that it hasn't given yet, in the game's order.</param>
/// <param name="Bound">How many actions its maps bind.</param>
public sealed record ActionCoverage(IReadOnlyList<string> Given, IReadOnlyList<string> NotYet, int Bound)
{
    /// <summary>
    /// What a controller gave (<paramref name="given"/>, as its virtual controller noted them) against what its maps bind
    /// (<paramref name="bound"/>, the game's order). An action given that its maps don't bind (another category's, or the
    /// game's own reading) is still among <see cref="Given"/>.
    /// </summary>
    public static ActionCoverage Of(IReadOnlyList<string> given, IReadOnlyList<string> bound)
    {
        ArgumentNullException.ThrowIfNull(given);
        ArgumentNullException.ThrowIfNull(bound);
        var gave = new HashSet<string>(given, StringComparer.Ordinal);
        var distinct = new HashSet<string>(StringComparer.Ordinal);
        var notYet = new List<string>();
        foreach (string action in bound)
        {
            if (distinct.Add(action) && !gave.Contains(action))
            {
                notYet.Add(action);
            }
        }

        return new ActionCoverage(given, notYet, distinct.Count);
    }

    /// <summary>
    /// "40 of the 55 actions its maps bind given so far (the game has 62)", with "and 2 others" when it gave actions its
    /// maps don't bind.
    /// </summary>
    /// <param name="gameActions">How many actions the game has.</param>
    public string Line(int gameActions)
    {
        int boundGiven = Bound - NotYet.Count;
        int others = Math.Max(0, Given.Count - boundGiven);
        return $"{boundGiven} of the {Bound} action{(Bound == 1 ? string.Empty : "s")} its maps bind given so far" +
            (others > 0 ? $", and {others} other{(others == 1 ? string.Empty : "s")}" : string.Empty) + $" (the game has {gameActions})";
    }

    /// <summary>
    /// Names joined with ", " up to about <paramref name="maxChars"/> characters, then "and N more"; "none" for no names.
    /// At least one name is always given in full.
    /// </summary>
    public static string Names(IReadOnlyList<string> names, int maxChars)
    {
        ArgumentNullException.ThrowIfNull(names);
        if (names.Count == 0)
        {
            return "none";
        }

        var text = new StringBuilder();
        for (int i = 0; i < names.Count; i++)
        {
            string next = names[i];
            if (i > 0 && text.Length + 2 + next.Length > maxChars)
            {
                text.Append(CultureInfo.InvariantCulture, $" and {names.Count - i} more");
                break;
            }

            text.Append(i == 0 ? string.Empty : ", ").Append(next);
        }

        return text.ToString();
    }
}
