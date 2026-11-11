namespace T2TOnline.Core.Session;

/// <summary>What <see cref="WinRuleWatch.Close"/> found as a match's end showed.</summary>
public enum WinRuleClose
{
    /// <summary>Nothing to say: the match isn't a campaign's, its rule is kept already, its end doesn't show yet, or it was never loaded here.</summary>
    Nothing = 0,

    /// <summary>The rule read while the match was loaded here is the one to note.</summary>
    Read = 1,

    /// <summary>The match was loaded here and its rule never read: its moments judge a win as the home team ahead.</summary>
    Unread = 2,
}

/// <summary>
/// When a friend's game reads the game's rule for the winner of the host's campaign match (D135,
/// <see cref="MatchWinRules"/>), and which read counts. The game decides a match's winner as it ends, from what the match
/// holds then; a friend's game holds the same things only while the match is loaded there. The goal target is final
/// from the load on: a load starts with the setup's (what the host's map node set: in a speedrun run an Elite's 3, a
/// Boss's 4), and the match's own set-up works it out again while the scene loads, before the match's first frame, here
/// as on the host (<c>GameLoader.Awake</c> runs <c>NormalMatchGameModeController.Initialize</c>, whose
/// <c>NewNationalAnthem.StartAnthem</c> calls <c>OnAnthemOver</c> back at once, through <c>Skip</c> and <c>Cleanup</c>,
/// and that runs <c>InitializeEndOfGame</c>: in a run played to a goal target, a match that ends at one gets an Elite's
/// 4, a Boss's 5 or 6); nothing in the game writes it after that (the Nov 6 build). The watch relies on none of it
/// staying the same through a match:
/// <list type="bullet">
/// <item>the rule is read only while the match is loaded here (<see cref="ReadDue"/>): as soon as it is, then again
/// every <see cref="RereadSeconds"/>, and once more on the frame its end shows;</item>
/// <item>the newest read is held (<see cref="Took"/>), never one made after the match has gone;</item>
/// <item>it is given out once, as the end shows here (<see cref="Close"/>), whether the match is still loaded then or
/// not. A match whose end never shows here keeps no rule: its moments need its end seen.</item>
/// </list>
/// A match whose rule the record already holds (a rejoin after this game restarted) isn't read again: what was noted first
/// stays (<see cref="MatchView.With"/>). Game-independent. Not thread-safe (one game thread).
/// </summary>
public sealed class WinRuleWatch
{
    /// <summary>How long after a read the next one is due while the match is loaded.</summary>
    public const double RereadSeconds = 2;

    private ulong _session;
    private bool _settled = true;
    private bool _tried;
    private double _nextRead;
    private MatchView _held = MatchView.Unknown;
    private string _problem = string.Empty;

    /// <summary>The host's match watched (its number on the host; 0: none).</summary>
    public int Match { get; private set; }

    /// <summary>True when <paramref name="match"/> of hosting session <paramref name="session"/> isn't the one watched: <see cref="Begin"/> it.</summary>
    public bool IsAnother(ulong session, int match) => session != _session || match != Match;

    /// <summary>
    /// Another match (or none: 0) of hosting session <paramref name="session"/> is noted here; <paramref name="known"/>:
    /// what this PC's record holds of it. Only a campaign match with no rule kept yet is read. What was held of the match
    /// before is dropped: its end never showed here.
    /// </summary>
    public void Begin(ulong session, int match, in MatchView known)
    {
        _session = session;
        Match = match;
        _settled = match <= 0 || !known.IsCampaign || known.Rule != WinRule.Unknown;
        _tried = false;
        _nextRead = 0;
        _held = MatchView.Unknown;
        _problem = string.Empty;
    }

    /// <summary>
    /// Once a frame: true when the rule is to be read from the game now (then <see cref="Took"/> or <see cref="Missed"/>).
    /// <paramref name="loaded"/>: the match's scene is in this game (playing, or held on its last frame);
    /// <paramref name="ended"/>: its end shows here; <paramref name="now"/>: seconds on any steady clock.
    /// </summary>
    public bool ReadDue(bool loaded, bool ended, double now)
    {
        if (_settled || !loaded || (now < _nextRead && !ended))
        {
            return false;
        }

        _nextRead = now + RereadSeconds;
        _tried = true;
        return true;
    }

    /// <summary>
    /// A read gave <paramref name="read"/> (its <see cref="MatchView.Rule"/> and <see cref="MatchView.GoalTarget"/>). True when
    /// it is news: the first one, or another rule or goal target than the one held.
    /// </summary>
    public bool Took(in MatchView read)
    {
        if (_settled || read.Rule == WinRule.Unknown)
        {
            return false;
        }

        bool news = _held.Rule != read.Rule || _held.GoalTarget != read.GoalTarget;
        _held = MatchView.Unknown with { Rule = read.Rule, GoalTarget = read.GoalTarget };
        return news;
    }

    /// <summary>A read gave nothing (<paramref name="problem"/>: why): what was held before stays.</summary>
    public void Missed(string problem) => _problem = problem ?? string.Empty;

    /// <summary>
    /// Once a frame, after the read: when the match's end shows here (<paramref name="ended"/>) for the first time, what to
    /// do about its rule. <see cref="WinRuleClose.Read"/>: <paramref name="ruled"/> is the newest read made while it was
    /// loaded, to note; <see cref="WinRuleClose.Unread"/>: it was loaded here and never read (<paramref name="problem"/>:
    /// the last read's). Nothing is read for this match after that.
    /// </summary>
    public WinRuleClose Close(bool ended, out MatchView ruled, out string problem)
    {
        ruled = MatchView.Unknown;
        problem = string.Empty;
        if (_settled || !ended)
        {
            return WinRuleClose.Nothing;
        }

        _settled = true;
        if (_held.Rule != WinRule.Unknown)
        {
            ruled = _held;
            return WinRuleClose.Read;
        }

        problem = _problem;
        return _tried ? WinRuleClose.Unread : WinRuleClose.Nothing;
    }
}
