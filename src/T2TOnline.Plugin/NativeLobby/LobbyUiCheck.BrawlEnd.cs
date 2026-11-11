#if HAS_INTEROP
using System;
using System.Collections.Generic;
using System.Globalization;
using Il2Cpp;
using Il2CppState;
using T2TOnline.Core.Lobby;
using T2TOnline.Plugin.Diagnostics;
using T2TOnline.Plugin.Match;
using T2TOnline.Plugin.Online;
using T2TOnline.Plugin.Streaming;

namespace T2TOnline.Plugin.NativeLobby;

// The brawl key's brawlend=game (Core's CampaignBrawlEndTally; the brawl step reports it). In the fresh offer's match, once the
// friend's press has taken the offer and its fight has begun, the dwell is cut and the match is ended by the tour's usual forced
// win with the fight on, without the tour's own end of its shared state (BrawlEndSharedState): the state a Concede or Quit during
// a fight leaves. Then the mod's guard (Match/BrawlLeftoverGuard) must end it at the match scene's unload: on the map after it,
// the guard's act and both of the game's fight flags are read; in the next match, the skater of every player on the run's
// team (a game player not on its list, such as the PC's idle keyboard, is named and not counted); after every later
// match, the host's brawl lines that said a match ended during a brawl, excused only in a match whose own fight the tour saw
// begin. Nothing here runs without brawlend=game: brawlend=tour (the default) leaves the brawl key as it always was.
internal static partial class LobbyUiCheck
{
    // How long after the forced end the guard may take to act (the match scene's unload comes before the map is in).
    private const double BrawlEndGuardSeconds = 10;

    // How long the next match's game players may take to hold a skater once it is in play.
    private const double BrawlEndSkatersSeconds = 15;

    private static readonly CampaignBrawlEndTally BrawlEndTally = new();

    // This match's fight is to be left on at its forced end; the map after it is still to be read.
    private static bool _brawlLeaveOn;
    private static bool _brawlEndMapDue;
    private static int _brawlEndGuardBefore;
    private static int _brawlCutShortSeen;

    private static bool BrawlEndingByGame => Brawling && _cpArgs.BrawlEndByGame;

    private static void ResetBrawlEnd()
    {
        BrawlEndTally.Reset();
        _brawlLeaveOn = _brawlEndMapDue = false;
        _brawlEndGuardBefore = 0;
        _brawlCutShortSeen = Session?.TourBrawlsCutShort ?? 0;
    }

    /// <summary>
    /// brawlend=game, in place of waiting the fresh offer's fight out: its start, then the match is to be ended at once with the
    /// fight on (the dwell is cut: <see cref="_brawlLeaveOn"/>).
    /// </summary>
    private static IEnumerable<Wait> BrawlLeaveOn(int number)
    {
        if (!BrawlUnderWay())
        {
            BrawlEndTally.NotShown = "the offer's fight never began (no accepted start, no fight state)";
            yield break;
        }

        if (!_brawlBegun)
        {
            yield return Soft(() => { Watch(); return FightState() || !PendingStart(); }, CampaignTour.BrawlStartSeconds, "the brawl to begin");
            if (!FightState())
            {
                BrawlEndTally.NotShown = PendingStart() ? "the accepted offer's fight hadn't begun in time" : "the game took the accepted offer back before its fight began";
                yield break;
            }

            _brawlBegun = true;
            BrawlTally.Brawls++;
        }

        // A moment in, as a player would pause: the fight's start sets its matchmaker and its HUD in the same call as its state.
        yield return Seconds(1);
        _brawlLeaveOn = true;
        BrawlEndTally.Match = number;
        StepNote($"campaign: {CampaignTour.BrawlEndKey}={CampaignTour.BrawlEndByGame}: match {number}: the brawl began; the match is ended now with it on, without the tour's own end (what a Concede or Quit during a fight leaves)");
    }

    /// <summary>
    /// Before the forced end of the match whose fight is left on: the game's state and both flags as they are, noted; the
    /// guard's count kept to compare after the unload. True when the forced end comes now with nothing done to the fight.
    /// </summary>
    private static bool BrawlLeftOnAtForcedEnd()
    {
        if (!_brawlLeaveOn)
        {
            return false;
        }

        _brawlLeaveOn = false;
        bool fighting = FightState();
        BrawlEndTally.MatchmakerOnAtEnd = MatchmakerOn();
        BrawlEndTally.HudUpAtEnd = BrawlHudUp();
        if (!fighting && !BrawlEndTally.MatchmakerOnAtEnd && !BrawlEndTally.HudUpAtEnd)
        {
            BrawlEndTally.NotShown = "the fight had ended before the forced end";
            return true;
        }

        _brawlEndGuardBefore = BrawlLeftoverGuard.Acts;
        _brawlEndMapDue = true;
        BrawlEndTally.TeamAtForcedEnd = TeamListNumbers(out _)?.Count ?? -1; // the next match's line says it beside its own count
        StepNote(string.Create(CultureInfo.InvariantCulture,
            $"campaign: {CampaignTour.BrawlEndKey}={CampaignTour.BrawlEndByGame}: match {BrawlEndTally.Match}'s forced end with its fight on: game state {HudModel.CurrentGameState()?.ToString(CultureInfo.InvariantCulture) ?? "none"} (the fight is {(int)GameState.Fight}), matchmaker {(BrawlEndTally.MatchmakerOnAtEnd ? "on" : "off")}, brawl HUD {(BrawlEndTally.HudUpAtEnd ? "up" : "down")}"));
        return true;
    }

    /// <summary>
    /// On the map after match <paramref name="number"/>: after the match left on, the guard's act (waited for) and both flags;
    /// after a later match, the host's brawl lines that said the match ended during a brawl with no fight of that match seen.
    /// </summary>
    private static IEnumerable<Wait> BrawlEndAfterMatch(int number)
    {
        if (!BrawlEndingByGame)
        {
            yield break;
        }

        if (_brawlEndMapDue)
        {
            _brawlEndMapDue = false;
            yield return Soft(() => { Watch(); return BrawlLeftoverGuard.Acts > _brawlEndGuardBefore; }, BrawlEndGuardSeconds, "the mod's guard after the match's unload");
            BrawlEndTally.GuardActs = BrawlLeftoverGuard.Acts - _brawlEndGuardBefore;
            BrawlEndTally.GuardClean = BrawlLeftoverGuard.LastClean;
            BrawlEndTally.AfterRead = true;
            BrawlEndTally.MatchmakerOnAfter = MatchmakerOn();
            BrawlEndTally.HudUpAfter = BrawlHudUp();
            _brawlCutShortSeen = Sd().TourBrawlsCutShort; // that match's own line ("the match ended during it") is a true one
            StepNote(string.Create(CultureInfo.InvariantCulture,
                $"campaign: {CampaignTour.BrawlEndKey}={CampaignTour.BrawlEndByGame}: on the map after match {number}: the mod's guard acted {BrawlEndTally.GuardActs} time(s)" +
                $"{(BrawlEndTally.GuardActs > 0 ? (BrawlEndTally.GuardClean ? ", cleanly" : ", not cleanly") : string.Empty)}; matchmaker {(BrawlEndTally.MatchmakerOnAfter ? "still on" : "off")}, brawl HUD {(BrawlEndTally.HudUpAfter ? "still up" : "down")}"));
            yield break;
        }

        int cutShort = Sd().TourBrawlsCutShort;
        int added = cutShort - _brawlCutShortSeen;
        _brawlCutShortSeen = cutShort;
        if (BrawlEndTally.Match > 0 && number > BrawlEndTally.Match && !_brawlBegun && added > 0)
        {
            BrawlEndTally.CutShortAfter += added;
            StepNote(string.Create(CultureInfo.InvariantCulture,
                $"campaign: {CampaignTour.BrawlEndKey}={CampaignTour.BrawlEndByGame}: match {number}: {added} brawl line(s) said it ended during a brawl, and no fight of it began"));
        }
    }

    /// <summary>
    /// The brawl step, when the map after the match left on was never read (the run broke or ended there): the guard's acts
    /// since its forced end, read now; the flags stay unread.
    /// </summary>
    private static void BrawlEndLateRead()
    {
        if (!_brawlEndMapDue)
        {
            return;
        }

        _brawlEndMapDue = false;
        BrawlEndTally.GuardActs = BrawlLeftoverGuard.Acts - _brawlEndGuardBefore;
        BrawlEndTally.GuardClean = BrawlLeftoverGuard.LastClean;
    }

    /// <summary>
    /// The match after the one left on, in play: every player of the run's team must hold a skater (the game's matchmaker left
    /// on refuses every one), waited for a moment and noted player by player. Only the team's players play: a game player not
    /// on its list (the PC's idle keyboard, a friend who sat out) holds no skater in any match, so it is named and not counted.
    /// </summary>
    private static IEnumerable<Wait> BrawlEndNextMatch(int number)
    {
        if (!BrawlEndingByGame || BrawlEndTally.Match == 0 || BrawlEndTally.NotShown != null || BrawlEndTally.NextMatch != 0 || number <= BrawlEndTally.Match)
        {
            yield break;
        }

        // The team's list holds for the match (the game seats each match from it at its start): read once for the wait.
        List<int>? team = TeamListNumbers(out _);
        var players = new List<CampaignGamePlayer>(4);
        yield return Soft(() => { Watch(); return GamePlayersOnSkaters(players, out _) && CampaignTour.TeamOnSkaters(players, team); }, BrawlEndSkatersSeconds, "every player of the run's team on a skater");
        bool read = GamePlayersOnSkaters(players, out string? playersUnread);
        team = TeamListNumbers(out string? teamUnread);
        BrawlEndTally.ReadNextMatch(number, read ? players : null, team);
        string line = CampaignTour.BrawlEndPlayersLine(number, read ? players : null, team);
        if (playersUnread != null)
        {
            line += $" (the game players: {playersUnread})";
        }

        if (teamUnread != null)
        {
            line += $" (the team's list: {teamUnread})";
        }

        StepNote(line);
    }

    /// <summary>
    /// This match's game players into <paramref name="players"/> (cleared first): each one's game number and whether it holds a
    /// skater. False when they can't be read (<paramref name="unread"/> says why). Never throws.
    /// </summary>
    private static bool GamePlayersOnSkaters(List<CampaignGamePlayer> players, out string? unread)
    {
        players.Clear();
        unread = null;
        try
        {
            Il2CppSystem.Collections.Generic.List<Il2CppTape2Tape.Player>? list = PlayersManager.Instance?._players;
            for (int i = 0; list != null && i < list.Count; i++)
            {
                Il2CppTape2Tape.Player? player = list[i];
                if (player == null || player.WasCollected)
                {
                    continue;
                }

                bool on = player.controlledSkater is { } skater && !skater.WasCollected && skater != null;
                players.Add(new CampaignGamePlayer(player._PlayerId_k__BackingField, on));
            }

            unread = list == null ? "no players manager or list" : null;
            return list != null;
        }
        catch (Exception ex)
        {
            players.Clear();
            unread = ModLog.SafeErrorText(ex);
            return false;
        }
    }

    /// <summary>
    /// The game numbers of the players on the run's team's list, the list the campaign's matches seat from (the same read as
    /// the late and sides keys' <see cref="OnTeamList"/>), in its order; null when it can't be read (<paramref name="unread"/>
    /// says why). Never throws.
    /// </summary>
    private static List<int>? TeamListNumbers(out string? unread)
    {
        unread = null;
        try
        {
            Il2CppSystem.Collections.Generic.List<Il2CppTape2Tape.Player>? list = TeamSeats.TeamList(null)?.homeTeamPlayers;
            if (list == null)
            {
                unread = "no team selection or list";
                return null;
            }

            var numbers = new List<int>(list.Count);
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i] is { } player && !player.WasCollected)
                {
                    numbers.Add(player._PlayerId_k__BackingField);
                }
            }

            return numbers;
        }
        catch (Exception ex)
        {
            unread = ModLog.SafeErrorText(ex);
            return null;
        }
    }
}
#endif
