using System;
using System.Collections.Generic;

namespace T2TOnline.Core.Stream;

/// <summary>Why a friend's object takes the host's component ids over again (<see cref="RetakeSchedule"/>).</summary>
[Flags]
public enum RetakeReasons
{
    /// <summary>None.</summary>
    None = 0,

    /// <summary>The host announced the object again with another component list (its game added or removed one).</summary>
    HostChanged = 1,

    /// <summary>
    /// This PC's game changed the object's components (a follower gave or took a talent): taken over again a frame later,
    /// once a component it removed has gone (Unity destroys at the frame's end, and until then it's still in the list).
    /// </summary>
    ChangedHere = 2,
}

/// <summary>
/// A friend's objects waiting to take the host's component ids over again, each by the frame it's due. A request for an
/// object already waiting merges: the later frame and both reasons, so the take-over runs once, after everything that
/// asked for it. At most <see cref="MaxPending"/> objects wait; more are counted and dropped. Nothing allocated while
/// empty. Never throws.
/// </summary>
public sealed class RetakeSchedule
{
    /// <summary>Most objects waiting at once.</summary>
    public const int MaxPending = 256;

    private readonly Dictionary<ushort, (long Due, RetakeReasons Reasons)> _pending = new();

    /// <summary>Objects waiting.</summary>
    public int Count => _pending.Count;

    /// <summary>Requests dropped because <see cref="MaxPending"/> objects were already waiting.</summary>
    public long Dropped { get; private set; }

    /// <summary>Object <paramref name="netId"/> takes the host's ids over again at frame <paramref name="dueFrame"/> or later.</summary>
    public void Request(ushort netId, long dueFrame, RetakeReasons reasons)
    {
        if (_pending.TryGetValue(netId, out (long Due, RetakeReasons Reasons) known))
        {
            _pending[netId] = (Math.Max(known.Due, dueFrame), known.Reasons | reasons);
            return;
        }

        if (_pending.Count >= MaxPending)
        {
            Dropped++;
            return;
        }

        _pending[netId] = (dueFrame, reasons);
    }

    /// <summary>
    /// Moves objects due by <paramref name="frame"/> into <paramref name="due"/> (cleared first), with their reasons, at
    /// most <paramref name="max"/> of them: the rest stay due for the next call (a host announcing many lists at once can't
    /// make one frame take them all over).
    /// </summary>
    public void TakeDue(long frame, List<(ushort NetId, RetakeReasons Reasons)> due, int max = int.MaxValue)
    {
        ArgumentNullException.ThrowIfNull(due);
        due.Clear();
        if (_pending.Count == 0)
        {
            return;
        }

        foreach (KeyValuePair<ushort, (long Due, RetakeReasons Reasons)> pair in _pending)
        {
            if (due.Count >= max)
            {
                break;
            }

            if (pair.Value.Due <= frame)
            {
                due.Add((pair.Key, pair.Value.Reasons));
            }
        }

        foreach ((ushort netId, RetakeReasons _) in due)
        {
            _pending.Remove(netId);
        }
    }

    /// <summary>Forgets everything (a new playback).</summary>
    public void Clear()
    {
        _pending.Clear();
        Dropped = 0;
    }
}
