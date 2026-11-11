using System.Runtime.InteropServices;

namespace T2TOnline.Steam.Native;

/// <summary>
/// <c>FriendGameInfo_t</c> (isteamfriends.h, <c>#pragma pack(8)</c> on Windows): 24 bytes, 4-byte aligned.
/// <c>m_gameID</c> (CGameID, 8 bytes) 0, <c>m_unGameIP</c> 8, <c>m_usGamePort</c> 12, <c>m_usQueryPort</c> 14,
/// <c>m_steamIDLobby</c> (CSteamID, 8 bytes) 16. CSteamID and CGameID are declared under <c>#pragma pack(1)</c>, so the
/// struct's alignment is 4 (from the uint32), not 8. Verified with MSVC x64 <c>offsetof</c>/<c>alignof</c> against the
/// Steamworks SDK headers.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly struct FriendGameInfo
{
    /// <summary>Size of the native struct.</summary>
    public const int SizeInBytes = 24;

    /// <summary><c>k_EGameIDTypeApp</c>: a plain Steam app (not a mod, shortcut or P2P file).</summary>
    public const int GameIdTypeApp = 0;

    private readonly ulong _gameId;
    private readonly uint _gameIp;
    private readonly ushort _gamePort;
    private readonly ushort _queryPort;
    private readonly ulong _lobbyId;

    /// <summary>Builds one in managed code (tests; Steam fills it in otherwise).</summary>
    internal FriendGameInfo(ulong gameId, ulong lobbyId)
    {
        _gameId = gameId;
        _gameIp = 0;
        _gamePort = 0;
        _queryPort = 0;
        _lobbyId = lobbyId;
    }

    /// <summary>
    /// The raw 64-bit CGameID (<c>m_gameID</c>): app ID in bits 0-23, type in bits 24-31, mod ID in bits 32-63.
    /// </summary>
    public ulong GameId => _gameId;

    /// <summary>The app ID part of <see cref="GameId"/>.</summary>
    public uint AppId => (uint)(_gameId & 0xFFFFFF);

    /// <summary>The type part of <see cref="GameId"/>; <see cref="GameIdTypeApp"/> for a normal game.</summary>
    public int GameIdType => (int)((_gameId >> 24) & 0xFF);

    /// <summary>Game server IP, if on one (<c>m_unGameIP</c>).</summary>
    public uint GameIp => _gameIp;

    /// <summary>Game server port (<c>m_usGamePort</c>).</summary>
    public ushort GamePort => _gamePort;

    /// <summary>Game server query port (<c>m_usQueryPort</c>).</summary>
    public ushort QueryPort => _queryPort;

    /// <summary>The Steam ID of the lobby the friend is in, or 0 (<c>m_steamIDLobby</c>).</summary>
    public ulong LobbyId => _lobbyId;

    /// <summary>True if the friend is playing exactly <paramref name="appId"/> (not a mod or shortcut of it).</summary>
    public bool IsPlayingApp(uint appId) => GameIdType == GameIdTypeApp && AppId == appId && (_gameId >> 32) == 0;
}
