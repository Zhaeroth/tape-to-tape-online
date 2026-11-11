#if HAS_INTEROP
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using Il2CppCinemachine;
using Il2CppInterop.Runtime;
using Il2CppUltimateReplay;
using T2TOnline.Plugin.Diagnostics;
using T2TOnline.Plugin.Native;
using UnityEngine;

namespace T2TOnline.Plugin.Replay;

/// <summary>
/// M0.3's second half: plays a captured stretch of the current match back onto the live scene from the captured
/// bytes alone, the way a friend's PC will show the host's match. F11 during play (not from the pause menu, which hides
/// the rink): the match is frozen by the mod itself, game time at 0 and physics in script mode, while the camera keeps
/// following on real time.
/// </summary>
/// <remarks>
/// <para>
/// It drives UltimateReplay's per-component lifecycle directly, as its own playback does
/// (ReplayPlaybackOperation.ReplayPlaybackUpdate): replay-start on every replay component of the captured objects,
/// then for each snapshot interval deserialize snapshot i, apply it fully, deserialize snapshot i+1 as the target, and
/// every frame update with t = the blend factor between them. Physics is switched to script mode for the duration (no
/// collisions). The game's own "replay started" events are not raised, so no game logic takes part.
/// </para>
/// <para>
/// Objects are matched by ReplayIdentity id through the recording scene the capture came from (skaters aren't in
/// UltimateReplay's global registry), so the capture must come from the match being played.
/// </para>
/// <para>
/// Putting the scene back is what matters most, so it's guarded twice: before playing, every object's current state
/// goes through the same load-and-apply path (a failure there stops and undoes); afterwards that state is applied again,
/// and any object still away from where it was is moved back directly.
/// </para>
/// </remarks>
internal sealed class FrozenReplay
{
    private const double MinMappedShare = 0.5;
    private const float PositionTolerance = 0.05f;

    private readonly Stopwatch _clock = new();
    private readonly Dictionary<int, ReplayObject> _objects = new();
    private readonly List<Saved> _saved = new();
    private Il2CppSystem.Collections.Generic.HashSet<ReplayBehaviour>? _behaviours;
    private IReadOnlyList<CapturedSnapshot>? _snapshots;
    private SimulationMode _physicsMode;
    private float _timeScale = 1f;
    private readonly List<(CinemachineTargetGroup Group, CinemachineTargetGroup.UpdateMethod Method)> _groups = new();
    private CinemachineBrain? _brain;
    private bool _brainIgnoredTimeScale;
    private CinemachineBrain.UpdateMethod _brainUpdate;
    private CinemachineBrain.BrainUpdateMethod _brainBlendUpdate;
    private int _interval = -1;
    private int _applied;
    private int _failures;
    private int _skipped;
    private int _frames;
    private int _poolAtStart = -1;

    /// <summary>True while a replay is running.</summary>
    public bool Active => _snapshots != null;

    /// <summary>
    /// Starts replaying <paramref name="capture"/> onto the scene it came from (or, if the game has moved on to another
    /// recording scene since, onto <paramref name="currentScene"/>), or stops the running replay.
    /// </summary>
    public void Toggle(IReadOnlyList<CapturedSnapshot>? capture, ReplayScene? captureScene, ReplayScene? currentScene)
    {
        if (Active)
        {
            Stop("F11 pressed");
            return;
        }

        ReplayScene? scene = captureScene ?? currentScene;
        if (capture == null || capture.Count < 2 || scene == null)
        {
            ModLog.Warn("Frozen replay (F11): no capture in memory yet. Press Ctrl+F9 in this match, play 10 s, then F11.");
            return;
        }

        if (Time.timeScale < 0.01f)
        {
            ModLog.Warn("Frozen replay (F11): the game is paused, and the pause menu hides the rink. Unpause and press F11 during play; the mod freezes the match itself.");
            return;
        }

        try
        {
            if (!Start(capture, scene) && currentScene != null && currentScene.Pointer != scene.Pointer)
            {
                ModLog.Info("Frozen replay: trying the game's current recording scene instead.");
                Start(capture, currentScene);
            }
        }
        catch (Exception ex)
        {
            ModLog.Error("Frozen replay: could not start", ex);
            Stop("start failed");
        }
    }

    /// <summary>Call once per frame, after the game's own updates.</summary>
    public void LateUpdate()
    {
        if (_snapshots == null)
        {
            return;
        }

        try
        {
            double playback = _snapshots[0].Time + _clock.Elapsed.TotalSeconds;
            int interval = _interval < 0 ? 0 : _interval;
            while (interval + 1 < _snapshots.Count && _snapshots[interval + 1].Time <= playback)
            {
                interval++;
            }

            if (interval + 1 >= _snapshots.Count)
            {
                Stop("finished");
                return;
            }

            CapturedSnapshot from = _snapshots[interval];
            CapturedSnapshot to = _snapshots[interval + 1];
            if (interval != _interval)
            {
                // The same order as UltimateReplay's playback: settle on snapshot i, then aim at snapshot i+1.
                Apply(from);
                ReplayBehaviour.InvokeReplayUpdateEvent(_behaviours, 1f);
                Apply(to);
                _interval = interval;
            }

            float span = Math.Max(to.Time - from.Time, 1e-4f);
            float t = (float)Math.Clamp((playback - from.Time) / span, 0, 1);
            ReplayBehaviour.InvokeReplayUpdateEvent(_behaviours, t);
            _frames++;
        }
        catch (Exception ex)
        {
            ModLog.Error("Frozen replay: playback failed", ex);
            Stop("an error during playback");
        }
    }

    /// <summary>Stops the replay and puts every object back as it was before.</summary>
    public void Stop(string reason)
    {
        if (_snapshots == null)
        {
            return;
        }

        IReadOnlyList<CapturedSnapshot> snapshots = _snapshots;
        _snapshots = null;
        _clock.Stop();
        int restoreFailures = 0;
        int moved = 0;
        try
        {
            if (_behaviours != null)
            {
                foreach (Saved saved in _saved)
                {
                    restoreFailures += Deserialize(saved.Object, saved.State) ? 0 : 1;
                }

                ReplayBehaviour.InvokeReplayUpdateEvent(_behaviours, 1f);
                ReplayBehaviour.InvokeReplayEndEvent(_behaviours);
            }
        }
        catch (Exception ex)
        {
            ModLog.Error("Frozen replay: restoring through the replay components failed; moving objects back directly", ex);
        }
        finally
        {
            moved = PutBackPositions();
            Physics.simulationMode = _physicsMode;
            Physics.SyncTransforms();
            Unfreeze();
        }

        float shown = _interval < 0 ? 0 : snapshots[Math.Min(_interval + 1, snapshots.Count - 1)].Time - snapshots[0].Time;
        ModLog.Info(string.Create(CultureInfo.InvariantCulture, $"Frozen replay: stopped ({reason}). Played {shown:0.0} s of {snapshots[^1].Time - snapshots[0].Time:0.0} s over {_frames} frames: ") +
            $"{_applied} object states applied, {_skipped} skipped (object not in the scene), {_failures} failed. " +
            $"Restore: {_saved.Count} objects, {restoreFailures} failed, {moved} moved back directly; physics back to {_physicsMode}. " +
            $"{ReplayStates.Describe(_poolAtStart)}.");
        _objects.Clear();
        _saved.Clear();
        _behaviours = null;
    }

    /// <summary>Starts playing; false if too few of the capture's objects are in <paramref name="scene"/>.</summary>
    private bool Start(IReadOnlyList<CapturedSnapshot> capture, ReplayScene scene)
    {
        _objects.Clear();
        _saved.Clear();
        _interval = -1;
        _applied = _failures = _skipped = _frames = 0;
        _poolAtStart = ReplayStates.PoolCount();

        Il2CppSystem.Collections.Generic.HashSet<ReplayObject>.Enumerator live = scene.ActiveReplayObjects.GetEnumerator();
        while (live.MoveNext())
        {
            ReplayObject replayObject = live.Current;
            if (replayObject != null)
            {
                _objects.TryAdd(replayObject.ReplayIdentity.ID, replayObject);
            }
        }

        HashSet<int> captured = capture.SelectMany(s => s.Objects).Select(o => o.Id).ToHashSet();
        List<int> mapped = captured.Where(_objects.ContainsKey).ToList();
        if (mapped.Count < captured.Count * MinMappedShare)
        {
            ModLog.Warn($"Frozen replay: only {mapped.Count} of the capture's {captured.Count} objects are in this scene; the capture is from another match. Press Ctrl+F9 in this match first.");
            _objects.Clear();
            return false;
        }

        var behaviours = new Il2CppSystem.Collections.Generic.HashSet<ReplayBehaviour>();
        foreach (int id in mapped)
        {
            ReplayObject replayObject = _objects[id];
            foreach (ReplayBehaviour behaviour in Il2CppLists.Items(replayObject.Behaviours))
            {
                behaviours.Add(behaviour);
            }

            byte[] state = ReplayStates.Serialize(replayObject);
            Transform transform = replayObject.transform;
            _saved.Add(new Saved(replayObject, state, transform.position, transform.rotation));
        }

        // Marked running before anything changes, so any failure from here on goes through Stop and gets undone.
        _behaviours = behaviours;
        _snapshots = capture;
        _physicsMode = Physics.simulationMode;
        Physics.simulationMode = SimulationMode.Script;
        Freeze();
        ReplayBehaviour.InvokeReplayStartEvent(behaviours);

        // Dry run, inside the replay lifecycle (components such as the skaters' Spine replay switch their own animation
        // off on update and back on at replay end): re-apply every object's current state through the exact
        // load-and-apply path. Nothing visible changes; if any object can't take it, stop here and undo.
        int dryRunFailures = _saved.Count(saved => !Deserialize(saved.Object, saved.State));
        ReplayBehaviour.InvokeReplayUpdateEvent(behaviours, 1f);
        _applied = 0;
        if (dryRunFailures > 0)
        {
            ModLog.Warn($"Frozen replay: {dryRunFailures} of {_saved.Count} objects couldn't re-apply their own current state; not playing.");
            Stop("dry run failed");
            return true;
        }

        _clock.Restart();
        ModLog.Info(string.Create(CultureInfo.InvariantCulture, $"Frozen replay: playing {capture[^1].Time - capture[0].Time:0.0} s ({capture.Count} snapshots) onto {mapped.Count} of {captured.Count} captured objects ") +
            string.Create(CultureInfo.InvariantCulture, $"({behaviours.Count} replay components); dry run passed. game time frozen (was {_timeScale}), physics set to Script, {CameraNote()}. Don't touch the controller; F11 stops early."));
        return true;
    }

    private void Apply(CapturedSnapshot snapshot)
    {
        foreach (CapturedObject captured in snapshot.Objects)
        {
            if (!_objects.TryGetValue(captured.Id, out ReplayObject? replayObject))
            {
                _skipped++;
                continue;
            }

            Deserialize(replayObject, captured.State);
        }
    }

    /// <summary>Loads <paramref name="bytes"/> into the object and applies it (<see cref="ReplayStates.TryDeserialize"/>).</summary>
    private bool Deserialize(ReplayObject replayObject, byte[] bytes)
    {
        if (ReplayStates.TryDeserialize(replayObject, bytes, out string? error))
        {
            _applied++;
            return true;
        }

        if (_failures++ < 5)
        {
            ModLog.Warn($"Frozen replay: could not apply a state to \"{LogNames.ObjectName(replayObject)}\": {error}");
        }

        return false;
    }

    /// <summary>
    /// Stops game time (gameplay, physics steps, animation) and lets the camera keep following on real time. With the
    /// time scale at 0 no FixedUpdate runs, so the camera brain and any target groups are switched to LateUpdate too:
    /// left on FixedUpdate (or SmartUpdate with physics-driven targets) they would never move.
    /// </summary>
    private void Freeze()
    {
        _timeScale = Time.timeScale;
        Time.timeScale = 0f;
        _brain = UnityEngine.Object.FindAnyObjectByType(Il2CppType.Of<CinemachineBrain>())?.TryCast<CinemachineBrain>();
        if (_brain != null)
        {
            _brainIgnoredTimeScale = _brain.m_IgnoreTimeScale;
            _brainUpdate = _brain.m_UpdateMethod;
            _brainBlendUpdate = _brain.m_BlendUpdateMethod;
            _brain.m_IgnoreTimeScale = true;
            _brain.m_UpdateMethod = CinemachineBrain.UpdateMethod.LateUpdate;
            _brain.m_BlendUpdateMethod = CinemachineBrain.BrainUpdateMethod.LateUpdate;
        }

        _groups.Clear();
        foreach (UnityEngine.Object found in UnityEngine.Object.FindObjectsByType(Il2CppType.Of<CinemachineTargetGroup>(), FindObjectsSortMode.None))
        {
            CinemachineTargetGroup? group = found.TryCast<CinemachineTargetGroup>();
            if (group != null)
            {
                _groups.Add((group, group.m_UpdateMethod));
                group.m_UpdateMethod = CinemachineTargetGroup.UpdateMethod.LateUpdate;
            }
        }
    }

    private string CameraNote() => _brain == null
        ? "no camera brain found, so the camera stays put"
        : $"camera following on real time (brain was {_brainUpdate}/{_brainBlendUpdate}, ignore time scale {_brainIgnoredTimeScale}; {_groups.Count} target group(s) were {string.Join("/", _groups.Select(g => g.Method).Distinct())}; all set to LateUpdate)";

    private void Unfreeze()
    {
        // Only undo our own freeze: if something else changed the time scale meanwhile, leave its value.
        if (Time.timeScale < 0.01f)
        {
            Time.timeScale = _timeScale;
        }

        foreach ((CinemachineTargetGroup group, CinemachineTargetGroup.UpdateMethod method) in _groups)
        {
            try
            {
                group.m_UpdateMethod = method;
            }
            catch (Exception ex)
            {
                ModLog.Warn($"Frozen replay: could not put a camera target group back: {ModLog.SafeErrorText(ex)}");
            }
        }

        _groups.Clear();
        if (_brain != null)
        {
            _brain.m_IgnoreTimeScale = _brainIgnoredTimeScale;
            _brain.m_UpdateMethod = _brainUpdate;
            _brain.m_BlendUpdateMethod = _brainBlendUpdate;
            _brain = null;
        }
    }

    /// <summary>Moves every saved object that's more than a few centimetres off back to where it was. Returns how many.</summary>
    private int PutBackPositions()
    {
        var moved = new List<string>();
        foreach (Saved saved in _saved)
        {
            try
            {
                Transform transform = saved.Object.transform;
                float off = (transform.position - saved.Position).magnitude;
                if (off > PositionTolerance)
                {
                    transform.SetPositionAndRotation(saved.Position, saved.Rotation);
                    moved.Add(string.Create(CultureInfo.InvariantCulture, $"{LogNames.ObjectName(saved.Object)} ({off:0.00} m)"));
                }
            }
            catch (Exception ex)
            {
                ModLog.Warn($"Frozen replay: could not move an object back: {ModLog.SafeErrorText(ex)}");
            }
        }

        if (moved.Count > 0)
        {
            ModLog.Info($"Frozen replay: moved back directly: {string.Join(", ", moved)}.");
        }

        return moved.Count;
    }

    private sealed record Saved(ReplayObject Object, byte[] State, Vector3 Position, Quaternion Rotation);
}
#endif
