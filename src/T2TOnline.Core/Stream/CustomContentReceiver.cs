using System.Collections.Generic;
using System.Globalization;
using T2TOnline.Core.Protocol;

namespace T2TOnline.Core.Stream;

/// <summary>
/// A friend's store of the host's custom content for one session: items put back together from their pieces
/// (<see cref="CustomContentMessage"/>), each checked against its hash before it's kept. Untrusted input: pieces must come
/// in order (the channel is reliable and ordered; a piece out of order drops what's partly in), and what's kept and what's
/// in flight are capped in count and bytes: for a new item's bytes, what was left part way goes first, then the oldest
/// complete items (an item sent again in full counts as the newest: the host's sender keeps the same count of the
/// complete ones, and sends again what it knows was dropped). Game-independent.
/// </summary>
public sealed class CustomContentReceiver
{
    /// <summary>Most complete items kept.</summary>
    public const int MaxItems = 64;

    /// <summary>Most items partly received at once.</summary>
    public const int MaxInFlight = 8;

    /// <summary>Most bytes kept, complete and in flight together.</summary>
    public const long MaxStoredBytes = 32L * 1024 * 1024;

    private readonly Dictionary<ulong, CustomItem> _items = new();
    private readonly LinkedList<ulong> _order = new();
    private readonly Dictionary<ulong, Partial> _inFlight = new();
    private readonly Queue<CustomItem> _completed = new();
    private long _bytes;
    private long _started;

    /// <summary>Complete items held.</summary>
    public int Count => _items.Count;

    /// <summary>Bytes held, complete and in flight.</summary>
    public long StoredBytes => _bytes;

    /// <summary>Items put together and kept.</summary>
    public long Received { get; private set; }

    /// <summary>Pieces of an item already held (a resend after a rejoin).</summary>
    public long Duplicates { get; private set; }

    /// <summary>Pieces that made no sense here (out of order, disagreeing with the item's first piece) and items whose bytes didn't match their hash.</summary>
    public long Rejected { get; private set; }

    /// <summary>Complete items dropped to make room.</summary>
    public long Evicted { get; private set; }

    /// <summary>
    /// Takes one piece. False when it's refused (counted in <see cref="Rejected"/>); a piece of an item already held is
    /// fine (counted in <see cref="Duplicates"/>).
    /// </summary>
    public bool Receive(in CustomChunk chunk)
    {
        if (chunk.Bytes == null || chunk.Bytes.Length == 0 || chunk.Offset < 0 || chunk.TotalBytes <= 0 ||
            chunk.Offset + (long)chunk.Bytes.Length > chunk.TotalBytes)
        {
            Rejected++;
            return false;
        }

        if (_items.ContainsKey(chunk.Hash))
        {
            // Sent again in full (the host counted it as dropped here, or this PC rejoined): from its last piece on it's the
            // newest here, as the host's sender counts it (CustomContentSender), so the two drop the same items.
            if (chunk.Offset + (long)chunk.Bytes.Length == chunk.TotalBytes && _order.Remove(chunk.Hash))
            {
                _order.AddLast(chunk.Hash);
            }

            Duplicates++;
            return true;
        }

        if (!_inFlight.TryGetValue(chunk.Hash, out Partial? partial))
        {
            if (chunk.Offset != 0)
            {
                // Its start never came (a session that began mid-item): nothing to add it to.
                Rejected++;
                return false;
            }

            if (_inFlight.Count >= MaxInFlight)
            {
                // The host sends one item after another, so a partial left behind was abandoned (its session restarted).
                DropOldestInFlight();
            }

            if (!MakeRoom(chunk.TotalBytes))
            {
                Rejected++;
                return false;
            }

            partial = new Partial(chunk.Kind, chunk.Key, new byte[chunk.TotalBytes], _started++);
            _inFlight[chunk.Hash] = partial;
            _bytes += chunk.TotalBytes;
        }
        else if (partial.Kind != chunk.Kind || partial.Key != chunk.Key || partial.Bytes.Length != chunk.TotalBytes)
        {
            Drop(chunk.Hash, partial);
            Rejected++;
            return false;
        }
        else if (chunk.Offset == 0)
        {
            // Sent again from its start (the host resends what a rejoined friend may lack): start over.
            partial.Received = 0;
        }
        else if (chunk.Offset != partial.Received)
        {
            Drop(chunk.Hash, partial);
            Rejected++;
            return false;
        }

        chunk.Bytes.CopyTo(partial.Bytes, chunk.Offset);
        partial.Received += chunk.Bytes.Length;
        if (partial.Received < partial.Bytes.Length)
        {
            return true;
        }

        _inFlight.Remove(chunk.Hash);
        if (CustomItem.ComputeHash(partial.Kind, partial.Key, partial.Bytes) != chunk.Hash)
        {
            _bytes -= partial.Bytes.Length;
            Rejected++;
            return false;
        }

        var item = new CustomItem(partial.Kind, partial.Key, partial.Bytes);
        _items[item.Hash] = item;
        _order.AddLast(item.Hash);
        _completed.Enqueue(item);
        Received++;
        return true;
    }

    /// <summary>The item with <paramref name="hash"/>, when it's complete here.</summary>
    public bool TryGet(ulong hash, out CustomItem? item) => _items.TryGetValue(hash, out item);

    /// <summary>The next item completed since the last call, oldest first (the caller acts on logos as they arrive).</summary>
    public bool TryTakeCompleted(out CustomItem? item)
    {
        bool taken = _completed.TryDequeue(out CustomItem? next);
        item = next;
        return taken;
    }

    /// <summary>
    /// How much of the item with <paramref name="hash"/> is here: <paramref name="received"/> of <paramref name="total"/>
    /// bytes (both 0 when none of it has come: its size isn't known until its first piece). True when it's complete.
    /// </summary>
    public bool ProgressOf(ulong hash, out long received, out long total)
    {
        if (_items.TryGetValue(hash, out CustomItem? item))
        {
            received = total = item.Bytes.Length;
            return true;
        }

        if (_inFlight.TryGetValue(hash, out Partial? partial))
        {
            received = partial.Received;
            total = partial.Bytes.Length;
            return false;
        }

        received = total = 0;
        return false;
    }

    /// <summary>For logs: "complete", "2 of 300 KB here" or "not here".</summary>
    public string Progress(ulong hash)
    {
        if (_items.ContainsKey(hash))
        {
            return "complete";
        }

        return _inFlight.TryGetValue(hash, out Partial? partial)
            ? string.Create(CultureInfo.InvariantCulture, $"{partial.Received / 1024} of {(partial.Bytes.Length + 1023) / 1024} KB here")
            : "not here";
    }

    /// <summary>Everything held and in flight is dropped (the session ended).</summary>
    public void Clear()
    {
        _items.Clear();
        _order.Clear();
        _inFlight.Clear();
        _completed.Clear();
        _bytes = 0;
    }

    /// <summary>
    /// Room for <paramref name="bytes"/> more (a new item's first piece): first from the items left part way, the oldest
    /// first, then from the oldest complete items; false when there can't be. The host sends one item after another, so
    /// an item still part way when a new one starts was given up (a refused piece, a rejoin), and the host's sender
    /// can't count its room: taken before a complete item's, it never makes the store drop one the host counts as held.
    /// </summary>
    private bool MakeRoom(long bytes)
    {
        while (_bytes + bytes > MaxStoredBytes && _inFlight.Count > 0)
        {
            DropOldestInFlight();
        }

        while ((_items.Count >= MaxItems || _bytes + bytes > MaxStoredBytes) && _order.First is LinkedListNode<ulong> oldest)
        {
            _order.RemoveFirst();
            if (_items.Remove(oldest.Value, out CustomItem? dropped))
            {
                _bytes -= dropped.Bytes.Length;
                Evicted++;
            }
        }

        return _items.Count < MaxItems && _bytes + bytes <= MaxStoredBytes;
    }

    private void Drop(ulong hash, Partial partial)
    {
        _inFlight.Remove(hash);
        _bytes -= partial.Bytes.Length;
    }

    private void DropOldestInFlight()
    {
        ulong oldest = 0;
        Partial? found = null;
        foreach (KeyValuePair<ulong, Partial> pair in _inFlight)
        {
            if (found == null || pair.Value.Started < found.Started)
            {
                oldest = pair.Key;
                found = pair.Value;
            }
        }

        if (found != null)
        {
            Drop(oldest, found);
            Rejected++;
        }
    }

    private sealed class Partial
    {
        public Partial(CustomContentKind kind, string key, byte[] bytes, long started)
        {
            Kind = kind;
            Key = key;
            Bytes = bytes;
            Started = started;
        }

        public long Started { get; }

        public CustomContentKind Kind { get; }

        public string Key { get; }

        public byte[] Bytes { get; }

        public int Received { get; set; }
    }
}
