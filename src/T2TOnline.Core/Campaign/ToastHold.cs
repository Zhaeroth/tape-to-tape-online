using System;
using System.Collections.Generic;

namespace T2TOnline.Core.Campaign;

/// <summary>What a friend's try at making and queueing one of the host's toasts came to.</summary>
public enum ToastAttempt
{
    /// <summary>Made and queued the game's own way.</summary>
    Shown,

    /// <summary>
    /// Not yet: something it needs isn't on this PC now (the match's teams or a skater of the match before this PC's copy of
    /// the match has loaded, or the scene's toast queue); tried again.
    /// </summary>
    Later,

    /// <summary>It can't be made here (dropped, with why).</summary>
    Failed,
}

/// <summary>
/// A friend's hold on the host's toast events it can't do yet. The host's game queues some toasts while its match loads
/// (a relic's, about the match's home team), before a friend's game has started loading its own copy: such a toast waits
/// here until this PC's game can make it, at most <see cref="HoldSeconds"/>, instead of being dropped. The host's order is
/// kept where it matters, per timing (the game's flags, shared by a toast and the pops that show it): a pop or a toast that
/// shares a timing with something held waits behind it; one that doesn't goes at once. A clear drops everything held, as
/// the host's queue dropped it. At most <see cref="MaxHeld"/> events wait; tried again at most every
/// <see cref="RetrySeconds"/> (a try can look the scene over). Not thread-safe.
/// </summary>
public sealed class ToastHold
{
    /// <summary>Events held at most (a held toast or pop past this goes at once: a toast is dropped, a pop done).</summary>
    public const int MaxHeld = 16;

    /// <summary>Longest a toast waits for this PC's game to be able to make it (a slow match load), in seconds.</summary>
    public const double HoldSeconds = 30;

    /// <summary>Shortest time between two tries at what's held, in seconds.</summary>
    public const double RetrySeconds = 0.25;

    private readonly List<(NotificationEvent Event, double Since)> _held = new(MaxHeld);
    private double _retryAt = double.NegativeInfinity;

    /// <summary>Events waiting.</summary>
    public int Count => _held.Count;

    /// <summary>The timing flags <paramref name="e"/> is about: a toast's, or the timings a pop shows; 0 for a clear.</summary>
    public static int TimingOf(NotificationEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        return e.Op switch
        {
            NotificationOp.Queue => e.Copy?.Timing ?? 0,
            NotificationOp.Pop => e.Timing,
            _ => 0,
        };
    }

    /// <summary>True when <paramref name="e"/> must wait behind something held that shares a timing with it (never a clear).</summary>
    public bool Blocks(NotificationEvent e)
    {
        int timing = TimingOf(e);
        if (timing == 0)
        {
            return false;
        }

        foreach ((NotificationEvent held, _) in _held)
        {
            if ((TimingOf(held) & timing) != 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Holds <paramref name="e"/> (a toast or a pop) from <paramref name="now"/>, behind everything held. False, with nothing
    /// held, for a clear or once <see cref="MaxHeld"/> wait already: the caller does it now (a pop) or drops it (a toast).
    /// </summary>
    public bool Hold(NotificationEvent e, double now)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (e.Op is not (NotificationOp.Queue or NotificationOp.Pop) || _held.Count >= MaxHeld)
        {
            return false;
        }

        _held.Add((e, now));
        return true;
    }

    /// <summary>True when what's held is due another try at <paramref name="now"/> (something is held, and the last try was long enough ago).</summary>
    public bool Due(double now) => _held.Count > 0 && now >= _retryAt;

    /// <summary>
    /// Tries what's held, in the host's order: a toast through <paramref name="show"/> (with how long it has waited), a pop
    /// through <paramref name="pop"/> once no toast of its timing waits before it. A toast that's still
    /// <see cref="ToastAttempt.Later"/> stays (and holds back what shares its timing behind it) until it has waited
    /// <see cref="HoldSeconds"/>; then it goes to <paramref name="expired"/>. Each event is taken off before its callback runs,
    /// so one whose callback throws never comes back (the exception goes to the caller; the rest stay held).
    /// </summary>
    public void Drain(double now, Func<NotificationEvent, double, ToastAttempt> show, Action<NotificationEvent> pop, Action<NotificationEvent, double> expired)
    {
        ArgumentNullException.ThrowIfNull(show);
        ArgumentNullException.ThrowIfNull(pop);
        ArgumentNullException.ThrowIfNull(expired);
        _retryAt = now + RetrySeconds;
        int blocked = 0;
        int i = 0;
        while (i < _held.Count)
        {
            (NotificationEvent e, double since) = _held[i];
            int timing = TimingOf(e);
            if ((timing & blocked) != 0)
            {
                blocked |= timing;
                i++;
                continue;
            }

            _held.RemoveAt(i);
            if (e.Op == NotificationOp.Pop)
            {
                pop(e);
                continue;
            }

            double waited = Math.Max(0, now - since);
            if (show(e, waited) != ToastAttempt.Later)
            {
                continue;
            }

            if (waited >= HoldSeconds)
            {
                expired(e, waited);
                continue;
            }

            _held.Insert(i, (e, since));
            blocked |= timing;
            i++;
        }
    }

    /// <summary>Forgets everything held (the host's queue was cleared, or following stopped). Returns how many toasts were among it.</summary>
    public int Clear()
    {
        int toasts = 0;
        foreach ((NotificationEvent e, _) in _held)
        {
            toasts += e.Op == NotificationOp.Queue ? 1 : 0;
        }

        _held.Clear();
        _retryAt = double.NegativeInfinity;
        return toasts;
    }
}
