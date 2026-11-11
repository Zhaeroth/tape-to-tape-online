using System;

namespace T2TOnline.Core.Snapshots;

/// <summary>
/// Mean and percentiles of a long run of delays in fixed memory: quarter-millisecond buckets up to a second, everything
/// longer in the last one. A three-hour match at 144 frames a second is 1.5 million samples; this keeps 4,000 counters.
/// </summary>
public sealed class DelayHistogram
{
    /// <summary>Bucket width in microseconds.</summary>
    public const long BucketMicros = 250;

    /// <summary>Delays from this up share the last bucket.</summary>
    public const long MaxMicros = 1_000_000;

    private readonly long[] _buckets = new long[(MaxMicros / BucketMicros) + 1];
    private double _sum;

    /// <summary>Delays added.</summary>
    public long Count { get; private set; }

    /// <summary>
    /// The mean delay in microseconds, 0 when empty: exact over the delays as added, each clamped to 0 to
    /// <see cref="MaxMicros"/> (so a delay over a second counts as one second).
    /// </summary>
    public double Mean => Count == 0 ? 0 : _sum / Count;

    /// <summary>Adds one delay in microseconds; negative ones count as 0.</summary>
    public void Add(long micros)
    {
        long clamped = Math.Clamp(micros, 0, MaxMicros);
        _buckets[clamped / BucketMicros]++;
        _sum += clamped;
        Count++;
    }

    /// <summary>
    /// The delay at fraction <paramref name="p"/> (0 to 1) of the sorted samples, to the bucket's upper edge (so never
    /// understated by more than nothing, overstated by at most <see cref="BucketMicros"/>). 0 when empty.
    /// </summary>
    public long Percentile(double p)
    {
        if (Count == 0)
        {
            return 0;
        }

        long rank = Math.Clamp((long)Math.Ceiling(Math.Clamp(p, 0, 1) * Count), 1, Count);
        long seen = 0;
        for (int i = 0; i < _buckets.Length; i++)
        {
            seen += _buckets[i];
            if (seen >= rank)
            {
                return Math.Min((i + 1) * BucketMicros, MaxMicros);
            }
        }

        return MaxMicros;
    }

    /// <summary>Forgets every sample.</summary>
    public void Clear()
    {
        Array.Clear(_buckets);
        _sum = 0;
        Count = 0;
    }
}
