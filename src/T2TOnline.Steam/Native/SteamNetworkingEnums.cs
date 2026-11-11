namespace T2TOnline.Steam.Native;

/// <summary><c>ESteamNetworkingConnectionState</c> (steamnetworkingtypes.h): the high-level state of a session.</summary>
public enum SteamNetworkingConnectionState
{
    /// <summary><c>k_ESteamNetworkingConnectionState_None</c>: no session (also returned when Steam isn't available).</summary>
    None = 0,

    /// <summary><c>k_ESteamNetworkingConnectionState_Connecting</c>.</summary>
    Connecting = 1,

    /// <summary><c>k_ESteamNetworkingConnectionState_FindingRoute</c>: P2P route finding (ICE or relay).</summary>
    FindingRoute = 2,

    /// <summary><c>k_ESteamNetworkingConnectionState_Connected</c>.</summary>
    Connected = 3,

    /// <summary><c>k_ESteamNetworkingConnectionState_ClosedByPeer</c>.</summary>
    ClosedByPeer = 4,

    /// <summary><c>k_ESteamNetworkingConnectionState_ProblemDetectedLocally</c>: timeout or other local failure.</summary>
    ProblemDetectedLocally = 5,

    /// <summary><c>k_ESteamNetworkingConnectionState_FinWait</c> (internal).</summary>
    FinWait = -1,

    /// <summary><c>k_ESteamNetworkingConnectionState_Linger</c> (internal).</summary>
    Linger = -2,

    /// <summary><c>k_ESteamNetworkingConnectionState_Dead</c> (internal).</summary>
    Dead = -3,
}

/// <summary><c>ESteamNetworkingAvailability</c> (steamnetworkingtypes.h): status of the relay network and its parts.</summary>
public enum SteamNetworkingAvailability
{
    /// <summary><c>k_ESteamNetworkingAvailability_CannotTry</c>: a dependency is missing (e.g. no Internet).</summary>
    CannotTry = -102,

    /// <summary><c>k_ESteamNetworkingAvailability_Failed</c>: tried long enough; never succeeded.</summary>
    Failed = -101,

    /// <summary><c>k_ESteamNetworkingAvailability_Previously</c>: worked once, failing now.</summary>
    Previously = -100,

    /// <summary><c>k_ESteamNetworkingAvailability_Retrying</c>: failed before, retrying now.</summary>
    Retrying = -10,

    /// <summary><c>k_ESteamNetworkingAvailability_Unknown</c>: not applicable (also returned when Steam isn't available).</summary>
    Unknown = 0,

    /// <summary><c>k_ESteamNetworkingAvailability_NeverTried</c>: <c>InitRelayNetworkAccess</c> hasn't been called yet.</summary>
    NeverTried = 1,

    /// <summary><c>k_ESteamNetworkingAvailability_Waiting</c>: waiting on a dependency (e.g. the network config).</summary>
    Waiting = 2,

    /// <summary><c>k_ESteamNetworkingAvailability_Attempting</c>: trying now.</summary>
    Attempting = 3,

    /// <summary><c>k_ESteamNetworkingAvailability_Current</c>: online and available.</summary>
    Current = 100,
}

/// <summary><c>ESteamNetworkingIdentityType</c> (steamnetworkingtypes.h): the kind of a <see cref="SteamNetworkingIdentity"/>.</summary>
public enum SteamNetworkingIdentityType
{
    /// <summary><c>k_ESteamNetworkingIdentityType_Invalid</c>: cleared or unknown.</summary>
    Invalid = 0,

    /// <summary><c>k_ESteamNetworkingIdentityType_IPAddress</c>.</summary>
    IPAddress = 1,

    /// <summary><c>k_ESteamNetworkingIdentityType_GenericString</c>.</summary>
    GenericString = 2,

    /// <summary><c>k_ESteamNetworkingIdentityType_GenericBytes</c>.</summary>
    GenericBytes = 3,

    /// <summary><c>k_ESteamNetworkingIdentityType_UnknownType</c>.</summary>
    UnknownType = 4,

    /// <summary><c>k_ESteamNetworkingIdentityType_SteamID</c>: a 64-bit Steam ID.</summary>
    SteamId = 16,

    /// <summary><c>k_ESteamNetworkingIdentityType_XboxPairwiseID</c>.</summary>
    XboxPairwiseId = 17,

    /// <summary><c>k_ESteamNetworkingIdentityType_SonyPSN</c>.</summary>
    SonyPsn = 18,
}
