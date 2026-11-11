using System;
using System.Collections.Generic;
using T2TOnline.Core.Protocol;
using T2TOnline.Core.Serialization;
using T2TOnline.Core.Session;

namespace T2TOnline.Core.Stream;

/// <summary>What the host's custom content sender finished for one friend (<see cref="CustomContentSender.TryTakeDelivery"/>).</summary>
/// <param name="Friend">The friend.</param>
/// <param name="Items">Items sent to them in full since the last report.</param>
/// <param name="Bytes">Bytes sent to them since the last report (headers included).</param>
/// <param name="Refused">True when the transport refused a piece: what was under way for them was dropped, and goes again from its start with the next send (a rejoin, the next match).</param>
public readonly record struct CustomContentDelivery(ulong Friend, int Items, long Bytes, bool Refused);

/// <summary>How far the custom content queued for one friend has gone (<see cref="CustomContentSender.Progress"/>).</summary>
/// <param name="PendingBytes">Content bytes not handed to the transport yet (0: all of it went).</param>
/// <param name="BatchBytes">Content bytes queued for them since their queue was last empty (the last batch's, once it went).</param>
public readonly record struct ContentProgress(long PendingBytes, long BatchBytes)
{
    /// <summary>The batch's bytes handed to the transport so far.</summary>
    public long SentBytes => Math.Max(0, BatchBytes - PendingBytes);
}

/// <summary>
/// The host's side of custom content (<see cref="CustomContentMessage"/>): each item goes to each friend once a session, in
/// pieces, on the bulk channel (reliable and ordered, so the pieces arrive in order). A friend who rejoins is sent
/// everything again (<see cref="Forget"/>): their game may have restarted. Game-independent.
/// </summary>
/// <remarks>
/// <para>Paced: a match's logos can come to several MB, more than Steam's send buffer takes at once and more
/// than the retrying transport keeps for a friend before it drops them as not keeping up. With <c>queuedBytes</c> (the
/// retrying transport's backlog for a friend), pieces go out only while nothing waits there, and the rest goes from
/// <see cref="Update"/> as the link takes it; each friend has its own queue, so a large item for one never holds up
/// another. A piece the transport refuses (the friend overflowed or left) drops what was under way for that friend: it
/// goes again from its first piece with the next send.</para>
/// <para>A friend's store is bounded (<see cref="CustomContentReceiver.MaxItems"/>,
/// <see cref="CustomContentReceiver.MaxStoredBytes"/>): at an item's first piece it drops its oldest items to make room.
/// The same is counted here, per friend, so an item it dropped is no longer counted as sent: it goes again when a later
/// send names it, and at once when the send that made it drop names it too (once per send, so a send naming more than
/// the store holds can't go round for ever). Without that, a long session's content past the store's bounds left a
/// friend's load waiting for an item the host would never send again.</para>
/// </remarks>
public sealed class CustomContentSender
{
    /// <summary>
    /// The bytes of a friend's store this side counts with: its limit less one largest item, so an item is forgotten here
    /// no later than it's dropped there. What a friend's store holds of items left part way (a refused piece, a rejoin)
    /// can't be counted here: the store takes that room first, before it drops a complete item.
    /// </summary>
    internal const long HeldBudget = CustomContentReceiver.MaxStoredBytes - CustomItem.MaxLogoBytes;

    private readonly ISessionTransport _transport;
    private readonly Func<ulong, long>? _queuedBytes;
    private readonly Dictionary<ulong, Outbox> _friends = new();
    private readonly Queue<CustomContentDelivery> _deliveries = new();
    private readonly List<ulong> _scratch = new();
    private readonly BitWriter _writer = new(CustomContentMessage.MaxChunkBytes + 512);
    private ulong _sequence;

    /// <summary>
    /// Creates the sender over <paramref name="transport"/>. With <paramref name="queuedBytes"/> (bytes the transport holds
    /// back for a friend), pieces go only while nothing is held back for them; without it, every piece goes at once.
    /// </summary>
    public CustomContentSender(ISessionTransport transport, Func<ulong, long>? queuedBytes = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _queuedBytes = queuedBytes;
    }

    /// <summary>Items sent in full so far (to anyone).</summary>
    public long ItemsSent { get; private set; }

    /// <summary>Bytes sent so far (headers included).</summary>
    public long BytesSent { get; private set; }

    /// <summary>Pieces the transport refused.</summary>
    public long Refused { get; private set; }

    /// <summary>Items counted as dropped from a friend's store to make room (each goes again when a send names it).</summary>
    public long Dropped { get; private set; }

    /// <summary>True when <paramref name="friend"/> has been sent <paramref name="item"/> in full and their store hasn't dropped it since.</summary>
    public bool HasSent(ulong friend, CustomItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return _friends.TryGetValue(friend, out Outbox? outbox) && outbox.Sent.Contains(item.Hash);
    }

    /// <summary>True while items wait to go (or are part way) to <paramref name="friend"/>.</summary>
    public bool IsSending(ulong friend) => _friends.TryGetValue(friend, out Outbox? outbox) && outbox.Pending.Count > 0;

    /// <summary>Bytes the transport holds back for <paramref name="friend"/> (0 without a backlog to ask).</summary>
    public long HeldBytes(ulong friend) => _queuedBytes?.Invoke(friend) ?? 0;

    /// <summary>
    /// How far <paramref name="friend"/>'s queue has gone: the content bytes not handed to the transport yet, and the batch
    /// they belong to (the host's match waits for a friend while their content still moves). Allocates nothing.
    /// </summary>
    public ContentProgress Progress(ulong friend)
    {
        if (!_friends.TryGetValue(friend, out Outbox? outbox))
        {
            return default;
        }

        long pending = -outbox.Offset;
        foreach (CustomItem item in outbox.Pending)
        {
            pending += item.Bytes.Length;
        }

        return new ContentProgress(Math.Max(0, pending), outbox.BatchBytes);
    }

    /// <summary>
    /// Queues each of <paramref name="items"/> <paramref name="friend"/> hasn't had this session (or had, and their store
    /// dropped since) and isn't getting now, and sends what the link takes now. Returns how many items went out in full
    /// during this call.
    /// </summary>
    public int Send(ulong friend, IReadOnlyList<CustomItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (!_friends.TryGetValue(friend, out Outbox? outbox))
        {
            outbox = new Outbox();
            _friends[friend] = outbox;
        }

        outbox.Wanted.Clear();
        foreach (CustomItem item in items)
        {
            if (item == null)
            {
                continue;
            }

            outbox.Wanted[item.Hash] = item;
            if (!outbox.Sent.Contains(item.Hash) && outbox.Queued.Add(item.Hash))
            {
                Enqueue(outbox, item);
            }
        }

        return Pump(friend, outbox);
    }

    /// <summary>Call every frame while hosting: more pieces go to each friend as their link takes them.</summary>
    public void Update()
    {
        _scratch.Clear();
        foreach (KeyValuePair<ulong, Outbox> pair in _friends)
        {
            if (pair.Value.Pending.Count > 0)
            {
                _scratch.Add(pair.Key);
            }
        }

        foreach (ulong friend in _scratch)
        {
            Pump(friend, _friends[friend]);
        }
    }

    /// <summary>
    /// The next report of what was finished for a friend: all that was queued for them went in full (with how many items
    /// and bytes since the last report), or a piece was refused.
    /// </summary>
    public bool TryTakeDelivery(out CustomContentDelivery delivery) => _deliveries.TryDequeue(out delivery);

    /// <summary>A friend left or rejoined: everything goes to them again when next asked (what was under way is dropped).</summary>
    public void Forget(ulong friend) => _friends.Remove(friend);

    /// <summary>Hosting ended: nothing is remembered.</summary>
    public void Clear()
    {
        _friends.Clear();
        _deliveries.Clear();
    }

    /// <summary>Sends <paramref name="friend"/> pieces while the link takes them; returns the items finished now.</summary>
    private int Pump(ulong friend, Outbox outbox)
    {
        int finished = 0;
        while (outbox.Pending.Count > 0)
        {
            if (_queuedBytes != null && _queuedBytes(friend) > 0)
            {
                break; // the transport holds some back for them: wait until that has gone
            }

            CustomItem item = outbox.Pending.Peek();
            if (outbox.Offset == 0)
            {
                MakeRoom(outbox, item.Bytes.Length);
            }

            _writer.Reset();
            new MessageHeader(MessageType.CustomContent, _sequence++, 0, 0).Write(_writer);
            int next = CustomContentMessage.Write(_writer, item, outbox.Offset);
            ReadOnlySpan<byte> message = _writer.AsSpan();
            if (!_transport.Send(friend, Channel.Bulk, message))
            {
                // The friend overflowed or is gone: the reliable channel has a gap, so what was under way starts over.
                Refused++;
                outbox.Pending.Clear();
                outbox.Queued.Clear();
                outbox.Offset = 0;
                outbox.BatchBytes = 0;
                _deliveries.Enqueue(new CustomContentDelivery(friend, outbox.Items, outbox.Bytes, Refused: true));
                outbox.Items = 0;
                outbox.Bytes = 0;
                return finished;
            }

            BytesSent += message.Length;
            outbox.Bytes += message.Length;
            outbox.Offset = next;
            if (next >= item.Bytes.Length)
            {
                outbox.Pending.Dequeue();
                outbox.Queued.Remove(item.Hash);
                outbox.Sent.Add(item.Hash);
                outbox.Held.AddLast((item.Hash, item.Bytes.Length)); // the newest in the friend's store, as it counts them
                outbox.HeldBytes += item.Bytes.Length;
                outbox.Offset = 0;
                outbox.Items++;
                ItemsSent++;
                finished++;
            }
        }

        if (outbox.Pending.Count == 0 && outbox.Items > 0)
        {
            _deliveries.Enqueue(new CustomContentDelivery(friend, outbox.Items, outbox.Bytes, Refused: false));
            outbox.Items = 0;
            outbox.Bytes = 0;
        }

        return finished;
    }

    private static void Enqueue(Outbox outbox, CustomItem item)
    {
        if (outbox.Pending.Count == 0)
        {
            outbox.BatchBytes = 0; // a new batch
        }

        outbox.Pending.Enqueue(item);
        outbox.BatchBytes += item.Bytes.Length;
    }

    /// <summary>
    /// An item of <paramref name="bytes"/> starts going: the friend's store drops its oldest items until it has room
    /// (<see cref="CustomContentReceiver"/>), and so does the count here (within <see cref="HeldBudget"/>). What it drops is
    /// no longer sent; one the newest send named goes again after what's queued.
    /// </summary>
    private void MakeRoom(Outbox outbox, int bytes)
    {
        while (outbox.Held.First is LinkedListNode<(ulong Hash, int Bytes)> oldest &&
            (outbox.Held.Count >= CustomContentReceiver.MaxItems || outbox.HeldBytes + bytes > HeldBudget))
        {
            outbox.Held.RemoveFirst();
            outbox.HeldBytes -= oldest.Value.Bytes;
            outbox.Sent.Remove(oldest.Value.Hash);
            Dropped++;
            if (outbox.Wanted.Remove(oldest.Value.Hash, out CustomItem? again) && outbox.Queued.Add(again.Hash))
            {
                Enqueue(outbox, again);
            }
        }
    }

    /// <summary>One friend's items: what they have in full, and what's queued (the first part way, at <see cref="Offset"/>).</summary>
    private sealed class Outbox
    {
        public HashSet<ulong> Sent { get; } = new();

        /// <summary>The items of <see cref="Sent"/> as the friend's store keeps them: oldest first, each with its size.</summary>
        public LinkedList<(ulong Hash, int Bytes)> Held { get; } = new();

        public long HeldBytes { get; set; }

        /// <summary>The items the newest send named, by hash: one dropped from the friend's store goes again (and leaves this).</summary>
        public Dictionary<ulong, CustomItem> Wanted { get; } = new();

        public HashSet<ulong> Queued { get; } = new();

        public Queue<CustomItem> Pending { get; } = new();

        public int Offset { get; set; }

        public int Items { get; set; }

        public long Bytes { get; set; }

        public long BatchBytes { get; set; }
    }
}
