#if HAS_INTEROP
using System;
using System.Collections.Generic;
using Il2CppScriptableStates;
using Il2CppTape2Tape.Hockey.UI;
using T2TOnline.Core.Lobby;
using T2TOnline.Core.Stream;
using T2TOnline.Plugin.Campaign.Scenes;
using T2TOnline.Plugin.Diagnostics;
using T2TOnline.Plugin.Match;

namespace T2TOnline.Plugin.Online;

// A seat in the lobby means you play (D145): at each campaign start while hosting, the host's players and every friend's are remembered on
// the team in the game's own memory before the game's own seating runs (Online/TeamSeats.cs); a friend who becomes a game player during the
// run goes onto the game's team list on the map and plays from the next match, never mid-match; every write is put back when the run ends
// and when hosting stops. Core's TeamSeatBook decides; this reads the game and the session, and writes.
internal sealed partial class OnlineSession
{
    private readonly TeamSeatBook _seats = new();
    private readonly List<ulong> _lateSeats = new();
    private TeamSelection? _seatsList;
    private bool _seatsUnreadSaid;

    /// <summary>The start's hook and the session's two calls from it. Once, from the constructor.</summary>
    private void InstallTeamSeats()
    {
        TeamSeats.Install();
        TeamSeats.Starting = SeatAtStart;
        TeamSeats.Gone = ForgetSeat;
    }

    /// <summary>
    /// The game's campaign start is about to seat its players (<c>AssignDefaultControllers</c>): while hosting, who plays is
    /// remembered on the team first, as the game's controller screen would save it. Quick Game never comes here.
    /// </summary>
    private void SeatAtStart(TitleScreen title)
    {
        if (_host == null || _broken)
        {
            return;
        }

        _seats.StartSeen(); // a start came while hosting, planned or not: the run under way isn't one hosting began during
        Dictionary<int, int>? memory = TeamSeats.ReadMemory(out string problem);
        if (memory == null)
        {
            ModLog.Warn($"Lobby: the game's memory of who plays couldn't be read ({problem}); the game seats by its own rule this start.");
            return;
        }

        List<SeatCandidate> players = SeatCandidates();
        bool solo = TeamSeats.IsSoloSquad(out string squadProblem);
        if (squadProblem.Length > 0)
        {
            ModLog.Warn($"Lobby: the run's squad couldn't be read ({squadProblem}); seating as for any squad (on the solo squad the game ignores it).");
        }

        SeatPlan plan = _seats.PlanStart(players, memory, solo);
        string? failed = TeamSeats.ApplyAll(plan.Writes, _seats);
        _seatsList = title.m_TeamSelection;
        _lateSeats.Clear();
        ModLog.Info(plan.Line);
        foreach (string line in plan.NotSeatedLines)
        {
            ModLog.Info(line);
        }

        if (failed != null)
        {
            ModLog.Warn($"Lobby: the game's memory of who plays couldn't be written ({failed}); the game seats by its own rule whoever wasn't written.");
        }
    }

    /// <summary>
    /// The host's game players now: the host's own (no friend's controller) in the game's order, then friends in the lobby's seat order,
    /// then any friend whose seat is kept while they're away. Players the game doesn't have are left out.
    /// </summary>
    private List<SeatCandidate> SeatCandidates()
    {
        var candidates = new List<SeatCandidate>();
        Il2Cpp.PlayersManager? manager = Il2Cpp.PlayersManager.Instance;
        Il2CppSystem.Collections.Generic.List<Il2CppTape2Tape.Player>? players = manager?._players;
        if (manager == null || players == null)
        {
            return candidates;
        }

        var friends = new Dictionary<IntPtr, (int Order, string Tag)>();
        int order = 0;
        foreach (ulong boardId in _board?.FriendsInSeatOrder() ?? (IReadOnlyList<ulong>)Array.Empty<ulong>())
        {
            ulong steamId = FromBoardId(boardId);
            if (_pads.TryGetValue(steamId, out RemotePad? pad) || (_parked.TryGetValue(steamId, out (RemotePad Pad, TimeSpan Until) parked) && (pad = parked.Pad) != null))
            {
                NoteFriendPlayer(manager, pad, order++, friends);
            }
        }

        foreach (RemotePad pad in _pads.Values)
        {
            NoteFriendPlayer(manager, pad, order++, friends);
        }

        foreach ((RemotePad pad, _) in _parked.Values)
        {
            NoteFriendPlayer(manager, pad, order++, friends);
        }

        var friendSeats = new List<(int Order, SeatCandidate Candidate)>();
        for (int i = 0; i < players.Count; i++)
        {
            Il2CppTape2Tape.Player? player = players[i];
            if (player == null || player.WasCollected)
            {
                continue;
            }

            int id = player._PlayerId_k__BackingField;
            if (friends.TryGetValue(player.Pointer, out (int Order, string Tag) friend))
            {
                friendSeats.Add((friend.Order, new SeatCandidate(id, Friend: true, TeamSeats.IsPad(player), friend.Tag)));
            }
            else
            {
                candidates.Add(new SeatCandidate(id, Friend: false, TeamSeats.IsPad(player), "the host"));
            }
        }

        friendSeats.Sort((a, b) => a.Order.CompareTo(b.Order));
        foreach ((_, SeatCandidate candidate) in friendSeats)
        {
            candidates.Add(candidate);
        }

        return candidates;
    }

    /// <summary>A friend's game player, by the game's own lookup from their controller, kept with its first (lowest) order.</summary>
    private static void NoteFriendPlayer(Il2Cpp.PlayersManager manager, RemotePad pad, int order, Dictionary<IntPtr, (int Order, string Tag)> friends)
    {
        Il2CppTape2Tape.Player? player = manager.GetPlayerFromRewired(pad.Pad.Player);
        if (player != null && !player.WasCollected && !friends.ContainsKey(player.Pointer))
        {
            friends[player.Pointer] = (order, pad.Tag);
        }
    }

    /// <summary>
    /// A friend's controller became a game player (<see cref="RelayJoins"/>). During a seated run they wait for the run's map, then go onto
    /// the team's list: they play from the next match, and watch the rest of a match they joined in.
    /// </summary>
    private void NoteLateJoiner(RemotePad pad, int gamePlayer)
    {
        if (_host == null || _lateSeats.Contains(pad.SteamId))
        {
            return;
        }

        if (!_seats.RunOpen)
        {
            // Hosting begun during a campaign run (the developer's keys) has no seated run: said once, and nothing is changed.
            if (TeamSeats.Installed && _seats.UnseatedRunLine(new SeatCandidate(gamePlayer, Friend: true, Gamepad: false, pad.Tag), InCampaignRun()) is { } line)
            {
                ModLog.Info(line);
            }

            return;
        }

        _lateSeats.Add(pad.SteamId);
        bool inMatch = !MatchSetup.CurrentMatch(out _).IsNone;
        ModLog.Info($"Lobby: {pad.Tag} (game player {gamePlayer}) joined during the run: on the team from the next match{(inMatch ? " (they watch the rest of this one)" : string.Empty)}.");
    }

    /// <summary>
    /// True when the host's game is in a campaign run now: on the run's world map, or in a match the game loaded as a campaign's
    /// (its game type as the match loaded: the match's setup itself is read only while hosting, and this match loaded before).
    /// Read only when a friend becomes a game player with no seated run open (a scene name is a new string each read).
    /// </summary>
    private static bool InCampaignRun() =>
        WorldMap.MapLoaded || (!MatchSetup.CurrentMatch(out _).IsNone && MatchSetup.LoadedGameType == CampaignMatchValues.CampaignGameType);

    /// <summary>Every frame while hosting: late joiners onto the team's list once the host is on the run's map (no match loaded). Nothing to do costs a count.</summary>
    private void TickTeamSeats()
    {
        if (_lateSeats.Count == 0)
        {
            return;
        }

        if (!_seats.RunOpen || _host == null)
        {
            _lateSeats.Clear();
            return;
        }

        // The match first: the map's test reads the active scene's name (a new string each call), which a frame in a match never needs.
        if (MatchSetup.Loading || !MatchSetup.CurrentMatch(out _).IsNone || !WorldMap.MapLoaded)
        {
            return;
        }

        foreach (ulong steamId in _lateSeats)
        {
            try
            {
                SeatLateJoiner(steamId);
            }
            catch (Exception ex)
            {
                ModLog.Error($"Lobby: putting {Tag(steamId)} on the run's team failed; the game's controller screen can still put them there", ex);
            }
        }

        _lateSeats.Clear();
    }

    /// <summary>
    /// One late joiner onto the team's list, as the game's controller screen's save fills it on the map, and remembered on the team. A late joiner who
    /// dropped before the map, their seat kept, goes on too, as a kept seat does at a start: the AI plays for them until they're back, and a return
    /// within the kept seat is the same game player, which no later join would note.
    /// </summary>
    private void SeatLateJoiner(ulong steamId)
    {
        Il2Cpp.PlayersManager? manager = Il2Cpp.PlayersManager.Instance;
        RemotePad? pad = _pads.TryGetValue(steamId, out RemotePad? here) ? here
            : _parked.TryGetValue(steamId, out (RemotePad Pad, TimeSpan Until) kept) ? kept.Pad
            : null;
        if (pad == null || manager == null || manager.GetPlayerFromRewired(pad.Pad.Player) is not { } player || player.WasCollected)
        {
            ModLog.Info($"Lobby: {Tag(steamId)} is no game player any more; nobody is added to the run's team.");
            return;
        }

        TeamSelection? teams = TeamSeats.TeamList(_seatsList);
        Il2CppSystem.Collections.Generic.List<Il2CppTape2Tape.Player>? list = teams?.homeTeamPlayers;
        Dictionary<int, int>? memory = TeamSeats.ReadMemory(out string problem);
        if (teams == null || list == null || memory == null)
        {
            ModLog.Warn($"Lobby: {pad.Tag} couldn't be put on the run's team ({(memory == null ? problem : "the game's team list isn't there")}); the game's controller screen can put them there.");
            return;
        }

        int onTeam = 0;
        bool already = false;
        for (int i = 0; i < list.Count; i++)
        {
            if (list[i] is { } member && !member.WasCollected)
            {
                onTeam++;
                already |= member.Pointer == player.Pointer;
            }
        }

        var candidate = new SeatCandidate(player._PlayerId_k__BackingField, Friend: true, TeamSeats.IsPad(player), pad.Tag);
        LateSeatPlan plan = _seats.PlanLateJoin(candidate, memory, onTeam, already);
        if (plan.Outcome == LateSeat.Add)
        {
            if (plan.Write is SideWrite write && TeamSeats.ApplyAll(new[] { write }, _seats) is { } failed)
            {
                ModLog.Warn($"Lobby: the game's memory of who plays couldn't be written ({failed}); {pad.Tag} is on the team's list all the same.");
            }

            list.Add(player);
        }

        ModLog.Info(plan.Line);
    }

    /// <summary>A friend's game player went for good (<see cref="RemoveForGood"/>): its id's side goes back to what it was before the run.</summary>
    private void ForgetSeat(int gamePlayer, string tag)
    {
        if (gamePlayer < 0)
        {
            return;
        }

        Dictionary<int, int>? memory = TeamSeats.ReadMemory(out _);
        if (memory != null && _seats.Forget(gamePlayer, memory) is SideWrite write)
        {
            string? failed = TeamSeats.ApplyAll(new[] { write }, null);
            ModLog.Info(failed == null
                ? $"Lobby: game player {gamePlayer}'s remembered side is put back as before the run ({tag}'s game player went; the game reuses its id)."
                : $"Lobby: game player {gamePlayer}'s remembered side couldn't be put back ({failed}).");
        }
    }

    /// <summary>The host is back at the lobby: the controller screen's changes this run kept as choices, and the game's memory put back as before the run's start.</summary>
    private void EndSeatedRun(string when)
    {
        _lateSeats.Clear();
        if (!_seats.RunOpen)
        {
            return;
        }

        Dictionary<int, int>? memory = TeamSeats.ReadMemory(out string problem);
        if (memory == null)
        {
            SayMemoryUnread(problem);
            return;
        }

        (IReadOnlyList<SideWrite> putBack, IReadOnlyList<int> chosen) = _seats.EndRun(memory);
        if (_seats.ChoicesLine(chosen) is { } choices)
        {
            ModLog.Info(choices);
        }

        SayPutBack(putBack, when);
    }

    /// <summary>Hosting stopped (<see cref="LeaveAll"/>): the game's memory put back as before the open run's start, every choice forgotten.</summary>
    private void EndTeamSeats(string when)
    {
        _lateSeats.Clear();
        _seatsList = null;
        bool open = _seats.RunOpen;
        Dictionary<int, int>? memory = TeamSeats.ReadMemory(out string problem);
        if (memory == null)
        {
            if (open)
            {
                SayMemoryUnread(problem);
            }

            _seats.EndSession(new Dictionary<int, int>());
            return;
        }

        IReadOnlyList<SideWrite> putBack = _seats.EndSession(memory);
        if (open)
        {
            SayPutBack(putBack, when);
        }
    }

    private static void SayPutBack(IReadOnlyList<SideWrite> putBack, string when)
    {
        string? failed = TeamSeats.ApplyAll(putBack, null);
        if (failed == null)
        {
            ModLog.Info(TeamSeatBook.PutBackLine(putBack.Count, when));
        }
        else
        {
            ModLog.Warn($"Lobby: the game's remembered sides couldn't all be put back ({failed}); they last until the game closes.");
        }
    }

    private void SayMemoryUnread(string problem)
    {
        if (!_seatsUnreadSaid)
        {
            _seatsUnreadSaid = true;
            ModLog.Warn($"Lobby: the game's memory of who plays couldn't be read to put it back ({problem}); it lasts until the game closes.");
        }
    }
}
#endif
