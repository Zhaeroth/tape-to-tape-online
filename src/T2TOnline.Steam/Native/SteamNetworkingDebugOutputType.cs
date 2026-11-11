namespace T2TOnline.Steam.Native;

/// <summary>
/// <c>ESteamNetworkingSocketsDebugOutputType</c> (steamnetworkingtypes.h): how much of Steam's networking debug output a
/// registered function gets (each level includes the ones before it), and the type of each line it gets.
/// </summary>
public enum SteamNetworkingDebugOutputType
{
    /// <summary><c>k_ESteamNetworkingSocketsDebugOutputType_None</c>: nothing (with a null function: unregistered).</summary>
    None = 0,

    /// <summary><c>k_ESteamNetworkingSocketsDebugOutputType_Bug</c>: a bug in Steam's networking.</summary>
    Bug = 1,

    /// <summary><c>k_ESteamNetworkingSocketsDebugOutputType_Error</c>.</summary>
    Error = 2,

    /// <summary><c>k_ESteamNetworkingSocketsDebugOutputType_Important</c>.</summary>
    Important = 3,

    /// <summary><c>k_ESteamNetworkingSocketsDebugOutputType_Warning</c>.</summary>
    Warning = 4,

    /// <summary><c>k_ESteamNetworkingSocketsDebugOutputType_Msg</c>: what Steam says of a connection's state changes.</summary>
    Msg = 5,

    /// <summary><c>k_ESteamNetworkingSocketsDebugOutputType_Verbose</c>: the P2P rendezvous' details, among them a connection's ICE line.</summary>
    Verbose = 6,

    /// <summary><c>k_ESteamNetworkingSocketsDebugOutputType_Debug</c>.</summary>
    Debug = 7,

    /// <summary><c>k_ESteamNetworkingSocketsDebugOutputType_Everything</c>.</summary>
    Everything = 8,
}
