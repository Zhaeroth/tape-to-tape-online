using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace T2TOnline.Core.Campaign;

/// <summary>
/// Host side: the beaten node's explosion and the map's appear sound held for friends' maps. Each PC's game
/// plays both at its own map's reveal, and a friend's map loads only after the host's view of it (typically about 0.6 s
/// later): the host's would come that long before theirs. The loading screen isn't touched: only these two wait, once
/// the game wants to play them, until every friend in the session reports following a map view at or after the first one of
/// this map, and then as long again as the host's own reveal took after its map was in (a friend's reveal takes as long after
/// its own). <see cref="MaxHoldSeconds"/> past the moment the game asked, each goes whatever friends say. Times are seconds on
/// one clock. Pure logic; the glue holds the game's calls. Not thread-safe.
/// </summary>
public sealed class MapRevealHold
{
    /// <summary>The longest either waits, from the moment the game would have played it.</summary>
    public const double MaxHoldSeconds = 2;

    /// <summary>How long a map's hold stays ready for its explosion after the map was made (a slow reveal's menus, the loading screen).</summary>
    public const double ReadySeconds = 15;

    private readonly Dictionary<ulong, double?> _arrived = new();
    private uint _viewEpoch;
    private double _viewAt = double.NaN;
    private double _since;

    /// <summary>True from a map's making until <see cref="ReadySeconds"/> later or <see cref="End"/>.</summary>
    public bool Active { get; private set; }

    /// <summary>The host's game makes a map at <paramref name="now"/> with <paramref name="friends"/> in the session. False with none: nothing waits.</summary>
    public bool Begin(IEnumerable<ulong> friends, double now)
    {
        ArgumentNullException.ThrowIfNull(friends);
        _arrived.Clear();
        foreach (ulong friend in friends)
        {
            _arrived[friend] = null;
        }

        _viewEpoch = 0;
        _viewAt = double.NaN;
        _since = now;
        Active = _arrived.Count > 0;
        return Active;
    }

    /// <summary>The host published a map view (its epoch): the first one after the map was made is what friends' reports count from.</summary>
    public void ViewPublished(uint epoch, double now)
    {
        if (Active && _viewEpoch == 0 && epoch != 0)
        {
            _viewEpoch = epoch;
            _viewAt = now;
        }
    }

    /// <summary>
    /// A friend's newest follow report: <paramref name="following"/> view <paramref name="epoch"/>. One for this map's first
    /// view or a later one, while following, means its map is in (noted once, at <paramref name="now"/>).
    /// </summary>
    public void Report(ulong friend, uint epoch, bool following, double now)
    {
        if (Active && _viewEpoch != 0 && following && epoch >= _viewEpoch && _arrived.TryGetValue(friend, out double? at) && at == null)
        {
            _arrived[friend] = now;
        }
    }

    /// <summary>A friend left the session: no longer waited for.</summary>
    public void FriendLeft(ulong friend) => _arrived.Remove(friend);

    /// <summary>
    /// Whether what the game asked to play at <paramref name="dueAt"/> plays now, and why: every friend's map is in and as long
    /// has passed as the host's reveal took after its map was in, no friend is left, or <see cref="MaxHoldSeconds"/> have passed.
    /// </summary>
    public bool Release(double dueAt, double now, out string why)
    {
        why = string.Empty;
        // Every frame of a hold: the values' own enumerator, nothing boxed.
        int missing = 0;
        double last = double.MinValue;
        foreach (double? at in _arrived.Values)
        {
            if (at is double arrived)
            {
                last = Math.Max(last, arrived);
            }
            else
            {
                missing++;
            }
        }

        if (_arrived.Count == 0)
        {
            why = "no friend is left in the session";
            return true;
        }

        if (missing == 0)
        {
            // The host's reveal came this long after its view; a friend's comes this long after its report.
            double reveal = double.IsNaN(_viewAt) ? 0 : Math.Max(0, dueAt - _viewAt);
            if (now >= last + reveal || now - dueAt >= MaxHoldSeconds)
            {
                why = "every friend's game has the map";
                return true;
            }

            return false;
        }

        if (now - dueAt >= MaxHoldSeconds)
        {
            why = string.Create(CultureInfo.InvariantCulture, $"{missing} friend(s) still loading the map after {MaxHoldSeconds:0} s");
            return true;
        }

        return false;
    }

    /// <summary>True when nothing more can come for this map: <see cref="ReadySeconds"/> have passed since it was made.</summary>
    public bool Expired(double now) => Active && now - _since >= ReadySeconds;

    /// <summary>Each friend's time to the map since this map was made, or that they didn't have it, for the log; <paramref name="nameOf"/> names them.</summary>
    public string DescribeWaits(Func<ulong, string> nameOf)
    {
        ArgumentNullException.ThrowIfNull(nameOf);
        return _arrived.Count == 0
            ? "no friends"
            : string.Join(", ", _arrived.Select(p => p.Value is double at
                ? string.Create(CultureInfo.InvariantCulture, $"{nameOf(p.Key)} had it after {(at - (double.IsNaN(_viewAt) ? _since : _viewAt)) * 1000:0} ms")
                : $"{nameOf(p.Key)} didn't"));
    }

    /// <summary>Lets go: nothing waits for this map any more.</summary>
    public void End()
    {
        Active = false;
        _arrived.Clear();
    }
}
