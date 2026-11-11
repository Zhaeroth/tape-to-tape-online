#if HAS_INTEROP
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Il2CppBishopGames.UI;
using Il2CppTape2Tape.Customization.UI;
using Il2CppTape2Tape.Hockey.UI;
using MelonLoader;
using T2TOnline.Core.Campaign;
using T2TOnline.Core.Diagnostics;
using T2TOnline.Core.Lobby;
using T2TOnline.Core.Protocol;
using T2TOnline.Core.Session;
using T2TOnline.Plugin.Campaign;
using T2TOnline.Plugin.Campaign.Menus;
using T2TOnline.Plugin.Campaign.Scenes;
using T2TOnline.Plugin.Client;
using T2TOnline.Plugin.Diagnostics;
using T2TOnline.Plugin.Input;
using T2TOnline.Plugin.Match;
using T2TOnline.Plugin.NativeLobby;
using T2TOnline.Plugin.Streaming;
using T2TOnline.Steam;

namespace T2TOnline.Plugin.Online;

// One session flow for every way in: the native rows, F7, Shift+F7, F1, Steam's invites and Join Game, +connect_lobby. LobbyFlow (Core) decides;
// this runs its effects and tells it what the game is doing. A friend joins in two steps: in the lobby their device, saves and game stay theirs; at Start, BeginPlay
// takes what following needs, and EndPlay gives it all back once their game is home, exactly once, on every path. The host keeps the board, the barrier and the game's own start
// calls; everything that ends a game meets at the host's title load. The screens are a view (ILobbyScreens, null-safe): nothing here depends on one being up. Apart from what the
// session does underneath (OnlineSession.cs).
internal sealed partial class OnlineSession
{
    private static readonly TimeSpan TitleSignalInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan BoardInterval = TimeSpan.FromMilliseconds(250);
    // In a game nobody sees a board, and it would otherwise be built four times a second and sent, reliably, at each ping's change, through a whole match. What matters still goes at once (_nextBoard, the flow's PublishBoard).
    private static readonly TimeSpan BoardInGameInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan SaveRowInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan GameFactsInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan HostingTooInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan HostDeviceInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan PickerInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan InviteMarkFor = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan SettingsSavesFor = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan GameStartWait = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan HomeWatchdog = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan PressToJoinWait = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan LostToastInterval = TimeSpan.FromSeconds(20);
    private const long SnapshotMicros = 100_000;
    private const ulong NowhereLobby = 109775244444444444; // a lobby's id in form, with no lobby behind it: the check tour's join

    private readonly LobbyFlow _flow = new();
    private readonly LobbyMeter _meter = new();
    private readonly HashSet<ulong> _removed = new();
    private readonly HashSet<ulong> _following = new();
    private readonly HashSet<ulong> _arriving = new();
    private readonly LeavingMembers _leaving = new();
    private readonly Dictionary<ulong, (string Name, TimeSpan Until)> _invited = new();
    private readonly Dictionary<ulong, string> _hostingToo = new();
    private LobbyActions? _actions;
    private LobbyBoardBuilder? _board;
    private LobbyBoardMessage? _lastBoard;
    private long _lastBoardAt;
    private LobbyMode _lobbyMode;
    private bool _startNewRun;
    private bool _ready;
    private HostActivity _activity;
    private LobbyResult _result = LobbyResult.None;
    private LobbySaveRow _saveRow = LobbySaveRow.None;
    private TimeSpan _nextSaveRead;
    private TimeSpan _nextBoard;
    private TimeSpan _nextTitleSignal;
    private TimeSpan _nextGameFacts;
    private TimeSpan _nextHostingToo;
    private TimeSpan _nextHostDevice;
    private TimeSpan _nextPickerRead;
    private (InputKind Kind, byte Family) _hostDevice = (InputKind.KeyboardMouse, 0);
    private IReadOnlyList<PickerFriend> _picker = Array.Empty<PickerFriend>();
    private IReadOnlyList<LobbyListing> _listings = Array.Empty<LobbyListing>();
    private int _listingsVersion = -1;
    private int _listingsInvites = -1;
    private TimeSpan _listingsReadAt = TimeSpan.MinValue;
    private TitleScreen? _flowTitle;
    private bool _titleSceneArrived;
    private bool _titleReady;
    private bool _titleWasOff;
    private bool _playOn;
    private bool _flowEnding;
    private string? _flowEndReason;
    private LobbyFailure? _failure;
    private string _failureHost = string.Empty;
    private ulong _failureHostId;
    private string _failureTheirs = string.Empty;
    private ulong _joinLobby;
    private string _joinName = string.Empty;
    private ulong _helloHost;
    private LobbyRoute _route = LobbyRoute.Key;
    private bool _metered;
    private bool _gameStartDue;
    private TimeSpan _gameStartBy;
    private string _startWhat = string.Empty;
    private string _waitingFor = string.Empty;
    private TimeSpan _homeSince = TimeSpan.MinValue;
    private bool _wasLost;
    private LobbyGameFacts _gameFacts = new() { OurScore = -1, TheirScore = -1 };
    private SaveAllowance? _closingSettings;
    private TimeSpan _closingSettingsUntil;
    private LobbyState _snapshot = LobbyState.None;
    private int _snapshotFrame = -1;
    private long _snapshotAt;
    private bool _gameWasImplicit;
    private bool _newRunFlagLogged;
    private long _dropped;
    private bool _heldSaid;
    private TimeSpan _nextHeldCheck;
    private TimeSpan _pressToJoinBy = TimeSpan.MinValue;
    private TimeSpan _nextPressToJoinLook;
    private TimeSpan _nextLostToast;
    private int _gameFactsMatch;

    /// <summary>The native screens, or null: a session behaves the same with none up. The runtime sets it.</summary>
    public ILobbyScreens? Screens { get; set; }

    /// <summary>What the native screens call: this session's <see cref="ILobbyActions"/>.</summary>
    public ILobbyActions Actions => _actions ??= new LobbyActions(this);

    /// <summary>True while this PC hosts or is in a lobby: the game's onboarding prompt is held off meanwhile (<see cref="TitleCursor"/>).</summary>
    public static bool LobbyUp { get; private set; }

    /// <summary>The game's end of a campaign match, as its <c>EndOfGameContext</c> says (from the progress patch that reads it): run won, run failed or conceded.</summary>
    internal static Action<bool, bool>? RunEndNoted { get; private set; }

    /// <summary>The flow, for the check tour and the panel's reading: never for changing.</summary>
    internal LobbyFlow Flow => _flow;

    /// <summary>A friend's device goes to the host's game only in a play period (the one-PC test has none: it starts with the session).</summary>
    private bool DeviceWithHost => _solo || _playOn;

    /// <summary>Listens for the title's scene: the return's first moment. Once, from the constructor.</summary>
    private void InstallLobbyFlow()
    {
        MelonEvents.OnSceneWasLoaded.Subscribe((_, name) =>
        {
            if (name == CampaignGame.TitleScene)
            {
                _titleSceneArrived = true;
            }
        });
        LobbyShell.HandOverBackedOut += () =>
        {
            // Only a host's says anything: no flow runs on a friend's title, so its hand-over "ends" half a second in, whatever the host is doing.
            if (_flow.Role == LobbyRole.Host)
            {
                ModLog.Info("Lobby: start backed out (the shell is on top again).");
            }
        };
        RunEndNoted = NoteCampaignEnd;
    }

    // ---- Ways in ----

    /// <summary>Every way of hosting: the Steam lobby comes from the flow's <see cref="LobbyEffect.CreateLobby"/>.</summary>
    private void BeginHost(LobbyRoute route)
    {
        BeforeSessionStarts?.Invoke();
        ArmIceProbe(); // DevTools only (OnlineSession.IceProbe.cs)
        ResetIfStale();
        _route = route;
        _metered = true;
        _failure = null;
        Run(_flow.Handle(LobbyEvent.HostPressed, Now));
    }

    /// <summary>Every way of joining: the Steam lobby comes from the flow's <see cref="LobbyEffect.JoinLobby"/>.</summary>
    private void BeginJoin(ulong lobby, string name, LobbyRoute route)
    {
        if (TourRefusesJoin(lobby))
        {
            return; // a check tour never joins anyone's game (OnlineSession.TourHolds.cs)
        }

        BeforeSessionStarts?.Invoke();
        ArmIceProbe(); // DevTools only (OnlineSession.IceProbe.cs)
        ResetIfStale();
        _joinLobby = lobby;
        _joinName = name;
        _failureHost = name;
        _failureHostId = 0;
        _failureTheirs = string.Empty;
        _route = route;
        _metered = true;
        _failure = null;
        Run(_flow.Handle(LobbyEvent.JoinPressed, Now));
    }

    /// <summary>A flow out of step with the link (nothing is in a lobby, and it thinks otherwise) can't start anything: it's ended first.</summary>
    private void ResetIfStale()
    {
        if (_flow.Role != LobbyRole.None && !InLobby)
        {
            ModLog.Warn($"Lobby: the flow was still {(_flow.Role == LobbyRole.Host ? _flow.Host : _flow.Friend)} with no lobby; ending it first.");
            FlowLeaving("no lobby");
        }
    }

    /// <summary>The Steam lobby refused or never answered: <see cref="LobbyEvent.LobbyFailed"/> with why.</summary>
    private void LobbyCallFailed()
    {
        bool hosting = _flow.Host == LobbyHostState.Creating;
        _failure = hosting ? LobbyFailure.CreateFailed : _link.LastEnterResponse is int response ? JoinFailures.FromSteamEnter(response) : LobbyFailure.Unreachable;
        if (!hosting)
        {
            ModLog.Info(LobbyMeter.JoinFailed(_failure.Value, RowAgeSeconds()));
        }

        Run(_flow.Handle(LobbyEvent.LobbyFailed, Now));
    }

    /// <summary>The row's age for the join failure's line: how long ago the browser's list last changed, or -1 when it didn't come from a row.</summary>
    private int RowAgeSeconds() => _route == LobbyRoute.Browser && _listingsReadAt != TimeSpan.MinValue ? (int)Math.Max(0, (_clock.Elapsed - _listingsReadAt).TotalSeconds) : -1;

    // ---- Every frame ----

    /// <summary>Every frame, after the session's events: the flow's clock, then what the title says, or (friend) whether its game is home.</summary>
    private void UpdateLobbyFlow(long now, TimeSpan elapsed)
    {
        LobbyUp = _flow.Role != LobbyRole.None;
        LobbyQuestion.Tick();
        DisposeClosingSettings(elapsed);
        if (_flow.Role != LobbyRole.None)
        {
            Run(_flow.Update(now));
            if (_flow.Host != LobbyHostState.Idle)
            {
                HostFlowTick(now, elapsed);
            }
            else if (_flow.Friend != LobbyFriendState.Idle)
            {
                FriendFlowTick(now, elapsed);
            }
        }

        TickSoloFlow(now, elapsed);
        LogFlow();
    }

    private void LogFlow()
    {
        while (_flow.TryTakeChange(out LobbyFlowChange change))
        {
            ModLog.Info(change.Line);
        }
    }

    private void HostFlowTick(long now, TimeSpan elapsed)
    {
        ClearNewRunFlag();
        TickTeamSeats(); // late joiners onto the run's team on its map (OnlineSession.TeamSeats.cs)
        if (_titleSceneArrived)
        {
            _titleSceneArrived = false;
            _titleWasOff = false;
            Run(_flow.Handle(LobbyEvent.TitleSceneLoaded, now));
        }

        if (_gameStartDue)
        {
            TryCallGameStart(elapsed);
        }

        if (elapsed >= _nextTitleSignal)
        {
            _nextTitleSignal = elapsed + TitleSignalInterval;
            TitleSignal(now);
        }

        if (elapsed >= _nextGameFacts)
        {
            _nextGameFacts = elapsed + GameFactsInterval;
            ReadGameFacts();
        }

        if (elapsed >= _nextBoard)
        {
            _nextBoard = elapsed + (_flow.Host == LobbyHostState.InGame ? BoardInGameInterval : BoardInterval);
            PublishBoardNow(now);
            NoteArrivals();
            // As a press of Start is: not under the host's own Settings, or with the title's warm-ups running.
            if (_flow.IsWaitingForEveryone && _titleReady && LobbyPeople.FirstNotHere(Snapshot()) == null)
            {
                Run(_flow.Handle(LobbyEvent.EveryoneHere, now));
                ModLog.Info(LobbyMeter.StartedWithout(_waitingFor, _flow.LastWaitMicros, wentOnAnyway: false));
            }
        }

        if (elapsed >= _nextHostingToo)
        {
            _nextHostingToo = elapsed + HostingTooInterval;
            NoteHostingToo();
        }
    }

    /// <summary>What's true of the title now, for the flow (<see cref="LobbySignals.TitleEvent"/>), and whether Start can run.</summary>
    private void TitleSignal(long now)
    {
        LobbyTitleView view = ReadTitleView(out TitleScreen? title);
        if (!view.SceneLoaded)
        {
            _titleWasOff = true;
        }
        else if (_titleWasOff)
        {
            // The scene event's flag was missed (the session's first frame after a load): the same edge.
            _titleWasOff = false;
            _titleSceneArrived = false;
            Run(_flow.Handle(LobbyEvent.TitleSceneLoaded, now));
        }

        if (LobbySignals.TitleEvent(view, _flow.Host, _gameStartDue) is { } signal)
        {
            Run(_flow.Handle(signal, now));
        }

        _titleReady = title != null && LobbySignals.StartCanRun(view, RunSync.Instance?.ReadyToStart(title) == true);
    }

    private LobbyTitleView ReadTitleView(out TitleScreen? title)
    {
        title = null;
        if (!CampaignGame.TitleLoaded())
        {
            _flowTitle = null;
            return default;
        }

        title = _flowTitle != null && !_flowTitle.WasCollected && _flowTitle.isActiveAndEnabled ? _flowTitle : (_flowTitle = CampaignGame.Title());
        if (title == null || !title.m_HasStarted)
        {
            return new LobbyTitleView { SceneLoaded = true };
        }

        bool start = false;
        bool other = false;
        Il2CppSystem.Collections.Generic.List<string>? menus = title.m_MenusOverSelf;
        for (int i = 0; menus != null && i < menus.Count; i++)
        {
            string id = menus[i];
            if (LobbySignals.IsStartMenu(id))
            {
                start = true;
            }
            else if (!LobbyShell.IsShellId(id))
            {
                other = true;
            }
        }

        // Only during a start: the game's prompts over the title aren't on its list (StartCampaignTutorialFlow's is shown by the UI module, verified in the game's code).
        bool onTop = false;
        if (_flow.Host == LobbyHostState.GameFlow && !start && !other)
        {
            AbstractMenu? top = LobbyShell.TopShown();
            onTop = top != null && !LobbyShell.IsShellId(MenuGame.MenuId(top));
        }

        return new LobbyTitleView
        {
            SceneLoaded = true,
            Up = true,
            Leaving = title.m_IsExiting,
            StartingRun = title.m_IsStartingRun,
            StartMenuOver = start,
            OtherMenuOver = other,
            Clicking = title.m_IsClickingButton,
            Refreshing = title.m_IsRefreshingCampaignState,
            Preparing = ExhibitionScreen.PreparingTitleMenus(),
            MenuOnTop = onTop,
        };
    }

    private void FriendFlowTick(long now, TimeSpan elapsed)
    {
        if (_pressToJoinBy != TimeSpan.MinValue)
        {
            TickPressToJoin(elapsed);
        }

        // A frozen picture for up to two minutes, and a toast's few seconds: said again while it lasts.
        if (_flow.Friend == LobbyFriendState.Lost && _flow.PlayActive && elapsed >= _nextLostToast)
        {
            _nextLostToast = elapsed + LostToastInterval;
            NotifyAbout(_hostSteamId, NoticeId.HostReconnecting, _hostName);
        }

        if (_wasLost && _flow.Friend != LobbyFriendState.Lost)
        {
            _wasLost = false;
            Screen("the reconnecting state", s => s.ShowReconnecting(false));
            if (_flow.PlayActive)
            {
                NotifyAbout(_hostSteamId, NoticeId.HostBack, _hostName);
            }
        }

        if (_flow.Friend != LobbyFriendState.Returning)
        {
            _homeSince = TimeSpan.MinValue;
            return;
        }

        if (_homeSince == TimeSpan.MinValue || OwnUnlocks.HoldsWayHome)
        {
            // This player's own new-unlocks screen holds the way home (within its own limits): the watchdog counts from its close.
            _homeSince = elapsed;
        }

        if (elapsed < _nextTitleSignal)
        {
            return;
        }

        _nextTitleSignal = elapsed + TitleSignalInterval;
        var facts = new OwnTitleFacts
        {
            Following = _campaign.Following,
            // Nothing of the host's came yet (a back-out before our ack reached them): nothing moved this game.
            TargetIsTitle = _campaign.View is not { } target || target.Screen == Campaign.Screens.Title,
            ShowingTitle = _campaign.Showing is not { } showing || showing.Screen == Campaign.Screens.Title,
            OnTitleScene = WorldMap.SceneName == WorldMap.TitleScene && CampaignGame.TitleLoaded(),
            SceneLoading = WorldMap.Loading,
            MatchBusy = _relay.FriendBusy || MatchSetup.Loading,
        };
        if (LobbySignals.OwnTitleBack(facts))
        {
            Run(_flow.Handle(LobbyEvent.OwnTitleBack, now));
        }
        else if (elapsed - _homeSince >= HomeWatchdog)
        {
            // A following that never stops is the worst of this: lock, guard and device are given back whatever kept this game away from home.
            ModLog.Warn($"Lobby: this game isn't home {HomeWatchdog.TotalSeconds:0} s after {HostTag()} went back to their lobby ({facts}); giving your device and saves back anyway.");
            Run(_flow.Handle(LobbyEvent.OwnTitleBack, now));
        }
    }

    // ---- The flow's effects ----

    /// <summary>Runs what the flow asks, in order. <paramref name="peer"/>: the friend an event was about. A failure is logged, and the next effect still runs.</summary>
    private void Run(IReadOnlyList<LobbyEffect> effects, ulong peer = 0)
    {
        if (effects.Count == 0)
        {
            return;
        }

        LogFlow();
        bool ending = false;
        for (int i = 0; i < effects.Count; i++)
        {
            ending |= effects[i] == LobbyEffect.EndSession;
        }

        for (int i = 0; i < effects.Count; i++)
        {
            LobbyEffect effect = effects[i];
            try
            {
                RunEffect(effect, peer, ending);
            }
            catch (Exception ex)
            {
                ModLog.Error($"Lobby: {effect} failed", ex);
            }
        }

        LogFlow();
    }

    private void RunEffect(LobbyEffect effect, ulong peer, bool ending)
    {
        long now = Now;
        switch (effect)
        {
            case LobbyEffect.ShowLobby:
                // Not after this list's own session failed to start (StartHosting, below): there's no lobby to show.
                if (_flow.Role != LobbyRole.None)
                {
                    Screen("showing the lobby", s => s.ShowLobby());
                }

                break;
            case LobbyEffect.HideLobby:
                Screen("hiding the lobby", s => s.HideLobby());
                break;
            case LobbyEffect.CloseLobbyForStart:
                Screen("handing over for the start", s => s.HandOverForStart(_startWhat.Length > 0 ? _startWhat : "the host's game"));
                break;
            case LobbyEffect.EndSession:
                if (!_flowEnding)
                {
                    LeaveAll(_flowEndReason ?? "the lobby's flow ended");
                }

                break;
            case LobbyEffect.ShowFailure:
                SayFailure();
                break;
            case LobbyEffect.CreateLobby:
                _link.Create(MaxLobbyMembers, _clock.Elapsed, TourLobbyPrivate()); // private while a check tour runs (OnlineSession.TourHolds.cs)
                break;
            case LobbyEffect.StartHosting:
                try
                {
                    StartHosting();
                    _board = new LobbyBoardBuilder(_self, MaxLobbyMembers);
                    ModLog.Info(_meter.Entered(_route, now));
                }
                catch (Exception ex)
                {
                    // Not an effect to log and go on from: the flow would sit at Open with no session under it.
                    ModLog.Error("Lobby: hosting couldn't start; the lobby is closed", ex);
                    _failure = LobbyFailure.CreateFailed;
                    LeaveAll("hosting couldn't start");
                    SayFailure();
                }

                break;
            case LobbyEffect.ClearNewRunFlag:
                _newRunFlagLogged = false;
                ClearNewRunFlag();
                break;
            case LobbyEffect.PublishBoard:
                PublishBoardNow(now);
                break;
            case LobbyEffect.BackupAgain:
                RunSync.Instance?.BackupAgain("before this Start");
                break;
            case LobbyEffect.AssignGamePlayers:
                AssignGamePlayers();
                break;
            case LobbyEffect.CallGameStart:
                _gameStartDue = true;
                _gameStartBy = _clock.Elapsed + GameStartWait;
                TryCallGameStart(_clock.Elapsed);
                break;
            case LobbyEffect.FriendFollowing:
                HostFriendFollows(peer);
                break;
            case LobbyEffect.ShowOnlineGroup:
                Screen("back on the Online group", s => s.ShowOnlineGroup());
                break;
            case LobbyEffect.JoinLobby:
                _link.Join(_joinLobby, _joinName, _clock.Elapsed);
                break;
            case LobbyEffect.SayHello:
                bool saidHello = false;
                try
                {
                    saidHello = StartFriend(_helloHost, _joinName, solo: false);
                }
                catch (Exception ex)
                {
                    // Not an effect to log and go on from: the flow has no clock of its own in Greeting, so with no hello on its way it would wait for good.
                    ModLog.Error("Lobby: saying hello failed; the join ends", ex);
                }

                if (!saidHello)
                {
                    _failure = LobbyFailure.Unknown;
                    Run(_flow.Handle(LobbyEvent.Refused, Now));
                }

                break;
            case LobbyEffect.BeginPlay:
                BeginPlay();
                break;
            case LobbyEffect.EndPlay:
                // A session's end gives it all back itself, in its proven order (EndFriendSide): only a return home is this alone.
                if (!ending)
                {
                    EndPlay("the host is back at their lobby");
                }

                break;
            case LobbyEffect.SendMember:
                SendMember();
                break;
            case LobbyEffect.ShowReconnecting:
                _wasLost = true;
                Screen("the reconnecting state", s => s.ShowReconnecting(true));
                if (_flow.PlayActive)
                {
                    _nextLostToast = _clock.Elapsed + LostToastInterval;
                    NotifyAbout(_hostSteamId, NoticeId.HostReconnecting, _hostName); // in play there's no lobby screen to say so
                }

                break;
            case LobbyEffect.SayLost:
                _failure = LobbyFailure.HostLost;
                SayFailure();
                break;
            case LobbyEffect.ShowJoinList:
                ulong lobby = _joinLobby;
                Screen("the Join list", s => s.ShowJoinList(lobby));
                break;
            case LobbyEffect.CloseJoinList:
                Screen("closing the Join list", s => s.CloseJoinList());
                break;
        }
    }

    /// <summary>A screen's call: the session never depends on one, so a failure is logged and it goes on (<see cref="TryScreens"/>).</summary>
    private void Screen(string what, Action<ILobbyScreens> call) => _ = TryScreens(what, call);

    /// <summary>The remembered failure, on the native screens (the status box when they can't carry it: <see cref="NotifyFailure"/>).</summary>
    private void SayFailure()
    {
        LobbyFailure failure = _failure ?? LobbyFailure.Unknown;
        // Empty stays empty: JoinFailures says the plain line when the other's version isn't known.
        string theirs = _failureTheirs.Length > 0 ? ReleaseVersion.ForScreen(_failureTheirs) : string.Empty;
        FailureMessage message = JoinFailures.Message(failure, _failureHost, ReleaseVersion.ForScreen(_identity.ModVersion), theirs);
        // The log's copy names the host by tag when this PC got as far as their lobby, else as "the host" (D127), and their version by its shape (a lobby's own text).
        string hostTag = _failureHost.Length == 0 ? string.Empty : _failureHostId != 0 ? Tag(_failureHostId) : "the host";
        string theirsForLog = _failureTheirs.Length > 0 ? ReleaseVersion.ForScreen(SentText.Release(_failureTheirs)) : string.Empty;
        ModLog.Info($"Lobby: the failure said: {failure}.");
        NotifyFailure(message, JoinFailures.Message(failure, hostTag, ReleaseVersion.ForScreen(_identity.ModVersion), theirsForLog));
    }

    /// <summary>
    /// The flow ends with the session, whatever ended it (F7, an error, the host closing, Steam): told first, so screens and the play period's end come before the session's
    /// own. A reason remembered in <see cref="_failure"/> is said; a plain leave isn't.
    /// </summary>
    private void FlowLeaving(string reason)
    {
        if (_flow.Role == LobbyRole.None || _flowEnding)
        {
            return;
        }

        _flowEnding = true;
        _flowEndReason = reason;
        try
        {
            Run(_flow.Handle(_failure != null ? LobbyEvent.SessionEnded : LobbyEvent.LeavePressed, Now));
        }
        finally
        {
            _flowEnding = false;
            _flowEndReason = null;
            LobbyUp = false;
        }
    }

    /// <summary>Everything the session's lobby part keeps, at its end: nothing of it outlives the session.</summary>
    private void ForgetLobby()
    {
        if (_metered)
        {
            _metered = false;
            ModLog.Info(_meter.Summary());
        }

        _board = null;
        _lastBoard = null;
        _removed.Clear();
        _following.Clear();
        _arriving.Clear();
        _leaving.Clear();
        _invited.Clear();
        _hostingToo.Clear();
        _ready = false;
        _lobbyInputSaid = false;
        _activity = HostActivity.None;
        _result = LobbyResult.None;
        _saveRow = LobbySaveRow.None;
        _gameFacts = new LobbyGameFacts { OurScore = -1, TheirScore = -1 };
        _gameStartDue = false;
        _gameWasImplicit = false;
        _startWhat = string.Empty;
        _homeSince = TimeSpan.MinValue;
        _pressToJoinBy = TimeSpan.MinValue;
        _wasLost = false;
        _titleSceneArrived = false;
        _titleWasOff = false;
        _titleReady = false;
        _snapshot = LobbyState.None;
        _snapshotFrame = -1;
        _flowTitle = null;
        _advert = LobbyAdvert.Default;
        LobbyUp = false;
        LobbyQuestion.Cancel("the session ended");
        DisposeClosingSettings(TimeSpan.MaxValue); // after an error nothing ticks this any more
    }

    // ---- A friend's two steps ----

    /// <summary>The friend's first step: their device stays theirs, and nothing goes to the host.</summary>
    private void SayDeviceStays(string device) =>
        ModLog.Info($"Lobby: your device stays yours until Start ({device} plays your own game; nothing goes to {HostTag()} until then).");

    /// <summary>
    /// The host's game is starting: following begins. Whatever of this player's is still open put away (a remapper first, so what it changed is what's read), input chosen again
    /// (a pad swapped or remapped in the lobby), backup, save guard, run saves skipped, UI lock (all by <c>StartFollowing</c>), the device handed over, prompts pinned. Ends in <see cref="LobbyEvent.PlayReady"/>, or
    /// <see cref="LobbyEvent.PlayFailed"/>, whose <see cref="LobbyEffect.EndPlay"/> and session's end undo whatever got done.
    /// </summary>
    private void BeginPlay()
    {
        if (_playOn)
        {
            Run(_flow.Handle(LobbyEvent.PlayReady, Now));
            return;
        }

        try
        {
            FriendSession friend = _friend ?? throw new InvalidOperationException("no session to follow in");
            long sentBefore = friend.PadPackets + friend.KeyboardPackets;
            _playOn = true;
            CloseOwnMenusForPlay();
            ChooseInputAgain(friend);
            _campaignFromHost = _campaign.StartFollowing(new CurrentFriendTransport(this), friend.Host, _hostName);
            string device = _localPad != null ? $"your pad \"{SafeText.Clean(_localPad.Joystick.name, 48)}\"" : "your keyboard and mouse";
            if (!_ownMenu.HoldsDevice)
            {
                _localPad?.Detach();
                _localKeyboard?.Detach();
            }

            _padBack = true;
            _localPadWasConnected = true;
            PinPrompts();
            ModLog.Info($"Lobby: your device goes to the host's game ({device}; {sentBefore} input packet(s) went to {HostTag()} before this).");
            Run(_flow.Handle(LobbyEvent.PlayReady, Now));
        }
        catch (Exception ex)
        {
            ModLog.Error("Lobby: getting ready to follow failed; everything it did is given back", ex);
            _failure = LobbyFailure.Unknown;
            Run(_flow.Handle(LobbyEvent.PlayFailed, Now));
        }
    }

    /// <summary>
    /// What this player plays with now, read again: the lobby is their own game, so they may have swapped pads or remapped. Another family goes through the session's pad device
    /// message (<see cref="SyncPadDevice"/>), another kind through a second hello (the host's <c>MatchInput</c> swaps in place). Nothing to play with now keeps what the hello
    /// said: following still works, and their pad is looked for (<see cref="WatchLocalPad"/>).
    /// </summary>
    private void ChooseInputAgain(FriendSession friend)
    {
        InputKind before = _localKeyboard != null ? InputKind.KeyboardMouse : InputKind.Gamepad;
        if (!ChooseInput(out LocalPad? pad, out LocalKeyboard? keyboard))
        {
            ModLog.Warn("Lobby: nothing to play with right now; keeping what your hello said, and your pad is looked for.");
            return;
        }

        _localPad = pad;
        _localKeyboard = keyboard;
        _padSwapJoystick = int.MinValue;
        _controls = keyboard?.Controls ?? PadRemapReader.Read(pad!, NextControlsGeneration());
        NotePadName();
        InputKind after = keyboard != null ? InputKind.KeyboardMouse : InputKind.Gamepad;
        if (after != before)
        {
            ModLog.Info($"Lobby: you play with your {(after == InputKind.KeyboardMouse ? "keyboard" : "pad")} now, not what your hello said; saying hello again, so {HostTag()}'s game swaps your controller.");
            _relinking = true;
            StartFriendSession(friend.Host);
            return;
        }

        if (_controls != null)
        {
            friend.SetBindings(_controls);
        }
    }

    /// <summary>
    /// What's open of this player's own at Start goes, all of it: nothing may be left up that nobody can answer under the lock. Their remapper with its own close (it's Unity UI,
    /// on no menu's list: left open, it stays over the host's game until they leave); then each menu over the lobby's shell, top first (a prompt over Settings, say). A failure here
    /// costs a menu left up, never the session. Off the title (their own game, started under a failed shell) nothing: the host's scene takes over, and a match's own menus aren't
    /// theirs to close. Nor during the title's warm-ups: what's up then is the warm-up's.
    /// </summary>
    private void CloseOwnMenusForPlay()
    {
        if (!CampaignGame.TitleLoaded() || ExhibitionScreen.PreparingTitleMenus())
        {
            return;
        }

        Step("closing this player's remapper", () =>
        {
            var settings = new OwnSettingsMenu();
            if (settings.Find() != null && settings.OpenMapper is { } mapper)
            {
                ModLog.Info("Lobby: this PC's remapper was open at Start; closed with its own close, which saves what was changed (your controls are read next).");
                mapper.Close(true);
            }
        });
        Step("closing what was open at Start", () =>
        {
            foreach (AbstractMenu menu in MenusOverShell())
            {
                Step($"closing {SafeText.Clean(MenuGame.MenuId(menu), 48)}", () => CloseOwnMenu(menu));
            }
        });
    }

    /// <summary>What's shown over the lobby's shell, top first. With no shell to tell by, only the top one (nothing says what else on the game's list is this player's).</summary>
    private static List<AbstractMenu> MenusOverShell()
    {
        var over = new List<AbstractMenu>();
        AbstractMenu? shell = LobbyShell.Menu;
        UIModule? ui = MenuGame.Module();
        if (shell == null || ui == null)
        {
            if (LobbyShell.TopShown() is { } top)
            {
                over.Add(top);
            }

            return over;
        }

        var menus = new Il2CppSystem.Collections.Generic.List<AbstractMenu>();
        ui.GetAllMenus(menus.Cast<Il2CppSystem.Collections.Generic.IList<AbstractMenu>>());
        for (int i = 0; i < menus.Count; i++)
        {
            AbstractMenu? menu = menus[i];
            if (menu != null && !menu.WasCollected && !menu.IsHidden && !menu.IsHiding && menu.EffectiveRenderingOrder > shell.EffectiveRenderingOrder)
            {
                over.Add(menu);
            }
        }

        over.Sort(static (a, b) => b.EffectiveRenderingOrder.CompareTo(a.EffectiveRenderingOrder));
        return over;
    }

    /// <summary>
    /// One menu of this player's at Start: the game's onboarding question with its safe answer (No: nothing starts, and it isn't asked again here); their own Settings or another title
    /// menu, as its Back would, its files allowed a moment (the guard comes on next, and its close saves them); any other prompt goes unanswered.
    /// </summary>
    private void CloseOwnMenu(AbstractMenu top)
    {
        string id = MenuGame.MenuId(top);
        if (LobbyShell.IsShellId(id))
        {
            return;
        }

        CustomizableYesNoPrompt? prompt = top.TryCast<CustomizableYesNoPrompt>();
        TitleScreen? title = CampaignGame.Title();
        if (prompt != null && title != null && IsOnboarding(prompt, title))
        {
            ModLog.Info("Lobby: the game's onboarding question was still up at Start; closed with No (it stays yours to play later).");
            prompt.OnNoClick(null!);
            return;
        }

        _closingSettings ??= SaveGuard.Allow("your own settings, closing as the host's game starts", OwnSettingsFiles.Names.ToArray());
        _closingSettingsUntil = _clock.Elapsed + SettingsSavesFor;
        ModLog.Info($"Lobby: {SafeText.Clean(id, 48)} was open here at Start; closed, as nothing here can answer it under the lock.");
        _ = top.Hide();
    }

    private static bool IsOnboarding(CustomizableYesNoPrompt prompt, TitleScreen title)
    {
        try
        {
            string? shown = prompt.m_TitleText?.text;
            return !string.IsNullOrEmpty(shown) && shown == title.m_StartBasicTutorialTitle?.ToString();
        }
        catch (Exception ex)
        {
            ModLog.Warn($"Lobby: couldn't tell whether the prompt up is the onboarding question ({ex.GetType().Name}); closing it unanswered.");
            return false;
        }
    }

    private void DisposeClosingSettings(TimeSpan elapsed)
    {
        if (_closingSettings != null && elapsed >= _closingSettingsUntil)
        {
            SaveAllowance saves = _closingSettings;
            _closingSettings = null;
            Step("ending the settings' allowance", saves.Dispose);
        }
    }

    /// <summary>
    /// The second step's end: BeginPlay's exact reverse, once: following off (the progress flush, the save check, the guard, the lock), the device back, prompts unpinned. The
    /// friend's session and seat stay: they're in the lobby again. A session's own end does the same steps (<see cref="EndFriendSide"/>).
    /// </summary>
    private void EndPlay(string reason)
    {
        if (!_playOn)
        {
            return;
        }

        _playOn = false;
        _ready = false; // a mark is about now: "ready" from before a game isn't ready for the next one
        Step("ending the question about the host's match", () => EndFollowQuestion(reason));
        Step("ending your own menu", () => EndOwnMenu(reason));

        // The session outlives this period: an own menu open as it ended said the device is away, and only a pad's input update ever says otherwise (a keyboard friend's next period would send nothing).
        Step("telling the host your device isn't away", () => _friend?.SetPadConnected(true));
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
        Step("stopping following the host's campaign", () => _campaign.StopFollowing(reason, _gameClosing));
        _campaignFromHost = null;
        ModLog.Info($"Lobby: your device and saves are yours again ({reason}; {_dropped} campaign message(s) dropped in the lobby so far).");
        LobbyShell.ReassertFocus("the friend's game is home");
    }

    /// <summary>Friend: the host's campaign messages reach following only; in the lobby they're taken and dropped, not counted as undelivered (the host stops once we say we're home).</summary>
    private bool FriendCampaignMessage(ulong sender, Channel channel, ReadOnlySpan<byte> message, long nowMicros)
    {
        if (_campaignFromHost != null)
        {
            return _campaignFromHost(sender, channel, message, nowMicros);
        }

        _dropped++;
        return true;
    }

    /// <summary>Friend: a welcome (first, again, or after a broken Steam session): the flow decides the rest. The member message always goes: a new session object forgot it.</summary>
    private void FriendWelcomed(bool returning, bool relinked)
    {
        FriendSession friend = _friend!;
        long now = Now;
        if (relinked)
        {
            ModLog.Info($"Online: back in after the Steam session restarted, as Rewired player {friend.Welcome.Slot} again; the host resends what we may have missed.");
        }
        else if (returning)
        {
            ModLog.Info($"Online: back in, as Rewired player {friend.Welcome.Slot} again.");
            NotifyText(NoticeId.LobbyJoined, $"Back in {_hostName}'s game.", $"Back in {HostTag()}'s game.");
        }
        else
        {
            ModLog.Info($"Online: joined {HostTag()}'s session as Rewired player {friend.Welcome.Slot}; the host's game says {friend.Welcome.Phase}.");
            ModLog.Info(_meter.Entered(_route, now));
            NotifyText(NoticeId.LobbyJoined, $"Joined {_hostName}'s lobby. Your game stays yours until they start.", $"Joined {HostTag()}'s lobby. Your game stays yours until they start.");
        }

        // Not joined to this game by Start: a first arrival, or one who dropped from the lobby and missed Start (a rejoin in a game they were in has their player kept).
        bool missedStart = !_flow.PlayActive;
        Run(_flow.Handle(LobbyFlow.WelcomeEvent(friend.Welcome.Phase), now));
        SendMember();
        if (friend.Welcome.Phase == T2TOnline.Core.Protocol.SessionPhase.Playing && missedStart && !relinked)
        {
            // A game is in play: this player presses to join it, and nothing else tells them. Not now: the title that would show it is about to go, with the toast. Once that game is on their screen.
            _pressToJoinBy = _clock.Elapsed + PressToJoinWait;
        }
    }

    /// <summary>
    /// A friend who came into a game in play is told what to press once it's on their screen: its match or map, or the host's menus at the title, which stay. Not at the
    /// title while the host's match couldn't load here: they were just told their game couldn't keep up, and there's nothing to press into yet (the host's next screen says it).
    /// </summary>
    private void TickPressToJoin(TimeSpan elapsed)
    {
        if (elapsed < _nextPressToJoinLook || _flow.Friend != LobbyFriendState.Playing)
        {
            return;
        }

        _nextPressToJoinLook = elapsed + TitleSignalInterval;
        bool onScreen = !WorldMap.Loading && (_relay.PlayingHostMatch || WorldMap.SceneName == WorldMap.MapScene ||
            (elapsed >= _pressToJoinBy && CampaignGame.TitleLoaded() && !_relay.FriendMatchFailed));
        if (onScreen)
        {
            _pressToJoinBy = TimeSpan.MinValue;
            if (_lastBoard is { Mode: LobbyMode.Campaign, Save: { HasRun: true } run })
            {
                // The act and floor of the host's last save, as its lobby's save row says them.
                NotifyAbout(_hostSteamId, NoticeId.JoinedRun, _hostName, LobbyWords.Number(run.Act), LobbyWords.Number(run.Floor));
            }

            Notify(_localKeyboard != null ? NoticeId.PressToJoinKeyboard : NoticeId.PressToJoin);
        }
    }

    /// <summary>Friend: a new board: the screen's, and the flow's if its phase says so.</summary>
    private void FriendBoardChanged()
    {
        if (_friend?.Board is not { } board)
        {
            return;
        }

        _lastBoard = board;
        RegisterBoardNames(board); // OnlineSession.LogTags.cs
        _lastBoardAt = _friend.BoardReceivedMicros;
        _snapshotFrame = -1;
        Run(_flow.Handle(LobbyFlow.BoardEvent(board.Phase), Now, board.Epoch));
    }

    private void SendMember() => _friend?.SendMember(_flow.Member(_ready));

    // ---- The host's friends ----

    /// <summary>Host: a friend's hello: seen, with whatever they acked forgotten. They follow once their member message says so.</summary>
    private void HostFriendArrived(ulong steamId)
    {
        _following.Remove(steamId);
        _arriving.Remove(steamId);
        _invited.Remove(steamId);
        _board?.SeatOf(BoardId(steamId));
        _nextBoard = TimeSpan.Zero;
        Run(_flow.Handle(LobbyEvent.FriendArrived, Now, peer: steamId));
    }

    private void HostFriendGone(ulong steamId)
    {
        _following.Remove(steamId);
        _nextBoard = TimeSpan.Zero;
        Run(_flow.Handle(LobbyEvent.FriendGone, Now, peer: steamId), steamId);
    }

    /// <summary>Host: what a friend says of themselves: an ack, and once they're home, the campaign link is theirs no more until the next Start.</summary>
    private void HostMemberSays(ulong steamId, string tag, in LobbyMemberMessage member, long now)
    {
        _nextBoard = TimeSpan.Zero;
        Run(_flow.Handle(LobbyEvent.FriendAcked, now, member.AckedEpoch, steamId), steamId);
        if (member.Stage == LobbyMemberStage.InLobby && _following.Remove(steamId))
        {
            ModLog.Info($"Lobby: {tag} is home in the lobby; the campaign's messages to them stop until the next Start.");
            _campaign.FriendLeft(steamId);
        }
        else if (LobbySignals.FollowsAfterGame(member.Stage, _flow.Host, _following.Contains(steamId)))
        {
            _following.Add(steamId);
            ModLog.Info($"Lobby: {tag} came back still following the game that's over; the campaign's messages bring them home, with what it credited while they were away.");
            _campaign.FriendJoined(steamId);
        }
    }

    /// <summary>Host: the flow's <see cref="LobbyEffect.FriendFollowing"/>: only now do they get what a late friend gets.</summary>
    private void HostFriendFollows(ulong steamId)
    {
        _following.Add(steamId);
        // From the barrier's start, or for someone who came in after it (a late joiner, a rejoin) from their own arrival.
        string after = _flow.LastAckAfterMicros < 0 ? string.Empty
            : _flow.LastAckFromArrival ? string.Create(CultureInfo.InvariantCulture, $" {_flow.LastAckAfterMicros / 1000} ms after they arrived (they came in after the Start)")
            : string.Create(CultureInfo.InvariantCulture, $" in {_flow.LastAckAfterMicros / 1000} ms");
        ModLog.Info($"Lobby: {Tag(steamId)} acked{after} (epoch {_flow.Epoch}).");
        _campaign.FriendJoined(steamId);
    }

    /// <summary>Host: how long a friend's seat is kept (Left's rule, <see cref="LobbyTiming.KeepSeatMicros"/>; a game keeps it <see cref="ParkFor"/>).</summary>
    private TimeSpan KeepSeatFor(LeaveReason reason) => TimeSpan.FromTicks(LobbyTiming.KeepSeatMicros(_flow.Host, reason) * (TimeSpan.TicksPerMillisecond / 1000));

    /// <summary>Friends become game players at Start, in seat order, as if each pressed their join button. The game's own limit is said, and they stay on their virtual pad to press later.</summary>
    private void AssignGamePlayers()
    {
        Il2CppControllerConnection.RewiredPlayerInputAssigner? assigner = Il2CppControllerConnection.RewiredPlayerInputAssigner.Instance;
        if (assigner == null || _board == null)
        {
            return;
        }

        foreach (ulong id in _board.FriendsInSeatOrder())
        {
            if (!_pads.TryGetValue(FromBoardId(id), out RemotePad? pad))
            {
                continue;
            }

            int rewired = pad.Pad.RewiredPlayerId;
            if (Il2CppControllerConnection.RewiredPlayerInputAssigner.GetPlayerId(rewired) >= 0)
            {
                continue;
            }

            assigner.AssignNextPlayer(rewired);
            int player = Il2CppControllerConnection.RewiredPlayerInputAssigner.GetPlayerId(rewired);
            if (player < 0)
            {
                ModLog.Warn($"Lobby: {pad.Tag} couldn't be a game player at Start (the game's max player limit? its [PlayerAssigner] lines say why); they watch, and can press their join button if a seat frees.");
            }
            else
            {
                ModLog.Info($"Lobby: {pad.Tag} is game player {player} (seat order, at Start).");
            }
        }
    }

    /// <summary>
    /// Who turned up in the Steam lobby and has no seat yet (<c>Lobby: X is arriving</c>), and who's stopped arriving. Not someone who
    /// has just said goodbye while Steam's list still names them (<see cref="LeavingMembers"/>).
    /// </summary>
    private void NoteArrivals()
    {
        if (_host == null)
        {
            return;
        }

        long now = Now;
        foreach (ulong member in _link.Members)
        {
            if (member != _self && !_pads.ContainsKey(member) && !_parked.ContainsKey(member) && !IsRefused(member) && !_leaving.IsLeaving(member, now) &&
                _arriving.Add(member))
            {
                ModLog.Info($"Lobby: {Tag(member)} is arriving (in the Steam lobby, no seat yet).");
            }
        }

        _arriving.RemoveWhere(id => Array.IndexOf(_link.Members, id) < 0);
    }

    /// <summary>Someone this host won't seat (removed, or refused for a reason their last hello gave): still in the Steam lobby they aren't arriving.</summary>
    private bool IsRefused(ulong steamId) => _removed.Contains(steamId) || _refusals.ContainsKey(steamId);

    /// <summary>The other half of two friends both hosting: a friend's game is in another lobby while this one is alone.</summary>
    private void NoteHostingToo()
    {
        if (_host == null || _pads.Count > 0 || _link.Id == 0)
        {
            return;
        }

        foreach (FriendLobby lobby in SteamFriendsLobbies.FindFriendLobbies(SteamRuntime.AppId))
        {
            if (lobby.LobbySteamId != _link.Id && _hostingToo.TryAdd(lobby.FriendSteamId, lobby.PersonaName))
            {
                ModLog.Info($"Lobby: friend {Tag(lobby.FriendSteamId)} is hosting too (lobby {Tags.Lobby(lobby.LobbySteamId)}).");
                NotifyAbout(lobby.FriendSteamId, NoticeId.HostingToo, SafeText.Clean(lobby.PersonaName, 32));
            }
        }
    }

    // ---- The board ----

    /// <summary>The board from the session's real inputs, out to whoever needs it (idempotent, rate-limited per friend), and the lobby's advert kept current.</summary>
    private void PublishBoardNow(long now)
    {
        if (_host == null || _board == null)
        {
            return;
        }

        LobbyBoardMessage board = BuildBoard(now);
        _lastBoard = board;
        _lastBoardAt = now;
        _snapshotFrame = -1;
        _host.PublishBoard(board, now);
        var advert = new LobbyAdvert(
            board.Phase is LobbyPhase.Playing or LobbyPhase.Starting ? ListedState.Playing : ListedState.Lobby,
            _lobbyMode == LobbyMode.QuickGame ? ListedMode.Quick : ListedMode.Campaign,
            _lobbyMode == LobbyMode.Campaign && _saveRow.HasRun ? new ListedSave(_saveRow.SquadId, _saveRow.Act, _saveRow.Floor) : null);
        if (advert != _advert)
        {
            Advert = advert;
        }
    }

    private LobbyBoardMessage BuildBoard(long now)
    {
        TimeSpan elapsed = _clock.Elapsed;
        ReadSaveRow(elapsed);
        ReadHostDevice(elapsed);
        var seated = new List<LobbySeated>(_pads.Count);
        foreach ((ulong id, RemotePad pad) in _pads)
        {
            if (_host?.Find(id) is not { IsJoined: true } friend)
            {
                continue;
            }

            seated.Add(new LobbySeated
            {
                SteamId = BoardId(id),
                Name = pad.DisplayName,
                Member = friend.Member,
                HasMember = friend.HasMember,
                DeviceKind = pad.Pad.IsKeyboard ? InputKind.KeyboardMouse : InputKind.Gamepad,
                DeviceFamily = (byte)pad.Pad.DeviceType,
                PingMs = friend.Link.LastRoundTripMicros < 0 ? -1 : (int)Math.Min(friend.Link.LastRoundTripMicros / 1000, int.MaxValue),
                LossPercent = 0, // nothing streams in a lobby; the bars read the round trip until there's a stream to measure
            });
        }

        var kept = new List<(ulong, string, int)>(_parked.Count);
        foreach ((ulong id, (RemotePad pad, TimeSpan until)) in _parked)
        {
            kept.Add((BoardId(id), pad.DisplayName, (int)Math.Max(0, Math.Ceiling((until - elapsed).TotalSeconds))));
        }

        var invited = new List<(ulong, string)>(_invited.Count);
        if (_invited.Count > 0)
        {
            foreach (ulong id in _invited.Keys.ToList())
            {
                (string name, TimeSpan until) = _invited[id];
                if (elapsed >= until)
                {
                    _invited.Remove(id);
                }
                else
                {
                    invited.Add((id, name));
                }
            }
        }

        IReadOnlyList<LobbyPerson> people = LobbyPeople.Of(seated, kept, BoardMembers(), invited, _self, _flow.BoardPhase, BoardNameOf, BoardRefused);

        // A seat kept for nobody any more goes.
        foreach (ulong id in _board!.FriendsInSeatOrder())
        {
            bool still = false;
            foreach (LobbyPerson person in people)
            {
                still |= person.SteamId == id && person.Stage != SeatStage.Invited;
            }

            if (!still)
            {
                _board.Free(id);
            }
        }

        return _board.Build(new LobbyBoardInputs
        {
            Phase = _flow.BoardPhase,
            Epoch = _flow.Epoch,
            CountdownMs = _flow.CountdownMsLeft(now),
            Mode = _lobbyMode,
            Save = _saveRow,
            Activity = _flow.IsWaitingForEveryone ? HostActivity.WaitingForEveryone : _activity,
            Result = _result,
            Host = new LobbyPerson
            {
                SteamId = _self,
                Name = SafeText.Clean(SteamFriends.GetPersonaName(), 32),
                Stage = SeatStage.Here,
                Ready = _ready,
                DeviceKind = _hostDevice.Kind,
                DeviceFamily = _hostDevice.Family,
                PingMs = -1,
            },
            People = people,
        });
    }

    /// <summary>The chosen slot's row, read again every few seconds and at once after a change (its reading is cached on the file's stamp).</summary>
    private void ReadSaveRow(TimeSpan elapsed)
    {
        if (elapsed < _nextSaveRead)
        {
            return;
        }

        _nextSaveRead = elapsed + SaveRowInterval;
        _saveRow = ToRow(CoopSlots.DescribeOne(CoopSlots.Selected));
    }

    private static LobbySaveRow ToRow(CoopSlotInfo info) => new(
        LobbySignals.Clamp(info.Slot),
        info.HasRun,
        info.HasRun ? SafeText.Clean(info.SquadId, LobbyBoardMessage.MaxSquadBytes) : string.Empty,
        LobbySignals.Clamp(info.Act),
        LobbySignals.Clamp(info.Floor),
        info.SavedAt == DateTime.MinValue ? 0 : new DateTimeOffset(info.SavedAt).ToUnixTimeSeconds());

    /// <summary>The icon on the host's own seat, read only where a seat is drawn: nothing in a match, and nothing of a pad's maps or the log's (<see cref="LocalPad.PeekDeviceType"/>).</summary>
    private void ReadHostDevice(TimeSpan elapsed)
    {
        if (elapsed < _nextHostDevice || _flow.Host is not (LobbyHostState.Open or LobbyHostState.Counting))
        {
            return;
        }

        _nextHostDevice = elapsed + HostDeviceInterval;
        bool hadPad = _hostDevice.Kind == InputKind.Gamepad;
        _hostDevice = LocalPad.PeekDeviceType() is { } family ? (InputKind.Gamepad, (byte)family) : (InputKind.KeyboardMouse, (byte)Il2CppBishopGames.Input.EInputDeviceType.MouseKeyboard);
        if (hadPad && _hostDevice.Kind != InputKind.Gamepad)
        {
            PadWentInLobby();
        }
    }

    /// <summary>
    /// Friend, twice a second in the lobby: their pad going is said, as the host's is (<see cref="ReadHostDevice"/>). Nothing else is done: it's their own game's pad until Start, and the
    /// game takes it as a new pad when it's back. <see cref="BeginPlay"/> takes whichever pad is connected then, so a pad that comes back as a new joystick needs nothing from here.
    /// </summary>
    private void WatchLobbyPad(TimeSpan elapsed)
    {
        if (DeviceWithHost || _localPad == null || elapsed < _nextPadSearch)
        {
            return;
        }

        _nextPadSearch = elapsed + PadSearchInterval;
        bool connected = _localPad.IsConnected;
        if (connected != _localPadWasConnected)
        {
            _localPadWasConnected = connected;
            if (!connected)
            {
                PadWentInLobby();
            }
        }
    }

    /// <summary>A pad going in the lobby costs nothing (the cursor and the seat stay), but a press does nothing until it's back or another is pressed: the toast says so.</summary>
    private void PadWentInLobby()
    {
        ModLog.Info("Lobby: your pad disconnected; nothing's lost (your seat and the lobby's cursor stay).");
        Notify(NoticeId.PadLost);
    }

    // ---- Start and the game's own calls ----

    /// <summary>The lobby's Start: the not-here rule, then the countdown, then the barrier; the game's call comes from the flow.</summary>
    private void StartFromLobby(bool newRun, bool anyway)
    {
        long now = Now;
        if (_flow.Host != LobbyHostState.Open || !_titleReady)
        {
            ModLog.Info($"Lobby: Start does nothing now ({_flow.Host}{(_titleReady ? string.Empty : ", the title isn't ready")}).");
            return;
        }

        _startNewRun = newRun;
        LobbySeat? notHere = LobbyPeople.FirstNotHere(Snapshot());
        if (notHere != null && !anyway)
        {
            _waitingFor = Tag(FromBoardId(notHere.SteamId)); // the log's name for them (the seat's name is as the lobby draws it); the one-PC test's seat is this PC's own id
            ModLog.Info(LobbyMeter.StartWaits(_waitingFor));
            Run(_flow.Handle(LobbyEvent.WaitPressed, now));
            return;
        }

        if (notHere != null)
        {
            ModLog.Info(LobbyMeter.StartedWithout(Tag(FromBoardId(notHere.SteamId)), 0, wentOnAnyway: true));
        }

        Run(_flow.Handle(LobbyEvent.StartPressed, now));
    }

    private void CancelStartFromLobby()
    {
        if (_flow.Host == LobbyHostState.Counting)
        {
            _meter.CountdownCancelled();
            ModLog.Info("Lobby: countdown cancelled.");
        }

        Run(_flow.Handle(LobbyEvent.CancelPressed, Now));
    }

    /// <summary>
    /// The game's own start, called directly (<c>PerformButtonClickCallback</c> refuses with the shell on the title's list, and the shell stays under the game's flow). Waits for
    /// <c>RunSync.ReadyToStart</c> a moment if a refresh is running; never called on anything but an idle title.
    /// </summary>
    private void TryCallGameStart(TimeSpan elapsed)
    {
        if (_flow.Host != LobbyHostState.GameFlow)
        {
            _gameStartDue = false; // the game left by itself, or the session moved on: this call's moment has gone
            return;
        }

        LobbyTitleView view = ReadTitleView(out TitleScreen? title);
        if (title == null || !LobbySignals.StartCanRun(view, RunSync.Instance?.ReadyToStart(title) == true))
        {
            if (elapsed >= _gameStartBy)
            {
                _gameStartDue = false;
                ModLog.Warn($"Lobby: the title wasn't idle within {GameStartWait.TotalSeconds:0} s of Start, so the game's start wasn't called; the lobby comes back.");
            }

            return;
        }

        _gameStartDue = false;
        LobbyStartKind kind = LobbyRules.StartKindOf(_lobbyMode, _saveRow, _startNewRun);
        bool tutorial = kind == LobbyStartKind.NewRun && title.m_CampaignTutorial?.ShouldSkipTutorial() == false;
        _startWhat = tutorial ? "StartCampaignTutorialFlow" : kind switch
        {
            LobbyStartKind.QuickGame => "LaunchPlayNow",
            LobbyStartKind.ContinueRun => "ContinueRun",
            _ => "NewRun",
        };
        if (title._isTransitioningGroup)
        {
            title.CompleteGroupTransition();
        }

        _gameFacts = new LobbyGameFacts { Mode = _lobbyMode, OurScore = -1, TheirScore = -1 };
        _gameWasImplicit = false;
        _result = LobbyResult.None;
        Screen("handing over for the start", s => s.HandOverForStart($"Start: {_startWhat}"));
        ModLog.Info($"Lobby: game flow started: {_startWhat}.");
        try
        {
            if (tutorial)
            {
                title.StartCampaignTutorialFlow();
            }
            else if (kind == LobbyStartKind.QuickGame)
            {
                title.LaunchPlayNow();
            }
            else if (kind == LobbyStartKind.ContinueRun)
            {
                title.ContinueRun();
            }
            else
            {
                title.NewRun();
            }
        }
        catch (Exception ex)
        {
            ModLog.Error($"Lobby: the game's {_startWhat} failed; the lobby comes back", ex);
        }
    }

    // ---- The return ----

    /// <summary>
    /// The game's "open the squad screen at the title" flag (<c>RunEndHelper.EndRun</c> sets it just before its scene load; the title reads it frames later, after its warm-ups;
    /// verified in the game's code), cleared every frame while hosting a lobby: back from a run means the lobby.
    /// </summary>
    private void ClearNewRunFlag()
    {
        if (!_flow.KeepsNewRunFlagClear || !Il2CppTape2Tape.CampaignUtil.startNewRunImmediately)
        {
            return;
        }

        Il2CppTape2Tape.CampaignUtil.startNewRunImmediately = false;
        if (!_newRunFlagLogged)
        {
            _newRunFlagLogged = true;
            ModLog.Info("Lobby: new-run flag cleared (the title would have opened the squad screen by itself).");
        }
    }

    /// <summary>Read twice a second while a game runs, and held: what the game reports for the ribbon, nothing guessed.</summary>
    private void ReadGameFacts()
    {
        if (_flow.Host is not (LobbyHostState.GameFlow or LobbyHostState.InGame))
        {
            if (_flow.Host == LobbyHostState.Open && _flow.BackFromGame && !_gameFactsSettled)
            {
                _gameFactsSettled = true;
                SettleResult();
            }

            return;
        }

        _gameFactsSettled = false;
        if (_flow.IsImplicitStart)
        {
            // The title's own buttons started this one (with no shell up, say): what they started needn't be the lobby's mode, and these numbers belong to no game the lobby knows.
            _gameWasImplicit = true;
            _result = LobbyResult.None;
        }

        // A rematch is a new match in the same game: what the last one reported isn't this one's, and a quit in it has no result.
        if (MatchSetup.LoadedSerial != _gameFactsMatch)
        {
            _gameFactsMatch = MatchSetup.LoadedSerial;
            _gameFacts = _gameFacts with { MatchEnded = false, OurScore = -1, TheirScore = -1 };
        }

        Il2Cpp.GameManager? game = HudModel.GameManagerInstance();
        if (game != null && game._homeTeam is { } home && home != null && game._visitorTeam is { } visitor && visitor != null)
        {
            int homePlayers = home.Players?.Count ?? 0;
            int visitorPlayers = visitor.Players?.Count ?? 0;
            bool versus = homePlayers > 0 && visitorPlayers > 0;
            bool oursVisitor = !versus && visitorPlayers > homePlayers;
            _gameFacts = _gameFacts with
            {
                OurScore = oursVisitor ? visitor.teamStats._goals : home.teamStats._goals,
                TheirScore = oursVisitor ? home.teamStats._goals : visitor.teamStats._goals,
                Versus = versus,
                MatchEnded = _gameFacts.MatchEnded || game.isGameEnded,
            };
        }

        if (_lobbyMode == LobbyMode.Campaign && CampaignGame.State()?.runData is { } run && !string.IsNullOrEmpty(run.runID))
        {
            _gameFacts = LobbyResults.WithRun(_gameFacts, run._currentAct, run.currentFloor, run.isRunFailed);
        }
    }

    /// <summary>The ribbon, once the title has the slot back (a run left from the map's menu is still in it).</summary>
    private void SettleResult()
    {
        _ready = false; // the host's own mark too: it was about the last game
        EndSeatedRun("back at the lobby"); // the game's remembered sides as before the run's start (OnlineSession.TeamSeats.cs)
        _nextSaveRead = TimeSpan.Zero;
        ReadSaveRow(_clock.Elapsed);
        _gameFacts = _gameFacts with { RunStillInSlot = _saveRow.HasRun };
        _result = _gameWasImplicit ? LobbyResult.None : LobbyResults.Of(_gameFacts);
        _nextBoard = TimeSpan.Zero;
        ModLog.Info($"Lobby: back at the lobby ({(_result.Kind == LobbyResultKind.None ? "no result to show: the game reported none" : $"{_result.Kind} {_result.First}-{_result.Second}")}).");
    }

    /// <summary>What the game's end of a campaign match said: a run won or lost (<c>EndOfGameContext</c>), from the patch that already reads it.</summary>
    internal void NoteCampaignEnd(bool runWon, bool runFailed)
    {
        if (runWon || runFailed)
        {
            _gameFacts = _gameFacts with { RunWon = _gameFacts.RunWon || runWon, RunFailed = _gameFacts.RunFailed || runFailed };
        }
    }

    // ---- What the screens read ----

    /// <summary>What the lobby's screen draws now: made at most once a frame.</summary>
    internal LobbyState Snapshot()
    {
        if (_flow.Role == LobbyRole.None)
        {
            return LobbyState.None;
        }

        // At most ten times a second, or when the board changed (_snapshotFrame -1): it asks Steam for every seat's name and the game for its language, and a screen asks every frame.
        int frame = FrameClock.Frame;
        long now = Now;
        if (frame == _snapshotFrame || (_snapshotFrame != -1 && now - _snapshotAt < SnapshotMicros))
        {
            return _snapshot;
        }

        _snapshotFrame = frame;
        _snapshotAt = now;
        LobbyRole role = _flow.Role;
        if (_lastBoard == null)
        {
            _snapshot = new LobbyState { Role = role, Reconnecting = _flow.Friend == LobbyFriendState.Lost };
            return _snapshot;
        }

        _snapshot = LobbyState.From(_lastBoard, new LobbyViewInputs
        {
            Role = role,
            Self = _self,
            BoardAgeMicros = now - _lastBoardAt,
            InputLocked = _flow.IsInputLocked(now),
            BackFromGame = _flow.BackFromGame,
            Reconnecting = _flow.Friend == LobbyFriendState.Lost,
            TitleReady = _titleReady,
            WaitingForEveryone = _flow.IsWaitingForEveryone,
            Language = LanguageIndex(),
            NameOf = static (id, board) => SteamFriends.GetFriendPersonaName(id),
            CanDraw = NativeLobby.LobbyScreens.CanDraw,
        });
        return _snapshot;
    }

    /// <summary>This PC's language for the lobby's lines: English when the game's is one the words don't know (the same rule as the words in the game's source).</summary>
    private static int LanguageIndex() => NativeLobby.LobbyLocalization.Language();

    /// <summary>Why Online can't be used now: <see cref="Ready"/>'s reasons as words and a button.</summary>
    private FailureMessage? WhyNotOnline()
    {
        if (Campaign.SaveBackups.BlocksSessions(out _))
        {
            return JoinFailures.Message(LobbyFailure.RestoredBackup);
        }

        if (!T2TOnlinePlugin.OnlineAvailable)
        {
            // The words for a game update only for one: with this screen up the build is a tested one, unless a developer asked otherwise, and what's off is a check (the panel names it).
            return JoinFailures.Message(T2TOnlinePlugin.GameNotTested ? LobbyFailure.OnlineOff : LobbyFailure.OnlineBroken);
        }

        if (_broken)
        {
            return JoinFailures.Message(LobbyFailure.OnlineBroken);
        }

        return _self != 0 ? null : JoinFailures.Message(_started ? SteamOffFailure : LobbyFailure.SteamWaiting);
    }

    private static LobbySaveRow[] SlotRows()
    {
        IReadOnlyList<CoopSlotInfo> slots = CoopSlots.Describe();
        var rows = new LobbySaveRow[slots.Count];
        for (int i = 0; i < slots.Count; i++)
        {
            rows[i] = ToRow(slots[i]);
        }

        return rows;
    }

    private bool SetSlotFromLobby(int slot)
    {
        if (_flow.Host is not (LobbyHostState.Idle or LobbyHostState.Open))
        {
            ModLog.Info($"Lobby: slot {slot} wasn't chosen: a Start is running.");
            return false;
        }

        try
        {
            if (!CoopSlots.TrySelect(slot, out string problem))
            {
                ModLog.Info($"Lobby: slot {slot} wasn't chosen: {problem}.");
                return false;
            }
        }
        catch (Exception ex)
        {
            // A screen's press: every action is safe in any state (ILobbyActions), and the run file's switch runs the game's code.
            ModLog.Error($"Lobby: choosing slot {slot} failed", ex);
            return false;
        }

        _nextSaveRead = TimeSpan.Zero;
        _nextBoard = TimeSpan.Zero;
        _result = LobbyResult.None;
        return true;
    }

    private void SetModeFromLobby(LobbyMode mode)
    {
        if (_flow.Host == LobbyHostState.Open && mode != _lobbyMode)
        {
            _lobbyMode = mode;
            TourModeChanges++;
            _nextBoard = TimeSpan.Zero;
            ModLog.Info($"Lobby: mode {mode}.");
        }
    }

    private void ToggleReadyMark()
    {
        _ready = !_ready;
        _meter.ReadyToggled();
        if (_flow.Role == LobbyRole.Friend)
        {
            SendMember();
        }

        _nextBoard = TimeSpan.Zero;
    }

    private bool InviteFromLobby(ulong friend)
    {
        // A press that lands on a friend's row mustn't reach a real person's Steam (OnlineSession.TourHolds.cs).
        if (TourRefusesInvite("an invite was pressed"))
        {
            return false;
        }

        string name = DisplayNameOf(friend);
        bool sent = InviteFriend(friend);
        if (sent)
        {
            _invited[friend] = (name, _clock.Elapsed + InviteMarkFor);
            _nextBoard = TimeSpan.Zero;
            ModLog.Info($"Invite: sent to {Tag(friend)}.");
            NotifyAbout(friend, NoticeId.InviteSent, name);
        }
        else
        {
            ModLog.Info($"Invite: couldn't send to {Tag(friend)}.");
            NotifyAbout(friend, NoticeId.InviteFailed, name);
        }

        return sent;
    }

    /// <summary>Host: a player or kept seat goes, refused for the session. The screen has asked.</summary>
    private void RemoveFromLobby(ulong boardId)
    {
        if (_host == null || boardId == _self)
        {
            return;
        }

        ulong steamId = FromBoardId(boardId);
        _removed.Add(steamId);
        _invited.Remove(steamId);
        _nextBoard = TimeSpan.Zero;
        ModLog.Info($"Lobby: {Tag(steamId)} removed by the host; they're refused for this session.");
        if (_host.Find(steamId) is { IsJoined: true })
        {
            _host.Remove(steamId, LeaveReason.Removed);
        }
        else
        {
            FreeSeat(steamId);
        }
    }

    /// <summary>This PC's own Settings from the lobby: the game's, with no guard: it's their own game until Start. Not while a Start runs.</summary>
    private void OpenOwnSettingsFromLobby()
    {
        if (_playOn || _flow.Host is not (LobbyHostState.Idle or LobbyHostState.Open))
        {
            ModLog.Info("Lobby: own Settings wait: a game is under way (My Settings on the pause menu or the map has yours while you follow).");
            return;
        }

        TitleScreen? title = CampaignGame.IdleTitle();
        if (title == null)
        {
            ModLog.Info("Lobby: own Settings didn't open: the title isn't idle.");
            return;
        }

        ModLog.Info("Lobby: this PC's own Settings open from the lobby.");
        title.LaunchSettings();
    }

    private IReadOnlyList<LobbyListing> ListingsNow()
    {
        // An invite that arrives or lapses changes a row (InvitedUs) with no change to the list.
        if (_listingsVersion != _browserVersion || _listingsInvites != _invitesVersion)
        {
            _listingsInvites = _invitesVersion;
            _listingsVersion = _browserVersion;
            _listingsReadAt = _clock.Elapsed;
            var rows = new List<LobbyListing>(_browser.Listings.Count);
            int language = LanguageIndex(); // read from the game once, not once a row
            foreach (SteamLobbyListing listing in _browser.Listings)
            {
                rows.Add(new LobbyListing
                {
                    LobbyId = listing.LobbyId,
                    HostId = listing.HostId,
                    HostName = DisplayName.Clean(listing.HostId != 0 ? listing.HostName : string.Join(", ", listing.FriendNames), rows.Count + 1, language, NativeLobby.LobbyScreens.CanDraw),
                    Players = listing.Players is > 0 and <= MaxLobbyMembers ? listing.Players : listing.FriendsSeen,
                    MaxPlayers = MaxLobbyMembers,
                    State = listing.State switch { ListedState.Lobby => ListingState.InLobby, ListedState.Playing => ListingState.InGame, _ => ListingState.Unknown },
                    Mode = listing.Mode switch { ListedMode.Campaign => LobbyMode.Campaign, ListedMode.Quick => LobbyMode.QuickGame, _ => null },
                    Save = listing.Save is { } save ? new LobbySaveRow(0, true, save.Squad, LobbySignals.Clamp(save.Act), LobbySignals.Clamp(save.Floor), 0) : LobbySaveRow.None,
                    Protocol = int.TryParse(listing.Protocol, NumberStyles.None, CultureInfo.InvariantCulture, out int protocol) ? protocol : -1,
                    Build = listing.Build,
                    ModVersion = SafeText.Clean(listing.ModVersion, 32),
                    PingMs = listing.PingMs,
                    InvitedUs = SteamInviteBook.Shared.IsInvited(listing.LobbyId, _clock.Elapsed),
                }.WithBrowserAnswer(listing.Answer == ListingAnswer.Answered, listing.Answer == ListingAnswer.NoAnswer));
            }

            _listings = rows;
        }

        return _listings;
    }

    private IReadOnlyList<PickerFriend> FriendsNow()
    {
        TimeSpan elapsed = _clock.Elapsed;
        if (elapsed >= _nextPickerRead)
        {
            _nextPickerRead = elapsed + PickerInterval;
            IReadOnlyList<SteamPickerFriend> friends = SteamFriendsPicker.Read(SteamRuntime.AppId);
            var rows = new PickerFriend[friends.Count];
            int language = LanguageIndex(); // read from the game once, not once a friend
            for (int i = 0; i < friends.Count; i++)
            {
                SteamPickerFriend friend = friends[i];
                PickerActivity activity = friend.Activity switch
                {
                    SteamFriendActivity.InThisGame or SteamFriendActivity.InThisGameLobby => PickerActivity.PlayingT2T,
                    SteamFriendActivity.InOtherGame => PickerActivity.OtherGame,
                    _ => PickerActivity.Online,
                };
                rows[i] = new PickerFriend(friend.SteamId, DisplayName.Clean(friend.Name, i + 1, language, NativeLobby.LobbyScreens.CanDraw), activity, _pads.ContainsKey(friend.SteamId) || Array.IndexOf(_link.Members, friend.SteamId) >= 0);
            }

            _picker = rows;
        }

        return _picker;
    }

    private void HostFromRow()
    {
        _nextSaveRead = TimeSpan.Zero;
        HostGameCore(LobbyRoute.HostRow);
    }

    /// <summary>What the native screens call: the session's own methods under the seam's names (<see cref="ILobbyActions"/>).</summary>
    private sealed class LobbyActions : ILobbyActions
    {
        private readonly OnlineSession _session;

        public LobbyActions(OnlineSession session) => _session = session;

        public bool InSession => _session._flow.Role != LobbyRole.None;

        public LobbyMeter Meter => _session._meter;

        public bool LeavingForTitle => _session._relay.LeavingHostMatch || SessionExit.Leaving || _session.TourLeavingForTitle;

        public void NoteOnlineOpened() => _session._meter.OnlineOpened(_session.Now);

        public LobbyState Snapshot() => _session.Snapshot();

        public FailureMessage? WhyNotOnline() => _session.WhyNotOnline();

        public void HostLobby() => _session.Guarded("Host Lobby", () => _session.HostFromRow());

        public void JoinLobby(ulong lobby) => _session.Guarded("Join Lobby", () => _session.JoinGameCore(lobby, LobbyRoute.Browser));

        public void CancelJoin() => _session.Guarded("Cancel the join", () => _session.LeaveAll("the join was cancelled"));

        public void LeaveOrClose() => _session.Guarded("Leave the lobby", () => _session.LeaveAll("left, as asked"));

        public void SetMode(LobbyMode mode) => _session.SetModeFromLobby(mode);

        public bool SetSlot(int slot) => _session.SetSlotFromLobby(slot);

        public IReadOnlyList<LobbySaveRow> Slots() => SlotRows();

        public void Start(bool newRun, bool anyway) => _session.Guarded("Start", () => _session.StartFromLobby(newRun, anyway));

        public void CancelStart() => _session.Guarded("Cancel the start", _session.CancelStartFromLobby);

        public void ToggleReady() => _session.ToggleReadyMark();

        public void NoteActivity(HostActivity activity)
        {
            _session._activity = activity;
            _session._nextBoard = TimeSpan.Zero;
        }

        public bool Invite(ulong friend) => _session.InviteFromLobby(friend);

        public bool OpenInviteOverlay() => _session.InviteFriends().Opened;

        public bool OpenProfile(ulong steamId) => OnlineSession.OpenProfile(_session.FromBoardId(steamId)) == SteamOverlayResult.Opened;

        public void Remove(ulong steamId) => _session.RemoveFromLobby(steamId);

        public void OpenOwnSettings() => _session.Guarded("Own Settings", _session.OpenOwnSettingsFromLobby);

        public void SetBrowserOpen(bool open)
        {
            if (open)
            {
                _session.OpenBrowser();
            }
            else
            {
                _session.CloseBrowser();
            }
        }

        public IReadOnlyList<LobbyListing> Listings() => _session.ListingsNow();

        public void RefreshListings()
        {
            _session.TourRefreshes++;
            _session.FindGames();
        }

        public IReadOnlyList<PickerFriend> Friends() => _session.FriendsNow();
    }

    // ---- The check tour's own reach (developer only) ----

    /// <summary>The tour's question: is a friend's play period off: no lock, no campaign following, no guard for it, no device taken, no prompt pin? Empty when all's clear, else what's stuck.</summary>
    internal string TourStuckLine()
    {
        var stuck = new List<string>();
        if (FriendUi.Locked)
        {
            stuck.Add("the UI lock");
        }

        if (_campaign.Following)
        {
            stuck.Add("campaign following");
        }

        if (SaveGuard.CampaignHolds)
        {
            stuck.Add("the save guard's campaign hold");
        }

        if (_localPad is { IsDetached: true })
        {
            stuck.Add("the pad");
        }

        if (_localKeyboard is { IsDetached: true })
        {
            stuck.Add("the keyboard");
        }

        if (PromptPin.Pinned)
        {
            stuck.Add("the prompt pin");
        }

        return string.Join(", ", stuck);
    }

    /// <summary>
    /// The tour's solo run of a friend's two steps, with no host: the input chosen, following started, the device taken, prompts pinned; then all of it given back by
    /// <see cref="EndPlay"/>. No session, no lobby, nothing sent. What it takes and gives back are the real things, and a stuck one is the worst failure here.
    /// </summary>
    internal bool TourBeginPlay(out string problem)
    {
        problem = string.Empty;
        if (!ChooseInput(out LocalPad? pad, out LocalKeyboard? keyboard))
        {
            problem = "nothing to play with";
            return false;
        }

        _localPad = pad;
        _localKeyboard = keyboard;
        _controls = keyboard?.Controls ?? PadRemapReader.Read(pad!, NextControlsGeneration());
        _hostName = "the check tour";
        _hostSteamId = 0;
        _playOn = true;
        CloseOwnMenusForPlay();
        _campaignFromHost = _campaign.StartFollowing(new CurrentFriendTransport(this), _self, _hostName);
        _localPad?.Detach();
        _localKeyboard?.Detach();
        PinPrompts();
        ModLog.Info("Lobby: your device goes to the host's game (the check tour: nothing is sent; it's given back in a moment).");
        return true;
    }

    /// <summary>Ends <see cref="TourBeginPlay"/>.</summary>
    internal void TourEndPlay()
    {
        EndPlay("the check tour's play period ended");
        _localPad = null;
        _localKeyboard = null;
        _controls = null;
        _hostName = string.Empty;
        _hostSteamId = 0;
    }

    /// <summary>The session's clock, for the tour's questions to the flow.</summary>
    internal long TourNow() => Now;

    /// <summary>The tour: how many times the mode really changed, to the press: a state read at 10 Hz can't count them.</summary>
    internal int TourModeChanges { get; private set; }

    /// <summary>The tour: how many times the Join list's Refresh really ran, to the press.</summary>
    internal int TourRefreshes { get; private set; }

    /// <summary>The tour: as a friend's game is when its host closed in a match: on its way out of a match for the title, with a prompt waiting to be read.</summary>
    internal bool TourLeavingForTitle { get; set; }

    /// <summary>The tour: when set, each notice told, in order.</summary>
    internal List<NoticeId>? TourNotices { get; set; }

    /// <summary>The ribbon's way in, proven by the tour with the game's numbers as read: the match counts as ended (a real end would save stats and unlock things).</summary>
    internal void TourMarkMatchEnded() => _gameFacts = _gameFacts with { MatchEnded = true };

    /// <summary>The tour's real failure: a join of a lobby that isn't there, as an invite's would go. Steam answers, and it fails as any join does.</summary>
    internal void TourJoinNowhere() => Guarded("the tour's join", () => BeginJoin(NowhereLobby, "the check tour", LobbyRoute.Panel));

    /// <summary>The tour's way out, whatever it left: the session ended as a press would.</summary>
    internal void TourLeave() => Guarded("the tour's leave", () =>
    {
        if (_playOn)
        {
            TourEndPlay();
        }

        if (InLobby || _flow.Role != LobbyRole.None)
        {
            LeaveAll("the check tour ended");
        }
    });

    private bool _gameFactsSettled;
}
#endif
