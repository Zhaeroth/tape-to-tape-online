namespace T2TOnline.Core.Input;

/// <summary>
/// Host-side buffer for one friend's pad: an <see cref="InputPlayout{T}"/> of <see cref="PadState"/> frames (see there for
/// how it plays them out). A pad gone stale shows nothing held and centred sticks, its press counters kept.
/// </summary>
public sealed class PadPlayout : InputPlayout<PadState>
{
    /// <summary>Creates a playout buffer.</summary>
    /// <param name="minSpareFrames">Frames kept in reserve at the tightest moment, as a safety margin.</param>
    /// <param name="maxBacklogFrames">Buffered frames beyond which the backlog is cut at once.</param>
    /// <param name="windowFrames">Ticks of history behind each decision to cut delay.</param>
    /// <param name="staleAfterTicks">Ticks without any packet before output goes neutral.</param>
    public PadPlayout(int minSpareFrames = 1, int maxBacklogFrames = 16, int windowFrames = 120, int staleAfterTicks = 30)
        : base(minSpareFrames, maxBacklogFrames, windowFrames, staleAfterTicks)
    {
    }
}
