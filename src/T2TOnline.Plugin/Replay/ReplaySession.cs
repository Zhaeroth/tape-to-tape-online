#if HAS_INTEROP
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppUltimateReplay;
using T2TOnline.Core.Protocol;
using T2TOnline.Core.Stream;
using T2TOnline.Plugin.Diagnostics;
using T2TOnline.Plugin.Native;
using UnityEngine;

namespace T2TOnline.Plugin.Replay;

/// <summary>
/// Drives a set of live replay objects from recorded states through UltimateReplay's own per-component lifecycle, the
/// way its playback does: replay-start on every component, then per snapshot interval load state i, update to 1, load
/// state i+1 as the target, and update every frame with the blend factor; replay-end at the close. Proven by the M0.3
/// frozen replay (35,000+ states applied, 0 failed). Objects can join after the start (a live host's object table grows).
/// </summary>
/// <remarks>
/// <para>Putting the scene back is what matters most, so it's guarded twice. Before driving, every object's current state
/// goes through the same load-and-apply path as a dry run (an object that fails is left out). Afterwards that state is
/// applied again, and anything still away from where it was is moved back directly.</para>
/// <para>A recorded state names its components by their replay ids, which are random per load. An object from a freshly
/// loaded match takes the host's ids, each component the id of the host's component of its own type, in order (by position
/// when the host sent no types), and gets its own back at the end (<see cref="ComponentAlignment"/>): a component one game
/// added at run time and the other didn't (a talent's effect) leaves the rest driven. The component lookup
/// (<c>ReplayObject.GetReplayBehaviour</c>) compares the <c>replayIdentity</c> field linearly, so writing the field is all
/// it takes.</para>
/// </remarks>
internal sealed class ReplaySession
{
    private const float PositionTolerance = 0.05f;

    private readonly List<Saved> _saved = new();
    private readonly List<(ReplayBehaviour Behaviour, uint Id)> _ownIds = new();
    private Il2CppSystem.Collections.Generic.HashSet<ReplayBehaviour>? _behaviours;

    /// <summary>True between <see cref="Begin"/> and <see cref="End"/>.</summary>
    public bool Active => _behaviours != null;

    /// <summary>States applied.</summary>
    public long Applied { get; private set; }

    /// <summary>States that failed to apply.</summary>
    public long Failures { get; private set; }

    /// <summary>Objects being driven.</summary>
    public int Objects => _saved.Count;

    /// <summary>Replay components being driven.</summary>
    public int Components => _behaviours?.Count ?? 0;

    /// <summary>Components that took the host's replay ids.</summary>
    public int Renamed => _ownIds.Count;

    /// <summary>Starts a session with no objects yet (<see cref="Add"/> brings them in).</summary>
    public void Begin()
    {
        End();
        _saved.Clear();
        _ownIds.Clear();
        Applied = Failures = 0;
        PartlyDriven = 0;
        _behaviours = new Il2CppSystem.Collections.Generic.HashSet<ReplayBehaviour>();
    }

    /// <summary>
    /// Brings objects into the session: saves each one's state, starts its components' replay lifecycle, checks it can
    /// re-apply its own state, then (for a fresh match) gives its components the host's ids (<see cref="HostComponents"/>:
    /// null for the recording's own match). Each object joins whole or not at all: one that fails anywhere on the way has
    /// its lifecycle ended and its own ids back. Returns the objects that couldn't be driven (left as they were);
    /// <paramref name="problem"/> says why for the first few, <paramref name="partly"/> which joined without some of their
    /// components (their component lists differ from the host's; the rest is driven).
    /// </summary>
    public List<ReplayObject> Add(IEnumerable<(ReplayObject Object, HostComponents? Host)> objects, out string problem, out string partly)
    {
        var failed = new List<ReplayObject>();
        var reasons = new List<string>();
        var partial = new List<string>();
        if (_behaviours == null)
        {
            problem = "the session isn't running";
            partly = string.Empty;
            failed.AddRange(objects.Select(o => o.Object));
            return failed;
        }

        foreach ((ReplayObject replayObject, HostComponents? host) in objects)
        {
            if (!TryAdd(replayObject, host, out string? reason, out string? part))
            {
                failed.Add(replayObject);
                if (reason != null)
                {
                    reasons.Add(reason);
                }
            }
            else if (part != null)
            {
                partial.Add(part);
            }
        }

        problem = string.Join("; ", reasons.Distinct().Take(5));
        partly = partial.Count == 0 ? string.Empty : $"{string.Join("; ", partial.Take(5))}{(partial.Count > 5 ? $"; {partial.Count - 5} more" : string.Empty)}";
        PartlyDriven += partial.Count;
        return failed;
    }

    /// <summary>Objects that joined without some of their components (their lists differ from the host's).</summary>
    public int PartlyDriven { get; private set; }

    /// <summary>
    /// Gives an object already in the session another host object's component ids (a copy made for the stream, reused
    /// for a new host object of the same template). False, with why, if its components don't line up.
    /// </summary>
    public bool Retarget(ReplayObject replayObject, HostComponents? host, out string problem)
    {
        int index = _saved.FindIndex(s => s.Object.Pointer == replayObject.Pointer);
        if (index < 0)
        {
            problem = "it isn't in the session";
            return false;
        }

        if (host is not { Ids.Count: > 0 })
        {
            problem = string.Empty;
            return true;
        }

        // A copy stands in for a new host object of its own template only when every component lines up.
        Saved saved = _saved[index];
        IReadOnlyList<ReplayBehaviour> ordered = saved.Behaviours;
        ComponentMatch match = Match(ordered, host);
        if (!match.Whole)
        {
            problem = $"\"{LogNames.ObjectName(replayObject)}\"'s replay components aren't the host's ({ComponentAlignment.Describe(match)})";
            return false;
        }

        // Its own ids were saved when it joined; only the host's change.
        for (int i = 0; i < ordered.Count; i++)
        {
            ordered[i].replayIdentity = new ReplayIdentity { id = match.HostIdFor[i]!.Value };
        }

        _saved[index] = saved with { HostIds = host.Ids };
        problem = string.Empty;
        return true;
    }

    /// <summary>
    /// An object in the session whose component list changed (this PC's game added or removed a talent's effect to follow
    /// the host's, or the host announced its own list again): components gone leave the session, new ones join it (their
    /// lifecycle starts, their own ids are saved), and every component takes the host's id of its type again
    /// (<see cref="ComponentAlignment"/>). Running it again with nothing changed changes nothing. False, with why, when it
    /// isn't in the session or nothing lines up any more (its ids are then left as they were).
    /// <paramref name="changed"/>: whether any component joined, left or took another id.
    /// </summary>
    public bool Realign(ReplayObject replayObject, HostComponents? host, out string outcome, out bool changed)
    {
        changed = false;
        int index = _saved.FindIndex(s => s.Object.Pointer == replayObject.Pointer);
        if (index < 0 || _behaviours == null || host is not { Ids.Count: > 0 })
        {
            outcome = index < 0 ? "it isn't in the session" : "nothing to take over";
            return false;
        }

        Saved saved = _saved[index];
        List<ReplayBehaviour> ordered = Il2CppLists.Items(replayObject.Behaviours).Where(b => b != null).ToList();
        ComponentMatch match = Match(ordered, host);
        if (match.Matched == 0)
        {
            outcome = $"none of its components match the host's any more ({ComponentAlignment.Describe(match)})";
            return false;
        }

        // A component this PC's game destroyed this frame is still in the list until the frame's end (its OnDestroy takes
        // it out); a take-over asked for by a follower runs again a frame later for that (Streaming/StreamObjects).
        var now = new HashSet<IntPtr>(ordered.Select(b => b.Pointer));
        foreach (ReplayBehaviour gone in saved.Behaviours.Where(b => b == null || !now.Contains(b.Pointer)).ToList())
        {
            changed = true;
            Safely(() => _behaviours.Remove(gone));
        }

        var before = new HashSet<IntPtr>(saved.Behaviours.Where(b => b != null).Select(b => b.Pointer));
        var joining = new Il2CppSystem.Collections.Generic.HashSet<ReplayBehaviour>();
        for (int i = 0; i < ordered.Count; i++)
        {
            ReplayBehaviour behaviour = ordered[i];
            if (!before.Contains(behaviour.Pointer))
            {
                _ownIds.Add((behaviour, behaviour.replayIdentity.id));
                joining.Add(behaviour);
                _behaviours.Add(behaviour);
                changed = true;
            }

            uint id = behaviour.replayIdentity.id;
            ushort next = match.HostIdFor[i] ?? ComponentAlignment.FreeId((ushort)id, host.Ids);
            if (next != id)
            {
                behaviour.replayIdentity = new ReplayIdentity { id = next };
                changed = true;
            }
        }

        if (joining.Count > 0)
        {
            Safely(() => ReplayBehaviour.InvokeReplayStartEvent(joining));
        }

        _saved[index] = saved with { Behaviours = ordered, HostIds = host.Ids };
        outcome = ComponentAlignment.Describe(match);
        return true;
    }

    /// <summary>
    /// The replay ids an object in the session answers to now, in its component order (the host's, once it took them), or
    /// null when it isn't in the session. A state reaches only the components whose ids it names.
    /// </summary>
    public IReadOnlyList<ushort>? IdsOf(ReplayObject replayObject)
    {
        Saved? saved = _saved.FirstOrDefault(s => s.Object.Pointer == replayObject.Pointer);

        // The host's whole list once it took the host's ids (some of them may have no component here).
        return saved?.HostIds ?? saved?.Behaviours.Select(behaviour => behaviour == null ? (ushort)0 : (ushort)behaviour.replayIdentity.id).ToArray();
    }

    /// <summary>
    /// Takes an object out of the session without putting it back (it's about to be destroyed: a copy made for the
    /// stream): its components' lifecycle ends. True if it was in the session.
    /// </summary>
    public bool Remove(ReplayObject replayObject)
    {
        int index = _saved.FindIndex(s => s.Object.Pointer == replayObject.Pointer);
        if (index < 0 || _behaviours == null)
        {
            return false;
        }

        Saved saved = _saved[index];
        _saved.RemoveAt(index);
        Forget(saved);
        var ending = new Il2CppSystem.Collections.Generic.HashSet<ReplayBehaviour>();
        foreach (ReplayBehaviour behaviour in saved.Behaviours)
        {
            ending.Add(behaviour);
        }

        Safely(() => ReplayBehaviour.InvokeReplayEndEvent(ending));
        return true;
    }

    /// <summary>Drops objects destroyed since they joined (their match unloaded part of itself). Returns how many.</summary>
    public int Prune()
    {
        int pruned = 0;
        for (int i = _saved.Count - 1; i >= 0; i--)
        {
            // Unity's null: destroyed.
            if (_saved[i].Object == null)
            {
                Forget(_saved[i]);
                _saved.RemoveAt(i);
                pruned++;
            }
        }

        return pruned;
    }

    /// <summary>Loads one recorded state into an object (count it as applied or failed).</summary>
    public void Load(ReplayObject replayObject, byte[] state)
    {
        if (ReplayStates.TryDeserialize(replayObject, state, out string? error))
        {
            Applied++;
        }
        else if (Failures++ < 5)
        {
            ModLog.Warn($"Replay: could not apply a state to \"{LogNames.ObjectName(replayObject)}\": {error}");
        }
    }

    /// <summary>Blends every component between the states loaded last (0 = previous, 1 = the target).</summary>
    public void Update(float t)
    {
        if (_behaviours != null && _behaviours.Count > 0)
        {
            ReplayBehaviour.InvokeReplayUpdateEvent(_behaviours, Math.Clamp(t, 0f, 1f));
        }
    }

    /// <summary>
    /// Gives every component its own id back, puts every object back as it was when it joined, and ends the lifecycle.
    /// Returns how many objects were moved back directly.
    /// </summary>
    public int End()
    {
        if (_behaviours == null)
        {
            return 0;
        }

        Il2CppSystem.Collections.Generic.HashSet<ReplayBehaviour> behaviours = _behaviours;
        _behaviours = null;
        GiveIdsBack(_ownIds);
        _ownIds.Clear();
        int restoreFailures = 0;
        try
        {
            foreach (Saved saved in _saved)
            {
                // Unity's null: destroyed with its match (the player left during playback); nothing to put back.
                if (saved.Object != null)
                {
                    restoreFailures += ReplayStates.TryDeserialize(saved.Object, saved.State, out _) ? 0 : 1;
                }
            }

            if (behaviours.Count > 0)
            {
                ReplayBehaviour.InvokeReplayUpdateEvent(behaviours, 1f);
                ReplayBehaviour.InvokeReplayEndEvent(behaviours);
            }
        }
        catch (Exception ex)
        {
            ModLog.Error("Replay: restoring through the replay components failed; moving objects back directly", ex);
        }

        int moved = PutBackPositions();
        if (restoreFailures > 0)
        {
            ModLog.Warn($"Replay: {restoreFailures} of {_saved.Count} objects didn't take their saved state back.");
        }

        _saved.Clear();
        return moved;
    }

    /// <summary>
    /// The object's own name, as the object table carries it to friends (who name their copies by it), or "?" when it's
    /// gone (a destroyed object's name can't be read). Never for a log line: a skater's object is named after the skater,
    /// which a custom skater's player typed (<see cref="LogNames.ObjectName(UnityEngine.Component?)"/> is for logs).
    /// </summary>
    internal static string ObjectName(ReplayObject? replayObject)
    {
        try
        {
            return replayObject != null && replayObject.gameObject is GameObject gameObject && gameObject != null ? gameObject.name ?? "?" : "?";
        }
        catch (Exception)
        {
            return "?";
        }
    }

    private static void Safely(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            ModLog.Warn($"Replay: {ModLog.SafeErrorText(ex)}");
        }
    }

    private static void GiveIdsBack(IEnumerable<(ReplayBehaviour Behaviour, uint Id)> ids)
    {
        foreach ((ReplayBehaviour behaviour, uint id) in ids)
        {
            Safely(() =>
            {
                if (behaviour != null)
                {
                    behaviour.replayIdentity = new ReplayIdentity { id = id };
                }
            });
        }
    }

    /// <summary>
    /// One object joins, or nothing changes: everything that can throw (reading its components, its state) happens
    /// before the lifecycle starts, and a failure after that ends it again and gives back any ids already taken.
    /// </summary>
    private bool TryAdd(ReplayObject replayObject, HostComponents? host, out string? reason, out string? partly)
    {
        var renamed = new List<(ReplayBehaviour Behaviour, uint Id)>();
        Il2CppSystem.Collections.Generic.HashSet<ReplayBehaviour>? behaviours = null;
        bool started = false;
        Saved saved;
        partly = null;
        try
        {
            List<ReplayBehaviour> ordered = Il2CppLists.Items(replayObject.Behaviours).Where(b => b != null).ToList();
            IReadOnlyList<ushort>? hostIds = host is { Ids.Count: > 0 } ? host.Ids : null;
            ComponentMatch? match = hostIds == null ? null : Match(ordered, host!);
            if (match != null && match.Matched == 0)
            {
                reason = match.ByType
                    ? $"\"{LogNames.ObjectName(replayObject)}\" has none of the host's replay component types ({ordered.Count} here, {hostIds!.Count} on the host)"
                    : $"\"{LogNames.ObjectName(replayObject)}\" has {ordered.Count} replay components here, {hostIds!.Count} on the host";
                return false;
            }

            behaviours = new Il2CppSystem.Collections.Generic.HashSet<ReplayBehaviour>();
            foreach (ReplayBehaviour behaviour in ordered)
            {
                behaviours.Add(behaviour);
            }

            Transform transform = replayObject.transform;
            saved = new Saved(replayObject, ReplayStates.Serialize(replayObject), transform.position, transform.rotation, ordered, hostIds);
            ReplayBehaviour.InvokeReplayStartEvent(behaviours);
            started = true;
            bool ok = ReplayStates.TryDeserialize(replayObject, saved.State, out _);
            ReplayBehaviour.InvokeReplayUpdateEvent(behaviours, 1f);
            if (!ok)
            {
                ReplayBehaviour.InvokeReplayEndEvent(behaviours);
                reason = $"\"{LogNames.ObjectName(replayObject)}\" couldn't re-apply its own current state";
                return false;
            }

            if (match != null)
            {
                for (int i = 0; i < ordered.Count; i++)
                {
                    // A component with no counterpart on the host keeps an id none of the host's states names.
                    ReplayBehaviour behaviour = ordered[i];
                    uint own = behaviour.replayIdentity.id;
                    ushort id = match.HostIdFor[i] ?? ComponentAlignment.FreeId((ushort)own, hostIds!);
                    renamed.Add((behaviour, own));
                    behaviour.replayIdentity = new ReplayIdentity { id = id };
                }

                if (!match.Whole)
                {
                    IEnumerable<string> hereOnly = ordered.Where((_, i) => match.HostIdFor[i] == null).Select(ReplayComponentTypes.NameOf);
                    partly = $"\"{LogNames.ObjectName(replayObject)}\": {ComponentAlignment.Describe(match)}" +
                        $"{(match.LocalOnly > 0 ? $" (here only: {string.Join(", ", hereOnly.Take(4))})" : string.Empty)}";
                }
            }
        }
        catch (Exception ex)
        {
            reason = $"\"{LogNames.ObjectName(replayObject)}\": {ModLog.SafeErrorText(ex)}";
            GiveIdsBack(renamed);
            if (started && behaviours != null)
            {
                Safely(() => ReplayBehaviour.InvokeReplayEndEvent(behaviours));
            }

            return false;
        }

        _ownIds.AddRange(renamed);
        _saved.Add(saved);
        foreach (ReplayBehaviour behaviour in saved.Behaviours)
        {
            _behaviours!.Add(behaviour);
        }

        reason = null;
        return true;
    }

    /// <summary>Drops an object's components from the driven set (its own ids stay saved only for components still alive).</summary>
    private void Forget(Saved saved)
    {
        foreach (ReplayBehaviour behaviour in saved.Behaviours)
        {
            Safely(() => _behaviours?.Remove(behaviour));
        }

        var behaviours = new HashSet<IntPtr>(saved.Behaviours.Select(b => b.Pointer));
        _ownIds.RemoveAll(o => behaviours.Contains(o.Behaviour.Pointer));
    }

    private int PutBackPositions()
    {
        var moved = new List<string>();
        foreach (Saved saved in _saved)
        {
            if (saved.Object == null)
            {
                continue;
            }

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
                ModLog.Warn($"Replay: could not move an object back: {ModLog.SafeErrorText(ex)}");
            }
        }

        if (moved.Count > 0)
        {
            ModLog.Info($"Replay: moved back directly: {string.Join(", ", moved.Take(20))}.");
        }

        return moved.Count;
    }

    /// <summary>The host's ids for <paramref name="ordered"/> (this PC's components, in order), by type when the host sent types.</summary>
    private static ComponentMatch Match(IReadOnlyList<ReplayBehaviour> ordered, HostComponents host) =>
        ComponentAlignment.Align(host.Ids, host.Types, ordered.Count, host.Types == null ? null : ReplayComponentTypes.Of(ordered));

    private sealed record Saved(ReplayObject Object, byte[] State, Vector3 Position, Quaternion Rotation, IReadOnlyList<ReplayBehaviour> Behaviours, IReadOnlyList<ushort>? HostIds);
}

/// <summary>A host object's component ids, in its order, and their types when the host sent them (an older host didn't).</summary>
/// <param name="Ids">The host's replay ids of the object's components.</param>
/// <param name="Types">One type per id (<see cref="ComponentAlignment.TypeHash"/>), or null.</param>
internal sealed record HostComponents(IReadOnlyList<ushort> Ids, IReadOnlyList<uint>? Types)
{
    /// <summary>The entry's components, or null when it has none (nothing to take over).</summary>
    public static HostComponents? Of(ObjectEntry entry) =>
        entry.Components is { Count: > 0 } ids ? new HostComponents(ids, entry.ComponentTypes) : null;
}

/// <summary>
/// Reading and writing one replay object's state through its recorders, through one scratch <c>ReplayState</c> kept for
/// the session.
/// </summary>
/// <remarks>
/// <para>Never a new state per call: a state made with <c>new</c> and disposed goes onto UltimateReplay's own pool
/// (<c>ReplayState.pool</c>, an unbounded stack), and the game only takes from it when an object first needs states for
/// its components, so one state and its 512-byte buffer per call piled up there (the host's recorder: every object, 60
/// times a second). The scratch is cleared before each use (<c>Clear</c>: size and read position back to 0) and never
/// disposed; a call made while it's in use (none is expected) gets a state of its own that is simply dropped.</para>
/// <para>Reading copies only the state's own bytes, straight out of its buffer (<c>ToArray</c> copies the whole
/// 512-byte buffer, and the interop copies that again). Writing copies the recorded bytes into one IL2CPP array kept for
/// the session and hands the game that with their length (<c>Write(byte[], int, int)</c>: in this build
/// <c>EnsureCapacity(size + length)</c>, <c>Buffer.BlockCopy</c> into the state's own buffer, <c>size += length</c>, the
/// same as <c>Write(byte[])</c> on an array of exactly that length). It was a new IL2CPP array, interop wrapper, GC handle
/// and weak-table entry per distinct recorded array: about 720 a second on a friend's PC (a recorded host match
/// changes 12 objects a snapshot at 60 a second, and each change decodes into a fresh array), all garbage for IL2CPP's
/// stop-the-world collector. Now no state the mod applies allocates on the IL2CPP heap.</para>
/// </remarks>
internal static class ReplayStates
{
    // An IL2CPP array's elements start after its header: class, monitor, bounds, length.
    private static readonly int ArrayDataOffset = 4 * IntPtr.Size;
    private static ReplayState? _scratch;
    private static Il2CppStructArray<byte>? _input;
    private static bool _busy;
    private static int _bytesOffset = -1;

    /// <summary>States the mod made this session: one scratch, plus any for a call made while it was in use.</summary>
    public static int Created { get; private set; }

    /// <summary>IL2CPP arrays made this session to hand recorded state bytes to the game (one, grown if a state needs more).</summary>
    public static long NativeArrays { get; private set; }

    /// <summary>The object's current state, exactly as the game's recorder would store it.</summary>
    public static byte[] Serialize(ReplayObject replayObject)
    {
        ReplayState state = Take(out bool scratch);
        try
        {
            replayObject.OnReplaySerialize(state);
            return Copy(state);
        }
        finally
        {
            if (scratch)
            {
                _busy = false;
            }
        }
    }

    /// <summary>
    /// Loads <paramref name="bytes"/> into the object. In this build's UltimateReplay, <c>ReplayState.FromByteArray</c> /
    /// <c>InitializeFromData</c> copy the bytes but leave the state's size at 0; <c>Write</c> sets it, then
    /// <c>PrepareForRead</c> rewinds.
    /// </summary>
    public static bool TryDeserialize(ReplayObject replayObject, byte[] bytes, out string? error)
    {
        bool scratch = false;
        try
        {
            ReplayState state = Take(out scratch);
            // The game copies the bytes into the state's own buffer here, so the input array is free again right after.
            state.Write(Input(bytes), 0, bytes.Length);
            state.PrepareForRead();
            replayObject.OnReplayDeserialize(state);
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = ModLog.SafeErrorText(ex);
            return false;
        }
        finally
        {
            if (scratch)
            {
                _busy = false;
            }
        }
    }

    /// <summary>
    /// For the log when recording or playback stops: the states the mod made and how many states wait in the game's
    /// pool. With the scratch, the pool doesn't grow with snapshots (it grew by one per object per snapshot before).
    /// </summary>
    public static string Describe(int poolAtStart)
    {
        int pool = PoolCount();
        string poolText = pool < 0 ? "the game's state pool couldn't be read"
            : $"the game's state pool holds {pool}{(poolAtStart >= 0 ? $" ({pool - poolAtStart:+0;-0;0} since the start)" : string.Empty)}";
        return $"replay states: {Created} made by the mod this session, {NativeArrays} IL2CPP input arrays; {poolText}";
    }

    /// <summary>States waiting in <c>ReplayState.pool</c>, or -1 if it can't be read.</summary>
    public static int PoolCount()
    {
        try
        {
            return ReplayState.pool?.waitingInstances?.Count ?? -1;
        }
        catch (Exception)
        {
            return -1;
        }
    }

    private static ReplayState Take(out bool scratch)
    {
        if (_busy)
        {
            scratch = false;
            Created++;
            return new ReplayState();
        }

        if (_scratch != null)
        {
            try
            {
                _scratch.Clear();
            }
            catch (Exception)
            {
                // Disposed by something else (Clear refuses a disposed state): make another.
                _scratch = null;
            }
        }

        if (_scratch == null)
        {
            _scratch = new ReplayState();
            Created++;
        }

        _busy = scratch = true;
        return _scratch;
    }

    /// <summary>The state's <c>Size</c> bytes, read from its buffer.</summary>
    private static byte[] Copy(ReplayState state)
    {
        int size = state.Size;
        if (size <= 0)
        {
            return Array.Empty<byte>();
        }

        IntPtr buffer = BufferOf(state);
        if (buffer == IntPtr.Zero)
        {
            byte[] whole = state.ToArray();
            return whole.AsSpan(0, Math.Min(size, whole.Length)).ToArray();
        }

        var bytes = new byte[Math.Min(size, (int)IL2CPP.il2cpp_array_length(buffer))];
        Marshal.Copy(buffer + ArrayDataOffset, bytes, 0, bytes.Length);
        return bytes;
    }

    /// <summary>The state's <c>bytes</c> array (IL2CPP), or zero when its field can't be found.</summary>
    private static IntPtr BufferOf(ReplayState state)
    {
        if (_bytesOffset < 0)
        {
            IntPtr field = IL2CPP.GetIl2CppField(Il2CppClassPointerStore<ReplayState>.NativeClassPtr, "bytes");
            _bytesOffset = field == IntPtr.Zero ? 0 : (int)IL2CPP.il2cpp_field_get_offset(field);
        }

        return _bytesOffset > 0 ? Marshal.ReadIntPtr(state.Pointer, _bytesOffset) : IntPtr.Zero;
    }

    /// <summary>
    /// The session's IL2CPP input array holding <paramref name="bytes"/> at its start: made once with room for any state a
    /// snapshot may carry (<see cref="T2TOnline.Core.Snapshots.SnapshotFrame.MaxStateBytes"/>), made again only for a
    /// longer one (a state saved from a local object can be).
    /// </summary>
    private static Il2CppStructArray<byte> Input(byte[] bytes)
    {
        if (_input == null || _input.Length < bytes.Length)
        {
            _input = new Il2CppStructArray<byte>(Math.Max(bytes.Length, T2TOnline.Core.Snapshots.SnapshotFrame.MaxStateBytes));
            NativeArrays++;
        }

        if (bytes.Length > 0)
        {
            Marshal.Copy(bytes, 0, _input.Pointer + ArrayDataOffset, bytes.Length);
        }

        return _input;
    }
}
#endif
