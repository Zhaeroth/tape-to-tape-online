using System;
using System.Collections.Generic;
using System.Globalization;
using T2TOnline.Core.Campaign;
using T2TOnline.Core.Serialization;
using T2TOnline.Core.Stream;

namespace T2TOnline.Core.Session;

/// <summary>The kind of one of the host's matches, as the setup a friend's game loaded it from says. Values are kept in a file: only add.</summary>
public enum MatchKind : byte
{
    /// <summary>Not known (a record written before the kind was kept, or a setup that names none).</summary>
    Unknown = 0,

    /// <summary>An exhibition (Play Now) match.</summary>
    Exhibition = 1,

    /// <summary>A campaign Challenge node's match.</summary>
    Challenge = 2,

    /// <summary>A campaign Elite node's match.</summary>
    Elite = 3,

    /// <summary>A campaign Boss node's match.</summary>
    Boss = 4,

    /// <summary>A campaign match of another node type.</summary>
    OtherNode = 5,
}

/// <summary>
/// What a friend's own stream showed of one of the host's matches, beyond its score: signs its events carried, and whether
/// enough of it played here for its moments. Values are kept in a file: only add.
/// </summary>
[Flags]
public enum MatchSigns : byte
{
    /// <summary>Nothing.</summary>
    None = 0,

    /// <summary>The mercy rule ended it on an elite (the game's <c>RogueEvents.WinByMercy</c> with its elite flag set).</summary>
    EliteMercy = 1,

    /// <summary>A headshot goal (the game's <c>RogueEvents.HeadshotGoal</c>).</summary>
    HeadshotGoal = 2,

    /// <summary>
    /// At least <see cref="MatchViewTracker.PlayedEnoughMicros"/> of it played here before its end showed: what every moment
    /// named for it needs (a friend who joined in its last seconds didn't share it).
    /// </summary>
    PlayedEnough = 4,
}

/// <summary>
/// What a friend's game saw of one of the host's matches it played (D135): the kind of match (the setup it loaded), the act
/// and the skaters per side, whether it saw the host end it, the final score its own HUD showed, and the signs its own
/// stream carried; for a campaign match also the game's rule for its winner as this PC's own run and copy of the visitors
/// gave it (<see cref="Rule"/>, <see cref="GoalTarget"/>). A shared moment is judged against it (<see cref="MomentCheck"/>).
/// Kept per match in <see cref="SharedPlayRecord"/> as
/// <c>&lt;kind&gt;.&lt;act&gt;.&lt;skaters&gt;.&lt;home&gt;.&lt;visitor&gt;.&lt;signs&gt;</c> (goals -1 while it hasn't ended
/// here), followed by <c>.&lt;rule&gt;.&lt;goal target&gt;</c> once a rule is known (a view without one is written as before,
/// so earlier builds still read it). Game-independent.
/// </summary>
/// <param name="Kind">The kind of match.</param>
/// <param name="Act">The campaign act (0: none).</param>
/// <param name="Skaters">
/// The skaters per side of the side the humans play (0: not known): a campaign's home team; in an exhibition, where players
/// may be on either team, the smaller side's.
/// </param>
/// <param name="Ended">True once this PC saw the host end it.</param>
/// <param name="HomeGoals">The home team's goals as this PC's HUD last showed them (-1: none seen).</param>
/// <param name="VisitorGoals">The visitors' goals as this PC's HUD last showed them (-1: none seen).</param>
/// <param name="Signs">What this PC's stream of it carried.</param>
public readonly record struct MatchView(MatchKind Kind, int Act, int Skaters, bool Ended, int HomeGoals, int VisitorGoals, MatchSigns Signs)
{
    /// <summary>The largest act, skater count or goal count kept (larger is damage).</summary>
    public const int MaxNumber = 999;

    private const int ChallengeNode = 0;
    private const int EliteNode = 1;
    private const int BossNode = 2;

    /// <summary>Nothing known but that the match played here.</summary>
    public static MatchView Unknown => new(MatchKind.Unknown, 0, 0, false, -1, -1, MatchSigns.None);

    /// <summary>
    /// The game's rule for the match's winner, as this PC's game held what it reads while the match played here
    /// (<see cref="MatchWinRules.Of"/>; <see cref="WinRule.Unknown"/>: not read, a win is the home team ahead).
    /// </summary>
    public WinRule Rule { get; init; }

    /// <summary>The match's goal target as this PC's game held it, for <see cref="WinRule.GoalTarget"/> (0: not read).</summary>
    public int GoalTarget { get; init; }

    /// <summary>
    /// True when this PC saw it end with the home team (the humans' side) winning by the game's own rule for it
    /// (<see cref="Rule"/>): ahead, unless this PC's run or its copy of the visitors changed the rule.
    /// </summary>
    public bool HomeWon => Ended && MatchWinRules.HomeWins(Rule, Math.Max(HomeGoals, 0), Math.Max(VisitorGoals, 0), GoalTarget);

    /// <summary>True when this PC saw it end with the home team ahead (what the mercy rule's win needs, whatever the rule).</summary>
    public bool HomeAhead => Ended && Math.Max(HomeGoals, 0) > Math.Max(VisitorGoals, 0);

    /// <summary>True for a campaign match (a node's; the game's winner rules with relics and squads apply to these).</summary>
    public bool IsCampaign => Kind is MatchKind.Challenge or MatchKind.Elite or MatchKind.Boss or MatchKind.OtherNode;

    /// <summary>
    /// The kind, act and skaters of the match <paramref name="setup"/> describes (the host's setup this PC loaded): a
    /// campaign match by its node type (the game's <c>NodeType</c>: Challenge 0, Elite 1, Boss 2), else an exhibition. A
    /// campaign's human team is the home team; an exhibition's players may be on either team (the game's
    /// <c>TeamSelection.homeTeamPlayers</c> and <c>visitorTeamPlayers</c>), so its skaters are the smaller side's.
    /// </summary>
    public static MatchView FromSetup(IReadOnlyList<KeyValuePair<string, NetValue>>? setup)
    {
        if (setup == null || setup.Count == 0)
        {
            return Unknown;
        }

        int skaters = Whole(setup, "mode.skaterHome");
        if (!CampaignMatchValues.IsCampaign(setup))
        {
            int visitors = Whole(setup, "mode.skaterVisitor");
            int fewest = skaters == 0 ? visitors : visitors == 0 ? skaters : Math.Min(skaters, visitors);
            return Unknown with { Kind = MatchKind.Exhibition, Skaters = fewest };
        }

        NetValue node = MatchSetupValues.Get(setup, "campaign.nodeType");
        MatchKind kind = node.Kind != NetValueKind.Whole ? MatchKind.Unknown : node.WholeValue switch
        {
            ChallengeNode => MatchKind.Challenge,
            EliteNode => MatchKind.Elite,
            BossNode => MatchKind.Boss,
            _ => MatchKind.OtherNode,
        };
        return Unknown with { Kind = kind, Act = Whole(setup, "campaign.act"), Skaters = skaters };
    }

    /// <summary>
    /// This view with what was seen since (<paramref name="later"/>): the kind, act and skaters once known, the end once
    /// seen, the newest score seen (a value not seen keeps the last), every sign either saw, the winner's rule (and its goal
    /// target) as first read.
    /// </summary>
    public MatchView With(in MatchView later) => new(
        Kind == MatchKind.Unknown ? later.Kind : Kind,
        Act == 0 ? later.Act : Act,
        Skaters == 0 ? later.Skaters : Skaters,
        Ended || later.Ended,
        later.HomeGoals >= 0 ? later.HomeGoals : HomeGoals,
        later.VisitorGoals >= 0 ? later.VisitorGoals : VisitorGoals,
        Signs | later.Signs)
    {
        Rule = Rule == WinRule.Unknown ? later.Rule : Rule,
        GoalTarget = Rule == WinRule.Unknown ? later.GoalTarget : GoalTarget,
    };

    /// <summary>The view as a record token (see the type).</summary>
    public string Format()
    {
        string seen = string.Create(CultureInfo.InvariantCulture,
            $"{(int)Kind}.{Act}.{Skaters}.{(Ended ? Math.Max(HomeGoals, 0) : -1)}.{(Ended ? Math.Max(VisitorGoals, 0) : -1)}.{(int)Signs}");
        return Rule == WinRule.Unknown ? seen
            : string.Create(CultureInfo.InvariantCulture, $"{seen}.{(int)Rule}.{Math.Clamp(GoalTarget, 0, MaxNumber)}");
    }

    /// <summary>
    /// A view from a record token (<see cref="Format"/>: six parts, or eight with the winner's rule); false for anything
    /// else. Never throws.
    /// </summary>
    public static bool TryParse(string? text, out MatchView view)
    {
        view = Unknown;
        string[] parts = (text ?? string.Empty).Split('.');
        if ((parts.Length != 6 && parts.Length != 8) || !TryNumber(parts[0], 0, (int)MatchKind.OtherNode, out int kind) || !TryNumber(parts[1], 0, MaxNumber, out int act) ||
            !TryNumber(parts[2], 0, MaxNumber, out int skaters) || !TryNumber(parts[3], -1, MaxNumber, out int home) ||
            !TryNumber(parts[4], -1, MaxNumber, out int visitor) || !TryNumber(parts[5], 0, (int)(MatchSigns.EliteMercy | MatchSigns.HeadshotGoal | MatchSigns.PlayedEnough), out int signs))
        {
            return false;
        }

        int rule = 0, target = 0;
        if (parts.Length == 8 && (!TryNumber(parts[6], (int)WinRule.Ahead, (int)WinRule.NotTwoBehind, out rule) || !TryNumber(parts[7], 0, MaxNumber, out target)))
        {
            return false;
        }

        bool ended = home >= 0 && visitor >= 0;
        view = new MatchView((MatchKind)kind, act, skaters, ended, ended ? home : -1, ended ? visitor : -1, (MatchSigns)signs)
        {
            Rule = (WinRule)rule,
            GoalTarget = target,
        };
        return true;
    }

    /// <summary>
    /// For logs: "an Elite match of act 1, seen to end 3-0 here, the mercy rule on an elite seen"; a campaign match's
    /// winner rule is named when it isn't the home team ahead, and, once its end is seen, when it wasn't read here (the
    /// rule is noted as the end shows, <see cref="WinRuleWatch"/>: before that it isn't missing).
    /// </summary>
    public override string ToString()
    {
        string end = Ended ? string.Create(CultureInfo.InvariantCulture, $", seen to end {Math.Max(HomeGoals, 0)}-{Math.Max(VisitorGoals, 0)} here") : ", its end not seen here";
        string signs = (Signs.HasFlag(MatchSigns.EliteMercy) ? ", the mercy rule on an elite seen" : string.Empty) +
            (Signs.HasFlag(MatchSigns.HeadshotGoal) ? ", a headshot goal seen" : string.Empty) +
            (Signs.HasFlag(MatchSigns.PlayedEnough) ? string.Empty
                : string.Create(CultureInfo.InvariantCulture, $", less than {StatBounds.SecondsPerMatch:0} seconds of it played here"));
        string rule = IsCampaign && Rule != WinRule.Ahead && (Ended || Rule != WinRule.Unknown) ? ", " + MatchWinRules.Words(Rule, GoalTarget) : string.Empty;
        return KindText() + end + signs + rule;
    }

    /// <summary>For logs: the kind of match alone ("an Elite match of act 1", "an exhibition match").</summary>
    public string KindText()
    {
        string kind = Kind switch
        {
            MatchKind.Exhibition => "an exhibition match",
            MatchKind.Challenge => "a Challenge match",
            MatchKind.Elite => "an Elite match",
            MatchKind.Boss => "a Boss match",
            MatchKind.OtherNode => "a campaign match",
            _ => "a match of a kind not known here",
        };
        return kind + (Act > 0 ? string.Create(CultureInfo.InvariantCulture, $" of act {Act}") : string.Empty);
    }

    private static int Whole(IReadOnlyList<KeyValuePair<string, NetValue>> setup, string key) =>
        MatchSetupValues.Get(setup, key) is { Kind: NetValueKind.Whole, WholeValue: >= 0 and <= MaxNumber } value ? (int)value.WholeValue : 0;

    private static bool TryNumber(string text, int least, int most, out int value) =>
        int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value) && value >= least && value <= most;
}

/// <summary>
/// A friend's running view of the host's match playing here (D135), read from its own stream once a frame and noted only
/// when something changed (<see cref="TryNote"/>): the score its HUD showed (a value not seen yet in a playback, a rejoin's
/// until the host's next full HUD, keeps the last one seen), the signs its events carried, whether enough of it played
/// here before its end showed (<see cref="MatchSigns.PlayedEnough"/>, added up over a rejoin's new playback of the same
/// match; a step of at most a second, so a stalled PC adds no more), and the end. The end counts once it has shown here for
/// <see cref="EndSettleMicros"/>: the game raises the mercy rule's event right after the end-of-game event, in the same
/// frame (<c>Mercy.OnAfterReplay</c> calls <c>GameManager.RequestEndOfGame</c> first), and a late packet or a frame's
/// delivery cap can show the two here apart. After that only a sign can still come (the score is final). A match number
/// belongs to one hosting session (it's the host game's own count, which starts over when that game restarts, and another
/// host has its own): another hosting session forgets the match noted. Game-independent. Not thread-safe (one game
/// thread).
/// </summary>
public sealed class MatchViewTracker
{
    /// <summary>How long the end shows here before it counts (what the host raised with it may be shown a little later).</summary>
    public const long EndSettleMicros = 1_000_000;

    /// <summary>
    /// How much of a match must play here before its end for the moments named for it (<see cref="MatchSigns.PlayedEnough"/>):
    /// what a match needs to count for the play bounds too (<see cref="StatBounds.SecondsPerMatch"/>).
    /// </summary>
    public const long PlayedEnoughMicros = (long)(StatBounds.SecondsPerMatch * 1_000_000);

    private const long MaxStepMicros = 1_000_000;

    private ulong _session;
    private bool _ended;
    private long _endShownAt = long.MinValue;
    private long _played;
    private long _playedAt = long.MinValue;
    private int _home = -1;
    private int _visitor = -1;
    private MatchSigns _signs;

    /// <summary>The host's match being noted (its number on the host; 0: none).</summary>
    public int Match { get; private set; }

    /// <summary>
    /// The hosting session this PC is in now (this PC's key for it; 0: none). Another one than before forgets the match
    /// noted, so a match of the same number there starts from nothing.
    /// </summary>
    public void InSession(ulong session)
    {
        if (session != _session)
        {
            _session = session;
            Begin(0);
        }
    }

    /// <summary>
    /// The host's match <paramref name="match"/> (its number on the host; 0 or less: one without its number) started to play
    /// here. Another match than the one noted starts from nothing; the same one again (a rejoin's new playback) keeps what
    /// was seen of it.
    /// </summary>
    public void Played(int match)
    {
        match = Math.Max(match, 0);
        if (match != Match)
        {
            Begin(match);
        }
    }

    /// <summary>
    /// What this PC's stream shows of the match at <paramref name="nowMicros"/>: the home and visitor goals its HUD last
    /// showed (-1: none yet in this playback), the signs this playback's events carried, whether its end shows, and whether
    /// it plays here now (<paramref name="playing"/>). True, with <paramref name="view"/> to note
    /// (<see cref="SharedPlayGate.NoteView"/>), when something new was seen; false otherwise (asked every frame: nothing is
    /// made then).
    /// </summary>
    public bool TryNote(int home, int visitor, MatchSigns signs, bool ended, bool playing, long nowMicros, out MatchView view)
    {
        view = MatchView.Unknown;
        if (Match <= 0)
        {
            return false;
        }

        // The time it plays here before its end: enough of it is what the moments named for it need.
        if (playing && !ended && !_ended)
        {
            _played += _playedAt == long.MinValue ? 0 : Math.Clamp(nowMicros - _playedAt, 0, MaxStepMicros);
            _playedAt = nowMicros;
        }
        else
        {
            _playedAt = long.MinValue;
        }

        signs = (signs & ~MatchSigns.PlayedEnough) | (_played >= PlayedEnoughMicros ? MatchSigns.PlayedEnough : MatchSigns.None);
        if (!ended)
        {
            _endShownAt = long.MinValue;
        }
        else if (_endShownAt == long.MinValue)
        {
            _endShownAt = nowMicros;
        }

        bool endCounts = ended && nowMicros - _endShownAt >= EndSettleMicros;
        bool newSign = (signs & ~_signs) != 0;
        if (_ended || (!endCounts && (home < 0 || home == _home) && (visitor < 0 || visitor == _visitor)))
        {
            // Nothing new before the end counts; after it, only a sign raised with it may still come.
            if (!newSign)
            {
                return false;
            }
        }
        else
        {
            _home = home >= 0 ? home : _home;
            _visitor = visitor >= 0 ? visitor : _visitor;
            _ended = endCounts;
        }

        _signs |= signs;
        view = new MatchView(MatchKind.Unknown, 0, 0, _ended, _home, _visitor, _signs);
        return true;
    }

    private void Begin(int match)
    {
        Match = match;
        _ended = false;
        _endShownAt = long.MinValue;
        _played = 0;
        _playedAt = long.MinValue;
        _home = _visitor = -1;
        _signs = MatchSigns.None;
    }
}
