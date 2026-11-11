#if HAS_INTEROP
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Il2CppInterop.Runtime;
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
/// Which local object each of a stream's objects is, for <see cref="StreamPlayer"/>:
/// <list type="bullet">
/// <item>every replay object in the loaded scenes is indexed by host-style replay id and by stable key
/// (<see cref="ObjectKeys"/>, cut as the wire cuts it: <see cref="BitWriter.CutShortString"/>), each object's key worked
/// out once per playback and each scene's roots listed once per pass. Objects sharing a key here each go to their own
/// host object, in the order both sides list them, and the shared keys are logged;</item>
/// <item>the same match maps by replay id, a freshly loaded one by key; an object not found is looked for again (the
/// scene is re-indexed at most once a second) for a while, as a live host's object can appear before ours has
/// spawned;</item>
/// <item>in a fresh match, an object the host's game made during play at the scene's root (an effect, a pool growing) is
/// made here from the game's own template the first time a snapshot shows it (the templates are found in one look over
/// the loaded objects per playback; never a root that carries gameplay or a singleton: a skater, goalie, puck, team or
/// manager), at most <see cref="MaxCopiesPerSecond"/> a second, <see cref="MaxCopies"/> in play at once and
/// <see cref="MaxCopiesPerTemplate"/> of one template. A copy the stream has hidden for
/// a while stands in for the next new host object of its template (taking that object's component ids), and one hidden
/// for long is destroyed, so copies don't pile up as the host's game makes and drops effects; a host object whose copy
/// went is made again if it shows again. A template object the host's game makes again under the key a copy here holds
/// (a ball and chain's ball at every faceoff, a dropped helmet, a fake puck) has new component ids, which the old copy
/// doesn't answer to, so it goes the same way, never onto the old copy (<see cref="SharedKeys"/>);</item>
/// <item>objects destroyed here during playback are dropped once a second, and looked for again.</item>
/// </list>
/// Visibility follows the stream through <see cref="Presence"/>.
/// </summary>
internal sealed class StreamObjects
{
    /// <summary>Most copies made from templates per second (the rest wait for a later snapshot that shows them).</summary>
    public const int MaxCopiesPerSecond = 16;

    /// <summary>
    /// Most copies in play at once (a match makes a handful: a rehearsal's table had 12 template objects); beyond
    /// it, new ones wait until hidden ones are put away, so a host can't fill a friend's game with copies.
    /// </summary>
    public const int MaxCopies = 64;

    /// <summary>Most copies of one template in play at once.</summary>
    public const int MaxCopiesPerTemplate = 16;

    private const string SingletonPrefix = "MonoSingleton";

    private const double MinMappedShare = 0.5;
    private const double SameMatchShare = 0.8;
    private const double UnmappedRetrySeconds = 10;
    private const double ReindexEverySeconds = 1;
    private const double MaintainEverySeconds = 1;
    private const double ReuseHiddenSeconds = 2;
    private const double DiscardHiddenSeconds = 30;

    /// <summary>
    /// Classes (or their bases) a template root mustn't carry: gameplay objects and managers, whose start-up registers
    /// itself where this PC's game reads it. Matched by IL2CPP class name; singletons by <see cref="SingletonPrefix"/>.
    /// </summary>
    private static readonly HashSet<string> RefusedTemplateClasses = new(StringComparer.Ordinal) { "Skater", "Goaltender", "Puck", "Team", "GameManager", "PlayersManager" };

    private readonly ReplaySession _session;
    private readonly Dictionary<ushort, ReplayObject> _objects = new();
    private readonly HashSet<ushort> _considered = new();
    private readonly Dictionary<ushort, double> _unmappedSince = new();
    private readonly Dictionary<int, ReplayObject> _byReplayId = new();
    private readonly Dictionary<string, ReplayObject> _byKey = new(StringComparer.Ordinal);
    private readonly HashSet<string> _duplicateKeys = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<ReplayObject>> _twins = new(StringComparer.Ordinal);
    private readonly HashSet<string> _loggedDuplicates = new(StringComparer.Ordinal);
    private readonly List<string> _refusedTemplates = new();
    private readonly List<string> _unmappedNames = new();
    private readonly Dictionary<ushort, (ObjectEntry Entry, GameObject Template)> _pendingSpawns = new();
    private readonly List<ushort> _due = new();
    private readonly List<Copy> _copies = new();
    private Dictionary<IntPtr, (ReplayObject Object, string Key)> _keys = new();
    private Dictionary<string, GameObject>? _prefabs;
    private Dictionary<string, GameObject>? _liveTemplates;
    private ReplayScene? _scene;
    private double _lastIndex;
    private double _lastMaintain;
    private double _copyWindow = double.MinValue;
    private int _copiesInWindow;
    private int _made;
    private int _reused;
    private int _discarded;
    private int _deferred;
    private int _spawnFailures;
    private int _pruned;
    private int _capped;
    private int _merged;
    private int _remade;
    private int _undriven;
    private int _partlyLogged;

    // Objects taking the host's component ids over again (Retake): a frame after a follower changed them here, or as the
    // host announces a changed list; at most so many a frame (the rest the next). The first few are logged, the rest counted.
    private const int RetakesLogged = 5;
    private const int RetakesPerFrame = 16;
    private readonly RetakeSchedule _retake = new();
    private readonly List<(ushort NetId, RetakeReasons Reasons)> _retakesDue = new();
    private int _retakesLogged;
    private int _laterRetakes;
    private int _hostChanges;

    public StreamObjects(ReplaySession session) => _session = session;

    /// <summary>The live map from network id to local object (it grows while playing; the channels look ids up in it).</summary>
    public IReadOnlyDictionary<ushort, ReplayObject> Objects => _objects;

    /// <summary>Visibility of the mapped objects.</summary>
    public StreamPresence Presence { get; } = new();

    /// <summary>True when objects are found by replay id (the recording's own match), false by stable key (a fresh one).</summary>
    public bool SameMatch { get; set; }

    /// <summary>The stream's objects dealt with so far (mapped, waiting to be made, or given up on).</summary>
    public int Considered => _considered.Count;

    /// <summary>
    /// True once objects joined or changed hands since the player last loaded a whole snapshot into them: the next load
    /// must include the interval's start.
    /// </summary>
    public bool Joined { get; set; }

    /// <summary>Stream objects not found here.</summary>
    public int Unmapped { get; private set; }

    /// <summary>Stream objects found here that couldn't be driven.</summary>
    public int Undrivable { get; private set; }

    /// <summary>Forgets the last playback and indexes the loaded scenes (<paramref name="scene"/>'s active objects first).</summary>
    public void Prepare(ReplayScene? scene)
    {
        Clear();
        _scene = scene;
        IndexScene();
    }

    /// <summary>Forgets everything, letting go of every object (playback over; the copies are destroyed first).</summary>
    public void Clear()
    {
        _objects.Clear();
        _considered.Clear();
        _unmappedSince.Clear();
        _byReplayId.Clear();
        _byKey.Clear();
        _duplicateKeys.Clear();
        _twins.Clear();
        _loggedDuplicates.Clear();
        _refusedTemplates.Clear();
        _keys = new Dictionary<IntPtr, (ReplayObject Object, string Key)>();
        _unmappedNames.Clear();
        _pendingSpawns.Clear();
        _copies.Clear();
        _prefabs = _liveTemplates = null;
        _scene = null;
        Presence.Clear();
        Joined = false;
        Unmapped = Undrivable = 0;
        _made = _reused = _discarded = _deferred = _spawnFailures = _pruned = _capped = _merged = _remade = _undriven = _partlyLogged = 0;
        _retake.Clear();
        _retakesLogged = _laterRetakes = _hostChanges = 0;
        _copiesInWindow = 0;
        _copyWindow = _lastMaintain = double.MinValue;
    }

    /// <summary>True when most keyed objects are here under the recorded replay id with the same key: this is the recording's own match.</summary>
    public bool LooksLikeSameMatch(IStreamSource source)
    {
        int keyed = 0, same = 0;
        foreach (ObjectEntry entry in source.Objects.Values.Where(e => e.Key.Length > 0))
        {
            keyed++;
            same += _byReplayId.TryGetValue(entry.HostReplayId, out ReplayObject? local) && KeyOf(local) == entry.Key ? 1 : 0;
        }

        return keyed > 0 && same >= keyed * SameMatchShare;
    }

    /// <summary>A recording's whole table is known up front: refuse a match that has too few of its objects.</summary>
    public bool Mappable(IStreamSource source, out string problem)
    {
        int wanted = source.Objects.Count;
        List<ObjectEntry> missing = source.Objects.Values.Where(e => Find(e) == null).ToList();
        int spawnable = SameMatch ? 0 : missing.Count(e => Template(e) != null);
        int found = wanted - missing.Count + spawnable;
        int keyed = source.Objects.Values.Count(e => e.Key.Length > 0);
        int byKey = source.Objects.Values.Count(e => e.Key.Length > 0 && _byKey.ContainsKey(e.Key));
        IEnumerable<string> lost = missing.Where(e => SameMatch || Template(e) == null).Select(e => $"{e.Name} ({(e.Key.Length > 0 ? e.Key : "no key")})");
        ModLog.Info($"Stream player: stable keys: {keyed} of {wanted} recorded objects have one, {byKey} found here by key" +
            $"{(_duplicateKeys.Count > 0 ? $"; keys shared by several objects here: {string.Join(", ", _duplicateKeys.Take(8))}" : string.Empty)}; " +
            $"{wanted - missing.Count} found {(SameMatch ? "by replay id (same match)" : "by key (fresh match)")}" +
            $"{(spawnable > 0 ? $", {spawnable} more to be made here from the game's templates when they first show" : string.Empty)}" +
            $"{(found < wanted ? $"; not here: {string.Join(", ", lost.Take(8))}{(wanted - found > 8 ? ", ..." : string.Empty)}" : string.Empty)}.");
        if (wanted == 0 || found < wanted * MinMappedShare)
        {
            problem = $"only {found} of the stream's {wanted} objects are in this match" +
                (SameMatch ? "; record in this match first" : "; the match doesn't have the recording's teams or arena");
            return false;
        }

        problem = string.Empty;
        return true;
    }

    /// <summary>
    /// Brings the stream's objects not seen yet into playback (<paramref name="showing"/>: playback has shown a frame, so
    /// say when more join). One not found here is looked for again (the scene is re-indexed at most once a second) for a
    /// while: a live host's object can appear before ours has spawned.
    /// </summary>
    public void MapNew(IStreamSource source, bool showing)
    {
        double now = FrameClock.Seconds;
        var batch = new List<(ReplayObject Object, HostComponents? Host)>();
        var batchIds = new List<(ushort NetId, ReplayObject Object)>();
        HashSet<IntPtr>? mapped = null;
        bool reindexed = false;
        foreach (ObjectEntry entry in source.Objects.Values)
        {
            if (_considered.Contains(entry.NetId))
            {
                continue;
            }

            ReplayObject? local = Find(entry);
            if (local == null && !reindexed && now - _lastIndex >= ReindexEverySeconds)
            {
                IndexScene();
                reindexed = true;
                local = Find(entry);
            }

            if (local == null && !SameMatch && Template(entry) is GameObject template)
            {
                // Made here the first time a snapshot shows it (SpawnShown).
                _considered.Add(entry.NetId);
                _unmappedSince.Remove(entry.NetId);
                _pendingSpawns[entry.NetId] = (entry, template);
                continue;
            }

            if (local == null)
            {
                double since = _unmappedSince.TryGetValue(entry.NetId, out double first) ? first : _unmappedSince[entry.NetId] = now;
                if (!source.Live || now - since >= UnmappedRetrySeconds)
                {
                    _considered.Add(entry.NetId);
                    Unmapped++;
                    if (source.Live && _unmappedNames.Count < 8)
                    {
                        // A few by name as they come; the count is in the playout report.
                        _unmappedNames.Add(entry.Name);
                        ModLog.Warn($"Stream player: the host's \"{NameText(entry)}\" ({KeyText(entry)}) isn't in this match; it won't be shown" +
                            $"{(_unmappedNames.Count == 8 ? " (further ones are only counted)" : string.Empty)}.");
                    }
                }

                continue;
            }

            _considered.Add(entry.NetId);
            _unmappedSince.Remove(entry.NetId);
            mapped ??= new HashSet<IntPtr>(_objects.Values.Select(o => o.Pointer));
            HashSet<IntPtr> driven = mapped;
            bool Taken(ReplayObject candidate) => driven.Contains(candidate.Pointer) || batchIds.Any(b => b.Object.Pointer == candidate.Pointer);
            if (Taken(local) && !SameMatch && _twins.TryGetValue(entry.Key, out List<ReplayObject>? twins))
            {
                // Several objects here share this key (two skaters from one asset on one side, say): the next host object
                // with it gets the next one, never one another host object already drives.
                local = twins.FirstOrDefault(twin => twin != null && !Taken(twin)) ?? local;
            }

            if (Taken(local))
            {
                // Two host objects resolve to one object here: the host made one again under a new id (it announces each
                // replay id once), or they share a key this match has fewer objects with. A state names its components by
                // id, and the game skips the ones an object doesn't have (ReplayObject.DeserializeReplayComponents, no
                // error), so only a host object with the ids this one answers to can drive it (Core.Stream.SharedKeys).
                Copy? copy = SameMatch ? null : _copies.Find(c => c.Object != null && c.Object.Pointer == local.Pointer);
                IReadOnlyList<ushort>? answers = SameMatch ? null : IdsDriving(local, batch);
                switch (SharedKeys.Decide(SameMatch, copy != null, entry.Components, answers))
                {
                    case SharedKeyStep.NewCopy:
                        // The host made a template object again (a ball at each faceoff, a dropped helmet): a copy of its
                        // own when a snapshot shows it, a hidden one of the same template or a new one (SpawnShown). The
                        // copy standing for the old one hides with it, as the stream stops showing the old id.
                        _pendingSpawns[entry.NetId] = (entry, copy!.Template);
                        if (_remade++ < 3)
                        {
                            ModLog.Info($"Stream player: the host made \"{NameText(entry)}\" ({KeyText(entry)}) again, with new component ids; it gets a copy of its own " +
                                $"(a hidden one of the same template, or a new one) and the copy of the old one hides with it{(_remade == 3 ? " (further ones are only counted)" : string.Empty)}.");
                        }

                        continue;
                    case SharedKeyStep.ShareUndriven:
                        if (_undriven++ == 0)
                        {
                            ModLog.Warn($"Stream player: the host's \"{NameText(entry)}\" ({KeyText(entry)}) finds an object here that another of the host's drives, " +
                                "with other component ids: its states can't move that object (the game skips the components a state names that it can't find), " +
                                "so it shows here only as the other left it. Further ones are counted.");
                        }

                        break;
                    default:
                        if (_merged++ < 5)
                        {
                            ModLog.Info($"Stream player: the host's \"{NameText(entry)}\" ({KeyText(entry)}) is the same object here as another of the host's, " +
                                $"with the same component ids; both drive it{(_merged == 5 ? " (further ones are only counted)" : string.Empty)}.");
                        }

                        break;
                }

                _objects[entry.NetId] = local;
                Presence.Add(entry.NetId, local);
                continue;
            }

            batch.Add((local, SameMatch ? null : HostComponents.Of(entry)));
            batchIds.Add((entry.NetId, local));
        }

        LogSharedKeys(source.Live);
        if (batch.Count == 0)
        {
            return;
        }

        List<ReplayObject> failed = _session.Add(batch, out string problem, out string partly);
        var failedPointers = new HashSet<IntPtr>(failed.Select(f => f.Pointer));
        foreach ((ushort netId, ReplayObject replayObject) in batchIds)
        {
            if (failedPointers.Contains(replayObject.Pointer))
            {
                Undrivable++;
                continue;
            }

            _objects[netId] = replayObject;
            Presence.Add(netId, replayObject);
        }

        Joined |= batch.Count > failed.Count;
        if (failed.Count > 0)
        {
            ModLog.Warn($"Stream player: {failed.Count} objects can't be driven and stay as they are: {problem}.");
        }

        LogPartly(partly);

        if (showing)
        {
            ModLog.Info($"Stream player: {batch.Count - failed.Count} more objects joined playback ({_objects.Count} in all).");
        }
    }

    /// <summary>
    /// Makes the pending objects <paramref name="from"/> shows and brings them into playback: a copy hidden for a while
    /// takes the new object over, else a new one is made from its template (within the per-second budget). Made active at
    /// once (their own start-up runs as on the host when they appeared there); the recorded state is loaded into them in
    /// the same frame, before anything is drawn (<see cref="Joined"/>).
    /// </summary>
    public void SpawnShown(SnapshotFrame from)
    {
        if (_pendingSpawns.Count == 0)
        {
            return;
        }

        _due.Clear();
        foreach (KeyValuePair<ushort, (ObjectEntry Entry, GameObject Template)> pending in _pendingSpawns)
        {
            if (from.Objects.ContainsKey(pending.Key))
            {
                _due.Add(pending.Key);
            }
        }

        if (_due.Count == 0)
        {
            return;
        }

        double now = FrameClock.Seconds;
        var made = new List<(ushort NetId, ObjectEntry Entry, ReplayObject Object, GameObject Template)>();
        int reused = 0;
        foreach (ushort netId in _due)
        {
            (ObjectEntry entry, GameObject template) = _pendingSpawns[netId];
            if (TryReuse(netId, entry, template, from, now))
            {
                _pendingSpawns.Remove(netId);
                reused++;
                continue;
            }

            if (_copies.Count + made.Count >= MaxCopies ||
                _copies.Count(c => c.Template.Pointer == template.Pointer) + made.Count(m => m.Template.Pointer == template.Pointer) >= MaxCopiesPerTemplate)
            {
                // Stays pending: made once copies hidden for long have been put away.
                if (_capped++ == 0)
                {
                    ModLog.Warn($"Stream player: {_copies.Count + made.Count} copies made for the stream are in play (at most {MaxCopies}, {MaxCopiesPerTemplate} of one template); " +
                        $"\"{NameText(entry)}\" and further ones wait until some are put away (counted).");
                }

                continue;
            }

            if (!TakeCopyBudget(now))
            {
                // Stays pending: made at a later interval that shows it.
                _deferred++;
                continue;
            }

            _pendingSpawns.Remove(netId);
            if (MakeCopy(entry, template) is ReplayObject replayObject)
            {
                made.Add((netId, entry, replayObject, template));
            }
        }

        Joined |= reused > 0;
        if (made.Count == 0)
        {
            return;
        }

        List<ReplayObject> failed = _session.Add(made.Select(m => (m.Object, HostComponents.Of(m.Entry))), out string problem, out string partly);
        LogPartly(partly);
        var failedPointers = new HashSet<IntPtr>(failed.Select(f => f.Pointer));
        int joined = 0;
        foreach ((ushort netId, ObjectEntry entry, ReplayObject replayObject, GameObject template) in made)
        {
            if (failedPointers.Contains(replayObject.Pointer))
            {
                _spawnFailures++;
                DestroyQuietly(replayObject.gameObject);
                continue;
            }

            _objects[netId] = replayObject;
            Presence.Add(netId, replayObject);
            _copies.Add(new Copy(netId, entry, replayObject, template));
            joined++;
        }

        Joined |= joined > 0;
        _made += joined;
        if (failed.Count > 0)
        {
            ModLog.Warn($"Stream player: {failed.Count} objects made here can't be driven: {problem}.");
        }

        if (_made <= joined || _made / 10 != (_made - joined) / 10)
        {
            ModLog.Info($"Stream player: made {joined} object(s) the host's game made during play ({string.Join(", ", made.Select(m => NameText(m.Entry)).Distinct().Take(5))}); " +
                $"{_made} made so far, {_reused} times a hidden copy stood in for a new one, {_copies.Count} copies now.");
        }
    }

    /// <summary>
    /// Once a second: drops objects destroyed here (looked for again), and destroys copies the stream has hidden for long
    /// (their host objects are made again if they show again).
    /// </summary>
    public void Maintain()
    {
        double now = FrameClock.Seconds;
        if (now - _lastMaintain < MaintainEverySeconds)
        {
            return;
        }

        _lastMaintain = now;
        List<ushort>? gone = null;
        foreach ((ushort netId, ReplayObject replayObject) in _objects)
        {
            // Unity's null: destroyed here (part of the match unloaded, or the game destroyed it).
            if (replayObject == null)
            {
                (gone ??= new List<ushort>()).Add(netId);
            }
        }

        if (gone != null)
        {
            foreach (ushort netId in gone)
            {
                _objects.Remove(netId);
                _considered.Remove(netId);
            }

            int sessionPruned = _session.Prune();
            Presence.Prune();
            _copies.RemoveAll(c => c.Object == null);
            if (_pruned == 0)
            {
                ModLog.Info($"Stream player: {gone.Count} objects were destroyed here during playback ({sessionPruned} were driven); dropped and looked for again (further ones are counted).");
            }

            _pruned += gone.Count;
        }

        for (int i = _copies.Count - 1; i >= 0; i--)
        {
            Copy copy = _copies[i];
            if (Presence.HiddenFor(copy.GameObjectPointer, now) < DiscardHiddenSeconds)
            {
                continue;
            }

            _copies.RemoveAt(i);
            _session.Remove(copy.Object);
            _objects.Remove(copy.NetId);
            Presence.Forget(copy.GameObjectPointer);
            _pendingSpawns[copy.NetId] = (copy.Entry, copy.Template);
            DestroyQuietly(copy.GameObject);
            _discarded++;
        }
    }

    /// <summary>Copies made for the stream go when playback ends (the match is the game's own again).</summary>
    public void DestroySpawned()
    {
        foreach (Copy copy in _copies)
        {
            DestroyQuietly(copy.GameObject);
        }

        if (_made > 0 || _spawnFailures > 0 || _deferred > 0 || _capped > 0 || _undriven > 0)
        {
            ModLog.Info($"Stream player: {_made} objects made for the stream ({_copies.Count} put away now; {_reused} times a hidden copy stood in for a new one, " +
                $"{_discarded} destroyed after {DiscardHiddenSeconds:0} s hidden){(_spawnFailures > 0 ? $"; {_spawnFailures} couldn't be made or driven" : string.Empty)}" +
                $"{(_deferred > 0 ? $"; {_deferred} waited for the budget of {MaxCopiesPerSecond} a second" : string.Empty)}" +
                $"{(_capped > 0 ? $"; {_capped} times one waited for room under {MaxCopies} in play ({MaxCopiesPerTemplate} of one template)" : string.Empty)}" +
                $"{(_merged > 0 ? $"; {_merged} of the host's objects shared an object here with another (same component ids)" : string.Empty)}" +
                $"{(_remade > 0 ? $"; {_remade} times the host made a template object again (each got a copy of its own)" : string.Empty)}" +
                $"{(_undriven > 0 ? $"; {_undriven} of the host's objects found an object here with other component ids and couldn't move it" : string.Empty)}" +
                $"{(_pendingSpawns.Count > 0 ? $"; {_pendingSpawns.Count} not showing (never shown, or put away and not shown again)" : string.Empty)}" +
                $"{(_pruned > 0 ? $"; {_pruned} objects were destroyed here during playback" : string.Empty)}.");
        }
        else if (_pruned > 0)
        {
            ModLog.Info($"Stream player: {_pruned} objects were destroyed here during playback.");
        }

        _copies.Clear();
        _pendingSpawns.Clear();
    }

    /// <summary>
    /// Every replay object in the loaded scenes: the replay scene's active ones, and inactive ones too (a pool keeps its
    /// thrown sticks switched off between throws, out of the replay scene).
    /// </summary>
    private static IEnumerable<ReplayObject> SceneReplayObjects(ReplayScene? scene)
    {
        var seen = new HashSet<IntPtr>();
        if (scene != null)
        {
            Il2CppSystem.Collections.Generic.HashSet<ReplayObject>.Enumerator live = scene.ActiveReplayObjects.GetEnumerator();
            while (live.MoveNext())
            {
                ReplayObject replayObject = live.Current;
                if (replayObject != null && seen.Add(replayObject.Pointer))
                {
                    yield return replayObject;
                }
            }
        }

        // The non-generic overload: it's in this build for certain, a generic instance may not be.
        foreach (UnityEngine.Object found in Resources.FindObjectsOfTypeAll(Il2CppType.Of<ReplayObject>()))
        {
            // Assets (prefabs) aren't in a scene.
            ReplayObject? replayObject = found?.TryCast<ReplayObject>();
            if (replayObject != null && replayObject.gameObject.scene.IsValid() && seen.Add(replayObject.Pointer))
            {
                yield return replayObject;
            }
        }
    }

    private static void DestroyQuietly(GameObject? gameObject)
    {
        try
        {
            if (gameObject != null)
            {
                UnityEngine.Object.Destroy(gameObject);
            }
        }
        catch (Exception ex)
        {
            ModLog.Warn($"Stream player: couldn't put away an object made for the stream: {ModLog.SafeErrorText(ex)}");
        }
    }

    /// <summary>Every replay object in the loaded scenes, by host-style replay id and by stable key.</summary>
    private void IndexScene()
    {
        _byReplayId.Clear();
        _byKey.Clear();
        _duplicateKeys.Clear();
        _twins.Clear();
        var roots = new RootOrdinals();
        var keys = new Dictionary<IntPtr, (ReplayObject Object, string Key)>(_keys.Count);
        foreach (ReplayObject replayObject in SceneReplayObjects(_scene))
        {
            _byReplayId.TryAdd(replayObject.ReplayIdentity.ID, replayObject);
            string key = _keys.TryGetValue(replayObject.Pointer, out (ReplayObject Object, string Key) known) ? known.Key : LocalKey(replayObject, roots);
            if (key.Length == 0)
            {
                continue;
            }

            // Kept with the object itself, so its address can't be another object's while it's here.
            keys[replayObject.Pointer] = (replayObject, key);
            if (!_byKey.TryAdd(key, replayObject) && _byKey[key].Pointer != replayObject.Pointer)
            {
                _duplicateKeys.Add(key);
                if (!_twins.TryGetValue(key, out List<ReplayObject>? twins))
                {
                    twins = new List<ReplayObject> { _byKey[key] };
                    _twins[key] = twins;
                }

                twins.Add(replayObject);
            }
        }

        _keys = keys;
        _lastIndex = FrameClock.Seconds;
    }

    /// <summary>
    /// The object's key as a host's object table would carry it: cut to the wire's 255 bytes (a longer scene path would
    /// otherwise never match its host object).
    /// </summary>
    private static string LocalKey(ReplayObject replayObject, RootOrdinals? roots = null) => BitWriter.CutShortString(ObjectKeys.KeyOf(replayObject, roots));

    private string KeyOf(ReplayObject replayObject) =>
        _keys.TryGetValue(replayObject.Pointer, out (ReplayObject Object, string Key) known) ? known.Key : LocalKey(replayObject);

    /// <summary>
    /// The host component ids <paramref name="local"/> answers to: the ones it takes in this pass's <paramref name="batch"/>,
    /// else the ones it holds in the session (null when it's in neither).
    /// </summary>
    private IReadOnlyList<ushort>? IdsDriving(ReplayObject local, List<(ReplayObject Object, HostComponents? Host)> batch)
    {
        foreach ((ReplayObject candidate, HostComponents? host) in batch)
        {
            if (candidate.Pointer == local.Pointer)
            {
                return host?.Ids;
            }
        }

        return _session.IdsOf(local);
    }

    /// <summary>
    /// Objects that joined playback without some of their components (this PC's game gave the object other replay
    /// components than the host's: one added at run time on one PC only, a talent's effect from a relic's random pick):
    /// the components of a type both have are driven, the rest stay as they are. The first few logged per playback.
    /// </summary>
    private void LogPartly(string partly)
    {
        if (partly.Length == 0 || _partlyLogged >= 3)
        {
            return;
        }

        _partlyLogged++;
        ModLog.Warn($"Stream player: objects driven without some of their replay components (their lists differ from the host's; " +
            $"each component of a type both have is driven): {partly}{(_partlyLogged == 3 ? " (further ones are counted)" : string.Empty)}.");
    }

    /// <summary>
    /// The local object <paramref name="gameObject"/> (a skater whose talents this PC's game just changed to follow the
    /// host's) takes its host object's component ids again by type: what joined drives, what left is dropped. A component
    /// the game destroyed this frame is still listed until the frame's end, so the take-over runs again a frame later
    /// (<see cref="Retake"/>). Null when it isn't a mapped object; else what came of it, for the log.
    /// </summary>
    public string? Realign(GameObject gameObject, IStreamSource source)
    {
        foreach (KeyValuePair<ushort, ReplayObject> pair in _objects)
        {
            ReplayObject local = pair.Value;
            if (local == null || local.gameObject == null || local.gameObject.Pointer != gameObject.Pointer ||
                !source.Objects.TryGetValue(pair.Key, out ObjectEntry entry))
            {
                continue;
            }

            // A refusal changes nothing: the object keeps the ids it had, and what drove before still does.
            Joined = true;
            _retake.Request(pair.Key, FrameClock.Frame + 1, RetakeReasons.ChangedHere);
            return _session.Realign(local, HostComponents.Of(entry), out string outcome, out _) ? outcome : $"its ids left as they were: {outcome}";
        }

        return null;
    }

    /// <summary>
    /// Each frame of playback: objects due to take the host's component ids over again do (<see cref="ReplaySession.Realign"/>):
    /// one the host announced again with another component list (its game added or removed a skater's component during
    /// play; <see cref="IStreamSource.TryTakeChangedObject"/>), at once, and one a follower changed here, a frame later, once
    /// what the game destroyed has gone. A host's change is logged (it proves the list arrived); a later take-over only
    /// when it changed something (the follower's own line said what it did first). Nothing allocated while none waits.
    /// Never throws: an object that fails is left as it was. One the stream hasn't mapped yet maps from its newest entry.
    /// </summary>
    public void Retake(IStreamSource source)
    {
        while (source.TryTakeChangedObject(out ushort changedId))
        {
            _retake.Request(changedId, FrameClock.Frame, RetakeReasons.HostChanged);
        }

        if (_retake.Count == 0)
        {
            return;
        }

        _retake.TakeDue(FrameClock.Frame, _retakesDue, RetakesPerFrame);
        foreach ((ushort netId, RetakeReasons reasons) in _retakesDue)
        {
            try
            {
                if (!_objects.TryGetValue(netId, out ReplayObject? local) || local == null || !source.Objects.TryGetValue(netId, out ObjectEntry entry))
                {
                    continue;
                }

                bool taken = _session.Realign(local, HostComponents.Of(entry), out string outcome, out bool changed);
                bool fromHost = (reasons & RetakeReasons.HostChanged) != 0;
                Joined |= changed;
                _hostChanges += fromHost ? 1 : 0;
                _laterRetakes += !fromHost && changed ? 1 : 0;
                if ((fromHost || changed) && _retakesLogged < RetakesLogged)
                {
                    _retakesLogged++;
                    string result = taken ? outcome : $"its ids left as they were: {outcome}";
                    string further = _retakesLogged == RetakesLogged ? " (further ones are counted)" : string.Empty;
                    ModLog.Info(fromHost
                        ? $"Stream player: the host announced \"{LogNames.ObjectName(local)}\"'s replay components again ({entry.Components?.Count ?? 0} now: its game " +
                            $"changed them during play); this PC's taken over again{(changed ? string.Empty : ", unchanged")}: {result}{further}."
                        : $"Stream player: \"{LogNames.ObjectName(local)}\"'s replay components taken over again a frame after this PC's game changed them ({result}){further}.");
                }
            }
            catch (Exception ex)
            {
                if (_retakesLogged++ < RetakesLogged)
                {
                    ModLog.Warn($"Stream player: taking over an object's replay components again failed ({ModLog.SafeErrorText(ex)}); it stays as it was.");
                }
            }
        }
    }

    /// <summary>For the playout summary: the host's component lists taken over during play and the later take-overs that changed something, or empty.</summary>
    public string RetakeSummary =>
        _hostChanges == 0 && _laterRetakes == 0 && _retake.Dropped == 0 ? string.Empty
        : $"; replay components taken over again: {_hostChanges} as the host announced a changed list, {_laterRetakes} a frame after this PC's game changed them" +
            $"{(_retake.Dropped > 0 ? $" ({_retake.Dropped} requests over the limit dropped)" : string.Empty)}";

    /// <summary>Logs keys several objects here share, once each per playback (a live host's are only seen here).</summary>
    private void LogSharedKeys(bool live)
    {
        if (!live || _duplicateKeys.Count == _loggedDuplicates.Count || _loggedDuplicates.Count >= 32)
        {
            return;
        }

        List<string> fresh = _duplicateKeys.Where(_loggedDuplicates.Add).Take(8).ToList();
        if (fresh.Count > 0)
        {
            ModLog.Info($"Stream player: keys several objects here share: {string.Join(", ", fresh.Select(k => $"{k} ×{(_twins.TryGetValue(k, out List<ReplayObject>? twins) ? twins.Count : 2)}"))}; " +
                "each of the host's objects with one gets its own (in the order both sides list them).");
        }
    }

    /// <summary>
    /// Once a few seconds into playback: replay objects shown in this match that no stream entry drives. Client mode holds
    /// their logic, so they stand where they are for the whole match (anything active here but not on the host, or that a
    /// setup difference added). Listed, not hidden: which they are decides whether hiding them is right. Idle sticks of the
    /// game's stick pool are counted apart: the pool keeps every stick active with its renderer off, out of the record
    /// scene, on the host and here alike (<c>StickPool.DeactivateStick</c>), so they're never in a stream and draw nothing
    /// (in every match checked, the list was exactly the pool's 120). Of the rest, only
    /// what draws something (an enabled renderer under it) is named, so a real one stands out.
    /// </summary>
    public void ReportUnnamed()
    {
        try
        {
            var driven = new HashSet<IntPtr>(_objects.Values.Where(o => o != null).Select(o => o.Pointer));
            var drawn = new List<string>();
            int idleSticks = 0, drawnCount = 0, notDrawn = 0;
            foreach (ReplayObject replayObject in SceneReplayObjects(_scene))
            {
                GameObject gameObject = replayObject.gameObject;
                if (driven.Contains(replayObject.Pointer) || !gameObject.activeInHierarchy)
                {
                    continue;
                }

                if (IsIdlePoolStick(gameObject))
                {
                    idleSticks++;
                    continue;
                }

                if (!Draws(gameObject))
                {
                    notDrawn++;
                    continue;
                }

                drawnCount++;
                if (drawn.Count < 8)
                {
                    string key = KeyOf(replayObject);
                    drawn.Add($"{LogNames.ObjectName(gameObject)} ({(key.Length > 0 ? LogNames.ReferenceText(key) : "no key")})");
                }
            }

            int count = idleSticks + drawnCount + notDrawn;
            if (count == 0)
            {
                ModLog.Info($"Stream player: every replay object shown in this match is driven by the stream ({driven.Count}).");
                return;
            }

            var parts = new List<string>();
            if (idleSticks > 0)
            {
                parts.Add($"{idleSticks} idle in the stick pool (not drawn here or on the host)");
            }

            if (notDrawn > 0)
            {
                parts.Add($"{notDrawn} that draw nothing here (no renderer on)");
            }

            parts.Add(drawnCount == 0 ? "nothing drawn"
                : $"{drawnCount} drawn, standing as they are: {string.Join(", ", drawn)}{(drawnCount > drawn.Count ? ", ..." : string.Empty)}");
            ModLog.Info($"Stream player: {count} replay objects here aren't in the stream: {string.Join("; ", parts)}.");
        }
        catch (Exception ex)
        {
            ModLog.Warn($"Stream player: couldn't list this match's objects the stream doesn't drive: {ModLog.SafeErrorText(ex)}");
        }
    }

    /// <summary>
    /// Builds the index of the game's templates for objects the host's game makes during play now, at playback start (a
    /// fresh match only): built at the first such object it was a look over every loaded object (about 11,000) inside a
    /// live frame, an 11-17 ms hitch on a friend mid-match (seen as "receive" spikes in the logs).
    /// </summary>
    public void PrepareTemplates()
    {
        if (!SameMatch && (_prefabs == null || _liveTemplates == null))
        {
            try
            {
                IndexTemplates();
            }
            catch (Exception ex)
            {
                // Not worth a friend's match: the first object that needs a template asks again.
                _prefabs = _liveTemplates = null;
                ModLog.Warn($"Stream player: indexing the game's templates at the start failed ({ModLog.SafeErrorText(ex)}); done at the first object that needs one.");
            }
        }
    }

    /// <summary>True for a stick of the game's pool, whole or broken, whose renderers are off (the pool's idle state).</summary>
    private static bool IsIdlePoolStick(GameObject gameObject)
    {
        if (gameObject.GetComponent<Il2CppPools.Stick>() is Il2CppPools.Stick stick && stick != null)
        {
            return stick._MeshRenderer is MeshRenderer mesh && mesh != null && !mesh.enabled;
        }

        return gameObject.GetComponent<Il2CppPools.BrokenStick>() is Il2CppPools.BrokenStick broken && broken != null &&
            broken._MeshRendererPart1 is MeshRenderer part && part != null && !part.enabled;
    }

    /// <summary>True when the object or a child of it (active ones) has a renderer switched on.</summary>
    private static bool Draws(GameObject gameObject)
    {
        foreach (Component found in gameObject.GetComponentsInChildren(Il2CppType.Of<Renderer>(), false))
        {
            if (found?.TryCast<Renderer>() is Renderer renderer && renderer.enabled)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// An entry's key for a log line (it's the host's text; a scene path through a skater's object holds the skater's
    /// name, which is written as its shape: <see cref="LogNames.ReferenceText"/>).
    /// </summary>
    private static string KeyText(ObjectEntry entry) => entry.Key.Length > 0 ? SafeText.Clean(LogNames.ReferenceText(entry.Key), 120) : "no key";

    /// <summary>
    /// An entry's name for a log line: the host's name for the object, but for a skater's its key's asset id (the game
    /// names a skater's object after the skater, and a custom skater's name is typed by its player:
    /// <see cref="ObjectEntryLogText.OfEntry"/>).
    /// </summary>
    private static string NameText(ObjectEntry entry) => ObjectEntryLogText.OfEntry(entry.Key, entry.Name);

    /// <summary>The local object for <paramref name="entry"/>, or null (also when the one indexed was destroyed since).</summary>
    private ReplayObject? Find(ObjectEntry entry)
    {
        ReplayObject? found = SameMatch ? _byReplayId.GetValueOrDefault(entry.HostReplayId) : entry.Key.Length > 0 ? _byKey.GetValueOrDefault(entry.Key) : null;
        return found != null ? found : null;
    }

    /// <summary>
    /// The game's template for an object the host's game made during play at the scene's root ("go:&lt;scene&gt;/&lt;name&gt;#n":
    /// effects, pools growing), found by name, without "(Clone)": a prefab asset with a replay object, else a live one in
    /// the scene. Null for anything else (it can't be told apart from a missing scene object).
    /// </summary>
    private GameObject? Template(ObjectEntry entry)
    {
        string key = entry.Key;
        if (!key.StartsWith(ObjectRefs.ScenePrefix, StringComparison.Ordinal))
        {
            return null;
        }

        int slash = key.IndexOf('/', ObjectRefs.ScenePrefix.Length);
        if (slash < 0 || key.IndexOf('/', slash + 1) >= 0)
        {
            return null;
        }

        int hash = key.LastIndexOf('#');
        string name = hash > slash ? key[(slash + 1)..hash] : key[(slash + 1)..];
        string baseName = name.EndsWith("(Clone)", StringComparison.Ordinal) ? name[..^"(Clone)".Length].TrimEnd() : name;
        if (_prefabs == null || _liveTemplates == null)
        {
            IndexTemplates();
        }

        return _prefabs!.GetValueOrDefault(baseName) ?? _prefabs!.GetValueOrDefault(name) ??
            _liveTemplates!.GetValueOrDefault(baseName) ?? _liveTemplates!.GetValueOrDefault(name);
    }

    /// <summary>
    /// Every root object with a replay object, by name: prefab assets and live ones apart. One look over every loaded
    /// object per playback (it was one per new name).
    /// </summary>
    private void IndexTemplates()
    {
        var watch = Stopwatch.StartNew();
        _prefabs = new Dictionary<string, GameObject>(StringComparer.Ordinal);
        _liveTemplates = new Dictionary<string, GameObject>(StringComparer.Ordinal);
        int looked = 0;
        foreach (UnityEngine.Object found in Resources.FindObjectsOfTypeAll(Il2CppType.Of<GameObject>()))
        {
            looked++;
            GameObject? candidate = found?.TryCast<GameObject>();
            if (candidate == null || candidate.transform.parent != null || candidate.GetComponent(Il2CppType.Of<ReplayObject>()) == null)
            {
                continue;
            }

            if (GameplayClassOn(candidate) is string refused)
            {
                // Never made from a host's table: a copy would start up as a second one of these.
                if (_refusedTemplates.Count < 16)
                {
                    _refusedTemplates.Add($"{LogNames.ObjectName(candidate)} ({refused})");
                }

                continue;
            }

            (candidate.scene.IsValid() ? _liveTemplates : _prefabs).TryAdd(candidate.name, candidate);
        }

        ModLog.Info($"Stream player: templates for objects the host's game makes during play: {_prefabs.Count} prefabs, {_liveTemplates.Count} live objects " +
            $"(looked over {looked} objects in {watch.Elapsed.TotalMilliseconds:0} ms)" +
            $"{(_refusedTemplates.Count > 0 ? $"; never made here (gameplay or a singleton): {string.Join(", ", _refusedTemplates.Take(8))}{(_refusedTemplates.Count > 8 ? ", ..." : string.Empty)}" : string.Empty)}.");
    }

    /// <summary>
    /// The first component class on <paramref name="root"/> (itself or a base class) that a template mustn't carry
    /// (<see cref="RefusedTemplateClasses"/>, or a singleton), or null.
    /// </summary>
    private static string? GameplayClassOn(GameObject root)
    {
        foreach (Component component in root.GetComponents(Il2CppType.Of<Component>()))
        {
            if (component == null)
            {
                continue;
            }

            for (IntPtr klass = IL2CPP.il2cpp_object_get_class(component.Pointer); klass != IntPtr.Zero; klass = IL2CPP.il2cpp_class_get_parent(klass))
            {
                string name = IL2CPP.il2cpp_class_get_name_(klass) ?? string.Empty;
                if (RefusedTemplateClasses.Contains(name) || name.StartsWith(SingletonPrefix, StringComparison.Ordinal))
                {
                    return name;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// A copy of the same template the stream has hidden for a while takes the new host object over: its components get
    /// the new object's ids, and the host object it stood for is made again if it shows again.
    /// </summary>
    private bool TryReuse(ushort netId, ObjectEntry entry, GameObject template, SnapshotFrame from, double now)
    {
        foreach (Copy copy in _copies)
        {
            if (copy.Template.Pointer != template.Pointer || from.Objects.ContainsKey(copy.NetId) ||
                Presence.HiddenFor(copy.GameObjectPointer, now) < ReuseHiddenSeconds || copy.Object == null ||
                !_session.Retarget(copy.Object, HostComponents.Of(entry), out _))
            {
                continue;
            }

            _objects.Remove(copy.NetId);
            Presence.RemoveId(copy.NetId, copy.GameObject);
            _pendingSpawns[copy.NetId] = (copy.Entry, template);
            copy.NetId = netId;
            copy.Entry = entry;
            copy.GameObject.name = entry.Name;
            _objects[netId] = copy.Object;
            Presence.Add(netId, copy.Object);
            _reused++;
            return true;
        }

        return false;
    }

    private bool TakeCopyBudget(double now)
    {
        if (now - _copyWindow >= 1.0)
        {
            _copyWindow = now;
            _copiesInWindow = 0;
        }

        return _copiesInWindow++ < MaxCopiesPerSecond;
    }

    /// <summary>A new copy of <paramref name="template"/> named as the host's object, or null (counted, the first few logged).</summary>
    private ReplayObject? MakeCopy(ObjectEntry entry, GameObject template)
    {
        try
        {
            // The non-generic overload (a generic instance may not be in this build).
            GameObject? copy = template != null ? UnityEngine.Object.Instantiate((UnityEngine.Object)template)?.TryCast<GameObject>() : null;
            if (copy == null)
            {
                _spawnFailures++;
                return null;
            }

            copy.name = entry.Name;
            ReplayObject? replayObject = copy.GetComponent(Il2CppType.Of<ReplayObject>())?.TryCast<ReplayObject>();
            if (replayObject == null)
            {
                _spawnFailures++;
                DestroyQuietly(copy);
                return null;
            }

            // Made after client mode started: its folded per-frame components weren't switched off with the rest.
            Client.ClientMode.DisableFoldedOn(copy);
            return replayObject;
        }
        catch (Exception ex)
        {
            if (_spawnFailures++ < 3)
            {
                ModLog.Warn($"Stream player: couldn't make \"{NameText(entry)}\" here: {ModLog.SafeErrorText(ex)}");
            }

            return null;
        }
    }

    /// <summary>A copy made for the stream: the host object it stands for now, and the template it came from.</summary>
    private sealed class Copy
    {
        public Copy(ushort netId, ObjectEntry entry, ReplayObject replayObject, GameObject template)
        {
            NetId = netId;
            Entry = entry;
            Object = replayObject;
            GameObject = replayObject.gameObject;
            GameObjectPointer = GameObject.Pointer;
            Template = template;
        }

        public ushort NetId { get; set; }

        public ObjectEntry Entry { get; set; }

        public ReplayObject Object { get; }

        public GameObject GameObject { get; }

        public IntPtr GameObjectPointer { get; }

        public GameObject Template { get; }
    }
}
#endif
