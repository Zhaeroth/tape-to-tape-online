namespace T2TOnline.Core.Hosting;

/// <summary>How a punch held at its apex for a friend's block ends (D35), or that it keeps waiting.</summary>
public enum GraceOutcome
{
    /// <summary>Keep waiting.</summary>
    Waiting,

    /// <summary>Every friend in reach blocks: the apex happens and the game resolves the hit.</summary>
    Blocked,

    /// <summary>Every friend in reach blocks, at least one high for a low punch or low for a high one (the game deals partial damage).</summary>
    PartlyBlocked,

    /// <summary>The window ran out: the punch goes on as normal.</summary>
    TimedOut,

    /// <summary>No friend who isn't blocking could block any more (punching, staggered, knocked out, or gone).</summary>
    Unblockable,

    /// <summary>The fight grace window was switched off in the settings.</summary>
    SwitchedOff,

    /// <summary>Hosting stopped, or every friend left.</summary>
    SessionEnded,

    /// <summary>The punch is no longer live (its attacker interrupted, out of the fight, or gone): let go without resolving.</summary>
    Interrupted,
}

/// <summary>
/// The fight grace window's verdict for one held punch, from what its victims do now (FightGrace in the plugin reads the
/// game; D35). Give it each friend's fighter in the punch's reach with <see cref="Victim"/>, then ask
/// <see cref="Outcome"/>. In order: a punch that's no longer live is let go; when every victim blocks, the punch goes on;
/// then the window's end; then whether any victim who isn't blocking could still block (one that's gone can't); then
/// whether the window is still on.
/// </summary>
public struct GraceCheck
{
    private bool _anyNotBlocking;
    private bool _anyOtherWay;
    private bool _anyCouldStillBlock;

    /// <summary>
    /// One victim: <paramref name="gone"/> (destroyed), <paramref name="blocking"/> (in its block state),
    /// <paramref name="blockingRightWay"/> (its block is the type that stops this punch), <paramref name="canBlock"/> (its
    /// current state lets it start blocking).
    /// </summary>
    public void Victim(bool gone, bool blocking, bool blockingRightWay, bool canBlock)
    {
        if (!gone && blocking)
        {
            _anyOtherWay |= !blockingRightWay;
            return;
        }

        _anyNotBlocking = true;
        _anyCouldStillBlock |= !gone && canBlock;
    }

    /// <summary>
    /// The verdict: <paramref name="live"/> (the punch is still its attacker's state, in a fight), the time against the
    /// window's <paramref name="deadlineMicros"/>, <paramref name="switchedOn"/> (the setting), <paramref name="active"/>
    /// (hosting with friends in, whatever the setting: <paramref name="switchedOn"/> alone ends a wait when it's off).
    /// </summary>
    public readonly GraceOutcome Outcome(bool live, long nowMicros, long deadlineMicros, bool switchedOn, bool active) =>
        !live ? GraceOutcome.Interrupted
        : !_anyNotBlocking ? (_anyOtherWay ? GraceOutcome.PartlyBlocked : GraceOutcome.Blocked)
        : nowMicros >= deadlineMicros ? GraceOutcome.TimedOut
        : !_anyCouldStillBlock ? GraceOutcome.Unblockable
        : !switchedOn ? GraceOutcome.SwitchedOff
        : !active ? GraceOutcome.SessionEnded
        : GraceOutcome.Waiting;
}
