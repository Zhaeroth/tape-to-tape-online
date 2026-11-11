using System;
using System.Runtime.InteropServices;

namespace T2TOnline.Steam.Native;

/// <summary>
/// <c>SteamNetConnectionInfo_t</c> (steamnetworkingtypes.h, <c>#pragma pack(8)</c> on Windows): 696 bytes.
/// <code>
///   0 SteamNetworkingIdentity m_identityRemote (136, pack 1)
/// 136 int64    m_nUserData
/// 144 uint32   m_hListenSocket
/// 148 SteamNetworkingIPAddr m_addrRemote (18, pack 1; all zero for relayed/ICE P2P)
/// 166 uint16   m__pad1
/// 168 uint32   m_idPOPRemote
/// 172 uint32   m_idPOPRelay
/// 176 int      m_eState
/// 180 int      m_eEndReason
/// 184 char[128] m_szEndDebug
/// 312 char[128] m_szConnectionDescription
/// 440 int      m_nFlags
/// 444 uint32[63] reserved
/// </code>
/// Offsets verified with MSVC x64 <c>offsetof</c> against the Steamworks SDK headers. Only the fields used here are
/// declared; the rest is covered by the explicit size.
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = SizeInBytes)]
public readonly struct SteamNetConnectionInfo
{
    /// <summary>Size of the native struct.</summary>
    public const int SizeInBytes = 696;

    /// <summary>Offset of <c>m_szEndDebug</c>.</summary>
    public const int EndDebugOffset = 184;

    /// <summary>Offset of <c>m_szConnectionDescription</c>.</summary>
    public const int ConnectionDescriptionOffset = 312;

    /// <summary>Length of each of the two char arrays (<c>k_cchSteamNetworkingMaxConnectionCloseReason</c>, <c>..Description</c>).</summary>
    public const int StringLength = 128;

    [FieldOffset(0)]
    private readonly SteamNetworkingIdentity _remoteIdentity;

    [FieldOffset(136)]
    private readonly long _userData;

    [FieldOffset(144)]
    private readonly uint _listenSocket;

    [FieldOffset(168)]
    private readonly uint _popRemote;

    [FieldOffset(172)]
    private readonly uint _popRelay;

    [FieldOffset(176)]
    private readonly SteamNetworkingConnectionState _state;

    [FieldOffset(180)]
    private readonly int _endReason;

    [FieldOffset(440)]
    private readonly SteamConnectionTraits _flags;

    /// <summary>Who is on the other end (<c>m_identityRemote</c>).</summary>
    public SteamNetworkingIdentity RemoteIdentity => _remoteIdentity;

    /// <summary>Application user data (<c>m_nUserData</c>).</summary>
    public long UserData => _userData;

    /// <summary>Listen socket handle (<c>m_hListenSocket</c>); 0 for ISteamNetworkingMessages sessions.</summary>
    public uint ListenSocket => _listenSocket;

    /// <summary>Data center of the remote host, 0 if unknown (<c>m_idPOPRemote</c>); see <see cref="SteamPopId"/>.</summary>
    public uint RemotePopId => _popRemote;

    /// <summary>Relay in use, 0 if none (<c>m_idPOPRelay</c>); see <see cref="SteamPopId"/>.</summary>
    public uint RelayPopId => _popRelay;

    /// <summary>High-level state (<c>m_eState</c>).</summary>
    public SteamNetworkingConnectionState State => _state;

    /// <summary>Why the session ended or has a problem, an <c>ESteamNetConnectionEnd</c> value (<c>m_eEndReason</c>).</summary>
    public int EndReason => _endReason;

    /// <summary>Connection flags (<c>m_nFlags</c>).</summary>
    public SteamConnectionTraits Flags => _flags;

    /// <summary>True when the route goes through a relay (SDR or TURN) rather than direct.</summary>
    public bool IsRelayed => (_flags & SteamConnectionTraits.Relayed) != 0;

    /// <summary>
    /// Steam's English explanation of the end reason (<c>m_szEndDebug</c>). Steam's own text, whose words this library doesn't
    /// control: clean it before logging it (D127).
    /// </summary>
    public string EndDebug => NativeStrings.FromStructBuffer(in this, EndDebugOffset, StringLength);

    /// <summary>
    /// Steam's debug description of the connection (<c>m_szConnectionDescription</c>, such as <c>#1234 P2P steamid:7656... vport 0</c>).
    /// It names the peer by Steam ID, so it never goes into a log as it is (D127).
    /// </summary>
    public string ConnectionDescription => NativeStrings.FromStructBuffer(in this, ConnectionDescriptionOffset, StringLength);
}

/// <summary>The <c>k_nSteamNetworkConnectionInfoFlags_*</c> bits of <see cref="SteamNetConnectionInfo.Flags"/>.</summary>
[Flags]
public enum SteamConnectionTraits
{
    /// <summary>No flags.</summary>
    None = 0,

    /// <summary><c>_Unauthenticated</c>: no certificate for the remote host.</summary>
    Unauthenticated = 1,

    /// <summary><c>_Unencrypted</c>.</summary>
    Unencrypted = 2,

    /// <summary><c>_LoopbackBuffers</c>: internal loopback.</summary>
    LoopbackBuffers = 4,

    /// <summary><c>_Fast</c>: localhost or (probably) the same LAN.</summary>
    Fast = 8,

    /// <summary><c>_Relayed</c>: relayed via SDR or TURN.</summary>
    Relayed = 16,

    /// <summary><c>_DualWifi</c>.</summary>
    DualWifi = 32,
}
