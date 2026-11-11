using System;
using System.Collections.Generic;
using T2TOnline.Core.Protocol;
using T2TOnline.Core.Serialization;
using T2TOnline.Core.Session;
using T2TOnline.Core.Snapshots;

namespace T2TOnline.Core.Stream;

/// <summary>One friend's share of the host's match stream.</summary>
public sealed class StreamFriend
{
    internal StreamFriend(ulong steamId) => SteamId = steamId;

    /// <summary>Their Steam ID.</summary>
    public ulong SteamId { get; }

    /// <summary>True once they said they loaded the current match (the setup hash matched): they get the stream.</summary>
    public bool Streaming { get; internal set; }

    /// <summary>The setup hash of the last ready message, whatever it said (0 before any).</summary>
    public ulong ReadyHash { get; internal set; }

    /// <summary>True when their last ready message named a different match than the current one.</summary>
    public bool LoadedWrongMatch { get; internal set; }

    /// <summary>Why they couldn't load the current match (empty unless they said so).</summary>
    public string LoadFailure { get; internal set; } = string.Empty;

    /// <summary>
    /// True while they're still expected to load the current match: not streaming, and they haven't said they can't (a
    /// ready for another match is usually the previous one, finished just before this setup reached them).
    /// </summary>
    public bool Loading => !Streaming && LoadFailure.Length == 0;

    /// <summary>Which snapshot to delta against for them.</summary>
    public SnapshotPeer Peer { get; } = new();

    /// <summary>Snapshots sent.</summary>
    public long SnapshotsSent { get; internal set; }

    /// <summary>Snapshot bytes sent.</summary>
    public long SnapshotBytes { get; internal set; }

    /// <summary>Other stream messages sent.</summary>
    public long MessagesSent { get; internal set; }

    /// <summary>
    /// Every stream byte sent to them: snapshots, setups, match ends, tables and other messages. The session's own
    /// messages (welcome, pings, pongs, achievements) are in their session link's <see cref="PeerLink.BytesOut"/>.
    /// </summary>
    public long BytesSent { get; internal set; }

    /// <summary>Stream sends the transport refused.</summary>
    public long SendFailures { get; internal set; }

    /// <summary>
    /// Realtime messages sent over <see cref="ProtocolInfo.MaxUnreliableMessageBytes"/>: Steam splits them into several
    /// packets, and losing any one loses the whole message.
    /// </summary>
    public long OversizeSends { get; internal set; }

    /// <summary>Their newest playout report (default before the first).</summary>
    public StreamReport Report { get; internal set; }

    /// <summary>When <see cref="Report"/> arrived (host clock, microseconds), or <see cref="long.MinValue"/> before the first.</summary>
    public long ReportMicros { get; internal set; } = long.MinValue;

    internal ulong NextSnapshotSequence { get; set; }

    internal void Reset()
    {
        Streaming = false;
        LoadedWrongMatch = false;
        LoadFailure = string.Empty;
        Report = default;
        ReportMicros = long.MinValue;
        Peer.Reset();
    }
}

/// <summary>
/// The host's side of the live match stream: the match setup to every friend, then, to
/// each friend whose game has loaded that match, the object and event tables, every snapshot (delta-encoded against
/// the newest one that friend acknowledged), and every other stream message as the recorder writes it. Friends who
/// load late or reload get the tables again and a keyframe, and the caller is asked for a full HUD state
/// (<see cref="TakeFullStateRequest"/>). Game-independent; the caller supplies the transport.
/// </summary>
public sealed class MatchStreamHost
{
    /// <summary>Snapshots kept as delta bases.</summary>
    public const int HistoryCapacity = 128;

    private readonly ISessionTransport _transport;
    private readonly Dictionary<ulong, StreamFriend> _friends = new();
    private readonly SnapshotHistory _history = new(HistoryCapacity);
    private readonly List<byte[]> _tables = new();
    private readonly BitWriter _writer = new(8192);
    private readonly ulong[] _sequences = new ulong[256];
    private readonly List<EncodedSnapshot> _encoded = new();
    private byte[]? _setup;
    private bool _fullStateWanted;

    /// <summary>Creates the host side over <paramref name="transport"/>.</summary>
    public MatchStreamHost(ISessionTransport transport) => _transport = transport ?? throw new ArgumentNullException(nameof(transport));

    /// <summary>The current match's setup hash, 0 between matches.</summary>
    public ulong SetupHash { get; private set; }

    /// <summary>True while a match is being streamed (between <see cref="BeginMatch"/> and <see cref="EndMatch"/>).</summary>
    public bool InMatch => _setup != null;

    /// <summary>Every friend (as the dictionary's own collection: the relay walks it every frame, and that boxes no enumerator).</summary>
    public Dictionary<ulong, StreamFriend>.ValueCollection Friends => _friends.Values;

    /// <summary>Messages from friends that didn't decode or made no sense.</summary>
    public long Malformed { get; private set; }

    /// <summary>
    /// Snapshot encodings the codec refused (a host time out of range, or a body over <see cref="SnapshotCodec.MaxBodyBytes"/>):
    /// the friends they were for got nothing that frame.
    /// </summary>
    public long UnencodableSnapshots { get; private set; }

    /// <summary>Snapshot bodies encoded: one per distinct delta base in each <see cref="Snapshot"/> call, shared by friends with that base.</summary>
    public long SnapshotEncodings { get; private set; }

    /// <summary>The friend with this Steam ID, or null.</summary>
    public StreamFriend? Find(ulong steamId) => _friends.TryGetValue(steamId, out StreamFriend? friend) ? friend : null;

    /// <summary>True once after a friend started streaming: the caller should send a full HUD state.</summary>
    public bool TakeFullStateRequest()
    {
        bool wanted = _fullStateWanted;
        _fullStateWanted = false;
        return wanted;
    }

    /// <summary>
    /// A new match: its setup goes to every friend now, and to friends who join later. Everyone stops streaming until
    /// they say they loaded it.
    /// </summary>
    public void BeginMatch(IReadOnlyList<KeyValuePair<string, NetValue>> setup)
    {
        ArgumentNullException.ThrowIfNull(setup);
        SetupHash = MatchSetupMessage.Hash(setup);
        Begin(MessageType.MatchSetup, 0);
        MatchSetupMessage.Write(_writer, SetupHash, setup);
        _setup = _writer.AsSpan().ToArray();
        _tables.Clear();
        _history.Clear();
        foreach (StreamFriend friend in _friends.Values)
        {
            friend.Reset();
            Send(friend, Channel.Events, _setup);
        }
    }

    /// <summary>The match is over (or the host left it): every friend is told, and the stream stops.</summary>
    public void EndMatch()
    {
        if (_setup == null)
        {
            return;
        }

        Begin(MessageType.MatchEnd, 0);
        MatchEndMessage.Write(_writer, SetupHash);
        byte[] end = _writer.AsSpan().ToArray();
        foreach (StreamFriend friend in _friends.Values)
        {
            Send(friend, Channel.Events, end);
            friend.Reset();
        }

        _setup = null;
        SetupHash = 0;
        _tables.Clear();
        _history.Clear();
    }

    /// <summary>
    /// A friend joined the session, or rejoined it (<see cref="HostEventKind.Rejoined"/>: their session restarted before the
    /// host's noticed): they get the current match's setup, or, between matches, word that there's no match (they may
    /// still be showing one from before they dropped). A friend already here starts over: no stream until they say again
    /// that they loaded the match, then the tables and a keyframe.
    /// </summary>
    public void AddFriend(ulong steamId)
    {
        if (_friends.TryGetValue(steamId, out StreamFriend? friend))
        {
            friend.Reset();
        }
        else
        {
            friend = new StreamFriend(steamId);
            _friends[steamId] = friend;
        }

        if (_setup != null)
        {
            Send(friend, Channel.Events, _setup);
            return;
        }

        Begin(MessageType.MatchEnd, 0);
        MatchEndMessage.Write(_writer, 0);
        Send(friend, Channel.Events, _writer.AsSpan());
    }

    /// <summary>A friend left the session.</summary>
    public void RemoveFriend(ulong steamId) => _friends.Remove(steamId);

    /// <summary>
    /// Handles a stream message from a friend (<see cref="MessageType.SnapshotAck"/>, <see cref="MessageType.MatchReady"/>,
    /// <see cref="MessageType.StreamReport"/>). False when it's malformed or from someone who isn't a friend here.
    /// </summary>
    public bool Receive(ulong sender, Channel channel, ReadOnlySpan<byte> message, long nowMicros)
    {
        var reader = new BitReader(message);
        if (!_friends.TryGetValue(sender, out StreamFriend? friend) || !MessageHeader.TryRead(ref reader, out MessageHeader header))
        {
            Malformed++;
            return false;
        }

        switch (header.Type)
        {
            case MessageType.SnapshotAck:
                if (!SnapshotAckMessage.TryRead(ref reader, out uint newest, out bool needKeyframe))
                {
                    Malformed++;
                    return false;
                }

                if (friend.Streaming)
                {
                    friend.Peer.OnAck(newest, needKeyframe);
                }

                return true;
            case MessageType.StreamReport:
                if (!StreamReportMessage.TryRead(ref reader, out StreamReport report))
                {
                    Malformed++;
                    return false;
                }

                if (friend.Streaming)
                {
                    friend.Report = report;
                    friend.ReportMicros = nowMicros;
                }

                return true;
            case MessageType.MatchReady:
                if (!MatchReadyMessage.TryRead(ref reader, out ulong hash, out bool ready, out string reason))
                {
                    Malformed++;
                    return false;
                }

                friend.ReadyHash = hash;
                if (ready && _setup != null && hash == SetupHash)
                {
                    StartStreaming(friend);
                }
                else if (ready)
                {
                    friend.Reset();
                    friend.LoadedWrongMatch = _setup != null;
                }
                else
                {
                    friend.Reset();
                    friend.LoadFailure = _setup != null && hash == SetupHash ? reason : string.Empty;
                }

                return true;
            default:
                Malformed++;
                return false;
        }
    }

    /// <summary>
    /// A new snapshot: each streaming friend gets it encoded against what they acknowledged. Friends with the same base
    /// (a keyframe, or the same acknowledged snapshot) share one encoding; only the header is theirs.
    /// </summary>
    public void Snapshot(SnapshotFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (_setup == null)
        {
            return;
        }

        _history.Add(frame);
        int encoded = 0;
        foreach (StreamFriend friend in _friends.Values)
        {
            if (!friend.Streaming)
            {
                continue;
            }

            if (friend.Peer.Plan(frame, _history, out SnapshotFrame? baseFrame) == SnapshotPlan.Hold)
            {
                continue;
            }

            EncodedSnapshot? body = null;
            for (int i = 0; i < encoded && body == null; i++)
            {
                body = ReferenceEquals(_encoded[i].Base, baseFrame) ? _encoded[i] : null;
            }

            if (body == null)
            {
                if (encoded == _encoded.Count)
                {
                    _encoded.Add(new EncodedSnapshot());
                }

                body = _encoded[encoded++];
                body.Encode(frame, baseFrame);
                SnapshotEncodings++;
                UnencodableSnapshots += body.Encoded ? 0 : 1;
            }

            if (!body.Encoded)
            {
                continue;
            }

            _writer.Reset();
            new MessageHeader(MessageType.Snapshot, friend.NextSnapshotSequence++, frame.Sequence, StreamTime(frame.HostTimeMicros)).Write(_writer);
            _writer.WriteBytes(body.Bytes);
            ReadOnlySpan<byte> bytes = _writer.AsSpan();
            if (Send(friend, Channel.Realtime, bytes))
            {
                friend.SnapshotsSent++;
                friend.SnapshotBytes += bytes.Length;
            }
        }

        for (int i = 0; i < encoded; i++)
        {
            _encoded[i].Base = null; // no frame kept alive past the history
        }
    }

    /// <summary>
    /// Any other stream message, header included, exactly as the recorder wrote it: it goes to every streaming friend;
    /// object and event tables are also kept for friends who start later.
    /// </summary>
    public void Message(MessageType type, Channel channel, ReadOnlySpan<byte> message)
    {
        if (_setup == null)
        {
            return;
        }

        if (type is MessageType.ObjectTable or MessageType.EventTable)
        {
            _tables.Add(message.ToArray());
        }

        foreach (StreamFriend friend in _friends.Values)
        {
            if (friend.Streaming && Send(friend, channel, message))
            {
                friend.MessagesSent++;
            }
        }
    }

    /// <summary>The stream clock in whole milliseconds, as message headers carry it.</summary>
    public static uint StreamTime(long micros) => (uint)Math.Clamp(micros / 1000, 0, uint.MaxValue);

    private void StartStreaming(StreamFriend friend)
    {
        if (friend.Streaming)
        {
            return;
        }

        friend.Reset();
        friend.Streaming = true;
        foreach (byte[] table in _tables)
        {
            Send(friend, Channel.Events, table);
        }

        _fullStateWanted = true;
    }

    private void Begin(MessageType type, uint hostTick)
    {
        _writer.Reset();
        new MessageHeader(type, _sequences[(byte)type]++, hostTick, 0).Write(_writer);
    }

    private bool Send(StreamFriend friend, Channel channel, ReadOnlySpan<byte> message)
    {
        if (!_transport.Send(friend.SteamId, channel, message))
        {
            friend.SendFailures++;
            return false;
        }

        friend.BytesSent += message.Length;
        if (channel == Channel.Realtime && message.Length > ProtocolInfo.MaxUnreliableMessageBytes)
        {
            friend.OversizeSends++;
        }

        return true;
    }

    /// <summary>One snapshot body (after the header), encoded against <see cref="Base"/> for every friend with that base.</summary>
    private sealed class EncodedSnapshot
    {
        private readonly BitWriter _body = new(2048);

        public SnapshotFrame? Base { get; set; }

        public bool Encoded { get; private set; }

        public ReadOnlySpan<byte> Bytes => _body.AsSpan();

        public void Encode(SnapshotFrame frame, SnapshotFrame? baseFrame)
        {
            Base = baseFrame;
            _body.Reset();
            Encoded = SnapshotCodec.Write(_body, frame, baseFrame);
        }
    }
}
