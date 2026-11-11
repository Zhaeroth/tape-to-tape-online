using System;

namespace T2TOnline.Core.Input;

/// <summary>
/// One sample of a gamepad: held buttons, per-button press counters, and six analog axes.
/// Press counters let a tap shorter than one sample survive: the counter advances even if the
/// button is already released by the time the next sample is taken.
/// </summary>
public readonly struct PadState : IEquatable<PadState>, IPlayoutFrame<PadState>
{
    /// <summary>Number of digital buttons.</summary>
    public const int ButtonCount = 14;

    /// <summary>Number of analog axes.</summary>
    public const int AxisCount = 6;

    /// <summary>Bits used by each button's press counter.</summary>
    public const int PressCounterBits = 2;

    /// <summary>Mask covering every press counter.</summary>
    public const uint PressCounterMask = (1u << (ButtonCount * PressCounterBits)) - 1;

    /// <summary>Creates a sample from raw values.</summary>
    public PadState(
        PadButtons buttons,
        uint pressCounters,
        short leftStickX,
        short leftStickY,
        short rightStickX,
        short rightStickY,
        ushort leftTrigger,
        ushort rightTrigger)
    {
        Buttons = buttons & PadButtons.All;
        PressCounters = pressCounters & PressCounterMask;
        LeftStickX = leftStickX;
        LeftStickY = leftStickY;
        RightStickX = rightStickX;
        RightStickY = rightStickY;
        LeftTrigger = leftTrigger;
        RightTrigger = rightTrigger;
    }

    /// <summary>A pad with nothing pressed and sticks centred.</summary>
    public static PadState Neutral => default;

    /// <summary>Buttons held at the moment of the sample.</summary>
    public PadButtons Buttons { get; }

    /// <summary>Two bits per button, button <c>i</c> at bits <c>2i</c> and <c>2i+1</c>, counting presses modulo 4.</summary>
    public uint PressCounters { get; }

    /// <summary>Raw left stick X, -32767 to 32767.</summary>
    public short LeftStickX { get; }

    /// <summary>Raw left stick Y, -32767 to 32767.</summary>
    public short LeftStickY { get; }

    /// <summary>Raw right stick X, -32767 to 32767.</summary>
    public short RightStickX { get; }

    /// <summary>Raw right stick Y, -32767 to 32767.</summary>
    public short RightStickY { get; }

    /// <summary>Raw left trigger, 0 to 65535.</summary>
    public ushort LeftTrigger { get; }

    /// <summary>Raw right trigger, 0 to 65535.</summary>
    public ushort RightTrigger { get; }

    /// <summary>True when every button in <paramref name="buttons"/> is held.</summary>
    public bool IsHeld(PadButtons buttons) => (Buttons & buttons) == buttons && buttons != PadButtons.None;

    /// <summary>The press counter (0 to 3) of the button at <paramref name="buttonIndex"/>.</summary>
    public int GetPressCounter(int buttonIndex)
    {
        if ((uint)buttonIndex >= ButtonCount)
        {
            throw new ArgumentOutOfRangeException(nameof(buttonIndex));
        }

        return (int)((PressCounters >> (buttonIndex * PressCounterBits)) & 0b11);
    }

    /// <summary>An axis as a float: sticks -1 to 1, triggers 0 to 1.</summary>
    public float GetAxis(PadAxis axis) => axis switch
    {
        PadAxis.LeftStickX => LeftStickX / 32767f,
        PadAxis.LeftStickY => LeftStickY / 32767f,
        PadAxis.RightStickX => RightStickX / 32767f,
        PadAxis.RightStickY => RightStickY / 32767f,
        PadAxis.LeftTrigger => LeftTrigger / 65535f,
        PadAxis.RightTrigger => RightTrigger / 65535f,
        _ => throw new ArgumentOutOfRangeException(nameof(axis)),
    };

    /// <summary>Converts a stick value (-1 to 1) to its raw form. Out-of-range and NaN values clamp.</summary>
    public static short StickToRaw(float value) =>
        float.IsNaN(value) ? (short)0 : (short)Math.Round(Math.Clamp(value, -1f, 1f) * 32767f);

    /// <summary>Converts a trigger value (0 to 1) to its raw form. Out-of-range and NaN values clamp.</summary>
    public static ushort TriggerToRaw(float value) =>
        float.IsNaN(value) ? (ushort)0 : (ushort)Math.Round(Math.Clamp(value, 0f, 1f) * 65535f);

    /// <summary>This sample with nothing held and the sticks and triggers released; the press counters are kept.</summary>
    public PadState Released() => new(PadButtons.None, PressCounters, 0, 0, 0, 0, 0, 0);

    /// <summary>True when held buttons and press counters match <paramref name="other"/>.</summary>
    public bool DigitalEquals(in PadState other) => Buttons == other.Buttons && PressCounters == other.PressCounters;

    /// <summary>True when both sticks match <paramref name="other"/>.</summary>
    public bool SticksEqual(in PadState other) =>
        LeftStickX == other.LeftStickX && LeftStickY == other.LeftStickY &&
        RightStickX == other.RightStickX && RightStickY == other.RightStickY;

    /// <summary>True when both triggers match <paramref name="other"/>.</summary>
    public bool TriggersEqual(in PadState other) => LeftTrigger == other.LeftTrigger && RightTrigger == other.RightTrigger;

    /// <inheritdoc />
    public bool Equals(PadState other) => DigitalEquals(other) && SticksEqual(other) && TriggersEqual(other);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is PadState other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(
        Buttons, PressCounters, LeftStickX, LeftStickY, RightStickX, RightStickY, LeftTrigger, RightTrigger);

    /// <summary>Value equality.</summary>
    public static bool operator ==(PadState left, PadState right) => left.Equals(right);

    /// <summary>Value inequality.</summary>
    public static bool operator !=(PadState left, PadState right) => !left.Equals(right);

    /// <inheritdoc />
    public override string ToString() =>
        $"[{Buttons}] counters={PressCounters:X7} L=({LeftStickX},{LeftStickY}) R=({RightStickX},{RightStickY}) T=({LeftTrigger},{RightTrigger})";
}
