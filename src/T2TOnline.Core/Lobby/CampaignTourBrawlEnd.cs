using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace T2TOnline.Core.Lobby;

/// <summary>One game player of a match as read: the game's number for it and whether it holds a skater.</summary>
/// <param name="Number">The game's number for the player (its player id).</param>
/// <param name="OnSkater">It holds a skater.</param>
public readonly record struct CampaignGamePlayer(int Number, bool OnSkater);

/// <summary>
/// What <c>brawlend=game</c> saw: the fresh offer's match ended by the forced win with its fight on and without the tour's own
/// end of its state (what a Concede or Quit during a fight leaves), then the mod's guard at the match's unload, the game's two
/// fight flags on the map after it, the skaters of the run's team's players in the next match, and the host's brawl lines
/// after it. Only the players on the run's team play: a game player not on its list (the PC's idle keyboard, a friend who sat
/// out) holds no skater in any match and is named, not counted.
/// </summary>
public sealed class CampaignBrawlEndTally
{
    /// <summary>The match ended with its fight on (0 while none was).</summary>
    public int Match { get; set; }

    /// <summary>Why it couldn't be shown (no fight began in the fresh offer's match, or it ended first), or null.</summary>
    public string? NotShown { get; set; }

    /// <summary>The fight matchmaker was on at the forced end.</summary>
    public bool MatchmakerOnAtEnd { get; set; }

    /// <summary>The brawl HUD's flag was on at the forced end.</summary>
    public bool HudUpAtEnd { get; set; }

    /// <summary>Times the mod's guard acted from the forced end to the map.</summary>
    public int GuardActs { get; set; }

    /// <summary>Its last act was clean (both flags off afterwards, no call failed).</summary>
    public bool GuardClean { get; set; }

    /// <summary>The two flags were read on the map after the match.</summary>
    public bool AfterRead { get; set; }

    /// <summary>The matchmaker read on the map after the match.</summary>
    public bool MatchmakerOnAfter { get; set; }

    /// <summary>The brawl HUD's flag read on the map after the match.</summary>
    public bool HudUpAfter { get; set; }

    /// <summary>The next match, whose players were read (0 when it wasn't reached).</summary>
    public int NextMatch { get; set; }

    /// <summary>The run's team's list was read in the next match.</summary>
    public bool TeamRead { get; set; }

    /// <summary>The run's team's players in the next match (the game players on its list).</summary>
    public int TeamPlayers { get; set; }

    /// <summary>Of those, the ones with no skater once it was in play (one not among the match's game players included).</summary>
    public int TeamWithoutSkater { get; set; }

    /// <summary>The next match's game players not on the run's team: they don't play, so they aren't counted.</summary>
    public List<int> NotOnTeam { get; } = new();

    /// <summary>The players on the run's team's list in the fresh offer's match, at its forced end; -1 when it wasn't read.</summary>
    public int TeamAtForcedEnd { get; set; } = -1;

    /// <summary>The host's brawl lines that said a later match ended during a brawl, in a match where no fight of its own began.</summary>
    public int CutShortAfter { get; set; }

    /// <summary>Back to nothing seen.</summary>
    public void Reset()
    {
        Match = NextMatch = TeamPlayers = TeamWithoutSkater = GuardActs = CutShortAfter = 0;
        TeamAtForcedEnd = -1;
        NotOnTeam.Clear();
        NotShown = null;
        MatchmakerOnAtEnd = HudUpAtEnd = GuardClean = AfterRead = MatchmakerOnAfter = HudUpAfter = TeamRead = false;
    }

    /// <summary>
    /// Match <paramref name="match"/>'s read, once it was in play: its game players (<paramref name="players"/>, null when
    /// unread) against the run's team's list (<paramref name="team"/>, the game numbers on it; null when unread). Each team
    /// player counts, with no skater when it holds none or isn't among the match's game players; a game player not on the list
    /// is kept by number and not counted.
    /// </summary>
    public void ReadNextMatch(int match, IReadOnlyList<CampaignGamePlayer>? players, IReadOnlyList<int>? team)
    {
        NextMatch = match;
        NotOnTeam.Clear();
        TeamRead = team != null;
        TeamPlayers = TeamWithoutSkater = 0;
        if (team == null)
        {
            return;
        }

        for (int i = 0; i < team.Count; i++)
        {
            if (IndexOf(team, team[i]) != i)
            {
                continue; // listed twice: one player
            }

            TeamPlayers++;
            TeamWithoutSkater += CampaignTour.OnSkater(players, team[i]) ? 0 : 1;
        }

        for (int i = 0; players != null && i < players.Count; i++)
        {
            if (IndexOf(team, players[i].Number) < 0 && !NotOnTeam.Contains(players[i].Number))
            {
                NotOnTeam.Add(players[i].Number);
            }
        }
    }

    /// <summary>Where <paramref name="value"/> first stands in <paramref name="list"/>, or -1.</summary>
    internal static int IndexOf(IReadOnlyList<int> list, int value)
    {
        for (int i = 0; i < list.Count; i++)
        {
            if (list[i] == value)
            {
                return i;
            }
        }

        return -1;
    }
}

/// <summary>The brawl key's <c>brawlend=game</c>: its key, its rule with <c>brawl=on</c>, its line and its verdict.</summary>
public static partial class CampaignTour
{
    /// <summary>
    /// The key that chooses how the fresh offer's fight ends (<c>brawlend=game</c> or <c>brawlend=tour</c>, the default). It adds
    /// no step: <see cref="BrawlStep"/> reports it.
    /// </summary>
    public const string BrawlEndKey = "brawlend";

    /// <summary><c>brawlend=game</c>: the match is ended with its fight on; the mod's guard ends what it leaves at the match's unload.</summary>
    public const string BrawlEndByGame = "game";

    /// <summary><c>brawlend=tour</c>: the tour waits the fight out, as without the key.</summary>
    public const string BrawlEndByTour = "tour";

    /// <summary>
    /// Why <paramref name="arguments"/>' <c>brawlend</c> can't run, or null: it needs <c>brawl=on</c>, whose fresh offer begins
    /// the fight it ends.
    /// </summary>
    public static string? BrawlEndKeyProblem(IReadOnlyList<KeyValuePair<string, string>> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        string? brawlEnd = null;
        bool brawl = false;
        foreach (KeyValuePair<string, string> argument in arguments)
        {
            if (string.Equals(argument.Key, BrawlEndKey, StringComparison.Ordinal))
            {
                brawlEnd = argument.Value;
            }
            else if (string.Equals(argument.Key, BrawlKey, StringComparison.Ordinal))
            {
                brawl = string.Equals(argument.Value, "on", StringComparison.Ordinal);
            }
        }

        return brawlEnd == null || brawl ? null
            : $"{BrawlEndKey}={brawlEnd} needs {BrawlKey}=on: the fight it ends is the one the brawl key's fresh offer begins";
    }

    /// <summary>
    /// The <c>brawlend=game</c> line, written by the brawl step: <c>campaign: brawlend=game: match 1 ended by the forced win with its
    /// fight on (matchmaker on, brawl HUD up), without the tour's own end; at its unload the mod's guard acted 1 time(s), cleanly; on
    /// the map after it: matchmaker off, brawl HUD down; in match 2, 2 of 2 of the team's player(s) on a skater (the team's list
    /// held 2 in match 1; game player 0 isn't on the team); no brawl line after it said a match ended during a brawl it didn't
    /// have</c>.
    /// </summary>
    public static string BrawlEndLine(CampaignBrawlEndTally tally)
    {
        ArgumentNullException.ThrowIfNull(tally);
        string head = $"campaign: {BrawlEndKey}={BrawlEndByGame}: ";
        if (tally.NotShown != null || tally.Match == 0)
        {
            return head + $"not shown ({NotShownReason(tally)}): the mod's guard wasn't tested";
        }

        string guard = tally.GuardActs == 0 ? "the mod's guard didn't act"
            : string.Create(CultureInfo.InvariantCulture, $"the mod's guard acted {tally.GuardActs} time(s), {(tally.GuardClean ? "cleanly" : "not cleanly (its WARN line says what stayed on)")}");
        string after = tally.AfterRead
            ? $"on the map after it: matchmaker {(tally.MatchmakerOnAfter ? "still on" : "off")}, brawl HUD {(tally.HudUpAfter ? "still up" : "down")}"
            : "the flags weren't read on the map after it";
        string next = tally.NextMatch == 0 ? "the next match wasn't reached" : NextMatchText(tally);
        string lines = tally.CutShortAfter == 0 ? "no brawl line after it said a match ended during a brawl it didn't have"
            : string.Create(CultureInfo.InvariantCulture, $"{tally.CutShortAfter} brawl line(s) after it said a match ended during a brawl it didn't have");
        return string.Create(CultureInfo.InvariantCulture,
            $"{head}match {tally.Match} ended by the forced win with its fight on (matchmaker {(tally.MatchmakerOnAtEnd ? "on" : "off")}, brawl HUD {(tally.HudUpAtEnd ? "up" : "down")}), without the tour's own end; " +
            $"at its unload {guard}; {after}; {next}; {lines}");
    }

    /// <summary>
    /// What's wrong with what <c>brawlend=game</c> saw, or null. The key asks for the guard's proof, so a run that doesn't give
    /// it fails too: no fight left on at the forced end (the offer declined, its fight never begun, or over first), the flags
    /// not read on the map after it, or the next match not reached. A failure besides: the guard didn't act, or not cleanly;
    /// a flag still on on the map; a player of the run's team with no skater in the next match (or none of them read: the
    /// team's list unread or empty); a later brawl line that only a left-over flag makes. A game player not on the team doesn't
    /// play and isn't counted.
    /// </summary>
    public static string? BrawlEndProblem(CampaignBrawlEndTally tally)
    {
        ArgumentNullException.ThrowIfNull(tally);
        if (tally.NotShown != null || tally.Match == 0)
        {
            return $"{BrawlEndKey}={BrawlEndByGame}: not shown ({NotShownReason(tally)}): the mod's guard wasn't tested; run the tour again (another seed)";
        }

        string match = tally.Match.ToString(CultureInfo.InvariantCulture);
        if (tally.GuardActs == 0)
        {
            return $"{BrawlEndKey}={BrawlEndByGame}: the mod's guard didn't act after match {match} ended with its fight on";
        }

        if (!tally.GuardClean)
        {
            return $"{BrawlEndKey}={BrawlEndByGame}: the mod's guard didn't end all of the fight state match {match} left on";
        }

        if (!tally.AfterRead)
        {
            return $"{BrawlEndKey}={BrawlEndByGame}: the game's fight flags weren't read on the map after match {match} (the run didn't reach it)";
        }

        if (tally.MatchmakerOnAfter || tally.HudUpAfter)
        {
            return $"{BrawlEndKey}={BrawlEndByGame}: the game's fight state was still on on the map after match {match}";
        }

        if (tally.NextMatch == 0)
        {
            return $"{BrawlEndKey}={BrawlEndByGame}: the match after match {match} wasn't reached, so no player's skater was read after it";
        }

        if (!tally.TeamRead || tally.TeamPlayers == 0)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{BrawlEndKey}={BrawlEndByGame}: in match {tally.NextMatch}, none of the run's team's players read ({NoTeamReason(tally)})");
        }

        if (tally.TeamWithoutSkater > 0)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{BrawlEndKey}={BrawlEndByGame}: in match {tally.NextMatch}, {tally.TeamWithoutSkater} of {tally.TeamPlayers} of the team's player(s) had no skater");
        }

        return tally.CutShortAfter > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{BrawlEndKey}={BrawlEndByGame}: {tally.CutShortAfter} brawl line(s) after match {match} said a match ended during a brawl it didn't have (a flag left on)")
            : null;
    }

    /// <summary>
    /// True when game player <paramref name="number"/> is among <paramref name="players"/> and holds a skater; false when it holds
    /// none, isn't among them, or they weren't read.
    /// </summary>
    public static bool OnSkater(IReadOnlyList<CampaignGamePlayer>? players, int number)
    {
        for (int i = 0; players != null && i < players.Count; i++)
        {
            if (players[i].Number == number && players[i].OnSkater)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True when the run's team (<paramref name="team"/>) has at least one player and every one of them holds a skater among
    /// <paramref name="players"/>: what the next match's wait is for. False when either wasn't read.
    /// </summary>
    public static bool TeamOnSkaters(IReadOnlyList<CampaignGamePlayer>? players, IReadOnlyList<int>? team)
    {
        if (players == null || team == null || team.Count == 0)
        {
            return false;
        }

        for (int i = 0; i < team.Count; i++)
        {
            if (!OnSkater(players, team[i]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The next match's line, player by player, written once it is in play: <c>campaign: brawlend=game: match 2 in play: game
    /// player 0 with no skater (not on the team), game player 1 on a skater, game player 2 on a skater; the run's team: game
    /// player(s) 1, 2</c>. A team player not among the match's game players is named as such; an unread list is said.
    /// </summary>
    public static string BrawlEndPlayersLine(int match, IReadOnlyList<CampaignGamePlayer>? players, IReadOnlyList<int>? team)
    {
        var line = new StringBuilder(string.Create(CultureInfo.InvariantCulture, $"campaign: {BrawlEndKey}={BrawlEndByGame}: match {match} in play: "));
        if (players == null || players.Count == 0)
        {
            line.Append(players == null ? "the game players unread" : "no game player read");
        }

        for (int i = 0; players != null && i < players.Count; i++)
        {
            CampaignGamePlayer player = players[i];
            line.Append(i == 0 ? string.Empty : ", ")
                .Append(CultureInfo.InvariantCulture, $"game player {player.Number} {(player.OnSkater ? "on a skater" : "with no skater")}")
                .Append(team != null && !Contains(team, player.Number) ? " (not on the team)" : string.Empty);
        }

        if (team == null)
        {
            return line.Append("; the run's team's list unread").ToString();
        }

        line.Append("; the run's team: ").Append(team.Count == 0 ? "no player" : "game player(s) ");
        for (int i = 0; i < team.Count; i++)
        {
            line.Append(i == 0 ? string.Empty : ", ").Append(team[i].ToString(CultureInfo.InvariantCulture));
            bool inMatch = false;
            for (int j = 0; players != null && j < players.Count; j++)
            {
                inMatch |= players[j].Number == team[i];
            }

            line.Append(inMatch ? string.Empty : " (not in the match)");
        }

        return line.ToString();
    }

    // The next match's part of the line: the team's players on a skater, then the team's count in the fresh offer's match and
    // the game players not on the team, when there are any.
    private static string NextMatchText(CampaignBrawlEndTally tally)
    {
        if (!tally.TeamRead || tally.TeamPlayers == 0)
        {
            return string.Create(CultureInfo.InvariantCulture, $"in match {tally.NextMatch}, none of the run's team's players read ({NoTeamReason(tally)})");
        }

        var text = new StringBuilder(string.Create(CultureInfo.InvariantCulture,
            $"in match {tally.NextMatch}, {tally.TeamPlayers - tally.TeamWithoutSkater} of {tally.TeamPlayers} of the team's player(s) on a skater"));
        var notes = new List<string>(2);
        if (tally.TeamAtForcedEnd >= 0)
        {
            notes.Add(string.Create(CultureInfo.InvariantCulture, $"the team's list held {tally.TeamAtForcedEnd} in match {tally.Match}"));
        }

        if (tally.NotOnTeam.Count > 0)
        {
            notes.Add(tally.NotOnTeam.Count == 1
                ? string.Create(CultureInfo.InvariantCulture, $"game player {tally.NotOnTeam[0]} isn't on the team")
                : $"game players {string.Join(", ", tally.NotOnTeam.ConvertAll(n => n.ToString(CultureInfo.InvariantCulture)))} aren't on the team");
        }

        return notes.Count == 0 ? text.ToString() : text.Append(" (").Append(string.Join("; ", notes)).Append(')').ToString();
    }

    // Why no team player was read: its list unread, or empty.
    private static string NoTeamReason(CampaignBrawlEndTally tally) => tally.TeamRead ? "the team's list was empty" : "the team's list couldn't be read";

    private static bool Contains(IReadOnlyList<int> list, int value) => CampaignBrawlEndTally.IndexOf(list, value) >= 0;

    // Why no fight was left on at the forced end: what the tour saw, or no fight begun in the fresh offer's match.
    private static string NotShownReason(CampaignBrawlEndTally tally) => tally.NotShown ?? "no fight began in the fresh offer's match";
}
