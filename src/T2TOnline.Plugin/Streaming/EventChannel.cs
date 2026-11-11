#if HAS_INTEROP
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Il2CppInterop.Runtime;
using Il2CppUltimateReplay;
using T2TOnline.Core.Diagnostics;
using T2TOnline.Core.Protocol;
using T2TOnline.Core.Serialization;
using T2TOnline.Core.Stream;
using T2TOnline.Plugin.Diagnostics;
using T2TOnline.Plugin.Hooks;

namespace T2TOnline.Plugin.Streaming;

/// <summary>
/// Friend side of the event bridge: re-delivers the host's events to presentation listeners
/// only (sound, crowd, VFX, lights, HUD; Hooks/presentation.txt).
/// <list type="bullet">
/// <item>There's no single raise method to call (the generic raise is compiled into 25 separate bodies), and calling the
/// event's own raise would reach every listener. So each allowed listener is called directly, in list order, each in its
/// own try/catch, as the game's safe invocation does.</item>
/// <item>While client mode is on, every other listener is detached, so presentation code that raises an event itself
/// (the in-game menu raises ResetMap, the screen wipe raises its start and end) can't reach game logic. A detached
/// listener's slot holds a do-nothing stand-in that the game takes for the listener itself (<see cref="StandIns"/>: the
/// same delegate class, target and method, so IL2CPP's delegate equality and hash match), so the game's own list upkeep
/// treats the slot as the listener: <c>Register</c>'s compaction (which drops empty slots) keeps it, its duplicate check
/// sees the listener there, a wipe (<c>UnregisterAll</c>) drops it exactly when it would have dropped the listener (the
/// persistent set answers for the stand-in as for the listener), and the owner's own <c>Unregister</c> finds and empties
/// it. When playback ends each stand-in is swapped back for its listener, so the list order is the game's own. A
/// listener whose owner was destroyed, that left during playback (unsubscribed or wiped), or that subscribed again (its
/// newer slot wins), isn't put back. Should an equal stand-in not be possible, the slot gets a plain one (a delegate of
/// its own, made persistent alongside), which the owner's <c>Unregister</c> can't find.</item>
/// <item>Local raises are counted for the leak audit. A listener that subscribed during playback (an object the stream
/// showed, a copy made for it) and isn't presentation is detached when the stream next delivers that event, or when the
/// event is next raised here (a gameplay event raised locally is a leak: its later raises must not reach logic). Not
/// while a finished match is held: the real menus open over it then are this PC's own.</item>
/// </list>
/// </summary>
internal sealed class EventChannel : IPresentationChannel, IDeliveryDetail
{
    private static readonly MessageType[] HandledTypes = { MessageType.GameEvent };

    private static string? _expectedRaise;

    /// <summary>References noted per delivery (an event's arguments hold a handful; a list's items a few dozen).</summary>
    private const int MaxNotedReferences = 64;

    /// <summary>Events naming an object not found here, and collisions whose collider listener was skipped, logged per playback.</summary>
    private const int LoggedPerPlayback = 5;

    /// <summary>What a delivery is called before its event is known (unreadable, or not in this build's table).</summary>
    private const string UnnamedEvent = "an event not known here";

    private readonly Func<bool> _holding;

    private readonly Dictionary<ushort, BusEvent> _byStreamId = new();
    private readonly List<Detached> _detached = new();
    private readonly HashSet<IntPtr> _ownListeners = new();
    private readonly List<(BusEvent Event, Il2CppSystem.Delegate Listener)> _counters = new();
    private readonly RefCache _refCache = new();
    private readonly MissingObjects _missing = new();
    private readonly Dictionary<string, int> _skippedForMissing = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _failures = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _localRaises = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _detachedByClass = new(StringComparer.Ordinal);
    private readonly HashSet<(ushort Event, string Owner)> _called = new();
    private readonly List<(BusEvent Event, object?[] Args)> _redeliver = new();
    private List<Detached> _redeliverTo = new();
    private readonly List<NetValue> _values = new();
    private readonly List<(NetValue Value, object? Found)> _references = new();
    private readonly Dictionary<string, int> _unresolvedEvents = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _collisionSkips = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _expectedRaises = new(StringComparer.Ordinal);
    private readonly Func<NetValue, Type, object?> _resolve;
    private PresentationList _list = PresentationList.Empty;
    private IReadOnlyDictionary<ushort, ReplayObject> _objects = new Dictionary<ushort, ReplayObject>();
    private IStreamSource? _source;
    private bool _begun;
    private int _mappedEntries;
    private int _mismatched;
    private bool _delivering;
    private int _delivered;
    private int _calls;
    private int _skippedLocal;
    private int _unknown;
    private int _undecodable;
    private int _decodeFailures;
    private int _noListener;
    private int _plainStandIns;
    private int _lateDetached;
    private int _unresolvedLogged;
    private int _collisionSkipsLogged;
    private string _lastEvent = UnnamedEvent;
    private string? _lastListener;
    private double _lastListenerMicros;

    /// <summary>The bridge; <paramref name="holding"/> says whether the player holds a finished match (no detaching then).</summary>
    public EventChannel(Func<bool> holding)
    {
        _holding = holding;
        _resolve = (value, type) =>
        {
            object? found = ObjectRefs.Resolve(value, type, _objects, _refCache);
            if (_references.Count < MaxNotedReferences && LookupBudget.IsReference(value))
            {
                // What each reference of this delivery found here: the failure readout and the not-found count read it.
                _references.Add((value, found));
            }

            return found;
        };
    }

    /// <summary>
    /// Listeners the mod itself keeps on the game's events (the match-loaded listener): never detached, never called by
    /// the channel. Register a listener here before any playback.
    /// </summary>
    public static HashSet<IntPtr> ModListeners { get; } = new();

    /// <summary>
    /// Listener classes outside presentation.txt that get the host's events too and stay attached, by owner name (M4,
    /// D42): a friend's own bench quest system, so a skater's goals, knockouts and shots in the shared run count on the
    /// friend's own quests (Campaign/Progress). Null, or false for a class: presentation only. Asked for every listener;
    /// must be quick and never throw.
    /// </summary>
    public static Func<string, bool>? AlsoDeliverTo { get; set; }

    /// <summary>
    /// True while a playback holds the game's own logic listeners off the event buses (from <see cref="Begin"/> to
    /// <see cref="End"/>): a raise on this PC reaches presentation listeners only. A friend's shared progress applied then
    /// (at a run's end, while the run's last match stays loaded) calls the one game handler it needs by hand
    /// (Campaign/Progress).
    /// </summary>
    public static bool LogicDetached { get; private set; }

    /// <summary>
    /// True while this PC's own shared progress is being applied (Campaign/Progress): a bus event it raises meanwhile (an
    /// unlock check's <c>RogueEvents.UnlockEarned</c>) is this player's own progress, not gameplay leaking past client mode,
    /// so it's counted as expected. Null, or false: not applying. Must be quick and never throw.
    /// </summary>
    public static Func<bool>? OwnRaising { get; set; }

    /// <summary>
    /// The next raise on this PC of the gameplay event <paramref name="name"/> is this PC's own presentation's (the held
    /// anthem's ResetMap, Match/MatchIntro): it reaches the presentation listeners as it would, and isn't a leak.
    /// </summary>
    public static void ExpectLocalRaise(string name) => _expectedRaise = name;

    /// <summary>
    /// A menu event (presentation.txt [menu]) was raised on this PC, with its action: "pause", "resume" or "leave".
    /// Raised from inside the game's event: handlers must be quick, or stop playback outright for "leave".
    /// </summary>
    public event Action<string>? MenuAction;

    /// <inheritdoc />
    public IReadOnlyCollection<MessageType> Handles => HandledTypes;

    /// <inheritdoc />
    public (string What, string? Inner, double InnerMicros) LastDelivery => (_lastEvent, _lastListener, _lastListenerMicros);

    /// <inheritdoc />
    public void Begin(IStreamSource source, IReadOnlyDictionary<ushort, ReplayObject> objects)
    {
        // Set first: an End after a Begin that threw partway puts back whatever was detached by then.
        _begun = true;
        LogicDetached = true;
        _list = PresentationList.LoadEmbedded();
        _objects = objects;
        _source = source;
        ResetCounts();
        MapEvents();

        // The counters first: registering may compact a list, which must happen before slots are taken over.
        CountLocalRaises();
        foreach (BusEvent busEvent in EventCatalog.All)
        {
            DetachLogic(busEvent);
        }

        int bare = _detached.Count(d => d.Placeholder == null);
        ModLog.Info($"Event channel: {_byStreamId.Count} of the stream's {source.Events.Count} events mapped so far; {_detached.Count} logic listeners detached " +
            $"from {_detachedByClass.Count} classes{(bare > 0 ? $" ({bare} without a stand-in: their slot is emptied)" : string.Empty)}" +
            $"{(_plainStandIns > 0 ? $" ({_plainStandIns} with a plain stand-in their owner's Unregister can't find)" : string.Empty)}; " +
            $"{_list.Listeners.Count} presentation classes allowed.");
    }

    /// <inheritdoc />
    public void Deliver(in TimedMessage message)
    {
        _lastEvent = UnnamedEvent;
        _lastListener = null;
        _lastListenerMicros = 0;
        var reader = new BitReader(message.Payload);
        _values.Clear();
        if (!GameEventMessage.TryRead(ref reader, out ushort streamId, _values))
        {
            _undecodable++;
            return;
        }

        if (!_byStreamId.TryGetValue(streamId, out BusEvent? busEvent) && (_source == null || _source.Events.Count == _mappedEntries || !MapEvents() ||
            !_byStreamId.TryGetValue(streamId, out busEvent)))
        {
            _unknown++;
            return;
        }

        _lastEvent = busEvent.Name;
        Online.HostMatchWitness.NoteEvent(busEvent.Name, _values);
        if (_list.LocalEvents.Contains(busEvent.Name))
        {
            _skippedLocal++;
            return;
        }

        if (_called.Add((busEvent.Id, string.Empty)))
        {
            ModLog.Trace($"Event channel: first {busEvent.Name} received ({_values.Count} values)");
        }

        object?[] args;
        _references.Clear();
        CompoundValues.ClearRebuilds();
        long heldBackBefore = _refCache.Deferred;
        try
        {
            if (!busEvent.TryDecode(_values, _resolve, out args))
            {
                _undecodable++;
                return;
            }
        }
        catch (Exception ex)
        {
            // Untrusted values the decoder didn't expect (a setter or constructor that refuses them): dropped and counted.
            _undecodable++;
            if (_decodeFailures++ < 3)
            {
                Exception inner = ex is System.Reflection.TargetInvocationException { InnerException: Exception cause } ? cause : ex;
                ModLog.Warn($"Event channel: couldn't rebuild a {busEvent.Name}'s arguments ({ModLog.SafeErrorText(inner)}){(_decodeFailures == 3 ? "; further ones are only counted" : string.Empty)}.");
            }

            return;
        }

        _delivered++;
        bool objectsMissing = NoteUnresolved(busEvent, _refCache.Deferred > heldBackBefore);
        bool skipColliderListener = CollisionCheck.Skips(CollisionCheck.ColliderListener, CompoundValues.Rebuilds);
        Il2CppSystem.Collections.Generic.List<Il2CppSystem.Delegate>? listeners = busEvent.Event.m_Listeners;
        if (listeners == null)
        {
            return;
        }

        // Over a copy, as the game does: a listener may subscribe or unsubscribe others. Anything that subscribed after
        // playback started (a late spawn) and isn't presentation is detached first, before any listener runs.
        var snapshot = new List<(Il2CppSystem.Delegate Listener, string Owner)>();
        for (int i = 0; i < listeners.Count; i++)
        {
            Il2CppSystem.Delegate? listener = listeners[i];
            if (listener != null && !_ownListeners.Contains(listener.Pointer) && !ModListeners.Contains(listener.Pointer))
            {
                snapshot.Add((listener, Listeners.OwnerOf(listener)));
            }
        }

        foreach ((Il2CppSystem.Delegate listener, string owner) in snapshot.Where(s => !Delivered(s.Owner)))
        {
            Detach(busEvent, listeners, listener, owner);
        }

        if (objectsMissing && MissingObjects.SkipsEvent(busEvent.WholeArguments(_values, args)))
        {
            // The event is about an object this PC doesn't have: each listener would be handed null for it.
            SkipForMissing(busEvent.Name, null);
            return;
        }

        bool any = false;
        foreach ((Il2CppSystem.Delegate listener, string listenerClass) in snapshot)
        {
            if (!Delivered(listenerClass))
            {
                continue;
            }

            any = true;
            if (skipColliderListener && CollisionCheck.Skips(listenerClass, CompoundValues.Rebuilds))
            {
                SkipCollision(busEvent, listenerClass);
                continue;
            }

            if (_missing.Skips(busEvent.Name, listenerClass, objectsMissing))
            {
                SkipForMissing(busEvent.Name, listenerClass);
                continue;
            }

            _calls++;
            if (_called.Add((busEvent.Id, listenerClass)))
            {
                // The first call of each event into each listener class leaves a breadcrumb: a native crash can't be
                // caught, but the log's last line then names the handler.
                ModLog.Trace($"Event channel: first {busEvent.Name} → {listenerClass}");
            }

            _delivering = true;
            long callStart = CostMeter.Start();
            try
            {
                busEvent.Call(listener, args);
            }
            catch (Exception ex)
            {
                string key = $"{busEvent.Name} → {listenerClass}";
                int count = _failures.GetValueOrDefault(key);
                _failures[key] = count + 1;
                if (count == 0)
                {
                    Exception inner = ex is System.Reflection.TargetInvocationException { InnerException: Exception cause } ? cause : ex;
                    ModLog.Warn($"Event channel: {key} failed: {ModLog.SafeErrorText(inner)}{DescribeReferences()}");
                }

                if (_missing.NoteFailure(busEvent.Name, listenerClass, objectsMissing))
                {
                    ModLog.Info($"Event channel: {key} is skipped from now on while a {busEvent.Name} names an object not found here (it failed without it).");
                }
            }
            finally
            {
                _delivering = false;
                double micros = CostMeter.MicrosSince(callStart);
                if (micros > _lastListenerMicros)
                {
                    // The slowest listener of this delivery, for the cost report (StreamPlayer's slowest delivery).
                    _lastListenerMicros = micros;
                    _lastListener = listenerClass;
                }
            }
        }

        _noListener += any ? 0 : 1;
    }

    /// <inheritdoc />
    public void End(bool matchLoaded)
    {
        // Everything here only lets go of what playback took (counters, detached listeners), so it's the same whether the
        // match is still loaded or not: listeners of destroyed owners aren't put back either way.
        if (!_begun)
        {
            return;
        }

        _begun = false;
        LogicDetached = false;
        int strayCounters = 0;
        foreach ((BusEvent busEvent, Il2CppSystem.Delegate listener) in _counters)
        {
            Safely(() => busEvent.Event.UnregisterDelegate(listener));
            Safely(() =>
            {
                // The game's Unregister finds by equality; make sure it was ours that went.
                Il2CppSystem.Collections.Generic.List<Il2CppSystem.Delegate>? listeners = busEvent.Event.m_Listeners;
                int at = listeners == null ? -1 : IndexOf(listeners, listener);
                if (at >= 0)
                {
                    listeners![at] = null!;
                    busEvent.Event.m_PersistentListeners?.Remove(listener);
                    strayCounters++;
                }
            });
            ListenerFactory.Release(listener);
        }

        if (strayCounters > 0)
        {
            ModLog.Warn($"Event channel: {strayCounters} of the mod's own counters were still subscribed after unsubscribing; removed by hand.");
        }

        _counters.Clear();
        _ownListeners.Clear();
        var outcomes = new Dictionary<Outcome, int>();
        var notBackIn = new Dictionary<Outcome, SortedSet<string>>();
        // Newest first: a listener detached twice (it subscribed again during playback) goes back in its newer slot.
        for (int i = _detached.Count - 1; i >= 0; i--)
        {
            Outcome outcome = Restore(_detached[i]);
            if (_detached[i] is { Placeholder: Il2CppSystem.Delegate plain, EqualStandIn: false })
            {
                // A plain stand-in made for this playback: done with (an equal one isn't hand-built by the factory).
                ListenerFactory.Release(plain);
            }

            outcomes[outcome] = outcomes.GetValueOrDefault(outcome) + 1;
            if (outcome != Outcome.Restored)
            {
                (notBackIn.TryGetValue(outcome, out SortedSet<string>? names) ? names : notBackIn[outcome] = new SortedSet<string>(StringComparer.Ordinal))
                    .Add($"{_detached[i].Event.Name} ({Listeners.OwnerOf(_detached[i].Listener)})");
            }
        }

        _redeliverTo = _detached.Where(d => _redeliver.Any(r => r.Event.Id == d.Event.Id)).ToList();
        _detached.Clear();
        string lookups = _refCache.Budget.Describe();
        long deferredLookups = _refCache.Deferred;
        _refCache.Clear();
        foreach ((string name, int count) in _localRaises.Where(p => !_list.LocalEvents.Contains(p.Key) && !_list.MenuEvents.ContainsKey(p.Key)))
        {
            // Something local raised a gameplay event: game logic still running, or presentation the list doesn't know.
            if (count > _expectedRaises.GetValueOrDefault(name))
            {
                Client.ClientMode.CountLeak($"{name} raised locally", count - _expectedRaises.GetValueOrDefault(name));
            }
        }

        string failures = _failures.Count == 0 ? "none" : string.Join(", ", _failures.OrderByDescending(p => p.Value).Take(10).Select(p => $"{p.Key} ×{p.Value}"));
        string local = _localRaises.Count == 0 ? "none" : string.Join(", ", _localRaises.OrderByDescending(p => p.Value).Take(12).Select(p => $"{p.Key} ×{p.Value}"));
        ModLog.Info($"Event channel: {_delivered} events delivered ({_calls} listener calls; {_noListener} had no presentation listener; {_skippedLocal} " +
            $"left to local presentation code; {_unknown} unknown; {_undecodable} undecodable" +
            $"; reference lookups: {lookups}{(deferredLookups > 0 ? " (held back: their listeners got nothing for them)" : string.Empty)}" +
            $"{(_unresolvedEvents.Count > 0 ? $"; events naming an object not found here: {string.Join(", ", _unresolvedEvents.OrderByDescending(p => p.Value).Take(8).Select(p => $"{p.Key} ×{p.Value}"))}" : string.Empty)}" +
            $"{(_collisionSkips.Count > 0 ? $"; collision effects skipped (the hit object isn't here): {string.Join(", ", _collisionSkips.Select(p => $"{p.Key} ×{p.Value}"))}" : string.Empty)}" +
            $"{(_skippedForMissing.Count > 0 ? $"; not handed an object not found here: {string.Join(", ", _skippedForMissing.OrderByDescending(p => p.Value).Take(8).Select(p => $"{p.Key} ×{p.Value}"))}" : string.Empty)}). " +
            $"Listener failures: {failures}.");
        string notBack = string.Join(", ", outcomes.Where(p => p.Key != Outcome.Restored)
            .Select(p => $"{p.Value} {Describe(p.Key)}: {string.Join(", ", notBackIn[p.Key].Take(4))}{(notBackIn[p.Key].Count > 4 ? ", ..." : string.Empty)}"));
        ModLog.Info($"Event channel: raised locally during playback: {local}. Logic listeners put back: {outcomes.GetValueOrDefault(Outcome.Restored)}" +
            $"{(_lateDetached > 0 ? $" ({_lateDetached} of them subscribed during playback and were detached when their event was raised here)" : string.Empty)}" +
            $"{(notBack.Length > 0 ? $"; not put back: {notBack}" : string.Empty)}; {StructListeners.Describe()}.");
    }

    private void ResetCounts()
    {
        _byStreamId.Clear();
        _mappedEntries = _mismatched = 0;
        _detached.Clear();
        _refCache.Clear();
        _failures.Clear();
        _localRaises.Clear();
        _detachedByClass.Clear();
        _called.Clear();
        _redeliver.Clear();
        _references.Clear();
        _unresolvedEvents.Clear();
        _collisionSkips.Clear();
        _skippedForMissing.Clear();
        _missing.Clear();
        _expectedRaises.Clear();
        _expectedRaise = null;
        _delivered = _calls = _skippedLocal = _unknown = _undecodable = _decodeFailures = _noListener = _plainStandIns = _lateDetached = 0;
        _unresolvedLogged = _collisionSkipsLogged = 0;
    }

    /// <summary>
    /// The host's arguments named objects this delivery couldn't find here (a reference the host sent as an object, found as
    /// nothing): counted per event, the first few logged with what they were and why (held back by the lookup budget, or not
    /// on this PC). True when there were any (<see cref="MissingObjects"/>): an event whose argument is itself such an
    /// object goes to no listener (each would be handed null for it); one with such an object in an argument's field still
    /// goes to every listener (skipping it would also drop what the others show: a goal's lights and crowd for a missing
    /// scorer), except a listener that already threw on it. A collision missing a collider is handled per listener
    /// (<see cref="CollisionCheck"/>).
    /// </summary>
    private bool NoteUnresolved(BusEvent busEvent, bool heldBack)
    {
        int missing = 0;
        foreach ((NetValue _, object? found) in _references)
        {
            missing += found == null ? 1 : 0;
        }

        if (missing == 0)
        {
            return false;
        }

        _unresolvedEvents[busEvent.Name] = _unresolvedEvents.GetValueOrDefault(busEvent.Name) + 1;
        if (_unresolvedLogged++ < LoggedPerPlayback)
        {
            IEnumerable<string> names = _references.Where(r => r.Found == null).Take(4).Select(r => DescribeValue(r.Value));
            ModLog.Info($"Event channel: {busEvent.Name} names {missing} object(s) not found here ({string.Join(", ", names)}; " +
                $"{(heldBack ? "a lookup was held back by the budget" : "not on this PC")}); no listener is handed nothing for an argument that is such an object, " +
                $"and one that failed without {(missing == 1 ? "it" : "them")} is skipped{(_unresolvedLogged == LoggedPerPlayback ? " (further ones are counted)" : string.Empty)}.");
        }

        return true;
    }

    /// <summary>Counts a delivery (or one listener's) left out because an object it names isn't here.</summary>
    private void SkipForMissing(string eventName, string? owner)
    {
        string key = owner == null ? eventName : $"{eventName} → {owner}";
        _skippedForMissing[key] = _skippedForMissing.GetValueOrDefault(key) + 1;
    }

    /// <summary>
    /// The collider listener is skipped for a collision missing its hit collider here (<see cref="CollisionCheck"/>): its
    /// effect needs the real collider, and the throw would stop the rest of its UnityEvent. Counted, the first few logged.
    /// </summary>
    private void SkipCollision(BusEvent busEvent, string listenerClass)
    {
        _collisionSkips[busEvent.Name] = _collisionSkips.GetValueOrDefault(busEvent.Name) + 1;
        if (_collisionSkipsLogged++ < LoggedPerPlayback)
        {
            string members = string.Join(", ", CompoundValues.MissingMembers.Take(2).Select(DescribeValue));
            ModLog.Info($"Event channel: a {busEvent.Name}'s hit object isn't on this PC ({members}); {listenerClass}, whose effect needs it, is skipped for it " +
                $"(the other listeners get it){(_collisionSkipsLogged == LoggedPerPlayback ? "; further ones are counted" : string.Empty)}.");
        }
    }

    /// <summary>
    /// For a listener's first failure: what each reference in the arguments was found as here, a skater with its team and
    /// data (a game handler throws on a skater without them: SoundManager.OnChargingSlapshotStart, OnGoalScored), so the
    /// log tells a reference not found here from local data missing.
    /// </summary>
    private string DescribeReferences()
    {
        if (_references.Count == 0)
        {
            return string.Empty;
        }

        try
        {
            IEnumerable<string> parts = _references.Take(6).Select(r => $"{DescribeValue(r.Value)} as {DescribeFound(r.Found)}");
            return $" (its references here: {string.Join(", ", parts)}{(_references.Count > 6 ? ", ..." : string.Empty)})";
        }
        catch (Exception ex)
        {
            return $" (its references couldn't be described: {ModLog.SafeErrorText(ex)})";
        }
    }

    /// <summary>
    /// A reference for a log line: a stream object by its id, a scene path or an asset's name as the host sent it, with a
    /// skater's name in it written as its shape (<see cref="LogNames.ReferenceText"/>).
    /// </summary>
    private static string DescribeValue(NetValue value) =>
        value.Kind == NetValueKind.Reference ? $"stream object {value.WholeValue}" : SafeText.Clean(LogNames.ReferenceText(value.Text ?? "?"), 80);

    private static string DescribeFound(object? found)
    {
        if (found is not UnityEngine.Object unityObject || unityObject == null)
        {
            return found == null ? "nothing (not found)" : found.GetType().Name;
        }

        if (unityObject.TryCast<Il2CppSkaters.Skater>() is not Il2CppSkaters.Skater skater)
        {
            // A component or an object by its object's name (a skater's by its asset id), an asset by its own name.
            string name = unityObject.TryCast<UnityEngine.Component>() is UnityEngine.Component component ? LogNames.ObjectName(component)
                : unityObject.TryCast<UnityEngine.GameObject>() is UnityEngine.GameObject gameObject ? LogNames.ObjectName(gameObject)
                : LogNames.ReferenceText(unityObject.name ?? "?");
            return SafeText.Clean(name, 48);
        }

        Il2CppSkaters.Forward? forward = skater.TryCast<Il2CppSkaters.Forward>();
        Il2CppTape2Tape.Team? team = skater._team;
        string teamText = team == null ? "no team here" : team._teamData == null ? "its team has no data here" : "with its team";
        string data = forward == null ? string.Empty : forward._forwardData == null ? ", no skater data here" : string.Empty;
        return $"{SafeText.Clean(LogNames.ObjectName(skater), 48)} ({teamText}{data})";
    }

    /// <summary>Maps the stream's event table onto this build's events (again when a live host's table has grown). True if it changed.</summary>
    private bool MapEvents()
    {
        if (_source == null || _source.Events.Count == _mappedEntries)
        {
            return false;
        }

        _mappedEntries = _source.Events.Count;
        Dictionary<string, BusEvent> local = EventCatalog.ByName();
        foreach (EventEntry entry in _source.Events.Values)
        {
            if (_byStreamId.ContainsKey(entry.EventId) || !local.TryGetValue(entry.Name, out BusEvent? busEvent))
            {
                continue;
            }

            if (!entry.ArgFields.SequenceEqual(busEvent.ValueNames, StringComparer.Ordinal))
            {
                if (_mismatched++ < 5)
                {
                    // The host's field names are the host's text: counted, never written (this build's own are).
                    ModLog.Warn($"Event channel: {entry.Name}'s arguments differ from this build's ({HostText.FieldsDifference(entry.ArgFields, busEvent.ValueNames)}); not delivered.");
                }

                continue;
            }

            _byStreamId[entry.EventId] = busEvent;
        }

        return true;
    }

    /// <summary>True for a listener class that gets the host's events: presentation, or one <see cref="AlsoDeliverTo"/> adds.</summary>
    private bool Delivered(string owner) => _list.Listeners.Contains(owner) || AlsoDeliverTo?.Invoke(owner) == true;

    private void DetachLogic(BusEvent busEvent)
    {
        Il2CppSystem.Collections.Generic.List<Il2CppSystem.Delegate>? listeners = busEvent.Event.m_Listeners;
        if (listeners == null)
        {
            return;
        }

        var logic = new List<(Il2CppSystem.Delegate Listener, string Owner)>();
        for (int i = 0; i < listeners.Count; i++)
        {
            Il2CppSystem.Delegate? listener = listeners[i];
            if (listener == null || _ownListeners.Contains(listener.Pointer) || ModListeners.Contains(listener.Pointer))
            {
                continue;
            }

            string owner = Listeners.OwnerOf(listener);
            if (!Delivered(owner))
            {
                logic.Add((listener, owner));
            }
        }

        foreach ((Il2CppSystem.Delegate listener, string owner) in logic)
        {
            Detach(busEvent, listeners, listener, owner);
        }
    }

    /// <summary>
    /// Takes the listener out of its slot. The slot gets a stand-in (see the class remarks): one the game takes for the
    /// listener, else a plain one where the event's delegate class allows it; otherwise it's emptied, as the game's own
    /// Unregister does, and the index is kept.
    /// </summary>
    private void Detach(BusEvent busEvent, Il2CppSystem.Collections.Generic.List<Il2CppSystem.Delegate> listeners, Il2CppSystem.Delegate listener, string owner)
    {
        int index = IndexOf(listeners, listener);
        if (index < 0)
        {
            return;
        }

        bool persistent = false;
        Il2CppSystem.Delegate? placeholder = null;
        bool equal = false;
        try
        {
            persistent = busEvent.Event.m_PersistentListeners?.Contains(listener) == true;
            placeholder = StandIns.EqualTo(listener);
            equal = placeholder != null;
            if (placeholder == null && busEvent.ActionType != null)
            {
                // A delegate of its own: the persistent set needs it alongside the listener to keep it through a wipe.
                placeholder = ListenerFactory.Create(busEvent.ActionType, busEvent.ArgTypes, static _ => { });
                if (persistent)
                {
                    busEvent.Event.m_PersistentListeners!.Add(placeholder);
                }

                _plainStandIns++;
            }

            if (placeholder != null)
            {
                _ownListeners.Add(placeholder.Pointer);
            }
        }
        catch (Exception ex)
        {
            placeholder = null;
            equal = false;
            ModLog.Warn($"Event channel: no stand-in for a {busEvent.Name} listener ({ModLog.SafeErrorText(ex.InnerException ?? ex)}); its slot is emptied instead.");
        }

        listeners[index] = placeholder!;
        _detached.Add(new Detached(busEvent, listener, placeholder, persistent, index, equal));
        _detachedByClass[owner] = _detachedByClass.GetValueOrDefault(owner) + 1;
    }

    private static int IndexOf(Il2CppSystem.Collections.Generic.List<Il2CppSystem.Delegate> listeners, Il2CppSystem.Delegate wanted)
    {
        for (int i = 0; i < listeners.Count; i++)
        {
            if (listeners[i]?.Pointer == wanted.Pointer)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Swaps a detached listener back in for its stand-in, or says why it stays out.</summary>
    private static Outcome Restore(Detached detached)
    {
        try
        {
            Il2CppSystem.Collections.Generic.List<Il2CppSystem.Delegate>? listeners = detached.Event.Event.m_Listeners;
            if (listeners == null)
            {
                return Outcome.Failed;
            }

            if (detached.Placeholder is Il2CppSystem.Delegate placeholder)
            {
                if (detached.Persistent && !detached.EqualStandIn)
                {
                    // Only a plain stand-in went into the persistent set (an equal one would be taken for the listener there).
                    detached.Event.Event.m_PersistentListeners?.Remove(placeholder);
                }

                int slot = IndexOf(listeners, placeholder);
                if (slot < 0)
                {
                    // An equal stand-in goes when its owner unsubscribes the listener, or with a wipe that would have taken
                    // the listener; a plain one only with a wipe of the event's non-persistent listeners.
                    return detached.EqualStandIn ? Outcome.Left : Outcome.Wiped;
                }

                listeners[slot] = null!;
                if (OwnerDestroyed(detached.Listener))
                {
                    return Outcome.OwnerDestroyed;
                }

                if (listeners.Contains(detached.Listener))
                {
                    return Outcome.Resubscribed;
                }

                listeners[slot] = detached.Listener;
                return Outcome.Restored;
            }

            // No stand-in: back into its old slot if that's still empty, else as near as the list allows.
            if (OwnerDestroyed(detached.Listener))
            {
                return Outcome.OwnerDestroyed;
            }

            if (listeners.Contains(detached.Listener))
            {
                return Outcome.Resubscribed;
            }

            if (detached.Index < listeners.Count && listeners[detached.Index] == null)
            {
                listeners[detached.Index] = detached.Listener;
            }
            else
            {
                listeners.Insert(Math.Min(detached.Index, listeners.Count), detached.Listener);
            }

            return Outcome.Restored;
        }
        catch (Exception ex)
        {
            ModLog.Warn($"Event channel: couldn't put a {detached.Event.Name} listener back: {ModLog.SafeErrorText(ex)}");
            return Outcome.Failed;
        }
    }

    private static bool OwnerDestroyed(Il2CppSystem.Delegate listener) =>
        listener.m_target?.TryCast<UnityEngine.Object>() is UnityEngine.Object owner && owner == null;

    private static string Describe(Outcome outcome) => outcome switch
    {
        Outcome.OwnerDestroyed => "owners destroyed",
        Outcome.Resubscribed => "subscribed again during playback (kept in their newer slot)",
        Outcome.Wiped => "wiped by the game during playback",
        Outcome.Left => "left during playback (unsubscribed by their owners, or wiped by the game)",
        _ => "failed",
    };

    /// <summary>A persistent counter on every event: raises that don't come from the stream are local, and go in the audit.</summary>
    private void CountLocalRaises()
    {
        foreach (BusEvent busEvent in EventCatalog.All)
        {
            if (busEvent.ActionType == null)
            {
                continue;
            }

            try
            {
                string name = busEvent.Name;
                BusEvent captured = busEvent;
                Il2CppSystem.Delegate counter = ListenerFactory.Create(busEvent.ActionType, busEvent.ArgTypes, args =>
                {
                    if (_delivering)
                    {
                        return;
                    }

                    _localRaises[name] = _localRaises.GetValueOrDefault(name) + 1;
                    if (name == _expectedRaise)
                    {
                        _expectedRaise = null;
                        _expectedRaises[name] = _expectedRaises.GetValueOrDefault(name) + 1;
                    }
                    else if (OwnRaising?.Invoke() == true)
                    {
                        // This player's own shared progress applying (a run's end, its last match still loaded): not a leak.
                        _expectedRaises[name] = _expectedRaises.GetValueOrDefault(name) + 1;
                    }
                    else if (_list.MenuEvents.TryGetValue(name, out string? action))
                    {
                        if (action == "leave")
                        {
                            // The game's own handlers for this were detached; they get it once they're back.
                            _redeliver.Add((captured, args));
                        }

                        MenuAction?.Invoke(action);
                    }
                    else if (!_list.LocalEvents.Contains(name) && !_holding())
                    {
                        // A gameplay event raised here: logic that subscribed during playback has heard it (the game calls a
                        // copy of the list), but won't again. Inside the raise, which iterates a copy: safe to change the list.
                        int before = _detached.Count;
                        DetachLogic(captured);
                        _lateDetached += _detached.Count - before;
                    }
                });
                busEvent.Event.RegisterPersistentDelegate(counter, "T2TOnline");
                _ownListeners.Add(counter.Pointer);
                _counters.Add((busEvent, counter));
            }
            catch (Exception ex)
            {
                ModLog.Warn($"Event channel: can't count local raises of {busEvent.Name}: {ModLog.SafeErrorText(ex.InnerException ?? ex)}");
            }
        }
    }

    /// <summary>
    /// After client mode is off: a leave event raised during playback goes to the game's own listeners that were detached
    /// when it happened (the match's exit handling), as the game's safe invocation would: in list order, each guarded.
    /// </summary>
    public void Finish()
    {
        List<Detached> detached = _redeliverTo;
        _redeliverTo = new List<Detached>();
        foreach ((BusEvent busEvent, object?[] args) in _redeliver)
        {
            // Only listeners that are back in the list, in its order (a destroyed or wiped one wasn't put back).
            Il2CppSystem.Collections.Generic.List<Il2CppSystem.Delegate>? current = busEvent.Event.m_Listeners;
            List<Detached> listeners = current == null ? new List<Detached>() : detached
                .Where(d => d.Event.Id == busEvent.Id)
                .Select(d => (Detached: d, At: IndexOf(current, d.Listener)))
                .Where(p => p.At >= 0)
                .GroupBy(p => p.At)
                .Select(g => g.First())
                .OrderBy(p => p.At)
                .Select(p => p.Detached)
                .ToList();
            foreach (Detached listener in listeners)
            {
                try
                {
                    busEvent.Call(listener.Listener, args);
                }
                catch (Exception ex)
                {
                    Exception inner = ex is System.Reflection.TargetInvocationException { InnerException: Exception cause } ? cause : ex;
                    ModLog.Warn($"Event channel: {busEvent.Name} -> {Listeners.OwnerOf(listener.Listener)} failed after playback: {ModLog.SafeErrorText(inner)}");
                }
            }

            ModLog.Info($"Event channel: {busEvent.Name} was raised during playback; given to the game's {listeners.Count} own listeners now that they're back.");
        }

        _redeliver.Clear();
    }

    private static void Safely(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            ModLog.Warn($"Event channel: {ModLog.SafeErrorText(ex)}");
        }
    }

    /// <summary>
    /// A detached listener: its stand-in (null when the slot was emptied instead), whether it was persistent, its slot
    /// then, and whether the stand-in is one the game takes for the listener (<see cref="StandIns.EqualTo"/>).
    /// </summary>
    private readonly record struct Detached(BusEvent Event, Il2CppSystem.Delegate Listener, Il2CppSystem.Delegate? Placeholder, bool Persistent, int Index, bool EqualStandIn);

    private enum Outcome
    {
        Restored,
        OwnerDestroyed,
        Resubscribed,
        Wiped,
        Left,
        Failed,
    }
}

/// <summary>Who a listener belongs to.</summary>
internal static class Listeners
{
    private static readonly Dictionary<IntPtr, string> ClassNames = new();

    /// <summary>
    /// The listener's class by the game's own name, outermost declaring type first ("SoundManager" for a lambda compiled
    /// into SoundManager+&lt;&gt;c). From the handler method; from the target object for the rare delegate without one.
    /// </summary>
    public static string OwnerOf(Il2CppSystem.Delegate listener)
    {
        IntPtr method = listener.method;
        IntPtr klass = method != IntPtr.Zero ? IL2CPP.il2cpp_method_get_class(method) : IntPtr.Zero;
        if (klass == IntPtr.Zero && listener.m_target != null)
        {
            klass = IL2CPP.il2cpp_object_get_class(listener.m_target.Pointer);
        }

        if (klass == IntPtr.Zero)
        {
            return "?";
        }

        if (ClassNames.TryGetValue(klass, out string? name))
        {
            return name;
        }

        IntPtr outer = klass;
        for (IntPtr declaring = IL2CPP.il2cpp_class_get_declaring_type(outer); declaring != IntPtr.Zero; declaring = IL2CPP.il2cpp_class_get_declaring_type(outer))
        {
            outer = declaring;
        }

        string? space = IL2CPP.il2cpp_class_get_namespace_(outer);
        string simple = IL2CPP.il2cpp_class_get_name_(outer) ?? "?";
        name = string.IsNullOrEmpty(space) ? simple : $"{space}.{simple}";
        ClassNames[klass] = name;
        return name;
    }
}

/// <summary>The embedded presentation.txt: allowed listener classes and locally raised events.</summary>
internal sealed class PresentationList
{
    private static bool _checked;

    private PresentationList(HashSet<string> listeners, HashSet<string> localEvents, Dictionary<string, string> menuEvents)
    {
        Listeners = listeners;
        LocalEvents = localEvents;
        MenuEvents = menuEvents;
    }

    /// <summary>An empty list (nothing allowed).</summary>
    public static PresentationList Empty { get; } = new(new HashSet<string>(), new HashSet<string>(), new Dictionary<string, string>());

    /// <summary>Classes whose listeners get the host's events.</summary>
    public IReadOnlySet<string> Listeners { get; }

    /// <summary>Events presentation code raises itself on a friend's PC.</summary>
    public IReadOnlySet<string> LocalEvents { get; }

    /// <summary>Events the player's own pause menu raises, with what playback does: "pause", "resume" or "leave".</summary>
    public IReadOnlyDictionary<string, string> MenuEvents { get; }

    /// <summary>Loads and checks the embedded list (unknown classes are logged once).</summary>
    public static PresentationList LoadEmbedded()
    {
        using Stream stream = typeof(PresentationList).Assembly.GetManifestResourceStream("T2TOnline.presentation.txt")
            ?? throw new InvalidOperationException("Embedded resource T2TOnline.presentation.txt is missing.");
        using var reader = new StreamReader(stream);
        PresentationList list = Parse(reader.ReadToEnd());
        if (!_checked)
        {
            _checked = true;
            string[] missing = list.Listeners.Where(name => Il2CppTypeLocator.Find(name) == null).ToArray();
            if (missing.Length > 0)
            {
                ModLog.Warn($"presentation.txt: {missing.Length} classes aren't in this build: {string.Join(", ", missing)}.");
            }
        }

        return list;
    }

    /// <summary>Parses presentation.txt text: [listeners] class names, [local] event names, [menu] event names and actions.</summary>
    public static PresentationList Parse(string text)
    {
        var listeners = new HashSet<string>(StringComparer.Ordinal);
        var local = new HashSet<string>(StringComparer.Ordinal);
        var menu = new Dictionary<string, string>(StringComparer.Ordinal);
        HashSet<string>? section = null;
        bool inMenu = false;
        int lineNumber = 0;
        foreach (string raw in text.Split('\n'))
        {
            lineNumber++;
            string line = raw.Split('#')[0].Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (line.StartsWith('['))
            {
                inMenu = line == "[menu]";
                section = line switch
                {
                    "[listeners]" => listeners,
                    "[local]" => local,
                    "[menu]" => null,
                    _ => throw new FormatException($"presentation.txt:{lineNumber}: unknown section {line}."),
                };
                continue;
            }

            if (inMenu)
            {
                string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length != 2 || parts[1] is not ("pause" or "resume" or "leave"))
                {
                    throw new FormatException($"presentation.txt:{lineNumber}: a [menu] line is an event name, then pause, resume or leave.");
                }

                menu[parts[0]] = parts[1];
                continue;
            }

            (section ?? throw new FormatException($"presentation.txt:{lineNumber}: an entry before any section.")).Add(line);
        }

        return new PresentationList(listeners, local, menu);
    }
}
#endif
