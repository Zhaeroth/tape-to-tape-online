using System;
using System.Diagnostics;
using System.Globalization;

namespace T2TOnline.Core.Diagnostics;

/// <summary>
/// What one repeated step costs (a per-frame or per-snapshot piece of work on the game's main thread), in fixed memory:
/// how often it ran, the mean, the 95th percentile and the longest, in microseconds. Two windows run side by side: the
/// whole run (for a stop line) and since the last <see cref="TakeInterval"/> (for a periodic report). Durations go into
/// buckets eight to an octave from 1 µs to about 16 s, so a percentile is never understated and overstated by at most
/// 9%, and never above the longest seen. Adding one costs a logarithm; nothing allocates.
/// </summary>
public sealed class CostMeter
{
    private const int PerOctave = 8;
    private const int Octaves = 24;
    private const int BucketCount = 1 + (PerOctave * Octaves);

    private readonly Window _total = new();
    private readonly Window _interval = new();

    /// <summary>The step's name in reports.</summary>
    public string Name { get; }

    /// <summary>A meter for the step named <paramref name="name"/>.</summary>
    public CostMeter(string name) => Name = name ?? throw new ArgumentNullException(nameof(name));

    /// <summary>Times measured over the whole run.</summary>
    public long Count => _total.Count;

    /// <summary>A start stamp for <see cref="AddSince"/> (<see cref="Stopwatch.GetTimestamp"/>).</summary>
    public static long Start() => Stopwatch.GetTimestamp();

    /// <summary>The time since <paramref name="startTimestamp"/> (from <see cref="Start"/>), in microseconds.</summary>
    public static double MicrosSince(long startTimestamp) => (Stopwatch.GetTimestamp() - startTimestamp) * 1_000_000.0 / Stopwatch.Frequency;

    /// <summary>Adds the time since <paramref name="startTimestamp"/> (from <see cref="Start"/>).</summary>
    public void AddSince(long startTimestamp) => AddMicros(MicrosSince(startTimestamp));

    /// <summary>Adds one duration in microseconds (negative counts as 0).</summary>
    public void AddMicros(double micros)
    {
        double value = double.IsFinite(micros) ? Math.Max(0, micros) : 0;
        int bucket = BucketOf(value);
        _total.Add(value, bucket);
        _interval.Add(value, bucket);
    }

    /// <summary>The whole run so far.</summary>
    public CostSummary Total => _total.Summarize();

    /// <summary>Everything since the last call (or the start), and starts a new interval.</summary>
    public CostSummary TakeInterval()
    {
        CostSummary summary = _interval.Summarize();
        _interval.Clear();
        return summary;
    }

    /// <summary>Forgets everything (a new run).</summary>
    public void Clear()
    {
        _total.Clear();
        _interval.Clear();
    }

    /// <summary>"name 24/38/343 µs ×1200" for the whole run, or the empty string when nothing was measured.</summary>
    public string DescribeTotal() => Describe(Name, Total);

    /// <summary>"name 24/38/343 µs ×1200" since the last interval (starting a new one), or empty when nothing ran.</summary>
    public string DescribeInterval() => Describe(Name, TakeInterval());

    private static string Describe(string name, CostSummary summary) => summary.Count == 0 ? string.Empty : $"{name} {summary}";

    private static int BucketOf(double micros) =>
        micros < 1 ? 0 : Math.Min(BucketCount - 1, 1 + (int)Math.Floor(Math.Log2(micros) * PerOctave));

    /// <summary>The upper edge of a bucket in microseconds.</summary>
    private static double UpperEdge(int bucket) => bucket == 0 ? 1 : Math.Pow(2, (double)bucket / PerOctave);

    private sealed class Window
    {
        private readonly long[] _buckets = new long[BucketCount];
        private double _sum;
        private double _max;

        public long Count { get; private set; }

        public void Add(double micros, int bucket)
        {
            _buckets[bucket]++;
            _sum += micros;
            _max = Math.Max(_max, micros);
            Count++;
        }

        public CostSummary Summarize()
        {
            if (Count == 0)
            {
                return default;
            }

            long rank = Math.Max(1, (long)Math.Ceiling(0.95 * Count));
            long seen = 0;
            double p95 = _max;
            for (int i = 0; i < _buckets.Length; i++)
            {
                seen += _buckets[i];
                if (seen >= rank)
                {
                    p95 = Math.Min(UpperEdge(i), _max);
                    break;
                }
            }

            return new CostSummary(Count, _sum / Count, p95, _max, _sum / 1000.0);
        }

        public void Clear()
        {
            Array.Clear(_buckets);
            _sum = _max = 0;
            Count = 0;
        }
    }
}

/// <summary>A <see cref="CostMeter"/> window: how often, mean, 95th percentile and longest in µs, and the sum in ms.</summary>
/// <param name="Count">Times measured.</param>
/// <param name="MeanMicros">Mean duration.</param>
/// <param name="P95Micros">95th percentile (to its bucket's upper edge, never above the longest).</param>
/// <param name="MaxMicros">Longest duration.</param>
/// <param name="TotalMillis">All durations added up, in milliseconds.</param>
public readonly record struct CostSummary(long Count, double MeanMicros, double P95Micros, double MaxMicros, double TotalMillis)
{
    /// <summary>"24/38/343 µs ×1200" (mean/p95/max), for logs.</summary>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{MeanMicros:0}/{P95Micros:0}/{MaxMicros:0} µs ×{Count}");
}
