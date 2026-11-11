using System;

namespace T2TOnline.Steam.Native;

/// <summary>
/// Send flags for <c>ISteamNetworkingMessages::SendMessageToUser</c>: the <c>k_nSteamNetworkingSend_*</c> constants
/// in steamnetworkingtypes.h. Use one of the combined values (<see cref="UnreliableNoDelay"/>,
/// <see cref="Reliable"/>, ...) or OR the single bits together. The SDK's <c>k_nSteamNetworkingSend_UnreliableNoNagle</c>
/// (1) is <see cref="NoNagle"/> here, since <see cref="Unreliable"/> is 0.
/// </summary>
[Flags]
public enum SteamSendType
{
    /// <summary><c>k_nSteamNetworkingSend_Unreliable</c> (0): may be lost, duplicated or reordered; Nagle applies.</summary>
    Unreliable = 0,

    /// <summary>
    /// <c>k_nSteamNetworkingSend_NoNagle</c> (1): put this message on the wire without waiting for Nagle. On its own it is
    /// also <c>k_nSteamNetworkingSend_UnreliableNoNagle</c>.
    /// </summary>
    NoNagle = 1,

    /// <summary>
    /// <c>k_nSteamNetworkingSend_NoDelay</c> (4): drop the message (returning k_EResultIgnored) rather than queue it
    /// if the connection isn't ready or the queue is too long to send it within ~200 ms.
    /// </summary>
    NoDelay = 4,

    /// <summary>
    /// <c>k_nSteamNetworkingSend_UnreliableNoDelay</c> (5 = Unreliable | NoDelay | NoNagle): the realtime channel's mode.
    /// </summary>
    UnreliableNoDelay = Unreliable | NoDelay | NoNagle,

    /// <summary><c>k_nSteamNetworkingSend_Reliable</c> (8): delivered once, in order per channel; Nagle applies.</summary>
    Reliable = 8,

    /// <summary><c>k_nSteamNetworkingSend_ReliableNoNagle</c> (9 = Reliable | NoNagle).</summary>
    ReliableNoNagle = Reliable | NoNagle,

    /// <summary><c>k_nSteamNetworkingSend_UseCurrentThread</c> (16): do the send work on the calling thread.</summary>
    UseCurrentThread = 16,

    /// <summary>
    /// <c>k_nSteamNetworkingSend_AutoRestartBrokenSession</c> (32): silently re-establish a session the peer closed
    /// or that failed, instead of refusing the send (k_EResultConnectFailed in GameNetworkingSockets; the SDK header
    /// says k_EResultNoConnection).
    /// </summary>
    AutoRestartBrokenSession = 32,
}
