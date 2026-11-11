#if HAS_INTEROP
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Il2CppUltimateReplay;
using T2TOnline.Core.Diagnostics;
using T2TOnline.Core.Protocol;
using T2TOnline.Core.Serialization;
using T2TOnline.Core.Snapshots;
using T2TOnline.Core.Stream;
using T2TOnline.Plugin.Diagnostics;
using T2TOnline.Plugin.Replay;
using UnityEngine;

namespace T2TOnline.Plugin.Streaming;

/// <summary>
/// Records what a host sends its friends, to a <c>.t2tstream</c> file, to the friends
/// themselves over the network (<see cref="MatchStreamHost"/>), or both:
/// <list type="bullet">
/// <item>every replay object's state at up to 60 Hz, serialized through its own recorders (the game's recorder pauses during
/// goal replays and menus, so this doesn't wait for it);</item>
/// <item>the rendered camera's pose and the global time scale in every snapshot (per-group time scales aren't read:
/// <see cref="SnapshotFrame.GroupScales"/> stays empty, so a friend's groups stay at 1). Objects are read in the mod's
/// LateUpdate, and the snapshot goes out just before that same frame renders, with the pose that renders it: read in
/// LateUpdate, the pose would be the previous frame's (the render comes after every LateUpdate), a frame behind the
/// objects. With no render by the next LateUpdate (a minimised window), it goes then with the pose read there;</item>
/// <item>the object table as objects appear;</item>
/// <item>whatever the presentation channels add (events, sounds, HUD) through <see cref="Write"/>.</item>
/// </list>
/// In the file, snapshots are keyframes every 2 s and deltas against the previous snapshot in between; on the network each
/// friend gets deltas against the newest snapshot they acknowledged. The file is flushed every second, so a crash loses
/// at most a second. Every message header carries the stream time in milliseconds, so a friend shows it on time.
/// The file is the lesser half: when it can't be made or written (a full disk) the network goes on without it; any other
/// failure stops the recorder through its owner (<see cref="Failure"/>), never one error after another every frame.
/// The work per snapshot is timed (<see cref="Costs"/>).
/// </summary>
internal sealed class StreamRecorder : IDisposable
{
    /// <summary>Snapshots per second, on a fixed beat (a frame that misses its slot takes the next one, so the average holds).</summary>
    public const int SnapshotRate = 60;

    private const long SnapshotInterval = 1_000_000 / SnapshotRate;

    private const long KeyframeEveryMicros = 2_000_000;
    private const long FlushEveryMicros = 1_000_000;

    private readonly Dictionary<int, ushort> _netIds = new();
    private readonly List<ObjectEntry> _newEntries = new();
    private readonly ulong[] _sequences = new ulong[256];
    private readonly BitWriter _buffer = new(4096);
    private readonly CostMeter _statesCost = new("object states");
    private readonly CostMeter _fileCost = new("file");
    private readonly CostMeter _friendsCost = new("to friends");
    private MatchStreamWriter? _writer;
    private MatchStreamHost? _network;
    private ReplayScene? _scene;
    private Func<ReplayScene?>? _sceneSource;
    private SnapshotFrame? _pending;
    private SnapshotFrame? _previous;
    private uint _snapshotSequence;
    private long _lastKeyframe;
    private long _nextSnapshot;
    private long _nextFlush;
    private double _startedAt;
    private long _lastNow;
    private ushort _nextNetId = 1;

    // Each kind of component list the match's skaters have, logged once each (NoteSkaterComponents).
    private const int MaxSkaterComponentLists = 4;
    private readonly HashSet<string> _skaterComponentLists = new(StringComparer.Ordinal);

    // Skaters whose replay component list is watched during play (CheckComponentLists): a talent added or removed during
    // the match adds or removes a component, and friends get the new list. Looked at twice a second; the first few changes
    // are logged, the rest counted.
    private const long ComponentCheckMicros = 500_000;
    private const int MaxWatched = 64;
    private const int ComponentChangesLogged = 5;
    private readonly List<WatchedList> _watched = new();
    private long _nextComponentCheck;
    private int _componentChanges;
    private int _watchFailures;
    private long _snapshotBytes;
    private long _refusedSnapshots;
    private int _serializeFailures;
    private bool _running;
    private string? _failure;
    private int _poolAtStart = -1;

    /// <summary>True while recording (to a file, the network, or both).</summary>
    public bool Recording => _running;

    /// <summary>True while friends get the stream over the network.</summary>
    public bool Streaming => _network != null;

    /// <summary>The file being (or last) recorded.</summary>
    public string? FilePath { get; private set; }

    /// <summary>Scene paths already worked out for this recording's references (events, HUD values).</summary>
    public PathMemo Paths { get; } = new();

    /// <summary>
    /// Microseconds since recording started, on the frame clock (<see cref="FrameClock"/>: one real-time stamp per frame,
    /// taken early in the frame), never going backwards. Snapshots stamped with it are spaced the way the game moved
    /// things; everything recorded in one frame (snapshot, events, HUD) shares its stamp.
    /// </summary>
    public long Now
    {
        get
        {
            long now = (long)((FrameClock.Seconds - _startedAt) * 1_000_000);
            _lastNow = Math.Max(_lastNow, now);
            return _lastNow;
        }
    }

    /// <summary>Snapshots written.</summary>
    public uint Snapshots => _snapshotSequence;

    /// <summary>
    /// What each snapshot costs on the host's main thread: reading every object's state, the file's own encode and write,
    /// and the encodes and sends for friends.
    /// </summary>
    public IEnumerable<CostMeter> Costs => new[] { _statesCost, _fileCost, _friendsCost };

    /// <summary>Called right after each snapshot is written, with its time: the presentation channels poll there.</summary>
    public event Action<long>? SnapshotWritten;

    /// <summary>
    /// Why the recorder can't go on (an error while recording, or its file failed with no friends to stream to), or null.
    /// Once set it records nothing more, and its owner stops it (<see cref="MatchStreaming"/> checks after every
    /// <see cref="LateUpdate"/>).
    /// </summary>
    public string? Failure => _failure;

    /// <summary>
    /// Starts recording the objects of the game's current replay scene: into a new file in <paramref name="directory"/>
    /// (unless null) and to <paramref name="network"/>'s friends (unless null; its match must have begun). The file starts
    /// with <paramref name="setup"/>, the match setup, when there is one. <paramref name="sceneSource"/> gives the scene
    /// the game records from (it makes a new one at every faceoff).
    /// </summary>
    public bool Start(Func<ReplayScene?> sceneSource, string? directory, MatchStreamHeader header, MatchStreamHost? network,
        IReadOnlyList<KeyValuePair<string, NetValue>>? setup, out string problem)
    {
        ReplayScene? scene = sceneSource();
        if (scene == null)
        {
            problem = "no match is being played";
            return false;
        }

        if (Recording)
        {
            problem = "already recording";
            return false;
        }

        FilePath = null;
        if (directory == null && network == null)
        {
            problem = "nowhere to record to";
            return false;
        }

        if (directory != null)
        {
            try
            {
                Directory.CreateDirectory(directory);
                FilePath = NewFilePath(directory);
                _writer = new MatchStreamWriter(new FileStream(FilePath, FileMode.CreateNew, FileAccess.Write, FileShare.Read), header);
                DevOutput.PruneStreams(directory, FilePath);
                DevOutput.EnsureNotice();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _writer = null;
                if (network == null)
                {
                    FilePath = null;
                    problem = ModLog.SafeErrorText(ex);
                    return false;
                }

                // As when it fails during the match: the friends' stream is what matters.
                ModLog.Warn($"Stream recorder: the file can't be made ({ModLog.SafeErrorText(ex)}; {DevOutput.Label(FilePath)}); streaming to friends without it.");
                FilePath = null;
            }
        }

        _running = true;
        _failure = null;
        _poolAtStart = ReplayStates.PoolCount();
        _network = network;
        _scene = scene;
        _sceneSource = sceneSource;
        _pending = null;
        foreach (CostMeter meter in Costs)
        {
            meter.Clear();
        }

        RenderHook.BeforeRender -= OnBeforeRender;
        RenderHook.BeforeRender += OnBeforeRender;
        _netIds.Clear();
        _newEntries.Clear();
        _skaterComponentLists.Clear();
        _watched.Clear();
        _nextComponentCheck = 0;
        _componentChanges = _watchFailures = 0;
        Paths.Clear();
        Array.Clear(_sequences);
        _previous = null;
        _snapshotSequence = 0;
        _nextNetId = 1;
        _snapshotBytes = 0;
        _refusedSnapshots = 0;
        _serializeFailures = 0;
        _lastKeyframe = long.MinValue;
        _nextSnapshot = 0;
        _nextFlush = FlushEveryMicros;
        _startedAt = FrameClock.Seconds;
        _lastNow = 0;
        if (setup is { Count: > 0 } && _writer != null)
        {
            WriteTo(MessageType.MatchSetup, Channel.Events, w => MatchSetupMessage.Write(w, MatchSetupMessage.Hash(setup), setup), network: false);
        }

        problem = string.Empty;
        return true;
    }

    /// <summary>True once after a friend started getting the stream: the HUD recorder sends every value then.</summary>
    public bool TakeFullStateRequest() => _network?.TakeFullStateRequest() == true;

    /// <summary>
    /// Call once per frame after the camera has moved: takes a snapshot when one is due. Never throws: a failure sets
    /// <see cref="Failure"/>, and nothing more is recorded.
    /// </summary>
    public void LateUpdate()
    {
        if (!Recording || _scene == null || _failure != null)
        {
            return;
        }

        try
        {
            if (_pending != null)
            {
                // Nothing rendered since the last capture (a minimised window): it goes now, with the pose now.
                Send(_pending, CameraState.Read());
            }

            long now = Now;
            if (now < _nextSnapshot)
            {
                return;
            }

            // On a fixed beat: the next slot is one interval after this one, not after now. Behind by more than a slot (a
            // hitch), start the beat again from now rather than catching up with a burst.
            _nextSnapshot += SnapshotInterval;
            if (now - _nextSnapshot > SnapshotInterval)
            {
                _nextSnapshot = now + SnapshotInterval;
            }

            _scene = _sceneSource?.Invoke() ?? _scene;
            CheckComponentLists(now);
            Capture(now);
            SnapshotWritten?.Invoke(now);
            if (now >= _nextFlush)
            {
                _nextFlush = now + FlushEveryMicros;
                FlushFile();
            }
        }
        catch (Exception ex)
        {
            _failure = $"an error while recording ({ModLog.SafeErrorText(ex)})";
            ModLog.Error("Stream recorder: recording failed; it stops now", ex);
        }
    }

    /// <summary>
    /// Writes one message to the file and the friends: <paramref name="payload"/> fills in the body after the header, which
    /// carries the newest snapshot's sequence and the stream time.
    /// </summary>
    public void Write(MessageType type, Channel channel, Action<BitWriter> payload) => WriteTo(type, channel, payload, network: true);

    private void WriteTo(MessageType type, Channel channel, Action<BitWriter> payload, bool network)
    {
        if (!Recording || _failure != null)
        {
            return;
        }

        long now = Now;
        _buffer.Reset();
        new MessageHeader(type, _sequences[(byte)type]++, _snapshotSequence, MatchStreamHost.StreamTime(now)).Write(_buffer);
        payload(_buffer);
        Emit(now, channel, type, network);
    }

    /// <summary>
    /// The file's copy of a snapshot, encoded against <paramref name="baseFrame"/> (null for a keyframe). False, with
    /// nothing written, when the codec refuses the frame (it would never decode).
    /// </summary>
    private bool WriteSnapshotRecord(SnapshotFrame frame, SnapshotFrame? baseFrame)
    {
        if (!Recording || _failure != null)
        {
            return false;
        }

        // Stamped with its capture time (it may go out a moment later, just before the frame renders).
        long now = frame.HostTimeMicros;
        _buffer.Reset();
        new MessageHeader(MessageType.Snapshot, _sequences[(byte)MessageType.Snapshot], _snapshotSequence, MatchStreamHost.StreamTime(now)).Write(_buffer);
        if (!SnapshotCodec.Write(_buffer, frame, baseFrame))
        {
            return false;
        }

        _sequences[(byte)MessageType.Snapshot]++;
        Emit(now, Channel.Realtime, MessageType.Snapshot, network: false);
        return true;
    }

    /// <summary>The buffer's record to the file (a file error closes the file and the network goes on) and, if asked, the network.</summary>
    private void Emit(long now, Channel channel, MessageType type, bool network)
    {
        if (_writer != null)
        {
            try
            {
                _writer.Write(now, channel, _buffer.AsSpan());
            }
            catch (Exception ex) when (IsFileError(ex))
            {
                CloseFailedFile(ex);
            }
        }

        if (network)
        {
            _network?.Message(type, channel, _buffer.AsSpan());
        }
    }

    private static bool IsFileError(Exception ex) => ex is IOException or UnauthorizedAccessException or ObjectDisposedException;

    /// <summary>
    /// The file can't be written (a full disk, the file taken away): it's closed with a warning and the network goes on.
    /// With no friends to stream to, the recorder can't go on (<see cref="Failure"/>).
    /// </summary>
    private void CloseFailedFile(Exception ex)
    {
        MatchStreamWriter? writer = _writer;
        _writer = null;
        long records = writer?.Records ?? 0;
        try
        {
            writer?.Dispose();
        }
        catch (Exception)
        {
            // The same error again on the way out: the file is as complete as it got.
        }

        ModLog.Warn($"Stream recorder: the file can't be written ({ModLog.SafeErrorText(ex)}); closed after {records} records ({DevOutput.Label(FilePath)})" +
            (_network != null ? "; the stream to friends goes on." : "; with no friends to stream to, recording stops."));
        if (_network == null)
        {
            _failure = $"the file couldn't be written ({ModLog.SafeErrorText(ex)})";
        }
    }

    /// <summary>
    /// The network id of a replay object, assigning one (with its stable key, <see cref="ObjectKeys"/>) and announcing it in
    /// the object table if it's new.
    /// </summary>
    public ushort NetIdOf(ReplayObject replayObject)
    {
        int replayId = replayObject.ReplayIdentity.ID;
        if (!_netIds.TryGetValue(replayId, out ushort netId))
        {
            netId = _nextNetId++;
            _netIds[replayId] = netId;
            (ushort[] ids, uint[] types) = Components(replayObject);
            string key = ObjectKeys.KeyOf(replayObject);
            var entry = new ObjectEntry(netId, replayId, key, ReplaySession.ObjectName(replayObject), ids, types);
            _newEntries.Add(entry);
            NoteSkaterComponents(replayObject, key);
            Watch(replayObject, entry);
        }

        return netId;
    }

    /// <summary>
    /// A skater (key "F:") joins the watch: its component list as announced, and the list's own change count
    /// (<c>List._version</c>, which every add, remove and rebuild moves; <c>ReplayObject.Behaviours</c> is its runtime list,
    /// the same object for its whole life). Never throws.
    /// </summary>
    private void Watch(ReplayObject replayObject, ObjectEntry entry)
    {
        if (_watched.Count >= MaxWatched || !entry.Key.StartsWith("F:", StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            if (replayObject.Behaviours?.TryCast<Il2CppSystem.Collections.Generic.List<ReplayBehaviour>>() is { } list)
            {
                _watched.Add(new WatchedList(replayObject, list, entry, list._version));
            }
        }
        catch (Exception ex)
        {
            if (_watchFailures++ == 0)
            {
                ModLog.Warn($"Stream recorder: a skater's replay component list can't be watched ({ModLog.SafeErrorText(ex)}); " +
                    "a change to it during play won't reach friends.");
            }
        }
    }

    /// <summary>
    /// Twice a second, before the capture: each watched skater whose component list changed since it was announced (a
    /// talent's component added or removed during play) is announced again with its new list, before the snapshot that
    /// names it goes out; a friend takes it over by type. An unchanged list costs a field read and allocates nothing. A
    /// skater that's gone, or can't be read, leaves the watch.
    /// </summary>
    private void CheckComponentLists(long now)
    {
        if (now < _nextComponentCheck || _watched.Count == 0)
        {
            return;
        }

        _nextComponentCheck = now + ComponentCheckMicros;
        for (int i = _watched.Count - 1; i >= 0; i--)
        {
            WatchedList watched = _watched[i];
            try
            {
                // Unity's null: destroyed with part of the match.
                if (watched.Object == null)
                {
                    _watched.RemoveAt(i);
                    continue;
                }

                int version = watched.List._version;
                if (version == watched.Version)
                {
                    continue;
                }

                watched.Version = version;
                (ushort[] ids, uint[] types) = Components(watched.Object);
                ObjectEntry changed = watched.Entry with { Components = ids, ComponentTypes = types };
                if (ComponentAlignment.SameComponents(watched.Entry, changed))
                {
                    continue;
                }

                NoteComponentChange(watched.Entry, changed, watched.Object);
                watched.Entry = changed;
                _newEntries.Add(changed);
            }
            catch (Exception ex)
            {
                _watched.RemoveAt(i);
                if (_watchFailures++ == 0)
                {
                    ModLog.Warn($"Stream recorder: a skater's replay component list couldn't be read ({ModLog.SafeErrorText(ex)}); it's no longer watched.");
                }
            }
        }
    }

    /// <summary>
    /// Counts a skater's component list announced again; the first few logged with what it gained and lost, the skater by
    /// its asset id, never its object's name (<see cref="LogNames.ObjectName(UnityEngine.GameObject?)"/>).
    /// </summary>
    private void NoteComponentChange(ObjectEntry before, ObjectEntry after, ReplayObject skater)
    {
        if (_componentChanges++ >= ComponentChangesLogged)
        {
            return;
        }

        (List<uint> added, List<uint> removed) = ComponentAlignment.Difference(before.ComponentTypes, after.ComponentTypes);
        var parts = new List<string>();
        if (added.Count > 0)
        {
            parts.Add($"+ {string.Join(", ", added.ConvertAll(ReplayComponentTypes.NameOfHash))}");
        }

        if (removed.Count > 0)
        {
            parts.Add($"- {string.Join(", ", removed.ConvertAll(ReplayComponentTypes.NameOfHash))}");
        }

        string what = parts.Count > 0 ? string.Join("; ", parts) : "the same kinds, other ids or order";
        ModLog.Info($"Stream recorder: \"{LogNames.ObjectName(skater)}\"'s replay components changed during play ({before.Components?.Count ?? 0} to " +
            $"{after.Components?.Count ?? 0}: {what}); friends get its new list{(_componentChanges == ComponentChangesLogged ? " (further changes are counted)" : string.Empty)}.");
    }

    /// <summary>
    /// The object's components' replay ids and types, in its own order: the state bytes address components by their ids, so
    /// a friend's freshly loaded copy takes them over, each by its own type (a component one game added at run time and the
    /// other didn't leaves the rest matched, <see cref="ComponentAlignment"/>).
    /// </summary>
    private static (ushort[] Ids, uint[] Types) Components(ReplayObject replayObject)
    {
        var ids = new List<ushort>();
        var types = new List<uint>();
        foreach (ReplayBehaviour behaviour in Native.Il2CppLists.Items(replayObject.Behaviours))
        {
            if (behaviour != null && ids.Count < ObjectEntry.MaxComponents)
            {
                ids.Add((ushort)behaviour.ReplayIdentity.ID);
                types.Add(ReplayComponentTypes.Of(behaviour));
            }
        }

        return (ids.ToArray(), types.ToArray());
    }

    /// <summary>
    /// A skater's replay components, for the log: each kind of component list the match's skaters have, once a match (at
    /// most a few). Two games can give a skater different components (one added at run time on one PC only: a talent's
    /// effect from a relic's random pick); a friend's "driven without some of their replay components" line says which of
    /// its skaters, this one which components the host's have.
    /// </summary>
    private void NoteSkaterComponents(ReplayObject replayObject, string key)
    {
        if (!key.StartsWith("F:", StringComparison.Ordinal) || _skaterComponentLists.Count >= MaxSkaterComponentLists)
        {
            return;
        }

        try
        {
            var names = new List<string>();
            foreach (ReplayBehaviour behaviour in Native.Il2CppLists.Items(replayObject.Behaviours))
            {
                if (behaviour != null)
                {
                    names.Add(ReplayComponentTypes.NameOf(behaviour));
                }
            }

            string list = string.Join(", ", names);
            if (_skaterComponentLists.Add(list))
            {
                ModLog.Info($"Stream recorder: a skater's replay components this match ({LogNames.ObjectName(replayObject)}, {names.Count}): {list}" +
                    $"{(_skaterComponentLists.Count == MaxSkaterComponentLists ? " (further kinds aren't logged)" : string.Empty)}.");
            }
        }
        catch (Exception ex)
        {
            _skaterComponentLists.Add(ex.GetType().Name);
        }
    }

    /// <summary>The network id already given to the object with this host replay id, or 0.</summary>
    public ushort NetIdOf(int replayId) => _netIds.GetValueOrDefault(replayId);

    /// <summary>Writes object-table entries for objects given network ids since the last call (before anything refers to them).</summary>
    public void WriteNewObjects()
    {
        if (_newEntries.Count == 0)
        {
            return;
        }

        for (int i = 0; i < _newEntries.Count; i += ObjectTableMessage.MaxEntries)
        {
            List<ObjectEntry> part = _newEntries.GetRange(i, Math.Min(ObjectTableMessage.MaxEntries, _newEntries.Count - i));
            Write(MessageType.ObjectTable, Channel.Events, w => ObjectTableMessage.Write(w, part));
        }

        _newEntries.Clear();
    }

    /// <summary>Stops recording and closes the file (the network side stays the caller's: it ends the match there).</summary>
    public void Stop(string reason)
    {
        if (!Recording)
        {
            return;
        }

        if (_pending != null && _failure == null)
        {
            // Captured this frame, not rendered yet: it goes now rather than being lost.
            try
            {
                Send(_pending, CameraState.Read());
            }
            catch (Exception ex)
            {
                ModLog.Warn($"Stream recorder: the last snapshot couldn't be sent ({ModLog.SafeErrorText(ex)}).");
            }
        }

        _pending = null;
        _running = false;
        double seconds = Now / 1_000_000.0;
        long records = 0, bytes = 0;
        if (_writer != null)
        {
            try
            {
                records = _writer.Records;
                bytes = _writer.Bytes;
                _writer.Dispose();
            }
            catch (Exception ex) when (IsFileError(ex))
            {
                ModLog.Warn($"Stream recorder: the file couldn't be closed cleanly ({ModLog.SafeErrorText(ex)}); it may end early.");
            }
        }

        _writer = null;
        _network = null;
        _scene = null;
        _sceneSource = null;
        Paths.Clear();

        // Lets go of the watched skaters' objects and lists (the count stays for the line below).
        _watched.Clear();
        RenderHook.BeforeRender -= OnBeforeRender;
        string file = FilePath != null && records > 0 ? $" File: {DevOutput.Label(FilePath)}" : string.Empty; // a label, never the full path (D127)
        ModLog.Info(string.Create(CultureInfo.InvariantCulture,
            $"Stream recorder: stopped ({reason}). {seconds:0.0} s, {_snapshotSequence} snapshots ({_snapshotSequence / Math.Max(seconds, 0.001):0}/s, ") +
            $"{_netIds.Count} objects){(records > 0 ? $", {records} records, {bytes / 1024} KB; snapshots averaged {(_snapshotSequence == 0 ? 0 : _snapshotBytes / _snapshotSequence)} B in the file" : string.Empty)}" +
            $"{(_serializeFailures > 0 ? $"; {_serializeFailures} object states couldn't be read" : string.Empty)}" +
            $"{(_componentChanges > 0 ? $"; {_componentChanges} skaters' replay component lists announced again during play" : string.Empty)}.{file}");
        ModLog.Info($"Stream recorder: {ReplayStates.Describe(_poolAtStart)}.");
    }

    /// <summary>Flushes the file (a crash loses at most what came since); a file error closes it.</summary>
    private void FlushFile()
    {
        try
        {
            _writer?.Flush();
        }
        catch (Exception ex) when (IsFileError(ex))
        {
            CloseFailedFile(ex);
        }
    }

    /// <inheritdoc />
    public void Dispose() => Stop("disposed");

    /// <summary>
    /// Just before the frame renders (after Cinemachine): the snapshot captured this frame goes out with the pose that
    /// renders it. Inside the engine's render loop: never throws (a failure stops the recorder as in LateUpdate).
    /// </summary>
    private void OnBeforeRender()
    {
        if (!Recording || _pending == null || _failure != null)
        {
            return;
        }

        try
        {
            Send(_pending, CameraState.Read());
        }
        catch (Exception ex)
        {
            _pending = null;
            _failure = $"an error while recording ({ModLog.SafeErrorText(ex)})";
            ModLog.Error("Stream recorder: sending a snapshot failed; recording stops now", ex);
        }
    }

    /// <summary>A watched skater: its object, its runtime component list, its entry as last announced, and the list's change count then.</summary>
    private sealed class WatchedList
    {
        public WatchedList(ReplayObject replayObject, Il2CppSystem.Collections.Generic.List<ReplayBehaviour> list, ObjectEntry entry, int version)
        {
            Object = replayObject;
            List = list;
            Entry = entry;
            Version = version;
        }

        public ReplayObject Object { get; }

        public Il2CppSystem.Collections.Generic.List<ReplayBehaviour> List { get; }

        public ObjectEntry Entry { get; set; }

        public int Version { get; set; }
    }

    /// <summary>A file name no other recording has: by the second, then -2, -3... for more in the same second.</summary>
    private static string NewFilePath(string directory)
    {
        string stamp = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        string path = Path.Combine(directory, $"match-{stamp}.t2tstream");
        for (int n = 2; File.Exists(path) && n < 100; n++)
        {
            path = Path.Combine(directory, $"match-{stamp}-{n}.t2tstream");
        }

        return path;
    }

    /// <summary>
    /// Reads every object's state into this frame's snapshot (the object table goes out first); it's sent just before the
    /// frame renders (<see cref="Send"/>), or at once without the render hook.
    /// </summary>
    private void Capture(long now)
    {
        long start = CostMeter.Start();
        var frame = new SnapshotFrame { Sequence = ++_snapshotSequence, HostTimeMicros = now, TimeScale = Time.timeScale };
        Il2CppSystem.Collections.Generic.HashSet<ReplayObject>.Enumerator objects = _scene!.ActiveReplayObjects.GetEnumerator();
        while (objects.MoveNext())
        {
            ReplayObject replayObject = objects.Current;
            if (replayObject == null || frame.Objects.Count >= SnapshotFrame.MaxObjects)
            {
                continue;
            }

            try
            {
                byte[] state = ReplayStates.Serialize(replayObject);
                if (state.Length <= SnapshotFrame.MaxStateBytes)
                {
                    frame.Objects[NetIdOf(replayObject)] = state;
                }
            }
            catch (Exception ex)
            {
                if (_serializeFailures++ < 5)
                {
                    ModLog.Warn($"Stream recorder: could not read \"{LogNames.ObjectName(replayObject)}\": {ModLog.SafeErrorText(ex)}");
                }
            }
        }

        WriteNewObjects();
        _statesCost.AddSince(start);
        if (RenderHook.Installed)
        {
            _pending = frame;
        }
        else
        {
            Send(frame, CameraState.Read());
        }
    }

    /// <summary>The captured snapshot, with the camera <paramref name="pose"/>, to the file and to friends.</summary>
    private void Send(SnapshotFrame frame, CameraPose? pose)
    {
        _pending = null;
        frame.Camera = pose;
        long now = frame.HostTimeMicros;
        bool written = true;
        bool keyframe = _previous == null || now - _lastKeyframe >= KeyframeEveryMicros;
        if (keyframe)
        {
            _lastKeyframe = now;
        }

        if (_writer != null)
        {
            long start = CostMeter.Start();
            SnapshotFrame? baseFrame = keyframe ? null : _previous;
            long before = _writer.Bytes;
            written = WriteSnapshotRecord(frame, baseFrame);
            _snapshotBytes += _writer != null ? _writer.Bytes - before : 0;
            if (!written && _refusedSnapshots++ < 3)
            {
                ModLog.Warn($"Stream recorder: snapshot {frame.Sequence} can't be encoded (its time or size is out of range); left out of the file.");
            }

            _fileCost.AddSince(start);
        }

        // Each friend's copy is encoded against what that friend acknowledged.
        if (_network != null)
        {
            long start = CostMeter.Start();
            _network.Snapshot(frame);
            _friendsCost.AddSince(start);
        }

        if (written)
        {
            _previous = frame; // a refused frame is no base for the file's next delta
        }
    }
}
#endif
