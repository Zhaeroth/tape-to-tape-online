#if HAS_INTEROP
using System;
using System.Collections.Generic;
using System.Globalization;
using Il2CppFMOD.Studio;
using Il2CppFMODUnity;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppRogue.Powerups.Helpers;
using Il2CppUltimateReplay;
using T2TOnline.Core.Diagnostics;
using T2TOnline.Core.Protocol;
using T2TOnline.Core.Serialization;
using T2TOnline.Core.Stream;
using T2TOnline.Plugin.Diagnostics;
using UnityEngine;

namespace T2TOnline.Plugin.Streaming;

/// <summary>
/// Host side: the game's looping gameplay sounds, which gameplay code a friend's client mode holds
/// starts, moves and stops (magnets, the gravity well, the retro rocket, the exploding puck, the fus-ro-dah charge:
/// <see cref="LoopKind"/>). Their starters are partly inlined into their owners and their stops and moves are inline, so
/// nothing is patched: after each snapshot the recorder asks FMOD for the instances of those eight events (first only how
/// many, which makes no list; the list only when there are or were some), with each one's playback state and position,
/// and <see cref="LoopTracker"/> turns that into the stream's starts, moves and stops (<see cref="MessageType.SoundLoop"/>).
/// Every snapshot while a loop is known, every second one (30 a second) while none is. With none playing a poll makes nothing
/// on the mod's side; the interop boxes each call's result on the game's heap (verified in the game's code:
/// <c>il2cpp_runtime_invoke</c>: eight small boxes a poll, about 8 KB a second on a host). The work is timed
/// (<see cref="Cost"/>).
/// </summary>
internal sealed class LoopRecorder
{
    private const int MaxFailures = 10;

    private static bool _watchLogged;

    private readonly StreamRecorder _recorder;
    private readonly LoopTracker _tracker = new();
    private readonly List<SoundLoopMessage> _changes = new();
    private readonly EventDescription[] _descriptions = new EventDescription[LoopKinds.Count];
    private readonly bool[] _found = new bool[LoopKinds.Count];
    private readonly CostMeter _cost = new("loops");
    private bool _watching;
    private int _snapshots;
    private int _failures;

    public LoopRecorder(StreamRecorder recorder) => _recorder = recorder;

    /// <summary>What the polls cost on the host's main thread.</summary>
    public CostMeter Cost => _cost;

    /// <summary>Finds the eight events in this PC's own SoundManager and starts watching them.</summary>
    public void Start()
    {
        _tracker.Reset();
        _cost.Clear();
        _snapshots = _failures = 0;
        _watching = false;
        Array.Clear(_found);
        var missing = new List<string>();
        Il2Cpp.SoundManager? sound = Il2Cpp.SoundManager.Instance;
        if (sound == null)
        {
            ModLog.Warn("Loop recorder: there's no SoundManager in this match, so its looping gameplay sounds (magnets, the retro rocket, ...) won't reach friends.");
            return;
        }

        for (int kind = 0; kind < LoopKinds.Count; kind++)
        {
            try
            {
                EventReference reference = EventOf(sound, (LoopKind)kind);
                if (!reference.IsNull)
                {
                    EventDescription description = RuntimeManager.GetEventDescription(reference);
                    _descriptions[kind] = description;
                    _found[kind] = description.isValid();
                }
            }
            catch (Exception ex)
            {
                missing.Add($"{LoopKinds.Name((LoopKind)kind)} ({ex.GetType().Name})");
                continue;
            }

            if (!_found[kind])
            {
                missing.Add(LoopKinds.Name((LoopKind)kind));
            }
        }

        _watching = missing.Count < LoopKinds.Count;
        if (missing.Count > 0)
        {
            ModLog.Warn($"Loop recorder: {missing.Count} of the game's {LoopKinds.Count} looping gameplay sounds aren't in FMOD here ({string.Join(", ", missing)}); " +
                "friends won't hear those.");
        }
        else if (!_watchLogged)
        {
            _watchLogged = true;
            ModLog.Info($"Loop recorder: watching the game's {LoopKinds.Count} looping gameplay sounds ({string.Join(", ", AllNames())}); friends start the same ones " +
                "with the game's own SoundManager starters, move and stop them with the host's.");
        }
    }

    /// <summary>Called after each snapshot: polls when one is due. Never throws (it runs inside the recorder's frame).</summary>
    public void OnSnapshot(long now)
    {
        if (!_watching || !_recorder.Recording || (++_snapshots % 2 != 0 && !_tracker.Any))
        {
            return;
        }

        long start = CostMeter.Start();
        try
        {
            Poll(now);
        }
        catch (Exception ex)
        {
            if (++_failures <= 3)
            {
                ModLog.Warn($"Loop recorder: a poll failed ({ModLog.SafeErrorText(ex)}).");
            }

            if (_failures >= MaxFailures)
            {
                _watching = false;
                ModLog.Warn($"Loop recorder: {MaxFailures} polls failed; looping sounds aren't recorded for the rest of this match.");
            }
        }

        _cost.AddSince(start);
    }

    /// <summary>Logs what this recording's loops were.</summary>
    public void Stop() =>
        ModLog.Info($"Loop recorder: {_tracker.Started} gameplay loop(s) this match ({_tracker.DescribeStarted()})" +
            (_tracker.Started == 0 ? string.Empty : $", {_tracker.Stopped} stopped by the game, {_tracker.Playing} playing at the end; {_tracker.Moves} moves, {_tracker.Refreshes} starts again") +
            $"{(_tracker.Refused > 0 ? $"; {_tracker.Refused} refused (over {LoopTracker.MaxLoops} at once)" : string.Empty)}" +
            $"{(_failures > 0 ? $"; {_failures} polls failed" : string.Empty)}.");

    /// <summary>The SoundManager event a kind plays, as the game's own starters pick it (StartMagnetSound by MagnetType).</summary>
    private static EventReference EventOf(Il2Cpp.SoundManager sound, LoopKind kind) => kind switch
    {
        LoopKind.Magnet => sound.magnetActivationEvent,
        LoopKind.GravityWell => sound.gravityWellEvent,
        LoopKind.Magneto => sound.magnetAbilityEvent,
        LoopKind.MagnetRebound => sound.magnetReboundEvent,
        LoopKind.MagnetSafeBounce => sound.magnetSafeBounceEvent,
        LoopKind.RetroRocket => sound.retroRocketEvent,
        LoopKind.ExplodingPuck => sound.explodingPuckEvent,
        _ => sound.fusrodahChargingEvent,
    };

    private static IEnumerable<string> AllNames()
    {
        for (int kind = 0; kind < LoopKinds.Count; kind++)
        {
            yield return LoopKinds.Name((LoopKind)kind);
        }
    }

    private void Poll(long now)
    {
        _tracker.BeginPoll(now);
        for (int kind = 0; kind < LoopKinds.Count; kind++)
        {
            // A copy on the stack: the interop gives the game this struct's address, and the array is free to move meanwhile.
            EventDescription description = _descriptions[kind];
            if (!_found[kind] || description.getInstanceCount(out int count) != Il2CppFMOD.RESULT.OK || (count == 0 && _tracker.Known((LoopKind)kind) == 0))
            {
                continue;
            }

            if (count == 0)
            {
                _tracker.Polled((LoopKind)kind);
                continue;
            }

            if (description.getInstanceList(out Il2CppStructArray<EventInstance> list) != Il2CppFMOD.RESULT.OK || list == null)
            {
                continue; // not looked at: what the tracker knows of this kind stays as it was (never "gone")
            }

            _tracker.Polled((LoopKind)kind);
            for (int i = 0; i < list.Length; i++)
            {
                EventInstance instance = list[i];
                bool playing = instance.getPlaybackState(out PLAYBACK_STATE state) == Il2CppFMOD.RESULT.OK
                    && state is PLAYBACK_STATE.PLAYING or PLAYBACK_STATE.SUSTAINING or PLAYBACK_STATE.STARTING;
                Il2CppFMOD.VECTOR position = instance.get3DAttributes(out Il2CppFMOD.ATTRIBUTES_3D attributes) == Il2CppFMOD.RESULT.OK ? attributes.position : default;
                _tracker.Seen((ulong)instance.handle, (LoopKind)kind, playing, position.x, position.y, position.z);
            }
        }

        _changes.Clear();
        _tracker.EndPoll(_changes);
        foreach (SoundLoopMessage change in _changes)
        {
            // Starts and stops reliably (a lost stop would leave a loop playing); moves unreliably (the next supersedes one).
            _recorder.Write(MessageType.SoundLoop, change.Op == SoundLoopOp.Move ? Channel.Realtime : Channel.Events, change.Write);
        }
    }
}

/// <summary>
/// Friend side: the host's looping gameplay sounds (<see cref="MessageType.SoundLoop"/>), each started with the game's own
/// starter on this PC's SoundManager (<c>StartMagnetSound(position, MagnetType)</c> for the magnets and the gravity well,
/// <c>StartRetroRocketSound</c>, <c>StartExplodingPuckSound</c>, <c>StartDragonShoutChargeSound</c>), moved as their owners
/// move them (<c>set3DAttributes(RuntimeUtils.To3DAttributes(position))</c>) and stopped as they stop them
/// (<c>stop(ALLOWFADEOUT)</c>, <c>release</c>), each when render time reaches it. Never a stuck loop: every one stops at
/// playback's end and when playback holds (the match ended, the session ended), and one with no word from the host for
/// <see cref="LoopPlayback.SilenceMicros"/> of the stream's own running time stops (its stop was lost); while the stream
/// stalls (nothing new shown for half a second: a drop) every loop is paused, and goes on with the stream, or stops if the
/// stall lasts 5 s (the host's next start again brings back those still playing there).
/// </summary>
internal sealed class SoundLoopChannel : IPresentationChannel
{
    private const double StallSeconds = 0.5;
    private const double LongStallSeconds = 5;
    private const int LoggedStalls = 3;

    private static readonly MessageType[] HandledTypes = { MessageType.SoundLoop };

    private readonly LoopPlayback _loops = new();
    private readonly Dictionary<uint, EventInstance> _instances = new();
    private readonly List<uint> _handles = new();
    private long _runningMicros;
    private long _lastFrames;
    private long _lastHeld;
    private double _lastFrameAt;
    private double _lastProgressAt;
    private bool _paused;
    private bool _begun;
    private bool _firstLogged;
    private int _stalls;
    private int _stoppedAtHold;
    private int _stoppedInStall;
    private int _startFailures;
    private int _malformed;
    private int _failures;

    /// <inheritdoc />
    public IReadOnlyCollection<MessageType> Handles => HandledTypes;

    /// <inheritdoc />
    public void Begin(IStreamSource source, IReadOnlyDictionary<ushort, ReplayObject> objects)
    {
        StopAll();
        _loops.Reset();
        _runningMicros = _lastFrames = _lastHeld = 0;
        _lastFrameAt = _lastProgressAt = FrameClock.Seconds;
        _paused = _firstLogged = false;
        _stalls = _stoppedAtHold = _stoppedInStall = _startFailures = _malformed = _failures = 0;
        _begun = true;
    }

    /// <inheritdoc />
    public void Deliver(in TimedMessage message)
    {
        var reader = new BitReader(message.Payload);
        if (!SoundLoopMessage.TryRead(ref reader, out SoundLoopMessage loop))
        {
            _malformed++;
            return;
        }

        LoopStep step = LoopStep.None;
        try
        {
            step = _loops.Apply(loop, _runningMicros);
            switch (step)
            {
                case LoopStep.Start:
                    Start(loop);
                    break;
                case LoopStep.Move when _instances.TryGetValue(loop.Handle, out EventInstance instance):
                    instance.set3DAttributes(RuntimeUtils.To3DAttributes(new Vector3(loop.X, loop.Y, loop.Z)));
                    break;
                case LoopStep.Stop:
                    StopInstance(loop.Handle);
                    break;
            }
        }
        catch (Exception ex)
        {
            if (step == LoopStep.Start && !_instances.ContainsKey(loop.Handle))
            {
                _loops.Forget(loop.Handle);
                _startFailures++;
            }

            Failed($"handle {loop.Handle} {loop.Op}", ex);
        }
    }

    /// <summary>
    /// Call once per frame after the player's: every loop stops when playback holds, pauses while the stream stalls and goes
    /// on with it, and stops when the host has been silent about it too long. Never throws.
    /// </summary>
    public void AfterFrame(StreamPlayer player)
    {
        if (!_begun || !player.Playing)
        {
            return;
        }

        try
        {
            if (player.Holding)
            {
                if (_loops.Count > 0)
                {
                    _stoppedAtHold += _loops.Count;
                    StopAll();
                }

                return;
            }

            PlayoutTotals totals = player.Totals;
            double now = FrameClock.Seconds;
            bool progressed = totals.Frames > _lastFrames && totals.HeldFrames == _lastHeld;
            if (progressed)
            {
                // The stream's own running time: it stands still while the stream stalls or the pause menu holds a recording.
                _runningMicros += (long)(Math.Max(0, now - _lastFrameAt) * 1_000_000);
                _lastProgressAt = now;
            }

            (_lastFrames, _lastHeld, _lastFrameAt) = (totals.Frames, totals.HeldFrames, now);
            if (_loops.Count == 0)
            {
                _paused = false;
                return;
            }

            if (progressed && _paused)
            {
                SetPaused(false);
            }
            else if (!progressed && !_paused && now - _lastProgressAt >= StallSeconds)
            {
                SetPaused(true);
            }
            else if (_paused && now - _lastProgressAt >= LongStallSeconds)
            {
                _stoppedInStall += _loops.Count;
                StopAll();
                return;
            }

            _handles.Clear();
            _loops.TakeSilent(_runningMicros, _handles);
            foreach (uint handle in _handles)
            {
                StopInstance(handle);
            }
        }
        catch (Exception ex)
        {
            Failed("the frame's check", ex);
        }
    }

    /// <inheritdoc />
    public void End(bool matchLoaded)
    {
        // The loops are this PC's own FMOD instances, not the match's: stopped whether or not the match is still loaded.
        int atEnd = _loops.Count;
        StopAll();
        if (!_begun)
        {
            return;
        }

        _begun = false;
        if (_loops.Started + _loops.Refused + _malformed + _startFailures == 0)
        {
            return;
        }

        ModLog.Info($"Loop channel: {_loops.Started} gameplay loop(s) played with the game's own sound code ({_loops.DescribeStarted()}): " +
            $"{_loops.StoppedByHost} stopped by the host, {_stoppedAtHold} when playback held (the match or the session ended), {atEnd} at playback's end, " +
            $"{_loops.Silenced} after {LoopPlayback.SilenceMicros / 1_000_000} s without word from the host, {_stoppedInStall} in a stall of {LongStallSeconds:0} s; " +
            $"{_loops.Moves} moves; paused {_stalls} time(s) while the stream stalled" +
            $"{(_loops.Refused > 0 ? $"; {_loops.Refused} refused (over {LoopPlayback.MaxLoops} at once)" : string.Empty)}" +
            $"{(_startFailures > 0 ? $"; {_startFailures} couldn't start" : string.Empty)}{(_malformed > 0 ? $"; {_malformed} malformed" : string.Empty)}" +
            $"{(_failures > 0 ? $"; {_failures} failed" : string.Empty)}.");
    }

    /// <inheritdoc />
    public void Finish()
    {
    }

    /// <summary>Starts a loop with the game's own starter for its kind, on this PC's SoundManager.</summary>
    private void Start(SoundLoopMessage loop)
    {
        Il2Cpp.SoundManager? sound = Il2Cpp.SoundManager.Instance;
        var position = new Vector3(loop.X, loop.Y, loop.Z);
        EventInstance instance = sound == null ? default : loop.Kind switch
        {
            <= LoopKind.MagnetSafeBounce => sound.StartMagnetSound(position, (MagnetType)(int)loop.Kind),
            LoopKind.RetroRocket => sound.StartRetroRocketSound(position),
            LoopKind.ExplodingPuck => sound.StartExplodingPuckSound(position),
            _ => sound.StartDragonShoutChargeSound(position),
        };

        if (sound == null || !instance.isValid())
        {
            _loops.Forget(loop.Handle);
            if (_startFailures++ < 3)
            {
                ModLog.Warn($"Loop channel: the host's {LoopKinds.Name(loop.Kind)} couldn't start here ({(sound == null ? "no SoundManager" : "the game's starter gave no sound")}).");
            }

            return;
        }

        _instances[loop.Handle] = instance;
        if (_paused)
        {
            instance.setPaused(true);
        }

        if (!_firstLogged)
        {
            _firstLogged = true;
            ModLog.Info($"Loop channel: first gameplay loop from the host: {LoopKinds.Name(loop.Kind)} (SoundManager.{StarterName(loop.Kind)}, handle {loop.Handle}).");
        }
    }

    private static string StarterName(LoopKind kind) => kind switch
    {
        <= LoopKind.MagnetSafeBounce => "StartMagnetSound",
        LoopKind.RetroRocket => "StartRetroRocketSound",
        LoopKind.ExplodingPuck => "StartExplodingPuckSound",
        _ => "StartDragonShoutChargeSound",
    };

    /// <summary>Stops every loop playing here (playback's end, a hold), each the game's way.</summary>
    private void StopAll()
    {
        _handles.Clear();
        _loops.TakeAll(_handles);
        foreach (uint handle in _handles)
        {
            StopInstance(handle);
        }

        // Anything left (a start the playback no longer knows): stopped too, never left playing.
        if (_instances.Count > 0)
        {
            _handles.Clear();
            _handles.AddRange(_instances.Keys);
            foreach (uint handle in _handles)
            {
                StopInstance(handle);
            }
        }

        _paused = false;
    }

    /// <summary>
    /// As the game's own StopSfx: stop with its fade-out, then release. One paused by a stall goes on first (a paused instance's
    /// timeline stands still, so its fade-out would never run and it would never go, holding its place among the event's
    /// instances; and once out of <see cref="_instances"/> nothing here would unpause it). Never throws.
    /// </summary>
    private void StopInstance(uint handle)
    {
        if (!_instances.Remove(handle, out EventInstance instance))
        {
            return;
        }

        try
        {
            if (instance.isValid())
            {
                if (_paused)
                {
                    instance.setPaused(false);
                }

                instance.stop(Il2CppFMOD.Studio.STOP_MODE.ALLOWFADEOUT);
                instance.release();
            }
        }
        catch (Exception ex)
        {
            Failed($"stopping handle {handle}", ex);
        }
    }

    /// <summary>Pauses every loop while the stream stalls, or lets them go on.</summary>
    private void SetPaused(bool paused)
    {
        _paused = paused;
        if (paused && _stalls++ < LoggedStalls)
        {
            ModLog.Info(string.Create(CultureInfo.InvariantCulture,
                $"Loop channel: the stream stalled for {StallSeconds:0.0} s; {_instances.Count} loop(s) paused until it goes on") +
                $"{(_stalls == LoggedStalls ? " (further stalls are counted)" : string.Empty)}.");
        }

        foreach (EventInstance instance in _instances.Values)
        {
            instance.setPaused(paused);
        }
    }

    private void Failed(string what, Exception ex)
    {
        if (_failures++ < 3)
        {
            ModLog.Warn($"Loop channel: {what} failed ({ModLog.SafeErrorText(ex)}).");
        }
    }
}
#endif
