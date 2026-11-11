using System;

namespace T2TOnline.Core.Campaign;

/// <summary>A speaker's bubble as this PC's game has it: the game's <c>SpeechBubbleView.Phase</c>, or no bubble at all.</summary>
public enum SpeechPhase
{
    /// <summary>No bubble on the speaker (between two lines of a speech made of single lines, or no speech).</summary>
    None = -1,

    /// <summary>
    /// A bubble with nothing to press: not typing yet, or its line's press has come. The game never puts a bubble's own phase
    /// back (verified in the game's code: written only at the start of typing, and of waiting, and a pooled bubble keeps it), so
    /// the glue reads a "waiting" bubble whose press has come as this.
    /// </summary>
    Idle = 0,

    /// <summary>The line is typing out.</summary>
    Typing = 1,

    /// <summary>The line is shown in full and waits (for a press, as every line in this game does).</summary>
    Waiting = 2,
}

/// <summary>
/// Counts the lines a speaker starts (the speech mirror). The game gives the newest bubble of a speaker a new
/// resolved line object for every line (<c>SpeechBubbleController.ResolveDefaults</c> makes a copy, which the bubble keeps
/// while the line is up), so a new object is the next line, and so is any object after the bubble has gone (a line's bubble
/// never comes back; the runtime may give a later line the memory of an earlier one nothing holds any more: Pucklin's second
/// line, a Hatfreeze line after his first). Where that object can't be read, each start of typing is. <see cref="Begin"/>
/// starts a speech: what's on the speaker then isn't counted, and its first new line gets the key asked for. Pure logic; the
/// game glue reads the bubble.
/// </summary>
public sealed class SpeechLineCounter
{
    /// <summary>The line object can't be read: lines are counted by each start of typing instead.</summary>
    public const long Unknown = -1;

    private long _last;
    private SpeechPhase _lastPhase = SpeechPhase.None;

    /// <summary>The newest line's key (0: none yet in this speech).</summary>
    public int Key { get; private set; }

    /// <summary>True once the speech had more lines than <see cref="SpeechProgress.MaxKey"/> (the key stays at the cap).</summary>
    public bool Saturated { get; private set; }

    /// <summary>
    /// A speech begins: <paramref name="line"/> and <paramref name="phase"/> are what the speaker shows now (not counted),
    /// and its first new line gets <paramref name="firstKey"/> (1 to <see cref="SpeechProgress.MaxKey"/>; clamped).
    /// </summary>
    public void Begin(long line, SpeechPhase phase, int firstKey = 1)
    {
        _last = line == Unknown ? 0 : line;
        _lastPhase = phase;
        Key = Math.Clamp(firstKey, 1, SpeechProgress.MaxKey) - 1;
        Saturated = false;
    }

    /// <summary>
    /// What the speaker shows now: its newest bubble's line object (0: no bubble; <see cref="Unknown"/>: unreadable) and
    /// phase. True when a new line started (<see cref="Key"/> is its key).
    /// </summary>
    public bool Observe(long line, SpeechPhase phase)
    {
        bool started = line == Unknown
            ? phase == SpeechPhase.Typing && _lastPhase != SpeechPhase.Typing
            : line != 0 && (line != _last || _lastPhase == SpeechPhase.None);
        if (line != 0 && line != Unknown)
        {
            _last = line;
        }

        _lastPhase = phase;
        if (!started)
        {
            return false;
        }

        if (Key < SpeechProgress.MaxKey)
        {
            Key++;
        }
        else
        {
            Saturated = true;
        }

        return true;
    }
}

/// <summary>What a friend's speaker does this frame.</summary>
public enum SpeechFollowAction
{
    /// <summary>Nothing.</summary>
    None = 0,

    /// <summary>One press on this PC's bubble, the game's own skip: a typing line shows in full, a waiting one advances.</summary>
    Skip = 1,
}

/// <summary>What a step means, for the log (each once per line and phase).</summary>
public enum SpeechFollowEvent
{
    /// <summary>Nothing to say.</summary>
    None = 0,

    /// <summary>The host advanced past this line: it advances here.</summary>
    Advanced = 1,

    /// <summary>The host showed this line in full: it does here.</summary>
    ShownInFull = 2,

    /// <summary>The host advanced past this line while it still types here (a longer line in this PC's language): it's shown in full first.</summary>
    ShownFirst = 3,

    /// <summary>The host is past this line: it's skipped here to catch up.</summary>
    CaughtUp = 4,

    /// <summary>This PC's bubble didn't move for <see cref="SpeechFollow.StuckSeconds"/> of skips (said once; skips slow down).</summary>
    Stuck = 5,

    /// <summary>The host is on a line this PC's speech doesn't have (none came for <see cref="SpeechFollow.MissingSeconds"/>; said once a speech).</summary>
    Missing = 6,

    /// <summary>The host's speech is over (what follows it is up on its screen): whatever still speaks here runs out.</summary>
    HostOver = 7,
}

/// <summary>One decision: the action, what it means, and the line (this PC's key; the host's for <see cref="SpeechFollowEvent.Missing"/>).</summary>
/// <param name="Action">What to do.</param>
/// <param name="Event">What it means, for the log.</param>
/// <param name="Key">The line it's about.</param>
public readonly record struct SpeechFollowStep(SpeechFollowAction Action, SpeechFollowEvent Event, int Key);

/// <summary>
/// A friend's speaker following the host's (the speech mirror). Every line in this game waits for a press, which a friend can never give: the
/// host's presses come as its <see cref="SpeechProgress"/>, and this decides when this PC's own bubble gets the game's own skip, one a frame at
/// most:
/// <list type="bullet">
/// <item>this PC's line is behind the host's: skip (a typing line shows in full, the next frame it advances: catching up);</item>
/// <item>the same line: the host advanced past it → skip (shown in full first while it still types); the host showed it in
/// full and it types here → skip;</item>
/// <item>ahead of the host, no bubble, or idle between lines → nothing, unless the host's speech is over: then every line here
/// runs out (a speech the menu's next step waits for, the Canada curse's cards, can't hang here on a line the host's never had).</item>
/// </list>
/// A bubble that doesn't move for <see cref="StuckSeconds"/> of skips is said once and then skipped only every
/// <see cref="StuckRetrySeconds"/>; a host line this PC's speech never gets to (no bubble for <see cref="MissingSeconds"/>
/// once this speech had one) is said once. It counts what it did for the speech's summary. Pure logic; the glue reads the
/// bubble, skips and logs.
/// </summary>
public sealed class SpeechFollow
{
    /// <summary>How long skips may leave the bubble where it was before that's said.</summary>
    public const double StuckSeconds = 2;

    /// <summary>How often a stuck bubble is skipped again.</summary>
    public const double StuckRetrySeconds = 0.5;

    /// <summary>How long this PC may show no bubble while the host is on a later line before that line is called missing.</summary>
    public const double MissingSeconds = 3;

    private readonly SpeechLineCounter _counter = new();
    private int _firstKey = 1;
    private int _skipKey = -1;
    private SpeechPhase _skipPhase = SpeechPhase.None;
    private double _skipSince;
    private double _lastSkip = double.NegativeInfinity;
    private bool _stuckSaid;
    private double _behindSince = double.NaN;
    private bool _missingSaid;
    private int _caughtUpKey;

    /// <summary>This PC's line (0: none yet in this speech).</summary>
    public int OwnKey => _counter.Key;

    /// <summary>This PC's bubble's phase, as last observed.</summary>
    public SpeechPhase Phase { get; private set; } = SpeechPhase.None;

    /// <summary>The key this PC's speech started at (2 or more: a late start from the host's line).</summary>
    public int FirstKey => _firstKey;

    /// <summary>Lines started here in this speech.</summary>
    public int Lines { get; private set; }

    /// <summary>Lines advanced here as the host's were.</summary>
    public int Advanced { get; private set; }

    /// <summary>Lines shown in full here as the host's were.</summary>
    public int ShownInFull { get; private set; }

    /// <summary>Lines still typing here when the host advanced past them (shown in full first).</summary>
    public int ShownFirst { get; private set; }

    /// <summary>Lines skipped here to catch up with the host.</summary>
    public int CaughtUp { get; private set; }

    /// <summary>The host's presses replayed here (one for each line and phase skipped; retries of a skip the game hasn't taken yet aren't counted).</summary>
    public int Presses { get; private set; }

    /// <summary>
    /// A speech begins here: <paramref name="line"/> and <paramref name="phase"/> are what the speaker shows now (not
    /// counted, see <see cref="SpeechLineCounter.Begin"/>); its first new line is <paramref name="firstKey"/>. Clears the
    /// counts.
    /// </summary>
    public void Begin(long line, SpeechPhase phase, int firstKey = 1)
    {
        _counter.Begin(line, phase, firstKey);
        _firstKey = Math.Clamp(firstKey, 1, SpeechProgress.MaxKey);
        Phase = phase;
        Lines = Advanced = ShownInFull = ShownFirst = CaughtUp = Presses = 0;
        _skipKey = -1;
        _skipPhase = SpeechPhase.None;
        _stuckSaid = false;
        _behindSince = double.NaN;
        _missingSaid = false;
        _caughtUpKey = 0;
    }

    /// <summary>What the speaker's newest bubble shows now (see <see cref="SpeechLineCounter.Observe"/>). True when a new line started.</summary>
    public bool Observe(long line, SpeechPhase phase)
    {
        Phase = phase;
        bool started = _counter.Observe(line, phase);
        if (started)
        {
            Lines++;
        }

        return started;
    }

    /// <summary>
    /// The step for this frame, from the host's progress <paramref name="host"/>, at time <paramref name="now"/> (seconds).
    /// <paramref name="hostOver"/>: the host's speech is over, whatever its progress says.
    /// </summary>
    public SpeechFollowStep Next(SpeechProgress host, double now, bool hostOver = false)
    {
        int own = OwnKey;
        bool speaking = Phase is SpeechPhase.Typing or SpeechPhase.Waiting;
        if (host.IsNone && hostOver && speaking)
        {
            _behindSince = double.NaN;
            return Skip(own, SpeechFollowEvent.HostOver, now);
        }

        if (host.IsNone || !speaking)
        {
            ResetSkip();
            return Behind(host, own, now);
        }

        _behindSince = double.NaN;
        SpeechFollowEvent meaning;
        if (own < host.Key)
        {
            meaning = SpeechFollowEvent.CaughtUp;
        }
        else if (own == host.Key && host.Stage == SpeechStage.Advanced)
        {
            meaning = Phase == SpeechPhase.Typing ? SpeechFollowEvent.ShownFirst : SpeechFollowEvent.Advanced;
        }
        else if (own == host.Key && host.Stage == SpeechStage.Shown && Phase == SpeechPhase.Typing)
        {
            meaning = SpeechFollowEvent.ShownInFull;
        }
        else if (hostOver)
        {
            meaning = SpeechFollowEvent.HostOver;
        }
        else
        {
            ResetSkip();
            return default;
        }

        return Skip(own, meaning, now);
    }

    /// <summary>No bubble to press: a host line this speech never gets to is said (once a speech), after <see cref="MissingSeconds"/>.</summary>
    private SpeechFollowStep Behind(SpeechProgress host, int own, double now)
    {
        if (host.IsNone || own >= host.Key || Lines == 0 || Phase == SpeechPhase.Idle)
        {
            _behindSince = double.NaN;
            return default;
        }

        if (double.IsNaN(_behindSince))
        {
            _behindSince = now;
            return default;
        }

        if (now - _behindSince < MissingSeconds || _missingSaid)
        {
            return default;
        }

        _missingSaid = true;
        return new SpeechFollowStep(SpeechFollowAction.None, SpeechFollowEvent.Missing, host.Key);
    }

    /// <summary>A skip for line <paramref name="own"/>: what it means the first time for this line and phase, then retries (slowed once stuck).</summary>
    private SpeechFollowStep Skip(int own, SpeechFollowEvent meaning, double now)
    {
        if (own != _skipKey || Phase != _skipPhase)
        {
            _skipKey = own;
            _skipPhase = Phase;
            _skipSince = now;
            _stuckSaid = false;
            _lastSkip = now;
            Presses++;
            Count(own, meaning);
            return new SpeechFollowStep(SpeechFollowAction.Skip, meaning, own);
        }

        if (now - _skipSince < StuckSeconds)
        {
            _lastSkip = now;
            return new SpeechFollowStep(SpeechFollowAction.Skip, SpeechFollowEvent.None, own);
        }

        if (!_stuckSaid)
        {
            _stuckSaid = true;
            _lastSkip = now;
            return new SpeechFollowStep(SpeechFollowAction.Skip, SpeechFollowEvent.Stuck, own);
        }

        if (now - _lastSkip < StuckRetrySeconds)
        {
            return default;
        }

        _lastSkip = now;
        return new SpeechFollowStep(SpeechFollowAction.Skip, SpeechFollowEvent.None, own);
    }

    private void Count(int own, SpeechFollowEvent meaning)
    {
        switch (meaning)
        {
            case SpeechFollowEvent.Advanced:
                Advanced++;
                break;
            case SpeechFollowEvent.ShownInFull:
                ShownInFull++;
                break;
            case SpeechFollowEvent.ShownFirst:
                ShownFirst++;
                break;
            case SpeechFollowEvent.CaughtUp when own != _caughtUpKey:
                _caughtUpKey = own; // a line caught up over two skips (shown, then advanced) counts once
                CaughtUp++;
                break;
        }
    }

    private void ResetSkip()
    {
        _skipKey = -1;
        _skipPhase = SpeechPhase.None;
        _stuckSaid = false;
    }
}
