using System;
using System.Collections.Generic;
using System.Globalization;
using T2TOnline.Core.Serialization;
using T2TOnline.Core.Stream;

namespace T2TOnline.Core.Session;

/// <summary>
/// The rule by which the game's own code decides who won one of the host's campaign matches
/// (<c>GameManager.WinningTeam</c>), as a friend's game holds what that rule reads (<see cref="MatchWinRules.Of"/>).
/// Values are kept in a file: only add.
/// </summary>
public enum WinRule : byte
{
    /// <summary>
    /// Not read on this PC (an exhibition, a record written before rules were kept, or this PC couldn't read its run then):
    /// a win is the home team ahead, as a friend judged it before the rule was read.
    /// </summary>
    Unknown = 0,

    /// <summary>The home team wins ahead: nothing changes the game's last rule, or the match ends at the next goal.</summary>
    Ahead = 1,

    /// <summary>
    /// The home team wins on reaching the goal target, whatever the visitors score: the run is the speedrun squad's, or
    /// the home team holds the speedrun curse.
    /// </summary>
    GoalTarget = 2,

    /// <summary>The home team wins only two goals ahead: the visitors hold Tied Homie.</summary>
    TwoAhead = 3,

    /// <summary>The home team also wins at a tie or one goal behind: it holds Tied Homie.</summary>
    NotTwoBehind = 4,
}

/// <summary>
/// What the game's rule for a campaign match's winner reads (<see cref="MatchWinRules.Of"/>), as a friend's game holds it
/// for the host's match playing there: its own run and its own copy of the visitors, never a host's word.
/// </summary>
/// <param name="SpeedrunSquad">The run this PC holds is the speedrun squad's (<see cref="MatchWinRules.SpeedrunSquad"/>).</param>
/// <param name="HomeSpeedrunCurse">The home team's relics hold the speedrun curse (<c>Rogue.Relics.FinalSquadRelics.SpeedrunCurse</c>).</param>
/// <param name="HomeTiedHomie">The home team's relics hold Tied Homie (<c>Rogue.TieDomi</c>).</param>
/// <param name="VisitorTiedHomie">The visitors' relics hold Tied Homie.</param>
/// <param name="GoalTarget">The match's goal target as this PC's game holds it (<c>MaxGoal.goalToScore</c>).</param>
public readonly record struct WinFacts(bool SpeedrunSquad, bool HomeSpeedrunCurse, bool HomeTiedHomie, bool VisitorTiedHomie, int GoalTarget);

/// <summary>
/// The game's rules for a campaign match's winner (D135): a friend applies them to its own run, its own copy of the
/// visitors and the score its own stream showed, so a win the game gives without a lead counts there, and a claimed win
/// those don't support doesn't. <c>GameManager.WinningTeam</c> (the Nov 6 build), in its order:
/// <list type="number">
/// <item>the match's end conditions hold the next goal (<c>EndOfGameConditionType.NextGoal</c>): the home team ahead;</item>
/// <item>the speedrun squad (<c>CampaignUtil.IsPlayingSquad("speedrun")</c>) or the speedrun curse on the home team:
/// the home team at the goal target (<c>MaxGoal.goalToScore</c>) or above;</item>
/// <item>Tied Homie on the visitors: the home team two goals ahead;</item>
/// <item>Tied Homie on the home team: the visitors not two goals ahead;</item>
/// <item>else the home team ahead.</item>
/// </list>
/// The game reads a team's relics by type (any relic of the type, <c>TeamData.HaveRelic</c>). Game-independent.
/// </summary>
public static class MatchWinRules
{
    /// <summary>The speedrun squad's id (the game's <c>RunSquadScriptableObject</c> id its winner rule asks for).</summary>
    public const string SpeedrunSquad = "speedrun";

    /// <summary>The game's <c>EndOfGameConditionType.NextGoal</c> as a setup's end conditions hold it.</summary>
    public const int NextGoalCondition = 1;

    /// <summary>The setup key of the match's end conditions (the game's condition numbers, comma-separated).</summary>
    public const string EndConditionsKey = "rules.endConditions";

    /// <summary>
    /// The rule the game applies to a match with <paramref name="facts"/>, whose end conditions hold the next goal or not
    /// (<paramref name="nextGoalEnds"/>). A goal-target rule with a target below 1 isn't one the game's matches set (a
    /// target of 0 would make every match a win): <see cref="WinRule.Unknown"/>, judged as the home team ahead.
    /// </summary>
    public static WinRule Of(bool nextGoalEnds, in WinFacts facts)
    {
        if (nextGoalEnds)
        {
            return WinRule.Ahead;
        }

        if (facts.SpeedrunSquad || facts.HomeSpeedrunCurse)
        {
            return facts.GoalTarget is >= 1 and <= MatchView.MaxNumber ? WinRule.GoalTarget : WinRule.Unknown;
        }

        if (facts.VisitorTiedHomie)
        {
            return WinRule.TwoAhead;
        }

        return facts.HomeTiedHomie ? WinRule.NotTwoBehind : WinRule.Ahead;
    }

    /// <summary>
    /// True when <paramref name="setup"/> (the host's setup this PC loaded) ends its match at the next goal: its end
    /// conditions (<see cref="EndConditionsKey"/>) hold <see cref="NextGoalCondition"/>. False for anything else.
    /// </summary>
    public static bool NextGoalEnds(IReadOnlyList<KeyValuePair<string, NetValue>>? setup)
    {
        if (setup == null || MatchSetupValues.Get(setup, EndConditionsKey) is not { Kind: NetValueKind.Text, Text: { Length: > 0 } list })
        {
            return false;
        }

        foreach (string part in list.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            if (int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out int condition) && condition == NextGoalCondition)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True when the game calls a match that ended <paramref name="home"/>-<paramref name="visitor"/> the home team's win
    /// under <paramref name="rule"/> (<paramref name="goalTarget"/>: the goal target, for <see cref="WinRule.GoalTarget"/>).
    /// </summary>
    public static bool HomeWins(WinRule rule, int home, int visitor, int goalTarget) => rule switch
    {
        WinRule.GoalTarget => home >= goalTarget,
        WinRule.TwoAhead => home - 1 > visitor,
        WinRule.NotTwoBehind => visitor - 1 <= home,
        _ => home > visitor,
    };

    /// <summary><paramref name="rule"/> in words, for the log ("the home team also wins at a tie or one goal behind (it holds Tied Homie)").</summary>
    public static string Words(WinRule rule, int goalTarget) => rule switch
    {
        WinRule.Ahead => "the home team wins ahead",
        WinRule.GoalTarget => string.Create(CultureInfo.InvariantCulture,
            $"the home team wins on reaching the goal target of {goalTarget}, whatever the visitors score (the speedrun squad, or the speedrun curse on its run)"),
        WinRule.TwoAhead => "the home team wins only two goals ahead (the visitors hold Tied Homie)",
        WinRule.NotTwoBehind => "the home team also wins at a tie or one goal behind (its run holds Tied Homie)",
        _ => "the game's rule for its winner wasn't read here (a win is the home team ahead)",
    };

    /// <summary><paramref name="facts"/> for the log (the TRACE line of a rule read): yes or no each, and the goal target.</summary>
    public static string FactsText(bool nextGoalEnds, in WinFacts facts) => string.Create(CultureInfo.InvariantCulture,
        $"ends at the next goal: {YesNo(nextGoalEnds)}; the speedrun squad: {YesNo(facts.SpeedrunSquad)}; on the home team: Tied Homie {YesNo(facts.HomeTiedHomie)}, " +
        $"the speedrun curse {YesNo(facts.HomeSpeedrunCurse)}; Tied Homie on the visitors: {YesNo(facts.VisitorTiedHomie)}; goal target {facts.GoalTarget}");

    private static string YesNo(bool value) => value ? "yes" : "no";
}
