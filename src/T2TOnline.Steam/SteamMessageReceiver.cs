using System;
using System.Runtime.InteropServices;
using T2TOnline.Steam.Native;

namespace T2TOnline.Steam;

/// <summary>Receives one message: the sender's Steam ID, the channel it arrived on, and its bytes.</summary>
/// <remarks><paramref name="payload"/> is only valid during the call; copy anything you keep.</remarks>
public delegate void SteamMessageHandler(ulong senderSteamId, int channel, ReadOnlySpan<byte> payload);

/// <summary>
/// Receives one message with the state passed to
/// <see cref="SteamMessageReceiver.Drain{TState}(int, TState, SteamMessageHandler{TState}, int)"/>, so the handler can
/// be a <c>static</c> lambda that captures nothing and draining allocates no closure.
/// </summary>
/// <typeparam name="TState">The caller's state; a struct (such as a value tuple) is passed without allocating.</typeparam>
/// <remarks><paramref name="payload"/> is only valid during the call; copy anything you keep.</remarks>
public delegate void SteamMessageHandler<TState>(TState state, ulong senderSteamId, int channel, ReadOnlySpan<byte> payload);

/// <summary>
/// One message <see cref="SteamMessageReceiver.Discard"/> dropped unread: who sent it, the channel and its size in bytes (its
/// bytes were never copied).
/// </summary>
public delegate void SteamMessageDropped(ulong senderSteamId, int channel, int size);

/// <summary>
/// Drains <c>ISteamNetworkingMessages</c> channels into a handler. Reuses its pointer batch and payload buffer, so
/// steady-state receiving allocates nothing (the buffer only grows when a bigger message arrives). Every native message
/// is released exactly once, including when the handler throws. Messages that look wrong (null pointer, negative or
/// oversized length, null data, a sender that isn't a Steam ID) are released and counted in <see cref="Skipped"/>,
/// never delivered and never thrown about. <see cref="Discard"/> empties a channel nobody should send on without copying
/// anything (Steam keeps an unread channel's messages without limit). Main thread only; one instance can serve every channel,
/// one drain at a time (a handler can't drain the receiver that is calling it).
/// </summary>
public sealed class SteamMessageReceiver
{
    private const int InitialBufferSize = 2048;

    private static readonly SteamMessageHandler<SteamMessageHandler> Forward =
        static (handler, sender, channel, payload) => handler(sender, channel, payload);

    private readonly ISteamMessageQueue _queue;
    private readonly nint[] _batch;
    private readonly int _maxMessageSize;
    private byte[] _buffer = new byte[InitialBufferSize];
    private bool _draining;

    /// <summary>
    /// Creates a receiver that asks Steam for up to <paramref name="batchSize"/> messages per call and skips messages
    /// bigger than <paramref name="maxMessageSize"/> bytes.
    /// </summary>
    public SteamMessageReceiver(int batchSize = 64, int maxMessageSize = SteamMessages.MaxSendSize)
        : this(NativeMessageQueue.Instance, batchSize, maxMessageSize)
    {
    }

    internal SteamMessageReceiver(ISteamMessageQueue queue, int batchSize, int maxMessageSize)
    {
        if (batchSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(batchSize), "Batch size must be positive.");
        }

        if (maxMessageSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxMessageSize), "Message size limit must be positive.");
        }

        _queue = queue;
        _batch = new nint[batchSize];
        _maxMessageSize = maxMessageSize;
    }

    /// <summary>Messages delivered to a handler since creation.</summary>
    public long Delivered { get; private set; }

    /// <summary>Messages released without delivery because they looked wrong.</summary>
    public long Skipped { get; private set; }

    /// <summary>Messages released without delivery because a handler threw before reaching them.</summary>
    public long Dropped { get; private set; }

    /// <summary>Messages <see cref="Discard"/> released unread on purpose.</summary>
    public long Discarded { get; private set; }

    /// <summary>
    /// True while a <see cref="Drain(int, SteamMessageHandler, int)"/> or <see cref="Discard"/> runs, so a handler is inside a
    /// batch Steam handed over: it can't drain this receiver (refused), and work that shouldn't run there (closing a session the
    /// batch's messages came on) can wait for the next frame.
    /// </summary>
    public bool Draining => _draining;

    /// <summary>
    /// When the message being handled arrived, on Steam's local clock in microseconds (<see cref="SteamNetworking.GetLocalTimestamp"/>):
    /// read it inside the handler. Network time, not when this PC's frame got to the message.
    /// </summary>
    public long TimeReceived { get; private set; }

    /// <summary>
    /// Takes up to <paramref name="maxMessages"/> waiting messages on <paramref name="channel"/> from Steam, delivers
    /// the good ones to <paramref name="handler"/> oldest first, and returns how many were delivered. Returns 0 when
    /// Steam isn't available. Native failures never throw. An exception from <paramref name="handler"/> propagates
    /// after the rest of the current batch has been released (and counted in <see cref="Dropped"/>); messages Steam
    /// hasn't handed over yet stay queued for the next call. Not re-entrant: a handler that drains this same receiver
    /// gets an <see cref="InvalidOperationException"/>, thrown before anything is taken from Steam, which then propagates
    /// like any handler exception unless the handler catches it.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="handler"/> is null.</exception>
    /// <exception cref="InvalidOperationException">Called from inside one of this receiver's handlers.</exception>
    public int Drain(int channel, SteamMessageHandler handler, int maxMessages = int.MaxValue)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return Drain(channel, handler, Forward, maxMessages);
    }

    /// <summary>
    /// <see cref="Drain(int, SteamMessageHandler, int)"/> with caller state: <paramref name="handler"/> gets
    /// <paramref name="state"/> with every message, so it can be a <c>static</c> lambda and no closure is allocated per
    /// drain. The same rules apply (including no re-entry).
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="handler"/> is null.</exception>
    /// <exception cref="InvalidOperationException">Called from inside one of this receiver's handlers.</exception>
    public int Drain<TState>(int channel, TState state, SteamMessageHandler<TState> handler, int maxMessages = int.MaxValue)
    {
        ArgumentNullException.ThrowIfNull(handler);
        if (_draining)
        {
            // A nested drain would refill _batch while the outer one still owns the pointers in it, so native messages
            // would be released twice and read after release.
            throw new InvalidOperationException("SteamMessageReceiver.Drain was called from one of its own handlers; drain after it returns, or use another receiver.");
        }

        _draining = true;
        try
        {
            return DrainBatches(channel, state, handler, maxMessages);
        }
        finally
        {
            _draining = false;
        }
    }

    /// <summary>
    /// Takes up to <paramref name="maxMessages"/> waiting messages on <paramref name="channel"/> from Steam and releases them
    /// unread: no payload is copied. Each one's sender and size go to <paramref name="dropped"/> (when given); one that looks
    /// wrong is counted in <see cref="Skipped"/> instead. Returns how many were dropped (also counted in <see cref="Discarded"/>);
    /// 0 when Steam isn't available. Every message is released exactly once, a throwing <paramref name="dropped"/> included
    /// (the rest of its batch counted in <see cref="Dropped"/>, as for <see cref="Drain(int, SteamMessageHandler, int)"/>); not
    /// re-entrant either.
    /// </summary>
    /// <exception cref="InvalidOperationException">Called from inside one of this receiver's handlers.</exception>
    public int Discard(int channel, SteamMessageDropped? dropped, int maxMessages = int.MaxValue)
    {
        if (_draining)
        {
            throw new InvalidOperationException("SteamMessageReceiver.Discard was called from one of its own handlers; discard after it returns, or use another receiver.");
        }

        _draining = true;
        try
        {
            return DiscardBatches(channel, dropped, maxMessages);
        }
        finally
        {
            _draining = false;
        }
    }

    private int DiscardBatches(int channel, SteamMessageDropped? dropped, int maxMessages)
    {
        int discarded = 0;
        int taken = 0;
        while (taken < maxMessages)
        {
            int want = Math.Min(_batch.Length, maxMessages - taken);
            int count = Math.Min(_queue.Receive(channel, _batch, want), want);
            if (count <= 0)
            {
                break;
            }

            taken += count;
            int next = 0;
            try
            {
                while (next < count)
                {
                    nint message = _batch[next];
                    _batch[next] = 0;
                    next++;
                    bool ok;
                    ulong sender;
                    int size;
                    try
                    {
                        ok = TryPeek(message, _maxMessageSize, out sender, out size, out _);
                    }
                    finally
                    {
                        _queue.Release(message);
                    }

                    if (!ok)
                    {
                        Skipped++;
                        continue;
                    }

                    Discarded++;
                    discarded++;
                    dropped?.Invoke(sender, channel, size);
                }
            }
            finally
            {
                for (; next < count; next++)
                {
                    _queue.Release(_batch[next]);
                    _batch[next] = 0;
                    Dropped++;
                }
            }

            if (count < want)
            {
                break;
            }
        }

        return discarded;
    }

    private int DrainBatches<TState>(int channel, TState state, SteamMessageHandler<TState> handler, int maxMessages)
    {
        int delivered = 0;
        int taken = 0;
        while (taken < maxMessages)
        {
            int want = Math.Min(_batch.Length, maxMessages - taken);
            int count = Math.Min(_queue.Receive(channel, _batch, want), want);
            if (count <= 0)
            {
                break;
            }

            taken += count;
            int next = 0;
            try
            {
                while (next < count)
                {
                    nint message = _batch[next];
                    _batch[next] = 0;
                    next++;
                    bool ok;
                    ulong sender;
                    int length;
                    long received;
                    try
                    {
                        ok = TryCopy(message, _maxMessageSize, ref _buffer, out sender, out length, out received);
                    }
                    finally
                    {
                        _queue.Release(message);
                    }

                    if (!ok)
                    {
                        Skipped++;
                        continue;
                    }

                    Delivered++;
                    delivered++;
                    TimeReceived = received;
                    handler(state, sender, channel, new ReadOnlySpan<byte>(_buffer, 0, length));
                }
            }
            finally
            {
                for (; next < count; next++)
                {
                    _queue.Release(_batch[next]);
                    _batch[next] = 0;
                    Dropped++;
                }
            }

            if (count < want)
            {
                break;
            }
        }

        return delivered;
    }

    /// <summary>
    /// Reads a native <c>SteamNetworkingMessage_t</c> at <paramref name="message"/> and copies its payload into
    /// <paramref name="buffer"/> (growing it if needed). False, copying nothing, if the message looks wrong.
    /// </summary>
    internal static bool TryCopy(nint message, int maxMessageSize, ref byte[] buffer, out ulong sender, out int length, out long received)
    {
        sender = 0;
        length = 0;
        received = 0;
        if (!TryPeek(message, maxMessageSize, out ulong steamId, out int size, out nint data))
        {
            return false;
        }

        if (size > buffer.Length)
        {
            Array.Resize(ref buffer, Math.Min(Math.Max(size, buffer.Length * 2), maxMessageSize));
        }

        if (size > 0)
        {
            Marshal.Copy(data, buffer, 0, size);
        }

        sender = steamId;
        length = size;
        received = Marshal.ReadInt64(message, SteamNetworkingMessage.TimeReceivedOffset);
        return true;
    }

    /// <summary>
    /// Reads a native <c>SteamNetworkingMessage_t</c>'s sender, size and data pointer at <paramref name="message"/>, copying
    /// nothing. False if the message looks wrong (null, a sender that isn't a Steam ID, a negative or oversized length, or no
    /// data for a non-empty one).
    /// </summary>
    internal static bool TryPeek(nint message, int maxMessageSize, out ulong sender, out int size, out nint data)
    {
        sender = 0;
        size = 0;
        data = 0;
        if (message == 0)
        {
            return false;
        }

        nint payload = Marshal.ReadIntPtr(message, SteamNetworkingMessage.DataOffset);
        int length = Marshal.ReadInt32(message, SteamNetworkingMessage.SizeOffset);
        int identityType = Marshal.ReadInt32(message, SteamNetworkingMessage.IdentityPeerOffset + SteamNetworkingIdentity.TypeOffset);
        ulong steamId = unchecked((ulong)Marshal.ReadInt64(message, SteamNetworkingMessage.IdentityPeerOffset + SteamNetworkingIdentity.SteamId64Offset));
        if (identityType != (int)SteamNetworkingIdentityType.SteamId || steamId == 0 ||
            length < 0 || length > maxMessageSize || (length > 0 && payload == 0))
        {
            return false;
        }

        sender = steamId;
        size = length;
        data = payload;
        return true;
    }

    private sealed class NativeMessageQueue : ISteamMessageQueue
    {
        public static readonly NativeMessageQueue Instance = new();

        public int Receive(int channel, nint[] messages, int maxMessages)
        {
            nint self = SteamInterfaces.NetworkingMessages();
            if (self == 0)
            {
                return 0;
            }

            try
            {
                return SteamNative.ReceiveMessagesOnChannel(self, channel, messages, maxMessages);
            }
            catch (Exception e)
            {
                SteamRuntime.ReportFailure(e);
                return 0;
            }
        }

        public void Release(nint message)
        {
            if (message == 0)
            {
                return;
            }

            try
            {
                SteamNative.MessageRelease(message);
            }
            catch (Exception e)
            {
                SteamRuntime.ReportFailure(e);
            }
        }
    }
}

/// <summary>Where <see cref="SteamMessageReceiver"/> gets messages from; Steam in the game, a fake in tests.</summary>
internal interface ISteamMessageQueue
{
    /// <summary>Fills <paramref name="messages"/> with up to <paramref name="maxMessages"/> message pointers; returns the count.</summary>
    int Receive(int channel, nint[] messages, int maxMessages);

    /// <summary>Releases one message; ignores 0.</summary>
    void Release(nint message);
}
