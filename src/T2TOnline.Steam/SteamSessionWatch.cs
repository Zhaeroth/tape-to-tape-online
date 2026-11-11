using System;
using System.Collections.Generic;
using T2TOnline.Steam.Native;

namespace T2TOnline.Steam;

/// <summary>A change in the Steam session with one watched peer (<see cref="SteamSessionWatch"/>).</summary>
/// <param name="Peer">The peer's Steam ID.</param>
/// <param name="From">The state before (<see cref="SteamNetworkingConnectionState.None"/> before the first read).</param>
/// <param name="To">The state now.</param>
/// <param name="Broke">
/// True when a session that had been connected isn't any more, or a send found it broken: reliable messages sent on it may
/// never have arrived, and Steam can't say which.
/// </param>
/// <param name="EndReason">Steam's end reason (<c>ESteamNetConnectionEnd</c>) when the session ended, else 0.</param>
/// <param name="EndDebug">Steam's explanation when it gave one (from Steam: clean it before showing it).</param>
public readonly record struct SteamSessionChange(ulong Peer, SteamNetworkingConnectionState From, SteamNetworkingConnectionState To, bool Broke, int EndReason, string EndDebug);

/// <summary>
/// Makes a broken <c>ISteamNetworkingMessages</c> session impossible to miss (steamnetworkingtypes.h, on
/// <c>k_nSteamNetworkingSend_AutoRestartBrokenSession</c>: when a session breaks, some reliable messages may not have been
/// delivered, and in general it isn't possible to know which).
/// <para>
/// Sends go out without that flag first, so a broken session refuses them: the break is noted for a watched peer, with
/// Steam's end reason read at once, and the message is sent again with the flag, which starts a new session (the SDK's own
/// advice: "or you can simply add this flag and retry"). Steam refuses with <see cref="SteamResult.ConnectFailed"/>
/// (GameNetworkingSockets' <c>SendMessageToUser</c>: no live connection, and the last one's state isn't None), though the
/// SDK header names <see cref="SteamResult.NoConnection"/>; both count as broken. A session can also break or be replaced
/// without a send of ours finding it (the peer started a new one), so <see cref="Poll"/> reads each watched peer's state, as
/// the SDK suggests ("querying the session state may return none, connecting, and findingroute again"): one that was
/// connected and isn't any more broke. Every state change of a watched peer is reported, for the log.
/// </para>
/// <para>
/// Sends to <see cref="Loopback"/> (this PC's own account: the one-PC test) always carry the flag and are neither noted nor
/// counted: Steam's session with yourself reads as closed by the peer after its first message (its pipe connection is
/// unlinked while still connecting), so every message to yourself starts a new one. Without the flag the second message to
/// yourself is refused, and every one after it (the one-PC test never got past hello).
/// </para>
/// <para>
/// <see cref="Close"/> closes a session on purpose (a peer the host refused or removed, once the refusal has left): never the
/// loopback, and a failed close turns closing off without touching anything else.
/// </para>
/// Main thread only. Never throws: Steam's failures come back as results.
/// </summary>
public sealed class SteamSessionWatch
{
    private readonly ISteamSessionApi _api;
    private readonly Dictionary<ulong, Watched> _peers = new();
    private readonly Queue<SteamSessionChange> _changes = new();

    /// <summary>Creates a watch over Steam's sessions.</summary>
    public SteamSessionWatch()
        : this(NativeSessionApi.Instance)
    {
    }

    internal SteamSessionWatch(ISteamSessionApi api) => _api = api;

    /// <summary>Sends that found a broken session and started a new one (any peer, watched or not; never <see cref="Loopback"/>).</summary>
    public long Restarts { get; private set; }

    /// <summary>
    /// This PC's own Steam ID (0 until known): sends to it, the one-PC test's, always carry the restart flag (see the
    /// class remarks).
    /// </summary>
    public ulong Loopback { get; set; }

    /// <summary>
    /// Sends <paramref name="payload"/> to <paramref name="peer"/> on Steam channel <paramref name="channel"/> with
    /// <paramref name="flags"/> (<see cref="SteamSendType.AutoRestartBrokenSession"/> is ignored: a broken session is noted,
    /// then restarted). Returns Steam's result, as <see cref="SteamMessages.Send"/>.
    /// </summary>
    public SteamResult Send(ulong peer, int channel, ReadOnlySpan<byte> payload, SteamSendType flags)
    {
        if (peer != 0 && peer == Loopback)
        {
            return _api.Send(peer, channel, payload, flags | SteamSendType.AutoRestartBrokenSession);
        }

        flags &= ~SteamSendType.AutoRestartBrokenSession;
        SteamResult result = _api.Send(peer, channel, payload, flags);
        if (result is not (SteamResult.ConnectFailed or SteamResult.NoConnection))
        {
            return result;
        }

        // Read before the restart replaces it: once a new session starts, the old one's end reason is gone.
        if (_peers.TryGetValue(peer, out Watched? watched))
        {
            SteamNetworkingConnectionState state = _api.GetState(peer);
            (int reason, string debug) = _api.GetEnd(peer);
            _changes.Enqueue(new SteamSessionChange(peer, watched.State, state, Broke: watched.Connected, reason, debug));
            watched.State = state;
            watched.Connected = false;
        }

        Restarts++;
        return _api.Send(peer, channel, payload, flags | SteamSendType.AutoRestartBrokenSession);
    }

    /// <summary>True once a close failed (<see cref="SteamSessionClose.Failed"/>): every later <see cref="Close"/> is <see cref="SteamSessionClose.Off"/>.</summary>
    public bool ClosesOff { get; private set; }

    /// <summary>Why closing is off: the failed close's exception, described (empty while <see cref="ClosesOff"/> is false).</summary>
    public string CloseFailure { get; private set; } = string.Empty;

    /// <summary>Sessions closed (<see cref="SteamSessionClose.Closed"/>) since creation.</summary>
    public long Closes { get; private set; }

    /// <summary>
    /// Closes the Steam session with <paramref name="peer"/> (<see cref="SteamMessages.CloseSession"/>): what it queued is thrown
    /// away, so only once nothing of ours still has to reach them. Never <see cref="Loopback"/> (<see cref="SteamSessionClose.NotAllowed"/>).
    /// The peer stops being watched first: a session closed on purpose is no break. After a failed close every later one is
    /// <see cref="SteamSessionClose.Off"/>, and nothing else changes: sends and session reads go on as before.
    /// </summary>
    public SteamSessionClose Close(ulong peer)
    {
        if (peer == 0 || peer == Loopback)
        {
            return SteamSessionClose.NotAllowed;
        }

        if (ClosesOff)
        {
            return SteamSessionClose.Off;
        }

        _peers.Remove(peer);
        SteamSessionClose result;
        string failure;
        try
        {
            result = _api.Close(peer, out failure);
        }
        catch (Exception e)
        {
            // The API never throws; a fake one might. Treated as Steam's own failure.
            result = SteamSessionClose.Failed;
            failure = e.GetType().Name;
        }

        if (result == SteamSessionClose.Failed)
        {
            ClosesOff = true;
            CloseFailure = failure.Length > 0 ? failure : "no reason given";
        }
        else if (result == SteamSessionClose.Closed)
        {
            Closes++;
        }

        return result;
    }

    /// <summary>Starts watching <paramref name="peer"/> (nothing if already watched). Its first state is reported once read.</summary>
    public void Watch(ulong peer)
    {
        if (peer != 0 && !_peers.ContainsKey(peer))
        {
            _peers[peer] = new Watched();
        }
    }

    /// <summary>Stops watching <paramref name="peer"/>.</summary>
    public void Forget(ulong peer) => _peers.Remove(peer);

    /// <summary>Stops watching everyone and drops unread changes.</summary>
    public void Clear()
    {
        _peers.Clear();
        _changes.Clear();
    }

    /// <summary>True while <paramref name="peer"/> is watched.</summary>
    public bool IsWatching(ulong peer) => _peers.ContainsKey(peer);

    /// <summary>
    /// Reads every watched peer's session state (call once a frame): a change is queued for <see cref="TryTakeChange"/>,
    /// with Steam's end reason, and a session that was connected and isn't any more is a break. Allocates only on a change.
    /// </summary>
    public void Poll()
    {
        foreach (KeyValuePair<ulong, Watched> entry in _peers)
        {
            Watched watched = entry.Value;
            SteamNetworkingConnectionState state = _api.GetState(entry.Key);
            if (state == watched.State)
            {
                continue;
            }

            bool broke = watched.Connected && state != SteamNetworkingConnectionState.Connected;
            (int reason, string debug) = state is SteamNetworkingConnectionState.Connected or SteamNetworkingConnectionState.Connecting or SteamNetworkingConnectionState.FindingRoute
                ? (0, string.Empty)
                : _api.GetEnd(entry.Key);
            _changes.Enqueue(new SteamSessionChange(entry.Key, watched.State, state, broke, reason, debug));
            watched.State = state;
            watched.Connected = state == SteamNetworkingConnectionState.Connected;
        }
    }

    /// <summary>Takes the oldest change not taken yet.</summary>
    public bool TryTakeChange(out SteamSessionChange change) => _changes.TryDequeue(out change);

    /// <summary>One watched peer: the state last read, and whether the session is connected (so a change breaks it).</summary>
    private sealed class Watched
    {
        public SteamNetworkingConnectionState State { get; set; } = SteamNetworkingConnectionState.None;

        public bool Connected { get; set; }
    }

    private sealed class NativeSessionApi : ISteamSessionApi
    {
        public static readonly NativeSessionApi Instance = new();

        public SteamResult Send(ulong peer, int channel, ReadOnlySpan<byte> payload, SteamSendType flags) => SteamMessages.Send(peer, channel, payload, flags);

        public SteamNetworkingConnectionState GetState(ulong peer) => SteamMessages.GetSessionInfo(peer, out _, out _);

        public (int Reason, string Debug) GetEnd(ulong peer)
        {
            SteamMessages.GetSessionInfo(peer, out SteamNetConnectionInfo info, out _);
            return (info.EndReason, info.EndDebug);
        }

        public SteamSessionClose Close(ulong peer, out string failure) => SteamMessages.CloseSession(peer, out failure);
    }
}

/// <summary>The session calls <see cref="SteamSessionWatch"/> needs; Steam in the game, a fake in tests. Never throws.</summary>
internal interface ISteamSessionApi
{
    /// <summary>Sends one message, as <see cref="SteamMessages.Send"/>.</summary>
    SteamResult Send(ulong peer, int channel, ReadOnlySpan<byte> payload, SteamSendType flags);

    /// <summary>The session's state with <paramref name="peer"/>, as <see cref="SteamMessages.GetSessionInfo"/>.</summary>
    SteamNetworkingConnectionState GetState(ulong peer);

    /// <summary>The session's end reason and Steam's explanation (0 and empty when it hasn't ended).</summary>
    (int Reason, string Debug) GetEnd(ulong peer);

    /// <summary>Closes the session with <paramref name="peer"/>, as <see cref="SteamMessages.CloseSession"/>.</summary>
    SteamSessionClose Close(ulong peer, out string failure);
}
