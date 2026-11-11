#if HAS_INTEROP
using UnityEngine;

namespace T2TOnline.Plugin.Streaming;

/// <summary>
/// One timestamp per rendered frame, in seconds of real time, for everything the stream records or plays in that frame.
/// The frame's motion was computed for one moment; a clock read late in the frame (LateUpdate) would add that frame's own
/// CPU time as jitter, uneven spacing the friend's interpolation would show as stutter.
/// </summary>
/// <remarks>
/// This build strips <c>Time.unscaledTimeAsDouble</c>, and the float <c>Time.unscaledTime</c> loses sub-millisecond
/// precision after an hour. So the precise <c>Time.realtimeSinceStartupAsDouble</c> is read once per frame: at the mod's
/// Update (<see cref="Latch"/>, the same point every frame), or at the first use in a frame that comes earlier (an event
/// raised during physics).
/// </remarks>
internal static class FrameClock
{
    private static int _frame = -1;
    private static double _seconds;

    /// <summary>This frame's timestamp, in seconds of real time.</summary>
    public static double Seconds
    {
        get
        {
            Latch();
            return _seconds;
        }
    }

    /// <summary>This frame's number (Unity's frame count), latched with its timestamp.</summary>
    public static int Frame
    {
        get
        {
            Latch();
            return _frame;
        }
    }

    /// <summary>This frame's timestamp, in microseconds of real time.</summary>
    public static long Micros => (long)(Seconds * 1_000_000);

    /// <summary>The real time right now (not the frame's stamp), in microseconds, on the same clock: for arrival times.</summary>
    public static long NowMicros => (long)(Time.realtimeSinceStartupAsDouble * 1_000_000);

    /// <summary>Takes this frame's timestamp if it hasn't been taken yet. Call early each frame.</summary>
    public static void Latch()
    {
        int frame = Time.frameCount;
        if (frame != _frame)
        {
            _frame = frame;
            _seconds = Time.realtimeSinceStartupAsDouble;
        }
    }
}
#endif
