namespace T2TOnline.Core.Input;

/// <summary>
/// Builds <see cref="PadState"/> samples on a friend's PC. Call <see cref="ObserveButtons"/> as often as
/// input is polled (every rendered frame) and <see cref="Capture"/> at the send rate (120 Hz); presses that
/// start and end between two captures still advance the press counters.
/// </summary>
public sealed class PadSampler
{
    private PadButtons _held;
    private uint _counters;

    /// <summary>Buttons currently held, as last observed.</summary>
    public PadButtons Held => _held;

    /// <summary>Records the buttons held right now, counting every new press.</summary>
    public void ObserveButtons(PadButtons current)
    {
        current &= PadButtons.All;
        var rising = (ushort)(current & ~_held);
        for (int i = 0; rising != 0; i++, rising >>= 1)
        {
            if ((rising & 1) != 0)
            {
                _counters = IncrementCounter(_counters, i);
            }
        }

        _held = current;
    }

    /// <summary>Takes a sample with the latest buttons and the given axis values.</summary>
    public PadState Capture(float leftStickX, float leftStickY, float rightStickX, float rightStickY, float leftTrigger, float rightTrigger) =>
        new(
            _held,
            _counters,
            PadState.StickToRaw(leftStickX),
            PadState.StickToRaw(leftStickY),
            PadState.StickToRaw(rightStickX),
            PadState.StickToRaw(rightStickY),
            PadState.TriggerToRaw(leftTrigger),
            PadState.TriggerToRaw(rightTrigger));

    /// <summary>Forgets held buttons, for example when the pad disconnects. Counters keep counting.</summary>
    public void ReleaseAll() => _held = PadButtons.None;

    internal static uint IncrementCounter(uint counters, int buttonIndex)
    {
        int shift = buttonIndex * PadState.PressCounterBits;
        uint current = (counters >> shift) & 0b11;
        uint next = (current + 1) & 0b11;
        return (counters & ~(0b11u << shift)) | (next << shift);
    }
}
