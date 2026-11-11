using System;
using System.Numerics;

namespace T2TOnline.Core.Input;

/// <summary>
/// Builds <see cref="KeyboardMouseState"/> samples on a friend's PC, over the friend's key table. Call
/// <see cref="Observe"/> as often as input is polled (every input update), with what is held and what went down since the
/// last poll, and <see cref="Capture"/> at the send rate (120 Hz). A key pressed and let go between two polls still
/// counts, as long as the input system saw it go down.
/// </summary>
public sealed class KeyboardMouseSampler
{
    private readonly ulong _mask;
    private KeyboardMouseState _state;

    /// <summary>Creates a sampler for a table of <paramref name="elementCount"/> elements (1 to 64).</summary>
    public KeyboardMouseSampler(int elementCount)
    {
        if (elementCount is < 1 or > KeyboardMouseState.MaxElements)
        {
            throw new ArgumentOutOfRangeException(nameof(elementCount), $"A key table has 1 to {KeyboardMouseState.MaxElements} elements.");
        }

        ElementCount = elementCount;
        _mask = KeyboardMouseState.MaskOf(elementCount);
    }

    /// <summary>Elements in the table.</summary>
    public int ElementCount { get; }

    /// <summary>Elements held, as last observed.</summary>
    public ulong Held => _state.Held;

    /// <summary>
    /// Records the elements held now (<paramref name="held"/>) and those the input system saw go down since the last poll
    /// (<paramref name="pressed"/>), counting each new press once: a press is either.
    /// </summary>
    public void Observe(ulong held, ulong pressed)
    {
        held &= _mask;
        ulong rising = ((held & ~_state.Held) | pressed) & _mask;
        KeyboardMouseState state = new(held, _state.CountersLow, _state.CountersHigh);
        while (rising != 0)
        {
            int element = BitOperations.TrailingZeroCount(rising);
            rising &= rising - 1;
            state = state.WithPress(element);
        }

        _state = state;
    }

    /// <summary>Takes a sample with the latest held elements and counters.</summary>
    public KeyboardMouseState Capture() => _state;

    /// <summary>Forgets held elements (the window lost focus, or the keyboard was switched off). Counters keep counting.</summary>
    public void ReleaseAll() => _state = _state.Released();
}
