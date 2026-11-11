namespace T2TOnline.Core.Hosting;

/// <summary>What the AI stand-in does for one friend this frame (<see cref="StandInTimer.Step"/>).</summary>
public enum StandInStep
{
    /// <summary>Nothing changes.</summary>
    None,

    /// <summary>The friend has been away long enough: the AI takes their skater from now on.</summary>
    Start,

    /// <summary>
    /// The friend has been back long enough: hand their skater back, then call <see cref="StandInTimer.HandedBack"/> (or
    /// ask again next frame if it can't be handed back yet).
    /// </summary>
    HandBack,
}

/// <summary>
/// The AI stand-in's timing for one friend when they drop mid-match (AiStandIn in the
/// plugin sets the skater flags): once they've been away (silent, dropped or unplugged) for <see cref="AwayMicros"/> and
/// hold a skater, the AI takes it; once they're back and steady for <see cref="BackMicros"/>, it's handed back. Going away
/// again before that starts the count over; coming back before the AI took over cancels it. A friend whose game said it
/// can't play the match (its load failed) is away with no wait: they aren't coming back in a moment, and their pad would
/// otherwise drive a skater they can't see.
/// </summary>
public sealed class StandInTimer
{
    /// <summary>How long a friend is gone before the AI takes their skater (product decision: 3 s).</summary>
    public const long AwayMicros = 3_000_000;

    /// <summary>How long a friend is back before their skater is handed back (their input steady again).</summary>
    public const long BackMicros = 500_000;

    /// <summary>True while the AI plays the friend's skater.</summary>
    public bool Active { get; private set; }

    /// <summary>When the friend went away (this time), or <see cref="long.MinValue"/> while they're here.</summary>
    public long AwaySince { get; private set; } = long.MinValue;

    /// <summary>When the friend came back while the AI plays for them, or <see cref="long.MinValue"/>.</summary>
    public long BackSince { get; private set; } = long.MinValue;

    /// <summary>
    /// Call every frame: <paramref name="away"/> while the friend is silent, dropped or unplugged; <paramref name="hasSkater"/>
    /// while their game player holds a skater (none between matches, or before they joined); <paramref name="atOnce"/> while
    /// they're away for a reason that has no wait (their game couldn't load the match): the AI takes the skater as soon as
    /// there is one.
    /// </summary>
    public StandInStep Step(bool away, bool hasSkater, long nowMicros, bool atOnce = false)
    {
        if (away)
        {
            BackSince = long.MinValue;
            if (AwaySince == long.MinValue)
            {
                AwaySince = nowMicros;
            }

            if (!Active && hasSkater && (atOnce || nowMicros - AwaySince >= AwayMicros))
            {
                Active = true;
                return StandInStep.Start;
            }

            return StandInStep.None;
        }

        AwaySince = long.MinValue;
        if (!Active)
        {
            return StandInStep.None;
        }

        if (BackSince == long.MinValue)
        {
            BackSince = nowMicros;
        }

        return nowMicros - BackSince >= BackMicros ? StandInStep.HandBack : StandInStep.None;
    }

    /// <summary>The skater was handed back: the friend plays it again.</summary>
    public void HandedBack()
    {
        Active = false;
        BackSince = long.MinValue;
    }
}
