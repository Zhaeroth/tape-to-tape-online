using System.Runtime.InteropServices;

namespace T2TOnline.Steam.Native;

/// <summary>
/// <c>SteamRelayNetworkStatus_t</c> (isteamnetworkingutils.h, default packing): 272 bytes.
/// <c>m_eAvail</c> 0, <c>m_bPingMeasurementInProgress</c> 4 (int), <c>m_eAvailNetworkConfig</c> 8,
/// <c>m_eAvailAnyRelay</c> 12, <c>m_debugMsg</c> char[256] at 16.
/// Verified with MSVC x64 <c>offsetof</c> against the Steamworks SDK headers.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 8, Size = SizeInBytes)]
public readonly struct SteamRelayNetworkStatus
{
    /// <summary>Size of the native struct.</summary>
    public const int SizeInBytes = 272;

    /// <summary>Offset of <c>m_debugMsg</c>.</summary>
    public const int DebugMessageOffset = 16;

    /// <summary>Length of <c>m_debugMsg</c>.</summary>
    public const int DebugMessageLength = 256;

    private readonly SteamNetworkingAvailability _availability;
    private readonly int _pingMeasurementInProgress;
    private readonly SteamNetworkingAvailability _networkConfigAvailability;
    private readonly SteamNetworkingAvailability _anyRelayAvailability;

    /// <summary>Summary: can we use the relay network right now (<c>m_eAvail</c>)?</summary>
    public SteamNetworkingAvailability Availability => _availability;

    /// <summary>True while ping measurement is running (<c>m_bPingMeasurementInProgress</c>).</summary>
    public bool IsPingMeasurementInProgress => _pingMeasurementInProgress != 0;

    /// <summary>Status of the network configuration download (<c>m_eAvailNetworkConfig</c>).</summary>
    public SteamNetworkingAvailability NetworkConfigAvailability => _networkConfigAvailability;

    /// <summary>Status of contact with at least one relay (<c>m_eAvailAnyRelay</c>).</summary>
    public SteamNetworkingAvailability AnyRelayAvailability => _anyRelayAvailability;

    /// <summary>
    /// Steam's English diagnostic text (<c>m_debugMsg</c>). Steam's own text, whose words this library doesn't control: clean it
    /// before logging it (D127).
    /// </summary>
    public string DebugMessage => NativeStrings.FromStructBuffer(in this, DebugMessageOffset, DebugMessageLength);
}
