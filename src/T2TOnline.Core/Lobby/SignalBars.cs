namespace T2TOnline.Core.Lobby;

/// <summary>
/// Signal bars: a count, never only a colour (XAG 103). A plain mark of the round trip the host measured: the
/// thresholds are a first calibration (a round trip near 60 ms plays without noticeable delay).
/// </summary>
public static class SignalBars
{
    /// <summary>The most bars.</summary>
    public const int Most = 3;

    /// <summary>The longest round trip that still shows three bars, in ms.</summary>
    public const int ThreeBarsMs = 80;

    /// <summary>The longest round trip that still shows two bars, in ms.</summary>
    public const int TwoBarsMs = 150;

    /// <summary>Loss above this many percent shows one bar, whatever the round trip.</summary>
    public const double MostLossPercent = 2;

    /// <summary>Bars for a round trip of <paramref name="pingMs"/>: none when it's unknown (negative), else 3 up to 80 ms, 2 up to 150, and 1 above that or with loss over 2 percent.</summary>
    public static int Of(int pingMs, double lossPercent = 0)
    {
        if (pingMs < 0)
        {
            return 0;
        }

        if (lossPercent > MostLossPercent || pingMs > TwoBarsMs)
        {
            return 1;
        }

        return pingMs > ThreeBarsMs ? 2 : Most;
    }
}
