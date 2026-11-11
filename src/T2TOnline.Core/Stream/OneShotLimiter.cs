using System;

namespace T2TOnline.Core.Stream;

/// <summary>
/// Keeps a host from playing sounds without end on a friend's PC, whatever the stream asks (a burst delivered together
/// after a hitch or a restarted playout clock, a hostile host): at most <see cref="Burst"/> direct sounds at once and
/// <see cref="PerSecond"/> a second sustained (a token bucket). A sound started this way can't be stopped from here (the
/// game's <c>PlayOneShot</c> lets go of it as it starts it), so the count is all that bounds one that never ends. Normal
/// play is far under it: a whistle, a goalie's save, a few a minute. Times in microseconds on the caller's clock, which
/// never goes backwards. Game-independent.
/// </summary>
public sealed class OneShotLimiter
{
    /// <summary>The most sounds played at once (the bucket's size).</summary>
    public const int Burst = 16;

    /// <summary>The most sounds played a second, sustained (the bucket's refill).</summary>
    public const int PerSecond = 16;

    private double _tokens = Burst;
    private long _refilledAt = long.MinValue;

    /// <summary>Sounds let through since the last <see cref="Reset"/>.</summary>
    public long Played { get; private set; }

    /// <summary>Sounds refused since the last <see cref="Reset"/>.</summary>
    public long Refused { get; private set; }

    /// <summary>Whether one more sound plays at <paramref name="nowMicros"/>: counted as played, or as refused.</summary>
    public bool TryTake(long nowMicros)
    {
        if (_refilledAt != long.MinValue && nowMicros > _refilledAt)
        {
            _tokens = Math.Min(Burst, _tokens + ((nowMicros - _refilledAt) * PerSecond / 1_000_000.0));
        }

        _refilledAt = _refilledAt == long.MinValue ? nowMicros : Math.Max(_refilledAt, nowMicros);
        if (_tokens < 1)
        {
            Refused++;
            return false;
        }

        _tokens -= 1;
        Played++;
        return true;
    }

    /// <summary>Starts again (a new playback): a full bucket, the counts zero.</summary>
    public void Reset()
    {
        _tokens = Burst;
        _refilledAt = long.MinValue;
        Played = Refused = 0;
    }
}
