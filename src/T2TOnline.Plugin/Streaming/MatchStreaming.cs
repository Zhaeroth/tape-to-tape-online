#if HAS_INTEROP
using System;
using System.Collections.Generic;
using System.Linq;
using Il2CppUltimateReplay;
using T2TOnline.Core.Diagnostics;
using T2TOnline.Core.Serialization;
using T2TOnline.Core.Stream;
using T2TOnline.Plugin.Diagnostics;

namespace T2TOnline.Plugin.Streaming;

/// <summary>
/// Everything that turns a match into a stream and back:
/// <list type="bullet">
/// <item>recording (host): snapshots, camera and time scale (<see cref="StreamRecorder"/>), game events
/// (<see cref="EventRecorder"/>), HUD values and FMOD global parameters (<see cref="HudRecorder"/>), and the sounds
/// gameplay plays directly (<see cref="SoundRecorder"/>);</item>
/// <item>playback (a friend's PC, or the same PC for the offline test): <see cref="StreamPlayer"/> in client mode with the
/// HUD, event and sound channels.</item>
/// </list>
/// HUD values recorded at the same instant as an event go first, and the player delivers them first, so a widget that
/// reads the model while handling the event sees the new value. The game's vibration of a friend's skater goes to their
/// pad (<see cref="RumbleRecorder"/>, <see cref="RumbleChannel"/>), and its looping gameplay sounds play on every friend's
/// PC (<see cref="LoopRecorder"/>, <see cref="SoundLoopChannel"/>).
/// </summary>
internal sealed class MatchStreaming : IDisposable
{
    private readonly StreamRecorder _recorder = new();
    private readonly EventRecorder _events;
    private readonly HudRecorder _hud;
    private readonly SoundRecorder _sounds;
    private readonly RumbleRecorder _rumble;
    private readonly LoopRecorder _loops;
    private readonly SoundLoopChannel _loopChannel = new();
    private readonly StreamPlayer _player = new();
    private readonly HudChannel _hudChannel;

    /// <summary>Sets up both sides. <paramref name="harmony"/> takes the direct-sound and rumble patches.</summary>
    public MatchStreaming(HarmonyLib.Harmony harmony)
    {
        _events = new EventRecorder(_recorder);
        _hud = new HudRecorder(_recorder);
        _sounds = new SoundRecorder(_recorder);
        _rumble = new RumbleRecorder(_recorder);
        _loops = new LoopRecorder(_recorder);
        _recorder.SnapshotWritten += _hud.OnSnapshot;
        _recorder.SnapshotWritten += _loops.OnSnapshot;
        _events.BeforeEvent += _hud.BeforeEvent;
        var events = new EventChannel(() => _player.Holding);
        events.MenuAction += OnMenuAction;
        _hudChannel = new HudChannel(_player.Slowmo);
        _player.AddChannel(_hudChannel);
        _player.AddChannel(events);
        _player.AddChannel(new SoundChannel());
        _player.AddChannel(new RumbleChannel());
        _player.AddChannel(_loopChannel);
        RenderHook.Install();
        SoundRecorder.Install(harmony);
        RumbleRecorder.Install(harmony);
        ReplayFlashProbe.Install(harmony);
    }

    /// <summary>True while recording.</summary>
    public bool Recording => _recorder.Recording;

    /// <summary>True while playing a stream.</summary>
    public bool Playing => _player.Playing;

    /// <summary>The file being (or last) recorded.</summary>
    public string? FilePath => _recorder.FilePath;

    /// <summary>True while friends get the stream over the network.</summary>
    public bool Streaming => _recorder.Streaming;

    /// <summary>The player (a friend's PC, or the offline test).</summary>
    public StreamPlayer Player => _player;

    /// <summary>
    /// Raised (from <see cref="LateUpdate"/>, the mod's own frame step) when recording stopped by itself, with why: an error
    /// while recording, or its file failing with no friends to stream to. Every part has stopped by then; an online host
    /// ends the match for its friends here.
    /// </summary>
    public event Action<string>? RecordingStopped;

    /// <summary>
    /// Starts recording the match the game is recording from <paramref name="sceneSource"/>: to a file in
    /// <paramref name="directory"/> (unless null) and to <paramref name="network"/>'s friends (unless null), starting with
    /// the match <paramref name="setup"/> when there is one.
    /// </summary>
    public bool StartRecording(Func<ReplayScene?> sceneSource, string? directory, MatchStreamHeader header, MatchStreamHost? network,
        IReadOnlyList<KeyValuePair<string, NetValue>>? setup, out string problem)
    {
        if (Playing)
        {
            problem = "a stream is playing";
            return false;
        }

        if (!_recorder.Start(sceneSource, directory, header, network, setup, out problem))
        {
            return false;
        }

        _hud.Start();
        _events.Start();
        _sounds.Start();
        _rumble.Start(network);
        _loops.Start();
        return true;
    }

    /// <summary>Stops recording and logs each part's summary, and what recording cost.</summary>
    public void StopRecording(string reason)
    {
        if (!Recording)
        {
            return;
        }

        Guarded("event recorder", () => _events.Stop());
        Guarded("sound recorder", _sounds.Stop);
        Guarded("rumble recorder", _rumble.Stop);
        Guarded("loop recorder", _loops.Stop);
        Guarded("HUD recorder", _hud.Stop);
        _recorder.Stop(reason);
        string costs = Costs(m => m.DescribeTotal());
        if (costs.Length > 0)
        {
            ModLog.Info($"Stream recorder: cost on this PC's main thread (mean/p95/max per snapshot, sample or event): {costs}.");
        }
    }

    /// <summary>
    /// What recording cost on this PC's main thread since the last call (mean/p95/max per snapshot, sample or event), for
    /// a periodic report; empty when nothing ran.
    /// </summary>
    public string TakeRecordingCosts() => Costs(m => m.DescribeInterval());

    /// <summary>Plays <paramref name="source"/> onto <paramref name="scene"/>'s objects in client mode.</summary>
    public bool Play(IStreamSource source, ReplayScene? scene, PlayMatch match, out string problem)
    {
        if (Recording)
        {
            problem = "recording; stop it first";
            return false;
        }

        return _player.Play(source, scene, match, out problem);
    }

    /// <summary>The live stream starts again for the match playing here (a rejoin): what it says now is a jump, not a change.</summary>
    public void StreamRestarted() => _hudChannel.StreamRestarted();

    /// <summary>Stops playing and puts the match back while it's still loaded (<see cref="StreamPlayer.Stop"/>).</summary>
    public void StopPlaying(string reason) => _player.Stop(reason);

    /// <summary>Call once per frame after the game's updates.</summary>
    public void LateUpdate()
    {
        _recorder.LateUpdate();
        if (_recorder.Recording && _recorder.Failure is string failure)
        {
            StopRecording(failure);
            RecordingStopped?.Invoke(failure);
        }

        _player.LateUpdate();
        MatchPresentation.Tick();
        ChainMirror.Tick();
        _loopChannel.AfterFrame(_player);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        StopPlaying("closing");
        StopRecording("closing");
    }

    /// <summary>
    /// The player's own pause menu during playback: pausing holds playback, resuming lets it go on, and leaving the match
    /// stops it at once, before the match is torn down (called from inside the game's event).
    /// </summary>
    private void OnMenuAction(string action)
    {
        switch (action)
        {
            case "pause":
                _player.RequestPause(true);
                break;
            case "resume":
                _player.RequestPause(false);
                break;
            case "leave" when _player.Holding:
                // A held match is left with its scene (the relay's own quit to the menu comes through here): the hold ends
                // when it unloads, so nothing is handed back meanwhile, and the quit needs none of the detached logic.
                break;
            case "leave":
                Guarded("stream player", () => _player.Stop(StreamPlayer.LeftMatch));
                break;
        }
    }

    private string Costs(Func<CostMeter, string> describe) =>
        string.Join(", ", _recorder.Costs.Concat(_hud.Costs).Append(_events.Cost).Append(_loops.Cost).Select(describe).Where(s => s.Length > 0));

    private static void Guarded(string what, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            ModLog.Error($"Match streaming: the {what} failed to stop", ex);
        }
    }
}
#endif
