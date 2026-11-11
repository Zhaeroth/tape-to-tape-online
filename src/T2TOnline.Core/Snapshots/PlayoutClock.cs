using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace T2TOnline.Core.Snapshots;

/// <summary>
/// Decides what host time a friend renders at: as close behind the host as the link
/// allows, without running out of snapshots and without visible jumps.
/// <list type="bullet">
/// <item><b>Where the host is.</b> Each snapshot's arrival time minus its host time is the clock offset plus that
/// snapshot's delay. The smallest over the last few seconds is the fastest path; how far the others arrive behind it is
/// the jitter.</item>
/// <item><b>How far behind to render.</b> Render time must stay before the newest snapshot, which only advances when the
/// next arrives: one snapshot interval, plus the jitter (95th percentile), plus a small margin, plus a cushion that
/// learns from experience. Each time render time catches up with the newest snapshot (a hold: an irregular gap on the
/// host, a lost packet), the cushion grows by <see cref="CushionStep"/>; while there are none it shrinks by
/// <see cref="CushionDecayPerSecond"/>. That's the interpolation delay: short on a clean link, longer only as far as the
/// link and the host's own timing need.</item>
/// <item><b>Getting there smoothly.</b> The render clock runs at real speed, warped by at most
/// <see cref="MaxWarp"/> (5%) to close any gap to the target, so motion never jumps; only a gap over
/// <see cref="SnapThreshold"/> (a stall, a long pause) is closed at once.</item>
/// </list>
/// Times are microseconds: host times from the snapshots, local times from this PC's own clock. Arrival times must be
/// when a snapshot really arrived (the network layer's receive stamp), not when a frame got round to it: a hitch on this
/// PC isn't network jitter, and counting it as such made the delay balloon to a quarter second in early builds.
/// </summary>
public sealed class PlayoutClock
{
    /// <summary>The most the render clock runs fast or slow to close a gap (0.05 = 5%).</summary>
    public const double MaxWarp = 0.05;

    /// <summary>A gap to the target larger than this is closed at once instead of warped.</summary>
    public const long SnapThreshold = 250_000;

    /// <summary>Added to the measured need, for timing noise on this PC.</summary>
    public const long Margin = 4_000;

    /// <summary>The smallest delay ever used.</summary>
    public const long MinDelay = 8_000;

    /// <summary>The largest delay ever used (beyond it, the link is too bad to hide).</summary>
    public const long MaxDelay = 400_000;

    /// <summary>How long arrivals are remembered for the offset and jitter.</summary>
    public const long Window = 3_000_000;

    /// <summary>
    /// A snapshot stamped this far ahead of where the host is (by the fastest arrivals), or this far behind the newest
    /// snapshot, is ignored.
    /// </summary>
    public const long OutlierLimit = 5_000_000;

    /// <summary>
    /// Ignored snapshots arriving one after another for this long (and at least <see cref="ReanchorCount"/> of them) mean
    /// the stamps really moved, or the first one was wrong: the clock starts over from the newest (<see cref="Reanchors"/>).
    /// </summary>
    public const long ReanchorAfter = 1_000_000;

    /// <summary>The fewest ignored snapshots in a row that start the clock over (see <see cref="ReanchorAfter"/>).</summary>
    public const int ReanchorCount = 10;

    /// <summary>What the cushion grows by at each hold (render time caught up with the newest snapshot).</summary>
    public const long CushionStep = 4_000;

    /// <summary>What the cushion shrinks by per second without a hold.</summary>
    public const long CushionDecayPerSecond = 2_000;

    /// <summary>Seconds to close a gap at full warp: the correction rate is gap / this, capped at <see cref="MaxWarp"/>.</summary>
    private const double CorrectionSeconds = 0.5;

    /// <summary>Host spacings remembered for the median.</summary>
    private const int SpacingsKept = 120;

    private readonly Queue<(long Arrival, long Offset)> _arrivals = new();
    private readonly Queue<long> _spacings = new();
    private readonly long[] _spacingScratch = new long[SpacingsKept];
    private readonly List<long> _scratch = new();
    private long _lastHost = long.MinValue;
    private long _lastLocal = long.MinValue;
    private double _render = double.NaN;
    private long _baseOffset;
    private double _cushion;
    private bool _holding;
    private int _ignoredRun;
    private long _ignoredSince;

    /// <summary>True once a snapshot has arrived.</summary>
    public bool Started => _lastHost != long.MinValue;

    /// <summary>The interpolation delay aimed for now.</summary>
    public long TargetDelay => Math.Clamp(Spacing + Jitter + Margin + (long)_cushion, MinDelay, MaxDelay);

    /// <summary>The learned extra delay (see <see cref="CushionStep"/>).</summary>
    public long Cushion => (long)_cushion;

    /// <summary>Snapshots ignored as stamped far ahead of the host or far behind the newest (see <see cref="OutlierLimit"/>).</summary>
    public int Outliers { get; private set; }

    /// <summary>
    /// Times the clock started over from a run of ignored snapshots (see <see cref="ReanchorAfter"/>). A caller keeping a
    /// timeline drops it when this goes up: it may hold the stamps that were wrong.
    /// </summary>
    public int Reanchors { get; private set; }

    /// <summary>Times render time caught up with the newest snapshot and had to hold (each a run of <see cref="Underruns"/> frames).</summary>
    public int Holds { get; private set; }

    /// <summary>The measured jitter (95th percentile of how late snapshots arrive behind the fastest).</summary>
    public long Jitter { get; private set; }

    /// <summary>The typical time between snapshots on the host (median).</summary>
    public long Spacing { get; private set; } = 16_667;

    /// <summary>The newest host time received.</summary>
    public long NewestHostTime => _lastHost;

    /// <summary>The current warp: 1.05 runs 5% fast, 0.95 runs 5% slow.</summary>
    public double Rate { get; private set; } = 1;

    /// <summary>Times the render clock was moved at once (start, stalls).</summary>
    public int Snaps { get; private set; }

    /// <summary>Calls to <see cref="Advance"/> where render time had caught up with the newest snapshot (nothing new to show).</summary>
    public int Underruns { get; private set; }

    /// <summary>
    /// A snapshot from the host, stamped <paramref name="hostTime"/>, arrived at <paramref name="localTime"/> (when it
    /// really arrived, not when this PC's frame got to it). False when it's ignored: stamped more than
    /// <see cref="OutlierLimit"/> ahead of where the host is, or that far behind the newest snapshot (one corrupt or hostile
    /// stamp would otherwise hold render time there). Callers keep ignored snapshots out of the timeline too. Only a run of
    /// them lasting <see cref="ReanchorAfter"/> is believed: the clock starts over from the newest, which is taken.
    /// </summary>
    public bool OnSnapshot(long hostTime, long localTime)
    {
        if (Started && (localTime - hostTime < _baseOffset - OutlierLimit || hostTime < _lastHost - OutlierLimit))
        {
            Outliers++;
            if (_ignoredRun++ == 0)
            {
                _ignoredSince = localTime;
            }

            if (_ignoredRun < ReanchorCount || localTime - _ignoredSince < ReanchorAfter)
            {
                return false;
            }

            // Nothing but outliers for a second: the host's stamps moved for good (or the first snapshot's was wrong, and
            // every true one since is "far behind" it). Ignoring them for ever would freeze the match on one frame.
            Reanchor();
        }

        _ignoredRun = 0;

        if (_lastHost != long.MinValue && hostTime > _lastHost)
        {
            _spacings.Enqueue(hostTime - _lastHost);
            if (_spacings.Count > SpacingsKept)
            {
                _spacings.Dequeue();
            }

            _spacings.CopyTo(_spacingScratch, 0);
            Spacing = Percentile(_spacingScratch.AsSpan(0, _spacings.Count), 0.5);
        }

        _lastHost = Math.Max(_lastHost, hostTime);
        _arrivals.Enqueue((localTime, localTime - hostTime));
        while (_arrivals.Count > 1 && localTime - _arrivals.Peek().Arrival > Window)
        {
            _arrivals.Dequeue();
        }

        long lowest = long.MaxValue;
        foreach ((_, long offset) in _arrivals)
        {
            lowest = Math.Min(lowest, offset);
        }

        _baseOffset = lowest;
        _scratch.Clear();
        foreach ((_, long offset) in _arrivals)
        {
            _scratch.Add(offset - lowest);
        }

        Jitter = Percentile(CollectionsMarshal.AsSpan(_scratch), 0.95);
        return true;
    }

    /// <summary>
    /// The host time to render at <paramref name="localTime"/>; call once per frame. Never moves backwards, and never past
    /// the newest snapshot (it holds there and counts an underrun).
    /// </summary>
    public long Advance(long localTime)
    {
        if (!Started)
        {
            return long.MinValue;
        }

        double elapsed = _lastLocal == long.MinValue ? 0 : Math.Max(0, localTime - _lastLocal);
        double target = localTime - _baseOffset - TargetDelay;
        // The gap is measured from where render time would be at real speed: a long frame here moves both alike, so it's
        // never mistaken for falling behind.
        double gap = double.IsNaN(_render) ? double.PositiveInfinity : target - (_render + elapsed);
        // During a stall render time holds at the newest snapshot however far behind it falls: a jump needs something newer
        // to jump to.
        if (gap > SnapThreshold && (double.IsNaN(_render) || _lastHost - _render > SnapThreshold))
        {
            // Starting, or far behind after a stall: jump forward to where the link allows.
            _render = target;
            Snaps++;
            Rate = 1;
        }
        else if (gap < -SnapThreshold)
        {
            // Far ahead (the link just got much slower): wait for it rather than going backwards.
            Rate = 0;
        }
        else
        {
            Rate = 1 + Math.Clamp(gap / (CorrectionSeconds * 1_000_000), -MaxWarp, MaxWarp);
            _render += elapsed * Rate;
        }

        _lastLocal = localTime;
        if (_render >= _lastHost)
        {
            _render = _lastHost;
            Underruns++;
            if (!_holding)
            {
                _holding = true;
                Holds++;
                _cushion = Math.Min(_cushion + CushionStep, MaxDelay);
            }
        }
        else
        {
            _holding = false;
            _cushion = Math.Max(0, _cushion - (elapsed / 1_000_000 * CushionDecayPerSecond));
        }

        return (long)_render;
    }

    /// <summary>Forgets everything (a new stream).</summary>
    public void Reset()
    {
        _arrivals.Clear();
        _spacings.Clear();
        _lastHost = _lastLocal = long.MinValue;
        _render = double.NaN;
        _cushion = 0;
        _holding = false;
        Jitter = 0;
        Spacing = 16_667;
        Rate = 1;
        Snaps = Underruns = Holds = Outliers = Reanchors = 0;
        _ignoredRun = 0;
    }

    /// <summary>Starts over (as <see cref="Reset"/>) but keeps the counts, which are about the whole stream.</summary>
    private void Reanchor()
    {
        (int snaps, int underruns, int holds, int outliers, int reanchors) = (Snaps, Underruns, Holds, Outliers, Reanchors);
        Reset();
        (Snaps, Underruns, Holds, Outliers, Reanchors) = (snaps, underruns, holds, outliers, reanchors + 1);
    }

    /// <summary>The value at <paramref name="fraction"/> of <paramref name="values"/>, which it sorts in place (scratch, no copy).</summary>
    private static long Percentile(Span<long> values, double fraction)
    {
        if (values.Length == 0)
        {
            return 0;
        }

        values.Sort();
        return values[Math.Min(values.Length - 1, (int)(fraction * values.Length))];
    }
}
