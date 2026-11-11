using System;
using System.Collections.Generic;

namespace T2TOnline.Core.Snapshots;

/// <summary>
/// Snapshots in host-time order, sampled at a render time: the two around it and the blend factor between them. The
/// friend renders a little behind the newest snapshot (the interpolation delay), so there's normally one on each side.
/// </summary>
public sealed class SnapshotTimeline
{
    private readonly List<SnapshotFrame> _frames = new();
    private readonly int _capacity;

    /// <summary>Creates a timeline holding at most <paramref name="capacity"/> snapshots (the oldest go first).</summary>
    public SnapshotTimeline(int capacity = 256)
    {
        if (capacity < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), "At least two snapshots.");
        }

        _capacity = capacity;
    }

    /// <summary>Snapshots held.</summary>
    public int Count => _frames.Count;

    /// <summary>The newest snapshot's host time, or <see cref="long.MinValue"/> when empty.</summary>
    public long NewestTime => _frames.Count == 0 ? long.MinValue : _frames[^1].HostTimeMicros;

    /// <summary>The oldest snapshot's host time, or <see cref="long.MaxValue"/> when empty.</summary>
    public long OldestTime => _frames.Count == 0 ? long.MaxValue : _frames[0].HostTimeMicros;

    /// <summary>
    /// Adds a snapshot where its host time puts it (a late one goes between the two around it); when full, the oldest is
    /// dropped to make room. False, with nothing changed, for a duplicate (the same sequence at the same time) or, when
    /// full, one older than everything held.
    /// </summary>
    public bool Add(SnapshotFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        int index = _frames.Count;
        while (index > 0 && _frames[index - 1].HostTimeMicros > frame.HostTimeMicros)
        {
            index--;
        }

        if (index == 0 && _frames.Count >= _capacity)
        {
            return false;
        }

        // A duplicate sits somewhere in the run of snapshots with the same time.
        for (int i = index - 1; i >= 0 && _frames[i].HostTimeMicros == frame.HostTimeMicros; i--)
        {
            if (_frames[i].Sequence == frame.Sequence)
            {
                return false;
            }
        }

        _frames.Insert(index, frame);
        if (_frames.Count > _capacity)
        {
            _frames.RemoveAt(0);
        }

        return true;
    }

    /// <summary>
    /// The snapshots around <paramref name="renderTime"/> and how far between them it is (0 at <paramref name="from"/>,
    /// 1 at <paramref name="to"/>). Past the newest snapshot, both are the newest and the factor is 1 (hold). False
    /// when empty or when render time is before the oldest snapshot.
    /// </summary>
    public bool TrySample(long renderTime, out SnapshotFrame from, out SnapshotFrame to, out float t)
    {
        from = to = null!;
        t = 0;
        if (_frames.Count == 0 || renderTime < _frames[0].HostTimeMicros)
        {
            return false;
        }

        if (renderTime >= _frames[^1].HostTimeMicros)
        {
            from = to = _frames[^1];
            t = 1;
            return true;
        }

        int hi = _frames.Count - 1;
        int lo = 0;
        while (hi - lo > 1)
        {
            int mid = (lo + hi) / 2;
            if (_frames[mid].HostTimeMicros <= renderTime)
            {
                lo = mid;
            }
            else
            {
                hi = mid;
            }
        }

        from = _frames[lo];
        to = _frames[hi];
        long span = to.HostTimeMicros - from.HostTimeMicros;
        t = span <= 0 ? 1f : (float)(renderTime - from.HostTimeMicros) / span;
        return true;
    }

    /// <summary>Drops snapshots no longer needed for <paramref name="renderTime"/>: all but the newest one at or before it.</summary>
    public void TrimBefore(long renderTime)
    {
        int keep = 0;
        while (keep + 1 < _frames.Count && _frames[keep + 1].HostTimeMicros <= renderTime)
        {
            keep++;
        }

        if (keep > 0)
        {
            _frames.RemoveRange(0, keep);
        }
    }

    /// <summary>Forgets everything.</summary>
    public void Clear() => _frames.Clear();
}
