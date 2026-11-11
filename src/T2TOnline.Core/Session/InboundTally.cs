using System;

namespace T2TOnline.Core.Session;

/// <summary>What one peer sent the host over a window of <see cref="InboundTally"/>.</summary>
/// <param name="ReadBytes">Bytes of the messages the host read (handed to the session).</param>
/// <param name="ReadMessages">How many messages the host read.</param>
/// <param name="DroppedBytes">Bytes of the messages the host dropped unread (the bulk channel, which only a host sends on).</param>
/// <param name="DroppedMessages">How many messages the host dropped unread.</param>
public readonly record struct InboundWindow(long ReadBytes, long ReadMessages, long DroppedBytes, long DroppedMessages)
{
    /// <summary>True when nothing came in the window.</summary>
    public bool IsEmpty => ReadMessages == 0 && DroppedMessages == 0;
}

/// <summary>
/// The host's count of what each peer sent it, read or dropped, per window (the 10 s inbound line): set beside Steam's own
/// count of what arrived from them, the gap is what they sent that nothing read (its headers and acknowledgements, and any
/// channel nobody reads). A fixed table of <see cref="MaxPeers"/> peers: one more is counted in <see cref="OtherMessages"/>
/// only, and a peer silent for a whole window gives its place up. Counting allocates nothing. Pure logic; not thread-safe
/// (one game thread).
/// </summary>
public sealed class InboundTally
{
    /// <summary>The most peers counted apart (a lobby of four, and room for peers coming and going within a window).</summary>
    public const int MaxPeers = 8;

    private readonly ulong[] _ids = new ulong[MaxPeers];
    private readonly long[] _readBytes = new long[MaxPeers];
    private readonly long[] _readMessages = new long[MaxPeers];
    private readonly long[] _droppedBytes = new long[MaxPeers];
    private readonly long[] _droppedMessages = new long[MaxPeers];

    /// <summary>Messages from peers past the table, read or dropped, since <see cref="Clear"/>.</summary>
    public long OtherMessages { get; private set; }

    /// <summary>Messages dropped unread since <see cref="Clear"/>, every peer's.</summary>
    public long DroppedMessages { get; private set; }

    /// <summary>Bytes dropped unread since <see cref="Clear"/>, every peer's.</summary>
    public long DroppedBytes { get; private set; }

    /// <summary>The peer whose message was the first dropped since <see cref="Clear"/>, or 0.</summary>
    public ulong FirstDropFrom { get; private set; }

    /// <summary>Counts a message of <paramref name="bytes"/> from <paramref name="peer"/> that the host read.</summary>
    public void Read(ulong peer, int bytes)
    {
        int slot = Slot(peer);
        if (slot < 0)
        {
            OtherMessages++;
            return;
        }

        _readBytes[slot] += Math.Max(0, bytes);
        _readMessages[slot]++;
    }

    /// <summary>
    /// Counts a message of <paramref name="bytes"/> from <paramref name="peer"/> that the host dropped unread. True for the
    /// first one since <see cref="Clear"/> (whoever sent it), for the one line that says so.
    /// </summary>
    public bool Dropped(ulong peer, int bytes)
    {
        bool first = DroppedMessages == 0;
        DroppedMessages++;
        DroppedBytes += Math.Max(0, bytes);
        if (first)
        {
            FirstDropFrom = peer;
        }

        int slot = Slot(peer);
        if (slot < 0)
        {
            OtherMessages++;
            return first;
        }

        _droppedBytes[slot] += Math.Max(0, bytes);
        _droppedMessages[slot]++;
        return first;
    }

    /// <summary>
    /// What <paramref name="peer"/> sent in the window so far: false (and an empty window) when nothing of theirs is counted
    /// in it.
    /// </summary>
    public bool TryGet(ulong peer, out InboundWindow window)
    {
        int slot = peer == 0 ? -1 : Array.IndexOf(_ids, peer);
        if (slot < 0)
        {
            window = default;
            return false;
        }

        window = new InboundWindow(_readBytes[slot], _readMessages[slot], _droppedBytes[slot], _droppedMessages[slot]);
        return !window.IsEmpty;
    }

    /// <summary>The peers counted in the window so far, at most <see cref="MaxPeers"/> (0 for a free place).</summary>
    public ReadOnlySpan<ulong> Peers => _ids;

    /// <summary>Starts the next window: every count goes back to 0, and a peer who sent nothing in the one ending gives its place up.</summary>
    public void NextWindow()
    {
        for (int i = 0; i < MaxPeers; i++)
        {
            if (_readMessages[i] == 0 && _droppedMessages[i] == 0)
            {
                _ids[i] = 0;
            }

            _readBytes[i] = _readMessages[i] = _droppedBytes[i] = _droppedMessages[i] = 0;
        }
    }

    /// <summary>Forgets everything (a hosting session starts or ends).</summary>
    public void Clear()
    {
        Array.Clear(_ids);
        Array.Clear(_readBytes);
        Array.Clear(_readMessages);
        Array.Clear(_droppedBytes);
        Array.Clear(_droppedMessages);
        OtherMessages = 0;
        DroppedMessages = 0;
        DroppedBytes = 0;
        FirstDropFrom = 0;
    }

    /// <summary>The peer's place, taken if they have none and one is free; -1 when the table is full (or for 0).</summary>
    private int Slot(ulong peer)
    {
        if (peer == 0)
        {
            return -1;
        }

        int free = -1;
        for (int i = 0; i < MaxPeers; i++)
        {
            if (_ids[i] == peer)
            {
                return i;
            }

            if (free < 0 && _ids[i] == 0)
            {
                free = i;
            }
        }

        if (free >= 0)
        {
            _ids[free] = peer;
        }

        return free;
    }
}
