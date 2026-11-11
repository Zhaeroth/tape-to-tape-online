using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using T2TOnline.Core.Protocol;
using T2TOnline.Core.Serialization;
using T2TOnline.Core.Snapshots;

namespace T2TOnline.Core.Stream;

/// <summary>What a stream player plays: the host's snapshots and other messages as they reach this PC.</summary>
public interface IStreamSource
{
    /// <summary>For logs: where the stream comes from.</summary>
    string Description { get; }

    /// <summary>
    /// True for a live host: playback keeps going behind this PC's own pause menu (the host's match doesn't stop for it).
    /// False for a recording: the pause menu holds playback.
    /// </summary>
    bool Live { get; }

    /// <summary>The match's objects by network id (a live host adds entries as objects appear).</summary>
    IReadOnlyDictionary<ushort, ObjectEntry> Objects { get; }

    /// <summary>The host's events by stream id.</summary>
    IReadOnlyDictionary<ushort, EventEntry> Events { get; }

    /// <summary>True once nothing more will arrive (the recording ran out, or the host ended the match).</summary>
    bool Finished { get; }

    /// <summary>Snapshots that never arrived (a simulated link's loss; live loss shows in the session's report).</summary>
    long Lost { get; }

    /// <summary>
    /// The next object the host announced again with another component list since the last call (a skater's talent added
    /// or removed a replay component during play); its entry in <see cref="Objects"/> holds the new list. False when none
    /// waits (a recording keeps each object's first list).
    /// </summary>
    bool TryTakeChangedObject(out ushort netId);

    /// <summary>
    /// Moves everything that reached this PC by <paramref name="nowMicros"/> (the player's clock) into the lists:
    /// snapshots with their arrival time, other messages in the host's order.
    /// </summary>
    void Poll(long nowMicros, List<ArrivedSnapshot> snapshots, List<TimedMessage> messages);
}

/// <summary>The network a recording is played through: one-way delay, random extra delay, and loss.</summary>
/// <param name="DelayMs">One-way delay.</param>
/// <param name="JitterMs">Extra delay per snapshot, uniformly 0 to this.</param>
/// <param name="LossPercent">Share of snapshots lost (events, HUD and sounds are reliable: delayed, never lost).</param>
public readonly record struct SimulatedLink(int DelayMs, int JitterMs, int LossPercent)
{
    /// <summary>True when the link changes anything.</summary>
    public bool Active => DelayMs > 0 || JitterMs > 0 || LossPercent > 0;

    /// <inheritdoc />
    public override string ToString() => Active ? $"{DelayMs} ms delay, 0-{JitterMs} ms jitter, {LossPercent}% loss" : "none";
}

/// <summary>
/// A recording played as if it came over the network: every snapshot "arrives" at its time plus the simulated link's
/// delay and jitter (or is lost), and is decoded only then, in file order, so memory stays small however long the
/// recording. The clock starts at 0 with the first snapshot.
/// </summary>
public sealed class FileStreamSource : IStreamSource
{
    private readonly MatchRecording _stream;
    private readonly SimulatedLink _link;
    private readonly long[] _arrival;
    private readonly int[] _arrivalOrder;
    private readonly long[] _messageArrival;
    private readonly SnapshotHistory _history = new(128);
    private readonly Dictionary<int, SnapshotFrame> _decoded = new();
    private int _nextArrival;
    private int _nextDecode;
    private int _nextMessage;

    /// <summary>Plays <paramref name="stream"/> through <paramref name="link"/>.</summary>
    public FileStreamSource(MatchRecording stream, SimulatedLink link)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (stream.Snapshots.Count == 0)
        {
            throw new ArgumentException("The recording has no snapshots.", nameof(stream));
        }

        _stream = stream;
        _link = link;
        var random = new System.Random(1);
        long start = stream.Snapshots[0].HostTimeMicros;
        _arrival = new long[stream.Snapshots.Count];
        for (int i = 0; i < _arrival.Length; i++)
        {
            bool lost = i > 0 && i < _arrival.Length - 1 && random.Next(100) < link.LossPercent;
            Lost += lost ? 1 : 0;
            _arrival[i] = lost ? long.MaxValue : stream.Snapshots[i].HostTimeMicros - start + Delay(random);
        }

        _arrivalOrder = Enumerable.Range(0, _arrival.Length).Where(i => _arrival[i] != long.MaxValue).OrderBy(i => _arrival[i]).ToArray();

        // Events, HUD and sounds go on the reliable, ordered channel: same delay and jitter, never lost, never reordered.
        _messageArrival = new long[stream.Messages.Count];
        long previous = 0;
        for (int i = 0; i < _messageArrival.Length; i++)
        {
            previous = Math.Max(previous, stream.Messages[i].TimeMicros - start + Delay(random));
            _messageArrival[i] = previous;
        }
    }

    /// <inheritdoc />
    public string Description => string.Create(CultureInfo.InvariantCulture, $"a {_stream.Seconds:0.0} s recording over a simulated link: {_link}");

    /// <inheritdoc />
    public bool Live => false;

    /// <inheritdoc />
    public IReadOnlyDictionary<ushort, ObjectEntry> Objects => _stream.Objects;

    /// <inheritdoc />
    public IReadOnlyDictionary<ushort, EventEntry> Events => _stream.Events;

    /// <inheritdoc />
    public bool Finished => _nextArrival >= _arrivalOrder.Length && _nextMessage >= _messageArrival.Length;

    /// <inheritdoc />
    public long Lost { get; }

    /// <inheritdoc />
    public bool TryTakeChangedObject(out ushort netId)
    {
        netId = 0;
        return false;
    }

    /// <summary>Snapshots that failed to decode at playback (should be none: the file was checked when loaded).</summary>
    public int DecodeFailures { get; private set; }

    /// <inheritdoc />
    public void Poll(long nowMicros, List<ArrivedSnapshot> snapshots, List<TimedMessage> messages)
    {
        while (_nextArrival < _arrivalOrder.Length && _arrival[_arrivalOrder[_nextArrival]] <= nowMicros)
        {
            int index = _arrivalOrder[_nextArrival++];
            DecodeThrough(index);
            if (_decoded.Remove(index, out SnapshotFrame? frame))
            {
                snapshots.Add(new ArrivedSnapshot(frame, _arrival[index]));
            }
        }

        while (_nextMessage < _messageArrival.Length && _messageArrival[_nextMessage] <= nowMicros)
        {
            messages.Add(_stream.Messages[_nextMessage++]);
        }
    }

    private long Delay(System.Random random) => (_link.DelayMs * 1000L) + (_link.JitterMs > 0 ? random.Next(_link.JitterMs * 1000) : 0);

    /// <summary>Decodes every snapshot up to <paramref name="index"/> in file order (deltas need their bases); lost ones only as bases.</summary>
    private void DecodeThrough(int index)
    {
        while (_nextDecode <= index)
        {
            int i = _nextDecode++;
            var reader = new BitReader(_stream.Snapshots[i].Message);
            if (!MessageHeader.TryRead(ref reader, out _) || !SnapshotCodec.TryRead(ref reader, _history, out SnapshotFrame frame, out _))
            {
                DecodeFailures++;
                continue;
            }

            _history.Add(frame);
            if (_arrival[i] != long.MaxValue)
            {
                _decoded[i] = frame;
            }
        }
    }
}

/// <summary>The host's live stream, as <see cref="MatchStreamClient"/> receives it (arrival times on the player's clock).</summary>
public sealed class LiveStreamSource : IStreamSource
{
    private readonly MatchStreamClient _client;
    private readonly string _host;

    /// <summary>Plays what <paramref name="client"/> receives from the host named <paramref name="host"/>.</summary>
    public LiveStreamSource(MatchStreamClient client, string host)
    {
        _client = client;
        _host = host;
    }

    /// <summary>The session's stream this plays from: one per session with a host (a rejoin keeps it), so it tells sessions apart.</summary>
    public MatchStreamClient Client => _client;

    /// <inheritdoc />
    public string Description => $"{_host}'s live match";

    /// <inheritdoc />
    public bool Live => true;

    /// <inheritdoc />
    public IReadOnlyDictionary<ushort, ObjectEntry> Objects => _client.Objects;

    /// <inheritdoc />
    public IReadOnlyDictionary<ushort, EventEntry> Events => _client.Events;

    /// <inheritdoc />
    public bool Finished => _client.MatchEnded;

    /// <inheritdoc />
    public long Lost => 0;

    /// <inheritdoc />
    public bool TryTakeChangedObject(out ushort netId) => _client.TryTakeChangedObject(out netId);

    /// <inheritdoc />
    public void Poll(long nowMicros, List<ArrivedSnapshot> snapshots, List<TimedMessage> messages)
    {
        while (_client.TryTakeSnapshot(out ArrivedSnapshot snapshot))
        {
            snapshots.Add(snapshot);
        }

        while (_client.TryTakeMessage(out TimedMessage message))
        {
            messages.Add(message);
        }
    }
}
