using System;

namespace T2TOnline.Core.Stream;

/// <summary>What <see cref="RumbleLimiter"/> decided for one rumble.</summary>
public enum RumbleVerdict
{
    /// <summary>Play it.</summary>
    Play,

    /// <summary>Refused: more than the pad takes at once or in a second (a flood, a burst delivered together).</summary>
    TooMany,

    /// <summary>Refused: the pad has buzzed without a break for too long, and rests now.</summary>
    TooLong,
}

/// <summary>
/// Keeps a friend's pad from buzzing without end, whatever the stream asks (a burst delivered together after a hitch or a
/// restarted playout clock, a game that nudges without pause, a hostile host): at most <see cref="Burst"/> rumbles at once
/// and <see cref="PerSecond"/> a second sustained (a token bucket), and never more than <see cref="MaxContinuousMicros"/>
/// of continuous buzzing: past that the pad rests, every rumble refused, until it has been still for
/// <see cref="RestMicros"/>. Normal play is far under both (a pass, a shot, a check: one or two a second, each a fraction of
/// a second long). Times in microseconds on the caller's clock, which never goes backwards. Game-independent.
/// </summary>
public sealed class RumbleLimiter
{
    /// <summary>The most rumbles played at once (the bucket's size).</summary>
    public const int Burst = 4;

    /// <summary>The most rumbles played a second, sustained (the bucket's refill).</summary>
    public const int PerSecond = 8;

    /// <summary>The longest the pad buzzes without a break before it rests.</summary>
    public const long MaxContinuousMicros = 3_000_000;

    /// <summary>How long the pad stays still after buzzing too long before it plays again.</summary>
    public const long RestMicros = 500_000;

    /// <summary>The longest one rumble counts for (a longer or broken duration counts as this; the pad plays the game's).</summary>
    public const float MaxDurationSeconds = 2f;

    private double _tokens = Burst;
    private long _refilledAt = long.MinValue;
    private long _buzzingSince = long.MinValue;
    private long _buzzingUntil = long.MinValue;
    private long _restUntil = long.MinValue;

    /// <summary>Rumbles played since the last <see cref="Reset"/>.</summary>
    public long Played { get; private set; }

    /// <summary>Rumbles refused as <see cref="RumbleVerdict.TooMany"/> since the last <see cref="Reset"/>.</summary>
    public long TooMany { get; private set; }

    /// <summary>Rumbles refused as <see cref="RumbleVerdict.TooLong"/> since the last <see cref="Reset"/>.</summary>
    public long TooLong { get; private set; }

    /// <summary>
    /// Whether a rumble lasting <paramref name="durationSeconds"/> (the game's nudge) plays now. A played one counts against
    /// the rate and extends the continuous buzz; a refused one changes nothing but its count.
    /// </summary>
    public RumbleVerdict Decide(long nowMicros, float durationSeconds)
    {
        Refill(nowMicros);
        if (nowMicros < _restUntil)
        {
            TooLong++;
            return RumbleVerdict.TooLong;
        }

        bool buzzing = nowMicros < _buzzingUntil;
        if (buzzing && nowMicros - _buzzingSince >= MaxContinuousMicros)
        {
            // Still while what's playing runs out, then still for the rest.
            _restUntil = _buzzingUntil + RestMicros;
            TooLong++;
            return RumbleVerdict.TooLong;
        }

        if (_tokens < 1)
        {
            TooMany++;
            return RumbleVerdict.TooMany;
        }

        _tokens -= 1;
        float seconds = float.IsFinite(durationSeconds) ? Math.Clamp(durationSeconds, 0f, MaxDurationSeconds) : MaxDurationSeconds;
        if (!buzzing)
        {
            _buzzingSince = nowMicros;
        }

        _buzzingUntil = Math.Max(_buzzingUntil, nowMicros + (long)(seconds * 1_000_000));
        Played++;
        return RumbleVerdict.Play;
    }

    /// <summary>Starts again (a new playback): a full bucket, the pad still, the counts zero.</summary>
    public void Reset()
    {
        _tokens = Burst;
        _refilledAt = long.MinValue;
        _buzzingSince = _buzzingUntil = _restUntil = long.MinValue;
        Played = TooMany = TooLong = 0;
    }

    private void Refill(long nowMicros)
    {
        if (_refilledAt != long.MinValue && nowMicros > _refilledAt)
        {
            _tokens = Math.Min(Burst, _tokens + ((nowMicros - _refilledAt) * PerSecond / 1_000_000.0));
        }

        _refilledAt = _refilledAt == long.MinValue ? nowMicros : Math.Max(_refilledAt, nowMicros);
    }
}
