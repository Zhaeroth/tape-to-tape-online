using System.Collections.Generic;

namespace T2TOnline.Core.Input;

/// <summary>
/// Measures how long each button stays held, from a stream of held-button observations. Used to check that a
/// remote pad's hold times (pass power, slapshot charge) survive the trip intact.
/// </summary>
public sealed class HoldTracker
{
    private readonly long[] _pressedAt = new long[PadState.ButtonCount];
    private PadButtons _held;

    /// <summary>
    /// Records the buttons held at <paramref name="timestamp"/> (any monotonic unit) and adds a
    /// <see cref="CompletedHold"/> to <paramref name="completed"/> for every button released since the last call.
    /// </summary>
    public void Observe(long timestamp, PadButtons held, List<CompletedHold> completed)
    {
        held &= PadButtons.All;
        var changed = (ushort)(held ^ _held);
        for (int i = 0; changed != 0; i++, changed >>= 1)
        {
            if ((changed & 1) == 0)
            {
                continue;
            }

            var button = (PadButtons)(1 << i);
            if ((held & button) != 0)
            {
                _pressedAt[i] = timestamp;
            }
            else
            {
                completed.Add(new CompletedHold(button, _pressedAt[i], timestamp - _pressedAt[i]));
            }
        }

        _held = held;
    }
}

/// <summary>One finished button hold.</summary>
/// <param name="Button">The button.</param>
/// <param name="PressedAt">When it went down.</param>
/// <param name="Duration">How long it stayed down, in the observation timestamps' unit.</param>
public readonly record struct CompletedHold(PadButtons Button, long PressedAt, long Duration);
