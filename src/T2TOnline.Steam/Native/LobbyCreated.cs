using System.Runtime.InteropServices;

namespace T2TOnline.Steam.Native;

/// <summary>
/// <c>LobbyCreated_t</c> (isteammatchmaking.h, <c>#pragma pack(8)</c> on Windows), the call result of
/// <c>CreateLobby</c>: 16 bytes, <c>m_eResult</c> (EResult) at 0, <c>m_ulSteamIDLobby</c> (uint64) at 8.
/// Verified with MSVC x64 <c>offsetof</c> against the Steamworks SDK headers.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 8)]
public readonly struct LobbyCreated
{
    /// <summary><c>k_iCallback</c>: <c>k_iSteamMatchmakingCallbacks</c> (500) + 13.</summary>
    public const int CallbackId = 513;

    /// <summary>Size of the native struct.</summary>
    public const int SizeInBytes = 16;

    private readonly SteamResult _result;
    private readonly ulong _lobbyId;

    /// <summary>OK if the lobby was created (<c>m_eResult</c>).</summary>
    public SteamResult Result => _result;

    /// <summary>The new lobby's Steam ID, 0 on failure (<c>m_ulSteamIDLobby</c>).</summary>
    public ulong LobbyId => _lobbyId;
}
