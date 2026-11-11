using System;
using System.Globalization;
using T2TOnline.Core.Campaign;
using T2TOnline.Core.Serialization;

namespace T2TOnline.Core.Stream;

/// <summary>
/// The HUD values (string-keyed, <c>HudStateMessage</c>) that keep a friend's own match presentations in step with the
/// host's: the match intro (the anthem's skip sequence and the team presentation),
/// the end-of-match win banner, the golfer outro after a won run against the Golfers, and the golfer boss's forms. The
/// host's sampler writes them; a friend's PC reads them with <see cref="PresentationValues"/>. A peer that doesn't know a
/// key ignores it (the HUD channel's rule).
/// </summary>
public static class PresentationKeys
{
    /// <summary>Flag: the host's match intro runs (its skaters are in and no loading barrier holds its match).</summary>
    public const string IntroGo = "intro.go";

    /// <summary>Whole: how far the host's intro is (<see cref="IntroStep"/>).</summary>
    public const string IntroStep = "intro.step";

    /// <summary>Flag: a Canada curse pick runs on the host (<c>CanadaCursePicker.IsPickRunning</c>).</summary>
    public const string IntroCanada = "intro.canada";

    /// <summary>Whole: the host's golfer outro (<see cref="OutroPhase"/>); absent while there's none.</summary>
    public const string OutroPhase = "outro.phase";

    /// <summary>Whole: the seed the host's outro stages its celebration with (<c>GolferOutroDirector._stageSeed</c>).</summary>
    public const string OutroSeed = "outro.seed";

    /// <summary>Flag: the host skipped its outro's celebration (<c>GolferOutroDirector._skipCelebration</c>).</summary>
    public const string OutroSkip = "outro.skip";

    /// <summary>Whole: the comic pages the host's outro moved on from (its continue presses, counted per outro).</summary>
    public const string OutroNext = "outro.next";

    /// <summary>
    /// Whole: the host's comic's speech as one progress (<see cref="SpeechProgress.Value"/> of
    /// <see cref="ComicSpeech.Combine"/>): its lines so far, of any speaker, and how far its presses took the newest.
    /// </summary>
    public const string OutroSpeech = "outro.speech";

    /// <summary>Flag: the host's comic waits for its continue press (<c>GolferComicPage._continue</c> pending): its speech is over.</summary>
    public const string OutroWaits = "outro.waits";

    /// <summary>Flag: the host's end-of-match win banner is up (<c>EndGameAnimatedScoreUI</c>'s canvas shows).</summary>
    public const string Banner = "endgame.banner";

    /// <summary>Flag: the host's arena is dimmed for its win banner (<c>SceneBackdrop.m_DimOnly</c>; only the end-of-match step sets it).</summary>
    public const string BannerDim = "endgame.dim";

    /// <summary>
    /// Flag: the host's cover is going black or black before a post-game reward menu, whose show takes it away
    /// (<c>RogueRewardsHandler.m_FadeInPending</c>).
    /// </summary>
    public const string RewardFade = "endgame.fade";

    /// <summary>What every golfer boss key starts with.</summary>
    public const string GolferPrefix = "golfer.";

    /// <summary>Reference: the forward a transformation shows as drawn on the host (<c>GolferBossTierSystem._drawnForward</c>); null while none.</summary>
    public const string GolferDrawn = "golfer.drawn";

    /// <summary>The most golfer boss transformers carried (the Golfers field five skaters).</summary>
    public const int MaxGolfers = 8;

    private static readonly string[] GolferForms = Keys("golfer.form.");
    private static readonly string[] GolferWhos = Keys("golfer.who.");

    /// <summary>
    /// Whole: the form of the golfer boss's transformer <paramref name="index"/> on the host (the game's
    /// <c>GolferBossTierSystem.Form</c>: 0 normal, 1 mutant, 2 giant mutant).
    /// </summary>
    public static string GolferForm(int index) => GolferForms[index];

    /// <summary>Reference: the forward of the golfer boss's transformer <paramref name="index"/> on the host.</summary>
    public static string GolferWho(int index) => GolferWhos[index];

    /// <summary>What every key of a match-time random pick starts with (<see cref="TalentPick"/>).</summary>
    public const string PickPrefix = "pick.";

    /// <summary>Whole: how many of the host's match-time talent picks the <see cref="PickWho"/>/<see cref="PickTalent"/> keys carry.</summary>
    public const string PickCount = "pick.count";

    /// <summary>The most match-time talent picks carried (a relic's grant to each skater of a team, both teams).</summary>
    public const int MaxPicks = 16;

    /// <summary>What every cup cultist key starts with.</summary>
    public const string CultistPrefix = "cultist.";

    /// <summary>The most cup cultists carried (the Cup Cultists field a team of them).</summary>
    public const int MaxCultists = 8;

    private static readonly string[] PickWhos = Keys("pick.who.", MaxPicks);
    private static readonly string[] PickTalents = Keys("pick.talent.", MaxPicks);
    private static readonly string[] CultistWhos = Keys("cultist.who.", MaxCultists);
    private static readonly string[] CultistTurns = Keys("cultist.turned.", MaxCultists);

    /// <summary>Reference: the forward the host's match-time pick <paramref name="index"/> gave a talent to.</summary>
    public static string PickWho(int index) => PickWhos[index];

    /// <summary>Text: the host's match-time pick <paramref name="index"/> (<see cref="TalentPick.Format"/>).</summary>
    public static string PickTalent(int index) => PickTalents[index];

    /// <summary>Reference: the cup cultist <paramref name="index"/> that can turn into its evil twin (<c>TransformationCultistAI</c>).</summary>
    public static string CultistWho(int index) => CultistWhos[index];

    /// <summary>Flag: whether the cup cultist <paramref name="index"/> is its evil twin on the host now (<c>_isTransformed</c>).</summary>
    public static string CultistTurned(int index) => CultistTurns[index];

    /// <summary>True for a key this file names (the HUD channel hands those to the presentation follower).</summary>
    public static bool IsKey(string key) =>
        key != null && (key.StartsWith("intro.", StringComparison.Ordinal) || key.StartsWith("outro.", StringComparison.Ordinal) ||
        key.StartsWith("endgame.", StringComparison.Ordinal) || key.StartsWith(GolferPrefix, StringComparison.Ordinal) ||
        key.StartsWith(PickPrefix, StringComparison.Ordinal) || key.StartsWith(CultistPrefix, StringComparison.Ordinal) ||
        key.StartsWith("frog.", StringComparison.Ordinal) || key.StartsWith("zap.", StringComparison.Ordinal));

    private static string[] Keys(string prefix, int count = MaxGolfers)
    {
        var keys = new string[count];
        for (int i = 0; i < count; i++)
        {
            keys[i] = prefix + i.ToString(CultureInfo.InvariantCulture);
        }

        return keys;
    }
}

/// <summary>How far the host's match intro is (<see cref="PresentationKeys.IntroStep"/>).</summary>
public enum IntroStep
{
    /// <summary>Not started: the skaters aren't in yet, or the loading barrier holds the match for friends.</summary>
    None = 0,

    /// <summary>The intro's sequence runs (its reset, the anthem's end, a Canada curse pick, the reveal's wait).</summary>
    Running = 1,

    /// <summary>The arena is revealed (<c>NewNationalAnthem.IsReadyToReveal</c>); the team presentation is next.</summary>
    Revealed = 2,

    /// <summary>The team presentation plays (<c>UI.TeamPresentation.IsPlaying</c>).</summary>
    Presenting = 3,

    /// <summary>The intro is over (the presentation played, or the match was past it when the host started sampling).</summary>
    Over = 4,
}

/// <summary>Where the host's golfer outro is (<see cref="PresentationKeys.OutroPhase"/>).</summary>
public enum OutroPhase
{
    /// <summary>The outro was begun at the match's end and waits for its step in the end-of-match pipeline.</summary>
    Waiting = 0,

    /// <summary>It plays: the screen fades to black before the celebration is staged.</summary>
    Revealing = 1,

    /// <summary>The celebration (staging, confetti, crowd, the skip prompt).</summary>
    Celebrating = 2,

    /// <summary>The comic (photo, strips, dialogue, the cup, its continue presses).</summary>
    Comic = 3,

    /// <summary>Over.</summary>
    Done = 4,
}

/// <summary>
/// Host side: the match intro's step from what the game shows (<see cref="IntroStep"/>), once per HUD sample. A match the
/// host started sampling after its reveal (hosting began mid-match) counts as past its intro unless the presentation is
/// playing. Pure logic; the glue reads the game. Not thread-safe.
/// </summary>
public sealed class HostIntroTracker
{
    private bool _sampled;
    private bool _startedRevealed;
    private bool _sawPresenting;

    /// <summary>The step of the last sample.</summary>
    public IntroStep Step { get; private set; }

    /// <summary>A new match: forgets everything.</summary>
    public void Reset()
    {
        _sampled = _startedRevealed = _sawPresenting = false;
        Step = IntroStep.None;
    }

    /// <summary>
    /// One sample: <paramref name="initialized"/> (the anthem's skaters are in), <paramref name="holding"/> (the loading
    /// barrier holds the match), <paramref name="revealed"/> (the arena is revealed), <paramref name="presenting"/> (the
    /// team presentation plays). Returns the step.
    /// </summary>
    public IntroStep Sample(bool initialized, bool holding, bool revealed, bool presenting)
    {
        if (!_sampled)
        {
            _sampled = true;
            _startedRevealed = revealed && !presenting;
        }

        _sawPresenting |= presenting;
        Step = !initialized || holding ? IntroStep.None
            : presenting ? IntroStep.Presenting
            : revealed ? (_sawPresenting || _startedRevealed ? IntroStep.Over : IntroStep.Revealed)
            : IntroStep.Running;
        return Step;
    }
}

/// <summary>Why a friend's held match intro opened (<see cref="IntroGate"/>).</summary>
public enum IntroOpen
{
    /// <summary>Still held, or nothing held.</summary>
    None,

    /// <summary>The host's intro runs: this PC's starts with it.</summary>
    HostIntroRuns,

    /// <summary>The host's intro was over before this PC's match loaded (a friend joining late): this PC's plays now.</summary>
    HostPastIntro,

    /// <summary>
    /// The host's HUD values never said when its intro runs (an older host), though they came for longer than a full set's
    /// period (<see cref="IntroGate.OlderHostMicros"/>): opened.
    /// </summary>
    HostSaysNothing,

    /// <summary>This PC played the host's stream for a while without a HUD value: opened.</summary>
    NoHostValues,

    /// <summary>Held longer than <see cref="IntroGate.MaxHoldMicros"/>: opened.</summary>
    TimedOut,

    /// <summary>The session or the host's match went away: opened so this PC's match isn't left frozen.</summary>
    Released,
}

/// <summary>
/// Friend side: the match intro's start in step with the host's. Each PC's
/// game starts its intro when its own skaters are in (<c>NewNationalAnthem.OnAllSkatersInitialized</c>), and the host's is
/// then held by the loading barrier while friends load, so a friend's intro used to run ahead of the host's. While this PC
/// loads or plays a host's match, its anthem's start is held (<see cref="Hold"/>) and opened when the host's HUD says its
/// intro runs, with fallbacks so nothing is left frozen. It opens once per match and never closes again. All times are
/// microseconds on one clock. Not thread-safe.
/// </summary>
public sealed class IntroGate
{
    /// <summary>
    /// The longest hold: the host's loading barrier, which lasts 45 s and up to <see cref="ContentHold.MaxHold"/> (120 s)
    /// while the host's custom content still reaches a friend, and some room. Past it the friend's intro runs whatever the
    /// host says.
    /// </summary>
    public const long MaxHoldMicros = 130_000_000;

    /// <summary>How long the host's HUD values may stop, once they've come, before this PC's intro opens.</summary>
    public const long SilenceMicros = 10_000_000;

    /// <summary>
    /// How long this PC's playback may wait for the host's first HUD value before its intro opens: the host's stream starts
    /// only once the host's own match has loaded, and a friend can be far ahead of a slow host (its intro stays with the
    /// host's as long as that's sane).
    /// </summary>
    public const long FirstValuesMicros = 30_000_000;

    /// <summary>
    /// How long HUD values may come without the intro's before the host counts as one that doesn't send it: longer than the
    /// period of the host's full set of values (5 s), which always carries it. A message carries only the values that
    /// changed, so the first one heard (a value just before an event, or a friend joining between full sets) may not.
    /// </summary>
    public const long OlderHostMicros = 6_000_000;

    private long _heldAt = long.MinValue;
    private long _showingAt = long.MinValue;
    private long _firstValuesAt = long.MinValue;
    private long _lastValuesAt = long.MinValue;
    private bool _goHeard;
    private bool _go;
    private IntroStep _step;

    /// <summary>True while the anthem's start is held.</summary>
    public bool Holding { get; private set; }

    /// <summary>True once the gate opened this match (it never holds again until <see cref="Reset"/>).</summary>
    public bool Opened { get; private set; }

    /// <summary>Why it opened (<see cref="IntroOpen.None"/> while holding or before).</summary>
    public IntroOpen Reason { get; private set; }

    /// <summary>How long it held, once open (microseconds).</summary>
    public long HeldMicros { get; private set; }

    /// <summary>The host's step as last heard.</summary>
    public IntroStep HostStep => _step;

    /// <summary>A new host match is loading here: forgets the last one.</summary>
    public void Reset()
    {
        _heldAt = _showingAt = _firstValuesAt = _lastValuesAt = long.MinValue;
        _goHeard = _go = false;
        _step = IntroStep.None;
        Holding = Opened = false;
        Reason = IntroOpen.None;
        HeldMicros = 0;
    }

    /// <summary>
    /// The game asks to start this PC's intro at <paramref name="now"/>. Returns <see cref="IntroOpen.None"/> when it's held
    /// now; otherwise why it may start at once (the host's intro already runs, or this match's gate already opened).
    /// </summary>
    public IntroOpen Hold(long now)
    {
        if (Opened)
        {
            return Reason;
        }

        if (_go)
        {
            Open(now, _step >= IntroStep.Presenting ? IntroOpen.HostPastIntro : IntroOpen.HostIntroRuns);
            HeldMicros = 0;
            return Reason;
        }

        if (!Holding)
        {
            Holding = true;
            _heldAt = now;
        }

        return IntroOpen.None;
    }

    /// <summary>This PC started showing the host's stream at <paramref name="now"/> (the silence fallback counts from then).</summary>
    public void Showing(long now)
    {
        if (_showingAt == long.MinValue)
        {
            _showingAt = now;
        }
    }

    /// <summary>
    /// One HUD message from the host at <paramref name="now"/>, with the intro values it carried (null when it carried none:
    /// values unchanged since the last message). Returns why the gate opens now, or <see cref="IntroOpen.None"/>.
    /// </summary>
    public IntroOpen Message(bool? go, IntroStep? step, long now)
    {
        if (_firstValuesAt == long.MinValue)
        {
            _firstValuesAt = now;
        }

        _lastValuesAt = now;
        if (go is bool g)
        {
            _go = g;
            _goHeard = true;
        }

        if (step is IntroStep s)
        {
            _step = s;
        }

        return Holding && _go ? Open(now, _step >= IntroStep.Presenting ? IntroOpen.HostPastIntro : IntroOpen.HostIntroRuns) : IntroOpen.None;
    }

    /// <summary>
    /// Every frame: the fallbacks (held too long, a host whose values never carry the intro's, or the host's stream shows
    /// with no HUD values).
    /// </summary>
    public IntroOpen Tick(long now)
    {
        if (!Holding)
        {
            return IntroOpen.None;
        }

        if (now - _heldAt >= MaxHoldMicros)
        {
            return Open(now, IntroOpen.TimedOut);
        }

        if (!_goHeard && _firstValuesAt != long.MinValue && now - _firstValuesAt >= OlderHostMicros)
        {
            return Open(now, IntroOpen.HostSaysNothing);
        }

        if (_showingAt == long.MinValue)
        {
            return IntroOpen.None;
        }

        bool silent = _lastValuesAt == long.MinValue
            ? now - _showingAt >= FirstValuesMicros
            : now - Math.Max(_showingAt, _lastValuesAt) >= SilenceMicros;
        return silent ? Open(now, IntroOpen.NoHostValues) : IntroOpen.None;
    }

    /// <summary>The session or the host's match went away: opens a held gate (returns whether one was held).</summary>
    public bool Release(long now)
    {
        if (!Holding)
        {
            return false;
        }

        Open(now, IntroOpen.Released);
        return true;
    }

    private IntroOpen Open(long now, IntroOpen reason)
    {
        HeldMicros = Holding && _heldAt != long.MinValue ? Math.Max(0, now - _heldAt) : 0;
        Holding = false;
        Opened = true;
        Reason = reason;
        return reason;
    }
}

/// <summary>Why a friend's wait for the host's Canada curse pick ended (<see cref="CanadaWait"/>).</summary>
public enum CanadaWaitEnd
{
    /// <summary>Still waiting.</summary>
    None,

    /// <summary>The host's pick ran and is over.</summary>
    HostPicked,

    /// <summary>The host's intro is past the pick (revealed or later).</summary>
    HostPastPick,

    /// <summary>Waited <see cref="CanadaWait.MaxWaitMicros"/>.</summary>
    TimedOut,

    /// <summary>The session or the host's match went away.</summary>
    Released,
}

/// <summary>
/// Friend side: in a Team Canada match the intro waits for the curse pick (<c>CanadaCursePicker.Run</c>, awaited by the
/// anthem's sequence). The pick is the host's; a friend's intro waits until the host's pick has run and is over (its HUD
/// flag <see cref="PresentationKeys.IntroCanada"/> went up and down), or the host's intro is past it, or at most
/// <see cref="MaxWaitMicros"/>. Feed it every host value (<see cref="Host"/>) whether or not it waits. Not thread-safe.
/// </summary>
public sealed class CanadaWait
{
    /// <summary>The longest wait (a player picks a curse from a menu; a long one is still a pick).</summary>
    public const long MaxWaitMicros = 180_000_000;

    private bool _sawRunning;
    private bool _picked;
    private IntroStep _step;
    private long _since = long.MinValue;

    /// <summary>True while this PC's intro waits.</summary>
    public bool Waiting { get; private set; }

    /// <summary>A new match: forgets the host's pick.</summary>
    public void Reset()
    {
        _sawRunning = _picked = Waiting = false;
        _step = IntroStep.None;
        _since = long.MinValue;
    }

    /// <summary>The host's values (null when a message didn't carry one).</summary>
    public void Host(bool? pickRunning, IntroStep? step)
    {
        if (pickRunning is bool running)
        {
            _picked |= _sawRunning && !running;
            _sawRunning |= running;
        }

        if (step is IntroStep s)
        {
            _step = s;
        }
    }

    /// <summary>This PC's intro reached the pick at <paramref name="now"/> and waits from now.</summary>
    public void Start(long now)
    {
        Waiting = true;
        _since = now;
    }

    /// <summary>Whether this PC's intro may go on now (<paramref name="connected"/>: the host's match is still followed here).</summary>
    public CanadaWaitEnd Check(long now, bool connected)
    {
        if (!Waiting)
        {
            return CanadaWaitEnd.None;
        }

        CanadaWaitEnd end = !connected ? CanadaWaitEnd.Released
            : _picked ? CanadaWaitEnd.HostPicked
            : _step >= IntroStep.Revealed ? CanadaWaitEnd.HostPastPick
            : now - _since >= MaxWaitMicros ? CanadaWaitEnd.TimedOut
            : CanadaWaitEnd.None;
        if (end != CanadaWaitEnd.None)
        {
            Waiting = false;
        }

        return end;
    }
}

/// <summary>
/// The host's presentation values as a friend's PC last heard them, checked: a value of the wrong kind or out of range is
/// ignored (untrusted network input), and a message that doesn't carry a key keeps the last value. Feed it each HUD value
/// (<see cref="Take"/>) and call <see cref="EndMessage"/> after each message. Not thread-safe.
/// </summary>
public sealed class PresentationValues
{
    private readonly NetValue[] _golferWhos = new NetValue[PresentationKeys.MaxGolfers];
    private readonly int[] _golferForms = new int[PresentationKeys.MaxGolfers];
    private readonly NetValue[] _pickWhos = new NetValue[PresentationKeys.MaxPicks];
    private readonly TalentPick?[] _picks = new TalentPick?[PresentationKeys.MaxPicks];
    private readonly NetValue[] _cultistWhos = new NetValue[PresentationKeys.MaxCultists];
    private readonly bool[] _cultistTurned = new bool[PresentationKeys.MaxCultists];

    /// <summary>The host's frogged skaters and the frog time each has left (<see cref="ForwardSlots.Frogs"/>).</summary>
    public ForwardSlots Frogs { get; } = ForwardSlots.Frogs();

    /// <summary>The host's zapped skaters and the skater each is now (<see cref="ForwardSlots.Zaps"/>).</summary>
    public ForwardSlots Zaps { get; } = ForwardSlots.Zaps();

    /// <summary>How many of the host's match-time talent picks hold (0 to <see cref="PresentationKeys.MaxPicks"/>).</summary>
    public int PickCount { get; private set; }

    /// <summary>True when the last message changed the host's match-time talent picks.</summary>
    public bool PicksChanged { get; private set; }

    /// <summary>True when the last message changed a cup cultist's form or forward.</summary>
    public bool CultistsChanged { get; private set; }

    /// <summary>The host's match-time talent pick <paramref name="index"/> (below <see cref="PickCount"/>): its forward and talent, or false.</summary>
    public bool TryPick(int index, out NetValue forward, out TalentPick pick)
    {
        forward = index >= 0 && index < PickCount ? _pickWhos[index] : default;
        pick = index >= 0 && index < PickCount ? _picks[index] ?? default : default;
        return index >= 0 && index < PickCount && forward.Kind == NetValueKind.Reference && _picks[index] != null;
    }

    /// <summary>The host's cup cultist <paramref name="index"/>: its forward (a reference value) and whether it's turned, or false.</summary>
    public bool TryCultist(int index, out NetValue forward, out bool turned)
    {
        forward = index >= 0 && index < PresentationKeys.MaxCultists ? _cultistWhos[index] : default;
        turned = forward.Kind == NetValueKind.Reference && _cultistTurned[index];
        return forward.Kind == NetValueKind.Reference;
    }

    /// <summary>The intro go flag this message carried, or null.</summary>
    public bool? IntroGoNow { get; private set; }

    /// <summary>The intro step this message carried, or null.</summary>
    public IntroStep? IntroStepNow { get; private set; }

    /// <summary>The Canada pick flag this message carried, or null.</summary>
    public bool? CanadaNow { get; private set; }

    /// <summary>The host's outro phase, or null while it has none (or the value was bad).</summary>
    public OutroPhase? Outro { get; private set; }

    /// <summary>The host's outro stage seed (0 when not known).</summary>
    public int OutroSeed { get; private set; }

    /// <summary>Whether the host skipped its outro's celebration.</summary>
    public bool OutroSkip { get; private set; }

    /// <summary>The host's comic continue presses this outro (0 to <see cref="OutroSteps.MaxContinues"/>).</summary>
    public int OutroNext { get; private set; }

    /// <summary>The host's comic's speech as one progress (<see cref="ComicSpeech.Combine"/>).</summary>
    public SpeechProgress OutroSpeech { get; private set; }

    /// <summary>Whether the host's comic waits for its continue press (its speech is over).</summary>
    public bool OutroWaits { get; private set; }

    /// <summary>Whether the host's win banner is up (null: never heard).</summary>
    public bool? Banner { get; private set; }

    /// <summary>Whether the host's arena is dimmed for its win banner (null: never heard).</summary>
    public bool? BannerDim { get; private set; }

    /// <summary>Whether the host's cover waits for its next reward menu (null: never heard).</summary>
    public bool? RewardFade { get; private set; }

    /// <summary>The forward a transformation shows as drawn on the host: a reference, or none.</summary>
    public NetValue GolferDrawn { get; private set; }

    /// <summary>True when the last message changed a golfer boss form or forward.</summary>
    public bool GolfersChanged { get; private set; }

    /// <summary>Values refused for their kind or range (counted for the log).</summary>
    public int Refused { get; private set; }

    /// <summary>Forgets everything (a new playback).</summary>
    public void Reset()
    {
        IntroGoNow = null;
        IntroStepNow = null;
        CanadaNow = null;
        Outro = null;
        OutroSeed = 0;
        OutroSkip = false;
        OutroNext = 0;
        OutroSpeech = SpeechProgress.None;
        OutroWaits = false;
        Banner = null;
        BannerDim = null;
        RewardFade = null;
        GolferDrawn = NetValue.Null;
        GolfersChanged = false;
        Refused = 0;
        Array.Clear(_golferWhos);
        Array.Clear(_golferForms);
        Array.Clear(_pickWhos);
        Array.Clear(_picks);
        Array.Clear(_cultistWhos);
        Array.Clear(_cultistTurned);
        PickCount = 0;
        PicksChanged = false;
        CultistsChanged = false;
        Frogs.Reset();
        Zaps.Reset();
    }

    /// <summary>After a message has been read: the per-message values are cleared for the next one.</summary>
    public void EndMessage()
    {
        IntroGoNow = null;
        IntroStepNow = null;
        CanadaNow = null;
        GolfersChanged = false;
        PicksChanged = false;
        CultistsChanged = false;
        Frogs.EndMessage();
        Zaps.EndMessage();
    }

    /// <summary>The host's golfer boss transformer <paramref name="index"/>: its forward (a reference value) and form, or false.</summary>
    public bool TryGolfer(int index, out NetValue forward, out int form)
    {
        forward = index >= 0 && index < PresentationKeys.MaxGolfers ? _golferWhos[index] : default;
        form = forward.Kind == NetValueKind.Reference ? _golferForms[index] : 0;
        return forward.Kind == NetValueKind.Reference;
    }

    /// <summary>Reads one HUD value. True when it's a presentation key (taken, or refused as bad); false for any other key.</summary>
    public bool Take(string key, NetValue value)
    {
        if (!PresentationKeys.IsKey(key))
        {
            return false;
        }

        switch (key)
        {
            case PresentationKeys.IntroGo:
                IntroGoNow = Flag(value);
                break;
            case PresentationKeys.IntroStep:
                IntroStepNow = Whole(value, 0, (int)IntroStep.Over) is int step ? (IntroStep)step : null;
                break;
            case PresentationKeys.IntroCanada:
                CanadaNow = Flag(value);
                break;
            case PresentationKeys.OutroPhase:
                Outro = value.Kind == NetValueKind.Null ? null : Whole(value, 0, (int)OutroPhase.Done) is int phase ? (OutroPhase)phase : Outro;
                break;
            case PresentationKeys.OutroSeed:
                OutroSeed = Whole(value, int.MinValue, int.MaxValue) ?? OutroSeed;
                break;
            case PresentationKeys.OutroSkip:
                OutroSkip = Flag(value) ?? OutroSkip;
                break;
            case PresentationKeys.OutroNext:
                OutroNext = Whole(value, 0, OutroSteps.MaxContinues) ?? OutroNext;
                break;
            case PresentationKeys.OutroSpeech:
                if (Whole(value, 0, SpeechProgress.MaxValue) is int speech)
                {
                    if (SpeechProgress.TryRead(speech, out SpeechProgress progress))
                    {
                        OutroSpeech = progress;
                    }
                    else
                    {
                        Refused++;
                    }
                }

                break;
            case PresentationKeys.OutroWaits:
                OutroWaits = Flag(value) ?? OutroWaits;
                break;
            case PresentationKeys.Banner:
                Banner = Flag(value) ?? Banner;
                break;
            case PresentationKeys.BannerDim:
                BannerDim = Flag(value) ?? BannerDim;
                break;
            case PresentationKeys.RewardFade:
                RewardFade = Flag(value) ?? RewardFade;
                break;
            default:
                if (key.StartsWith(PresentationKeys.PickPrefix, StringComparison.Ordinal))
                {
                    TakePick(key, value);
                }
                else if (key.StartsWith(PresentationKeys.CultistPrefix, StringComparison.Ordinal))
                {
                    TakeCultist(key, value);
                }
                else if (Frogs.Take(key, value) || Zaps.Take(key, value))
                {
                    // Taken (or refused, counted by the slots).
                }
                else
                {
                    TakeGolfer(key, value);
                }

                break;
        }

        return true;
    }

    private void TakePick(string key, NetValue value)
    {
        if (key == PresentationKeys.PickCount)
        {
            if (Whole(value, 0, PresentationKeys.MaxPicks) is int count)
            {
                PicksChanged |= PickCount != count;
                PickCount = count;
            }

            return;
        }

        for (int i = 0; i < PresentationKeys.MaxPicks; i++)
        {
            if (key == PresentationKeys.PickWho(i))
            {
                if (value.Kind is NetValueKind.Reference or NetValueKind.Null or NetValueKind.Text)
                {
                    PicksChanged |= _pickWhos[i] != value;
                    _pickWhos[i] = value;
                }
                else
                {
                    Refused++;
                }

                return;
            }

            if (key == PresentationKeys.PickTalent(i))
            {
                TalentPick? pick = value.Kind == NetValueKind.Text && TalentPick.TryParse(value.Text, out TalentPick parsed) ? parsed : null;
                if (pick == null && value.Kind != NetValueKind.Null)
                {
                    Refused++;
                    return;
                }

                PicksChanged |= _picks[i] != pick;
                _picks[i] = pick;
                return;
            }
        }
    }

    private void TakeCultist(string key, NetValue value)
    {
        for (int i = 0; i < PresentationKeys.MaxCultists; i++)
        {
            if (key == PresentationKeys.CultistWho(i))
            {
                if (value.Kind is NetValueKind.Reference or NetValueKind.Null or NetValueKind.Text)
                {
                    CultistsChanged |= _cultistWhos[i] != value;
                    _cultistWhos[i] = value;
                }
                else
                {
                    Refused++;
                }

                return;
            }

            if (key == PresentationKeys.CultistTurned(i))
            {
                if (Flag(value) is bool turned)
                {
                    CultistsChanged |= _cultistTurned[i] != turned;
                    _cultistTurned[i] = turned;
                }

                return;
            }
        }
    }

    private void TakeGolfer(string key, NetValue value)
    {
        if (key == PresentationKeys.GolferDrawn)
        {
            if (value.Kind is NetValueKind.Reference or NetValueKind.Null or NetValueKind.Text)
            {
                GolfersChanged |= GolferDrawn != value;
                GolferDrawn = value;
            }
            else
            {
                Refused++;
            }

            return;
        }

        for (int i = 0; i < PresentationKeys.MaxGolfers; i++)
        {
            if (key == PresentationKeys.GolferWho(i))
            {
                // A scene path's text is a golfer the host's stream doesn't carry (yet): nothing to follow, nothing bad.
                if (value.Kind is NetValueKind.Reference or NetValueKind.Null or NetValueKind.Text)
                {
                    GolfersChanged |= _golferWhos[i] != value;
                    _golferWhos[i] = value;
                }
                else
                {
                    Refused++;
                }

                return;
            }

            if (key == PresentationKeys.GolferForm(i))
            {
                if (Whole(value, 0, GolferForms.Max) is int form)
                {
                    GolfersChanged |= _golferForms[i] != form;
                    _golferForms[i] = form;
                }

                return;
            }
        }

        // An unknown golfer key (a newer host): ignored, not refused.
    }

    private bool? Flag(NetValue value)
    {
        if (value.Kind == NetValueKind.Flag)
        {
            return value.WholeValue != 0;
        }

        Refused++;
        return null;
    }

    private int? Whole(NetValue value, long min, long max)
    {
        if (value.Kind == NetValueKind.Whole && value.WholeValue >= min && value.WholeValue <= max)
        {
            return (int)value.WholeValue;
        }

        Refused++;
        return null;
    }
}

/// <summary>What a friend's PC does about the golfer outro now (<see cref="OutroSteps.Next"/>).</summary>
public enum OutroAction
{
    /// <summary>Nothing.</summary>
    None,

    /// <summary>Begin this PC's own outro (the game's <c>GolferOutroDirector.Begin</c> and <c>PlayToEnd</c>) with the host's seed.</summary>
    Start,

    /// <summary>Skip this PC's celebration, as the host did.</summary>
    Skip,

    /// <summary>Move this PC's comic on one page (its continue), as the host did.</summary>
    Continue,

    /// <summary>The host's outro is over and this PC's isn't after <see cref="OutroSteps.GraceMicros"/>: end it here.</summary>
    CutShort,
}

/// <summary>
/// Friend side: a friend's PC plays its own golfer outro (the game's director) in step with the host's:
/// it starts when the host's starts playing, skips its celebration when the
/// host did, moves its comic on as the host presses continue (each press applied once, when this PC's comic waits for
/// one), and is ended here a moment after the host's is over. Idempotent: a value heard twice does nothing new; a press
/// heard before this PC's comic waits is kept until it does. Pure logic; the glue runs the game's code. Not thread-safe.
/// </summary>
public sealed class OutroSteps
{
    /// <summary>The most continue presses carried (the comic waits for one at most; more are refused as bad values).</summary>
    public const int MaxContinues = 16;

    /// <summary>How long this PC's outro may run on after the host's is over before it's ended here.</summary>
    public const long GraceMicros = 3_000_000;

    private long _hostDoneAt = long.MinValue;

    /// <summary>True once this PC's outro was started.</summary>
    public bool Started { get; private set; }

    /// <summary>True once this PC's celebration was skipped for the host.</summary>
    public bool Skipped { get; private set; }

    /// <summary>The host's continue presses applied here.</summary>
    public int Continued { get; private set; }

    /// <summary>True once this PC's outro was ended here (or ended by itself after starting).</summary>
    public bool Finished { get; private set; }

    /// <summary>The host phase seen first (for the log: a friend joining mid-outro starts there).</summary>
    public OutroPhase? FirstSeen { get; private set; }

    /// <summary>A new match: forgets the last outro.</summary>
    public void Reset()
    {
        Started = Skipped = Finished = false;
        Continued = 0;
        FirstSeen = null;
        _hostDoneAt = long.MinValue;
    }

    /// <summary>
    /// What to do now, given the host's outro (<paramref name="host"/>: phase, or null for none; whether it skipped; its
    /// continue presses), this PC's (<paramref name="localDone"/>: its outro is over; <paramref name="pageWaits"/>: its
    /// comic waits for a continue it hasn't had) and whether it can start one (<paramref name="canStart"/>: the host's
    /// match is shown here). One action per call: call again until it returns <see cref="OutroAction.None"/>. The caller
    /// reports what it did (<see cref="Did"/>).
    /// </summary>
    public OutroAction Next(OutroPhase? host, bool hostSkipped, int hostContinues, bool localDone, bool pageWaits, bool canStart, long now)
    {
        if (host is OutroPhase seen && FirstSeen == null)
        {
            FirstSeen = seen;
        }

        if (Finished)
        {
            return OutroAction.None;
        }

        if (!Started)
        {
            // Only a playing outro starts one here: one heard first when it's over (a friend joining after it) doesn't.
            return canStart && host is OutroPhase.Revealing or OutroPhase.Celebrating or OutroPhase.Comic ? OutroAction.Start : OutroAction.None;
        }

        if (localDone)
        {
            Finished = true;
            return OutroAction.None;
        }

        if (!Skipped && (hostSkipped || host is OutroPhase.Comic or OutroPhase.Done))
        {
            // The host skipped, or it's already at its comic (a friend that joined late): straight on here too.
            return OutroAction.Skip;
        }

        // A comic that waits with the host's outro over goes on too: the host's own had no continue (its comic ended with the
        // cup's exit), so no press is coming, and this PC's own ending (its fade) is better than being cut off.
        if (pageWaits && (Continued < hostContinues || host is OutroPhase.Done or null))
        {
            return OutroAction.Continue;
        }

        if (host is OutroPhase.Done or null)
        {
            if (_hostDoneAt == long.MinValue)
            {
                _hostDoneAt = now;
            }

            if (now - _hostDoneAt >= GraceMicros)
            {
                return OutroAction.CutShort;
            }
        }
        else
        {
            _hostDoneAt = long.MinValue;
        }

        return OutroAction.None;
    }

    /// <summary>The caller did <paramref name="action"/>.</summary>
    public void Did(OutroAction action)
    {
        switch (action)
        {
            case OutroAction.Start:
                Started = true;
                break;
            case OutroAction.Skip:
                Skipped = true;
                break;
            case OutroAction.Continue:
                Continued++;
                break;
            case OutroAction.CutShort:
                Finished = true;
                break;
        }
    }
}

/// <summary>
/// Host side: counts the golfer comic's continue presses from its waits (<c>GolferComicPage._continue</c>, a new
/// completion source per wait): a wait seen completed counts once. Pure logic; the glue reads the page. Not thread-safe.
/// </summary>
public sealed class ComicPressCounter
{
    private long _counted;

    /// <summary>Presses counted this outro (capped at <see cref="OutroSteps.MaxContinues"/>).</summary>
    public int Count { get; private set; }

    /// <summary>A new outro.</summary>
    public void Reset()
    {
        _counted = 0;
        Count = 0;
    }

    /// <summary>
    /// One sample of the page's current wait (<paramref name="wait"/>: an identity for it, 0 for none;
    /// <paramref name="completed"/>: whether it's no longer pending). Returns the count.
    /// </summary>
    public int Sample(long wait, bool completed)
    {
        if (wait != 0 && completed && wait != _counted && Count < OutroSteps.MaxContinues)
        {
            _counted = wait;
            Count++;
        }

        return Count;
    }
}

/// <summary>How far the host's golfer comic is, for a friend's comic's speech (<see cref="ComicSpeech.StageFor"/>).</summary>
public enum ComicHost
{
    /// <summary>Its speech may still go on.</summary>
    Speaking,

    /// <summary>It waits for its continue press: its speech is over, and its player reads the page.</summary>
    Waits,

    /// <summary>It has moved on (its continue was pressed, or its outro is over).</summary>
    MovedOn,
}

/// <summary>
/// The golfer comic's speech on a friend's PC. Every line is a manual one, so a
/// friend's comic stays on its first line unless something presses for it, and it shows this player's own strip and cups
/// (D42 amendment), so its dialogue can be another one than the host's, with other speakers and another number of lines
/// (Cup 1 has 6, Cup 3 has 4, the ending narration 15). So the comic is followed as one speech, whoever speaks: a friend's
/// line number n goes on when the host's has gone past its line number n (<see cref="StageFor"/>; the same dialogue is then
/// followed exactly, press for press). A friend's dialogue longer than the host's goes on at a reading pace once the host's
/// page waits for its continue, and at once when the host has moved on, so it never stalls; one shorter waits at its own
/// continue prompt for the host's press. The stage goes to each speaker through the speech mirror
/// (<c>SpeechMirror.FriendHostValue</c>), which presses this PC's bubble with the game's own skip. Pure logic. Not thread-safe.
/// </summary>
public sealed class ComicSpeech
{
    /// <summary>How long a line the host's speech has nothing for stays up here while the host's page waits (seconds).</summary>
    public const double ExtraLineSeconds = 3.5;

    private int _line;
    private double _lineSince;
    private bool _ranOutSaid;

    /// <summary>Lines of this PC's comic that went on after the host's speech was over (the host's dialogue was shorter).</summary>
    public int RanOut { get; private set; }

    /// <summary>
    /// The host's speakers' progress values as one progress: the lines so far, of any speaker (capped at
    /// <see cref="SpeechProgress.MaxKey"/>), and how far the newest is: one line speaks at a time, so every earlier one's press
    /// has come, and the lowest stage among the speakers is the newest line's. A value that isn't a progress counts as none.
    /// </summary>
    public static SpeechProgress Combine(ReadOnlySpan<int> speakerValues)
    {
        int lines = 0;
        SpeechStage stage = SpeechStage.Advanced;
        foreach (int value in speakerValues)
        {
            if (SpeechProgress.TryRead(value, out SpeechProgress progress) && !progress.IsNone)
            {
                lines += progress.Key;
                stage = progress.Stage < stage ? progress.Stage : stage;
            }
        }

        return lines == 0 ? SpeechProgress.None : SpeechProgress.Line(Math.Min(lines, SpeechProgress.MaxKey), stage);
    }

    /// <summary>A new comic.</summary>
    public void Reset()
    {
        _line = 0;
        _lineSince = 0;
        _ranOutSaid = false;
        RanOut = 0;
    }

    /// <summary>
    /// Where this PC's line number <paramref name="ownLine"/> (its lines so far, of any speaker; 0: none yet) should be now
    /// (<paramref name="now"/>, seconds), from the host's speech as one progress and how far its comic is.
    /// <see cref="SpeechStage.Advanced"/> presses it on (shown in full first while it types).
    /// </summary>
    public SpeechStage StageFor(int ownLine, SpeechProgress host, ComicHost state, double now)
    {
        if (ownLine != _line)
        {
            _line = ownLine;
            _lineSince = now;
            _ranOutSaid = false;
        }

        if (ownLine <= 0)
        {
            return SpeechStage.Started;
        }

        if (host.Key > ownLine || (host.Key == ownLine && host.Stage == SpeechStage.Advanced))
        {
            return SpeechStage.Advanced;
        }

        if (state == ComicHost.MovedOn || (state == ComicHost.Waits && now - _lineSince >= ExtraLineSeconds))
        {
            if (!_ranOutSaid)
            {
                _ranOutSaid = true;
                RanOut++;
            }

            return SpeechStage.Advanced;
        }

        return host.Key == ownLine ? host.Stage : SpeechStage.Started;
    }
}

/// <summary>The golfer boss's forms (the game's <c>GolferBossTierSystem.Form</c>), named for logs.</summary>
public static class GolferForms
{
    /// <summary>The highest form (giant mutant).</summary>
    public const int Max = 2;

    /// <summary>The form's name for logs: normal, mutant or giant mutant.</summary>
    public static string Name(int form) => form switch
    {
        0 => "normal",
        1 => "mutant",
        2 => "giant mutant",
        _ => form.ToString(CultureInfo.InvariantCulture),
    };
}
