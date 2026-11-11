namespace T2TOnline.Core.Campaign;

/// <summary>What a friend's Nurse procedure screen does now to follow the host's.</summary>
public enum NurseFollowAction
{
    /// <summary>Nothing to do.</summary>
    None = 0,

    /// <summary>Wait: the screen here isn't ready for the next step yet, or its stage is still getting to the host's.</summary>
    Wait = 1,

    /// <summary>Replay the step through the screen's own input handler (<see cref="NurseFollowStep.Step"/>).</summary>
    Apply = 2,

    /// <summary>Focus the option the host's screen has (<see cref="NurseFollowStep.Focus"/>; the screen's own <c>DebugFocusOption</c>).</summary>
    Refocus = 3,

    /// <summary>End the sacrifice's reveal here (it held the screen's input past the wait).</summary>
    SkipReveal = 4,

    /// <summary>Stop following: the screen here can't be the host's (<see cref="NurseFollowStep.Why"/>); it shows as text instead.</summary>
    Fallback = 5,
}

/// <summary>One decision: what to do, with which step or option, and why (a fallback).</summary>
/// <param name="Action">What to do.</param>
/// <param name="Step">The step to replay (<see cref="NurseFollowAction.Apply"/>), else null.</param>
/// <param name="Focus">The option to focus (<see cref="NurseFollowAction.Refocus"/>), else -1.</param>
/// <param name="Why">Why it falls back, else "".</param>
public readonly record struct NurseFollowStep(NurseFollowAction Action, NurseStep? Step = null, int Focus = -1, string Why = "");

/// <summary>
/// A friend's Butcher lines on the real procedure screen. Unlike every other line in the game they
/// wait for no press (the game's <c>UntilNext</c> mode: a line stays until the next one, and the screen takes input all the
/// while): each one starts with the screen's stage, so the host's steps start them here too. The one thing a press on the
/// host can do to one is show it in full while it's still typing, so that's the one thing followed: once per host's line, on
/// the line here once this PC's screen has every step the host's has. Pressing a line already shown would play the bubble's
/// "next line" sound, and nothing here ever presses to catch up: a line behind the host's goes when its step comes. Pure
/// logic; the game glue reads the bubble and presses.
/// </summary>
public sealed class NurseLineFollow
{
    private int _handled;

    /// <summary>
    /// True when the host's progress <paramref name="host"/> asks for something here: a press showed its line in full, a line
    /// not dealt with yet, and this PC's screen is in step (<paramref name="inStep"/>: every host's step replayed).
    /// </summary>
    public bool Wanted(SpeechProgress host, bool inStep) => inStep && !host.IsNone && host.Stage != SpeechStage.Started && host.Key != _handled;

    /// <summary>
    /// What's on the Butcher here (<paramref name="phase"/>) for a <see cref="Wanted"/> progress: true when the line is typing,
    /// so this PC's bubble gets the game's own skip. A line shown already is dealt with; none yet waits for it to start.
    /// </summary>
    public bool Press(SpeechProgress host, SpeechPhase phase)
    {
        if (phase is SpeechPhase.None or SpeechPhase.Idle)
        {
            return false;
        }

        _handled = host.Key;
        return phase == SpeechPhase.Typing;
    }
}

/// <summary>
/// How a friend's Nurse procedure screen follows the host's: the host's steps (<see cref="NurseStepsData"/>) are replayed in
/// order, each once the screen here takes input; after each, the stage here must reach the host's stage after it (within
/// <see cref="SettleSeconds"/>) and its focus is put on the host's if it differs (a held direction, a press refused here). A
/// step the host's screen made by itself (<see cref="NurseStepKind.Auto"/>) is waited for: the screen here makes it too.
/// Anything this can't reconcile (a stage that differs, steps missed, a screen that never takes input, opening too far behind)
/// falls back, with why, and the screen shows as text. One per takeover; pure logic, the game glue does the calls.
/// </summary>
public sealed class NurseFollower
{
    /// <summary>How long a step waits for the screen here to take input before its sacrifice reveal is ended (then as long again before falling back).</summary>
    public const double ReadySeconds = 2;

    /// <summary>How long the stage here may take to reach the host's after a step.</summary>
    public const double SettleSeconds = 1;

    /// <summary>How long the screen here has to make a change the host's made by itself.</summary>
    public const double AutoSeconds = 3;

    /// <summary>Most steps the host's screen may be ahead when the screen here opens (more: it shows as text).</summary>
    public const int MaxBehind = 4;

    private NurseStep? _settling;
    private double _settleSince;
    private double _waitSince = -1;
    private bool _skipped;

    /// <summary>The newest host step handled here (0: none yet).</summary>
    public int Applied { get; private set; }

    /// <summary>True once it fell back (it does nothing more).</summary>
    public bool FellBack { get; private set; }

    /// <summary>Why it fell back, or "".</summary>
    public string Why { get; private set; } = string.Empty;

    /// <summary>
    /// The screen here just opened at <paramref name="stage"/>: falls back when it isn't the host's first stage, or when the
    /// host's screen is more than <see cref="MaxBehind"/> steps ahead; else nothing (the steps follow with <see cref="Next"/>).
    /// </summary>
    public NurseFollowStep Opened(NurseStepsData host, int stage)
    {
        System.ArgumentNullException.ThrowIfNull(host);
        if (host.Newest > MaxBehind)
        {
            return Fall($"the host's screen was {host.Newest} steps ahead when it opened here");
        }

        if (stage != host.StartStage)
        {
            return Fall($"it opened here at stage {stage}, the host's at stage {host.StartStage}");
        }

        return new NurseFollowStep(NurseFollowAction.None);
    }

    /// <summary>
    /// What to do now. <paramref name="host"/>: the host's newest steps; <paramref name="ready"/>: the screen here takes input
    /// (its input open, not closing); <paramref name="stage"/> and <paramref name="focus"/>: the screen here now;
    /// <paramref name="now"/>: the time (seconds).
    /// </summary>
    public NurseFollowStep Next(NurseStepsData host, bool ready, int stage, int focus, double now)
    {
        System.ArgumentNullException.ThrowIfNull(host);
        if (FellBack)
        {
            return new NurseFollowStep(NurseFollowAction.None);
        }

        if (_settling is { } settling)
        {
            if (stage != settling.Stage)
            {
                return now - _settleSince < SettleSeconds ? new NurseFollowStep(NurseFollowAction.Wait)
                    : Fall($"after the host's {settling} the screen here is at stage {stage}");
            }

            _settling = null;
            if (settling.Focus >= 0 && focus != settling.Focus)
            {
                return new NurseFollowStep(NurseFollowAction.Refocus, Focus: settling.Focus);
            }
        }

        if (host.Steps.Count > 0 && host.Steps[0].Number > Applied + 1)
        {
            return Fall($"steps {Applied + 1} to {host.Steps[0].Number - 1} of the host's were missed here");
        }

        NurseStep? next = host.Find(Applied + 1);
        if (next == null)
        {
            _waitSince = -1;
            return new NurseFollowStep(NurseFollowAction.None);
        }

        switch (next.Kind)
        {
            case NurseStepKind.Auto:
                if (stage != next.Stage)
                {
                    _waitSince = _waitSince < 0 ? now : _waitSince;
                    return now - _waitSince <= AutoSeconds ? new NurseFollowStep(NurseFollowAction.Wait)
                        : Fall($"the host's screen moved to stage {next.Stage} by itself; the screen here stayed at stage {stage}");
                }

                Handled(next, now);
                return new NurseFollowStep(NurseFollowAction.None);
            case NurseStepKind.Lineup:
                // The lineup comes as its own view; the screen here waits under it.
                Applied = next.Number;
                _waitSince = -1;
                return new NurseFollowStep(NurseFollowAction.None);
            case NurseStepKind.Focus:
                Handled(next, now);
                return focus != next.Arg ? new NurseFollowStep(NurseFollowAction.Refocus, Focus: next.Arg) : new NurseFollowStep(NurseFollowAction.None);
        }

        if (!ready)
        {
            _waitSince = _waitSince < 0 ? now : _waitSince;
            if (!_skipped && now - _waitSince > ReadySeconds)
            {
                _skipped = true;
                return new NurseFollowStep(NurseFollowAction.SkipReveal);
            }

            return now - _waitSince > 2 * ReadySeconds ? Fall($"the screen here took no input for {2 * ReadySeconds:0} s (the host's {next} waited)")
                : new NurseFollowStep(NurseFollowAction.Wait);
        }

        Handled(next, now);
        _skipped = false;
        return new NurseFollowStep(NurseFollowAction.Apply, next);
    }

    /// <summary>Stops following, with why (the glue failed a call, the screen closed early).</summary>
    public NurseFollowStep Fall(string why)
    {
        if (!FellBack)
        {
            FellBack = true;
            Why = why;
        }

        return new NurseFollowStep(NurseFollowAction.Fallback, Why: Why);
    }

    private void Handled(NurseStep step, double now)
    {
        Applied = step.Number;
        _waitSince = -1;
        _settling = step.Stage >= 0 ? step : null;
        _settleSince = now;
    }
}
