using System;
using T2TOnline.Steam.Native;

namespace T2TOnline.Steam;

/// <summary>
/// The Steam interface pointers, fetched fresh on every call (the flat accessors cache internally and return null
/// after Steam shuts down). Each returns 0 if the bindings aren't ready, Steam isn't initialised, or the call failed.
/// A caller about to call an optional export passes its <see cref="SteamNative"/> method name as <c>needs</c>, and gets
/// 0 without any native call when the export is missing.
/// </summary>
internal static class SteamInterfaces
{
    public static nint NetworkingMessages(string? needs = null)
    {
        if (!Usable(needs))
        {
            return 0;
        }

        try
        {
            return SteamNative.SteamNetworkingMessages();
        }
        catch (Exception e)
        {
            SteamRuntime.ReportFailure(e);
            return 0;
        }
    }

    public static nint NetworkingUtils(string? needs = null)
    {
        if (!Usable(needs))
        {
            return 0;
        }

        try
        {
            return SteamNative.SteamNetworkingUtils();
        }
        catch (Exception e)
        {
            SteamRuntime.ReportFailure(e);
            return 0;
        }
    }

    public static nint Matchmaking(string? needs = null)
    {
        if (!Usable(needs))
        {
            return 0;
        }

        try
        {
            return SteamNative.SteamMatchmaking();
        }
        catch (Exception e)
        {
            SteamRuntime.ReportFailure(e);
            return 0;
        }
    }

    public static nint Utils(string? needs = null)
    {
        if (!Usable(needs))
        {
            return 0;
        }

        try
        {
            return SteamRuntime.HasExport(nameof(SteamNative.SteamUtilsV011)) ? SteamNative.SteamUtilsV011() : SteamNative.SteamUtilsV010();
        }
        catch (Exception e)
        {
            SteamRuntime.ReportFailure(e);
            return 0;
        }
    }

    public static nint User(string? needs = null)
    {
        if (!Usable(needs))
        {
            return 0;
        }

        try
        {
            return SteamNative.SteamUser();
        }
        catch (Exception e)
        {
            SteamRuntime.ReportFailure(e);
            return 0;
        }
    }

    public static nint Friends(string? needs = null)
    {
        if (!Usable(needs))
        {
            return 0;
        }

        try
        {
            return SteamRuntime.HasExport(nameof(SteamNative.SteamFriendsV018)) ? SteamNative.SteamFriendsV018() : SteamNative.SteamFriendsV017();
        }
        catch (Exception e)
        {
            SteamRuntime.ReportFailure(e);
            return 0;
        }
    }

    /// <summary>The client pipe handle, or 0 (also when <c>SteamAPI_GetHSteamPipe</c>, an optional export, is missing).</summary>
    public static int Pipe()
    {
        if (!SteamRuntime.HasExport(nameof(SteamNative.GetHSteamPipe)))
        {
            return 0;
        }

        try
        {
            return SteamNative.GetHSteamPipe();
        }
        catch (Exception e)
        {
            SteamRuntime.ReportFailure(e);
            return 0;
        }
    }

    private static bool Usable(string? needs) => needs is null ? SteamRuntime.BindingsReady : SteamRuntime.HasExport(needs);
}
