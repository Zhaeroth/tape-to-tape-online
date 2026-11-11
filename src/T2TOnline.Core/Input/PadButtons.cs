using System;

namespace T2TOnline.Core.Input;

/// <summary>The 14 digital inputs of a standard gamepad, in Rewired gamepad-template terms.</summary>
[Flags]
public enum PadButtons : ushort
{
    /// <summary>No buttons.</summary>
    None = 0,

    /// <summary>Bottom face button (Xbox A, PlayStation Cross).</summary>
    A = 1 << 0,

    /// <summary>Right face button (Xbox B, PlayStation Circle).</summary>
    B = 1 << 1,

    /// <summary>Left face button (Xbox X, PlayStation Square).</summary>
    X = 1 << 2,

    /// <summary>Top face button (Xbox Y, PlayStation Triangle).</summary>
    Y = 1 << 3,

    /// <summary>Left shoulder.</summary>
    LeftBumper = 1 << 4,

    /// <summary>Right shoulder.</summary>
    RightBumper = 1 << 5,

    /// <summary>Back / View / Share.</summary>
    Back = 1 << 6,

    /// <summary>Start / Menu / Options.</summary>
    Start = 1 << 7,

    /// <summary>Left stick click.</summary>
    LeftStick = 1 << 8,

    /// <summary>Right stick click.</summary>
    RightStick = 1 << 9,

    /// <summary>D-pad up.</summary>
    DPadUp = 1 << 10,

    /// <summary>D-pad down.</summary>
    DPadDown = 1 << 11,

    /// <summary>D-pad left.</summary>
    DPadLeft = 1 << 12,

    /// <summary>D-pad right.</summary>
    DPadRight = 1 << 13,

    /// <summary>Every button.</summary>
    All = (1 << 14) - 1,
}

/// <summary>The six analog inputs of a standard gamepad.</summary>
public enum PadAxis
{
    /// <summary>Left stick, -1 (left) to 1 (right).</summary>
    LeftStickX = 0,

    /// <summary>Left stick, -1 (down) to 1 (up).</summary>
    LeftStickY = 1,

    /// <summary>Right stick, -1 (left) to 1 (right).</summary>
    RightStickX = 2,

    /// <summary>Right stick, -1 (down) to 1 (up).</summary>
    RightStickY = 3,

    /// <summary>Left trigger, 0 (released) to 1 (fully pressed).</summary>
    LeftTrigger = 4,

    /// <summary>Right trigger, 0 (released) to 1 (fully pressed).</summary>
    RightTrigger = 5,
}
