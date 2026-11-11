#if HAS_INTEROP
using System;
using System.Collections.Generic;
using System.Linq;
using T2TOnline.Core.Diagnostics;
using T2TOnline.Core.Protocol;
using T2TOnline.Core.Serialization;
using T2TOnline.Plugin.Diagnostics;

namespace T2TOnline.Plugin.Streaming;

/// <summary>
/// Host side of the event bridge: listens to every event on the game's buses and writes each raise,
/// with its arguments, into the stream.
/// <list type="bullet">
/// <item>Listeners are persistent (<c>RegisterPersistentDelegate</c>): the game wipes ordinary listeners at every match
/// init, on GameManager's destroy, and in Puck/GoalReplay.OnDestroy.</item>
/// <item>They're added last, so arguments are read after the game's own listeners have changed them (a shot turning
/// critical, a save being overridden).</item>
/// <item><see cref="BeforeEvent"/> lets the HUD channel write its values first, so a friend's scoreboard already has the new
/// score when the goal event arrives.</item>
/// </list>
/// </summary>
internal sealed class EventRecorder : IDisposable
{
    private readonly StreamRecorder _recorder;
    private readonly Func<UnityEngine.Object, NetValue> _refOf;
    private readonly List<(BusEvent Event, Il2CppSystem.Delegate Listener)> _listeners = new();
    private readonly Dictionary<ushort, int> _counts = new();
    private readonly CostMeter _cost = new("event");
    private int _failures;
    private int _depth;

    public EventRecorder(StreamRecorder recorder)
    {
        _recorder = recorder;
        // Made once: every raise encodes its references through it.
        Func<Il2CppUltimateReplay.ReplayObject, ushort> netIdOf = recorder.NetIdOf;
        _refOf = value => ObjectRefs.ToNet(value, netIdOf, recorder.Paths);
    }

    /// <summary>Raised just before an event is written, with its name.</summary>
    public event Action<BusEvent>? BeforeEvent;

    /// <summary>Events written since <see cref="Start"/>.</summary>
    public int Written { get; private set; }

    /// <summary>What recording one raise costs inside the game's event (its before-event HUD sample included).</summary>
    public CostMeter Cost => _cost;

    /// <summary>Writes the event table and starts listening. Call right after the stream recorder starts.</summary>
    public void Start()
    {
        Stop(log: false);
        // An argument class flattened into more fields than a message carries is left out (none in this build).
        List<BusEvent> events = EventCatalog.All.Where(e => e.ValueNames.Count <= EventTableMessage.MaxFields).ToList();
        var entries = events.Select(e => new EventEntry(e.Id, e.Name, e.ValueNames)).ToList();
        for (int i = 0; i < entries.Count; i += EventTableMessage.MaxEntries)
        {
            List<EventEntry> part = entries.GetRange(i, Math.Min(EventTableMessage.MaxEntries, entries.Count - i));
            _recorder.Write(MessageType.EventTable, Channel.Events, w => EventTableMessage.Write(w, part));
        }

        int failed = 0;
        foreach (BusEvent busEvent in events)
        {
            if (busEvent.ActionType == null)
            {
                continue;
            }

            try
            {
                BusEvent captured = busEvent;
                Il2CppSystem.Delegate listener = ListenerFactory.Create(busEvent.ActionType, busEvent.ArgTypes, args => OnRaised(captured, args));
                busEvent.Event.RegisterPersistentDelegate(listener, "T2TOnline");
                _listeners.Add((busEvent, listener));
            }
            catch (Exception ex)
            {
                if (failed++ < 5)
                {
                    ModLog.Warn($"Event recorder: can't listen to {busEvent.Name}: {ModLog.SafeErrorText(ex.InnerException ?? ex)}");
                }
            }
        }

        Written = 0;
        _counts.Clear();
        _cost.Clear();
        _failures = 0;
        int tooWide = EventCatalog.All.Count - events.Count;
        ModLog.Info($"Event recorder: listening to {_listeners.Count} of {EventCatalog.All.Count} events{(failed > 0 ? $" ({failed} couldn't be listened to)" : string.Empty)}" +
            $"{(tooWide > 0 ? $" ({tooWide} with over {EventTableMessage.MaxFields} argument fields left out)" : string.Empty)}.");
    }

    /// <summary>Stops listening and logs what was recorded.</summary>
    public void Stop(bool log = true)
    {
        foreach ((BusEvent busEvent, Il2CppSystem.Delegate listener) in _listeners)
        {
            try
            {
                busEvent.Event.UnregisterDelegate(listener);
            }
            catch (Exception ex)
            {
                ModLog.Warn($"Event recorder: couldn't stop listening to {busEvent.Name}: {ModLog.SafeErrorText(ex)}");
            }

            // A hand-built one's callback goes too: should it still be on the event, a later raise does nothing.
            ListenerFactory.Release(listener);
        }

        bool had = _listeners.Count > 0;
        _listeners.Clear();
        if (!log || !had)
        {
            return;
        }

        Dictionary<ushort, string> names = EventCatalog.All.ToDictionary(e => e.Id, e => e.Name);
        string top = string.Join(", ", _counts.OrderByDescending(p => p.Value).Take(12).Select(p => $"{names.GetValueOrDefault(p.Key, "?")} {p.Value}"));
        ModLog.Info($"Event recorder: stopped. {Written} raises of {_counts.Count} different events{(_failures > 0 ? $", {_failures} couldn't be read" : string.Empty)}. Most frequent: {top}.");
    }

    /// <inheritdoc />
    public void Dispose() => Stop();

    private void OnRaised(BusEvent busEvent, object?[] args)
    {
        // Runs inside the game's event raise: never throw.
        if (!_recorder.Recording || _depth > 8)
        {
            return;
        }

        _depth++;
        long start = CostMeter.Start();
        try
        {
            BeforeEvent?.Invoke(busEvent);
            var values = new List<NetValue>(busEvent.ValueNames.Count);
            busEvent.Encode(args, _refOf, values);
            _recorder.WriteNewObjects();
            _recorder.Write(MessageType.GameEvent, Channel.Events, w => GameEventMessage.Write(w, busEvent.Id, values));
            Written++;
            _counts[busEvent.Id] = _counts.GetValueOrDefault(busEvent.Id) + 1;
        }
        catch (Exception ex)
        {
            if (_failures++ < 5)
            {
                ModLog.Warn($"Event recorder: couldn't record {busEvent.Name}: {ModLog.SafeErrorText(ex)}");
            }
        }
        finally
        {
            _depth--;
            _cost.AddSince(start);
        }
    }
}
#endif
