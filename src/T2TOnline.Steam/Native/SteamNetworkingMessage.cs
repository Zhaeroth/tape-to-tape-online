using System.Runtime.InteropServices;

namespace T2TOnline.Steam.Native;

/// <summary>
/// <c>SteamNetworkingMessage_t</c> (steamnetworkingtypes.h, default packing, which is 8 on Windows x64): 216 bytes.
/// Messages are owned by Steam and only ever seen through a pointer, so <see cref="SteamMessageReceiver"/> reads the fields it
/// needs at the <c>*Offset</c> constants with <see cref="Marshal"/> instead of copying the whole struct; this type
/// documents the layout and lets the tests check the constants against the marshaller's own layout.
/// <code>
///   0 void*    m_pData
///   8 int      m_cbSize
///  12 uint32   m_conn
///  16 SteamNetworkingIdentity m_identityPeer (136 bytes)
/// 152 int64    m_nConnUserData
/// 160 int64    m_usecTimeReceived
/// 168 int64    m_nMessageNumber
/// 176 void*    m_pfnFreeData
/// 184 void*    m_pfnRelease
/// 192 int      m_nChannel
/// 196 int      m_nFlags
/// 200 int64    m_nUserData
/// 208 uint16   m_idxLane
/// 210 uint16   _pad1__
/// </code>
/// Offsets verified with MSVC x64 <c>offsetof</c> against the Steamworks SDK headers.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 8)]
public readonly struct SteamNetworkingMessage
{
    /// <summary>Size of the native struct.</summary>
    public const int SizeInBytes = 216;

    /// <summary>Offset of <c>m_pData</c> (payload pointer).</summary>
    public const int DataOffset = 0;

    /// <summary>Offset of <c>m_cbSize</c> (payload length, int).</summary>
    public const int SizeOffset = 8;

    /// <summary>Offset of <c>m_identityPeer</c> (the sender).</summary>
    public const int IdentityPeerOffset = 16;

    /// <summary>Offset of <c>m_usecTimeReceived</c> (local Steam timestamp, microseconds).</summary>
    public const int TimeReceivedOffset = 160;

    /// <summary>Offset of <c>m_nMessageNumber</c>.</summary>
    public const int MessageNumberOffset = 168;

    /// <summary>Offset of <c>m_nChannel</c> (the local channel it arrived on).</summary>
    public const int ChannelOffset = 192;

    /// <summary>Offset of <c>m_nFlags</c> (the sender's <see cref="SteamSendType"/>).</summary>
    public const int FlagsOffset = 196;

    private readonly nint _data;
    private readonly int _size;
    private readonly uint _connection;
    private readonly SteamNetworkingIdentity _identityPeer;
    private readonly long _connectionUserData;
    private readonly long _timeReceived;
    private readonly long _messageNumber;
    private readonly nint _freeData;
    private readonly nint _release;
    private readonly int _channel;
    private readonly int _flags;
    private readonly long _userData;
    private readonly ushort _lane;
    private readonly ushort _padding;

    /// <summary>Payload pointer (<c>m_pData</c>).</summary>
    public nint Data => _data;

    /// <summary>Payload length (<c>m_cbSize</c>).</summary>
    public int Size => _size;

    /// <summary>Connection handle (<c>m_conn</c>); not used with ISteamNetworkingMessages.</summary>
    public uint Connection => _connection;

    /// <summary>Sender (<c>m_identityPeer</c>).</summary>
    public SteamNetworkingIdentity IdentityPeer => _identityPeer;

    /// <summary>Connection user data (<c>m_nConnUserData</c>).</summary>
    public long ConnectionUserData => _connectionUserData;

    /// <summary>Local Steam timestamp when received, microseconds (<c>m_usecTimeReceived</c>).</summary>
    public long TimeReceived => _timeReceived;

    /// <summary>Message number (<c>m_nMessageNumber</c>).</summary>
    public long MessageNumber => _messageNumber;

    /// <summary>Free-data callback (<c>m_pfnFreeData</c>).</summary>
    public nint FreeData => _freeData;

    /// <summary>Release callback (<c>m_pfnRelease</c>); call <c>SteamAPI_SteamNetworkingMessage_t_Release</c> instead.</summary>
    public nint Release => _release;

    /// <summary>Local channel (<c>m_nChannel</c>).</summary>
    public int Channel => _channel;

    /// <summary>Send flags the sender used (<c>m_nFlags</c>).</summary>
    public int Flags => _flags;

    /// <summary>Arbitrary user data (<c>m_nUserData</c>).</summary>
    public long UserData => _userData;

    /// <summary>Lane (<c>m_idxLane</c>).</summary>
    public ushort Lane => _lane;

    /// <summary>Padding (<c>_pad1__</c>).</summary>
    public ushort Padding => _padding;
}
