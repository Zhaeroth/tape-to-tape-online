#if HAS_INTEROP
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using Il2CppUltimateReplay;
using Il2CppUltimateReplay.Storage;
using T2TOnline.Core.Diagnostics;
using T2TOnline.Plugin.Diagnostics;

namespace T2TOnline.Plugin.Replay;

/// <summary>
/// M0.3 test tools on the game's own replay recorder, riding on <see cref="ReplaySceneTracker"/>'s postfix on
/// <c>ReplayScene.CaptureSnapshot</c>:
/// <list type="bullet">
/// <item>measuring (opt-in, off unless <see cref="Install"/> is told to measure): each snapshot's per-object states (the
/// bytes friends' PCs would be sent) are read without serializing anything: size, which objects changed since the last
/// snapshot, record rate, and the probe's own cost, summed up in the log every 10 seconds of recording; the first
/// recording of the session is also saved (10 seconds);</item>
/// <item>Ctrl+F9 saves the next 10 seconds of snapshots to a file for offline work on client mode (F11 plays them back),
/// measuring or not. Only the newest 10 captures are kept (<see cref="DevFileRetention.KeptCaptures"/>).</item>
/// </list>
/// A failure switches these tools off for the session, never the tracker that online play depends on.
/// <see cref="LastScene"/> and <see cref="SecondsSinceLastSnapshot"/> are the tracker's.
/// </summary>
internal static class SnapshotProbe
{
    /// <summary>Estimated bytes a packet would spend per object besides its state: a 4-byte id and a 2-byte length.</summary>
    private const int PerObjectOverhead = 6;
    private const double WindowSeconds = 10;
    private const double CaptureSeconds = 10;
    private const double RecordingEndedSeconds = 5;
    private const int TopObjectsLogged = 15;

    private static readonly Dictionary<int, ObjectStats> Objects = new();
    private static readonly Dictionary<IntPtr, (int Sequence, float Time)> LastSnapshotByScene = new();
    private static readonly SampleWindow FullBytes = new();
    private static readonly SampleWindow DeltaBytes = new();
    private static readonly SampleWindow ChangedObjects = new();
    private static readonly SampleWindow RecordedObjects = new();
    private static readonly SampleWindow IntervalMs = new();
    private static readonly SampleWindow ProbeMicroseconds = new();
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static string _captureDirectory = string.Empty;
    private static bool _enabled;
    private static bool _measuring;
    private static bool _autoCaptured;
    private static double _windowStart = -1;
    private static double _lastSnapshotAt = -1;
    private static long _snapshotsInWindow;
    private static long _snapshotsTotal;
    private static SnapshotCapture? _capture;

    /// <summary>The snapshots of the last finished capture, oldest first, or null if there's none yet.</summary>
    public static IReadOnlyList<CapturedSnapshot>? LastCapture { get; private set; }

    /// <summary>The recording scene the last capture came from: its objects are the ones the capture's ids name.</summary>
    public static ReplayScene? LastCaptureScene { get; private set; }

    /// <summary>The scene the recorder captured from most recently (it can change at a faceoff): <see cref="ReplaySceneTracker.LastScene"/>.</summary>
    public static ReplayScene? LastScene => ReplaySceneTracker.LastScene;

    /// <summary>Seconds since the game's recorder last captured a snapshot (infinity before the first): the tracker's.</summary>
    public static double SecondsSinceLastSnapshot => ReplaySceneTracker.SecondsSinceLastSnapshot;

    private static ReplayScene? _captureScene;

    /// <summary>
    /// Installs the scene tracker (always wanted: online play depends on it) and readies the tools: Ctrl+F9 captures, and
    /// with <paramref name="measure"/> the 10-second summaries and the first recording's capture. Returns false, logging
    /// why, if the recorder can't be patched.
    /// </summary>
    public static bool Install(HarmonyLib.Harmony harmony, string captureDirectory, bool measure = false)
    {
        if (!ReplaySceneTracker.Install(harmony))
        {
            return false;
        }

        _captureDirectory = captureDirectory;
        _measuring = measure;
        _enabled = true;
        ModLog.Info(measure
            ? "Snapshot probe: measuring. It reports every 10 s while the game records a match and saves the first 10 s" +
                (Startup.ModSettings.DevTools ? "; Ctrl+F9 saves the next 10 s of snapshots." : ".")
            : Startup.ModSettings.DevTools ? "Snapshot probe: not measuring (opt-in); Ctrl+F9 saves the next 10 s of snapshots." : "Snapshot probe: not measuring (opt-in).");
        return true;
    }

    /// <summary>Starts saving the next 10 seconds of snapshots (Ctrl+F9).</summary>
    public static void RequestCapture(string reason)
    {
        if (!_enabled || _capture != null)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(_captureDirectory);
            string stamp = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
            _capture = new SnapshotCapture(Path.Combine(_captureDirectory, $"snapshots-{stamp}.t2tsnap"), Clock.Elapsed.TotalSeconds + CaptureSeconds);
            ModLog.Info($"Snapshot probe: saving 10 s of snapshots ({reason}) to {DevOutput.Label(_capture.Path)}.");
            DevOutput.PruneCaptures(_captureDirectory, _capture.Path);
            DevOutput.EnsureNotice();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ModLog.Error("Snapshot probe: could not start a capture", ex);
        }
    }

    /// <summary>Per-frame: notices when recording stops, and closes a finished capture.</summary>
    public static void Update()
    {
        double now = Clock.Elapsed.TotalSeconds;
        if (_capture != null && now >= _capture.EndsAt)
        {
            FinishCapture();
        }

        if (_lastSnapshotAt >= 0 && now - _lastSnapshotAt > RecordingEndedSeconds)
        {
            _lastSnapshotAt = -1;
            LogWindow();
            LogTopObjects("recording paused or ended");
        }
    }

    /// <summary>Logs whatever hasn't been reported yet (game closing).</summary>
    public static void Flush()
    {
        if (_capture != null)
        {
            FinishCapture();
        }

        if (_snapshotsInWindow > 0)
        {
            LogWindow();
        }

        if (_snapshotsTotal > 0)
        {
            LogTopObjects("session end");
        }
    }

    /// <summary>
    /// The tracker saw a snapshot (from inside the game's recorder): measured and captured as wanted. Never throws; a
    /// failure switches the tools off for the session (the tracker carries on).
    /// </summary>
    internal static void OnSnapshot(ReplayScene scene, ReplaySnapshot snapshot)
    {
        if (!_enabled || (!_measuring && _capture == null))
        {
            return;
        }

        try
        {
            Observe(scene, snapshot);
        }
        catch (Exception ex)
        {
            _enabled = false;
            ModLog.Error("Snapshot probe failed and is off for this session (the scene tracker carries on)", ex);
        }
    }

    private static void Observe(ReplayScene scene, ReplaySnapshot snapshot)
    {
        long started = Stopwatch.GetTimestamp();
        double now = Clock.Elapsed.TotalSeconds;
        if (_windowStart < 0)
        {
            _windowStart = now;
        }

        if (_measuring && !_autoCaptured)
        {
            _autoCaptured = true;
            RequestCapture("first recording of the session");
        }

        int sequence = snapshot.SequenceID;
        float time = snapshot.TimeStamp;
        bool continuous = LastSnapshotByScene.TryGetValue(scene.Pointer, out var last) && sequence == last.Sequence + 1;
        LastSnapshotByScene[scene.Pointer] = (sequence, time);

        int recorded = 0;
        int changed = 0;
        long fullBytes = 0;
        long deltaBytes = 0;
        if (_capture != null)
        {
            _captureScene = scene;
        }

        _capture?.BeginSnapshot(time, sequence);
        Il2CppSystem.Collections.Generic.HashSet<ReplayObject>.Enumerator objects = scene.ActiveReplayObjects.GetEnumerator();
        while (objects.MoveNext())
        {
            ReplayObject replayObject = objects.Current;
            ReplayIdentity identity = replayObject.ReplayIdentity;
            ReplayState? state = snapshot.RestoreSnapshot(identity);
            if (state == null)
            {
                continue;
            }

            int id = identity.ID;
            int size = state.Size;
            long hash = state.DataHash;
            if (!Objects.TryGetValue(id, out ObjectStats? stats))
            {
                stats = new ObjectStats(LogNames.ObjectName(replayObject)); // logged: a skater goes by its asset id
                Objects[id] = stats;
            }

            recorded++;
            fullBytes += size + PerObjectOverhead;
            bool isChanged = !continuous || !stats.Seen || stats.LastHash != hash;
            if (isChanged)
            {
                changed++;
                deltaBytes += size + PerObjectOverhead;
                stats.Changes++;
            }

            stats.Seen = true;
            stats.LastHash = hash;
            stats.Snapshots++;
            stats.Bytes += size;
            stats.MaxBytes = Math.Max(stats.MaxBytes, size);
            _capture?.WriteObject(id, state.ToArray(), size, stats.Name);
        }

        _capture?.EndSnapshot(recorded);
        if (!_measuring)
        {
            // Capturing only (Ctrl+F9): no summaries.
            return;
        }

        RecordedObjects.Add(recorded);
        ChangedObjects.Add(changed);
        FullBytes.Add(fullBytes);
        DeltaBytes.Add(deltaBytes);
        if (continuous)
        {
            IntervalMs.Add((time - last.Time) * 1000.0);
        }

        _snapshotsInWindow++;
        _snapshotsTotal++;
        _lastSnapshotAt = now;
        ProbeMicroseconds.Add((Stopwatch.GetTimestamp() - started) * 1_000_000.0 / Stopwatch.Frequency);
        if (now - _windowStart >= WindowSeconds)
        {
            LogWindow();
        }
    }

    private static void LogWindow()
    {
        if (_snapshotsInWindow == 0)
        {
            return;
        }

        double seconds = Math.Max(Clock.Elapsed.TotalSeconds - _windowStart, 0.001);
        SampleSummary interval = IntervalMs.Summarize();
        ModLog.Info(string.Create(CultureInfo.InvariantCulture,
            $"Snapshot probe: {_snapshotsInWindow} snapshots in {seconds:0.0} s ({_snapshotsInWindow / seconds:0.0}/s; recorder interval {interval.Mean:0.0} ms mean, {interval.Max:0.0} max). ") +
            $"Objects recorded {RecordedObjects.Summarize()}, changed {ChangedObjects.Summarize()}. " +
            $"Bytes per snapshot (mean/p95/max): every object {FullBytes.Summarize()}, changed objects only {DeltaBytes.Summarize()}. " +
            $"Probe cost {ProbeMicroseconds.Summarize()} µs.");
        foreach (SampleWindow window in new[] { RecordedObjects, ChangedObjects, FullBytes, DeltaBytes, IntervalMs, ProbeMicroseconds })
        {
            window.Clear();
        }

        _snapshotsInWindow = 0;
        _windowStart = Clock.Elapsed.TotalSeconds;
    }

    private static void LogTopObjects(string when)
    {
        if (Objects.Count == 0)
        {
            return;
        }

        IEnumerable<string> lines = Objects
            .OrderByDescending(pair => pair.Value.Bytes)
            .Take(TopObjectsLogged)
            .Select(pair => string.Create(CultureInfo.InvariantCulture,
                $"  #{pair.Key} {pair.Value.Name}: {pair.Value.Bytes / Math.Max(pair.Value.Snapshots, 1)} B avg, {pair.Value.MaxBytes} max, changed in {100.0 * pair.Value.Changes / Math.Max(pair.Value.Snapshots, 1):0}% of snapshots"));
        ModLog.Info($"Snapshot probe: {Objects.Count} distinct objects so far ({_snapshotsTotal} snapshots, {when}). Heaviest:{Environment.NewLine}{string.Join(Environment.NewLine, lines)}");
    }

    private static void FinishCapture()
    {
        SnapshotCapture capture = _capture!;
        _capture = null;
        try
        {
            capture.Dispose();
            LastCapture = capture.Captured;
            LastCaptureScene = _captureScene;
            ModLog.Info($"Snapshot probe: saved {capture.Snapshots} snapshots ({capture.Bytes} bytes of state) to {DevOutput.Label(capture.Path)}.");
        }
        catch (IOException ex)
        {
            ModLog.Error("Snapshot probe: could not finish the capture file", ex);
        }
    }

    private sealed class ObjectStats
    {
        public ObjectStats(string name) => Name = name;

        public string Name { get; }

        public bool Seen { get; set; }

        public long LastHash { get; set; }

        public long Snapshots { get; set; }

        public long Changes { get; set; }

        public long Bytes { get; set; }

        public int MaxBytes { get; set; }
    }
}
#endif
