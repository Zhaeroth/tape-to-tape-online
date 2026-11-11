namespace T2TOnline.Core.Campaign;

/// <summary>
/// What the title's New Run and Abandon Run presses meet while the title's campaign buttons aren't reloaded yet for the
/// run file as it now is (D46). The title decides from what it last loaded: whether New Run asks before it replaces a
/// run, and which run Abandon Run ends. After the run file switched (back to the solo run when hosting stopped, when a
/// host's run was put away, before a co-op slot's restore), run sync reloads the title as soon as nothing is open on
/// it, a moment later. A press in that moment would act on the run file from before: New Run would replace the run in
/// the file without asking, and Abandon Run would end the run in memory and delete the other file's. So such a press
/// does nothing, as a press during the title's own transitions does nothing. Never while this PC follows a host (its
/// runs are the host's), and never for long: if the reload hasn't come
/// <see cref="CapSeconds"/> after the first press held, presses pass again and the log says so, so nobody is kept from
/// New Run by a reload that can't run. Game-independent; times are seconds on any clock that only goes forward. Not
/// thread-safe: the game thread only.
/// </summary>
public sealed class TitleRunButtonsHold
{
    /// <summary>How long after the first press held, in one wait for a reload, presses are held at most.</summary>
    public const double CapSeconds = 3;

    private double _period = double.NaN;
    private double _firstHeld = -1;
    private bool _late;

    /// <summary>
    /// What a press at <paramref name="now"/> meets, given <paramref name="title"/>. Outside a wait for the reload it
    /// passes. In one, the first press is held and says so (<see cref="TitlePress.HeldFirst"/>), later ones are held
    /// quietly; from <see cref="CapSeconds"/> after the first, they pass (<see cref="TitlePress.PassedLateFirst"/> once,
    /// then <see cref="TitlePress.PassedLate"/>) until the reload has come. A new wait (another
    /// <see cref="TitleReload.Since"/>) starts afresh.
    /// </summary>
    public TitlePress Press(in TitleReload title, double now)
    {
        if (!title.Waited(now))
        {
            _firstHeld = -1;
            _late = false;
            return TitlePress.Passed;
        }

        // Each switch of the run file marks its own wait: an earlier wait's presses don't count against this one's cap.
        if (!title.Since.Equals(_period))
        {
            _period = title.Since;
            _firstHeld = -1;
            _late = false;
        }

        if (_firstHeld < 0)
        {
            _firstHeld = now;
            return TitlePress.HeldFirst;
        }

        if (now - _firstHeld < CapSeconds)
        {
            return TitlePress.Held;
        }

        if (_late)
        {
            return TitlePress.PassedLate;
        }

        _late = true;
        return TitlePress.PassedLateFirst;
    }
}

/// <summary>Where the title's reload stands after a switch of the run file, for <see cref="TitleRunButtonsHold.Press"/>.</summary>
/// <param name="Following">This PC follows a host: nothing is held.</param>
/// <param name="ReloadOwed">The run file switched (or a host's run was put away) and run sync hasn't started its reload of the title yet.</param>
/// <param name="Since">When that switch was marked: one wait's identity.</param>
/// <param name="ReloadStartedAt">When run sync last started a reload of the title; negative for never.</param>
/// <param name="TitleRefreshing">The title is loading its campaign state now (its buttons change when that ends).</param>
public readonly record struct TitleReload(bool Following, bool ReloadOwed, double Since, double ReloadStartedAt, bool TitleRefreshing)
{
    /// <summary>
    /// True while the title's campaign buttons may still be for the run file from before, at <paramref name="now"/>:
    /// not following a host, with the reload owed, or started and still running. A reload started longer than
    /// <see cref="TitleRunButtonsHold.CapSeconds"/> ago counts as over whatever the title is loading now (a later load is
    /// the title's own, of the right file).
    /// </summary>
    public bool Waited(double now) =>
        !Following && (ReloadOwed || (TitleRefreshing && ReloadStartedAt >= 0 && now - ReloadStartedAt < TitleRunButtonsHold.CapSeconds));
}

/// <summary>What a press on the title's New Run or Abandon Run meets (<see cref="TitleRunButtonsHold.Press"/>).</summary>
public enum TitlePress
{
    /// <summary>The press goes to the game.</summary>
    Passed,

    /// <summary>Held, the first of this wait: the press does nothing, and the log says why.</summary>
    HeldFirst,

    /// <summary>Held: the press does nothing.</summary>
    Held,

    /// <summary>The reload didn't come in time: the press goes to the game, and the log warns, once per wait.</summary>
    PassedLateFirst,

    /// <summary>The reload still hasn't come: the press goes to the game.</summary>
    PassedLate,
}
