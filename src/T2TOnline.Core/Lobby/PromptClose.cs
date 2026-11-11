namespace T2TOnline.Core.Lobby;

/// <summary>What a prompt of the game's has come to (<see cref="PromptClose.Look"/>).</summary>
public enum PromptAnswer
{
    /// <summary>Not over yet: it's up, or closing with its answer still to come.</summary>
    None,

    /// <summary>Its first button.</summary>
    First,

    /// <summary>Its second button.</summary>
    Second,

    /// <summary>Over with neither button: Back, a notice dismissed, a close by whoever asked, or a close by the game (another menu's start, a scene load).</summary>
    Neither,

    /// <summary>As <see cref="Neither"/>, taken because its close ran longer than <see cref="PromptClose.CloseSeconds"/> (the log says so).</summary>
    NeitherLate,
}

/// <summary>
/// One frame's look at a prompt that was found, read from the game's menu object. <paramref name="Gone"/> is the engine's word that the menu is
/// destroyed; the object itself, and with it the result the game sent, stays readable for as long as it's held, so
/// <paramref name="ResultSent"/> and the buttons are read whatever <paramref name="Gone"/> says.
/// </summary>
/// <param name="YesNo">A two-button prompt (it sends a result); false for a one-button notice.</param>
/// <param name="ResultSent">The game has sent the prompt's result.</param>
/// <param name="First">The result is its first button.</param>
/// <param name="Second">The result is its second button.</param>
/// <param name="Gone">The menu is destroyed.</param>
/// <param name="Hiding">Its close is running.</param>
/// <param name="Shown">It is shown.</param>
public readonly record struct PromptLook(bool YesNo, bool ResultSent, bool First, bool Second, bool Gone, bool Hiding, bool Shown);

/// <summary>
/// When a prompt of the game's is over, and with which answer. A prompt asked for stays open until this says so, whatever the engine says of
/// its menu: whoever asked is always told, once. The game sends a two-button prompt's result as its close ends (the pressed button, or Cancel
/// for Back), and Cancel when the menu is destroyed with none sent: a prompt the game closes by itself (its plain hide, then the menu's
/// disposal; a scene load) has its result one frame after it's gone. So the result is looked for first, on a menu that is hiding, hidden or
/// destroyed alike; a two-button prompt that is closed with no result gets <see cref="AnswerSeconds"/> for one to come, then it is neither
/// button; a notice, and a prompt closed by whoever asked, is over as soon as its close starts. Pure logic; not thread-safe (one game thread).
/// </summary>
public sealed class PromptClose
{
    /// <summary>How long a closed two-button prompt's result is waited for.</summary>
    public const double AnswerSeconds = 0.25;

    /// <summary>The longest a close may run before the prompt is taken as over.</summary>
    public const double CloseSeconds = 5;

    private double _closingSince = -1;
    private double _closedSince = -1;

    /// <summary>True once whoever asked closed it (<see cref="ClosedByAsker"/>): its result is no one's.</summary>
    public bool ByAsker { get; private set; }

    /// <summary>A new prompt was asked for.</summary>
    public void Begin()
    {
        _closingSince = -1;
        _closedSince = -1;
        ByAsker = false;
    }

    /// <summary>Whoever asked closes it (what it asked about has gone): over as soon as its close starts, with neither button.</summary>
    public void ClosedByAsker() => ByAsker = true;

    /// <summary>This frame's look at <paramref name="now"/> seconds: <see cref="PromptAnswer.None"/> while it goes on, else its answer (ask no more after one).</summary>
    public PromptAnswer Look(in PromptLook look, double now)
    {
        // First, and whatever the engine says of the menu by now: the result comes as the close ends, and the menu may be gone in that very frame.
        if (look.YesNo && look.ResultSent && !ByAsker)
        {
            return look.First ? PromptAnswer.First : look.Second ? PromptAnswer.Second : PromptAnswer.Neither;
        }

        bool closed = look.Gone || (!look.Hiding && !look.Shown);
        if (!closed && !look.Hiding)
        {
            return PromptAnswer.None;
        }

        _closingSince = _closingSince < 0 ? now : _closingSince;
        _closedSince = !closed ? -1 : _closedSince < 0 ? now : _closedSince;
        if (!look.YesNo || ByAsker || (closed && now - _closedSince > AnswerSeconds))
        {
            return PromptAnswer.Neither;
        }

        return now - _closingSince > CloseSeconds ? PromptAnswer.NeitherLate : PromptAnswer.None;
    }
}
