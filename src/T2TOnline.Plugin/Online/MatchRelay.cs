#if HAS_INTEROP
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Il2CppUltimateReplay;
using T2TOnline.Core.Diagnostics;
using T2TOnline.Core.Protocol;
using T2TOnline.Core.Serialization;
using T2TOnline.Core.Session;
using T2TOnline.Core.Snapshots;
using T2TOnline.Core.Stream;
using T2TOnline.Plugin.Campaign;
using T2TOnline.Plugin.Diagnostics;
using T2TOnline.Plugin.Match;
using T2TOnline.Plugin.Replay;
using T2TOnline.Plugin.Streaming;
using UnityEngine;

namespace T2TOnline.Plugin.Online;

/// <summary>
/// The match half of an online session:
/// <list type="bullet">
/// <item>Host: when a match starts loading, its setup (<see cref="MatchSetup"/>) goes to every friend, who loads it
/// meanwhile. Once the match is in play and the game records it, it's streamed (<see cref="MatchStreamHost"/>) to each
/// friend whose game has loaded it. While a friend is still loading, the host's match is held at time scale 0 (up to
/// <see cref="BarrierTimeout"/>, and longer while this PC's custom content still reaches them: <see cref="ContentHold"/>),
/// so everyone starts together. When the host leaves the match, friends are told.</item>
/// <item>Friend: the host's setup is loaded as a fresh match (<see cref="MatchSetup.TryLoad"/>), then played live in client
/// mode (<see cref="StreamPlayer"/>), and the host is told it's ready, or why it couldn't load. The host's match is never
/// handed back to this game while it's loaded: when it ends, playback fails, a new setup comes or the session ends, its
/// last frame is held in client mode (<see cref="StreamPlayer.Hold"/>) until its scene unloads. Then the game goes back to
/// the main menu, once no new match has followed within a few seconds (after a campaign match the campaign follower
/// takes it on instead), or straight away when playback failed or the session ended; a new match's load replaces it.
/// Handed back, it would play on AI-only, its HUD and objects back at their start, stats and all.</item>
/// <item>The one-PC test (F6) measures the stream over Steam without loading anything: the same process is the host.</item>
/// </list>
/// Nothing here throws into the game. A step that fails is logged; on the host the match stream ends (and the barrier
/// lets go), on a friend loading or playing stops with the reason on screen.
/// </summary>
internal sealed partial class MatchRelay
{
    /// <summary>How long the host's match waits for friends to load it (longer for a friend whose custom content still moves: <see cref="ContentHold"/>).</summary>
    public static readonly TimeSpan BarrierTimeout = ContentHold.BaseHold;

    private static readonly TimeSpan LoadTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan MatchLoadTimeout = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan NextMatchGrace = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan SetupWaitLimit = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan SetupWaitCheckInterval = TimeSpan.FromSeconds(0.25);
    private static readonly TimeSpan ReportInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan StreamReportInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan LeaveRetryInterval = TimeSpan.FromSeconds(5);

    // A friend's match load failures that are always the same words: the host's log writes only these as they are. Every other
    // reason a friend sends is made by Fail from its kind, whose opening words the host's log keeps (MatchLoadFailures).
    private static readonly string[] FixedLoadFailures = { TourFailedLoadReason };

    private const int MaxErrorLogs = 5;
    private const int MaxLeaveTries = 3;

    private readonly MatchStreaming _streaming;
    private readonly string _streamDirectory;
    private readonly string _modVersion;
    private readonly ulong _gameBuild;
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private readonly System.Random _random = new();
    private readonly RateWindow<ulong> _rates = new();
    private readonly MemoryReport _hostMemory = new();
    private readonly MemoryReport _friendMemory = new();
    private readonly HashSet<ulong> _reportedFailures = new();
    private readonly ContentHold _contentHold = new();
    private readonly HashSet<ulong> _holdExtended = new();

    private MatchStreamHost? _host;
    private int _handledCapture;
    private bool _joinCurrent;
    private int _minSerial;
    private List<KeyValuePair<string, NetValue>>? _hostSetup;
    private TimeSpan _setupSentAt;
    private MatchToken _streamedMatch;
    private MatchToken _blockedMatch;
    private bool _holding;
    private TimeSpan _holdUntil;
    private float _heldTimeScale = 1f;
    private bool _restoreAfterMenu;
    private int _holdFrames;
    private int _holdResets;
    private TimeSpan _nextHostReport;
    private int _hostErrors;
    private int _unsharedSerial;
    private CustomContentSender? _content;
    private long _contentDropsSaid; // the sender's drop count the last delivery line said
    private List<CustomItem> _hostContent = new();
    private List<CustomItem> _screenContent = new();
    private CustomItem? _runLogo;
    private CustomItem? _runLook;
    private TimeSpan _nextRunLogoCheck;
    private TimeSpan _nextContentNote;

    private MatchStreamClient? _client;
    private string _hostName = string.Empty;
    private ulong _hostSteamId;
    private bool _measureOnly;
    private int _handledSetup;
    private int _setupWaitVersion;
    private TimeSpan _setupWaitFrom;
    private TimeSpan _setupWaitNextCheck;
    private FriendPhase _phase;
    private string _problem = string.Empty;
    private TimeSpan _phaseSince;
    private MatchToken _before;
    private MatchToken _hostMatch;
    private bool _leaveWhenLoaded;
    private MatchToken _leaveBefore;
    private TimeSpan _leaveUntil;
    private MatchToken _leaving;
    private TimeSpan _leavingUntil;
    private TimeSpan _leaveRetryAt;
    private int _leaveTries;
    private bool _leaveWaitLogged;
    private ulong _loadedHash;
    private TimeSpan _loadFrom;
    private long _measuredSnapshots;
    private long _measuredMessages;
    private TimeSpan _nextFriendReport;
    private int _friendErrors;
    private string? _recorderStopped;
    private int _slot = -1;
    private TimeSpan _nextStreamReport;
    private PlayoutTotals _reportedTotals;
    private long _reportedDecoded;
    private long _reportedMissing;

    /// <summary>Friend: loading or playing the host's match failed on this PC (said as a toast, by the online session).</summary>
    public Action? PlayFailed { get; set; }

    /// <summary>Host: a friend couldn't load the match (said as a toast, by the online session).</summary>
    public Action<ulong>? FriendLoadFailed { get; set; }

    /// <summary>Friend: true while a host's match mustn't load here yet: a friend in the lobby has handed nothing over. Its setup is held until this says so.</summary>
    public Func<bool>? HoldFriendLoads { get; set; }

    /// <summary>Friend: true once the host's board says it's back at its lobby: no rematch is coming, so a finished match goes at once, not after <see cref="NextMatchGrace"/>.</summary>
    public Func<bool>? HostBackAtLobby { get; set; }

    /// <summary>Creates the relay over the streaming tools.</summary>
    public MatchRelay(MatchStreaming streaming, string streamDirectory, string modVersion, ulong gameBuild)
    {
        _streaming = streaming;
        _streaming.RecordingStopped += reason => _recorderStopped = reason;
        _streamDirectory = streamDirectory;
        _modVersion = modVersion;
        _gameBuild = gameBuild;
    }

    private enum FriendPhase
    {
        Idle,
        Loading,
        Playing,
        Ending,
        Failed,
    }

    /// <summary>A friend as the host's log lines name them: their tag (D127).</summary>
    private static string Tag(ulong steamId) => ModLog.Scrub.Tags.Player(steamId);

    /// <summary>The host as a friend's log lines name them: their tag (D127); the screen's lines keep their name.</summary>
    private string HostTag() => _hostSteamId == 0 ? "the host" : Tag(_hostSteamId);

    /// <summary>True while this PC streams (or is about to stream) a match to friends.</summary>
    public bool HostStreaming => _host is { InMatch: true };

    /// <summary>
    /// True while this PC loads, plays or holds a host's match (held: its last frame, until its scene unloads). A finished
    /// campaign match this game follows isn't: the campaign follower takes this game on from it, and that scene
    /// change ends the hold.
    /// </summary>
    public bool FriendBusy => _phase is FriendPhase.Loading or FriendPhase.Ending || ((_phase == FriendPhase.Playing || HoldingEndedMatch) && !CampaignMatchOver);

    /// <summary>
    /// True while this PC holds the last frame of a host's match (<see cref="StreamPlayer.Hold"/>: the host ended it, or it
    /// can't go on here), until its scene unloads.
    /// </summary>
    public bool HoldingEndedMatch => _streaming.Player.Holding;

    /// <summary>True from a host match that couldn't be loaded or played here until the host's next match begins loading (the player was told).</summary>
    public bool FriendMatchFailed => _phase == FriendPhase.Failed;

    /// <summary>
    /// Friend: true once the host's match played here reached its end here (the host ended it and playback holds its last
    /// frame, or stopped there), until the next match loads: what the shared moments call a match seen to end (D135).
    /// </summary>
    public bool HostEndedHere => _phase != FriendPhase.Loading && PlayedHostMatch &&
        ((HoldingEndedMatch && _streaming.Player.StopReason == StreamPlayer.HostEnded) || (_client is { MatchEnded: true } && !_streaming.Playing));

    /// <summary>
    /// Friend: true while the host's match played here is loaded and this game plays it (the playing phase): from its
    /// load until playback stops; a finished campaign match this game follows also while held on its last frame, until
    /// its scene goes (any other match's hold is the ending phase: not counted, though its scene is still here). What
    /// the game's own objects of the match can be read in. The one-PC test loads nothing: there, while the host's own
    /// match, this game's, goes on (from its setup's arrival until the host leaves it; <see cref="MeasuresOnly"/>).
    /// </summary>
    public bool HostMatchLoadedHere => _measureOnly ? PlayedHostMatch && _client is { MatchEnded: false } : _phase == FriendPhase.Playing;

    /// <summary>Friend: true in the one-PC test, whose friend loads and plays nothing: the host's match is this game's own.</summary>
    public bool MeasuresOnly => _client != null && _measureOnly;

    /// <summary>True while the held match is a finished campaign match this game follows: the follower takes it from here.</summary>
    private bool CampaignMatchOver => HoldingEndedMatch && _streaming.Player.StopReason == StreamPlayer.HostEnded && FollowsCampaign;

    /// <summary>True when the host's current setup is a campaign match and this game follows the host's campaign.</summary>
    private bool FollowsCampaign => _client != null && CampaignMatchValues.IsCampaign(_client.Setup) && CampaignRelay.Current is { Following: true };

    /// <summary>
    /// Friend: true once one of the host's matches has loaded and played here in this session (the one-PC test, which
    /// loads nothing: once its setup arrived). Achievements from the host count only after that.
    /// </summary>
    public bool PlayedHostMatch { get; private set; }

    /// <summary>
    /// Friend: how many of the host's matches have played here in this game's life (the one-PC test: setups arrived); with
    /// <see cref="PlayedHostSerial"/>, the newest one's number on the host. The shared moments count only for these (D135).
    /// </summary>
    public int HostMatchesPlayed { get; private set; }

    /// <summary>
    /// Friend: the host's number for the newest of its matches played here (its setup's <c>match.serial</c>; 0 when it named
    /// none).
    /// </summary>
    public int PlayedHostSerial { get; private set; }

    // ---------------------------------------------------------------- host

    /// <summary>Hosting started: friends who join get the match stream (a match in play now is streamed at once).</summary>
    public StreamMessageHandler StartHosting(ISessionTransport transport)
    {
        StopHosting("a new session");
        _host = new MatchStreamHost(transport);
        MatchSetup.ReadForFriends = true;
        // Paced by what the retrying transport holds back for a friend, so a match's logos never overflow it.
        _content = new CustomContentSender(transport, transport is RetryingTransport retrying ? retrying.QueuedBytes : null);
        _contentDropsSaid = 0;
        _hostContent = new List<CustomItem>();
        _screenContent = new List<CustomItem>();
        _runLogo = null;
        _runLook = null;
        _handledCapture = MatchSetup.Captures;
        _streamedMatch = default;
        _joinCurrent = true;
        _hostErrors = 0;
        _unsharedSerial = 0;
        return _host.Receive;
    }

    /// <summary>A friend joined the host's session.</summary>
    public void FriendJoined(ulong steamId) => Guarded("adding a friend to the stream", () =>
    {
        // The custom content the match (and the campaign's run team) needs goes first; a rejoining friend gets it all again.
        _content?.Forget(steamId);
        SendContent(steamId);
        _host?.AddFriend(steamId);
        _reportedFailures.Remove(steamId);
    });

    /// <summary>A friend left the host's session.</summary>
    public void FriendLeft(ulong steamId) => Guarded("removing a friend from the stream", () =>
    {
        _host?.RemoveFriend(steamId);
        _content?.Forget(steamId);
        _rates.Remove(steamId);
    });

    /// <summary>Hosting ended: friends are told the match is over and the stream stops.</summary>
    public void StopHosting(string reason)
    {
        if (_host == null)
        {
            return;
        }

        Guarded("ending the hosted match", () => EndHostMatch(reason));
        _host = null;
        MatchSetup.ReadForFriends = false;
        _content = null;
        _hostContent = new List<CustomItem>();
        _screenContent = new List<CustomItem>();
        _runLogo = null;
        _runLook = null;
        _rates.Clear();
        ScoringSquad.Stop();
    }

    /// <summary>
    /// The host's match lifecycle: a match starting to load sends its setup (friends load while the host does); once
    /// it's in play and the game records it, it's streamed, with the loading barrier; when it's gone, friends are told.
    /// </summary>
    private void UpdateHost()
    {
        RestoreTimeScaleAfterMenu();
        if (_host == null)
        {
            return;
        }

        ScoringSquad.Update(AchievementMirror.Sharing?.Invoke() == true);
        UpdateRunLogo();
        UpdateContent();
        if (MatchSetup.Captures != _handledCapture)
        {
            _handledCapture = MatchSetup.Captures;
            _joinCurrent = false;
            EndHostMatch("a new match is loading");
            if (MatchSetup.LastCapture is not List<KeyValuePair<string, NetValue>> setup)
            {
                ModLog.Warn($"Online: this match won't be played online: {MatchSetup.LastCaptureProblem}.");
            }
            else if (MatchSetup.CampaignNotShared(setup) is string notShared)
            {
                // Nothing goes out, so friends don't load it and the match doesn't wait for them.
                _unsharedSerial = MatchSetup.LoadedSerial + 1;
                ModLog.Info($"Online: this campaign match isn't played online: {notShared}.");
            }
            else
            {
                // The match loading now will be the next one to finish loading: never stream an older one under its setup.
                SendSetup(new List<KeyValuePair<string, NetValue>>(setup), MatchSetup.LoadedSerial + 1, MatchSetup.LastCaptureContent);
            }
        }

        MatchToken current = MatchSetup.CurrentMatch(out ReplayScene? scene);
        if (_joinCurrent && !current.IsNone)
        {
            // Hosting started in the middle of a match: stream it as it is; friends join in progress.
            _joinCurrent = false;
            if (!MatchSetup.TryCapture(out List<KeyValuePair<string, NetValue>> setup, out List<CustomItem> content, out string problem))
            {
                ModLog.Warn($"Online: this match won't be played online: {problem}.");
            }
            else if (MatchSetup.CampaignNotShared(setup) is string notShared)
            {
                _unsharedSerial = current.Serial;
                ModLog.Info($"Online: this campaign match isn't played online: {notShared}.");
            }
            else
            {
                SendSetup(setup, current.Serial, content);
            }
        }

        if (!_host.InMatch)
        {
            return;
        }

        if (_recorderStopped is string why)
        {
            // The recorder failed and stopped itself: friends would wait for snapshots that never come.
            _recorderStopped = null;
            if (!_streamedMatch.IsNone)
            {
                EndHostMatch($"the stream recorder stopped ({why})");
                return;
            }
        }

        bool loaded = !current.IsNone && current.Serial >= _minSerial;
        if (_streamedMatch.IsNone)
        {
            if (loaded && scene != null)
            {
                StartStreaming(current);
            }
            else if (!loaded && _clock.Elapsed - _setupSentAt > MatchLoadTimeout)
            {
                // Loaded but not recorded yet is fine (the anthem; the recorder starts at the first faceoff). Never loaded isn't.
                EndHostMatch($"the match never finished loading ({MatchLoadTimeout.TotalSeconds:0} s; matches loaded so far: {MatchSetup.LoadedSerial})");
                return;
            }
        }
        else if (current != _streamedMatch)
        {
            EndHostMatch(current.IsNone ? "the host left the match" : "another match started");
            return;
        }

        ReportFriendFailures();
        UpdateBarrier();
        ReportHost();
    }

    /// <summary>
    /// The match's setup, with its own id, goes to every friend now and to friends who join later, after the custom content
    /// it names (<paramref name="content"/>; each friend gets each piece once a session).
    /// </summary>
    private void SendSetup(List<KeyValuePair<string, NetValue>> setup, int minSerial, IReadOnlyList<CustomItem> content)
    {
        // Every match gets its own id, so a rematch with the very same teams is still a new match to friends; and the number
        // this game loads it as, which its shared moments carry (a friend's game checks it played that match, D135).
        setup.Add(new KeyValuePair<string, NetValue>(MatchSetupValues.MatchIdKey, NetValue.FromInt(_random.Next(1, int.MaxValue))));
        setup.Add(new KeyValuePair<string, NetValue>(MatchSetupValues.HostSerialKey, NetValue.FromInt(minSerial)));
        _hostContent = new List<CustomItem>(content);
        if (_hostContent.Count > 0)
        {
            ModLog.Info($"Online: this match uses {_hostContent.Count} piece(s) of this PC's custom content " +
                $"({string.Join(", ", _hostContent.Select(i => i.Ref.ToString()))}; {_hostContent.Sum(i => (long)i.Bytes.Length) / 1024} KB); friends get each once a session.");
            foreach (StreamFriend friend in _host!.Friends)
            {
                SendContent(friend.SteamId);
            }
        }

        _host!.BeginMatch(setup);
        _hostSetup = setup;
        _minSerial = minSerial;
        _setupSentAt = _clock.Elapsed;
        _streamedMatch = default;
        _reportedFailures.Clear();
        _contentHold.Clear();
        _holdExtended.Clear();
        NoteContent(force: true);
        ModLog.Info($"Online: sent this match's setup to {_host.Friends.Count} friend(s) ({MatchSetup.Describe(setup)}; setup {_host.SetupHash:x16}); " +
            "they load it now, and the stream starts once the game records the match.");
    }

    private void StartStreaming(MatchToken match)
    {
        if (_streaming.Playing || _streaming.Recording)
        {
            // An F3 recording or F4 playback has the match.
            if (_blockedMatch != match)
            {
                _blockedMatch = match;
                ModLog.Warn("Online: a test recording or playback (F3/F4) has this match, so it isn't streamed to friends. Stop it (F3/F4).");
            }

            return;
        }

        var header = new MatchStreamHeader(ProtocolInfo.Version, _modVersion, _gameBuild, DateTime.UtcNow.Ticks, "online match (host)");
        // A file too only when asked (a developer aid for F2 and the analysis tools): it costs a second encode per snapshot
        // (about 24 µs, measured on a recording) and about 10 MB per 400 s on disk, up to 20 kept.
        // A campaign check tour's record=on records into the tour's own folder: the recorder prunes the folder it records into.
        string? tourFolder = NativeLobby.LobbyUiCheck.TourStreamsFolder;
        string? directory = Startup.ModSettings.RecordOnlineMatches ? tourFolder ?? _streamDirectory : null;
        if (!_streaming.StartRecording(() => SnapshotProbe.LastScene, directory, header, _host, _hostSetup, out string problem))
        {
            if (_blockedMatch != match)
            {
                _blockedMatch = match;
                ModLog.Warn($"Online: couldn't start streaming the match: {problem}.");
            }

            return;
        }

        _streamedMatch = match;
        // A new window from each friend's session totals as they are now (otherwise the first report of every
        // match after the first divided the session's totals by 10 s).
        _rates.Rebase(_clock.Elapsed.TotalSeconds, _host!.Friends.Select(f => (f.SteamId, f.SnapshotsSent, f.SnapshotBytes)));
        _nextHostReport = _clock.Elapsed + ReportInterval;
        int loading = _host!.Friends.Count(f => f.Loading);
        // The recording by its file name in the streams folder: a full path can hold the Windows user's name (D127).
        string recorded = _streaming.FilePath is string file ? $"Also recorded to {(tourFolder == null ? "the streams folder" : "the check tour's folder")} ({System.IO.Path.GetFileName(file)})."
            : directory == null ? "Not recorded to a file (the RecordOnlineMatches setting)." : "Not recorded to a file (it couldn't be made).";
        ModLog.Info(string.Create(CultureInfo.InvariantCulture, $"Online: streaming this match ({MatchSetup.SecondsSinceLoaded:0.0} s after it loaded) to friends: {_host.Friends.Count(f => f.Streaming)} ready, {loading} still loading") +
            $"{(loading > 0 ? $"; the match waits for them up to {BarrierTimeout.TotalSeconds:0} s (longer while this PC's custom content still reaches them, at most {ContentHold.MaxHold.TotalSeconds:0} s)" : string.Empty)}. {recorded}");
        _contentHold.StreamStarted(_clock.Elapsed);
        if (match.Serial != _minSerial)
        {
            // Its shared moments carry this number, and friends' games know it by the setup's (D135).
            ModLog.Warn($"Online: this match loaded as match {match.Serial}, not the {_minSerial} its setup told friends; their games won't credit its shared moments.");
        }

        if (loading > 0)
        {
            _holding = true;
            MatchIntro.HostBarrier(true);
            _holdUntil = _clock.Elapsed + BarrierTimeout;
            _heldTimeScale = Time.timeScale > 0.01f ? Time.timeScale : 1f;
            _holdFrames = _holdResets = 0;
        }
    }

    private void EndHostMatch(string reason)
    {
        ReleaseBarrier("the match ended");
        if (_streaming.Streaming)
        {
            Guarded("stopping the stream recorder", () => _streaming.StopRecording(reason));
        }

        if (_host is { InMatch: true })
        {
            ReportHost(force: true);
            _host.EndMatch();
            ModLog.Info($"Online: the match stream ended ({reason}); friends go back to their menu.");
        }

        _streamedMatch = default;
        _hostSetup = null;
    }

    /// <summary>
    /// A friend's match load failure for the host's log: a fixed one as it is, a known kind by its first words and the length of
    /// the rest (which can quote the host's setup back), any other by its length (its own log has it in full).
    /// </summary>
    private static string LoadFailureForLog(string sent) => MatchLoadFailures.ForLog(sent, FixedLoadFailures);

    /// <summary>A friend who couldn't load the match says why: it goes in the host's log once, and they're no longer waited for.</summary>
    private void ReportFriendFailures()
    {
        foreach (StreamFriend friend in _host!.Friends)
        {
            if (friend.LoadFailure.Length > 0 && _reportedFailures.Add(friend.SteamId))
            {
                ModLog.Warn($"Online: {Tag(friend.SteamId)} couldn't load this match: {LoadFailureForLog(friend.LoadFailure)}.");
                FriendLoadFailed?.Invoke(friend.SteamId);
            }
            else if (friend.LoadedWrongMatch && _reportedFailures.Add(friend.SteamId))
            {
                ModLog.Warn($"Online: {Tag(friend.SteamId)} says they're ready for a different match ({friend.ReadyHash:x16}); waiting for them to load this one.");
            }
        }
    }

    /// <summary>The loading barrier: the match stands still while a joined friend is still loading it.</summary>
    private void UpdateBarrier()
    {
        if (!_holding)
        {
            return;
        }

        // Each friend still loading holds the match for its own time: the barrier's 45 s, longer while this PC's custom
        // content still moves to them (ContentHold).
        TimeSpan now = _clock.Elapsed;
        int loading = 0, held = 0;
        foreach (StreamFriend friend in _host!.Friends)
        {
            if (!friend.Loading)
            {
                continue;
            }

            loading++;
            if (_contentHold.Holds(friend.SteamId, now))
            {
                held++;
                if (_contentHold.Extended(friend.SteamId, now) && _holdExtended.Add(friend.SteamId))
                {
                    ModLog.Info($"Online: the match waits for {Tag(friend.SteamId)} while this PC's custom content reaches them " +
                        $"({_contentHold.Of(friend.SteamId)!.Describe(now)}); up to {ContentHold.MaxHold.TotalSeconds:0} s after the stream started.");
                }
            }
        }

        if (loading == 0)
        {
            ReleaseBarrier("every friend has loaded the match (or said why they can't)" + ContentSummary(now));
            return;
        }

        if (held == 0)
        {
            var why = new List<string>();
            foreach (StreamFriend friend in _host.Friends)
            {
                if (friend.Loading)
                {
                    why.Add($"{Tag(friend.SteamId)}: {_contentHold.Ended(friend.SteamId, now)}");
                }
            }

            ReleaseBarrier($"{loading} friend(s) still loading ({string.Join("; ", why)}); they join in progress");
            return;
        }

        if (HostMenuOpen())
        {
            // The host's pause menu owns the time scale while it's open.
            return;
        }

        if (Time.timeScale > 0.01f)
        {
            // After the first frame, a time scale that isn't 0 was set by the game (a menu closing, a state change).
            _holdResets += _holdFrames > 0 ? 1 : 0;
            TourBarrierResets += _holdFrames > 0 ? 1 : 0;
            Time.timeScale = 0f;
        }

        _holdFrames++;
    }

    private void ReleaseBarrier(string why)
    {
        if (!_holding)
        {
            return;
        }

        _holding = false;
        MatchIntro.HostBarrier(false);
        if (HostMenuOpen())
        {
            // Unfreezing under the pause menu would run the match behind it: once it's closed.
            _restoreAfterMenu = true;
        }
        else
        {
            Time.timeScale = _heldTimeScale;
        }

        ModLog.Info($"Online: the match goes on ({why}); it was held for {_holdFrames} frames{(_holdResets > 0 ? $", and the game set the time scale back {_holdResets} times meanwhile" : string.Empty)}" +
            $"{(_restoreAfterMenu ? "; time runs again once the pause menu closes" : string.Empty)}.");
    }

    /// <summary>
    /// A barrier released under the pause menu: once the menu is closed, the match must run (the menu may have saved our
    /// 0). Runs every frame from <see cref="Update"/>, and from <see cref="UpdateAfterError"/> once online is off.
    /// </summary>
    private void RestoreTimeScaleAfterMenu()
    {
        if (!_restoreAfterMenu || HostMenuOpen())
        {
            return;
        }

        _restoreAfterMenu = false;
        if (Time.timeScale < 0.01f)
        {
            Time.timeScale = _heldTimeScale;
        }
    }

    private static bool HostMenuOpen()
    {
        try
        {
            Il2CppUI.InGameMenu? menu = Il2CppUI.InGameMenu.Instance;
            return menu != null && menu.IsGameMenuActive;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void ReportHost(bool force = false)
    {
        if (_host == null || (!force && _clock.Elapsed < _nextHostReport))
        {
            return;
        }

        double now = _clock.Elapsed.TotalSeconds;
        _nextHostReport = _clock.Elapsed + ReportInterval;
        string costs = _streaming.TakeRecordingCosts();
        if (costs.Length > 0)
        {
            ModLog.Info($"Online stream cost on this PC's main thread since the last report (mean/p95/max per snapshot, sample or event): {costs}; {_hostMemory.Take()}.");
        }

        foreach (StreamFriend friend in _host.Friends)
        {
            RateSample rate = _rates.Take(friend.SteamId, friend.SnapshotsSent, friend.SnapshotBytes, now);
            string state = friend.Streaming ? "streaming" : friend.LoadFailure.Length > 0 ? $"couldn't load ({LoadFailureForLog(friend.LoadFailure)})"
                : friend.LoadedWrongMatch ? "loaded a different match" : "loading";
            ModLog.Info(string.Create(CultureInfo.InvariantCulture,
                $"Online stream to {Tag(friend.SteamId)}: {state}; {rate.CountPerSecond:0}/s snapshots, {rate.Kbps:0} kbps, ") +
                $"avg {rate.BytesPerCount} B; {friend.Peer.Keyframes} keyframes / {friend.Peer.Deltas} deltas / {friend.Peer.Held} held in all, acked {friend.Peer.Acked}; " +
                $"{friend.MessagesSent} other messages, {friend.BytesSent / 1024} KB in all; {friend.SendFailures} refused sends, " +
                $"{friend.OversizeSends} over one packet, {friend.Peer.BogusAcks} acks for snapshots never sent" + TheirPlayout(friend) + ".");
        }

        _rates.EndWindow(now);
    }

    /// <summary>
    /// The host's Play Now screen shows teams that use this PC's custom content: it goes to every friend (and to one who joins
    /// while it's up), each piece once a session, so their real screen can show those teams before the match's setup names them.
    /// </summary>
    public void ShareScreenContent(IReadOnlyList<CustomItem> items) => Guarded("sending the controller screen's custom content", () =>
    {
        _screenContent = new List<CustomItem>(items);
        if (_host != null)
        {
            foreach (StreamFriend friend in _host.Friends)
            {
                SendContent(friend.SteamId);
            }
        }
    });

    /// <summary>
    /// The custom content friends need: the current match's, the Play Now screen's, the custom logo the host's shared campaign run team wears, and
    /// that team's look beyond its run file (<see cref="RunLook"/>). Each piece goes to <paramref name="friend"/> once a session (again when their store dropped it to make room), as fast as their link takes it
    /// (<see cref="CustomContentSender"/>; the rest from <see cref="UpdateContent"/>).
    /// </summary>
    private void SendContent(ulong friend)
    {
        if (_content == null)
        {
            return;
        }

        var items = new List<CustomItem>(_hostContent);
        items.AddRange(_screenContent);
        if (_runLogo != null)
        {
            items.Add(_runLogo);
        }

        if (_runLook != null)
        {
            items.Add(_runLook);
        }

        _content.Send(friend, items);
        ReportContent();
    }

    /// <summary>
    /// Every frame while hosting: more custom content goes to each friend as their link takes it (a match's logos can come
    /// to several MB, more than Steam and the retrying transport hold at once), and what finished is logged.
    /// </summary>
    private void UpdateContent()
    {
        if (_content == null)
        {
            return;
        }

        _content.Update();
        ReportContent();
        NoteContent(force: false);
    }

    /// <summary>
    /// A few times a second from a match's setup until its stream starts, and while the barrier holds: how far each friend's
    /// custom content has gone (the sender's queue and what the transport still holds back), for the barrier
    /// (<see cref="ContentHold"/>).
    /// </summary>
    private void NoteContent(bool force)
    {
        TimeSpan now = _clock.Elapsed;
        if (_content == null || _host is not { InMatch: true } || (!force && now < _nextContentNote) || (!_holding && !_streamedMatch.IsNone))
        {
            return;
        }

        _nextContentNote = now + TimeSpan.FromSeconds(0.25);
        foreach (StreamFriend friend in _host.Friends)
        {
            ContentProgress progress = _content.Progress(friend.SteamId);
            long held = progress.PendingBytes > 0 ? _content.HeldBytes(friend.SteamId) : 0;
            _contentHold.Note(friend.SteamId, progress.PendingBytes + held, progress.BatchBytes, now);
        }
    }

    /// <summary>"; custom content: "Name" (N) 4.0 of 4.0 MB, 240 KB/s", for the barrier's line (empty when none moved this match).</summary>
    private string ContentSummary(TimeSpan now)
    {
        var parts = new List<string>();
        foreach (StreamFriend friend in _host!.Friends)
        {
            if (_contentHold.Of(friend.SteamId) is { StartedAt: not null } movement)
            {
                parts.Add($"{Tag(friend.SteamId)} {movement.Describe(now)}");
            }
        }

        return parts.Count == 0 ? string.Empty : $"; custom content: {string.Join(", ", parts)}";
    }

    /// <summary>The status box's word on the barrier: how far the custom content still moving to a friend has gone, else the seconds left.</summary>
    private string HoldStatus()
    {
        TimeSpan now = _clock.Elapsed;
        TimeSpan? latest = null;
        foreach (StreamFriend friend in _host!.Friends)
        {
            if (!friend.Loading)
            {
                continue;
            }

            if (_contentHold.Of(friend.SteamId) is { StartedAt: not null, PendingBytes: > 0 } movement)
            {
                return $"your custom content reaching them: {movement.Describe(now)}";
            }

            TimeSpan? deadline = _contentHold.Deadline(friend.SteamId, now);
            latest = deadline != null && (latest == null || deadline > latest) ? deadline : latest;
        }

        return $"{Math.Max(0, ((latest ?? _holdUntil) - now).TotalSeconds):0} s";
    }

    private void ReportContent()
    {
        while (_content != null && _content.TryTakeDelivery(out CustomContentDelivery delivery))
        {
            if (delivery.Refused)
            {
                ModLog.Warn($"Online: Steam refused custom content for {Tag(delivery.Friend)} ({_content.Refused} refused piece(s) in all); what was under way goes " +
                    "again from its start when they rejoin or the next match starts.");
            }
            else
            {
                // How many items friends' stores have dropped to make room (each sent again when named), when that moved: a long
                // session's resends show in the log.
                long dropped = _content.Dropped;
                string drops = dropped != _contentDropsSaid ? $"; {dropped} item(s) in all counted as dropped from friends' stores so far, each sent again when named" : string.Empty;
                _contentDropsSaid = dropped;
                ModLog.Info($"Online: sent {delivery.Items} piece(s) of custom content to {Tag(delivery.Friend)} ({delivery.Bytes / 1024} KB{drops}).");
            }
        }
    }

    /// <summary>
    /// Once a second while hosting a shared campaign: the run team's custom logo, if it wears one, and its look beyond the run
    /// file (<see cref="RunLook"/>) go to every friend (again when they change).
    /// </summary>
    private void UpdateRunLogo()
    {
        if (_clock.Elapsed < _nextRunLogoCheck)
        {
            return;
        }

        _nextRunLogoCheck = _clock.Elapsed + TimeSpan.FromSeconds(1);
        bool shared = RunSync.Instance?.HostOnCoopSlot == true;
        CustomItem? look = shared ? RunLook.Item(CampaignGame.State()) : null;
        bool lookChanged = look != null && _runLook?.Hash != look.Hash;
        _runLook = look;
        CustomItem? logo = shared ? CustomContent.RunLogoItem(CampaignGame.State()) : null;
        if (logo == null)
        {
            _runLogo = null;
            if (lookChanged)
            {
                foreach (StreamFriend friend in _host!.Friends)
                {
                    SendContent(friend.SteamId);
                }
            }

            return;
        }

        bool changed = _runLogo?.Hash != logo.Hash || lookChanged;
        _runLogo = logo;
        if (changed)
        {
            foreach (StreamFriend friend in _host!.Friends)
            {
                SendContent(friend.SteamId);
            }
        }
    }

    // ---------------------------------------------------------------- friend

    /// <summary>
    /// Joined a host's session: their match stream comes through the returned client. <paramref name="measureOnly"/>
    /// (the one-PC test): receive and acknowledge everything but load and play nothing.
    /// </summary>
    public MatchStreamClient StartFriend(ISessionTransport transport, ulong host, string hostName, bool measureOnly)
    {
        StopFriend("a new session");
        _client = new MatchStreamClient(transport, host);
        _hostName = hostName;
        _hostSteamId = host;
        _measureOnly = measureOnly;
        if (!measureOnly)
        {
            // The one-PC test is the host itself: its own custom content is already in its game.
            CustomContent.StartFriend(_client.Content, hostName);
        }

        _handledSetup = 0;
        _tourFailedSetup = 0;
        _setupWaitVersion = 0;
        _loadedHash = 0;
        _measuredSnapshots = _measuredMessages = 0;
        _friendErrors = 0;
        PlayedHostMatch = false;
        ResetStreamReport();
        SetPhase(FriendPhase.Idle);
        _nextFriendReport = _clock.Elapsed + ReportInterval;
        return _client;
    }

    /// <summary>
    /// The friend session joined the host (first time, or again after a drop): the host has forgotten our ready, so the
    /// match we're playing is announced again, by the hash of the match actually loaded (a newer setup that arrived with
    /// the welcome hasn't been loaded yet).
    /// </summary>
    public void FriendSessionJoined(int slot) => Guarded("re-announcing the loaded match", () =>
    {
        _slot = slot;
        PlayerProxies.YourRewired = slot;
        if (_client == null)
        {
            return;
        }

        if (_measureOnly ? _handledSetup > 0 && _handledSetup != _tourFailedSetup : _phase == FriendPhase.Playing && _loadedHash != 0 && _loadedHash == _client.SetupHash)
        {
            _client.SendReady(_client.SetupHash, true);
            _streaming.StreamRestarted();
        }
    });

    /// <summary>
    /// The session with the host ended: playback stops, and the host's match, if it's loaded here (or still loading), is
    /// left for the main menu, as after a match's end. <paramref name="backToMenu"/> false (the game is closing) leaves it be.
    /// </summary>
    public void StopFriend(string reason, bool backToMenu = true)
    {
        if (_client == null)
        {
            return;
        }

        if (_streaming.Player.Source is LiveStreamSource)
        {
            // Left for the menu: held until its scene unloads. The game closing: stopped now.
            Guarded("stopping playback", () =>
            {
                if (backToMenu)
                {
                    _streaming.Player.Hold($"the session ended ({reason})");
                }
                else
                {
                    _streaming.StopPlaying(reason);
                }
            });
        }

        // First, so nothing below that fails can leave the host's logos in this game's logo list; it never
        // throws, and a match still held here keeps what it uses.
        CustomContent.StopFriend(reason);
        if (_phase == FriendPhase.Loading)
        {
            // A load the game's loading screen has started brings a match in anyway (the game's own pick): left once it's in.
            // One still waiting for the host's run or custom content is only dropped: nothing comes in, and the next match to
            // load is this player's own. One that came in on this very frame (the game has finished loading it, and
            // WaitForLoad hasn't taken it up yet) is the host's too: left the same way.
            MatchToken loaded = MatchSetup.CurrentMatch(out _);
            bool leave = LateLoad.LeaveWhenIn(backToMenu, MatchSetup.LoadingHostScene, matchCameIn: !loaded.IsNone && loaded != _before);
            MatchSetup.CancelLoad();
            _leaveWhenLoaded = leave;
            _leaveBefore = _before;
            _leaveUntil = _clock.Elapsed + LoadTimeout;
        }

        MatchIntro.ReleaseFriend($"the session with {HostTag()} ended");
        if (backToMenu)
        {
            LeaveHostMatch($"the session with {HostTag()} ended ({reason})");
        }

        _hostMatch = default;
        _client = null;
        SetPhase(FriendPhase.Idle);
    }

    /// <summary>
    /// The host's match is done here (it ended with no new one, playback failed, or the session ended): while it's still
    /// loaded (held on its last frame), back to the main menu, as after a match's end, waiting out a load under way and
    /// trying again (<see cref="KeepLeaving"/>).
    /// </summary>
    private void LeaveHostMatch(string why)
    {
        MatchToken match = _hostMatch;
        _hostMatch = default;
        if (match.IsNone || MatchSetup.CurrentMatch(out _) != match)
        {
            return; // never loaded here, or already left
        }

        ModLog.Info($"Online: {why}; leaving {HostTag()}'s match for the main menu.");
        StartLeaving(match);
    }

    /// <summary>A host match whose load was under way when the session ended: once it's in, it's left too.</summary>
    private void LeaveLateLoad()
    {
        MatchToken current = MatchSetup.CurrentMatch(out _);
        if (!current.IsNone && current != _leaveBefore)
        {
            _leaveWhenLoaded = false;
            ModLog.Info("Online: the match that was loading when the session ended is in; leaving it for the main menu.");
            StartLeaving(current);
        }
        else if (_clock.Elapsed > _leaveUntil)
        {
            _leaveWhenLoaded = false;
        }
    }

    /// <summary>A host's match is being left for the main menu here, or will be once a load under way brings it in: the game's quit takes seconds, and hides every menu at its end.</summary>
    public bool LeavingHostMatch => !_leaving.IsNone || _leaveWhenLoaded;

    /// <summary>Leaves <paramref name="match"/> for the main menu now, or as soon as the game can (<see cref="KeepLeaving"/>).</summary>
    private void StartLeaving(MatchToken match)
    {
        _leaving = match;
        _leavingUntil = _clock.Elapsed + LoadTimeout;
        _leaveRetryAt = TimeSpan.Zero;
        _leaveTries = 0;
        _leaveWaitLogged = false;
        Guarded("returning to the menu", KeepLeaving);
    }

    /// <summary>
    /// A host's match being left for the main menu, every frame until it's gone. The game's quit loads the start screen
    /// through its loading screen, which ignores a load while another is starting (<c>LoadingScreenManager.LoadScene</c>):
    /// a quit then (a campaign follower's map load, say) would do nothing, and the match could stay after the session. So a
    /// load under way is waited for (it replaces the match anyway), and the quit is tried again while the match is still
    /// here, up to <see cref="MaxLeaveTries"/> times. Callers guard it.
    /// </summary>
    private void KeepLeaving()
    {
        if (_leaving.IsNone)
        {
            return;
        }

        if (MatchSetup.CurrentMatch(out _) != _leaving || _clock.Elapsed > _leavingUntil)
        {
            if (MatchSetup.CurrentMatch(out _) == _leaving)
            {
                ModLog.Warn($"Online: {HostTag()}'s match is still here {LoadTimeout.TotalSeconds:0} s after leaving it; quit to the main menu from its pause menu.");
            }

            _leaving = default;
            return;
        }

        if (_clock.Elapsed < _leaveRetryAt)
        {
            return;
        }

        // Asked only when a quit is due (a search of the scene): a load under way is waited for, at each try.
        if (MatchSetup.SceneLoadStarting)
        {
            if (!_leaveWaitLogged)
            {
                _leaveWaitLogged = true;
                ModLog.Info("Online: a scene load is under way; the host's match is left once it's done, if it's still here then.");
            }

            return;
        }

        if (_leaveTries == MaxLeaveTries)
        {
            ModLog.Warn($"Online: {HostTag()}'s match is still here after {MaxLeaveTries} quits; quit to the main menu from its pause menu.");
            _leaving = default;
            return;
        }

        _leaveTries++;
        _leaveRetryAt = _clock.Elapsed + LeaveRetryInterval;
        if (_leaveTries > 1)
        {
            ModLog.Info($"Online: {HostTag()}'s match is still here; quitting it again ({_leaveTries} of {MaxLeaveTries}).");
        }

        MatchSetup.ReturnToMenu();
    }

    private void UpdateFriend()
    {
        if (_client == null)
        {
            return;
        }

        _client.Update(FrameClock.NowMicros);
        if (_measureOnly)
        {
            Measure();
            return;
        }

        if (_client.SetupVersion != _handledSetup && _client.Setup.Count > 0 && HoldFriendLoads?.Invoke() != true)
        {
            if (_client.MatchEnded)
            {
                // Over before it could load here (held in the lobby, or waiting its turn): loading it would only leave it again.
                _handledSetup = _client.SetupVersion;
                ModLog.Info($"Online: {HostTag()}'s match ({Describe(_client.Setup)}) was over before it could load here; not loading it.");
            }
            else if (!SetupWaitsItsTurn())
            {
                _handledSetup = _client.SetupVersion;
                LoadHostMatch();
            }
        }

        switch (_phase)
        {
            case FriendPhase.Loading:
                WaitForLoad();
                break;
            case FriendPhase.Playing when !_streaming.Playing:
                PlaybackStopped();
                break;
            case FriendPhase.Playing when HoldingEndedMatch:
                PlaybackHeld();
                break;
            case FriendPhase.Ending when _clock.Elapsed - _phaseSince >= NextMatchGrace || HostBackAtLobby?.Invoke() == true:
                SetPhase(FriendPhase.Idle);
                if (FollowsCampaign)
                {
                    // A campaign match: the host's game goes on to its rewards and the world map, and the campaign
                    // follower takes this game there. The main menu would leave the host's campaign.
                    _hostMatch = default;
                    ModLog.Info($"Online: {HostTag()}'s campaign match is over; this game follows the host's campaign from here.");
                    break;
                }

                // No new match followed: back to the main menu, as the game's own "quit" after a match does (a load under
                // way is waited for, and the quit tried again). The held match stays held until it unloads.
                LeaveHostMatch($"{HostTag()}'s match is over and no new one followed");
                break;
            case FriendPhase.Idle or FriendPhase.Failed when _client.QueuedSnapshots > 0 && (!_streaming.Playing || HoldingEndedMatch):
                _client.DropQueued(); // not playing anything (a held match takes nothing more): nothing to keep
                break;
        }

        SendStreamReport();
        ReportFriend();
    }

    /// <summary>
    /// A new exhibition's setup while this game can't start a load yet: its loading screen is starting another scene (it ignores
    /// a second one until that scene is in: this PC's quit from the last match, its way home) or it's between places one can
    /// load from. The setup comes once, so failing here left this friend out of the whole match; it waits its turn instead, as
    /// a campaign match's own load does (<c>MatchSetup.StepCampaignLoad</c>), up to <see cref="SetupWaitLimit"/>, after which
    /// the load says why it can't. The host's barrier holds its match meanwhile. While it waits, the game is asked again a few
    /// times a second, not every frame (the question searches the scene for its loading screen).
    /// </summary>
    private bool SetupWaitsItsTurn()
    {
        TimeSpan now = _clock.Elapsed;
        if (_setupWaitVersion == _client!.SetupVersion && now < _setupWaitNextCheck)
        {
            return now - _setupWaitFrom < SetupWaitLimit;
        }

        string? wait = CampaignMatchValues.IsCampaign(_client.Setup) ? null : MatchSetup.ExhibitionLoadWait();
        if (wait == null)
        {
            return false;
        }

        _setupWaitNextCheck = now + SetupWaitCheckInterval;
        if (_setupWaitVersion != _client.SetupVersion)
        {
            _setupWaitVersion = _client.SetupVersion;
            _setupWaitFrom = now;
            ModLog.Info($"Online: {HostTag()} started a match; it loads here as soon as this game can ({wait}).");
        }

        return now - _setupWaitFrom < SetupWaitLimit;
    }

    /// <summary>"; they see it 15 ms behind the stream ...": the friend's newest playout report, for the host's log.</summary>
    private static string TheirPlayout(StreamFriend friend)
    {
        if (friend.ReportMicros == long.MinValue)
        {
            return string.Empty;
        }

        StreamReport r = friend.Report;
        return string.Create(CultureInfo.InvariantCulture,
            $"; they see it {r.RenderBehindMicros / 1000.0:0.0} ms behind the stream (aim {r.TargetDelayMicros / 1000.0:0.0}, jitter {r.JitterMicros / 1000.0:0.0}), " +
            $"{100.0 * r.HeldFrames / Math.Max(1, r.Frames):0.0}% held, {r.Frames} fps");
    }

    /// <summary>Host: the match stream's state for one friend, or null (not hosting, or not a friend here).</summary>
    public StreamFriend? StreamTo(ulong steamId) => _host?.Find(steamId);

    /// <summary>Friend: the host's stream as received here, or null.</summary>
    public MatchStreamClient? FromHost => _client;

    /// <summary>Friend: true while playing the host's match (the playout report is current; not while it's held).</summary>
    public bool PlayingHostMatch => _phase == FriendPhase.Playing && _streaming.Player.Showing && !HoldingEndedMatch;

    /// <summary>The newest playout report this PC sent its host (its own net overlay shows it too).</summary>
    public StreamReport LastStreamReport { get; private set; }

    /// <summary>A new session or playback: the next report counts from now (a new client counts from 0).</summary>
    private void ResetStreamReport()
    {
        _reportedTotals = default;
        _reportedDecoded = _client?.Receiver.Decoded ?? 0;
        _reportedMissing = _client?.Receiver.MissingBase ?? 0;
        _nextStreamReport = _clock.Elapsed + StreamReportInterval;
        LastStreamReport = default;
    }

    /// <summary>
    /// Friend, playing: about once a second, how playout went since the last report (render delay, holds, what arrived),
    /// for the host's net overlay and its fight grace window.
    /// </summary>
    private void SendStreamReport()
    {
        if (_client == null || !PlayingHostMatch || _clock.Elapsed < _nextStreamReport)
        {
            return;
        }

        _nextStreamReport = _clock.Elapsed + StreamReportInterval;
        PlayoutTotals totals = _streaming.Player.Totals;
        if (totals.Frames < _reportedTotals.Frames)
        {
            _reportedTotals = default; // a new playback started over
        }

        long frames = totals.Frames - _reportedTotals.Frames;
        long decoded = _client.Receiver.Decoded;
        long missing = _client.Receiver.MissingBase;
        static uint Clamp(long value) => (uint)Math.Clamp(value, 0, uint.MaxValue);
        LastStreamReport = new StreamReport(
            Clamp(frames == 0 ? 0 : (totals.BehindMicros - _reportedTotals.BehindMicros) / frames),
            Clamp(totals.TargetDelay),
            Clamp(totals.Jitter),
            Clamp(frames),
            Clamp(Math.Min(frames, totals.HeldFrames - _reportedTotals.HeldFrames)),
            Clamp(totals.Holds - _reportedTotals.Holds),
            Clamp(decoded - _reportedDecoded),
            Clamp(missing - _reportedMissing));
        _reportedTotals = totals;
        _reportedDecoded = decoded;
        _reportedMissing = missing;
        _client.SendReport(LastStreamReport);
    }

    private void LoadHostMatch()
    {
        if (_streaming.Playing)
        {
            // The last match stays held, in client mode, until the new match's load unloads it: stopping now would hand it
            // back to this game, which would play it on with the AI meanwhile (a recording, F4, just stops).
            _streaming.Player.Hold("the host started a new match");
            if (HoldingEndedMatch)
            {
                ModLog.Info($"Online: {HostTag()}'s last match stays held here until the new one replaces it.");
            }
        }

        if (_streaming.Recording)
        {
            _streaming.StopRecording("the host started a new match");
        }

        _loadedHash = 0;
        _leaveWhenLoaded = false;
        MatchIntro.ArmFriend();
        ModLog.Info($"Online: {HostTag()} started a match ({Describe(_client!.Setup)}; setup {_client.SetupHash:x16}); loading it here.");
        _before = MatchSetup.CurrentMatch(out _);
        if (!MatchSetup.TryLoad(_client.Setup, out string problem))
        {
            Fail(MatchLoadFailure.NotLoaded, problem, tellHost: true);
            return;
        }

        SetPhase(FriendPhase.Loading);
        _loadFrom = _clock.Elapsed;
    }

    private void WaitForLoad()
    {
        MatchToken current = MatchSetup.CurrentMatch(out ReplayScene? scene);
        if (current.IsNone || current == _before)
        {
            // A campaign match loads the host's run first, so its load can fail after it started: say why now.
            if (MatchSetup.TakeLoadProblem(out string loadProblem))
            {
                Fail(MatchLoadFailure.NotLoaded, loadProblem, tellHost: true);
                return;
            }

            if (MatchSetup.WaitingForContent)
            {
                // The host's custom content still arriving has its own limits (ContentWait); this one counts from its arrival.
                _loadFrom = _clock.Elapsed;
            }

            if (_clock.Elapsed - _loadFrom > LoadTimeout)
            {
                MatchSetup.CancelLoad();
                Fail(MatchLoadFailure.NotLoadedInTime, $"{LoadTimeout.TotalSeconds:0} s (matches loaded so far: {MatchSetup.LoadedSerial}; " +
                    $"{(MatchSetup.Loading ? "still waiting for it" : "the load was dropped")})", tellHost: true);
            }

            return;
        }

        // Loaded: check it's the same match, then play the host's stream onto it.
        _hostMatch = current;
        if (MatchSetup.TryCapture(out List<KeyValuePair<string, NetValue>> local, out string captureProblem))
        {
            string differences = SafeText.Clean(MatchSetupValues.Differences(_client!.Setup, local, LogNames.TextShape), 400);
            ModLog.Info(differences.Length == 0
                ? $"Online: the match loaded here has exactly {HostTag()}'s setup."
                : $"Online: the match loaded here differs from {HostTag()}'s: {differences}. Playing it anyway; objects that differ won't be found.");
        }
        else
        {
            ModLog.Warn($"Online: couldn't read back the loaded match's setup ({captureProblem}).");
        }

        if (HoldingEndedMatch)
        {
            // The last match's hold (kept through this load) ends with its scene; in case that hasn't been seen yet.
            _streaming.StopPlaying("the next match loaded");
        }

        if (!_streaming.Play(new LiveStreamSource(_client!, HostTag()), scene, PlayMatch.Fresh, out string problem))
        {
            Fail(MatchLoadFailure.NotPlayed, problem, tellHost: true);
            return;
        }

        _loadedHash = _client!.SetupHash;
        _client.SendReady(_loadedHash, true);
        TimeSpan loadedAfter = _clock.Elapsed - _phaseSince; // the Loading phase's start: before the phase changes
        SetPhase(FriendPhase.Playing);
        PlayedHostMatch = true;
        NotePlayed(_client.Setup);
        ModLog.Info(string.Create(CultureInfo.InvariantCulture, $"Online: {HostTag()}'s match is loaded here after {loadedAfter.TotalSeconds:0.0} s; playing their stream."));
        ResetStreamReport();
    }

    private void PlaybackStopped()
    {
        if (_client!.MatchEnded)
        {
            if (CampaignMatchValues.IsCampaign(_client.Setup) && CampaignRelay.Current is { Following: true })
            {
                // A campaign match has no rematch: its scene unloaded because this game followed the host on.
                SetPhase(FriendPhase.Idle);
                _hostMatch = default;
                ModLog.Info($"Online: {HostTag()}'s campaign match is over and unloaded here; this game follows the host's campaign from here.");
                return;
            }

            // The host's match is over. A rematch or a new match sends its setup right after: wait a moment for it.
            SetPhase(FriendPhase.Ending);
            return;
        }

        string why = _streaming.Player.StopReason;
        if (why == StreamPlayer.LeftMatch)
        {
            // Left locally (the pause menu's quit): the host stops sending until we load again.
            ModLog.Info($"Online: playback of {HostTag()}'s match stopped here ({why}); telling them.");
            _client.SendReady(_loadedHash, false);
            _loadedHash = 0;
            SetPhase(FriendPhase.Idle);
            LeaveHostMatch("playback stopped with the match still loaded here");
            return;
        }

        // Anything else (the match unloaded under playback, a start that failed) goes on screen and to the host.
        _loadedHash = 0;
        Fail(MatchLoadFailure.PlaybackStopped, why, tellHost: true);
    }

    /// <summary>
    /// Playback holds the host's match on its last frame (<see cref="StreamPlayer.Hold"/>) until its scene unloads; this
    /// decides what unloads it. The host ended it: a campaign match this game follows is taken on by the campaign follower
    /// (nothing to do here), any other waits <see cref="NextMatchGrace"/> for a rematch or a new match before going back to
    /// the main menu. It can't go on here (an error): the reason goes on screen and to the host, and it's left for the
    /// main menu now.
    /// </summary>
    private void PlaybackHeld()
    {
        string why = _streaming.Player.StopReason;
        if (why == StreamPlayer.HostEnded)
        {
            if (!FollowsCampaign)
            {
                SetPhase(FriendPhase.Ending);
            }

            return;
        }

        _loadedHash = 0;
        Fail(MatchLoadFailure.PlaybackStopped, why, tellHost: true);
    }

    /// <summary>A host match played here: its number on the host (<see cref="PlayedHostSerial"/>) for the shared moments.</summary>
    private void NotePlayed(IReadOnlyList<KeyValuePair<string, NetValue>> setup)
    {
        PlayedHostSerial = MatchSetupValues.HostSerialOf(setup);
        HostMatchesPlayed++;
    }

    /// <summary>The one-PC test: everything is received and acknowledged; the numbers go in the log.</summary>
    private void Measure()
    {
        if (_client!.SetupVersion != _handledSetup && _client.Setup.Count > 0)
        {
            _handledSetup = _client.SetupVersion;
            if (TourFailsThisLoad())
            {
                return; // the check tour's stand-in said it couldn't load this match (MatchRelay.Tour.cs)
            }

            _client.SendReady(_client.SetupHash, true);
            PlayedHostMatch = true;
            NotePlayed(_client.Setup);
            ModLog.Info($"Online one-PC test: your match's setup arrived over Steam ({Describe(_client.Setup)}); measuring the stream (nothing is loaded).");
        }

        while (_client.TryTakeSnapshot(out _))
        {
            _measuredSnapshots++;
        }

        while (_client.TryTakeMessage(out _))
        {
            _measuredMessages++;
        }

        ReportFriend();
    }

    private void ReportFriend()
    {
        if (_client == null || _clock.Elapsed < _nextFriendReport)
        {
            return;
        }

        _nextFriendReport = _clock.Elapsed + ReportInterval;
        SnapshotReceiver receiver = _client.Receiver;
        ModLog.Info($"Online stream from {HostTag()}: {receiver.Decoded} snapshots decoded ({receiver.MissingBase} without their base, {receiver.Malformed} malformed), " +
            $"newest {receiver.Newest}; {_client.Objects.Count} objects, {_client.Events.Count} events in the tables; {_client.Malformed} bad messages; " +
            $"{_client.Overflowed} dropped unplayed, {_client.Oversized} too large, {_client.EarlySnapshots} before this PC played the match" +
            $"{(_measureOnly ? $"; measured {_measuredSnapshots} snapshots and {_measuredMessages} other messages" : string.Empty)}.");
        string costs = _streaming.Player.TakeCostReport();
        if (costs.Length > 0)
        {
            ModLog.Info($"Online playback cost on this PC's main thread since the last report (mean/p95/max per frame or per snapshot interval): {costs}; {_friendMemory.Take()}.");
        }
    }

    /// <summary>
    /// Loading or playing the host's match failed: the reason (<paramref name="kind"/>'s words with <paramref name="detail"/>)
    /// goes on screen, in the log, and (when it's about loading) to the host, and a host match still loaded here is left for
    /// the main menu. Made from its kind only, so the host's log always knows its opening words (<see cref="MatchLoadFailures"/>).
    /// </summary>
    private void Fail(MatchLoadFailure kind, string detail, bool tellHost)
    {
        string problem = SafeText.Clean(MatchLoadFailures.Reason(kind, detail), 240); // it can quote the host's setup
        _problem = problem;
        SetPhase(FriendPhase.Failed);
        MatchIntro.ReleaseFriend("the host's match can't be played here");
        ModLog.Warn($"Online: {problem}.");

        // The loading transition shown for this load would cover the screen, and block the game's UI, until a guard's limit:
        // it goes now, before the notice, so the notice is seen.
        Campaign.Menus.FriendTransitions.LoadGivenUp(Campaign.Menus.FriendTransitions.HostMatch, "the host's match can't load here");
        PlayFailed?.Invoke();
        if (tellHost && _client != null)
        {
            _client.SendReady(_client.SetupHash, false, problem.Length > MatchReadyMessage.MaxReasonBytes / 2 ? problem[..(MatchReadyMessage.MaxReasonBytes / 2)] : problem);
        }

        LeaveHostMatch("the host's match can't be played here");
    }

    /// <summary>A setup's description for the log, cleaned: its team names come from the host.</summary>
    private static string Describe(IReadOnlyList<KeyValuePair<string, NetValue>> setup) => SafeText.Clean(MatchSetup.Describe(setup), 200);

    private void SetPhase(FriendPhase phase)
    {
        _phase = phase;
        _phaseSince = _clock.Elapsed;
        if (phase != FriendPhase.Failed)
        {
            _problem = string.Empty;
        }
    }

    // ---------------------------------------------------------------- both

    /// <summary>Call once per frame (after the session has drained its messages).</summary>
    public void Update()
    {
        try
        {
            UpdateHost();
        }
        catch (Exception ex)
        {
            // Never leave the host's match held, or friends watching a stream that's no longer fed.
            if (_hostErrors++ < MaxErrorLogs)
            {
                ModLog.Error("Online: the host's match stream failed; ending it for friends", ex);
            }

            Guarded("ending the hosted match", () => EndHostMatch("an error"));
        }

        try
        {
            UpdateFriend();
        }
        catch (Exception ex)
        {
            if (_friendErrors++ < MaxErrorLogs)
            {
                ModLog.Error("Online: following the host's match failed", ex);
            }

            if (_phase is FriendPhase.Loading or FriendPhase.Playing)
            {
                // Held, not handed back: left for the main menu below (Fail).
                Guarded("holding playback", () => _streaming.Player.Hold("an error following the host's match"));
                MatchSetup.CancelLoad();
                Fail(MatchLoadFailure.FollowingFailed, ex.GetType().Name, tellHost: true);
            }
        }

        if (_leaveWhenLoaded)
        {
            Guarded("leaving a match that loaded after the session ended", LeaveLateLoad);
        }

        if (!_leaving.IsNone)
        {
            Guarded("returning to the menu", KeepLeaving);
        }
    }

    /// <summary>
    /// Call every frame instead of <see cref="Update"/> once an error has turned online off (both sessions already
    /// stopped): what the session left still gets done. A barrier released under the pause menu gives the match its time
    /// back once the menu closes, a host match whose load was under way is left once it's in, and a host match being left
    /// is left once the game can. Failures reach the caller, which logs the first few.
    /// </summary>
    public void UpdateAfterError()
    {
        RestoreTimeScaleAfterMenu();
        if (_leaveWhenLoaded)
        {
            LeaveLateLoad();
        }

        KeepLeaving();
    }

    /// <summary>Lines for the on-screen status box.</summary>
    public IEnumerable<string> Status()
    {
        if (_host is { InMatch: true })
        {
            int loading = _host.Friends.Count(f => f.Loading);
            int failed = _host.Friends.Count(f => f.LoadFailure.Length > 0);
            yield return _holding
                ? $"   waiting for {loading} friend(s) to load the match · {HoldStatus()}"
                : $"   streaming the match to {_host.Friends.Count(f => f.Streaming)} friend(s){(loading > 0 ? $" ({loading} still loading)" : string.Empty)}";
            if (failed > 0)
            {
                yield return $"   {failed} friend(s) couldn't load the match (see the log)";
            }
        }
        else if (_host != null && _unsharedSerial != 0 && MatchSetup.CurrentMatch(out _).Serial == _unsharedSerial)
        {
            yield return "   this campaign match isn't shared with friends (the log says why)";
        }

        if (_client != null && !_measureOnly)
        {
            switch (_phase)
            {
                case FriendPhase.Loading:
                    yield return $"   loading {_hostName}'s match...";
                    break;
                case FriendPhase.Playing:
                    yield return HoldingEndedMatch ? $"   {_hostName}'s match is over{(FollowsCampaign ? "; following them" : string.Empty)}"
                        : !_streaming.Player.Showing ? $"   loaded; waiting for {_hostName}'s stream..."
                        : _streaming.Player.StreamStopped ? $"   {_hostName}'s match is stopped (paused, or waiting for everyone to load)"
                        : $"   watching {_hostName}'s match{You()}";
                    break;
                case FriendPhase.Ending:
                    yield return $"   {_hostName}'s match is over";
                    break;
                case FriendPhase.Idle when HoldingEndedMatch:
                    yield return $"   {_hostName}'s match is over; back to the main menu";
                    break;
                case FriendPhase.Failed:
                    yield return $"   {_problem}";
                    break;
            }
        }
        else if (_client != null && _handledSetup > 0)
        {
            yield return $"   stream over Steam: {_client.Receiver.Decoded} snapshots received";
        }
    }

    /// <summary>
    /// " · your marker: orange" (in that colour): which marker over the skaters is this PC's pad, by its Rewired player on
    /// the host, once the stream has said. Player numbers wouldn't help: the friend never sees the host's join screen.
    /// </summary>
    private string You() =>
        _slot >= 0 && PlayerProxies.TryMarkerColour(_slot, out Color colour)
            ? $" · your marker: {StatusOverlay.Coloured(PlayerProxies.ColourName(colour), colour)}"
            : string.Empty;

    private static void Guarded(string what, Action action)
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
}
#endif
