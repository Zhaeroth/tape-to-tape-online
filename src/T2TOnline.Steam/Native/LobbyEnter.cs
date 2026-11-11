using System.Runtime.InteropServices;

namespace T2TOnline.Steam.Native;

/// <summary>
/// <c>LobbyEnter_t</c> (isteammatchmaking.h, <c>#pragma pack(8)</c> on Windows), the call result of <c>JoinLobby</c>
/// (also posted as a broadcast callback, which the game's Facepunch dispatcher sees): 24 bytes.
/// <c>m_ulSteamIDLobby</c> (uint64) 0, <c>m_rgfChatPermissions</c> (uint32) 8, <c>m_bLocked</c> (bool, 1 byte) 12,
/// <c>m_EChatRoomEnterResponse</c> (uint32) 16. Verified with MSVC x64 <c>offsetof</c> against the Steamworks SDK headers.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 8)]
public readonly struct LobbyEnter
{
    /// <summary><c>k_iCallback</c>: <c>k_iSteamMatchmakingCallbacks</c> (500) + 4.</summary>
    public const int CallbackId = 504;

    /// <summary>Size of the native struct.</summary>
    public const int SizeInBytes = 24;

    private readonly ulong _lobbyId;
    private readonly uint _chatPermissions;
    private readonly byte _locked;
    private readonly uint _enterResponse;

    /// <summary>The lobby entered (<c>m_ulSteamIDLobby</c>).</summary>
    public ulong LobbyId => _lobbyId;

    /// <summary>The local user's permissions (<c>m_rgfChatPermissions</c>).</summary>
    public uint ChatPermissions => _chatPermissions;

    /// <summary>True if only invited users may join (<c>m_bLocked</c>).</summary>
    public bool IsLocked => _locked != 0;

    /// <summary>The outcome; <see cref="SteamChatRoomEnterResponse.Success"/> means joined (<c>m_EChatRoomEnterResponse</c>).</summary>
    public SteamChatRoomEnterResponse EnterResponse => (SteamChatRoomEnterResponse)_enterResponse;
}
