using System;
using T2TOnline.Core.Diagnostics;
using T2TOnline.Core.Protocol;
using T2TOnline.Core.Serialization;

namespace T2TOnline.Core.Session;

/// <summary>Sends session messages to one peer. Implemented over Steam in the game, in memory in tests.</summary>
public interface ISessionTransport
{
    /// <summary>
    /// Sends <paramref name="message"/> to <paramref name="peer"/>. <see cref="Channel.Realtime"/> is unreliable and
    /// never waits; the other channels are reliable and ordered. Returns false if the message was refused (a reliable one
    /// too: Steam refuses when a peer's send buffer is full; <see cref="RetryingTransport"/> keeps and resends those).
    /// </summary>
    bool Send(ulong peer, Channel channel, ReadOnlySpan<byte> message);
}

/// <summary>
/// Handles a match-stream message (<c>Core.Stream</c>) a session passed on, header included. False when it's malformed;
/// the session counts it against the peer.
/// </summary>
public delegate bool StreamMessageHandler(ulong sender, Channel channel, ReadOnlySpan<byte> message, long nowMicros);

/// <summary>What a peer says about itself in the handshake.</summary>
/// <param name="ProtocolVersion">Its <see cref="ProtocolInfo.Version"/>.</param>
/// <param name="ModVersion">Its mod version, for logs.</param>
/// <param name="GameBuild">The first 8 bytes of its GameAssembly.dll SHA-256.</param>
public sealed record SessionIdentity(byte ProtocolVersion, string ModVersion, ulong GameBuild);

/// <summary>Session timing, in microseconds. One set for both ends.</summary>
public static class SessionTiming
{
    /// <summary>A peer silent this long is shown as lagging.</summary>
    public const long LaggingAfterMicros = 2_000_000;

    /// <summary>A peer silent this long is gone.</summary>
    public const long GoneAfterMicros = 10_000_000;

    /// <summary>Time between pings to each peer.</summary>
    public const long PingIntervalMicros = 500_000;

    /// <summary>Time between hellos while a friend waits for the host's answer.</summary>
    public const long HelloRetryMicros = 2_000_000;

    /// <summary>A friend stops waiting for the host's answer after this long.</summary>
    public const long HelloGiveUpMicros = 20_000_000;

    /// <summary>
    /// A first join the host never answered is tried again for this long after its first <see cref="HelloGiveUpMicros"/> (the
    /// host may be loading), then given up: a joining friend's hellos come for at most the sum of the two from their lobby entry.
    /// </summary>
    public const long FirstJoinRetryMicros = 40_000_000;

    /// <summary>
    /// The host looks at a first hello from one sender at most this often: each costs it a friend check, log lines and
    /// perhaps a virtual controller, and someone refused, or a friend who leaves and says hello again at once, could repeat that
    /// as fast as they send. A real friend's retries are <see cref="HelloRetryMicros"/> apart.
    /// </summary>
    public const long NewHelloIntervalMicros = 500_000;

    /// <summary>
    /// Most messages taken from Steam at one go on the realtime channel (the rest wait in Steam's queue for the next): a
    /// peer's flood costs a frame what this many cost, never a frozen game. Three pads at 120 Hz, or a stream of 60 snapshots a
    /// second with its pings, are a few per frame.
    /// </summary>
    public const int RealtimeMessagesPerDrain = 128;

    /// <summary>Most messages taken at one go on the events channel: a stream's events and HUD, or a rejoin's burst, spread over
    /// a few frames at worst.</summary>
    public const int EventMessagesPerDrain = 64;

    /// <summary>
    /// Most messages taken at one go on the bulk channel. A run checkpoint can inflate to 8 MB and be hashed from a few KB, so
    /// these are what a flood is made of; the host sends one checkpoint per save, and custom content a piece at a time.
    /// </summary>
    public const int BulkMessagesPerDrain = 2;

    /// <summary>
    /// Most messages a host takes from the bulk channel at one go and drops unread. Only a host sends on that channel, so
    /// whatever reaches a host there comes from a modified client; Steam keeps a channel nobody reads without limit, so the
    /// host empties it, at the cost of a release per message (nothing is copied or decoded).
    /// </summary>
    public const int DiscardMessagesPerDrain = 256;

    /// <summary>
    /// A joined friend's repeated hellos raise <see cref="HostEventKind.Rejoined"/> at most this often: each one makes the
    /// host send that friend everything again (setup, tables, run checkpoint, progress, menus), so a friend's hello retries
    /// (one every <see cref="HelloRetryMicros"/> while it waits for the welcome) or a misbehaving friend can't make it do so
    /// over and over. The repeats are still answered.
    /// </summary>
    public const long RejoinIntervalMicros = 5_000_000;

    /// <summary>Round trips above this are discarded as bogus.</summary>
    public const long MaxRoundTripMicros = 10_000_000;

    /// <summary>
    /// A friend's pad family changes (<see cref="HostEventKind.PadDeviceChanged"/>) are taken at most this often, the newest
    /// one winning: each re-registers their virtual pad's button prompts on the host and is logged, so a misbehaving friend
    /// can't make the host do that every frame.
    /// </summary>
    public const long PadDeviceIntervalMicros = 1_000_000;

    /// <summary>
    /// A friend's controls (<see cref="HostEventKind.BindingsChanged"/>) are taken at most this often, the newest winning:
    /// each rebuilds their virtual controller's maps on the host, so a misbehaving friend can't make the host do that every
    /// frame.
    /// </summary>
    public const long RemapsIntervalMicros = 1_000_000;

    /// <summary>
    /// A friend's prompt names (<see cref="HostEventKind.PromptNamesChanged"/>) are taken at most this often, the newest
    /// winning: each is decoded and replaces the table the host's prompts for them are named from, so a misbehaving friend
    /// can't make the host do that every frame.
    /// </summary>
    public const long PromptNamesIntervalMicros = 1_000_000;

    /// <summary>
    /// The lobby's board goes to each friend at most this often (four times a second), the newest winning, so a busy lobby
    /// or a host's selector held down can't flood a friend. A friend's events about it are limited the same way.
    /// </summary>
    public const long LobbyBoardIntervalMicros = 250_000;

    /// <summary>
    /// A friend's member messages (<see cref="HostEventKind.LobbyMember"/>) are taken at most this often, the newest winning:
    /// each redraws the host's lobby and is checked against the barrier, so a friend flipping their ready mark can't make
    /// the host do that every frame. An ack waits at most this long.
    /// </summary>
    public const long LobbyMemberIntervalMicros = 250_000;
}

/// <summary>Traffic and health of the link to one peer.</summary>
public sealed class PeerLink
{
    /// <summary>Pings remembered for their pongs (16 s of them at <see cref="SessionTiming.PingIntervalMicros"/>).</summary>
    internal const int RememberedPings = 32;

    private readonly uint[] _pingIds = new uint[RememberedPings];
    private readonly long[] _pingSentAt = new long[RememberedPings];
    private Snapshots.DelayHistogram? _roundTrips;

    internal PeerLink(long nowMicros)
    {
        LastHeardMicros = nowMicros;
        Array.Fill(_pingSentAt, long.MinValue);
    }

    /// <summary>Messages received, match stream messages included.</summary>
    public long MessagesIn { get; internal set; }

    /// <summary>Bytes received, match stream messages included.</summary>
    public long BytesIn { get; internal set; }

    /// <summary>Session messages sent. The match stream sends past the session and counts its own (<c>Stream.StreamFriend</c>).</summary>
    public long MessagesOut { get; internal set; }

    /// <summary>Bytes of session messages sent. The host's match stream counts its own (<c>Stream.StreamFriend.BytesSent</c>).</summary>
    public long BytesOut { get; internal set; }

    /// <summary>Session message sends the transport refused.</summary>
    public long SendFailures { get; internal set; }

    /// <summary>Messages that didn't decode, or that made no sense in the session's state.</summary>
    public long Malformed { get; internal set; }

    /// <summary>When anything last arrived from the peer.</summary>
    public long LastHeardMicros { get; internal set; }

    /// <summary>The latest round trip, or -1 before the first pong.</summary>
    public long LastRoundTripMicros { get; internal set; } = -1;

    /// <summary>Round trips in milliseconds since the caller last cleared it.</summary>
    public SampleWindow RoundTripMs { get; } = new();

    /// <summary>
    /// Every round trip since the link started, in microseconds (never cleared by a report): the session's percentiles when
    /// a peer goes. Made with the first round trip (a refused hello's link never has one).
    /// </summary>
    public Snapshots.DelayHistogram RoundTrips => _roundTrips ??= new();

    /// <summary>Loss on the peer's realtime stream, from its sequence numbers.</summary>
    public SequenceLoss RealtimeLoss { get; } = new();

    /// <summary>True while the peer is silent past <see cref="SessionTiming.LaggingAfterMicros"/>.</summary>
    public bool IsLagging { get; internal set; }

    /// <summary>Next sequence number per message type, so each stream's gaps show its own loss.</summary>
    internal ulong[] NextSequence { get; } = new ulong[256];

    internal uint NextPingId { get; set; }

    internal long NextPingAtMicros { get; set; }

    /// <summary>Remembers when ping <paramref name="id"/> left, for its pong's round trip.</summary>
    internal void RememberPing(uint id, long sentAtMicros)
    {
        int slot = (int)(id % RememberedPings);
        _pingIds[slot] = id;
        _pingSentAt[slot] = sentAtMicros;
    }

    /// <summary>When ping <paramref name="id"/> left, once: false for one never sent, answered already, or long forgotten.</summary>
    internal bool TryTakePing(uint id, out long sentAtMicros)
    {
        int slot = (int)(id % RememberedPings);
        sentAtMicros = _pingSentAt[slot];
        if (sentAtMicros == long.MinValue || _pingIds[slot] != id)
        {
            return false;
        }

        _pingSentAt[slot] = long.MinValue;
        return true;
    }
}

/// <summary>
/// Estimates loss on an unreliable stream from its sequence numbers. A jump of more than <see cref="NewRunAfter"/> either
/// way isn't loss: the sender started counting again (a new session, or a corrupt number), so a new run starts there.
/// </summary>
public sealed class SequenceLoss
{
    /// <summary>
    /// A jump larger than this, forward or back, starts a new run: over half a minute of a 120 Hz stream, where a session
    /// times out after <see cref="SessionTiming.GoneAfterMicros"/>.
    /// </summary>
    public const ulong NewRunAfter = 4096;

    private bool _started;
    private ulong _first;
    private ulong _highest;
    private long _earlierRuns;
    private long _received;
    private long _windowExpected;
    private long _windowReceived;

    /// <summary>Messages seen, duplicates included.</summary>
    public long Received => _received;

    /// <summary>Messages the sequence numbers say were sent: in each run, from the first seen to the highest. Never decreases.</summary>
    public long Expected => _earlierRuns + (_started ? (long)(_highest - _first + 1) : 0);

    /// <summary>Records one message's sequence number.</summary>
    public void Observe(ulong sequence)
    {
        if (!_started)
        {
            _started = true;
            _first = _highest = sequence;
        }
        else if ((sequence > _highest && sequence - _highest > NewRunAfter) || (sequence < _first && _first - sequence > NewRunAfter))
        {
            // Far outside this run. Runs grow at most NewRunAfter at a time, so their lengths (and Expected) stay positive.
            _earlierRuns = Expected;
            _first = _highest = sequence;
        }
        else if (sequence > _highest)
        {
            _highest = sequence;
        }
        else if (sequence < _first)
        {
            _first = sequence; // a straggler older than the first one seen
        }

        _received++;
    }

    /// <summary>Forgets everything (the peer's stream started over).</summary>
    public void Reset()
    {
        _started = false;
        _first = _highest = 0;
        _earlierRuns = _received = _windowExpected = _windowReceived = 0;
    }

    /// <summary>Loss since the last call, in percent (0 when nothing was expected).</summary>
    public double TakeWindowLossPercent()
    {
        long expected = Expected - _windowExpected;
        long received = _received - _windowReceived;
        _windowExpected = Expected;
        _windowReceived = _received;
        return expected <= 0 ? 0 : Math.Max(0, 100.0 * (expected - received) / expected);
    }
}

/// <summary>Writes and reads the framing every session message shares.</summary>
internal static class SessionWire
{
    /// <summary>Starts a message in <paramref name="writer"/>: the header, with the next sequence number for its type.</summary>
    public static void Begin(BitWriter writer, PeerLink link, MessageType type)
    {
        writer.Reset();
        ulong sequence = link.NextSequence[(byte)type]++;
        new MessageHeader(type, sequence, 0, 0).Write(writer);
    }

    /// <summary>Sends <paramref name="link"/>'s next ping (realtime) and remembers when, for its pong.</summary>
    public static void SendPing(ISessionTransport transport, ulong peer, PeerLink link, BitWriter writer, long nowMicros)
    {
        uint id = link.NextPingId++;
        Begin(writer, link, MessageType.Ping);
        new PingMessage(id, nowMicros).Write(writer);
        link.RememberPing(id, nowMicros);
        Send(transport, peer, link, Channel.Realtime, writer);
    }

    /// <summary>Sends what <paramref name="writer"/> holds and counts it.</summary>
    public static bool Send(ISessionTransport transport, ulong peer, PeerLink link, Channel channel, BitWriter writer)
    {
        ReadOnlySpan<byte> bytes = writer.AsSpan();
        bool sent = transport.Send(peer, channel, bytes);
        if (sent)
        {
            link.MessagesOut++;
            link.BytesOut += bytes.Length;
        }
        else
        {
            link.SendFailures++;
        }

        return sent;
    }

    /// <summary>
    /// Records a pong's round trip, timed from when we sent its ping (<paramref name="sentAtMicros"/>): the send time the
    /// pong echoes is the peer's to forge. False for a pong whose ping isn't remembered (never sent, answered already, or
    /// long gone) or an impossible round trip.
    /// </summary>
    public static bool AddRoundTrip(PeerLink link, in PongMessage pong, long nowMicros, out long sentAtMicros)
    {
        if (!link.TryTakePing(pong.Id, out sentAtMicros))
        {
            return false;
        }

        long roundTrip = nowMicros - sentAtMicros;
        if (roundTrip is < 0 or > SessionTiming.MaxRoundTripMicros)
        {
            return false;
        }

        link.LastRoundTripMicros = roundTrip;
        link.RoundTripMs.Add(roundTrip / 1000.0);
        link.RoundTrips.Add(roundTrip);
        return true;
    }
}
