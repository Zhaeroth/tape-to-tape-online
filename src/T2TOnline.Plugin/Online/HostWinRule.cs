#if HAS_INTEROP
using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime.InteropTypes;
using T2TOnline.Core.Serialization;
using T2TOnline.Core.Session;
using T2TOnline.Core.Stream;
using T2TOnline.Plugin.Campaign;
using T2TOnline.Plugin.Campaign.Menus;
using T2TOnline.Plugin.Diagnostics;
using GameManager = Il2Cpp.GameManager;
using MaxGoal = Il2CppState.EndConditions.MaxGoal;
using Relic = Il2CppRogue.Relic;
using RelicList = Il2CppSystem.Collections.Generic.List<Il2CppRogue.Relic>;
using RunData = Il2CppData.RunData;
using SpeedrunCurse = Il2CppRogue.Relics.FinalSquadRelics.SpeedrunCurse;
using Team = Il2CppTape2Tape.Team;
using TeamData = Il2CppData.TeamData;
using TiedHomie = Il2CppRogue.TieDomi;

namespace T2TOnline.Plugin.Online;

/// <summary>
/// Friend: the game's own rule for the winner of the host's campaign match playing here (D135, <see cref="MatchWinRules"/>),
/// from what this PC's game holds, never from a host's word: the relics of its match's two teams as <c>GameManager.WinningTeam</c>
/// reads them (<c>Team._teamData.relics</c>: the home team's are this PC's copy of the host's run, the visitors' its own copy
/// of the opponent), the squad of the run it holds (<c>runData.squadId</c>, what <c>CampaignUtil.IsPlayingSquad</c>
/// compares), its goal target (<c>MaxGoal.goalToScore</c>, which the match's own set-up works out here as on the host,
/// while its scene loads: final before the first read) and the end conditions of the setup it loaded. The game decides
/// the winner as the match ends, so the read that counts is the newest one made while the match is loaded here
/// (<see cref="WinRuleWatch"/>): read as soon as it is loaded, again every <see cref="WinRuleWatch.RereadSeconds"/>
/// seconds, once more on the frame its end shows, and noted then; never read once the match has gone. A match loaded
/// here whose rule was never read gets a WARN as its end shows (its moments then judge a win as the home team ahead, as
/// before). A TRACE line says what was read, the first time and whenever the rule changes; an INFO line the rule noted
/// when it isn't the lead. A read costs a few field reads and two short lists; a settled match a few compares a frame.
/// Called on the game thread; never throws.
/// </summary>
internal static class HostWinRule
{
    private static readonly WinRuleWatch Watch = new();

    /// <summary>
    /// Once a frame while following. <paramref name="match"/>: the host's match noted here (its number on the host; 0: none),
    /// in hosting session <paramref name="session"/>; <paramref name="witness"/>: what this PC saw of it; <paramref name="loaded"/>:
    /// its scene is in this game now (playing, or held on its end); <paramref name="ended"/>: its end shows here;
    /// <paramref name="setup"/>: the host's setup this PC loaded; <paramref name="hostsOwnGame"/>: the one-PC test, whose
    /// friend loads nothing: the match read is this game's own, the host's. True, with <paramref name="ruled"/> to note,
    /// once: as the match's end shows here with a rule read.
    /// </summary>
    public static bool TryRead(ulong session, int match, IMomentWitness witness, bool loaded, bool ended, double now,
        IReadOnlyList<KeyValuePair<string, NetValue>>? setup, bool hostsOwnGame, out MatchView ruled)
    {
        ruled = MatchView.Unknown;
        try
        {
            if (Watch.IsAnother(session, match))
            {
                // Only a campaign match's winner has rules beyond the lead; a rule kept before (a rejoin after this game restarted) stays.
                Watch.Begin(session, match, match > 0 ? witness.ViewOf(match) : MatchView.Unknown);
            }

            if (Watch.ReadDue(loaded, ended, now))
            {
                try
                {
                    Read(match, setup, hostsOwnGame);
                }
                catch (Exception ex)
                {
                    Watch.Missed(ModLog.SafeErrorText(ex)); // what was read before stays; the match's end still closes it below
                }
            }

            switch (Watch.Close(ended, out ruled, out string problem))
            {
                case WinRuleClose.Read:
                    if (ruled.Rule != WinRule.Ahead)
                    {
                        ModLog.Info($"Achievements: the host's match {match}'s winner by the game's own rule here: {MatchWinRules.Words(ruled.Rule, ruled.GoalTarget)}.");
                    }

                    return true;
                case WinRuleClose.Unread:
                    ModLog.Warn($"Achievements: couldn't read the game's rule for the winner of the host's match {match} here ({problem}); " +
                        "its moments judge a win as the home team ahead, as before.");
                    break;
            }

            return false;
        }
        catch (Exception ex)
        {
            // The watch's own steps don't throw; what the record says of the match could (a new match's first frame). The
            // match is left unread, said once: nothing here runs for it again.
            Watch.Begin(session, match, MatchView.Unknown);
            ModLog.Warn($"Achievements: the game's rule for the winner of the host's match {match} isn't read here ({ModLog.SafeErrorText(ex)}); " +
                "its moments judge a win as the home team ahead, as before.");
            ruled = MatchView.Unknown;
            return false;
        }
    }

    /// <summary>One read of the game, given to the watch; what was read is said when it is news (the first read, or a rule or goal target that changed).</summary>
    private static void Read(int match, IReadOnlyList<KeyValuePair<string, NetValue>>? setup, bool hostsOwnGame)
    {
        if (!TryReadFacts(match, setup, out bool nextGoal, out WinFacts facts, out int homeRelics, out int visitorRelics, out string problem))
        {
            Watch.Missed(problem);
            return;
        }

        WinRule rule = MatchWinRules.Of(nextGoal, facts);
        if (rule == WinRule.Unknown)
        {
            // The speedrun rule with a goal target the game's matches never set (0 would make every match a win): not a read.
            Watch.Missed($"the speedrun rule with a goal target of {facts.GoalTarget}, where the game's speedrun matches set 1 or more");
            return;
        }

        if (Watch.Took(MatchView.Unknown with { Rule = rule, GoalTarget = rule == WinRule.GoalTarget ? facts.GoalTarget : 0 }))
        {
            ModLog.Trace($"Achievements: the host's match {match}'s winner by the game's own rule here: {MatchWinRules.Words(rule, facts.GoalTarget)} " +
                $"({MatchWinRules.FactsText(nextGoal, facts)}; {homeRelics} and {visitorRelics} relic(s) on the teams)" +
                $"{(hostsOwnGame ? "; the one-PC test: read from this game's own match, the host's" : string.Empty)}.");
        }
    }

    /// <summary>
    /// What the game's rule reads, as this game holds it now: false with why when it can't be read now (the setup isn't this
    /// match's, the match, a team or the run isn't there).
    /// </summary>
    private static bool TryReadFacts(int match, IReadOnlyList<KeyValuePair<string, NetValue>>? setup, out bool nextGoal, out WinFacts facts,
        out int homeRelics, out int visitorRelics, out string problem)
    {
        nextGoal = false;
        facts = default;
        homeRelics = visitorRelics = 0;
        if (setup == null || MatchSetupValues.HostSerialOf(setup) != match)
        {
            problem = setup == null ? "no setup of the host's is held here" : "the setup held here is another match's";
            return false;
        }

        GameManager? game = GameMenuCapture.Game();
        if (game == null)
        {
            problem = "the match's game manager isn't there";
            return false;
        }

        RelicList? home = Relics(game._homeTeam);
        RelicList? visitors = Relics(game._visitorTeam);
        if (home == null || visitors == null)
        {
            problem = home == null ? "the home team's relics aren't there" : "the visitors' relics aren't there";
            return false;
        }

        RunData? run = CampaignGame.State()?.runData;
        if (run == null)
        {
            problem = "this PC holds no run";
            return false;
        }

        nextGoal = MatchWinRules.NextGoalEnds(setup);
        facts = new WinFacts(
            string.Equals(run.squadId, MatchWinRules.SpeedrunSquad, StringComparison.Ordinal),
            Has<SpeedrunCurse>(home),
            Has<TiedHomie>(home),
            Has<TiedHomie>(visitors),
            MaxGoal.goalToScore);
        homeRelics = home.Count;
        visitorRelics = visitors.Count;
        problem = string.Empty;
        return true;
    }

    /// <summary>A team's relics as the game's rule reads them (<c>Team._teamData.relics</c>), or null when the team or its data isn't there.</summary>
    private static RelicList? Relics(Team? team)
    {
        TeamData? data = team != null ? team._teamData : null;
        return data != null ? data.relics : null;
    }

    /// <summary>True when <paramref name="relics"/> hold a relic of type <typeparamref name="T"/> (the game's <c>TeamData.HaveRelic</c>: any element that is one).</summary>
    private static bool Has<T>(RelicList relics)
        where T : Il2CppObjectBase
    {
        for (int i = 0; i < relics.Count; i++)
        {
            Relic? relic = relics[i];
            if (relic is not null && relic.TryCast<T>() is not null)
            {
                return true;
            }
        }

        return false;
    }
}
#endif
