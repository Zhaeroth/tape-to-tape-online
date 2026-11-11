using System;
using System.Collections.Generic;
using T2TOnline.Core.Protocol;

namespace T2TOnline.Core.Lobby;

/// <summary>Whose view of the lobby a <see cref="LobbyState"/> is.</summary>
public enum LobbyRole : byte
{
    /// <summary>In no session.</summary>
    None = 0,

    /// <summary>This PC hosts the lobby and the game.</summary>
    Host = 1,

    /// <summary>This PC is in a friend's lobby.</summary>
    Friend = 2,
}

/// <summary>Where the lobby is, as the board says. Values are on the wire (<see cref="LobbyBoardMessage"/>): never renumber.</summary>
public enum LobbyPhase : byte
{
    /// <summary>The lobby is up and nothing is happening.</summary>
    Open = 0,

    /// <summary>The 3 s countdown runs: the only time a Start can be cancelled.</summary>
    Counting = 1,

    /// <summary>The countdown is over: friends get ready to follow, the host waits for them (the barrier, usually under 0.5 s).</summary>
    Starting = 2,

    /// <summary>The host's game has left the idle title: friends follow it.</summary>
    Playing = 3,

    /// <summary>The host's title is loading after a game: friends come home.</summary>
    Returning = 4,
}

/// <summary>What the lobby will start. Values are on the wire: never renumber.</summary>
public enum LobbyMode : byte
{
    /// <summary>The co-op campaign, on the chosen save.</summary>
    Campaign = 0,

    /// <summary>The game's "Quick Game" (<c>v2ui/title_screen/quick_match</c>), what the title's Play Now runs.</summary>
    QuickGame = 1,
}

/// <summary>Where a seat's person is. Values are on the wire: never renumber.</summary>
public enum SeatStage : byte
{
    /// <summary>Nobody: an empty seat. Never on the wire (the board lists who's there).</summary>
    Open = 0,

    /// <summary>A Steam invite went out: a ghost on the next empty seat, for the 2-minute resend window.</summary>
    Invited = 1,

    /// <summary>In the Steam lobby, not seated yet.</summary>
    Joining = 2,

    /// <summary>Seated and on the lobby screen (or following the game while one runs).</summary>
    Here = 3,

    /// <summary>Seated, and getting ready to follow or coming home from a game.</summary>
    Loading = 4,

    /// <summary>Dropped; their seat is kept for <see cref="LobbySeat.ReconnectSeconds"/> more.</summary>
    Reconnecting = 5,
}

/// <summary>What the host is busy with, so friends can tell the lobby isn't frozen. Unknown values read as <see cref="None"/>.</summary>
public enum HostActivity : byte
{
    /// <summary>Nothing to say.</summary>
    None = 0,

    /// <summary>The host's focus is on the save row.</summary>
    PickingSave = 1,

    /// <summary>The host's focus is on the mode row.</summary>
    PickingMode = 2,

    /// <summary>The host's friend list or Steam's invite window is open.</summary>
    Inviting = 3,

    /// <summary>The host pressed Start and chose to wait for someone who isn't here yet.</summary>
    WaitingForEveryone = 4,
}

/// <summary>A friend's own stage, in their member message. Values are on the wire: never renumber.</summary>
public enum LobbyMemberStage : byte
{
    /// <summary>On the lobby screen: their game, device and saves are theirs.</summary>
    InLobby = 0,

    /// <summary>Getting ready to follow (backup, save guard, device handover).</summary>
    Preparing = 1,

    /// <summary>Following the host's game.</summary>
    Playing = 2,

    /// <summary>Coming home from a game.</summary>
    Returning = 3,
}

/// <summary>What ended the last game, for the ribbon on the lobby. Unknown values read as <see cref="None"/>.</summary>
public enum LobbyResultKind : byte
{
    /// <summary>No ribbon: no game yet, or nothing measured to show.</summary>
    None = 0,

    /// <summary>A match won: <see cref="LobbyResult.First"/> our score, <see cref="LobbyResult.Second"/> theirs.</summary>
    MatchWon = 1,

    /// <summary>A match lost: the same numbers.</summary>
    MatchLost = 2,

    /// <summary>A match that ended level: the same numbers.</summary>
    MatchTied = 3,

    /// <summary>A run won: <see cref="LobbyResult.First"/> its act, <see cref="LobbyResult.Second"/> its floor.</summary>
    RunWon = 4,

    /// <summary>A run lost or conceded: the same numbers.</summary>
    RunOver = 5,

    /// <summary>The host left a run from the map's menu, and it's still in the save: the same numbers.</summary>
    RunSaved = 6,

    /// <summary>A match with players on both sides, so nobody's "we": the score alone, <see cref="LobbyResult.First"/> the home side's, <see cref="LobbyResult.Second"/> the visitors'.</summary>
    MatchScore = 7,
}

/// <summary>Why the host can't start right now.</summary>
public enum StartBlock : byte
{
    /// <summary>Nothing: Start is lit.</summary>
    None = 0,

    /// <summary>Only the host starts: a friend's lobby has no Start.</summary>
    NotHost = 1,

    /// <summary>The lobby isn't <see cref="LobbyPhase.Open"/>.</summary>
    NotOpen = 2,

    /// <summary>The title isn't ready: a load, a save switch or a menu over it (<c>RunSync.ReadyToStart</c> says no).</summary>
    TitleBusy = 3,
}

/// <summary>The co-op save the lobby shows: the slot's row, or <see cref="None"/> (Quick Game, or none chosen).</summary>
/// <param name="Slot">The slot's number, from 1; 0 for none.</param>
/// <param name="HasRun">True when the slot holds a run that can go on.</param>
/// <param name="SquadId">The run's squad id: each PC names it in its own language. Empty when there's no run.</param>
/// <param name="Act">The run's act, or 0.</param>
/// <param name="Floor">The run's floor, or 0.</param>
/// <param name="SavedAtUnixSeconds">When the slot was last written, UTC seconds; 0 when unknown. Each PC shows it in its own time.</param>
public readonly record struct LobbySaveRow(byte Slot, bool HasRun, string SquadId, byte Act, byte Floor, long SavedAtUnixSeconds)
{
    /// <summary>No save: Quick Game, or none chosen.</summary>
    public static LobbySaveRow None { get; } = new(0, false, string.Empty, 0, 0, 0);
}

/// <summary>The ribbon after a game: measured numbers only, and none at all when nothing was measured.</summary>
/// <param name="Kind">What happened.</param>
/// <param name="First">Our score, or the run's act (see <see cref="LobbyResultKind"/>).</param>
/// <param name="Second">Their score, or the run's floor.</param>
public readonly record struct LobbyResult(LobbyResultKind Kind, byte First, byte Second)
{
    /// <summary>No ribbon.</summary>
    public static LobbyResult None { get; } = new(LobbyResultKind.None, 0, 0);
}

/// <summary>One seat on a screen. <see cref="LobbyState.Seats"/> always holds one per seat of the lobby, empty ones included.</summary>
public sealed record LobbySeat
{
    /// <summary>The seat's place, from 0. It's the player number less one, the host being 0.</summary>
    public int Index { get; init; }

    /// <summary>Their Steam ID, or 0 for an empty seat.</summary>
    public ulong SteamId { get; init; }

    /// <summary>The name the host knows them by, cleaned: what to show until (or unless) this PC's Steam knows better.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>True on the host's seat: shows the crown and the word.</summary>
    public bool IsHost { get; init; }

    /// <summary>True on this PC's own seat.</summary>
    public bool IsSelf { get; init; }

    /// <summary>Where they are; <see cref="SeatStage.Open"/> for an empty seat.</summary>
    public SeatStage Stage { get; init; }

    /// <summary>Their optional ready mark. Never gates anything.</summary>
    public bool Ready { get; init; }

    /// <summary>What they play with.</summary>
    public InputKind DeviceKind { get; init; }

    /// <summary>Their pad's family, the game's own device type.</summary>
    public byte DeviceFamily { get; init; }

    /// <summary>Their player colour's index, for the game's <c>PlayersManager.GetPlayerColor</c>.</summary>
    public int PlayerColor { get; init; }

    /// <summary>Their round trip to the host, in ms, or -1 when unknown (the host's own seat, or nothing measured yet).</summary>
    public int PingMs { get; init; } = -1;

    /// <summary>Signal bars to draw: 0 when unknown (<see cref="SignalBars"/>).</summary>
    public int Bars { get; init; }

    /// <summary>Seconds their seat is still kept while <see cref="SeatStage.Reconnecting"/>.</summary>
    public int ReconnectSeconds { get; init; }

    /// <summary>The player number shown: colour is never the only mark.</summary>
    public int PlayerNumber => Index + 1;

    /// <summary>True when nobody's in the seat.</summary>
    public bool IsEmpty => Stage == SeatStage.Open;

    /// <summary>The icon and word for this seat's state: colour is never the only mark (XAG 103).</summary>
    public LobbyMark Mark => LobbyMarks.Of(Stage, ReconnectSeconds);
}

/// <summary>What a <see cref="LobbyState"/> needs beside the board: what only this PC knows.</summary>
public readonly record struct LobbyViewInputs
{
    /// <summary>Host or friend.</summary>
    public LobbyRole Role { get; init; }

    /// <summary>This PC's Steam ID, to mark its own seat.</summary>
    public ulong Self { get; init; }

    /// <summary>How long ago the board arrived or was made, to run its countdown down here.</summary>
    public long BoardAgeMicros { get; init; }

    /// <summary>The flow's <see cref="LobbyFlow.IsInputLocked"/>: the 0.5 s after a return.</summary>
    public bool InputLocked { get; init; }

    /// <summary>The flow's <see cref="LobbyFlow.BackFromGame"/>: focus goes to Start, and the ribbon shows.</summary>
    public bool BackFromGame { get; init; }

    /// <summary>Friend: the link to the host is lost, and we're trying to get back.</summary>
    public bool Reconnecting { get; init; }

    /// <summary>Host: <c>RunSync.ReadyToStart</c>: the title is idle and the chosen save is what it shows.</summary>
    public bool TitleReady { get; init; }

    /// <summary>The flow's <see cref="LobbyFlow.IsWaitingForEveryone"/>: Start was pressed, and it starts by itself once everyone's here.</summary>
    public bool WaitingForEveryone { get; init; }

    /// <summary>This player's language, an index into <see cref="LobbyWords.Languages"/>, for a name that needs its fallback.</summary>
    public int Language { get; init; }

    /// <summary>
    /// The name to show for a Steam ID, given the host's name for them: this PC's Steam if it knows them (it says "[unknown]" for a friend of
    /// a friend, and the board's name stands in). Null keeps the board's. Cleaned for drawing after this either way. May throw: the board's name stands in.
    /// </summary>
    public Func<ulong, string, string?>? NameOf { get; init; }

    /// <summary>Whether a font can draw a code point (<see cref="DisplayName.Clean"/>). Null keeps every character.</summary>
    public Func<int, bool>? CanDraw { get; init; }
}

/// <summary>
/// Everything a lobby screen shows, as plain data: every screen is a function of this, and keeps no truth of its own. On a
/// host it's built from the board the host publishes (<see cref="LobbyBoardBuilder"/>), on a friend from the board that
/// arrived (<see cref="LobbyFlow"/> says where the flow is); the tour feeds fake ones.
/// </summary>
public sealed record LobbyState
{
    /// <summary>Most seats a lobby can have.</summary>
    public const int MaxSeats = 10;

    /// <summary>The seats the lobby is tuned for: more show as experimental.</summary>
    public const int TunedSeats = 4;

    /// <summary>An empty state: no session.</summary>
    public static LobbyState None { get; } = new();

    /// <summary>Host, friend, or no session.</summary>
    public LobbyRole Role { get; init; }

    /// <summary>Where the lobby is.</summary>
    public LobbyPhase Phase { get; init; }

    /// <summary>The current Start's number: a stale ack can't confuse a later Start. 0 before the first.</summary>
    public uint Epoch { get; init; }

    /// <summary>What Start will start.</summary>
    public LobbyMode Mode { get; init; }

    /// <summary>The save row; <see cref="LobbySaveRow.None"/> for Quick Game.</summary>
    public LobbySaveRow Save { get; init; } = LobbySaveRow.None;

    /// <summary>One per seat of the lobby, in seat order, empty ones included.</summary>
    public IReadOnlyList<LobbySeat> Seats { get; init; } = Array.Empty<LobbySeat>();

    /// <summary>How much of the countdown is left, in ms; 0 outside <see cref="LobbyPhase.Counting"/>.</summary>
    public int CountdownMs { get; init; }

    /// <summary>What the host is busy with.</summary>
    public HostActivity Activity { get; init; }

    /// <summary>The ribbon after a game; <see cref="LobbyResult.None"/> before one.</summary>
    public LobbyResult Result { get; init; } = LobbyResult.None;

    /// <summary>Start is lit: this PC hosts, the lobby is open, and the title is ready. Someone not here doesn't stop it: Start then asks.</summary>
    public bool StartAllowed => WhyNot == StartBlock.None;

    /// <summary>Why not; <see cref="StartBlock.None"/> when it's allowed.</summary>
    public StartBlock WhyNot { get; init; } = StartBlock.NotHost;

    /// <summary>
    /// Who's seated or arriving but not here, for Start's one prompt (Wait / Start anyway): Joining, Loading, Reconnecting.
    /// A friend only invited isn't counted: an invite nobody answers must not hold a Start.
    /// </summary>
    public IReadOnlyList<LobbySeat> NotHere { get; init; } = Array.Empty<LobbySeat>();

    /// <summary>Start was pressed, and it starts by itself once everyone's here.</summary>
    public bool WaitingForEveryone { get; init; }

    /// <summary>The 0.5 s after a return: presses are ignored, as people arrive mashing through results.</summary>
    public bool InputLocked { get; init; }

    /// <summary>The host just came back from a game: focus starts on Start.</summary>
    public bool BackFromGame { get; init; }

    /// <summary>Friend: the link to the host is lost, and we're trying to get back.</summary>
    public bool Reconnecting { get; init; }

    /// <summary>The host's name, or empty before a board.</summary>
    public string HostName => HostSeat?.Name ?? string.Empty;

    /// <summary>The host's seat, or null before a board.</summary>
    public LobbySeat? HostSeat
    {
        get
        {
            foreach (LobbySeat seat in Seats)
            {
                if (seat.IsHost)
                {
                    return seat;
                }
            }

            return null;
        }
    }

    /// <summary>This PC's seat, or null.</summary>
    public LobbySeat? SelfSeat
    {
        get
        {
            foreach (LobbySeat seat in Seats)
            {
                if (seat.IsSelf)
                {
                    return seat;
                }
            }

            return null;
        }
    }

    /// <summary>People seated: everyone but empty seats and invited ghosts.</summary>
    public int Players
    {
        get
        {
            int players = 0;
            foreach (LobbySeat seat in Seats)
            {
                if (seat.Stage is not (SeatStage.Open or SeatStage.Invited))
                {
                    players++;
                }
            }

            return players;
        }
    }

    /// <summary>True for more seats than the lobby is tuned for.</summary>
    public bool Experimental => Seats.Count > TunedSeats;

    /// <summary>
    /// A screen's state from <paramref name="board"/>: seats filled in to the lobby's size with names ready to draw and never blank, the
    /// countdown run down by the board's age, and what only this PC knows.
    /// </summary>
    public static LobbyState From(LobbyBoardMessage board, in LobbyViewInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(board);

        int capacity = Math.Clamp((int)board.Capacity, 1, MaxSeats);
        var seats = new LobbySeat[capacity];
        var notHere = new List<LobbySeat>();
        for (int i = 0; i < capacity; i++)
        {
            seats[i] = new LobbySeat { Index = i, PlayerColor = i };
        }

        foreach (LobbyBoardSeat from in board.Seats)
        {
            if (from.Index >= capacity)
            {
                continue;
            }

            var seat = new LobbySeat
            {
                Index = from.Index,
                SteamId = from.SteamId,
                Name = NameFor(from, inputs),
                IsHost = from.IsHost,
                IsSelf = from.SteamId == inputs.Self && inputs.Self != 0,
                Stage = from.Stage,
                Ready = from.Ready,
                DeviceKind = from.DeviceKind,
                DeviceFamily = from.DeviceFamily,
                PlayerColor = from.PlayerColor,
                PingMs = from.PingMs,
                Bars = from.Bars,
                ReconnectSeconds = from.ReconnectSeconds,
            };
            seats[from.Index] = seat;
            if (from.Stage is SeatStage.Joining or SeatStage.Loading or SeatStage.Reconnecting)
            {
                notHere.Add(seat);
            }
        }

        long countdownMicros = board.Phase == LobbyPhase.Counting ? (board.CountdownMs * 1000L) - Math.Max(0, inputs.BoardAgeMicros) : 0;
        return new LobbyState
        {
            Role = inputs.Role,
            Phase = board.Phase,
            Epoch = board.Epoch,
            Mode = board.Mode,
            Save = board.Save,
            Seats = seats,
            CountdownMs = countdownMicros <= 0 ? 0 : (int)Math.Min(countdownMicros / 1000, int.MaxValue),
            Activity = board.Activity,
            Result = board.Result,
            WhyNot = inputs.Role != LobbyRole.Host ? StartBlock.NotHost
                : board.Phase != LobbyPhase.Open ? StartBlock.NotOpen
                : !inputs.TitleReady ? StartBlock.TitleBusy
                : StartBlock.None,
            NotHere = notHere,
            WaitingForEveryone = inputs.WaitingForEveryone,
            InputLocked = inputs.InputLocked,
            BackFromGame = inputs.BackFromGame,
            Reconnecting = inputs.Reconnecting,
        };
    }

    private static string NameFor(LobbyBoardSeat seat, in LobbyViewInputs inputs)
    {
        string? name = seat.Name;
        if (inputs.NameOf != null)
        {
            try
            {
                name = inputs.NameOf(seat.SteamId, seat.Name);
            }
            catch (Exception)
            {
                name = seat.Name; // this PC's Steam failed: the host's name for them will do
            }
        }

        // What this PC's Steam doesn't know: the host's name for them, before the number.
        return DisplayName.Clean(string.IsNullOrWhiteSpace(name) || name == DisplayName.SteamUnknown ? seat.Name : name, seat.Index + 1, inputs.Language, inputs.CanDraw);
    }
}
