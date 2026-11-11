using System;
using System.Runtime.InteropServices;
using T2TOnline.Steam.Native;

namespace T2TOnline.Steam;

/// <summary>
/// <c>ISteamNetworkingMessages</c> addressed by Steam ID: send, sessions and session status. Receiving is
/// <see cref="SteamMessageReceiver"/>, which keeps reusable buffers.
/// <para>
/// Sessions without callbacks: sending to a peer implicitly accepts that peer's pending session, so both sides should
/// send first once they know each other (from the lobby). <see cref="AcceptSession"/> only succeeds while a request is
/// pending, so a host that wants to accept without sending can poll it for known lobby members.
/// </para>
/// None of these throw; when Steam isn't available they return <see cref="SteamResult.None"/>, false,
/// <see cref="SteamSessionClose.Unavailable"/> or <see cref="SteamNetworkingConnectionState.None"/>.
/// <para>
/// <see cref="CloseSession"/> is called only through <see cref="SteamSessionWatch.Close"/>, for a peer the host refused or removed
/// once the refusal has left (closing also throws away messages still queued, such as a goodbye). <see cref="CloseChannel"/> is
/// not called by the mod, so never run against the real DLL, only tested without Steam. A broken session is restarted by
/// sending again (<see cref="SteamSessionWatch"/>), not closed.
/// </para>
/// </summary>
public static class SteamMessages
{
    /// <summary>Largest message Steam will send (<c>k_cbMaxSteamNetworkingSocketsMessageSizeSend</c>, 512 KB).</summary>
    public const int MaxSendSize = 512 * 1024;

    /// <summary>
    /// Sends <paramref name="payload"/> to <paramref name="steamId"/> on <paramref name="channel"/>. Returns
    /// <see cref="SteamResult.OK"/> once queued, Steam's own failure code (e.g. <see cref="SteamResult.ConnectFailed"/>
    /// for a broken session without <see cref="SteamSendType.AutoRestartBrokenSession"/>,
    /// <see cref="SteamResult.Ignored"/> for a dropped <see cref="SteamSendType.NoDelay"/> send,
    /// <see cref="SteamResult.LimitExceeded"/> when the send buffer is full), <see cref="SteamResult.InvalidParam"/> for
    /// a zero Steam ID, negative channel or oversized payload, or <see cref="SteamResult.None"/> when Steam isn't
    /// available. Steam copies the payload before returning.
    /// </summary>
    public static SteamResult Send(ulong steamId, int channel, ReadOnlySpan<byte> payload, SteamSendType flags)
    {
        if (steamId == 0 || channel < 0 || payload.Length > MaxSendSize)
        {
            return SteamResult.InvalidParam;
        }

        nint self = SteamInterfaces.NetworkingMessages();
        if (self == 0)
        {
            return SteamResult.None;
        }

        var identity = SteamNetworkingIdentity.FromSteamId(steamId);
        try
        {
            return SteamNative.SendMessageToUser(self, in identity, in MemoryMarshal.GetReference(payload), (uint)payload.Length, flags, channel);
        }
        catch (Exception e)
        {
            SteamRuntime.ReportFailure(e);
            return SteamResult.None;
        }
    }

    /// <summary>
    /// Accepts a pending session request from <paramref name="steamId"/>. True if one was pending or a session is
    /// already active; false if there is none, or Steam isn't available.
    /// </summary>
    public static bool AcceptSession(ulong steamId)
    {
        nint self = SteamInterfaces.NetworkingMessages();
        if (self == 0 || steamId == 0)
        {
            return false;
        }

        var identity = SteamNetworkingIdentity.FromSteamId(steamId);
        try
        {
            return SteamNative.AcceptSessionWithUser(self, in identity);
        }
        catch (Exception e)
        {
            SteamRuntime.ReportFailure(e);
            return false;
        }
    }

    /// <summary>
    /// Closes the session with <paramref name="steamId"/> on every channel, freeing its resources now: what it queued, sent or
    /// received, read or not, is thrown away, and the peer sees the session closed. Returns
    /// <see cref="SteamSessionClose.Closed"/>, <see cref="SteamSessionClose.NoSession"/> when Steam had none,
    /// <see cref="SteamSessionClose.NotAllowed"/> for a zero Steam ID, or <see cref="SteamSessionClose.Unavailable"/> when Steam
    /// (or this export) isn't there. Unlike every other call here, an exception doesn't turn the bindings off: the call is
    /// <see cref="SteamSessionClose.Failed"/>, with the exception described in <paramref name="failure"/>, and the caller stops
    /// closing sessions (<see cref="SteamSessionWatch"/> does). The session goes on without the bound a close gives, which costs less
    /// than online switched off for a call made only to tidy up.
    /// </summary>
    public static SteamSessionClose CloseSession(ulong steamId, out string failure)
    {
        failure = string.Empty;
        if (steamId == 0)
        {
            return SteamSessionClose.NotAllowed;
        }

        nint self = SteamInterfaces.NetworkingMessages(nameof(SteamNative.CloseSessionWithUser));
        if (self == 0)
        {
            return SteamSessionClose.Unavailable;
        }

        var identity = SteamNetworkingIdentity.FromSteamId(steamId);
        try
        {
            return SteamNative.CloseSessionWithUser(self, in identity) ? SteamSessionClose.Closed : SteamSessionClose.NoSession;
        }
        catch (Exception e)
        {
            failure = SteamRuntime.Describe(e);
            return SteamSessionClose.Failed;
        }
    }

    /// <summary>Closes one channel with <paramref name="steamId"/>; the session closes when its last channel does.</summary>
    public static bool CloseChannel(ulong steamId, int channel)
    {
        nint self = SteamInterfaces.NetworkingMessages(nameof(SteamNative.CloseChannelWithUser));
        if (self == 0 || steamId == 0)
        {
            return false;
        }

        var identity = SteamNetworkingIdentity.FromSteamId(steamId);
        try
        {
            return SteamNative.CloseChannelWithUser(self, in identity, channel);
        }
        catch (Exception e)
        {
            SteamRuntime.ReportFailure(e);
            return false;
        }
    }

    /// <summary>
    /// The session state with <paramref name="steamId"/>, with full details in <paramref name="info"/> (route, relay,
    /// end reason) and live numbers in <paramref name="status"/> (ping, quality, queue). Both are zeroed and the result
    /// is <see cref="SteamNetworkingConnectionState.None"/> when there's no session or Steam isn't available. Sessions
    /// time out a while after failing, so read the end reason promptly.
    /// </summary>
    public static SteamNetworkingConnectionState GetSessionInfo(ulong steamId, out SteamNetConnectionInfo info, out SteamNetConnectionRealTimeStatus status)
    {
        info = default;
        status = default;
        nint self = SteamInterfaces.NetworkingMessages();
        if (self == 0 || steamId == 0)
        {
            return SteamNetworkingConnectionState.None;
        }

        var identity = SteamNetworkingIdentity.FromSteamId(steamId);
        try
        {
            return SteamNative.GetSessionConnectionInfo(self, in identity, out info, out status);
        }
        catch (Exception e)
        {
            SteamRuntime.ReportFailure(e);
            info = default;
            status = default;
            return SteamNetworkingConnectionState.None;
        }
    }
}

/// <summary>What closing a Steam session did (<see cref="SteamMessages.CloseSession"/>, <see cref="SteamSessionWatch.Close"/>).</summary>
public enum SteamSessionClose
{
    /// <summary>Steam closed it: what it queued is gone, and the peer sees the session closed.</summary>
    Closed,

    /// <summary>Steam had no session with the peer: nothing to close.</summary>
    NoSession,

    /// <summary>Not tried: Steam, or its export for this call, isn't available.</summary>
    Unavailable,

    /// <summary>Not tried: no peer, or this PC's own account (the one-PC test's session with itself is never closed).</summary>
    NotAllowed,

    /// <summary>Not tried: an earlier close failed, so closing is off (<see cref="SteamSessionWatch.ClosesOff"/>).</summary>
    Off,

    /// <summary>The call threw: nothing is known of the session, and closing is off from now on.</summary>
    Failed,
}
