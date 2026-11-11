using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace T2TOnline.Core.Campaign;

/// <summary>
/// Host side: an act's intro (its title card and the camera's fly-over, <c>WorldMapActIntro</c>) held until every friend's
/// game has the act's map. The host's game makes the map and starts the intro
/// at once; a friend's game loads that map after the host's run checkpoint, a second or two later, and plays its own copy
/// of the intro then. So the host's start of the intro waits (<see cref="Start"/>) until each friend in the session reports
/// following a map view at or after the one that carries the intro (<see cref="ViewPublished"/>, <see cref="Report"/>), or
/// <see cref="MaxWaitSeconds"/> at most. Times are seconds on one clock. Pure logic; the game glue holds and releases the
/// intro. Not thread-safe.
/// </summary>
public sealed class ActIntroBarrier
{
    /// <summary>The longest the host's intro waits for friends.</summary>
    public const double MaxWaitSeconds = 6;

    private readonly Dictionary<ulong, double?> _arrived = new();
    private uint _viewEpoch;
    private double _since;

    /// <summary>True while the host's intro is held.</summary>
    public bool Holding { get; private set; }

    /// <summary>The act whose intro is held (0 when none).</summary>
    public int Act { get; private set; }

    /// <summary>How long the last hold lasted, once released (seconds).</summary>
    public double Held { get; private set; }

    /// <summary>
    /// The host's intro for <paramref name="act"/> starts at <paramref name="now"/> with <paramref name="friends"/> in the
    /// session: held from now (none: nothing to hold, false).
    /// </summary>
    public bool Start(int act, IEnumerable<ulong> friends, double now)
    {
        ArgumentNullException.ThrowIfNull(friends);
        _arrived.Clear();
        foreach (ulong friend in friends)
        {
            _arrived[friend] = null;
        }

        _viewEpoch = 0;
        _since = now;
        Held = 0;
        Act = act;
        Holding = _arrived.Count > 0;
        return Holding;
    }

    /// <summary>The host published the map view carrying the intro (its epoch): friends that follow it from now count.</summary>
    public void ViewPublished(uint epoch)
    {
        if (Holding && _viewEpoch == 0 && epoch != 0)
        {
            _viewEpoch = epoch;
        }
    }

    /// <summary>
    /// A friend's newest follow report: <paramref name="following"/> view <paramref name="epoch"/> (false: still on its way,
    /// or it couldn't). A report for the intro's view or a later one, while following, means its map is in.
    /// </summary>
    public void Report(ulong friend, uint epoch, bool following, double now)
    {
        if (Holding && _viewEpoch != 0 && following && epoch >= _viewEpoch && _arrived.TryGetValue(friend, out double? at) && at == null)
        {
            _arrived[friend] = Math.Max(0, now - _since);
        }
    }

    /// <summary>A friend left the session: no longer waited for.</summary>
    public void FriendLeft(ulong friend) => _arrived.Remove(friend);

    /// <summary>
    /// Whether the intro goes on now, and why: every friend's map is in, none is left, or <see cref="MaxWaitSeconds"/> have
    /// passed. Releases the hold when it says so.
    /// </summary>
    public bool ShouldRelease(double now, out string why)
    {
        why = string.Empty;
        if (!Holding)
        {
            return false;
        }

        double waited = Math.Max(0, now - _since);
        int missing = 0;
        foreach (double? at in _arrived.Values)
        {
            missing += at == null ? 1 : 0; // every frame of a hold: the values' own enumerator, nothing boxed
        }

        if (_arrived.Count == 0)
        {
            why = "no friend is left in the session";
        }
        else if (missing == 0)
        {
            why = "every friend's game has the map";
        }
        else if (waited >= MaxWaitSeconds)
        {
            why = string.Create(CultureInfo.InvariantCulture, $"{missing} friend(s) still loading the map after {MaxWaitSeconds:0} s");
        }
        else
        {
            return false;
        }

        Holding = false;
        Held = waited;
        return true;
    }

    /// <summary>Each friend's wait (seconds), or null for one whose map wasn't in, for the log; <paramref name="nameOf"/> names them.</summary>
    public string DescribeWaits(Func<ulong, string> nameOf)
    {
        ArgumentNullException.ThrowIfNull(nameOf);
        return _arrived.Count == 0
            ? "no friends"
            : string.Join(", ", _arrived.Select(p => p.Value is double s
                ? string.Create(CultureInfo.InvariantCulture, $"{nameOf(p.Key)} had it after {s * 1000:0} ms")
                : $"{nameOf(p.Key)} didn't"));
    }

    /// <summary>Lets go of a hold without a release (the session ended, or the map went away).</summary>
    public void Abandon()
    {
        Holding = false;
        _arrived.Clear();
        Act = 0;
    }
}

/// <summary>
/// Friend side: a friend's copy of an act's intro waits for the host's to start (the map view's
/// <see cref="SceneViewKeys.IntroStarted"/>), so every screen plays it together; at most <see cref="MaxWaitSeconds"/>.
/// Not thread-safe.
/// </summary>
public sealed class ActIntroFollow
{
    /// <summary>The longest a friend's intro waits for the host's (the host waits up to 6 s for the slowest friend).</summary>
    public const double MaxWaitSeconds = 8;

    private double _since;

    /// <summary>The act whose intro waits here (0: none).</summary>
    public int Pending { get; private set; }

    /// <summary>A new session, or the map went away.</summary>
    public void Reset() => Pending = 0;

    /// <summary>This PC loaded the map of <paramref name="act"/>, whose intro the host played, at <paramref name="now"/>; true to play it at once.</summary>
    public bool Arrived(int act, bool hostStarted, double now)
    {
        if (hostStarted)
        {
            Pending = 0;
            return true;
        }

        Pending = act;
        _since = now;
        return false;
    }

    /// <summary>
    /// Whether the waiting intro plays now: the host's started, or the wait is over (<paramref name="timedOut"/>). It stays
    /// waiting until the caller says it was played (<see cref="Played"/>).
    /// </summary>
    public bool Due(bool hostStarted, double now, out bool timedOut)
    {
        timedOut = false;
        if (Pending == 0)
        {
            return false;
        }

        if (!hostStarted && now - _since < MaxWaitSeconds)
        {
            return false;
        }

        timedOut = !hostStarted;
        return true;
    }

    /// <summary>The waiting intro was played (or dropped).</summary>
    public void Played() => Pending = 0;
}
