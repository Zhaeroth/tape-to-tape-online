using System.Globalization;

namespace T2TOnline.Core.Diagnostics;

/// <summary>
/// One timed call and what it was: its length, what it did, the slowest part inside it, and the highest .NET garbage
/// collection generation that ran during it. The names are held by reference: noting one allocates nothing.
/// </summary>
/// <param name="Micros">How long the call took, in microseconds.</param>
/// <param name="What">What it did ("GameplayEvents.Knockout", "HUD values").</param>
/// <param name="Inner">Its slowest part (an event's listener), or null.</param>
/// <param name="InnerMicros">How long that part took, in microseconds.</param>
/// <param name="Collection">The highest .NET garbage collection generation that ran during it, or -1.</param>
public readonly record struct SlowCall(double Micros, string What, string? Inner, double InnerMicros, int Collection)
{
    /// <summary>
    /// "150.0 ms: GameplayEvents.Knockout, 149.9 ms of it in SoundManager; a .NET garbage collection (generation 2) ran
    /// during it", or the empty string for nothing measured.
    /// </summary>
    public override string ToString()
    {
        if (Micros <= 0 || string.IsNullOrEmpty(What))
        {
            return string.Empty;
        }

        CultureInfo c = CultureInfo.InvariantCulture;
        string inner = Inner == null ? string.Empty : string.Create(c, $", {InnerMicros / 1000.0:0.0} ms of it in {Inner}");
        string collection = Collection < 0 ? string.Empty : string.Create(c, $"; a .NET garbage collection (generation {Collection}) ran during it");
        return string.Create(c, $"{Micros / 1000.0:0.0} ms: {What}{inner}{collection}");
    }
}

/// <summary>
/// The slowest of a repeated call (each message a stream player delivers, say), in fixed memory: over the whole run and
/// since the last <see cref="TakeInterval"/>, beside a <see cref="CostMeter"/> that says how slow, so a report can say
/// what was slow. A caller times every call, asks <see cref="IsSlowest"/>, and gathers what the call was only for a new
/// slowest (<see cref="Note"/>).
/// </summary>
public sealed class SlowestCall
{
    private SlowCall _interval;
    private SlowCall _total;

    /// <summary>The slowest since the last <see cref="TakeInterval"/> (default before any).</summary>
    public SlowCall Interval => _interval;

    /// <summary>The slowest of the whole run (default before any).</summary>
    public SlowCall Total => _total;

    /// <summary>True when a call of <paramref name="micros"/> is the slowest of the interval or the run so far.</summary>
    public bool IsSlowest(double micros) => micros > _interval.Micros || micros > _total.Micros;

    /// <summary>Notes one call; it replaces the interval's and the run's slowest where it's slower.</summary>
    public void Note(in SlowCall call)
    {
        if (call.Micros > _interval.Micros)
        {
            _interval = call;
        }

        if (call.Micros > _total.Micros)
        {
            _total = call;
        }
    }

    /// <summary>The interval's slowest, and starts a new interval.</summary>
    public SlowCall TakeInterval()
    {
        SlowCall slowest = _interval;
        _interval = default;
        return slowest;
    }

    /// <summary>Forgets everything (a new run).</summary>
    public void Clear()
    {
        _interval = default;
        _total = default;
    }
}
