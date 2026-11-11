namespace T2TOnline.Core.Input;

/// <summary>One frame of a friend's input stream, as an <see cref="InputPlayout{T}"/> plays it out.</summary>
/// <typeparam name="T">The frame type itself.</typeparam>
public interface IPlayoutFrame<T>
    where T : struct
{
    /// <summary>
    /// This frame with nothing held (sticks centred), its press counters kept: what playout shows once the friend's
    /// stream went silent, so their skater stops without a later frame's counters reading as new presses.
    /// </summary>
    T Released();
}
