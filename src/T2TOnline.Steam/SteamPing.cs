using System;
using T2TOnline.Steam.Native;

namespace T2TOnline.Steam;

/// <summary>
/// Steam ping locations as strings, the form meant for sharing (lobby member data) and for estimating the ping
/// between any two players without connecting them. Estimates route through the relays, so they are an upper
/// bound on what a direct ICE route gives. None of these throw.
/// <para>
/// Not called by the mod, so never run against the real DLL, only tested without Steam: <see cref="Estimate"/> and
/// <see cref="CheckPingDataUpToDate"/>. Called by the mod: <see cref="TryGetLocalLocation"/> (logged, and published in its
/// lobby's data) and, since the native lobby, <see cref="EstimateFromLocalHost"/> (the friends browser's ping estimate to a
/// host from the location in its lobby data; its first run against the real DLL).
/// </para>
/// </summary>
public static class SteamPing
{
    /// <summary><c>k_nSteamNetworkingPing_Failed</c>: the estimate failed, or Steam isn't available, or a location didn't parse.</summary>
    public const int Failed = -1;

    /// <summary><c>k_nSteamNetworkingPing_Unknown</c>: not enough data yet (the local relay pings are still running).</summary>
    public const int Unknown = -2;

    /// <summary>Buffer size for a location string (<c>k_cchMaxSteamNetworkingPingLocationString</c>).</summary>
    public const int MaxLocationLength = 1024;

    /// <summary>
    /// The local ping location as a string. False until Steam has measured the relays (start that early with
    /// <see cref="SteamNetworking.InitRelayNetworkAccess"/>); <paramref name="ageSeconds"/> is how old the data is.
    /// </summary>
    public static bool TryGetLocalLocation(out string location, out float ageSeconds)
    {
        location = string.Empty;
        ageSeconds = -1;
        nint self = SteamInterfaces.NetworkingUtils();
        if (self == 0)
        {
            return false;
        }

        try
        {
            ageSeconds = SteamNative.GetLocalPingLocation(self, out SteamNetworkPingLocation local);
            if (ageSeconds < 0)
            {
                return false;
            }

            byte[] buffer = new byte[MaxLocationLength];
            SteamNative.ConvertPingLocationToString(self, in local, buffer, buffer.Length);
            location = NativeStrings.FromBuffer(buffer);
            return location.Length > 0;
        }
        catch (Exception e)
        {
            SteamRuntime.ReportFailure(e);
            location = string.Empty;
            return false;
        }
    }

    /// <summary>Estimated round-trip ms between two location strings, or <see cref="Failed"/>/<see cref="Unknown"/>.</summary>
    public static int Estimate(string locationA, string locationB)
    {
        nint self = SteamInterfaces.NetworkingUtils(nameof(SteamNative.EstimatePingTimeBetweenTwoLocations));
        if (self == 0 || !TryParse(self, locationA, out SteamNetworkPingLocation a) || !TryParse(self, locationB, out SteamNetworkPingLocation b))
        {
            return Failed;
        }

        try
        {
            return SteamNative.EstimatePingTimeBetweenTwoLocations(self, in a, in b);
        }
        catch (Exception e)
        {
            SteamRuntime.ReportFailure(e);
            return Failed;
        }
    }

    /// <summary>Estimated round-trip ms from this PC to a location string, or <see cref="Failed"/>/<see cref="Unknown"/>.</summary>
    public static int EstimateFromLocalHost(string remoteLocation)
    {
        nint self = SteamInterfaces.NetworkingUtils(nameof(SteamNative.EstimatePingTimeFromLocalHost));
        if (self == 0 || !TryParse(self, remoteLocation, out SteamNetworkPingLocation remote))
        {
            return Failed;
        }

        try
        {
            return SteamNative.EstimatePingTimeFromLocalHost(self, in remote);
        }
        catch (Exception e)
        {
            SteamRuntime.ReportFailure(e);
            return Failed;
        }
    }

    /// <summary>
    /// True if the local relay pings are no older than <paramref name="maxAgeSeconds"/>. If they are older, Steam
    /// starts re-measuring and this returns false.
    /// </summary>
    public static bool CheckPingDataUpToDate(float maxAgeSeconds)
    {
        nint self = SteamInterfaces.NetworkingUtils(nameof(SteamNative.CheckPingDataUpToDate));
        if (self == 0)
        {
            return false;
        }

        try
        {
            return SteamNative.CheckPingDataUpToDate(self, maxAgeSeconds);
        }
        catch (Exception e)
        {
            SteamRuntime.ReportFailure(e);
            return false;
        }
    }

    private static bool TryParse(nint self, string? text, out SteamNetworkPingLocation location)
    {
        location = default;
        if (string.IsNullOrEmpty(text) || text.Length >= MaxLocationLength || !SteamRuntime.HasExport(nameof(SteamNative.ParsePingLocationString)))
        {
            return false;
        }

        byte[]? utf8 = NativeStrings.ToUtf8(text);
        if (utf8 is null)
        {
            return false;
        }

        try
        {
            return SteamNative.ParsePingLocationString(self, utf8, out location);
        }
        catch (Exception e)
        {
            SteamRuntime.ReportFailure(e);
            location = default;
            return false;
        }
    }
}
