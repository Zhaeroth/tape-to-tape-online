using System;
using T2TOnline.Core.Diagnostics;
using T2TOnline.Core.Serialization;

namespace T2TOnline.Core.Protocol;

/// <summary>What a friend plays with.</summary>
public enum InputKind : byte
{
    /// <summary>A gamepad, mirrored into a virtual Rewired pad on the host.</summary>
    Gamepad = 0,

    /// <summary>
    /// Keyboard and mouse buttons, mirrored into a virtual Rewired keyboard on the host ("T2TOnline Keyboard"), bound from
    /// the friend's own maps (<see cref="MessageType.InputRemaps"/>).
    /// </summary>
    KeyboardMouse = 1,
}

/// <summary>Where the host's game is when a friend joins.</summary>
public enum SessionPhase : byte
{
    /// <summary>Not reported.</summary>
    Unknown = 0,

    /// <summary>In menus.</summary>
    Menus = 1,

    /// <summary>In a match.</summary>
    Match = 2,

    /// <summary>
    /// Protocol 7: the host's lobby is up and no game runs: the friend stays on their own game, device and saves until Start.
    /// </summary>
    Lobby = 3,

    /// <summary>Protocol 7: the host's game has started or is starting: the friend follows at once.</summary>
    Playing = 4,
}

/// <summary>Why a host refused a friend. New values may appear in later versions; print unknown ones as numbers.</summary>
public enum RejectReason : byte
{
    /// <summary>No reason given.</summary>
    None = 0,

    /// <summary>The friend's protocol version differs from the host's.</summary>
    ProtocolMismatch = 1,

    /// <summary>The friend's game build differs from the host's.</summary>
    GameBuildMismatch = 2,

    /// <summary>Every friend slot is taken.</summary>
    SessionFull = 3,

    /// <summary>The friend isn't in the host's lobby.</summary>
    NotInLobby = 4,

    /// <summary>The host couldn't give the friend a controller.</summary>
    NoControllerSlot = 5,

    /// <summary>The friend's input kind is one this host doesn't know (a later version's).</summary>
    UnsupportedInput = 6,

    /// <summary>The host has online mode off (an unknown game build or a failed start-up check).</summary>
    HostUnavailable = 7,

    /// <summary>Only the host's Steam friends get a seat.</summary>
    NotAFriend = 8,

    /// <summary>Protocol 7: the host removed this friend from the lobby, and they don't get back in while it lasts.</summary>
    Removed = 9,

    /// <summary>
    /// Protocol 9: Steam couldn't say whether the friend is one of the host's Steam friends (its friends interface wasn't
    /// usable), so the host doesn't seat them: only confirmed friends connect.
    /// </summary>
    FriendshipUnconfirmed = 10,
}

/// <summary>Why someone left. New values may appear in later versions; print unknown ones as numbers.</summary>
public enum LeaveReason : byte
{
    /// <summary>No reason given.</summary>
    None = 0,

    /// <summary>They chose to leave, or closed the game.</summary>
    Quit = 1,

    /// <summary>Nothing arrived from them for too long.</summary>
    TimedOut = 2,

    /// <summary>The host ended the session.</summary>
    HostClosed = 3,

    /// <summary>The host removed them.</summary>
    Removed = 4,

    /// <summary>They left the Steam lobby.</summary>
    LeftLobby = 5,
}

/// <summary>
/// Friend to host, first message of a session (channel 1). Its layout is frozen across protocol versions, as is
/// <see cref="RejectMessage"/>'s, so a host can always read it and answer "wrong version".
/// </summary>
/// <param name="ProtocolVersion">The friend's <see cref="ProtocolInfo.Version"/>.</param>
/// <param name="ModVersion">The friend's mod version, for logs.</param>
/// <param name="GameBuild">The first 8 bytes of the friend's GameAssembly.dll SHA-256.</param>
/// <param name="Input">What the friend plays with.</param>
/// <param name="DeviceType">The game's own device type for the friend's pad (button prompts, gamepad checks).</param>
public readonly record struct HelloMessage(byte ProtocolVersion, string ModVersion, ulong GameBuild, InputKind Input, byte DeviceType)
{
    /// <summary>Longest mod version kept.</summary>
    public const int MaxModVersionBytes = 48;

    /// <summary>Writes the payload (after the header).</summary>
    public void Write(BitWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.WriteByte(ProtocolVersion);
        writer.WriteShortString(ModVersion, MaxModVersionBytes);
        writer.WriteUInt64(GameBuild);
        writer.WriteByte((byte)Input);
        writer.WriteByte(DeviceType);
    }

    /// <summary>Reads the payload. False for truncated or malformed data.</summary>
    public static bool TryRead(ref BitReader reader, out HelloMessage message)
    {
        byte protocol = reader.ReadByte();
        string mod = SafeText.Clean(reader.ReadShortString(MaxModVersionBytes), MaxModVersionBytes);
        ulong build = reader.ReadUInt64();
        var input = (InputKind)reader.ReadByte();
        byte device = reader.ReadByte();
        message = new HelloMessage(protocol, mod, build, input, device);
        return !reader.IsOverrun;
    }
}

/// <summary>Host to friend (channel 1): accepted.</summary>
/// <param name="Slot">The Rewired player the friend's virtual pad is on.</param>
/// <param name="Phase">Where the host's game is, from its lobby flow (<c>Session.HostSession.WelcomePhase</c>):
/// <see cref="SessionPhase.Lobby"/> or <see cref="SessionPhase.Playing"/> since protocol 7. Any other value (a host with no
/// flow says <see cref="SessionPhase.Unknown"/>) is read as playing, the behaviour before the lobby.</param>
/// <param name="HostingSession">
/// The host's hosting session (random, never 0 from a protocol 6 host; the progress batches' session too): a friend that
/// shared this session's play, and whose game restarted since, knows it's the same session when it's welcomed back (D34).
/// Protocol 6; until then these 8 bytes were the host's clock, which nothing read (the layout is unchanged).
/// </param>
public readonly record struct WelcomeMessage(byte Slot, SessionPhase Phase, ulong HostingSession)
{
    /// <summary>Writes the payload (after the header).</summary>
    public void Write(BitWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.WriteByte(Slot);
        writer.WriteByte((byte)Phase);
        writer.WriteUInt64(HostingSession);
    }

    /// <summary>Reads the payload. False for truncated data.</summary>
    public static bool TryRead(ref BitReader reader, out WelcomeMessage message)
    {
        message = new WelcomeMessage(reader.ReadByte(), (SessionPhase)reader.ReadByte(), reader.ReadUInt64());
        return !reader.IsOverrun;
    }
}

/// <summary>Host to friend (channel 1): refused. Its layout is frozen across protocol versions.</summary>
/// <param name="Reason">Why.</param>
public readonly record struct RejectMessage(RejectReason Reason)
{
    /// <summary>Writes the payload (after the header).</summary>
    public void Write(BitWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.WriteByte((byte)Reason);
    }

    /// <summary>Reads the payload. False for truncated data.</summary>
    public static bool TryRead(ref BitReader reader, out RejectMessage message)
    {
        message = new RejectMessage((RejectReason)reader.ReadByte());
        return !reader.IsOverrun;
    }
}

/// <summary>Either way (channel 0): a round-trip and clock probe.</summary>
/// <param name="Id">Sender's counter.</param>
/// <param name="SendTimeMicros">Sender's clock when sent; echoed back as-is (the sender times the round trip from its own record, not the echo).</param>
public readonly record struct PingMessage(uint Id, long SendTimeMicros)
{
    /// <summary>Writes the payload (after the header).</summary>
    public void Write(BitWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.WriteVarUInt(Id);
        writer.WriteInt64(SendTimeMicros);
    }

    /// <summary>Reads the payload. False for truncated data.</summary>
    public static bool TryRead(ref BitReader reader, out PingMessage message)
    {
        ulong id = reader.ReadVarUInt();
        long sent = reader.ReadInt64();
        message = new PingMessage(unchecked((uint)id), sent);
        return !reader.IsOverrun && id <= uint.MaxValue;
    }
}

/// <summary>Either way (channel 0): the reply to a <see cref="PingMessage"/>.</summary>
/// <param name="Id">The ping's id.</param>
/// <param name="PingSendTimeMicros">The ping's send time, unchanged. Not trusted: the pinger times the round trip from its own
/// record of the ping by <paramref name="Id"/>.</param>
/// <param name="ReplyTimeMicros">The replier's clock when it replied.</param>
public readonly record struct PongMessage(uint Id, long PingSendTimeMicros, long ReplyTimeMicros)
{
    /// <summary>Writes the payload (after the header).</summary>
    public void Write(BitWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.WriteVarUInt(Id);
        writer.WriteInt64(PingSendTimeMicros);
        writer.WriteInt64(ReplyTimeMicros);
    }

    /// <summary>Reads the payload. False for truncated data.</summary>
    public static bool TryRead(ref BitReader reader, out PongMessage message)
    {
        ulong id = reader.ReadVarUInt();
        long sent = reader.ReadInt64();
        long reply = reader.ReadInt64();
        message = new PongMessage(unchecked((uint)id), sent, reply);
        return !reader.IsOverrun && id <= uint.MaxValue;
    }
}

/// <summary>
/// Either way (channel 1): a friend's pad family (<see cref="MessageType.PadDevice"/>). Any value decodes: what a family
/// means is the game's, and the host maps anything that isn't a gamepad family to one before using it.
/// </summary>
/// <param name="DeviceType">The game's own device type, as in <see cref="HelloMessage.DeviceType"/>.</param>
public readonly record struct PadDeviceMessage(byte DeviceType)
{
    /// <summary>Writes the payload (after the header).</summary>
    public void Write(BitWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.WriteByte(DeviceType);
    }

    /// <summary>Reads the payload. False for truncated data.</summary>
    public static bool TryRead(ref BitReader reader, out PadDeviceMessage message)
    {
        message = new PadDeviceMessage(reader.ReadByte());
        return !reader.IsOverrun;
    }
}

/// <summary>
/// Why a friend's pad (or keyboard) is away from the session (<see cref="MessageType.PadDisconnected"/>). New values may
/// appear in later versions; a reader takes one it doesn't know as <see cref="Unplugged"/>.
/// </summary>
public enum PadAwayReason : byte
{
    /// <summary>The physical pad was unplugged (also what a message with no reason means).</summary>
    Unplugged = 0,

    /// <summary>The friend is changing their controls: their own game's remapper is open, their device lent back to it.</summary>
    ChangingControls = 1,

    /// <summary>The friend has their own game's settings open (volume, video, controls), their device lent back to it.</summary>
    InSettings = 2,
}

/// <summary>
/// Friend to host (channel 1): the friend's pad, or keyboard, is away (<see cref="MessageType.PadDisconnected"/>), and why.
/// The reason byte is optional: a message without it, or with a reason this version doesn't know, reads as
/// <see cref="PadAwayReason.Unplugged"/>.
/// </summary>
/// <param name="Reason">Why.</param>
public readonly record struct PadDisconnectedMessage(PadAwayReason Reason)
{
    /// <summary>The highest reason this version knows.</summary>
    public const PadAwayReason LastKnown = PadAwayReason.InSettings;

    /// <summary>Writes the payload (after the header).</summary>
    public void Write(BitWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.WriteByte((byte)Reason);
    }

    /// <summary>Reads the payload: no reason, or one this version doesn't know, is <see cref="PadAwayReason.Unplugged"/>. Never fails.</summary>
    public static bool TryRead(ref BitReader reader, out PadDisconnectedMessage message)
    {
        if (reader.RemainingBits < 8)
        {
            message = new PadDisconnectedMessage(PadAwayReason.Unplugged);
            return true;
        }

        byte reason = reader.ReadByte();
        message = new PadDisconnectedMessage(reason <= (byte)LastKnown ? (PadAwayReason)reason : PadAwayReason.Unplugged);
        return true;
    }
}

/// <summary>Either way (channel 1): leaving.</summary>
/// <param name="Reason">Why.</param>
public readonly record struct LeaveMessage(LeaveReason Reason)
{
    /// <summary>Writes the payload (after the header).</summary>
    public void Write(BitWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.WriteByte((byte)Reason);
    }

    /// <summary>Reads the payload. False for truncated data.</summary>
    public static bool TryRead(ref BitReader reader, out LeaveMessage message)
    {
        message = new LeaveMessage((LeaveReason)reader.ReadByte());
        return !reader.IsOverrun;
    }
}
