using System;
using System.Globalization;

namespace T2TOnline.Core.Campaign;

/// <summary>
/// How long the host's reads of one open menu took, for its close line: the first read apart from the rest, since a menu
/// type's first opening reads much slower than its later ones (the first use of each code path and of each game member it
/// touches), and a menu that opens once a session would otherwise show only that. Fixed size; never throws.
/// </summary>
public sealed class MenuReadTimes
{
    private double _restTotalMs;

    /// <summary>How many reads were timed.</summary>
    public int Count { get; private set; }

    /// <summary>The first read's time in milliseconds (0 before any).</summary>
    public double FirstMs { get; private set; }

    /// <summary>The slowest read after the first, in milliseconds (0 before a second).</summary>
    public double RestSlowestMs { get; private set; }

    /// <summary>The slowest read of all, in milliseconds (0 before any).</summary>
    public double SlowestMs => Math.Max(FirstMs, RestSlowestMs);

    /// <summary>The mean of the reads after the first, in milliseconds (0 before a second).</summary>
    public double RestMeanMs => Count > 1 ? _restTotalMs / (Count - 1) : 0;

    /// <summary>Notes one read of <paramref name="ms"/> milliseconds (a negative or non-finite time counts as 0).</summary>
    public void Add(double ms)
    {
        ms = double.IsFinite(ms) && ms > 0 ? ms : 0;
        if (Count == 0)
        {
            FirstMs = ms;
        }
        else
        {
            _restTotalMs += ms;
            RestSlowestMs = Math.Max(RestSlowestMs, ms);
        }

        Count++;
    }

    /// <summary>
    /// For the close line: <c>read 17 time(s): the first 9.2 ms, the rest 0.4 ms on average, 0.9 ms at most</c>;
    /// <c>read 1 time(s): 9.2 ms</c>; <c>read 0 time(s)</c>.
    /// </summary>
    public override string ToString() => Count switch
    {
        0 => "read 0 time(s)",
        1 => string.Create(CultureInfo.InvariantCulture, $"read 1 time(s): {FirstMs:0.0} ms"),
        _ => string.Create(CultureInfo.InvariantCulture, $"read {Count} time(s): the first {FirstMs:0.0} ms, the rest {RestMeanMs:0.0} ms on average, {RestSlowestMs:0.0} ms at most"),
    };
}
