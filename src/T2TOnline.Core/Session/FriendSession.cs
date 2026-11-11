using System;
using System.Collections.Generic;
using T2TOnline.Core.Input;
using T2TOnline.Core.Protocol;
using T2TOnline.Core.Serialization;
using T2TOnline.Core.Timing;

namespace T2TOnline.Core.Session;

/// <summary>Where a <see cref="FriendSession"/> is.</summary>
public enum FriendSessionState
{
    /// <summary>Not started.</summary>
    Idle,

    /// <summary>Hello sent, waiting for the host's answer.</summary>
    Connecting,

    /// <summary>Accepted: the input stream is flowing.</summary>
    Joined,

    /// <summary>The host refused; <see cref="FriendSession.RejectReason"/> says why.</summary>
    Rejected,

    /// <summary>Over; <see cref="FriendSession.EndReason"/> says why.</summary>
    Ended,
}

/// <summary>What happened in a <see cref="FriendSession"/>.</summary>
public enum FriendEventKind
{
    /// <summary>The host accepted us.</summary>
    Joined,

    /// <summary>The host refused us.</summary>
    Rejected,

    /// <summary>The host went silent for <see cref="SessionTiming.LaggingAfterMicros"/>.</summary>
    Lagging,

    /// <summary>The host is heard again.</summary>
    Recovered,

    /// <summary>The session is over.</summary>
    Ended,

    /// <summary>The host earned an achievement (<see cref="FriendEvent.Achievement"/>).</summary>
    AchievementEarned,

    /// <summary>
    /// The host said which pad family it reads our virtual pad as, and that changed (<see cref="FriendEvent.DeviceType"/>,
    /// also in <see cref="FriendSession.HostDeviceType"/>).
    /// </summary>
    PadDeviceSeen,

    /// <summary>
    /// The lobby's board differs from the one before (<see cref="FriendSession.Board"/>). Raised at most once per
    /// <see cref="SessionTiming.LobbyBoardIntervalMicros"/>, the newest coming from <see cref="FriendSession.Update"/> after that,
    /// so a host repeating changes can't make this PC redraw every frame; <see cref="FriendSession.Board"/> is always the newest.
    /// </summary>
    BoardChanged,
}

/// <summary>One thing that happened in a <see cref="FriendSession"/>.</summary>
/// <param name="Kind">What.</param>
/// <param name="Reject">For <see cref="FriendEventKind.Rejected"/>.</param>
/// <param name="Reason">For <see cref="FriendEventKind.Ended"/>.</param>
/// <param name="Achievement">For <see cref="FriendEventKind.AchievementEarned"/>: the game's name for it.</param>
/// <param name="DeviceType">For <see cref="FriendEventKind.PadDeviceSeen"/>: the family the host reads our pad as.</param>
/// <param name="Moment">For <see cref="FriendEventKind.AchievementEarned"/>: the moment with the play it came from (protocol 9).</param>
public readonly record struct FriendEvent(FriendEventKind Kind, RejectReason Reject = RejectReason.None, LeaveReason Reason = LeaveReason.None, string Achievement = "", byte DeviceType = 0, AchievementMoment Moment = default);

/// <summary>
/// A friend's end of a session: hello until the host answers, then our controls (<see cref="SetBindings"/>, sent after
/// every welcome, with our prompt names, <see cref="SetPromptNames"/>) and the input stream at <see cref="PadInputSender.FrameRate"/> Hz (a pad's, or a keyboard's over the key
/// table of our controls), pings for round trip and the host clock, and silence timeouts. Game-independent; the caller
/// supplies the transport, the pad or keyboard, and the time (microseconds).
/// </summary>
public sealed class FriendSession
{
    private readonly SessionIdentity _identity;
    private readonly ISessionTransport _transport;
    private readonly InputKind _input;
    private readonly Queue<FriendEvent> _events = new();
    private readonly BitWriter _writer = new(256);
    private PadInputSender? _sender;
    private KeyboardMouseSender? _keyboard;
    private InputBindingSet? _bindings;
    private PromptNameTable? _promptNames;
    private long _startedAt;
    private long _nextHelloAt;
    private bool _padConnected = true;
    private PadAwayReason _awayReason;
    private byte _helloDeviceType;
    private long _padDeviceSeenAt = long.MinValue;
    private bool _padDeviceSeenWaiting;
    private byte[]? _boardPayload;
    private long _boardChangedAt = long.MinValue;
    private bool _boardChangedWaiting;
    private LobbyMemberMessage? _member;

    /// <summary>Creates a session with the lobby's host.</summary>
    /// <param name="identity">Our versions.</param>
    /// <param name="host">The host's Steam ID (the lobby owner).</param>
    /// <param name="input">What we play with: a pad, or the keyboard and mouse.</param>
    /// <param name="deviceType">The game's device type for our pad (the keyboard's, for a keyboard).</param>
    /// <param name="transport">Where messages go.</param>
    public FriendSession(SessionIdentity identity, ulong host, InputKind input, byte deviceType, ISessionTransport transport)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(transport);

        _identity = identity;
        Host = host;
        _input = input;
        DeviceType = deviceType;
        _transport = transport;
        Link = new PeerLink(0);
    }

    /// <summary>The host's Steam ID.</summary>
    public ulong Host { get; }

    /// <summary>Our pad's family, the game's own device type, as we tell the host (in our hello, then by <see cref="SetPadDevice"/>).</summary>
    public byte DeviceType { get; private set; }

    /// <summary>The family the host said it reads our virtual pad as, or -1 before it said (an older host never does).</summary>
    public int HostDeviceType { get; private set; } = -1;

    /// <summary>Where the session is.</summary>
    public FriendSessionState State { get; private set; }

    /// <summary>The host's welcome, once joined.</summary>
    public WelcomeMessage Welcome { get; private set; }

    /// <summary>Why the host refused, if it did.</summary>
    public RejectReason RejectReason { get; private set; }

    /// <summary>Why the session ended, if it did.</summary>
    public LeaveReason EndReason { get; private set; }

    /// <summary>The host's clock, estimated from pings.</summary>
    public ClockSync Clock { get; } = new();

    /// <summary>Traffic and health of the link to the host.</summary>
    public PeerLink Link { get; }

    /// <summary>What we play with.</summary>
    public InputKind Input => _input;

    /// <summary>Pad packets sent.</summary>
    public long PadPackets { get; private set; }

    /// <summary>Keyboard-and-mouse packets sent.</summary>
    public long KeyboardPackets { get; private set; }

    /// <summary>Times our controls went to the host (after every welcome, and after <see cref="SetBindings"/> while joined).</summary>
    public long BindingsSent { get; private set; }

    /// <summary>Our controls, as the host is told them (<see cref="SetBindings"/>), or null.</summary>
    public InputBindingSet? Bindings => _bindings;

    /// <summary>Times our prompt names went to the host (after every welcome, and after <see cref="SetPromptNames"/> while joined).</summary>
    public long PromptNamesSent { get; private set; }

    /// <summary>Our prompt names, as the host is told them (<see cref="SetPromptNames"/>), or null.</summary>
    public PromptNameTable? PromptNames => _promptNames;

    /// <summary>Messages from anyone but the host, dropped unread.</summary>
    public long FromStrangers { get; private set; }

    /// <summary>False while our pad (or keyboard) is away from the session: unplugged, or lent back to our own game (<see cref="SetPadConnected"/>).</summary>
    public bool PadConnected => _padConnected;

    /// <summary>Why our pad is away while <see cref="PadConnected"/> is false (<see cref="PadAwayReason.Unplugged"/> otherwise).</summary>
    public PadAwayReason AwayReason => _awayReason;

    /// <summary>The newest lobby board from the host, or null before one.</summary>
    public LobbyBoardMessage? Board { get; private set; }

    /// <summary>When <see cref="Board"/> arrived, to run its countdown down here.</summary>
    public long BoardReceivedMicros { get; private set; }

    /// <summary><see cref="MessageType.LobbyBoard"/> messages that were too big, didn't decode or came on another channel.</summary>
    public long BadBoards { get; private set; }

    /// <summary>What we last said about ourselves in the lobby (<see cref="SendMember"/>), or null.</summary>
    public LobbyMemberMessage? Member => _member;

    /// <summary>Takes the oldest unhandled event.</summary>
    public bool TryTakeEvent(out FriendEvent friendEvent) => _events.TryDequeue(out friendEvent);

    /// <summary>
    /// Gets the host's match stream (setup, end, tables, snapshots, events, sounds, HUD) once joined; unset, those count as
    /// malformed.
    /// </summary>
    public StreamMessageHandler? StreamMessages { get; set; }

    /// <summary>
    /// Gets the host's campaign messages (host views, run checkpoints, menus) once joined; unset, those count as malformed.
    /// </summary>
    public StreamMessageHandler? CampaignMessages { get; set; }

    /// <summary>Says hello to the host.</summary>
    public void Start(long nowMicros)
    {
        if (State != FriendSessionState.Idle)
        {
            throw new InvalidOperationException("Already started.");
        }

        State = FriendSessionState.Connecting;
        _startedAt = nowMicros;
        Link.LastHeardMicros = nowMicros;
        SendHello(nowMicros);
    }

    /// <summary>Handles one message from <paramref name="sender"/>.</summary>
    public void Receive(ulong sender, Channel channel, ReadOnlySpan<byte> message, long nowMicros)
    {
        if (sender != Host)
        {
            FromStrangers++;
            return;
        }

        if (State is FriendSessionState.Idle or FriendSessionState.Rejected or FriendSessionState.Ended)
        {
            return;
        }

        var reader = new BitReader(message);
        if (!MessageHeader.TryRead(ref reader, out MessageHeader header))
        {
            Link.Malformed++;
            return;
        }

        Link.LastHeardMicros = nowMicros;
        Link.MessagesIn++;
        Link.BytesIn += message.Length;
        if (Link.IsLagging)
        {
            Link.IsLagging = false;
            _events.Enqueue(new FriendEvent(FriendEventKind.Recovered));
        }

        switch (header.Type)
        {
            case MessageType.Welcome when State == FriendSessionState.Connecting:
                if (!WelcomeMessage.TryRead(ref reader, out WelcomeMessage welcome))
                {
                    Link.Malformed++;
                    break;
                }

                Welcome = welcome;
                State = FriendSessionState.Joined;
                _sender = _input == InputKind.Gamepad ? new PadInputSender(1_000_000) : null;
                _keyboard = NewKeyboardSender();
                Link.NextPingAtMicros = nowMicros;
                _events.Enqueue(new FriendEvent(FriendEventKind.Joined));
                SendBindings();
                SendPromptNames();
                SendMemberNow();
                if (!_padConnected)
                {
                    // Away before the host answered (unplugged, or lent to our own menu): the host assumes a pad until told
                    // otherwise.
                    SendPadConnection();
                }

                if (DeviceType != _helloDeviceType)
                {
                    // Another pad picked up after the hello the host answered.
                    SendPadDevice();
                }

                break;
            case MessageType.Welcome when State == FriendSessionState.Joined:
                // The answer to a repeated hello: the host started our controls over, so they go again, and it takes our pad
                // as back, so a pad still away says so again. Its phase may have moved on, and our member message starts over too.
                if (WelcomeMessage.TryRead(ref reader, out WelcomeMessage again))
                {
                    if (again.HostingSession != Welcome.HostingSession)
                    {
                        // A host's hosting session never changes while we're joined (another one is a new join, from a new
                        // hello). One naming another isn't an answer to our hello: ignored and counted, so the session
                        // the host's progress counts for here stays the one we joined (D135).
                        Link.Malformed++;
                        break;
                    }

                    Welcome = again;
                }

                SendBindings();
                SendPromptNames();
                SendMemberNow();
                if (!_padConnected)
                {
                    SendPadConnection();
                }

                break;
            case MessageType.Welcome:
                break;
            case MessageType.Reject when State == FriendSessionState.Connecting:
                RejectReason = RejectMessage.TryRead(ref reader, out RejectMessage reject) ? reject.Reason : RejectReason.None;
                State = FriendSessionState.Rejected;
                _events.Enqueue(new FriendEvent(FriendEventKind.Rejected, RejectReason));
                break;
            case MessageType.Ping:
                if (!PingMessage.TryRead(ref reader, out PingMessage ping))
                {
                    Link.Malformed++;
                    break;
                }

                SessionWire.Begin(_writer, Link, MessageType.Pong);
                new PongMessage(ping.Id, ping.SendTimeMicros, nowMicros).Write(_writer);
                SessionWire.Send(_transport, Host, Link, Channel.Realtime, _writer);
                break;
            case MessageType.Pong:
                if (!PongMessage.TryRead(ref reader, out PongMessage pong) || !SessionWire.AddRoundTrip(Link, pong, nowMicros, out long pingSentAt))
                {
                    Link.Malformed++;
                    break;
                }

                Clock.AddSample(pingSentAt, pong.ReplyTimeMicros, nowMicros);
                break;
            case MessageType.Leave:
                End(LeaveMessage.TryRead(ref reader, out LeaveMessage leave) ? leave.Reason : LeaveReason.None, tell: false);
                break;
            case MessageType.AchievementUnlocked when State == FriendSessionState.Joined:
                if (!AchievementUnlockedMessage.TryRead(ref reader, out AchievementMoment moment))
                {
                    Link.Malformed++;
                    break;
                }

                _events.Enqueue(new FriendEvent(FriendEventKind.AchievementEarned, Achievement: moment.Name, Moment: moment));
                break;
            case MessageType.PadDevice when State == FriendSessionState.Joined:
                if (!PadDeviceMessage.TryRead(ref reader, out PadDeviceMessage seen))
                {
                    Link.Malformed++;
                    break;
                }

                if (seen.DeviceType != HostDeviceType)
                {
                    // Always current for the status line; the event (a log line) at most once per PadDeviceIntervalMicros, the
                    // newest from Update after that, so a host repeating changes can't fill the log.
                    HostDeviceType = seen.DeviceType;
                    _padDeviceSeenWaiting = true;
                    RaisePadDeviceSeen(nowMicros);
                }

                break;
            case MessageType.LobbyBoard when State == FriendSessionState.Joined:
                OnBoard(channel, message, ref reader, nowMicros);
                break;
            case MessageType.MatchSetup or MessageType.MatchEnd or MessageType.Snapshot or MessageType.ObjectTable or MessageType.EventTable or
                MessageType.GameEvent or MessageType.SoundCall or MessageType.HudState or MessageType.CustomContent or MessageType.Rumble
                or MessageType.SoundLoop when State == FriendSessionState.Joined:
                if (header.Type == MessageType.Snapshot)
                {
                    Link.RealtimeLoss.Observe(header.Sequence);
                }

                if (StreamMessages?.Invoke(sender, channel, message, nowMicros) != true)
                {
                    Link.Malformed++;
                }

                break;
            case MessageType.HostView or MessageType.RunCheckpoint or MessageType.MenuOpen or MessageType.MenuNav or
                MessageType.MenuSelect or MessageType.MenuClick or MessageType.MenuAttachment or MessageType.Notification or MessageType.ProgressEvent or
                MessageType.ProgressStatus or MessageType.ProgressRunCopy when State == FriendSessionState.Joined:
                if (CampaignMessages?.Invoke(sender, channel, message, nowMicros) != true)
                {
                    Link.Malformed++;
                }

                break;
            default:
                Link.Malformed++;
                break;
        }
    }

    /// <summary>Call every frame: hello retries, pings and silence timeouts.</summary>
    public void Update(long nowMicros)
    {
        if (_padDeviceSeenWaiting)
        {
            RaisePadDeviceSeen(nowMicros);
        }

        if (_boardChangedWaiting)
        {
            RaiseBoardChanged(nowMicros);
        }

        switch (State)
        {
            case FriendSessionState.Connecting when nowMicros - _startedAt > SessionTiming.HelloGiveUpMicros:
                End(LeaveReason.TimedOut, tell: false);
                return;
            case FriendSessionState.Connecting when nowMicros >= _nextHelloAt:
                SendHello(nowMicros);
                return;
            case FriendSessionState.Joined:
                break;
            default:
                return;
        }

        long silence = nowMicros - Link.LastHeardMicros;
        if (silence > SessionTiming.GoneAfterMicros)
        {
            End(LeaveReason.TimedOut, tell: true);
            return;
        }

        if (silence > SessionTiming.LaggingAfterMicros && !Link.IsLagging)
        {
            Link.IsLagging = true;
            _events.Enqueue(new FriendEvent(FriendEventKind.Lagging));
        }

        if (nowMicros >= Link.NextPingAtMicros)
        {
            Link.NextPingAtMicros = nowMicros + SessionTiming.PingIntervalMicros;
            SessionWire.SendPing(_transport, Host, Link, _writer, nowMicros);
        }
    }

    /// <summary>
    /// Samples our pad. Once joined, sends a packet whenever a new <see cref="PadInputSender.FrameRate"/> Hz frame is
    /// due; call it every time input is polled. Returns true if a packet went out.
    /// </summary>
    public bool SendPad(long nowMicros, PadButtons held, float leftX, float leftY, float rightX, float rightY, float leftTrigger, float rightTrigger)
    {
        if (State != FriendSessionState.Joined || _sender == null || !_padConnected)
        {
            return false;
        }

        if (!_sender.Capture(nowMicros, held, leftX, leftY, rightX, rightY, leftTrigger, rightTrigger))
        {
            return false;
        }

        SessionWire.Begin(_writer, Link, MessageType.PadInput);
        _sender.WritePacket(_writer);
        bool sent = SessionWire.Send(_transport, Host, Link, Channel.Realtime, _writer);
        PadPackets += sent ? 1 : 0;
        return sent;
    }

    /// <summary>
    /// Samples our keyboard and mouse buttons over our key table: element <c>i</c> of <see cref="InputBindingSet.Elements"/>
    /// at bit <c>i</c>, <paramref name="held"/> held now, <paramref name="pressed"/> gone down since the last call. Once joined
    /// with our controls set, sends a packet whenever a new <see cref="PadInputSender.FrameRate"/> Hz frame is due; call it
    /// every time input is polled. Returns true if a packet went out.
    /// </summary>
    public bool SendKeyboard(long nowMicros, ulong held, ulong pressed)
    {
        if (State != FriendSessionState.Joined || _keyboard == null || !_padConnected)
        {
            return false;
        }

        if (!_keyboard.Capture(nowMicros, held, pressed))
        {
            return false;
        }

        SessionWire.Begin(_writer, Link, MessageType.KeyboardMouseInput);
        _keyboard.WritePacket(_writer);
        bool sent = SessionWire.Send(_transport, Host, Link, Channel.Realtime, _writer);
        KeyboardPackets += sent ? 1 : 0;
        return sent;
    }

    /// <summary>
    /// Our controls, a pad's or a keyboard's (<see cref="InputBindingSet.Device"/> must be what we play with): sent to the
    /// host after every welcome, and at once if we're joined. A keyboard's frames use its key table from then on.
    /// </summary>
    public void SetBindings(InputBindingSet bindings)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        if (bindings.Device != _input)
        {
            throw new ArgumentException($"Controls for {bindings.Device} in a session played with {_input}.", nameof(bindings));
        }

        _bindings = bindings;
        if (State == FriendSessionState.Joined)
        {
            _keyboard = NewKeyboardSender();
            SendBindings();
        }
    }

    /// <summary>
    /// Our button prompt names (<see cref="PromptNameTable"/>): sent to the host after every welcome (after our controls),
    /// and at once if we're joined. The host names its prompts for our game player from them.
    /// </summary>
    public void SetPromptNames(PromptNameTable names)
    {
        ArgumentNullException.ThrowIfNull(names);
        _promptNames = names;
        if (State == FriendSessionState.Joined)
        {
            SendPromptNames();
        }
    }

    /// <summary>
    /// What we say about ourselves in the lobby: our ready mark, our stage, the Start epoch we follow. Sent at once if we're
    /// joined and it changed, and again after every welcome. False when nothing went (not joined, or the same again).
    /// </summary>
    public bool SendMember(LobbyMemberMessage member)
    {
        bool changed = _member != member;
        _member = member;
        return changed && State == FriendSessionState.Joined && SendMemberNow();
    }

    /// <summary>
    /// Tells the host our pad (or keyboard) is away, and why (<paramref name="reason"/>: unplugged, or lent back to our own
    /// game's remapper or settings), or back. Sent once joined; one away before that is reported as soon as the host accepts
    /// us. Another reason while away is sent too; the same state again sends nothing. Nothing of the pad's or keyboard's goes
    /// to the host while it's away, and what was held counts once let go after it's back.
    /// </summary>
    public void SetPadConnected(bool connected, PadAwayReason reason = PadAwayReason.Unplugged)
    {
        PadAwayReason away = connected ? PadAwayReason.Unplugged : reason;
        if (connected == _padConnected && away == _awayReason)
        {
            return;
        }

        _padConnected = connected;
        _awayReason = away;
        _sender?.ReleaseAll();
        _keyboard?.ReleaseAll();
        if (State == FriendSessionState.Joined)
        {
            SendPadConnection();
        }
    }

    /// <summary>
    /// Our pad is now of family <paramref name="deviceType"/> (another pad was picked up): the host is told once joined (a
    /// change before that goes right after its welcome, and a later hello carries it anyway). False when it's the family
    /// the host already has from us.
    /// </summary>
    public bool SetPadDevice(byte deviceType)
    {
        if (deviceType == DeviceType)
        {
            return false;
        }

        DeviceType = deviceType;
        if (State == FriendSessionState.Joined)
        {
            SendPadDevice();
        }

        return true;
    }

    /// <summary>Leaves, telling the host.</summary>
    public void Leave(LeaveReason reason = LeaveReason.Quit)
    {
        if (State is FriendSessionState.Connecting or FriendSessionState.Joined)
        {
            End(reason, tell: true);
        }
    }

    private void SendHello(long nowMicros)
    {
        _nextHelloAt = nowMicros + SessionTiming.HelloRetryMicros;
        _helloDeviceType = DeviceType;
        SessionWire.Begin(_writer, Link, MessageType.Hello);
        new HelloMessage(_identity.ProtocolVersion, _identity.ModVersion, _identity.GameBuild, _input, DeviceType).Write(_writer);
        SessionWire.Send(_transport, Host, Link, Channel.Events, _writer);
    }

    private void SendPadConnection()
    {
        SessionWire.Begin(_writer, Link, _padConnected ? MessageType.PadReconnected : MessageType.PadDisconnected);
        if (!_padConnected)
        {
            new PadDisconnectedMessage(_awayReason).Write(_writer);
        }

        SessionWire.Send(_transport, Host, Link, Channel.Events, _writer);
    }

    private void SendBindings()
    {
        if (_bindings == null)
        {
            return;
        }

        SessionWire.Begin(_writer, Link, MessageType.InputRemaps);
        InputRemapsMessage.Write(_writer, _bindings);
        BindingsSent += SessionWire.Send(_transport, Host, Link, Channel.Events, _writer) ? 1 : 0;
    }

    private void SendPromptNames()
    {
        if (_promptNames == null)
        {
            return;
        }

        SessionWire.Begin(_writer, Link, MessageType.PromptNames);
        PromptNamesMessage.Write(_writer, _promptNames);
        PromptNamesSent += SessionWire.Send(_transport, Host, Link, Channel.Events, _writer) ? 1 : 0;
    }

    /// <summary>A sender over our key table, for a keyboard with its controls set; null otherwise.</summary>
    private KeyboardMouseSender? NewKeyboardSender() =>
        _input == InputKind.KeyboardMouse && _bindings != null ? new KeyboardMouseSender(1_000_000, _bindings.Generation, _bindings.Elements.Count) : null;

    /// <summary>Raises <see cref="FriendEventKind.PadDeviceSeen"/> for the newest family the host said, unless one went less than a second ago.</summary>
    private void RaisePadDeviceSeen(long nowMicros)
    {
        if (_padDeviceSeenAt != long.MinValue && nowMicros - _padDeviceSeenAt < SessionTiming.PadDeviceIntervalMicros)
        {
            return;
        }

        _padDeviceSeenAt = nowMicros;
        _padDeviceSeenWaiting = false;
        _events.Enqueue(new FriendEvent(FriendEventKind.PadDeviceSeen, DeviceType: (byte)HostDeviceType));
    }

    /// <summary>Takes a board from the host: the newest is kept at once, the same bytes again change nothing.</summary>
    private void OnBoard(Channel channel, ReadOnlySpan<byte> message, ref BitReader reader, long nowMicros)
    {
        int start = reader.BitPosition / 8;
        if (channel != Channel.Events || message.Length - start > LobbyBoardMessage.MaxPayloadBytes || !LobbyBoardMessage.TryRead(ref reader, out LobbyBoardMessage? board))
        {
            Link.Malformed++;
            BadBoards++;
            return;
        }

        ReadOnlySpan<byte> payload = message[start..];
        if (_boardPayload != null && payload.SequenceEqual(_boardPayload))
        {
            return;
        }

        _boardPayload = payload.ToArray();
        Board = board;
        BoardReceivedMicros = nowMicros;
        _boardChangedWaiting = true;
        RaiseBoardChanged(nowMicros);
    }

    /// <summary>Raises <see cref="FriendEventKind.BoardChanged"/>, unless one went less than <see cref="SessionTiming.LobbyBoardIntervalMicros"/> ago.</summary>
    private void RaiseBoardChanged(long nowMicros)
    {
        if (_boardChangedAt != long.MinValue && nowMicros - _boardChangedAt < SessionTiming.LobbyBoardIntervalMicros)
        {
            return;
        }

        _boardChangedAt = nowMicros;
        _boardChangedWaiting = false;
        _events.Enqueue(new FriendEvent(FriendEventKind.BoardChanged));
    }

    private bool SendMemberNow()
    {
        if (_member is not { } member)
        {
            return false;
        }

        SessionWire.Begin(_writer, Link, MessageType.LobbyMember);
        member.Write(_writer);
        return SessionWire.Send(_transport, Host, Link, Channel.Events, _writer);
    }

    private void SendPadDevice()
    {
        SessionWire.Begin(_writer, Link, MessageType.PadDevice);
        new PadDeviceMessage(DeviceType).Write(_writer);
        SessionWire.Send(_transport, Host, Link, Channel.Events, _writer);
    }

    private void End(LeaveReason reason, bool tell)
    {
        if (tell)
        {
            SessionWire.Begin(_writer, Link, MessageType.Leave);
            new LeaveMessage(reason).Write(_writer);
            SessionWire.Send(_transport, Host, Link, Channel.Events, _writer);
        }

        EndReason = reason;
        State = FriendSessionState.Ended;
        _sender = null;
        _keyboard = null;
        _events.Enqueue(new FriendEvent(FriendEventKind.Ended, Reason: reason));
    }
}
