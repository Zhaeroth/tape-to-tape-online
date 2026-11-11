#if HAS_INTEROP
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using T2TOnline.Core.Campaign;
using T2TOnline.Core.Diagnostics;
using T2TOnline.Core.Protocol;
using T2TOnline.Core.Serialization;
using T2TOnline.Core.Session;
using T2TOnline.Plugin.Campaign;
using T2TOnline.Plugin.Diagnostics;
using T2TOnline.Steam;

namespace T2TOnline.Plugin.Online;

/// <summary>
/// The campaign half of an online session, beside the match half (<see cref="MatchRelay"/>):
/// <list type="bullet">
/// <item>Host: the run checkpoints and host views the modules publish go to every friend through
/// <see cref="CampaignHost"/>, once each and again after a rejoin; each friend's follow reports are logged.</item>
/// <item>Friend: the host's views and checkpoints go to a <see cref="CampaignFollower"/>; each step it asks for goes to
/// the <see cref="IScreenFollower"/> for the view's screen; its reports go back to the host. Other campaign messages go
/// to the module that owns their type.</item>
/// </list>
/// The parts (run sync, scenes, matches, menus, progress) are <see cref="ICampaignModule"/>s from
/// <see cref="CampaignModules.Create"/>. Nothing here throws into the game: a module that throws is logged and switched
/// off for the rest of the session (the others carry on), and still gets the session's stop call, which puts back what it
/// changed; a follower that throws, or whose screen is dropped mid-move, counts as a failed move. A session that ends while
/// a scene this PC loads for the host is still loading is put away once that scene is in (<see cref="StopFollowing"/>),
/// and a new session started meanwhile starts only then, with what its link brought held for it.
/// </summary>
internal sealed class CampaignRelay
{
    /// <summary>Longest wait for a scene load to land before a session's end is put away regardless.</summary>
    private const double LandingSeconds = 90;

    /// <summary>Most message bytes held for a session waiting to start (a few run checkpoints and every progress batch).</summary>
    private const long MaxHeldBytes = 32L * 1024 * 1024;

    /// <summary>Friend: this PC's game couldn't follow the host's to a screen (said as a toast, by the online session).</summary>
    public Action? FollowFailed { get; set; }

    /// <summary>Host: a friend's game reports it couldn't follow this one (said as a toast, by the online session).</summary>
    public Action<ulong>? FriendFollowFailed { get; set; }

    /// <summary>A part of the campaign was switched off by an error (said as a toast, by the online session).</summary>
    public Action? ModuleSwitchedOff { get; set; }

    private readonly List<ICampaignModule> _modules;
    private readonly Dictionary<string, IScreenFollower> _followers = new(StringComparer.Ordinal);
    private readonly Dictionary<IScreenFollower, ICampaignModule> _followerOwners = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<MessageType, ICampaignModule> _owners = new();
    private readonly Dictionary<MessageType, ICampaignModule> _friendOwners = new();
    private readonly HashSet<ICampaignModule> _off = new();
    private readonly Dictionary<ulong, FollowReport> _reported = new();
    private readonly Dictionary<ulong, double> _failingSince = new();
    private readonly HashSet<ulong> _failingTold = new();

    // Report lines logged per friend in the current minute: a buggy or modified friend can't fill the host's log.
    private readonly Dictionary<ulong, (double Since, int Lines)> _reportBudget = new();
    private const int ReportLinesPerMinute = 20;

    // A friend's game tries a view three times, two seconds apart (CampaignFollower): ten seconds of failing is all of them used up.
    private const double FailingToldAfterSeconds = 10;
    private readonly Func<ulong, string> _tagOf;
    private CampaignHost? _host;
    private CampaignClient? _client;
    private IScreenFollower? _screen;
    private string _hostName = string.Empty;
    private HostView? _lastLogged;
    private long _now;
    private int _oversizeLogged;

    // Answers the follower's pending step (a move or a run update) as failed when its screen is dropped before it answers.
    private Action? _abandonStep;

    // The first view of this following was said (what this PC came into).
    private bool _firstViewSaid;

    // Following ended while a scene load was landing: everything is put away once it's in (or after LandingSeconds).
    private string? _stopping;
    private double _stoppingSince;

    // A session started while that end waits: it starts once everything is put away.
    private PendingStart? _pending;

    // Made once: Update calls it every frame of a friend's session.
    private readonly Action _updateScreen;

    /// <summary>
    /// Creates the relay with <paramref name="modules"/>; <paramref name="tagOf"/> names a Steam ID in the log (its tag, D127).
    /// </summary>
    public CampaignRelay(IEnumerable<ICampaignModule> modules, Func<ulong, string> tagOf)
    {
        _modules = modules?.ToList() ?? throw new ArgumentNullException(nameof(modules));
        _tagOf = tagOf ?? throw new ArgumentNullException(nameof(tagOf));
        _updateScreen = () => _screen?.Update();
        foreach (ICampaignModule module in _modules)
        {
            foreach (MessageType type in module.Owns)
            {
                if (!_owners.TryAdd(type, module))
                {
                    throw new InvalidOperationException($"Both {_owners[type].Name} and {module.Name} read {type}.");
                }
            }

            foreach (MessageType type in module.OwnsFromFriends)
            {
                if (!_friendOwners.TryAdd(type, module))
                {
                    throw new InvalidOperationException($"Both {_friendOwners[type].Name} and {module.Name} read {type} from friends.");
                }
            }
        }
    }

    /// <summary>The relay of the current session (for patches), or null when neither hosting nor following.</summary>
    public static CampaignRelay? Current { get; private set; }

    /// <summary>True while this PC hosts.</summary>
    public bool Hosting => _host != null;

    /// <summary>
    /// True while this PC follows a host (and while a session that ended waits for a scene load to land: the friend-side
    /// guards hold until everything is put away).
    /// </summary>
    public bool Following => _client != null;

    /// <summary>The host's name (following), for messages on screen.</summary>
    public string HostName => _hostName;

    /// <summary>
    /// Hosting: this hosting session's id (the friends' welcome's, D34), set before hosting starts; the modules use it as
    /// their session too (progress batches). 0 when not known.
    /// </summary>
    public ulong HostingSession { get; set; }

    /// <summary>Friends in the campaign link (hosting).</summary>
    public IReadOnlyList<ulong> Friends => _host?.Friends ?? Array.Empty<ulong>();

    /// <summary>The newest view this PC published (hosting) or was sent (following), or null.</summary>
    public HostView? View => _host?.View ?? _client?.Follower.Target;

    /// <summary>The view the friend's game is on (following), or null.</summary>
    public HostView? Showing => _client?.Follower.Current;

    /// <summary>The newest run checkpoint this PC published or holds, or null.</summary>
    public RunCheckpoint? LatestRun => _host?.Checkpoints.Latest ?? _client?.Follower.Checkpoints.Latest;

    /// <summary>The newest checkpoint the friend's game applied (following; 0: none).</summary>
    public uint AppliedRun => _client?.Follower.AppliedCheckpoint ?? 0;

    /// <summary>The module of type <typeparamref name="T"/>, or null (not built in, or switched off after an error).</summary>
    public T? Get<T>()
        where T : class, ICampaignModule =>
        _modules.OfType<T>().FirstOrDefault(m => !_off.Contains(m));

    /// <summary>
    /// Starts hosting over <paramref name="transport"/>; returns the handler for friends' campaign messages. While the last
    /// session's end waits for a scene to land, hosting starts once it has (<see cref="Update"/>).
    /// </summary>
    public StreamMessageHandler StartHosting(ISessionTransport transport)
    {
        if (!Defer("hosting", "hosting", following: false, () => BeginHosting(transport)))
        {
            BeginHosting(transport);
        }

        return (sender, _, message, _) => _pending is { Following: false } ? Hold(sender, message) : _host?.Receive(sender, message) ?? false;
    }

    /// <summary>A friend joined or rejoined: they get the newest run and view, and each module resends what it holds.</summary>
    public void FriendJoined(ulong friend)
    {
        if (_pending is { Following: false } pending)
        {
            if (!pending.Friends.Contains(friend))
            {
                pending.Friends.Add(friend); // joins once hosting starts
            }

            return;
        }

        if (_host == null)
        {
            return;
        }

        _host.AddFriend(friend);
        _reported.Remove(friend);
        _failingSince.Remove(friend);
        _failingTold.Remove(friend);
        Guard("saying what a friend who joined gets", () => SayJoined(friend));
        ForEach("adding a friend", m => m.FriendJoined(friend));
    }

    /// <summary>What state <paramref name="friend"/> joined into; what they're sent then has lines of its own, per module.</summary>
    private void SayJoined(ulong friend)
    {
        HostView? view = _host?.View;
        int menus = Get<Campaign.Menus.MenuViews>()?.Host.OpenMenus().Count ?? 0;
        ModLog.Info($"Campaign: {Tag(friend)} joined while the host is on the {view?.Screen ?? "no"} screen (view {view?.Epoch ?? 0}: {view}; run checkpoint {_host?.Checkpoints.Latest?.Sequence ?? 0}" +
            $"{(menus > 0 ? $"; {menus} menu(s) open" : string.Empty)}); they get those now.");
    }

    /// <summary>A friend left.</summary>
    public void FriendLeft(ulong friend)
    {
        if (_pending is { Following: false } pending)
        {
            pending.Friends.Remove(friend);
            return;
        }

        if (_host == null)
        {
            return;
        }

        _host.RemoveFriend(friend);
        _reported.Remove(friend);
        _failingSince.Remove(friend);
        _failingTold.Remove(friend);
        ForEach("removing a friend", m => m.FriendLeft(friend));
    }

    /// <summary>Stops hosting.</summary>
    public void StopHosting(string reason)
    {
        if (_pending is { Following: false })
        {
            DropPending(reason);
            return;
        }

        if (_host == null)
        {
            return;
        }

        ModLog.Info($"Campaign: the link to friends ended ({reason}): {_host.Checkpoints.Latest?.Sequence ?? 0} run checkpoint(s) and " +
            $"{_host.View?.Epoch ?? 0} view(s) published, {_host.OversizeCheckpoints} too big to send, {_host.RefusedSends} send(s) refused (and retried), " +
            $"{_host.MalformedReports} malformed report(s).");
        ForAll("stopping hosting", m => m.StopHosting(reason));
        _host = null;
        _reported.Clear();
        _reportBudget.Clear();
        _failingSince.Clear();
        _failingTold.Clear();
        Current = _client != null ? this : null;
    }

    /// <summary>
    /// Host: the host's game saved its run (the run file's bytes). It goes to friends as the next checkpoint unless it's
    /// unchanged. Returns the checkpoint, or null (unchanged, or not hosting).
    /// </summary>
    public RunCheckpoint? PublishRun(byte[] runFile)
    {
        if (_host == null)
        {
            return null;
        }

        RunCheckpoint? checkpoint = _host.PublishRun(new Dictionary<RunCheckpointPart, byte[]> { [RunCheckpointPart.Run] = runFile });
        if (checkpoint != null)
        {
            // What it cost the host's frame (a checkpoint and its map load cost friends' inputs).
            PublishCost cost = _host.LastPublishCost;
            ModLog.Info(string.Create(CultureInfo.InvariantCulture, $"Campaign: run checkpoint {checkpoint.Sequence} ({checkpoint.RawBytes / 1024.0:0.0} KB, hash {checkpoint.Hash:x16}) for {Friends.Count} friend(s)") +
                $"{(Friends.Count > 0 ? $" ({string.Join(", ", Friends.Select(Tag))})" : string.Empty)}; " +
                string.Create(CultureInfo.InvariantCulture, $"made in {cost.HashMs + cost.DeflateMs:0.0} ms (hash {cost.HashMs:0.0}, deflate {cost.DeflateMs:0.0}") +
                $"{(cost.MessageBytes > 0 ? string.Create(CultureInfo.InvariantCulture, $", {cost.MessageBytes / 1024.0:0.0} KB to send") : cost.MessageBytes < 0 ? ", too big to send" : string.Empty)}).");
        }

        return checkpoint;
    }

    /// <summary>
    /// Host: the host's game is on <paramref name="screen"/> with <paramref name="details"/>; friends follow it.
    /// <paramref name="needsRun"/>: friends need the newest checkpoint before they follow. Returns the view.
    /// </summary>
    public HostView? PublishView(string screen, IReadOnlyList<KeyValuePair<string, NetValue>> details, bool needsRun = true)
    {
        if (_host == null)
        {
            return null;
        }

        HostView? before = _host.View;
        HostView view = _host.SetView(screen, details, needsRun);
        if (!ReferenceEquals(before, view) && !(view.SameExcept(before, SceneViewKeys.Transient) && before!.Checkpoint == view.Checkpoint))
        {
            // Cursor and camera moves (several a second) aren't logged; every other change is.
            ModLog.Info($"Campaign: host view {view.Epoch}: {view} (needs run {view.Checkpoint}).");
        }

        return view;
    }

    /// <summary>
    /// Host: bytes of reliable messages to <paramref name="friend"/> still waiting in the session's retrying transport (Steam
    /// refused them for now; Core.Session.RetryingTransport resends them in order). 0 when nothing waits. Modules that send
    /// in bulk (shared progress's resend after a rejoin) pace themselves on it, so they never fill the queue's cap.
    /// </summary>
    public long QueuedBytes(ulong friend) => QueuedBytesSource?.Invoke(friend) ?? 0;

    /// <summary>Set by the online session: reads the host's retrying transport (see <see cref="QueuedBytes"/>).</summary>
    public Func<ulong, long>? QueuedBytesSource { get; set; }

    /// <summary>
    /// Friend: true while the link to the host is up (a session joined, the host heard from lately), false while it is lost or
    /// winning back (a rejoin's hello, a lagging host). True when the online session doesn't say. Shared progress's wait for a
    /// copy of the host's run counts only the time it's up.
    /// </summary>
    public bool LinkUp => LinkUpSource?.Invoke() ?? true;

    /// <summary>Set by the online session: whether the friend session is joined and the host isn't silent (see <see cref="LinkUp"/>).</summary>
    public Func<bool>? LinkUpSource { get; set; }

    /// <summary>
    /// Friend: the hosting session of this PC's current welcome (0: not joined now). Only that session's progress counts
    /// here (D135): a host can't start a session's limits over by naming another in its messages.
    /// </summary>
    public ulong FollowedSession => FollowedSessionSource?.Invoke() ?? 0;

    /// <summary>Set by the online session: the joined friend session's welcome (see <see cref="FollowedSession"/>).</summary>
    public Func<ulong>? FollowedSessionSource { get; set; }

    /// <summary>Friend: the Steam ID of the host this PC follows (0: not following). Never logged.</summary>
    public ulong FollowedHost { get; private set; }

    /// <summary>Friend: how many times the link to the host (re)started (<see cref="LinkJoined"/>): a rejoin is one more.</summary>
    public int LinkJoins { get; private set; }

    /// <summary>Host: a module's message to every friend. Returns how many it went to.</summary>
    public int Broadcast(MessageType type, Channel channel, Action<BitWriter> payload) => _host?.Broadcast(type, channel, payload) ?? 0;

    /// <summary>Host: a module's message to one friend. False when they aren't in the session or the send was refused.</summary>
    public bool SendTo(ulong friend, MessageType type, Channel channel, Action<BitWriter> payload) =>
        _host?.SendTo(friend, type, channel, payload) ?? false;

    /// <summary>Friend: a module's message to the host (a progress resend request). False when not following or the send was refused.</summary>
    public bool SendToHost(MessageType type, Channel channel, Action<BitWriter> payload) =>
        _stopping == null && _client != null && _client.SendToHost(type, channel, payload);

    /// <summary>How the log names a Steam ID: its tag in the lobby's log tags (D127), never a name or the ID.</summary>
    public string Tag(ulong friend) => _tagOf(friend);

    /// <summary>The same as <see cref="Tag"/> (a log's name for a Steam ID is its tag), for the campaign's callers of this name.</summary>
    public string NameOf(ulong friend) => _tagOf(friend);

    /// <summary>Friend: how the log names the host followed: its tag ("the host" while none is followed).</summary>
    public string HostTag() => FollowedHost == 0 ? "the host" : _tagOf(FollowedHost);

    /// <summary>Host: <paramref name="friend"/>'s newest follow report, or false.</summary>
    public bool TryGetReport(ulong friend, out FollowReport report)
    {
        report = default;
        return _host != null && _host.Follows.TryGet(friend, out report);
    }

    /// <summary>
    /// Starts following <paramref name="host"/> over <paramref name="transport"/>; returns the handler for the host's campaign
    /// messages. While the last session's end waits for a scene to land, following starts once it has (<see cref="Update"/>).
    /// </summary>
    public StreamMessageHandler StartFollowing(ISessionTransport transport, ulong host, string hostName)
    {
        if (!Defer($"following {Tag(host)}", $"following {SafeText.Clean(hostName, 32)}", following: true, () => BeginFollowing(transport, host, hostName)))
        {
            BeginFollowing(transport, host, hostName);
        }

        return (sender, _, message, _) => _pending is { Following: true } ? Hold(sender, message) : _client?.Receive(sender, message) ?? false;
    }

    private void BeginHosting(ISessionTransport transport)
    {
        EndFollowing("hosting instead", gameClosing: false);
        if (_host == null)
        {
            _off.Clear(); // a new session: every module takes part again
        }

        _host = new CampaignHost(transport) { FriendMessages = ReadFromFriend };
        _reported.Clear();
        _reportBudget.Clear();
        _failingSince.Clear();
        _failingTold.Clear();
        _oversizeLogged = 0;
        Current = this;
        ForEach("starting to host", m => m.StartHosting(this));
    }

    private void BeginFollowing(ISessionTransport transport, ulong host, string hostName)
    {
        EndFollowing("a new session", gameClosing: false);
        if (_host == null)
        {
            _off.Clear(); // a new session: every module takes part again
        }

        _client = new CampaignClient(transport, host) { OtherMessages = ReadOther };
        FollowedHost = host;
        _hostName = hostName;
        _screen = null;
        _lastLogged = null;
        _firstViewSaid = false;
        _followers.Clear();
        _followerOwners.Clear();
        foreach (ICampaignModule module in _modules)
        {
            foreach (IScreenFollower follower in Followers(module))
            {
                _followers[follower.Screen] = follower;
                _followerOwners[follower] = module;
            }
        }

        Current = this;
        ForEach("starting to follow", m => m.StartFollowing(this));
    }

    /// <summary>
    /// Stops following. <paramref name="gameClosing"/>: no scene loads, just put things back. While a scene load is landing
    /// (this PC loading the world map for the host, say) and <paramref name="waitForLoad"/>, nothing is put back yet: the
    /// screen is left and following stops at once, but the host's run and the friend-side guards stay until the scene is in
    /// (at most <see cref="LandingSeconds"/>). Put back mid-load, the world map would land on this game's own reset run and
    /// start (and save) a new run over it.
    /// </summary>
    public void StopFollowing(string reason, bool gameClosing, bool waitForLoad = true)
    {
        if (_pending != null && (_pending.Following || gameClosing || !waitForLoad))
        {
            // The session waiting to start ended first; the last one's end goes on waiting for its scene (unless the game
            // or the mod is stopping: then everything is put away now, and nothing starts).
            bool waiting = _pending.Following && !gameClosing && waitForLoad;
            DropPending(reason);
            if (waiting)
            {
                return;
            }
        }

        if (_client == null)
        {
            return;
        }

        if (!gameClosing && waitForLoad && SceneLanding())
        {
            if (_stopping == null)
            {
                _stopping = reason;
                _stoppingSince = Streaming.FrameClock.Seconds;
                LeaveScreen();
                ModLog.Info($"Campaign: stopped following {HostTag()} ({reason}) while this game was loading a scene; the host's run and this PC's guards " +
                    "stay until it's in, so the scene never starts a run of this game's own.");
            }

            return;
        }

        EndFollowing(_stopping ?? reason, gameClosing);
    }

    /// <summary>
    /// Following: the link to the host (re)started (a first join, or a rejoin after a drop). The host forgot where this
    /// game is when the friend rejoined, so the follow report goes out again.
    /// </summary>
    public void LinkJoined()
    {
        LinkJoins++;
        if (_pending is { Following: true } pending)
        {
            pending.LinkJoined = true; // said again once following starts
            return;
        }

        if (_client != null && _stopping == null)
        {
            _client.ReportAgain();
        }
    }

    /// <summary>
    /// After an error turned online off: a session's end that waits for a scene load is still put away once it's in; a
    /// session that was waiting to start doesn't start.
    /// </summary>
    public void UpdateAfterError()
    {
        if (_pending != null)
        {
            DropPending("online is off after an error");
        }

        if (_stopping != null)
        {
            PutAwayWhenLanded();
        }
    }

    /// <summary>Every frame: modules, sends, and (following) the next step towards the host's view.</summary>
    public void Update(long nowMicros)
    {
        _now = nowMicros;
        if (_stopping != null)
        {
            // Nothing else runs: the session is over. A session started meanwhile starts once it's put away (at once if
            // what its link brought meanwhile got too big to hold).
            if (_pending is { Overflowed: true })
            {
                ModLog.Warn($"Campaign: too much arrived for {_pending.What} while the last session's scene was loading; putting that session away now.");
                EndFollowing(_stopping, gameClosing: false);
            }
            else
            {
                PutAwayWhenLanded();
            }

            if (_stopping == null)
            {
                StartPending();
            }

            return;
        }

        if (_host == null && _client == null)
        {
            return;
        }

        ForEach("updating", m => m.Update());
        if (_host != null)
        {
            _host.Update();
            LogOversize();
            LogReports();
        }

        if (_client != null)
        {
            Step(_client, nowMicros);
            Guard("the current screen", _updateScreen);
            _client.Update();
        }
    }

    /// <summary>Lines for the status box.</summary>
    public IEnumerable<string> Status()
    {
        var lines = new List<string>();
        if (_stopping != null)
        {
            return new[] { $"leaving {_hostName}'s campaign once this scene has loaded{(_pending != null ? $", then {_pending.Shown}" : string.Empty)}..." };
        }

        if (_host?.View is { } shown)
        {
            // Where each friend is, when they aren't simply following (a friend still on the way, or stuck, is worth knowing
            // before a choice is made without them).
            foreach (ulong friend in _host.Friends)
            {
                if (TryGetReport(friend, out FollowReport report))
                {
                    lines.Add(FriendLine(friend, report, shown));
                }
            }
        }

        if (_host != null || _client != null)
        {
            foreach (ICampaignModule module in _modules)
            {
                if (_off.Contains(module))
                {
                    lines.Add($"campaign: {Part(module)} stopped after an error (see the log)");
                }
            }
        }

        if (_client != null)
        {
            FollowReport report = _client.Follower.Report;
            HostView? target = _client.Follower.Target;
            if (target != null && target.Screen != Screens.Title)
            {
                lines.Add(report.State switch
                {
                    FollowState.WaitingForRun => $"getting {_hostName}'s run...",
                    FollowState.Moving => $"following {_hostName} to the {Describe(target)}...",
                    FollowState.Failed => $"couldn't follow {_hostName} to the {Describe(target)}: {report.Reason}",
                    _ => string.Empty,
                });
            }
        }

        foreach (ICampaignModule module in _modules)
        {
            if (!_off.Contains(module))
            {
                Guard($"{module.Name}'s status", () => lines.AddRange(module.Status()));
            }
        }

        return lines.Where(l => l.Length > 0);
    }

    /// <summary>Every frame while following: the follower's next step, if any. Captures nothing, so a frame with no step allocates nothing.</summary>
    private void Step(CampaignClient client, long nowMicros)
    {
        CampaignFollower follower = client.Follower;
        switch (follower.Next(nowMicros))
        {
            case FollowStep.GoToView:
                GoTo(follower, follower.Target!, nowMicros);
                break;
            case FollowStep.UpdateRun:
                UpdateRun(follower);
                break;
        }
    }

    /// <summary>The follower's <see cref="FollowStep.UpdateRun"/>: the screen showing brings the newest checkpoint in, and answers once.</summary>
    private void UpdateRun(CampaignFollower follower)
    {
        RunCheckpoint run = follower.Checkpoints.Latest!;
        if (_screen == null)
        {
            follower.RunUpdated(0, ok: true); // nothing showing that needs it; the next screen applies it
            return;
        }

        IScreenFollower screen = _screen;
        Func<bool> answer = Pending(() => follower.RunUpdated(0, ok: false));
        Guard($"bringing run {run.Sequence} into the {screen.Screen} screen", () => screen.UpdateRun(run, result =>
        {
            if (!answer())
            {
                return;
            }

            if (!result.Ok)
            {
                ModLog.Warn($"Campaign: couldn't bring the host's run {run.Sequence} into the {HostText.KeyForLog(screen.Screen)} screen: {result.Reason}.");
            }

            follower.RunUpdated(result.Applied ? result.Checkpoint : 0, result.Ok);
        }), onError: () =>
        {
            if (answer())
            {
                follower.RunUpdated(0, ok: false);
            }
        });
    }

    private void GoTo(CampaignFollower follower, HostView view, long nowMicros)
    {
        if (!_followers.TryGetValue(view.Screen, out IScreenFollower? next))
        {
            follower.Missed($"this version can't show the host's \"{HostText.KeyForLog(view.Screen)}\" screen", nowMicros);
            return;
        }

        if (_followerOwners.TryGetValue(next, out ICampaignModule? owner) && _off.Contains(owner))
        {
            follower.Missed($"{Part(owner)} stopped after an error on this PC (see its log)", nowMicros);
            return;
        }

        if (!ReferenceEquals(next, _screen))
        {
            LeaveScreen();
            _screen = next;
        }

        Func<bool> answer = Pending(() =>
        {
            follower.Missed($"the {HostText.KeyForLog(view.Screen)} screen was put away before it got there", _now);
            ModLog.Warn($"Campaign: the move to the host's {HostText.KeyForLog(view.Screen)} screen (view {view.Epoch}) was dropped before it finished.");
        });
        RunCheckpoint? run = view.Checkpoint == 0 ? null : follower.Checkpoints.Latest;
        if (!_firstViewSaid)
        {
            // What this PC came into: the host's line for the same join names the same view.
            _firstViewSaid = true;
            ModLog.Info($"Campaign: first view from {HostTag()}: the {HostText.KeyForLog(view.Screen)} screen (view {view.Epoch}, needs run {view.Checkpoint}); this PC holds run {follower.AppliedCheckpoint}" +
                $"{(view.Checkpoint != 0 ? "; joined mid-run" : string.Empty)}.");
        }

        if (!view.SameExcept(_lastLogged, SceneViewKeys.Transient))
        {
            ModLog.Info($"Campaign: following {HostTag()} to view {view.Epoch}: {view}.");
        }

        Guard($"going to the {view.Screen} screen", () => next.Go(view, run, result =>
        {
            if (answer())
            {
                Arrive(follower, view, result);
            }
        }), onError: () =>
        {
            if (answer())
            {
                Arrive(follower, view, new FollowResult(false, 0, FollowReasons.ErrorSeeLog));
            }
        });
    }

    /// <summary>
    /// Starts one step of the follower's (a move or a run update), which must be answered exactly once, or the follower
    /// waits for it forever. Returns the answer's gate: true the first time it's called (the caller answers), false after.
    /// If the screen is put away before it answers (<see cref="LeaveScreen"/>), <paramref name="abandon"/> answers instead.
    /// </summary>
    private Func<bool> Pending(Action abandon)
    {
        bool answered = false;
        Action? cancel = null;
        bool Answer()
        {
            if (answered)
            {
                return false;
            }

            answered = true;
            if (ReferenceEquals(_abandonStep, cancel))
            {
                _abandonStep = null;
            }

            return true;
        }

        cancel = () =>
        {
            if (Answer())
            {
                abandon();
            }
        };
        _abandonStep = cancel;
        return Answer;
    }

    private void Arrive(CampaignFollower follower, HostView view, FollowResult result)
    {
        if (result.Ok)
        {
            follower.Arrived(view, result.Applied ? result.Checkpoint : 0);
            if (!view.SameExcept(_lastLogged, SceneViewKeys.Transient) || (result.Applied && result.Checkpoint != 0))
            {
                ModLog.Info($"Campaign: following {HostTag()}: on the {HostText.KeyForLog(view.Screen)} screen (view {view.Epoch}" +
                    (result.Applied && result.Checkpoint != 0 ? $", run {result.Checkpoint})." : ")."));
            }

            _lastLogged = view;
        }
        else
        {
            follower.Missed(result.Reason, _now);
            ModLog.Warn($"Campaign: couldn't follow {HostTag()} to the {HostText.KeyForLog(view.Screen)} screen (view {view.Epoch}): {result.Reason}.");
            if (follower.GaveUp)
            {
                FollowFailed?.Invoke(); // the last try failed: the player is told; an earlier one is tried again
            }
        }
    }

    /// <summary>The current screen is put away; a step it hadn't answered yet counts as failed (the follower tries again).</summary>
    private void LeaveScreen()
    {
        IScreenFollower? screen = _screen;
        Action? abandon = _abandonStep;
        _screen = null;
        _abandonStep = null;
        if (screen != null)
        {
            Guard($"leaving the {screen.Screen} screen", screen.Leave);
        }

        if (abandon != null)
        {
            Guard("answering the dropped screen's step", abandon);
        }
    }

    /// <summary>Ends following: every module (those switched off too) puts back what it changed.</summary>
    private void EndFollowing(string reason, bool gameClosing)
    {
        if (_client == null)
        {
            return;
        }

        LeaveScreen();
        CampaignClient client = _client;
        ModLog.Info($"Campaign: stopped following {HostTag()} ({reason}): {client.ViewsReceived} view(s) and {client.CheckpointsReceived} run checkpoint(s) " +
            $"received, {client.Malformed} malformed; the last run applied here was {client.Follower.AppliedCheckpoint}.");
        // In reverse: the last made stops first, so run sync (first) puts this PC's own run back last, after the others
        // (shared progress applies what waits while the host's run is still held here).
        ForAll("stopping following", m => m.StopFollowing(reason, gameClosing), reverse: true);
        _client = null;
        _stopping = null;
        _hostName = string.Empty;
        FollowedHost = 0;
        Current = _host != null ? this : null;
    }

    /// <summary>A session that ended mid-load: put away once the scene is in, or after <see cref="LandingSeconds"/> regardless.</summary>
    private void PutAwayWhenLanded()
    {
        bool late = Streaming.FrameClock.Seconds - _stoppingSince > LandingSeconds;
        if (!late && SceneLanding())
        {
            return;
        }

        if (late)
        {
            ModLog.Warn($"Campaign: the scene this game was loading for the host didn't land within {LandingSeconds:0} s; putting the host's campaign away anyway.");
        }

        EndFollowing(_stopping!, gameClosing: false);
    }

    /// <summary>
    /// True when a new session (<paramref name="what"/> for logs, a host by tag; <paramref name="shown"/> for the status box, by
    /// name) has to wait: the last session's end waits for a scene to land, and putting it away now would put this PC's own run
    /// back mid-load (the map would land on it). The session then starts from <see cref="Update"/> once that's done
    /// (<see cref="LandingSeconds"/> at most), and what its link brings meanwhile is held for it, in order. A session already
    /// waiting is replaced.
    /// </summary>
    private bool Defer(string what, string shown, bool following, Action start)
    {
        if (_stopping == null || !SceneLanding())
        {
            return false;
        }

        if (_pending != null)
        {
            DropPending($"{what} takes its place");
        }

        _pending = new PendingStart(what, shown, following, start);
        ModLog.Info($"Campaign: {what} starts once the scene this game is loading for {HostTag()} is in (at most {LandingSeconds:0} s after the last session ended); " +
            "until then the last session's guards hold.");
        return true;
    }

    /// <summary>A message for the session waiting to start: kept, in order, and read once it has. Always true (taken).</summary>
    private bool Hold(ulong sender, ReadOnlySpan<byte> message)
    {
        PendingStart pending = _pending!;
        pending.Messages.Add((sender, message.ToArray()));
        pending.Bytes += message.Length;
        if (pending.Bytes > MaxHeldBytes)
        {
            pending.Overflowed = true; // the next update stops waiting (nothing held is dropped)
        }

        return true;
    }

    /// <summary>The session that waited for the last one to be put away starts, and gets what its link brought meanwhile.</summary>
    private void StartPending()
    {
        PendingStart? pending = _pending;
        _pending = null;
        if (pending == null)
        {
            return;
        }

        ModLog.Info($"Campaign: the last session is put away; {pending.What} starts now ({pending.Messages.Count} message(s) held for it).");
        pending.Start();
        foreach (ulong friend in pending.Friends)
        {
            FriendJoined(friend);
        }

        foreach ((ulong sender, byte[] message) in pending.Messages)
        {
            _ = pending.Following ? _client?.Receive(sender, message) : _host?.Receive(sender, message);
        }

        if (pending.LinkJoined)
        {
            LinkJoined();
        }
    }

    /// <summary>The session waiting to start won't (it ended first, or another took its place).</summary>
    private void DropPending(string reason)
    {
        PendingStart? pending = _pending;
        _pending = null;
        if (pending != null)
        {
            ModLog.Info($"Campaign: {pending.What} won't start ({reason}); it ended before the last session's scene was in ({pending.Messages.Count} held message(s) dropped).");
        }
    }

    /// <summary>A line for the host's status box about <paramref name="friend"/> (empty while they simply follow the host).</summary>
    private static string FriendLine(ulong friend, FollowReport report, HostView shown)
    {
        string name = SafeText.Clean(SteamFriends.GetFriendPersonaName(friend), 32); // the status box is a screen: names, not tags
        string where = report.Epoch == shown.Epoch ? $" to the {Describe(shown)}" : string.Empty;
        return report.State switch
        {
            FollowState.WaitingForRun => $"   {name} is getting your run...",
            FollowState.Moving => $"   {name} is on the way{where}...",
            FollowState.Failed => $"   {name} couldn't follow you{where}: {SafeText.Clean(report.Reason, 80)}",
            _ => string.Empty,
        };
    }

    /// <summary>A module's name as a part of the campaign, for messages ("the Scenes part").</summary>
    private static string Part(ICampaignModule module) => $"the {module.Name} part";

    /// <summary>True while the game is loading a scene from the active one (false when that can't be told).</summary>
    private static bool SceneLanding()
    {
        try
        {
            return Campaign.Scenes.WorldMap.Loading;
        }
        catch (Exception ex)
        {
            ModLog.Warn($"Campaign: couldn't tell whether a scene is loading ({ModLog.SafeErrorText(ex)}).");
            return false;
        }
    }

    /// <summary>Host: a checkpoint too big for one message is never sent, so friends can't follow until a smaller save; said once per checkpoint.</summary>
    private void LogOversize()
    {
        if (_host == null || _host.OversizeCheckpoints == _oversizeLogged)
        {
            return;
        }

        _oversizeLogged = _host.OversizeCheckpoints;
        RunCheckpoint? latest = _host.Checkpoints.Latest;
        ModLog.Error(string.Create(CultureInfo.InvariantCulture, $"Campaign: run checkpoint {latest?.Sequence ?? 0} ({(latest?.RawBytes ?? 0) / 1024.0:0.0} KB) is too big to send even compressed ") +
            $"(at most {RunCheckpointMessage.MaxMessageBytes / 1024} KB); friends wait for the run until the host's game saves a smaller one.");
    }

    private bool ReadOther(MessageType type, ref BitReader reader)
    {
        if (!_owners.TryGetValue(type, out ICampaignModule? module) || _off.Contains(module))
        {
            return false;
        }

        try
        {
            return module.Receive(type, ref reader);
        }
        catch (Exception ex)
        {
            SwitchOff(module, $"reading a {type} message", ex);
            return false;
        }
    }

    private bool ReadFromFriend(ulong friend, MessageType type, ref BitReader reader)
    {
        if (!_friendOwners.TryGetValue(type, out ICampaignModule? module) || _off.Contains(module))
        {
            return false;
        }

        try
        {
            return module.ReceiveFromFriend(friend, type, ref reader);
        }
        catch (Exception ex)
        {
            SwitchOff(module, $"reading a {type} message from {Tag(friend)}", ex);
            return false;
        }
    }

    private void LogReports()
    {
        if (_host == null)
        {
            return;
        }

        foreach (ulong friend in _host.Friends)
        {
            bool known = _host.Follows.TryGet(friend, out FollowReport report);
            NoteFailing(friend, known && report.State == FollowState.Failed);
            if (!known || (_reported.TryGetValue(friend, out FollowReport seen) &&
                seen.State == report.State && seen.Checkpoint == report.Checkpoint && seen.Reason == report.Reason))
            {
                continue; // nothing new: a friend following each cursor move isn't worth a line
            }

            _reported[friend] = report;
            if (!SpendReportLine(friend))
            {
                continue;
            }

            string tag = Tag(friend);
            switch (report.State)
            {
                case FollowState.Following:
                    ModLog.Info($"Campaign: {tag} follows view {report.Epoch} (run {report.Checkpoint}).");
                    break;
                case FollowState.Failed:
                    ModLog.Warn($"Campaign: {tag} couldn't follow view {report.Epoch}: {FollowReasons.ForLog(report.Reason)}."); // a friend's text: a fixed reason in clear, a known kind by its first words
                    break;
                case FollowState.WaitingForRun:
                    ModLog.Info($"Campaign: {tag} is waiting for the run checkpoint view {report.Epoch} needs.");
                    break;
            }
        }
    }

    /// <summary>
    /// A friend whose game keeps failing to follow (every report <see cref="FollowState.Failed"/> for <see cref="FailingToldAfterSeconds"/>, longer than its tries take) is
    /// told once to the host's player, until it follows again: a first failure is tried again by the friend's game, and isn't news.
    /// </summary>
    private void NoteFailing(ulong friend, bool failing)
    {
        if (!failing)
        {
            _failingSince.Remove(friend);
            _failingTold.Remove(friend);
            return;
        }

        double now = Streaming.FrameClock.Seconds;
        if (!_failingSince.TryGetValue(friend, out double since))
        {
            _failingSince[friend] = now;
        }
        else if (now - since >= FailingToldAfterSeconds && _failingTold.Add(friend))
        {
            FriendFollowFailed?.Invoke(friend);
        }
    }

    /// <summary>True when <paramref name="friend"/>'s report may be logged (at most <see cref="ReportLinesPerMinute"/> a minute).</summary>
    private bool SpendReportLine(ulong friend)
    {
        double now = Streaming.FrameClock.Seconds;
        (double since, int lines) = _reportBudget.TryGetValue(friend, out var budget) && now - budget.Since < 60 ? budget : (now, 0);
        lines++;
        _reportBudget[friend] = (since, lines);
        if (lines == ReportLinesPerMinute + 1)
        {
            ModLog.Warn($"Campaign: {Tag(friend)} reports where it is unusually often; its reports aren't logged for the rest of this minute.");
        }

        return lines <= ReportLinesPerMinute;
    }

    private static string Describe(HostView view) => view.Screen switch
    {
        Screens.Title => "title screen",
        Screens.Map => "world map",
        Screens.Preview => "match preview",
        Screens.Match => "match",
        Screens.Menu => "menu",
        _ => view.Screen,
    };

    private static IEnumerable<IScreenFollower> Followers(ICampaignModule module) =>
        module is IScreenFollowerSource source ? source.Followers : Array.Empty<IScreenFollower>();

    private void ForEach(string what, Action<ICampaignModule> action)
    {
        foreach (ICampaignModule module in _modules)
        {
            if (_off.Contains(module))
            {
                continue;
            }

            try
            {
                action(module);
            }
            catch (Exception ex)
            {
                SwitchOff(module, what, ex);
            }
        }
    }

    /// <summary>
    /// A session's stop call, for every module, those switched off by an error too: the stop puts back what the module
    /// changed (the friend's UI lock, its own run, the save guard), whatever happened to it since.
    /// </summary>
    private void ForAll(string what, Action<ICampaignModule> action, bool reverse = false)
    {
        for (int i = 0; i < _modules.Count; i++)
        {
            ICampaignModule module = _modules[reverse ? _modules.Count - 1 - i : i];
            try
            {
                action(module);
            }
            catch (Exception ex)
            {
                _off.Add(module);
                ModLog.Error($"Campaign: {module.Name} failed while {what}: {ex}");
            }
        }
    }

    private void SwitchOff(ICampaignModule module, string what, Exception ex)
    {
        _off.Add(module);
        ModLog.Error($"Campaign: {module.Name} failed while {what} and is off for the rest of the session (it still puts things back when the session ends): {ex}");
        ModuleSwitchedOff?.Invoke();
        if (_screen != null && _followerOwners.TryGetValue(_screen, out ICampaignModule? owner) && ReferenceEquals(owner, module))
        {
            LeaveScreen();
        }
    }

    private static void Guard(string what, Action action, Action? onError = null)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            ModLog.Error($"Campaign: failed while {what}: {ex}");
            onError?.Invoke();
        }
    }

    /// <summary>A session waiting for the last one to be put away, and what its link brought meanwhile.</summary>
    private sealed class PendingStart
    {
        public PendingStart(string what, string shown, bool following, Action start)
        {
            What = what;
            Shown = shown;
            Following = following;
            Start = start;
        }

        /// <summary>"hosting", "following P#a1b2c3": for logs, a host by their tag (D127).</summary>
        public string What { get; }

        /// <summary>"hosting", "following Alex": for the status box, a screen, which names people.</summary>
        public string Shown { get; }

        /// <summary>True for a following session, false for hosting.</summary>
        public bool Following { get; }

        public Action Start { get; }

        public List<(ulong Sender, byte[] Message)> Messages { get; } = new();

        public long Bytes { get; set; }

        /// <summary>Hosting: friends who joined meanwhile.</summary>
        public List<ulong> Friends { get; } = new();

        /// <summary>Following: the link to the host (re)started meanwhile.</summary>
        public bool LinkJoined { get; set; }

        /// <summary>More than <see cref="MaxHeldBytes"/> came: stop waiting.</summary>
        public bool Overflowed { get; set; }
    }
}

/// <summary>A module that brings screen followers (most modules bring none).</summary>
internal interface IScreenFollowerSource
{
    /// <summary>The followers this module brings, one per screen name.</summary>
    IEnumerable<IScreenFollower> Followers { get; }
}
#endif
