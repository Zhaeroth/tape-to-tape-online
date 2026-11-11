#if HAS_INTEROP
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using Il2CppBishopGames.Input;
using Il2CppControllerConnection;
using Il2CppInterop.Runtime;
using Il2CppRewired;
using T2TOnline.Core.Diagnostics;
using T2TOnline.Core.Input;
using T2TOnline.Core.Lobby;
using T2TOnline.Core.Protocol;
using T2TOnline.Core.Session;
using T2TOnline.Core.Stream;
using T2TOnline.Plugin.Diagnostics;
using T2TOnline.Plugin.Input;
using T2TOnline.Plugin.Match;
using T2TOnline.Plugin.Startup;
using T2TOnline.Plugin.Streaming;
using T2TOnline.Steam;
using T2TOnline.Steam.Native;
using UnityEngine;

namespace T2TOnline.Plugin.Online;

/// <summary>
/// Friends drive the host: online sessions over Steam. F7 joins a friend's Tape to Tape lobby if one exists (once
/// the host starts, your pad drives a virtual pad on their game, and your own game stops reacting to it: OnlineSession.Lobby.cs),
/// otherwise creates a friends-only lobby and hosts (every friend who joins gets a virtual pad on your game, and becomes a
/// game player at Start, or presses their join button in a game already running); F7 again leaves. The native rows, Steam's
/// invites and "Join Game" join the same way (OnlineSession.Steam.cs). Ctrl+F6 while hosting runs the one-PC test: your own
/// pad goes out over Steam to yourself as a friend, through the whole path a friend's pad takes. Ctrl+F5, as a friend, simulates a 15 s network drop.
/// </summary>
/// <remarks>
/// Pads are fed in Rewired's input-source update, the last moment before the game reads input: the friend's side
/// samples and sends there, the host's side drains the realtime channel and plays each friend's stream out there.
/// Everything is logged so a test session explains itself: joins, refusals and their versions, and every 5 s each
/// link's round trip, loss, playout delay and hold-time accuracy.
/// <para>
/// Drops are survivable, as on the couch. A friend's pad that dies or disconnects is looked for again and picked back
/// up when it returns; meanwhile their player stands still. A friend who drops out of the session keeps their virtual
/// pad (so their game player) parked on the host for <see cref="ParkFor"/> (and, mid-match, until the match is over), and
/// a friend whose host went silent rejoins by itself for as long, so they come back as the same player without pressing
/// anything. Anyone who falls out of the Steam lobby (host or friend) asks to be let back in, and the session carries on;
/// a host whose lobby is gone makes a new one. A Steam session that breaks under a running session (reliable messages on
/// it may be lost) is noticed (<see cref="SteamSessionWatch"/>): the friend says hello again and the host resends
/// everything a rejoining friend gets.
/// </para>
/// <para>
/// The lobby itself is <see cref="SteamLobbyLink"/>; the log reports, the net overlay's lines and the status box are built
/// in OnlineSession.Text.cs, and what the mod's panel reads and calls is in OnlineSession.Panel.cs.
/// </para>
/// </remarks>
internal sealed partial class OnlineSession
{
    private const int MaxFriends = 3;
    private const int MaxLobbyMembers = MaxFriends + 1;
    private const int HoldsLoggedIndividually = 40;
    private const string ModKey = LobbyDataKeys.Mod;
    private const string ProtocolKey = LobbyDataKeys.Protocol;
    private const string BuildKey = LobbyDataKeys.Build;
    // The host's Steam ID: friends say hello to it, not to the lobby's owner, which Steam hands to a friend when the host
    // falls out of the lobby with friends in it.
    private const string HostKey = LobbyDataKeys.Host;
    // How many play in the session (the host and the friends with a seat), for friends choosing a game in the panel.
    private const string PlayersKey = LobbyDataKeys.Players;
    private static readonly TimeSpan ReportInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MemberPollInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan StartRetryInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ParkFor = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan PadSearchInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan SimulatedDrop = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ReadyNoticeFor = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan StatusInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan RefusalSummaryInterval = TimeSpan.FromMinutes(1);
    // A first join the host never answers is tried again for this long after its first 20 s (the host may be loading),
    // then given up: nothing was joined, so there's no player to come back to.
    private static readonly TimeSpan FirstJoinRetryFor = TimeSpan.FromTicks(SessionTiming.FirstJoinRetryMicros * (TimeSpan.TicksPerMillisecond / 1000));
    // Host: a friend whose Steam session broke has this long to say hello again before they're asked to rejoin.
    private static readonly TimeSpan RelinkGrace = TimeSpan.FromSeconds(3);
    // Friend: a host who closed their side of the link and left the lobby, silent this long, closed their game.
    private static readonly TimeSpan HostGoneAfter = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RelayCheckInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan RelayWarnAfter = TimeSpan.FromSeconds(30);
    // The game shuts Steam down right after the mod's quit: the host's goodbyes get this long to leave.
    private static readonly TimeSpan GoodbyeWait = TimeSpan.FromMilliseconds(250);
    private const long PadSilenceMicros = 250_000;

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly SteamMessageReceiver _receiver = new();
    private readonly SessionIdentity _identity;
    private readonly string _buildHex;
    private readonly Dictionary<ulong, RemotePad> _pads = new();
    private readonly Dictionary<ulong, (RemotePad Pad, TimeSpan Until)> _parked = new();
    private readonly List<ulong> _expired = new();
    private readonly Dictionary<ulong, Refusals> _refusals = new();
    private readonly SteamLobbyLink _link = new(ModLog.SteamInfo, ModLog.SteamWarn);
    private readonly SteamSessionWatch _sessions = new();
    private readonly Dictionary<ulong, TimeSpan> _linkBrokeAt = new();
    private readonly SampleWindow _soloHoldErrorMs = new();
    private readonly SteamMessageHandler _hostRealtime;
    private readonly SteamMessageHandler _hostEvents;
    private readonly SteamMessageHandler _friendRealtime;
    private readonly SteamMessageHandler _friendEvents;
    private readonly SteamMessageHandler _friendBulk;
    private ulong _self;
    private bool _started;
    private bool _broken;
    private int _brokenFailures;
    private bool _gameClosing;
    private bool _modStopped;
    private string _steamOff = string.Empty;
    private SteamNetworkingAvailability? _relayAvailability;
    private bool _relayLocationLogged;
    private bool _relayWarned;
    private TimeSpan _nextRelayCheck;
    private string _undelivered = string.Empty;
    private int _playersPublished = -1;
    private TimeSpan _nextStartTry;
    private TimeSpan _nextMemberPoll;
    private TimeSpan _nextReport;
    private HostSession? _host;
    private FriendSession? _friend;
    private string _hostName = string.Empty;
    private ulong _hostSteamId;
    private long _friendLastPackets;
    private long _friendLastBytes;
    private TimeSpan _friendLastReport;
    private SteamSessionTransport? _friendTransport;

    // Reliable sends Steam refuses for now (its per-peer send buffer is full) are kept and resent in order: one
    // around the host's transport, one around each friend session's.
    private RetryingTransport? _hostTransport;
    private RetryingTransport? _friendRetrying;
    private LocalPad? _localPad;
    private bool _localPadWasConnected = true;
    private bool _lobbyInputSaid;
    private bool _padBack = true;
    private TimeSpan _nextPadSearch;
    private TimeSpan _rejoinUntil = TimeSpan.MinValue;
    private bool _everJoined;
    private bool _relinking;
    private TimeSpan _hostClosedLinkAt = TimeSpan.MinValue;
    private bool _hostMissing;
    private TimeSpan _dropUntil = TimeSpan.MinValue;
    private TimeSpan _lastMemberRefresh;
    private long _drainNow;
    private string? _notice;
    private TimeSpan _noticeUntil;
    private bool _wasFocused = true;
    private readonly List<string> _statusLines = new();
    private IReadOnlyList<string> _status = Array.Empty<string>();
    private TimeSpan _nextStatus;
    private bool _solo;
    private HoldMatcher? _soloHolds;
    private int _soloHoldsLogged;
    private Il2CppSystem.Action? _inputHook;
    private bool _faulted;
    private string? _simulated;
    private TimeSpan _startedAt = TimeSpan.MinValue;
    private int _graceReported;
    private readonly MatchRelay _relay;
    private readonly CampaignRelay _campaign;
    private readonly NetOverlay _net = new();
    private bool _netOverlayHinted;
    private readonly AchievementLedger _achievements = new();
    private readonly MomentInbox _momentInbox = new();
    private readonly SharedPlayFile _sharedPlay = new();
    private readonly AiStandIn _standIn = new();
    private MatchStreamClient? _streamClient;
    private StreamMessageHandler? _campaignFromHost;
    private long _steamToFrameClock = long.MinValue;

    /// <summary>Creates the session tools.</summary>
    /// <param name="modVersion">Our mod version, for the lobby and the handshake.</param>
    /// <param name="gameBuild">The first 8 bytes of GameAssembly.dll's SHA-256: friends must match it.</param>
    /// <param name="gameRoot">The game folder, to find steam_api64.dll.</param>
    /// <param name="relay">The match half of the session: the host's match stream, a friend's loading and playing it.</param>
    /// <param name="campaignModules">The campaign's parts: run sync, scenes, matches, menus, progress.</param>
    public OnlineSession(string modVersion, ulong gameBuild, string gameRoot, MatchRelay relay, IEnumerable<Campaign.ICampaignModule> campaignModules)
    {
        _relay = relay;
        _campaign = new CampaignRelay(campaignModules, Tag);
        // A friend's shared progress waits for a run's copy only while this holds: a lost link is a rejoin's hello.
        _campaign.LinkUpSource = () => _friend is { State: FriendSessionState.Joined } joined && !joined.Link.IsLagging;
        // A friend takes the host's progress only for the hosting session of its current welcome (D135).
        _campaign.FollowedSessionSource = () => _friend is { State: FriendSessionState.Joined } joined ? joined.Welcome.HostingSession : 0;
        _identity = new SessionIdentity(ProtocolInfo.Version, modVersion, gameBuild);
        _buildHex = gameBuild.ToString("x16", CultureInfo.InvariantCulture);
        SteamRuntime.ErrorText = ModLog.SafeErrorText; // a native failure in the Steam lines, cleaned as the log's own (D127)
        SteamRuntime.SetLibraryPath(Path.Combine(gameRoot, "Tape to Tape_Data", "Plugins", "x86_64", "steam_api64.dll"));
        // Made once: the channels are drained every frame (the host's pads every input update too), and a handler that
        // captured the time would be a new delegate each time. The time is _drainNow.
        _hostRealtime = (sender, _, payload) => HostReceive(sender, Channel.Realtime, payload, _drainNow);
        _hostEvents = (sender, _, payload) => HostReceive(sender, Channel.Events, payload, _drainNow);
        _friendRealtime = (sender, _, payload) => _friend?.Receive(sender, Channel.Realtime, payload, _drainNow);
        _friendEvents = (sender, _, payload) => _friend?.Receive(sender, Channel.Events, payload, _drainNow);
        _friendBulk = (sender, _, payload) => _friend?.Receive(sender, Channel.Bulk, payload, _drainNow);
        // Recorded whenever hosting, so a friend who's dropped at that moment still gets it when they're back.
        AchievementMirror.Sharing = () => ModSettings.ShareAchievements && !_broken && _host != null;
        // A friend's own achievements (its progress, its unlocks) are logged as its game unlocks them.
        AchievementMirror.Following = () => !_broken && _friend != null;
        // Every friend in the session has a pad in _pads (a dropped friend's is parked); asked at every punch's apex. A
        // punch waits only with the FightGraceWindow setting on too; its apexes go to the brawl's line either way
        // (OnlineSession.Brawl.cs).
        FightGrace.Hosting = () => !_broken && _host != null && _pads.Count > 0;
        FightGrace.WindowFor = GraceWindow;
        FightGrace.Met = BrawlMet;
        FightGrace.WaitEnded = BrawlWaitEnded;
        // The host's run history page at the title: the custom logo it wears goes to friends (Campaign/Menus/RunHistoryPages.cs).
        Campaign.Menus.RunHistoryPages.ShareLogo = _relay.ShareHistoryLogo;
        // The controller screen's panel names each friend's player (Campaign/Menus/MenuPayloads.cs).
        Campaign.Menus.MenuPayloads.PadOwnerOf = PadOwnerOf;
        Campaign.Menus.ExhibitionScreen.ShareContent = _relay.ShareScreenContent;
        // Friends' virtual controller maps are checked again a second after every scene load (OnlineSession.Maps.cs).
        InstallMapsGuard();
        _relay.HoldFriendLoads = () => !DeviceWithHost; // a friend in the lobby loads nothing of the host's (OnlineSession.Lobby.cs)
        _relay.HostBackAtLobby = () => _flow.Friend == LobbyFriendState.Returning;
        InstallNoticeSources(); // what the campaign and match parts tell the player (OnlineSession.Notices.cs)
        InstallLobbyFlow();
        InstallOwnUnlocks(); // a friend's own new-unlocks screen lends their device (OnlineSession.OwnUnlocks.cs)
        InstallLogTags(); // the log names players by tags, never by name or ID (OnlineSession.LogTags.cs)
        InstallTeamSeats(); // every lobby player on a campaign's team at its start (OnlineSession.TeamSeats.cs, D145)
    }

    /// <summary>True while this PC streams a match to friends.</summary>
    public bool HostStreaming => _relay.HostStreaming;

    /// <summary>True while this PC loads or plays a host's match.</summary>
    public bool FriendBusy => _relay.FriendBusy;

    /// <summary>True while this PC's own pad is in use by the session (as a friend, or in the one-PC test).</summary>
    public bool UsesLocalPad => _localPad != null;

    /// <summary>
    /// What the on-screen status box shows, refreshed four times a second; empty when there's nothing to say. A new list
    /// whenever the lines change and the same one while they don't, so the overlay keeps its layout in between.
    /// </summary>
    public IReadOnlyList<string> Status => _status;

    /// <summary>The net overlay's lines (empty while it's off).</summary>
    public IReadOnlyList<string> NetLines => _net.Lines;

    /// <summary>
    /// F9: the net overlay on or off (not after an error: there's nothing left to measure). A developer's tool: with the ShowStatusBox setting off it does nothing but say so,
    /// once, in the log.
    /// </summary>
    public void ToggleNetOverlay()
    {
        if (_broken)
        {
            ModLog.Warn("Net overlay (F9): online is off after an error, so there's nothing to show; restart the game to play online.");
            return;
        }

        if (!ModSettings.ShowStatusBox)
        {
            if (!_netOverlayHinted)
            {
                _netOverlayHinted = true;
                ModLog.Info("Net overlay (F9): it's a developer's tool and it's off (ShowStatusBox = true in the [T2TOnline] settings turns it on). Nothing was done.");
            }

            return;
        }

        Guarded("F9", _net.Toggle);
    }

    private long Now => _clock.Elapsed.Ticks / (TimeSpan.TicksPerMillisecond / 1000);

    private bool InLobby => _link.Active;

    /// <summary>F7: join a friend's lobby or host one; leave if already in one.</summary>
    public void Toggle() => Guarded("F7", ToggleCore);

    /// <summary>
    /// Shift+F7, and the panel's Host: host now, even when friends have games open. Not while in a session (F7 leaves it
    /// first); refused like F7 while online is off.
    /// </summary>
    public void HostGame() => Guarded("Host", HostGameCore);

    /// <summary>
    /// The panel's Join: join the friend's game in lobby <paramref name="lobbyId"/>, one of <see cref="JoinableGames"/>.
    /// Not while in a session (F7 leaves it first); refused like F7 while online is off, and for a game the list says
    /// can't be joined (another T2T Online version or game update).
    /// </summary>
    public void JoinGame(ulong lobbyId) => Guarded("Join", () => JoinGameCore(lobbyId));

    /// <summary>
    /// The friends' games for the native Join list's refresh and the developer's panel: looks now (Steam's friends list), then reads each game's T2T Online details as
    /// Steam sends them, in a second or two (<see cref="JoinableGames"/> fills in; given up after
    /// <see cref="SteamLobbyBrowser.NoAnswerAfter"/>), and keeps looking for <see cref="SteamLobbyBrowser.Linger"/>.
    /// </summary>
    public void FindGames() => Guarded("Finding friends' games", FindGamesCore);

    /// <summary>Ctrl+F6: start or stop the one-PC test while hosting.</summary>
    public void ToggleSolo() => Guarded(SoloKey, ToggleSoloCore);

    /// <summary>Ctrl+F5: as a friend, cut our link for <see cref="SimulatedDrop"/>, to test dropping out and coming back.</summary>
    public void SimulateDrop() => Guarded(DropKey, SimulateDropCore);

    private void ToggleCore()
    {
        // Leaving always works, even after online switched off mid-session (Ready refuses only new sessions).
        if (InLobby)
        {
            bool hosting = _host != null;
            LeaveAll("F7 pressed");
            Notify(NoticeId.SessionLeft, hosting ? "Session ended." : $"Left the online session. {PadBackNotice()}");
            NotifyPadBack();
            return;
        }

        if (!Ready())
        {
            return;
        }

        IReadOnlyList<FriendLobby> lobbies = SteamFriendsLobbies.FindFriendLobbies(SteamRuntime.AppId);
        if (lobbies.Count > 0)
        {
            FriendLobby target = lobbies[0];
            string host = SafeText.Clean(target.PersonaName);
            if (Campaign.RunSync.BlocksJoining(out string ownRun))
            {
                // This player's own campaign run is in play: joining would replace it. F7 doesn't host instead.
                ModLog.Warn($"Online: not joining {Tag(target.FriendSteamId)}'s game: {ownRun}");
                Notify(NoticeId.OwnRunInPlay, ownRun);
                return;
            }

            // Friends in the same lobby are one game; only other lobbies are other games this F7 didn't pick.
            string others = string.Join(", ", lobbies.Where(l => l.LobbySteamId != target.LobbySteamId).GroupBy(l => l.LobbySteamId)
                .Select(g => SafeText.Clean(g.First().PersonaName)));
            int otherLobbies = lobbies.Select(l => l.LobbySteamId).Distinct().Count() - 1;
            ModLog.Info($"Online: {lobbies.Count} friend lobby(ies) found; joining {Tag(target.FriendSteamId)}'s ({Tags.Lobby(target.LobbySteamId)})" +
                $"{(otherLobbies > 0 ? $"; friends are in {otherLobbies} other lobby(ies) too" : string.Empty)}.");
            if (others.Length > 0)
            {
                NotifyText(NoticeId.JoiningNote, $"Joining {host}'s game. Friends in other games: {others}.",
                    $"Joining {Tag(target.FriendSteamId)}'s game. Friends are in {otherLobbies} other game(s).");
            }

            BeginJoin(target.LobbySteamId, host, LobbyRoute.Key);
            return;
        }

        // A host whose Steam profile hides game details is invisible here: the commonest reason a
        // friend ends up hosting by mistake.
        ModLog.Info("Online: no friend is in a Tape to Tape lobby; creating a friends-only lobby to host (a host whose Steam \"Game details\" aren't open to friends can't be seen).");
        Notify(NoticeId.HostingOwn);
        BeginHost(LobbyRoute.Key);
    }

    private void HostGameCore() => HostGameCore(LobbyRoute.Key);

    private void HostGameCore(LobbyRoute route)
    {
        if (InLobby)
        {
            ModLog.Warn($"Online (host): already in a session; {LeaveWay} leaves it first.");
            Notify(NoticeId.AlreadyInSession, $"You're already in a session; {LeaveWay} leaves it.");
            return;
        }

        if (!Ready())
        {
            return;
        }

        ModLog.Info("Online: hosting, as asked (whatever friends have open); creating a friends-only lobby.");
        BeginHost(route);
    }

    private void JoinGameCore(ulong lobbyId) => JoinGameCore(lobbyId, LobbyRoute.Panel);

    private void JoinGameCore(ulong lobbyId, LobbyRoute route)
    {
        if (InLobby)
        {
            ModLog.Warn($"Online (join): already in a session; {LeaveWay} leaves it first.");
            Notify(NoticeId.AlreadyInSession, $"You're already in a session; {LeaveWay} leaves it.");
            return;
        }

        if (!Ready())
        {
            return;
        }

        // Only a game from the friends list: the panel never joins a lobby id from anywhere else.
        JoinableGame? game = _games.FirstOrDefault(g => g.LobbyId == lobbyId);
        if (game == null)
        {
            ModLog.Warn($"Online (join): lobby {Tags.Lobby(lobbyId)} isn't one of the friends' games found; nothing done.");
            Notify(NoticeId.ListChanged, "That game isn't in the list any more; look again.");
            return;
        }

        if (!game.Joinable)
        {
            ModLog.Warn($"Online (join): not joining {GameHostTag(game)}'s game (lobby {Tags.Lobby(lobbyId)}): {game.Note}.");
            NotifyText(NoticeId.ListChanged, $"Can't join {game.HostName}: {game.Note}.", $"Can't join {GameHostTag(game)}: {game.Note}.");
            return;
        }

        if (Campaign.RunSync.BlocksJoining(out string ownRun))
        {
            ModLog.Warn($"Online (join): not joining {GameHostTag(game)}'s game (lobby {Tags.Lobby(lobbyId)}): {ownRun}");
            Notify(NoticeId.OwnRunInPlay, ownRun);
            return;
        }

        ModLog.Info($"Online: joining {GameHostTag(game)}'s game (lobby {Tags.Lobby(lobbyId)}, T2T Online {(game.ModVersion.Length > 0 ? game.ModVersion : "(none)")}), picked in {(route == LobbyRoute.Browser ? "the Join list" : "the panel")}.");
        BeginJoin(lobbyId, game.HostName, route);
    }

    private void ToggleSoloCore()
    {
        if (!Ready())
        {
            return;
        }

        if (_friend != null)
        {
            if (_solo)
            {
                _friend.Leave();
                ModLog.Info($"Online one-PC test: stopping ({SoloKey} pressed).");
            }
            else
            {
                ModLog.Warn($"Online: {SoloKey} is for the one-PC test while hosting; you're in a friend's session (F7 leaves it).");
            }

            return;
        }

        if (_host == null || _link.Id == 0)
        {
            ModLog.Warn($"Online: {SoloKey} is the one-PC test. Press Shift+F7 first to host (it creates a lobby), then {SoloKey}.");
            return;
        }

        StartFriend(_self, "you", solo: true);
    }

    private void SimulateDropCore()
    {
        if (!Ready())
        {
            return;
        }

        if (_friend == null || _friendTransport == null)
        {
            ModLog.Warn($"Online ({DropKey}): simulating a network drop needs a friend session: join one (F7), or run the one-PC test ({SoloKey}).");
            return;
        }

        _friendTransport.Cut = true;
        _dropUntil = _clock.Elapsed + SimulatedDrop;
        ModLog.Info($"Online ({DropKey}): simulating a {SimulatedDrop.TotalSeconds:0} s network drop: nothing goes out or comes in on our side. " +
            $"Expect lagging at {SessionTiming.LaggingAfterMicros / 1_000_000} s, both sides dropping each other at {SessionTiming.GoneAfterMicros / 1_000_000} s, " +
            "then us rejoining by ourselves as the same player once the link is back.");
    }

    /// <summary>Runs a key's action; a failure turns online off for this session rather than leaving anything half-done.</summary>
    private void Guarded(string key, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Break($"Online ({key}) failed; online is off until the game restarts", ex);
        }
    }

    /// <summary>An error: online is off for the rest of this session, everything is left, and the status box says so.</summary>
    private void Break(string message, Exception? ex)
    {
        _broken = true;
        ModLog.Error(message, ex);
        _failure ??= LobbyFailure.OnlineBroken;
        TryLeaveAll("an error");
        _net.Hide();
        _nextStatus = TimeSpan.Zero;
    }

    /// <summary>Call once per frame.</summary>
    public void Update()
    {
        TimeSpan elapsed = _clock.Elapsed;
        TickNotices(elapsed);
        if (!_started)
        {
            TryStart(elapsed);
            return;
        }

        if (_self == 0)
        {
            RefreshStatus(elapsed); // the box is what says Steam is off when the screens can't, and it says nothing it isn't asked to build
            return;
        }

        if (_broken)
        {
            UpdateAfterError();
            return;
        }

        try
        {
            if (!SteamRuntime.BindingsReady)
            {
                // A native call failed, so every Steam call returns its failure value from now on: nothing would leave or
                // arrive, and the session would only decay into timeouts. Said once, with Steam's failure.
                Break($"Steam: {SteamRuntime.Status}; online is off until the game restarts", null);
                Notify(NoticeId.OnlineBroken);
                return;
            }

            if (_faulted)
            {
                _faulted = false;
                LeaveAll("an error in the input path");
                Notify(NoticeId.PadsFailed);
                return;
            }

            if (LeaveDirectLink())
            {
                return; // D133: the link to the host went direct (OnlineSession.Privacy.cs)
            }

            if (!T2TOnlinePlugin.OnlineAvailable && InLobby)
            {
                // A later start-up check switched online off (crash reporting found running): the session ends here.
                ModLog.Warn("Online: online was switched off during the session (see the log above); leaving.");
                LeaveAll("online switched off");
                Notify(NoticeId.OnlineSwitchedOff);
                return;
            }

            long now = Now;
            PollIceProbe(); // DevTools only: Steam's own ICE line for the session's first connection (OnlineSession.IceProbe.cs)
            ReportRelay(elapsed);
            _link.PollAbandoned();
            PollLobby(elapsed);
            PollLobbyRejoin(elapsed);
            EndSimulatedDrop(elapsed);
            WatchLocalPad(elapsed);
            ExpireParked(elapsed);
            if (_link.Id != 0 && elapsed >= _nextMemberPoll)
            {
                _nextMemberPoll = elapsed + MemberPollInterval;
                PollMembers();
            }

            if (_host != null)
            {
                _host.WelcomePhase = _flow.WelcomePhase;
            }

            SyncTourAdmission(); // a check tour lets in only its one-PC stand-in (OnlineSession.TourHolds.cs)

            Drain(now);
            FlushRetries();
            _host?.Update(now);
            _friend?.Update(now);
            HandleHostEvents(now);
            long costStart = InputCostStart();
            GuardFriendMaps(elapsed);
            InputCostAdd(_mapsGuardCost, costStart);
            UpdateStandIns(now);
            UpdateBrawl(elapsed, now); // a brawl's start and end, for its log lines (OnlineSession.Brawl.cs)
            costStart = InputCostStart();
            NoteFriendActions();
            InputCostAdd(_actionsCost, costStart);
            _standIn.RestorePending(); // hosting or not: skaters left while the game's own sequence held them
            HandleFriendEvents();
            UpdateLobbyFlow(now, elapsed); // a session's whole life, on both ends (OnlineSession.Lobby.cs)
            UpdateOwnMenu(now); // a friend's own remapper or settings (OnlineSession.Remap.cs)
            UpdateMySettings(); // a friend's My Settings on the host's pause menu (OnlineSession.MySettings.cs)
            UpdateFollowQuestion(now); // a friend whose game couldn't load the host's match is asked what to do (OnlineSession.FollowQuestion.cs)
            UpdateLanguage(now); // the game's language, followed (OnlineSession.Language.cs)
            OwnName.Tick(); // this PC's own Steam name for the log's cleaner: now, then every 30 s (Diagnostics/OwnName.cs)
            WatchSessions(elapsed);
            CheckHostClosed(elapsed);
            RelayJoins();
            _relay.Update();
            _campaign.Update(now);
            UpdateAchievements();
            PublishPlayers();
            PollGames(elapsed);
            NativeLobby.AvatarCache.Update();
            PollSteamJoins(elapsed);
            SayNewInvites(elapsed);
            UpdatePresence();
            SyncPadDevice();
            UpdatePromptNames(elapsed);
            ReportInput();
            HideCursorAfterPadPress(elapsed);
            UpdateFocus();
            RefreshStatus(elapsed);
            if (_net.Due(elapsed))
            {
                try
                {
                    BuildNet(elapsed);
                }
                catch (Exception ex)
                {
                    // The overlay is a diagnostic: it goes off, the session carries on.
                    _net.Toggle();
                    ModLog.Warn($"Net overlay: failed ({ModLog.SafeErrorText(ex)}); it's off (F9 to try again).");
                }
            }

            if (elapsed >= _nextReport)
            {
                _nextReport = elapsed + ReportInterval;
                Report();
            }
        }
        catch (Exception ex)
        {
            // Never into the game: online is off for the rest of this session; the game carries on.
            Break("Online: the session failed and online is off until the game restarts", ex);
        }
    }

    /// <summary>
    /// Ends everything. <paramref name="removeGamePlayers"/>: friends' game players go too (after an error, so no player is
    /// left without a controller); when the game is closing they stay, as the game is going anyway, and a friend's game
    /// doesn't leave the host's match for the menu either.
    /// </summary>
    public void Shutdown(bool removeGamePlayers = false)
    {
        _gameClosing = !removeGamePlayers;
        if (removeGamePlayers)
        {
            // The mod stopped after an error: only UpdateAfterError runs from now on, and the status box says so.
            _broken = true;
            _modStopped = true;
            _net.Hide();
            _nextStatus = TimeSpan.Zero;
        }

        TryLeaveAll(removeGamePlayers ? "an error" : "game closing", removeGamePlayers);
        Step("clearing Steam's rich presence", () => ShowPresence(null));
        // Nothing updates the campaign after this: a session's end waiting for a scene load is put away now.
        Step("putting the host's campaign away", () => _campaign.StopFollowing("the mod stopped", _gameClosing, waitForLoad: false));
    }

    /// <summary>
    /// Call every frame once online is off after an error, the session's own or the mod's (after <see cref="Shutdown"/>
    /// with removeGamePlayers): lobbies created or joined just before it still need leaving, skaters the AI played still
    /// need their flags once the game's own sequence lets go of them, a match whose loading barrier let go under the pause
    /// menu still needs its time back once the menu closes, and a host match that was loading here is left once it's in.
    /// The status box says online is off.
    /// </summary>
    public void UpdateAfterError()
    {
        BrokenStep("finishing abandoned lobby calls", _link.PollAbandoned);
        BrokenStep("putting skaters back", _standIn.RestorePending);
        BrokenStep("the match relay's leftovers", _relay.UpdateAfterError);
        BrokenStep("the campaign's leftovers", _campaign.UpdateAfterError);
        BrokenStep("giving the keyboard and mouse back", () => FocusGuard.Update(Application.isFocused));
        BrokenStep("clearing Steam's rich presence", () => ShowPresence(null));
        BrokenStep("answering joins from Steam", () => RefuseSteamJoins(_clock.Elapsed));
        RefreshStatus(_clock.Elapsed);
    }

    private bool Ready()
    {
        if (Campaign.SaveBackups.BlocksSessions(out string restoreWaits))
        {
            // A restored backup waits for the game to restart; nothing would be saved in a session.
            ModLog.Warn($"Online: {restoreWaits}");
            Notify(NoticeId.RestoreBlocksSession);
            return false;
        }

        if (!T2TOnlinePlugin.OnlineAvailable)
        {
            ModLog.Warn("Online: off this session (see the start-up lines: an untested game build, a failed check, or crash reporting that may be live).");
            Notify(T2TOnlinePlugin.GameNotTested ? NoticeId.OnlineOff : NoticeId.OnlineBroken);
            return false;
        }

        if (_broken)
        {
            ModLog.Warn("Online: off after an error this session; restart the game to try again.");
            Notify(NoticeId.OnlineBroken);
            return false;
        }

        if (_self == 0)
        {
            ModLog.Warn(_started ? $"Online: {_steamOff}; nothing to do." : "Online: still waiting for Steam to start; try again in a few seconds.");
            Notify(_started ? SteamOffNotice : NoticeId.SteamWaiting);
            return false;
        }

        return true;
    }

    /// <summary>Waits for the game to start Steam (up to 30 s), then reports its state and starts relay access.</summary>
    private void TryStart(TimeSpan elapsed)
    {
        if (elapsed < _nextStartTry)
        {
            return;
        }

        _nextStartTry = elapsed + StartRetryInterval;
        if (SteamRuntime.LocalSteamId == 0 && elapsed < StartTimeout)
        {
            return;
        }

        _started = true;
        _startedAt = elapsed;
        ModLog.Info($"Steam: {SteamRuntime.Status}.");
        bool layoutOk = SteamRuntime.VerifyIdentityLayout(out string identity);
        _self = layoutOk ? SteamRuntime.LocalSteamId : 0;
        _sessions.Loopback = _self; // the one-PC test's messages to ourselves (SteamSessionWatch)
        if (!layoutOk)
        {
            ModLog.Warn("Steam: the identity layout doesn't match this Steam build; every message would be misaddressed, so online is off.");
        }

        ModLog.Info($"Steam: {identity}. {(_self != 0 ? "Signed in" : "Not signed in")} (names and Steam IDs never go in this log), app {SteamRuntime.AppId}.");
        if (_self == 0)
        {
            _steamOff = !SteamRuntime.BindingsReady ? "T2T Online can't use this Steam (see the log)"
                : !layoutOk ? "This Steam's networking doesn't match T2T Online (see the log)"
                : "Steam isn't running or isn't signed in";
            ModLog.Warn($"Steam: not available ({_steamOff}); online play won't work this session.");
            return;
        }

        ReportFriendCheck();
        bool relay = SteamNetworking.InitRelayNetworkAccess();
        if (!ApplyIce())
        {
            return; // relay only that Steam won't keep: online is off this session (OnlineSession.Privacy.cs, D133)
        }

        ApplySimulatedNetwork();
        // What Steam does with a session that goes quiet: past TimeoutConnected it breaks (and the session watch notices).
        ModLog.Info($"Steam: relay network access {(relay ? "requested" : "could not be requested")}; " +
            $"sessions time out after {GlobalConfig(SteamNetworkingConfigValue.TimeoutConnected)} ms connected, " +
            $"{GlobalConfig(SteamNetworkingConfigValue.TimeoutInitial)} ms connecting. {(ModSettings.DevTools ? "F7" : "Online on the title screen")}: join a friend's lobby or host one. Protocol {ProtocolInfo.Version}, game build {_buildHex}.");
        StartSteamJoins(elapsed);
    }

    /// <summary>A global Steam networking setting as it's in effect, for the log ("unreadable" if Steam won't say).</summary>
    private static string GlobalConfig(SteamNetworkingConfigValue value) =>
        SteamNetworking.TryGetGlobalConfig(value, out int data) ? data.ToString(CultureInfo.InvariantCulture) : "unreadable";

    /// <summary>
    /// Host: the AI plays each friend's skater while they're away (<see cref="AiStandIn"/>): dropped (parked), silent past
    /// the quarter second after which their pad goes neutral, or with their pad unplugged; and at once while their game says it
    /// couldn't load the match (<see cref="StreamFriend.LoadFailure"/>, cleared when they load one, rejoin, or the match ends):
    /// their pad would otherwise drive a skater they can't see.
    /// </summary>
    private void UpdateStandIns(long now)
    {
        if (_host == null)
        {
            return;
        }

        // In the lobby no device is expected from anyone (a friend's stream starts at BeginPlay): the AI stands in for nobody until a game runs.
        bool enabled = ModSettings.AiStandIn && _flow.Host is LobbyHostState.GameFlow or LobbyHostState.InGame;
        foreach ((ulong steamId, RemotePad pad) in _pads)
        {
            RemoteFriend? friend = _host.Find(steamId);
            bool blind = enabled && _relay.StreamTo(steamId) is { LoadFailure.Length: > 0 };
            bool away = enabled && (blind || friend is not { IsJoined: true } || !friend.PadConnected || now - friend.Link.LastHeardMicros > PadSilenceMicros);
            _standIn.Track(pad.Pad.RewiredPlayerId, pad.Tag, away, StandInPlayer(pad, away), now, blind);
        }

        foreach ((RemotePad pad, _) in _parked.Values)
        {
            _standIn.Track(pad.Pad.RewiredPlayerId, pad.Tag, away: enabled, StandInPlayer(pad, enabled), now);
        }

        _standIn.Sweep();
    }

    /// <summary>
    /// A friend's game player for <see cref="AiStandIn.Track"/>, asked of the game only when it's read: while they're away, or while the AI plays
    /// for them (it follows their skater). Otherwise null: two interop calls a friend a frame, all session, for nothing.
    /// </summary>
    private Il2CppTape2Tape.Player? StandInPlayer(RemotePad pad, bool away) =>
        away || _standIn.IsStandingIn(pad.Pad.RewiredPlayerId) ? Il2Cpp.PlayersManager.Instance?.GetPlayerFromRewired(pad.Pad.Player) : null;

    /// <summary>
    /// Host: the fight grace window for the friend on Rewired player <paramref name="rewiredId"/>, or -1 if it isn't a
    /// friend's: how late they see a punch coming (half the round trip plus how far behind the stream they render) plus how
    /// late their block arrives (the other half plus the pad playout). <see cref="FightGrace"/> caps it.
    /// </summary>
    private long GraceWindow(int rewiredId)
    {
        if (_host == null || rewiredId < 0)
        {
            return -1;
        }

        foreach ((ulong steamId, RemotePad pad) in _pads)
        {
            if (pad.Pad.RewiredPlayerId != rewiredId || _host.Find(steamId) is not { IsJoined: true } friend)
            {
                continue;
            }

            // A friend who can't press anything right now (silent, unplugged, or the AI plays for them) gets no wait.
            if (friend.Link.IsLagging || !friend.PadConnected || Now - friend.Link.LastHeardMicros > PadSilenceMicros || _standIn.IsStandingIn(rewiredId))
            {
                return -1;
            }

            long roundTrip = friend.Link.LastRoundTripMicros >= 0 ? friend.Link.LastRoundTripMicros : 60_000;
            long render = _relay.StreamTo(steamId) is { ReportMicros: not long.MinValue } stream && Now - stream.ReportMicros < 3_000_000
                ? stream.Report.RenderBehindMicros
                : 30_000;
            long playout = (friend.Received?.BufferedFrames ?? 2) * 1_000_000L / PadInputSender.FrameRate;
            return roundTrip + render + playout;
        }

        return -1;
    }

    /// <summary>
    /// Host: the friend whose virtual pad is game player <paramref name="gamePlayer"/> (a dropped friend's kept player too),
    /// or null for the host's own players and nobody's.
    /// </summary>
    private Campaign.Menus.PadOwner? PadOwnerOf(int gamePlayer)
    {
        if (_host == null || gamePlayer < 0)
        {
            return null;
        }

        foreach ((ulong steamId, RemotePad pad) in _pads)
        {
            if (pad.GamePlayer == gamePlayer)
            {
                return new Campaign.Menus.PadOwner(steamId, pad.DisplayName);
            }
        }

        foreach ((ulong steamId, (RemotePad pad, _)) in _parked)
        {
            if (pad.GamePlayer == gamePlayer)
            {
                return new Campaign.Menus.PadOwner(steamId, $"{pad.DisplayName} (dropped)");
            }
        }

        return null;
    }

    /// <summary>
    /// Shared moments (D34, D48), every frame. Host: which match each friend watches, and the moments the game earned since
    /// the last frame go to the friends who shared their play. Friend: moments that waited for this PC to share the host's
    /// play are unlocked once it has.
    /// </summary>
    private void UpdateAchievements()
    {
        NoteMatchWatchers();
        SendEarnedAchievements();
        UnlockWaitingAchievements();
    }

    /// <summary>Host: the friends watching the stream of the host's match now (a match moment is theirs).</summary>
    private void NoteMatchWatchers()
    {
        if (_host == null)
        {
            return;
        }

        int match = MatchSetup.LoadedSerial;
        foreach (ulong steamId in _pads.Keys)
        {
            if (_relay.StreamTo(steamId) is { Streaming: true })
            {
                _achievements.Watching(steamId, match);
            }
        }
    }

    /// <summary>Host: the shared moments the game earned since the last frame go out (queued by the achievement patch).</summary>
    private void SendEarnedAchievements()
    {
        while (AchievementMirror.TryTakeEarned(out EarnedMoment moment))
        {
            try
            {
                HostEarned(moment);
            }
            catch (Exception ex)
            {
                ModLog.Warn($"Achievements: couldn't send {moment.Name}: {ModLog.SafeErrorText(ex)}");
            }
        }
    }

    /// <summary>
    /// Host: this PC's game earned a shared moment while hosting: it goes to each friend in the session who shared the play
    /// it came from and hasn't had it for that match (one who dropped gets it when they're back; earned again in a later
    /// match a friend watches, it goes to them again for that match: their game may have refused the earlier one). Earned
    /// as hosting stopped, it goes nowhere, and the log says so.
    /// </summary>
    private void HostEarned(in EarnedMoment moment)
    {
        string achievement = moment.Name;
        if (_host == null)
        {
            ModLog.Info($"Achievements: {achievement} was earned as the session ended; not sent.");
            return;
        }

        MomentSource source = AchievementMirror.SourceOf(achievement);
        MomentShares shares = _achievements.Record(achievement, source, moment.Match, moment.RunShared, _pads.Keys.Concat(_parked.Keys));
        if (!shares.FirstTime && shares.NewlyOwed.Count == 0)
        {
            return; // earned again in a match the same friends were sent it for: nothing new to send
        }

        var sentTo = new List<string>();
        var later = new List<string>();
        var sent = new AchievementMoment(achievement, Math.Max(0, moment.Match), source == MomentSource.Run);
        foreach (ulong steamId in shares.NewlyOwed)
        {
            if (_host.Find(steamId) is { IsJoined: true } && _host.SendAchievement(steamId, sent))
            {
                sentTo.Add(FriendLabel(steamId));
            }
            else
            {
                later.Add(FriendLabel(steamId));
            }
        }

        string notShared = shares.NotShared.Count == 0 ? string.Empty
            : $"; not for {string.Join(", ", shares.NotShared.Select(FriendLabel))} " +
              (source == MomentSource.Run ? "(the host's run isn't the shared co-op run)" : "(they didn't watch that match)");
        ModLog.Info($"Achievements: {achievement} earned in this session; sent to {(sentTo.Count == 0 ? "nobody now" : string.Join(", ", sentTo))}" +
            $"{(later.Count == 0 ? string.Empty : $"; {string.Join(", ", later)} get it when they're back")}{notShared}.");
    }

    /// <summary>How the achievement lines name a friend in the session.</summary>
    private string FriendLabel(ulong steamId) =>
        _pads.TryGetValue(steamId, out RemotePad? pad) ? pad.Tag
        : _parked.TryGetValue(steamId, out (RemotePad Pad, TimeSpan Until) parked) ? parked.Pad.Tag
        : Tag(steamId);

    /// <summary>
    /// Host: a friend joined or came back: every shared moment they're owed this session (earned in play they shared), in
    /// case they missed any. Their game unlocks each only for play it saw (<see cref="MomentInbox"/>): a match it played, the
    /// run it held.
    /// </summary>
    private void SendMissedAchievements(ulong steamId, string tag)
    {
        if (_host == null || !ModSettings.ShareAchievements)
        {
            return;
        }

        // Each as it was owed: with its match and whether it's the run's, so their game checks it against its own play (D135).
        IReadOnlyList<AchievementMoment> owed = _achievements.OwedTo(steamId);
        int sent = owed.Count(moment => _host.SendAchievement(steamId, moment));
        if (owed.Count > 0)
        {
            ModLog.Info($"Achievements: sent {tag} the {sent} of play they shared this session ({string.Join(", ", owed.Select(MomentWords))}).");
        }
    }

    /// <summary>
    /// The settings' simulated network (Steam's own fake lag, jitter and loss on everything this PC sends), for testing
    /// at a bad ping on one PC. Off by default; when on, the log and the status box say so.
    /// </summary>
    private void ApplySimulatedNetwork()
    {
        int lag = Math.Clamp(ModSettings.SimulatedLagMs, 0, 300);
        int jitter = Math.Clamp(ModSettings.SimulatedJitterMs, 0, 100);
        int loss = Math.Clamp(ModSettings.SimulatedLossPercent, 0, 20);
        if (lag == 0 && jitter == 0 && loss == 0)
        {
            return;
        }

        int refused = SteamNetworking.ApplyFakeConditions(new SteamFakeNetworkConditions
        {
            LagSendMs = lag,
            LossSendPercent = loss,
            JitterSendAvgMs = jitter,
            JitterSendMaxMs = jitter * 4,
            JitterSendPercent = jitter > 0 ? 100f : 0f,
        });
        _simulated = $"SIMULATED NETWORK: +{lag} ms, ~{jitter} ms jitter, {loss}% loss on everything this PC sends";
        ModLog.Warn($"Online: {_simulated}{(refused == 0 ? string.Empty : $" (Steam refused {refused} of the settings)")} (UserData/T2TOnline/settings.cfg, Simulated*; set to 0 to play normally).");
    }

    /// <summary>
    /// The relay network's state, checked once a second: every change is logged (a network stuck waiting or retrying
    /// shows too), the ping location once it's settled, and a warning once if it isn't up 30 s after start-up (friends
    /// whose PCs can't reach each other directly then can't connect at all).
    /// </summary>
    private void ReportRelay(TimeSpan elapsed)
    {
        if (elapsed < _nextRelayCheck)
        {
            return;
        }

        _nextRelayCheck = elapsed + RelayCheckInterval;
        SteamNetworkingAvailability availability = SteamNetworking.GetRelayNetworkStatus(out SteamRelayNetworkStatus status);
        if (availability != _relayAvailability)
        {
            _relayAvailability = availability;
            bool settled = availability is SteamNetworkingAvailability.Current or SteamNetworkingAvailability.Failed or SteamNetworkingAvailability.CannotTry;
            string location = string.Empty;
            if (settled && !_relayLocationLogged)
            {
                _relayLocationLogged = true;
                // How many relays were measured and the nearest one's ping, never the data centres: they say where this PC is (D127).
                location = $"; ping location {(SteamPing.TryGetLocalLocation(out string ping, out float age) ? LinkShape.PingLocation(ping, age) : "unknown")}";
            }

            ModLog.Info($"Steam: relay network {availability} ({SafeText.Clean(status.DebugMessage, 200)}){location}.");
        }

        if (!_relayWarned && availability != SteamNetworkingAvailability.Current && elapsed - _startedAt >= RelayWarnAfter)
        {
            _relayWarned = true;
            ModLog.Warn($"Steam: the relay network still isn't up {RelayWarnAfter.TotalSeconds:0} s after start-up ({availability}); " +
                "friends whose PCs can't reach yours directly won't be able to connect until it is.");
        }
    }

    /// <summary>A lobby create or join that finished (or was given up on) this frame: hosting starts, or the friend side.</summary>
    private void PollLobby(TimeSpan elapsed)
    {
        switch (_link.Poll(elapsed))
        {
            case SteamLobbyCall.Created:
                Run(_flow.Handle(LobbyEvent.LobbyCreated, Now));
                break;
            case SteamLobbyCall.Entered:
                EnteredLobby();
                break;
            case SteamLobbyCall.Failed:
                NotifyText(NoticeId.JoinRefused, $"{_link.Problem} {JoinWay} tries again.", $"{_link.ProblemForLog} {JoinWay} tries again.");
                LobbyCallFailed();
                break;
        }
    }

    /// <summary>The lobby data friends read before joining: who hosts, which versions, how many play.</summary>
    private void PublishLobbyData()
    {
        ulong lobby = _link.Id;
        SetLobbyData(lobby, ModKey, _identity.ModVersion);
        SetLobbyData(lobby, ProtocolKey, ProtocolInfo.Version.ToString(CultureInfo.InvariantCulture));
        SetLobbyData(lobby, BuildKey, _buildHex);
        SetLobbyData(lobby, HostKey, _self.ToString(CultureInfo.InvariantCulture));
        PublishLogTagKey(lobby); // OnlineSession.LogTags.cs
        _advertiser.Publish(lobby, _advert, _clock.Elapsed); // state, mode, save and ping location (OnlineSession.Browser.cs)
        _playersPublished = -1;
        PublishPlayers();
    }

    /// <summary>Host: how many play now, in the lobby data, when it changed (only the lobby's owner can change it).</summary>
    private void PublishPlayers()
    {
        int players = 1 + _pads.Count + _parked.Count;
        if (_host == null || _link.Id == 0 || players == _playersPublished)
        {
            return;
        }

        bool first = _playersPublished < 0;
        _playersPublished = players;
        if (!SteamLobbies.SetLobbyData(_link.Id, PlayersKey, players.ToString(CultureInfo.InvariantCulture)) && first)
        {
            ModLog.Info($"Online: Steam didn't take the player count for lobby {Tags.Lobby(_link.Id)} (we may not own it any more); friends' Join lists won't show it.");
        }
    }

    /// <summary>Sets one lobby data key; a refusal is logged, as friends then can't tell it's a T2T Online lobby.</summary>
    private static void SetLobbyData(ulong lobby, string key, string value)
    {
        if (!SteamLobbies.SetLobbyData(lobby, key, value))
        {
            ModLog.Warn($"Online: Steam refused lobby data {key} = {(key == LobbyDataKeys.LogTag ? "a value" : value)} for lobby {Tags.Lobby(lobby)}; friends joining may be told it isn't a T2T Online lobby, or be on the wrong version.");
        }
    }

    private void StartHosting()
    {
        ulong lobby = _link.Id;
        NewLogTagKey(); // this hosting session's log tags (OnlineSession.LogTags.cs)
        PublishLobbyData();
        DiscardQueued(shifted: false);
        var transport = new RetryingTransport(new SteamSessionTransport(_self, shiftMessagesToSelf: true, _sessions));
        _hostTransport = transport;
        ulong hosting = HostSession.NewHostingSession(); // in every welcome, and the progress batches' session (D34)
        _campaign.HostingSession = hosting;
        _host = new HostSession(_identity, transport, MaxFriends, hosting)
        {
            AdmitOnly = UiCheckHolds.AdmitOnly(TourRuns, _self),
            StreamMessages = _relay.StartHosting(transport),
            CampaignMessages = _campaign.StartHosting(transport),
        };
        _campaign.QueuedBytesSource = transport.QueuedBytes;
        PollMembers();
        InstallInputHook();
        ModLog.Info($"Online: hosting {HostedLobbyKind()} {Tags.Lobby(lobby)} (created after {_link.Polls} polls via {_link.Path}). " +
            (_link.IsPrivate ? TourLobbyNote : (ModSettings.DevTools
                ? $"Friends with T2T Online {_identity.ModVersion} join from Online > Join Lobby (or F7, Join Game on you in Steam, or your invite); {SoloKey} runs the one-PC test; F7 again ends the session."
                : $"Friends with T2T Online {_identity.ModVersion} join from Online > Join Lobby (or Join Game on you in Steam, or your invite); {LeaveWay} ends the session.")));
    }

    private void EnteredLobby()
    {
        ulong lobby = _link.Id;
        ulong owner = SteamLobbies.GetOwner(lobby);
        // The host named in the lobby, not its owner: Steam hands ownership to a friend while the host is out of the lobby.
        // A lobby without the key (an older host) or naming us falls back to the owner.
        ulong host = ulong.TryParse(SteamLobbies.GetLobbyData(lobby, HostKey), NumberStyles.None, CultureInfo.InvariantCulture, out ulong named) && named != 0 && named != _self
            ? named
            : owner;
        ReadLogTagKey(lobby, host); // the host's log tags, before the first line naming them (OnlineSession.LogTags.cs)
        string name = SafeText.Clean(SteamFriends.GetFriendPersonaName(host));
        string mod = SteamLobbies.GetLobbyData(lobby, ModKey);
        string protocol = SteamLobbies.GetLobbyData(lobby, ProtocolKey);
        string build = SteamLobbies.GetLobbyData(lobby, BuildKey);
        ModLog.Info($"Online: joined lobby {Tags.Lobby(lobby)} after {_link.Polls} polls; host {Tag(host)} runs T2T Online {SentText.Release(mod)}, " +
            $"protocol {SentText.Number(protocol)}, game build {SentText.Hex(build, 16)}; ours {_identity.ModVersion}, {ProtocolInfo.Version}, {_buildHex}.");
        if (host != owner)
        {
            ModLog.Info($"Online: the lobby's owner is {Tag(owner)}, not its host (Steam passes ownership on while the host is out of the lobby); saying hello to the host.");
        }

        if (Array.IndexOf(SteamLobbies.GetMembers(lobby), host) < 0)
        {
            ModLog.Warn($"Online: {Tag(host)} isn't in the lobby right now (their connection to Steam may have dropped); saying hello anyway, as they'll be back.");
        }
        string? problem =
            mod.Length == 0 ? "it isn't a T2T Online lobby"
            : protocol != ProtocolInfo.Version.ToString(CultureInfo.InvariantCulture) ? $"its protocol is {SentText.Number(protocol)} and ours is {ProtocolInfo.Version}; both need the same T2T Online version"
            : build != _buildHex ? "the host's game build differs from ours; both need the same game update"
            : null;
        _failureHost = name;
        _failureHostId = host;
        _failureTheirs = SafeText.Clean(mod, 32);
        if (problem != null)
        {
            ModLog.Warn($"Online: leaving {Tag(host)}'s lobby: {problem}.");
            NotifyText(NoticeId.JoinRefused, $"Can't join {name}: {problem}.", $"Can't join {Tag(host)}: {problem}.");
            _failure = build != _buildHex && mod.Length != 0 && protocol == ProtocolInfo.Version.ToString(CultureInfo.InvariantCulture) ? LobbyFailure.GameBuild : LobbyFailure.ModVersion;
            ModLog.Info(LobbyMeter.JoinFailed(_failure.Value, RowAgeSeconds()));
            LeaveAll("version mismatch");
            return;
        }

        // Only a Steam friend's game is joined (D37 the other way round): a join from Steam (an invite, Join
        // Game, a +connect_lobby command line) can name any lobby, and the host's game then sends this one custom content,
        // run checkpoints and menus to load. A host seats only its own Steam friends anyway, so no real session is lost.
        // When Steam can't tell, the game isn't joined either, as on the host (D134).
        if (host != _self && FriendCheck.FriendFailure(CheckFriend(host, out string friendProblem)) is { } notFriends)
        {
            string why = notFriends == LobbyFailure.NotAFriend ? "they aren't on your Steam friends list" : "Steam couldn't check your friends list";
            ModLog.Warn(notFriends == LobbyFailure.NotAFriend
                ? $"Online: leaving {Tag(host)}'s lobby {Tags.Lobby(lobby)}: they aren't on your Steam friends list (only friends' games are joined, as only friends get a seat)."
                : $"Online: Steam couldn't say whether {Tag(host)} is your friend ({friendProblem}); leaving their lobby {Tags.Lobby(lobby)} (only confirmed Steam friends' games are joined).");
            NotifyText(NoticeId.JoinRefused, $"Can't join {name}: {why}.", $"Can't join {Tag(host)}: {why}.");
            _failure = notFriends;
            ModLog.Info(LobbyMeter.JoinFailed(_failure.Value, RowAgeSeconds()));
            LeaveAll(notFriends == LobbyFailure.NotAFriend ? "not a friend" : "friends list unchecked");
            return;
        }

        // Same protocol, another release: they connect, but what one build fixed may come back on the other.
        if (ModVersion.MismatchNotice(Tag(host), SentText.Release(mod), hosting: false) is { } versions)
        {
            ModLog.Warn($"Online: {versions}");
            NotifyAbout(host, NoticeId.ReleaseDiffers, name);
        }

        SteamInviteBook.Shared.Forget(lobby); // taken up: its row mustn't say so again after this session
        _helloHost = host;
        _joinName = name;
        Run(_flow.Handle(LobbyEvent.LobbyEntered, Now));
    }

    private bool StartFriend(ulong host, string hostName, bool solo)
    {
        LocalPad? pad = null;
        LocalKeyboard? keyboard = null;
        if (_tourPadFriend)
        {
            _inputWhy = "the check tour asked for a pad";
        }
        else if (!ChooseInput(out pad, out keyboard))
        {
            return false;
        }

        if (solo && pad is { OwnerJoined: true })
        {
            // The test's virtual pad would join as a second player, and the first (this pad's) would be left without a
            // controller: the menus follow the first player, so nothing would answer the pad.
            int gamePlayer = RewiredPlayerInputAssigner.GetPlayerId(pad.OwnerPlayerId);
            ModLog.Warn($"Online one-PC test ({SoloKey}): your pad is already a player in this game (game player {gamePlayer}), so the test can't take it over. " +
                $"Restart the game, then press Shift+F7 and {SoloKey} at the title screen before pressing anything on the pad.");
            Notify(NoticeId.OnePcTest, $"{SoloKey} needs a fresh start: restart the game, then Shift+F7 and {SoloKey} at the title screen, before pressing anything on the pad.");
            return false;
        }

        _localPad = pad;
        _localKeyboard = keyboard;
        _controls = keyboard?.Controls ?? (pad == null ? null : PadRemapReader.Read(pad, NextControlsGeneration()));
        NotePadName();
        if (solo)
        {
            NativeLobby.AvatarCache.SetStandIn(SoloSeatId, _self);
        }

        _solo = solo;
        _hostName = hostName;
        _hostSteamId = host;
        _friendLastPackets = 0;
        _friendLastBytes = 0;
        _friendLastReport = _clock.Elapsed;
        _soloHolds = solo ? new HoldMatcher() : null;
        _soloHoldErrorMs.Clear();
        _soloHoldsLogged = 0;
        _localPadWasConnected = true;
        _padBack = true;
        _hostMissing = false;
        _rejoinUntil = TimeSpan.MinValue;
        DiscardQueued(_solo);
        _momentInbox.Clear(); // what waited for an earlier session's play isn't this one's
        _sharedPlay.Clear();
        _streamClient = _relay.StartFriend(new CurrentFriendTransport(this), host, hostName, measureOnly: solo);
        // The one-PC test's friend is this same game: it never follows the campaign (that would load the host's own
        // scenes over them); the host's campaign messages to it are taken and dropped. A friend follows from BeginPlay on
        // (OnlineSession.Lobby.cs): until the host's game starts, the lobby is all that's joined.
        _campaignFromHost = solo ? static (_, _, _, _) => true : null;
        StartFriendSession(host);
        InstallInputHook();
        string device = pad != null ? $"your pad (\"{pad.Joystick.name}\", {pad.DeviceType})" : keyboard != null ? "your keyboard and mouse" : "the check tour's stream of a pad";
        if (!solo)
        {
            SayDeviceStays(device);
        }

        ModLog.Info(solo
            ? $"Online one-PC test: {device} goes out over Steam to yourself as a friend ({_inputWhy})" +
              (keyboard != null ? "; the keyboard stays on this game's own player too, so both move together." : ".") + " Saying hello..."
            : $"Online: saying hello to {Tag(host)} with {device} ({_inputWhy})...");
        return true;
    }

    private void PollMembers()
    {
        // Out of the lobby ourselves, the members we knew stay and the session goes on (a friend's times out and rejoins
        // by itself) while the link asks to be let back in.
        if (!_link.RefreshMembers(_self, _clock.Elapsed, hosting: _host != null, _host != null ? string.Empty : HostTag()))
        {
            return;
        }

        if (_host != null)
        {
            _host.SetMembers(_link.Members);
            _leaving.Prune(_link.Members, Now);
            AcceptPendingSessions();
        }

        if (_friend != null && !_solo)
        {
            NoteHostInLobby(_friend.Host);
        }

        NoteLogTagMembers();
    }

    /// <summary>
    /// Friend: whether the host is still in the lobby, logged when that changes. Missing from it isn't the end: their
    /// connection to Steam can drop as ours can, and a host who quits says so. While they're heard the session goes on;
    /// if they go quiet it times out and rejoins by itself (<see cref="CanRejoin"/>), as for any lost host.
    /// </summary>
    private void NoteHostInLobby(ulong host)
    {
        bool missing = Array.IndexOf(_link.Members, host) < 0;
        if (missing == _hostMissing)
        {
            return;
        }

        _hostMissing = missing;
        if (missing)
        {
            ModLog.Warn($"Online: {HostTag()} isn't in the lobby any more (they left it, or their connection to Steam dropped); carrying on while they're heard, " +
                $"and trying to rejoin if they go quiet (for up to {LobbyTiming.RejoinInGameMicros / 1_000_000} s from a game, {LobbyTiming.RejoinInLobbyMicros / 1_000_000} s from the lobby).");
        }
        else
        {
            ModLog.Info($"Online: {HostTag()} is back in the lobby.");
        }
    }

    /// <summary>
    /// Getting back into the lobby after falling out of it: back in (who's there now, and whether Steam gave the lobby to
    /// someone else meanwhile), a new lobby made because ours was gone (hosting: its data goes up), or the lobby gone (a
    /// friend: if the host has made a new one, we ask for that one).
    /// </summary>
    private void PollLobbyRejoin(TimeSpan elapsed)
    {
        switch (_link.PollRejoin(elapsed, hosting: _host != null))
        {
            case SteamLobbyRejoin.Back:
                _nextMemberPoll = TimeSpan.Zero;
                ulong owner = SteamLobbies.GetOwner(_link.Id);
                if (_host != null && owner != _self)
                {
                    ModLog.Warn($"Online: Steam made {Tag(owner)} the lobby's owner while we were out. Friends in the session carry on, and friends " +
                        "joining later still find you (the lobby names you as its host), but its player count can't be updated any more.");
                }

                break;
            case SteamLobbyRejoin.Replaced:
                _nextMemberPoll = TimeSpan.Zero;
                PublishLobbyData();
                Notify(NoticeId.LobbyReplaced);
                break;
            case SteamLobbyRejoin.Gone when _friend != null && !_solo:
                // The host may have made a new lobby (theirs was gone too): the friends list says which.
                ulong host = _friend.Host;
                _link.Retarget(SteamFriendsLobbies.FindFriendLobbies(SteamRuntime.AppId).FirstOrDefault(l => l.FriendSteamId == host).LobbySteamId);
                break;
        }
    }

    private void Drain(long now)
    {
        if (_host != null)
        {
            DrainHostRealtime(now);
            _receiver.Drain(SteamSessionTransport.SteamChannel(Channel.Events, shifted: false), _hostEvents, SessionTiming.EventMessagesPerDrain);
            DiscardHostBulk(); // only a host sends on it (OnlineSession.Admission.cs)
        }

        if (_friend != null && _friendTransport is { Cut: true })
        {
            DiscardQueued(_solo, quiet: true); // a simulated drop: what the host sends is lost
        }
        else if (_friend != null)
        {
            // Steam stamps each message when it arrived; the match stream wants that on the frame clock.
            long steamNow = SteamNetworking.GetLocalTimestamp();
            _steamToFrameClock = steamNow == 0 ? long.MinValue : FrameClock.NowMicros - steamNow;
            _drainNow = now;
            _receiver.Drain(SteamSessionTransport.SteamChannel(Channel.Events, _solo), _friendEvents, SessionTiming.EventMessagesPerDrain);
            _receiver.Drain(SteamSessionTransport.SteamChannel(Channel.Bulk, _solo), _friendBulk, SessionTiming.BulkMessagesPerDrain);
            _receiver.Drain(SteamSessionTransport.SteamChannel(Channel.Realtime, _solo), _friendRealtime, SessionTiming.RealtimeMessagesPerDrain);
        }
    }

    /// <summary>
    /// Resends what Steam refused earlier. A peer whose backlog passed the cap can't keep up: a friend is dropped
    /// (they rejoin by themselves and get everything again); a host we can't send to ends our session (we rejoin).
    /// </summary>
    private void FlushRetries()
    {
        if (_hostTransport != null)
        {
            _hostTransport.Flush();
            while (_hostTransport != null && _hostTransport.TryTakeOverflow(out ulong friend))
            {
                ModLog.Warn($"Online: {Tag(friend)} can't keep up: more than {RetryingTransport.DefaultMaxQueuedBytesPerPeer / 1024 / 1024} MB waits to go to them; " +
                    "dropping them (they rejoin by themselves and get everything again).");
                _host?.Remove(friend, LeaveReason.TimedOut);
            }
        }

        if (_friendRetrying != null)
        {
            _friendRetrying.Flush();
            if (_friendRetrying.TryTakeOverflow(out _))
            {
                ModLog.Warn("Online: too much waits to go to the host; leaving the session (it rejoins by itself).");
                _friend?.Leave(LeaveReason.TimedOut);
            }
        }
    }

    /// <summary>The session's end: how often Steam refused a reliable send and what happened to it (only when it did).</summary>
    private static void LogRetries(string direction, RetryingTransport? transport)
    {
        if (transport is { Held: > 0 })
        {
            ModLog.Info($"Online: reliable sends {direction}: {transport.Held} refused by Steam for a moment and kept, {transport.Resent} resent, " +
                string.Create(CultureInfo.InvariantCulture, $"{transport.Discarded} dropped with their peer; at most {transport.PeakQueuedBytes / 1024.0:0.0} KB waited; {transport.Overflows} peer(s) couldn't keep up."));
        }
    }

    /// <summary>When the message being drained arrived, on the frame clock (when Steam's stamp can't be had: now).</summary>
    private long ArrivalMicros() =>
        _steamToFrameClock == long.MinValue || _receiver.TimeReceived == 0 ? FrameClock.NowMicros : _receiver.TimeReceived + _steamToFrameClock;

    /// <summary>The match stream's messages from the host, with their arrival time.</summary>
    private bool FriendStreamMessage(ulong sender, Channel channel, ReadOnlySpan<byte> message, long nowMicros) =>
        _streamClient?.Receive(sender, channel, message, ArrivalMicros()) ?? false;

    /// <summary>
    /// Drops whatever an earlier session left queued on the channels a new one listens on: in the one-PC test, the
    /// host's goodbye to itself would otherwise end the next test as it starts.
    /// </summary>
    private void DiscardQueued(bool shifted, bool quiet = false)
    {
        int dropped = _receiver.Drain(SteamSessionTransport.SteamChannel(Channel.Realtime, shifted), static (_, _, _) => { })
            + _receiver.Drain(SteamSessionTransport.SteamChannel(Channel.Events, shifted), static (_, _, _) => { })
            + _receiver.Drain(SteamSessionTransport.SteamChannel(Channel.Bulk, shifted), static (_, _, _) => { });
        if (dropped > 0 && !quiet)
        {
            ModLog.Info($"Online: dropped {dropped} message(s) left over from an earlier session.");
        }
    }

    private void DrainHostRealtime(long now)
    {
        _drainNow = now;
        _receiver.Drain(SteamSessionTransport.SteamChannel(Channel.Realtime, shifted: false), _hostRealtime, SessionTiming.RealtimeMessagesPerDrain);
    }

    /// <summary>
    /// Hands a message to the host session. A friend rejoining the lobby can say hello (over a Steam session still open
    /// from before) before the once-a-second member poll has seen them, which would drop the hello and cost them a 2 s
    /// retry, so an unknown sender refreshes the member list first (at most every 100 ms).
    /// </summary>
    private void HostReceive(ulong sender, Channel channel, ReadOnlySpan<byte> payload, long now)
    {
        if (_host == null)
        {
            return;
        }

        _inbound.Read(sender, payload.Length); // the 10 s inbound line's "read here" (OnlineSession.Admission.cs)
        if (_link.Id != 0 && Array.IndexOf(_link.Members, sender) < 0 && _clock.Elapsed - _lastMemberRefresh >= TimeSpan.FromMilliseconds(100))
        {
            _lastMemberRefresh = _clock.Elapsed;
            PollMembers();
        }

        _host?.Receive(sender, channel, payload, now);
    }

    /// <summary>
    /// The host session's events. <paramref name="closing"/> (hosting is ending): friends who leave go for good, with
    /// their game players if <paramref name="removeGamePlayers"/>; otherwise a friend who leaves is parked, whatever
    /// reason they sent.
    /// </summary>
    private void HandleHostEvents(long now, bool closing = false, bool removeGamePlayers = true)
    {
        if (_host == null)
        {
            return;
        }

        while (_host.TryTakeEvent(out HostEvent e))
        {
            string tag = Tag(e.SteamId);
            switch (e.Kind)
            {
                case HostEventKind.JoinRequested:
                    _linkBrokeAt.Remove(e.SteamId);
                    _leaving.Back(e.SteamId); // a hello: arriving for real again
                    AcceptFriend(e, tag, now);
                    break;
                case HostEventKind.Refused:
                    LogRefusal(e, tag);
                    NoteRefusedSession(e.SteamId); // their Steam session closes once the refusal has left (OnlineSession.Admission.cs)
                    break;
                case HostEventKind.Joined:
                    if (e.SteamId != _self)
                    {
                        _sessions.Watch(e.SteamId);
                    }

                    _relay.FriendJoined(e.SteamId);
                    HostFriendArrived(e.SteamId); // the campaign reaches them once they follow (OnlineSession.Lobby.cs); the one-PC test's friend has a seat and a flow too (OnlineSession.OnePc.cs)
                    SendMissedAchievements(e.SteamId, tag);
                    break; // logged when accepted
                case HostEventKind.Rejoined:
                    // Their session restarted before ours noticed (or their Steam session broke): same seat and pad, and
                    // everything a rejoining friend gets, again. Their pad stream starts over, and so does its report.
                    ModLog.Info($"Online: {tag} said hello again (their session restarted); they get the current match's setup, the campaign and what they may have missed again.");
                    _linkBrokeAt.Remove(e.SteamId);
                    if (_pads.TryGetValue(e.SteamId, out RemotePad? again))
                    {
                        MatchInput(again, e.Hello, tag); // they may have come back with another pad, or the other kind of input
                        again.ResetReport(_clock.Elapsed, _host.Find(e.SteamId)?.Link.BytesIn ?? 0);
                        TellPadDevice(e.SteamId, again.Pad);
                    }

                    _relay.FriendJoined(e.SteamId);
                    HostFriendArrived(e.SteamId);
                    SendMissedAchievements(e.SteamId, tag);
                    break;
                case HostEventKind.LobbyMember:
                    HostMemberSays(e.SteamId, tag, e.Member, now);
                    break;
                case HostEventKind.Lagging:
                    ModLog.Warn($"Online: nothing from {tag} for {SessionTiming.LaggingAfterMicros / 1_000_000} s; their pad went neutral after a quarter second of silence.");
                    break;
                case HostEventKind.Recovered:
                    ModLog.Info($"Online: {tag} is back.");
                    break;
                case HostEventKind.PadDisconnected:
                    PadAway(e.SteamId, tag, e.Away);
                    break;
                case HostEventKind.PadReconnected:
                    if (PadAwayLine(e.SteamId, tag))
                    {
                        ModLog.Info(e.Away switch
                        {
                            PadAwayReason.ChangingControls => $"Online: {tag} is back from changing their controls (their new ones follow if they changed).",
                            PadAwayReason.InSettings => $"Online: {tag} is back from their own settings.",
                            _ => $"Online: {tag}'s pad is back.",
                        });
                    }

                    break;
                case HostEventKind.PadDeviceChanged:
                    PadDeviceChanged(e.SteamId, tag, e.DeviceType);
                    break;
                case HostEventKind.BindingsChanged:
                    ApplyControls(e.SteamId, tag);
                    break;
                case HostEventKind.PromptNamesChanged:
                    PromptNamesArrived(e.SteamId, tag);
                    break;
                case HostEventKind.Left:
                    if (e.Reason == LeaveReason.Quit && e.SteamId != _self)
                    {
                        // They leave the Steam lobby too, but its member list names them for a while yet: not arriving meanwhile
                        // (the board published next would show them Joining, their seat held).
                        _leaving.Left(e.SteamId, Now);
                    }

                    HostFriendGone(e.SteamId);
                    _relay.FriendLeft(e.SteamId);
                    _campaign.FriendLeft(e.SteamId);
                    _sessions.Forget(e.SteamId);
                    _hostTransport?.Forget(e.SteamId);
                    _linkBrokeAt.Remove(e.SteamId);
                    // No CloseSession here: it would throw away the goodbye just queued to them. Steam closes idle
                    // sessions by itself.
                    if (!_pads.Remove(e.SteamId, out RemotePad? pad))
                    {
                        ModLog.Info($"Online: {tag} left the session ({e.Reason}).");
                    }
                    else if (closing)
                    {
                        // Decided here, never by the reason a friend sent: only the host closing removes a pad at once.
                        ModLog.Info($"Online: {tag} left the session ({e.Reason}).");
                        RemoveForGood(pad, removeGamePlayers);
                    }
                    else if (e.SteamId == _self && e.Reason == LeaveReason.Quit)
                    {
                        // The one-PC test stopped (Ctrl+F6): nobody comes back for this pad, and the menus follow the first
                        // player, which it is (the test starts at the title), so it goes now, with its game player. Not mid-match:
                        // removing a player there pauses the match for the controller screen, so it goes once it's over.
                        StopSoloPad(pad);
                    }
                    else
                    {
                        // Left's rule (OnlineSession.Lobby.cs): in the lobby a deliberate leave frees the seat at once and a drop keeps it 30 s; in a game it's kept
                        // the 2 minutes, whatever they said. Removed goes at once, or once the match in play is over.
                        TimeSpan keep = KeepSeatFor(e.Reason);
                        pad.Pad.ApplyNeutral(); // what they hold when they're back counts once let go
                        LogLinkTotals(pad, "so far");
                        LogMapsSummary(pad, "so far (their player is kept, and its maps checked, while they're away)");
                        if (keep == TimeSpan.Zero && MatchSetup.CurrentMatch(out _).IsNone)
                        {
                            ModLog.Info($"Online: {tag} left the session ({e.Reason}); their seat is free now.");
                            if (e.Reason == LeaveReason.Removed)
                            {
                                NotifyText(NoticeId.FriendRemoved, $"{pad.DisplayName} was removed.", $"{tag} was removed.");
                            }
                            else
                            {
                                NotifyAbout(e.SteamId, NoticeId.FriendLeft, pad.DisplayName);
                            }

                            _standIn.Forget(pad.Pad.RewiredPlayerId);
                            RemoveForGood(pad, removeGamePlayer: true);
                            break;
                        }

                        string kept = keep >= TimeSpan.FromMinutes(1) ? $"{keep.TotalMinutes:0} minutes" : $"{keep.TotalSeconds:0} s";
                        _parked[e.SteamId] = (pad, _clock.Elapsed + keep);
                        NotifyAbout(e.SteamId, NoticeId.FriendDropped, pad.DisplayName);
                        ModLog.Info($"Online: {tag} left the session ({e.Reason}). Their {pad.Pad.Kind.ToLowerInvariant()} stays on Rewired player {pad.Pad.RewiredPlayerId} " +
                            $"(game player {pad.GamePlayer}), standing still, for {kept}: if they come back by then, they're the same player again.");
                    }

                    break;
            }
        }
    }

    private void AcceptFriend(in HostEvent e, string tag, long now)
    {
        // Removed from this lobby by the host: refused until the session ends (their parked seat went with it).
        if (_removed.Contains(e.SteamId))
        {
            ModLog.Info($"Online: {tag} was removed from this session, so they don't get a seat.");
            _host!.Refuse(e.SteamId, RejectReason.Removed);
            return;
        }

        // Only gamepad families: anything else from the network would make the game treat the pad as a keyboard.
        EInputDeviceType deviceType = VirtualPad.GamepadTypeFrom(e.Hello.DeviceType);
        if (_pads.Remove(e.SteamId, out RemotePad? stale))
        {
            // Not expected (their leaving comes first and parks the pad), but their pad and game player are still theirs:
            // taken back as a parked pad, below.
            ModLog.Warn($"Online: {tag} said hello again while their {stale.Pad.Kind.ToLowerInvariant()} was still in use; they get the same one back.");
            _parked[e.SteamId] = (stale, _clock.Elapsed + ParkFor);
        }

        if (_parked.Remove(e.SteamId, out (RemotePad Pad, TimeSpan Until) parked))
        {
            if (!_host!.Accept(e.SteamId, (byte)parked.Pad.Pad.RewiredPlayerId, now))
            {
                _parked[e.SteamId] = parked;
                return;
            }

            parked.Pad.ResetReport(_clock.Elapsed, 0);
            parked.Pad.RemovalDeferred = false;
            MatchInput(parked.Pad, e.Hello, tag); // they may have come back with another pad, or the other kind of input
            TellPadDevice(e.SteamId, parked.Pad.Pad);
            _pads[e.SteamId] = parked.Pad;
            _refusals.Remove(e.SteamId);
            NotifyAbout(e.SteamId, NoticeId.FriendBack, parked.Pad.DisplayName);
            ModLog.Info($"Online: {tag} is back: same {parked.Pad.Pad.Kind.ToLowerInvariant()}, Rewired player {parked.Pad.Pad.RewiredPlayerId}, game player {parked.Pad.GamePlayer}.");
            return;
        }

        // Only the host's Steam friends get a seat (a friends-only lobby can still be entered by its id, or through a
        // friend of a friend). When Steam can't tell, they're refused too (D134). Only the one-PC test's own friend
        // (this same account) is exempt, also while the test runs.
        if (e.SteamId != _self)
        {
            FriendCheckResult friendship = CheckFriend(e.SteamId, out string friendProblem);
            RejectReason refusal = FriendCheck.HostRefusal(friendship);
            if (refusal != RejectReason.None)
            {
                ModLog.Warn(friendship == FriendCheckResult.NotAFriend
                    ? $"Online: {tag} isn't on your Steam friends list, so they don't get a seat."
                    : $"Online: Steam couldn't say whether {tag} is your friend ({friendProblem}), so they don't get a seat (only confirmed Steam friends play together).");
                _host!.Refuse(e.SteamId, refusal);
                return;
            }
        }

        // Seats kept for friends who dropped count too (HostSession counts only friends in the session): a newcomer
        // mustn't take a dropped friend's player.
        if (_pads.Count + _parked.Count >= MaxFriends)
        {
            ModLog.Info($"Online: no seat for {tag}: {_pads.Count} friend(s) in and {_parked.Count} kept for friends who dropped out.");
            _host!.Refuse(e.SteamId, RejectReason.SessionFull);
            return;
        }

        VirtualController? pad;
        string problem;
        try
        {
            pad = CreateController(e.Hello, $"T2TOnline net:{tag}", -1, null, null, out problem);
        }
        catch (Exception ex)
        {
            pad = null;
            problem = ModLog.SafeErrorText(ex);
            ModLog.Error("Online: creating a friend's virtual controller threw", ex);
        }

        if (pad == null)
        {
            ModLog.Warn($"Online: no {(e.Hello.Input == InputKind.KeyboardMouse ? "virtual keyboard" : "virtual pad")} for {tag}: {problem}. Refusing them.");
            _host!.Refuse(e.SteamId, RejectReason.NoControllerSlot);
            return;
        }

        if (!_host!.Accept(e.SteamId, (byte)pad.RewiredPlayerId, now))
        {
            pad.Dispose(); // they stopped waiting meanwhile
            return;
        }

        var remote = new RemotePad(pad, e.SteamId, DisplayNameOf(e.SteamId));
        NoteMapsBuilt(remote, pad.IsKeyboard ? "made, empty until their controls arrive" : "made with the game's gamepad maps (their controls come next)");
        remote.ResetReport(_clock.Elapsed, 0);
        TellPadDevice(e.SteamId, pad);
        _pads[e.SteamId] = remote;
        _refusals.Remove(e.SteamId);
        NotifyAbout(e.SteamId, NoticeId.FriendJoined, remote.DisplayName);
        ModLog.Info(pad.IsKeyboard
            ? $"Online: {tag} joined (T2T Online {SentText.Release(e.Hello.ModVersion)}, keyboard and mouse): their keyboard drives a virtual keyboard on Rewired player {pad.RewiredPlayerId}, " +
              "bound from their own maps once those arrive. They press Space or Return (any key their Activation map binds) to join the game."
            : $"Online: {tag} joined (T2T Online {SentText.Release(e.Hello.ModVersion)}, {deviceType}): their pad drives a virtual pad on Rewired player {pad.RewiredPlayerId}. " +
              "They press their join button to join the game.");

        // Same protocol, another release: said on screen too, after the join line (a notice shows one at a time).
        if (ModVersion.MismatchNotice(tag, SentText.Release(e.Hello.ModVersion), hosting: true) is { } versions)
        {
            ModLog.Warn($"Online: {versions}");
            NotifyAbout(e.SteamId, NoticeId.ReleaseDiffers, remote.DisplayName);
        }
    }

    private void HandleFriendEvents()
    {
        if (_friend == null)
        {
            return;
        }

        while (_friend != null && _friend.TryTakeEvent(out FriendEvent e))
        {
            string where = _solo ? "Online one-PC test" : "Online";
            switch (e.Kind)
            {
                case FriendEventKind.Joined:
                    // Whether this is a first join or a return, the pad must be ours (a first join can time out while
                    // the host loads, and then comes in through the rejoin path).
                    bool returning = _everJoined;
                    bool relinked = _relinking;
                    _everJoined = true;
                    _relinking = false;
                    _hostClosedLinkAt = TimeSpan.MinValue;
                    _rejoinUntil = TimeSpan.MinValue;
                    if (!_solo)
                    {
                        _sessions.Watch(_friend.Host);
                    }

                    // Not while it's lent to the friend's own menu: it's taken back when that closes (OnlineSession.Remap.cs). Not in the
                    // lobby phase at all: a friend's device stays theirs until BeginPlay (OnlineSession.Lobby.cs).
                    if (DeviceWithHost && _localPad is { IsDetached: false } && !DeviceLent)
                    {
                        _localPad.Detach();
                    }

                    if (DeviceWithHost && _localKeyboard is { IsDetached: false } && !_solo && !DeviceLent)
                    {
                        _localKeyboard.Detach();
                    }

                    _relay.FriendSessionJoined(_friend.Welcome.Slot);
                    if (!_solo)
                    {
                        MomentsWelcomed(_friend.Host, _friend.Welcome.HostingSession);
                    }

                    if (!_solo)
                    {
                        _campaign.LinkJoined(); // a rejoining friend is new to the host's campaign link: where we are goes out again
                        FriendWelcomed(returning, relinked); // the flow says what comes next: the lobby, or following at once
                        break;
                    }

                    SoloWelcomed();
                    if (relinked)
                    {
                        ModLog.Info($"{where}: back in after the Steam session restarted, as Rewired player {_friend.Welcome.Slot} again; the host resends what we may have missed.");
                        break;
                    }

                    if (returning)
                    {
                        ModLog.Info($"{where}: back in, as Rewired player {_friend.Welcome.Slot} again.");
                        NotifyText(NoticeId.OnePcTest, $"Back in {_hostName}'s game.", $"Back in {HostTag()}'s game.");
                        break;
                    }

                    // Only the one-PC test comes here: a real friend's welcome went to the flow above.
                    if (_localKeyboard != null)
                    {
                        ModLog.Info($"{where}: joined your own session: your keyboard now drives Rewired player {_friend.Welcome.Slot} on this game, through Steam. Press Space or Return to join; {SoloKey} gives the keyboard back.");
                        Notify(NoticeId.OnePcTest, $"One-PC test: press Space to join as the friend. {SoloKey} stops.");
                        break;
                    }

                    ModLog.Info($"{where}: joined your own session: your pad now drives Rewired player {_friend.Welcome.Slot} on this game, through Steam, and this game no longer reacts to it. Press the join button to join; {SoloKey} gives the pad back.");
                    Notify(NoticeId.OnePcTest, $"One-PC test: press A / Cross to join as the friend. {SoloKey} stops.");
                    break;
                case FriendEventKind.PadDeviceSeen:
                    PadDeviceSeen(e.DeviceType);
                    break;
                case FriendEventKind.BoardChanged:
                    if (_solo)
                    {
                        SoloBoardChanged();
                    }
                    else
                    {
                        FriendBoardChanged();
                    }

                    break;
                case FriendEventKind.Rejected:
                    ModLog.Warn($"{where}: the host refused us: {_friend.RejectReason}.");
                    NotifyText(NoticeId.JoinRefused, $"{_hostName}'s game refused you: {RejectSentence(_friend.RejectReason, toFriend: true)}.",
                        $"{HostTag()}'s game refused you: {RejectSentence(_friend.RejectReason, toFriend: true)}.");
                    if (!_solo)
                    {
                        _failure = JoinFailures.FromReject(_friend.RejectReason);
                        ModLog.Info(LobbyMeter.JoinFailed(_failure.Value, RowAgeSeconds()));
                    }

                    EndFriend();
                    break;
                case FriendEventKind.Lagging:
                    ModLog.Warn($"{where}: nothing from the host for {SessionTiming.LaggingAfterMicros / 1_000_000} s.");
                    break;
                case FriendEventKind.Recovered:
                    ModLog.Info($"{where}: the host is back.");
                    break;
                case FriendEventKind.AchievementEarned:
                    ReceiveAchievement(e.Moment);
                    break;
                // Not only a timeout: the host also drops a friend it saw leave the lobby (ours may have been a Steam blip
                // while we're getting back in), and a goodbye that didn't decode says nothing final. D22: we come back.
                case FriendEventKind.Ended when (_friend.EndReason is LeaveReason.TimedOut or LeaveReason.LeftLobby or LeaveReason.None) && CanRejoin():
                    LeaveReason lost = _friend.EndReason;
                    if (_everJoined && !_solo)
                    {
                        Run(_flow.Handle(LobbyEvent.LinkLost, Now));
                    }
                    else if (_everJoined)
                    {
                        SoloLinkLost();
                    }

                    // From the lobby the flow gives up sooner than this window (LobbyTiming) and ends the session then: the line says the sooner.
                    double rejoinFor = (_rejoinUntil - _clock.Elapsed).TotalSeconds;
                    if (_everJoined && !_solo && _flow.Friend == LobbyFriendState.Lost)
                    {
                        rejoinFor = Math.Min(rejoinFor, _flow.RejoinSecondsLeft(Now));
                    }

                    ModLog.Warn(_everJoined
                        ? $"{where}: lost the host ({lost}); trying to rejoin by ourselves for up to {rejoinFor:0} s ({(_solo ? SoloKey : LeaveWay)} stops)."
                        : $"{where}: the host hasn't answered yet; saying hello again for up to {rejoinFor:0} s more (they may be loading; {LeaveWay} stops).");
                    StartFriendSession(_friend!.Host);
                    break;
                case FriendEventKind.Ended:
                    LeaveReason ended = _friend.EndReason;
                    bool joined = _everJoined;
                    bool soloStopped = _solo && ended == LeaveReason.Quit;
                    ModLog.Info($"{where}: session over ({ended}; your controls went to the host {_friend.BindingsSent} time(s), your prompt names {_friend.PromptNamesSent}).");
                    if (!_solo)
                    {
                        _failure = JoinFailures.FromLeave(ended, joined);
                    }

                    EndFriend();
                    if (!soloStopped)
                    {
                        // The one-PC test's own stop is told on the host's side (its pad and player go).
                        NotifyText(NoticeId.SessionOver, $"{EndSentence(ended, joined, _hostName)} {PadBackNotice()}", $"{EndSentence(ended, joined, HostTag())} {PadBackNotice()}");
                        NotifyPadBack();
                    }

                    break;
            }
        }
    }

    private void StartFriendSession(ulong host)
    {
        _friendTransport = new SteamSessionTransport(_self, shiftMessagesToSelf: false, _sessions) { Cut = (_friendTransport?.Cut ?? false) || _tourHoldHello };
        _friendRetrying = new RetryingTransport(_friendTransport);
        (InputKind input, byte device) = FriendInput();
        _friend = new FriendSession(_identity, host, input, device, _friendRetrying)
        {
            StreamMessages = FriendStreamMessage,
            CampaignMessages = FriendCampaignMessage,
        };
        if (_controls != null)
        {
            _friend.SetBindings(_controls); // sent after every welcome
        }

        _friend.Start(Now);
        _friendLastPackets = 0;
        _friendLastBytes = 0;
        _friendLastReport = _clock.Elapsed;
    }

    /// <summary>
    /// After the host went silent: rejoin for up to <see cref="ParkFor"/>, the time the host keeps our virtual pad in a game
    /// (from the lobby the lobby's flow gives up sooner, <see cref="LobbyTiming.RejoinInLobbyMicros"/>, and ends the session). Who's
    /// in the lobby doesn't stop it (we or the host may have fallen out of it with our connection to Steam, and a host who
    /// quits says so); the window does. A first join that was never answered has nothing to come back to: it's tried for
    /// <see cref="FirstJoinRetryFor"/> more (the host may be loading), then given up.
    /// </summary>
    private bool CanRejoin()
    {
        if (_friend == null || _link.Id == 0 || Campaign.SaveBackups.BlocksSessions(out _))
        {
            return false;
        }

        if (_rejoinUntil == TimeSpan.MinValue)
        {
            _rejoinUntil = _clock.Elapsed + (_everJoined ? ParkFor : FirstJoinRetryFor);
        }

        return _clock.Elapsed < _rejoinUntil;
    }

    /// <summary>Final report, the pad back to the game, and (for a real session) out of the lobby.</summary>
    private void EndFriend()
    {
        if (_friend == null)
        {
            return;
        }

        bool solo = _solo;
        if (!solo)
        {
            FlowLeaving("the session ended");
        }

        EndFriendSide("the session ended", sayGoodbye: false);
        if (!solo)
        {
            LeaveAll("the session ended");
        }

        RemoveInputHookIfIdle();
    }

    /// <summary>
    /// Ends this PC's friend side, however far it got (StartFriend's steps can fail part-way): the goodbye (with
    /// <paramref name="sayGoodbye"/>), the last report, the pad back to the game, the match stream and the campaign following
    /// stopped, and every friend-side field cleared. Each step on its own, so one failure doesn't skip the rest.
    /// </summary>
    private void EndFriendSide(string reason, bool sayGoodbye)
    {
        // The steps below give back everything a play period took, in their proven order, on every way out: the flow's EndPlay is this on a session's end.
        _playOn = false;
        FriendSession? friend = _friend;
        if (friend != null)
        {
            if (sayGoodbye)
            {
                Step("saying goodbye to the host", () => friend.Leave());
            }

            Step("the friend report", () => ReportFriend(final: true));
            Step("the shared moments left", SayMomentsLeft);
            _sessions.Forget(friend.Host);
        }

        Step("ending the question about the host's match", () => EndFollowQuestion(reason));
        Step("ending your own menu", () => EndOwnMenu(reason));
        if (_localPad is { IsDetached: true })
        {
            LocalPad pad = _localPad;
            Step("giving the pad back", () => _padBack = pad.GiveBackToOwner());
        }

        if (_localKeyboard is { IsDetached: true })
        {
            LocalKeyboard keyboard = _localKeyboard;
            Step("giving the keyboard and mouse back", keyboard.GiveBack);
        }

        Step("unpinning the button prompts", () => UnpinPrompts(reason));
        ForgetPromptNames();
        _localPad = null;
        _localKeyboard = null;
        _controls = null;
        _friend = null;
        _friendTransport = null;
        LogRetries("to the host", _friendRetrying);
        _friendRetrying = null;
        Step("stopping the match stream from the host", () => _relay.StopFriend(reason, backToMenu: !_gameClosing));
        Step("stopping following the host's campaign", () => _campaign.StopFollowing(reason, _gameClosing));
        _streamClient = null;
        _campaignFromHost = null;
        _rejoinUntil = TimeSpan.MinValue;
        _dropUntil = TimeSpan.MinValue;
        _everJoined = false;
        _relinking = false;
        _hostClosedLinkAt = TimeSpan.MinValue;
        _hostMissing = false;
        _solo = false;
        _soloHolds = null;
        _soloFlow = null;
        _soloReady = false;
        _soloHomeAt = TimeSpan.MinValue;
        _tourPadFriend = false;
        _tourHoldHello = false;
        _tourFriendHeld = PadButtons.None;
    }

    private void RelayJoins()
    {
        foreach (RemotePad pad in _pads.Values)
        {
            int gamePlayer = GameJoin.TryRelay(pad.Pad, pad.JoinLabel);
            if (gamePlayer != pad.GamePlayer)
            {
                ModLog.Info(gamePlayer >= 0
                    ? $"Online: {pad.Tag}'s {pad.Pad.Kind.ToLowerInvariant()} is game player {gamePlayer}."
                    : $"Online: {pad.Tag}'s {pad.Pad.Kind.ToLowerInvariant()} is no longer a joined player; they press their join button to join again.");
                pad.GamePlayer = gamePlayer;
                if (gamePlayer >= 0)
                {
                    NoteLateJoiner(pad, gamePlayer); // during a seated run: on the team from the next match (OnlineSession.TeamSeats.cs)
                }
            }
        }
    }

    /// <summary>Rewired's hook for feeding custom controllers: runs after input sources update, before players read them.</summary>
    private void OnInputSourceUpdate()
    {
        if (_faulted || _broken)
        {
            return;
        }

        try
        {
            long now = Now;
            // Nothing of ours goes to the host while our own game has our device (our own menu, our new-unlocks screen, the question
            // about a match that couldn't load here: DeviceLent), nor in the lobby phase, where the keyboard, as the pad (never detached
            // before BeginPlay), is this game's own.
            if (_friend is { State: FriendSessionState.Joined } && DeviceWithHost && _localKeyboard != null && !DeviceLent)
            {
                SendKeyboard(_localKeyboard, _friend, now);
            }

            if (_friend is { State: FriendSessionState.Joined } && _localPad is { IsDetached: true } && !DeviceLent)
            {
                int takenFrom = _localPad.KeepDetached();
                if (takenFrom >= 0)
                {
                    NoteTakenBack(takenFrom, "pad");
                }

                bool connected = _localPad.IsConnected;
                _friend.SetPadConnected(connected);
                if (connected && _friendTransport is not { Cut: true })
                {
                    (PadButtons held, float lx, float ly, float rx, float ry, float lt, float rt) = _localPad.Read();
                    MySettingsPad(ref held, ref lx, ref ly, ref rx, ref ry, ref lt, ref rt); // My Settings' press, kept from the host (OnlineSession.MySettings.cs)
                    _friend.SendPad(now, held, lx, ly, rx, ry, lt, rt);
                    NoteOwnBrawlPad(held, lx, ly, rx, ry, lt, rt, now); // in the host's brawl: its line's presses (OnlineSession.Brawl.cs)
                    _soloHolds?.ObserveSent(now, held);
                    NotePadPress(held, lt, rt); // the cursor hides at a press, as for a pad on a player (OnlineSession.Cursor.cs)
                }
            }

            if (_tourPadFriend && _friend is { State: FriendSessionState.Joined } && _friendTransport is not { Cut: true })
            {
                _friend.SendPad(now, _tourFriendHeld, 0, 0, 0, 0, 0, 0);
            }

            long feedStart = InputCostStart();
            if (_host != null)
            {
                DrainHostRealtime(now);
                // By pad, then friend: the friends collection is an interface, whose enumerator would be a new object each update.
                foreach ((ulong steamId, RemotePad pad) in _pads)
                {
                    if (_host.Find(steamId) is not { } friend)
                    {
                        continue;
                    }

                    // A stream of the other kind than their controller (a hello with the other device too soon after the last
                    // rejoin: the swap waits for their controls, ApplyControls): the controller stands still until then, not
                    // on whatever it last had (a custom controller keeps its values until they're changed).
                    if (pad.Pad is VirtualKeyboard keyboard)
                    {
                        if (friend.Keyboard != null)
                        {
                            FeedKeyboard(friend, pad, keyboard, now);
                        }
                        else
                        {
                            keyboard.ApplyNeutral();
                            pad.Combo.Hold();
                        }

                        continue;
                    }

                    if (pad.Pad is not VirtualPad virtualPad)
                    {
                        continue;
                    }

                    if (friend.Input == null)
                    {
                        virtualPad.ApplyNeutral();
                        pad.Combo.Hold();
                        continue;
                    }

                    (PadState state, PadButtons taps) = friend.Input.Update(now);
                    bool lobbyHolds = LobbyHolds(friend, pad, state.Buttons != PadButtons.None);
                    if (!friend.PadConnected || lobbyHolds || _standIn.IsStandingIn(pad.Pad.RewiredPlayerId))
                    {
                        // Their pad is unplugged: stand still at once, not after the stream goes stale. Or the AI plays
                        // their skater: the game's pass, shot and check handlers don't ask who drives, so their buttons
                        // would still act on it; nothing does until it's handed back (decided by their link, not this).
                        // A button they hold through the hand-back counts once let go, not as a fresh press. Or no game is
                        // starting: the lobby's own screens don't take their pad, and nor does the game's press-to-join.
                        (state, taps) = (PadState.Neutral, PadButtons.None);
                        virtualPad.ApplyNeutral();
                        pad.Combo.Hold(); // and LB+RB held through the hand-back is no fresh press either
                    }
                    else
                    {
                        virtualPad.Apply(state, taps);
                        NotePlayed(pad, state, taps);
                        NoteBrawlPad(pad.Pad.RewiredPlayerId, state, taps, now); // in a brawl: its line's presses
                        RelayBrawlCombo(pad, (ulong)state.Buttons, (ulong)taps, (ulong)BrawlCombo.PadCombo, "LB+RB");
                    }

                    if (friend.SteamId == _self)
                    {
                        _soloHolds?.ObservePlayed(now, state.Buttons | taps);
                    }
                }
            }

            InputCostAdd(_feedCost, feedStart);
        }
        catch (Exception ex)
        {
            // Never throw into Rewired. Update() ends the session on the next frame.
            _faulted = true;
            ModLog.Error("Online: the input path failed", ex);
        }
    }

    /// <summary>
    /// Host: this friend's controller stays neutral: the lobby is up and no game is starting (<see cref="LobbySignals.HoldsFriendInput"/>). A press held back is said once a hosting: a friend on
    /// their way home, or a client that isn't ours.
    /// </summary>
    private bool LobbyHolds(RemoteFriend friend, RemotePad pad, bool pressing)
    {
        if (!LobbySignals.HoldsFriendInput(_flow.Host, friend.SteamId == _self))
        {
            return false;
        }

        if (pressing && !_lobbyInputSaid)
        {
            _lobbyInputSaid = true;
            ModLog.Info($"Online: {pad.Tag} sends input with this lobby open and no game starting (on their way home, or a modified client); their controller stays neutral until the next Start.");
        }

        return true;
    }

    private void InstallInputHook()
    {
        if (_inputHook != null)
        {
            return;
        }

        _inputHook = DelegateSupport.ConvertDelegate<Il2CppSystem.Action>(new Action(OnInputSourceUpdate));
        ReInput.add_InputSourceUpdateEvent(_inputHook);
    }

    /// <summary>Removes the input hook whatever is still running (cleanup after an error).</summary>
    private void RemoveInputHook()
    {
        if (_inputHook != null)
        {
            ReInput.remove_InputSourceUpdateEvent(_inputHook);
            _inputHook = null;
        }
    }

    private void RemoveInputHookIfIdle()
    {
        if (_inputHook == null || _host != null || _friend != null)
        {
            return;
        }

        ReInput.remove_InputSourceUpdateEvent(_inputHook);
        _inputHook = null;
    }

    private void TryLeaveAll(string reason, bool removeGamePlayers = true)
    {
        try
        {
            LeaveAll(reason, removeGamePlayers);
        }
        catch (Exception ex)
        {
            ModLog.Error("Online: leaving failed", ex);
        }
        finally
        {
            Step("leaving the focus guard", FocusGuard.Exit);
            Step("removing the input hook", RemoveInputHook);
        }
    }

    /// <summary>One cleanup step: a failure is logged and the next step still runs.</summary>
    private static void Step(string what, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            ModLog.Error($"Online: {what} failed", ex);
        }
    }

    /// <summary>A step that runs every frame after an error turned online off: three failures are logged, the rest aren't.</summary>
    private void BrokenStep(string what, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            if (_brokenFailures++ < 3)
            {
                ModLog.Error($"Online: {what} failed (after the error)", ex);
            }
        }
    }

    /// <summary>
    /// Ends every session and leaves the lobby. Friends' virtual pads go for good, and with
    /// <paramref name="removeGamePlayers"/> their game players too, as the game does for an unplugged pad.
    /// </summary>
    private void LeaveAll(string reason, bool removeGamePlayers = true)
    {
        // The flow first, whatever ended this: the screens are told, and it's over before the session is.
        FlowLeaving(reason);

        // Also when the friend session never started (a step before it failed): whatever did start is undone. As the game closes, the
        // goodbye to the host gets its moment to leave as the host's do below; lost, the host's lobby would show this player dropped.
        ulong toldHost = _gameClosing && !_solo && _friend is { State: FriendSessionState.Joined } joined ? joined.Host : 0;
        EndFriendSide(reason, sayGoodbye: true);
        if (toldHost != 0)
        {
            Step("waiting for the goodbye to leave", () => WaitForGoodbyes(new[] { toldHost }, "the host"));
        }

        if (_host != null)
        {
            HostSession host = _host;
            ulong[] told = _gameClosing ? host.Friends.Select(f => f.SteamId).Where(id => id != _self).ToArray() : Array.Empty<ulong>();

            // First, while friends' players are still in the match and it isn't paused: their skaters back from the AI (so
            // the game's own reassignment as their players go sees them selectable), and waiting punches resolved (so the
            // report below counts them).
            Step("handing friends' skaters back from the AI", _standIn.ForgetAll);
            Step("letting waiting punches go", () => FightGrace.ReleaseAll("hosting stopped"));
            Step("ending the match stream to friends", () => _relay.StopHosting(reason));
            Step("ending the campaign link to friends", () => _campaign.StopHosting(reason));
            Step("the host report", Report);
            Step("the hosting's input totals", ReportHostingInputTotals);
            Step("closing the host session", () => host.Close());
            Step("sending what still waited", () => _hostTransport?.Flush());
            Step("the host's last events", () => HandleHostEvents(Now, closing: true, removeGamePlayers));
            if (told.Length > 0)
            {
                Step("waiting for the goodbyes to leave", () => WaitForGoodbyes(told, $"{told.Length} friend(s)"));
            }

            _host = null;
            LogRetries("to friends", _hostTransport);
            _hostTransport = null;
            _campaign.QueuedBytesSource = null;
            _achievements.Clear();
            Step("the Steam sessions' summary", EndAdmission); // closes nothing: the goodbyes must leave (OnlineSession.Admission.cs)
            _refusals.Clear();
            _playersPublished = -1;
        }

        // A brawl the session ended during still gets its lines (its waiting punches were let go above).
        Step("the brawl's lines", () => EndBrawl(Now, "the session ended during it"));
        _sessions.Clear();
        _linkBrokeAt.Clear();

        foreach (RemotePad pad in _pads.Values.Concat(_parked.Values.Select(p => p.Pad)).ToList())
        {
            Step($"removing {pad.Tag}", () => RemoveForGood(pad, removeGamePlayers));
        }

        _pads.Clear();
        _parked.Clear();
        Step("the team's remembered sides", () => EndTeamSeats("hosting stopped")); // as before the run's start (OnlineSession.TeamSeats.cs)
        _boundActions.Clear(); // it holds their controllers; only the panel's Controller tab would have let them go
        Step("leaving the lobby", () => _link.Leave(reason));
        Step("forgetting the lobby", ForgetLobby);
        Step("the log tags", EndLogTags);
        Step("Steam's debug output", EndIceProbe);
        _everJoined = false;
        Step("removing the input hook", RemoveInputHookIfIdle);
    }

    private void EndSimulatedDrop(TimeSpan elapsed)
    {
        if (_dropUntil == TimeSpan.MinValue || elapsed < _dropUntil)
        {
            return;
        }

        _dropUntil = TimeSpan.MinValue;
        if (_friendTransport != null)
        {
            _friendTransport.Cut = false;
        }

        ModLog.Info($"Online ({DropKey}): the simulated drop is over; the link is back.");
    }

    /// <summary>
    /// Our pad as a friend: when it disconnects (battery, cable, wireless dropout) the host is told and our player
    /// stands still; meanwhile we look for a gamepad again, since a reconnected pad comes back as a new Rewired
    /// joystick that the game would otherwise take.
    /// </summary>
    private void WatchLocalPad(TimeSpan elapsed)
    {
        if (_friend == null || _localPad is not { IsDetached: true })
        {
            if (_friend != null)
            {
                WatchLobbyPad(elapsed); // not yet taken: the lobby's (OnlineSession.Lobby.cs)
            }

            return;
        }

        bool connected = _localPad.IsConnected;
        if (!connected && elapsed >= _nextPadSearch)
        {
            _nextPadSearch = elapsed + PadSearchInterval;
            connected = _localPad.TryReacquire();
        }

        if (connected != _localPadWasConnected)
        {
            _localPadWasConnected = connected;
            if (connected)
            {
                NotePadName(); // it may be another pad now (SyncPadDevice tells the host if it's another family)
                _padSwapPending = true; // another pad has its own maps (OnlineSession.Remap.cs)
                ModLog.Info($"Online: your pad is back (\"{_padName}\"); you're driving your player again.");
            }
            else
            {
                ModLog.Warn("Online: your pad disconnected. Your player stands still until it's back; reconnect it (or plug in another) to carry on.");
                Notify(NoticeId.PadLost);
            }
        }
    }

    /// <summary>
    /// A friend's virtual pad goes for good: with <paramref name="removeGamePlayer"/>, their game player is removed
    /// first, the way the game removes a player whose pad is unplugged (ControllerConnectionHandler's
    /// RemovePlayerDeferred: activation mode for the Rewired player, then PlayersManager.RemovePlayer). Otherwise a
    /// joined player would stay behind with no controller.
    /// </summary>
    private static void RemoveForGood(RemotePad pad, bool removeGamePlayer)
    {
        try
        {
            int rewiredId = pad.Pad.RewiredPlayerId;
            int gamePlayer = RewiredPlayerInputAssigner.Instance != null ? RewiredPlayerInputAssigner.GetPlayerId(rewiredId) : -1;
            Il2CppTape2Tape.Player? player = removeGamePlayer && gamePlayer >= 0 ? Il2Cpp.PlayersManager.Instance?.GetPlayerFromRewired(pad.Pad.Player) : null;
            if (player != null)
            {
                RewiredPlayerInputAssigner.SetControlsToActivationMode(rewiredId);
                Il2Cpp.PlayersManager.Instance!.RemovePlayer(player);
                ModLog.Info($"Online: removed {pad.Tag}'s game player {gamePlayer}, as the game does for an unplugged pad.");
                TeamSeats.Gone?.Invoke(gamePlayer, pad.Tag); // its id's remembered side put back: ids are reused (OnlineSession.TeamSeats.cs)
            }
        }
        catch (Exception ex)
        {
            ModLog.Error($"Online: could not remove {pad.Tag}'s game player", ex);
        }

        try
        {
            (IReadOnlyList<string> seen, int of) = pad.Pad.ActionsSeen();
            ActionCoverage coverage = ActionCoverage.Of(seen, BoundNames(pad.Pad));
            ModLog.Info($"Online: {pad.Tag}'s {pad.Pad.Kind.ToLowerInvariant()} gave the game {seen.Count} of its {of} actions while it was here" +
                (seen.Count > 0 ? $": {string.Join(", ", seen)}" : string.Empty) +
                $"; bound by its maps but never given: {(coverage.NotYet.Count == 0 ? "none" : string.Join(", ", coverage.NotYet))}.");
        }
        catch (Exception ex)
        {
            ModLog.Warn($"Online: the actions {pad.Tag}'s controller gave the game couldn't be listed ({ModLog.SafeErrorText(ex)}).");
        }

        LogPlayed(pad);
        LogLinkTotals(pad, "this session");
        LogMapsSummary(pad, "this session");
        PromptNames.Forget(pad.Pad.RewiredPlayerId);
        pad.Pad.Dispose();
        ModLog.Info($"Online: {pad.Tag}'s {pad.Pad.Kind.ToLowerInvariant()} is gone.");
    }

    /// <summary>
    /// Host: notes which of the game's actions each friend's virtual controller gave the game this frame, listed when it
    /// goes (<see cref="RemoveForGood"/>): a friend's maps proven to reach the game's actions, triggers and remaps included.
    /// A parked controller stands still, so it isn't read.
    /// </summary>
    private void NoteFriendActions()
    {
        foreach (RemotePad pad in _pads.Values)
        {
            pad.Pad.NoteActions();
        }
    }

    /// <summary>
    /// Parked pads whose friend didn't come back in time go for good, with their game player. Not mid-match: removing a
    /// player there pauses the match for the game's controller screen, so the AI keeps playing for them until it's over.
    /// </summary>
    private void ExpireParked(TimeSpan elapsed)
    {
        if (_parked.Count == 0)
        {
            return;
        }

        _expired.Clear();
        foreach (KeyValuePair<ulong, (RemotePad Pad, TimeSpan Until)> parked in _parked)
        {
            if (elapsed >= parked.Value.Until)
            {
                _expired.Add(parked.Key);
            }
        }

        if (_expired.Count == 0)
        {
            return;
        }

        bool inMatch = !MatchSetup.CurrentMatch(out _).IsNone;
        foreach (ulong id in _expired)
        {
            RemotePad pad = _parked[id].Pad;
            if (inMatch)
            {
                if (!pad.RemovalDeferred)
                {
                    pad.RemovalDeferred = true;
                    ModLog.Info($"Online: {pad.Tag} didn't come back while their seat was kept; their player stays until this match is over " +
                        "(removing it now would pause the match for the controller screen).");
                }

                continue;
            }

            _parked.Remove(id);
            ModLog.Info(pad.RemovalDeferred
                ? $"Online: the match is over, and {pad.Tag} isn't back; their player goes."
                : $"Online: {pad.Tag} didn't come back while their seat was kept.");
            if (id != _self)
            {
                NotifyAbout(id, NoticeId.FriendLeft, pad.DisplayName); // "dropped" was said when their seat was kept; this is the end of it (in the lobby the card says it)
            }

            _standIn.Forget(pad.Pad.RewiredPlayerId);
            RemoveForGood(pad, removeGamePlayer: true);
        }
    }

    /// <summary>
    /// The one-PC test's virtual pad after Ctrl+F6 stopped it: nobody comes back for it, and the menus follow the first player,
    /// which it is, so it goes now with its game player. Mid-match it goes once the match is over (removing a player there
    /// pauses the match for the controller screen).
    /// </summary>
    private void StopSoloPad(RemotePad pad)
    {
        pad.Pad.ApplyNeutral();
        // The keyboard test never took the keyboard from this game's own player, so only the pad needs joining again.
        bool keyboard = pad.Pad.IsKeyboard;
        string what = keyboard ? "virtual keyboard" : "virtual pad";
        if (MatchSetup.CurrentMatch(out _).IsNone)
        {
            ModLog.Info(keyboard
                ? "Online one-PC test: stopped; its virtual keyboard goes now, with its game player. Your keyboard stayed on your own player throughout."
                : "Online one-PC test: stopped; its virtual pad goes now, with its game player. Your pad joins again with its join button.");
            _standIn.Forget(pad.Pad.RewiredPlayerId);
            RemoveForGood(pad, removeGamePlayer: true);
            Notify(NoticeId.OnePcTest, keyboard ? "One-PC test stopped. Your keyboard plays on as before." : "One-PC test stopped. Press your pad's join button to play on.");
            return;
        }

        pad.RemovalDeferred = true;
        _parked[_self] = (pad, _clock.Elapsed);
        ModLog.Info($"Online one-PC test: stopped mid-match; its {what} and game player go once this match is over (removing a player now would pause it).");
        Notify(NoticeId.OnePcTest, "One-PC test stopped. Its player goes once this match is over.");
    }

    /// <summary>
    /// The game is closing and Steam shuts down right after (the game's own quit ends in <c>SteamClient.Shutdown</c>): the
    /// goodbyes just queued to <paramref name="peers"/> (<paramref name="whom"/>, for the log: friends, or a friend's host) get up to
    /// <see cref="GoodbyeWait"/> to leave, or friends would wait out a timeout and then try to rejoin for minutes, and a host would
    /// show a friend who closed their game as dropped, their seat kept.
    /// </summary>
    private static void WaitForGoodbyes(ulong[] peers, string whom)
    {
        var waited = Stopwatch.StartNew();
        int waiting;
        while (true)
        {
            waiting = 0;
            foreach (ulong peer in peers)
            {
                if (SteamMessages.GetSessionInfo(peer, out _, out SteamNetConnectionRealTimeStatus status) == SteamNetworkingConnectionState.Connected &&
                    status.PendingReliable + status.SentUnackedReliable > 0)
                {
                    waiting++;
                }
            }

            if (waiting == 0 || waited.Elapsed >= GoodbyeWait)
            {
                break;
            }

            System.Threading.Thread.Sleep(5);
        }

        ModLog.Info(waiting == 0
            ? $"Online: the goodbye(s) to {whom} are through ({waited.ElapsedMilliseconds} ms)."
            : $"Online: {waiting} of {peers.Length} goodbye(s) to {whom} still on their way after {waited.ElapsedMilliseconds} ms; they see the session end when it times out.");
    }

    /// <summary>
    /// The Steam sessions under the online session (<see cref="SteamSessionWatch"/>): every state change is logged with
    /// Steam's end reason, and a session that broke (reliable messages on it may never have arrived) is mended. A friend
    /// says hello again, so the host resends everything a rejoining friend gets; a host gives the friend
    /// <see cref="RelinkGrace"/> to do that, then asks them to rejoin.
    /// </summary>
    private void WatchSessions(TimeSpan elapsed)
    {
        _sessions.Poll();
        while (_sessions.TryTakeChange(out SteamSessionChange change))
        {
            string tag = Tag(change.Peer);
            string end = change.EndReason != 0 || change.EndDebug.Length > 0 ? $" (end reason {change.EndReason}: {SafeText.Clean(change.EndDebug, 128)})" : string.Empty;
            bool fromHost = _friend != null && change.Peer == _friend.Host;
            if (fromHost && change.To == SteamNetworkingConnectionState.ClosedByPeer)
            {
                _hostClosedLinkAt = elapsed; // with the host out of the lobby and silent, they closed their game (CheckHostClosed)
            }
            else if (fromHost && change.To == SteamNetworkingConnectionState.Connected)
            {
                _hostClosedLinkAt = TimeSpan.MinValue;
            }

            if (!change.Broke)
            {
                ModLog.Info($"Online: Steam session with {tag}: {change.From} -> {change.To}{end}.");
                continue;
            }

            ModLog.Warn($"Online: the Steam session with {tag} broke ({change.From} -> {change.To}{end}); reliable messages sent on it may not have arrived.");
            if (fromHost && _friend!.State == FriendSessionState.Joined)
            {
                ModLog.Info($"{(_solo ? "Online one-PC test" : "Online")}: saying hello to {HostTag()} again, so their game resends what we may have missed (the match, the campaign, menus, progress).");
                _relinking = true;
                StartFriendSession(_friend.Host);
            }
            else if (_host?.Find(change.Peer) is { IsJoined: true })
            {
                ModLog.Info($"Online: waiting up to {RelinkGrace.TotalSeconds:0} s for {tag} to say hello again; otherwise they're asked to rejoin (their player is kept).");
                _linkBrokeAt[change.Peer] = elapsed;
            }
        }

        if (_linkBrokeAt.Count == 0 || _host == null)
        {
            return;
        }

        foreach ((ulong friend, TimeSpan since) in _linkBrokeAt.ToList())
        {
            if (elapsed - since < RelinkGrace)
            {
                continue;
            }

            _linkBrokeAt.Remove(friend);
            if (_host.Find(friend) is { IsJoined: true })
            {
                // Their game may not have noticed: dropped as timed out, they rejoin by themselves (D22) and get everything again.
                ModLog.Warn($"Online: {Tag(friend)} didn't say hello again after their Steam session broke; asking them to rejoin, so they get everything again.");
                _host.Remove(friend, LeaveReason.TimedOut);
            }
        }
    }

    /// <summary>
    /// Friend: a host who closed their side of the Steam session, left the lobby and has been silent for
    /// <see cref="HostGoneAfter"/> closed their game (their goodbye didn't get out before Steam shut down): the session ends
    /// now rather than after a timeout and minutes of trying to rejoin.
    /// </summary>
    private void CheckHostClosed(TimeSpan elapsed)
    {
        if (_friend == null || _solo || _hostClosedLinkAt == TimeSpan.MinValue || !_hostMissing || elapsed - _hostClosedLinkAt <= HostGoneAfter ||
            Now - _friend.Link.LastHeardMicros <= HostGoneAfter.Ticks / (TimeSpan.TicksPerMillisecond / 1000))
        {
            return;
        }

        ModLog.Info($"Online: {HostTag()} closed their side of the link, left the lobby, and nothing has come from them since: they closed their game. Session over.");
        string host = _hostName;
        _failure = LobbyFailure.HostClosed;
        EndFriend();
        NotifyText(NoticeId.SessionOver, $"{host} closed their game, so the session is over. {PadBackNotice()}", $"{HostTag()} closed their game, so the session is over. {PadBackNotice()}");
        NotifyPadBack();
    }

    /// <summary>Where this PC's pad is after a session, for a notice.</summary>
    private string PadBackNotice() => _padBack ? "Your device is back on your game." : "To use your pad here again, unplug it and plug it back in.";

    /// <summary>
    /// The focus guard is on exactly while a session is (and, while it is, the keyboard and mouse are off without focus);
    /// focus changes are logged while online.
    /// </summary>
    private void UpdateFocus()
    {
        bool online = _host != null || _friend != null;
        if (online != FocusGuard.Active)
        {
            if (online)
            {
                FocusGuard.Enter();
            }
            else
            {
                FocusGuard.Exit();
            }
        }

        if (!online && FocusGuard.Idle)
        {
            _wasFocused = true;
            return; // no session, nothing to give back: the window's focus isn't read
        }

        bool focused = Application.isFocused;
        FocusGuard.Update(focused);
        if (!online)
        {
            _wasFocused = true;
            return;
        }

        if (focused != _wasFocused)
        {
            _wasFocused = focused;
            LogFocusChange(focused);
        }
    }

    /// <summary>The friend side's transport as it is now (a rejoin or a simulated drop replaces or cuts it).</summary>
    private sealed class CurrentFriendTransport : ISessionTransport
    {
        private readonly OnlineSession _session;

        public CurrentFriendTransport(OnlineSession session) => _session = session;

        public bool Send(ulong peer, Channel channel, ReadOnlySpan<byte> message) =>
            _session._friendRetrying?.Send(peer, channel, message) ?? false;
    }

    /// <summary>A friend's virtual pad (or keyboard) on the host, with what the reports need between windows.</summary>
    private sealed class RemotePad
    {
        public RemotePad(VirtualController pad, ulong steamId, string displayName)
        {
            Pad = pad;
            SteamId = steamId;
            DisplayName = displayName;
            JoinLabel = $"Online ({Tag})";
        }

        /// <summary>Their virtual pad or keyboard (swapped in place when they come back with the other kind of input).</summary>
        public VirtualController Pad { get; set; }

        /// <summary>Their brawl combo, input update by input update (pad buttons or key-table elements).</summary>
        public BrawlComboWatch Combo { get; } = new();

        /// <summary>The generation of their controls last taken (0: none yet): another one while they're here is a change mid-session.</summary>
        public byte ControlsGeneration { get; set; }

        /// <summary>Their Steam ID.</summary>
        public ulong SteamId { get; }

        /// <summary>
        /// How the log names them: their tag in the lobby's log tags (D127). Made again only when the tags in use changed or
        /// widened, as it's asked every frame (the AI stand-in's tracking).
        /// </summary>
        public string Tag
        {
            get
            {
                LogTags tags = ModLog.Scrub.Tags;
                int digits = tags.Digits;
                if (!ReferenceEquals(tags, _tagsUsed) || digits != _tagDigits)
                {
                    _tagsUsed = tags;
                    _tagDigits = digits;
                    _tag = tags.Player(SteamId);
                }

                return _tag;
            }
        }

        /// <summary>Just the Steam name, for the screen (never the log).</summary>
        public string DisplayName { get; }

        /// <summary>How the join relay names the pad in the log (made once: it's asked every frame).</summary>
        public string JoinLabel { get; }

        private LogTags? _tagsUsed;
        private int _tagDigits;
        private string _tag = string.Empty;

        public int GamePlayer { get; set; } = -1;

        /// <summary>Parked past its time mid-match: it goes once the match is over (removing it mid-match would pause it).</summary>
        public bool RemovalDeferred { get; set; }

        /// <summary>Their link since they last joined (taken each frame, kept after they go, for the departure's totals: OnlineSession.InputReport.cs).</summary>
        public RemoteFriend? Link { get; set; }

        /// <summary>When <see cref="Link"/> started (<c>FrameClock.Seconds</c>).</summary>
        public double LinkSince { get; set; }

        /// <summary>Their maps' full checks, in microseconds (the maps guard's cost per friend).</summary>
        public Core.Snapshots.DelayHistogram MapsChecks { get; } = new();

        /// <summary>The maps guard's quick looks at their maps, their time in all and the longest, in microseconds.</summary>
        public (long Count, long Micros, long Max) MapsLooks { get; set; }

        /// <summary>"Couldn't be put back" warnings written for them (the first few; the rest are counted in their maps summary).</summary>
        public int UnfixedWarned { get; set; }

        /// <summary>Their pad's buttons and axes as played here, with each axis's peak (OnlineSession.InputReport.cs); a keyboard friend's stays empty.</summary>
        public PadUsage Played { get; } = new();

        /// <summary>Triggers whose first pull past the game's threshold reached this PC (bit 1 LT, bit 2 RT), and those not logged yet.</summary>
        public int TriggersArrived { get; set; }

        /// <summary>Triggers that arrived and wait for their log line (OnlineSession.InputReport.cs).</summary>
        public int TriggersToLog { get; set; }

        /// <summary>Their controller's maps as the mod built them, checked for the whole session (OnlineSession.Maps.cs); kept across a swap of controller.</summary>
        public MapsGuard Maps { get; } = new();

        /// <summary>The slowest full check of their maps, in milliseconds.</summary>
        public double SlowestMapsCheckMs { get; set; }

        /// <summary>Checks that found another device on their Rewired player.</summary>
        public int ForeignSeen { get; set; }

        /// <summary>Which other devices on their Rewired player were named in the log (once each kind).</summary>
        public HashSet<string> ForeignLogged { get; } = new(StringComparer.Ordinal);

        public TimeSpan LastReport { get; set; }

        public long LastPackets { get; set; }

        public long LastBytes { get; set; }

        public int HoldsLogged { get; set; }

        /// <summary>Starts a fresh report window, so the first report after a join shows real rates.</summary>
        public void ResetReport(TimeSpan now, long bytesIn)
        {
            LastReport = now;
            LastPackets = 0;
            LastBytes = bytesIn;
        }
    }

    /// <summary>One sender's refusals since the last line about them in the log.</summary>
    private sealed class Refusals
    {
        public Refusals(string tag, RejectReason reason, TimeSpan loggedAt)
        {
            Tag = tag;
            Reason = reason;
            LoggedAt = loggedAt;
        }

        /// <summary>The sender's log tag.</summary>
        public string Tag { get; }

        public RejectReason Reason { get; }

        /// <summary>Refusals not in the log yet.</summary>
        public int Since { get; set; }

        public TimeSpan LoggedAt { get; set; }
    }
}
#endif
