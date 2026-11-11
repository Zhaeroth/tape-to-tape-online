using System;
using System.Collections.Generic;
using T2TOnline.Core.Protocol;

namespace T2TOnline.Core.Lobby;

/// <summary>Where a host's session is. See <see cref="LobbyFlow"/>.</summary>
public enum LobbyHostState
{
    /// <summary>Not hosting.</summary>
    Idle,

    /// <summary>Steam is making the lobby.</summary>
    Creating,

    /// <summary>The lobby is up and the title is idle: the lobby's screen is the title's whole business.</summary>
    Open,

    /// <summary>The 3 s countdown runs. The only state a Start can be cancelled in.</summary>
    Counting,

    /// <summary>The barrier: friends get ready to follow, for at most 3 s.</summary>
    Starting,

    /// <summary>The game's own start flow runs at the title (squad screen, controller screen, prompts): it can still be backed out of.</summary>
    GameFlow,

    /// <summary>The host's game has left the title scene.</summary>
    InGame,

    /// <summary>The title scene is loading again after a game: everyone's coming back to the lobby.</summary>
    Returning,
}

/// <summary>Where a friend's session is. See <see cref="LobbyFlow"/>.</summary>
public enum LobbyFriendState
{
    /// <summary>In no one's lobby.</summary>
    Idle,

    /// <summary>Steam is letting us into the lobby.</summary>
    JoiningLobby,

    /// <summary>In the Steam lobby, saying hello to the host.</summary>
    Greeting,

    /// <summary>Seated, on the lobby screen. Our game, device and saves are ours.</summary>
    InLobby,

    /// <summary><see cref="LobbyEffect.BeginPlay"/> is running: backup, save guard, device handover.</summary>
    Preparing,

    /// <summary>Following the host's game. Our device plays on it.</summary>
    Playing,

    /// <summary>The host is back at its lobby, and our game is coming home.</summary>
    Returning,

    /// <summary>The link to the host is lost, and we're trying to get back.</summary>
    Lost,
}

/// <summary>What happened, as the session tells <see cref="LobbyFlow"/>. Events that make no sense in a state do nothing.</summary>
public enum LobbyEvent
{
    // Both ends.

    /// <summary>F7, Shift+F7, F1 or the Host Lobby row: this PC hosts. Run the checks (<c>Ready()</c>) first.</summary>
    HostPressed,

    /// <summary>F7, F1, a browser row, an invite or Join Game: this PC joins. Run the checks first.</summary>
    JoinPressed,

    /// <summary>Host: Steam made the lobby.</summary>
    LobbyCreated,

    /// <summary>Friend: we're in the Steam lobby, and its versions and host check out.</summary>
    LobbyEntered,

    /// <summary>Steam didn't make or let us into the lobby, or what we found there is refused: remember why before raising this.</summary>
    LobbyFailed,

    /// <summary>Friend: the host refused us: remember why before raising this.</summary>
    Refused,

    /// <summary>The player chose to leave or close, past any prompt.</summary>
    LeavePressed,

    /// <summary>The session ended without being asked to (a friend: the host closed or removed us; a host: Steam or the mod stopped): remember why before raising this.</summary>
    SessionEnded,

    // Host: the lobby's buttons.

    /// <summary>Start, past the prompt for whoever isn't here (or with everyone here).</summary>
    StartPressed,

    /// <summary>Start's prompt answered Wait: it starts by itself when <see cref="EveryoneHere"/> comes.</summary>
    WaitPressed,

    /// <summary>Everyone seated is here. Level-triggered: raise it whenever that's so; it matters only while waiting.</summary>
    EveryoneHere,

    /// <summary>B during the countdown or the wait.</summary>
    CancelPressed,

    // Host: friends.

    /// <summary>A friend joined or said hello again (<c>peer</c>): anything they acked before is forgotten.</summary>
    FriendArrived,

    /// <summary>A friend left (<c>peer</c>): the barrier stops waiting for them.</summary>
    FriendGone,

    /// <summary>A friend's member message named an epoch (<c>peer</c>, <c>epoch</c>). Only the current Start's counts.</summary>
    FriendAcked,

    // Host: the title. Level-triggered: raise what's true each tick, at no cost when nothing changes.

    /// <summary>
    /// The title has left idle for a game: a Start is running, or the title's own buttons were pressed (<c>m_IsStartingRun</c>,
    /// the game's start menus over it). Outside a Start this is an implicit Start, so what isn't a game's start must not raise it:
    /// the lobby's own prompts and Settings, the Glossary, a language change.
    /// </summary>
    TitleBusy,

    /// <summary>The title is idle: no start, no menu over it, no load. Held for 1 s in <see cref="LobbyHostState.GameFlow"/> it's a back-out.</summary>
    TitleIdle,

    /// <summary>The host's game isn't on the title scene.</summary>
    SceneLeftTitle,

    /// <summary>The title scene is loading after a game (<c>SceneFollowing.HostSceneChanged</c> at <c>start_screen</c>). An edge, once per load.</summary>
    TitleSceneLoaded,

    /// <summary>
    /// The title that just loaded is ready for input: an edge, after <see cref="TitleSceneLoaded"/>. <see cref="TitleIdle"/> counts as this
    /// too, and is what the session raises (<see cref="LobbySignals.TitleEvent"/> never gives this one); kept for a caller that has the edge.
    /// </summary>
    TitleReady,

    // Friend: the session and the board.

    /// <summary>The host's welcome said <see cref="SessionPhase.Lobby"/>.</summary>
    WelcomedLobby,

    /// <summary>The host's welcome said anything else: a game is running or starting, so we follow at once.</summary>
    WelcomedPlaying,

    /// <summary>A board says the host is at its lobby (<see cref="LobbyPhase.Open"/> or <see cref="LobbyPhase.Returning"/>), with its <c>epoch</c>.</summary>
    BoardOpen,

    /// <summary>A board says the countdown runs, with its <c>epoch</c>.</summary>
    BoardCounting,

    /// <summary>A board says the countdown is over, with its <c>epoch</c>.</summary>
    BoardStarting,

    /// <summary>A board says the host's game is running, with its <c>epoch</c>.</summary>
    BoardPlaying,

    /// <summary><see cref="LobbyEffect.BeginPlay"/> finished.</summary>
    PlayReady,

    /// <summary><see cref="LobbyEffect.BeginPlay"/> failed: remember why before raising this.</summary>
    PlayFailed,

    /// <summary>Our game is at its own title after following one (<c>TitleFollower</c> arrived). Level-triggered: raise it each tick it's so.</summary>
    OwnTitleBack,

    /// <summary>The link to the host dropped, and the session tries to get back by itself.</summary>
    LinkLost,

    /// <summary>
    /// The link to the host is back. The session doesn't raise it: what comes back after a drop is the host's welcome or a board, and either
    /// one ends <see cref="LobbyFriendState.Lost"/> by itself.
    /// </summary>
    LinkBack,

    /// <summary>
    /// The session gave up getting back. The session doesn't raise it: <see cref="LobbyFlow.Update"/> ends the session with the same effects
    /// once the window is over.
    /// </summary>
    RejoinExpired,
}

/// <summary>What the session must do, in the order given. <see cref="LobbyFlow.Handle"/> says; the flow itself touches nothing.</summary>
public enum LobbyEffect
{
    // Both ends.

    /// <summary>Open the lobby's screen, or reopen it, if the shell is allowed and the title is idle. The session goes on the same if it can't.</summary>
    ShowLobby,

    /// <summary>Close the lobby's screen now.</summary>
    HideLobby,

    /// <summary>Close the lobby's screen the way a Start does: once the game's next menu is up or the scene is leaving. A back-out returns to a live title.</summary>
    CloseLobbyForStart,

    /// <summary>End the session and leave the Steam lobby, if that isn't done yet (safe to run on what's already gone). A host's friends are told, and land on the Join list.</summary>
    EndSession,

    /// <summary>Show the failure remembered for this (<see cref="JoinFailures"/>).</summary>
    ShowFailure,

    // Host.

    /// <summary>Make the Steam lobby (friends-only, the lobby's seats).</summary>
    CreateLobby,

    /// <summary>The lobby exists: the host's session, lobby data, backup, slot switch, presence.</summary>
    StartHosting,

    /// <summary>Clear <c>CampaignUtil.startNewRunImmediately</c> (and keep clearing it: <see cref="LobbyFlow.KeepsNewRunFlagClear"/>), or the title opens the squad screen by itself.</summary>
    ClearNewRunFlag,

    /// <summary>Publish the board now: where the flow is changed (<see cref="LobbyFlow.BoardPhase"/>, <see cref="LobbyFlow.Epoch"/>).</summary>
    PublishBoard,

    /// <summary>Back up again before this Start (<c>EndSession</c>, <c>BeginSession</c>): a game has run since the last backup, and New Run can delete a slot.</summary>
    BackupAgain,

    /// <summary>Make friends game players, in seat order (<see cref="LobbyBoardBuilder.FriendsInSeatOrder"/>).</summary>
    AssignGamePlayers,

    /// <summary>Run the game's own start for the mode and save (<see cref="LobbyRules.StartKindOf"/>). Never comes with an implicit Start: the game has already gone.</summary>
    CallGameStart,

    /// <summary>The friend named by the event's <c>peer</c> is now following: send them what a late friend gets (<c>FriendJoined</c>).</summary>
    FriendFollowing,

    /// <summary>Back on the Online group, on Host Lobby.</summary>
    ShowOnlineGroup,

    // Friend.

    /// <summary>Ask Steam for the lobby.</summary>
    JoinLobby,

    /// <summary>Start the friend's session: hello, seat, welcome. Nothing else of theirs changes yet.</summary>
    SayHello,

    /// <summary>
    /// The play period starts: choose the input again, backup, save guard, UI lock, device detach, prompts pinned, following
    /// on. Raise <see cref="LobbyEvent.PlayReady"/> when done (<see cref="LobbyEvent.PlayFailed"/> if it can't).
    /// </summary>
    BeginPlay,

    /// <summary>
    /// The play period ends, BeginPlay's reverse: following off (the progress flush, the save check), guard off, device back,
    /// prompts unpinned. Comes exactly once per <see cref="BeginPlay"/>, on every path, before anything else on that path.
    /// </summary>
    EndPlay,

    /// <summary>Send our member message: ready mark, <see cref="LobbyFlow.MemberStage"/>, <see cref="LobbyFlow.AckedEpoch"/>.</summary>
    SendMember,

    /// <summary>The link dropped: say we're reconnecting, with a Back.</summary>
    ShowReconnecting,

    /// <summary>Say the host is gone for good, and that the friend's own progress is safe.</summary>
    SayLost,

    /// <summary>Land on the Join list (the row we came from if it's there).</summary>
    ShowJoinList,

    /// <summary>
    /// Friend: following a game that was already running when they were welcomed, with no lobby screen of theirs up: the Join
    /// list they came from (if it's up) has done its job and closes with its own close. Left up, it would sit under the
    /// friend's locked UI behind the host's screens, where nothing can be pressed on it.
    /// </summary>
    CloseJoinList,
}

/// <summary>The flow's timings, in microseconds.</summary>
public static class LobbyTiming
{
    /// <summary>The countdown on every screen.</summary>
    public const long CountdownMicros = 3_000_000;

    /// <summary>How long the host waits for friends' acks once the countdown is over.</summary>
    public const long BarrierMicros = 3_000_000;

    /// <summary>How long the title must stay idle after a Start before it's a back-out.</summary>
    public const long BackOutDebounceMicros = 1_000_000;

    /// <summary>How long presses are ignored after a return: people arrive mashing through results.</summary>
    public const long InputLockMicros = 500_000;

    /// <summary>How long a friend tries to get back to a lobby.</summary>
    public const long RejoinInLobbyMicros = 40_000_000;

    /// <summary>How long a friend tries to get back to a game (and how long the host keeps its seat, D22).</summary>
    public const long RejoinInGameMicros = 120_000_000;

    /// <summary>How long the host keeps a seat for a friend who dropped from the lobby.</summary>
    public const long KeepSeatInLobbyMicros = 30_000_000;

    /// <summary>
    /// How long the host keeps a seat for a friend who went, by their state and why. In the lobby a deliberate Quit frees it
    /// at once (a friend gains nothing by lying) and a drop keeps it 30 s; in a game it's kept 2 minutes whatever they said (D22).
    /// </summary>
    public static long KeepSeatMicros(LobbyHostState state, LeaveReason reason) => state switch
    {
        LobbyHostState.Open or LobbyHostState.Counting => reason is LeaveReason.Quit or LeaveReason.Removed ? 0 : KeepSeatInLobbyMicros,
        LobbyHostState.Idle or LobbyHostState.Creating => 0,
        _ => reason == LeaveReason.Removed ? 0 : RejoinInGameMicros,
    };
}

/// <summary>One state change, for the session log (<see cref="LobbyFlow.TryTakeChange"/>).</summary>
/// <param name="From">The state before.</param>
/// <param name="To">The state now.</param>
/// <param name="Epoch">The Start's number.</param>
/// <param name="Why">What moved it, in words for the log.</param>
/// <param name="AtMicros">When.</param>
public readonly record struct LobbyFlowChange(string From, string To, uint Epoch, string Why, long AtMicros)
{
    /// <summary>The session log's line.</summary>
    public string Line => $"Lobby: phase {From} -> {To} (epoch {Epoch}): {Why}.";
}

/// <summary>
/// The session's state machine, on both ends (one PC is host or friend, never both): pure events in, ordered effects out, no
/// Unity, no Steam, never throws. One flow for every way in (the native rows, F7, Shift+F7, F1, Steam joins), and the
/// screen is only a view of it: a session behaves the same whether or not the shell can open.
/// </summary>
/// <remarks>
/// <code>
/// Host   Idle -Host-> Creating -Created-> Open -Start-> Counting -3 s-> Starting -all acked or 3 s-> GameFlow -scene left-> InGame
///                                          ^  ^--- cancel -----'                      (players, game's start)  |                |
///                                          |-------------------- title idle for 1 s (a back-out) --------------'    title scene loads
///                                          '------------------------- title ready &lt;- Returning &lt;---------------------------'
///        Open or Counting -title busy, or the scene left-> GameFlow or InGame: an implicit Start, with no call of the game's.
/// Friend Idle -Join-> JoiningLobby -Entered-> Greeting -Welcomed(Lobby)-> InLobby -Board Starting|Playing-> Preparing -Ready-> Playing
///                                                 '------Welcomed(Playing)--------------------------------------^                |
///                                                                               ^                                        Board at the lobby
///                                                                               '---- Returning &lt;-- own title back ----------------'
///        any -LinkLost-> Lost -LinkBack-> where it was (a board then says where to go); the window over -> Idle, SayLost.
/// </code>
/// Epochs: each Start has a number, on the board and in every ack; an ack of an earlier Start counts for nothing. A friend's
/// <see cref="LobbyEffect.BeginPlay"/> runs when the board says Starting, after the countdown, so a cancel never churns backups.
/// Every path that ends a friend's play period starts with <see cref="LobbyEffect.EndPlay"/>.
/// </remarks>
public sealed class LobbyFlow
{
    private const int MaxChanges = 32;
    private const int MaxPeers = 64;

    private readonly List<LobbyEffect> _effects = new(8);
    private readonly HashSet<ulong> _present = new();
    private readonly HashSet<ulong> _awaiting = new();
    private readonly Dictionary<ulong, uint> _acked = new();
    private readonly Dictionary<ulong, long> _arrivedAt = new();
    private readonly Queue<LobbyFlowChange> _changes = new();
    private long _countdownEndsAt;
    private long _barrierStartedAt;
    private long _barrierEndsAt;
    private long _idleSince = long.MinValue;
    private long _inputLockedUntil = long.MinValue;
    private long _waitingSince;
    private long _rejoinEndsAt;
    private bool _lobbyUp;
    private bool _gameSinceBackup;
    private bool _hostBackAtLobby;
    private LobbyFriendState _resume;

    /// <summary>Where this PC's hosting is; <see cref="LobbyHostState.Idle"/> unless it hosts.</summary>
    public LobbyHostState Host { get; private set; }

    /// <summary>Where this PC's friend session is; <see cref="LobbyFriendState.Idle"/> unless it's joining or joined.</summary>
    public LobbyFriendState Friend { get; private set; }

    /// <summary>Host, friend, or neither.</summary>
    public LobbyRole Role => Host != LobbyHostState.Idle ? LobbyRole.Host : Friend != LobbyFriendState.Idle ? LobbyRole.Friend : LobbyRole.None;

    /// <summary>The current Start's number: the host's own, or the newest a board said. 0 before the first.</summary>
    public uint Epoch { get; private set; }

    /// <summary>Host: what the board says the phase is.</summary>
    public LobbyPhase BoardPhase => Host switch
    {
        LobbyHostState.Counting => LobbyPhase.Counting,
        LobbyHostState.Starting => LobbyPhase.Starting,
        LobbyHostState.GameFlow or LobbyHostState.InGame => LobbyPhase.Playing,
        LobbyHostState.Returning => LobbyPhase.Returning,
        _ => LobbyPhase.Open,
    };

    /// <summary>
    /// Host: what a welcome says now (<c>HostSession.WelcomePhase</c>): from <see cref="LobbyHostState.Starting"/> to the game's
    /// end a friend who joins follows at once; otherwise they wait in the lobby.
    /// </summary>
    public SessionPhase WelcomePhase => Host is LobbyHostState.Starting or LobbyHostState.GameFlow or LobbyHostState.InGame ? SessionPhase.Playing : SessionPhase.Lobby;

    /// <summary>
    /// Host: true while the new-run flag must stay clear, each frame (<c>RunSync.Step</c> runs anyway): <c>RunEndHelper.EndRun</c>
    /// sets it just before its scene load, and the title would open the squad screen by itself.
    /// </summary>
    public bool KeepsNewRunFlagClear => Host != LobbyHostState.Idle;

    /// <summary>Friend: <see cref="LobbyEffect.BeginPlay"/> ran, and <see cref="LobbyEffect.EndPlay"/> hasn't yet.</summary>
    public bool PlayActive { get; private set; }

    /// <summary>Friend: the stage a member message says.</summary>
    public LobbyMemberStage MemberStage => (Friend == LobbyFriendState.Lost ? _resume : Friend) switch
    {
        LobbyFriendState.Preparing => LobbyMemberStage.Preparing,
        LobbyFriendState.Playing => LobbyMemberStage.Playing,
        LobbyFriendState.Returning => LobbyMemberStage.Returning,
        _ => LobbyMemberStage.InLobby,
    };

    /// <summary>Friend: the newest Start we follow, for the member message. 0 before the first.</summary>
    public uint AckedEpoch { get; private set; }

    /// <summary>Friend: what <see cref="LobbyEffect.SendMember"/> sends (<c>FriendSession.SendMember</c>), with the ready mark the session keeps.</summary>
    public LobbyMemberMessage Member(bool ready) => new(ready, MemberStage, AckedEpoch);

    /// <summary>Host: Start was answered Wait: it starts by itself when <see cref="LobbyEvent.EveryoneHere"/> comes, or B cancels.</summary>
    public bool IsWaitingForEveryone { get; private set; }

    /// <summary>Host: how long the last wait lasted before its Start, for the log (<c>Lobby: start: P#a1b2c3 not here, waited 4 s</c>); 0 if there was none.</summary>
    public long LastWaitMicros { get; private set; }

    /// <summary>Host: the current game started without the lobby's Start: the title's own buttons, or a game already running.</summary>
    public bool IsImplicitStart { get; private set; }

    /// <summary>True from a game's return until the next Start or the session's end: focus starts on Start, and the ribbon shows.</summary>
    public bool BackFromGame { get; private set; }

    /// <summary>Host: who the barrier still waits for; after it, who missed it (they catch up with <see cref="LobbyEffect.FriendFollowing"/> when they ack).</summary>
    public IReadOnlyCollection<ulong> AwaitingAcks => _awaiting;

    /// <summary>
    /// Host: how long the last ack taken took (<c>Lobby: X acked in 412 ms</c>), or -1: from the barrier's start, or for a friend who
    /// arrived after it (a late joiner, a rejoin) from their own arrival (<see cref="LastAckFromArrival"/>).
    /// </summary>
    public long LastAckAfterMicros { get; private set; } = -1;

    /// <summary>Host: <see cref="LastAckAfterMicros"/> counts from that friend's arrival, which came after the barrier's start.</summary>
    public bool LastAckFromArrival { get; private set; }

    /// <summary>The countdown left, in ms: 0 outside <see cref="LobbyHostState.Counting"/>.</summary>
    public int CountdownMsLeft(long nowMicros)
    {
        if (Host != LobbyHostState.Counting || nowMicros >= _countdownEndsAt)
        {
            return 0;
        }

        return (int)Math.Min((_countdownEndsAt - nowMicros + 999) / 1000, LobbyBoardMessage.MaxCountdownMs);
    }

    /// <summary>True for the title's load after a game and the 0.5 s after it: lobby presses do nothing.</summary>
    public bool IsInputLocked(long nowMicros) =>
        Host == LobbyHostState.Returning || (_inputLockedUntil != long.MinValue && nowMicros < _inputLockedUntil);

    /// <summary>True when a return has set the input lock, whether or not it's over: the check tour's question, as a screenshot can outlast it.</summary>
    public bool InputLockWasSet => _inputLockedUntil != long.MinValue;

    /// <summary>Host: true when <paramref name="peer"/> acked the current Start while a game runs.</summary>
    public bool IsFollowing(ulong peer) =>
        Host is LobbyHostState.Starting or LobbyHostState.GameFlow or LobbyHostState.InGame && _acked.TryGetValue(peer, out uint epoch) && epoch == Epoch;

    /// <summary>Friend: the seconds left to get back; 0 unless <see cref="LobbyFriendState.Lost"/>.</summary>
    public int RejoinSecondsLeft(long nowMicros)
    {
        if (Friend != LobbyFriendState.Lost || nowMicros >= _rejoinEndsAt)
        {
            return 0;
        }

        return (int)Math.Min((_rejoinEndsAt - nowMicros + 999_999) / 1_000_000, int.MaxValue);
    }

    /// <summary>Takes the oldest state change not logged yet.</summary>
    public bool TryTakeChange(out LobbyFlowChange change) => _changes.TryDequeue(out change);

    /// <summary>
    /// Handles one event. Returns what the session must do, in order; empty (allocating nothing) when nothing changes.
    /// Never throws: an event that makes no sense now, or a value it doesn't know, does nothing.
    /// </summary>
    /// <param name="e">What happened.</param>
    /// <param name="nowMicros">The time now, from one clock for the whole session.</param>
    /// <param name="epoch">For <see cref="LobbyEvent.FriendAcked"/> and the board's events: the epoch named.</param>
    /// <param name="peer">For <see cref="LobbyEvent.FriendArrived"/>, <see cref="LobbyEvent.FriendGone"/> and <see cref="LobbyEvent.FriendAcked"/>: which friend, by the transport's word.</param>
    public IReadOnlyList<LobbyEffect> Handle(LobbyEvent e, long nowMicros, uint epoch = 0, ulong peer = 0)
    {
        _effects.Clear();
        if (Host != LobbyHostState.Idle || (e == LobbyEvent.HostPressed && Friend == LobbyFriendState.Idle))
        {
            HandleHost(e, nowMicros, epoch, peer);
        }
        else if (Friend != LobbyFriendState.Idle || e == LobbyEvent.JoinPressed)
        {
            HandleFriend(e, nowMicros, epoch);
        }

        return TakeEffects();
    }

    /// <summary>
    /// A board's phase as an event: <see cref="Handle"/> it with the board's epoch.
    /// </summary>
    public static LobbyEvent BoardEvent(LobbyPhase phase) => phase switch
    {
        LobbyPhase.Counting => LobbyEvent.BoardCounting,
        LobbyPhase.Starting => LobbyEvent.BoardStarting,
        LobbyPhase.Playing => LobbyEvent.BoardPlaying,
        _ => LobbyEvent.BoardOpen,
    };

    /// <summary>A welcome's phase as an event: anything but <see cref="SessionPhase.Lobby"/> follows at once.</summary>
    public static LobbyEvent WelcomeEvent(SessionPhase phase) => phase == SessionPhase.Lobby ? LobbyEvent.WelcomedLobby : LobbyEvent.WelcomedPlaying;

    /// <summary>
    /// Call every frame: the countdown, the barrier's 3 s, the back-out's 1 s and the rejoin window. Returns what must be
    /// done, in order; empty (allocating nothing) on almost every call.
    /// </summary>
    public IReadOnlyList<LobbyEffect> Update(long nowMicros)
    {
        _effects.Clear();
        switch (Host)
        {
            case LobbyHostState.Counting when nowMicros >= _countdownEndsAt:
                EnterStarting(nowMicros, "the countdown ended");
                break;
            case LobbyHostState.Starting when nowMicros >= _barrierEndsAt:
                EnterGameFlow(nowMicros, callGame: true, $"the barrier timed out with {_awaiting.Count} not ready");
                break;
            case LobbyHostState.GameFlow:
                CheckBackOut(nowMicros);
                break;
        }

        if (Friend == LobbyFriendState.Lost && nowMicros >= _rejoinEndsAt)
        {
            EndFriend(nowMicros, LobbyEffect.SayLost, "the rejoin window ended");
        }

        return TakeEffects();
    }

    private void HandleHost(LobbyEvent e, long now, uint epoch, ulong peer)
    {
        switch (e)
        {
            case LobbyEvent.HostPressed when Host == LobbyHostState.Idle:
                SetHost(LobbyHostState.Creating, now, "hosting, as asked");
                _effects.Add(LobbyEffect.CreateLobby);
                break;
            case LobbyEvent.LobbyCreated when Host == LobbyHostState.Creating:
                _gameSinceBackup = false; // hosting itself takes a backup
                SetHost(LobbyHostState.Open, now, "Steam made the lobby");
                _effects.Add(LobbyEffect.StartHosting);
                _effects.Add(LobbyEffect.ClearNewRunFlag);
                ShowLobby();
                _effects.Add(LobbyEffect.PublishBoard);
                break;
            case LobbyEvent.LobbyFailed when Host == LobbyHostState.Creating:
                SetHost(LobbyHostState.Idle, now, "Steam made no lobby");
                _effects.Add(LobbyEffect.ShowFailure);
                Reset();
                break;
            case LobbyEvent.LeavePressed:
                EndHost(now, "the host closed the lobby");
                break;
            case LobbyEvent.SessionEnded:
                EndHost(now, "the session ended");
                break;
            case LobbyEvent.StartPressed when Host == LobbyHostState.Open:
                EnterCounting(now, "Start");
                break;
            case LobbyEvent.WaitPressed when Host == LobbyHostState.Open && !IsWaitingForEveryone:
                IsWaitingForEveryone = true;
                _waitingSince = now;
                _effects.Add(LobbyEffect.PublishBoard);
                break;
            case LobbyEvent.EveryoneHere when Host == LobbyHostState.Open && IsWaitingForEveryone:
                EnterCounting(now, "everyone is here");
                break;
            case LobbyEvent.CancelPressed when Host == LobbyHostState.Open && IsWaitingForEveryone:
                IsWaitingForEveryone = false;
                _effects.Add(LobbyEffect.PublishBoard);
                break;
            case LobbyEvent.CancelPressed when Host == LobbyHostState.Counting:
                SetHost(LobbyHostState.Open, now, "the countdown was cancelled");
                _effects.Add(LobbyEffect.PublishBoard);
                break;
            case LobbyEvent.FriendArrived when peer != 0 && (_present.Contains(peer) || _present.Count < MaxPeers):
                _present.Add(peer);
                _acked.Remove(peer);
                _arrivedAt[peer] = now;
                break;
            case LobbyEvent.FriendGone:
                _present.Remove(peer);
                _acked.Remove(peer);
                _arrivedAt.Remove(peer);
                if (_awaiting.Remove(peer) && Host == LobbyHostState.Starting && _awaiting.Count == 0)
                {
                    EnterGameFlow(now, callGame: true, "the last friend waited for went");
                }

                break;
            case LobbyEvent.FriendAcked:
                OnAck(now, epoch, peer);
                break;
            case LobbyEvent.TitleBusy:
                _idleSince = long.MinValue;
                if (Host is LobbyHostState.Open or LobbyHostState.Counting or LobbyHostState.Starting)
                {
                    ImplicitStart(now, sceneLeft: false, "the title left idle by itself");
                }

                break;
            case LobbyEvent.TitleIdle when Host == LobbyHostState.GameFlow:
                if (_idleSince == long.MinValue)
                {
                    _idleSince = now;
                }

                CheckBackOut(now);
                break;
            case LobbyEvent.TitleIdle or LobbyEvent.TitleReady when Host == LobbyHostState.Returning:
                ArriveBack(now);
                break;
            case LobbyEvent.SceneLeftTitle:
                OnSceneLeft(now);
                break;
            case LobbyEvent.TitleSceneLoaded when Host is LobbyHostState.GameFlow or LobbyHostState.InGame:
                EnterReturning(now);
                break;
            case LobbyEvent.TitleReady when Host is LobbyHostState.GameFlow or LobbyHostState.InGame:
                // The load's own event was missed: the same return, all at once.
                EnterReturning(now);
                ArriveBack(now);
                break;
        }
    }

    private void OnSceneLeft(long now)
    {
        switch (Host)
        {
            case LobbyHostState.Open or LobbyHostState.Counting or LobbyHostState.Starting or LobbyHostState.Returning:
                ImplicitStart(now, sceneLeft: true, "the game left the title by itself");
                break;
            case LobbyHostState.GameFlow:
                _idleSince = long.MinValue;
                SetHost(LobbyHostState.InGame, now, "the scene left the title");
                HideLobby();
                break;
        }
    }

    private void OnAck(long now, uint epoch, ulong peer)
    {
        if (epoch == 0 || epoch != Epoch || !_present.Contains(peer) || Host is not (LobbyHostState.Starting or LobbyHostState.GameFlow or LobbyHostState.InGame))
        {
            return; // an earlier Start's, one of nobody's, or no game to follow
        }

        if (_acked.TryGetValue(peer, out uint acked) && acked == epoch)
        {
            return;
        }

        _acked[peer] = epoch;
        long from = _barrierStartedAt;
        LastAckFromArrival = _arrivedAt.TryGetValue(peer, out long arrived) && arrived > _barrierStartedAt;
        if (LastAckFromArrival)
        {
            from = arrived; // a late joiner or a rejoin: the Start they missed was minutes ago
        }

        LastAckAfterMicros = Math.Max(0, now - from);
        _effects.Add(LobbyEffect.FriendFollowing);
        if (_awaiting.Remove(peer) && Host == LobbyHostState.Starting && _awaiting.Count == 0)
        {
            EnterGameFlow(now, callGame: true, "every friend acked");
        }
    }

    private void EnterCounting(long now, string why)
    {
        LastWaitMicros = IsWaitingForEveryone ? Math.Max(0, now - _waitingSince) : 0;
        IsWaitingForEveryone = false;
        BackFromGame = false;
        IsImplicitStart = false;
        Epoch++;
        _countdownEndsAt = now + LobbyTiming.CountdownMicros;
        SetHost(LobbyHostState.Counting, now, why);
        _effects.Add(LobbyEffect.PublishBoard);
    }

    private void EnterStarting(long now, string why)
    {
        SetHost(LobbyHostState.Starting, now, why);
        BackupIfDue();
        _awaiting.Clear();
        _awaiting.UnionWith(_present);
        _barrierStartedAt = now;
        _barrierEndsAt = now + LobbyTiming.BarrierMicros;
        LastAckAfterMicros = -1;
        LastAckFromArrival = false;
        AddOnce(LobbyEffect.PublishBoard);
        if (_awaiting.Count == 0)
        {
            EnterGameFlow(now, callGame: true, "no friend to wait for");
        }
    }

    private void EnterGameFlow(long now, bool callGame, string why)
    {
        SetHost(LobbyHostState.GameFlow, now, why);
        _idleSince = long.MinValue;
        AddOnce(LobbyEffect.PublishBoard); // friends follow first: what the game's start publishes must reach them
        _effects.Add(LobbyEffect.AssignGamePlayers);
        if (callGame)
        {
            _effects.Add(LobbyEffect.CallGameStart);
        }

        CloseLobbyForStart();
    }

    /// <summary>The host's game left idle by another way than the lobby's Start: friends still follow, and nothing depends on a screen being up.</summary>
    private void ImplicitStart(long now, bool sceneLeft, string why)
    {
        if (Host is LobbyHostState.Open or LobbyHostState.Returning)
        {
            Epoch++;
        }

        bool fromBarrier = Host == LobbyHostState.Starting;
        IsWaitingForEveryone = false;
        BackFromGame = false;
        IsImplicitStart = true;
        if (!fromBarrier)
        {
            BackupIfDue();
            _awaiting.Clear();
            _awaiting.UnionWith(_present);
            _barrierStartedAt = now;
            LastAckAfterMicros = -1;
            LastAckFromArrival = false;
        }

        if (sceneLeft)
        {
            SetHost(LobbyHostState.InGame, now, why);
            _idleSince = long.MinValue;
            AddOnce(LobbyEffect.PublishBoard);
            HideLobby();
        }
        else
        {
            EnterGameFlow(now, callGame: false, why);
        }
    }

    private void CheckBackOut(long now)
    {
        if (_idleSince != long.MinValue && now - _idleSince >= LobbyTiming.BackOutDebounceMicros)
        {
            _idleSince = long.MinValue;
            IsImplicitStart = false;
            SetHost(LobbyHostState.Open, now, "the start was backed out");
            ShowLobby();
            _effects.Add(LobbyEffect.PublishBoard);
        }
    }

    private void EnterReturning(long now)
    {
        _idleSince = long.MinValue;
        _gameSinceBackup = true;
        IsImplicitStart = false;
        BackFromGame = true;
        SetHost(LobbyHostState.Returning, now, "the title is loading after a game");
        _effects.Add(LobbyEffect.ClearNewRunFlag);
        ShowLobby();
        _effects.Add(LobbyEffect.PublishBoard);
    }

    private void ArriveBack(long now)
    {
        _inputLockedUntil = now + LobbyTiming.InputLockMicros;
        SetHost(LobbyHostState.Open, now, "back at the lobby");
        AddOnce(LobbyEffect.PublishBoard);
    }

    private void EndHost(long now, string why)
    {
        HideLobby();
        _effects.Add(LobbyEffect.EndSession);
        _effects.Add(LobbyEffect.ShowOnlineGroup);
        SetHost(LobbyHostState.Idle, now, why);
        Reset();
    }

    private void BackupIfDue()
    {
        if (_gameSinceBackup)
        {
            _gameSinceBackup = false;
            _effects.Add(LobbyEffect.BackupAgain);
        }
    }

    private void HandleFriend(LobbyEvent e, long now, uint epoch)
    {
        bool board = e is LobbyEvent.BoardOpen or LobbyEvent.BoardCounting or LobbyEvent.BoardStarting or LobbyEvent.BoardPlaying;
        if (board && Friend is LobbyFriendState.Idle or LobbyFriendState.JoiningLobby or LobbyFriendState.Greeting)
        {
            return; // the welcome comes first, on the same channel
        }

        if (board)
        {
            if (Friend == LobbyFriendState.Lost)
            {
                Reconnected(now, "a board arrived");
            }

            OnBoard(e, now, epoch);
            return;
        }

        switch (e)
        {
            case LobbyEvent.JoinPressed when Friend == LobbyFriendState.Idle:
                SetFriend(LobbyFriendState.JoiningLobby, now, "joining, as asked");
                _effects.Add(LobbyEffect.JoinLobby);
                break;
            case LobbyEvent.LobbyEntered when Friend == LobbyFriendState.JoiningLobby:
                SetFriend(LobbyFriendState.Greeting, now, "in the Steam lobby");
                _effects.Add(LobbyEffect.SayHello);
                break;
            case LobbyEvent.LobbyFailed when Friend == LobbyFriendState.JoiningLobby:
                EndFriend(now, LobbyEffect.ShowFailure, "couldn't get into the lobby");
                break;
            case LobbyEvent.Refused when Friend is LobbyFriendState.Greeting or LobbyFriendState.Lost:
                EndFriend(now, LobbyEffect.ShowFailure, "the host refused");
                break;
            case LobbyEvent.LeavePressed:
                EndFriend(now, null, "left, as asked");
                break;
            case LobbyEvent.SessionEnded:
                EndFriend(now, LobbyEffect.ShowFailure, "the session ended");
                break;
            case LobbyEvent.WelcomedLobby when Friend is LobbyFriendState.Greeting or LobbyFriendState.Lost:
                Welcomed(now, playing: false);
                break;
            case LobbyEvent.WelcomedPlaying when Friend is LobbyFriendState.Greeting or LobbyFriendState.Lost:
                Welcomed(now, playing: true);
                break;
            case LobbyEvent.PlayReady when Friend == LobbyFriendState.Preparing:
                PlayIsReady(now);
                break;
            case LobbyEvent.PlayReady when Friend == LobbyFriendState.Lost && _resume == LobbyFriendState.Preparing:
                _resume = _hostBackAtLobby ? LobbyFriendState.Returning : LobbyFriendState.Playing;
                AckedEpoch = Epoch;
                break;
            case LobbyEvent.PlayFailed when Friend == LobbyFriendState.Preparing || (Friend == LobbyFriendState.Lost && _resume == LobbyFriendState.Preparing):
                EndFriend(now, LobbyEffect.ShowFailure, "couldn't get ready to follow");
                break;
            case LobbyEvent.OwnTitleBack when Friend == LobbyFriendState.Returning:
                EndPlay();
                _inputLockedUntil = now + LobbyTiming.InputLockMicros;
                BackFromGame = true;
                SetFriend(LobbyFriendState.InLobby, now, "our game is home");
                ShowLobby();
                _effects.Add(LobbyEffect.SendMember);
                break;
            case LobbyEvent.LinkLost when Friend is LobbyFriendState.Greeting or LobbyFriendState.InLobby or LobbyFriendState.Preparing or LobbyFriendState.Playing or LobbyFriendState.Returning:
                _resume = Friend;
                _rejoinEndsAt = now + (PlayActive ? LobbyTiming.RejoinInGameMicros : LobbyTiming.RejoinInLobbyMicros);
                SetFriend(LobbyFriendState.Lost, now, "the link to the host dropped");
                _effects.Add(LobbyEffect.ShowReconnecting);
                break;
            case LobbyEvent.LinkBack when Friend == LobbyFriendState.Lost:
                Reconnected(now, "the link to the host is back");
                break;
            case LobbyEvent.RejoinExpired when Friend == LobbyFriendState.Lost:
                EndFriend(now, LobbyEffect.SayLost, "gave up rejoining");
                break;
        }
    }

    private void OnBoard(LobbyEvent e, long now, uint epoch)
    {
        bool newEpoch = epoch != Epoch;
        Epoch = epoch;
        bool gameRuns = e is LobbyEvent.BoardStarting or LobbyEvent.BoardPlaying;
        if (gameRuns || e == LobbyEvent.BoardCounting)
        {
            BackFromGame = false;
        }

        switch (Friend)
        {
            case LobbyFriendState.InLobby when gameRuns:
                BeginPlay(now, "the board says go");
                break;
            case LobbyFriendState.Preparing:
                // Our BeginPlay is still running: PlayReady goes on from there, and nothing overtakes it.
                _hostBackAtLobby = !gameRuns;
                break;
            case LobbyFriendState.Playing when gameRuns:
                if (newEpoch)
                {
                    AckedEpoch = epoch; // a welcome's follow, then its board; or a board that skipped a back-out
                    AddOnce(LobbyEffect.SendMember);
                }

                break;
            case LobbyFriendState.Playing:
                SetFriend(LobbyFriendState.Returning, now, "the host is back at its lobby");
                AddOnce(LobbyEffect.SendMember);
                break;
            case LobbyFriendState.Returning when gameRuns:
                // Still following, and the host has started again: the same play period, no backup or handover.
                AckedEpoch = epoch;
                SetFriend(LobbyFriendState.Playing, now, "the host started again before we got home");
                AddOnce(LobbyEffect.SendMember);
                break;
        }
    }

    private void Welcomed(long now, bool playing)
    {
        if (Friend == LobbyFriendState.Lost)
        {
            Reconnected(now, "welcomed back");
        }

        if (playing)
        {
            switch (Friend)
            {
                case LobbyFriendState.Greeting or LobbyFriendState.InLobby:
                    BeginPlay(now, "welcomed to a game that's running");
                    break;
                case LobbyFriendState.Returning:
                    SetFriend(LobbyFriendState.Playing, now, "welcomed to a game that's running");
                    AddOnce(LobbyEffect.SendMember);
                    break;
            }

            return;
        }

        switch (Friend)
        {
            case LobbyFriendState.Greeting:
                SetFriend(LobbyFriendState.InLobby, now, "welcomed to the lobby");
                ShowLobby();
                AddOnce(LobbyEffect.SendMember);
                break;
            case LobbyFriendState.Preparing:
                _hostBackAtLobby = true;
                break;
            case LobbyFriendState.Playing:
                SetFriend(LobbyFriendState.Returning, now, "welcomed back, and the host is at its lobby");
                AddOnce(LobbyEffect.SendMember);
                break;
        }
    }

    private void Reconnected(long now, string why)
    {
        LobbyFriendState back = _resume;
        SetFriend(back, now, why);
        if (back == LobbyFriendState.InLobby)
        {
            ShowLobby();
        }

        if (back != LobbyFriendState.Greeting)
        {
            AddOnce(LobbyEffect.SendMember);
        }
    }

    private void BeginPlay(long now, string why)
    {
        PlayActive = true;
        _hostBackAtLobby = false;
        SetFriend(LobbyFriendState.Preparing, now, why);
        AddOnce(LobbyEffect.SendMember);
        _effects.Add(LobbyEffect.BeginPlay);
    }

    private void PlayIsReady(long now)
    {
        AckedEpoch = Epoch;
        if (_hostBackAtLobby)
        {
            _hostBackAtLobby = false;
            SetFriend(LobbyFriendState.Returning, now, "ready, but the host is back at its lobby");
            _effects.Add(LobbyEffect.SendMember);
            return;
        }

        SetFriend(LobbyFriendState.Playing, now, "ready to follow");
        _effects.Add(LobbyEffect.SendMember);
        if (_lobbyUp)
        {
            CloseLobbyForStart();
        }
        else
        {
            // Welcomed to a game that's running: no lobby screen showed, and the Join list it came from goes.
            _effects.Add(LobbyEffect.CloseJoinList);
        }
    }

    private void EndPlay()
    {
        if (PlayActive)
        {
            PlayActive = false;
            _effects.Add(LobbyEffect.EndPlay);
        }
    }

    /// <summary>Every way out of a friend's session: the device back first, then the screen, the session, the words, the list.</summary>
    private void EndFriend(long now, LobbyEffect? say, string why)
    {
        EndPlay();
        HideLobby();
        _effects.Add(LobbyEffect.EndSession);
        if (say is { } effect)
        {
            _effects.Add(effect);
        }

        _effects.Add(LobbyEffect.ShowJoinList);
        SetFriend(LobbyFriendState.Idle, now, why);
        Reset();
    }

    private void ShowLobby()
    {
        if (!_lobbyUp)
        {
            _lobbyUp = true;
            _effects.Add(LobbyEffect.ShowLobby);
        }
    }

    private void HideLobby()
    {
        if (_lobbyUp)
        {
            _lobbyUp = false;
            _effects.Add(LobbyEffect.HideLobby);
        }
    }

    private void CloseLobbyForStart()
    {
        if (_lobbyUp)
        {
            _lobbyUp = false;
            _effects.Add(LobbyEffect.CloseLobbyForStart);
        }
    }

    private void AddOnce(LobbyEffect effect)
    {
        if (!_effects.Contains(effect))
        {
            _effects.Add(effect);
        }
    }

    private void SetHost(LobbyHostState state, long now, string why)
    {
        NoteChange(Host.ToString(), state.ToString(), now, why);
        Host = state;
    }

    private void SetFriend(LobbyFriendState state, long now, string why)
    {
        NoteChange(Friend.ToString(), state.ToString(), now, why);
        Friend = state;
    }

    private void NoteChange(string from, string to, long now, string why)
    {
        if (_changes.Count >= MaxChanges)
        {
            _changes.Dequeue();
        }

        _changes.Enqueue(new LobbyFlowChange(from, to, Epoch, why, now));
    }

    /// <summary>The session is over: everything about it goes. (The log's changes stay until they're taken.)</summary>
    private void Reset()
    {
        _present.Clear();
        _awaiting.Clear();
        _acked.Clear();
        _arrivedAt.Clear();
        Epoch = 0;
        AckedEpoch = 0;
        PlayActive = false;
        IsWaitingForEveryone = false;
        IsImplicitStart = false;
        BackFromGame = false;
        LastWaitMicros = 0;
        LastAckAfterMicros = -1;
        LastAckFromArrival = false;
        _idleSince = long.MinValue;
        _inputLockedUntil = long.MinValue;
        _lobbyUp = false;
        _gameSinceBackup = false;
        _hostBackAtLobby = false;
        _resume = LobbyFriendState.Idle;
    }

    private LobbyEffect[] TakeEffects() => _effects.Count == 0 ? Array.Empty<LobbyEffect>() : _effects.ToArray();
}
