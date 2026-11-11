using System;
using System.Collections.Generic;
using System.Globalization;

namespace T2TOnline.Core.Diagnostics;

/// <summary>
/// Rates and growth of running totals from one sample to the next (the net overlay, F9): each total is remembered by its
/// key at every sample, so a per-second rate, the growth since the last sample or the loss over it needs no window of its
/// own and never disturbs the counters' other readers. A total seen for the first time, or one that went down (its counter
/// started over, as a rejoining friend's pad stream does), reads as 0 for that sample.
/// </summary>
public sealed class RunningTotals
{
    private readonly Dictionary<string, long> _last = new(StringComparer.Ordinal);
    private TimeSpan _sampledAt = TimeSpan.MinValue;

    /// <summary>Seconds between the last two samples (0 at the first one, which has nothing to compare with).</summary>
    public double Seconds { get; private set; }

    /// <summary>When the last sample started, or <see cref="TimeSpan.MinValue"/> before the first.</summary>
    public TimeSpan SampledAt => _sampledAt;

    /// <summary>Starts a new sample at <paramref name="now"/>. A clock that went backwards counts as a millisecond.</summary>
    public void Begin(TimeSpan now)
    {
        Seconds = _sampledAt == TimeSpan.MinValue ? 0 : Math.Max((now - _sampledAt).TotalSeconds, 0.001);
        _sampledAt = now;
    }

    /// <summary>Forgets every total and the last sample's time: the next sample is a first one.</summary>
    public void Reset()
    {
        _last.Clear();
        _sampledAt = TimeSpan.MinValue;
        Seconds = 0;
    }

    /// <summary>The per-second rate of a running total since the last sample (0 on the first, or after it went down).</summary>
    public double PerSecond(string key, long total)
    {
        bool known = _last.TryGetValue(key, out long previous);
        _last[key] = total;
        return !known || Seconds <= 0 || total < previous ? 0 : (total - previous) / Seconds;
    }

    /// <summary>How much a running total grew since the last sample (0 on the first, or after it went down).</summary>
    public long Delta(string key, long total)
    {
        bool known = _last.TryGetValue(key, out long previous);
        _last[key] = total;
        return !known || total < previous ? 0 : total - previous;
    }

    /// <summary>
    /// Loss over the last sample, in percent (0 to 100), from running counts of what was expected and what arrived: 0 when
    /// nothing was expected.
    /// </summary>
    public double LossPercent(string key, long expected, long received)
    {
        long sent = Delta(key + ".expected", expected);
        long got = Delta(key + ".received", received);
        return sent <= 0 ? 0 : Math.Clamp(100.0 * (sent - got) / sent, 0, 100);
    }

    /// <summary>Microseconds as milliseconds for display: one decimal under 10 ms ("9.5"), whole above ("49"), "-" when negative (none measured).</summary>
    public static string Ms(long micros) =>
        micros < 0 ? "-" : (micros / 1000.0).ToString(micros < 10_000 ? "0.0" : "0", CultureInfo.InvariantCulture);
}
