namespace T2TOnline.Core.OnlineSettings;

/// <summary>
/// The failures of one part of the native screens' layer, which ticks each part every frame and switches a failing one off for the game: a part
/// goes off at its <see cref="MaxFailures"/>th failure with no clean stretch of <see cref="CleanTicks"/> ticks between two of them. Failures
/// "in a row" alone would never switch off a part that does its work only every so many frames (one look a second, four a second): the frames
/// it idles through in between succeed, the run starts again each time, and its failure is logged for as long as the game runs. A failure
/// that is one of a kind is forgotten after the clean stretch. Not thread-safe (the game's thread only).
/// </summary>
public sealed class PartFailures
{
    /// <summary>The failures that switch a part off.</summary>
    public const int MaxFailures = 10;

    /// <summary>The ticks with no failure after which earlier failures no longer count: half a minute at 60 frames a second.</summary>
    public const long CleanTicks = 1800;

    private long _lastFailure;

    /// <summary>The failures counted now.</summary>
    public int Failures { get; private set; }

    /// <summary>True once the part is off for the game.</summary>
    public bool Off => Failures >= MaxFailures;

    /// <summary>The part's tick at <paramref name="tick"/> (the layer's count of frames) didn't fail.</summary>
    public void Succeeded(long tick)
    {
        if (Failures > 0 && !Off && tick - _lastFailure > CleanTicks)
        {
            Failures = 0;
        }
    }

    /// <summary>The part's tick at <paramref name="tick"/> failed. True when this failure switches it off.</summary>
    public bool Failed(long tick)
    {
        if (Off)
        {
            return false;
        }

        if (Failures > 0 && tick - _lastFailure > CleanTicks)
        {
            Failures = 0;
        }

        _lastFailure = tick;
        Failures++;
        return Off;
    }
}
