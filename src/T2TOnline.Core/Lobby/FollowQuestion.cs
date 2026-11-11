using System.Globalization;

namespace T2TOnline.Core.Lobby;

/// <summary>A friend's answer to the question a failed match load asks (<see cref="FollowQuestion"/>).</summary>
public enum FollowAnswer
{
    /// <summary>No answer: B, the prompt closed by the game (a scene load hides every menu), or the session ending. Following goes on.</summary>
    None,

    /// <summary>The first button, Try again: leave and join the same lobby again (a mid-run join brings the host's current run and match).</summary>
    TryAgain,

    /// <summary>The second button, Leave: the session ends, as the lobby's Leave or F7 ends it.</summary>
    Leave,
}

/// <summary>What the session does about the question now (<see cref="FollowQuestion.Step"/>).</summary>
public enum FollowQuestionStep
{
    /// <summary>Nothing.</summary>
    Nothing,

    /// <summary>Ask it now (the screen can carry it and the friend's device can answer it).</summary>
    Ask,

    /// <summary>It couldn't be asked within <see cref="FollowQuestion.WaitMicros"/>: the notice's toast says it instead.</summary>
    Tell,

    /// <summary>
    /// It's up and its reason went (the host's next match is loading here): the prompt closes now with no answer, and what was
    /// lent for it goes back now (a prompt closed for its reason sends no answer of its own).
    /// </summary>
    Close,

    /// <summary>It went with no answer reaching here (the game hid it, or it failed): what was lent for it goes back.</summary>
    Gone,
}

/// <summary>
/// Friend: the question a host match that couldn't load here asks, on the game's own two-button prompt: the friend's UI is
/// locked while following, so it waits for a moment it can be asked (no scene load, no other prompt, the friend's own menus
/// closed), up to <see cref="WaitMicros"/>, then the toast says it instead. One question per failure: the same failure said
/// again while one waits or is up changes nothing. Pure logic; not thread-safe (one game thread).
/// </summary>
public sealed class FollowQuestion
{
    /// <summary>How long a failure waits for a moment it can be asked before the toast says it instead.</summary>
    public const long WaitMicros = 20_000_000;

    private long _since;

    /// <summary>A failure waits for its question.</summary>
    public bool Waiting { get; private set; }

    /// <summary>The question is up (asked, and not answered yet).</summary>
    public bool Asking { get; private set; }

    /// <summary>Questions asked this session.</summary>
    public int Asked { get; private set; }

    /// <summary>Failures said by the toast instead (no moment came to ask).</summary>
    public int Told { get; private set; }

    /// <summary>Answered Try again.</summary>
    public int TriedAgain { get; private set; }

    /// <summary>Answered Leave.</summary>
    public int Left { get; private set; }

    /// <summary>Closed with no answer (B, a scene load, the reason gone).</summary>
    public int Dismissed { get; private set; }

    /// <summary>A host match couldn't load here at <paramref name="nowMicros"/>. False when a question already waits or is up for it.</summary>
    public bool Failed(long nowMicros)
    {
        if (Waiting || Asking)
        {
            return false;
        }

        _since = nowMicros;
        Waiting = true;
        return true;
    }

    /// <summary>The reason went (the host's next match is loading here): a waiting question is forgotten. One that's up is the caller's to close.</summary>
    public void Cleared() => Waiting = false;

    /// <summary>Once a frame while one waits: <paramref name="canAsk"/> when the screen can carry it and the friend can answer it now.</summary>
    public FollowQuestionStep Step(bool canAsk, long nowMicros)
    {
        if (!Waiting)
        {
            return FollowQuestionStep.Nothing;
        }

        if (canAsk)
        {
            Waiting = false;
            Asking = true;
            Asked++;
            return FollowQuestionStep.Ask;
        }

        if (nowMicros >= _since && unchecked((ulong)(nowMicros - _since)) >= WaitMicros)
        {
            Waiting = false;
            Told++;
            return FollowQuestionStep.Tell;
        }

        return FollowQuestionStep.Nothing;
    }

    /// <summary>
    /// Once a frame while it's up (<see cref="Asking"/>): <paramref name="reasonGone"/> when the host's next match is loading
    /// here, <paramref name="promptUp"/> while its prompt is on its way or up. Either way out is no answer (counted once), and
    /// the question is over: <see cref="FollowQuestionStep.Close"/> (close the prompt, give back what was lent) or
    /// <see cref="FollowQuestionStep.Gone"/> (give it back). Else nothing: an answer comes through <see cref="Answer"/>.
    /// </summary>
    public FollowQuestionStep WhileAsking(bool reasonGone, bool promptUp)
    {
        if (!Asking || (!reasonGone && promptUp))
        {
            return FollowQuestionStep.Nothing;
        }

        Asking = false;
        Dismissed++;
        return reasonGone ? FollowQuestionStep.Close : FollowQuestionStep.Gone;
    }

    /// <summary>The ask failed at the last moment (the prompt couldn't be shown): it's told instead.</summary>
    public void NotAsked()
    {
        if (Asking)
        {
            Asking = false;
            Asked--;
            Told++;
        }
    }

    /// <summary>
    /// The prompt's result: <paramref name="first"/> for its first button, <paramref name="second"/> for its second; neither is
    /// no answer. Nothing when no question is up.
    /// </summary>
    public FollowAnswer Answer(bool first, bool second)
    {
        if (!Asking)
        {
            return FollowAnswer.None;
        }

        Asking = false;
        if (first)
        {
            TriedAgain++;
            return FollowAnswer.TryAgain;
        }

        if (second)
        {
            Left++;
            return FollowAnswer.Leave;
        }

        Dismissed++;
        return FollowAnswer.None;
    }

    /// <summary>Forgets a waiting or open question (the session ended); the counts stay for the closing line.</summary>
    public void Reset()
    {
        Waiting = false;
        Asking = false;
    }

    /// <summary>The closing line's numbers.</summary>
    public string Summary() => string.Create(CultureInfo.InvariantCulture,
        $"{Asked} asked ({TriedAgain} try again, {Left} leave, {Dismissed} no answer), {Told} said by a toast instead");
}
