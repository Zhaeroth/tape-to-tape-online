using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace T2TOnline.Core.Lobby;

/// <summary>
/// The sides the game's controller screen remembers for a game player (its <c>ControllerAssignmentPlayer.ESide</c>, kept in
/// <c>ControllerAssignmentState</c> for the game's life), and the campaign team's size.
/// </summary>
public static class TeamSides
{
    /// <summary>The left side (a versus screen's; the campaign's screen has none).</summary>
    public const int Left = 0;

    /// <summary>The middle: not playing.</summary>
    public const int Center = 1;

    /// <summary>The right side: the campaign's one team. A campaign's start seats every player remembered here.</summary>
    public const int Right = 2;

    /// <summary>How many players the game's controller screen takes on a team (its prefabs' <c>m_MaxPlayersPerTeam</c>).</summary>
    public const int TeamCap = 6;

    /// <summary>The highest game player id read from the game's memory (the game hands out 0 to 10).</summary>
    public const int MaxGamePlayer = 15;

    /// <summary>A side in the log's words.</summary>
    public static string Text(int side) => side switch
    {
        Right => "on the team",
        Center => "in the middle",
        Left => "on the left",
        _ => string.Create(CultureInfo.InvariantCulture, $"on side {side}"),
    };
}

/// <summary>One game player on the host's game at a campaign's start.</summary>
/// <param name="GamePlayer">The game player id (<c>Player.PlayerId</c>).</param>
/// <param name="Friend">True for a friend's virtual controller (a dropped friend's kept one too); false for the host's own players.</param>
/// <param name="Gamepad">True when the game reads the player as a pad player (its own <c>IsUsingGamepad</c>).</param>
/// <param name="Who">How the log names a friend: their tag (never a name). Unused for the host's players.</param>
public readonly record struct SeatCandidate(int GamePlayer, bool Friend, bool Gamepad, string Who);

/// <summary>What a campaign's start does with one player.</summary>
public enum SeatOutcome
{
    /// <summary>Remembered on the team: the game seats them.</summary>
    OnTeam,

    /// <summary>Not on the team: the game's controller screen put them in the middle earlier this session.</summary>
    SitsOut,

    /// <summary>Not on the team: the team is full (<see cref="TeamSides.TeamCap"/>).</summary>
    NoRoom,

    /// <summary>One of the host's own players the game doesn't seat (not remembered on the team, not the host's default): left as it is.</summary>
    LeftAsIs,
}

/// <summary>One player's outcome at a start.</summary>
/// <param name="Player">The player.</param>
/// <param name="Outcome">What the start does with them.</param>
/// <param name="Name">How the log names them.</param>
public readonly record struct SeatDecision(SeatCandidate Player, SeatOutcome Outcome, string Name);

/// <summary>A change to the game's memory of sides: <paramref name="Side"/> remembered for <paramref name="GamePlayer"/>, or (null) the id forgotten.</summary>
/// <param name="GamePlayer">The game player id.</param>
/// <param name="Side">The side, or null to remove the id from the memory.</param>
public readonly record struct SideWrite(int GamePlayer, int? Side);

/// <summary>What a campaign's start writes, and what the log says of it.</summary>
public sealed class SeatPlan
{
    internal SeatPlan(bool soloSquad, IReadOnlyList<SeatDecision> decisions, IReadOnlyList<SideWrite> writes)
    {
        SoloSquad = soloSquad;
        Decisions = decisions;
        Writes = writes;
    }

    /// <summary>The run plays the solo squad: nothing is written, the game seats its one player by its own rule.</summary>
    public bool SoloSquad { get; }

    /// <summary>Every player's outcome, in the order they were given.</summary>
    public IReadOnlyList<SeatDecision> Decisions { get; }

    /// <summary>The writes to make, before the game's own seating runs.</summary>
    public IReadOnlyList<SideWrite> Writes { get; }

    /// <summary>The start's line: who is on the team.</summary>
    public string Line
    {
        get
        {
            if (SoloSquad)
            {
                return "Lobby: the solo squad: the game puts one player on the team by its own rule (its first pad player, else its keyboard player); nothing is remembered for the others.";
            }

            List<string> on = Decisions.Where(d => d.Outcome == SeatOutcome.OnTeam).Select(Named).ToList();
            return on.Count == 0
                ? "Lobby: nobody is remembered on the team for this run; the game seats by its own rule."
                : $"Lobby: on the team for this run: {string.Join(", ", on)}; the game seats them at its start.";
        }
    }

    /// <summary>
    /// A line for each player kept off the team, with why: a friend in the middle by the controller screen's choice, anyone
    /// past the team's size. (The host's own players the screen left in the middle are told by <see cref="Line"/>'s list.)
    /// </summary>
    public IEnumerable<string> NotSeatedLines => Decisions
        .Where(d => d.Outcome == SeatOutcome.NoRoom || (d.Outcome == SeatOutcome.SitsOut && d.Player.Friend))
        .Select(d => $"Lobby: not on the team for this run: {Named(d)}: {(d.Outcome == SeatOutcome.NoRoom ? NoRoomText : SitsOutText)}.");

    internal static string NoRoomText => string.Create(CultureInfo.InvariantCulture, $"no room: the game's team takes {TeamSides.TeamCap}");

    internal const string SitsOutText = "they were put in the middle on the game's controller screen this session";

    private static string Named(SeatDecision d) => string.Create(CultureInfo.InvariantCulture, $"{d.Name} (game player {d.Player.GamePlayer})");
}

/// <summary>What happens to a friend who became a game player during a run.</summary>
public enum LateSeat
{
    /// <summary>Added to the team from the next match.</summary>
    Add,

    /// <summary>Already on the team's list (nothing to do).</summary>
    AlreadyOnTeam,

    /// <summary>No seated run: the next start seats them.</summary>
    NoRun,

    /// <summary>The solo squad: the game's one player only.</summary>
    SoloSquad,

    /// <summary>The team is full.</summary>
    NoRoom,

    /// <summary>The game's controller screen put them in the middle this session.</summary>
    SitsOut,
}

/// <summary>A late joiner's outcome.</summary>
/// <param name="Outcome">What happens.</param>
/// <param name="Write">The memory write to make with an add, or null.</param>
/// <param name="Line">The log's line.</param>
public readonly record struct LateSeatPlan(LateSeat Outcome, SideWrite? Write, string Line);

/// <summary>
/// The host's seating book for one hosting session (D145): who goes on a campaign's team at each start, what the mod wrote
/// to the game's memory of sides, the controller screen's own choices this session, and what puts the memory back.
/// </summary>
/// <remarks>
/// A campaign's start seats every player whose remembered side is the right one (the team), else the first pad player
/// alone. At each start the book remembers on the team the host's players (those the game already remembers there, else
/// the host's first pad player, else its keyboard player: the game's own default, among the host's own players only) and
/// every friend, in the order given, at most <see cref="TeamSides.TeamCap"/>. A side the game's controller screen saved
/// during a seated run (anything that differs, at the run's end, from what the book wrote or found) is that player's
/// choice for the rest of the session, unless the run played the solo squad (its screen takes one player). The memory is put back as it was before each run's start when the run ends, and
/// when the session ends; a friend gone for good has their id put back at once (ids are reused).
/// </remarks>
public sealed class TeamSeatBook
{
    private readonly Dictionary<int, int> _before = new();
    private readonly Dictionary<int, int> _ours = new();
    private readonly Dictionary<int, int> _choices = new();
    private bool _runOpen;
    private bool _soloRun;
    private bool _startSeen;
    private bool _unseatedRunSaid;

    /// <summary>True from a start's plan until its run ends (<see cref="EndRun"/>) or the session does.</summary>
    public bool RunOpen => _runOpen;

    /// <summary>The controller screen's choices this session, by game player id.</summary>
    public IReadOnlyDictionary<int, int> Choices => _choices;

    /// <summary>
    /// A campaign start came while hosting (the game's own seating is about to run), whether or not it could be planned: from
    /// here on, a run under way with no seated run open isn't one that began before hosting (<see cref="UnseatedRunLine"/>).
    /// </summary>
    public void StartSeen() => _startSeen = true;

    /// <summary>
    /// A friend became a game player with no seated run open (<see cref="RunOpen"/> false), and <paramref name="inCampaignRun"/>
    /// says the host's game is in a campaign run now (on its map or in its match). With no campaign start since hosting began,
    /// hosting began during that run: the start that seats the lobby never ran, so nobody who joins is put on this run's team.
    /// The line that says so, once per hosting session (a developer's path: the players' build hosts only at the title); else null.
    /// </summary>
    public string? UnseatedRunLine(SeatCandidate friend, bool inCampaignRun)
    {
        if (_runOpen || _startSeen || _unseatedRunSaid || !inCampaignRun)
        {
            return null;
        }

        _unseatedRunSaid = true;
        return string.Create(CultureInfo.InvariantCulture,
            $"Lobby: hosting began during this run, so {NameOf(friend, false)} (game player {friend.GamePlayer}), and any friend who joins it, isn't on its team until the host's Controllers screen puts them there; the lobby seats everyone at the next New Run or Continue (said once while hosting).");
    }

    /// <summary>
    /// Plans a campaign's start: <paramref name="players"/> are the host's own players in the game's order, then the friends in
    /// the lobby's seat order; <paramref name="memory"/> is the game's memory of sides now. Opens the run (its memory before
    /// any write is what <see cref="EndRun"/> puts back). Call <see cref="Written"/> for each write that was made.
    /// </summary>
    public SeatPlan PlanStart(IReadOnlyList<SeatCandidate> players, IReadOnlyDictionary<int, int> memory, bool soloSquad)
    {
        ArgumentNullException.ThrowIfNull(players);
        ArgumentNullException.ThrowIfNull(memory);
        _startSeen = true;
        if (!_runOpen)
        {
            _before.Clear();
            foreach (KeyValuePair<int, int> side in memory)
            {
                _before[side.Key] = side.Value;
            }

            _ours.Clear();
            _runOpen = true;
        }

        _soloRun = soloSquad;
        var decisions = new List<SeatDecision>(players.Count);
        var writes = new List<SideWrite>();
        if (soloSquad)
        {
            foreach (SeatCandidate player in players)
            {
                decisions.Add(new SeatDecision(player, SeatOutcome.LeftAsIs, NameOf(player, false)));
            }

            return new SeatPlan(true, decisions, writes);
        }

        var outcomes = new SeatOutcome[players.Count];
        bool hostOnTeam = false;
        bool hostChose = false;
        for (int i = 0; i < players.Count; i++)
        {
            SeatCandidate player = players[i];
            if (_choices.TryGetValue(player.GamePlayer, out int chosen))
            {
                hostChose |= !player.Friend;
                outcomes[i] = chosen == TeamSides.Right ? SeatOutcome.OnTeam : SeatOutcome.SitsOut;
            }
            else if (player.Friend)
            {
                outcomes[i] = SeatOutcome.OnTeam;
            }
            else
            {
                outcomes[i] = memory.TryGetValue(player.GamePlayer, out int side) && side == TeamSides.Right ? SeatOutcome.OnTeam : SeatOutcome.LeftAsIs;
            }

            hostOnTeam |= !player.Friend && outcomes[i] == SeatOutcome.OnTeam;
        }

        if (!hostOnTeam && !hostChose && HostDefault(players) is int index)
        {
            outcomes[index] = SeatOutcome.OnTeam;
        }

        int onTeam = 0;
        bool hostNamed = false;
        for (int i = 0; i < players.Count; i++)
        {
            SeatCandidate player = players[i];
            if (outcomes[i] == SeatOutcome.OnTeam && ++onTeam > TeamSides.TeamCap)
            {
                outcomes[i] = SeatOutcome.NoRoom;
            }

            bool first = !player.Friend && !hostNamed && outcomes[i] == SeatOutcome.OnTeam;
            hostNamed |= first;
            decisions.Add(new SeatDecision(player, outcomes[i], NameOf(player, first)));
            int? target = outcomes[i] switch
            {
                SeatOutcome.OnTeam => TeamSides.Right,
                SeatOutcome.NoRoom => TeamSides.Center,
                SeatOutcome.SitsOut => _choices[player.GamePlayer],
                _ => null,
            };
            if (target is not int wanted)
            {
                continue;
            }

            if (memory.TryGetValue(player.GamePlayer, out int now) && now == wanted)
            {
                _ours[player.GamePlayer] = wanted; // already so: what the book expects at the run's end
            }
            else
            {
                writes.Add(new SideWrite(player.GamePlayer, wanted));
            }
        }

        return new SeatPlan(false, decisions, writes);
    }

    /// <summary>A write of a plan was made in the game's memory.</summary>
    public void Written(SideWrite write)
    {
        if (!_runOpen)
        {
            return;
        }

        if (write.Side is int side)
        {
            _ours[write.GamePlayer] = side;
        }
        else
        {
            _ours.Remove(write.GamePlayer);
        }
    }

    /// <summary>
    /// A friend became a game player during a run: <paramref name="onTeam"/> is how many live players the game's team list
    /// holds, <paramref name="alreadyOnTeam"/> whether this one is among them.
    /// </summary>
    public LateSeatPlan PlanLateJoin(SeatCandidate friend, IReadOnlyDictionary<int, int> memory, int onTeam, bool alreadyOnTeam)
    {
        ArgumentNullException.ThrowIfNull(memory);
        string who = string.Create(CultureInfo.InvariantCulture, $"{NameOf(friend, false)} (game player {friend.GamePlayer})");
        LateSeat outcome = !_runOpen ? LateSeat.NoRun
            : alreadyOnTeam ? LateSeat.AlreadyOnTeam
            : _soloRun ? LateSeat.SoloSquad
            : _choices.TryGetValue(friend.GamePlayer, out int chosen) && chosen != TeamSides.Right ? LateSeat.SitsOut
            : onTeam >= TeamSides.TeamCap ? LateSeat.NoRoom
            : LateSeat.Add;
        SideWrite? write = outcome == LateSeat.Add && !(memory.TryGetValue(friend.GamePlayer, out int side) && side == TeamSides.Right)
            ? new SideWrite(friend.GamePlayer, TeamSides.Right)
            : null;
        if (outcome == LateSeat.Add && write == null)
        {
            _ours[friend.GamePlayer] = TeamSides.Right;
        }

        string line = outcome switch
        {
            LateSeat.Add => $"Lobby: {who} is on the run's team from the next match (the game's home list, as its controller screen fills it).",
            LateSeat.AlreadyOnTeam => $"Lobby: {who} is already on the run's team.",
            LateSeat.NoRun => $"Lobby: {who} joined with no run under way; the next start puts them on the team.",
            LateSeat.SoloSquad => $"Lobby: {who} joined during the run and can't be put on the team: the solo squad has one player.",
            LateSeat.SitsOut => $"Lobby: {who} joined during the run and isn't put on the team: {SeatPlan.SitsOutText}.",
            _ => $"Lobby: {who} joined during the run and can't be put on the team: {SeatPlan.NoRoomText}.",
        };
        return new LateSeatPlan(outcome, write, line);
    }

    /// <summary>
    /// A friend's game player went for good: their id's side goes back to what it was before the run (ids are reused, and a
    /// later player on it must not be seated by theirs), and any choice for it is forgotten. Returns the write, if any.
    /// </summary>
    public SideWrite? Forget(int gamePlayer, IReadOnlyDictionary<int, int> memory)
    {
        ArgumentNullException.ThrowIfNull(memory);
        _choices.Remove(gamePlayer);
        if (!_runOpen)
        {
            return null;
        }

        _ours.Remove(gamePlayer);
        return PutBackOne(gamePlayer, memory);
    }

    /// <summary>
    /// The seated run is over (the host is back at the lobby): the controller screen's changes are kept as this session's
    /// choices, and the writes that put the memory back as before the run's start are returned. Nothing when no run is open.
    /// A solo squad's run keeps no choice: its screen takes one player on the team and saves everyone else in the middle,
    /// which is that squad's rule, not anyone's choice to sit out a later run.
    /// </summary>
    public (IReadOnlyList<SideWrite> PutBack, IReadOnlyList<int> Chosen) EndRun(IReadOnlyDictionary<int, int> memory)
    {
        ArgumentNullException.ThrowIfNull(memory);
        if (!_runOpen)
        {
            return (Array.Empty<SideWrite>(), Array.Empty<int>());
        }

        var changed = new List<int>();
        foreach (KeyValuePair<int, int> side in memory)
        {
            if (Expected(side.Key) != side.Value)
            {
                changed.Add(side.Key);
            }
        }

        var chosen = new List<int>();
        if (changed.Count > 0 && !_soloRun)
        {
            // The screen's save writes every player it shows: what it kept as the book wrote counts as chosen too.
            foreach (int id in changed.Concat(_ours.Keys).Distinct().OrderBy(id => id))
            {
                if (memory.TryGetValue(id, out int side))
                {
                    _choices[id] = side;
                    chosen.Add(id);
                }
            }
        }

        IReadOnlyList<SideWrite> putBack = PutBackAll(memory);
        CloseRun();
        return (putBack, chosen);
    }

    /// <summary>The session is over: the memory goes back as before the open run's start (if any), and every choice is forgotten.</summary>
    public IReadOnlyList<SideWrite> EndSession(IReadOnlyDictionary<int, int> memory)
    {
        ArgumentNullException.ThrowIfNull(memory);
        IReadOnlyList<SideWrite> putBack = _runOpen ? PutBackAll(memory) : Array.Empty<SideWrite>();
        CloseRun();
        _choices.Clear();
        _startSeen = false;
        _unseatedRunSaid = false;
        return putBack;
    }

    /// <summary>The line for a put-back.</summary>
    public static string PutBackLine(int changes, string when) =>
        string.Create(CultureInfo.InvariantCulture, $"Lobby: the game's remembered sides are put back as before this run's start ({changes} change(s); {when}).");

    /// <summary>The line for the controller screen's choices kept, or null with none.</summary>
    public string? ChoicesLine(IReadOnlyList<int> chosen)
    {
        ArgumentNullException.ThrowIfNull(chosen);
        if (chosen.Count == 0)
        {
            return null;
        }

        var text = new StringBuilder("Lobby: the game's controller screen changed who plays this run: ");
        for (int i = 0; i < chosen.Count; i++)
        {
            text.Append(i > 0 ? ", " : string.Empty).Append(CultureInfo.InvariantCulture, $"game player {chosen[i]} {TeamSides.Text(_choices[chosen[i]])}");
        }

        return text.Append("; kept for this session's later starts.").ToString();
    }

    private static string NameOf(SeatCandidate player, bool host) => player.Friend ? player.Who : host ? "the host" : "another player on the host's PC";

    /// <summary>The host's default player, as the game picks one alone: the first pad player among the host's own, else the first of the others.</summary>
    private static int? HostDefault(IReadOnlyList<SeatCandidate> players)
    {
        int? keyboard = null;
        for (int i = 0; i < players.Count; i++)
        {
            if (players[i].Friend)
            {
                continue;
            }

            if (players[i].Gamepad)
            {
                return i;
            }

            keyboard ??= i;
        }

        return keyboard;
    }

    private int? Expected(int id) => _ours.TryGetValue(id, out int ours) ? ours : _before.TryGetValue(id, out int before) ? before : null;

    private SideWrite? PutBackOne(int id, IReadOnlyDictionary<int, int> memory)
    {
        bool has = memory.TryGetValue(id, out int now);
        if (_before.TryGetValue(id, out int before))
        {
            return has && now == before ? null : new SideWrite(id, before);
        }

        return has ? new SideWrite(id, null) : null;
    }

    private List<SideWrite> PutBackAll(IReadOnlyDictionary<int, int> memory)
    {
        var writes = new List<SideWrite>();
        foreach (int id in memory.Keys.Concat(_before.Keys).Distinct().OrderBy(id => id))
        {
            if (PutBackOne(id, memory) is SideWrite write)
            {
                writes.Add(write);
            }
        }

        return writes;
    }

    private void CloseRun()
    {
        _runOpen = false;
        _soloRun = false;
        _before.Clear();
        _ours.Clear();
    }
}
