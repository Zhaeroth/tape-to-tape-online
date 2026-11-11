#if HAS_INTEROP
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Il2CppUltimateReplay;
using T2TOnline.Core.Diagnostics;
using T2TOnline.Core.Protocol;
using T2TOnline.Core.Snapshots;
using T2TOnline.Core.Stream;
using T2TOnline.Plugin.Client;
using T2TOnline.Plugin.Diagnostics;
using T2TOnline.Plugin.Replay;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace T2TOnline.Plugin.Streaming;

/// <summary>Something that turns a stream's timed messages (events, sounds, HUD) into what the friend sees and hears.</summary>
internal interface IPresentationChannel
{
    /// <summary>Message types this channel handles.</summary>
    IReadOnlyCollection<MessageType> Handles { get; }

    /// <summary>
    /// Playback is starting: prepare. <paramref name="objects"/> is the player's live map from network id to local object;
    /// it grows while playing (a live host's object table does), so look ids up when needed rather than copying it.
    /// </summary>
    void Begin(IStreamSource source, IReadOnlyDictionary<ushort, ReplayObject> objects);

    /// <summary>One message is due now.</summary>
    void Deliver(in TimedMessage message);

    /// <summary>
    /// Playback is ending: undo anything still in effect (stop looping sounds, put HUD values back). Also called after a
    /// <see cref="Begin"/> that threw partway (undo what it did), and safe to call twice. <paramref name="matchLoaded"/>
    /// false: the match's scene has gone (a held match's normal end), so let go of what playback took (listeners, hooks)
    /// but write nothing into the game: the values saved at the start belong to a match that no longer exists, and
    /// globals (FMOD parameters) belong to whatever loaded since.
    /// </summary>
    void End(bool matchLoaded);

    /// <summary>Playback has ended and client mode is off: the match is the game's own again.</summary>
    void Finish();
}

/// <summary>
/// A presentation channel that says what its last delivery was, for the cost report's slowest delivery (a channel without
/// it is named by its kind).
/// </summary>
internal interface IDeliveryDetail
{
    /// <summary>
    /// What the last <see cref="IPresentationChannel.Deliver"/> delivered (an event's name), its slowest part (a listener's
    /// class, or null) and that part's time in microseconds. Held strings only: reading it allocates nothing.
    /// </summary>
    (string What, string? Inner, double InnerMicros) LastDelivery { get; }
}

/// <summary>Which local objects a stream's objects are.</summary>
internal enum PlayMatch
{
    /// <summary>Same match if most objects carry the recorded replay ids and keys, else a fresh one.</summary>
    Auto,

    /// <summary>The very match the stream was recorded in: objects by the host's replay ids.</summary>
    Same,

    /// <summary>A freshly loaded copy of the match (a friend's own): objects by stable key, components take the host's ids.</summary>
    Fresh,
}

/// <summary>
/// Plays a host's match stream through client mode, from a recording or live:
/// <list type="bullet">
/// <item>the match's gameplay is switched off (<see cref="ClientMode"/>);</item>
/// <item>snapshots arrive from an <see cref="IStreamSource"/> (a recording through a simulated link, or the host over the
/// network), go into a <see cref="SnapshotTimeline"/>, and a <see cref="PlayoutClock"/> decides what host time to render:
/// as little behind the newest snapshot as the measured jitter allows, warping at most 5% to stay there (D28);</item>
/// <item>every replay object is driven through UltimateReplay's own lifecycle (<see cref="ReplaySession"/>), interpolated
/// between the snapshots around render time; which local object each stream object is, the copies made from the game's
/// templates for objects the host's game made during play, and which are shown is <see cref="StreamObjects"/>'s
/// business;</item>
/// <item>the camera follows the recorded pose (a cut on the host stays a cut, <see cref="CameraPose.IsCut"/>), the time
/// scale follows the stream (held to 0..4), the slow-motion audio follows the host (<see cref="SlowmoAudio"/>), and the
/// presentation channels get each event, sound and HUD change when render time reaches it, a few hundred a frame at most
/// (the rest the next frame).</item>
/// </list>
/// A recording pauses with the game's own pause menu; a live host doesn't (its match goes on). When a recording ends,
/// every object is put back and gameplay switched on again (the F4 test's match carries on). A live host's match is never
/// handed back to this game while it's loaded: when it ends, or can't go on here, its last frame is held in client mode
/// (<see cref="Hold"/>) until its scene unloads, which ends playback without writing anything back. The per-frame work
/// is timed (<see cref="TakeCostReport"/>, and the stop line).
/// </summary>
internal sealed class StreamPlayer
{
    /// <summary><see cref="StopReason"/> when everything a live host sent has been shown and the host ended the match.</summary>
    public const string HostEnded = "the host ended the match";

    /// <summary><see cref="StopReason"/> when this PC's own pause menu left the match.</summary>
    public const string LeftMatch = "you left the match";

    /// <summary>Highest time scale a stream may set (the game's own slow motion and pauses are all below 1).</summary>
    private const float MaxTimeScale = 4f;

    /// <summary>Seconds after the first frame shown when local replay objects the stream never names are listed.</summary>
    private const double UnnamedCheckSeconds = 5;

    /// <summary>Playout clock re-anchors logged in full per playback; further ones are counted (the playout report).</summary>
    private const int LoggedReanchors = 3;

    /// <summary>Most messages delivered per frame; more wait for the next frame, in order.</summary>
    private const int MaxDeliveriesPerFrame = 256;

    /// <summary>Most messages waiting for their time; beyond this (a flood), new ones are dropped and counted.</summary>
    private const int MaxPending = 20_000;

    /// <summary>Most bytes of messages waiting for their time: a real stream keeps a few hundred KB waiting at most, and each message can be 32 KB.</summary>
    private const long MaxPendingBytes = 16L * 1024 * 1024;

    /// <summary>Channel failures logged in full per channel and step; further ones are counted for the stop log.</summary>
    private const int LoggedFailures = 3;

    private readonly List<IPresentationChannel> _channels = new();
    private readonly List<IPresentationChannel> _begun = new();
    private readonly ReplaySession _session = new();
    private readonly StreamObjects _map;

    /// <summary>
    /// The playing stream's <see cref="StreamObjects.Realign"/> for a skater's object, or null while no stream plays: a
    /// follower that changed a skater's talents (Match/MatchPicks) has its components taken over again.
    /// </summary>
    public static Func<GameObject, string?>? Realign { get; private set; }
    private readonly SlowmoAudio _slowmo = new();
    private readonly SnapshotTimeline _timeline = new(1024);
    private readonly PlayoutClock _clock = new();
    private readonly DelayHistogram _delays = new();
    private readonly List<ArrivedSnapshot> _arrived = new();
    private readonly List<TimedMessage> _arrivedMessages = new();
    private readonly Queue<TimedMessage> _pending = new();
    private readonly Dictionary<string, int> _channelFailures = new(StringComparer.Ordinal);
    private readonly CostMeter _frameCost = new("frame");
    private readonly CostMeter _receiveCost = new("receive");
    private readonly CostMeter _loadCost = new("load");
    private readonly CostMeter _blendCost = new("blend");
    private readonly CostMeter _deliverCost = new("deliver");
    private readonly SlowestCall _slowestDelivery = new();
    private readonly CostMeter[] _costs;
    private IStreamSource? _source;
    private Scene _matchScene;
    private double _virtualNow;
    private double _lastFrameTime;
    private uint _fromSequence;
    private uint _toSequence;
    private bool _pauseRequested;
    private bool _localMenuOpen;
    private float _timeScale;
    private float _restoreTimeScale = -1f;
    private float _streamTimeScale = 1f;
    private float _appliedTimeScale = -1f;
    private int _localTimeScaleWrites;
    private int _timeScalesHeld;
    private CameraPose? _pose;
    private Behaviour? _cameraBrain;
    private bool _brainWasEnabled;
    private long _frames;
    private long _behindTotal;
    private long _firstRender;
    private long _lastRender;
    private long _newestHost;
    private long _snapshotsIn;
    private long _messagesIn;
    private long _delivered;
    private long _startsSkipped;
    private int _lateMessages;
    private int _droppedMessages;
    private int _framesAtCap;
    private int _pauses;
    private int _poolAtStart = -1;
    private int _oldBasePending;
    private int _cameraCuts;
    private bool _timeScaleGivenBack;
    private bool _unnamedChecked;
    private double _firstShownAt;
    private long _pendingBytes;
    private double _costsSince;
    private long _heldCallsAt;
    private double _pausedSeconds;
    private double _minRate = 1;
    private double _maxRate = 1;

    public StreamPlayer()
    {
        _map = new StreamObjects(_session);
        _costs = new[] { _frameCost, _receiveCost, _loadCost, _blendCost, _deliverCost };
    }

    /// <summary>True while playing (a held live match included: it's still this player's until its scene unloads).</summary>
    public bool Playing => _source != null;

    /// <summary>
    /// True while a live host's match is held on its last frame (<see cref="Hold"/>): in client mode, taking nothing more
    /// from the stream, until its scene unloads (or <see cref="Stop"/>).
    /// </summary>
    public bool Holding { get; private set; }

    /// <summary>
    /// Why the current hold began (<see cref="HostEnded"/> when the host ended the match), or why the last playback
    /// stopped (<see cref="LeftMatch"/>, "the match was unloaded", ...); empty before any.
    /// </summary>
    public string StopReason { get; private set; } = string.Empty;

    /// <summary>True while the player's own pause menu holds playback (recordings only).</summary>
    public bool Paused { get; private set; }

    /// <summary>The stream being played, or null.</summary>
    public IStreamSource? Source => _source;

    /// <summary>True once the first snapshot has been shown.</summary>
    public bool Showing => _frames > 0;

    /// <summary>Running totals of this playback, for reports over an interval (they start again at each playback).</summary>
    public PlayoutTotals Totals => new(_frames, _clock.Underruns, _clock.Holds, _behindTotal, _clock.TargetDelay, _clock.Jitter);

    /// <summary>Object states this playback (or the last) failed to apply: the stop line's "failed" (they start again at each playback).</summary>
    public long FailedStates => _session.Failures;

    /// <summary>True while the stream shows the host's match stopped (time scale 0: their pause menu, a loading wait).</summary>
    public bool StreamStopped => Playing && _frames > 0 && _streamTimeScale < 0.01f;

    /// <summary>The slow-motion audio (the HUD channel passes the host's flag on to it).</summary>
    public SlowmoAudio Slowmo => _slowmo;

    /// <summary>Adds a presentation channel (call before playing).</summary>
    public void AddChannel(IPresentationChannel channel) => _channels.Add(channel);

    /// <summary>
    /// The player's own pause menu opened or closed (safe to call from inside a game event): a recording pauses with it
    /// from the next frame; a live host's match doesn't, but a stop while the menu is open leaves the time scale to it.
    /// </summary>
    public void RequestPause(bool paused)
    {
        _localMenuOpen = paused;
        if (_source is { Live: false })
        {
            _pauseRequested = paused;
        }
    }

    /// <summary>
    /// Starts playing <paramref name="source"/> onto the loaded match's objects, found as <paramref name="match"/> says.
    /// <paramref name="scene"/> is the game's replay scene when known (its active objects first); every replay object in
    /// the loaded scenes is found either way.
    /// </summary>
    public bool Play(IStreamSource source, ReplayScene? scene, PlayMatch match, out string problem)
    {
        if (Playing)
        {
            problem = "already playing";
            return false;
        }

        if (!source.Live && Time.timeScale < 0.01f)
        {
            // The pause menu owns the time scale; playing under it would leave a frozen match behind when it closes.
            problem = "the game is paused: close the pause menu first";
            return false;
        }

        _map.Prepare(scene);
        _map.SameMatch = match switch
        {
            PlayMatch.Same => true,
            PlayMatch.Fresh => false,
            _ => _map.LooksLikeSameMatch(source),
        };

        // Before the first frame, not inside a live one (an 11-17 ms hitch mid-match on a friend).
        _map.PrepareTemplates();
        if (!source.Live && !_map.Mappable(source, out problem))
        {
            _map.Clear();
            return false;
        }

        if (!_map.SameMatch && source.Objects.Count > 0 && source.Objects.Values.Any(o => o.Components is not { Count: > 0 }))
        {
            ModLog.Warn("Stream player: this recording has no component ids (made before v0.4), so it can only play onto the match it was recorded in; " +
                "objects whose components don't line up will fail.");
        }

        if (!ClientMode.Enter(out problem))
        {
            _map.Clear();
            return false;
        }

        try
        {
            Start(source);
            return true;
        }
        catch (Exception ex)
        {
            problem = $"playback couldn't start ({ModLog.SafeErrorText(ex)})";
            ModLog.Error("Stream player: start failed; undoing", ex);
            Stop("playback couldn't start");
            return false;
        }
    }

    /// <summary>Call once per frame, after the game's own updates and the camera.</summary>
    public void LateUpdate()
    {
        if (_source == null)
        {
            RestoreTimeScaleAfterMenu();
            return;
        }

        try
        {
            if (!_matchScene.isLoaded)
            {
                Stop("the match was unloaded");
                return;
            }

            if (Holding)
            {
                // Held until the scene unloads: nothing more is taken from the stream (a new match's messages are the next
                // playback's), and the hold never turns back into playback, even when a new setup clears the ended flag.
                RestoreTimeScaleAfterMenu();
                return;
            }

            double frameTime = FrameClock.Seconds;
            double elapsed = Math.Max(0, (frameTime - _lastFrameTime) * 1_000_000);
            _lastFrameTime = frameTime;
            if (_pauseRequested != Paused)
            {
                Paused = _pauseRequested;
                _pauses += Paused ? 1 : 0;
                _appliedTimeScale = -1f;
                ModLog.Info(string.Create(CultureInfo.InvariantCulture, $"Stream player: {(Paused ? "paused (the pause menu is open)" : "resumed")} at {RenderSeconds():0.0} s."));
            }

            if (Paused)
            {
                // Both clocks stand still; the pause menu owns the time scale; the camera keeps the last pose.
                _pausedSeconds += elapsed / 1_000_000;
                return;
            }

            long frameStart = CostMeter.Start();
            if (PlayFrame(frameTime, elapsed))
            {
                _frameCost.AddSince(frameStart);
            }
        }
        catch (Exception ex)
        {
            // A live match is held rather than handed back to this game; a recording stops.
            ModLog.Error("Stream player: playback failed", ex);
            Hold("an error during playback");
        }
    }

    /// <summary>
    /// For a live host's match: stops taking anything from the stream and holds the last frame shown, in client mode, until
    /// the match's scene unloads (the next match's load, the quit to the menu, the campaign follower moving on), which
    /// ends playback without writing anything back. Handing the match back while it's loaded would let this game play the
    /// host's match on with the AI meanwhile, its HUD and objects back at their start, crediting stats that aren't the
    /// host's. The time scale and slow-motion audio are given back now: nothing waiting on scaled time (the game's own quit,
    /// a reward menu) stalls on a stopped stream; a period cover still up is taken down (its fade-out won't come). A
    /// recording just stops. Safe to call twice; never throws.
    /// </summary>
    public void Hold(string reason)
    {
        if (_source == null || Holding)
        {
            return;
        }

        if (!_source.Live)
        {
            Stop(reason);
            return;
        }

        Holding = true;
        StopReason = reason;
        IStreamSource source = _source;
        Safely("slow-motion audio", _slowmo.Stop);
        Safely("time scale", () => GiveTimeScaleBack(source));
        Safely("period cover", HudModel.HidePeriodCover);
        ModLog.Info(string.Create(CultureInfo.InvariantCulture,
            $"Stream player: holding the last frame of {source.Description} here ({reason}) until its scene unloads; time scale {Time.timeScale:0.##}") +
            $"{(_restoreTimeScale >= 0f ? " (put back once the pause menu closes)" : string.Empty)}.");
    }

    /// <summary>
    /// Stops playing and switches gameplay on again. While the match's scene is still loaded (a recording ending, the F4
    /// test, the game closing) everything is put back as it was when playback started; once it has gone (a held match's
    /// normal end) nothing is written back into the game, only what playback took is let go (client mode, listeners,
    /// hooks). Safe to call twice, and on a start that failed partway: each step undoes only what was done, and one step's
    /// failure doesn't skip the rest.
    /// </summary>
    public void Stop(string reason)
    {
        if (_source == null)
        {
            return;
        }

        IStreamSource source = _source;
        _source = null;
        Realign = null;
        bool held = Holding;
        Holding = false;
        StopReason = reason;
        bool matchLoaded = MatchLoaded();
        foreach (IPresentationChannel channel in _begun)
        {
            Guarded(channel, "end", c => c.End(matchLoaded));
        }

        RenderHook.BeforeRender -= OnBeforeRender;
        Safely("slow-motion audio", _slowmo.Stop);
        ClientMode.CountLeak("frames whose time scale was changed locally (put back)", _localTimeScaleWrites);
        // Each of these skips what was destroyed with the match, so after it has gone they only let go.
        Safely("visibility", _map.Presence.Restore);
        int moved = 0;
        Safely("replay objects", () => moved = _session.End());
        Safely("objects made for the stream", _map.DestroySpawned);
        Safely("camera", ReleaseCamera);
        if (matchLoaded)
        {
            Safely("time scale", () => GiveTimeScaleBack(source));
        }
        else
        {
            // The pause menu that owned it went with the match: whatever loaded since owns the time scale.
            _restoreTimeScale = -1f;
        }

        Safely("client mode", () => ClientMode.Exit(reason));
        foreach (IPresentationChannel channel in _begun)
        {
            Guarded(channel, "finish", c => c.Finish());
        }

        _begun.Clear();
        double played = _frames == 0 ? 0 : (_lastRender - _firstRender) / 1_000_000.0;
        ModLog.Info($"Stream player: stopped ({reason}{(held ? "; it was held on its last frame" : string.Empty)}{(matchLoaded ? string.Empty : "; the match had gone, so nothing was put back")}). " +
            string.Create(CultureInfo.InvariantCulture, $"Played {played:0.0} s of {source.Description} over {_frames} frames") +
            $"{(_pauses > 0 ? string.Create(CultureInfo.InvariantCulture, $" (paused {_pauses} times, {_pausedSeconds:0.0} s)") : string.Empty)}: {_snapshotsIn} snapshots in, {_session.Applied} object states applied " +
            $"({_startsSkipped} interval starts already held, not loaded again), {_session.Failures} failed; {_delivered} of {_messagesIn} other messages delivered" +
            $"{(_lateMessages > 0 ? $" ({_lateMessages} after their time)" : string.Empty)}{(_framesAtCap > 0 ? $"; {_framesAtCap} frames delivered the most a frame takes ({MaxDeliveriesPerFrame})" : string.Empty)}" +
            $"{(_droppedMessages > 0 ? $"; {_droppedMessages} dropped with too many waiting ({MaxPending}, or {MaxPendingBytes / 1024 / 1024} MB)" : string.Empty)}; " +
            $"{moved} objects moved back directly; {_slowmo.Describe()}; {_cameraCuts} camera cuts shown as cuts" +
            $"{(_timeScalesHeld > 0 ? string.Create(CultureInfo.InvariantCulture, $"; {_timeScalesHeld} snapshot time scales held to 0..{MaxTimeScale}") : string.Empty)}" +
            $"{(_channelFailures.Count > 0 ? $"; channel failures: {string.Join(", ", _channelFailures.Select(p => $"{p.Key} ×{p.Value}"))}" : string.Empty)}.");
        ModLog.Info(PlayoutReport(source));
        string costs = Costs(m => m.DescribeTotal());
        if (costs.Length > 0)
        {
            string slowest = _slowestDelivery.Total.ToString();
            ModLog.Info($"Stream player: cost on this PC's main thread (mean/p95/max per frame or per snapshot interval): {costs}" +
                $"{(slowest.Length > 0 ? $"; slowest delivery {slowest}" : string.Empty)}.");
        }

        ModLog.Info($"Stream player: {ReplayStates.Describe(_poolAtStart)}.");
        _map.Clear();
        _pending.Clear();
        _pendingBytes = 0;
        _timeline.Clear();
    }

    /// <summary>
    /// The per-frame costs since the last call (for the periodic report), with the rate of game calls client mode held, or
    /// empty when not playing.
    /// </summary>
    public string TakeCostReport()
    {
        double now = FrameClock.Seconds;
        double seconds = Math.Max(0.001, now - _costsSince);
        long held = ClientMode.HeldCalls;
        string heldRate = string.Create(CultureInfo.InvariantCulture, $"client mode held {(held - _heldCallsAt) / seconds:0} game calls/s");
        _costsSince = now;
        _heldCallsAt = held;
        string costs = Costs(m => m.DescribeInterval());
        string slowest = _slowestDelivery.TakeInterval().ToString();
        costs += slowest.Length > 0 && costs.Length > 0 ? $"; slowest delivery {slowest}" : string.Empty;
        return _source == null || Holding ? string.Empty : costs.Length > 0 ? $"{costs}; {heldRate}" : heldRate;
    }

    /// <summary>
    /// One frame of playback after the pause check. False when nothing was rendered this frame (no snapshot yet, or the
    /// stream ended and the match is now held or stopped).
    /// </summary>
    private bool PlayFrame(double frameTime, double elapsed)
    {
        IStreamSource source = _source!;
        _virtualNow += elapsed;
        long now = source.Live ? FrameClock.Micros : (long)_virtualNow;
        long step = CostMeter.Start();
        Receive(now);
        if (_map.Considered != source.Objects.Count)
        {
            _map.MapNew(source, _frames > 0);
        }

        _map.Maintain();
        _map.Retake(source);
        _receiveCost.AddSince(step);
        long render = _clock.Advance(now);
        if (source.Finished && (render == long.MinValue || render >= _newestHost))
        {
            // Everything that will ever arrive has been shown (or nothing ever came).
            if (source.Live)
            {
                Hold(HostEnded);
            }
            else
            {
                Stop("finished");
            }

            return false;
        }

        if (render == long.MinValue || !_timeline.TrySample(render, out SnapshotFrame from, out SnapshotFrame to, out float t))
        {
            return false;
        }

        if (from.Sequence != _fromSequence || to.Sequence != _toSequence)
        {
            step = CostMeter.Start();
            _map.SpawnShown(from);

            // The same order as UltimateReplay's playback: settle on snapshot i, then aim at snapshot i+1. When i is the
            // previous interval's target, every object holds it already (unless objects joined since): not loaded again.
            if (from.Sequence != _toSequence || _map.Joined)
            {
                LoadFrame(from);
            }
            else
            {
                _startsSkipped++;
            }

            _map.Joined = false;
            _session.Update(1f);
            LoadFrame(to);
            _fromSequence = from.Sequence;
            _toSequence = to.Sequence;
            _streamTimeScale = SaneTimeScale(from.TimeScale);
            _map.Presence.Show(from, frameTime);
            _slowmo.FromTimeScale(_streamTimeScale);
            _cameraCuts += from.Camera is CameraPose start && to.Camera is CameraPose end && CameraPose.IsCut(start, end, to.HostTimeMicros - from.HostTimeMicros) ? 1 : 0;
            _loadCost.AddSince(step);
        }

        // Every frame: local code (a controller prompt, a menu) may have written its own time scale.
        if (_appliedTimeScale >= 0f && Time.timeScale != _appliedTimeScale)
        {
            _localTimeScaleWrites++;
        }

        Time.timeScale = _streamTimeScale;
        _appliedTimeScale = _streamTimeScale;
        step = CostMeter.Start();
        _session.Update(t);
        if (from.Camera is CameraPose a && to.Camera is CameraPose b)
        {
            // A cut on the host is shown as a cut (the nearer pose), never a sweep through everything in between.
            _pose = CameraPose.IsCut(a, b, to.HostTimeMicros - from.HostTimeMicros) ? (t < 0.5f ? a : b) : CameraPose.Lerp(a, b, t);
            CameraState.Apply(_pose.Value);
        }

        _blendCost.AddSince(step);
        step = CostMeter.Start();
        DeliverMessages(render);
        _deliverCost.AddSince(step);
        if (_source == null || Holding)
        {
            // A channel stopped or held playback (this PC left the match).
            return false;
        }

        _timeline.TrimBefore(render);
        _firstRender = _frames == 0 ? render : _firstRender;
        _firstShownAt = _frames == 0 ? frameTime : _firstShownAt;
        _lastRender = render;
        _frames++;
        _delays.Add(_clock.NewestHostTime - render);
        _behindTotal += _clock.NewestHostTime - render;
        _minRate = Math.Min(_minRate, _clock.Rate);
        _maxRate = Math.Max(_maxRate, _clock.Rate);
        if (!_unnamedChecked && frameTime - _firstShownAt >= UnnamedCheckSeconds)
        {
            // Once, a few seconds in (a live host's table has filled by then): this match's objects the host never named.
            _unnamedChecked = true;
            _map.ReportUnnamed();
        }

        return true;
    }

    /// <summary>True while the match's scene is still loaded (read safely: false if it can't be).</summary>
    private bool MatchLoaded()
    {
        try
        {
            return _matchScene.isLoaded;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// The time scale from before playback, now, or once this PC's own pause menu has closed if it's open (it owns the time
    /// scale meanwhile). Once per playback: a hold gives it back, and the stop after it doesn't again (the game may have
    /// set its own since).
    /// </summary>
    private void GiveTimeScaleBack(IStreamSource source)
    {
        if (_timeScaleGivenBack)
        {
            return;
        }

        _timeScaleGivenBack = true;
        // A live stream sets 0 itself (the host paused, the loading barrier): only this PC's own pause menu owns it.
        bool menuOpen = source.Live ? _localMenuOpen : _pauseRequested || Paused || Time.timeScale < 0.01f;
        if (menuOpen)
        {
            _restoreTimeScale = _timeScale;
        }
        else
        {
            Time.timeScale = _timeScale;
        }
    }

    private string Costs(Func<CostMeter, string> describe) => string.Join(", ", _costs.Select(describe).Where(s => s.Length > 0));

    private void Start(IStreamSource source)
    {
        _source = source;
        Realign = gameObject => _source == null ? null : _map.Realign(gameObject, _source);
        _matchScene = SceneManager.GetActiveScene();
        _pending.Clear();
        _pendingBytes = 0;
        _timeline.Clear();
        _clock.Reset();
        _delays.Clear();
        _begun.Clear();
        _channelFailures.Clear();
        _session.Begin();
        _slowmo.Reset();
        _virtualNow = 0;
        _lastFrameTime = FrameClock.Seconds;
        _fromSequence = _toSequence = 0;
        _pauseRequested = Paused = _localMenuOpen = false;
        _frames = _pauses = _lateMessages = _droppedMessages = _framesAtCap = 0;
        _behindTotal = _startsSkipped = 0;
        _snapshotsIn = _messagesIn = _delivered = 0;
        _firstRender = _lastRender = 0;
        _newestHost = long.MinValue;
        _pausedSeconds = 0;
        _minRate = _maxRate = 1;
        _poolAtStart = ReplayStates.PoolCount();
        // What to put back: never 0 (a live start can come while the stream or a menu holds the game still).
        _timeScale = Time.timeScale < 0.01f ? 1f : Time.timeScale;
        _restoreTimeScale = -1f;
        _streamTimeScale = 1f;
        _appliedTimeScale = -1f;
        _localTimeScaleWrites = _timeScalesHeld = _cameraCuts = _oldBasePending = 0;
        _timeScaleGivenBack = _unnamedChecked = false;
        Holding = false;
        StopReason = string.Empty;
        _firstShownAt = 0;
        foreach (CostMeter meter in _costs)
        {
            meter.Clear();
        }

        _slowestDelivery.Clear();
        _costsSince = FrameClock.Seconds;
        _heldCallsAt = ClientMode.HeldCalls;
        _pose = null;
        _map.MapNew(source, showing: false);
        HoldCamera();
        RenderHook.BeforeRender -= OnBeforeRender;
        RenderHook.BeforeRender += OnBeforeRender;
        foreach (IPresentationChannel channel in _channels)
        {
            if (Guarded(channel, "start", c => c.Begin(source, _map.Objects)))
            {
                _begun.Add(channel);
            }
            else
            {
                // A start that threw partway may have changed things already (the event channel detaches listeners as it goes).
                Guarded(channel, "undo its failed start", c => c.End(matchLoaded: true));
            }
        }

        ModLog.Info($"Stream player: playing {source.Description} onto {_map.Objects.Count} of {source.Objects.Count} objects so far ({_map.Unmapped} not found here), " +
            $"{_session.Components} replay components{(_session.Renamed > 0 ? $" ({_session.Renamed} took the host's ids)" : string.Empty)}, " +
            $"mapped by {(_map.SameMatch ? "replay id (same match)" : "stable key (fresh match)")}; camera driven by the stream " +
            $"({(_cameraBrain != null ? "Cinemachine off" : "no Cinemachine brain found")}, {(RenderHook.Installed ? "imposed just before rendering" : "imposed in LateUpdate")}).");
    }

    /// <summary>
    /// Snapshots and messages that arrived: snapshots into the timeline and the playout clock, messages into the queue
    /// (up to <see cref="MaxPending"/> waiting).
    /// </summary>
    private void Receive(long now)
    {
        _arrived.Clear();
        _arrivedMessages.Clear();
        _source!.Poll(now, _arrived, _arrivedMessages);
        foreach (ArrivedSnapshot arrived in _arrived)
        {
            _snapshotsIn++;
            // Stamped with when it arrived, not with this frame: a hitch here isn't network jitter. A snapshot the clock
            // rejects as stamped far ahead stays out of the timeline too.
            int reanchors = _clock.Reanchors;
            bool taken = _clock.OnSnapshot(arrived.Frame.HostTimeMicros, arrived.ArrivalMicros);
            if (_clock.Reanchors != reanchors)
            {
                Reanchored(arrived.Frame);
            }

            if (taken && _timeline.Add(arrived.Frame))
            {
                _newestHost = Math.Max(_newestHost, arrived.Frame.HostTimeMicros);
            }
        }

        foreach (TimedMessage message in _arrivedMessages)
        {
            _messagesIn++;
            if (_pending.Count >= MaxPending || _pendingBytes + message.Payload.Length > MaxPendingBytes)
            {
                if (_droppedMessages++ == 0)
                {
                    ModLog.Warn($"Stream player: {_pending.Count} messages ({_pendingBytes / 1024} KB) are waiting for their time; further ones are dropped until they're delivered (counted).");
                }

                continue;
            }

            _pending.Enqueue(message);
            _pendingBytes += message.Payload.Length;
        }
    }

    /// <summary>
    /// The playout clock started over from <paramref name="newest"/> (a second of nothing but snapshots stamped far from the
    /// rest: the host's time moved for good). The frames held are on the old time base: dropped, so render time
    /// never samples between the two. The messages waiting are stamped on it too: due at once, in order, rather than
    /// waiting for a render time that may never come. Logged for the first few.
    /// </summary>
    private void Reanchored(SnapshotFrame newest)
    {
        int dropped = _timeline.Count;
        _timeline.Clear();
        _newestHost = long.MinValue;
        _fromSequence = _toSequence = 0;
        _oldBasePending = _pending.Count;
        if (_clock.Reanchors <= LoggedReanchors)
        {
            ModLog.Warn(string.Create(CultureInfo.InvariantCulture,
                $"Stream player: the host's snapshot times moved for good (now {newest.HostTimeMicros / 1_000_000.0:0.0} s); the playout clock started over from them, ") +
                $"{dropped} snapshots on the old times dropped and {_pending.Count} waiting messages delivered at once" +
                $"{(_clock.Reanchors == LoggedReanchors ? " (further ones are only counted)" : string.Empty)}.");
        }
    }

    /// <summary>The stream's time scale, held to 0..<see cref="MaxTimeScale"/> (logged the first time it isn't).</summary>
    private float SaneTimeScale(float scale)
    {
        float sane = Math.Clamp(scale, 0f, MaxTimeScale);
        if (sane != scale && _timeScalesHeld++ == 0)
        {
            ModLog.Warn(string.Create(CultureInfo.InvariantCulture, $"Stream player: the stream asked for a time scale of {scale}; held to {sane} (further ones are counted)."));
        }

        return sane;
    }

    private void LoadFrame(SnapshotFrame frame)
    {
        IReadOnlyDictionary<ushort, ReplayObject> objects = _map.Objects;
        foreach (KeyValuePair<ushort, byte[]> pair in frame.Objects)
        {
            if (objects.TryGetValue(pair.Key, out ReplayObject? replayObject))
            {
                _session.Load(replayObject, pair.Value);
            }
        }
    }

    /// <summary>
    /// Messages whose time render time has reached, in the host's order (a late one holds the rest), at most
    /// <see cref="MaxDeliveriesPerFrame"/> this frame.
    /// </summary>
    private void DeliverMessages(long renderTime)
    {
        int delivered = 0;
        while (_source != null && _pending.Count > 0 && (_oldBasePending > 0 || _pending.Peek().TimeMicros <= renderTime))
        {
            if (delivered++ == MaxDeliveriesPerFrame)
            {
                _framesAtCap++;
                return;
            }

            TimedMessage message = _pending.Dequeue();
            _pendingBytes -= message.Payload.Length;
            _oldBasePending = Math.Max(0, _oldBasePending - 1);
            // Shown 50 ms or more after its time (it was held up on the link).
            _lateMessages += renderTime - message.TimeMicros > 50_000 ? 1 : 0;
            _delivered++;
            // By index: a channel may stop playback (leaving the match), which empties the list.
            for (int i = 0; i < _begun.Count; i++)
            {
                IPresentationChannel channel = _begun[i];
                if (!channel.Handles.Contains(message.Type))
                {
                    continue;
                }

                // Each delivery timed on its own, and the .NET collections around it counted (no allocation): the cost
                // report names the slowest, and whether a garbage collection landed in it.
                long start = CostMeter.Start();
                int gen0 = GC.CollectionCount(0), gen1 = GC.CollectionCount(1), gen2 = GC.CollectionCount(2);
                try
                {
                    channel.Deliver(message);
                }
                catch (Exception ex)
                {
                    Failed(channel, "deliver", ex);
                }

                double micros = CostMeter.MicrosSince(start);
                if (_slowestDelivery.IsSlowest(micros))
                {
                    NoteSlowDelivery(channel, micros, gen0, gen1, gen2);
                }
            }
        }
    }

    /// <summary>
    /// A delivery slower than any this interval (or playback): what it was (the channel's own words, or its kind) and the
    /// highest .NET collection generation that ran during it (from the counts before it).
    /// </summary>
    private void NoteSlowDelivery(IPresentationChannel channel, double micros, int gen0, int gen1, int gen2)
    {
        int collection = GC.CollectionCount(2) != gen2 ? 2 : GC.CollectionCount(1) != gen1 ? 1 : GC.CollectionCount(0) != gen0 ? 0 : -1;
        (string what, string? inner, double innerMicros) = channel is IDeliveryDetail detail ? detail.LastDelivery : (KindOf(channel), null, 0);
        _slowestDelivery.Note(new SlowCall(micros, what, inner, innerMicros, collection));
    }

    /// <summary>A channel without its own words, by its kind.</summary>
    private static string KindOf(IPresentationChannel channel) => channel switch
    {
        HudChannel => "HUD values",
        SoundChannel => "a sound",
        SoundLoopChannel => "a sound loop",
        RumbleChannel => "rumble",
        _ => "a message",
    };

    /// <summary>After a stop under the pause menu: once the menu has closed (time runs again), the time scale from before playback.</summary>
    private void RestoreTimeScaleAfterMenu()
    {
        if (_restoreTimeScale < 0f || Time.timeScale < 0.01f)
        {
            return;
        }

        if (Time.timeScale != _restoreTimeScale)
        {
            ModLog.Info(string.Create(CultureInfo.InvariantCulture, $"Stream player: the pause menu closed; time scale {Time.timeScale:0.##} → {_restoreTimeScale:0.##} (as before playback)."));
            Time.timeScale = _restoreTimeScale;
        }

        _restoreTimeScale = -1f;
    }

    /// <summary>The playout numbers that matter for latency and smoothness.</summary>
    private string PlayoutReport(IStreamSource source)
    {
        if (_delays.Count == 0)
        {
            return "Stream player: playout: no frames rendered.";
        }

        static double Ms(double micros) => micros / 1000.0;
        return string.Create(CultureInfo.InvariantCulture,
            $"Stream player: playout of {source.Description}: rendered {Ms(_delays.Mean):0.0} ms behind the newest snapshot on average " +
            $"(p95 {Ms(_delays.Percentile(0.95)):0.0} ms; aimed for {Ms(_clock.TargetDelay):0.0} ms = snapshot spacing {Ms(_clock.Spacing):0.0} + " +
            $"jitter {Ms(_clock.Jitter):0.0} + margin {Ms(PlayoutClock.Margin):0.0} + learned cushion {Ms(_clock.Cushion):0.0}); " +
            $"{_clock.Underruns} frames held with nothing newer ({100.0 * _clock.Underruns / Math.Max(1, _frames):0.00}%, in {_clock.Holds} holds); {_clock.Snaps} jumps; " +
            $"clock warped {(_minRate - 1) * 100:+0.0;-0.0;0}% to {(_maxRate - 1) * 100:+0.0;-0.0;0}%" +
            $"{(source.Lost > 0 ? $"; {source.Lost} snapshots lost on the link" : string.Empty)}" +
            $"{(_clock.Outliers > 0 ? $"; {_clock.Outliers} snapshots ignored as stamped far from the rest" : string.Empty)}" +
            $"{(_clock.Reanchors > 0 ? $"; the clock started over {_clock.Reanchors} times (the host's times moved)" : string.Empty)}" +
            $"{(_map.Unmapped + _map.Undrivable > 0 ? $"; {_map.Unmapped} objects not found here, {_map.Undrivable} not drivable" : string.Empty)}" +
            $"{(_session.PartlyDriven > 0 ? $"; {_session.PartlyDriven} driven without some of their replay components (their lists differed from the host's)" : string.Empty)}" +
            $"{_map.RetakeSummary}.");
    }

    private double RenderSeconds() => _frames == 0 ? 0 : (_lastRender - _firstRender) / 1_000_000.0;

    /// <summary>Imposes the pose again right before rendering, after anything else that moved the camera this frame.</summary>
    private void OnBeforeRender()
    {
        if (_source != null && _pose is CameraPose pose)
        {
            CameraState.Apply(pose);
        }
    }

    /// <summary>The camera follows the stream: Cinemachine's brain would fight it every frame, so it's paused.</summary>
    private void HoldCamera()
    {
        _cameraBrain = null;
        Camera? camera = CameraState.GameCamera();
        if (camera == null)
        {
            return;
        }

        Component? brain = camera.GetComponent(Il2CppInterop.Runtime.Il2CppType.Of<Il2CppCinemachine.CinemachineBrain>());
        _cameraBrain = brain?.TryCast<Behaviour>();
        if (_cameraBrain != null)
        {
            _brainWasEnabled = _cameraBrain.enabled;
            _cameraBrain.enabled = false;
        }
    }

    private void ReleaseCamera()
    {
        if (_cameraBrain != null)
        {
            _cameraBrain.enabled = _brainWasEnabled;
            _cameraBrain = null;
        }
    }

    private bool Guarded(IPresentationChannel channel, string what, Action<IPresentationChannel> action)
    {
        try
        {
            action(channel);
            return true;
        }
        catch (Exception ex)
        {
            Failed(channel, what, ex);
            return false;
        }
    }

    /// <summary>A channel step threw: logged in full the first few times per channel and step, then counted for the stop log.</summary>
    private void Failed(IPresentationChannel channel, string what, Exception ex)
    {
        string key = $"{channel.GetType().Name} {what}";
        int count = _channelFailures.GetValueOrDefault(key);
        _channelFailures[key] = count + 1;
        if (count < LoggedFailures)
        {
            ModLog.Error($"Stream player: the {channel.GetType().Name} channel failed to {what}{(count == LoggedFailures - 1 ? " (further failures of this step are counted)" : string.Empty)}", ex);
        }
    }

    private static void Safely(string what, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            ModLog.Error($"Stream player: putting the {what} back failed", ex);
        }
    }
}

/// <summary>
/// A playback's running totals (<see cref="StreamPlayer.Totals"/>): frames rendered, frames held with nothing newer,
/// runs of them, the summed render delay behind the newest snapshot (microseconds), and the playout's current target
/// delay and jitter.
/// </summary>
internal readonly record struct PlayoutTotals(long Frames, long HeldFrames, long Holds, long BehindMicros, long TargetDelay, long Jitter);
#endif
