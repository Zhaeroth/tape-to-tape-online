using System;

namespace T2TOnline.Core.Input;

/// <summary>
/// Where each pad input sits in Rewired's gamepad template, which is how the game binds actions for every gamepad
/// model. Element ids are Rewired's <c>GamepadTemplate.elementId_*</c> constants, read from the game build.
/// </summary>
public static class GamepadTemplateLayout
{
    /// <summary>Template element id of each axis, indexed by <see cref="PadAxis"/>.</summary>
    private static readonly int[] AxisElements =
    {
        0, // leftStickX
        1, // leftStickY
        2, // rightStickX
        3, // rightStickY
        11, // leftShoulder2 (left trigger)
        13, // rightShoulder2 (right trigger)
    };

    /// <summary>Template element id of each button, indexed by bit position in <see cref="PadButtons"/>.</summary>
    private static readonly int[] ButtonElements =
    {
        4, // actionBottomRow1 (A)
        5, // actionBottomRow2 (B)
        7, // actionTopRow1 (X)
        8, // actionTopRow2 (Y)
        10, // leftShoulder1 (left bumper)
        12, // rightShoulder1 (right bumper)
        14, // center1 (back)
        15, // center2 (start)
        17, // leftStickButton
        18, // rightStickButton
        19, // dPadUp
        21, // dPadDown
        22, // dPadLeft
        20, // dPadRight
    };

    /// <summary>The template element id of <paramref name="axis"/>.</summary>
    public static int AxisElementId(PadAxis axis) =>
        (uint)axis < (uint)AxisElements.Length ? AxisElements[(int)axis] : throw new ArgumentOutOfRangeException(nameof(axis));

    /// <summary>The template element id of the button at bit <paramref name="buttonIndex"/> of <see cref="PadButtons"/>.</summary>
    public static int ButtonElementId(int buttonIndex) =>
        (uint)buttonIndex < (uint)ButtonElements.Length ? ButtonElements[buttonIndex] : throw new ArgumentOutOfRangeException(nameof(buttonIndex));

    // Rewired's GamepadTemplate element ids, in its order (elementId_leftStickX = 0 ... elementId_dPad = 25).
    private static readonly string[] ElementNames =
    {
        "LeftStickX", "LeftStickY", "RightStickX", "RightStickY", "A", "B", "C (a six-button pad's third)", "X", "Y",
        "Z (a six-button pad's third)", "LeftBumper", "LeftTrigger", "RightBumper", "RightTrigger", "Back", "Start",
        "Guide (Home, PS or Steam button)", "LeftStick click", "RightStick click", "DPadUp", "DPadRight", "DPadDown", "DPadLeft",
        "LeftStick (whole stick)", "RightStick (whole stick)", "DPad (whole pad)",
    };

    /// <summary>A gamepad template element's name for the log ("element 31" for an id Rewired doesn't have).</summary>
    public static string ElementName(int templateElementId) =>
        (uint)templateElementId < (uint)ElementNames.Length ? ElementNames[templateElementId] : $"element {templateElementId}";

    /// <summary>True when the online pad carries the template element (14 buttons and 6 axes; not C, Z, Guide or the whole-stick ones).</summary>
    public static bool Carries(int templateElementId) => TryGetPadInput(templateElementId, out _, out _);

    /// <summary>
    /// Finds the pad input for a template element. Returns false for template elements a standard pad doesn't have
    /// (the third face-button row, the guide button) and for compound elements (whole sticks, the whole d-pad).
    /// </summary>
    public static bool TryGetPadInput(int templateElementId, out bool isAxis, out int index)
    {
        int axis = Array.IndexOf(AxisElements, templateElementId);
        if (axis >= 0)
        {
            isAxis = true;
            index = axis;
            return true;
        }

        int button = Array.IndexOf(ButtonElements, templateElementId);
        isAxis = false;
        index = button;
        return button >= 0;
    }
}
