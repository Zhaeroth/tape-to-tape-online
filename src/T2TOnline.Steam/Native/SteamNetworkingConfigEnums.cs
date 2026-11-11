namespace T2TOnline.Steam.Native;

/// <summary><c>ESteamNetworkingConfigScope</c> (steamnetworkingtypes.h): what a config value is applied to.</summary>
public enum SteamNetworkingConfigScope
{
    /// <summary><c>k_ESteamNetworkingConfig_Global</c>: global options and defaults for narrower scopes.</summary>
    Global = 1,

    /// <summary><c>k_ESteamNetworkingConfig_SocketsInterface</c>.</summary>
    SocketsInterface = 2,

    /// <summary><c>k_ESteamNetworkingConfig_ListenSocket</c>.</summary>
    ListenSocket = 3,

    /// <summary><c>k_ESteamNetworkingConfig_Connection</c>.</summary>
    Connection = 4,
}

/// <summary><c>ESteamNetworkingConfigDataType</c> (steamnetworkingtypes.h): the type of a config value's data.</summary>
internal enum SteamNetworkingConfigDataType
{
    /// <summary><c>k_ESteamNetworkingConfig_Int32</c>.</summary>
    Int32 = 1,

    /// <summary><c>k_ESteamNetworkingConfig_Int64</c>.</summary>
    Int64 = 2,

    /// <summary><c>k_ESteamNetworkingConfig_Float</c>.</summary>
    Float = 3,

    /// <summary><c>k_ESteamNetworkingConfig_String</c>.</summary>
    String = 4,

    /// <summary><c>k_ESteamNetworkingConfig_Ptr</c>.</summary>
    Ptr = 5,
}

/// <summary><c>ESteamNetworkingGetConfigValueResult</c> (steamnetworkingtypes.h).</summary>
internal enum SteamNetworkingGetConfigValueResult
{
    /// <summary><c>k_ESteamNetworkingGetConfigValue_BadValue</c>: no such configuration value.</summary>
    BadValue = -1,

    /// <summary><c>k_ESteamNetworkingGetConfigValue_BadScopeObj</c>: bad connection handle, etc.</summary>
    BadScopeObject = -2,

    /// <summary><c>k_ESteamNetworkingGetConfigValue_BufferTooSmall</c>.</summary>
    BufferTooSmall = -3,

    /// <summary><c>k_ESteamNetworkingGetConfigValue_OK</c>: set explicitly at this scope.</summary>
    OK = 1,

    /// <summary><c>k_ESteamNetworkingGetConfigValue_OKInherited</c>: not set here; the inherited or default value.</summary>
    OKInherited = 2,
}
