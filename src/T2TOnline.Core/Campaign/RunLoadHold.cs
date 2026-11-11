namespace T2TOnline.Core.Campaign;

/// <summary>What a map trip does with its run load now.</summary>
public enum RunLoadStep
{
    /// <summary>Load it: nothing holds it.</summary>
    Go = 0,

    /// <summary>Wait: this PC's event screen is still up or closing.</summary>
    Wait = 1,

    /// <summary>Load it: it waited, and the event screen is gone now.</summary>
    GoAfterWait = 2,

    /// <summary>Load it anyway: it waited <see cref="RunLoadHold.MaxWaitSeconds"/>.</summary>
    GoAfterTimeout = 3,
}

/// <summary>
/// A friend's map trip holds its run load while this PC's event screen is up or closing. The host's game saves the run
/// during an event's exit, before its menu hides (exit → <c>SendPlayerToNode</c> → <c>SaveRunData</c> → <c>Hide</c>), so the
/// host's new run and map reach a friend a moment before the event's close: loaded at once, the run's objects are swapped
/// under a menu still showing the old ones, and a reload of the map runs under the closing screen. The trip waits for the
/// close, at most <see cref="MaxWaitSeconds"/>. Pure logic; the glue says what holds it.
/// </summary>
public static class RunLoadHold
{
    /// <summary>The longest a run load waits for this PC's event screen.</summary>
    public const double MaxWaitSeconds = 3;

    /// <summary>How long a screen this PC closed still holds it while it hides.</summary>
    public const double ClosingSeconds = 2;

    /// <summary>
    /// The step: <paramref name="loads"/> (the trip loads a run: it moves or reloads), <paramref name="held"/> (an event
    /// screen is up or closing here), <paramref name="waited"/> (seconds since the trip first waited; negative: it hasn't).
    /// </summary>
    public static RunLoadStep Decide(bool loads, bool held, double waited)
    {
        if (!loads)
        {
            return RunLoadStep.Go;
        }

        if (!held)
        {
            return waited >= 0 ? RunLoadStep.GoAfterWait : RunLoadStep.Go;
        }

        return waited >= MaxWaitSeconds ? RunLoadStep.GoAfterTimeout : RunLoadStep.Wait;
    }
}
