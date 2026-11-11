using System;
using System.Collections.Generic;
using System.Linq;
using T2TOnline.Core.Input;
using T2TOnline.Core.Protocol;
using T2TOnline.Core.Serialization;

namespace T2TOnline.Core.Session;

/// <summary>A friend as the host sees them.</summary>
public sealed class RemoteFriend
{
    internal RemoteFriend(ulong steamId, HelloMessage hello, long nowMicros)
    {
        SteamId = steamId;
        Hello = hello;
        DeviceType = hello.DeviceType;
        Link = new PeerLink(nowMicros);
    }

    /// <summary>The friend's Steam ID (from Steam, not from anything they sent).</summary>
    public ulong SteamId { get; }

    /// <summary>What they said about themselves (in their latest hello).</summary>
    public HelloMessage Hello { get; internal set; }

    /// <summary>False while waiting for the host to accept them.</summary>
    public bool IsJoined { get; internal set; }

    /// <summary>The Rewired player their virtual pad is on, once joined.</summary>
    public byte Slot { get; internal set; }

    /// <summary>
    /// Their pad stream, once joined with a pad (<see cref="HelloMessage.Input"/>): call <see cref="PadInputReceiver.Update"/>
    /// once per input update. Null for a keyboard friend.
    /// </summary>
    public PadInputReceiver? Input { get; internal set; }

    /// <summary>
    /// Their keyboard-and-mouse stream, once joined with a keyboard: call <see cref="KeyboardMouseReceiver.Update"/> once per
    /// input update. Its frames are read against the key table of their <see cref="Bindings"/> (neutral until those arrive).
    /// Null for a pad friend.
    /// </summary>
    public KeyboardMouseReceiver? Keyboard { get; internal set; }

    /// <summary>Their input stream, whichever kind it is (for reports), or null before they joined.</summary>
    public IInputReceiver? Received => (IInputReceiver?)Input ?? Keyboard;

    /// <summary>
    /// Their controls (<see cref="HostEventKind.BindingsChanged"/>): their latest <see cref="MessageType.InputRemaps"/> taken,
    /// or null until one is (each hello starts them over).
    /// </summary>
    public InputBindingSet? Bindings { get; internal set; }

    /// <summary><see cref="MessageType.InputRemaps"/> payloads that didn't decode, or were for another kind of device than theirs.</summary>
    public long BadRemaps { get; internal set; }

    /// <summary>A remaps payload not taken yet (taken at most once a second), or null.</summary>
    internal byte[]? PendingRemaps { get; set; }

    /// <summary>When a remaps payload was last taken, or <see cref="long.MinValue"/>.</summary>
    internal long RemapsTakenMicros { get; set; } = long.MinValue;

    /// <summary>
    /// Their button prompt names (<see cref="HostEventKind.PromptNamesChanged"/>): their latest
    /// <see cref="MessageType.PromptNames"/> taken, or null until one is (each hello starts them over).
    /// </summary>
    public PromptNameTable? PromptNames { get; internal set; }

    /// <summary><see cref="MessageType.PromptNames"/> payloads that were too big or didn't decode.</summary>
    public long BadPromptNames { get; internal set; }

    /// <summary>A prompt names payload not taken yet (taken at most once a second), or null.</summary>
    internal byte[]? PendingPromptNames { get; set; }

    /// <summary>When a prompt names payload was last taken, or <see cref="long.MinValue"/>.</summary>
    internal long PromptNamesTakenMicros { get; set; } = long.MinValue;

    /// <summary>False while their pad (or keyboard) is away: unplugged, or lent back to their own game's remapper or settings.</summary>
    public bool PadConnected { get; internal set; } = true;

    /// <summary>Why their pad is away while <see cref="PadConnected"/> is false (<see cref="PadAwayReason.Unplugged"/> otherwise).</summary>
    public PadAwayReason AwayReason { get; internal set; }

    /// <summary>
    /// Their pad's family, the game's own device type: from their hello, then from each change they sent
    /// (<see cref="HostEventKind.PadDeviceChanged"/>).
    /// </summary>
    public byte DeviceType { get; internal set; }

    /// <summary>A family they sent that isn't taken yet (changes are taken at most once a second), or -1.</summary>
    internal int PendingDeviceType { get; set; } = -1;

    /// <summary>When a change of family was last taken, or <see cref="long.MinValue"/>.</summary>
    internal long DeviceChangedMicros { get; set; } = long.MinValue;

    /// <summary>Traffic and health.</summary>
    public PeerLink Link { get; }

    /// <summary>
    /// Repeated hellos answered without raising <see cref="HostEventKind.Rejoined"/>, because one was raised for them less
    /// than <see cref="SessionTiming.RejoinIntervalMicros"/> before.
    /// </summary>
    public long RejoinsHeldBack { get; internal set; }

    /// <summary>When <see cref="HostEventKind.Rejoined"/> was last raised for them, or <see cref="long.MinValue"/>.</summary>
    internal long LastRejoinedMicros { get; set; } = long.MinValue;

    /// <summary>
    /// What they last said about themselves in the lobby (<see cref="HostEventKind.LobbyMember"/>), once <see cref="HasMember"/>;
    /// each hello starts it over. Theirs by the transport's word: they can't speak for another seat.
    /// </summary>
    public LobbyMemberMessage Member { get; internal set; }

    /// <summary>False until a member message of theirs was taken (each hello starts it over).</summary>
    public bool HasMember { get; internal set; }

    /// <summary><see cref="MessageType.LobbyMember"/> messages that didn't decode or came on another channel.</summary>
    public long BadMembers { get; internal set; }

    /// <summary>A member message not taken yet (taken at most four times a second), or null.</summary>
    internal LobbyMemberMessage? PendingMember { get; set; }

    /// <summary>When a member message was last taken, or <see cref="long.MinValue"/>.</summary>
    internal long MemberTakenMicros { get; set; } = long.MinValue;

    /// <summary>The board last sent to them (the same array as the session's newest when they're up to date), or null.</summary>
    internal byte[]? BoardSent { get; set; }

    /// <summary>When a board last went to them, or <see cref="long.MinValue"/>.</summary>
    internal long BoardSentMicros { get; set; } = long.MinValue;
}

/// <summary>What happened in a <see cref="HostSession"/>.</summary>
public enum HostEventKind
{
    /// <summary>A friend said hello with matching versions; call <see cref="HostSession.Accept"/> or <see cref="HostSession.Refuse"/>.</summary>
    JoinRequested,

    /// <summary>
    /// A friend was refused (versions, full); <see cref="HostEvent.Reject"/> says why. Usually before joining; a joined
    /// friend whose new hello no longer matches (another protocol or game build, or an input kind this host doesn't know) is
    /// refused and removed, and <see cref="Left"/> follows.
    /// </summary>
    Refused,

    /// <summary>A friend joined.</summary>
    Joined,

    /// <summary>A friend went silent for <see cref="SessionTiming.LaggingAfterMicros"/>.</summary>
    Lagging,

    /// <summary>A lagging friend is heard again.</summary>
    Recovered,

    /// <summary>
    /// A friend's pad (or keyboard) is away (<see cref="HostEvent.Away"/> says why: unplugged, or lent to their own game's
    /// remapper or settings); raised again when they send another reason while away.
    /// </summary>
    PadDisconnected,

    /// <summary>A friend's pad (or keyboard) is back; <see cref="HostEvent.Away"/> says why it had been away.</summary>
    PadReconnected,

    /// <summary>A friend left or was removed; <see cref="HostEvent.Reason"/> says why.</summary>
    Left,

    /// <summary>
    /// A joined friend said hello again: their session restarted before this one noticed (or a hello retry crossed the
    /// welcome). They're answered again, and their input stream (a pad's or a keyboard's: they may come back with the other,
    /// <see cref="HostEvent.Hello"/>), their controls and their loss count start over; call
    /// <c>Stream.MatchStreamHost.AddFriend</c> again so they get the current match. Raised at most once per
    /// <see cref="SessionTiming.RejoinIntervalMicros"/> per friend (<see cref="RemoteFriend.RejoinsHeldBack"/> counts the
    /// repeats in between, which are answered all the same).
    /// </summary>
    Rejoined,

    /// <summary>
    /// A joined friend's pad is now another family (<see cref="HostEvent.DeviceType"/>, also in
    /// <see cref="RemoteFriend.DeviceType"/>): they picked up another pad. Taken at most once per
    /// <see cref="SessionTiming.PadDeviceIntervalMicros"/> per friend, the newest family winning. Tell them what their
    /// virtual pad reads as with <see cref="HostSession.SendPadDevice"/>.
    /// </summary>
    PadDeviceChanged,

    /// <summary>
    /// A joined friend's controls arrived (<see cref="MessageType.InputRemaps"/>, sent after every welcome), in
    /// <see cref="RemoteFriend.Bindings"/>: their virtual controller's maps follow them. Taken at most once per
    /// <see cref="SessionTiming.RemapsIntervalMicros"/> per friend, the newest winning. A keyboard friend's frames are read
    /// against the new key table from now on.
    /// </summary>
    BindingsChanged,

    /// <summary>
    /// A joined friend's button prompt names arrived (<see cref="MessageType.PromptNames"/>, sent after every welcome and
    /// whenever their device or controls change), in <see cref="RemoteFriend.PromptNames"/>: the host's prompts for their
    /// game player are named from them. Taken at most once per <see cref="SessionTiming.PromptNamesIntervalMicros"/> per
    /// friend, the newest winning.
    /// </summary>
    PromptNamesChanged,

    /// <summary>
    /// What a joined friend says about themselves in the lobby changed (<see cref="HostEvent.Member"/>, also in
    /// <see cref="RemoteFriend.Member"/>): their ready mark, their stage, the Start epoch they follow. Taken at most once per
    /// <see cref="SessionTiming.LobbyMemberIntervalMicros"/> per friend, the newest winning. The same again raises nothing.
    /// </summary>
    LobbyMember,
}

/// <summary>One thing that happened in a <see cref="HostSession"/>.</summary>
/// <param name="Kind">What.</param>
/// <param name="SteamId">Which friend.</param>
/// <param name="Reason">For <see cref="HostEventKind.Left"/>.</param>
/// <param name="Reject">For <see cref="HostEventKind.Refused"/>.</param>
/// <param name="Hello">The friend's hello, for <see cref="HostEventKind.JoinRequested"/>, <see cref="HostEventKind.Refused"/> and
/// <see cref="HostEventKind.Rejoined"/>.</param>
/// <param name="DeviceType">For <see cref="HostEventKind.PadDeviceChanged"/>: the friend's pad family now.</param>
/// <param name="Away">For <see cref="HostEventKind.PadDisconnected"/>: why their pad is away; for
/// <see cref="HostEventKind.PadReconnected"/>: why it had been.</param>
/// <param name="Member">For <see cref="HostEventKind.LobbyMember"/>: what the friend says now.</param>
public readonly record struct HostEvent(HostEventKind Kind, ulong SteamId, LeaveReason Reason = LeaveReason.None, RejectReason Reject = RejectReason.None, HelloMessage Hello = default, byte DeviceType = 0,
    PadAwayReason Away = PadAwayReason.Unplugged, LobbyMemberMessage Member = default);

/// <summary>
/// The host's end of a session: the handshake (versions, room), each friend's input stream (a pad's into a
/// <see cref="PadInputReceiver"/>, a keyboard's into a <see cref="KeyboardMouseReceiver"/>), controls
/// (<see cref="InputBindingSet"/>) and prompt names (<see cref="PromptNameTable"/>), pings, and silence timeouts. Game-independent; the caller supplies the transport,
/// the Steam lobby's members, and the time.
/// </summary>
/// <remarks>
/// Only lobby members and friends already in the session are heard: messages from anyone else are dropped unread.
/// Everything received is untrusted: malformed messages are counted and dropped, never thrown.
/// </remarks>
public sealed class HostSession
{
    private readonly SessionIdentity _identity;
    private readonly ISessionTransport _transport;
    private readonly int _maxFriends;
    private const int MaxNewHelloSenders = 64;

    private readonly Dictionary<ulong, RemoteFriend> _friends = new();
    private readonly Dictionary<ulong, long> _newHelloAt = new();
    private readonly HashSet<ulong> _members = new();
    private readonly Queue<HostEvent> _events = new();
    private readonly List<RemoteFriend> _updateScratch = new();
    private readonly BitWriter _writer = new(256);
    private readonly BitWriter _boardWriter = new(256);
    private byte[]? _boardPayload;

    /// <summary>Creates a host session.</summary>
    /// <param name="identity">The host's versions.</param>
    /// <param name="transport">Where messages go.</param>
    /// <param name="maxFriends">Most friends at once (the couch game has four players).</param>
    /// <param name="hostingSession">This hosting session's id (<see cref="HostingSession"/>); 0 makes a new one.</param>
    public HostSession(SessionIdentity identity, ISessionTransport transport, int maxFriends = 3, ulong hostingSession = 0)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(transport);
        if (maxFriends < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxFriends), "At least one friend.");
        }

        _identity = identity;
        _transport = transport;
        _maxFriends = maxFriends;
        HostingSession = hostingSession != 0 ? hostingSession : NewHostingSession();
    }

    /// <summary>
    /// This hosting session's id, random and never 0, sent in every friend's welcome (<see cref="WelcomeMessage.HostingSession"/>):
    /// what a friend shared of it survives that friend's restart and rejoin (D34), and its progress batches carry it too.
    /// </summary>
    public ulong HostingSession { get; }

    /// <summary>A new hosting session's id: random, never 0.</summary>
    public static ulong NewHostingSession()
    {
        Span<byte> bytes = stackalloc byte[8];
        ulong id;
        do
        {
            System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);
            id = BitConverter.ToUInt64(bytes);
        }
        while (id == 0);
        return id;
    }

    /// <summary>Friends waiting or joined.</summary>
    public IReadOnlyCollection<RemoteFriend> Friends => _friends.Values;

    /// <summary>Messages dropped because the sender is neither in the lobby nor a friend in the session.</summary>
    public long FromStrangers { get; private set; }

    /// <summary>Messages from lobby members that didn't decode before a friend entry existed for them.</summary>
    public long MalformedFromMembers { get; private set; }

    /// <summary>First hellos not looked at, for coming sooner than <see cref="SessionTiming.NewHelloIntervalMicros"/> after the sender's last.</summary>
    public long HellosHeldBack { get; private set; }

    /// <summary>Gets the match stream's messages from joined friends (acknowledgements, match ready); unset, they count as malformed.</summary>
    public StreamMessageHandler? StreamMessages { get; set; }

    /// <summary>Gets the campaign's messages from joined friends (follow reports); unset, they count as malformed.</summary>
    public StreamMessageHandler? CampaignMessages { get; set; }

    /// <summary>
    /// Where each welcome says the host's game is, set from the lobby flow (<c>Lobby.LobbyFlow.WelcomePhase</c>). Unset it's
    /// <see cref="SessionPhase.Unknown"/>, which a friend reads as playing, the behaviour before the lobby.
    /// </summary>
    public SessionPhase WelcomePhase { get; set; } = SessionPhase.Unknown;

    /// <summary>
    /// While not 0, the only sender let in: every other hello is refused with <see cref="RejectReason.HostUnavailable"/>
    /// before anything else about it is looked at, and a joined friend who isn't this one is refused and dropped at their
    /// next hello. The developer's check tour sets it to its one-PC stand-in (this same account) for as long as it runs, so
    /// nobody else gets a seat in a tour's lobby. 0 (the default) lets in anyone the other rules do.
    /// </summary>
    public ulong AdmitOnly { get; set; }

    /// <summary>The newest board published (<see cref="PublishBoard"/>), or null.</summary>
    public LobbyBoardMessage? Board { get; private set; }

    /// <summary>Boards published that differed from the one before.</summary>
    public long BoardChanges { get; private set; }

    /// <summary>The friend with <paramref name="steamId"/>, or null.</summary>
    public RemoteFriend? Find(ulong steamId) => _friends.TryGetValue(steamId, out RemoteFriend? friend) ? friend : null;

    /// <summary>Takes the oldest unhandled event.</summary>
    public bool TryTakeEvent(out HostEvent hostEvent) => _events.TryDequeue(out hostEvent);

    /// <summary>
    /// Sets who's in the Steam lobby: only they can join. A friend missing from it is still heard, and is removed
    /// (<see cref="LeaveReason.LeftLobby"/>, told) by <see cref="Update"/> only once also silent for
    /// <see cref="SessionTiming.LaggingAfterMicros"/>: a lobby poll that misses a friend who's still playing mustn't drop them.
    /// </summary>
    public void SetMembers(IEnumerable<ulong> members)
    {
        ArgumentNullException.ThrowIfNull(members);

        _members.Clear();
        _members.UnionWith(members);
    }

    /// <summary>Handles one message from <paramref name="sender"/>.</summary>
    public void Receive(ulong sender, Channel channel, ReadOnlySpan<byte> message, long nowMicros)
    {
        if (!_members.Contains(sender) && !_friends.ContainsKey(sender))
        {
            FromStrangers++;
            return;
        }

        _friends.TryGetValue(sender, out RemoteFriend? friend);
        var reader = new BitReader(message);
        if (!MessageHeader.TryRead(ref reader, out MessageHeader header))
        {
            CountMalformed(friend);
            return;
        }

        if (friend != null)
        {
            Heard(friend, message.Length, nowMicros);
        }

        switch (header.Type)
        {
            case MessageType.Hello:
                OnHello(sender, friend, ref reader, message.Length, nowMicros);
                break;
            case MessageType.LobbyMember when friend is { IsJoined: true }:
                if (channel != Channel.Events || !LobbyMemberMessage.TryRead(ref reader, out LobbyMemberMessage member))
                {
                    friend.Link.Malformed++;
                    friend.BadMembers++;
                    break;
                }

                friend.PendingMember = member;
                TakeMember(friend, nowMicros);
                break;
            case MessageType.Ping:
                OnPing(sender, friend, ref reader, nowMicros);
                break;
            case MessageType.Pong when friend != null:
                if (!PongMessage.TryRead(ref reader, out PongMessage pong) || !SessionWire.AddRoundTrip(friend.Link, pong, nowMicros, out _))
                {
                    friend.Link.Malformed++;
                }

                break;
            case MessageType.PadInput when friend?.Input != null:
                if (channel != Channel.Realtime || !friend.Input.Receive(ref reader))
                {
                    friend.Link.Malformed++;
                    break;
                }

                friend.Link.RealtimeLoss.Observe(header.Sequence);
                break;
            case MessageType.KeyboardMouseInput when friend?.Keyboard != null:
                if (channel != Channel.Realtime || !friend.Keyboard.Receive(ref reader))
                {
                    friend.Link.Malformed++;
                    break;
                }

                friend.Link.RealtimeLoss.Observe(header.Sequence);
                break;
            case MessageType.InputRemaps when friend is { IsJoined: true }:
                // Kept as bytes and read when taken (at most once a second): a flood costs a copy each, not a read.
                int start = reader.BitPosition / 8;
                if (channel != Channel.Events || message.Length - start > InputRemapsMessage.MaxPayloadBytes)
                {
                    friend.Link.Malformed++;
                    friend.BadRemaps++;
                    break;
                }

                friend.PendingRemaps = message[start..].ToArray();
                TakeRemaps(friend, nowMicros);
                break;
            case MessageType.PromptNames when friend is { IsJoined: true }:
                // As remaps: kept as bytes and read when taken (at most once a second).
                int namesStart = reader.BitPosition / 8;
                if (channel != Channel.Events || message.Length - namesStart > PromptNamesMessage.MaxPayloadBytes)
                {
                    friend.Link.Malformed++;
                    friend.BadPromptNames++;
                    break;
                }

                friend.PendingPromptNames = message[namesStart..].ToArray();
                TakePromptNames(friend, nowMicros);
                break;
            case MessageType.PadDisconnected when friend is { IsJoined: true }:
                // Never fails (no reason, or an unknown one, is Unplugged); the same state again changes nothing.
                _ = PadDisconnectedMessage.TryRead(ref reader, out PadDisconnectedMessage away);
                if (friend.PadConnected || friend.AwayReason != away.Reason)
                {
                    friend.PadConnected = false;
                    friend.AwayReason = away.Reason;
                    _events.Enqueue(new HostEvent(HostEventKind.PadDisconnected, sender, Away: away.Reason));
                }
                break;
            case MessageType.PadReconnected when friend is { IsJoined: true, PadConnected: false }:
                PadBack(friend);
                break;
            case MessageType.PadDevice when friend is { IsJoined: true }:
                if (!PadDeviceMessage.TryRead(ref reader, out PadDeviceMessage device))
                {
                    friend.Link.Malformed++;
                    break;
                }

                friend.PendingDeviceType = device.DeviceType;
                TakePadDevice(friend, nowMicros);
                break;
            case MessageType.Leave when friend != null:
                LeaveReason reason = LeaveMessage.TryRead(ref reader, out LeaveMessage leave) ? leave.Reason : LeaveReason.None;
                Drop(sender, reason, tell: false);
                break;
            case MessageType.SnapshotAck or MessageType.MatchReady or MessageType.StreamReport when friend is { IsJoined: true }:
                if (StreamMessages?.Invoke(sender, channel, message, nowMicros) != true)
                {
                    CountMalformed(friend);
                }

                break;
            case MessageType.FollowReport or MessageType.ProgressResend or MessageType.ProgressRunRequest when friend is { IsJoined: true }:
                if (CampaignMessages?.Invoke(sender, channel, message, nowMicros) != true)
                {
                    CountMalformed(friend);
                }

                break;
            default:
                // A known type that makes no sense here (or arrives before its friend joined): counted, ignored.
                CountMalformed(friend);
                break;
        }
    }

    /// <summary>
    /// Accepts a friend waiting after <see cref="HostEventKind.JoinRequested"/>, with their virtual pad on Rewired
    /// player <paramref name="slot"/>. False if they're no longer waiting.
    /// </summary>
    public bool Accept(ulong steamId, byte slot, long nowMicros)
    {
        if (!_friends.TryGetValue(steamId, out RemoteFriend? friend) || friend.IsJoined)
        {
            return false;
        }

        friend.IsJoined = true;
        friend.Slot = slot;
        NewInputStream(friend);
        friend.Link.NextPingAtMicros = nowMicros;
        SendWelcome(friend);
        SendBoard(friend, nowMicros);
        _events.Enqueue(new HostEvent(HostEventKind.Joined, steamId));
        return true;
    }

    /// <summary>
    /// The lobby's board, as it is now: each joined friend gets it if it differs from what they last got, at most once per
    /// <see cref="SessionTiming.LobbyBoardIntervalMicros"/> (<see cref="Update"/> sends what had to wait, the newest winning), and a
    /// friend who joins later gets the newest at once. Call it whenever something on it may have changed: an unchanged
    /// board costs an encode and a compare. True when it differed from the one before.
    /// </summary>
    public bool PublishBoard(LobbyBoardMessage board, long nowMicros)
    {
        ArgumentNullException.ThrowIfNull(board);

        _boardWriter.Reset();
        board.Write(_boardWriter);
        bool changed = _boardPayload == null || !_boardWriter.AsSpan().SequenceEqual(_boardPayload);
        if (changed)
        {
            Board = board;
            _boardPayload = _boardWriter.ToArray();
            BoardChanges++;
        }

        foreach (RemoteFriend friend in _friends.Values)
        {
            SendBoard(friend, nowMicros);
        }

        return changed;
    }

    /// <summary>
    /// Tells a joined friend the host earned an achievement, with the play it came from (reliable, in order with
    /// everything else on the events channel). False if they aren't joined, the moment can't be sent, or the transport
    /// refused.
    /// </summary>
    public bool SendAchievement(ulong steamId, in AchievementMoment moment)
    {
        ArgumentNullException.ThrowIfNull(moment.Name);
        if (!_friends.TryGetValue(steamId, out RemoteFriend? friend) || !friend.IsJoined || !AchievementUnlockedMessage.IsPlausibleName(moment.Name) || moment.Match < 0)
        {
            return false;
        }

        SessionWire.Begin(_writer, friend.Link, MessageType.AchievementUnlocked);
        AchievementUnlockedMessage.Write(_writer, moment);
        return SessionWire.Send(_transport, friend.SteamId, friend.Link, Channel.Events, _writer);
    }

    /// <summary>
    /// Tells a joined friend which pad family their virtual pad reads as here (after a join, a rejoin and each
    /// <see cref="HostEventKind.PadDeviceChanged"/>), so their status box can say so. False if they aren't joined or the
    /// transport refused.
    /// </summary>
    public bool SendPadDevice(ulong steamId, byte deviceType)
    {
        if (!_friends.TryGetValue(steamId, out RemoteFriend? friend) || !friend.IsJoined)
        {
            return false;
        }

        SessionWire.Begin(_writer, friend.Link, MessageType.PadDevice);
        new PadDeviceMessage(deviceType).Write(_writer);
        return SessionWire.Send(_transport, friend.SteamId, friend.Link, Channel.Events, _writer);
    }

    /// <summary>Refuses a waiting friend (for example, no controller slot could be made for them).</summary>
    public void Refuse(ulong steamId, RejectReason reason)
    {
        if (_friends.TryGetValue(steamId, out RemoteFriend? friend) && !friend.IsJoined)
        {
            SendReject(steamId, friend.Link, reason);
            _friends.Remove(steamId);
            _events.Enqueue(new HostEvent(HostEventKind.Refused, steamId, Reject: reason, Hello: friend.Hello));
        }
    }

    /// <summary>Removes a friend, telling them why.</summary>
    public void Remove(ulong steamId, LeaveReason reason) => Drop(steamId, reason, tell: true);

    /// <summary>Ends the session: every friend is told and removed.</summary>
    public void Close()
    {
        foreach (ulong id in _friends.Keys.ToList())
        {
            Drop(id, LeaveReason.HostClosed, tell: true);
        }
    }

    /// <summary>Call every frame: pings, silence timeouts, and friends gone from the lobby.</summary>
    public void Update(long nowMicros)
    {
        _updateScratch.Clear();
        _updateScratch.AddRange(_friends.Values);
        foreach (RemoteFriend friend in _updateScratch)
        {
            long silence = nowMicros - friend.Link.LastHeardMicros;
            if (silence > SessionTiming.GoneAfterMicros)
            {
                Drop(friend.SteamId, LeaveReason.TimedOut, tell: true);
                continue;
            }

            if (silence > SessionTiming.LaggingAfterMicros && !_members.Contains(friend.SteamId))
            {
                // Out of the lobby and silent: they left (out of the lobby alone can be a poll that missed them).
                Drop(friend.SteamId, LeaveReason.LeftLobby, tell: true);
                continue;
            }

            if (silence > SessionTiming.LaggingAfterMicros && !friend.Link.IsLagging)
            {
                friend.Link.IsLagging = true;
                _events.Enqueue(new HostEvent(HostEventKind.Lagging, friend.SteamId));
            }

            if (friend.IsJoined && nowMicros >= friend.Link.NextPingAtMicros)
            {
                friend.Link.NextPingAtMicros = nowMicros + SessionTiming.PingIntervalMicros;
                SessionWire.SendPing(_transport, friend.SteamId, friend.Link, _writer, nowMicros);
            }

            if (friend.PendingDeviceType >= 0)
            {
                TakePadDevice(friend, nowMicros);
            }

            if (friend.PendingRemaps != null)
            {
                TakeRemaps(friend, nowMicros);
            }

            if (friend.PendingPromptNames != null)
            {
                TakePromptNames(friend, nowMicros);
            }

            if (friend.PendingMember != null)
            {
                TakeMember(friend, nowMicros);
            }

            SendBoard(friend, nowMicros);
        }

        _updateScratch.Clear();
    }

    private void OnHello(ulong sender, RemoteFriend? friend, ref BitReader reader, int length, long nowMicros)
    {
        if (!HelloMessage.TryRead(ref reader, out HelloMessage hello))
        {
            CountMalformed(friend);
            return;
        }

        RejectReason reject =
            AdmitOnly != 0 && sender != AdmitOnly ? RejectReason.HostUnavailable
            : hello.ProtocolVersion != _identity.ProtocolVersion ? RejectReason.ProtocolMismatch
            : hello.GameBuild != _identity.GameBuild ? RejectReason.GameBuildMismatch
            : hello.Input is not (InputKind.Gamepad or InputKind.KeyboardMouse) ? RejectReason.UnsupportedInput
            : RejectReason.None;
        if (friend != null)
        {
            if (reject != RejectReason.None)
            {
                // A repeat that no longer matches (their game restarted on another version or build, or with an input this
                // host doesn't know): refused as a first hello would be, and out of the session, joined or not.
                SendReject(sender, friend.Link, reject);
                _events.Enqueue(new HostEvent(HostEventKind.Refused, sender, Reject: reject, Hello: hello));
                if (friend.IsJoined)
                {
                    Drop(sender, LeaveReason.Removed, tell: false);
                }
                else
                {
                    _friends.Remove(sender);
                }

                return;
            }

            // A repeat (the friend retries until answered, or its session restarted before this one noticed): answer a
            // joined friend again with a fresh input stream, so counters that restarted at 0 can't read as presses, and a
            // fresh loss count, so sequence numbers that restarted at 0 aren't a gap. A new session's pad starts out
            // plugged in (it says so if it isn't). Rejoined (which makes the caller send them everything again) is raised
            // at most once per RejoinIntervalMicros: hello retries and a misbehaving friend can't repeat that at will.
            if (friend.IsJoined)
            {
                // Their hello says which pad (or keyboard) they have now (the caller re-registers it, or swaps their virtual
                // controller, with Rejoined); a change still waiting from before is older than that. Their controls start
                // over too: they send them after every welcome, and a restarted session's key table may differ under the
                // same generation. The once-a-second limit on taking them stays, so hellos can't speed it up.
                friend.Hello = hello;
                friend.DeviceType = hello.DeviceType;
                friend.PendingDeviceType = -1;
                NewInputStream(friend);
                friend.Link.RealtimeLoss.Reset();
                friend.BoardSent = null; // they get the board again, within its limit: hellos can't speed it up
                SendWelcome(friend);
                SendBoard(friend, nowMicros);
                if (friend.LastRejoinedMicros == long.MinValue || nowMicros - friend.LastRejoinedMicros >= SessionTiming.RejoinIntervalMicros)
                {
                    friend.LastRejoinedMicros = nowMicros;
                    _events.Enqueue(new HostEvent(HostEventKind.Rejoined, sender, Hello: hello));
                }
                else
                {
                    friend.RejoinsHeldBack++;
                }

                if (!friend.PadConnected)
                {
                    PadBack(friend);
                }
            }

            return;
        }

        if (!TakeNewHello(sender, nowMicros))
        {
            HellosHeldBack++;
            return;
        }

        if (reject == RejectReason.None && _friends.Count >= _maxFriends)
        {
            reject = RejectReason.SessionFull;
        }

        if (reject != RejectReason.None)
        {
            SendReject(sender, new PeerLink(nowMicros), reject);
            _events.Enqueue(new HostEvent(HostEventKind.Refused, sender, Reject: reject, Hello: hello));
            return;
        }

        var waiting = new RemoteFriend(sender, hello, nowMicros);
        Heard(waiting, length, nowMicros);
        _friends[sender] = waiting;
        _events.Enqueue(new HostEvent(HostEventKind.JoinRequested, sender, Hello: hello));
    }

    /// <summary>False when <paramref name="sender"/>'s last first hello was looked at less than <see cref="SessionTiming.NewHelloIntervalMicros"/> ago.</summary>
    private bool TakeNewHello(ulong sender, long nowMicros)
    {
        if (_newHelloAt.TryGetValue(sender, out long last) && nowMicros - last < SessionTiming.NewHelloIntervalMicros)
        {
            return false;
        }

        if (_newHelloAt.Count >= MaxNewHelloSenders)
        {
            _newHelloAt.Clear(); // only lobby members get here, at most a few at a time: this is years of them
        }

        _newHelloAt[sender] = nowMicros;
        return true;
    }

    private void OnPing(ulong sender, RemoteFriend? friend, ref BitReader reader, long nowMicros)
    {
        if (!PingMessage.TryRead(ref reader, out PingMessage ping))
        {
            CountMalformed(friend);
            return;
        }

        // Only friends in the session get answers: a lobby member who hasn't said hello learns nothing.
        if (friend == null)
        {
            return;
        }

        SessionWire.Begin(_writer, friend.Link, MessageType.Pong);
        new PongMessage(ping.Id, ping.SendTimeMicros, nowMicros).Write(_writer);
        SessionWire.Send(_transport, sender, friend.Link, Channel.Realtime, _writer);
    }

    /// <summary>
    /// Takes the pad family a joined friend sent last, unless one was taken less than
    /// <see cref="SessionTiming.PadDeviceIntervalMicros"/> ago (then <see cref="Update"/> takes it once that's past). A
    /// family they already have changes nothing.
    /// </summary>
    private void TakePadDevice(RemoteFriend friend, long nowMicros)
    {
        if (friend.DeviceChangedMicros != long.MinValue && nowMicros - friend.DeviceChangedMicros < SessionTiming.PadDeviceIntervalMicros)
        {
            return;
        }

        byte deviceType = (byte)friend.PendingDeviceType;
        friend.PendingDeviceType = -1;
        if (deviceType == friend.DeviceType)
        {
            return;
        }

        friend.DeviceType = deviceType;
        friend.DeviceChangedMicros = nowMicros;
        _events.Enqueue(new HostEvent(HostEventKind.PadDeviceChanged, friend.SteamId, DeviceType: deviceType));
    }

    /// <summary>
    /// Takes the controls a joined friend sent last, unless some were taken less than
    /// <see cref="SessionTiming.RemapsIntervalMicros"/> ago (then <see cref="Update"/> takes them once that's past). Read
    /// here, not as they arrive: controls that don't decode, or are for another kind of device than the friend's, are
    /// counted and dropped.
    /// </summary>
    private void TakeRemaps(RemoteFriend friend, long nowMicros)
    {
        if (friend.RemapsTakenMicros != long.MinValue && nowMicros - friend.RemapsTakenMicros < SessionTiming.RemapsIntervalMicros)
        {
            return;
        }

        byte[] payload = friend.PendingRemaps!;
        friend.PendingRemaps = null;
        friend.RemapsTakenMicros = nowMicros;
        var reader = new BitReader(payload);
        if (!InputRemapsMessage.TryRead(ref reader, out InputBindingSet? set) || set!.Device != friend.Hello.Input)
        {
            friend.Link.Malformed++;
            friend.BadRemaps++;
            return;
        }

        friend.Bindings = set;
        if (set.Device == InputKind.KeyboardMouse)
        {
            friend.Keyboard?.UseTable(set.Generation, set.Elements.Count);
        }

        _events.Enqueue(new HostEvent(HostEventKind.BindingsChanged, friend.SteamId));
    }

    /// <summary>
    /// Takes the prompt names a joined friend sent last, unless some were taken less than
    /// <see cref="SessionTiming.PromptNamesIntervalMicros"/> ago (then <see cref="Update"/> takes them once that's past).
    /// Names that don't decode are counted and dropped; the table before stays.
    /// </summary>
    private void TakePromptNames(RemoteFriend friend, long nowMicros)
    {
        if (friend.PromptNamesTakenMicros != long.MinValue && nowMicros - friend.PromptNamesTakenMicros < SessionTiming.PromptNamesIntervalMicros)
        {
            return;
        }

        byte[] payload = friend.PendingPromptNames!;
        friend.PendingPromptNames = null;
        friend.PromptNamesTakenMicros = nowMicros;
        var reader = new BitReader(payload);
        if (!PromptNamesMessage.TryRead(ref reader, out PromptNameTable? table))
        {
            friend.Link.Malformed++;
            friend.BadPromptNames++;
            return;
        }

        friend.PromptNames = table;
        _events.Enqueue(new HostEvent(HostEventKind.PromptNamesChanged, friend.SteamId));
    }

    /// <summary>
    /// Takes the member message a joined friend sent last, unless one was taken less than
    /// <see cref="SessionTiming.LobbyMemberIntervalMicros"/> ago (then <see cref="Update"/> takes it once that's past). What they
    /// already said changes nothing.
    /// </summary>
    private void TakeMember(RemoteFriend friend, long nowMicros)
    {
        if (friend.MemberTakenMicros != long.MinValue && nowMicros - friend.MemberTakenMicros < SessionTiming.LobbyMemberIntervalMicros)
        {
            return;
        }

        LobbyMemberMessage member = friend.PendingMember!.Value;
        friend.PendingMember = null;
        if (friend.HasMember && friend.Member == member)
        {
            return;
        }

        friend.Member = member;
        friend.HasMember = true;
        friend.MemberTakenMicros = nowMicros;
        _events.Enqueue(new HostEvent(HostEventKind.LobbyMember, friend.SteamId, Member: member));
    }

    /// <summary>Sends a joined friend the newest board if they're behind and it's been long enough. A refused send is tried again at the next.</summary>
    private void SendBoard(RemoteFriend friend, long nowMicros)
    {
        if (_boardPayload == null || !friend.IsJoined || ReferenceEquals(friend.BoardSent, _boardPayload) ||
            (friend.BoardSentMicros != long.MinValue && nowMicros - friend.BoardSentMicros < SessionTiming.LobbyBoardIntervalMicros))
        {
            return;
        }

        friend.BoardSentMicros = nowMicros;
        SessionWire.Begin(_writer, friend.Link, MessageType.LobbyBoard);
        _writer.WriteBytes(_boardPayload);
        if (SessionWire.Send(_transport, friend.SteamId, friend.Link, Channel.Events, _writer))
        {
            friend.BoardSent = _boardPayload;
        }
    }

    /// <summary>A friend's pad is back: <see cref="HostEventKind.PadReconnected"/>, with why it had been away.</summary>
    private void PadBack(RemoteFriend friend)
    {
        PadAwayReason was = friend.AwayReason;
        friend.PadConnected = true;
        friend.AwayReason = PadAwayReason.Unplugged;
        _events.Enqueue(new HostEvent(HostEventKind.PadReconnected, friend.SteamId, Away: was));
    }

    /// <summary>A fresh input stream of the kind the friend's hello says, and no controls yet.</summary>
    private static void NewInputStream(RemoteFriend friend)
    {
        bool keyboard = friend.Hello.Input == InputKind.KeyboardMouse;
        friend.Input = keyboard ? null : new PadInputReceiver(1_000_000);
        friend.Keyboard = keyboard ? new KeyboardMouseReceiver(1_000_000) : null;
        friend.Bindings = null;
        friend.PendingRemaps = null;
        friend.PromptNames = null;
        friend.PendingPromptNames = null;
        friend.Member = default;
        friend.HasMember = false;
        friend.PendingMember = null;
    }

    private void SendWelcome(RemoteFriend friend)
    {
        SessionWire.Begin(_writer, friend.Link, MessageType.Welcome);
        new WelcomeMessage(friend.Slot, WelcomePhase, HostingSession).Write(_writer);
        SessionWire.Send(_transport, friend.SteamId, friend.Link, Channel.Events, _writer);
    }

    private void SendReject(ulong steamId, PeerLink link, RejectReason reason)
    {
        SessionWire.Begin(_writer, link, MessageType.Reject);
        new RejectMessage(reason).Write(_writer);
        SessionWire.Send(_transport, steamId, link, Channel.Events, _writer);
    }

    private void Drop(ulong steamId, LeaveReason reason, bool tell)
    {
        if (!_friends.Remove(steamId, out RemoteFriend? friend))
        {
            return;
        }

        if (tell)
        {
            SessionWire.Begin(_writer, friend.Link, MessageType.Leave);
            new LeaveMessage(reason).Write(_writer);
            SessionWire.Send(_transport, steamId, friend.Link, Channel.Events, _writer);
        }

        _events.Enqueue(new HostEvent(HostEventKind.Left, steamId, reason));
    }

    private void Heard(RemoteFriend friend, int bytes, long nowMicros)
    {
        friend.Link.LastHeardMicros = nowMicros;
        friend.Link.MessagesIn++;
        friend.Link.BytesIn += bytes;
        if (friend.Link.IsLagging)
        {
            friend.Link.IsLagging = false;
            _events.Enqueue(new HostEvent(HostEventKind.Recovered, friend.SteamId));
        }
    }

    private void CountMalformed(RemoteFriend? friend)
    {
        if (friend != null)
        {
            friend.Link.Malformed++;
        }
        else
        {
            MalformedFromMembers++;
        }
    }
}
