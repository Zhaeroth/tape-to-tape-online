namespace T2TOnline.Steam.Native;

/// <summary>
/// Values for <see cref="SteamNetworkingConfigValue.P2PTransportIceEnable"/>: the
/// <c>k_nSteamNetworkingConfig_P2P_Transport_ICE_Enable_*</c> constants in steamnetworkingtypes.h.
/// </summary>
public static class SteamIceCandidates
{
    /// <summary><c>_Default</c> (-1): use the user's defaults.</summary>
    public const int Default = -1;

    /// <summary><c>_Disable</c> (0): no ICE at all; never share IP addresses with the peer (relay only).</summary>
    public const int Disable = 0;

    /// <summary><c>_Relay</c> (1): relayed connections via TURN servers.</summary>
    public const int Relay = 1;

    /// <summary><c>_Private</c> (2): link-local and RFC 1918 host addresses (same LAN).</summary>
    public const int Private = 2;

    /// <summary><c>_Public</c> (4): STUN reflexive addresses and public host addresses.</summary>
    public const int Public = 4;

    /// <summary><c>_All</c> (0x7fffffff): every candidate type.</summary>
    public const int All = 0x7fffffff;
}
