namespace T2TOnline.Core.Diagnostics;

/// <summary>
/// What a friend's periodic input line reports that decides whether it's written (<see cref="QuietReports"/>): this
/// window's loss and round trip, and the running totals of what goes wrong.
/// </summary>
/// <param name="LossPercent">Packets lost this window, in percent.</param>
/// <param name="RoundTripP95Ms">This window's round-trip 95th percentile, in milliseconds (0 when none was measured).</param>
/// <param name="Missing">Frames missing so far.</param>
/// <param name="Stale">Stale ticks so far.</param>
/// <param name="Resyncs">Playout resyncs so far.</param>
/// <param name="DroppedTicks">Ticks dropped so far (the host's frames late beyond the catch-up limit).</param>
/// <param name="Underruns">Playout underruns so far.</param>
/// <param name="HoldsOffByMoreThanAFrame">Holds played more than a frame off so far.</param>
/// <param name="Refused">Controls messages, malformed packets and other refusals so far.</param>
public readonly record struct InputReportCounters(
    double LossPercent,
    double RoundTripP95Ms,
    long Missing,
    long Stale,
    long Resyncs,
    long DroppedTicks,
    long Underruns,
    long HoldsOffByMoreThanAFrame,
    long Refused);

/// <summary>
/// The rate rule for a friend's 5 s input line on the host: written every time, one friend's input lines are nearly
/// a third of the host's log, about 4,300 lines for three friends over two hours, burying the rest. A
/// line is written when something moved since the last one written (any loss this window, the round trip's p95 up by
/// more than <see cref="RoundTripRiseMs"/>, missing, stale, resyncs, dropped ticks or underruns up by more than
/// <see cref="Slack"/>, a hold off by more than a frame, a refusal, or a total that went down: new counters after a
/// rejoin), and otherwise once every <see cref="QuietEverySeconds"/>, saying how many quiet ones were left out. The first
/// is always written. Not thread-safe.
/// </summary>
public sealed class QuietReports
{
    /// <summary>The longest a quiet friend goes without a line.</summary>
    public const double QuietEverySeconds = 30;

    /// <summary>How much a playout counter may grow and still be quiet.</summary>
    public const long Slack = 2;

    /// <summary>How much the round trip's p95 may rise and still be quiet.</summary>
    public const double RoundTripRiseMs = 20;

    private InputReportCounters? _written;
    private double _writtenAt;

    /// <summary>Quiet reports left out since the last line written.</summary>
    public int LeftOut { get; private set; }

    /// <summary>
    /// Whether this report (<paramref name="now"/>, at <paramref name="nowSeconds"/>) is written. When it is,
    /// <paramref name="leftOut"/> says how many quiet ones came before it unwritten, and it's the one later reports are
    /// compared with.
    /// </summary>
    public bool ShouldWrite(InputReportCounters now, double nowSeconds, out int leftOut)
    {
        leftOut = 0;
        if (_written is InputReportCounters last && nowSeconds - _writtenAt < QuietEverySeconds && Quiet(last, now))
        {
            LeftOut++;
            return false;
        }

        leftOut = LeftOut;
        LeftOut = 0;
        _written = now;
        _writtenAt = nowSeconds;
        return true;
    }

    /// <summary>True when nothing in <paramref name="now"/> moved enough since <paramref name="last"/> to be worth a line.</summary>
    public static bool Quiet(InputReportCounters last, InputReportCounters now)
    {
        static bool Grew(long before, long after) => after < before || after - before > Slack;
        return now.LossPercent <= 0 &&
            now.RoundTripP95Ms - last.RoundTripP95Ms <= RoundTripRiseMs &&
            !Grew(last.Missing, now.Missing) && !Grew(last.Stale, now.Stale) && !Grew(last.Resyncs, now.Resyncs) &&
            !Grew(last.DroppedTicks, now.DroppedTicks) && !Grew(last.Underruns, now.Underruns) &&
            now.HoldsOffByMoreThanAFrame == last.HoldsOffByMoreThanAFrame && now.Refused == last.Refused;
    }

    /// <summary>Forgets the last line (the friend left): the next report is written.</summary>
    public void Clear()
    {
        _written = null;
        LeftOut = 0;
    }
}
