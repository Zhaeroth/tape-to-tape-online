using System;
using System.Collections.Generic;
using T2TOnline.Core.Protocol;

namespace T2TOnline.Core.Session;

/// <summary>
/// Keeps the reliable channels reliable when the transport underneath refuses a send (Steam does when a peer's send
/// buffer is full: <c>k_EResultLimitExceeded</c>). A reliable message the inner transport refuses is kept, with everything
/// sent after it to the same peer on the same channel, and they go out in order from <see cref="Flush"/> (call it every
/// frame) as the inner transport takes them: nothing sent on <see cref="Channel.Events"/> or <see cref="Channel.Bulk"/> is
/// lost or reordered, so no sender needs a retry of its own. <see cref="Channel.Realtime"/> goes straight through: a
/// refused realtime message is lost, as any realtime message can be.
/// </summary>
/// <remarks>
/// A peer whose kept messages would come to more than the cap (<see cref="DefaultMaxQueuedBytesPerPeer"/> by default) isn't
/// keeping up: what was kept for it is dropped, every reliable send to it is refused from then on (its reliable channels
/// now have a gap, which must not go unnoticed), and it's reported once by <see cref="TryTakeOverflow"/> so the session
/// can drop it. <see cref="Forget"/> (call it when a peer leaves) starts it afresh. Senders that send as fast as they're
/// allowed (a progress re-send) pace themselves by <see cref="QueuedBytes"/>. Game-independent. Not thread-safe (one game
/// thread).
/// </remarks>
public sealed class RetryingTransport : ISessionTransport
{
    /// <summary>
    /// The default cap on what's kept for one peer: 16 s of Steam's default send rate (256 KB/s) on top of its own 512 KB
    /// buffer, and room for several of the largest reliable messages (run checkpoints and progress batches of up to 500 KB).
    /// </summary>
    public const long DefaultMaxQueuedBytesPerPeer = 4L * 1024 * 1024;

    private readonly ISessionTransport _inner;
    private readonly long _maxQueuedBytesPerPeer;
    private readonly Dictionary<ulong, Backlog> _peers = new();
    private readonly List<ulong> _overflows = new();
    private readonly List<ulong> _scratch = new();

    /// <summary>Wraps <paramref name="inner"/>, keeping at most <paramref name="maxQueuedBytesPerPeer"/> bytes for any one peer.</summary>
    public RetryingTransport(ISessionTransport inner, long maxQueuedBytesPerPeer = DefaultMaxQueuedBytesPerPeer)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        if (maxQueuedBytesPerPeer < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxQueuedBytesPerPeer), "At least one byte.");
        }

        _maxQueuedBytesPerPeer = maxQueuedBytesPerPeer;
    }

    /// <summary>The transport underneath.</summary>
    public ISessionTransport Inner => _inner;

    /// <summary>Reliable messages kept for later: refused by the inner transport, or sent behind one that was.</summary>
    public long Held { get; private set; }

    /// <summary>Kept messages the inner transport took later.</summary>
    public long Resent { get; private set; }

    /// <summary>Kept messages thrown away: a peer's when it overflowed or was forgotten.</summary>
    public long Discarded { get; private set; }

    /// <summary>Times a peer's kept messages would have gone over the cap.</summary>
    public int Overflows { get; private set; }

    /// <summary>Reliable sends refused because their peer overflowed and hasn't been forgotten yet (the overflowing one included).</summary>
    public long RefusedAfterOverflow { get; private set; }

    /// <summary>The most bytes kept for one peer at any time.</summary>
    public long PeakQueuedBytes { get; private set; }

    /// <summary>Bytes kept for <paramref name="peer"/>, waiting for the inner transport (0: everything sent went through).</summary>
    public long QueuedBytes(ulong peer) => _peers.TryGetValue(peer, out Backlog? backlog) ? backlog.Bytes : 0;

    /// <summary>Messages kept for <paramref name="peer"/>, waiting for the inner transport.</summary>
    public int QueuedMessages(ulong peer) => _peers.TryGetValue(peer, out Backlog? backlog) ? backlog.Count : 0;

    /// <summary>True when <paramref name="peer"/> overflowed and hasn't been forgotten since: its reliable sends are refused.</summary>
    public bool HasOverflowed(ulong peer) => _peers.TryGetValue(peer, out Backlog? backlog) && backlog.Overflowed;

    /// <summary>
    /// Sends <paramref name="message"/>. A reliable one goes to the inner transport unless something is already waiting on
    /// its channel to <paramref name="peer"/>; refused, or behind a waiting one, it's kept and true is returned: it will go,
    /// in order. False for a realtime message the inner transport refused, and for a reliable one to a peer that
    /// overflowed (this one may be what overflowed it).
    /// </summary>
    public bool Send(ulong peer, Channel channel, ReadOnlySpan<byte> message)
    {
        if (channel is not (Channel.Events or Channel.Bulk))
        {
            return _inner.Send(peer, channel, message);
        }

        if (!_peers.TryGetValue(peer, out Backlog? backlog))
        {
            if (_inner.Send(peer, channel, message))
            {
                return true;
            }

            backlog = new Backlog();
            _peers[peer] = backlog;
        }
        else if (backlog.Overflowed)
        {
            RefusedAfterOverflow++;
            return false;
        }
        else if (backlog.Waiting(channel).Count == 0 && _inner.Send(peer, channel, message))
        {
            return true;
        }

        if (backlog.Bytes + message.Length > _maxQueuedBytesPerPeer)
        {
            // The peer isn't keeping up. Keeping more would only grow memory and the delay of everything behind it.
            Overflows++;
            RefusedAfterOverflow++;
            Discarded += backlog.Count;
            backlog.Clear();
            backlog.Overflowed = true;
            _overflows.Add(peer);
            return false;
        }

        backlog.Waiting(channel).Enqueue(message.ToArray());
        backlog.Bytes += message.Length;
        backlog.Count++;
        Held++;
        PeakQueuedBytes = Math.Max(PeakQueuedBytes, backlog.Bytes);
        return true;
    }

    /// <summary>
    /// Call every frame (before the frame's sends, so new messages can go straight out once a backlog clears): offers each
    /// peer's kept messages to the inner transport again, oldest first per channel, until it refuses one.
    /// </summary>
    public void Flush()
    {
        if (_peers.Count == 0)
        {
            return;
        }

        _scratch.Clear();
        _scratch.AddRange(_peers.Keys);
        foreach (ulong peer in _scratch)
        {
            Backlog backlog = _peers[peer];
            if (backlog.Overflowed)
            {
                continue;
            }

            Drain(peer, Channel.Events, backlog);
            Drain(peer, Channel.Bulk, backlog);
            if (backlog.Count == 0)
            {
                _peers.Remove(peer);
            }
        }

        _scratch.Clear();
    }

    /// <summary>A peer that overflowed since the last call (each reported once). The session should drop them.</summary>
    public bool TryTakeOverflow(out ulong peer)
    {
        if (_overflows.Count == 0)
        {
            peer = 0;
            return false;
        }

        peer = _overflows[0];
        _overflows.RemoveAt(0);
        return true;
    }

    /// <summary>
    /// Forgets <paramref name="peer"/> (they left the session): what was kept for them is dropped, and a peer that
    /// overflowed is sent to again.
    /// </summary>
    public void Forget(ulong peer)
    {
        if (_peers.Remove(peer, out Backlog? backlog))
        {
            Discarded += backlog.Count;
        }

        _overflows.RemoveAll(p => p == peer);
    }

    /// <summary>Forgets every peer (the session ended).</summary>
    public void Clear()
    {
        foreach (Backlog backlog in _peers.Values)
        {
            Discarded += backlog.Count;
        }

        _peers.Clear();
        _overflows.Clear();
    }

    private void Drain(ulong peer, Channel channel, Backlog backlog)
    {
        Queue<byte[]> waiting = backlog.Waiting(channel);
        while (waiting.Count > 0 && _inner.Send(peer, channel, waiting.Peek()))
        {
            byte[] sent = waiting.Dequeue();
            backlog.Bytes -= sent.Length;
            backlog.Count--;
            Resent++;
        }
    }

    /// <summary>What's kept for one peer, per reliable channel, in the order it was sent.</summary>
    private sealed class Backlog
    {
        private readonly Queue<byte[]> _events = new();
        private readonly Queue<byte[]> _bulk = new();

        public long Bytes { get; set; }

        public int Count { get; set; }

        public bool Overflowed { get; set; }

        public Queue<byte[]> Waiting(Channel channel) => channel == Channel.Bulk ? _bulk : _events;

        public void Clear()
        {
            _events.Clear();
            _bulk.Clear();
            Bytes = 0;
            Count = 0;
        }
    }
}
