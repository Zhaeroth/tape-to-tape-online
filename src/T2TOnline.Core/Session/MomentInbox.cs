using System;
using System.Collections.Generic;
using System.Globalization;
using T2TOnline.Core.Protocol;

namespace T2TOnline.Core.Session;

/// <summary>What <see cref="MomentInbox.Arrived"/> decided for a moment the host sent.</summary>
public enum MomentArrival
{
    /// <summary>Unlock it now: this PC's game saw the play it came from.</summary>
    UnlockNow,

    /// <summary>It waits: the play it names may still come here (the match loading, the run not held yet).</summary>
    Waiting,

    /// <summary>It's waiting already (the host sent it again).</summary>
    AlreadyWaiting,

    /// <summary>Too many are waiting; it's dropped (the host sends it again when this PC joins again).</summary>
    Full,

    /// <summary>Refused: this PC's game didn't see the play it came from, and can't now (<see cref="MomentReason"/> says why).</summary>
    Refused,

    /// <summary>The same moment of the same match was decided before in this hosting session: ignored.</summary>
    Repeat,
}

/// <summary>Why a shared moment doesn't unlock (yet) on a friend's PC (<see cref="MomentCheck.Judge"/>).</summary>
public enum MomentReason
{
    /// <summary>None: this PC saw the play it came from.</summary>
    None,

    /// <summary>The name isn't one of the shared moments.</summary>
    NotShared,

    /// <summary>The message says it came from the run (or from a match), and that moment comes from the other.</summary>
    WrongSource,

    /// <summary>A match moment that names no match.</summary>
    NoMatch,

    /// <summary>A match moment of a match this PC didn't play (it played a later one, so it won't).</summary>
    MatchNotPlayed,

    /// <summary>A match moment of a match this PC hasn't played yet (it may be loading it).</summary>
    MatchNotYet,

    /// <summary>A run moment while this PC hasn't held the host's co-op run in this hosting session.</summary>
    RunNotHeld,

    /// <summary>The match is still playing here (or held on its last frame before the end): its end decides.</summary>
    MatchNotOver,

    /// <summary>This PC played the match but didn't see it end (it dropped out, and a later match played here since).</summary>
    EndNotSeen,

    /// <summary>The match wasn't the kind the moment comes from (an elite node's for ELITE_WIN, act 2's boss for WIN_ACT2...).</summary>
    WrongKind,

    /// <summary>
    /// This PC saw the match end without the home team (the humans' side) ahead: the mercy rule's win, or a match whose
    /// winner the game decides by the lead (<see cref="WinRule.Ahead"/>, or its rule wasn't read here).
    /// </summary>
    NotWon,

    /// <summary>This PC's stream of the match didn't carry the mercy rule ending it on an elite.</summary>
    NoMercy,

    /// <summary>This PC's stream of the match carried no headshot goal.</summary>
    NoHeadshot,

    /// <summary>
    /// The humans' team (a campaign's home team; either in an exhibition) scored fewer goals in the match here than every one
    /// of its skaters scoring needs.
    /// </summary>
    TooFewGoals,

    /// <summary>
    /// More moments came in this hosting session than the inbox remembers (<see cref="MomentInbox.MaxDecided"/>): an honest
    /// host sends each friend at most one of each shared moment per match.
    /// </summary>
    TooMany,

    /// <summary>
    /// The moment's match played here for less than <see cref="Campaign.StatBounds.SecondsPerMatch"/> before its end
    /// (<see cref="MatchSigns.PlayedEnough"/>): this PC joined it in its last seconds, or was out of it for nearly all of it.
    /// </summary>
    TooLittlePlayed,

    /// <summary>
    /// This PC saw the match end without the home team winning by the game's rule for it, a rule other than the lead
    /// (<see cref="MatchView.Rule"/>: two goals ahead against Tied Homie, the goal target in a speedrun).
    /// </summary>
    NotWonByRule,
}

/// <summary>
/// A friend's check of a shared moment against the play its own game saw (D135): only for a match this PC played under the
/// number the moment names, of the kind the game's own condition needs, as this PC saw it (<see cref="MatchView"/>); a run
/// moment (WIN_ACT3, GAWAIN) also only while this PC holds the host's co-op run (or held it earlier in the hosting session:
/// a drop or a restart came between). Per moment, the game's condition and what this PC checks of it:
/// <list type="bullet">
/// <item>MERCY (<c>Mercy.OnAfterReplay</c> raises <c>RogueEvents.WinByMercy(isElite)</c> once the home team leads by the
/// mercy margin, or reaches its goal threshold ahead; <c>AchievementManager.OnWinByMercy</c> unlocks for an elite only, and
/// asks no winner): an Elite node's match, the mercy event on an elite in this PC's stream of it, seen to end with the home
/// team ahead (<see cref="MatchView.HomeAhead"/>, whatever the winner's rule).</item>
/// <item>ELITE_WIN, WIN_ACT2 (<c>AchievementManager.OnGameWin</c>: the home team winning a campaign match at an Elite node,
/// or at a Boss node in act 2, <c>Team.IsWinning</c>): that kind of match, seen to end with the home team winning by the
/// game's own rule for it (<see cref="MatchView.HomeWon"/>, <see cref="MatchWinRules"/>: ahead, unless this PC's run or
/// its copy of the visitors changes the rule).</item>
/// <item>WIN_ACT3 (<c>RogueEvents.RunWon</c>: the run's last boss beaten), GAWAIN (<c>RogueEvents.EvolveGawain</c>, raised
/// only by a boss win's evolution, <c>BossMapNode.OnGameEnd</c>; both from the end-of-match context's winner,
/// <c>Team.IsWinning</c>): a Boss node's match (WIN_ACT3: act 3 or later), seen to end with the home team winning by the
/// game's rule for it, the run held. Whether Gawain is in the squad is the run's, not the match's.</item>
/// <item>SCORING_SQUAD (<c>AchievementManager.OnGoalScored</c>: every forward on the line of the scorer's team, the team
/// with players, has scored): seen to end with that team's goals at least <see cref="GoalsForSquad"/>; this PC doesn't keep
/// who scored. The team with players is a campaign's home team; in an exhibition it may be either, so either side's goals
/// count.</item>
/// <item>HEADSHOT_REDIRECT (<c>AchievementManager.OnHeadshotGoal</c>, any <c>RogueEvents.HeadshotGoal</c>): that event in
/// this PC's stream of the match.</item>
/// </list>
/// Every moment, a run moment's named match too, also needs <see cref="Campaign.StatBounds.SecondsPerMatch"/> of its match
/// played here before its end (<see cref="MatchSigns.PlayedEnough"/>): a friend who joined in its last seconds didn't share
/// it, and a host can't make one of matches a few seconds long. A moment of a match still playing here waits for its end
/// (and those seconds). Game-independent.
/// </summary>
public static class MomentCheck
{
    /// <summary>What <paramref name="witness"/> says of <paramref name="moment"/>: <see cref="MomentReason.None"/> when it unlocks.</summary>
    public static MomentReason Judge(in AchievementMoment moment, IMomentWitness witness)
    {
        ArgumentNullException.ThrowIfNull(witness);
        if (!SharedMoments.Contains(moment.Name))
        {
            return MomentReason.NotShared;
        }

        bool run = SharedMoments.SourceOf(moment.Name) == MomentSource.Run;
        if (run != moment.FromRun)
        {
            return MomentReason.WrongSource;
        }

        if (run && !witness.HeldRun)
        {
            return MomentReason.RunNotHeld;
        }

        if (moment.Match <= 0)
        {
            return MomentReason.NoMatch;
        }

        bool later = witness.NewestPlayed > moment.Match;
        if (!witness.Played(moment.Match))
        {
            return later ? MomentReason.MatchNotPlayed : MomentReason.MatchNotYet;
        }

        return JudgeView(moment.Name, witness.ViewOf(moment.Match), later);
    }

    /// <summary>
    /// The goals SCORING_SQUAD needs of the humans' team at the least: two (a line has more than one skater), or one when
    /// that team plays a single skater (<see cref="MatchView.Skaters"/>). The line's exact size isn't in what this PC sees
    /// for sure, so no more is asked.
    /// </summary>
    public static int GoalsForSquad(int skaters) => skaters == 1 ? 1 : 2;

    /// <summary>True when a moment with <paramref name="reason"/> may still unlock (it waits).</summary>
    public static bool MayCome(MomentReason reason) => reason is MomentReason.MatchNotYet or MomentReason.RunNotHeld or MomentReason.MatchNotOver;

    /// <summary>A moment of a match this PC played, against what it saw of it (<paramref name="later"/>: it played a later one since).</summary>
    private static MomentReason JudgeView(string name, in MatchView view, bool later)
    {
        bool playedEnough = view.Signs.HasFlag(MatchSigns.PlayedEnough);
        if (name == "HEADSHOT_REDIRECT" && view.Signs.HasFlag(MatchSigns.HeadshotGoal) && playedEnough)
        {
            return MomentReason.None;
        }

        bool rightKind = name switch
        {
            "MERCY" or "ELITE_WIN" => view.Kind == MatchKind.Elite,
            "WIN_ACT2" => view.Kind == MatchKind.Boss && view.Act == 2,
            "WIN_ACT3" => view.Kind == MatchKind.Boss && view.Act >= 3,
            "GAWAIN" => view.Kind == MatchKind.Boss,
            _ => view.Kind != MatchKind.Unknown,
        };
        if (!rightKind)
        {
            return MomentReason.WrongKind;
        }

        if (!view.Ended)
        {
            return later ? MomentReason.EndNotSeen : MomentReason.MatchNotOver;
        }

        if (!playedEnough)
        {
            return MomentReason.TooLittlePlayed;
        }

        return name switch
        {
            "MERCY" when !view.Signs.HasFlag(MatchSigns.EliteMercy) => MomentReason.NoMercy,
            "MERCY" => view.HomeAhead ? MomentReason.None : MomentReason.NotWon,
            "SCORING_SQUAD" => SquadGoals(view) >= GoalsForSquad(view.Skaters) ? MomentReason.None : MomentReason.TooFewGoals,
            "HEADSHOT_REDIRECT" => MomentReason.NoHeadshot,
            _ => view.HomeWon ? MomentReason.None : view.Rule is WinRule.Unknown or WinRule.Ahead ? MomentReason.NotWon : MomentReason.NotWonByRule,
        };
    }

    /// <summary>
    /// For the line of a moment unlocked here: " (won by the game's own rule for that match: ...)" when a rule other than the
    /// lead let <paramref name="name"/>'s win through (<see cref="MatchView.Rule"/>), else nothing. Only the moments the
    /// game gives for a won match (ELITE_WIN, WIN_ACT2, WIN_ACT3, GAWAIN) name it.
    /// </summary>
    public static string WinRuleNote(string? name, in MatchView view) =>
        name is "ELITE_WIN" or "WIN_ACT2" or "WIN_ACT3" or "GAWAIN" && view.Rule is not (WinRule.Unknown or WinRule.Ahead)
            ? string.Create(CultureInfo.InvariantCulture,
                $" (won by the game's own rule for that match: {MatchWinRules.Words(view.Rule, view.GoalTarget)}; seen to end {Math.Max(view.HomeGoals, 0)}-{Math.Max(view.VisitorGoals, 0)} here)")
            : string.Empty;

    /// <summary>
    /// The goals of the team the humans may have played in <paramref name="view"/>: a campaign's home team; in an
    /// exhibition, where players may be on either team, the side that scored more.
    /// </summary>
    private static int SquadGoals(in MatchView view) =>
        view.Kind == MatchKind.Exhibition ? Math.Max(view.HomeGoals, view.VisitorGoals) : view.HomeGoals;

    /// <summary><paramref name="reason"/> in words, for the log.</summary>
    public static string Words(MomentReason reason) => reason switch
    {
        MomentReason.None => "this PC saw the play it came from",
        MomentReason.NotShared => "it isn't one of the shared moments",
        MomentReason.WrongSource => "the message names the wrong kind of play for it (a match or the run)",
        MomentReason.NoMatch => "it names no match",
        MomentReason.MatchNotPlayed => "this PC didn't play that match",
        MomentReason.MatchNotYet => "this PC hasn't played that match (yet)",
        MomentReason.RunNotHeld => "this PC hasn't held the host's co-op run in this hosting session",
        MomentReason.MatchNotOver => "this PC hasn't seen that match end yet",
        MomentReason.EndNotSeen => "this PC didn't see that match end (it played a later one since)",
        MomentReason.WrongKind => "that match, as this PC loaded it, isn't the kind this moment comes from",
        MomentReason.NotWon => "this PC saw that match end without the home team ahead",
        MomentReason.NoMercy => "this PC's stream of that match didn't show the mercy rule ending an elite",
        MomentReason.NoHeadshot => "this PC's stream of that match carried no headshot goal",
        MomentReason.TooFewGoals => "the humans' team scored too few goals in that match here for every skater to have scored",
        MomentReason.TooMany => $"more moments came from the host in this hosting session than an honest host sends ({MomentInbox.MaxDecided} decided already)",
        MomentReason.TooLittlePlayed => string.Create(CultureInfo.InvariantCulture,
            $"this PC played less than {Campaign.StatBounds.SecondsPerMatch:0} seconds of that match before its end (it joined in its last seconds, or was out of it for nearly all of it)"),
        MomentReason.NotWonByRule => "this PC saw that match end without the home team winning by the game's rule for it",
        _ => reason.ToString(),
    };

    /// <summary>
    /// <paramref name="name"/> (a name the host sent) for the log: the name when it's a shared moment's, else words that say
    /// so. Anything else is text the host's game chose, which never goes in a log.
    /// </summary>
    public static string NameForLog(string? name) => SharedMoments.Contains(name) ? name! : "an achievement that isn't a shared moment";
}

/// <summary>
/// A friend's side of the shared moments (D34, D135): each one the host sends is judged against what this PC's game saw of
/// the host's play (<see cref="MomentCheck"/>). One it saw unlocks; one it can't have seen is refused; one whose play may
/// still come here (a match still loading, the run not held yet, a restart before the host's run loads again) waits and
/// is judged again (<see cref="TryTakeDecided"/>). Each moment of each match is decided once per hosting session: the host
/// sends a friend everything they're owed whenever they join again, and those repeats are ignored. One per F7 session.
/// </summary>
public sealed class MomentInbox
{
    /// <summary>Most moments kept waiting (the game has seven shared moments; a host can't make this PC hold more).</summary>
    public const int MaxWaiting = 16;

    /// <summary>
    /// Most decided moments remembered per hosting session. An honest host sends each friend at most one of each shared
    /// moment per match (the seven of them in every one of a hundred matches stay under it); beyond this many, a new one is
    /// refused (<see cref="MomentReason.TooMany"/>), so one sent over and over can't be unlocked again each time.
    /// </summary>
    public const int MaxDecided = 1024;

    private readonly List<AchievementMoment> _waiting = new();
    private readonly HashSet<(string Name, int Match)> _decided = new();
    private ulong _session;

    /// <summary>Moments waiting.</summary>
    public int Waiting => _waiting.Count;

    /// <summary>Moments the host sent again after they were decided (ignored).</summary>
    public int Repeats { get; private set; }

    /// <summary>
    /// The hosting session the moments now come from (a welcome). Another one than before: what waited and what was
    /// decided start over (the host's match numbers are its game's own; a new hosting session owes its own moments).
    /// </summary>
    public void Welcomed(ulong session)
    {
        if (session != _session)
        {
            _session = session;
            _waiting.Clear();
            _decided.Clear();
        }
    }

    /// <summary>The host sent <paramref name="moment"/>; <paramref name="reason"/> says why it doesn't unlock (now).</summary>
    public MomentArrival Arrived(in AchievementMoment moment, IMomentWitness witness, out MomentReason reason)
    {
        ArgumentNullException.ThrowIfNull(moment.Name);
        reason = MomentReason.None;
        if (_decided.Contains((moment.Name, moment.Match)))
        {
            Repeats++;
            return MomentArrival.Repeat;
        }

        if (_decided.Count >= MaxDecided)
        {
            reason = MomentReason.TooMany;
            return MomentArrival.Refused;
        }

        reason = MomentCheck.Judge(moment, witness);
        if (!MomentCheck.MayCome(reason))
        {
            Decide(moment);
            return reason == MomentReason.None ? MomentArrival.UnlockNow : MomentArrival.Refused;
        }

        foreach (AchievementMoment waiting in _waiting)
        {
            if (waiting == moment)
            {
                return MomentArrival.AlreadyWaiting;
            }
        }

        if (_waiting.Count >= MaxWaiting)
        {
            return MomentArrival.Full;
        }

        _waiting.Add(moment);
        return MomentArrival.Waiting;
    }

    /// <summary>
    /// The next waiting moment that's decided now, in the order they arrived: <paramref name="reason"/>
    /// <see cref="MomentReason.None"/> to unlock it, else why it's refused. False when none is (asked every frame: nothing
    /// is made when none waits).
    /// </summary>
    public bool TryTakeDecided(IMomentWitness witness, out AchievementMoment moment, out MomentReason reason)
    {
        for (int i = 0; i < _waiting.Count; i++)
        {
            reason = MomentCheck.Judge(_waiting[i], witness);
            if (!MomentCheck.MayCome(reason))
            {
                moment = _waiting[i];
                _waiting.RemoveAt(i);
                Decide(moment);
                return true;
            }
        }

        moment = default;
        reason = MomentReason.None;
        return false;
    }

    /// <summary>The moments still waiting, with why (the session is ending: they won't unlock now); the inbox forgets them.</summary>
    public IReadOnlyList<(AchievementMoment Moment, MomentReason Reason)> TakeLeft(IMomentWitness witness)
    {
        var left = new List<(AchievementMoment, MomentReason)>(_waiting.Count);
        foreach (AchievementMoment moment in _waiting)
        {
            left.Add((moment, MomentCheck.Judge(moment, witness)));
        }

        _waiting.Clear();
        return left;
    }

    /// <summary>Forgets everything (a new F7 session).</summary>
    public void Clear()
    {
        _session = 0;
        _waiting.Clear();
        _decided.Clear();
        Repeats = 0;
    }

    private void Decide(in AchievementMoment moment)
    {
        if (_decided.Count < MaxDecided)
        {
            _decided.Add((moment.Name, moment.Match));
        }
    }
}
