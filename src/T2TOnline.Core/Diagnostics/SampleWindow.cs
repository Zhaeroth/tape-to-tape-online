using System;
using System.Collections.Generic;
using System.Globalization;

namespace T2TOnline.Core.Diagnostics;

/// <summary>Collects measurements over a window and summarizes them (mean, 95th percentile, maximum).</summary>
public sealed class SampleWindow
{
    private readonly List<double> _samples = new();

    /// <summary>Number of samples in the window.</summary>
    public int Count => _samples.Count;

    /// <summary>Adds one measurement.</summary>
    public void Add(double value) => _samples.Add(value);

    /// <summary>Empties the window.</summary>
    public void Clear() => _samples.Clear();

    /// <summary>Summarizes the window. All zero when it's empty.</summary>
    public SampleSummary Summarize()
    {
        if (_samples.Count == 0)
        {
            return default;
        }

        var sorted = new double[_samples.Count];
        _samples.CopyTo(sorted);
        Array.Sort(sorted);
        double sum = 0;
        foreach (double value in sorted)
        {
            sum += value;
        }

        // Nearest-rank percentile: the smallest value with at least 95% of samples at or below it.
        int rank = (int)Math.Ceiling(0.95 * sorted.Length);
        return new SampleSummary(sorted.Length, sum / sorted.Length, sorted[Math.Max(rank, 1) - 1], sorted[^1]);
    }
}

/// <summary>A summary of a <see cref="SampleWindow"/>.</summary>
/// <param name="Count">Number of samples.</param>
/// <param name="Mean">Arithmetic mean.</param>
/// <param name="P95">95th percentile (nearest rank).</param>
/// <param name="Max">Largest sample.</param>
public readonly record struct SampleSummary(int Count, double Mean, double P95, double Max)
{
    /// <summary>"mean/p95/max" with no decimals, for logs.</summary>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Mean:0}/{P95:0}/{Max:0}");
}
