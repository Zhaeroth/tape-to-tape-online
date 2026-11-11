#if HAS_INTEROP
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Il2CppRewired;
using T2TOnline.Core.Input;
using T2TOnline.Plugin.Native;

namespace T2TOnline.Plugin.Input;

/// <summary>
/// Rewired's maps as <see cref="InputBinding"/>s and back: a keyboard's or mouse's element maps (keys, chords, mouse
/// buttons), a joystick's in gamepad-template terms, the game's gamepad-template maps; the game's own defaults of each;
/// and names for the log.
/// </summary>
internal static class RewiredBindings
{
    /// <summary>The game's action whose handler needs a mouse (see <see cref="SkipsAction"/>).</summary>
    public const string MouseClickActionName = "UI Mouse Click";

    private static int _mouseClickAction = int.MinValue;

    /// <summary>"UI Mouse Click"'s id in this build (65 in the Oct 29 build), or -1 if there's none.</summary>
    public static int MouseClickActionId
    {
        get
        {
            if (_mouseClickAction == int.MinValue)
            {
                _mouseClickAction = ReInput.mapping.GetActionId(MouseClickActionName);
            }

            return _mouseClickAction;
        }
    }

    /// <summary>
    /// True for an action a virtual controller must not carry: "UI Mouse Click". The game's UI reader answers it with
    /// <c>RewiredUIInputReader.OnInputMouseClick</c>, which asks the player for its mouse and throws a
    /// NullReferenceException into Rewired's input events when there's none, and a virtual controller's player never has
    /// one. The game's Fighting maps bind it (a stale id whose old name was the block-low): LeftControl on the keyboard, RT
    /// on a pad. The fighter never reads it (it reads actions 59, 60, 61 and 64; block-low is 76 in the Default maps), so
    /// leaving it out changes nothing but the exception.
    /// </summary>
    public static bool SkipsAction(int actionId) => actionId >= 0 && actionId == MouseClickActionId;

    private static int[]? _actionIds;

    /// <summary>Every action id this build's Rewired data has but <see cref="SkipsAction"/>'s, read once (63 in the Oct 29 build).</summary>
    public static IReadOnlyList<int> ActionIds()
    {
        if (_actionIds == null)
        {
            var ids = new List<int>();
            for (int id = 0; id <= InputBinding.MaxActionId; id++)
            {
                if (ReInput.mapping.GetAction(id) != null && !SkipsAction(id))
                {
                    ids.Add(id);
                }
            }

            _actionIds = ids.ToArray();
        }

        return _actionIds;
    }

    /// <summary>
    /// One element map of a keyboard map as a binding: a key with the modifiers it needs. Null for anything else (an
    /// element map without a key).
    /// </summary>
    public static InputBinding? FromKeyboard(int category, ActionElementMap map)
    {
        int key = (int)map.keyCode;
        if (key <= 0 || key > BindingElement.MaxCode)
        {
            return null;
        }

        var modifiers = KeyModifiers.None;
        modifiers |= Modifier(map.modifierKey1);
        modifiers |= Modifier(map.modifierKey2);
        modifiers |= Modifier(map.modifierKey3);
        return Binding(category, map, BindingElement.Key(key, modifiers), button: true);
    }

    /// <summary>One element map of a mouse map as a binding: a mouse button. Null for a mouse axis (movement, the wheel): online, the mouse only carries buttons.</summary>
    public static InputBinding? FromMouse(int category, ActionElementMap map)
    {
        if (map.elementType != ControllerElementType.Button || map.elementIdentifierId < 0 || map.elementIdentifierId > BindingElement.MaxCode)
        {
            return null;
        }

        return Binding(category, map, BindingElement.MouseButton(map.elementIdentifierId), button: true);
    }

    /// <summary>
    /// One element map of a joystick's map in gamepad-template terms (Rewired's joystick-to-template element table). Null
    /// for an element a standard pad doesn't have.
    /// </summary>
    public static InputBinding? FromJoystick(int category, Joystick joystick, ActionElementMap map)
    {
        ControllerElementIdentifier? template = ReInput.mapping.GetFirstJoystickTemplateElementIdentifier(joystick, map.elementIdentifierId);
        if (template == null || !GamepadTemplateLayout.TryGetPadInput(template.id, out bool isAxis, out int index))
        {
            return null;
        }

        BindingElement element = isAxis ? BindingElement.Axis((PadAxis)index) : BindingElement.PadButton(index);
        return Binding(category, map, element, button: map.elementType == ControllerElementType.Button);
    }

    /// <summary>One binding of the game's gamepad-template map. Null for an element a standard pad doesn't have.</summary>
    public static InputBinding? FromTemplate(int category, ControllerTemplateActionElementMap binding)
    {
        if (!GamepadTemplateLayout.TryGetPadInput(binding.elementIdentifierId, out bool isAxis, out int index) || binding.actionId is < 0 or > InputBinding.MaxActionId)
        {
            return null;
        }

        // Template axes (sticks, triggers) carry a range and inversion; template buttons only a contribution.
        BindingElement element = isAxis ? BindingElement.Axis((PadAxis)index) : BindingElement.PadButton(index);
        if (binding.TryCast<ControllerTemplateActionAxisMap>() is { } axis)
        {
            return new InputBinding((byte)category, (ushort)binding.actionId, element, axis.axisContribution == Pole.Negative, (BindingRange)axis.axisRange, axis.invert);
        }

        if (binding.TryCast<ControllerTemplateActionButtonMap>() is { } button)
        {
            return new InputBinding((byte)category, (ushort)binding.actionId, element, button.axisContribution == Pole.Negative);
        }

        return null;
    }

    /// <summary>
    /// The game's gamepad-template bindings in every category (what a virtual pad gets with no remaps), and how many it has
    /// that a standard pad can't carry.
    /// </summary>
    public static List<InputBinding> PadDefaults(IEnumerable<int> categories, out int skipped) => PadDefaults(categories, out skipped, null);

    /// <summary>
    /// As <see cref="PadDefaults(IEnumerable{int}, out int)"/>, naming each skipped binding in <paramref name="skippedNames"/>
    /// ("UI: UI Back on Guide (Home, PS or Steam button)"): what a friend's virtual pad can't do with the game's maps.
    /// </summary>
    public static List<InputBinding> PadDefaults(IEnumerable<int> categories, out int skipped, List<string>? skippedNames)
    {
        var bindings = new List<InputBinding>();
        skipped = 0;
        foreach (int category in categories)
        {
            ControllerTemplateMap? map = ReInput.mapping.GetControllerTemplateMapInstance(GamepadTemplate.typeGuid, category, 0);
            foreach (ControllerTemplateActionElementMap binding in Il2CppLists.Items(map?.ElementMaps))
            {
                if (FromTemplate(category, binding) is { } converted)
                {
                    bindings.Add(converted);
                }
                else
                {
                    skipped++;
                    skippedNames?.Add($"{CategoryName(category)}: {ActionName(binding.actionId)} on {GamepadTemplateLayout.ElementName(binding.elementIdentifierId)}");
                }
            }
        }

        return bindings;
    }

    /// <summary>The game's default keyboard and mouse bindings in every category (Rewired's default map instances), and the mouse axes left out.</summary>
    public static List<InputBinding> KeyboardDefaults(IEnumerable<int> categories, out int mouseAxes)
    {
        var bindings = new List<InputBinding>();
        mouseAxes = 0;
        foreach (int category in categories)
        {
            AddKeyboard(bindings, category, ReInput.mapping.GetKeyboardMapInstance(category, 0));
            mouseAxes += AddMouse(bindings, category, ReInput.mapping.GetMouseMapInstance(category, 0));
        }

        return bindings;
    }

    /// <summary>Adds a keyboard map's keys to <paramref name="bindings"/>; returns how many element maps had no key.</summary>
    public static int AddKeyboard(List<InputBinding> bindings, int category, ControllerMap? map)
    {
        int skipped = 0;
        foreach (ActionElementMap element in Il2CppLists.Items(map?.AllMaps))
        {
            if (FromKeyboard(category, element) is { } binding)
            {
                bindings.Add(binding);
            }
            else
            {
                skipped++;
            }
        }

        return skipped;
    }

    /// <summary>Adds a mouse map's buttons to <paramref name="bindings"/>; returns how many were axes (left out).</summary>
    public static int AddMouse(List<InputBinding> bindings, int category, ControllerMap? map)
    {
        int axes = 0;
        foreach (ActionElementMap element in Il2CppLists.Items(map?.AllMaps))
        {
            // An unassigned placeholder (the game's Default mouse map has one, action -1) isn't a binding.
            if (element.actionId < 0)
            {
                continue;
            }

            if (FromMouse(category, element) is { } binding)
            {
                bindings.Add(binding);
            }
            else
            {
                axes++;
            }
        }

        return axes;
    }

    /// <summary>Removes every element map from <paramref name="map"/> (the map itself, and whether it's on, stay). Returns how many.</summary>
    public static int Clear(ControllerMap map)
    {
        var ids = new List<int>();
        foreach (ActionElementMap element in Il2CppLists.Items(map.AllMaps))
        {
            ids.Add(element.id);
        }

        foreach (int id in ids)
        {
            map.DeleteElementMap(id);
        }

        return ids.Count;
    }

    /// <summary>Every map category id in this build's Rewired data (ids probed 0 to 63).</summary>
    public static int[] CategoryIds()
    {
        var ids = new List<int>();
        for (int id = 0; id < 64; id++)
        {
            if (ReInput.mapping.GetMapCategory(id) != null)
            {
                ids.Add(id);
            }
        }

        return ids.ToArray();
    }

    /// <summary>Bindings per category by name, for the log ("Default 30, Activation 13").</summary>
    public static string PerCategory(IEnumerable<InputBinding> bindings) =>
        string.Join(", ", bindings.GroupBy(b => b.Category).Select(g => $"{CategoryName(g.Key)} {g.Count().ToString(CultureInfo.InvariantCulture)}"));

    /// <summary>A binding for the log: "Default: Shoot on Space".</summary>
    public static string Describe(InputBinding binding) =>
        $"{CategoryName(binding.Category)}: {ActionName(binding.ActionId)}{(binding.Negative ? " (-)" : string.Empty)} on {ElementName(binding.Element)}";

    /// <summary>An element for the log: a key by Unity's name with its modifiers, a mouse button, a pad button or axis.</summary>
    public static string ElementName(BindingElement element) => element.Kind switch
    {
        BindingElementKind.Key => (element.Modifiers == KeyModifiers.None ? string.Empty : $"{element.Modifiers.ToString().Replace(", ", "+", StringComparison.Ordinal)}+") +
            ((UnityEngine.KeyCode)element.Code).ToString(),
        BindingElementKind.MouseButton => element.Code switch
        {
            3 => "Left Mouse Button",
            4 => "Right Mouse Button",
            5 => "Middle Mouse Button",
            _ => $"mouse button {element.Code.ToString(CultureInfo.InvariantCulture)}",
        },
        _ => element.ToString(),
    };

    /// <summary>An action's name in this build, or its id.</summary>
    public static string ActionName(int actionId) => ReInput.mapping.GetAction(actionId)?.name ?? $"action {actionId.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>A map category's name in this build, or its id.</summary>
    public static string CategoryName(int categoryId) => ReInput.mapping.GetMapCategory(categoryId)?.name ?? $"category {categoryId.ToString(CultureInfo.InvariantCulture)}";

    private static InputBinding? Binding(int category, ActionElementMap map, BindingElement element, bool button)
    {
        int action = map.actionId;
        if (action is < 0 or > InputBinding.MaxActionId || category is < 0 or > byte.MaxValue)
        {
            return null;
        }

        bool negative = map.axisContribution == Pole.Negative;
        return button
            ? new InputBinding((byte)category, (ushort)action, element, negative)
            : new InputBinding((byte)category, (ushort)action, element, negative, (BindingRange)map.axisRange, map.invert);
    }

    private static KeyModifiers Modifier(ModifierKey key) => key switch
    {
        ModifierKey.Control => KeyModifiers.Control,
        ModifierKey.Alt => KeyModifiers.Alt,
        ModifierKey.Shift => KeyModifiers.Shift,
        ModifierKey.Command => KeyModifiers.Command,
        _ => KeyModifiers.None,
    };
}
#endif
