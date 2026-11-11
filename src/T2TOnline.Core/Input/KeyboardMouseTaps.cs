namespace T2TOnline.Core.Input;

/// <summary>Finds key presses that happened entirely between two samples (as <see cref="PadTaps"/> does for pads).</summary>
public static class KeyboardMouseTaps
{
    /// <summary>
    /// Elements (of the first <paramref name="elementCount"/>) pressed more often between <paramref name="previous"/> and
    /// <paramref name="next"/> than the change in held state explains. The host turns each into a one-update press so
    /// short taps are never lost.
    /// </summary>
    public static ulong Between(in KeyboardMouseState previous, in KeyboardMouseState next, int elementCount)
    {
        if (next.CountersLow == previous.CountersLow && next.CountersHigh == previous.CountersHigh)
        {
            return 0; // the usual case: no new press at all
        }

        ulong taps = 0;
        int count = elementCount < KeyboardMouseState.MaxElements ? elementCount : KeyboardMouseState.MaxElements;
        for (int i = 0; i < count; i++)
        {
            int presses = (next.GetPressCounter(i) - previous.GetPressCounter(i)) & 0b11;
            if (presses == 0)
            {
                continue;
            }

            bool rose = !previous.IsHeld(i) && next.IsHeld(i);
            if (presses > (rose ? 1 : 0))
            {
                taps |= 1UL << i;
            }
        }

        return taps;
    }
}
