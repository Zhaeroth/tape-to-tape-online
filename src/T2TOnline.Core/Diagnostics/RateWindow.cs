using System;
using System.Collections.Generic;

namespace T2TOnline.Core.Diagnostics;

/// <summary>One key's growth over a <see cref="RateWindow{TKey}"/> window: counts, bytes and the seconds they took.</summary>
/// <param name="Count">How much the count grew.</param>
/// <param name="Bytes">How much the bytes grew.</param>
/// <param name="Seconds">The window's length for this key (never 0).</param>
public readonly record struct RateSample(long Count, long Bytes, double Seconds)
{
    /// <summary>Counts a second.</summary>
    public double CountPerSecond => Count / Seconds;

    /// <summary>Kilobits a second.</summary>
    public double Kbps => Bytes * 8 / Seconds / 1000;

    /// <summary>Bytes per count (0 with none).</summary>
    public long BytesPerCount => Count == 0 ? 0 : Bytes / Count;
}

/// <summary>
/// Rates from running totals, per key, over report windows. The host's stream report divides each friend's
/// snapshots and bytes since the last report by the window; a window cleared at every match's start, while the
/// friend's totals run for the whole session, makes the first report of every match after the first divide the session's
/// total by 10 s ("6117/s snapshots, 31457 kbps"). Here a new window (<see cref="Rebase"/>) starts from each key's totals
/// as they are, never from zero; a key first seen inside a window (a friend who joined since) starts from zero at the
/// window's start (a new friend's totals are new); a total that went down (a rejoining friend's new totals) reads from
/// zero; and the window's length is the real time since its start, so a report forced early (the match ending) isn't
/// divided by a whole interval. Not thread-safe.
/// </summary>
/// <typeparam name="TKey">What has totals (a friend, by Steam id).</typeparam>
public sealed class RateWindow<TKey>
    where TKey : notnull
{
    private const double ShortestSeconds = 0.001;

    private readonly Dictionary<TKey, (long Count, long Bytes, double At)> _base = new();
    private double _windowStart = double.NaN;

    /// <summary>Keys with a baseline.</summary>
    public int Keys => _base.Count;

    /// <summary>
    /// A new window starts now (<paramref name="nowSeconds"/>) for every key listed, from its totals as they are (a match
    /// starting: its first report counts only what was sent in it). Keys not listed are forgotten.
    /// </summary>
    public void Rebase(double nowSeconds, IEnumerable<(TKey Key, long Count, long Bytes)> totals)
    {
        ArgumentNullException.ThrowIfNull(totals);
        _base.Clear();
        _windowStart = nowSeconds;
        foreach ((TKey key, long count, long bytes) in totals)
        {
            _base[key] = (count, bytes, nowSeconds);
        }
    }

    /// <summary>
    /// What <paramref name="key"/>'s totals grew by since its baseline, over the time since; its baseline becomes these
    /// totals at <paramref name="nowSeconds"/>.
    /// </summary>
    public RateSample Take(TKey key, long count, long bytes, double nowSeconds)
    {
        (long Count, long Bytes, double At) from = _base.TryGetValue(key, out var known) ? known
            : (0, 0, double.IsNaN(_windowStart) ? nowSeconds : _windowStart);
        if (count < from.Count || bytes < from.Bytes)
        {
            // New totals under the same key: counted from zero.
            from = (0, 0, from.At);
        }

        _base[key] = (count, bytes, nowSeconds);
        return new RateSample(count - from.Count, bytes - from.Bytes, Math.Max(ShortestSeconds, nowSeconds - from.At));
    }

    /// <summary>Marks the end of a report: the next key first seen counts from here.</summary>
    public void EndWindow(double nowSeconds) => _windowStart = nowSeconds;

    /// <summary>Forgets <paramref name="key"/> (a friend left: if they rejoin, their new totals count from zero).</summary>
    public void Remove(TKey key) => _base.Remove(key);

    /// <summary>Forgets everything.</summary>
    public void Clear()
    {
        _base.Clear();
        _windowStart = double.NaN;
    }
}
