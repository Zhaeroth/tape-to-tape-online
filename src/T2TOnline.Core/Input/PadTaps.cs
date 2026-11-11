namespace T2TOnline.Core.Input;

/// <summary>Finds presses that happened entirely between two samples.</summary>
public static class PadTaps
{
    /// <summary>
    /// Buttons pressed more often between <paramref name="previous"/> and <paramref name="next"/> than the change
    /// in held state explains. The host turns each into a one-update press so short taps are never lost.
    /// </summary>
    public static PadButtons Between(in PadState previous, in PadState next)
    {
        PadButtons taps = PadButtons.None;
        for (int i = 0; i < PadState.ButtonCount; i++)
        {
            int presses = (next.GetPressCounter(i) - previous.GetPressCounter(i)) & 0b11;
            if (presses == 0)
            {
                continue;
            }

            var button = (PadButtons)(1 << i);
            bool rose = !previous.IsHeld(button) && next.IsHeld(button);
            if (presses > (rose ? 1 : 0))
            {
                taps |= button;
            }
        }

        return taps;
    }
}
