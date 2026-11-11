using System;

namespace T2TOnline.Core.Campaign;

/// <summary>What the host's log says about one failed read of a menu's data.</summary>
public enum MenuReadFailure
{
    /// <summary>The menu's first failed read: logged, and it's read again later.</summary>
    First,

    /// <summary>A further failed read, with reads left: read again later, nothing logged.</summary>
    Again,

    /// <summary>The last read allowed failed: logged once, and the menu isn't read again.</summary>
    GaveUp,
}

/// <summary>
/// The host's retry of a menu whose data can't be read. A menu read in its first frames can throw because the game hasn't
/// filled it yet (the squad screen's selectors before their <c>Init</c>); one failure no longer costs friends the menu's
/// text for its whole life. After a failure the next read waits <see cref="BaseSeconds"/>, doubling with each consecutive
/// failure up to <see cref="MaxSeconds"/>; after <see cref="MaxFailures"/> consecutive failures the menu isn't read again.
/// A read that works resets the count. Only the first failure, the give-up and the first recovery are for the log, once
/// each per menu. One per tracked menu; no allocation after it's made. Not thread-safe.
/// </summary>
public sealed class MenuReadRetry
{
    /// <summary>Consecutive failed reads after which the menu isn't read again.</summary>
    public const int MaxFailures = 3;

    private double _retryAt = double.NegativeInfinity;
    private bool _failedBefore;
    private bool _recoveryLogged;

    /// <summary>A retry that waits <paramref name="baseSeconds"/> after a first failure, doubling up to <paramref name="maxSeconds"/>.</summary>
    public MenuReadRetry(double baseSeconds, double maxSeconds)
    {
        if (!(baseSeconds > 0) || !(maxSeconds >= baseSeconds))
        {
            throw new ArgumentOutOfRangeException(nameof(baseSeconds), "the waits must be positive, the longest at least the first");
        }

        BaseSeconds = baseSeconds;
        MaxSeconds = maxSeconds;
    }

    /// <summary>The wait after a first failure, in seconds.</summary>
    public double BaseSeconds { get; }

    /// <summary>The longest wait between two reads, in seconds.</summary>
    public double MaxSeconds { get; }

    /// <summary>Consecutive failed reads so far.</summary>
    public int Failures { get; private set; }

    /// <summary>True once <see cref="MaxFailures"/> reads in a row failed: the menu isn't read again.</summary>
    public bool GaveUp => Failures >= MaxFailures;

    /// <summary>The wait before the next read after the latest failure, in seconds (0 with none).</summary>
    public double Wait => Failures == 0 ? 0 : Math.Min(MaxSeconds, BaseSeconds * (1 << Math.Min(Failures - 1, 16)));

    /// <summary>True when the menu may be read at <paramref name="now"/>: not given up, and past the wait after a failure.</summary>
    public bool MayRead(double now) => !GaveUp && now >= _retryAt;

    /// <summary>A read failed at <paramref name="now"/>: the next waits <see cref="Wait"/>. Returns what the log says.</summary>
    public MenuReadFailure Failed(double now)
    {
        if (GaveUp)
        {
            return MenuReadFailure.Again;
        }

        Failures++;
        _retryAt = now + Wait;
        if (GaveUp)
        {
            return MenuReadFailure.GaveUp;
        }

        bool first = !_failedBefore;
        _failedBefore = true;
        return first ? MenuReadFailure.First : MenuReadFailure.Again;
    }

    /// <summary>
    /// A read worked: the failures are forgotten. True the first time this happens after a failure (the log says the
    /// menu's text goes to friends again); false otherwise.
    /// </summary>
    public bool Succeeded()
    {
        bool recovered = Failures > 0 && !_recoveryLogged;
        _recoveryLogged |= recovered;
        Failures = 0;
        _retryAt = double.NegativeInfinity;
        return recovered;
    }
}
