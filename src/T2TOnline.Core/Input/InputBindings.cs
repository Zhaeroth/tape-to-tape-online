using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using T2TOnline.Core.Protocol;

namespace T2TOnline.Core.Input;

/// <summary>Where a friend's bindings came from.</summary>
public enum BindingSource : byte
{
    /// <summary>The game's own default maps (the friend never remapped, or nothing of theirs could be read).</summary>
    GameDefaults = 0,

    /// <summary>The friend's own maps: what their game has loaded, their saved remaps included.</summary>
    OwnMaps = 1,
}

/// <summary>What a binding is on.</summary>
public enum BindingElementKind : byte
{
    /// <summary>A gamepad-template button, by its bit index in <see cref="PadButtons"/> (0 to 13).</summary>
    PadButton = 0,

    /// <summary>A gamepad-template axis, by <see cref="PadAxis"/> (0 to 5).</summary>
    PadAxis = 1,

    /// <summary>A keyboard key, by Unity's key code, with the modifier keys that must be held with it.</summary>
    Key = 2,

    /// <summary>A mouse button, by Rewired's mouse element identifier id.</summary>
    MouseButton = 3,
}

/// <summary>Modifier keys a key binding needs held (Rewired's <c>ModifierKey</c> values, either side of the keyboard).</summary>
[Flags]
public enum KeyModifiers : byte
{
    /// <summary>None.</summary>
    None = 0,

    /// <summary>Either Control key.</summary>
    Control = 1,

    /// <summary>Either Alt key.</summary>
    Alt = 2,

    /// <summary>Either Shift key.</summary>
    Shift = 4,

    /// <summary>Either Command (Windows) key.</summary>
    Command = 8,

    /// <summary>Every modifier.</summary>
    All = 15,
}

/// <summary>The part of an axis a binding covers (Rewired's <c>AxisRange</c> values).</summary>
public enum BindingRange : byte
{
    /// <summary>The whole axis.</summary>
    Full = 0,

    /// <summary>Its positive half.</summary>
    Positive = 1,

    /// <summary>Its negative half.</summary>
    Negative = 2,
}

/// <summary>One element a binding is on: a pad button or axis (gamepad-template terms), or a key (with modifiers) or mouse button.</summary>
/// <param name="Kind">What kind of element.</param>
/// <param name="Code">Its index or code (see <see cref="BindingElementKind"/>).</param>
/// <param name="Modifiers">For a key: the modifier keys held with it.</param>
public readonly record struct BindingElement(BindingElementKind Kind, ushort Code, KeyModifiers Modifiers = KeyModifiers.None)
{
    /// <summary>The highest key code or mouse element id carried (nine bits; Unity's keyboard key codes are below 330).</summary>
    public const int MaxCode = 511;

    /// <summary>Unity's key code for Q (the brawl combo's first key).</summary>
    public const int KeyQ = 113;

    /// <summary>Unity's key code for E (the brawl combo's second key).</summary>
    public const int KeyE = 101;

    /// <summary>True for a key or a mouse button (an element of a keyboard friend's key table).</summary>
    public bool IsKeyboard => Kind is BindingElementKind.Key or BindingElementKind.MouseButton;

    /// <summary>True when the code is in range for its kind (and only a key has modifiers).</summary>
    public bool IsValid => Kind switch
    {
        BindingElementKind.PadButton => Code < PadState.ButtonCount && Modifiers == KeyModifiers.None,
        BindingElementKind.PadAxis => Code < PadState.AxisCount && Modifiers == KeyModifiers.None,
        BindingElementKind.Key => Code is >= 1 and <= MaxCode && (Modifiers & ~KeyModifiers.All) == 0,
        BindingElementKind.MouseButton => Code <= MaxCode && Modifiers == KeyModifiers.None,
        _ => false,
    };

    /// <summary>A pad button by its bit index in <see cref="PadButtons"/>.</summary>
    public static BindingElement PadButton(int index) => new(BindingElementKind.PadButton, (ushort)index);

    /// <summary>A pad axis.</summary>
    public static BindingElement Axis(PadAxis axis) => new(BindingElementKind.PadAxis, (ushort)axis);

    /// <summary>A key by Unity's key code, with modifiers.</summary>
    public static BindingElement Key(int keyCode, KeyModifiers modifiers = KeyModifiers.None) => new(BindingElementKind.Key, (ushort)keyCode, modifiers);

    /// <summary>A mouse button by Rewired's mouse element identifier id.</summary>
    public static BindingElement MouseButton(int elementId) => new(BindingElementKind.MouseButton, (ushort)elementId);

    /// <inheritdoc />
    public override string ToString() => Kind switch
    {
        BindingElementKind.PadButton => ((PadButtons)(1 << Math.Min((int)Code, 15))).ToString(),
        BindingElementKind.PadAxis => ((PadAxis)Code).ToString(),
        BindingElementKind.Key => (Modifiers == KeyModifiers.None ? string.Empty : $"{Modifiers.ToString().Replace(", ", "+", StringComparison.Ordinal)}+") +
            $"key {Code.ToString(CultureInfo.InvariantCulture)}",
        BindingElementKind.MouseButton => $"mouse button {Code.ToString(CultureInfo.InvariantCulture)}",
        _ => $"element {Code.ToString(CultureInfo.InvariantCulture)}",
    };
}

/// <summary>One binding of an action to an element, in one map category.</summary>
/// <param name="Category">The Rewired map category.</param>
/// <param name="ActionId">The Rewired action.</param>
/// <param name="Element">The element it's on.</param>
/// <param name="Negative">True when it drives the action's negative side (Rewired's axis contribution).</param>
/// <param name="Range">For an axis element: the part of it.</param>
/// <param name="Invert">For an axis element: inverted.</param>
public readonly record struct InputBinding(byte Category, ushort ActionId, BindingElement Element, bool Negative = false, BindingRange Range = BindingRange.Full, bool Invert = false)
{
    /// <summary>The highest action id carried (ten bits; the game's are below 100).</summary>
    public const int MaxActionId = 1023;

    /// <summary>True when every field is in range.</summary>
    public bool IsValid => ActionId <= MaxActionId && Element.IsValid && Range <= BindingRange.Negative;

    /// <summary>
    /// The binding as the input it names: on a pad axis, without its range and inversion. A pad's own Rewired map binds a
    /// trigger as an axis with a range where the game's gamepad template binds the same trigger as a button, so comparing a
    /// friend's maps with the template's (the host's log of what they changed) must not tell the two apart (else one
    /// remap names ten trigger bindings as changed).
    /// </summary>
    public InputBinding AsInput => Element.Kind == BindingElementKind.PadAxis ? this with { Range = BindingRange.Full, Invert = false } : this;
}

/// <summary>
/// A friend's controls, as they send them to the host (<see cref="MessageType.InputRemaps"/>) after every welcome: their
/// effective bindings (their game's loaded maps, saved remaps included) in every map category. A pad's are in
/// gamepad-template terms, so they fit whatever pad model the host's virtual pad stands in for; a pad on the game's own
/// defaults sends no bindings at all (the host keeps its template maps). A keyboard's are on the friend's key table
/// (<see cref="Elements"/>): every key, chord or mouse button their maps bind, plus Q and E always (the game's brawl
/// combo), at most 64; their frames (<see cref="KeyboardMouseState"/>) say which of those are held.
/// </summary>
public sealed class InputBindingSet
{
    /// <summary>Most map categories in a set.</summary>
    public const int MaxCategories = 16;

    /// <summary>Most bindings in a set.</summary>
    public const int MaxBindings = 1024;

    /// <summary>Most bindings in one category.</summary>
    public const int MaxBindingsPerCategory = 255;

    private readonly Dictionary<BindingElement, int> _index = new();

    /// <summary>Creates a set. Throws <see cref="ArgumentException"/> when it couldn't be sent as it is (see the limits).</summary>
    public InputBindingSet(byte generation, InputKind device, BindingSource source, byte differences, IReadOnlyList<BindingElement> elements, IReadOnlyList<InputBinding> bindings)
    {
        ArgumentNullException.ThrowIfNull(elements);
        ArgumentNullException.ThrowIfNull(bindings);
        if (!TryValidate(generation, device, source, elements, bindings, out string problem))
        {
            throw new ArgumentException(problem, nameof(bindings));
        }

        Generation = generation;
        Device = device;
        Source = source;
        Differences = differences;
        Elements = elements.ToArray();
        for (int i = 0; i < Elements.Count; i++)
        {
            _index[Elements[i]] = i;
        }

        // Grouped by category, as the wire carries them, so a set read back is the same set.
        Categories = bindings.Select(b => b.Category).Distinct().ToArray();
        Bindings = Categories.SelectMany(c => bindings.Where(b => b.Category == c)).ToArray();
    }

    /// <summary>Which set this is (1 to 255): a keyboard's frames name the generation whose table they use.</summary>
    public byte Generation { get; }

    /// <summary>A pad's bindings or a keyboard's.</summary>
    public InputKind Device { get; }

    /// <summary>The game's defaults, or the friend's own maps.</summary>
    public BindingSource Source { get; }

    /// <summary>How many of the friend's bindings differ from the game's defaults, as the friend counted them (capped at 255; for logs).</summary>
    public byte Differences { get; }

    /// <summary>A keyboard's key table (element <c>i</c> of its frames is <c>Elements[i]</c>); empty for a pad.</summary>
    public IReadOnlyList<BindingElement> Elements { get; }

    /// <summary>Every binding, grouped by category (in the order categories first appear), in order within each.</summary>
    public IReadOnlyList<InputBinding> Bindings { get; }

    /// <summary>The map categories with bindings, in the order they first appear.</summary>
    public IReadOnlyList<byte> Categories { get; }

    /// <summary>True for a pad on the game's defaults, sent without bindings: the host keeps the game's template maps.</summary>
    public bool KeepsTemplateMaps => Device == InputKind.Gamepad && Bindings.Count == 0;

    /// <summary>The key-table index of <paramref name="element"/>, or -1.</summary>
    public int IndexOf(BindingElement element) => _index.TryGetValue(element, out int index) ? index : -1;

    /// <summary>Bindings in <paramref name="category"/>.</summary>
    public int CountIn(byte category)
    {
        int count = 0;
        foreach (InputBinding binding in Bindings)
        {
            count += binding.Category == category ? 1 : 0;
        }

        return count;
    }

    /// <summary>True when <paramref name="other"/> has the same device, table and bindings (its generation and counts aside).</summary>
    public bool SameControlsAs(InputBindingSet? other) =>
        other != null && other.Device == Device && other.Elements.SequenceEqual(Elements) && other.Bindings.SequenceEqual(Bindings);

    /// <summary>
    /// How many bindings differ between <paramref name="own"/> and <paramref name="defaults"/>: the larger of those only in
    /// one and those only in the other, counted as multisets (a binding moved to another element is one difference; one
    /// removed is one).
    /// </summary>
    public static int CountDifferences(IEnumerable<InputBinding> own, IEnumerable<InputBinding> defaults)
    {
        ArgumentNullException.ThrowIfNull(own);
        ArgumentNullException.ThrowIfNull(defaults);
        var remaining = new Dictionary<InputBinding, int>();
        foreach (InputBinding binding in defaults)
        {
            remaining[binding] = remaining.GetValueOrDefault(binding) + 1;
        }

        int added = 0;
        foreach (InputBinding binding in own)
        {
            if (remaining.TryGetValue(binding, out int left) && left > 0)
            {
                remaining[binding] = left - 1;
            }
            else
            {
                added++;
            }
        }

        int removed = remaining.Values.Sum();
        return Math.Max(added, removed);
    }

    /// <summary>
    /// The bindings of <paramref name="own"/> that <paramref name="defaults"/> doesn't have (as multisets), in order, at most
    /// <paramref name="max"/>: for logs.
    /// </summary>
    public static List<InputBinding> NotIn(IEnumerable<InputBinding> own, IEnumerable<InputBinding> defaults, int max)
    {
        ArgumentNullException.ThrowIfNull(own);
        ArgumentNullException.ThrowIfNull(defaults);
        var remaining = new Dictionary<InputBinding, int>();
        foreach (InputBinding binding in defaults)
        {
            remaining[binding] = remaining.GetValueOrDefault(binding) + 1;
        }

        var result = new List<InputBinding>();
        foreach (InputBinding binding in own)
        {
            if (remaining.TryGetValue(binding, out int left) && left > 0)
            {
                remaining[binding] = left - 1;
            }
            else if (result.Count < max)
            {
                result.Add(binding);
            }
        }

        return result;
    }

    /// <summary>A pad on the game's own defaults: no bindings, the host keeps its template maps.</summary>
    public static InputBindingSet PadDefaults(byte generation) =>
        new(generation, InputKind.Gamepad, BindingSource.GameDefaults, 0, Array.Empty<BindingElement>(), Array.Empty<InputBinding>());

    /// <summary>Checks a set against the limits the wire format has. Never throws.</summary>
    internal static bool TryValidate(byte generation, InputKind device, BindingSource source, IReadOnlyList<BindingElement> elements, IReadOnlyList<InputBinding> bindings, out string problem)
    {
        problem = string.Empty;
        if (generation == 0)
        {
            problem = "generations start at 1";
        }
        else if (device is not (InputKind.Gamepad or InputKind.KeyboardMouse))
        {
            problem = $"unknown device {(byte)device}";
        }
        else if (source is not (BindingSource.GameDefaults or BindingSource.OwnMaps))
        {
            problem = $"unknown source {(byte)source}";
        }
        else if (device == InputKind.Gamepad && elements.Count > 0)
        {
            problem = "a pad has no key table";
        }
        else if (device == InputKind.KeyboardMouse && elements.Count is < 1 or > KeyboardMouseState.MaxElements)
        {
            problem = $"a key table has 1 to {KeyboardMouseState.MaxElements} elements, not {elements.Count}";
        }
        else if (elements.Any(e => !e.IsKeyboard || !e.IsValid) || elements.Distinct().Count() != elements.Count)
        {
            problem = "the key table has an invalid or repeated element";
        }
        else if (bindings.Count > MaxBindings)
        {
            problem = $"more than {MaxBindings} bindings";
        }
        else if (bindings.Any(b => !b.IsValid))
        {
            problem = "a binding is out of range";
        }
        else if (device == InputKind.Gamepad ? bindings.Any(b => b.Element.IsKeyboard) : bindings.Any(b => !elements.Contains(b.Element)))
        {
            problem = device == InputKind.Gamepad ? "a pad binding is on a key" : "a keyboard binding is on an element outside the key table";
        }
        else if (bindings.GroupBy(b => b.Category).Count() > MaxCategories || bindings.GroupBy(b => b.Category).Any(g => g.Count() > MaxBindingsPerCategory))
        {
            problem = $"more than {MaxCategories} categories, or more than {MaxBindingsPerCategory} bindings in one";
        }

        return problem.Length == 0;
    }
}

/// <summary>
/// Builds a keyboard friend's <see cref="InputBindingSet"/> from their maps: each distinct key, chord or mouse button their
/// bindings use gets one element of the key table, in the order first seen, after the elements always included (Q and
/// E: the game reads them directly for its brawl combo). A binding that would need a 65th element is skipped and counted.
/// </summary>
public sealed class KeyboardBindingTableBuilder
{
    private readonly List<BindingElement> _elements = new();
    private readonly List<InputBinding> _bindings = new();

    /// <summary>Creates a builder whose table starts with <paramref name="alwaysIncluded"/>.</summary>
    public KeyboardBindingTableBuilder(IEnumerable<BindingElement> alwaysIncluded)
    {
        ArgumentNullException.ThrowIfNull(alwaysIncluded);
        foreach (BindingElement element in alwaysIncluded)
        {
            if (!element.IsKeyboard || !element.IsValid)
            {
                throw new ArgumentException($"{element} isn't a key or mouse button.", nameof(alwaysIncluded));
            }

            if (!_elements.Contains(element) && _elements.Count < KeyboardMouseState.MaxElements)
            {
                _elements.Add(element);
            }
        }
    }

    /// <summary>The elements always included in a keyboard friend's table: Q and E (the game's brawl combo).</summary>
    public static IReadOnlyList<BindingElement> BrawlKeys { get; } = new[] { BindingElement.Key(BindingElement.KeyQ), BindingElement.Key(BindingElement.KeyE) };

    /// <summary>Bindings skipped because the table was full.</summary>
    public int SkippedTableFull { get; private set; }

    /// <summary>Bindings skipped because they were out of range (a key code or action id the format can't carry).</summary>
    public int SkippedInvalid { get; private set; }

    /// <summary>Bindings kept so far.</summary>
    public int Count => _bindings.Count;

    /// <summary>The table so far.</summary>
    public IReadOnlyList<BindingElement> Elements => _elements;

    /// <summary>Adds a binding (its element joins the table if it isn't there yet). False, counted, when it's skipped.</summary>
    public bool Add(InputBinding binding)
    {
        if (!binding.IsValid || !binding.Element.IsKeyboard || _bindings.Count >= InputBindingSet.MaxBindings)
        {
            SkippedInvalid++;
            return false;
        }

        if (!_elements.Contains(binding.Element))
        {
            if (_elements.Count >= KeyboardMouseState.MaxElements)
            {
                SkippedTableFull++;
                return false;
            }

            _elements.Add(binding.Element);
        }

        _bindings.Add(binding);
        return true;
    }

    /// <summary>The set, bindings grouped by category in the order categories first appear (at most 16, 255 bindings each; the rest are skipped and counted).</summary>
    public InputBindingSet Build(byte generation, BindingSource source, byte differences)
    {
        var categories = new List<byte>();
        foreach (InputBinding binding in _bindings)
        {
            if (!categories.Contains(binding.Category))
            {
                categories.Add(binding.Category);
            }
        }

        var ordered = new List<InputBinding>();
        foreach (byte category in categories.Take(InputBindingSet.MaxCategories))
        {
            List<InputBinding> inCategory = _bindings.Where(b => b.Category == category).ToList();
            SkippedInvalid += Math.Max(0, inCategory.Count - InputBindingSet.MaxBindingsPerCategory);
            ordered.AddRange(inCategory.Take(InputBindingSet.MaxBindingsPerCategory));
        }

        SkippedInvalid += _bindings.Count(b => categories.IndexOf(b.Category) >= InputBindingSet.MaxCategories);
        return new InputBindingSet(generation, InputKind.KeyboardMouse, source, differences, _elements, ordered);
    }
}
