using System;
using System.Collections.Generic;
using T2TOnline.Core.Protocol;
using T2TOnline.Core.Serialization;
using T2TOnline.Core.Session;
using T2TOnline.Core.Snapshots;

namespace T2TOnline.Core.Stream;

/// <summary>A snapshot as it reached this PC.</summary>
/// <param name="Frame">The decoded snapshot.</param>
/// <param name="ArrivalMicros">When it arrived, on the caller's clock (the network layer's receive time, not the frame's).</param>
public readonly record struct ArrivedSnapshot(SnapshotFrame Frame, long ArrivalMicros);

/// <summary>
/// A friend's side of the host's live match stream: the match setup to load, then the tables, snapshots (decoded against
/// the ones already here, acknowledged so the host deltas against them) and every other stream message, queued for the
/// player in arrival order. Untrusted input: anything malformed is counted and dropped, never thrown. Queues are capped,
/// in count and bytes, so a stream nobody plays (the friend left the match) or a hostile host can't grow them without
/// end; each queued message is capped by type, and the object and event tables by their number of ids
/// (<see cref="MaxObjects"/>, <see cref="MaxEvents"/>). Game-independent.
/// </summary>
public sealed class MatchStreamClient
{
    /// <summary>
    /// Most objects one match's table holds: what one snapshot can name (<see cref="SnapshotFrame.MaxObjects"/>; a real
    /// match announces under a hundred). A new id past it is refused; one already in the table still takes a new list.
    /// </summary>
    public const int MaxObjects = SnapshotFrame.MaxObjects;

    /// <summary>
    /// Most events the event table holds (<see cref="EventTableMessage.MaxEntries"/>; the game has under three hundred). A new
    /// id past it is refused; one already in the table is still replaced.
    /// </summary>
    public const int MaxEvents = EventTableMessage.MaxEntries;

    /// <summary>Most snapshots waiting for the player (about 8 s); older ones are dropped first.</summary>
    public const int MaxQueuedSnapshots = 512;

    /// <summary>Most other messages waiting for the player; older ones are dropped first.</summary>
    public const int MaxQueuedMessages = 16_384;

    /// <summary>Most bytes of other messages waiting for the player; older ones are dropped first.</summary>
    public const int MaxQueuedBytes = 4 * 1024 * 1024;

    /// <summary>Longest HUD state message accepted, header included (a full HUD is a few KB).</summary>
    public const int MaxHudStateBytes = 32 * 1024;

    /// <summary>Longest game event message accepted, header included (a few values, at most a 1 KB blob or two).</summary>
    public const int MaxGameEventBytes = 16 * 1024;

    /// <summary>Longest sound message accepted, header included (about 120 bytes at most).</summary>
    public const int MaxSoundCallBytes = 1024;

    /// <summary>Longest rumble message accepted, header included (21 bytes at most).</summary>
    public const int MaxRumbleBytes = 64;

    /// <summary>Longest looping sound message accepted, header included (38 bytes at most).</summary>
    public const int MaxSoundLoopBytes = 64;

    /// <summary>Most objects announced again with other components waiting for the player; more are only counted.</summary>
    public const int MaxChangedObjects = 256;

    private readonly ISessionTransport _transport;
    private readonly SnapshotReceiver _receiver = new();
    private readonly Queue<ArrivedSnapshot> _snapshots = new();
    private readonly Queue<TimedMessage> _messages = new();
    private readonly Dictionary<ushort, ObjectEntry> _objects = new();
    private readonly Dictionary<ushort, EventEntry> _events = new();
    private readonly List<ObjectEntry> _entryScratch = new();
    private readonly List<EventEntry> _eventScratch = new();
    private readonly Queue<ushort> _changed = new();
    private readonly HashSet<ushort> _changedWaiting = new();
    private readonly BitWriter _writer = new(64);
    private readonly ulong[] _sequences = new ulong[256];
    private List<KeyValuePair<string, NetValue>> _setup = new();
    private ulong _readyFor;
    private long _queuedBytes;

    /// <summary>Creates the friend side, sending acknowledgements to <paramref name="host"/> over <paramref name="transport"/>.</summary>
    public MatchStreamClient(ISessionTransport transport, ulong host)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        Host = host;
    }

    /// <summary>The host's Steam ID.</summary>
    public ulong Host { get; }

    /// <summary>The latest match setup's values (empty before the first).</summary>
    public IReadOnlyList<KeyValuePair<string, NetValue>> Setup => _setup;

    /// <summary>The latest match setup's hash, 0 before the first.</summary>
    public ulong SetupHash { get; private set; }

    /// <summary>Goes up by one with every new match setup, so a caller can tell a new match from the same one.</summary>
    public int SetupVersion { get; private set; }

    /// <summary>True once the host said the current match is over.</summary>
    public bool MatchEnded { get; private set; }

    /// <summary>Snapshots dropped for arriving before this PC said it plays the current match.</summary>
    public long EarlySnapshots { get; private set; }

    /// <summary>The match's objects by network id (the host adds entries as objects appear).</summary>
    public IReadOnlyDictionary<ushort, ObjectEntry> Objects => _objects;

    /// <summary>The host's events by stream id.</summary>
    public IReadOnlyDictionary<ushort, EventEntry> Events => _events;

    /// <summary>Snapshot decoding and acknowledgement state.</summary>
    public SnapshotReceiver Receiver => _receiver;

    /// <summary>The host's custom content received this session (custom teams, skaters, goalies and logos), kept across matches.</summary>
    public CustomContentReceiver Content { get; } = new();

    /// <summary>Messages that didn't decode or made no sense.</summary>
    public long Malformed { get; private set; }

    /// <summary>Snapshots and messages dropped because the player wasn't taking them.</summary>
    public long Overflowed { get; private set; }

    /// <summary>Messages refused for being longer than their type allows (<see cref="MaxHudStateBytes"/> and the others).</summary>
    public long Oversized { get; private set; }

    /// <summary>
    /// Objects the host announced again with another component list this match (its game added or removed a skater's
    /// replay component during play), each change counted.
    /// </summary>
    public long ComponentChanges { get; private set; }

    /// <summary>Snapshots waiting for the player.</summary>
    public int QueuedSnapshots => _snapshots.Count;

    /// <summary>Bytes of other messages waiting for the player.</summary>
    public long QueuedBytes => _queuedBytes;

    /// <summary>
    /// Handles one stream message from the host, arrived at <paramref name="arrivalMicros"/>. False when it's
    /// malformed, from someone else, or on the wrong channel.
    /// </summary>
    public bool Receive(ulong sender, Channel channel, ReadOnlySpan<byte> message, long arrivalMicros)
    {
        var reader = new BitReader(message);
        if (sender != Host || !MessageHeader.TryRead(ref reader, out MessageHeader header))
        {
            return Bad();
        }

        switch (header.Type)
        {
            case MessageType.MatchSetup:
                var setup = new List<KeyValuePair<string, NetValue>>();
                if (!MatchSetupMessage.TryRead(ref reader, out ulong hash, setup) || hash != MatchSetupMessage.Hash(setup))
                {
                    return Bad();
                }

                if (hash != SetupHash || MatchEnded)
                {
                    StartMatch(hash, setup);
                }

                return true;
            case MessageType.MatchEnd:
                if (!MatchEndMessage.TryRead(ref reader, out ulong ended))
                {
                    return Bad();
                }

                // 0: the host has no match (we came back after it left one): whatever we have is over.
                MatchEnded |= ended == SetupHash || (ended == 0 && SetupHash != 0);
                return true;
            case MessageType.Snapshot:
                if (channel != Channel.Realtime)
                {
                    return Bad();
                }

                if (_readyFor == 0 || _readyFor != SetupHash)
                {
                    // Not playing this match yet (or a previous match's straggler): decoding it would make it a delta base,
                    // and acknowledging it could name a snapshot the host never sent for this match.
                    EarlySnapshots++;
                    return true;
                }

                long missingBefore = _receiver.MissingBase;
                if (_receiver.TryReceive(ref reader, out SnapshotFrame frame))
                {
                    if (_snapshots.Count >= MaxQueuedSnapshots)
                    {
                        _snapshots.Dequeue();
                        Overflowed++;
                    }

                    _snapshots.Enqueue(new ArrivedSnapshot(frame, arrivalMicros));
                    return true;
                }

                // A missing base isn't the host's fault: the next acknowledgement asks for a keyframe.
                return _receiver.MissingBase > missingBefore || Bad();
            case MessageType.ObjectTable:
                _entryScratch.Clear();
                if (!ObjectTableMessage.TryRead(ref reader, _entryScratch))
                {
                    return Bad();
                }

                bool refused = false;
                foreach (ObjectEntry entry in _entryScratch)
                {
                    if (!_objects.TryGetValue(entry.NetId, out ObjectEntry known))
                    {
                        // A table as full as a snapshot can use: a new id past it is a modified host's.
                        if (_objects.Count >= MaxObjects)
                        {
                            refused = true;
                        }
                        else
                        {
                            _objects[entry.NetId] = entry;
                        }

                        continue;
                    }

                    // An object announced again: its component list changed on the host. Only the list is taken (the object
                    // was found here by its first entry's key), and the player takes it over again (TryTakeChangedObject).
                    if (!ComponentAlignment.SameComponents(known, entry))
                    {
                        _objects[entry.NetId] = known with { Components = entry.Components, ComponentTypes = entry.ComponentTypes };
                        ComponentChanges++;
                        if (_changedWaiting.Count < MaxChangedObjects && _changedWaiting.Add(entry.NetId))
                        {
                            _changed.Enqueue(entry.NetId);
                        }
                    }
                }

                return !refused || Bad();
            case MessageType.EventTable:
                _eventScratch.Clear();
                if (!EventTableMessage.TryRead(ref reader, _eventScratch))
                {
                    return Bad();
                }

                bool full = false;
                foreach (EventEntry entry in _eventScratch)
                {
                    if (_events.Count >= MaxEvents && !_events.ContainsKey(entry.EventId))
                    {
                        full = true; // as for objects: a new id past the cap is a modified host's
                        continue;
                    }

                    _events[entry.EventId] = entry;
                }

                return !full || Bad();
            case MessageType.GameEvent or MessageType.SoundCall or MessageType.HudState or MessageType.Rumble or MessageType.SoundLoop:
                int longest = header.Type switch
                {
                    MessageType.HudState => MaxHudStateBytes,
                    MessageType.GameEvent => MaxGameEventBytes,
                    MessageType.Rumble => MaxRumbleBytes,
                    MessageType.SoundLoop => MaxSoundLoopBytes,
                    _ => MaxSoundCallBytes,
                };
                if (message.Length > longest)
                {
                    Oversized++;
                    return false;
                }

                int offset = (reader.BitPosition + 7) / 8;
                byte[] payload = message[offset..].ToArray();
                while (_messages.Count > 0 && (_messages.Count >= MaxQueuedMessages || _queuedBytes + payload.Length > MaxQueuedBytes))
                {
                    _queuedBytes -= _messages.Dequeue().Payload.Length;
                    Overflowed++;
                }

                _messages.Enqueue(new TimedMessage(header.MatchTimeMillis * 1000L, header.Type, payload));
                _queuedBytes += payload.Length;
                return true;
            case MessageType.CustomContent:
                // Pieces must come in order: only the bulk channel is reliable and ordered for them.
                if (channel != Channel.Bulk || !CustomContentMessage.TryRead(ref reader, out CustomChunk chunk) || !Content.Receive(chunk))
                {
                    return Bad();
                }

                return true;
            default:
                return Bad();
        }
    }

    /// <summary>
    /// The next object the host announced again with another component list (its entry in <see cref="Objects"/> holds the
    /// new one), each once until taken; false when none waits.
    /// </summary>
    public bool TryTakeChangedObject(out ushort netId)
    {
        if (!_changed.TryDequeue(out netId))
        {
            return false;
        }

        _changedWaiting.Remove(netId);
        return true;
    }

    /// <summary>The next snapshot that arrived, oldest first.</summary>
    public bool TryTakeSnapshot(out ArrivedSnapshot snapshot) => _snapshots.TryDequeue(out snapshot);

    /// <summary>The next other message, in the order the host sent them.</summary>
    public bool TryTakeMessage(out TimedMessage message)
    {
        if (!_messages.TryDequeue(out message))
        {
            return false;
        }

        _queuedBytes -= message.Payload.Length;
        return true;
    }

    /// <summary>
    /// Tells the host this PC has loaded the match with <paramref name="setupHash"/> (the one actually loaded, which may
    /// not be the newest setup received), or, with <paramref name="ready"/> false, that it doesn't (any more), with why
    /// when it couldn't load it.
    /// </summary>
    public bool SendReady(ulong setupHash, bool ready, string reason = "")
    {
        _readyFor = ready ? setupHash : 0;
        _writer.Reset();
        new MessageHeader(MessageType.MatchReady, _sequences[(byte)MessageType.MatchReady]++, 0, 0).Write(_writer);
        MatchReadyMessage.Write(_writer, setupHash, ready, reason);
        return _transport.Send(Host, Channel.Events, _writer.AsSpan());
    }

    /// <summary>Tells the host how playout went over the last interval (for its net overlay and the fight grace window).</summary>
    public bool SendReport(in StreamReport report)
    {
        _writer.Reset();
        new MessageHeader(MessageType.StreamReport, _sequences[(byte)MessageType.StreamReport]++, 0, 0).Write(_writer);
        StreamReportMessage.Write(_writer, report);
        return _transport.Send(Host, Channel.Realtime, _writer.AsSpan());
    }

    /// <summary>Call every frame: acknowledges the newest snapshot (or asks for a keyframe) when one is due.</summary>
    public void Update(long nowMicros)
    {
        if (!_receiver.AckDue(nowMicros))
        {
            return;
        }

        _writer.Reset();
        new MessageHeader(MessageType.SnapshotAck, _sequences[(byte)MessageType.SnapshotAck]++, 0, 0).Write(_writer);
        SnapshotAckMessage.Write(_writer, _receiver.Newest, _receiver.NeedsKeyframe);
        _transport.Send(Host, Channel.Realtime, _writer.AsSpan());
    }

    /// <summary>Drops everything queued for the player (playback stopped; the next playback starts from a keyframe).</summary>
    public void DropQueued()
    {
        _snapshots.Clear();
        _messages.Clear();
        _queuedBytes = 0;

        // The next playback maps every object from its newest entry.
        _changed.Clear();
        _changedWaiting.Clear();
    }

    private void StartMatch(ulong hash, List<KeyValuePair<string, NetValue>> setup)
    {
        _setup = setup;
        SetupHash = hash;
        SetupVersion++;
        MatchEnded = false;
        _readyFor = 0;
        _objects.Clear();
        _events.Clear();
        _receiver.Reset();
        ComponentChanges = 0;
        DropQueued();
    }

    private bool Bad()
    {
        Malformed++;
        return false;
    }
}
